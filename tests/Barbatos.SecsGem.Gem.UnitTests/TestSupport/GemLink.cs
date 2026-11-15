// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// A real <see cref="GemEquipment"/> on a real <see cref="HsmsConnection"/>, with the test playing
/// the host at the other end of one duplex pipe.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a mock. The equipment runs its own actor loop, the connection runs its state
/// machine and its timers, and the test writes and reads HSMS frames - so a test that passes has
/// exercised the framing, the transaction matching and the state models together. The clock is the
/// only thing faked, because a test that waits out a thirty-second retry interval is a test nobody
/// runs.
/// </para>
/// <para>
/// The equipment is the passive end, as equipment is: the test connects to it and sends the
/// Select.req, which is also the order a fab sees.
/// </para>
/// </remarks>
internal sealed class GemLink : IAsyncDisposable
{
    private readonly InMemorySecsTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly IAsyncEnumerator<HsmsMessage> _inbound;
    private readonly HsmsPipeEncoder _encoder;
    private int _systemBytes = 0x4000;

    private GemLink(
        InMemorySecsTransport transport,
        HsmsConnection connection,
        GemEquipment equipment,
        SecsGemOptions linkOptions,
        GemOptions gemOptions,
        FakeTimeProvider clock)
    {
        _transport = transport;
        Connection = connection;
        Equipment = equipment;
        LinkOptions = linkOptions;
        Options = gemOptions;
        Clock = clock;

        _encoder = new HsmsPipeEncoder(linkOptions);
        _inbound = new HsmsPipeDecoder(linkOptions, clock)
            .DecodeAsync(transport.PeerPipe.Input, _cts.Token)
            .GetAsyncEnumerator(_cts.Token);

        ConnectionRunning = connection.RunAsync(_cts.Token);
        EquipmentRunning = equipment.RunAsync(_cts.Token);
    }

    /// <summary>Gets the connection under the equipment.</summary>
    public HsmsConnection Connection { get; }

    /// <summary>Gets the equipment under test.</summary>
    public GemEquipment Equipment { get; }

    /// <summary>Gets the E37 options.</summary>
    public SecsGemOptions LinkOptions { get; }

    /// <summary>Gets the E30 options.</summary>
    public GemOptions Options { get; }

    /// <summary>Gets the clock both the connection and the equipment run on.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Gets the connection's run task.</summary>
    public Task ConnectionRunning { get; }

    /// <summary>Gets the equipment's run task.</summary>
    public Task EquipmentRunning { get; }

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a link with the given model and options.</summary>
    /// <param name="model">Declares what the equipment publishes. Null gives the default model.</param>
    /// <param name="configure">Adjusts the GEM options.</param>
    /// <returns>The link.</returns>
    public static GemLink Start(
        Action<GemEquipmentModelBuilder>? model = null,
        Action<GemOptions>? configure = null)
    {
        var linkOptions = new SecsGemOptions
        {
            DeviceId = 7,
            Mode = HsmsConnectionMode.Passive,
            T3 = TimeSpan.FromSeconds(45),
            T6 = TimeSpan.FromSeconds(5),
            T7 = TimeSpan.FromSeconds(10),
            T8 = TimeSpan.FromSeconds(5),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var gemOptions = new GemOptions
        {
            ModelName = "BRBTOS",
            SoftwareRevision = "0.1.0",
            EstablishCommunicationsRetryInterval = TimeSpan.FromSeconds(30),
        };

        configure?.Invoke(gemOptions);

        var builder = new GemEquipmentModelBuilder();
        (model ?? DefaultModel).Invoke(builder);

        var clock = new FakeTimeProvider();
        var transport = new InMemorySecsTransport(isActive: false);

        // The cycle the factory overload of OnPrimary exists for, written out by hand because a
        // test has no container: the router needs the handlers, the handlers need the equipment,
        // and the equipment needs the connection the router is being built for. A captured local
        // breaks it the same way a deferred factory does.
        GemEquipment? equipment = null;
        var routes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            routes.OnPrimary(stream, function, (transaction, token) => equipment!.HandleAsync(transaction, token));
        }

        var connection = new HsmsConnection(linkOptions, transport, routes.Build(), clock);
        equipment = new GemEquipment(gemOptions, builder.Build(), connection, clock);

        return new GemLink(transport, connection, equipment, linkOptions, gemOptions, clock);
    }

