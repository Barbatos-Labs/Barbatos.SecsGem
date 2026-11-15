// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Gem;

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The catalogue: that it loads, that it is internally consistent, and that it does not contradict
/// the stack it ships beside.
/// </summary>
/// <remarks>
/// The counts are pinned on purpose. This is third-party reference data, and the point of pinning
/// is that refreshing it has to be a decision somebody makes rather than a diff nobody reads.
/// </remarks>
public class SecsCatalogTests
{
    // ----------------------------------------------------------------- loading

    [Fact]
    public void The_catalogue_loads_everything_the_reference_documents()
    {
        Assert.Equal(20, SecsCatalog.Streams.Count);
        Assert.Equal(524, SecsCatalog.Messages.Count);
        Assert.Equal(375, SecsCatalog.DataItems.Count);
    }

    [Fact]
    public void Stream_eleven_is_absent_because_E5_does_not_define_one()
    {
        Assert.False(SecsCatalog.TryGetStream(11, out _));
        Assert.DoesNotContain(SecsCatalog.Streams, s => s.Number == 11);

        // The ones around it are there, so this is a gap in the standard and not in the parser.
        Assert.True(SecsCatalog.TryGetStream(10, out _));
        Assert.True(SecsCatalog.TryGetStream(12, out _));
    }

    [Fact]
    public void A_stream_carries_its_own_messages_in_function_order()
    {
        Assert.True(SecsCatalog.TryGetStream(6, out var six));

        Assert.Equal("Data Collection", six!.Name);
        Assert.All(six.Messages, m => Assert.Equal(6, m.Stream));

        var functions = six.Messages.Select(m => m.Function).ToArray();
        Assert.Equal(functions.OrderBy(f => f), functions);
    }

    [Fact]
    public void Every_message_belongs_to_a_stream_the_catalogue_knows()
    {
        foreach (var message in SecsCatalog.Messages)
        {
            Assert.True(
                SecsCatalog.TryGetStream(message.Stream, out _),
                $"S{message.Stream} is named by {message} but is not a stream.");
        }
    }

    // ----------------------------------------------------------------- lookup

    [Fact]
    public void A_message_is_found_by_its_stream_and_function()
    {
        Assert.True(SecsCatalog.TryGetMessage(1, 1, out var areYouThere));

        Assert.Equal("Are You There Request", areYouThere!.Name);
        Assert.True(areYouThere.IsPrimary);
        Assert.Equal(SecsMessageDirection.Either, areYouThere.Direction);
        Assert.Equal<byte?>(2, areYouThere.ReplyFunction);
        Assert.Empty(areYouThere.DataItems);
    }

    [Fact]
    public void A_pair_nobody_defined_is_simply_not_there()
    {
        Assert.False(SecsCatalog.TryGetMessage(99, 99, out var missing));
        Assert.Null(missing);
        Assert.Null(SecsCatalog.NameOf(99, 99));

        // Describe still answers, because a log line should not need a null check.
        Assert.Equal("S99F99", SecsCatalog.Describe(99, 99));
    }

    [Fact]
    public void Describe_turns_a_designator_into_something_readable()
    {
        Assert.Equal("S6F11 Event Report Send", SecsCatalog.Describe(6, 11));
        Assert.Equal("Event Report Send", SecsCatalog.NameOf(6, 11));
    }

    [Fact]
    public void A_message_describes_itself_including_the_W_bit()
    {
        using var primary = new SecsMessage(6, 11, replyExpected: true);
        using var secondary = new SecsMessage(6, 12, replyExpected: false);
        using var unknown = new SecsMessage(99, 1, replyExpected: false);

        Assert.Equal("S6F11 W Event Report Send", primary.Describe());
        Assert.Equal("S6F12 Event Report Acknowledge", secondary.Describe());
        Assert.Equal("S99F1", unknown.Describe());

        Assert.Equal("Event Report Send", primary.Catalogued()!.Name);
        Assert.Null(unknown.Catalogued());
    }

    // ----------------------------------------------------------------- data items

