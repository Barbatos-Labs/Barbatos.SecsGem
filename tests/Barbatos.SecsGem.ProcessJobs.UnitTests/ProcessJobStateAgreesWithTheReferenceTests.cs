// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// <see cref="ProcessJobState"/> against <c>PRSTATE</c>, the reference's own table for it.
/// </summary>
/// <remarks>
/// <para>
/// This package's state machine comes from SEMI E40, not from the E5 item dictionary - and the
/// fourth provenance measure turned up the fact that makes this test worth writing: the dictionary
/// <strong>also</strong> defines the states, as <c>PRSTATE</c> with ten rows, and
/// <strong>no message in the reference names it</strong>. Two independent statements of one thing,
/// with nothing connecting them.
/// </para>
/// <para>
/// That is the shape of mistake this repository has made before. A stale <c>GRANT</c> figure
/// survived a chapter because one side was a hand count; the way it was caught was a measure that
/// made the two sides meet. So they meet here.
/// </para>
/// <para>
/// This is the first test in this repository that checks a shipping package against the catalogue.
/// It is why the test project references <c>Barbatos.SecsGem.Catalog</c> while the package itself
/// does not and must not: the dependency is the test's, not the library's.
/// </para>
/// </remarks>
public class ProcessJobStateAgreesWithTheReferenceTests
{
    private static SecsDataItemInfo ProcessState
    {
        get
        {
            Assert.True(SecsCatalog.TryGetDataItem("PRSTATE", out var item));
            return item!;
        }
    }

    [Fact]
    public void The_reference_defines_the_states_and_names_them_from_no_message()
    {
        // The premise, asserted so the rest of the file cannot be read as a claim about the wire.
        // Nothing can carry PRSTATE, so this package reaches E40's states by another route and the
        // agreement below is a coincidence worth keeping rather than a protocol requirement.
        Assert.True(ProcessState.IsNamedByNoMessage);
        Assert.Equal(SecsFormat.U1, ProcessState.Format);
        Assert.Equal(10, ProcessState.Codes.Count);
    }

    [Fact]
    public void Every_state_this_package_has_sits_on_the_number_the_reference_gives_it()
    {
        var reference = ProcessState.Codes.ToDictionary(
            code => code.Value,
            code => code.Meaning,
            StringComparer.Ordinal);

        // Numbers first, because a state machine that agreed on the names and not the numbers
        // would still put the wrong byte on a wire that carried it.
        Assert.Equal("Queued (pooled, awaiting selection)", reference["0"]);
        Assert.Equal("Setting up", reference["1"]);
        Assert.Equal("Waiting for start", reference["2"]);
        Assert.Equal("Processing", reference["3"]);
        Assert.Equal("Processing complete", reference["4"]);
        Assert.Equal("Pausing", reference["6"]);
        Assert.Equal("Paused", reference["7"]);
        Assert.Equal("Stopping", reference["8"]);
        Assert.Equal("Aborting", reference["9"]);

        Assert.Equal((byte)0, (byte)ProcessJobState.Queued);
        Assert.Equal((byte)1, (byte)ProcessJobState.SettingUp);
        Assert.Equal((byte)2, (byte)ProcessJobState.WaitingForStart);
        Assert.Equal((byte)3, (byte)ProcessJobState.Processing);
        Assert.Equal((byte)4, (byte)ProcessJobState.ProcessComplete);
        Assert.Equal((byte)6, (byte)ProcessJobState.Pausing);
        Assert.Equal((byte)7, (byte)ProcessJobState.Paused);
        Assert.Equal((byte)8, (byte)ProcessJobState.Stopping);
        Assert.Equal((byte)9, (byte)ProcessJobState.Aborting);
    }

    [Fact]
    public void The_gap_at_five_is_the_references_gap_and_not_an_accident()
    {
        // The detail a hand-written enum gets wrong, and the reason this test earns its keep:
        // PRSTATE reserves 5, so E40's nine states are numbered 0 to 9 with a hole. An enum
        // written by counting would have Pausing at 5 and every state after it one too low.
        Assert.Equal("Reserved", Assert.Single(ProcessState.Codes, code => code.Value == "5").Meaning);

        Assert.DoesNotContain((byte)5, Enum.GetValues<ProcessJobState>().Select(state => (byte)state));
    }

    [Fact]
    public void The_other_three_E40_tables_the_reference_defines_and_uses_nowhere_also_agree()
    {
        // The sweep that followed the measure found four E40 items specified with full tables and
        // named by no message. PRSTATE is above; these are the rest, and all three agree - which
        // is the outcome a measure should be able to reach, not only disagreement.
        Assert.True(SecsCatalog.TryGetDataItem("PRRECIPEMETHOD", out var method));
        Assert.True(method!.IsNamedByNoMessage);
        Assert.Equal("1", method.Codes[0].Value);
        Assert.Equal((byte)1, (byte)ProcessJobRecipeMethod.WithoutVariableTuning);
        Assert.Equal((byte)2, (byte)ProcessJobRecipeMethod.WithVariableTuning);
        Assert.Equal(method.Codes.Count, Enum.GetValues<ProcessJobRecipeMethod>().Length);

        Assert.True(SecsCatalog.TryGetDataItem("PRMTRLORDER", out var order));
        Assert.True(order!.IsNamedByNoMessage);
        Assert.Equal((byte)1, (byte)ProcessJobMaterialOrder.ArrivalSequence);
        Assert.Equal((byte)2, (byte)ProcessJobMaterialOrder.MaximizeThroughput);
        Assert.Equal((byte)3, (byte)ProcessJobMaterialOrder.ListedSequence);
        Assert.Equal(order.Codes.Count, Enum.GetValues<ProcessJobMaterialOrder>().Length);

        // Both of these number from 1, not 0 - which is the detail worth pinning, because every
        // other code table in this repository starts at zero and an enum written to that habit
        // would be one out across the board.
        Assert.DoesNotContain("0", method.Codes.Select(code => code.Value));
        Assert.DoesNotContain("0", order.Codes.Select(code => code.Value));

        Assert.True(SecsCatalog.TryGetDataItem("PREACK", out var acknowledgement));
        Assert.True(acknowledgement!.IsNamedByNoMessage);
        Assert.Equal((byte)0, (byte)ProcessJobAcknowledgement.Accepted);
        Assert.Equal((byte)1, (byte)ProcessJobAcknowledgement.Rejected);
    }

    [Fact]
    public void The_one_E40_table_nothing_here_models_is_recorded_rather_than_forgotten()
    {
        // PRCPREEXECHK enables or disables a pre-execution check on a process job. It is defined,
        // it carries two codes, no message names it, and this package does not model it. That is
        // a gap rather than a fault - nothing can carry the item - but a sweep that found it and
        // said nothing would leave the next reader to find it again.
        Assert.True(SecsCatalog.TryGetDataItem("PRCPREEXECHK", out var check));
        Assert.True(check!.IsNamedByNoMessage);
        Assert.Equal(2, check.Codes.Count);
    }

    [Fact]
    public void The_package_has_one_member_per_state_the_reference_names()
    {
        var reserved = 1;
        var named = ProcessState.Codes.Count - reserved;

        // Nine for nine, so neither side has a state the other lacks. A member added here without
        // a row there - or a row appearing in a refreshed reference - fails this rather than
        // passing unnoticed.
        Assert.Equal(named, Enum.GetValues<ProcessJobState>().Length);
    }
}
