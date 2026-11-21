// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The host side, tested against the equipment side rather than against a mock of it.
/// </summary>
/// <remarks>
/// Every test here runs a real <see cref="GemHost"/> and a real <see cref="GemEquipment"/> on the
/// two ends of one transport pair - see <see cref="GemPair"/>. A host tested against a fake machine
/// proves that the fake agreed with the host, and both were written on the same afternoon by the
/// same hand.
/// </remarks>
public class GemHostTests
{
    private double _pressure = 1_013.25d;
    private uint _limit = 42;
    private string? _startedLot;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(_pressure), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-001"))
            .AddConstant(new GemEquipmentConstant
            {
                Id = 100,
                Name = "Limit",
                Units = "mbar",
                Read = () => SecsItem.U4(_limit),
                Write = item =>
                {
                    var value = item.FirstValueOrDefault<uint>();

                    if (value > 100)
                    {
                        return false;
                    }

                    _limit = value;
                    return true;
                },
                Minimum = () => SecsItem.U4(0u),
                Maximum = () => SecsItem.U4(100u),
                Default = () => SecsItem.U4(42u),
            })
            .AddConstant(new GemEquipmentConstant
            {
                Id = 101,
                Name = "Plain",
                Read = () => SecsItem.U4(7u),
            })
            .AddEvent(1_000, "ProcessEnd")
            .AddEvent(2_000, "AlarmSet")
            .AddEvent(2_001, "AlarmCleared")
            .AddReport(5_000, 2, 10)
            .AddAlarm(new GemAlarm
            {
                Id = 9_000,
                Text = "Chamber pressure out of range",
                SetEventId = 2_000,
                ClearEventId = 2_001,
            })
            .AddRemoteCommand("START", (context, _) =>
            {
                _startedLot = context.GetText("LOTID");
                return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
            });

    // ----------------------------------------------------------------- the completeness claim

    [Fact]
    public async Task The_host_can_send_every_primary_the_equipment_declares()
    {
        await using var pair = GemPair.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            spool: new GemInMemorySpool(),
            terminal: new GemInMemoryTerminal());

        await pair.OpenAsync();

        var host = pair.Host;
        var token = GemPair.Token;

        // Every method once, in an order that leaves the equipment on-line for the ones that need
        // it. The acknowledgements are asserted in the tests below; what is being asserted here is
        // only that nothing in the equipment's declared surface has no way to reach it.
        await host.AreYouThereAsync(token);
        await host.EstablishCommunicationsAsync(token);
        (await host.ReadStatusAsync([2], token)).Dispose();
        await host.ReadStatusNamelistAsync([2], token);
        await host.RequestOfflineAsync(token);
        await host.RequestOnlineAsync(token);
        (await host.ReadConstantsAsync([100], token)).Dispose();
        await host.WriteConstantsAsync([(100UL, SecsItem.U4(50u))], token);
        await host.ReadConstantNamelistAsync([100], token);
        await host.ReadDataVariableNamelistAsync([10], token);
        await host.ReadCollectionEventNamelistAsync([1_000], token);
        await host.SendRemoteCommandAsync("START", token);
        await host.LoopbackAsync(new byte[] { 1, 2, 3 }, token);
        await host.ReadClockAsync(token);
        await host.SetClockAsync(cancellationToken: token);
        await host.DeleteAllReportsAsync(token);
        await host.DefineReportsAsync([(5_000UL, [2UL, 10UL])], token);
        await host.LinkEventsAsync([(1_000UL, [5_000UL])], token);
        await host.EnableEventsAsync(true, [1_000UL], token);
        await host.SendCommandAsync("START", [("LOTID", "LOT-1")], token);
        await host.EnableAlarmAsync(true, 9_000, token);
        await host.ListAlarmsAsync([], token);
        await host.ListEnabledAlarmsAsync(token);
        (await host.RequestEventReportAsync(1_000, token)).Dispose();
        (await host.RequestReportAsync(5_000, token)).Dispose();
        await host.RequestSpooledDataAsync(GemSpooledDataRequest.Purge, token);
        await host.InquireProcessProgramAsync("RECIPE-1", 3, token);
        await host.SendProcessProgramAsync("RECIPE-1", new byte[] { 1, 2, 3 }, token);
        await host.RequestProcessProgramAsync("RECIPE-1", token);
        await host.ListProcessProgramsAsync(token);
        await host.DeleteProcessProgramsAsync(["RECIPE-1"], token);
        await host.SendFormattedProcessProgramAsync(
            new GemFormattedProcessProgram("SHAPED", "BRBTOS-1", "0.1.0", []),
            token);
        await host.RequestFormattedProcessProgramAsync("SHAPED", token);
        await host.DisplayTerminalMessageAsync("one block", 0, token);
        await host.DisplayTerminalMessageAsync(["two", "blocks"], 0, token);
        await host.BroadcastAsync("everybody", token);

