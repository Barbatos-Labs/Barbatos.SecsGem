// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The E30 semantics of S2F33, S2F35, S2F37 and S5F3, tested where they live.
/// </summary>
/// <remarks>
/// Not one socket, clock or state machine in this file. "An S2F33 naming an unknown VID is DRACK 4
/// and changes nothing" is a fact about the model, so it is checked against the model - and the
/// handler above it is left with nothing to get wrong but a byte.
/// </remarks>
public class GemEquipmentModelTests
{
    private static GemEquipmentModel Model(Action<GemEquipmentModelBuilder>? extra = null)
    {
        var builder = new GemEquipmentModelBuilder()
            .AddStatusVariable(1, "A", () => SecsItem.U4(1u))
            .AddStatusVariable(2, "B", () => SecsItem.U4(2u))
            .AddStatusVariable(3, "C", () => SecsItem.U4(3u))
            .AddEvent(100, "First")
            .AddEvent(200, "Second")
            .AddEvent(300, "Disabled", enabledByDefault: false)
            .AddReport(10, 1, 2)
            .AddAlarm(new GemAlarm { Id = 900, Text = "Nine hundred" })
            .AddAlarm(new GemAlarm { Id = 901, Text = "Nine oh one", EnabledByDefault = false });

        extra?.Invoke(builder);
        return builder.Build();
    }

    // ----------------------------------------------------------------- defaults

    [Fact(Timeout = 15_000)]
    public void A_report_defined_at_configuration_time_is_there_from_the_start()
    {
        var model = Model();

        Assert.Equal<uint[]>([1, 2], [.. model.VariablesForReport(10)]);
    }

    [Fact(Timeout = 15_000)]
    public void EnabledByDefault_decides_the_starting_state()
    {
        var model = Model();

        Assert.True(model.IsEventEnabled(100));
        Assert.False(model.IsEventEnabled(300));
        Assert.True(model.IsAlarmEnabled(900));
        Assert.False(model.IsAlarmEnabled(901));
    }

    [Fact(Timeout = 15_000)]
    public void Nothing_is_linked_until_a_host_links_it()
    {
        var model = Model();

        // A report exists; an event is enabled; and still no report goes out, because E30 makes
        // the host say which reports belong to which event. Equipment that guessed would send a
        // host data it never asked for.
        Assert.Empty(model.ReportsForEvent(100));
    }

    // ----------------------------------------------------------------- S2F33

    [Fact(Timeout = 15_000)]
    public void Defining_a_report_accepts_it()
    {
        var model = Model();

        var drack = model.DefineReports([(20, new uint[] { 2, 3 })]);

        Assert.Equal(GemDefineReportAcknowledgement.Accepted, drack);
        Assert.Equal<uint[]>([2, 3], [.. model.VariablesForReport(20)]);
    }

    [Fact(Timeout = 15_000)]
    public void Defining_a_report_that_names_an_unknown_variable_is_refused_and_changes_nothing()
    {
        var model = Model();

        var drack = model.DefineReports([(20, new uint[] { 2, 999 })]);

        Assert.Equal(GemDefineReportAcknowledgement.UnknownVariable, drack);
        Assert.Empty(model.VariablesForReport(20));

        // The whole batch is rejected, not the bad entry: a host told DRACK 4 is entitled to
        // believe its definition did not take effect.
        Assert.Equal<uint[]>([1, 2], [.. model.VariablesForReport(10)]);
    }

    [Fact(Timeout = 15_000)]
    public void Defining_a_report_that_already_exists_is_refused()
    {
        var model = Model();

        var drack = model.DefineReports([(10, new uint[] { 3 })]);

        Assert.Equal(GemDefineReportAcknowledgement.ReportAlreadyDefined, drack);
        Assert.Equal<uint[]>([1, 2], [.. model.VariablesForReport(10)]);
    }

    [Fact(Timeout = 15_000)]
    public void A_report_with_no_variables_is_a_deletion()
    {
        var model = Model();

        var drack = model.DefineReports([(10, Array.Empty<uint>())]);

        Assert.Equal(GemDefineReportAcknowledgement.Accepted, drack);
        Assert.Empty(model.VariablesForReport(10));
    }

