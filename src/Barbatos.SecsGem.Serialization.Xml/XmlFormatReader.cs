// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO.Pipelines;
using System.Xml;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Xml;

/// <summary>
/// Reads XML into messages.
/// </summary>
/// <remarks>
/// Streams through <see cref="XmlReader"/>, so a document of any size costs memory for one
/// message. DTD processing is prohibited and the entity resolver is removed - see
/// <see cref="XmlFormat"/> for why that is not optional here.
/// </remarks>
public sealed class XmlFormatReader : ISecsFormatReader
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => XmlFormat.Descriptor;

    /// <inheritdoc/>
    public async IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= SecsReadOptions.Default;

        var stream = input.AsStream(leaveOpen: true);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = XmlReader.Create(stream, CreateSettings());

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (string.Equals(reader.Name, XmlFormat.MessageElement, StringComparison.Ordinal))
                {
                    yield return await ReadMessageAsync(reader, options).ConfigureAwait(false);
                    continue;
                }

                if (options.Strict
                    && !string.Equals(reader.Name, XmlFormat.RootElement, StringComparison.Ordinal))
                {
                    throw new SecsFormatException(
                        SecsFormatIds.Xml,
                        $"Expected <{XmlFormat.RootElement}> or <{XmlFormat.MessageElement}>, found <{reader.Name}>.");
                }
            }
        }
    }

    private static XmlReaderSettings CreateSettings() => new()
    {
        Async = true,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
    };

    private static async ValueTask<SecsMessage> ReadMessageAsync(XmlReader reader, SecsReadOptions options)
    {
        var name = reader.GetAttribute(XmlFormat.NameAttribute);
        var s = ReadByteAttribute(reader, XmlFormat.StreamAttribute);
        var f = ReadByteAttribute(reader, XmlFormat.FunctionAttribute);
        var replyExpected = ReadBooleanAttribute(reader, XmlFormat.ReplyExpectedAttribute);

        if (reader.IsEmptyElement)
        {
            return new SecsMessage(s, f, replyExpected) { Name = name };
        }

        SecsItem? item = null;
        try
        {
            await reader.ReadAsync().ConfigureAwait(false);

            while (true)
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (item is not null && options.Strict)
                    {
                        throw new SecsFormatException(
                            SecsFormatIds.Xml,
                            $"<{XmlFormat.MessageElement}> holds more than one root item.");
                    }

                    item?.Dispose();
                    item = await ReadItemAsync(reader, options).ConfigureAwait(false);
                    continue;
                }

                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
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

    /// <summary>
    /// Reads the item the reader is positioned on, leaving the reader just past its end element.
    /// </summary>
    private static async ValueTask<SecsItem> ReadItemAsync(XmlReader reader, SecsReadOptions options)
    {
        if (!SecsFormatExtensions.TryParse(reader.Name, out var format))
        {
            throw new SecsFormatException(SecsFormatIds.Xml, $"<{reader.Name}> is not a SECS-II item format.");
        }

        var label = options.ReadItemLabels ? reader.GetAttribute(XmlFormat.LabelAttribute) : null;

        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync().ConfigureAwait(false);
            return SecsItem.Empty(format).Named(label);
        }

        if (format != SecsFormat.List)
        {
            // Consumes the element and its end tag in one go.
            var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            try
            {
                return SecsItemText.Parse(format, format.IsText() ? Quote(text) : text).Named(label);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new SecsFormatException(SecsFormatIds.Xml, $"Bad {format} value: {ex.Message}", ex);
            }
        }

        var children = new List<SecsItem>();
        try
        {
            await reader.ReadAsync().ConfigureAwait(false);

            while (true)
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    children.Add(await ReadItemAsync(reader, options).ConfigureAwait(false));
                    continue;
                }

                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch
        {
            foreach (var child in children)
            {
                child.Dispose();
            }

            throw;
        }

        return SecsItem.L(children).Named(label);
    }

    /// <summary>
    /// Wraps element text in the quotes <see cref="SecsItemText"/> expects around a string.
    /// </summary>
    /// <remarks>
    /// XML has no string delimiters: the content between the tags <em>is</em> the value, spaces
    /// and all. Quoting it before parsing is what keeps <c>&lt;ASCII&gt; A &lt;/ASCII&gt;</c>
    /// from losing its spaces, and keeps one value-text implementation serving all three text
    /// formats.
    /// </remarks>
    private static string Quote(string text)
        => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    private static byte ReadByteAttribute(XmlReader reader, string attribute)
    {
        var raw = reader.GetAttribute(attribute);
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }

        return byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SecsFormatException(SecsFormatIds.Xml, $"Attribute {attribute}=\"{raw}\" is not a number from 0 to 255.");
    }

    private static bool ReadBooleanAttribute(XmlReader reader, string attribute)
    {
        var raw = reader.GetAttribute(attribute);
        return !string.IsNullOrEmpty(raw)
            && (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "1", StringComparison.Ordinal)
                || string.Equals(raw, "W", StringComparison.OrdinalIgnoreCase));
    }
}
