// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Json;

/// <summary>
/// Writes messages as JSON.
/// </summary>
/// <remarks>
/// Built on <see cref="Utf8JsonWriter"/> straight over the output buffer. There is no
/// intermediate <c>JsonDocument</c>, no <c>JsonNode</c> tree and no <see cref="string"/> per
/// value: numbers go from their binary form to UTF-8 bytes in one step.
/// </remarks>
public sealed class JsonFormatWriter : ISecsFormatWriter
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => JsonFormat.Descriptor;

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
        var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = options.Indent });
        WriteMessage(writer, message, options);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
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
        var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = options.Indent });
        var count = 0;

        writer.WriteStartArray();
        await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteMessage(writer, message, options);
            count++;

            // Flush per message: the array stays open, but the bytes written so far go out, so a
            // long capture does not accumulate in memory.
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return count;
    }

    /// <summary>Writes one message object.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="message">The message.</param>
    /// <param name="options">Formatting options.</param>
    public static void WriteMessage(Utf8JsonWriter writer, SecsMessage message, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        options ??= SecsWriteOptions.Default;

        writer.WriteStartObject();

        if (!string.IsNullOrEmpty(message.Name))
        {
            writer.WriteString(JsonFormat.NameProperty, message.Name);
        }

        writer.WriteNumber(JsonFormat.StreamProperty, message.S);
        writer.WriteNumber(JsonFormat.FunctionProperty, message.F);
        writer.WriteBoolean(JsonFormat.ReplyExpectedProperty, message.ReplyExpected);

        if (message.Item is not null)
        {
            writer.WritePropertyName(JsonFormat.ItemProperty);
            WriteItem(writer, message.Item, options);
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes one item object.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="item">The item.</param>
    /// <param name="options">Formatting options.</param>
    public static void WriteItem(Utf8JsonWriter writer, SecsItem item, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(item);
        options ??= SecsWriteOptions.Default;

        writer.WriteStartObject();
        writer.WritePropertyName(item.Format.GetName());

        switch (item.Format)
        {
            case SecsFormat.List:
                writer.WriteStartArray();
                foreach (var child in item.Items)
                {
                    WriteItem(writer, child, options);
                }

                writer.WriteEndArray();
                break;

            case SecsFormat.ASCII:
            case SecsFormat.JIS8:
                writer.WriteStringValue(item.GetString());
                break;

            case SecsFormat.Boolean:
                writer.WriteStartArray();
                foreach (var value in item.GetSpan<bool>())
                {
                    writer.WriteBooleanValue(value);
                }

                writer.WriteEndArray();
                break;

            case SecsFormat.Binary:
            case SecsFormat.U1:
                WriteNumbers(writer, item.GetSpan<byte>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.I1:
                WriteNumbers(writer, item.GetSpan<sbyte>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.I2:
                WriteNumbers(writer, item.GetSpan<short>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.I4:
                WriteNumbers(writer, item.GetSpan<int>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.U2:
                WriteNumbers(writer, item.GetSpan<ushort>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.U4:
                WriteNumbers(writer, item.GetSpan<uint>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.F4:
                WriteNumbers(writer, item.GetSpan<float>(), static (w, v) => w.WriteNumberValue(v));
                break;
            case SecsFormat.F8:
                WriteNumbers(writer, item.GetSpan<double>(), static (w, v) => w.WriteNumberValue(v));
                break;

            // 64-bit integers are the one place JSON cannot be trusted: a reader that parses
            // numbers as doubles rounds anything past 2^53. Quoting keeps them exact.
            case SecsFormat.I8:
                if (options.WriteLargeIntegersAsStrings)
                {
                    WriteNumbers(writer, item.GetSpan<long>(), static (w, v) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    WriteNumbers(writer, item.GetSpan<long>(), static (w, v) => w.WriteNumberValue(v));
                }

                break;
            case SecsFormat.U8:
                if (options.WriteLargeIntegersAsStrings)
                {
                    WriteNumbers(writer, item.GetSpan<ulong>(), static (w, v) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    WriteNumbers(writer, item.GetSpan<ulong>(), static (w, v) => w.WriteNumberValue(v));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item), (int)item.Format, "Not a SECS-II item format.");
        }

        if (options.WriteItemLabels && !string.IsNullOrEmpty(item.Label))
        {
            writer.WriteString(JsonFormat.LabelProperty, item.Label);
        }

        writer.WriteEndObject();
    }

    private static void WriteNumbers<T>(Utf8JsonWriter writer, ReadOnlySpan<T> values, Action<Utf8JsonWriter, T> write)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            write(writer, value);
        }

        writer.WriteEndArray();
    }
}
