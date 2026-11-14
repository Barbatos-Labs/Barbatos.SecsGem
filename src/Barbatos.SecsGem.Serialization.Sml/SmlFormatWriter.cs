// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Sml;

/// <summary>
/// Writes messages as SML.
/// </summary>
public sealed class SmlFormatWriter : ISecsFormatWriter
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => SmlFormat.Descriptor;

    /// <inheritdoc/>
    public async ValueTask WriteAsync(
        PipeWriter output,
        SecsMessage message,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);

        Write(output, message, options ?? SecsWriteOptions.Default);
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

        await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (count > 0)
            {
                // A blank line between messages: SML's own framing, and what makes a multi-message
                // file readable.
                new SecsUtf8Writer(output).WriteLine(options.NewLine);
            }

            Write(output, message, options);
            count++;

            // Flush per message so a long capture streams out instead of growing in memory.
            var flush = await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flush.IsCompleted)
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Writes one message's SML into a buffer, synchronously.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="message">The message.</param>
    /// <param name="options">Formatting options.</param>
    /// <remarks>
    /// Exposed because it is the whole of SML writing: no I/O, no state. The async members are
    /// this plus a flush.
    /// </remarks>
    public static void Write(IBufferWriter<byte> output, SecsMessage message, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);
        options ??= SecsWriteOptions.Default;

        var writer = new SecsUtf8Writer(output);

        if (!string.IsNullOrEmpty(message.Name))
        {
            writer.Write(message.Name);
            writer.Write(": ");
        }

        writer.Write("'S");
        writer.WriteValue(message.S);
        writer.Write('F');
        writer.WriteValue(message.F);
        writer.Write('\'');
        if (message.ReplyExpected)
        {
            writer.Write(" W");
        }

        writer.WriteLine(options.NewLine);

        if (message.Item is not null)
        {
            WriteItem(writer, message.Item, level: 0, options);
        }

        writer.Write(SmlFormat.MessageTerminator);
        writer.WriteLine(options.NewLine);
    }

    private static void WriteItem(SecsUtf8Writer writer, SecsItem item, int level, SecsWriteOptions options)
    {
        var indentSize = options.Indent ? options.IndentSize : 0;
        writer.WriteIndent(level, indentSize);

        writer.Write('<');
        writer.Write(item.Format.ToSmlToken());
        writer.Write(" [");
        writer.WriteValue(item.Count);
        writer.Write(']');

        if (item.IsList)
        {
            writer.WriteLine(options.NewLine);
            foreach (var child in item.Items)
            {
                WriteItem(writer, child, level + 1, options);
            }

            writer.WriteIndent(level, indentSize);
            writer.Write('>');
        }
        else
        {
            if (item.Count > 0)
            {
                writer.Write(' ');
                SecsItemText.WriteValues(writer, item, options);
            }

            writer.Write('>');
        }

        // The label rides in a trailing comment: every other SML parser skips it, and ours reads
        // it back, so a name survives sml -> ini -> sml.
        if (options.WriteItemLabels && !string.IsNullOrEmpty(item.Label))
        {
            writer.Write("   // ");
            writer.Write(item.Label);
        }

        writer.WriteLine(options.NewLine);
    }
}
