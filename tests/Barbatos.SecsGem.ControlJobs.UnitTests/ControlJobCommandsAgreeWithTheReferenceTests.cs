// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// <see cref="ControlJobCommands"/> against <c>CTLJOBCMD</c>, the reference's own table for them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ControlJobCommands"/> already says of two of its members that they are "not one of
/// <c>CTLJOBCMD</c>'s six" - so this package has known about the reference's table all along and
/// nothing asserted the relationship. That is the exact shape of a mistake this repository has
/// already made once: a stale figure survived a chapter because one side of it was prose.
/// </para>
/// <para>
/// The fourth provenance measure is what made the omission visible, by counting the definitions
/// the reference specifies and names from no message. <c>CTLJOBCMD</c> is one of the forty-four
/// such definitions that carry a full table of values, so nothing can put it on a wire and nothing
/// but a test can make the two sides meet.
/// </para>
/// </remarks>
public class ControlJobCommandsAgreeWithTheReferenceTests
{
    private static SecsDataItemInfo ControlJobCommand
    {
        get
        {
            Assert.True(SecsCatalog.TryGetDataItem("CTLJOBCMD", out var item));
            return item!;
        }
    }

    [Fact]
    public void The_reference_defines_the_commands_and_names_them_from_no_message()
    {
        // The premise. E94's commands arrive over S16F27's RCMD as free text, not as CTLJOBCMD -
        // which the reference defines and then wires to nothing - so the agreement below is a
        // vocabulary this package chose to honour rather than one a message carries.
        Assert.True(ControlJobCommand.IsNamedByNoMessage);
        Assert.Equal(SecsFormat.ASCII, ControlJobCommand.Format);
        Assert.Contains("CTLJOBCMD", SecsCatalog.Provenance.DetailedUnusedDefinitions);
    }

    [Fact]
    public void Every_command_the_reference_names_is_one_this_package_accepts()
    {
        var accepted = new[]
        {
            ControlJobCommands.Select,
            ControlJobCommands.Deselect,
            ControlJobCommands.Start,
            ControlJobCommands.Pause,
            ControlJobCommands.Resume,
            ControlJobCommands.Abort,
            ControlJobCommands.Stop,
            ControlJobCommands.Cancel,
        };

        // Six for six. A command in the table that this package did not know would be refused at
        // runtime with "not a command this equipment knows", which is a correct answer to a host
        // and a wrong one about the standard.
        Assert.Equal<string[]>(
            ["START", "STOP", "PAUSE", "RESUME", "CANCEL", "ABORT"],
            [.. ControlJobCommand.Codes.Select(code => code.Value)]);

        Assert.All(
            ControlJobCommand.Codes,
            code => Assert.Contains(code.Value, accepted));
    }

    [Fact]
    public void The_two_extra_commands_are_exactly_the_two_the_doc_comments_claim()
    {
        var reference = ControlJobCommand.Codes.Select(code => code.Value).ToHashSet(StringComparer.Ordinal);

        var extra = new[]
        {
            ControlJobCommands.Select,
            ControlJobCommands.Deselect,
            ControlJobCommands.Start,
            ControlJobCommands.Pause,
            ControlJobCommands.Resume,
            ControlJobCommands.Abort,
            ControlJobCommands.Stop,
            ControlJobCommands.Cancel,
        }
            .Where(command => !reference.Contains(command))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The claim in two <summary> tags, now checkable: SELECT and DESELECT are this package's
        // and not the table's. They are E94 transitions a host drives through S16F27 all the same,
        // so having them is right - what would be wrong is having them silently.
        Assert.Equal<string[]>(["DESELECT", "SELECT"], extra);
    }

    [Fact]
    public void The_commands_are_spelled_the_way_the_reference_spells_them()
    {
        // Upper case, because that is the table's own spelling and the manager compares against it
        // after one case fold. A constant that drifted in case would still work and would stop
        // matching the reference, which is the kind of difference only a test notices.
        Assert.All(
            ControlJobCommand.Codes,
            code => Assert.Equal(code.Value, code.Value.ToUpperInvariant()));

        Assert.Equal("START", ControlJobCommands.Start);
        Assert.Equal("STOP", ControlJobCommands.Stop);
        Assert.Equal("PAUSE", ControlJobCommands.Pause);
        Assert.Equal("RESUME", ControlJobCommands.Resume);
        Assert.Equal("CANCEL", ControlJobCommands.Cancel);
        Assert.Equal("ABORT", ControlJobCommands.Abort);
    }

    [Fact]
    public void Each_of_the_references_six_reaches_a_command_rather_than_the_refusal()
    {
        // The agreement driven rather than compared: every spelling in the table goes through
        // Command and comes back as something other than "not a command this equipment knows".
        // A job in the wrong state may refuse, which is a different refusal and the point - this
        // asserts the vocabulary is understood, not that every command is allowed right now.
        foreach (var code in ControlJobCommand.Codes)
        {
            var manager = new ControlJobManager();

            Assert.True(manager.Create("CJ-1", ["LOT-1"]).IsAccepted);

            var result = manager.Command("CJ-1", code.Value);

            Assert.DoesNotContain(
                "is not a command this equipment knows",
                result.Reason,
                StringComparison.Ordinal);
        }
    }
}
