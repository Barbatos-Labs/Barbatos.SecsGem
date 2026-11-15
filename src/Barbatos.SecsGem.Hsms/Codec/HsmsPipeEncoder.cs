// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Writes <see cref="HsmsMessage"/> frames onto an HSMS link.
/// </summary>
/// <remarks>
/// <para>
/// The frame is a 4-byte big-endian length, the 10-byte header, then the item tree - and the
/// length covers the header and the tree but not itself. That ordering is the whole difficulty:
/// the number has to go on the wire before the bytes it counts.
/// </para>
/// <para>
/// So the body is encoded first, into one pooled scratch buffer, and only then are the prefix and
/// header written - straight into the output, in one 14-byte span, with the length already known.
/// Reserving the prefix in the output and back-filling it would be the obvious alternative and is
/// not allowed: <see cref="IBufferWriter{T}"/> only promises a returned span stays valid until the
/// next <c>GetSpan</c> or <c>Advance</c>, and a <see cref="PipeWriter"/> really does move to a
/// fresh segment part way through a large message.
/// </para>
/// <para>
/// A frame with no body - every control message, and a data message such as S1F1 - needs no
/// scratch buffer at all and is written with zero allocation.
/// </para>
/// <para>
/// <strong>Ownership.</strong> Nothing here disposes anything. A message can be encoded twice,
/// which is what a T3 retry and a report fanned out to two hosts both need.
/// </para>
/// </remarks>
public sealed class HsmsPipeEncoder
{
    private const int PrefixAndHeaderSize = HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length;

    private readonly SecsCodecOptions _codec;
    private readonly int _encodeBufferInitialSize;
    private readonly int _maxMessageLength;

    /// <summary>Initialises a new encoder.</summary>
    /// <param name="options">
    /// The connection's settings. The codec options,
    /// <see cref="SecsGemOptions.EncodeBufferInitialSize"/> and
    /// <see cref="SecsGemOptions.MaxMessageLength"/> are read from it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public HsmsPipeEncoder(SecsGemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _codec = options.CreateCodecOptions();
        _encodeBufferInitialSize = options.EncodeBufferInitialSize;
        _maxMessageLength = options.MaxMessageLength;
    }

    /// <summary>Writes one frame and flushes it.</summary>
    /// <param name="output">The link's output.</param>
    /// <param name="message">The frame to send. Not disposed.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The flush result, which reports back pressure and whether the reader has gone away.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="HsmsMessageTooLongException">The encoded frame is longer than <see cref="SecsGemOptions.MaxMessageLength"/>.</exception>
    public ValueTask<FlushResult> WriteAsync(PipeWriter output, HsmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return WriteAsync(output, message.Header, message.Data, cancellationToken);
    }

    /// <summary>Writes one frame from a header and a body it does not take ownership of, and flushes it.</summary>
    /// <param name="output">The link's output.</param>
    /// <param name="header">The header to stamp on the wire.</param>
    /// <param name="data">The body, or <see langword="null"/> for a frame that has none.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The flush result, which reports back pressure and whether the reader has gone away.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> has a body but <paramref name="header"/> is a control message, which cannot carry one.</exception>
    /// <exception cref="HsmsMessageTooLongException">The encoded frame is longer than <see cref="SecsGemOptions.MaxMessageLength"/>.</exception>
    /// <remarks>
    /// This is the overload a transaction manager wants: the same <see cref="SecsMessage"/> can be
    /// sent again under fresh system bytes without being copied or wrapped.
    /// </remarks>
    public ValueTask<FlushResult> WriteAsync(
        PipeWriter output,
        MessageHeader header,
        SecsMessage? data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        Encode(output, header, data);
        return output.FlushAsync(cancellationToken);
    }

    /// <summary>Encodes one frame, without flushing.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="header">The header to stamp on the wire.</param>
    /// <param name="data">The body, or <see langword="null"/> for a frame that has none.</param>
    /// <returns>The number of bytes written, length prefix included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> has a body but <paramref name="header"/> is a control message, which cannot carry one.</exception>
    /// <exception cref="HsmsMessageTooLongException">The encoded frame is longer than <see cref="SecsGemOptions.MaxMessageLength"/>.</exception>
    /// <remarks>
    /// Public because it is the whole frame-building rule in one place: a benchmark, a test and a
    /// capture-file writer all want it without a pipe in the way.
    /// </remarks>
    public int Encode(IBufferWriter<byte> output, MessageHeader header, SecsMessage? data)
    {
        ArgumentNullException.ThrowIfNull(output);

        var isControl = header.MessageType != MessageType.DataMessage;
        if (isControl && data?.Item is not null)
        {
            throw new ArgumentException(
                $"{header.MessageType} is a control message and cannot carry a body, but one was supplied.",
                nameof(data));
        }

        var item = isControl ? null : data?.Item;

        if (item is null)
        {
            // No body: prefix and header go out in one span, nothing is allocated.
            CheckLength(MessageHeader.Length);

            var headerOnly = output.GetSpan(PrefixAndHeaderSize);
            BinaryPrimitives.WriteUInt32BigEndian(headerOnly, MessageHeader.Length);
            header.WriteTo(headerOnly[HsmsPipeDecoder.LengthPrefixSize..]);
            output.Advance(PrefixAndHeaderSize);
            return PrefixAndHeaderSize;
        }

        // The body first, so its length is known before the prefix is written.
        using var body = new ArrayPoolBufferWriter<byte>(_encodeBufferInitialSize);
        item.EncodeTo(body, _codec);

        var payloadLength = MessageHeader.Length + (long)body.WrittenCount;
        CheckLength(payloadLength);

        var span = output.GetSpan(PrefixAndHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)payloadLength);
        header.WriteTo(span[HsmsPipeDecoder.LengthPrefixSize..]);
        output.Advance(PrefixAndHeaderSize);
        output.Write(body.WrittenSpan);

        return PrefixAndHeaderSize + body.WrittenCount;
    }

    private void CheckLength(long payloadLength)
    {
        if (payloadLength > _maxMessageLength)
        {
            throw new HsmsMessageTooLongException(payloadLength, _maxMessageLength);
        }
    }
}
