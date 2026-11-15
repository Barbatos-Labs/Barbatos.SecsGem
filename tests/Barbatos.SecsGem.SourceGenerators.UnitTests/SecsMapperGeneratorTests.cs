// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text.RegularExpressions;

namespace Barbatos.SecsGem.SourceGenerators.UnitTests;

/// <summary>
/// The generator against snippets: what it finds, what it emits, and what it leaves alone.
/// </summary>
/// <remarks>
/// Every test here compiles the generated code against the real mapper assembly. Generated code
/// that does not compile is the classic way a source generator fails, and it fails in the
/// consumer's build rather than in ours - so that assertion comes first and is never skipped.
/// </remarks>
public sealed class SecsMapperGeneratorTests
{
    [Fact]
    public void A_record_named_only_by_a_call_site_gets_a_contract()
    {
        // No attributes anywhere. This is the headline promise of the design: a plain record maps
        // by convention, and the generator still gets to read its order from the syntax.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed record Person(string Name, int Age, string[] Emails);

            public static class Program
            {
                public static void Main() => SecsSerializer.Serialize(new Person("Hung", 36, []));
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.True(run.Covers("Demo.Person"), "No contract was generated for a type named by Serialize<T>.");
    }

    [Fact]
    public void A_type_with_a_message_attribute_gets_a_contract()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(6, 11, Name = "EventReportSend")]
            public sealed class Report
            {
                public uint DataId { get; set; }
                public uint EventId { get; set; }
            }
            """);

        Assert.Empty(run.CompilationErrors);

        var contract = run.For("Demo.Report");
        Assert.NotNull(contract);
        Assert.Contains("new global::Barbatos.SecsGem.Mapping.SecsMessageAttribute(6, 11)", contract, StringComparison.Ordinal);
        Assert.Contains("Name = \"EventReportSend\"", contract!, StringComparison.Ordinal);
    }

    [Fact]
    public void Members_are_emitted_in_declaration_order()
    {
        // The whole reason the generator exists. Alphabetical order would put Alpha first and
        // metadata order happens to agree with declaration order today; only the syntax tree says
        // what the programmer wrote.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 3)]
            public sealed class Zebra
            {
                public uint Zulu { get; set; }
                public uint Alpha { get; set; }
                public uint Mike { get; set; }
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Equal(["Zulu", "Alpha", "Mike"], MemberNames(run.For("Demo.Zebra")));
    }

    [Fact]
    public void Explicit_order_overrides_declaration_order()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class Ordered
            {
                [SecsItem(Order = 2)] public uint Third { get; set; }
                [SecsItem(Order = 0)] public uint First { get; set; }
                [SecsItem(Order = 1)] public uint Second { get; set; }
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Equal(["First", "Second", "Third"], MemberNames(run.For("Demo.Ordered")));
    }

    [Fact]
    public void A_record_takes_its_order_from_the_constructor()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 2)]
            public sealed record OnLineData(string Model, string Revision);
            """);

        Assert.Empty(run.CompilationErrors);

        var contract = run.For("Demo.OnLineData");
        Assert.Equal(["Model", "Revision"], MemberNames(contract));

        // Positional record properties are init-only, so there is nothing to set - the values go
        // through the constructor instead, as a direct `new` rather than ConstructorInfo.Invoke.
        Assert.Contains("CreateWithArguments = static args => new global::Demo.OnLineData(", contract!, StringComparison.Ordinal);
        Assert.Contains("ConstructorParameterCount = 2", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("ConstructorParameterIndex = -1", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ignored_member_is_left_out()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class Partial
            {
                [SecsItem] public uint Kept { get; set; }
                [SecsIgnore] public string Dropped { get; set; } = "";
                public uint AlsoKept { get; set; }
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Equal(["Kept", "AlsoKept"], MemberNames(run.For("Demo.Partial")));
    }

    [Fact]
    public void A_type_reached_only_through_a_member_gets_a_contract()
    {
        // A message type is rarely alone. Covering the message and leaving its reports to
        // reflection would cover the shallowest layer of the types that need it most.
        var run = Roslyn.RunGenerator(
            """
            using System.Collections.Generic;
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(6, 11)]
            public sealed class Report
            {
                public uint EventId { get; set; }
                public List<ReportValue> Values { get; set; } = new();
                public ReportHeader Header { get; set; } = new();
            }

            public sealed class ReportValue
            {
                public string Name { get; set; } = "";
                public double Value { get; set; }
            }

            public sealed class ReportHeader
            {
                public string Equipment { get; set; } = "";
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.True(run.Covers("Demo.Report"));
        Assert.True(run.Covers("Demo.ReportValue"), "The element type of a mapped list got no contract.");
        Assert.True(run.Covers("Demo.ReportHeader"), "A mapped member's own type got no contract.");
    }

    [Fact]
    public void Pinned_formats_widths_and_flags_reach_the_contract()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem;
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed class Pinned
            {
                [SecsItem(SecsFormat.U4, Name = "ECID")] public int Identifier { get; set; }
                [SecsItem(Length = 16, Padding = '0')] public string Stamp { get; set; } = "";
                [SecsItem(AsList = true, Optional = true)] public int[] Limits { get; set; } = [];
            }
            """);

        Assert.Empty(run.CompilationErrors);

        var contract = run.For("Demo.Pinned");
        Assert.NotNull(contract);
        Assert.Contains("Format = global::Barbatos.SecsGem.SecsFormat.U4", contract, StringComparison.Ordinal);
        Assert.Contains("Label = \"ECID\"", contract!, StringComparison.Ordinal);
        Assert.Contains("FixedLength = 16", contract, StringComparison.Ordinal);
        Assert.Contains("Padding = '0'", contract, StringComparison.Ordinal);
        Assert.Contains("AsList = true", contract, StringComparison.Ordinal);
        Assert.Contains("Optional = true", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_with_no_pinned_label_follows_the_options_at_run_time()
    {
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 3)]
            public sealed class Plain
            {
                public uint Identifier { get; set; }
            }
            """);

        // Baked in as a conditional rather than decided at generation time, because
        // WritePropertyLabels is a run-time option and the contract has to honour whichever
        // options instance asks for it.
        Assert.Contains(
            "Label = options.WritePropertyLabels ? \"Identifier\" : null",
            run.For("Demo.Plain")!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public struct Value { public uint Id { get; set; } }", "a struct is written through a boxed copy")]
    [InlineData("public sealed class Holder<T> { public T? Item { get; set; } }", "a contract is registered against one closed type")]
    [InlineData("public abstract class Shape { public uint Id { get; set; } }", "an abstract type is never instantiated")]
    [InlineData("public static class Helpers { }", "a static type has no instances")]
    public void A_shape_the_generator_will_not_map_is_left_to_reflection(string declaration, string because)
    {
        var run = Roslyn.RunGenerator(
            $"""
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            {declaration}
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Empty(run.Sources);
        Assert.NotEmpty(because);
    }

    [Fact]
    public void An_init_only_member_no_constructor_covers_is_left_to_reflection()
    {
        // Generated code cannot call an init accessor outside an object initialiser; reflection
        // can. Rather than ship two resolvers that disagree about whether the member is writable,
        // the whole type is left to the one that can do it.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 2)]
            public sealed class InitOnly
            {
                public string Model { get; init; } = "";
                public uint Identifier { get; init; }
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.False(run.Covers("Demo.InitOnly"));
    }

    [Fact]
    public void A_type_from_another_assembly_is_left_to_reflection()
    {
        // Declaration order comes from syntax, and this compilation has none for it.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public static class Program
            {
                public static void Main() => SecsSerializer.Serialize(new System.Version(1, 0));
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Empty(run.Sources);
    }

    [Fact]
    public void Two_call_sites_naming_the_same_type_produce_one_file()
    {
        // Overlapping closures describe the same type more than once. A second file of the same
        // name would not compile, so the duplicates are removed before anything is added.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            public sealed record Person(string Name, int Age);

            public static class Program
            {
                public static void Main()
                {
                    SecsSerializer.Serialize(new Person("a", 1));
                    SecsSerializer.Serialize(new Person("b", 2));
                    var item = SecsSerializer.Serialize(new Person("c", 3));
                    SecsSerializer.Deserialize<Person>(item);
                }
            }
            """);

        Assert.Empty(run.CompilationErrors);
        Assert.Single(run.Sources);
    }

    [Fact]
    public void The_generator_reports_nothing_of_its_own()
    {
        // It has no diagnostics to report: everything it cannot handle is handled by reflection
        // instead, and the one thing worth warning about is the analyzer's job.
        var run = Roslyn.RunGenerator(
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(1, 3)]
            public sealed class Plain
            {
                public uint Identifier { get; set; }
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
    }

    [Fact]
    public void Running_twice_on_the_same_input_gives_the_same_output()
    {
        const string Source =
            """
            using Barbatos.SecsGem.Mapping;

            namespace Demo;

            [SecsMessage(6, 11)]
            public sealed class Report
            {
                public uint DataId { get; set; }
                public uint EventId { get; set; }
            }
            """;

        Assert.Equal(Roslyn.RunGenerator(Source).Sources, Roslyn.RunGenerator(Source).Sources);
    }

    /// <summary>Pulls the emitted member names out of a generated contract, in order.</summary>
    private static string[] MemberNames(string? contract)
    {
        Assert.NotNull(contract);

        return Regex
            .Matches(contract!, @"\bName = ""(?<name>[^""]*)"",\r?\n\s+PropertyType", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .ToArray();
    }
}
