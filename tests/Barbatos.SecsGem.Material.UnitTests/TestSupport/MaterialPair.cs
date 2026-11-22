// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// A real <see cref="MaterialEquipment"/> and a real <see cref="MaterialHost"/> on the two ends of
/// one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are the real thing, so a passing test has exercised one end building a body, the
/// frame crossing a pipe, the other end parsing it, the answer coming back and the first end
/// decoding it. In stream 3 that matters in both directions, because both ends send primaries -
/// the host asks five questions and the equipment reports four things unasked.
/// </para>
/// <para>
/// Either end can be played by hand instead, which is the only way to exercise a body shape or a
/// format this library tolerates but does not write. Given how little of stream 3 the catalogue
/// pins down, that is not a corner of the test suite here; it is half of it.
/// </para>
/// </remarks>
internal sealed class MaterialPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedOfEquipment;
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedOfHost;

    private MaterialPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        MaterialHost host,
        MaterialEquipment equipment,
        MaterialInMemorySource source,
        MaterialHandoff sender,
        MaterialHandoff receiver,
        ConcurrentQueue<(byte Stream, byte Function)> askedOfEquipment,
        ConcurrentQueue<(byte Stream, byte Function)> askedOfHost)
    {
        _active = active;
        _passive = passive;
        _askedOfEquipment = askedOfEquipment;
        _askedOfHost = askedOfHost;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Source = source;
        Sender = sender;
        Receiver = receiver;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: the one that asks.</summary>
    public MaterialHost Host { get; }

    /// <summary>Gets the equipment end: the one that answers, and reports.</summary>
    public MaterialEquipment Equipment { get; }

    /// <summary>Gets the inventory behind the equipment.</summary>
    public MaterialInMemorySource Source { get; }

    /// <summary>Gets the handoff end on the host's link.</summary>
    /// <remarks>
    /// Called the sender because a test has to call it something, and <strong>the names are the
    /// test's choice rather than the link's</strong>: stream 4's handoff is symmetric, so either
    /// end may offer material and either may ask for it. One test swaps the two to prove that is
    /// true of this implementation and not just of the catalogue.
    /// </remarks>
    public MaterialHandoff Sender { get; }

    /// <summary>Gets the handoff end on the equipment's link.</summary>
    public MaterialHandoff Receiver { get; }

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the equipment has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedOfEquipment => _askedOfEquipment;

    /// <summary>Gets every stream and function the host has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedOfHost => _askedOfHost;

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="source">The equipment's inventory. Null gives a fresh empty one.</param>
    /// <param name="equipmentAnswer">Replaces the equipment with bodies the test writes itself.</param>
    /// <param name="hostAnswer">Replaces the host with bodies the test writes itself.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// The host takes the active end of the link, which is the usual way round: a host connects to
    /// a machine that is listening.
    /// </remarks>
    public static MaterialPair Start(
        MaterialInMemorySource? source = null,
        Func<SecsTransaction, SecsMessage?>? equipmentAnswer = null,
        Func<SecsTransaction, SecsMessage?>? hostAnswer = null,
        ILogger<MaterialHandoff>? receiverLogger = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var inventory = source ?? new MaterialInMemorySource();

        // The cycle the registrations solve with a deferred factory, written out by hand because a
        // test has no container.
        MaterialEquipment? pendingEquipment = null;
        MaterialHost? pendingHost = null;
        MaterialHandoff? pendingSender = null;
        MaterialHandoff? pendingReceiver = null;

        ConcurrentQueue<(byte Stream, byte Function)> askedOfEquipment = new();
        ConcurrentQueue<(byte Stream, byte Function)> askedOfHost = new();

        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in MaterialEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                askedOfEquipment.Enqueue((transaction.S, transaction.F));

                return Answer(transaction, token, equipmentAnswer, pendingEquipment!.HandleAsync);
            });
        }

        var hostRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in MaterialHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                askedOfHost.Enqueue((transaction.S, transaction.F));

                return Answer(transaction, token, hostAnswer, pendingHost!.HandleAsync);
            });
        }

        // And stream 4 on both routers, with the same nine primaries each - which is the whole
        // point of it: the catalogue marks every handoff row as travelling either way, so neither
        // end is the one that answers.
        foreach (var (stream, function) in MaterialHandoff.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                askedOfEquipment.Enqueue((transaction.S, transaction.F));

                return Answer(transaction, token, equipmentAnswer, pendingReceiver!.HandleAsync);
            });

            hostRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                askedOfHost.Enqueue((transaction.S, transaction.F));

                return Answer(transaction, token, hostAnswer, pendingSender!.HandleAsync);
            });
        }

        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);
        var hostLink = new HsmsConnection(options, active, hostRoutes.Build(), clock);

        pendingEquipment = new MaterialEquipment(inventory, equipmentLink);
        pendingHost = new MaterialHost(hostLink);
        pendingSender = new MaterialHandoff(hostLink);
        pendingReceiver = new MaterialHandoff(equipmentLink, receiverLogger);

        return new MaterialPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            pendingHost,
            pendingEquipment,
            inventory,
            pendingSender,
            pendingReceiver,
            askedOfEquipment,
            askedOfHost);
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
        // 400 and not 5,000, for the reason the Gem harnesses give: Task.Delay(1) waits about
        // 15.6 ms on Windows, so the old bound was 78 seconds per wait that never comes - free
        // when a test passes and ruinous when one does not. This suite was spending 77 seconds on
        // three failures and reporting nothing the whole time.
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

        await HostLink.DisposeAsync().ConfigureAwait(false);
        await EquipmentLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private static async ValueTask Answer(
        SecsTransaction transaction,
        CancellationToken token,
        Func<SecsTransaction, SecsMessage?>? scripted,
        Func<SecsTransaction, CancellationToken, ValueTask> real)
    {
        if (scripted is null)
        {
            await real(transaction, token).ConfigureAwait(false);
            return;
        }

        var reply = scripted(transaction);

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
    }
}
