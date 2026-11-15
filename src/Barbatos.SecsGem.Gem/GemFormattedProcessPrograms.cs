// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Gem;

/// <summary>One command inside a formatted process program: a CCODE and the PPARMs it takes.</summary>
/// <remarks>
/// <para>
/// E5 types CCODE as <c>U1[1]</c>, which is why <see cref="Code"/> is a byte. A host that sends the
/// code in a wider unsigned format is still understood as long as the value fits; one that does not
/// fit is answered with <see cref="GemProcessProgramAcknowledgement.ModeUnsupported"/> rather than
/// truncated into a different command.
/// </para>
/// <para>
/// A PPARM is a string here. E5 lets one be any item at all, and this stack carries the text case
/// and refuses the rest - see <see cref="IGemFormattedProcessProgramStore"/> for why that is a
/// refusal rather than a best effort.
/// </para>
/// </remarks>
public sealed class GemProcessProgramCommand
{
    /// <summary>Initialises a command.</summary>
    /// <param name="code">CCODE.</param>
    /// <param name="parameters">The PPARMs, in order. Copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/>, or one of them, is <see langword="null"/>.</exception>
    public GemProcessProgramCommand(byte code, IReadOnlyList<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var copy = new string[parameters.Count];

        for (var i = 0; i < parameters.Count; i++)
        {
            copy[i] = parameters[i] ?? throw new ArgumentNullException(
                nameof(parameters),
                $"PPARM {i} is null. A parameter the equipment cannot write back is not a parameter.");
        }

        Code = code;
        Parameters = copy;
    }

    /// <summary>Gets the command code.</summary>
    public byte Code { get; }

    /// <summary>Gets the parameters, in the order they appeared.</summary>
    /// <remarks>
    /// Not trimmed. A fixed-width PPARM that arrived space-padded goes back to the host padded the
    /// same way, because S7F26 has to return what S7F23 sent and a trim is not reversible.
    /// </remarks>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>Returns the code and how many parameters it carries.</summary>
    /// <returns>For example <c>CCODE 12 (3 parameters)</c>.</returns>
    public override string ToString() => $"CCODE {Code} ({Parameters.Count} parameters)";
}

/// <summary>
/// A formatted process program: S7F23's body as data rather than as an opaque block of bytes.
/// </summary>
/// <remarks>
/// <para>
/// The difference between this and the unformatted program behind <see cref="IGemProcessProgramStore"/>
/// is the whole point of there being two halves to stream 7. An unformatted program is a PPBODY -
/// bytes the equipment stores and hands back, whose meaning is between the host and the machine. A
/// formatted one is structure the standard defines: an identifier, the model and software revision
/// it was written for, and a list of commands.
/// </para>
/// <para>
/// E5's body is a list of four: PPID, MDLN, SOFTREV, and a list of commands, each of which is a
/// CCODE and a list of PPARMs. That shape is what this type holds, in that order.
/// </para>
/// <para>
/// <see cref="ModelName"/> and <see cref="SoftwareRevision"/> are the host's, not the equipment's.
/// Nothing here checks them against <see cref="GemOptions.ModelName"/>: whether a machine accepts a
/// recipe written for a different model is a decision for the store, which gets the whole program
/// and can answer <see cref="GemProcessProgramAcknowledgement.ModeUnsupported"/>.
/// </para>
/// </remarks>
public sealed class GemFormattedProcessProgram
{
    /// <summary>Initialises a formatted process program.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="modelName">MDLN, as the host sent it.</param>
    /// <param name="softwareRevision">SOFTREV, as the host sent it.</param>
    /// <param name="commands">The commands, in order. Copied.</param>
    /// <exception cref="ArgumentNullException">An argument, or one of the commands, is <see langword="null"/>.</exception>
    public GemFormattedProcessProgram(
        string id,
        string modelName,
        string softwareRevision,
        IReadOnlyList<GemProcessProgramCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(softwareRevision);
        ArgumentNullException.ThrowIfNull(commands);

        var copy = new GemProcessProgramCommand[commands.Count];

        for (var i = 0; i < commands.Count; i++)
        {
            copy[i] = commands[i] ?? throw new ArgumentNullException(
                nameof(commands),
                $"Command {i} is null. A program with a hole in it cannot be written back to the host.");
        }

        Id = id;
        ModelName = modelName;
        SoftwareRevision = softwareRevision;
        Commands = copy;
    }

