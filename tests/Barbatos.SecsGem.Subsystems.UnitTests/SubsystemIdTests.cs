// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// The type that exists because <c>SSID</c> has no dictionary entry and the catalogue's own
/// identifier-and-name pairs split five to three on what one looks like.
/// </summary>
public sealed class SubsystemIdTests
{
    [Fact]
    public void A_number_and_a_name_that_read_alike_are_not_the_same_subsystem()
    {
        var numbered = SubsystemId.Numbered(7);
        var named = SubsystemId.Named("7");

        // Both print as 7 through Name, and they are still two subsystems. Nothing in E5 says a
        // machine means them as one, and a store that conflated them would invent the equivalence
        // - the mistake the carrier package made when Enum.TryParse read "2" as whichever state
        // happened to be 2.
        Assert.Equal("7", numbered.Name);
        Assert.Equal("7", named.Name);
        Assert.NotEqual(numbered, named);
        Assert.True(numbered != named);
        Assert.NotEqual(numbered.GetHashCode(), named.GetHashCode());
    }

    [Fact]
    public void A_number_knows_it_is_one_and_a_name_knows_it_is_not()
    {
        Assert.True(SubsystemId.Numbered(3).IsNumber);
        Assert.Equal(3u, SubsystemId.Numbered(3).Number);

        Assert.False(SubsystemId.Named("CHAMBER-A").IsNumber);
        Assert.Equal("CHAMBER-A", SubsystemId.Named("CHAMBER-A").Name);

        // A name carries no number, and asking for one gets zero rather than an exception: zero is
        // not a number this identifier has, and a log that prints it is no worse off.
        Assert.Equal(0u, SubsystemId.Named("CHAMBER-A").Number);
    }

    [Fact]
    public void The_default_is_the_empty_name_and_not_the_number_zero()
    {
        // Which matters because a struct can arrive default from anywhere. A default that was the
        // number zero would make an unset identifier address subsystem 0, and some machine
        // somewhere numbers its subsystems from zero.
        Assert.False(default(SubsystemId).IsNumber);
        Assert.Equal(string.Empty, default(SubsystemId).Name);
        Assert.Equal(SubsystemId.Named(string.Empty), default);
    }

    [Fact]
    public void A_null_name_is_the_empty_name()
    {
        Assert.Equal(SubsystemId.Named(string.Empty), SubsystemId.Named(null));
    }

    [Fact]
    public void Two_of_a_kind_with_the_same_value_are_equal_and_hash_alike()
    {
        Assert.Equal(SubsystemId.Numbered(9), SubsystemId.Numbered(9));
        Assert.Equal(SubsystemId.Numbered(9).GetHashCode(), SubsystemId.Numbered(9).GetHashCode());

        Assert.Equal(SubsystemId.Named("PUMP"), SubsystemId.Named("PUMP"));
        Assert.Equal(SubsystemId.Named("PUMP").GetHashCode(), SubsystemId.Named("PUMP").GetHashCode());

        // Ordinal, because a fab that calls one subsystem PUMP and another pump has two.
        Assert.NotEqual(SubsystemId.Named("PUMP"), SubsystemId.Named("pump"));
    }

    [Fact]
    public void It_says_which_kind_it_is_in_a_log()
    {
        // The one place the two readings have to be told apart by eye, so the number gets a marker
        // and the name does not.
        Assert.Equal("#7", SubsystemId.Numbered(7).ToString());
        Assert.Equal("CHAMBER-A", SubsystemId.Named("CHAMBER-A").ToString());
    }

    [Fact]
    public void It_works_as_a_dictionary_key_in_both_readings()
    {
        var map = new Dictionary<SubsystemId, string>
        {
            [SubsystemId.Numbered(1)] = "numbered one",
            [SubsystemId.Named("1")] = "named one",
        };

        // Two entries, not one: the whole point of the strict equality is that a store cannot lose
        // one subsystem behind another.
        Assert.Equal(2, map.Count);
        Assert.Equal("numbered one", map[SubsystemId.Numbered(1)]);
        Assert.Equal("named one", map[SubsystemId.Named("1")]);
    }

    [Fact]
    public void It_compares_equal_to_itself_as_an_object_and_unequal_to_anything_else()
    {
        object boxed = SubsystemId.Numbered(4);

        Assert.True(boxed.Equals(SubsystemId.Numbered(4)));
        Assert.False(boxed.Equals("4"));
        Assert.False(boxed.Equals(null));
    }
}
