// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Trace data collection: sample these variables on this interval, report them in groups.
/// </summary>
/// <remarks>
/// Every test here moves a fake clock rather than waiting on a real one, which is the only way a
/// capability built out of timers can be tested at all. A trace of four samples a second apart is
/// four <c>Advance</c> calls and takes no time.
/// </remarks>
public class GemTraceTests
{
    private double _pressure = 1_013.25d;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(_pressure), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-1"))
            .AddEvent(1_000, "ProcessEnd");

    private static GemTraceRequest Trace(
        uint id = 1,
        int totalSamples = 4,
        int groupSize = 2,
        double seconds = 1d,
        params uint[] variables)
        => new(
            id,
            TimeSpan.FromSeconds(seconds),
            totalSamples,
            groupSize,
            variables.Length == 0 ? [2u] : variables);

    /// <summary>Subscribes to the groups and reads the values out before they die with the message.</summary>
    private static List<(uint Trace, uint Sample, double[] Values)> Collect(GemHost host)
    {
        List<(uint Trace, uint Sample, double[] Values)> groups = [];

        host.TraceDataReceived += (_, e) =>
        {
            var values = new double[e.Samples.Count];

            for (var i = 0; i < e.Samples.Count; i++)
            {
                // Inside the handler: each sample is a view into the message that arrived.
                values[i] = e.Samples[i][0].FirstValueOrDefault<double>();
            }

            lock (groups)
            {
                groups.Add((e.TraceId, e.SampleNumber, values));
            }
        };

        return groups;
    }

    // ----------------------------------------------------------------- opt-in

    [Fact]
    public void Traces_are_declared_on_their_own()
    {
        Assert.Single(GemEquipment.TraceMessages);
        Assert.Equal(((byte)2, (byte)23), GemEquipment.TraceMessages[0]);
        Assert.DoesNotContain(GemEquipment.TraceMessages[0], GemEquipment.HandledMessages);

        // S6F1 is the equipment's to send and the host's to answer.
        Assert.Contains(((byte)6, (byte)1), GemHost.HandledMessages);
        Assert.DoesNotContain(((byte)6, (byte)1), GemEquipment.HandledMessages);
    }

    // ----------------------------------------------------------------- the sampling

