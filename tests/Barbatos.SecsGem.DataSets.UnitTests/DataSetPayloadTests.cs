// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// The type that exists because <c>DSDATA</c> has no definition and a <see cref="SecsItem"/> has
/// no clone.
/// </summary>
public sealed class DataSetPayloadTests
{
    [Fact]
    public void A_payload_outlives_the_item_it_was_made_from()
    {
        var item = SecsItem.L(
            SecsItem.A("recipe"),
            SecsItem.B(1, 2, 3));

        var payload = DataSetPayload.From(item);

        // The whole reason this type is here. An item decoded off the wire borrows pooled memory
        // and is released with its message; a store that kept the item would be holding a buffer
        // the next message is about to write into.
        item.Dispose();

        using var again = payload.Decode();

        Assert.NotNull(again);
        Assert.Equal(SecsFormat.List, again.Format);
        Assert.Equal(2, again.Count);
        Assert.Equal("recipe", again.Items[0].GetString());
        Assert.Equal<byte>([1, 2, 3], again.Items[1].GetSpan<byte>().ToArray());
    }

    [Fact]
    public void A_payload_remembers_the_format_it_was_made_from()
    {
        using var item = SecsItem.U4(7);

        Assert.Equal(SecsFormat.U4, DataSetPayload.From(item).Format);
    }

    [Fact]
    public void A_payload_from_bytes_keeps_the_caller_s_buffer()
    {
        using var item = SecsItem.A("already mine");
        var bytes = item.ToByteArray();

        var payload = DataSetPayload.FromEncoded(bytes, SecsFormat.ASCII);

        // No copy: a store that read a data set off disk owns those bytes already, and charging it
        // for ownership it was handed would be this library being tidy at the application's
        // expense.
        Assert.True(payload.Encoded.Span.Overlaps(bytes));
        Assert.Equal(SecsFormat.ASCII, payload.Format);
    }

    [Fact]
    public void A_payload_from_bytes_need_not_say_what_format_they_are()
    {
        using var item = SecsItem.A("x");

        var payload = DataSetPayload.FromEncoded(item.ToByteArray());

        // The format is in those bytes, and reading it would mean decoding them - which is the
        // caller's call and not a property's.
        Assert.Null(payload.Format);
        Assert.False(payload.IsEmpty);
    }

    [Fact]
    public void An_empty_payload_decodes_to_nothing()
    {
        var payload = DataSetPayload.FromEncoded(default);

        Assert.True(payload.IsEmpty);
        Assert.Null(payload.Decode());
        Assert.Null(payload.Count);
    }

    [Fact]
    public void An_item_that_carried_nothing_is_empty_though_its_bytes_are_not()
    {
        using var nothing = SecsItem.L();

        var payload = DataSetPayload.From(nothing);

        // The trap this property exists for: an encoded empty item still has a format byte and a
        // length byte in front of it, so a zero-length list is two bytes and not none. In stream
        // 13 those two bytes are how S13F8 says "I have not got that", because the reply has no
        // acknowledgement code to say it with.
        Assert.False(payload.Encoded.IsEmpty);
        Assert.Equal(2, payload.Encoded.Length);
        Assert.Equal(0, payload.Count);
        Assert.True(payload.IsEmpty);

        // And it is still a real item, so it still decodes.
        using var again = payload.Decode();
        Assert.NotNull(again);
        Assert.Equal(SecsFormat.List, again.Format);
    }

    [Fact]
    public void An_empty_string_and_an_empty_binary_are_empty_too()
    {
        // Not just lists: any item carrying no values is a data set that is not there.
        using var text = SecsItem.A(string.Empty);
        using var binary = SecsItem.B([]);

        Assert.True(DataSetPayload.From(text).IsEmpty);
        Assert.True(DataSetPayload.From(binary).IsEmpty);
    }

    [Fact]
    public void Bytes_with_an_unknown_count_are_not_a_known_absence()
    {
        using var nothing = SecsItem.L();

        var payload = DataSetPayload.FromEncoded(nothing.ToByteArray());

        // No count given, so this says "I do not know" rather than "there is nothing" - and a
        // caller who does know says so.
        Assert.False(payload.IsEmpty);
        Assert.True(DataSetPayload.FromEncoded(nothing.ToByteArray(), SecsFormat.List, 0).IsEmpty);
    }

    [Fact]
    public void A_payload_round_trips_every_format_a_data_set_might_be_in()
    {
        // DSDATA has no defined format, so the only promise worth testing is that this package has
        // no favourite among them.
        SecsItem[] items =
        [
            SecsItem.A("a string"),
            SecsItem.B(0xDE, 0xAD, 0xBE, 0xEF),
            SecsItem.Boolean(true, false),
            SecsItem.I4(-1, 0, 1),
            SecsItem.U8(ulong.MaxValue),
            SecsItem.F8(double.Pi),
            SecsItem.L(SecsItem.A("nested"), SecsItem.L(SecsItem.U1(9))),
            SecsItem.L(),
        ];

        foreach (var item in items)
        {
            var expected = item.ToByteArray();
            var payload = DataSetPayload.From(item);

            using var again = payload.Decode();

            Assert.NotNull(again);
            Assert.Equal(expected, again.ToByteArray());
            Assert.Equal(item.Format, payload.Format);

            item.Dispose();
        }
    }

    [Fact]
    public void A_payload_describes_itself_for_a_log()
    {
        using var item = SecsItem.A("1234");

        Assert.Equal("ASCII[4], 6 encoded bytes", DataSetPayload.From(item).ToString());
        Assert.Equal("unknown, 0 encoded bytes", DataSetPayload.FromEncoded(default).ToString());
    }

    [Fact]
    public void A_payload_needs_an_item()
    {
        Assert.Throws<ArgumentNullException>(() => DataSetPayload.From(null!));
    }
}