    /// <summary>Gets the identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the model the program was written for.</summary>
    public string ModelName { get; }

    /// <summary>Gets the software revision the program was written for.</summary>
    public string SoftwareRevision { get; }

    /// <summary>Gets the commands, in the order they appeared.</summary>
    public IReadOnlyList<GemProcessProgramCommand> Commands { get; }

    /// <summary>Returns the identifier, what it was written for, and how long it is.</summary>
    /// <returns>For example <c>RECIPE-7 (BRBTOS 1.0.0, 3 commands)</c>.</returns>
    public override string ToString()
        => $"{Id} ({ModelName} {SoftwareRevision}, {Commands.Count} commands)";
}

/// <summary>
/// Where the equipment keeps its formatted process programs.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IGemProcessProgramStore"/> because the two halves of stream 7 are
/// separate capabilities, not two spellings of one. Equipment may offer either, both, or neither,
/// and a store for structured recipes rarely wants to be the same code as a store for opaque ones.
/// </para>
/// <para>
/// There is no <c>InquireAsync</c>. The formatted load inquire is S7F33, which SEMI E5 does not
/// define with the confidence it defines S7F1 - the reference this repository's catalogue was built
/// from flags it as representative - so it is not implemented rather than guessed at.
/// </para>
/// <para>
/// <see cref="DeleteAsync"/>, <see cref="DeleteAllAsync"/> and <see cref="ListAsync"/> exist
/// because S7F17 and S7F19 are not per-format: a host deleting <c>RECIPE-7</c> or asking what is
/// stored means both halves. The equipment merges whichever stores it has, so a formatted program
/// appears in S7F20 beside the unformatted ones.
/// </para>
/// <para>
/// Every method may be called from the connection's dispatch loop, so a slow one holds up every
/// other message on the link. Answer
/// <see cref="GemProcessProgramAcknowledgement.PerformedLater"/> if the work cannot be quick.
/// </para>
/// </remarks>
public interface IGemFormattedProcessProgramStore
{
    /// <summary>Stores a program.</summary>
    /// <param name="program">The program. Owns its own data, so it is safe to keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC7 to answer S7F23 with.</returns>
    /// <remarks>
    /// Unlike the unformatted <c>SaveAsync</c>, nothing here borrows the inbound message: the
    /// program was read out of it into strings before this call, so there is no copy to remember to
    /// make.
    /// </remarks>
    ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken);

    /// <summary>Reads a program back.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The program, or <see langword="null"/> when there is no such program.</returns>
    ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes a program.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC7 to answer S7F17 with.</returns>
    ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes every program.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC7 to answer S7F17 with.</returns>
    /// <remarks>What an S7F17 carrying an empty list means. E5 is explicit that it means all of them.</remarks>
    ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken);

    /// <summary>Lists what is stored.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every PPID, in whatever order the store keeps them.</returns>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A formatted process program store that keeps everything in memory.
/// </summary>
/// <remarks>
/// Enough for a simulator, a test, or equipment whose recipes are small and regenerated at
/// start-up. Real equipment that must survive a restart wants its own
/// <see cref="IGemFormattedProcessProgramStore"/> over whatever it already persists - and a
/// formatted program is far easier to persist than an unformatted one, because it is structure
/// rather than bytes.
/// </remarks>
public sealed class GemInMemoryFormattedProcessProgramStore : IGemFormattedProcessProgramStore
{
    private readonly ConcurrentDictionary<string, GemFormattedProcessProgram> _programs =
        new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many programs may be stored at once.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many programs are stored.</summary>
    public int Count => _programs.Count;

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Id.Length is 0 or > GemInMemoryProcessProgramStore.MaxIdLength)
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.NotFound);
        }

        if (!_programs.ContainsKey(program.Id) && _programs.Count >= Capacity)
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.PermissionNotGranted);
        }

        // No copy: the program is immutable and owns its own strings, which is the one real
        // advantage a formatted program has over a PPBODY borrowed from the inbound message.
        _programs[program.Id] = program;

        return ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return ValueTask.FromResult(_programs.TryGetValue(id, out var program) ? program : null);
    }

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        return ValueTask.FromResult(_programs.TryRemove(id, out _)
            ? GemProcessProgramAcknowledgement.Accepted
            : GemProcessProgramAcknowledgement.NotFound);
    }

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)
    {
        _programs.Clear();
        return ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<string>>(_programs.Keys.Order(StringComparer.Ordinal).ToArray());
}
