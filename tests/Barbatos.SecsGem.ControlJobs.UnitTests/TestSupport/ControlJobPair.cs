// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// A real <see cref="ControlJobManager"/> on one end of a transport pair and a host on the other,
/// with nothing between them but stream 14.
/// </summary>
/// <remarks>
/// <para>
/// E94 has no equipment class and no host class in this library, because it needs neither: the
/// machine is a state machine exposed as objects, and the host is <see cref="ObjectServices"/>.
/// This harness is that arrangement, and the tests over it are the only ones that prove the claim
/// the package is built on - that a host can run a control job without a single message outside
/// stream 14.
/// </para>
/// <para>
/// The state machine itself is tested with none of this, in <c>ControlJobManagerTests</c>. What a
/// link adds is the part a link can break: a body that does not decode, a command that has to
/// survive being an <c>A</c> item and come back as an <c>OBJACK</c>, and an attribute that has to
/// survive being an item at all.
/// </para>
/// </remarks>
internal sealed class ControlJobPair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private ControlJobPair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        ObjectServices host,
        ControlJobManager manager,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Manager = manager;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end: object services with no objects of its own.</summary>
    public ObjectServices Host { get; }

    /// <summary>Gets the state machine behind the equipment.</summary>
    public ControlJobManager Manager { get; }

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
    /// <returns>The pair.</returns>
    public static ControlJobPair Start(Action<ControlJobManager>? configure = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();
        var manager = new ControlJobManager();

        configure?.Invoke(manager);

        ObjectServices? pendingEquipment = null;

        ConcurrentQueue<(byte Stream, byte Function)> asked = new();
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                return pendingEquipment!.HandleAsync(transaction, token);
            });
        }

        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);

        // The host declares nothing: E94 gives it no primary to answer, the same as E87 and E90.
        var hostLink = new HsmsConnection(options, active, new SecsMessageRouterBuilder().Build(), clock);

        pendingEquipment = new ObjectServices(equipmentLink, new ControlJobObjectProvider(manager));

        return new ControlJobPair(
            active,
            passive,
            hostLink,
            equipmentLink,
            new ObjectServices(hostLink),
            manager,
            asked);
    }

    /// <summary>Waits for both ends of the link to select.</summary>
    /// <returns>A task that completes once they have.</returns>
    public Task OpenAsync()
        => WaitUntilAsync(() => EquipmentLink.IsSelected && HostLink.IsSelected, "both ends to be selected");

    /// <summary>Gets a job the manager must have.</summary>
    /// <param name="controlJobId">Which one.</param>
    /// <returns>The job.</returns>
    public ControlJob Job(string controlJobId = "CJ-1")
    {
        var job = Manager.Find(controlJobId);

        Assert.NotNull(job);

        return job;
    }

    /// <summary>Asserts that the equipment was asked a message.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="function">The function.</param>
    /// <remarks>
    /// Takes the two numbers rather than a tuple, so a test can write the stream and function the
    /// way the standard does instead of casting a literal to <see cref="byte"/> twice.
    /// </remarks>
    public void AssertAsked(byte stream, byte function)
        => Assert.Contains((stream, function), _asked);

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
