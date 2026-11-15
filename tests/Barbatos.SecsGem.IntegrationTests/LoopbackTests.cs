// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.IntegrationTests;

/// <summary>
/// A host and a piece of equipment, each running its own state machine, talking to each other.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is stubbed. Both ends are real <see cref="HsmsConnection"/> instances with their
/// own actor loops, their own transaction tables and their own timers, exchanging real encoded
/// frames over a real pair of <see cref="System.IO.Pipelines.Pipe"/> instances. The only thing
/// missing is the network - which is also the only thing that would make the test slow and flaky.
/// </para>
/// <para>
/// Both sides share one <c>FakeTimeProvider</c>, so T3, T5, T6 and T7 elapse on command and the
/// whole suite finishes in milliseconds.
/// </para>
/// </remarks>
public sealed class LoopbackTests
{
    [Fact]
    public async Task A_host_and_a_piece_of_equipment_select_each_other_with_no_help()
    {
        await using var fab = Fab.Start();

        await fab.SelectedAsync();

        // The host is active, so it sent Select.req; the equipment is passive, so it answered.
        // Neither needed telling.
        Assert.True(fab.Host.IsSelected);
        Assert.True(fab.Equipment.IsSelected);
    }

    [Fact]
    public async Task A_transaction_completes_end_to_end()
    {
        await using var fab = Fab.Start(equipment: routes => routes.OnPrimary(1, 1, Reply(() =>
            new SecsMessage(1, 2, replyExpected: false)
            {
                Item = SecsItem.L(SecsItem.A("BRBTOS", 6), SecsItem.A("0.1.0")),
            })));

        await fab.SelectedAsync();

        using var areYouThere = new SecsMessage(1, 1);
        using var onLineData = await fab.Host.SendAsync(areYouThere, Token);

        Assert.Equal(1, onLineData.S);
        Assert.Equal(2, onLineData.F);
        Assert.Equal(2, onLineData.Item?.Count);
        Assert.Equal("BRBTOS", onLineData.Item?[0].GetString());
        Assert.Equal("0.1.0", onLineData.Item?[1].GetString());
    }

    [Fact]
    public async Task A_notification_from_the_equipment_reaches_the_host()
    {
        var received = new TaskCompletionSource<SecsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fab = Fab.Start(host: routes => routes.OnPrimary(6, 11, (transaction, _) =>
        {
            // The handler owns the message only while it runs, so what the test keeps is a copy
            // of what it cares about rather than the message itself.
            received.TrySetResult(new SecsMessage(transaction.Message.S, transaction.Message.F, replyExpected: false)
            {
                Item = SecsItem.U4(transaction.Message.Item?[1].FirstValueOrDefault<uint>() ?? 0),
            });

            return ValueTask.CompletedTask;
        }));

        await fab.SelectedAsync();

        using var report = new SecsMessage(6, 11, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.U4(7001u)),
        };

        await fab.Equipment.PostAsync(report, Token);

