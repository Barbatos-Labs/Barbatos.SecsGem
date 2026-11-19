// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// A host and an equipment on the two ends of one transport pair, for E39's enhanced stream 2.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ObjectPair"/> this one has a direction, because the stream does: every one of
/// the eight primaries is marked host to equipment, so there is an asking end and an answering
/// end and they are not interchangeable.
/// </para>
/// <para>
/// One end can be played by hand with <c>answer</c>. With fourteen of these sixteen catalogue rows
/// marked as a representative subset, the shapes this library tolerates but does not itself write
/// are most of what there is to test.
/// </para>
/// </remarks>
internal sealed class EnhancedPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private EnhancedPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        EnhancedServicesHost host,
        EnhancedServicesEquipment equipment,
        EnhancedInMemoryProvider provider,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Provider = provider;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the end under test: the one that asks.</summary>
    public EnhancedServicesHost Host { get; }

    /// <summary>Gets the end that answers out of <see cref="Provider"/>.</summary>
    public EnhancedServicesEquipment Equipment { get; }

    /// <summary>Gets the machine's per-object data.</summary>
    public EnhancedInMemoryProvider Provider { get; }

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

    /// <summary>Starts a pair.</summary>
    /// <param name="configure">Fills the machine's data before the link opens.</param>
    /// <param name="answer">Replaces the equipment with bodies the test writes itself.</param>
    /// <param name="provider">A provider of the test's own, in place of the in-memory one.</param>
    /// <returns>The pair.</returns>
    public static EnhancedPair Start(
        Action<EnhancedInMemoryProvider>? configure = null,
        Func<SecsTransaction, SecsMessage?>? answer = null,
        IEnhancedServiceProvider? provider = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var store = new EnhancedInMemoryProvider();

        configure?.Invoke(store);

        EnhancedServicesEquipment? pending = null;
        ConcurrentQueue<(byte Stream, byte Function)> asked = new();

        // The host declares nothing, which is what the registration does: a host answers no
        // primary in this range.
        var hostRoutes = new SecsMessageRouterBuilder();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answer is null)
                {
                    await pending!.HandleAsync(transaction, token).ConfigureAwait(false);
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

        pending = new EnhancedServicesEquipment(provider ?? store);

        return new EnhancedPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            new EnhancedServicesHost(hostLink),
            pending,
            store,
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => ObjectPair.WaitUntilAsync(() => HostLink.IsSelected && EquipmentLink.IsSelected, "both ends to be selected");

    /// <summary>Asserts that the equipment was asked a particular primary.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="function">The function.</param>
    public void AssertAsked(byte stream, byte function)
        => Assert.Contains((stream, function), _asked);

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
