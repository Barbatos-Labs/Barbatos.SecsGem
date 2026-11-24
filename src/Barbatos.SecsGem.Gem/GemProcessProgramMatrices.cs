// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// Where the equipment keeps its process program matrices - the third capability of stream 7.
/// </summary>
/// <remarks>
/// <para>
/// E5 gives the matrix its own three transactions: S7F13 asks whether one may be sent, S7F11 sends
/// it, and S7F15 asks whether the machine holds the one the host has. There is <strong>no matrix
/// request and no matrix directory anywhere in the reference</strong>, which is why this interface
/// has no <c>ListAsync</c> and why these rows were refused here for a dozen chapters: "a store
/// nothing can read from is not a store".
/// </para>
/// <para>
/// <strong>That refusal looked in the wrong place.</strong> It searched the family for a row whose
/// name contains Request or Directory, found neither, and concluded the matrix can be written and
/// never read. But S7F15 <em>is</em> the read-back, in the only form the reference offers one: the
/// host holds the bytes already and asks whether the machine holds the same ones. This library
/// values exactly that at the other two halves of the stream - the formatted verify is served for
/// it, and its own remark says a program whose commands match but whose revision does not is not
/// the same program. A capability with no read-back at all is also already here, and has been
/// since stream 10: <see cref="IGemTerminal"/> has one method and nothing reads a terminal.
/// </para>
/// <para>
/// <strong>Separate from <see cref="IGemProcessProgramStore"/>, which is the other half of the
/// refusal answered.</strong> The matrix rows are item for item the plain ones, so sharing a store
/// would mean a matrix sent under <c>MATRIX-1</c> coming back from S7F5 as a plain process program.
/// It does not share one: S7F5 reads the unformatted store alone, S7F19 merges the unformatted and
/// formatted halves, and neither has ever seen this one. That is the same separation the formatted
/// half has had since it was written, and for the same reason - these are separate capabilities,
/// not two spellings of one.
/// </para>
/// <para>
/// So there is no delete and no directory here either, because the reference gives the family
/// neither and S7F17 and S7F19 say <em>process program</em> rather than <em>process program
/// matrix</em>. Wiring this store into those two would put matrix identifiers back in the answers
/// the separation exists to keep them out of. An application that has to clear a matrix does it
/// through its own store, which it owns.
/// </para>
/// <para>
/// <see cref="LoadAsync"/> is here because the verify needs it, and because the end that has to
/// read a matrix is the machine's own application - the producer-and-reader question the refusal
/// sweep settled, asked about a capability instead of about a field.
/// </para>
/// <para>
/// Every method may be called from the connection's dispatch loop, so a slow one holds up every
/// other message on the link. Answer
/// <see cref="GemProcessProgramAcknowledgement.PerformedLater"/> if the work cannot be quick.
/// </para>
/// </remarks>
public interface IGemProcessProgramMatrixStore
{
    /// <summary>Decides whether to accept a matrix the host has offered.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="length">The length the host says the body will be.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The PPGNT to answer S7F13 with.</returns>
    ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken);

    /// <summary>Stores a matrix.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="body">PPBODY. Belongs to the incoming message: copy what you keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC7 to answer S7F11 with.</returns>
    ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);

    /// <summary>Reads a matrix back, for the verify and for the application that owns it.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The body, or <see langword="null"/> when there is no such matrix.</returns>
    /// <remarks>
    /// Nothing on the wire calls this directly - the reference has no matrix request - so what it
    /// serves is S7F15 and whatever the machine does with a matrix once it has one.
    /// </remarks>
    ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// A process program matrix store that keeps everything in memory.
/// </summary>
/// <remarks>
/// Enough for a simulator, a test, or equipment whose matrices are small and regenerated at
/// start-up. Real equipment that must survive a restart wants its own
/// <see cref="IGemProcessProgramMatrixStore"/> over whatever it already persists.
/// </remarks>
public sealed class GemInMemoryProcessProgramMatrixStore : IGemProcessProgramMatrixStore
{
    /// <summary>E5's cap on the length of a PPID.</summary>
    public const int MaxIdLength = 120;

    private readonly ConcurrentDictionary<string, byte[]> _matrices = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many matrices may be stored at once.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets the largest body this store will accept.</summary>
    public int MaxBodyLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many matrices are stored.</summary>
    public int Count => _matrices.Count;

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        GemProcessProgramGrant grant;

        if (!IsValidId(id))
        {
            grant = GemProcessProgramGrant.InvalidId;
        }
        else if (_matrices.ContainsKey(id))
        {
            // Not an error, and the same answer the unformatted store gives: the host is told it
            // already sent this one. It may send it again, since a matrix has no delete to use
            // first - which is the one place this store's answer means something different from
            // its sibling's.
            grant = GemProcessProgramGrant.AlreadyHave;
        }
        else if (length > MaxBodyLength || _matrices.Count >= Capacity)
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

        if (!_matrices.ContainsKey(id) && _matrices.Count >= Capacity)
        {
            return ValueTask.FromResult(GemProcessProgramAcknowledgement.PermissionNotGranted);
        }

        // Copied, because the body belongs to the message it arrived in and that message is
        // disposed as soon as the handler returns.
        _matrices[id] = body.ToArray();

        return ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        // Written out rather than as a conditional, for the reason GemInMemoryProcessProgramStore
        // records at its own LoadAsync - and the first draft of this file used the one-liner anyway,
        // which is the argument for the comment being in both places:
        //
        //     TryGetValue(id, out var body) ? body : null        // body is byte[]
        //
        // The ternary's type is byte[], so the null takes the implicit byte[] to
        // ReadOnlyMemory<byte> conversion, becomes an EMPTY memory, and is wrapped in a non-null
        // nullable. "No such matrix" then arrives at S7F15 as "a matrix of zero bytes", and the
        // verify answers LengthError where it owes the host NotFound. It compiles, and it passes
        // every test that only verifies a matrix the machine really holds.
        if (!_matrices.TryGetValue(id, out var stored))
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(stored);
    }

    private static bool IsValidId(string id) => id.Length is > 0 and <= MaxIdLength;
}
