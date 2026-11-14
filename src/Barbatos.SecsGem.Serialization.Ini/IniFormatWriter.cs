// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Ini;

/// <summary>
/// Writes messages as path-addressed INI.
/// </summary>
public sealed class IniFormatWriter : ISecsFormatWriter
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => IniFormat.Descriptor;

    /// <inheritdoc/>
    public async ValueTask WriteAsync(
        PipeWriter output,
        SecsMessage message,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);

        Write(output, message, section: 1, options);
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
                new SecsUtf8Writer(output).WriteLine(options.NewLine);
            }

            Write(output, message, ++count, options);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Writes one message as an INI section.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="message">The message.</param>
    /// <param name="section">The section number, normally the message's one-based index.</param>
    /// <param name="options">Formatting options.</param>
    public static void Write(IBufferWriter<byte> output, SecsMessage message, int section = 1, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);
        options ??= SecsWriteOptions.Default;

        var writer = new SecsUtf8Writer(output);

        writer.Write('[');
        writer.WriteValue(section);
        writer.Write(']');
        writer.WriteLine(options.NewLine);

        if (!string.IsNullOrEmpty(message.Name))
        {
            WriteKey(writer, IniFormat.NameKey, options);
            writer.Write(message.Name);
            writer.WriteLine(options.NewLine);
        }

        WriteKey(writer, IniFormat.StreamKey, options);
        writer.WriteValue(message.S);
        writer.WriteLine(options.NewLine);

        WriteKey(writer, IniFormat.FunctionKey, options);
        writer.WriteValue(message.F);
        writer.WriteLine(options.NewLine);

        WriteKey(writer, IniFormat.ReplyExpectedKey, options);
        writer.Write(message.ReplyExpected ? "true" : "false");
        writer.WriteLine(options.NewLine);

        if (message.Item is not null)
        {
            WriteItem(writer, message.Item, IniFormat.RootPath, options);
        }
    }

    private static void WriteItem(SecsUtf8Writer writer, SecsItem item, string path, SecsWriteOptions options)
    {
        WriteKey(writer, IniFormat.ItemPrefix + path, options);

        writer.Write(item.Format.ToSmlToken());
        writer.Write('[');
        writer.WriteValue(item.Count);
        writer.Write(']');

        if (!item.IsList && item.Count > 0)
        {
            writer.Write(' ');
            SecsItemText.WriteValues(writer, item, options);
        }

        writer.WriteLine(options.NewLine);

        if (options.WriteItemLabels && !string.IsNullOrEmpty(item.Label))
        {
            WriteKey(writer, IniFormat.ItemPrefix + path + IniFormat.LabelSuffix, options);
            writer.Write(item.Label);
            writer.WriteLine(options.NewLine);
        }

        if (!item.IsList)
        {
            return;
        }

        for (var i = 0; i < item.Count; i++)
        {
            WriteItem(writer, item[i], $"{path}.{i + 1}", options);
        }
    }

    private static void WriteKey(SecsUtf8Writer writer, string key, SecsWriteOptions options)
    {
        writer.Write(key);
        writer.Write(options.Indent ? " = " : "=");
    }
}