        List<(byte Stream, byte Function)> declared =
        [
            .. GemEquipment.HandledMessages,
            .. GemEquipment.ProcessProgramMessages,
            .. GemEquipment.FormattedProcessProgramMessages,
            .. GemEquipment.SpoolMessages,
            .. GemEquipment.TerminalMessages,
        ];

        var asked = pair.Asked.ToHashSet();

        // The claim this class is built on, asserted rather than written in a comment: there is no
        // message the equipment answers that a host using this library has to hand-write.
        foreach (var message in declared)
        {
            Assert.True(asked.Contains(message), $"No GemHost method sends S{message.Stream}F{message.Function}.");
        }

        Assert.Equal(declared.Count, declared.Distinct().Count());
    }

    // ----------------------------------------------------------------- establishing

    [Fact]
    public async Task Both_ends_establish_communications_without_being_told_to()
    {
        await using var pair = GemPair.Start(Model);

        // Nothing drives this but the two objects: the active transport selects, the equipment
        // sends its own S1F13 when it sees Selected, and the host's routing table answers it.
        await pair.OpenAsync();

        Assert.True(pair.Equipment.IsCommunicating);
        Assert.True(pair.Host.IsCommunicating);
    }

    [Fact]
    public async Task A_host_that_refuses_the_machine_can_still_establish_from_its_own_side()
    {
        await using var pair = GemPair.Start(Model, host: options => options.AcceptCommunications = false);

        await GemPair.WaitUntilAsync(
            () => pair.HostLink.IsSelected && pair.EquipmentLink.IsSelected,
            "both ends to be selected");

        // The equipment asked and was told no, which is a real thing for a host under maintenance
        // to say: keep retrying rather than time out against silence.
        await GemPair.WaitUntilAsync(
            () => pair.Equipment.CommunicationState == GemCommunicationState.WaitingToRetry,
            "the equipment to be waiting to retry");

        Assert.False(pair.Equipment.IsCommunicating);
        Assert.False(pair.Host.IsCommunicating);

        // And refusing the machine's request does not disable the host's own. Contradictory, and
        // the host's contradiction to make - a flag that quietly disabled an explicit call would
        // be worse than one that does what it says.
        var commack = await pair.Host.EstablishCommunicationsAsync(GemPair.Token);

        Assert.Equal(GemCommunicationAcknowledgement.Accepted, commack);
        Assert.True(pair.Host.IsCommunicating);
    }

    [Fact]
    public async Task Communications_do_not_outlive_the_link()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var states = new List<bool>();
        pair.Host.CommunicationStateChanged += (_, e) => states.Add(e.IsCommunicating);

        await pair.HostLink.DeselectAsync(GemPair.Token);

        // Waiting for the event rather than for the property: the host sets IsCommunicating and
        // then raises the change, so a wait on the property followed by a read of the list is a
        // race - and unlike the spool's two properties, that order is the right one here. A
        // notification after the fact is what a notification is.
        await GemPair.WaitUntilAsync(() => states.Count > 0, "the host to report that it stopped communicating");

        // A selected link is a socket and communicating is an agreement, so the agreement cannot
        // outlive the socket it was made over.
        Assert.False(states[0]);
        Assert.False(pair.Host.IsCommunicating);
    }

    // ----------------------------------------------------------------- stream 1

    [Fact]
    public async Task The_machine_answers_who_it_is_and_stops_when_it_goes_off_line()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var identity = await pair.Host.AreYouThereAsync(GemPair.Token);

        Assert.NotNull(identity);
        Assert.Equal("BRBTOS-1", identity.ModelName);
        Assert.Equal("0.1.0", identity.SoftwareRevision);

        Assert.Equal(GemOfflineAcknowledgement.Acknowledged, await pair.Host.RequestOfflineAsync(GemPair.Token));

        // E30 has off-line equipment answer S1F1 with a zero-length list rather than its identity,
        // which is how a host learns the machine is alive but not available without asking twice.
        Assert.Null(await pair.Host.AreYouThereAsync(GemPair.Token));

        Assert.Equal(GemOnlineAcknowledgement.Accepted, await pair.Host.RequestOnlineAsync(GemPair.Token));
        Assert.NotNull(await pair.Host.AreYouThereAsync(GemPair.Token));
    }

    [Fact]
    public async Task Status_values_come_back_aligned_with_what_was_asked_for()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        _pressure = 500d;

        using var status = await pair.Host.ReadStatusAsync([2, 999, 1], GemPair.Token);

        Assert.Equal(3, status.Item!.Count);
        Assert.Equal(500d, status.Item[0].FirstValueOrDefault<double>());

        // E5 answers an unknown identifier with a zero-length item in its place rather than by
        // leaving it out, so position two is still the third thing asked for. Dropping it would
        // shift every later value by one and nothing would say so.
        Assert.Equal(0, status.Item[1].Count);
        Assert.Equal((byte)5, status.Item[2].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_namelist_says_which_identifiers_the_machine_does_not_have()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var namelist = await pair.Host.ReadStatusNamelistAsync([2, 999], GemPair.Token);

        Assert.Equal(2, namelist.Count);

        Assert.Equal(2UL, namelist[0].Id);
        Assert.Equal("Pressure", namelist[0].Name);
        Assert.Equal("mbar", namelist[0].Units);
        Assert.True(namelist[0].Exists);

        // Not an error and not an omission: an empty name is the machine saying "not mine".
        Assert.Equal(999UL, namelist[1].Id);
        Assert.False(namelist[1].Exists);
    }

    // ----------------------------------------------------------------- stream 2

    [Fact]
    public async Task A_constant_can_be_read_written_and_refused()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using (var before = await pair.Host.ReadConstantsAsync([100], GemPair.Token))
        {
            Assert.Equal(42u, before.Item![0].FirstValueOrDefault<uint>());
        }

        Assert.Equal(
            GemEquipmentConstantAcknowledgement.Accepted,
            await pair.Host.WriteConstantsAsync([(100UL, SecsItem.U4(50u))], GemPair.Token));

        Assert.Equal(50u, _limit);

        // Out of range, and the equipment's Write delegate is the authority on that rather than the
        // bounds in the namelist - a range is not always a pair of numbers.
        Assert.NotEqual(
            GemEquipmentConstantAcknowledgement.Accepted,
            await pair.Host.WriteConstantsAsync([(100UL, SecsItem.U4(500u))], GemPair.Token));

        Assert.Equal(50u, _limit);
    }

    [Fact]
    public async Task A_constant_namelist_carries_its_bounds_as_items()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var namelist = await pair.Host.ReadConstantNamelistAsync([100, 101], GemPair.Token);

        Assert.Equal(2, namelist.Count);
        Assert.Equal("Limit", namelist[0].Name);
        Assert.Equal("mbar", namelist[0].Units);

        // Encoded rather than decoded, because an ECMIN may be any format E5 allows and the reply
        // it arrived in is long gone by now. Bytes can be kept where a live item tree cannot.
        using var minimum = SecsItem.Decode(namelist[0].Minimum);
        using var maximum = SecsItem.Decode(namelist[0].Maximum);
        using var fallback = SecsItem.Decode(namelist[0].Default);

        Assert.Equal(0u, minimum.FirstValueOrDefault<uint>());
        Assert.Equal(100u, maximum.FirstValueOrDefault<uint>());
        Assert.Equal(42u, fallback.FirstValueOrDefault<uint>());

        // And a constant with no documented bound comes back empty rather than as two bytes that
        // decode to an empty list. "Not documented" is worth saying without a decode.
        Assert.Equal("Plain", namelist[1].Name);
        Assert.True(namelist[1].Minimum.IsEmpty);
        Assert.True(namelist[1].Maximum.IsEmpty);
        Assert.True(namelist[1].Default.IsEmpty);
    }

    [Fact]
    public async Task The_clock_can_be_read_and_set()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        Assert.NotNull(await pair.Host.ReadClockAsync(GemPair.Token));

        var when = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(GemTimeAcknowledgement.Accepted, await pair.Host.SetClockAsync(when, GemPair.Token));

        var read = await pair.Host.ReadClockAsync(GemPair.Token);

        Assert.NotNull(read);

        // To the second: E5's sixteen-character form carries hundredths, and the equipment keeps
        // the difference as an offset rather than touching the operating system clock.
        Assert.Equal(when.UtcDateTime, read.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_host_configures_its_own_reporting_and_receives_what_it_asked_for()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        ulong seenEvent = 0;
        var seenReports = 0;
        var seenPressure = 0d;
        var seenLot = string.Empty;

        pair.Host.EventReportReceived += (_, e) =>
        {
            seenEvent = e.EventId;
            seenReports = e.Reports.Count;

            // Read inside the handler. The values are a view into the message that arrived, and it
            // is disposed the moment this returns.
            seenPressure = e.Reports[0].Values[0].FirstValueOrDefault<double>();
            seenLot = e.Reports[0].Values[1].GetString();
        };

        var host = pair.Host;
        var token = GemPair.Token;

        // Delete first: a host that inherits definitions from whoever had the link last is a host
        // reading somebody else's reports.
        Assert.Equal(GemDefineReportAcknowledgement.Accepted, await host.DeleteAllReportsAsync(token));
        Assert.Equal(GemDefineReportAcknowledgement.Accepted, await host.DefineReportsAsync([(6_000UL, [2UL, 10UL])], token));
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, await host.LinkEventsAsync([(1_000UL, [6_000UL])], token));
        Assert.Equal(GemEnableEventAcknowledgement.Accepted, await host.EnableEventsAsync(true, [1_000UL], token));

        _pressure = 777d;

        Assert.True(await pair.Equipment.TriggerEventAsync(1_000, token));

        await GemPair.WaitUntilAsync(() => seenEvent != 0, "the event report to reach the host");

        Assert.Equal(1_000UL, seenEvent);
        Assert.Equal(1, seenReports);
        Assert.Equal(777d, seenPressure);
        Assert.Equal("LOT-001", seenLot);
    }

    [Fact]
    public async Task A_remote_command_reaches_the_machine_and_its_acknowledgement_is_not_a_byte()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var hcack = await pair.Host.SendCommandAsync("START", [("LOTID", "LOT-2026")], GemPair.Token);

        // HCACK 0 is done and HCACK 4 is "accepted, it will finish later". Both are successes, and
        // a host reading the byte rather than the enum is one typo from treating 1 - refused - as
        // the same kind of answer.
        Assert.Equal(GemHostCommandAcknowledgement.Accepted, hcack);
        Assert.Equal("LOT-2026", _startedLot);

        var unknown = await pair.Host.SendCommandAsync("NOPE", [], GemPair.Token);

        Assert.NotEqual(GemHostCommandAcknowledgement.Accepted, unknown);
        Assert.NotEqual(GemHostCommandAcknowledgement.CompletionLater, unknown);
    }

    [Fact]
    public async Task An_equipment_specific_acknowledgement_has_a_name_rather_than_being_a_cast()
    {
        // The machine is played by hand here, and it has to be. E5 reserves HCACK 7 to 63 for the
        // equipment, and this enum used to stop at 6 - so one of those arrived as a cast with no
        // name on it, printing as a number and missed by any switch without a default. It was
        // never the dangerous direction, because an unnamed code is not a success either way, but
        // it was a hole; it was closed in the chapter that served HCACK a second time, for S18F2,
        // because one item cannot be read two ways in one repository.
        //
        // A real GemEquipment cannot send 40: it writes whatever a handler returns, and a handler
        // returns this enum. The first version of this test returned Other, the machine wrote 7,
        // and the cast gave Other back - so it passed with the fix reverted and proved nothing.
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var machineRoutes = new SecsMessageRouterBuilder();

        machineRoutes.OnPrimary(2, 41, async (transaction, token) =>
        {
            using var reply = new SecsMessage(2, 42, replyExpected: false)
            {
                Name = "HostCommandAcknowledge",
                Item = SecsItem.L(SecsItem.B(40).Named("HCACK"), SecsItem.L()),
            };

            await transaction.ReplyAsync(reply, token).ConfigureAwait(false);
        });

        await using var hostLink = new HsmsConnection(options, active, new SecsMessageRouterBuilder().Build(), clock);
        await using var machineLink = new HsmsConnection(options, passive, machineRoutes.Build(), clock);

        using var cts = new CancellationTokenSource();
        var running = Task.WhenAll(hostLink.RunAsync(cts.Token), machineLink.RunAsync(cts.Token));

        try
        {
            for (var attempt = 0; attempt < 5_000 && !(hostLink.IsSelected && machineLink.IsSelected); attempt++)
            {
                await Task.Delay(1, GemPair.Token);
            }

            var host = new GemHost(new GemHostOptions(), hostLink, clock);

            var hcack = await host.SendCommandAsync("VENDOR", [], GemPair.Token);

            Assert.Equal(GemHostCommandAcknowledgement.Other, hcack);
            Assert.Equal("Other", hcack.ToString());
            Assert.True(Enum.IsDefined(hcack));
            Assert.NotEqual(GemHostCommandAcknowledgement.Accepted, hcack);
            Assert.NotEqual(GemHostCommandAcknowledgement.CompletionLater, hcack);
        }
        finally
        {
            await cts.CancelAsync();

            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how the loops stop.
            }

            await active.DisposeAsync();
            await passive.DisposeAsync();
        }
    }

    // ----------------------------------------------------------------- stream 5

    [Fact]
    public async Task An_alarm_arrives_and_the_listings_agree_with_it()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemAlarmReport? seen = null;
        pair.Host.AlarmReceived += (_, e) => seen = e.Alarm;

        Assert.Equal(
            GemAlarmAcknowledgement.Accepted,
            await pair.Host.EnableAlarmAsync(true, 9_000, GemPair.Token));

        Assert.True(await pair.Equipment.SetAlarmAsync(9_000, GemPair.Token));

        await GemPair.WaitUntilAsync(() => seen is not null, "the alarm to reach the host");

        Assert.Equal(9_000UL, seen!.Id);
        Assert.True(seen.IsSet);
        Assert.Equal("Chamber pressure out of range", seen.Text);

        var listed = Assert.Single(await pair.Host.ListAlarmsAsync([], GemPair.Token));

        Assert.Equal(9_000UL, listed.Id);
        Assert.True(listed.IsSet);

        var enabled = Assert.Single(await pair.Host.ListEnabledAlarmsAsync(GemPair.Token));

        Assert.Equal(9_000UL, enabled.Id);

        // Disabling is the other half of the ALED encoding, and the equipment reads the conformant
        // B[1] form this host sends rather than only the BOOLEAN its own tests used to send.
        Assert.Equal(
            GemAlarmAcknowledgement.Accepted,
            await pair.Host.EnableAlarmAsync(false, 9_000, GemPair.Token));

        Assert.Empty(await pair.Host.ListEnabledAlarmsAsync(GemPair.Token));
    }

    // ----------------------------------------------------------------- stream 7

    [Fact]
    public async Task A_recipe_goes_down_and_comes_back_in_both_flavours()
    {
        await using var pair = GemPair.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore());

        await pair.OpenAsync();

        var host = pair.Host;
        var token = GemPair.Token;
        var body = new byte[] { 1, 2, 3, 4, 5 };

        Assert.Equal(
            GemProcessProgramGrant.Ok,
            await host.InquireProcessProgramAsync("RECIPE-7", body.Length, token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await host.SendProcessProgramAsync("RECIPE-7", body, token));

        var read = await host.RequestProcessProgramAsync("RECIPE-7", token);

        Assert.NotNull(read);
        Assert.Equal(body, read.Value.ToArray());

        // A program nobody sent: S7F6 has no acknowledgement code to carry a refusal, so the
        // zero-length body is the refusal and this is null rather than empty.
        Assert.Null(await host.RequestProcessProgramAsync("NOPE", token));

        var shaped = new GemFormattedProcessProgram(
            "SHAPED",
            "BRBTOS-1",
            "0.1.0",
            [new GemProcessProgramCommand(12, ["SETPOINT", "350.0"])]);

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await host.SendFormattedProcessProgramAsync(shaped, token));

        var back = await host.RequestFormattedProcessProgramAsync("SHAPED", token);

        Assert.NotNull(back);
        Assert.Equal("SHAPED", back.Id);
        Assert.Equal("BRBTOS-1", back.ModelName);
        Assert.Equal((byte)12, Assert.Single(back.Commands).Code);
        Assert.Equal(["SETPOINT", "350.0"], back.Commands[0].Parameters);

        Assert.Null(await host.RequestFormattedProcessProgramAsync("NOPE", token));

        // One directory over both halves, which is what the equipment does with the two stores.
        Assert.Equal(["RECIPE-7", "SHAPED"], await host.ListProcessProgramsAsync(token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await host.DeleteProcessProgramsAsync([], token));

        Assert.Empty(await host.ListProcessProgramsAsync(token));
    }

    // ----------------------------------------------------------------- stream 9

    [Fact]
    public async Task A_message_the_machine_does_not_implement_arrives_as_an_error_report()
    {
        // No spool, so S6F23 is not in the equipment's routing table at all.
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        byte? function = null;
        pair.Host.ErrorReported += (_, e) => function = e.Function;

        var asked = pair.Host.RequestSpooledDataAsync(GemSpooledDataRequest.Transmit, GemPair.Token).AsTask();

        await GemPair.WaitUntilAsync(() => function is not null, "a stream 9 report to arrive");

        // S9F5, not S9F3: stream 6 is a stream the equipment understands, and 23 is a function in
        // it that this machine does not. The fork is the whole reason spooling is declared
        // separately from the rest.
        Assert.Equal((byte)5, function);

        // And the request itself gets no answer, because an error report is not a secondary. It
        // waits out T3 and throws - which is how "I do not do that" reaches a caller differently
        // from "no, not now".
        await pair.AdvanceUntilAsync(TimeSpan.FromSeconds(5), () => asked.IsCompleted, "T3 to expire");
        await Assert.ThrowsAsync<SecsTimeoutException>(() => asked);
    }
}
