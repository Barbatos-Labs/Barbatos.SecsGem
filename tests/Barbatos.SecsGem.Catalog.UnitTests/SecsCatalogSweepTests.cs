// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// What the sweep of the forty-four specified-but-unused definitions covered, and what it did not.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SecsCatalogProvenance.DetailedUnusedDefinitions"/> counts the definitions the
/// reference specifies with a full table of code values and names from no message. Two chapters
/// worked through them. <strong>This file is the record of that work</strong>, because a sweep
/// nobody wrote down is a sweep the next reader has to do again - and because a refreshed
/// reference with a forty-fifth should make somebody classify it rather than quietly enlarge a
/// count.
/// </para>
/// <para>
/// The three groups partition the set exactly, and the partition is the assertion. Each name is in
/// one group and every group is listed in full; adding a definition, removing one, or moving one
/// between groups fails this file.
/// </para>
/// <para>
/// <strong>Not in the forty-four, and worth saying so here:</strong> the audit of GEM's thirteen
/// acknowledgement enums against their items' reserved ranges is a different check entirely.
/// <c>ACKC7</c>, <c>ONLACK</c> and the rest are all <em>used</em> by messages, so none of them is
/// an unused definition - what was wrong there was the reader, not the reference. See
/// <c>GemAcknowledgementsAgreeWithTheReferenceTests</c>.
/// </para>
/// </remarks>
public sealed class SecsCatalogSweepTests
{
    /// <summary>
    /// The six whose vocabulary a package here states as well, now checked against it.
    /// </summary>
    /// <remarks>
    /// Each of these is a table the reference defines, names from no message, and that some
    /// package here arrived at independently from its own standard - E40, E94 or E87. Two
    /// independent statements of one thing with nothing connecting them is how a stale figure
    /// survives, so each pair now meets in a test.
    /// </remarks>
    private static readonly string[] Audited =
    [
        "ACCESSMODE",      // E87's load port access mode, against Carriers' PortAccessMode - INVERTED
        "CTLJOBCMD",       // E94's commands, against ControlJobCommands
        "PREACK",          // E40's process job acknowledgement
        "PRMTRLORDER",     // E40's material order, against ProcessJobMaterialOrder
        "PRRECIPEMETHOD",  // E40's recipe method, against ProcessJobRecipeMethod
        "PRSTATE",         // E40's nine states, against ProcessJobState - agrees, reserved slot and all
    ];

    /// <summary>
    /// The four that are the reference's own unused alternative to an item in service here.
    /// </summary>
    /// <remarks>
    /// The sharpest group, and the one worth checking first in a future sweep: for each of these a
    /// package here serves a <em>different</em> item in the same role, and the question was whether
    /// the rows name the one in service. They do, every time - so each of these four is a second,
    /// fuller vocabulary the reference wrote down and then wired to nothing.
    /// </remarks>
    private static readonly (string Unused, string InService, string Role)[] Alternatives =
    [
        ("ACKC15", "RMACK", "the recipe acknowledgement - ACKC15 names four reasons where RMACK has one refusal"),
        ("ACKC7A", "ACKC7", "stream 7's acknowledgement, the formatted half included"),
        ("RMGRNT", "RMACK", "the recipe download grant - RMGRNT names space and authority separately"),
        ("WRACK", "SSACK", "the subsystem data write acknowledgement, S18F8"),
    ];

