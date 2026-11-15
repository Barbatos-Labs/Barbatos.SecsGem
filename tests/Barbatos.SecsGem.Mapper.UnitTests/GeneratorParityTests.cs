// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;

namespace Barbatos.SecsGem.Mapper.UnitTests;

/// <summary>
/// The generated contracts against the reflected ones, for the same types.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the source generator exists to pass. Two resolvers that produce different item
/// orders for one type would be the worst bug this design could have: a message that is correct
/// until somebody installs a package, with nothing in either codebase to point at. So every shape
/// is mapped twice - once from the syntax tree, once from reflection metadata - and the two trees
/// have to be identical.
/// </para>
/// <para>
/// Each test first asserts that the generator actually saw the type. Without that, a generator
/// that silently did nothing would make every comparison below compare reflection with itself and
/// pass.
/// </para>
/// </remarks>
public sealed class GeneratorParityTests
{
    private static readonly SecsSerializerOptions Generated = new() { TypeInfoResolver = SecsTypeInfoResolver.Generated };
    private static readonly SecsSerializerOptions Reflected = new() { TypeInfoResolver = SecsTypeInfoResolver.Reflection };

    /// <summary>Every type the generator is expected to have found.</summary>
    public static TheoryData<Type> GeneratedTypes =>
    [
        typeof(Models.Person),
        typeof(Models.Settable),
        typeof(Models.FullyOrdered),
        typeof(Models.WithIgnored),
        typeof(Models.Pinned),
        typeof(Models.Report),
        typeof(Models.ReportValue),
        typeof(Models.ReportHeader),
        typeof(Models.Named),
        typeof(Models.WithEnum),
    ];

    [Theory]
    [MemberData(nameof(GeneratedTypes))]
    public void The_generator_found_the_type(Type type)
    {
        Assert.True(
            SecsTypeInfoResolver.Generated.Contains(type),
            $"The generator emitted no contract for {type.Name}. Every comparison in this class would "
            + "then be reflection against itself, and would pass for the wrong reason.");
    }

    [Fact]
    public void A_record_with_no_attributes_maps_the_same_either_way()
    {
        var person = new Models.Person("Hung", 36, ["a@x.com", "b@x.com"]);

        AssertSameTree(person);
    }

    [Fact]
    public void A_plain_class_maps_in_declaration_order_either_way()
    {
        var value = new Models.Settable { Model = "BRBTOS", Identifier = 7001, Enabled = true };

        using var item = SecsSerializer.Serialize(value, Generated);

        // Declared Model, Identifier, Enabled - and that is the order on the wire, with nothing
        // said about it anywhere.
        Assert.Equal(3, item.Count);
        Assert.Equal("BRBTOS", item[0].GetString());
        Assert.Equal(7001u, item[1].FirstValueOrDefault<uint>());
        Assert.True(item[2].FirstValueOrDefault<bool>());

        AssertSameTree(value);
    }

    [Fact]
    public void Explicit_order_beats_declaration_order_either_way()
    {
        var value = new Models.FullyOrdered { First = "one", Second = "two", Third = "three" };

        using var item = SecsSerializer.Serialize(value, Generated);

        Assert.Equal(["one", "two", "three"], new[] { item[0].GetString(), item[1].GetString(), item[2].GetString() });

        AssertSameTree(value);
    }

    [Fact]
    public void An_ignored_member_is_absent_either_way()
    {
        var value = new Models.WithIgnored { Kept = 1, Dropped = "nowhere", AlsoKept = 2 };

        using var item = SecsSerializer.Serialize(value, Generated);

        Assert.Equal(2, item.Count);
        Assert.Equal(1u, item[0].FirstValueOrDefault<uint>());
        Assert.Equal(2u, item[1].FirstValueOrDefault<uint>());

        AssertSameTree(value);
    }

    [Fact]
    public void Pinned_formats_labels_and_widths_come_out_the_same_either_way()
    {
        var value = new Models.Pinned
        {
            Identifier = 7001,
            Timestamp = "20261115093015",
            Code = "42",
            Limits = [1, 2, 3],
            Note = "a note",
        };

        using var item = SecsSerializer.Serialize(value, Generated);

        Assert.Equal(SecsFormat.U4, item[0].Format);
        Assert.Equal("ECID", item[0].Label);
        Assert.Equal(16, item[1].Count);
        Assert.Equal("20261115093015  ", item[1].GetString());
        Assert.Equal("4200", item[2].GetString());
        Assert.True(item[3].IsList);
        Assert.Equal(3, item[3].Count);

        AssertSameTree(value);
    }

    [Fact]
    public void A_nested_graph_the_generator_found_through_its_members_maps_the_same_either_way()
    {
        var report = new Models.Report
        {
            DataId = 1,
            EventId = 7001,
            Values =
            [
                new Models.ReportValue { Name = "PRESSURE", Value = 1013.25 },
                new Models.ReportValue { Name = "TEMPERATURE", Value = 23.5 },
            ],
            Header = new Models.ReportHeader { Equipment = "BRBTOS", Sequence = 9 },
        };

        // ReportValue and ReportHeader are never named by a call site or an attribute - the
        // generator reached them by walking Report's members, which is what keeps it from covering
        // only the shallowest layer of the types that need it most.
        Assert.True(SecsTypeInfoResolver.Generated.Contains(typeof(Models.ReportValue)));
        Assert.True(SecsTypeInfoResolver.Generated.Contains(typeof(Models.ReportHeader)));

        AssertSameTree(report);
    }

