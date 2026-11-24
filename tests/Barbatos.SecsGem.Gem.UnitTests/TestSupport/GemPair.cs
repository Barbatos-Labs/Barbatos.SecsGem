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
    /// <param name="hostLogger">
    /// Where the host writes. Needed by any test about a row whose reply cannot carry the
    /// distinction the host is making - S6F10 says "refused" and not "refused because I could not
    /// read it", so the log is the only place those two differ.
    /// </param>
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
        bool limits = false,
        RecordingLogger<GemHost>? hostLogger = null,
        IGemServiceProgramStore? servicePrograms = null,
        Func<SecsTransaction, SecsMessage?>? answerEquipment = null,
        IGemProcessProgramMatrixStore? matrices = null)
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

        if (servicePrograms is not null)
        {
            declared.AddRange(GemEquipment.ServiceProgramMessages);
        }

        if (matrices is not null)
        {
            declared.AddRange(GemEquipment.ProcessProgramMatrixMessages);
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
            equipmentRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answerEquipment is null)
                {
                    await pendingEquipment!.HandleAsync(transaction, token).ConfigureAwait(false);

                    return;
                }

                // The machine played by hand, which is the only way to test a reader against a
                // shape this library tolerates and never writes - a status wrapped in a list of
                // one, a body short of its items. Two real ends can only exercise what this
                // library itself emits. The same hook RecipePair has, and for the same reason.
                var reply = answerEquipment(transaction);

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
            terminal,
            servicePrograms,
            matrices);

        pendingHost = new GemHost(hostOptions, hostLink, clock, hostLogger);

        return new GemPair(active, passive, hostLink, equipmentLink, pendingHost, pendingEquipment, clock, asked);
    }

    /// <summary>Sends a hand-written primary from the machine to the host, bounded.</summary>
    /// <param name="primary">The primary. The caller owns it.</param>
    /// <returns>The host's reply. The caller disposes it.</returns>
    /// <remarks>
    /// <para>
    /// <strong>It sends from the equipment's end</strong>, so the handler it reaches is
    /// <see cref="Host"/>'s. The method was called <c>AskEquipmentAsync</c> until the stream 6
    /// chapter, which is the wrong way round and cost that chapter a wrong first draft - every one
    /// of its callers hands it an equipment primary, S5F9 and S6F3 among them. Its mirror is
    /// <see cref="AskEquipmentAsync"/>.
    /// </para>
    /// <para>
    /// <strong>The bound is the whole point of both.</strong> A hand-written primary is how a test
    /// reaches a handler that the real object at the other end cannot produce - a malformed body, a
    /// reserved code - and a handler that throws or answers on the wrong function leaves this end
    /// waiting on a T3 the FakeTimeProvider never advances. The run then dies on its own timeout
    /// with no summary, which is the worst way for a negative control to report: it says nothing
    /// about which rule broke.
    /// </para>
    /// <para>
    /// The timeout is on the real clock deliberately, which is the one place in this suite that is
    /// true. Everything the library does runs on the fake clock; this is the harness noticing that
    /// the library has stopped.
    /// </para>
    /// </remarks>
    public async Task<SecsMessage> AskHostAsync(SecsMessage primary)
        => await EquipmentLink
            .SendAsync(primary, Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), Token)
            .ConfigureAwait(false);

    /// <summary>Sends a hand-written primary from the host to the machine, bounded.</summary>
    /// <param name="primary">The primary. The caller owns it.</param>
    /// <returns>The equipment's reply. The caller disposes it.</returns>
    /// <remarks>
    /// <see cref="AskHostAsync"/> the other way round, and it carries the bound for the same
    /// reason. Needed wherever a reply the public API flattens has to be read as a frame - S6F28
    /// answers an event with nothing linked to it and an event that does not exist with two
    /// different bodies, and <c>RequestNotificationReportsAsync</c> returns an empty list for
    /// both, so only a frame can tell them apart.
    /// </remarks>
    public async Task<SecsMessage> AskEquipmentAsync(SecsMessage primary)
        => await HostLink
            .SendAsync(primary, Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), Token)
            .ConfigureAwait(false);

    /// <summary>Waits, bounded, for any exchange in flight to finish.</summary>
    /// <param name="sending">The call in flight.</param>
    /// <returns>Whatever it answered.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="AskHostAsync"/>'s mirror, and earned by the same class of control. A report the
    /// equipment sends with the W bit set waits for the host's acknowledgement under T3, and a host
    /// that answers on the wrong function leaves that wait unmatched - on a clock this suite never
    /// advances. A send to an absent host needs no bound, because nothing is awaited.
    /// </para>
    /// <para>
    /// <strong>It is for host requests too</strong>, which the stream 6 chapter learned the hard
    /// way: a control that removed S6F27 from the equipment's routing table made the machine answer
    /// S9F5, left the host waiting on T3, and the whole run died on its own timeout with no
    /// summary - so the control reported "found nothing" when what it had found was a hang. Wrap
    /// any host call whose primary the equipment might not answer.
    /// </para>
    /// </remarks>
    public static Task<T> SentAsync<T>(ValueTask<T> sending)
        => sending.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);

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