    [Fact(Timeout = 15_000)]
    public void An_empty_definition_list_deletes_every_report_and_every_link()
    {
        var model = Model();
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 10 })]));

        var drack = model.DefineReports([]);

        Assert.Equal(GemDefineReportAcknowledgement.Accepted, drack);
        Assert.Empty(model.DefinedReports);

        // A link to a report that no longer exists is not a link.
        Assert.Empty(model.ReportsForEvent(100));
    }

    [Fact(Timeout = 15_000)]
    public void Deleting_one_report_unlinks_it_without_touching_the_others()
    {
        var model = Model();
        Assert.Equal(GemDefineReportAcknowledgement.Accepted, model.DefineReports([(20, new uint[] { 3 })]));
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 10, 20 })]));

        Assert.Equal(GemDefineReportAcknowledgement.Accepted, model.DefineReports([(10, Array.Empty<uint>())]));

        Assert.Equal<uint[]>([20], [.. model.ReportsForEvent(100)]);
    }

    // ----------------------------------------------------------------- S2F35

    [Fact(Timeout = 15_000)]
    public void Linking_a_report_to_an_event_accepts_it()
    {
        var model = Model();

        var lrack = model.LinkEventReports([(100, new uint[] { 10 })]);

        Assert.Equal(GemLinkReportAcknowledgement.Accepted, lrack);
        Assert.Equal<uint[]>([10], [.. model.ReportsForEvent(100)]);
    }

    [Fact(Timeout = 15_000)]
    public void Linking_an_unknown_event_is_refused()
    {
        var model = Model();

        Assert.Equal(
            GemLinkReportAcknowledgement.UnknownEvent,
            model.LinkEventReports([(999, new uint[] { 10 })]));
    }

    [Fact(Timeout = 15_000)]
    public void Linking_an_undefined_report_is_refused()
    {
        var model = Model();

        Assert.Equal(
            GemLinkReportAcknowledgement.UnknownReport,
            model.LinkEventReports([(100, new uint[] { 999 })]));

        Assert.Empty(model.ReportsForEvent(100));
    }

    [Fact(Timeout = 15_000)]
    public void Linking_an_event_that_is_already_linked_is_refused()
    {
        var model = Model();
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 10 })]));

        Assert.Equal(
            GemLinkReportAcknowledgement.LinkAlreadyDefined,
            model.LinkEventReports([(100, new uint[] { 10 })]));
    }

    [Fact(Timeout = 15_000)]
    public void A_link_with_no_reports_removes_that_event_only()
    {
        var model = Model();
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 10 }), (200, new uint[] { 10 })]));

        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, Array.Empty<uint>())]));

        Assert.Empty(model.ReportsForEvent(100));
        Assert.Equal<uint[]>([10], [.. model.ReportsForEvent(200)]);
    }

    [Fact(Timeout = 15_000)]
    public void An_empty_link_list_removes_every_link()
    {
        var model = Model();
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 10 })]));

        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([]));

        Assert.Empty(model.ReportsForEvent(100));

        // The reports themselves survive: unlinking is not undefining.
        Assert.Equal<uint[]>([1, 2], [.. model.VariablesForReport(10)]);
    }

    // ----------------------------------------------------------------- S2F37

    [Fact(Timeout = 15_000)]
    public void Enabling_a_named_event_leaves_the_others_alone()
    {
        var model = Model();

        Assert.Equal(GemEnableEventAcknowledgement.Accepted, model.SetEventsEnabled([300], enabled: true));

        Assert.True(model.IsEventEnabled(300));
        Assert.True(model.IsEventEnabled(100));
    }

    [Fact(Timeout = 15_000)]
    public void An_empty_event_list_means_every_event()
    {
        var model = Model();

        Assert.Equal(GemEnableEventAcknowledgement.Accepted, model.SetEventsEnabled([], enabled: false));

        Assert.False(model.IsEventEnabled(100));
        Assert.False(model.IsEventEnabled(200));
        Assert.False(model.IsEventEnabled(300));

        Assert.Equal(GemEnableEventAcknowledgement.Accepted, model.SetEventsEnabled([], enabled: true));

        Assert.True(model.IsEventEnabled(100));
        Assert.True(model.IsEventEnabled(300));
    }

    [Fact(Timeout = 15_000)]
    public void Enabling_an_unknown_event_is_refused_and_changes_nothing()
    {
        var model = Model();

        Assert.Equal(
            GemEnableEventAcknowledgement.UnknownEvent,
            model.SetEventsEnabled([100, 999], enabled: false));

        Assert.True(model.IsEventEnabled(100));
    }

    // ----------------------------------------------------------------- S5F3

    [Fact(Timeout = 15_000)]
    public void Disabling_one_alarm_leaves_the_others_alone()
    {
        var model = Model();

        Assert.Equal(GemAlarmAcknowledgement.Accepted, model.SetAlarmEnabled(900, enabled: false));

        Assert.False(model.IsAlarmEnabled(900));
    }

    [Fact(Timeout = 15_000)]
    public void Alarm_identifier_zero_means_every_alarm()
    {
        var model = Model();

        Assert.Equal(GemAlarmAcknowledgement.Accepted, model.SetAlarmEnabled(0, enabled: true));

        Assert.True(model.IsAlarmEnabled(900));
        Assert.True(model.IsAlarmEnabled(901));

        Assert.Equal(GemAlarmAcknowledgement.Accepted, model.SetAlarmEnabled(0, enabled: false));

        Assert.False(model.IsAlarmEnabled(900));
        Assert.False(model.IsAlarmEnabled(901));
    }

    [Fact(Timeout = 15_000)]
    public void Enabling_an_unknown_alarm_is_an_error()
    {
        var model = Model();

        Assert.Equal(GemAlarmAcknowledgement.Error, model.SetAlarmEnabled(12_345, enabled: true));
    }

    // ----------------------------------------------------------------- reset

    [Fact(Timeout = 15_000)]
    public void Reset_puts_back_what_the_model_was_built_with()
    {
        var model = Model();

        Assert.Equal(GemDefineReportAcknowledgement.Accepted, model.DefineReports([(20, new uint[] { 3 })]));
        Assert.Equal(GemLinkReportAcknowledgement.Accepted, model.LinkEventReports([(100, new uint[] { 20 })]));
        Assert.Equal(GemEnableEventAcknowledgement.Accepted, model.SetEventsEnabled([], enabled: false));

        model.Reset();

        Assert.Equal<uint[]>([1, 2], [.. model.VariablesForReport(10)]);
        Assert.Empty(model.VariablesForReport(20));
        Assert.Empty(model.ReportsForEvent(100));
        Assert.True(model.IsEventEnabled(100));
        Assert.False(model.IsEventEnabled(300));
    }

    // ----------------------------------------------------------------- the builder

    [Fact(Timeout = 15_000)]
    public void A_duplicate_variable_identifier_is_a_configuration_error()
    {
        var builder = new GemEquipmentModelBuilder().AddStatusVariable(1, "A", () => SecsItem.U4(1u));

        // Two variables sharing an SVID would surface as a host reading the wrong sensor, which is
        // not a thing to discover in a fab.
        Assert.Throws<ArgumentException>(() => builder.AddStatusVariable(1, "B", () => SecsItem.U4(2u)));
    }

    [Fact(Timeout = 15_000)]
    public void A_duplicate_event_identifier_is_a_configuration_error()
    {
        var builder = new GemEquipmentModelBuilder().AddEvent(1, "First");

        Assert.Throws<ArgumentException>(() => builder.AddEvent(1, "Again"));
    }

    [Fact(Timeout = 15_000)]
    public void An_alarm_numbered_zero_is_refused()
    {
        var builder = new GemEquipmentModelBuilder();

        // S5F3 reserves zero for "every alarm", so an alarm with that identifier could never be
        // enabled or disabled on its own.
        Assert.Throws<ArgumentException>(() => builder.AddAlarm(new GemAlarm { Id = 0, Text = "Nowhere" }));
    }

    [Fact(Timeout = 15_000)]
    public void A_default_report_naming_an_unknown_variable_fails_the_build()
    {
        var builder = new GemEquipmentModelBuilder()
            .AddStatusVariable(1, "A", () => SecsItem.U4(1u))
            .AddReport(10, 1, 2);

        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("variable 2", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public void A_variable_is_read_every_time_it_is_asked_for()
    {
        var reads = 0;
        var model = new GemEquipmentModelBuilder()
            .AddStatusVariable(1, "Counter", () =>
            {
                reads++;
                return SecsItem.U4((uint)reads);
            })
            .Build();

        using var first = model.Variables[1].Read();
        using var second = model.Variables[1].Read();

        // A cached status variable reports the chamber pressure from whenever the cache was filled,
        // which reads as a sensor fault rather than as a bug.
        Assert.Equal(1u, first.FirstValueOrDefault<uint>());
        Assert.Equal(2u, second.FirstValueOrDefault<uint>());
    }
}
