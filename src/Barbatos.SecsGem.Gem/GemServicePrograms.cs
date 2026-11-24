// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Gem;

/// <summary><c>GRANT</c> - the answer to an inquire, at S2F2 and S2F40.</summary>
/// <remarks>
/// <para>
/// The fourth reader of <c>GRANT</c> in this repository, after
/// <c>Barbatos.SecsGem.Material</c>, <c>Barbatos.SecsGem.ControlPrograms</c> and
/// <c>Barbatos.SecsGem.ItemTransfer</c>. The members are those three's, name for name, and
/// <c>GemAcknowledgementsAgreeWithTheReferenceTests</c> checks every one of them against the
/// reference's own table rather than against another package's enum - which is what keeps the
/// agreement transitive now that four packages read one item.
/// </para>
/// <para>
/// <strong>Not <see cref="GemMultiBlockGrant"/>, which is <c>GRANT6</c>.</strong> Two items, two
/// tables, two types: <c>GRANT</c> names four codes and reserves 4 to 63, where <c>GRANT6</c>
/// names three and reserves 3 to 63. The extra code is <see cref="DuplicateOrInvalid"/>, which a
/// stream 6 grant has no room for because nothing there is being named.
/// </para>
/// </remarks>
public enum GemGrant : byte
{
    /// <summary>OK, will accept.</summary>
    Accepted = 0,

    /// <summary>Busy, try again.</summary>
    Busy = 1,

    /// <summary>No space available.</summary>
    NoSpace = 2,

    /// <summary>Duplicate identifier, or one this equipment will not take.</summary>
    DuplicateOrInvalid = 3,

    /// <summary>A code E5 reserves for the equipment and does not name.</summary>
    Other = 4,
}

/// <summary><c>ACKC2</c> - the answer to a service program send or run, S2F4 and S2F8.</summary>
/// <remarks>
/// <para>
/// <strong><c>ACKC2</c> is not in the reference's dictionary at all</strong> - no format and no
/// table - so this is an inference, and the route is the one
/// <c>Barbatos.SecsGem.ControlPrograms</c> took for <c>ACKC8</c>: read as <c>B[1]</c> with nought
/// accepted, off the eight <c>ACKCn</c> items the reference *does* define. Those eight are
/// <c>ACKC3</c>, <c>ACKC5</c>, <c>ACKC6</c>, <c>ACKC7</c>, <c>ACKC7A</c>, <c>ACKC10</c>,
/// <c>ACKC13</c> and <c>ACKC15</c>, and every one of them is a <c>B[1]</c> whose zero is
/// acceptance. <c>SecsCatalogStream2Tests</c> asserts that, so the inference is checked rather
/// than asserted in prose.
/// </para>
/// <para>
/// <strong>Two members, because an inference should not invent a vocabulary.</strong> What the
/// siblings agree on is that zero is acceptance; what they disagree on is everything else -
/// <c>ACKC5</c> and <c>ACKC6</c> reserve the whole non-zero range while <c>ACKC7</c> names six
/// refusals. So this says accepted or not accepted and no more, and the arriving byte is on
/// <see cref="GemServiceProgramResult.Code"/> for a fab whose machine means something by it.
/// </para>
/// </remarks>
public enum GemServiceProgramAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Not accepted, with no reason the reference can carry.</summary>
    Error = 1,
}

/// <summary>What a service program send or run was answered with, and the byte it arrived as.</summary>
/// <remarks>
/// The pair exists because <c>ACKC2</c> is undefined: this library reads nought as acceptance and
/// everything else as a refusal, and a fab whose machine distinguishes its refusals needs the code
/// that actually arrived. <see cref="Acknowledgement"/> is what to branch on;
/// <see cref="Code"/> is what to log.
/// </remarks>
/// <param name="Acknowledgement">What this library made of it.</param>
/// <param name="Code">The <c>ACKC2</c> byte as it arrived.</param>
public readonly record struct GemServiceProgramResult(
    GemServiceProgramAcknowledgement Acknowledgement,
    byte Code)
{
    /// <summary>Gets whether the machine accepted.</summary>
    public bool IsAccepted => Acknowledgement == GemServiceProgramAcknowledgement.Accepted;
}

/// <summary>
/// Where a piece of equipment keeps its service programs - E5's stream 2, S2F1 to S2F12.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A service program is not a recipe.</strong> The reference types <c>SPD</c> as
/// <c>A[n]</c>, where stream 7's <c>PPBODY</c> and stream 8's <c>CPBODY</c> are both <c>B</c> - so
/// a service program is <em>text</em> where a process program and a control program are opaque
/// bytes, and this interface takes a <see cref="string"/> rather than a buffer because that is
/// what the reference says travels.
/// </para>
/// <para>
/// <strong>And it runs.</strong> Stream 7 can send a recipe, fetch it, verify it and delete it;
/// stream 2 can send a service program, fetch it, <em>run</em> it and read what it produced.
/// S2F7 and S2F9 have no stream 7 equivalent at all, which is the whole reason this is a separate
/// capability rather than a second flavour of
/// <see cref="IGemProcessProgramStore"/>.
/// </para>
/// <para>
/// Identified by a number: <c>SPID</c> is a <c>U4</c>, where <c>PPID</c> is an <c>A[120]</c>. So
/// there is no identifier length to validate here and no character set to worry about.
/// </para>
/// </remarks>
public interface IGemServiceProgramStore
{
    /// <summary>Answers S2F1 - may the host send program <paramref name="id"/>.</summary>
    /// <param name="id">SPID.</param>
    /// <param name="length">LENGTH, in characters, as the host declared it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>GRANT.</returns>
    ValueTask<GemGrant> InquireAsync(uint id, int length, CancellationToken cancellationToken);

