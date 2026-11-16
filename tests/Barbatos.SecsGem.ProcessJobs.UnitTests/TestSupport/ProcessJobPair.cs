// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// A real <see cref="ProcessJobEquipment"/> and a real <see cref="ProcessJobHost"/> on one
/// transport pair, with object services on the equipment half.
/// </summary>
/// <remarks>
/// <para>
/// Both halves, because E40 needs both: stream 16 drives a job and stream 14 describes it. A
/// harness with only the twelve stream 16 primaries would be testing a job nobody can configure.
/// </para>
/// <para>
/// The state machine itself is tested with none of this, in <c>ProcessJobManagerTests</c>. What a
/// link adds is the part a link can break: a body that does not decode, a refusal that has to
/// survive being turned into a <c>CMDA</c> and read back, and the only timestamp in these four
/// standards.
/// </para>
/// </remarks>
internal sealed class ProcessJobPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private ProcessJobPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        ProcessJobHost host,
        ProcessJobEquipment equipment,
        ProcessJobManager manager,
        ObjectServices hostObjects,
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
        Manager = manager;
        HostObjects = hostObjects;
        Clock = clock;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: the one that asks and commands.</summary>
    public ProcessJobHost Host { get; }

    /// <summary>Gets the equipment end.</summary>
    public ProcessJobEquipment Equipment { get; }

    /// <summary>Gets the state machines behind the equipment.</summary>
    public ProcessJobManager Manager { get; }

    /// <summary>Gets the host's object services, for describing a job over stream 14.</summary>
    public ObjectServices HostObjects { get; }

    /// <summary>Gets the clock the equipment stamps an S16F11 with.</summary>
    /// <remarks>
    /// A fake one, and its own rather than the connections': E40 is the only standard in this group
    /// with a timestamp on the wire, and a test that asserted sixteen characters against a wall
    /// clock would be a test that fails at midnight.
    /// </remarks>
    public FakeTimeProvider Clock { get; }

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
    /// <param name="configure">Sets the machine up before anything is asked of it.</param>
    /// <param name="at">What the equipment's clock says, for the one message that carries a time.</param>
    /// <param name="answer">Replaces the equipment's stream 16 replies with bodies the test writes itself.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// <paramref name="answer"/> is how the shapes this library tolerates but does not write get
    /// tested. With none of stream 16's twenty-eight rows firm, those are most of what there is to
    /// test: a <c>JOBSTATE</c> in text, a code the catalogue reserves, an acknowledgement in a
    /// format this end would never have chosen.
    /// </remarks>
    public static ProcessJobPair Start(
        Action<ProcessJobManager>? configure = null,
        DateTimeOffset? at = null,
        Func<SecsTransaction, SecsMessage?>? answer = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var linkClock = new FakeTimeProvider();
        var equipmentClock = new FakeTimeProvider(at ?? new DateTimeOffset(2026, 11, 16, 19, 2, 6, 120, TimeSpan.Zero));
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var manager = new ProcessJobManager();

        configure?.Invoke(manager);

        ProcessJobEquipment? pendingEquipment = null;
        ObjectServices? pendingEquipmentObjects = null;
        ProcessJobHost? pendingHost = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in ProcessJobEquipment.HandledMessages)
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

        // And stream 14, out of the same state machines: this is how a host sets PRRECIPEMETHOD.
        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                return pendingEquipmentObjects!.HandleAsync(transaction, token);
            });
        }

        var hostRoutes = new SecsMessageRouterBuilder();

        // The host declares two, which is what E40 gives it - the first host in this stack to
        // declare any at all since stream 3.
        foreach (var (stream, function) in ProcessJobHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) =>
                pendingHost!.HandleAsync(transaction, token));
        }

        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), linkClock);
        var hostLink = new HsmsConnection(options, active, hostRoutes.Build(), linkClock);

        pendingEquipment = new ProcessJobEquipment(manager, equipmentLink, equipmentClock);
        pendingEquipmentObjects = new ObjectServices(equipmentLink, new ProcessJobObjectProvider(manager));
        pendingHost = new ProcessJobHost(hostLink);

        return new ProcessJobPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            pendingHost,
            pendingEquipment,
            manager,
            new ObjectServices(hostLink),
            equipmentClock,
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => EquipmentLink.IsSelected && HostLink.IsSelected, "both ends to be selected");

    /// <summary>Gets a job the manager must have.</summary>
    /// <param name="processJobId">Which one.</param>
    /// <returns>The job.</returns>
    public ProcessJob Job(string processJobId = "PJ-1")
    {
        var job = Manager.Find(processJobId);

        Assert.NotNull(job);

        return job;
    }

    /// <summary>Asserts that the equipment was asked a message.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="function">The function.</param>
    public void AssertAsked(byte stream, byte function) => Assert.Contains((stream, function), _asked);

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