    [Fact]
    public void Base_declared_members_come_first_either_way()
    {
        var value = new Models.Named { Identifier = 7, Name = "chamber" };

        using var item = SecsSerializer.Serialize(value, Generated);

        Assert.Equal(7u, item[0].FirstValueOrDefault<uint>());
        Assert.Equal("chamber", item[1].GetString());

        AssertSameTree(value);
    }

    [Fact]
    public void An_enum_maps_through_its_underlying_type_either_way()
    {
        var value = new Models.WithEnum { State = Models.ControlState.OnlineRemote, Sequence = 3 };

        using var item = SecsSerializer.Serialize(value, Generated);

        Assert.Equal(5, item[0].FirstValueOrDefault<byte>());

        AssertSameTree(value);
    }

    [Fact]
    public void A_message_attribute_survives_into_the_generated_contract()
    {
        var value = new Models.Pinned { Timestamp = "x", Code = "y" };

        using var generated = SecsSerializer.ToMessage(value, Generated);
        using var reflected = SecsSerializer.ToMessage(value, Reflected);

        Assert.Equal(2, generated.S);
        Assert.Equal(41, generated.F);
        Assert.Equal("HostCommandSend", generated.Name);
        Assert.Equal(reflected.S, generated.S);
        Assert.Equal(reflected.F, generated.F);
        Assert.Equal(reflected.Name, generated.Name);
        Assert.Equal(reflected.ReplyExpected, generated.ReplyExpected);
    }

    [Fact]
    public void An_init_only_type_is_left_to_reflection_on_purpose()
    {
        // The generator cannot call an init accessor from outside an object initialiser, while
        // reflection can. Rather than ship two resolvers that disagree about whether such a member
        // is writable, the whole type is left to reflection - and the chained default resolver
        // means nothing about using it changes.
        Assert.False(SecsTypeInfoResolver.Generated.Contains(typeof(Models.InitOnly)));

        using var item = SecsSerializer.Serialize(
            new Models.InitOnly { Model = "BRBTOS", Identifier = 7 },
            SecsSerializerOptions.Default);

        Assert.Equal(2, item.Count);
        Assert.Equal("BRBTOS", item[0].GetString());

        var back = SecsSerializer.Deserialize<Models.InitOnly>(item, SecsSerializerOptions.Default);
        Assert.Equal("BRBTOS", back?.Model);
        Assert.Equal(7u, back?.Identifier);
    }

    [Fact]
    public void The_default_resolver_prefers_the_generated_contract()
    {
        // The chain is generated first, reflection second. A type the generator has seen stops
        // using reflection the moment the package is added, with no call site changing.
        Assert.True(SecsTypeInfoResolver.Generated.Contains(typeof(Models.Person)));

        var person = new Models.Person("Hung", 36, []);

        using var viaDefault = SecsSerializer.Serialize(person, SecsSerializerOptions.Default);
        using var viaGenerated = SecsSerializer.Serialize(person, Generated);

        Assert.Equal(viaGenerated, viaDefault);
    }

    [Fact]
    public void A_generated_contract_round_trips_through_a_constructor()
    {
        var person = new Models.Person("Hung", 36, ["a@x.com"]);

        using var item = SecsSerializer.Serialize(person, Generated);
        var back = SecsSerializer.Deserialize<Models.Person>(item, Generated);

        // A positional record has no usable setter, so this went through CreateWithArguments -
        // a direct `new Person(...)` in generated code rather than ConstructorInfo.Invoke.
        Assert.NotNull(back);
        Assert.Equal(person.Name, back!.Name);
        Assert.Equal(person.Age, back.Age);
        Assert.Equal(person.Emails, back.Emails);
    }

    [Fact]
    public void A_generated_contract_round_trips_through_setters()
    {
        var value = new Models.Settable { Model = "BRBTOS", Identifier = 7001, Enabled = true };

        using var item = SecsSerializer.Serialize(value, Generated);
        var back = SecsSerializer.Deserialize<Models.Settable>(item, Generated);

        Assert.NotNull(back);
        Assert.Equal(value.Model, back!.Model);
        Assert.Equal(value.Identifier, back.Identifier);
        Assert.Equal(value.Enabled, back.Enabled);
    }

    /// <summary>Serialises a value through both resolvers and asserts the trees are identical.</summary>
    private static void AssertSameTree<T>(T value)
    {
        using var generated = SecsSerializer.Serialize(value, Generated);
        using var reflected = SecsSerializer.Serialize(value, Reflected);

        if (!Equals(generated, reflected))
        {
            Assert.Fail(
                $"The two resolvers disagree about {typeof(T).Name}.{Environment.NewLine}"
                + $"  generated:  {generated}{Environment.NewLine}"
                + $"  reflection: {reflected}");
        }

        // Labels are outside item equality, so they take their own comparison - and a label is
        // what every text format writes next to the value.
        Assert.Equal(Labels(generated), Labels(reflected));
    }

    private static List<string?> Labels(SecsItem? root)
    {
        var labels = new List<string?>();
        Walk(root);
        return labels;

        void Walk(SecsItem? item)
        {
            if (item is null)
            {
                return;
            }

            labels.Add(item.Label);

            if (!item.IsList)
            {
                return;
            }

            foreach (var child in item.Items)
            {
                Walk(child);
            }
        }
    }
}