    /// <summary>A small model with one of everything the tests need.</summary>
    public static void DefaultModel(GemEquipmentModelBuilder builder)
    {
        builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5), "")
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(1013.25d), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-001"))
            .AddConstant(new GemEquipmentConstant
            {
                Id = 100,
                Name = "ReadOnlyLimit",
                Read = () => SecsItem.U4(42u),
            })
            .AddEvent(1_000, "ProcessStart")
            .AddEvent(1_001, "ProcessEnd")
            .AddEvent(2_000, "AlarmSet")
            .AddEvent(2_001, "AlarmCleared")
            .AddReport(5_000, 1, 2)
            .AddAlarm(new GemAlarm
            {
                Id = 9_000,
                Text = "Chamber pressure out of range",
                SetEventId = 2_000,
                ClearEventId = 2_001,
            });
    }

    /// <summary>Completes the HSMS select, with the test as the active end.</summary>
    /// <returns>A task that completes once the connection is Selected.</returns>
    public async Task SelectAsync()
    {
        await SendFrameAsync(HsmsMessage.Control(MessageType.SelectRequest, 0x5001)).ConfigureAwait(false);

        using var response = await ReadFrameAsync().ConfigureAwait(false);
        Assert.Equal(MessageType.SelectResponse, response.Header.MessageType);
        Assert.Equal(SelectStatus.Success, response.Header.SelectStatus);

        await WaitUntilAsync(() => Connection.IsSelected, "the connection to reach Selected").ConfigureAwait(false);
    }

    /// <summary>Answers the equipment's S1F13 and waits for it to reach Communicating.</summary>
    /// <param name="commack">The COMMACK to answer with.</param>
    /// <returns>A task that completes once the equipment has read the answer.</returns>
    public async Task<SecsMessage> EstablishAsync(GemCommunicationAcknowledgement commack = GemCommunicationAcknowledgement.Accepted)
    {
        using var request = await ReadFrameAsync().ConfigureAwait(false);
        var primary = request.Data ?? throw new InvalidOperationException("S1F13 arrived with no body.");

        Assert.Equal(1, primary.S);
        Assert.Equal(13, primary.F);

        using var reply = new SecsMessage(1, 14, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.B((byte)commack),
                SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0"))),
        };

        await SendDataAsync(reply, request.Header.SystemBytes).ConfigureAwait(false);

        if (commack == GemCommunicationAcknowledgement.Accepted)
        {
            await WaitUntilAsync(() => Equipment.IsCommunicating, "the equipment to reach Communicating").ConfigureAwait(false);
        }

        return request.DetachData()!;
    }

    /// <summary>Selects and establishes in one step.</summary>
    /// <returns>A task that completes once the equipment is communicating.</returns>
    public async Task OpenAsync()
    {
        await SelectAsync().ConfigureAwait(false);
        using var request = await EstablishAsync().ConfigureAwait(false);
    }

    /// <summary>Sends a primary as the host and returns the equipment's reply.</summary>
    /// <param name="primary">The primary. Disposed by this method.</param>
    /// <returns>The secondary. The caller owns it.</returns>
    public async Task<SecsMessage> RequestAsync(SecsMessage primary)
    {
        var systemBytes = Interlocked.Increment(ref _systemBytes);

        using (primary)
        {
            await SendDataAsync(primary, systemBytes).ConfigureAwait(false);
        }

        using var frame = await ReadFrameAsync().ConfigureAwait(false);
        Assert.Equal(systemBytes, frame.Header.SystemBytes);

        return frame.DetachData() ?? throw new InvalidOperationException("The reply arrived with no body.");
    }

    /// <summary>Reads the next frame the equipment sends, and answers it if it wants a reply.</summary>
    /// <param name="acknowledgement">The single acknowledgement byte to answer with.</param>
    /// <returns>The message the equipment sent. The caller owns it.</returns>
    public async Task<SecsMessage> ReceiveAsync(byte acknowledgement = 0)
    {
        using var frame = await ReadFrameAsync().ConfigureAwait(false);
        var message = frame.Data ?? throw new InvalidOperationException("A frame arrived with no body.");

        if (frame.Header.ReplyExpected)
        {
            using var reply = new SecsMessage(message.S, (byte)(message.F + 1), replyExpected: false)
            {
                Item = SecsItem.B(acknowledgement),
            };

            await SendDataAsync(reply, frame.Header.SystemBytes).ConfigureAwait(false);
        }

        return frame.DetachData()!;
    }

    /// <summary>Drops the link the way a pulled cable does: the peer's output simply ends.</summary>
    /// <returns>A task that completes once the equipment has noticed.</returns>
    public async Task DropAsync()
    {
        await _transport.PeerPipe.Output.CompleteAsync().ConfigureAwait(false);
        await WaitUntilAsync(() => !Connection.IsSelected, "the connection to leave Selected").ConfigureAwait(false);
    }

    /// <summary>Reads the next frame the equipment sends, without answering it.</summary>
    /// <returns>The frame. The caller disposes it.</returns>
    public Task<HsmsMessage> ReadAsync() => ReadFrameAsync();

    /// <summary>Sends a data message as the host.</summary>
    /// <param name="message">The message.</param>
    /// <param name="systemBytes">The system bytes to stamp.</param>
    /// <returns>A task that completes once the bytes are flushed.</returns>
    public async ValueTask SendDataAsync(SecsMessage message, int systemBytes)
    {
        var header = new MessageHeader
        {
            SessionId = LinkOptions.DeviceId,
            Stream = message.S,
            Function = message.F,
            ReplyExpected = message.ReplyExpected,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = systemBytes,
        };

        await _encoder.WriteAsync(_transport.PeerPipe.Output, header, message, Token).ConfigureAwait(false);
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

    /// <summary>Stops everything and waits for both loops to finish.</summary>
    /// <returns>A task that completes once the link is down.</returns>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var running in new[] { ConnectionRunning, EquipmentRunning })
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how both loops stop.
            }
            catch (TimeoutException)
            {
                Assert.Fail("A loop did not stop within ten seconds of being cancelled.");
            }
        }

        await Equipment.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
        await _inbound.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async ValueTask SendFrameAsync(HsmsMessage frame)
    {
        using (frame)
        {
            await _encoder.WriteAsync(_transport.PeerPipe.Output, frame.Header, frame.Data, Token).ConfigureAwait(false);
        }
    }

    private async Task<HsmsMessage> ReadFrameAsync()
    {
        var moved = await _inbound.MoveNextAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), Token)
            .ConfigureAwait(false);

        return moved
            ? _inbound.Current
            : throw new InvalidOperationException("The equipment closed its output instead of sending a frame.");
    }
}
