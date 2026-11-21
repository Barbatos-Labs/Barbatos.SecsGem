// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// A real <see cref="ControlProgramHost"/> and a real <see cref="ControlProgramEquipment"/> on the
/// two ends of one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are the real thing, so a passing test has exercised the host building a body, the
/// frame crossing a pipe, the equipment parsing it, the store deciding, the reply coming back and
/// the host decoding it. An encoder tested against its own decoder proves only that the two agree.
/// </para>
/// <para>
/// It also records every stream and function the equipment is asked, which is what lets one test
/// assert that the host can send every primary the equipment declares.
/// </para>
/// </remarks>
internal sealed class ControlProgramPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private ControlProgramPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        ControlProgramHost host,
        ControlProgramEquipment equipment,
        IControlProgramStore store,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Store = store;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: the one that drives every transaction.</summary>
    public ControlProgramHost Host { get; }

    /// <summary>Gets the equipment end: the one that answers.</summary>
    public ControlProgramEquipment Equipment { get; }

    /// <summary>Gets the store behind the equipment.</summary>
    public IControlProgramStore Store { get; }

    /// <summary>Gets the store behind the equipment, as the in-memory one.</summary>
    /// <exception cref="InvalidOperationException">The pair was started with a store of the test's own.</exception>
    public ControlProgramInMemoryStore Memory => Store as ControlProgramInMemoryStore
        ?? throw new InvalidOperationException("This pair was started with a store that is not the in-memory one.");

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the equipment has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets what the equipment logged.</summary>
    public RecordingLogger<ControlProgramEquipment> Log { get; private init; } = new();

    /// <summary>Gets what the host logged.</summary>
    public RecordingLogger<ControlProgramHost> HostLog { get; private init; } = new();

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="store">The equipment's store. Null gives a fresh <see cref="ControlProgramInMemoryStore"/>.</param>
    /// <param name="answer">Replaces the equipment with a body the test writes itself.</param>
    /// <param name="alsoRoute">Extra pairs to route to the equipment, for the guard against a function outside its list.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// <paramref name="answer"/> is how the shapes this library tolerates but does not write get
    /// tested at all - a machine that sends its firmware as text, or a directory flat, or an
    /// <c>ACKC8</c> the item has no table for, has to be played by hand.
    /// </remarks>
    public static ControlProgramPair Start(
        IControlProgramStore? store = null,
        Func<SecsTransaction, SecsMessage?>? answer = null,
        IReadOnlyList<(byte Stream, byte Function)>? alsoRoute = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var programs = store ?? new ControlProgramInMemoryStore();

        // The host declares nothing - every message in stream 8 is a primary it sends or a reply to
        // one - so there is no route to register on this side at all.
        var hostRoutes = new SecsMessageRouterBuilder();

        ControlProgramEquipment? pendingEquipment = null;
        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in alsoRoute is null
            ? ControlProgramEquipment.HandledMessages
            : [.. ControlProgramEquipment.HandledMessages, .. alsoRoute])
        {
            equipmentRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answer is null)
                {
                    await pendingEquipment!.HandleAsync(transaction, token).ConfigureAwait(false);
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

        var hostLink = new HsmsConnection(options, active, hostRoutes.Build(), clock);
        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);

        var log = new RecordingLogger<ControlProgramEquipment>();
        var hostLog = new RecordingLogger<ControlProgramHost>();

        pendingEquipment = new ControlProgramEquipment(programs, log);

        return new ControlProgramPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            new ControlProgramHost(hostLink, hostLog),
            pendingEquipment,
            programs,
            asked)
        {
            Log = log,
            HostLog = hostLog,
        };
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => HostLink.IsSelected && EquipmentLink.IsSelected, "both ends to be selected");

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

        await HostLink.DisposeAsync().ConfigureAwait(false);
        await EquipmentLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
