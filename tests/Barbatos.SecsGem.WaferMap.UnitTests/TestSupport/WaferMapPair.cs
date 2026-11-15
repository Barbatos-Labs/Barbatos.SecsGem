// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.WaferMap.UnitTests;

/// <summary>
/// A real <see cref="WaferMapEquipment"/> and a real <see cref="WaferMapHost"/> on the two ends of
/// one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are the real thing, so a passing test has exercised the equipment building a body, the
/// frame crossing a pipe, the host parsing it, the store deciding, the reply coming back and the
/// equipment decoding it. A map encoder tested against its own decoder proves only that the two
/// agree, and in this package they were written by the same hand in the same hour.
/// </para>
/// <para>
/// It also records every stream and function the host is asked, which is what lets one test assert
/// that the equipment can send every primary the host declares - rather than that claim living in
/// a comment.
/// </para>
/// </remarks>
internal sealed class WaferMapPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private WaferMapPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection equipmentLink,
        HsmsConnection hostLink,
        WaferMapEquipment equipment,
        WaferMapHost host,
        IWaferMapStore store,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        EquipmentLink = equipmentLink;
        HostLink = hostLink;
        Equipment = equipment;
        Host = host;
        Store = store;

        Running = Task.WhenAll(
            equipmentLink.RunAsync(_cts.Token),
            hostLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the equipment end: the one that sends and asks.</summary>
    public WaferMapEquipment Equipment { get; }

    /// <summary>Gets the host end: the one that answers.</summary>
    public WaferMapHost Host { get; }

    /// <summary>Gets the store behind the host.</summary>
    public IWaferMapStore Store { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the host has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="store">The host's store. Null gives a fresh <see cref="WaferMapInMemoryStore"/>.</param>
    /// <param name="answer">
    /// Replaces the host with a body the test writes itself. Null routes every primary to the real
    /// <see cref="WaferMapHost"/>, which is what most tests want.
    /// </param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// <para>
    /// The equipment takes the active end of the link, which is the unusual way round and the right
    /// one here: in stream 12 the equipment is the end that initiates, and E37 lets either end
    /// connect.
    /// </para>
    /// <para>
    /// <paramref name="answer"/> is how the formats this library tolerates but does not write get
    /// tested at all. Two real ends can only ever exercise what this library itself emits; a
    /// machine that nests its map data differently, or sends a count as a <c>U4</c>, has to be
    /// played by hand. When it is supplied, <see cref="Host"/> is still built but nothing reaches
    /// it.
    /// </para>
    /// </remarks>
    public static WaferMapPair Start(
        IWaferMapStore? store = null,
        Func<SecsTransaction, SecsMessage?>? answer = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var maps = store ?? new WaferMapInMemoryStore();

        // The cycle both registrations solve with a deferred factory, written out by hand because
        // a test has no container: the end needs the connection, the connection needs the router,
        // and the router is what the end is being registered into.
        WaferMapEquipment? pendingEquipment = null;
        WaferMapHost? pendingHost = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in WaferMapEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
                pendingEquipment!.HandleAsync(transaction, token));
        }

        var hostRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answer is null)
                {
                    await pendingHost!.HandleAsync(transaction, token).ConfigureAwait(false);
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

        var equipmentLink = new HsmsConnection(options, active, equipmentRoutes.Build(), clock);
        var hostLink = new HsmsConnection(options, passive, hostRoutes.Build(), clock);

        pendingEquipment = new WaferMapEquipment(equipmentLink);
        pendingHost = new WaferMapHost(maps, hostLink);

        return new WaferMapPair(
            active,
            passive,
            equipmentLink,
            hostLink,
            pendingEquipment,
            pendingHost,
            maps,
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => EquipmentLink.IsSelected && HostLink.IsSelected, "both ends to be selected");

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

        await EquipmentLink.DisposeAsync().ConfigureAwait(false);
        await HostLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
