// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization.UnitTests;

/// <summary>
/// The SML reader against documents a person wrote.
/// </summary>
/// <remarks>
/// SML is the one format in this stack that humans type. Everything else arrives from a writer that
/// agrees with its reader by construction, so the round-trip matrix covers it; SML arrives from an
/// engineer's notes, a vendor's manual or another tool's output, in whatever shape they felt like.
/// That makes the reader's tolerance - and the precision of its complaints - part of the product.
/// </remarks>
public sealed class SmlReaderTests
{
    [Theory]
    [InlineData("S1F3", 1, 3, false)]
    [InlineData("'S1F3'", 1, 3, false)]
    [InlineData("\"S1F3\"", 1, 3, false)]
    [InlineData("S1F3 W", 1, 3, true)]
    [InlineData("'S1F3' W", 1, 3, true)]
    [InlineData("s1f3 w", 1, 3, true)]
    [InlineData("S6F11", 6, 11, false)]
    [InlineData("'S99F127' W", 99, 127, true)]
    [InlineData("  'S1F3'   W  ", 1, 3, true)]
    public async Task A_header_is_read_however_it_is_written(string header, int stream, int function, bool replyExpected)
    {
        using var message = await ReadOneAsync($"{header}\n<A [2] 'ok'>\n.");

        Assert.Equal(stream, message.S);
        Assert.Equal(function, message.F);
        Assert.Equal(replyExpected, message.ReplyExpected);
    }

    [Theory]
    [InlineData("GetStatus")]
    [InlineData("EventReportSend")]
    [InlineData("SelectedEquipmentStatusRequest")]
    [InlineData("Recipe.Download")]
    [InlineData("S1F3")]
    [InlineData("WaferStart")]
    public async Task A_message_name_is_read_whatever_letters_it_contains(string name)
    {
        // Each of these has caught a real split. A name with an S in it fools a scan that looks for
        // the stream marker first; a name with a dot fools one that looks for the terminator; a
        // name that *is* an S/F token fools both. The split is found from the quote instead.
        using var message = await ReadOneAsync($"{name}: 'S1F3' W\n<A [2] 'ok'>\n.");

        Assert.Equal(name, message.Name);
        Assert.Equal(1, message.S);
        Assert.Equal(3, message.F);
        Assert.True(message.ReplyExpected);
    }

    [Fact]
    public async Task A_message_with_no_name_reads_as_having_none()
    {
        using var message = await ReadOneAsync("'S1F3' W\n<A [2] 'ok'>\n.");

        Assert.Null(message.Name);
    }

    [Fact]
    public async Task A_message_with_no_body_reads_as_having_none()
    {
        using var message = await ReadOneAsync("'S1F1' W\n.");

        Assert.Equal(1, message.S);
        Assert.Equal(1, message.F);
        Assert.Null(message.Item);
    }

    [Fact]
    public async Task Whole_line_and_trailing_comments_are_skipped()
    {
        using var message = await ReadOneAsync(
            """
            // the whole line
            'S1F3' W   // and the end of this one
            <L [2]
              // another whole line
              <A [3] 'abc'>
              <U4 [1] 7>
            >
            .
            """);

        Assert.Equal(2, message.Item?.Count);
        Assert.Equal("abc", message.Item?[0].GetString());
    }

    [Fact]
    public async Task A_trailing_comment_becomes_the_item_label()
    {
        using var message = await ReadOneAsync(
            """
            'S6F11' W
            <L [2]
              <U4 [1] 1>   // DATAID
              <U4 [1] 7001>   // CEID
            >   // Body
            .
            """);

        Assert.Equal("Body", message.Item?.Label);
        Assert.Equal("DATAID", message.Item?[0].Label);
        Assert.Equal("CEID", message.Item?[1].Label);
    }

    [Fact]
    public async Task Labels_are_not_read_when_the_caller_says_not_to()
    {
        using var message = await ReadOneAsync(
            "'S6F11' W\n<L [1]\n  <U4 [1] 1>   // DATAID\n>\n.",
            new SecsReadOptions { ReadItemLabels = false });

        Assert.Null(message.Item?[0].Label);
        Assert.Equal(1u, message.Item?[0].FirstValueOrDefault<uint>());
    }

    [Theory]
    [InlineData("'it''s here'", "it's here")]
    [InlineData("\"it's here\"", "it's here")]
    [InlineData("'say \"hello\"'", "say \"hello\"")]
    [InlineData("'a > b'", "a > b")]
    [InlineData("'// not a comment'", "// not a comment")]
    [InlineData("'  padded  '", "  padded  ")]
    [InlineData("''", "")]
    public async Task A_quoted_value_keeps_what_is_inside_the_quotes(string quoted, string expected)
    {
        // The '>' case is the one worth staring at: a value item ends at the closing bracket, so a
        // reader that scans for it without tracking quotes truncates any ASCII item containing one.
        using var message = await ReadOneAsync($"'S1F4'\n<A {quoted}>\n.");

        Assert.Equal(expected, message.Item?.GetString());
    }

