// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// Two real <see cref="ItemTransferPeer"/> instances on the two ends of one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Two of the same type, because stream 21 has one kind of end: every row travels either way, so
/// there is no host and no equipment to put on the right side. Each peer has its own store, and a
/// test can drive either direction.
/// </para>
/// <para>
/// It records what each end was asked, which is what lets one test assert that a peer can send
/// every primary a peer declares - in both directions, since the set is the same one.
/// </para>
/// </remarks>
internal sealed class ItemPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedRight;
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedLeft;

    private ItemPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection leftLink,
        HsmsConnection rightLink,
        ItemTransferPeer left,
        ItemTransferPeer right,
        ItemInMemoryStore leftStore,
        ItemInMemoryStore rightStore,
        ConcurrentQueue<(byte Stream, byte Function)> askedRight,
        ConcurrentQueue<(byte Stream, byte Function)> askedLeft)
    {
        _active = active;
        _passive = passive;
        _askedRight = askedRight;
        _askedLeft = askedLeft;
        LeftLink = leftLink;
        RightLink = rightLink;
        Left = left;
        Right = right;
        LeftStore = leftStore;
        RightStore = rightStore;

        Running = Task.WhenAll(
            leftLink.RunAsync(_cts.Token),
            rightLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the end on the active side of the link.</summary>
    public ItemTransferPeer Left { get; }

    /// <summary>Gets the end on the passive side.</summary>
    public ItemTransferPeer Right { get; }

    /// <summary>Gets the left end's own items.</summary>
    public ItemInMemoryStore LeftStore { get; }

    /// <summary>Gets the right end's own items.</summary>
    public ItemInMemoryStore RightStore { get; }

    /// <summary>Gets the left end's connection.</summary>
    public HsmsConnection LeftLink { get; }

    /// <summary>Gets the right end's connection.</summary>
    public HsmsConnection RightLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the right end has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedRight => _askedRight;

    /// <summary>Gets every stream and function the left end has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedLeft => _askedLeft;

    /// <summary>Gets what the left end logged.</summary>
    public RecordingLogger<ItemTransferPeer> LeftLog { get; private init; } = new();

    /// <summary>Gets what the right end logged.</summary>
    /// <remarks>
    /// Both ends record, because this package has branches whose whole effect is a log line at the
    /// <em>answering</em> end - a body it could not read, a function it does not implement, a
    /// message from a stream that is not its own. Asserting those needs the log of the end that
    /// received the message, which is the right one for every hand-written send.
    /// </remarks>
    public RecordingLogger<ItemTransferPeer> RightLog { get; private init; } = new();

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="answerRight">Replaces the right end with a body the test writes itself.</param>
    /// <param name="alsoRouteRight">Extra pairs to route to the right end, for the guard against a function outside the six.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// <paramref name="answerRight"/> is how the shapes this library tolerates but does not write
    /// get tested at all: a machine that sends a directory flat, or an acknowledgement code the
    /// item has no table for, has to be played by hand.
    /// </remarks>
    public static ItemPair Start(
        Func<SecsTransaction, SecsMessage?>? answerRight = null,
        IReadOnlyList<(byte Stream, byte Function)>? alsoRouteRight = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var leftStore = new ItemInMemoryStore();
        var rightStore = new ItemInMemoryStore();

        ItemTransferPeer? pendingLeft = null;
        ItemTransferPeer? pendingRight = null;

        ConcurrentQueue<(byte Stream, byte Function)> askedRight = new();
        ConcurrentQueue<(byte Stream, byte Function)> askedLeft = new();

        // Both ends declare the same six, which is the one list in this stack besides stream 4's
        // that is both what a type sends and what it answers.
        var leftRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in ItemTransferPeer.HandledMessages)
        {
            leftRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                askedLeft.Enqueue((transaction.S, transaction.F));
                return pendingLeft!.HandleAsync(transaction, token);
            });
        }

        var rightRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in alsoRouteRight is null
            ? ItemTransferPeer.HandledMessages
            : [.. ItemTransferPeer.HandledMessages, .. alsoRouteRight])
        {
            rightRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                askedRight.Enqueue((transaction.S, transaction.F));

                if (answerRight is null)
                {
                    await pendingRight!.HandleAsync(transaction, token).ConfigureAwait(false);
                    return;
                }

                var reply = answerRight(transaction);

                if (reply is null)
                {
                    return;
                }

                using (reply)
                {
                    if (transaction.ReplyExpected)
                    {
                        await transaction.ReplyAsync(reply, token).ConfigureAwait(false);
                    }
                }
            });
        }

        var leftLink = new HsmsConnection(options, active, leftRoutes.Build(), clock);
        var rightLink = new HsmsConnection(options, passive, rightRoutes.Build(), clock);

        var leftLog = new RecordingLogger<ItemTransferPeer>();
        var rightLog = new RecordingLogger<ItemTransferPeer>();

        pendingLeft = new ItemTransferPeer(leftStore, leftLink, leftLog);
        pendingRight = new ItemTransferPeer(rightStore, rightLink, rightLog);

        return new ItemPair(
            active,
            passive,
            leftLink,
            rightLink,
            pendingLeft,
            pendingRight,
            leftStore,
            rightStore,
            askedRight,
            askedLeft)
        {
            LeftLog = leftLog,
            RightLog = rightLog,
        };
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => LeftLink.IsSelected && RightLink.IsSelected, "both ends to be selected");

    /// <summary>Waits for a condition, polling rather than sleeping on a wall clock.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What is being waited for, for the failure message.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    /// <remarks>
    /// Four hundred attempts rather than the five thousand the earlier packages use, because
    /// <c>Task.Delay(1)</c> on Windows waits a system timer tick - about fifteen milliseconds, not
    /// one. Five thousand of those is seventy-eight seconds for a wait that will never come good,
    /// which is long enough to look like a hang; four hundred is still six seconds of patience for
    /// a transport that answers in a millisecond.
    /// </remarks>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1, Token).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Stops everything and waits for the loops to finish.</summary>
    /// <returns>A task that completes once the pair is down.</returns>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: that is how the loops stop.
        }
        catch (TimeoutException)
        {
            Assert.Fail("A loop did not stop within ten seconds of being cancelled.");
        }

        await LeftLink.DisposeAsync().ConfigureAwait(false);
        await RightLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
