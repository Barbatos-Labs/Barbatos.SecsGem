// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// A real <see cref="CarrierEquipment"/> and a real <see cref="CarrierHost"/> on one transport
/// pair, with object services on both halves.
/// </summary>
/// <remarks>
/// <para>
/// Both halves, because E87 needs both: the actions move the state machines over stream 3 and the
/// attributes - the slot map among them - are read over stream 14. A harness with only the
/// carrier primaries would be testing half a standard.
/// </para>
/// <para>
/// The state machines themselves are tested without any of this, in
/// <c>CarrierManagerTests</c>. What a link adds is the part a link can break: a body that does not
/// decode, a refusal that has to survive being turned into <c>CAACK</c> and read back, and an
/// attribute that has to survive being an item.
/// </para>
/// </remarks>
internal sealed class CarrierPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private CarrierPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        CarrierHost host,
        CarrierEquipment equipment,
        CarrierManager manager,
        ObjectServices hostObjects,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Manager = manager;
        HostObjects = hostObjects;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: the one that decides.</summary>
    public CarrierHost Host { get; }

    /// <summary>Gets the equipment end.</summary>
    public CarrierEquipment Equipment { get; }

    /// <summary>Gets the state machines behind the equipment.</summary>
    public CarrierManager Manager { get; }

    /// <summary>Gets the host's object services, for reading carrier attributes over stream 14.</summary>
    public ObjectServices HostObjects { get; }

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the equipment has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair with one port, in service.</summary>
    /// <param name="answer">Replaces the equipment with bodies the test writes itself.</param>
    /// <param name="configure">Sets the machine up before anything is asked of it.</param>
    /// <returns>The pair.</returns>
    public static CarrierPair Start(
        Func<SecsTransaction, SecsMessage?>? answer = null,
        Action<CarrierManager>? configure = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var manager = new CarrierManager();

        manager.AddPort(1);
        manager.SetServiceStatus(1, inService: true);

        configure?.Invoke(manager);

        CarrierEquipment? pendingEquipment = null;
        ObjectServices? pendingEquipmentObjects = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in CarrierEquipment.HandledMessages)
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

        // And stream 14, out of the same state machines: this is how a host reads a slot map.
        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                return pendingEquipmentObjects!.HandleAsync(transaction, token);
            });
        }

        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);

        // The host declares nothing at all, which is what E87 gives it.
        var hostLink = new HsmsConnection(options, active, new SecsMessageRouterBuilder().Build(), clock);

        pendingEquipment = new CarrierEquipment(manager);
        pendingEquipmentObjects = new ObjectServices(equipmentLink, new CarrierObjectProvider(manager));

        return new CarrierPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            new CarrierHost(hostLink),
            pendingEquipment,
            manager,
            new ObjectServices(hostLink),
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => EquipmentLink.IsSelected && HostLink.IsSelected, "both ends to be selected");

    /// <summary>Puts a verified carrier on port 1, the way a machine would.</summary>
    /// <param name="carrierId">What to call it.</param>
    /// <returns>The carrier.</returns>
    public Carrier LoadVerifiedCarrier(string carrierId = "CAR-001")
    {
        Assert.True(Manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(Manager.PlaceCarrier(1, carrierId).IsAccepted);
        Assert.True(Manager.ReadCarrierId(carrierId, verified: true).IsAccepted);
        Assert.True(Manager.ReadSlotMap(carrierId, new byte[] { 1, 1, 0, 1 }, verified: true).IsAccepted);

        var carrier = Manager.FindCarrier(carrierId);

        Assert.NotNull(carrier);

        return carrier;
    }

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