    [Fact]
    public async Task A_declared_count_that_disagrees_with_the_values_is_an_error()
    {
        var error = await Assert.ThrowsAsync<SecsFormatException>(
            async () => await ReadOneAsync("'S1F4'\n<U4 [3] 1 2>\n."));

        Assert.Contains("declares [3]", error.Message, StringComparison.Ordinal);
        Assert.Contains("2 element", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_count_may_be_left_out()
    {
        using var message = await ReadOneAsync("'S1F4'\n<U4 1 2 3>\n.");

        Assert.Equal(3, message.Item?.Count);
    }

    [Fact]
    public async Task A_text_item_is_measured_in_bytes_and_so_is_not_cross_checked()
    {
        // Three characters, nine bytes once encoded. The bracket on a text item holds the byte
        // count, so comparing it against Count - which is characters - would reject a document
        // that is perfectly correct.
        using var message = await ReadOneAsync("'S1F4'\n<J [9] 'あいう'>\n.");

        Assert.Equal(3, message.Item?.Count);
        Assert.Equal("あいう", message.Item?.GetString());
    }

    [Fact]
    public async Task Several_messages_in_one_document_are_all_read()
    {
        var messages = await ReadAllAsync(
            """
            First: 'S1F1' W
            .
            Second: 'S1F2'
            <L [2]
              <A [6] 'BRBTOS'>
              <A [5] '0.1.0'>
            >
            .
            Third: 'S6F11' W
            <L [0]>
            .
            """);

        try
        {
            Assert.Equal(3, messages.Count);
            Assert.Equal(["First", "Second", "Third"], messages.Select(message => message.Name).ToArray());
            Assert.Null(messages[0].Item);
            Assert.Equal(2, messages[1].Item?.Count);
            Assert.Equal(0, messages[2].Item?.Count);
        }
        finally
        {
            messages.ForEach(message => message.Dispose());
        }
    }

    [Fact]
    public async Task An_empty_document_reads_as_no_messages()
    {
        var messages = await ReadAllAsync("   \n// nothing but a comment\n\n");

        Assert.Empty(messages);
    }

    [Theory]
    // A document that opens with an item, having forgotten its header, is told which line was read
    // as the header and what was wanted there - which is more use than "expected a header".
    [InlineData("<A [2] 'ok'>\n.", "Expected 'S' in the message header")]
    [InlineData("'X1F3'\n<A [2] 'ok'>\n.", "Expected 'S'")]
    [InlineData("'S1'\n<A [2] 'ok'>\n.", "Expected 'F'")]
    [InlineData("'S1F'\n<A [2] 'ok'>\n.", "Expected a number after 'F'")]
    [InlineData("'S1F3'\n<Q [1] 1>\n.", "is not a SECS-II item format")]
    public async Task A_malformed_document_fails_with_something_an_engineer_can_act_on(string document, string expected)
    {
        var error = await Assert.ThrowsAsync<SecsFormatException>(async () => await ReadOneAsync(document));

        Assert.Equal(SecsFormatIds.Sml, error.FormatId);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_names_the_line_it_happened_on()
    {
        var error = await Assert.ThrowsAsync<SecsFormatException>(async () => await ReadOneAsync(
            """
            'S1F1' W
            .
            'S1F3' W
            .
            'X9F9' W
            .
            """));

        // A document of four hundred messages is not much use if the complaint says only that one
        // of them is wrong.
        Assert.Equal(5, error.LineNumber);
    }

    [Fact]
    public async Task A_byte_order_mark_does_not_become_part_of_the_first_message()
    {
        var withPreamble = "﻿'S1F3' W\n<A [2] 'ok'>\n.";

        using var message = await ReadOneAsync(withPreamble);

        Assert.Equal(1, message.S);
        Assert.Equal("ok", message.Item?.GetString());
    }

    [Fact]
    public async Task Indentation_and_blank_lines_make_no_difference()
    {
        using var dense = await ReadOneAsync("'S6F11' W\n<L [2] <U4 [1] 1> <U4 [1] 7001>>\n.");
        using var sprawling = await ReadOneAsync(
            """
            'S6F11' W

                <L [2]

                        <U4 [1] 1>

                        <U4 [1] 7001>

                >

            .
            """);

        Assert.Equal(dense.Item, sprawling.Item);
    }

    private static async Task<SecsMessage> ReadOneAsync(string document, SecsReadOptions? options = null)
    {
        var messages = await ReadAllAsync(document, options);
        return Assert.Single(messages);
    }

    private static async Task<List<SecsMessage>> ReadAllAsync(string document, SecsReadOptions? options = null)
    {
        var read = await Formats.Transcoder
            .ReadTextAsync(SecsFormatIds.Sml, document, options, Formats.Token)
            .ConfigureAwait(false);

        return read;
    }
}
