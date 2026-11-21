// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// A real <see cref="SubsystemHost"/> and a real <see cref="SubsystemEquipment"/> on the two ends
/// of one transport pair.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are the real thing, so a passing test has exercised one end building a body, the frame
/// crossing a pipe, the other end parsing it, the store deciding, the reply coming back and the
/// first end decoding it. An encoder tested against its own decoder proves only that the two agree.
/// </para>
/// <para>
/// Stream 18 has both ends sending primaries, so this harness records what <em>each</em> end was
/// asked - which is what lets one test assert the two declarations are disjoint rather than leaving
/// that claim in a comment.
/// </para>
/// </remarks>
internal sealed class SubsystemPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedEquipment;
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _askedHost;

    private SubsystemPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        SubsystemHost host,
        SubsystemEquipment equipment,
        ISubsystemStore store,
        ConcurrentQueue<(byte Stream, byte Function)> askedEquipment,
        ConcurrentQueue<(byte Stream, byte Function)> askedHost)
    {
        _active = active;
        _passive = passive;
        _askedEquipment = askedEquipment;
        _askedHost = askedHost;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Store = store;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: the one that asks, and that is told.</summary>
    public SubsystemHost Host { get; }

    /// <summary>Gets the equipment end: the one the subsystems are on.</summary>
    public SubsystemEquipment Equipment { get; }

    /// <summary>Gets the store behind the equipment.</summary>
    public ISubsystemStore Store { get; }

    /// <summary>Gets the store behind the equipment, as the in-memory one.</summary>
    /// <exception cref="InvalidOperationException">The pair was started with a store of the test's own.</exception>
    public SubsystemInMemoryStore Memory => Store as SubsystemInMemoryStore
        ?? throw new InvalidOperationException("This pair was started with a store that is not the in-memory one.");

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the equipment has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedEquipment => _askedEquipment;

    /// <summary>Gets every stream and function the host has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> AskedHost => _askedHost;

    /// <summary>Gets what the equipment logged.</summary>
    public RecordingLogger<SubsystemEquipment> EquipmentLog { get; private init; } = new();

    /// <summary>Gets what the host logged.</summary>
    /// <remarks>
    /// The host has several branches whose whole effect is a log line - a reply that answers for
    /// another subsystem, an <c>ALCD</c> category the table leaves to the equipment, a
    /// <c>CPACK</c> of zero - so the log is the only thing a test can hold them to.
    /// </remarks>
    public RecordingLogger<SubsystemHost> HostLog { get; private init; } = new();

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="store">The equipment's store. Null gives a fresh <see cref="SubsystemInMemoryStore"/>.</param>
    /// <param name="answerEquipment">Replaces the equipment with a body the test writes itself.</param>
    /// <param name="answerHost">Replaces the host with a body the test writes itself.</param>
    /// <param name="alsoRouteEquipment">Extra pairs to route to the equipment, for the guard against a function outside its list.</param>
    /// <param name="alsoRouteHost">Extra pairs to route to the host, for the same guard there.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// The two <c>answer</c> hooks are how the shapes this library tolerates but does not write get
    /// tested at all - a machine that nests its listing differently, or sends a code the table
    /// never named, has to be played by hand. Stream 18 needs one at each end, because each end
    /// reads replies the other writes.
    /// </remarks>
    public static SubsystemPair Start(
        ISubsystemStore? store = null,
        Func<SecsTransaction, SecsMessage?>? answerEquipment = null,
        Func<SecsTransaction, SecsMessage?>? answerHost = null,
        IReadOnlyList<(byte Stream, byte Function)>? alsoRouteEquipment = null,
        IReadOnlyList<(byte Stream, byte Function)>? alsoRouteHost = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var subsystems = store ?? new SubsystemInMemoryStore();

        SubsystemEquipment? pendingEquipment = null;
        SubsystemHost? pendingHost = null;

        ConcurrentQueue<(byte Stream, byte Function)> askedEquipment = new();
        ConcurrentQueue<(byte Stream, byte Function)> askedHost = new();

        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in alsoRouteEquipment is null
            ? SubsystemEquipment.HandledMessages
            : [.. SubsystemEquipment.HandledMessages, .. alsoRouteEquipment])
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
                Dispatch(askedEquipment, transaction, token, answerEquipment, t => pendingEquipment!.HandleAsync(t, token)));
        }

        var hostRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in alsoRouteHost is null
            ? SubsystemHost.HandledMessages
            : [.. SubsystemHost.HandledMessages, .. alsoRouteHost])
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) =>
                Dispatch(askedHost, transaction, token, answerHost, t => pendingHost!.HandleAsync(t, token)));
        }

        var hostLink = new HsmsConnection(options, active, hostRoutes.Build(), clock);
        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);

        var equipmentLog = new RecordingLogger<SubsystemEquipment>();
        var hostLog = new RecordingLogger<SubsystemHost>();

        pendingEquipment = new SubsystemEquipment(subsystems, equipmentLink, equipmentLog);
        pendingHost = new SubsystemHost(hostLink, hostLog);

        return new SubsystemPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            pendingHost,
            pendingEquipment,
            subsystems,
            askedEquipment,
            askedHost)
        {
            EquipmentLog = equipmentLog,
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

    private static async ValueTask Dispatch(
        ConcurrentQueue<(byte Stream, byte Function)> asked,
        SecsTransaction transaction,
        CancellationToken token,
        Func<SecsTransaction, SecsMessage?>? answer,
        Func<SecsTransaction, ValueTask> real)
    {
        asked.Enqueue((transaction.S, transaction.F));

        if (answer is null)
        {
            await real(transaction).ConfigureAwait(false);
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
    }
}