    [Fact]
    public async Task A_trace_samples_on_its_interval_and_reports_in_groups()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        var groups = Collect(pair.Host);

        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 4, groupSize: 2), GemPair.Token));

        // Nothing yet: a trace reports when it has a group, not when it starts.
        Assert.Empty(groups);

        pair.Clock.Advance(TimeSpan.FromSeconds(2));

        await GemPair.WaitUntilAsync(() => groups.Count == 1, "the first group to arrive");

        pair.Clock.Advance(TimeSpan.FromSeconds(2));

        await GemPair.WaitUntilAsync(() => groups.Count == 2, "the second group to arrive");

        // SMPLN counts samples, not groups, and names the last one in the group - which is what
        // lets a host put a group that arrived late back in order.
        Assert.Equal(1u, groups[0].Trace);
        Assert.Equal(2u, groups[0].Sample);
        Assert.Equal(2, groups[0].Values.Length);
        Assert.Equal(4u, groups[1].Sample);

        // And it stopped itself: four samples were asked for and four were taken, so the slot is
        // free again even with room for only one trace.
        pair.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(20, GemPair.Token);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task The_last_group_goes_even_when_it_is_short()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        var groups = Collect(pair.Host);

        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 3, groupSize: 2), GemPair.Token));

        pair.Clock.Advance(TimeSpan.FromSeconds(3));

        await GemPair.WaitUntilAsync(() => groups.Count == 2, "both groups to arrive");

        // Two then one. The host asked for three samples and these are the end of them, so holding
        // the third back for a group that will never fill would lose it.
        Assert.Equal(2, groups[0].Values.Length);
        Assert.Single(groups[1].Values);
        Assert.Equal(3u, groups[1].Sample);
    }

    [Fact]
    public async Task Every_sample_carries_the_value_at_the_moment_it_was_taken()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        var groups = Collect(pair.Host);

        // Groups of one, so every sample is a message of its own and waiting for it is a condition
        // rather than a sleep. The first version of this test advanced the clock and slept five
        // milliseconds, which is long enough on an idle machine and not on a loaded one - it
        // failed about one full-suite run in twenty by reading the next value into the last
        // sample.
        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 3, groupSize: 1), GemPair.Token));

        var arrived = 0;

        foreach (var pressure in new[] { 100d, 200d, 300d })
        {
            _pressure = pressure;
            pair.Clock.Advance(TimeSpan.FromSeconds(1));

            arrived++;
            await GemPair.WaitUntilAsync(() => groups.Count == arrived, $"sample {arrived} to arrive");
        }

        // Each sample is the value at its own moment, rather than three reads of whatever the
        // variable holds when the group goes out.
        Assert.Equal([100d], groups[0].Values);
        Assert.Equal([200d], groups[1].Values);
        Assert.Equal([300d], groups[2].Values);
    }

    [Fact]
    public async Task A_trace_can_be_stopped_before_it_finishes()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        var groups = Collect(pair.Host);

        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 100, groupSize: 1), GemPair.Token));

        pair.Clock.Advance(TimeSpan.FromSeconds(1));
        await GemPair.WaitUntilAsync(() => groups.Count == 1, "the first sample to arrive");

        // S2F23 asking for no samples. Re-initialising a running trace to take none of them and
        // cancelling it are the same request.
        Assert.Equal(GemTraceAcknowledgement.Accepted, await pair.Host.StopTraceAsync(1, GemPair.Token));

        pair.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(20, GemPair.Token);

        Assert.Single(groups);

        // And stopping one that was never running is still accepted: the host wanted it stopped.
        Assert.Equal(GemTraceAcknowledgement.Accepted, await pair.Host.StopTraceAsync(99, GemPair.Token));
    }

    // ----------------------------------------------------------------- what TIAACK is for

    [Fact]
    public async Task A_variable_the_machine_does_not_trace_is_named_as_the_reason()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        Assert.Equal(
            GemTraceAcknowledgement.UnknownVariable,
            await pair.Host.StartTraceAsync(Trace(variables: [999u]), GemPair.Token));

        // A data variable is not a status variable: it only has a value inside the event it
        // belongs to, so tracing one would sample whatever it happened to hold.
        Assert.Equal(
            GemTraceAcknowledgement.UnknownVariable,
            await pair.Host.StartTraceAsync(Trace(variables: [10u]), GemPair.Token));
    }

    [Fact]
    public async Task A_group_size_that_does_not_fit_the_samples_asked_for_is_named_too()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        Assert.Equal(
            GemTraceAcknowledgement.InvalidGroupSize,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 4, groupSize: 5), GemPair.Token));

        Assert.Equal(
            GemTraceAcknowledgement.InvalidGroupSize,
            await pair.Host.StartTraceAsync(Trace(totalSamples: 4, groupSize: 0), GemPair.Token));
    }

    [Fact]
    public async Task More_variables_than_the_machine_will_sample_is_named_too()
    {
        await using var pair = GemPair.Start(Model, gem: options => options.MaxTraceVariables = 1, traces: true);
        await pair.OpenAsync();

        Assert.Equal(
            GemTraceAcknowledgement.TooManyVariables,
            await pair.Host.StartTraceAsync(Trace(variables: [1u, 2u]), GemPair.Token));

        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(variables: [2u]), GemPair.Token));
    }

    [Fact]
    public async Task One_trace_too_many_is_refused_but_replacing_one_is_not()
    {
        await using var pair = GemPair.Start(Model, gem: options => options.MaxTraces = 1, traces: true);
        await pair.OpenAsync();

        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(id: 1, totalSamples: 100), GemPair.Token));

        Assert.Equal(
            GemTraceAcknowledgement.NoMoreTracesAllowed,
            await pair.Host.StartTraceAsync(Trace(id: 2, totalSamples: 100), GemPair.Token));

        // Re-initialising the one that exists needs no slot: E5 defines S2F23 as initialising a
        // trace, and initialising the running one is what a host does to change its period.
        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(Trace(id: 1, totalSamples: 50, seconds: 2d), GemPair.Token));
    }

    [Fact]
    public async Task A_period_the_wire_cannot_carry_is_refused_before_it_is_sent()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        // DSPER is six or eight characters, so neither of these can travel - and a round trip
        // spent on arithmetic the host could have done is a round trip wasted.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await pair.Host.StartTraceAsync(Trace(seconds: 0d), GemPair.Token));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await pair.Host.StartTraceAsync(
                new GemTraceRequest(1, TimeSpan.FromHours(100), 4, 2, [2u]),
                GemPair.Token));
    }

    [Fact]
    public async Task A_period_in_hundredths_survives_the_wire()
    {
        await using var pair = GemPair.Start(Model, traces: true);
        await pair.OpenAsync();

        var groups = Collect(pair.Host);

        // 1.5 seconds: DSPER's eight-character form carries hundredths, so this is exactly
        // expressible where 1.505 would round.
        Assert.Equal(
            GemTraceAcknowledgement.Accepted,
            await pair.Host.StartTraceAsync(
                new GemTraceRequest(1, TimeSpan.FromSeconds(1.5d), 2, 2, [2u]),
                GemPair.Token));

        pair.Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(10, GemPair.Token);

        Assert.Empty(groups);

        pair.Clock.Advance(TimeSpan.FromSeconds(2));

        await GemPair.WaitUntilAsync(() => groups.Count == 1, "the group to arrive after three seconds");
    }

    [Fact]
    public async Task A_body_that_is_not_a_trace_request_is_refused_rather_than_run()
    {
        await using var link = GemLink.Start(Model, traces: true);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 23, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.A("000001")),
        });

        Assert.Equal(24, reply.F);

        // TIAACK has no code for "that body made no sense" - its five refusals each name a field -
        // so the specific code is a best effort. What matters is that it is not zero.
        Assert.NotEqual((byte)GemTraceAcknowledgement.Accepted, reply.Item!.FirstValueOrDefault<byte>());
    }

    /// <summary>
    /// A second S1F13 on an established conversation leaves a running trace alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The equipment cancels every trace where it resets the host's report definitions, which is
    /// on a <em>re-establish</em> - the link went down and came back, so the host has re-sent
    /// neither. A host that simply asks again on a live conversation has said nothing about its
    /// configuration, and <c>OnCommunicating</c> returns early for exactly that reason.
    /// </para>
    /// <para>
    /// So this is the half of that behaviour the harness can reach: an in-memory pair cannot
    /// re-select after a deselect, because the select is driven by the connection's own state
    /// machine and nothing public asks it to. The cancellation itself sits in the same
    /// <c>if</c> block as <c>Model.Reset()</c> and runs when it runs - which this test pins from
    /// the other side by proving neither happens when it should not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_second_request_to_establish_leaves_a_running_trace_alone()
    {
        await using var link = GemLink.Start(
            Model,
            options => options.MaxTraces = 1,
            traces: true);

        await link.OpenAsync();

        using var started = await link.RequestAsync(new SecsMessage(2, 23, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.A("000001"),
                SecsItem.U4(100u),
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.U4(2u))),
        });

        Assert.Equal((byte)GemTraceAcknowledgement.Accepted, started.Item!.FirstValueOrDefault<byte>());

        // A report definition as well, because it is the half of the same reset that is observable
        // from outside - and waiting for it is what makes this test deterministic rather than a
        // race against the equipment's loop.
        using var defined = await link.RequestAsync(new SecsMessage(2, 33, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(SecsItem.U4(6_000u), SecsItem.L(SecsItem.U4(2u))))),
        });

        Assert.Equal((byte)GemDefineReportAcknowledgement.Accepted, defined.Item!.FirstValueOrDefault<byte>());
        Assert.Contains(6_000u, link.Equipment.Model.DefinedReports);

        // The host establishes communications again, which is what a host that has just restarted
        // does. It has not re-sent its S2F23, and a trace it has forgotten about would keep
        // sampling with no TRID the host could cancel it by.
        using var established = await link.RequestAsync(new SecsMessage(1, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0")),
        });

        Assert.Equal((byte)GemCommunicationAcknowledgement.Accepted, established.Item![0].FirstValueOrDefault<byte>());

        // Nothing was reset, which is the documented behaviour: the report definition is still
        // there, so the trace is too.
        Assert.Contains(6_000u, link.Equipment.Model.DefinedReports);

        // Room for one trace, and the first one still has the slot.
        using var again = await link.RequestAsync(new SecsMessage(2, 23, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(2u),
                SecsItem.A("000001"),
                SecsItem.U4(100u),
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.U4(2u))),
        });

        Assert.Equal(
            (byte)GemTraceAcknowledgement.NoMoreTracesAllowed,
            again.Item!.FirstValueOrDefault<byte>());
    }

    // ----------------------------------------------------------------- the request on its own

    [Fact]
    public void A_request_owns_its_variables_and_says_what_it_asked_for()
    {
        List<uint> variables = [1, 2];
        var trace = new GemTraceRequest(7, TimeSpan.FromSeconds(1), 60, 10, variables);

        variables.Clear();

        Assert.Equal(2, trace.VariableIds.Count);
        Assert.Equal("trace 7: 60 samples of 2 variable(s) every 00:00:01 in groups of 10", trace.ToString());

        Assert.Equal("trace 7: cancel", new GemTraceRequest(7, TimeSpan.FromSeconds(1), 0, 0, []).ToString());
        Assert.Throws<ArgumentNullException>(() => new GemTraceRequest(1, TimeSpan.FromSeconds(1), 1, 1, null!));
    }
}
