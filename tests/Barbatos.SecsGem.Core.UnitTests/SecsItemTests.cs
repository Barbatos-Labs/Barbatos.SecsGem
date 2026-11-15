// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Core.UnitTests;

/// <summary>
/// The item tree outside the codec: the shared empty singletons, labels, and equality.
/// </summary>
public sealed class SecsItemTests
{
    [Theory]
    [InlineData(SecsFormat.List)]
    [InlineData(SecsFormat.ASCII)]
    [InlineData(SecsFormat.Binary)]
    [InlineData(SecsFormat.U4)]
    public void An_empty_item_is_a_shared_singleton(SecsFormat format)
    {
        Assert.Same(SecsItem.Empty(format), SecsItem.Empty(format));
    }

    [Fact]
    public void The_factories_hand_back_the_shared_empty_rather_than_allocating()
    {
        Assert.Same(SecsItem.Empty(SecsFormat.List), SecsItem.L());
        Assert.Same(SecsItem.Empty(SecsFormat.ASCII), SecsItem.A(null));
        Assert.Same(SecsItem.Empty(SecsFormat.ASCII), SecsItem.A(string.Empty));
        Assert.Same(SecsItem.Empty(SecsFormat.Binary), SecsItem.B());
    }

    [Theory]
    [InlineData(SecsFormat.List)]
    [InlineData(SecsFormat.ASCII)]
    [InlineData(SecsFormat.Binary)]
    [InlineData(SecsFormat.F8)]
    public void Disposing_a_shared_empty_item_leaves_it_usable(SecsFormat format)
    {
        // A caller cannot tell a singleton from an item of its own: SecsItem.L() and a decoded
        // <L[0]> both hand one back. So disposing the message that happens to contain one must not
        // take it away from the rest of the process.
        var shared = SecsItem.Empty(format);
        var bytes = shared.ToByteArray();

        shared.Dispose();
        shared.Dispose();

        Assert.Same(shared, SecsItem.Empty(format));
        Assert.Equal(0, SecsItem.Empty(format).Count);
        Assert.Equal(bytes, SecsItem.Empty(format).ToByteArray());
    }

    [Fact]
    public void An_empty_ascii_item_encodes_to_two_bytes()
    {
        // Format 0b010000 in the top six bits, one length byte in the bottom two, then a zero
        // length. The shortest legal item there is.
        Assert.Equal([0b0100_0001, 0x00], SecsItem.A(string.Empty).ToByteArray());
    }

    [Fact]
    public void Naming_a_shared_empty_item_gives_a_copy()
    {
        var shared = SecsItem.Empty(SecsFormat.List);

        using var named = shared.Named("ACKC10");

        Assert.NotSame(shared, named);
        Assert.Null(shared.Label);
        Assert.Equal("ACKC10", named.Label);
        Assert.Equal(shared, named);
    }

    [Fact]
    public void A_label_does_not_take_part_in_equality()
    {
        // The label exists so an ini or xml document can round-trip an item's name. It is not on
        // the wire, so two items that differ only by label are the same message.
        using var plain = SecsItem.U4(7001u);
        using var labelled = SecsItem.U4(7001u).Named("CEID");

        Assert.Equal(plain, labelled);
        Assert.Equal(plain.GetHashCode(), labelled.GetHashCode());
    }

    [Fact]
    public void Items_of_the_same_format_and_payload_are_equal()
    {
        using var left = SecsItem.L(SecsItem.A("x"), SecsItem.I4(1, 2, 3));
        using var right = SecsItem.L(SecsItem.A("x"), SecsItem.I4(1, 2, 3));

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact]
    public void Items_of_the_same_payload_but_a_different_format_are_not_equal()
    {
        // U1 and Binary are both one byte per element and identical on the wire except for the
        // format bits, which is exactly why they must not compare equal.
        using var binary = SecsItem.B(1, 2, 3);
        using var unsigned = SecsItem.U1(1, 2, 3);

        Assert.NotEqual(binary, unsigned);
    }

    [Fact]
    public void A_fixed_length_ascii_item_is_padded_to_its_width()
    {
        // GEM is full of fixed-width fields - MDLN is six characters, SOFTREV six - and equipment
        // that gets a short one rejects the message.
        using var padded = SecsItem.A("ABC", length: 6);

        Assert.Equal(6, padded.Count);
        Assert.Equal("ABC   ", padded.GetString());
    }

    [Fact]
    public void A_fixed_length_ascii_item_can_pad_with_something_else()
    {
        using var padded = SecsItem.A("42", length: 5, padding: '0');

        Assert.Equal("42000", padded.GetString());
    }

    [Fact]
    public void A_list_exposes_its_children_without_handing_out_its_array()
    {
        using var list = SecsItem.L(SecsItem.A("a"), SecsItem.A("b"));

        Assert.True(list.IsList);
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].GetString());
        Assert.Equal("b", list[1].GetString());
        Assert.Equal(2, list.Items.Length);
    }

    [Fact]
    public void A_value_item_refuses_to_be_treated_as_a_list()
    {
        using var value = SecsItem.U4(1u);

        Assert.False(value.IsList);
        Assert.Throws<NotSupportedException>(() => value[0]);
        Assert.Throws<NotSupportedException>(() => value.GetString());
    }

    [Fact]
    public void A_single_value_can_be_read_without_a_span()
    {
        using var item = SecsItem.U4(7001u);

        Assert.Equal(7001u, item.FirstValue<uint>());
        Assert.Equal(7001u, item.FirstValueOrDefault<uint>());
    }

    [Fact]
    public void An_empty_item_has_no_first_value_to_read()
    {
        var empty = SecsItem.Empty(SecsFormat.U4);

        Assert.Equal(0u, empty.FirstValueOrDefault<uint>());
        Assert.Equal(42u, empty.FirstValueOrDefault(42u));
    }

    [Fact]
    public void An_item_renders_as_its_format_and_count()
    {
        using var item = SecsItem.U4(1u, 2u, 3u);

        Assert.Equal("<U4 [3]>", item.ToString());
    }
}
