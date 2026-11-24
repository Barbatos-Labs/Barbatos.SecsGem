// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Text;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// A service program store that outlives the process, by keeping each program in a file.
/// </summary>
/// <remarks>
/// <para>
/// The last store in this repository that kept what a host sent and lost it on a restart. A service
/// program is text the host wrote and asked this machine to hold and run - <c>SPD</c>, which the
/// reference <em>does</em> define as an <c>A[n]</c>, which is why a service program is text here
/// where a control program is bytes - so it belongs on a disk for the same reason a recipe does.
/// </para>
/// <para>
/// <strong>The mechanism is <c>DurableRecordDirectory</c>'s</strong>, shared as source with the four
/// transfer packages and the three stream 7 stores: one file per program, written through, committed
/// with a rename, and a file name that is a sequence number and nothing else. Here the identifier is
/// a number and would have made a perfectly good file name, so what the shared layout buys is the
/// other half of the rule - the identifier is <em>inside</em> the record, so replacing a program is
/// one atomic rewrite of one file and a program never has two records.
/// </para>
/// <para>
/// <strong>The results of a run are not written down, and that is the whole judgement this store
/// had to make.</strong> Everything else here is what a host told the machine: the programs come
/// back, and so does nothing else. A result is what the machine <em>produced</em> by running code,
/// and after a restart it has not run anything - so answering S2F9 out of a file would be claiming
/// a run it never made. The sibling store already makes the weaker version of this argument: it
/// clears a program's results when the program is replaced, because leaving them would answer S2F9
/// with the output of code that is no longer here. A restart is that argument one step further, and
/// <see cref="HasResults"/> is false for every program on every open.
/// </para>
/// <para>
/// <see cref="Run"/> is the other thing not written down, and for a different reason: it is not
/// state at all but the machine's own wiring, which the application supplies on every start the way
/// it supplies the directory. Nor are <see cref="Capacity"/> and <see cref="MaxLength"/>, which are
/// configuration rather than anything a host said.
/// </para>
/// <para>
/// Records are <c>.serviceprogram</c>. One store per directory.
/// </para>
/// </remarks>
public sealed class GemFileServiceProgramStore : IGemServiceProgramStore
{
    /// <summary>The extension a finished program record carries.</summary>
    public const string RecordExtension = ".serviceprogram";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<uint, long> _programs = new();
    private readonly ConcurrentDictionary<uint, string> _results = new();
    private readonly DurableRecordDirectory _records;
    private readonly ILogger _logger;

    private long _next;

    /// <summary>Opens a store over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// The directory is created and scanned here rather than on first use, so a path that is wrong
    /// fails at start-up where an operator is looking. An unfinished write is swept, a file that is
    /// not this store's is left alone, and so is a record this version cannot read. Neither
    /// <see cref="Capacity"/> nor <see cref="MaxLength"/> is enforced on what is adopted: what is on
    /// the disk is a fact, and a store that hid some of it would answer S2F11 with a list the disk
    /// disagrees with.
    /// </remarks>
    public GemFileServiceProgramStore(string directory, ILogger<GemFileServiceProgramStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<GemFileServiceProgramStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            if (_programs.TryGetValue(record.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold service program {Id}; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Id,
                    Math.Max(held, sequence),
                    Math.Min(held, sequence));

                _records.Delete(Math.Min(held, sequence));

                if (sequence < held)
                {
                    continue;
                }
            }

            _programs[record.Id] = sequence;
            _next = Math.Max(_next, sequence + 1);
        }

