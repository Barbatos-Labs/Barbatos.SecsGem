// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// A real <see cref="RecipeHost"/> and a real <see cref="RecipeEquipment"/> on the two ends of one
/// transport pair.
/// </summary>
/// <remarks>
/// <para>
/// A host and an equipment rather than two of one type, because <strong>stream 15 runs one
/// way</strong>: all twenty-seven primaries are the host's. So only the equipment's thirteen
/// routes go into a table, and the host's side of the pair declares nothing - which is itself
/// asserted, in the hosting tests.
/// </para>
/// <para>
/// <strong>Every test in this project carries a <c>Timeout</c></strong>, which is the blanket
/// answer to the trap the paragraph below describes. A per-await real-clock bound, which
/// <c>GemPair</c> and <c>MaterialPair</c> use, catches the awaits a test can see; the attribute
/// catches those as well as every await inside the library under test, and costs one token per
/// test. Either way the point is the same: on a fake clock a missing reply must fail rather than
/// hang, because a hung run prints no summary and reads exactly like a control that found nothing.
/// </para>
/// <para>
/// <strong>Every message sent by hand goes out on <c>HostLink</c></strong>, because every primary
/// in this stream is the host's. A message put on the equipment's link travels away from the end
/// under test, reaches a peer with no route for it, comes back S9F5 and leaves the sender on a T3
/// that a fake clock never advances. That mistake hangs a suite rather than failing it, and it has
/// cost this repository two chapters - in a one-way stream there is no case where the other link
/// is the right one.
/// </para>
/// </remarks>
internal sealed class RecipePair : IAsyncDisposable
{
    private readonly InMemorySecsTransport _active;
    private readonly InMemorySecsTransport _passive;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<(byte Stream, byte Function)> _asked;

    private readonly RecipeInMemoryStore? _memory;

    private RecipePair(
        InMemorySecsTransport active,
        InMemorySecsTransport passive,
        HsmsConnection hostLink,
        HsmsConnection equipmentLink,
        RecipeHost host,
        RecipeEquipment equipment,
        RecipeInMemoryStore? store,
        ConcurrentQueue<(byte Stream, byte Function)> asked)
    {
        _active = active;
        _passive = passive;
        _asked = asked;
        _memory = store;
        HostLink = hostLink;
        EquipmentLink = equipmentLink;
        Host = host;
        Equipment = equipment;

        Running = Task.WhenAll(
            hostLink.RunAsync(_cts.Token),
            equipmentLink.RunAsync(_cts.Token));
    }

    /// <summary>Gets the host end, which drives every transaction.</summary>
    public RecipeHost Host { get; }

    /// <summary>Gets the machine's end.</summary>
    public RecipeEquipment Equipment { get; }

    /// <summary>Gets the machine's recipes.</summary>
    /// <exception cref="InvalidOperationException">The pair was started over a store of the test's own.</exception>
    /// <remarks>
    /// Throws rather than returning null, because a test that passed its own store and then read
    /// this would be asserting against a store nothing is wired to - and the four
    /// <see cref="IRecipeStore"/> members with default implementations are exactly where a test
    /// wants its own store, so the mistake is reachable.
    /// </remarks>
    public RecipeInMemoryStore Store => _memory
        ?? throw new InvalidOperationException("This pair was started over a store of the test's own.");

    /// <summary>Gets the host's connection, which is where every hand-written primary goes.</summary>
    public HsmsConnection HostLink { get; }

    /// <summary>Gets the machine's connection.</summary>
    public HsmsConnection EquipmentLink { get; }

    /// <summary>Gets the task both connections are running under.</summary>
    public Task Running { get; }

    /// <summary>Gets every stream and function the machine has been asked, in order.</summary>
    public IReadOnlyCollection<(byte Stream, byte Function)> Asked => _asked;

    /// <summary>Gets what the host end logged.</summary>
    public RecordingLogger<RecipeHost> HostLog { get; private init; } = new();

    /// <summary>Gets what the machine's end logged.</summary>
    /// <remarks>
    /// Both ends record, because this package has branches whose whole effect is a log line at the
    /// <em>answering</em> end - a body it could not read, a function it does not implement, a
    /// message from a stream that is not its own. <c>RMACK</c> has one refusal code, so the log is
    /// the only thing that distinguishes "I could not read that" from "no".
    /// </remarks>
    public RecordingLogger<RecipeEquipment> EquipmentLog { get; private init; } = new();

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a pair.</summary>
    /// <param name="answerEquipment">Replaces the machine's end with a reply the test writes itself.</param>
    /// <param name="alsoRoute">Extra pairs to route to the machine, for the guard against a function outside the thirteen.</param>
    /// <param name="store">The machine's recipes. <see langword="null"/> uses a fresh in-memory store, which <see cref="Store"/> then exposes.</param>
    /// <returns>The pair.</returns>
    /// <remarks>
    /// <paramref name="answerEquipment"/> is how the shapes this library tolerates but does not
    /// write get tested at all: a machine that sent a namespace list flat, or an <c>RMACK</c> in
    /// the reserved range, has to be played by hand. <paramref name="store"/> is the other
    /// direction - a machine whose <em>store</em> answers something the in-memory one never would,
    /// such as accepting a lock and minting no token.
    /// </remarks>
    public static RecipePair Start(
        Func<SecsTransaction, SecsMessage?>? answerEquipment = null,
        IReadOnlyList<(byte Stream, byte Function)>? alsoRoute = null,
        IRecipeStore? store = null)
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var memory = store is null ? new RecipeInMemoryStore() : null;

        RecipeEquipment? pending = null;
        ConcurrentQueue<(byte Stream, byte Function)> asked = new();

        // Only the machine's routes, because stream 15 gives the host nothing to answer. The host
        // link gets an empty table, which is what AddRecipeHost produces.
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in alsoRoute is null
            ? RecipeEquipment.HandledMessages
            : [.. RecipeEquipment.HandledMessages, .. alsoRoute])
        {
            equipmentRoutes.OnPrimary(stream, function, async (transaction, token) =>
            {
                asked.Enqueue((transaction.S, transaction.F));

                if (answerEquipment is null)
                {
                    await pending!.HandleAsync(transaction, token).ConfigureAwait(false);
                    return;
                }

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

        var hostLink = new HsmsConnection(options, active, new SecsMessageRouterBuilder().Build(), clock);
        var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build(), clock);

        var hostLog = new RecordingLogger<RecipeHost>();
        var equipmentLog = new RecordingLogger<RecipeEquipment>();

        pending = new RecipeEquipment(store ?? memory!, equipmentLog);

        return new RecipePair(
            active,
            passive,
            hostLink,
            equipmentLink,
            new RecipeHost(hostLink, hostLog),
            pending,
            memory,
            asked)
        {
            HostLog = hostLog,
            EquipmentLog = equipmentLog,
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
    /// <remarks>
    /// Four hundred attempts, because <c>Task.Delay(1)</c> on Windows waits a system timer tick -
    /// about fifteen milliseconds, not one. The five thousand the earlier packages use costs
    /// seventy-eight seconds for a wait that will never come good, which is long enough to read
    /// like a hang; four hundred is still six seconds of patience for a transport that answers in
    /// a millisecond.
    /// </remarks>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
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
}