    [Fact]
    public void A_coded_data_item_carries_its_standard_values()
    {
        Assert.True(SecsCatalog.TryGetDataItem("COMMACK", out var commack));

        Assert.True(commack!.IsCoded);
        Assert.Equal(SecsFormat.Binary, commack.Format);
        Assert.Equal("1", commack.Length);
        Assert.Equal("Accepted", commack.MeaningOf("0"));
        Assert.NotNull(commack.MeaningOf("1"));
        Assert.Null(commack.MeaningOf("2"));
    }

    [Fact]
    public void An_item_without_a_documented_length_says_so_rather_than_guessing()
    {
        Assert.True(SecsCatalog.TryGetDataItem("SVID", out var svid));

        Assert.Equal(SecsFormat.U4, svid!.Format);
        Assert.Equal(string.Empty, svid.Length);
        Assert.False(svid.IsCoded);
    }

    [Fact]
    public void Every_data_item_is_typed_and_the_typing_agrees_with_Core()
    {
        // This replaces a test that was skipped for one release with an open question on it. The
        // question was wrong rather than hard: I had assumed some items would carry a token that
        // is not an E5 format - a choice of several, or a note - and asserted that at least one
        // did. None does. All 375 use one of eleven tokens (A, B, BOOLEAN, L, I2, I4, F4, F8, U1,
        // U2, U4), so the untyped set was empty and the assertion that it was not was the line
        // failing all along.
        Assert.All(SecsCatalog.DataItems, item =>
        {
            Assert.NotEqual(string.Empty, item.FormatToken);
            Assert.True(
                SecsFormatExtensions.TryParse(item.FormatToken, out var format),
                $"{item.Name} carries the token '{item.FormatToken}', which Core cannot parse.");

            Assert.Equal(format, item.Format);
        });

        Assert.DoesNotContain(SecsCatalog.DataItems, i => i.Format is null);

        // Length is the field that really is absent sometimes - 58 of the 375 - which is why that
        // one is an empty string rather than a number and this one never is.
        Assert.Equal(58, SecsCatalog.DataItems.Count(i => i.Length.Length == 0));
    }

