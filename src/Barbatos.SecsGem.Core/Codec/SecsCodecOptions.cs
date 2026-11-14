// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.SecsGem;

/// <summary>
/// Controls how <see cref="SecsItem"/> values are turned into bytes and back.
/// </summary>
/// <remarks>
/// <para>
/// secs4net exposes these as mutable statics (<c>Item.ASCIIEncoding</c>), which makes the codec's
/// behaviour global, racy and untestable in parallel. Here they are an immutable options object
/// passed to the encode and decode calls, so two connections in one process can speak different
/// text encodings, and a test can pin its own without touching anyone else's.
/// </para>
/// </remarks>
public sealed class SecsCodecOptions
{
    /// <summary>The default options: ASCII text, UTF-8 for JIS-8, pooling above 1 KiB.</summary>
    public static SecsCodecOptions Default { get; } = new();

    /// <summary>
    /// Gets the encoding used for <see cref="SecsFormat.ASCII"/> items. Defaults to <see cref="Encoding.ASCII"/>.
    /// </summary>
    public Encoding AsciiEncoding { get; init; } = Encoding.ASCII;

    /// <summary>
    /// Gets the encoding used for <see cref="SecsFormat.JIS8"/> items. Defaults to <see cref="Encoding.UTF8"/>.
    /// </summary>
    /// <remarks>
    /// True JIS-8 is Shift-JIS; register <c>System.Text.Encoding.CodePages</c> and set this to
    /// code page 932 when talking to equipment that means it literally. UTF-8 is the pragmatic
    /// default because most modern equipment puts UTF-8 in a J item.
    /// </remarks>
    public Encoding Jis8Encoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Gets the item payload size, in bytes, above which decoding rents from <see cref="ArrayPool{T}"/>
    /// instead of allocating. Defaults to 1024.
    /// </summary>
    /// <remarks>
    /// Items above this size are backed by an <see cref="IMemoryOwner{T}"/> and are returned to the
    /// pool by <see cref="SecsItem.Dispose"/>. Keep the value well under 85,000 bytes - the Large
    /// Object Heap threshold - or large payloads will allocate there instead of being pooled.
    /// </remarks>
    public int PooledPayloadThreshold { get; init; } = 1024;

    /// <summary>
    /// Gets the text length, in bytes, up to which decoded strings are interned in a shared pool.
    /// Defaults to 256.
    /// </summary>
    /// <remarks>
    /// SECS traffic repeats the same short strings endlessly - model numbers, recipe ids, alarm
    /// text - so pooling them keeps a long-running connection from allocating a new string per
    /// message. Set to <c>0</c> to disable.
    /// </remarks>
    public int PooledStringThreshold { get; init; } = 256;

    /// <summary>
    /// Gets the largest item payload this codec will decode, in bytes. Defaults to 0xFFFFFF (the
    /// wire format's own 3-byte limit).
    /// </summary>
    /// <remarks>
    /// This is a denial-of-service guard: the length is a number the <em>peer</em> chose, so the
    /// decoder refuses to size a buffer from it beyond this bound. Lower it when a link should
    /// never carry large payloads.
    /// </remarks>
    public int MaxPayloadLength { get; init; } = 0xFF_FF_FF;

    /// <summary>
    /// Gets the initial capacity, in bytes, of the buffer a message is encoded into. Defaults to 4096.
    /// </summary>
    public int EncodeBufferInitialSize { get; init; } = 4096;

    /// <summary>Gets the encoding for <paramref name="format"/>.</summary>
    /// <param name="format">A text format.</param>
    /// <returns>The configured encoding.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a text format.</exception>
    public Encoding GetEncoding(SecsFormat format) => format switch
    {
        SecsFormat.ASCII => AsciiEncoding,
        SecsFormat.JIS8 => Jis8Encoding,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a text format."),
    };
}
