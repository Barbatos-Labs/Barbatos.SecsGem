// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// The open transactions on a link: what has been sent and is still waiting for an answer.
/// </summary>
/// <remarks>
/// <para>
/// Two tables, because the two kinds of transaction are answered differently and timed
/// differently. A data primary is answered by a secondary carrying an item tree and is timed by
/// T3. A control request is answered by a bare header and is timed by T6.
/// </para>
/// <para>
/// Both timeouts go through <see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/>
/// rather than a timer of their own, so there is no timer to leak and no callback to race: a
/// transaction that completes first simply never wakes its timeout, and a test drives both
/// instantly through <c>FakeTimeProvider</c>.
/// </para>
/// <para>
/// A waiter is always registered <em>before</em> the message is flushed. The reverse order is a
/// classic race on a fast link or an in-memory one: the reply arrives, finds no waiter, is
/// discarded as late, and the sender then waits out the full T3 for an answer it already had.
/// </para>
/// </remarks>
internal sealed class PendingTransactions
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<SecsMessage>> _data = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MessageHeader>> _control = new();
    private readonly TimeProvider _timeProvider;
    private int _lastSystemBytes;

    public PendingTransactions(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;

        // Start somewhere other than zero. Zero is what an uninitialised header carries, and a
        // transaction id that matches an uninitialised header is a debugging session nobody wants.
        _lastSystemBytes = Random.Shared.Next(1, int.MaxValue);
    }

    /// <summary>Gets how many data transactions are open.</summary>
    public int OpenDataTransactions => _data.Count;

    /// <summary>Allocates the next transaction id.</summary>
    /// <returns>A non-zero id, distinct from every other live one on this link.</returns>
    public int NextSystemBytes()
    {
        while (true)
        {
            var next = Interlocked.Increment(ref _lastSystemBytes);
            if (next != 0)
            {
                return next;
            }
        }
    }

    /// <summary>Registers a waiter for a data secondary, before the primary is sent.</summary>
    /// <param name="systemBytes">The transaction id.</param>
    /// <returns>The task the secondary will complete.</returns>
    public Task<SecsMessage> ExpectSecondary(int systemBytes)
    {
        var waiter = new TaskCompletionSource<SecsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _data[systemBytes] = waiter;
        return waiter.Task;
    }

    /// <summary>Registers a waiter for a control response, before the request is sent.</summary>
    /// <param name="systemBytes">The transaction id.</param>
    /// <returns>The task the response header will complete.</returns>
    public Task<MessageHeader> ExpectControlResponse(int systemBytes)
    {
        var waiter = new TaskCompletionSource<MessageHeader>(TaskCreationOptions.RunContinuationsAsynchronously);
        _control[systemBytes] = waiter;
        return waiter.Task;
    }

    /// <summary>Waits for a data secondary, under T3.</summary>
    /// <param name="pending">The task from <see cref="ExpectSecondary"/>.</param>
    /// <param name="systemBytes">The transaction id, so the waiter can be removed.</param>
    /// <param name="primary">The primary, to hang off the exception.</param>
    /// <param name="t3">The reply timeout.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The secondary. The caller owns it.</returns>
    /// <exception cref="SecsTimeoutException">T3 expired.</exception>
    public async Task<SecsMessage> AwaitSecondaryAsync(
        Task<SecsMessage> pending,
        int systemBytes,
        SecsMessage primary,
        TimeSpan t3,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pending.WaitAsync(t3, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SecsTimeoutException(SecsTimer.T3, t3, primary);
        }
        finally
        {
            _data.TryRemove(systemBytes, out _);
        }
    }

    /// <summary>Waits for a control response, under T6.</summary>
    /// <param name="pending">The task from <see cref="ExpectControlResponse"/>.</param>
    /// <param name="systemBytes">The transaction id, so the waiter can be removed.</param>
    /// <param name="t6">The control transaction timeout.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The response header.</returns>
    /// <exception cref="SecsTimeoutException">T6 expired.</exception>
    public async Task<MessageHeader> AwaitControlResponseAsync(
        Task<MessageHeader> pending,
        int systemBytes,
        TimeSpan t6,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pending.WaitAsync(t6, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SecsTimeoutException(SecsTimer.T6, t6);
        }
        finally
        {
            _control.TryRemove(systemBytes, out _);
        }
    }

    /// <summary>Hands a secondary to whoever is waiting for it.</summary>
    /// <param name="systemBytes">The transaction id from the secondary's header.</param>
    /// <param name="secondary">The secondary. Ownership passes to the waiter on success.</param>
    /// <returns><see langword="false"/> when nothing was waiting, in which case the caller still owns the message.</returns>
    /// <remarks>
    /// A secondary with no waiter is a reply that arrived after its T3 expired, or one the peer
    /// invented. Either way it is dropped rather than delivered as if it were a primary.
    /// </remarks>
    public bool TryCompleteSecondary(int systemBytes, SecsMessage secondary)
        => _data.TryRemove(systemBytes, out var waiter) && waiter.TrySetResult(secondary);

    /// <summary>Hands a control response to whoever is waiting for it.</summary>
    /// <param name="systemBytes">The transaction id from the response header.</param>
    /// <param name="response">The response header.</param>
    /// <returns><see langword="false"/> when nothing was waiting.</returns>
    public bool TryCompleteControl(int systemBytes, in MessageHeader response)
        => _control.TryRemove(systemBytes, out var waiter) && waiter.TrySetResult(response);

    /// <summary>Drops a waiter that was registered for a message that never went out.</summary>
    /// <param name="systemBytes">The transaction id.</param>
    /// <remarks>
    /// The waiter is registered before the flush, so a flush that throws leaves one behind. Without
    /// this it would sit in the table until the link closed, and the id could be answered by a
    /// later, unrelated message.
    /// </remarks>
    public void Forget(int systemBytes)
    {
        _data.TryRemove(systemBytes, out _);
        _control.TryRemove(systemBytes, out _);
    }

    /// <summary>Fails every open transaction, because the link has gone.</summary>
    /// <param name="error">What to fail them with.</param>
    /// <remarks>
    /// Called on every disconnection. Without it, a caller blocked in <c>SendAsync</c> waits out
    /// the whole of T3 for a reply that can no longer arrive, on a connection it can already see
    /// is down.
    /// </remarks>
    public void FailAll(Exception error)
    {
        foreach (var systemBytes in _data.Keys)
        {
            if (_data.TryRemove(systemBytes, out var waiter))
            {
                waiter.TrySetException(error);
            }
        }

        foreach (var systemBytes in _control.Keys)
        {
            if (_control.TryRemove(systemBytes, out var waiter))
            {
                waiter.TrySetException(error);
            }
        }
    }
}