    /// <summary>
    /// The thirty-four that describe a capability or a message this repository does not implement.
    /// </summary>
    /// <remarks>
    /// Checked and passed over rather than overlooked. Most are two-code acknowledgements for
    /// messages no package here serves; the rest are whole vocabularies for capabilities nothing
    /// here has - a table service, a comparison operator, recipe security codes, the namespace and
    /// change commands stream 15's implemented half does not reach. <c>PRCPREEXECHK</c> is the one
    /// in this group that belongs to a standard served here: E40's pre-execution check, which
    /// <c>Barbatos.SecsGem.ProcessJobs</c> does not model, and which that package's own test
    /// records.
    /// </remarks>
    private static readonly string[] NotImplementedHere =
    [
        "CCEACK", "CEPACK", "COACK", "COMPARISONOPERATOR", "CSAACK", "DATAACK", "MIDAC", "OCEACK",
        "PECEACK", "PRCPREEXECHK", "PSRACK", "PSREACK", "QPRKEACK", "QREACK", "QRXLEACK", "RAC",
        "RCPCMD", "RCPSECCODE", "RESPDESTAT", "RETAINRECIPE_DISABLE", "RMCHGSTAT", "RMCHGTYPE",
        "RMNSCMD", "RPMACK", "RRACK_S20", "RTSRSPSTAT", "SENDRSPSTAT", "SPAACK", "SSAACK",
        "SVCACK", "TBLACK", "TBLCMD", "TRDIR", "VERIFYRSPSTAT",
    ];

    [Fact]
    public void The_three_groups_partition_the_forty_four_exactly()
    {
        var swept = Audited
            .Concat(Alternatives.Select(alternative => alternative.Unused))
            .Concat(NotImplementedHere)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The whole claim in one assertion: every specified-but-unused definition has been looked
        // at and filed. A refreshed reference that adds one fails here, which is the point - the
        // count alone would just go up.
        Assert.Equal<string[]>([.. SecsCatalog.Provenance.DetailedUnusedDefinitions], swept);

        // The three sizes look redundant beside the set comparison above and are not, which is
        // worth a sentence because the obvious tidy-up is to delete them. A name MOVED between
        // groups leaves the union unchanged, so only the sizes notice - and no test can perform a
        // move on its own data, so a control that removed these found nothing while a move applied
        // by hand failed exactly here. They stay for the failure the suite cannot stage.
        Assert.Equal(6, Audited.Length);
        Assert.Equal(4, Alternatives.Length);
        Assert.Equal(34, NotImplementedHere.Length);
    }

    [Fact]
    public void No_name_is_in_two_groups()
    {
        var all = Audited
            .Concat(Alternatives.Select(alternative => alternative.Unused))
            .Concat(NotImplementedHere)
            .ToArray();

        // Because a partition that overlapped would make the count above come out right while
        // leaving something unfiled.
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_swept_name_really_is_defined_unused_and_tabled()
    {
        // The groups are hand-written, so this is what stops a typo becoming a silent omission:
        // a misspelled name would otherwise sit in a list looking audited.
        foreach (var name in Audited.Concat(Alternatives.Select(a => a.Unused)).Concat(NotImplementedHere))
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
            Assert.True(item!.IsNamedByNoMessage, $"{name} is named by a message after all");
            Assert.NotEmpty(item.Codes);
        }
    }

    [Fact]
    public void The_item_in_service_beside_each_alternative_really_is_in_service()
    {
        foreach (var (unused, inService, role) in Alternatives)
        {
            Assert.True(SecsCatalog.TryGetDataItem(inService, out var serving), inService);

            // The pairing is the finding: one of the two is carried by rows and the other is not,
            // and which is which is what a package had to get right.
            Assert.False(serving!.IsNamedByNoMessage, $"{inService} is unused, so {unused} is not its alternative");
            Assert.NotEmpty(role);
        }
    }

    [Fact]
    public void The_one_disagreement_the_sweep_found_is_the_one_it_says_it_is()
    {
        // Six audited, five agreed, one did not - and a reader should be able to find out which
        // from here rather than by opening six test files. ACCESSMODE is numbered 0 automatic and
        // 1 manual; Barbatos.SecsGem.Carriers has Manual = 0. The numbers never cross the link,
        // which that package's own test establishes; this is the signpost to it.
        Assert.True(SecsCatalog.TryGetDataItem("ACCESSMODE", out var accessMode));

        Assert.Equal("Automatic access", Assert.Single(accessMode!.Codes, code => code.Value == "0").Meaning);
        Assert.Contains("ACCESSMODE", Audited);
    }
}
