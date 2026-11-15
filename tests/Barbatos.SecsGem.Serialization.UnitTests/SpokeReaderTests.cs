// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization.UnitTests;

/// <summary>
/// The JSON, XML and INI readers against documents they did not write.
/// </summary>
/// <remarks>
/// The round-trip matrix pairs each reader with its own writer, which agree by construction. These
/// tests are the other case: a document from somewhere else, shaped the way that somewhere else
/// felt like shaping it - and, for XML, shaped maliciously.
/// </remarks>
public sealed class SpokeReaderTests
{
    // ------------------------------------------------------------------ XML, and XXE

    [Fact]
    public async Task Xml_refuses_a_document_type_declaration()
    {
        // The reason to care: a DTD is how XML external entity attacks get in. A reader that
        // resolves one can be made to read /etc/passwd, open a connection, or expand a few hundred
        // bytes into gigabytes of memory. A SECS document has no business having a DTD at all, so
        // the reader prohibits them outright rather than trying to resolve them safely.
        const string Document =
            """
            <?xml version="1.0"?>
            <!DOCTYPE SecsMessages [ <!ENTITY harmless "x"> ]>
            <SecsMessages>
              <SecsMessage s="1" f="3" w="true"><ASCII>ok</ASCII></SecsMessage>
            </SecsMessages>
            """;

        await Assert.ThrowsAnyAsync<Exception>(async () => await ReadAllAsync(SecsFormatIds.Xml, Document));
    }

