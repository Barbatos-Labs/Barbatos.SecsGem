// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.CodeAnalysis;

namespace Barbatos.SecsGem.SourceGenerators.UnitTests;

/// <summary>
/// BSG001: Order set on some mapped members but not all.
/// </summary>
/// <remarks>
/// Half the tests here are about staying quiet. An analyzer that fires on shapes that are fine
/// teaches people to turn warnings off, and then it is worse than not shipping one - so the cases
/// it must not report are pinned as carefully as the case it must.
/// </remarks>
public sealed class SecsMapperAnalyzerTests
{
    [Fact]
    public async Task Order_on_some_members_but_not_all_is_reported()
    {
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class PartiallyOrdered
            {
                [SecsItem(Order = 0)] public uint First { get; set; }
                public uint Second { get; set; }
                public uint Third { get; set; }
            }
            """);

        // Reported on each member that has no Order, because that is where the fix goes.
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("BSG001", diagnostic.Id));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity));

        var messages = diagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        Assert.Contains(messages, message => message.Contains("'PartiallyOrdered.Second'", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("'PartiallyOrdered.Third'", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("'PartiallyOrdered.First'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Order_on_every_member_is_fine()
    {
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class FullyOrdered
            {
                [SecsItem(Order = 1)] public uint Second { get; set; }
                [SecsItem(Order = 0)] public uint First { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Order_on_no_member_is_fine()
    {
        // Pure convention. Declaration order is exact under both resolvers, which is the point of
        // the design rather than a thing to warn about.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(6, 11)]
            public sealed class Report
            {
                public uint DataId { get; set; }
                public uint EventId { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Other_SecsItem_settings_without_Order_are_fine()
    {
        // [SecsItem] is for far more than ordering. Pinning a format or a width on one member says
        // nothing about where any of them sit.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem;
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class Pinned
            {
                [SecsItem(SecsFormat.U4, Name = "ECID")] public int Identifier { get; set; }
                [SecsItem(Length = 16)] public string Stamp { get; set; } = "";
                public uint Sequence { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_type_with_nothing_to_do_with_SECS_is_never_reported()
    {
        // The guard that keeps this analyzer tolerable in a real solution: without it, every DTO
        // in the build would be a candidate.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            namespace Demo;

            public sealed class Customer
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_ignored_member_is_not_counted()
    {
        // It is not on the wire, so it has no position to be missing.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class WithIgnored
            {
                [SecsItem(Order = 0)] public uint First { get; set; }
                [SecsIgnore] public string Note { get; set; } = "";
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_single_mapped_member_is_never_reported()
    {
        // One member has nothing to be out of order with.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 3)]
            public sealed class Single
            {
                public uint Identifier { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_static_member_is_not_counted()
    {
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class WithStatic
            {
                [SecsItem(Order = 0)] public uint First { get; set; }
                public static string Version { get; } = "0.1.0";
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_record_whose_members_are_partly_ordered_is_reported()
    {
        // A record's constructor order is exact, which is precisely why mixing in an explicit
        // Order is worth a warning: now two rules are competing for the same list.
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed record Mixed([property: SecsItem(Order = 0)] string Name, int Age);
            """);

        var reported = Assert.Single(diagnostics);
        Assert.Equal("BSG001", reported.Id);
        Assert.Contains("'Mixed.Age'", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_diagnostic_says_what_to_do_about_it()
    {
        var diagnostics = await Roslyn.RunAnalyzerAsync(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class PartiallyOrdered
            {
                [SecsItem(Order = 0)] public uint First { get; set; }
                public uint Second { get; set; }
            }
            """);

        var reported = Assert.Single(diagnostics);

        Assert.Contains("on every mapped member or on none", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("Usage", reported.Descriptor.Category);
        Assert.False(string.IsNullOrEmpty(reported.Descriptor.HelpLinkUri));
        Assert.Contains(
            "the item list",
            reported.Descriptor.Description.ToString(),
            StringComparison.Ordinal);
    }
}
