// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Barbatos.SecsGem.Serialization;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Serialization.Json;

/// <summary>
/// Reads JSON into messages.
/// </summary>
/// <remarks>
/// <para>
/// Parsing goes straight through <see cref="Utf8JsonReader"/> - no <c>JsonDocument</c> and no
/// <c>JsonNode</c>, so a 50 MB document does not become a 200 MB object graph on the way to
/// becoming item trees.
/// </para>
/// <para>
/// The document is buffered in pooled memory before parsing, unlike the SML and binary readers,
/// which stream message by message. That is JSON's fault rather than a shortcut: a top-level
/// array has no framing a reader can resynchronise on, so a streaming parser would have to carry
/// the whole reader state across every suspension point. Set
/// <see cref="SecsReadOptions.MaxDocumentLength"/> when the source is untrusted.
/// </para>
/// </remarks>
public sealed class JsonFormatReader : ISecsFormatReader
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => JsonFormat.Descriptor;

    /// <inheritdoc/>
    public async IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= SecsReadOptions.Default;

        using var document = await ReadToEndAsync(input, options, cancellationToken).ConfigureAwait(false);

        // A non-async local function, so no span-typed local has to survive a yield.
        var messages = ParseBuffer(document, options);

        foreach (var message in messages)
        {
            yield return message;
        }

        static List<SecsMessage> ParseBuffer(ArrayPoolBufferWriter<byte> buffer, SecsReadOptions options)
            => Parse(buffer.WrittenSpan, options);
    }

    /// <summary>Parses a complete JSON document.</summary>
    /// <param name="utf8Json">The document, as UTF-8 bytes. A byte-order mark is tolerated.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    /// <exception cref="SecsFormatException">The document is not valid for this format.</exception>
    public static List<SecsMessage> Parse(ReadOnlySpan<byte> utf8Json, SecsReadOptions? options = null)
    {
        options ??= SecsReadOptions.Default;

        if (utf8Json is [0xEF, 0xBB, 0xBF, ..])
        {
            utf8Json = utf8Json[3..];
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = !options.Strict,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var messages = new List<SecsMessage>();
        try
        {
            if (!reader.Read())
            {
                return messages;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        messages.Add(ReadMessage(ref reader, options));
                    }

                    break;

                case JsonTokenType.StartObject:
                    messages.Add(ReadMessage(ref reader, options));
                    break;

                default:
                    throw new SecsFormatException(
                        SecsFormatIds.Json,
                        $"Expected an object or an array at the top level, found {reader.TokenType}.");
            }

            return messages;
        }
        catch (JsonException ex)
        {
            Dispose(messages);
            throw new SecsFormatException(SecsFormatIds.Json, ex.Message, ex);
        }
        catch
        {
            Dispose(messages);
            throw;
        }

        static void Dispose(List<SecsMessage> messages)
        {
            foreach (var message in messages)
            {
                message.Dispose();
            }
        }
    }

    /// <summary>Parses a complete JSON document held in a string.</summary>
    /// <param name="json">The document.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    public static List<SecsMessage> Parse(string json, SecsReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json), options);
    }

    private static SecsMessage ReadMessage(ref Utf8JsonReader reader, SecsReadOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new SecsFormatException(SecsFormatIds.Json, $"Expected a message object, found {reader.TokenType}.");
        }

        string? name = null;
        byte s = 0;
        byte f = 0;
        var replyExpected = false;
        SecsItem? item = null;

        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new SecsFormatException(SecsFormatIds.Json, $"Expected a property name, found {reader.TokenType}.");
                }

                var property = reader.GetString()!;
                reader.Read();

                switch (property)
                {
                    case JsonFormat.NameProperty:
                        name = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    case JsonFormat.StreamProperty:
                        s = reader.GetByte();
                        break;
                    case JsonFormat.FunctionProperty:
                        f = reader.GetByte();
                        break;
                    case JsonFormat.ReplyExpectedProperty:
                        replyExpected = reader.TokenType == JsonTokenType.True
                            || (reader.TokenType == JsonTokenType.Number && reader.GetInt32() != 0);
                        break;
                    case JsonFormat.ItemProperty:
                        item = reader.TokenType == JsonTokenType.Null ? null : ReadItem(ref reader, options);
                        break;
                    default:
                        if (options.Strict)
                        {
                            throw new SecsFormatException(SecsFormatIds.Json, $"Unknown message property '{property}'.");
                        }

                        reader.Skip();
                        break;
                }
            }

            return new SecsMessage(s, f, replyExpected) { Name = name, Item = item };
        }
        catch
        {
            item?.Dispose();
            throw;
        }
    }

    private static SecsItem ReadItem(ref Utf8JsonReader reader, SecsReadOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new SecsFormatException(SecsFormatIds.Json, $"Expected an item object, found {reader.TokenType}.");
        }

        SecsItem? item = null;
        string? label = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new SecsFormatException(SecsFormatIds.Json, $"Expected a property name, found {reader.TokenType}.");
            }

            var property = reader.GetString()!;

            if (property == JsonFormat.LabelProperty)
            {
                reader.Read();
                label = reader.GetString();
                continue;
            }

            if (!SecsFormatExtensions.TryParse(property, out var format))
            {
                if (options.Strict)
                {
                    item?.Dispose();
                    throw new SecsFormatException(SecsFormatIds.Json, $"'{property}' is not a SECS-II item format.");
                }

                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            item?.Dispose();
            item = format == SecsFormat.List
                ? ReadList(ref reader, options)
                : ReadValue(ref reader, format);
        }

        if (item is null)
        {
            throw new SecsFormatException(SecsFormatIds.Json, "An item object has no format property.");
        }

        if (options.ReadItemLabels && !string.IsNullOrEmpty(label))
        {
            item = item.Named(label);
        }

        return item;
    }

    private static SecsItem ReadList(ref Utf8JsonReader reader, SecsReadOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new SecsFormatException(SecsFormatIds.Json, $"Expected an array of child items, found {reader.TokenType}.");
        }

        var children = new List<SecsItem>();
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                children.Add(ReadItem(ref reader, options));
            }

            return SecsItem.L(children);
        }
        catch
        {
            foreach (var child in children)
            {
                child.Dispose();
            }

            throw;
        }
    }

    private static SecsItem ReadValue(ref Utf8JsonReader reader, SecsFormat format)
    {
        // Text is a plain string; everything else is an array - of numbers, of booleans, or of
        // strings when 64-bit values were quoted to keep them exact.
        if (format.IsText())
        {
            var text = reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty;
            return format == SecsFormat.ASCII ? SecsItem.A(text) : SecsItem.J(text);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            // A whole array written as one string: "1 2 3". Accepted so a hand-edited file works.
            return SecsItemText.Parse(format, reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new SecsFormatException(SecsFormatIds.Json, $"Expected an array of {format} values, found {reader.TokenType}.");
        }

        var builder = new StringBuilder();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    builder.Append('T');
                    break;
                case JsonTokenType.False:
                    builder.Append('F');
                    break;
                case JsonTokenType.String:
                    builder.Append(reader.GetString());
                    break;
                case JsonTokenType.Number:
                    builder.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                    break;
                default:
                    throw new SecsFormatException(SecsFormatIds.Json, $"{reader.TokenType} is not a {format} value.");
            }
        }

        try
        {
            return SecsItemText.Parse(format, builder.ToString());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new SecsFormatException(SecsFormatIds.Json, $"Bad {format} value: {ex.Message}", ex);
        }
    }

    private static async ValueTask<ArrayPoolBufferWriter<byte>> ReadToEndAsync(
        PipeReader input,
        SecsReadOptions options,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayPoolBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var read = result.Buffer;

                if (!read.IsEmpty)
                {
                    if (options.MaxDocumentLength > 0 && buffer.WrittenCount + read.Length > options.MaxDocumentLength)
                    {
                        throw new SecsFormatException(
                            SecsFormatIds.Json,
                            $"The document is longer than the {options.MaxDocumentLength}-byte limit.");
                    }

                    Append(buffer, read);
                }

                input.AdvanceTo(read.End);

                if (result.IsCompleted)
                {
                    return buffer;
                }
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        static void Append(ArrayPoolBufferWriter<byte> buffer, in ReadOnlySequence<byte> source)
        {
            foreach (var segment in source)
            {
                buffer.Write(segment.Span);
            }
        }
    }
}