        if (_programs.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Service program directory {Directory} opened with {Programs} program(s) and no results, "
                    + "{Partial} unfinished write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _programs.Count,
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many programs may be stored at once. Default 64.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets how many characters a program may be. Default 65536.</summary>
    /// <remarks>
    /// E5 puts no cap on <c>SPD</c>, so this is a store's own limit rather than the reference's -
    /// and it is what lets <see cref="InquireAsync"/> answer <see cref="GemGrant.NoSpace"/> before
    /// the text arrives, which is the entire point of the inquire. Counted in characters, as the
    /// in-memory store counts it, where the record holds the UTF-8 those characters encode to.
    /// </remarks>
    public int MaxLength { get; set; } = 64 * 1024;

    /// <summary>Gets or sets what runs a program, given its identifier and its text.</summary>
    /// <remarks>
    /// Returns the results text, or <see langword="null"/> for a program that would not run - which
    /// is the default, because a directory cannot run anything either. <strong>Not durable, and not
    /// state</strong>: this is the machine's own wiring, supplied on every start the way the
    /// directory is.
    /// </remarks>
    public Func<uint, string, string?> Run { get; set; } = static (_, _) => null;

    /// <summary>Gets how many programs are stored.</summary>
    public int Count => _programs.Count;

    /// <summary>Gets how many programs have results from a run in this process.</summary>
    /// <remarks>Zero on every open: a result is something the machine produced, and after a restart it has produced nothing.</remarks>
    public int ResultCount => _results.Count;

    /// <summary>Gets whether a program is held.</summary>
    /// <param name="id">Which program.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>The cheap question, where <see cref="LoadAsync"/> reads a file.</remarks>
    public bool Has(uint id) => _programs.ContainsKey(id);

    /// <summary>Gets whether a program has results from a run in this process.</summary>
    /// <param name="id">Which program.</param>
    /// <returns>Whether it has.</returns>
    public bool HasResults(uint id) => _results.ContainsKey(id);

    /// <inheritdoc />
    /// <remarks>
    /// The in-memory store's two answers and its reasoning: replacing a program this store already
    /// holds takes no new room, and <c>DuplicateOrInvalid</c> is deliberately not used for an
    /// overwrite, because a store that refused every one could never be updated. Nothing is
    /// remembered between the inquire and the send - like stream 7 and unlike streams 8, 13, 15 and
    /// 21, there is no grant here to end the way an interruption ends.
    /// </remarks>
    public ValueTask<GemGrant> InquireAsync(uint id, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (length < 0 || length > MaxLength)
        {
            return ValueTask.FromResult(GemGrant.NoSpace);
        }

        return ValueTask.FromResult(
            !_programs.ContainsKey(id) && _programs.Count >= Capacity
                ? GemGrant.NoSpace
                : GemGrant.Accepted);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The record goes down before the acknowledgement, so an interruption between the two leaves a
    /// program the next open adopts - the right way round, because the host was not told it arrived
    /// and will send it again, onto the same record.
    /// </remarks>
    public async ValueTask<GemServiceProgramAcknowledgement> SaveAsync(
        uint id,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (text.Length > MaxLength || (!_programs.ContainsKey(id) && _programs.Count >= Capacity))
            {
                return GemServiceProgramAcknowledgement.Error;
            }

            var sequence = _programs.TryGetValue(id, out var held) ? held : _next;

            await _records.WriteRecordAsync(
                sequence,
                [DurableRecordDirectory.Bytes(id)],
                Encoding.UTF8.GetBytes(text),
                cancellationToken).ConfigureAwait(false);

            _next = Math.Max(_next, sequence + 1);
            _programs[id] = sequence;

            // Whatever the old program produced is not this one's results, and leaving them would
            // answer S2F9 with the output of code that is no longer here - the in-memory store's
            // rule, and the smaller version of the reason a restart keeps no results at all.
            _results.TryRemove(id, out _);

            return GemServiceProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> LoadAsync(uint id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_programs.TryGetValue(id, out var sequence))
            {
                return null;
            }

            if (Read(_records.PathFor(sequence)) is not { } record)
            {
                // The index says it is here and the file says otherwise, so somebody has been in the
                // directory. S2F6 carries the text and no acknowledgement, so an empty one is the
                // whole of the refusal and the log is the only place this differs from a program a
                // host asked for by the wrong number.
                _logger.LogWarning(
                    "The record for service program {Id} could not be read, so there is no text to send.",
                    id);

                return null;
            }

            return record.Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the text off the disk and hands it to <see cref="Run"/>, so a machine that was
    /// restarted runs the program it actually holds rather than one it remembers. The results stay
    /// in memory.
    /// </remarks>
    public async ValueTask<GemServiceProgramAcknowledgement> RunAsync(uint id, CancellationToken cancellationToken)
    {
        var text = await LoadAsync(id, cancellationToken).ConfigureAwait(false);

        if (text is null)
        {
            return GemServiceProgramAcknowledgement.Error;
        }

        var results = Run(id, text);

        if (results is null)
        {
            return GemServiceProgramAcknowledgement.Error;
        }

        _results[id] = results;

        return GemServiceProgramAcknowledgement.Accepted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Null after a restart for every program, which is not a gap: the machine has run nothing, and
    /// a result it reported would be the output of a run that never happened. A host that wants
    /// results after a restart sends S2F7 again.
    /// </remarks>
    public ValueTask<string?> ResultsAsync(uint id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_results.TryGetValue(id, out var results) ? results : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered, so a host can diff two answers to S2F11. No gate: the questions that only read
        // cost nothing here.
        return ValueTask.FromResult<IReadOnlyList<uint>>([.. _programs.Keys.Order()]);
    }

    // ---------------------------------------------------------------- the files

    /// <summary>Reads one program's record.</summary>
    /// <remarks>
    /// The text is the record's payload decoded as UTF-8. <c>SPD</c> is an <c>A[n]</c> on the wire,
    /// which is one byte a character, so for anything a host can actually send the two are the same
    /// bytes - and a store handed a <see cref="string"/> by an application keeps whatever was in it.
    /// </remarks>
    private (uint Id, string Text)? Read(string path)
    {
        if (_records.TryReadRecord(path) is not { Fields.Count: 1, Tail: { } text } record)
        {
            return null;
        }

        return DurableRecordDirectory.Number(record.Fields[0]) is { } id
            ? (id, Encoding.UTF8.GetString(text.Span))
            : null;
    }
}
