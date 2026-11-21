// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// Where the equipment keeps its control programs, and where an arriving one goes.
/// </summary>
/// <remarks>
/// <para>
/// Six methods, one per primary the equipment end answers: decide whether a program may be sent,
/// take it, hand a named one back, list them, abort a transfer, and report a revision.
/// </para>
/// <para>
/// <strong>This is the arrival point, not a notification</strong> - the same choice
/// <c>IWaferMapStore</c>, <c>IDataSetStore</c> and <c>ISubsystemStore</c> make, for the same reason:
/// an application that cares about a program needs the program, and a store method already has it,
/// can refuse it, and is awaited before the acknowledgement goes back.
/// </para>
/// <para>
/// <strong>Half of these can refuse and half cannot.</strong> S8F2 carries a <c>GRANT</c> and S8F4
/// and S8F14 carry an <c>ACKC8</c>, so the inquire, the send and the abort all have somewhere to
/// put a no. S8F6, S8F12 and S8F16 carry no code at all - a program, a directory and a revision are
/// data and nothing else - so a program this machine does not have comes back as an empty body and
/// an unknown revision as an empty string.
/// </para>
/// <para>
/// Every method is awaited inside the transaction the host is waiting on, so none should be slow.
/// Writing a megabyte of firmware to flash while a host holds its T3 open is how a transfer times
/// out; keep the bytes and acknowledge.
/// </para>
/// </remarks>
public interface IControlProgramStore
{
    /// <summary>Decides whether a control program may be sent - S8F1.</summary>
    /// <param name="length">How long the host says it is - <c>LENGTH</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    /// <remarks>
    /// The cheap place to say no: after the grant, the next message is the whole program. The
    /// inquire carries <strong>only</strong> a length - no identifier - so this is being asked
    /// about the boot program and not about one of the named ones.
    /// </remarks>
    ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken cancellationToken);

    /// <summary>Takes the control program the host sent - S8F3.</summary>
    /// <param name="body">The program. Already copied out of the arriving message, so it is the store's to keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    /// <remarks>
    /// <strong>It arrives with no identifier</strong>, which is E5's shape and not an omission
    /// here: S8F3's whole body is a <c>CPBODY</c>. A machine has one boot program, so the send does
    /// not name it - while the request three functions later must. The asymmetry is worth knowing
    /// before designing a store around it.
    /// </remarks>
    ValueTask<ControlProgramAcknowledgement> SaveAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);

    /// <summary>Hands back a named control program - S8F5.</summary>
    /// <param name="controlProgramId">Which program - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its bytes, or <see langword="null"/> when there is no such program.</returns>
    /// <remarks><see langword="null"/> goes out as a zero-length <c>CPBODY</c>, because S8F6 has no code to refuse with.</remarks>
    ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string controlProgramId, CancellationToken cancellationToken);

    /// <summary>Lists the control programs this machine has - S8F11.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Their identifiers. Empty is a legitimate answer.</returns>
    /// <remarks>S8F11 carries no body, like the directory request in stream 7 and the one in stream 13.</remarks>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Aborts a transfer - S8F13.</summary>
    /// <param name="controlProgramId">Which program's transfer - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was aborted.</returns>
    /// <remarks>
    /// Aborting is not deleting. Stream 8 has no message that removes a control program from a
    /// machine - stream 7 has S7F17 for a process program and stream 8 has no equivalent - so one
    /// that deleted would be this library inventing a message.
    /// </remarks>
    ValueTask<ControlProgramAcknowledgement> AbortAsync(
        string controlProgramId,
        CancellationToken cancellationToken);

    /// <summary>Reports what revision a control program is - S8F15.</summary>
    /// <param name="controlProgramId">Which program - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its revision - <c>SOFTREV</c> - or <see langword="null"/> when there is no such program.</returns>
    /// <remarks>
    /// The one transaction stream 8 has that stream 7 does not, and the only place <c>SOFTREV</c>
    /// appears in this package. It is defined, as an <c>A[20]</c>.
    /// </remarks>
    ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken cancellationToken);
}

/// <summary>
/// A control program store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a controller that passes firmware straight on to whatever
/// flashes it. A machine whose programs are real writes its own store; the interface is six
/// methods, and there is no snapshot pair here on purpose - a control program is not state a
/// machine claims about itself, so it survives a restart through an implementation rather than
/// through a <c>Capture</c>, the same answer <c>IGemSpool</c> and <c>IDataSetStore</c> give.
/// </para>
/// <para>
/// <strong>The boot program is held under the empty identifier</strong>, which is this class's
/// choice and not E5's: S8F3 sends a program with no name, and something has to be done with it.
/// It is therefore <em>not</em> in the directory - S8F12 lists what a host can ask for by name, and
/// a nameless program is not one of those.
/// </para>
/// </remarks>
public sealed class ControlProgramInMemoryStore : IControlProgramStore
{
    /// <summary>The identifier the boot program is held under, because S8F3 does not carry one.</summary>
    public const string BootProgramId = "";

