// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// Two real <see cref="ObjectServices"/> on the two ends of one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Stream 14 has no equipment and no host, so neither does this: there is an end that asks and an
/// end that answers, and <see cref="Start"/> can give the asking end a provider too, which is how
/// the claim that either end may do either gets tested rather than asserted in a comment.
/// </para>
/// <para>
/// One end can be played by hand instead. With twenty-seven of this stream's twenty-eight
/// catalogue rows marked as a representative subset, the shapes this library tolerates but does
/// not write are most of what there is to test.
/// </para>
/// </remarks>
internal sealed class ObjectPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private ObjectPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection askerLink,
        HsmsConnection answererLink,
        ObjectServices asker,
        ObjectServices answerer,
        ObjectInMemoryProvider provider,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        AskerLink = askerLink;
        AnswererLink = answererLink;
        Asker = asker;
        Answerer = answerer;
        Provider = provider;

        Running = Task.WhenAll(
            askerLink.RunAsync(_cts.Token),
            answererLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the end under test: the one that asks.</summary>
    public ObjectServices Asker { get; }

    /// <summary>Gets the end that answers out of <see cref="Provider"/>.</summary>
    public ObjectServices Answerer { get; }

    /// <summary>Gets the objects the answering end has.</summary>
    public ObjectInMemoryProvider Provider { get; }

    /// <summary>Gets the asking end's connection.</summary>
    public HsmsConnection AskerLink { get; }

    /// <summary>Gets the answering end's connection.</summary>
    public HsmsConnection AnswererLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the answering end has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="provider">The answering end's objects. Null gives a fresh empty provider.</param>
    /// <param name="answer">Replaces the answering end with bodies the test writes itself.</param>
    /// <param name="askerProvider">Gives the asking end objects of its own, so it can be asked as well.</param>
    /// <returns>The pair.</returns>
    public static ObjectPair Start(
        ObjectInMemoryProvider? provider = null,
        Func<SecsTransaction, SecsMessage?>? answer = null,
        IObjectProvider? askerProvider = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var objects = provider ?? new ObjectInMemoryProvider();

        ObjectServices? pendingAsker = null;
        ObjectServices? pendingAnswerer = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var askerRoutes = new SecsMessageRouterBuilder();

        // The asking end declares the stream only when it has objects of its own, which is what
        // the registration does too.
        if (askerProvider is not null)
        {
            foreach (var (stream, function) in ObjectServices.HandledMessages)
            {
                askerRoutes.OnPrimary(stream, function, (transaction, token) =>
                    pendingAsker!.HandleAsync(transaction, token));
            }
        }

        var answererRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            answererRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answer is null)
                {
                    await pendingAnswerer!.HandleAsync(transaction, token).ConfigureAwait(false);
                    return;
                }

                var reply = answer(transaction);

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

        var askerLink = new HsmsConnection(options, active, askerRoutes.Build(), clock);
        var answererLink = new HsmsConnection(options, passive, answererRoutes.Build(), clock);

        pendingAsker = new ObjectServices(askerLink, askerProvider);
        pendingAnswerer = new ObjectServices(answererLink, objects);

        return new ObjectPair(
            active,
            passive,
            askerLink,
            answererLink,
            pendingAsker,
            pendingAnswerer,
            objects,
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => AskerLink.IsSelected && AnswererLink.IsSelected, "both ends to be selected");

    /// <summary>Waits for a condition, polling rather than sleeping on a wall clock.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What is being waited for, for the failure message.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 5_000; attempt++)
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

        await AskerLink.DisposeAsync().ConfigureAwait(false);
        await AnswererLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
