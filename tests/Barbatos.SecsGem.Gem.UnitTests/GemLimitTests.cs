// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Limits monitoring: the host says which band it cares about, and the machine reports leaving it.
/// </summary>
/// <remarks>
/// What these tests can prove is that both ends of this library agree, which matters because three
/// of this capability's four message rows are representative entries in the catalogue rather than
/// definitions. What they cannot prove is that the nesting is what somebody else's host sends -
/// only a copy of SEMI E5 can say that, and the shapes are in one file so there is one place to
/// check.
/// </remarks>
public class GemLimitTests
{
    private const uint PressureVariable = 2;
    private const uint LimitEvent = 2_500;
    private const uint LimitReport = 6_500;

    private double _pressure = 1_013.25d;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(PressureVariable, "Pressure", () => SecsItem.F8(_pressure), "mbar", LimitEvent)
            .AddStatusVariable(3, "Recipe", () => SecsItem.A("RECIPE-1"), limitEventId: LimitEvent)
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-1"))
            .AddEvent(LimitEvent, "PressureLimitCrossed")
            .AddReport(LimitReport, PressureVariable);

    private static GemVariableLimitDefinition Band(
        uint variableId = PressureVariable,
        uint limitId = 1,
        double lower = 900d,
        double upper = 1_100d)
        => new(variableId, [new GemVariableLimit(limitId, lower, upper)]);

    /// <summary>Links the limit event to its report and switches it on, so a crossing can be seen.</summary>
    private static async Task WatchAsync(GemPair pair)
    {
        var token = GemPair.Token;

        Assert.Equal(
            GemLinkReportAcknowledgement.Accepted,
            await pair.Host.LinkEventsAsync([(LimitEvent, [LimitReport])], token));

        Assert.Equal(
            GemEnableEventAcknowledgement.Accepted,
            await pair.Host.EnableEventsAsync(true, [LimitEvent], token));
    }

    // ----------------------------------------------------------------- opt-in

    [Fact(Timeout = 15_000)]
    public void Limit_messages_are_declared_on_their_own()
    {
        Assert.Equal(2, GemEquipment.LimitMessages.Count);
        Assert.All(GemEquipment.LimitMessages, p => Assert.Equal(2, p.Stream));
        Assert.All(GemEquipment.LimitMessages, p => Assert.True(p.Function % 2 == 1));

        foreach (var pair in GemEquipment.LimitMessages)
        {
            Assert.DoesNotContain(pair, GemEquipment.HandledMessages);
        }
    }

    // ----------------------------------------------------------------- defining

    [Fact(Timeout = 15_000)]
    public async Task A_band_is_set_and_reported_back()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();

        var reply = await pair.Host.DefineVariableLimitsAsync([Band()], GemPair.Token);

        Assert.True(reply.Accepted);

        var variable = Assert.Single(reply.Variables);

        Assert.Equal(PressureVariable, variable.VariableId);
        Assert.True(variable.Accepted);
        Assert.Equal(GemVariableLimitAcknowledgement.Accepted, Assert.Single(variable.Limits).Acknowledgement);

        var attributes = Assert.Single(await pair.Host.ReadVariableLimitsAsync([], GemPair.Token));

        Assert.Equal(PressureVariable, attributes.VariableId);
        Assert.Equal("mbar", attributes.Units);

        var band = Assert.Single(attributes.Limits);

        Assert.Equal(1u, band.Id);
        Assert.Equal(900d, band.Lower);
        Assert.Equal(1_100d, band.Upper);

        // This equipment documents no range for a status variable, so it reports a zero-length
        // item - E5's way of saying there is no documented bound - and the host reads it as null
        // rather than as zero.
        Assert.Null(attributes.Minimum);
        Assert.Null(attributes.Maximum);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_variable_the_machine_does_not_have_is_refused_without_taking_the_others_down()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();

        var reply = await pair.Host.DefineVariableLimitsAsync(
            [Band(variableId: 999), Band()],
            GemPair.Token);

        // VLAACK says "not all of it", and the detail says which.
        Assert.False(reply.Accepted);
        Assert.Equal(2, reply.Variables.Count);

        Assert.False(reply.Variables[0].Accepted);
        Assert.Equal(
            GemVariableLimitAcknowledgement.NoSuchVariable,
            Assert.Single(reply.Variables[0].Limits).Acknowledgement);