        using var arrived = await received.Task.WaitAsync(Timeout, Token);
        Assert.Equal(6, arrived.S);
        Assert.Equal(11, arrived.F);
        Assert.Equal(7001u, arrived.Item?.FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task A_GEM_opening_sequence_runs_through()
    {
        var established = 0;

        await using var fab = Fab.Start(
            equipment: routes => routes
                // S1F13 Establish Communications Request: the first thing a host says after Select.
                .OnPrimary(1, 13, Reply(() =>
                {
                    Interlocked.Increment(ref established);
                    return new SecsMessage(1, 14, replyExpected: false)
                    {
                        Item = SecsItem.L(
                            SecsItem.B(0).Named("COMMACK"),
                            SecsItem.L(SecsItem.A("BRBTOS", 6), SecsItem.A("0.1.0"))),
                    };
                })),
            host: routes => routes
                .OnPrimary(6, 11, Reply(() => new SecsMessage(6, 12, replyExpected: false)
                {
                    Item = SecsItem.B(0).Named("ACKC6"),
                })));

        await fab.SelectedAsync();

        using var establish = new SecsMessage(1, 13)
        {
            Item = SecsItem.L(SecsItem.A("HOST", 6), SecsItem.A("1.0.0")),
        };

        using var commack = await fab.Host.SendAsync(establish, Token);
        Assert.Equal(1, commack.S);
        Assert.Equal(14, commack.F);
        Assert.Equal<byte?>(0, commack.Item?[0].FirstValueOrDefault<byte>());
        Assert.Equal(1, Volatile.Read(ref established));

        // Communications established, so the equipment starts reporting - and this time the
        // equipment is the one opening a transaction and the host the one answering.
        using var report = new SecsMessage(6, 11)
        {
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.U4(7001u), SecsItem.L()),
        };

        using var ackc6 = await fab.Equipment.SendAsync(report, Token);
        Assert.Equal(6, ackc6.S);
        Assert.Equal(12, ackc6.F);
        Assert.Equal<byte?>(0, ackc6.Item?.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task Concurrent_transactions_do_not_cross()
    {
        const int count = 40;

        await using var fab = Fab.Start(equipment: routes => routes.OnPrimary(1, 3, async (transaction, token) =>
        {
            // Echo the request's value back. A transaction table that mixed its system bytes up
            // would show here as a reply carrying somebody else's number.
            var requested = transaction.Message.Item?.FirstValueOrDefault<uint>() ?? 0;
            using var reply = new SecsMessage(1, 4, replyExpected: false) { Item = SecsItem.U4(requested) };
            await transaction.ReplyAsync(reply, token);
        }));

        await fab.SelectedAsync();

        var sending = Enumerable.Range(1, count).Select(async value =>
        {
            using var primary = new SecsMessage(1, 3) { Item = SecsItem.U4((uint)value) };
            using var reply = await fab.Host.SendAsync(primary, Token);
            return reply.Item?.FirstValueOrDefault<uint>();
        });

        var replies = await Task.WhenAll(sending);

        Assert.Equal(Enumerable.Range(1, count).Select(value => (uint?)value).ToArray(), replies);
    }

    [Fact]
    public async Task A_reply_that_never_comes_ends_as_T3_at_the_sender_and_S9F9_at_the_peer()
    {
        var reported = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fab = Fab.Start(
            equipment: routes => routes
                // Claimed, and then deliberately left unanswered: the handler bug this is all for.
                .OnPrimary(1, 1, (_, _) => ValueTask.CompletedTask)
                .OnPrimary(9, 9, (transaction, _) =>
                {
                    reported.TrySetResult(transaction.Message.Item?.GetSpan<byte>().ToArray() ?? []);
                    return ValueTask.CompletedTask;
                }));

        await fab.SelectedAsync();

        using var primary = new SecsMessage(1, 1);
        var sending = fab.Host.SendAsync(primary, Token).AsTask();

        await fab.AdvanceUntilAsync(fab.Options.T3 / 10, () => sending.IsCompleted, "T3 to expire");

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => sending);
        Assert.Equal(SecsTimer.T3, error.Timer);

        // The other half of the story, and the half secs4net leaves out: the peer is told. Without
        // it, two logs disagree about what happened and nobody can tell which end was at fault.
        var shead = await reported.Task.WaitAsync(Timeout, Token);
        var header = MessageHeader.Parse(shead);
        Assert.Equal(1, header.Stream);
        Assert.Equal(1, header.Function);
        Assert.True(header.ReplyExpected);
    }

    [Fact]
    public async Task A_message_the_equipment_does_not_implement_comes_back_as_S9F5()
    {
        var reported = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fab = Fab.Start(
            equipment: routes => routes.OnPrimary(1, 1, Reply(() => new SecsMessage(1, 2, replyExpected: false))),
            host: routes => routes.OnPrimary(9, 5, (transaction, _) =>
            {
                reported.TrySetResult(transaction.Message.Item?.GetSpan<byte>().ToArray() ?? []);
                return ValueTask.CompletedTask;
            }));

        await fab.SelectedAsync();

        // Stream 1 is implemented by the equipment, F13 is not, so this is S9F5 rather than S9F3.
        using var unimplemented = new SecsMessage(1, 13, replyExpected: false);
        await fab.Host.PostAsync(unimplemented, Token);

        var mhead = await reported.Task.WaitAsync(Timeout, Token);
        var header = MessageHeader.Parse(mhead);
        Assert.Equal(1, header.Stream);
        Assert.Equal(13, header.Function);
    }

    [Fact]
    public async Task A_message_in_a_stream_the_equipment_knows_nothing_about_comes_back_as_S9F3()
    {
        var reported = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fab = Fab.Start(
            equipment: routes => routes.OnPrimary(1, 1, Reply(() => new SecsMessage(1, 2, replyExpected: false))),
            host: routes => routes.OnPrimary(9, 3, (transaction, _) =>
            {
                reported.TrySetResult(transaction.Message.Item?.GetSpan<byte>().ToArray() ?? []);
                return ValueTask.CompletedTask;
            }));

        await fab.SelectedAsync();

        using var unknownStream = new SecsMessage(2, 41, replyExpected: false);
        await fab.Host.PostAsync(unknownStream, Token);

        var mhead = await reported.Task.WaitAsync(Timeout, Token);
        var header = MessageHeader.Parse(mhead);
        Assert.Equal(2, header.Stream);
        Assert.Equal(41, header.Function);
    }

    [Fact]
    public async Task Both_ends_see_the_same_frames_take_the_fast_path()
    {
        await using var fab = Fab.Start(equipment: routes => routes.OnPrimary(1, 1, Reply(() =>
            new SecsMessage(1, 2, replyExpected: false))));

        await fab.SelectedAsync();

        using var primary = new SecsMessage(1, 1);
        using var reply = await fab.Host.SendAsync(primary, Token);

        // Select.req + S1F1 at the equipment, Select.rsp + S1F2 at the host. All four were written
        // in one go and read in one go, so none of them needed the staging path.
        Assert.Equal(2, fab.Equipment.FastPathFrames);
        Assert.Equal(2, fab.Host.FastPathFrames);
        Assert.Equal(0, fab.Equipment.SlowPathFrames);
        Assert.Equal(0, fab.Host.SlowPathFrames);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <summary>Builds a handler that answers with a freshly built message.</summary>
    private static SecsPrimaryHandler Reply(Func<SecsMessage> build) => async (transaction, token) =>
    {
        using var reply = build();
        await transaction.ReplyAsync(reply, token);
    };

    /// <summary>A host and a piece of equipment wired to each other.</summary>
    private sealed class Fab : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly InMemorySecsTransport _active;
        private readonly InMemorySecsTransport _passive;

        private Fab(
            InMemorySecsTransport active,
            InMemorySecsTransport passive,
            HsmsConnection host,
            HsmsConnection equipment,
            SecsGemOptions options,
            FakeTimeProvider clock)
        {
            _active = active;
            _passive = passive;
            Host = host;
            Equipment = equipment;
            Options = options;
            Clock = clock;

            Running = Task.WhenAll(host.RunAsync(_cts.Token), equipment.RunAsync(_cts.Token));
        }

        public HsmsConnection Host { get; }

        public HsmsConnection Equipment { get; }

        public SecsGemOptions Options { get; }

        public FakeTimeProvider Clock { get; }

        public Task Running { get; }

        public static Fab Start(
            Action<SecsMessageRouterBuilder>? equipment = null,
            Action<SecsMessageRouterBuilder>? host = null,
            Action<SecsGemOptions>? configure = null)
        {
            // One device id on both sides: a host addressing a device the equipment does not
            // answer to is its own test, over in the S9F1 ones.
            var options = new SecsGemOptions
            {
                DeviceId = 7,
                T3 = TimeSpan.FromSeconds(45),
                T5 = TimeSpan.FromSeconds(10),
                T6 = TimeSpan.FromSeconds(5),
                T7 = TimeSpan.FromSeconds(10),
                T8 = TimeSpan.FromSeconds(5),

                // Off, so a test that walks the clock forward is measuring the timer it meant to.
                LinkTestInterval = TimeSpan.Zero,
            };

            configure?.Invoke(options);

            var hostRoutes = new SecsMessageRouterBuilder();
            host?.Invoke(hostRoutes);

            var equipmentRoutes = new SecsMessageRouterBuilder();
            equipment?.Invoke(equipmentRoutes);

            var clock = new FakeTimeProvider();
            var (active, passive) = InMemorySecsTransport.CreatePair();

            return new Fab(
                active,
                passive,
                new HsmsConnection(options, active, hostRoutes.Build(), clock),
                new HsmsConnection(options, passive, equipmentRoutes.Build(), clock),
                options,
                clock);
        }

        /// <summary>Waits for both ends to report themselves selected.</summary>
        public async Task SelectedAsync()
        {
            for (var attempt = 0; attempt < 5_000; attempt++)
            {
                if (Host.IsSelected && Equipment.IsSelected)
                {
                    return;
                }

                await Task.Delay(1, Token).ConfigureAwait(false);
            }

            Assert.Fail($"Timed out selecting: host is {Host.State}, equipment is {Equipment.State}.");
        }

        /// <summary>Walks the shared clock forward in small steps until something happens.</summary>
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

            Assert.Fail($"Timed out waiting for {what}.");
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Running.WaitAsync(Timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Both loops are being torn down; cancellation and closed pipes are expected here.
            }

            await Host.DisposeAsync().ConfigureAwait(false);
            await Equipment.DisposeAsync().ConfigureAwait(false);
            await _active.DisposeAsync().ConfigureAwait(false);
            await _passive.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