    [Fact]
    public async Task Xml_refuses_an_external_entity()
    {
        const string Document =
            """
            <?xml version="1.0"?>
            <!DOCTYPE SecsMessages [ <!ENTITY secret SYSTEM "file:///etc/passwd"> ]>
            <SecsMessages>
              <SecsMessage s="1" f="3" w="true"><ASCII>&secret;</ASCII></SecsMessage>
            </SecsMessages>
            """;

        var error = await Record.ExceptionAsync(async () => await ReadAllAsync(SecsFormatIds.Xml, Document));

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Xml_reads_a_document_an_editor_reformatted()
    {
        // No indentation, attributes in another order, single quotes on the attributes: all legal
        // XML and all things a different tool produces.
        const string Document =
            "<SecsMessages><SecsMessage w='true' f='11' s='6' name='EventReportSend'>"
            + "<List><U4>1</U4><U4>7001</U4></List></SecsMessage></SecsMessages>";

        using var message = await ReadOneAsync(SecsFormatIds.Xml, Document);

        Assert.Equal("EventReportSend", message.Name);
        Assert.Equal(6, message.S);
        Assert.Equal(11, message.F);
        Assert.True(message.ReplyExpected);
        Assert.Equal(2, message.Item?.Count);
        Assert.Equal(7001u, message.Item?[1].FirstValueOrDefault<uint>());
    }

    // ------------------------------------------------------------------ JSON

    [Fact]
    public async Task Json_reads_a_single_message_that_is_not_wrapped_in_an_array()
    {
        const string Document =
            """
            { "s": 1, "f": 3, "w": true, "item": { "U4": [7001] } }
            """;

        using var message = await ReadOneAsync(SecsFormatIds.Json, Document);

        Assert.Equal(1, message.S);
        Assert.Equal(3, message.F);
        Assert.Equal(7001u, message.Item?.FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task Json_reads_a_message_with_no_body()
    {
        using var message = await ReadOneAsync(SecsFormatIds.Json, """{ "s": 1, "f": 1, "w": true }""");

        Assert.Equal(1, message.S);
        Assert.Null(message.Item);
    }

    [Fact]
    public async Task Json_requires_a_numeric_item_to_be_an_array_even_for_one_value()
    {
        // A deliberate strictness rather than an oversight: a numeric SECS item is a sequence, and
        // one element is still a sequence of one. Accepting a bare number would make
        // { "U4": 7001 } and { "U4": [7001] } two spellings of one document, which is how a format
        // starts needing a compatibility note.
        var error = await Assert.ThrowsAsync<SecsFormatException>(
            async () => await ReadOneAsync(SecsFormatIds.Json, """{ "s": 1, "f": 3, "item": { "U4": 7001 } }"""));

        Assert.Equal(SecsFormatIds.Json, error.FormatId);
        Assert.Contains("Expected an array of U4 values", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_reads_text_as_a_scalar_because_a_text_item_is_one_value()
    {
        // The other half of the rule above, and the reason the asymmetry is right: an ASCII item's
        // count is characters, not elements, so the natural shape is a string. Writing it as
        // ["B","R","B","T","O","S"] would be absurd.
        using var message = await ReadOneAsync(
            SecsFormatIds.Json,
            """{ "s": 1, "f": 2, "item": { "ASCII": "BRBTOS" } }""");

        Assert.Equal(6, message.Item?.Count);
        Assert.Equal("BRBTOS", message.Item?.GetString());
    }

    [Fact]
    public async Task Json_reads_an_empty_list()
    {
        using var message = await ReadOneAsync(SecsFormatIds.Json, """{ "s": 1, "f": 3, "item": { "List": [] } }""");

        Assert.True(message.Item?.IsList);
        Assert.Equal(0, message.Item?.Count);
    }

    [Fact]
    public async Task Json_fails_clearly_on_a_format_it_does_not_know()
    {
        var error = await Assert.ThrowsAsync<SecsFormatException>(
            async () => await ReadOneAsync(SecsFormatIds.Json, """{ "s": 1, "f": 3, "item": { "Quux": [1] } }"""));

        Assert.Equal(SecsFormatIds.Json, error.FormatId);
    }

    [Fact]
    public async Task Json_reads_several_messages_from_an_array()
    {
        const string Document =
            """
            [
              { "name": "First",  "s": 1, "f": 1, "w": true },
              { "name": "Second", "s": 1, "f": 2, "item": { "ASCII": "BRBTOS" } }
            ]
            """;

        var messages = await ReadAllAsync(SecsFormatIds.Json, Document);

        try
        {
            Assert.Equal(2, messages.Count);
            Assert.Equal(["First", "Second"], messages.Select(message => message.Name).ToArray());
            Assert.Equal("BRBTOS", messages[1].Item?.GetString());
        }
        finally
        {
            messages.ForEach(message => message.Dispose());
        }
    }

    // ------------------------------------------------------------------ INI

    [Fact]
    public async Task Ini_reads_keys_in_whatever_order_they_appear()
    {
        // Path-addressed keys carry their own position, so the file does not have to be sorted -
        // which matters, because a person editing one will not keep it sorted.
        const string Document =
            """
            [1]
            item.1.2 = U4[1] 7001
            function = 11
            item.1 = L[2]
            stream = 6
            item.1.1 = U4[1] 1
            replyExpected = true
            name = EventReportSend
            """;

        using var message = await ReadOneAsync(SecsFormatIds.Ini, Document);

        Assert.Equal("EventReportSend", message.Name);
        Assert.Equal(6, message.S);
        Assert.Equal(11, message.F);
        Assert.Equal(2, message.Item?.Count);
        Assert.Equal(1u, message.Item?[0].FirstValueOrDefault<uint>());
        Assert.Equal(7001u, message.Item?[1].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task Ini_skips_comments_and_blank_lines()
    {
        const string Document =
            """
            ; a semicolon comment
            # and a hash one

            [1]
            stream = 1
            function = 3
            replyExpected = true

            ; the body
            item.1 = A[2] 'ok'
            """;

        using var message = await ReadOneAsync(SecsFormatIds.Ini, Document);

        Assert.Equal(1, message.S);
        Assert.Equal("ok", message.Item?.GetString());
    }

    [Fact]
    public async Task Ini_reads_several_sections_as_several_messages()
    {
        const string Document =
            """
            [1]
            name = First
            stream = 1
            function = 1
            replyExpected = true

            [2]
            name = Second
            stream = 1
            function = 2
            item.1 = A[6] 'BRBTOS'
            """;

        var messages = await ReadAllAsync(SecsFormatIds.Ini, Document);

        try
        {
            Assert.Equal(2, messages.Count);
            Assert.Equal(["First", "Second"], messages.Select(message => message.Name).ToArray());
            Assert.Equal("BRBTOS", messages[1].Item?.GetString());
        }
        finally
        {
            messages.ForEach(message => message.Dispose());
        }
    }

    [Fact]
    public async Task Ini_reads_a_quoted_value_containing_its_own_delimiters()
    {
        const string Document =
            """
            [1]
            stream = 1
            function = 3
            item.1 = A[38] 'key = value ; comment # hash [section]'
            """;

        using var message = await ReadOneAsync(SecsFormatIds.Ini, Document);

        Assert.Equal("key = value ; comment # hash [section]", message.Item?.GetString());
    }

    private static async Task<SecsMessage> ReadOneAsync(string format, string document)
        => Assert.Single(await ReadAllAsync(format, document));

    private static Task<List<SecsMessage>> ReadAllAsync(string format, string document)
        => Formats.Transcoder.ReadTextAsync(format, document, options: null, Formats.Token).AsTask();
}
