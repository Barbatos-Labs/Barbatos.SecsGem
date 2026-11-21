// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// This package's code tables against the reference's, for the three items it shares with others.
/// </summary>
/// <remarks>
/// <para>
/// Stream 18 borrows most of its vocabulary, so three of its tables are items other packages here
/// also serve: <c>HCACK</c> with <c>Barbatos.SecsGem.Gem</c> and
/// <c>Barbatos.SecsGem.ProcessJobs</c>, <c>CPACK</c> with both of those, and <c>ALCD</c> with GEM.
/// The working agreement is that one item cannot mean two things in one repository - and until this
/// file the rule was asserted for <c>GRANT</c> and nothing else.
/// </para>
/// <para>
/// Each package asserting against the <em>reference's</em> table rather than against the others'
/// enums is what makes the agreement transitive: three packages that each match one table match
/// each other, and no pairwise list has to be kept in step.
/// </para>
/// </remarks>
public class SubsystemCodesAgreeWithTheReferenceTests
{
    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void The_command_acknowledgement_is_HCACKs_table()
    {
        var hcack = Item("HCACK");

        // Seven named codes and a reserved range, so eight rows and eight members - the last of
        // them the catch-all at the boundary. GEM's enum is numbered identically, which is the
        // whole point: S18F2 and S2F42 carry the same item.
        Assert.Equal(8, hcack.Codes.Count);
        Assert.Equal("7-63", hcack.Codes[^1].Value);

        Assert.Equal((byte)0, (byte)SubsystemCommandAcknowledgement.Accepted);
        Assert.Equal((byte)1, (byte)SubsystemCommandAcknowledgement.InvalidCommand);
        Assert.Equal((byte)2, (byte)SubsystemCommandAcknowledgement.CannotPerformNow);
        Assert.Equal((byte)3, (byte)SubsystemCommandAcknowledgement.InvalidParameter);
        Assert.Equal((byte)4, (byte)SubsystemCommandAcknowledgement.CompletionLater);
        Assert.Equal((byte)5, (byte)SubsystemCommandAcknowledgement.AlreadyInDesiredCondition);
        Assert.Equal((byte)6, (byte)SubsystemCommandAcknowledgement.NoSuchSubsystem);
        Assert.Equal((byte)7, (byte)SubsystemCommandAcknowledgement.Other);

        Assert.Equal(hcack.Codes.Count, Enum.GetValues<SubsystemCommandAcknowledgement>().Length);
    }

    [Fact]
    public void The_sixth_code_is_named_for_this_stream_and_numbered_for_the_item()
    {
        // HCACK 6 is "no such object exists"; this package calls it NoSuchSubsystem because in
        // stream 18 the object is a subsystem. The name is the stream's and the number is the
        // item's, which is the only part that has to agree - and asserting the number is what
        // lets the name differ safely.
        Assert.Equal("No such object exists", Item("HCACK").Codes[6].Meaning);
        Assert.Equal((byte)6, (byte)SubsystemCommandAcknowledgement.NoSuchSubsystem);
    }

    [Fact]
    public void The_parameter_acknowledgement_has_no_zero_because_the_item_has_none()
    {
        var cpack = Item("CPACK");

        // The one exception in the reference's whole family of acknowledgement items, and chapter
        // 45's measure asserts that it is the only one: CPACK's table starts at 1.
        Assert.DoesNotContain("0", cpack.Codes.Select(code => code.Value));
        Assert.Equal("1", cpack.Codes[0].Value);
        Assert.Equal("4-63", cpack.Codes[^1].Value);

        Assert.Equal((byte)1, (byte)SubsystemParameterAcknowledgement.UnknownName);
        Assert.Equal((byte)2, (byte)SubsystemParameterAcknowledgement.IllegalValue);
        Assert.Equal((byte)3, (byte)SubsystemParameterAcknowledgement.IllegalFormat);
        Assert.Equal((byte)4, (byte)SubsystemParameterAcknowledgement.Other);

        Assert.Equal(cpack.Codes.Count, Enum.GetValues<SubsystemParameterAcknowledgement>().Length);
        Assert.DoesNotContain(
            (byte)0,
            Enum.GetValues<SubsystemParameterAcknowledgement>().Select(value => (byte)value));
    }

    [Fact]
    public void Barbatos_SecsGem_ProcessJobs_reads_the_same_item_with_a_zero_and_that_is_deliberate()
    {
        // The one place in this repository where two packages read one item differently, and it is
        // a decision rather than a contradiction - so it is asserted here, where somebody noticing
        // the difference will look.
        //
        // S18F2 and S2F42 carry the parameters that were WRONG: a list of failures needs no code
        // for success, which is why CPACK has no zero and why this enum has no member at zero.
        // S16F18, the process job parameter change, is read by that package as one code per
        // parameter NAMED - successes included - so it needs one, and it says so on the member.
        //
        // Both readings are of one item and neither can be had from the reference alone, because
        // the catalogue records which items a row carries and not how the row is shaped. What
        // would be wrong is either package reading it both ways, or neither saying the other
        // exists.
        var cpack = Item("CPACK");

        Assert.Equal(4, cpack.Codes.Count);
        Assert.Equal<byte[]>(
            [1, 2, 3, 4],
            [.. Enum.GetValues<SubsystemParameterAcknowledgement>().Select(value => (byte)value)]);
    }

    [Fact]
    public void The_alarm_category_is_ALCDs_low_bits()
    {
        var alcd = Item("ALCD");

        // ALCD is a bitfield rather than a list of values: bit 8 says set or cleared and bits 7-1
        // carry the category. So eleven rows describe two fields, and this enum is the second of
        // them - which is why its count cannot be compared with the row count the way the others
        // can, and why the categories are asserted one by one instead.
        Assert.Equal(11, alcd.Codes.Count);
        Assert.Contains(alcd.Codes, code => code.Value.StartsWith("bit 8", StringComparison.Ordinal));

        Assert.Equal((byte)1, (byte)SubsystemAlarmCategory.PersonalSafety);
        Assert.Equal((byte)2, (byte)SubsystemAlarmCategory.EquipmentSafety);
        Assert.Equal((byte)3, (byte)SubsystemAlarmCategory.ParameterControlWarning);
        Assert.Equal((byte)4, (byte)SubsystemAlarmCategory.ParameterControlError);
        Assert.Equal((byte)5, (byte)SubsystemAlarmCategory.IrrecoverableError);
        Assert.Equal((byte)6, (byte)SubsystemAlarmCategory.EquipmentStatusWarning);
        Assert.Equal((byte)7, (byte)SubsystemAlarmCategory.AttentionFlags);
        Assert.Equal((byte)8, (byte)SubsystemAlarmCategory.DataIntegrity);

        // Nine is where the reference leaves the categories to the equipment, and zero is this
        // package's own: an alarm with no category is not something ALCD can express, but it is
        // something a store can hold before anybody sets one.
        Assert.Equal((byte)9, (byte)SubsystemAlarmCategory.Other);
        Assert.Equal((byte)0, (byte)SubsystemAlarmCategory.None);

        foreach (var (value, meaning) in (( byte Value, string Meaning )[])
            [(1, "Personal safety"), (5, "Irrecoverable error"), (8, "Data integrity")])
        {
            Assert.Contains(
                alcd.Codes,
                code => code.Value.EndsWith($"= {value}", StringComparison.Ordinal)
                    && code.Meaning == meaning);
        }
    }
}
