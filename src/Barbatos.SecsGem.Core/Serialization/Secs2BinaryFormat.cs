// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// The native SECS-II binary format, framed the way HSMS frames it: a 4-byte big-endian length,
/// a 10-byte header, then the item tree.
/// </summary>
/// <remarks>
/// This spoke lives in the core assembly because it is not really a spoke at all - it is the hub
/// written down. Having it registered like any other format is what makes
/// <c>capture.secs2 -&gt; capture.sml</c> work with no special case in
/// <see cref="SecsTranscoder"/>, and it gives the round-trip tests a binary leg.
/// </remarks>
public static class Secs2BinaryFormat
{
    /// <summary>Describes the native binary format.</summary>
    /// <remarks>
    /// Note what is missing: no message names, no item labels, no comments. The wire format is
    /// purely positional, so converting SML to binary genuinely loses the names - and
    /// <see cref="SecsTranscoder"/> says so rather than letting it pass unremarked.
    /// </remarks>
    public static SecsFormatDescriptor Descriptor { get; } = new(
        SecsFormatIds.Secs2Binary,
        "SECS-II binary (HSMS framing)",
        [".secs2", ".secs", ".bin"],
        "application/octet-stream",
        SecsFormatCapabilities.MultipleMessages
        | SecsFormatCapabilities.ExactIntegers
        | SecsFormatCapabilities.BinaryPayload);

    /// <summary>The 4-byte length prefix that precedes every framed message.</summary>
    internal const int LengthPrefixSize = 4;
}