    /// <summary>Takes the program the host granted itself permission to send - S2F3.</summary>
    /// <param name="id">The SPID from the inquire this send answers.</param>
    /// <param name="text">SPD.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC2.</returns>
    /// <remarks>
    /// <strong>S2F3 carries no SPID</strong>, only the text, so the identifier is the one from the
    /// S2F1 that preceded it. That correlation is the handshake's, not this library's invention -
    /// but it does mean a host that sends S2F3 without an inquire is refused, because there is
    /// nothing to file the text under.
    /// </remarks>
    ValueTask<GemServiceProgramAcknowledgement> SaveAsync(
        uint id,
        string text,
        CancellationToken cancellationToken);

    /// <summary>Hands back a program - S2F5.</summary>
    /// <param name="id">SPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>SPD, or <see langword="null"/> for a program this machine does not have.</returns>
    ValueTask<string?> LoadAsync(uint id, CancellationToken cancellationToken);

    /// <summary>Runs a program - S2F7.</summary>
    /// <param name="id">SPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC2.</returns>
    /// <remarks>
    /// Accepted means the machine started it, not that it finished: S2F9 is how a host asks what
    /// it produced. Nothing in the row carries a duration, so a store that would block belongs
    /// behind its own queue.
    /// </remarks>
    ValueTask<GemServiceProgramAcknowledgement> RunAsync(uint id, CancellationToken cancellationToken);

    /// <summary>Hands back what a run produced - S2F9.</summary>
    /// <param name="id">SPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>SPR, or <see langword="null"/> for a program that has not run.</returns>
    ValueTask<string?> ResultsAsync(uint id, CancellationToken cancellationToken);

    /// <summary>Lists what this machine holds - S2F11.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every SPID, in whatever order the store keeps them.</returns>
    ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>A service program store in memory, for a machine with nowhere else to put them.</summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="GemInMemoryProcessProgramStore"/> and useful for the same two
/// things: getting a machine talking before its storage exists, and tests.
/// </para>
/// <para>
/// <strong>It cannot run anything</strong>, so <see cref="Run"/> refuses by default. That is the
/// same way round as <c>GemEquipment.AnswerExceptionAbort</c> and for the same reason: running a
/// service program is something the machine does, and a store that was never told how to run one
/// would be claiming a program had started when nothing started. Set <see cref="Run"/> to the
/// thing that actually runs them.
/// </para>
/// </remarks>
public sealed class GemInMemoryServiceProgramStore : IGemServiceProgramStore
{
    private readonly ConcurrentDictionary<uint, string> _programs = new();
    private readonly ConcurrentDictionary<uint, string> _results = new();

    /// <summary>Gets or sets how many programs may be stored at once.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets how many characters a program may be.</summary>
    /// <remarks>
    /// E5 puts no cap on <c>SPD</c>, so this one is a store's own limit rather than the
    /// reference's - and it is what lets <see cref="InquireAsync"/> answer
    /// <see cref="GemGrant.NoSpace"/> before the text arrives, which is the entire point of the
    /// inquire.
    /// </remarks>
    public int MaxLength { get; set; } = 64 * 1024;

    /// <summary>Gets or sets what runs a program, given its identifier and its text.</summary>
    /// <remarks>
    /// Returns the results text, or <see langword="null"/> for a program that would not run - which
    /// is the default, because nothing in memory can run anything.
    /// </remarks>
    public Func<uint, string, string?> Run { get; set; } = static (_, _) => null;

    /// <inheritdoc />
    public ValueTask<GemGrant> InquireAsync(uint id, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (length < 0 || length > MaxLength)
        {
            return ValueTask.FromResult(GemGrant.NoSpace);
        }

        // Replacing a program this store already holds is allowed and takes no new room, so the
        // capacity check is only for a program that is new. DuplicateOrInvalid is deliberately not
        // used for that: E5 offers it, and a store that refused every overwrite could never be
        // updated.
        return ValueTask.FromResult(
            !_programs.ContainsKey(id) && _programs.Count >= Capacity
                ? GemGrant.NoSpace
                : GemGrant.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<GemServiceProgramAcknowledgement> SaveAsync(
        uint id,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (text.Length > MaxLength || (!_programs.ContainsKey(id) && _programs.Count >= Capacity))
        {
            return ValueTask.FromResult(GemServiceProgramAcknowledgement.Error);
        }

        _programs[id] = text;

        // Whatever the old program produced is not this one's results. Leaving them would answer
        // S2F9 with the output of code that is no longer here.
        _results.TryRemove(id, out _);

        return ValueTask.FromResult(GemServiceProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<string?> LoadAsync(uint id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_programs.TryGetValue(id, out var text) ? text : null);
    }

    /// <inheritdoc />
    public ValueTask<GemServiceProgramAcknowledgement> RunAsync(uint id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_programs.TryGetValue(id, out var text))
        {
            return ValueTask.FromResult(GemServiceProgramAcknowledgement.Error);
        }

        var results = Run(id, text);

        if (results is null)
        {
            return ValueTask.FromResult(GemServiceProgramAcknowledgement.Error);
        }

        _results[id] = results;

        return ValueTask.FromResult(GemServiceProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<string?> ResultsAsync(uint id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_results.TryGetValue(id, out var results) ? results : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<uint>>([.. _programs.Keys]);
    }
}
