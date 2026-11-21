// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// A real <see cref="GemHost"/> and a real <see cref="GemEquipment"/> on the two ends of one
/// transport pair.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GemLink"/> puts the test in the host's chair and has it write the messages by hand,
/// which is the right harness for testing the equipment. This one is for testing the host: both
/// ends are the real thing, so a passing test has exercised the host building a body, the frame
/// crossing a pipe, the equipment parsing it, the reply coming back and the host decoding it. A
/// host tested against a mock equipment proves only that the mock agreed with the host, and the
/// two were written by the same hand on the same afternoon.
/// </para>
/// <para>
/// It also records every stream and function the equipment is asked, which is what lets one test
/// assert that the host can send every primary the equipment declares - rather than that claim
/// living in a comment.
/// </para>
/// </remarks>
internal sealed class GemPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private GemPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        GemHost host,
        GemEquipment equipment,
        FakeTimeProvider clock,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;
        Clock = clock;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token),
            equipment.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host under test.</summary>
    public GemHost Host { get; }

    /// <summary>Gets the equipment it is talking to.</summary>
    public GemEquipment Equipment { get; }

    /// <summary>Gets the host's connection.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the equipment's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the clock everything runs on.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Gets the task both connections and the equipment loop are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the equipment has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="model">What the equipment publishes. Null gives <see cref="GemLink.DefaultModel"/>.</param>
    /// <param name="gem">Adjusts the equipment's options.</param>
    /// <param name="host">Adjusts the host's options.</param>
    /// <param name="programs">The equipment's process program store, or null for none.</param>
    /// <param name="formatted">The equipment's formatted process program store, or null for none.</param>
    /// <param name="spool">The equipment's spool, or null for none.</param>
    /// <returns>The pair.</returns>
    public static GemPair Start(
        Action<GemEquipmentModelBuilder>? model = null,
        Action<GemOptions>? gem = null,
        Action<GemHostOptions>? host = null,
        IGemProcessProgramStore? programs = null,
        IGemFormattedProcessProgramStore? formatted = null,
        IGemSpool? spool = null,
        IGemTerminal? terminal = null,
        bool traces = false,
        bool limits = false)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var gemOptions = new GemOptions
        {
            ModelName = "BRBTOS-1",
            SoftwareRevision = "0.1.0",

            // On-line and remote, because that is the state a host does anything in: off-line
            // equipment answers S1F1 with a zero-length list and refuses remote commands, which
            // would make most of these tests a test of being off-line.
            DefaultControlState = GemControlState.OnlineRemote,
        };

        gem?.Invoke(gemOptions);

        var hostOptions = new GemHostOptions();
        host?.Invoke(hostOptions);

        var builder = new GemEquipmentModelBuilder();
        (model ?? GemLink.DefaultModel).Invoke(builder);

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        // The cycle both registrations solve with a deferred factory, written out twice by hand
        // because a test has no container.
        GemEquipment? pendingEquipment = null;
        GemHost? pendingHost = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        List<(byte Stream, byte Function)> declared = [.. GemEquipment.HandledMessages];

        if (programs is not null)
        {
            declared.AddRange(GemEquipment.ProcessProgramMessages);
        }

        if (formatted is not null)
        {
            declared.AddRange(GemEquipment.FormattedProcessProgramMessages);
        }

        if (spool is not null)
        {
            declared.AddRange(GemEquipment.SpoolMessages);
        }

        if (terminal is not null)
        {
            declared.AddRange(GemEquipment.TerminalMessages);
        }

        if (traces)
        {
            declared.AddRange(GemEquipment.TraceMessages);
        }

        if (limits)
        {
            declared.AddRange(GemEquipment.LimitMessages);
        }

        foreach (var (stream, function) in declared)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));
                return pendingEquipment!.HandleAsync(transaction, token);
            });
        }

        var hostRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in GemHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) => pendingHost!.HandleAsync(transaction, token));
        }

        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);
        var hostLink = new HsmsConnection(options, active, hostRoutes.Build(), clock);

        pendingEquipment = new GemEquipment(
            gemOptions,
            builder.Build(),
            equipmentLink,
            clock,
            logger: null,
            programs,
            spool,
            formatted,
            terminal);

        pendingHost = new GemHost(hostOptions, hostLink, clock);

        return new GemPair(active, passive, hostLink, equipmentLink, pendingHost, pendingEquipment, clock, asked);
    }

    /// <summary>Waits for the link to select and for both ends to be communicating.</summary>
    /// <returns>A task that completes once they are.</returns>
    /// <remarks>
    /// Nothing drives this but the two objects themselves: the active transport selects, the
    /// equipment sends its own S1F13 when it sees Selected, and the host's routing table answers
    /// it. So reaching this state at all is the first test.
    /// </remarks>
    public async Task OpenAsync()
    {
        await WaitUntilAsync(
            () => HostLink.IsSelected && EquipmentLink.IsSelected,
            "both ends to be selected");

        await WaitUntilAsync(() => Equipment.IsCommunicating, "the equipment to be communicating");
        await WaitUntilAsync(() => Host.IsCommunicating, "the host to be communicating");
    }

    /// <summary>Waits for a condition, polling rather than sleeping on a wall clock.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What is being waited for, for the failure message.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        // 400 and not 5,000. Task.Delay(1) waits about 15.6 ms on Windows, so the old bound was 78
        // seconds per wait that never comes - free when a test passes and ruinous when one does
        // not. A negative control that breaks an event makes four of these wait at once, and the
        // run was being killed by its own timeout instead of reporting which tests noticed. Both
        // ends of a test here are in-process pipes, so six seconds is already generous.
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

    /// <summary>Advances the fake clock in steps until a condition holds.</summary>
    /// <param name="step">How far to advance each time.</param>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What is being waited for, for the failure message.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    public async Task AdvanceUntilAsync(TimeSpan step, Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            Clock.Advance(step);
            await Task.Delay(1, Token).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for {what}, after advancing the clock by {step * 400}.");
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

        await Host.DisposeAsync().ConfigureAwait(false);
        await Equipment.DisposeAsync().ConfigureAwait(false);
        await HostLink.DisposeAsync().ConfigureAwait(false);
        await EquipmentLink.DisposeAsync().ConfigureAwait(false);
        await _active.DisposeAsync().ConfigureAwait(false);
        await _passive.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
