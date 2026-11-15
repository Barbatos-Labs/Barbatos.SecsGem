// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Gem;

/// <summary>S7F2 PPGNT - whether the equipment will accept a process program it has been offered.</summary>
/// <remarks>
/// The point of asking first is that a process program can be large. S7F1 names the identifier and
/// the length, and the equipment gets to say no before the bytes are on the wire rather than after.
/// </remarks>
public enum GemProcessProgramGrant : byte
{
    /// <summary>Send it.</summary>
    Ok = 0,

    /// <summary>This one is already stored.</summary>
    AlreadyHave = 1,

    /// <summary>Not enough room for it.</summary>
    NoSpace = 2,

    /// <summary>The identifier is not one this equipment can accept.</summary>
    InvalidId = 3,

    /// <summary>Busy; ask again later.</summary>
    Busy = 4,

    /// <summary>Refused, and not because of space or the identifier.</summary>
    WillNotAccept = 5,
}

/// <summary>ACKC7 - the answer to a process program operation.</summary>
public enum GemProcessProgramAcknowledgement : byte
{
    /// <summary>Done.</summary>
    Accepted = 0,

    /// <summary>The send was not granted. A host that skipped S7F1 can be told so here.</summary>
    PermissionNotGranted = 1,

    /// <summary>The body length does not match what S7F1 said it would be.</summary>
    LengthError = 2,

    /// <summary>Matrix overflow.</summary>
    MatrixOverflow = 3,

    /// <summary>There is no process program with that identifier.</summary>
    NotFound = 4,

    /// <summary>The equipment does not support this mode.</summary>
    ModeUnsupported = 5,

    /// <summary>Accepted, and it will be done later.</summary>
    PerformedLater = 6,
}

/// <summary>
/// Where the equipment keeps its process programs.
/// </summary>
/// <remarks>
/// <para>
/// A process program is a recipe, and where recipes live is the application's business - a file on
/// disk, a row in a database, something a controller already owns. So this is an interface with a
/// usable default rather than a storage decision baked into the GEM layer.
/// </para>
/// <para>
/// Every method may be called from the connection's dispatch loop, so a slow one holds up every
/// other message on the link. Write a large program to disk in the background and answer
/// <see cref="GemProcessProgramAcknowledgement.PerformedLater"/> if it cannot be quick.
/// </para>
/// </remarks>
public interface IGemProcessProgramStore
{
    /// <summary>Decides whether to accept a program the host has offered.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="length">The length the host says the body will be.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The PPGNT to answer S7F1 with.</returns>
    ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken);

    /// <summary>Stores a program.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="body">PPBODY. Belongs to the incoming message: copy what you keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC7 to answer S7F3 with.</returns>
    ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);

    /// <summary>Reads a program back.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The body, or <see langword="null"/> when there is no such program.</returns>
    ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken);

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
/// A process program store that keeps everything in memory.
/// </summary>
/// <remarks>
/// Enough to be useful for a simulator, a test, or equipment whose recipes are small and
/// regenerated at start-up. Real equipment that must survive a restart wants its own
/// <see cref="IGemProcessProgramStore"/> over whatever it already persists.
/// </remarks>
public sealed class GemInMemoryProcessProgramStore : IGemProcessProgramStore
{
    /// <summary>E5's cap on the length of a PPID.</summary>
    public const int MaxIdLength = 120;

    private readonly ConcurrentDictionary<string, byte[]> _programs = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many programs may be stored at once.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets the largest body this store will accept.</summary>
    public int MaxBodyLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many programs are stored.</summary>
    public int Count => _programs.Count;

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        GemProcessProgramGrant grant;

        if (!IsValidId(id))
        {
            grant = GemProcessProgramGrant.InvalidId;
        }
        else if (_programs.ContainsKey(id))
        {
            // Not an error. The host is told it already sent this one, and may delete and resend.
            grant = GemProcessProgramGrant.AlreadyHave;
        }
        else if (length > MaxBodyLength || _programs.Count >= Capacity)
        {
            grant = GemProcessProgramGrant.NoSpace;
        }
        else
        {
            grant = GemProcessProgramGrant.Ok;
        }

        return ValueTask.FromResult(grant);
    }

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!IsValidId(id))
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.NotFound);
        }

        if (body.Length > MaxBodyLength)
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.LengthError);
        }

        if (!_programs.ContainsKey(id) && _programs.Count >= Capacity)
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.PermissionNotGranted);
        }

        // Copied, because the body belongs to the message it arrived in and that message is
        // disposed as soon as the handler returns.
        _programs[id] = body.ToArray();

        return ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        // Written out rather than as a conditional, because the obvious one-liner is wrong in a way
        // that compiles and tests clean on the happy path:
        //
        //     TryGetValue(id, out var body) ? body : null        // body is byte[]
        //
        // The ternary's type is byte[], so the null goes through the implicit byte[] to
        // ReadOnlyMemory<byte> conversion - which turns it into an EMPTY memory - and that is then
        // wrapped in a non-null nullable. "No such program" arrives as "a program of zero bytes",
        // and S7F6 answers with a body instead of refusing.
        if (!_programs.TryGetValue(id, out var stored))
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(stored);
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

    private static bool IsValidId(string id)
        => id.Length is > 0 and <= MaxIdLength;
}