    private readonly ConcurrentDictionary<string, Held> _programs = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many named programs may be held, not counting the boot program. Default 16.</summary>
    /// <remarks>An inquire beyond this is refused with <see cref="ControlProgramGrant.NoSpace"/>, which is what the code is for.</remarks>
    public int Capacity { get; set; } = 16;

    /// <summary>Gets or sets how large a program may be, in bytes. Default 4 MiB. Zero means no limit.</summary>
    /// <remarks>
    /// Checked against the <c>LENGTH</c> the inquire declares, which is the one number in stream 8
    /// a store can act on before the bytes arrive - and, unlike stream 13, a length this package
    /// <em>can</em> compare afterwards, because <c>CPBODY</c> has a format and therefore a byte
    /// count. A mismatch is refused with <see cref="ControlProgramAcknowledgement.Denied"/>, the
    /// only refusal <c>ACKC8</c> has.
    /// </remarks>
    public int MaxLength { get; set; } = 4 * 1024 * 1024;

    /// <summary>Gets how many programs are held, the boot program included.</summary>
    public int Count => _programs.Count;

    /// <summary>Puts a named program in without a transaction, for a simulator or a test.</summary>
    /// <param name="controlProgramId">What it is called.</param>
    /// <param name="body">Its bytes.</param>
    /// <param name="revision">What revision it is - <c>SOFTREV</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controlProgramId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// How a machine offers a program a host can go and fetch, which stream 8 has no message for:
    /// there is no "I have a new control program" primary, so a host finds out with S8F11.
    /// </remarks>
    public void Add(string controlProgramId, ReadOnlyMemory<byte> body, string revision = "")
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        _programs[controlProgramId] = new Held(body, revision);
    }

    /// <summary>Gets a program, or <see langword="null"/> when there is none.</summary>
    /// <param name="controlProgramId">Which program. Empty for the boot program.</param>
    /// <returns>It.</returns>
    public ControlProgram? Find(string controlProgramId)
        => _programs.TryGetValue(controlProgramId, out var held)
            ? new ControlProgram { Id = controlProgramId, Body = held.Body }
            : null;

    /// <summary>Gets how long the last inquire said the program would be, or <see langword="null"/> when none has been granted.</summary>
    /// <remarks>Kept so <see cref="SaveAsync"/> can compare it with what arrives - see <see cref="MaxLength"/>.</remarks>
    public uint? GrantedLength { get; private set; }

    /// <inheritdoc />
    public ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MaxLength > 0 && length > (uint)MaxLength)
        {
            return ValueTask.FromResult(ControlProgramGrant.NoSpace);
        }

        if (!_programs.ContainsKey(BootProgramId) && _programs.Count >= Capacity + 1)
        {
            return ValueTask.FromResult(ControlProgramGrant.NoSpace);
        }

        GrantedLength = length;

        return ValueTask.FromResult(ControlProgramGrant.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<ControlProgramAcknowledgement> SaveAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (GrantedLength is null)
        {
            // Nothing was granted, so nothing was offered. ACKC8 has one refusal and no way to say
            // which of these happened, which is the limit of the item rather than of this store.
            return ValueTask.FromResult(ControlProgramAcknowledgement.Denied);
        }

        if (GrantedLength != (uint)body.Length)
        {
            // The check stream 13 could not make: there, LENGTH had nothing to measure against
            // because DSDATA has no format. Here CPBODY is a B, so the bytes have a count.
            GrantedLength = null;

            return ValueTask.FromResult(ControlProgramAcknowledgement.Denied);
        }

        GrantedLength = null;
        _programs[BootProgramId] = new Held(body, string.Empty);

        return ValueTask.FromResult(ControlProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> LoadAsync(
        string controlProgramId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
            _programs.TryGetValue(controlProgramId, out var held) ? held.Body : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sorted, so a host can diff two listings - and the boot program left out, because it has
        // no name for a host to ask with.
        IReadOnlyList<string> identifiers =
        [
            .. _programs.Keys
                .Where(id => id.Length > 0)
                .Order(StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(identifiers);
    }

    /// <inheritdoc />
    public ValueTask<ControlProgramAcknowledgement> AbortAsync(
        string controlProgramId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);
        cancellationToken.ThrowIfCancellationRequested();

        if (GrantedLength is null)
        {
            return ValueTask.FromResult(ControlProgramAcknowledgement.Denied);
        }

        // Aborting forgets the grant and keeps whatever is already stored: stream 8 has no message
        // that deletes a program, so an abort that threw one away would be inventing one.
        GrantedLength = null;

        return ValueTask.FromResult(ControlProgramAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _programs.TryGetValue(controlProgramId, out var held) && held.Revision.Length > 0
                ? held.Revision
                : null);
    }

    /// <summary>A held program: its bytes and what revision it is.</summary>
    private sealed record Held(ReadOnlyMemory<byte> Body, string Revision);
}