        // And the one that was fine is in force, because the variables are independent.
        Assert.True(reply.Variables[1].Accepted);

        var attributes = await pair.Host.ReadVariableLimitsAsync([], GemPair.Token);

        Assert.Equal([PressureVariable], attributes.Select(entry => entry.VariableId));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_band_that_is_not_a_band_is_named_and_takes_its_variable_with_it()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();

        // Hand-built, because GemVariableLimit will not hold an upper below its lower - which is
        // the point: a host using this library cannot send one, and a host that is not might.
        using var request = new SecsMessage(2, 45, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(
                    SecsItem.U4(PressureVariable),
                    SecsItem.L(
                        SecsItem.L(SecsItem.U4(1u), SecsItem.L(SecsItem.F8(100d), SecsItem.F8(900d))),
                        SecsItem.L(SecsItem.U4(2u), SecsItem.L(SecsItem.F8(1_100d), SecsItem.F8(900d))))))),
        };

        using var reply = await pair.HostLink.SendAsync(request, GemPair.Token);

        Assert.Equal(46, reply.F);

        // VLAACK: not zero, because not all of it was taken.
        Assert.NotEqual((byte)0, reply.Item![0].FirstValueOrDefault<byte>());

        var variable = reply.Item[1][0];
        var limits = variable[1][1];

        // Limit 1 is upper 100 over lower 900, which is not a band: LIMITACK 4. Limit 2 was fine
        // on its own and gets LIMITACK 1, because the definition it belongs to is the thing in
        // error - nothing on this variable is in force.
        Assert.Equal((byte)1, variable[1][0].FirstValueOrDefault<byte>());
        Assert.Equal((byte)GemVariableLimitAcknowledgement.ValueError, limits[0][1].FirstValueOrDefault<byte>());
        Assert.Equal((byte)GemVariableLimitAcknowledgement.DefinitionError, limits[1][1].FirstValueOrDefault<byte>());

        Assert.Empty(await pair.Host.ReadVariableLimitsAsync([], GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Limits_are_removed_one_variable_at_a_time_or_all_at_once()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();

        Assert.True((await pair.Host.DefineVariableLimitsAsync(
            [Band(), Band(variableId: 3, limitId: 9)],
            GemPair.Token)).Accepted);

        Assert.Equal(2, (await pair.Host.ReadVariableLimitsAsync([], GemPair.Token)).Count);

        // A variable with no bands has its bands removed.
        Assert.True((await pair.Host.DefineVariableLimitsAsync(
            [new GemVariableLimitDefinition(3, [])],
            GemPair.Token)).Accepted);

        Assert.Equal([PressureVariable], (await pair.Host.ReadVariableLimitsAsync([], GemPair.Token))
            .Select(a => a.VariableId));

        // And an empty list removes every limit on every variable, the way an empty S2F33 removes
        // every report definition.
        Assert.True((await pair.Host.DeleteAllVariableLimitsAsync(GemPair.Token)).Accepted);
        Assert.Empty(await pair.Host.ReadVariableLimitsAsync([], GemPair.Token));
    }

    // ----------------------------------------------------------------- the crossing

    [Fact(Timeout = 15_000)]
    public async Task Leaving_the_band_is_an_event_and_a_wobble_inside_it_is_not()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();
        await WatchAsync(pair);

        List<ulong> events = [];
        pair.Host.EventReportReceived += (_, e) =>
        {
            lock (events)
            {
                events.Add(e.EventId);
            }
        };

        Assert.True((await pair.Host.DefineVariableLimitsAsync([Band()], GemPair.Token)).Accepted);

        var token = GemPair.Token;

        // The first look establishes which side the value is on. There is nothing to have crossed
        // from, so nothing is reported.
        _pressure = 1_013.25d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // Still inside. A value that wobbles within the band is silent, which is what makes the
        // band a deadband rather than a threshold.
        _pressure = 1_099d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // Out.
        _pressure = 1_200d;
        Assert.True(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // Further out is not another crossing.
        _pressure = 5_000d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // And back in is.
        _pressure = 1_000d;
        Assert.True(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        await GemPair.WaitUntilAsync(() => events.Count == 2, "both crossings to reach the host");

        Assert.Equal([LimitEvent, LimitEvent], events);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_value_exactly_on_a_bound_is_inside()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();
        await WatchAsync(pair);

        Assert.True((await pair.Host.DefineVariableLimitsAsync([Band()], GemPair.Token)).Accepted);

        var token = GemPair.Token;

        _pressure = 1_000d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // Inclusive at both ends, which is the choice that makes a band of [x, x] mean "exactly x"
        // rather than nothing at all.
        _pressure = 1_100d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        _pressure = 1_100.001d;
        Assert.True(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_variable_with_no_band_no_event_or_no_number_reports_nothing()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();
        await WatchAsync(pair);

        var token = GemPair.Token;

        // No band defined on it.
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // A band on a variable whose value is text. A band cannot contain a string, so a host that
        // put one there gets no events rather than a wrong one.
        Assert.True((await pair.Host.DefineVariableLimitsAsync(
            [Band(variableId: 3, limitId: 4)],
            token)).Accepted);

        Assert.False(await pair.Equipment.CheckLimitsAsync(3, token));
        Assert.False(await pair.Equipment.CheckLimitsAsync(3, token));

        // A variable that does not exist at all.
        Assert.False(await pair.Equipment.CheckLimitsAsync(999, token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Redefining_a_band_forgets_which_side_the_value_was_on()
    {
        await using var pair = GemPair.Start(Model, limits: true);
        await pair.OpenAsync();
        await WatchAsync(pair);

        var token = GemPair.Token;

        Assert.True((await pair.Host.DefineVariableLimitsAsync([Band()], token)).Accepted);

        _pressure = 1_000d;
        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // The host moves the band so the value is now outside it. That is the host's own edit, not
        // a crossing - so the first look at the new band establishes a zone and says nothing.
        Assert.True((await pair.Host.DefineVariableLimitsAsync(
            [Band(lower: 1_200d, upper: 1_300d)],
            token)).Accepted);

        Assert.False(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));

        // And from there it behaves normally.
        _pressure = 1_250d;
        Assert.True(await pair.Equipment.CheckLimitsAsync(PressureVariable, token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_crossing_with_no_host_listening_is_not_reported_but_is_remembered()
    {
        await using var link = GemLink.Start(Model, limits: true);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        await GemLink.WaitUntilAsync(() => !link.Equipment.IsCommunicating, "the equipment to be not communicating");

        using var defined = await link.RequestAsync(new SecsMessage(2, 45, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(
                    SecsItem.U4(PressureVariable),
                    SecsItem.L(SecsItem.L(
                        SecsItem.U4(1u),
                        SecsItem.L(SecsItem.F8(1_100d), SecsItem.F8(900d))))))),
        });

        Assert.Equal((byte)0, defined.Item![0].FirstValueOrDefault<byte>());

        _pressure = 1_000d;
        Assert.False(await link.Equipment.CheckLimitsAsync(PressureVariable, GemLink.Token));

        // Out of the band with nobody to tell: false, because nothing was reported.
        _pressure = 1_200d;
        Assert.False(await link.Equipment.CheckLimitsAsync(PressureVariable, GemLink.Token));

        // But the crossing was recorded, so coming back in is measured from outside rather than
        // from where the host last heard. Still false - still nobody listening - and the zone is
        // what is being asserted here.
        _pressure = 1_000d;
        Assert.False(await link.Equipment.CheckLimitsAsync(PressureVariable, GemLink.Token));
        Assert.False(await link.Equipment.CheckLimitsAsync(PressureVariable, GemLink.Token));
    }

    // ----------------------------------------------------------------- the band on its own

    [Fact(Timeout = 15_000)]
    public void A_band_refuses_what_cannot_be_a_band()
    {
        var band = new GemVariableLimit(1, 900d, 1_100d);

        Assert.True(band.Contains(900d));
        Assert.True(band.Contains(1_100d));
        Assert.False(band.Contains(899.999d));
        Assert.Equal("limit 1 [900, 1100]", band.ToString());

        // A single point is a band. An upper below a lower is not, and neither is a bound that is
        // not a number - both are refused where they are written rather than where they are sent.
        Assert.True(new GemVariableLimit(1, 5d, 5d).Contains(5d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemVariableLimit(1, 1_100d, 900d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemVariableLimit(1, double.NaN, 900d));
        Assert.Throws<ArgumentNullException>(() => new GemVariableLimitDefinition(1, null!));
    }
}