/// <summary>
/// Reads HSMS-framed SECS-II binary.
/// </summary>
public sealed class Secs2BinaryFormatReader : ISecsFormatReader
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => Secs2BinaryFormat.Descriptor;

    /// <inheritdoc/>
    public async IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= SecsReadOptions.Default;

        while (true)
        {
            var length = await ReadLengthPrefixAsync(input, cancellationToken).ConfigureAwait(false);
            if (length is null)
            {
                yield break;
            }

            if (length.Value < MessageHeader.Length)
            {
                throw new SecsFormatException(
                    SecsFormatIds.Secs2Binary,
                    $"A framed message claims {length.Value} byte(s); the header alone is {MessageHeader.Length}.");
            }

            if (options.MaxDocumentLength > 0 && length.Value > options.MaxDocumentLength)
            {
                throw new SecsFormatException(
                    SecsFormatIds.Secs2Binary,
                    $"A framed message claims {length.Value} byte(s), past the {options.MaxDocumentLength}-byte limit.");
            }

            using var frame = await ReadExactAsync(input, (int)length.Value, cancellationToken).ConfigureAwait(false);
            yield return DecodeFrame(frame.Memory, options.Codec);
        }
    }

    /// <summary>Turns one complete frame into a message.</summary>
    /// <param name="frame">The header and body, without the length prefix.</param>
    /// <param name="codec">Text encodings.</param>
    /// <returns>The decoded message.</returns>
    /// <remarks>
    /// Nothing in the returned tree points back into <paramref name="frame"/> - the value
    /// decoders always copy into their own storage - so the caller is free to release the frame
    /// the moment this returns.
    /// </remarks>
    private static SecsMessage DecodeFrame(ReadOnlyMemory<byte> frame, SecsCodecOptions codec)
    {
        var header = MessageHeader.Parse(frame.Span);

        if (header.MessageType != MessageType.DataMessage)
        {
            // A control message has no body and no meaningful S/F. Surfacing it with its type as
            // the name keeps a raw capture readable instead of failing on it.
            return new SecsMessage(0, 0, replyExpected: false) { Name = header.MessageType.ToString() };
        }

        var body = frame[MessageHeader.Length..];
        if (body.IsEmpty)
        {
            return new SecsMessage(header.Stream, header.Function, header.ReplyExpected);
        }

        var sequence = new ReadOnlySequence<byte>(body);
        var item = SecsItem.Decode(ref sequence, codec);
        return new SecsMessage(header.Stream, header.Function, header.ReplyExpected) { Item = item };
    }

    private static async ValueTask<uint?> ReadLengthPrefixAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= Secs2BinaryFormat.LengthPrefixSize)
            {
                var length = ReadBigEndianUInt32(buffer);
                input.AdvanceTo(buffer.GetPosition(Secs2BinaryFormat.LengthPrefixSize));
                return length;
            }

            if (result.IsCompleted)
            {
                // A clean end of stream: nothing left at all is success, a partial prefix is not.
                var remaining = buffer.Length;
                input.AdvanceTo(buffer.End);
                return remaining == 0
                    ? null
                    : throw new SecsFormatException(
                        SecsFormatIds.Secs2Binary,
                        $"The stream ends with {remaining} stray byte(s), fewer than a {Secs2BinaryFormat.LengthPrefixSize}-byte length prefix.");
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }

        // Local, non-async, so the stack-allocated window never has to survive an await.
        static uint ReadBigEndianUInt32(in ReadOnlySequence<byte> buffer)
        {
            Span<byte> window = stackalloc byte[Secs2BinaryFormat.LengthPrefixSize];
            buffer.Slice(0, Secs2BinaryFormat.LengthPrefixSize).CopyTo(window);
            return BinaryPrimitives.ReadUInt32BigEndian(window);
        }
    }

    private static async ValueTask<MemoryOwner<byte>> ReadExactAsync(PipeReader input, int length, CancellationToken cancellationToken)
    {
        var owner = MemoryOwner<byte>.Allocate(length);
        try
        {
            var offset = 0;
            while (offset < length)
            {
                var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var take = (int)Math.Min(buffer.Length, length - offset);

                if (take > 0)
                {
                    CopyInto(buffer, take, owner, offset);
                    offset += take;
                    input.AdvanceTo(buffer.GetPosition(take));
                    continue;
                }

                if (result.IsCompleted)
                {
                    throw new SecsFormatException(
                        SecsFormatIds.Secs2Binary,
                        $"The stream ends {length - offset} byte(s) short of a complete message.");
                }

                input.AdvanceTo(buffer.Start, buffer.End);
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        static void CopyInto(in ReadOnlySequence<byte> source, int take, MemoryOwner<byte> destination, int offset)
            => source.Slice(0, take).CopyTo(destination.Span[offset..]);
    }
}

/// <summary>
/// Writes HSMS-framed SECS-II binary.
/// </summary>
/// <remarks>
/// The session id is written as <c>0</c> and the system bytes as the message's index in the
/// document. Neither belongs to a <see cref="SecsMessage"/> - they belong to the connection that
/// sends it - so a file has to invent them, and inventing them deterministically is what keeps a
/// fixture byte-identical between runs.
/// </remarks>
public sealed class Secs2BinaryFormatWriter : ISecsFormatWriter
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => Secs2BinaryFormat.Descriptor;

    /// <inheritdoc/>
    public async ValueTask WriteAsync(
        PipeWriter output,
        SecsMessage message,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(message);

        WriteFrame(output, message, systemBytes: 1, (options ?? SecsWriteOptions.Default).Codec);
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

        var codec = (options ?? SecsWriteOptions.Default).Codec;
        var count = 0;

        await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteFrame(output, message, ++count, codec);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Encodes one message into its complete HSMS frame.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="message">The message.</param>
    /// <param name="systemBytes">The transaction id to stamp into the header.</param>
    /// <param name="codec">Text encodings.</param>
    /// <remarks>
    /// <para>
    /// The frame is built in a pooled, contiguous scratch buffer and then copied out in one go.
    /// Reserving the length prefix in <paramref name="output"/> and back-filling it would be one
    /// copy cheaper, but <see cref="IBufferWriter{T}"/> only promises a returned buffer stays
    /// valid until the next <c>GetSpan</c> or <c>Advance</c> - and a <see cref="PipeWriter"/>
    /// really does move to a new segment mid-message. The zero-copy path is the HSMS encoder,
    /// which owns its buffer and can make that promise.
    /// </para>
    /// </remarks>
    internal static void WriteFrame(IBufferWriter<byte> output, SecsMessage message, int systemBytes, SecsCodecOptions codec)
    {
        var header = new MessageHeader
        {
            SessionId = 0,
            Stream = message.S,
            Function = message.F,
            ReplyExpected = message.ReplyExpected,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = systemBytes,
        };

        using var scratch = new ArrayPoolBufferWriter<byte>(codec.EncodeBufferInitialSize);

        header.WriteTo(scratch.GetSpan(MessageHeader.Length));
        scratch.Advance(MessageHeader.Length);
        message.Item?.EncodeTo(scratch, codec);

        var prefix = output.GetSpan(Secs2BinaryFormat.LengthPrefixSize);
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)scratch.WrittenCount);
        output.Advance(Secs2BinaryFormat.LengthPrefixSize);
        output.Write(scratch.WrittenSpan);
    }
}
