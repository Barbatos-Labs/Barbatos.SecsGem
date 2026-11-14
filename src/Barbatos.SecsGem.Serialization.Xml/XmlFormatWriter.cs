// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Xml;
using Barbatos.SecsGem.Serialization;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Serialization.Xml;

/// <summary>
/// Writes messages as XML.
/// </summary>
public sealed class XmlFormatWriter : ISecsFormatWriter
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => XmlFormat.Descriptor;

    /// <inheritdoc/>
    public async ValueTask WriteAsync(
        PipeWriter output,
        SecsMessage message,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);

        options ??= SecsWriteOptions.Default;

        // leaveOpen: the pipe belongs to the caller, who decides when it is complete.
        var stream = output.AsStream(leaveOpen: true);
        await using (stream.ConfigureAwait(false))
        {
            var writer = XmlWriter.Create(stream, CreateSettings(options));
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteStartDocumentAsync().ConfigureAwait(false);
                await WriteMessageAsync(writer, message, options, cancellationToken).ConfigureAwait(false);
                await writer.WriteEndDocumentAsync().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<int> WriteAllAsync(
        PipeWriter output,
        IAsyncEnumerable<SecsMessage> messages,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(messages);

        options ??= SecsWriteOptions.Default;
        var count = 0;

        var stream = output.AsStream(leaveOpen: true);
        await using (stream.ConfigureAwait(false))
        {
            var writer = XmlWriter.Create(stream, CreateSettings(options));
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteStartDocumentAsync().ConfigureAwait(false);
                await writer.WriteStartElementAsync(prefix: null, XmlFormat.RootElement, ns: null).ConfigureAwait(false);

                await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await WriteMessageAsync(writer, message, options, cancellationToken).ConfigureAwait(false);
                    count++;

                    await writer.FlushAsync().ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndDocumentAsync().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static XmlWriterSettings CreateSettings(SecsWriteOptions options) => new()
    {
        Async = true,
        Indent = options.Indent,
        IndentChars = new string(' ', Math.Max(0, options.IndentSize)),
        NewLineChars = options.NewLine,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        CloseOutput = false,
    };

    private static async ValueTask WriteMessageAsync(
        XmlWriter writer,
        SecsMessage message,
        SecsWriteOptions options,
        CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(prefix: null, XmlFormat.MessageElement, ns: null).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(message.Name))
        {
            await writer.WriteAttributeStringAsync(null, XmlFormat.NameAttribute, null, message.Name).ConfigureAwait(false);
        }

        await writer.WriteAttributeStringAsync(null, XmlFormat.StreamAttribute, null, message.S.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, XmlFormat.FunctionAttribute, null, message.F.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, XmlFormat.ReplyExpectedAttribute, null, message.ReplyExpected ? "true" : "false").ConfigureAwait(false);

        if (message.Item is not null)
        {
            await WriteItemAsync(writer, message.Item, options, cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteItemAsync(
        XmlWriter writer,
        SecsItem item,
        SecsWriteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await writer.WriteStartElementAsync(prefix: null, item.Format.GetName(), ns: null).ConfigureAwait(false);

        if (options.WriteItemLabels && !string.IsNullOrEmpty(item.Label))
        {
            await writer.WriteAttributeStringAsync(null, XmlFormat.LabelAttribute, null, item.Label).ConfigureAwait(false);
        }

        if (item.IsList)
        {
            // Indexed, not foreach over Items: a ReadOnlySpan local cannot survive an await, and
            // copying the span to an array would allocate once per list.
            for (var i = 0; i < item.Count; i++)
            {
                await WriteItemAsync(writer, item[i], options, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (item.Count > 0)
        {
            await writer.WriteStringAsync(FormatValues(item, options)).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Renders an item's values as the text content of its element.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="SecsItemText"/>, the same code SML and INI use, so a value is
    /// spelled identically in all three and survives a trip through any of them. ASCII is the
    /// exception: XML quotes nothing, so the surrounding single quotes are stripped.
    /// </remarks>
    private static string FormatValues(SecsItem item, SecsWriteOptions options)
    {
        if (item.Format.IsText())
        {
            return item.GetString();
        }

        using var buffer = new ArrayPoolBufferWriter<byte>();
        SecsItemText.WriteValues(new SecsUtf8Writer(buffer), item, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
