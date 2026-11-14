// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Barbatos.SecsGem.Serialization;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Serialization.Sml;

/// <summary>
/// One-line SML conversions.
/// </summary>
/// <remarks>
/// The convenient face of this spoke, for tests, logs and tooling. Everything here materialises
/// a <see cref="string"/>; the streaming path is <see cref="SmlFormatReader"/> and
/// <see cref="SmlFormatWriter"/> over a pipe.
/// </remarks>
public static class SmlConvert
{
    /// <summary>Renders a message as SML.</summary>
    /// <param name="message">The message.</param>
    /// <param name="options">Formatting options.</param>
    /// <returns>The SML text, terminated with <c>.</c>.</returns>
    public static string ToSml(this SecsMessage message, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var buffer = new ArrayPoolBufferWriter<byte>();
        SmlFormatWriter.Write(buffer, message, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Renders a single item as SML, without a message header.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Formatting options.</param>
    /// <returns>The SML text.</returns>
    public static string ToSml(this SecsItem item, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Not disposed on purpose: the wrapper borrows the caller's item, and disposing the
        // message would dispose an item this method does not own.
        var message = new SecsMessage(0, 0, replyExpected: false) { Item = item };
        var sml = message.ToSml(options);

        // Drop the synthetic header line and the terminator: the caller asked for the item.
        var start = sml.IndexOf('\n', StringComparison.Ordinal) + 1;
        var end = sml.LastIndexOf('.');
        return end > start ? sml[start..end].TrimEnd('\r', '\n') : sml[start..];
    }

    /// <summary>Parses SML holding exactly one message.</summary>
    /// <param name="sml">The SML text.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The message. The caller disposes it.</returns>
    /// <exception cref="SecsFormatException">The text is not valid SML, or holds no message, or holds more than one.</exception>
    public static SecsMessage ParseSml(string sml, SecsReadOptions? options = null)
    {
        var messages = SmlFormatReader.Parse(sml, options);

        if (messages.Count == 1)
        {
            return messages[0];
        }

        foreach (var message in messages)
        {
            message.Dispose();
        }

        throw new SecsFormatException(
            SecsFormatIds.Sml,
            messages.Count == 0
                ? "The text holds no message."
                : $"The text holds {messages.Count} messages; use {nameof(ParseSmlMessages)}.");
    }

    /// <summary>Parses SML holding any number of messages.</summary>
    /// <param name="sml">The SML text.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    /// <exception cref="SecsFormatException">The text is not valid SML.</exception>
    public static List<SecsMessage> ParseSmlMessages(string sml, SecsReadOptions? options = null)
        => SmlFormatReader.Parse(sml, options);

    /// <summary>Parses SML holding a single bare item, with no message header.</summary>
    /// <param name="sml">The SML text, for example <c>&lt;U4 [1] 1234&gt;</c>.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The item. The caller disposes it.</returns>
    /// <exception cref="SecsFormatException">The text is not a single valid item.</exception>
    public static SecsItem ParseSmlItem(string sml, SecsReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sml);

        // The wrapper message is deliberately not disposed: its item is the return value, and
        // ownership passes to the caller. The message itself holds nothing else.
        var message = ParseSml($"'S0F0'\r\n{sml}\r\n.\r\n", options);
        return message.Item
            ?? throw new SecsFormatException(SecsFormatIds.Sml, "The text holds no item.");
    }
}