    [Fact]
    public void Nothing_in_the_catalogue_loses_its_name()
    {
        Assert.All(SecsCatalog.Messages, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.All(SecsCatalog.Streams, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.All(SecsCatalog.DataItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public void The_item_dictionary_is_looked_up_the_way_E5_writes_the_names()
    {
        Assert.True(SecsCatalog.TryGetDataItem("HCACK", out _));

        // Ordinal, case-sensitive. E5 writes data item names in upper case and a lookup that
        // folded case would make ALID and alid the same name.
        Assert.False(SecsCatalog.TryGetDataItem("hcack", out _));
    }

    // ----------------------------------------------------------------- internal consistency

    [Fact]
    public void An_odd_function_is_always_a_primary_and_an_even_one_never_is()
    {
        foreach (var message in SecsCatalog.Messages)
        {
            Assert.Equal(message.Function % 2 == 1, message.IsPrimary);
        }
    }

    [Fact]
    public void A_primary_is_answered_by_the_next_function_up_and_that_message_exists()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => m.IsPrimary && m.ReplyFunction is not null))
        {
            Assert.Equal((byte)(message.Function + 1), message.ReplyFunction);

            Assert.True(
                SecsCatalog.TryGetMessage(message.Stream, message.ReplyFunction!.Value, out var reply),
                $"{message} names a reply that is not in the catalogue.");

            Assert.False(reply!.IsPrimary);
        }
    }

    [Fact]
    public void A_secondary_points_back_at_the_primary_it_answers()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => !m.IsPrimary && m.ReplyFunction is not null))
        {
            Assert.Equal((byte)(message.Function - 1), message.ReplyFunction);
        }
    }

    [Fact]
    public void A_Stream_9_report_has_no_reply_because_nothing_answers_it()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => m.Stream == 9))
        {
            Assert.True(message.IsPrimary);
            Assert.Null(message.ReplyFunction);
        }
    }

    // ----------------------------------------------------------------- what the source does not know

    [Fact]
    public void The_entries_the_reference_calls_representative_are_flagged_as_such()
    {
        var representative = SecsCatalog.Messages.Count(m => m.IsRepresentative);

        // 405 of 524. Carried through rather than dropped: a catalogue that presented every entry
        // with equal confidence would be claiming more than it knows.
        Assert.Equal(405, representative);

        // The streams this repository implements against are the well-covered ones.
        Assert.All(
            SecsCatalog.Messages.Where(m => m.Stream == 9),
            m => Assert.False(m.IsRepresentative));
    }

    [Fact]
    public void Some_names_appear_in_bodies_without_a_dictionary_entry_and_that_is_the_sources_gap()
    {
        var named = SecsCatalog.Messages.SelectMany(m => m.DataItems).ToHashSet(StringComparer.Ordinal);
        var defined = SecsCatalog.DataItems.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var undefined = named.Except(defined, StringComparer.Ordinal).ToArray();

        // 74 of them, ACKC2 and RIACK among them. Pinned so a refresh of the data has to be looked
        // at: the number going up means the reference grew bodies it did not document, and going
        // down means it filled some in.
        Assert.Equal(74, undefined.Length);
    }

    // ----------------------------------------------------------------- agreement with the implementation

    [Fact]
    public void Every_message_the_GEM_layer_answers_is_catalogued_as_a_primary_with_a_reply()
    {
        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            Assert.True(
                SecsCatalog.TryGetMessage(stream, function, out var info),
                $"The GEM layer answers S{stream}F{function}, which is not in the catalogue.");

            Assert.True(info!.IsPrimary, $"{info} is handled as a primary but catalogued as a secondary.");
            Assert.Equal((byte)(function + 1), info.ReplyFunction);
        }
    }

    [Theory]
    [InlineData(1, 2, SecsMessageDirection.Either)]
    [InlineData(1, 4, SecsMessageDirection.EquipmentToHost)]
    [InlineData(1, 13, SecsMessageDirection.Either)]
    [InlineData(2, 41, SecsMessageDirection.HostToEquipment)]
    [InlineData(5, 1, SecsMessageDirection.EquipmentToHost)]
    [InlineData(5, 3, SecsMessageDirection.HostToEquipment)]
    [InlineData(6, 11, SecsMessageDirection.EquipmentToHost)]
    [InlineData(6, 12, SecsMessageDirection.HostToEquipment)]
    public void The_messages_the_GEM_layer_sends_travel_the_way_it_assumes(
        byte stream,
        byte function,
        SecsMessageDirection expected)
    {
        Assert.True(SecsCatalog.TryGetMessage(stream, function, out var info));
        Assert.Equal(expected, info!.Direction);
    }

    [Fact]
    public void S6F11_is_the_correction_this_catalogue_carries()
    {
        Assert.True(SecsCatalog.TryGetMessage(6, 11, out var send));
        Assert.True(SecsCatalog.TryGetMessage(6, 12, out var acknowledge));

        // The reference has this pair reversed, naming S6F11 an "Event Report Request" travelling
        // H->E. It is the one place it disagreed with code that is tested and running, and the code
        // is right: GEM equipment sends S6F11 and the host answers S6F12 with ACKC6. This test is
        // what stops a refresh of the data putting the error back.
        Assert.Equal("Event Report Send", send!.Name);
        Assert.Equal(SecsMessageDirection.EquipmentToHost, send.Direction);
        Assert.Contains("CEID", send.DataItems);
        Assert.Contains("DATAID", send.DataItems);

        Assert.Equal("Event Report Acknowledge", acknowledge!.Name);
        Assert.Equal(SecsMessageDirection.HostToEquipment, acknowledge.Direction);
        Assert.Equal(["ACKC6"], acknowledge.DataItems);
    }

    [Theory]
    [InlineData(1, 12, new[] { "SVID", "SVNAME", "UNITS" })]
    [InlineData(2, 30, new[] { "ECID", "ECNAME", "ECMIN", "ECMAX", "ECDEF", "UNITS" })]
    [InlineData(5, 6, new[] { "ALCD", "ALID", "ALTX" })]
    [InlineData(2, 41, new[] { "RCMD", "CPNAME", "CPVAL" })]
    public void The_bodies_the_GEM_layer_builds_hold_the_items_the_catalogue_lists(
        byte stream,
        byte function,
        string[] expected)
    {
        Assert.True(SecsCatalog.TryGetMessage(stream, function, out var info));
        Assert.Equal(expected, info!.DataItems);
    }
}
