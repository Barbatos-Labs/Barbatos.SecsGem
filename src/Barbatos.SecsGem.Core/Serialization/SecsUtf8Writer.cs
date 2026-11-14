// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Writes UTF-8 text straight into an <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The text spokes (SML, INI, and the XML and JSON writers' scalar values) all need the same
/// thing: append a few characters or a formatted number to a byte buffer without going through
/// a <see cref="string"/> first. Doing that per value with <c>ToString()</c> is what makes a
/// naive writer allocate once per item.
/// </para>
/// <para>
/// Numbers are formatted through <see cref="IUtf8SpanFormattable"/>, so they go from
/// <see cref="int"/> to UTF-8 bytes with no intermediate string at all, and always with
/// <see cref="CultureInfo.InvariantCulture"/> - a recipe parameter written on a machine with a
/// comma decimal separator has to read back the same everywhere.
/// </para>
/// </remarks>
/// <param name="writer">The destination buffer.</param>
public sealed class SecsUtf8Writer(IBufferWriter<byte> writer)
{
    private const int NumberBufferSize = 64;

    private readonly IBufferWriter<byte> _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <summary>Appends a character.</summary>
    /// <param name="value">The character. Must be ASCII.</param>
    public void Write(char value)
    {
        Debug.Assert(value < 0x80, "Use the span overload for non-ASCII characters.");
        var span = _writer.GetSpan(1);
        span[0] = (byte)value;
        _writer.Advance(1);
    }

    /// <summary>Appends text.</summary>
    /// <param name="value">The text. <see langword="null"/> writes nothing.</param>
    public void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Write(value.AsSpan());
        }
    }

    /// <summary>Appends text.</summary>
    /// <param name="value">The text.</param>
    public void Write(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        var span = _writer.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        var written = Encoding.UTF8.GetBytes(value, span);
        _writer.Advance(written);
    }

    /// <summary>Appends a formattable value - an integer, a floating-point number, a date.</summary>
    /// <typeparam name="T">A type that can format itself straight to UTF-8.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">An optional format string.</param>
    /// <exception cref="FormatException">The value needs more than 512 bytes to format.</exception>
    public void WriteValue<T>(T value, string? format = null)
        where T : IUtf8SpanFormattable
    {
        var span = _writer.GetSpan(NumberBufferSize);
        if (value.TryFormat(span, out var written, format, CultureInfo.InvariantCulture))
        {
            _writer.Advance(written);
            return;
        }

        span = _writer.GetSpan(512);
        if (!value.TryFormat(span, out written, format, CultureInfo.InvariantCulture))
        {
            throw new FormatException($"Formatting a {typeof(T).Name} needed more than 512 bytes.");
        }

        _writer.Advance(written);
    }

    /// <summary>Appends a boolean as <c>T</c> or <c>F</c> - the SML spelling.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => Write(value ? 'T' : 'F');

    /// <summary>Appends a byte as two upper-case hexadecimal digits.</summary>
    /// <param name="value">The byte.</param>
    public void WriteHex(byte value)
    {
        var span = _writer.GetSpan(2);
        span[0] = ToHexDigit(value >> 4);
        span[1] = ToHexDigit(value & 0xF);
        _writer.Advance(2);

        static byte ToHexDigit(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
    }

    /// <summary>Appends a newline.</summary>
    /// <param name="newLine">The newline sequence, normally from <see cref="SecsWriteOptions.NewLine"/>.</param>
    public void WriteLine(string newLine = "\r\n") => Write(newLine);

    /// <summary>Appends <paramref name="level"/> levels of indentation.</summary>
    /// <param name="level">The nesting level.</param>
    /// <param name="size">Spaces per level. <c>0</c> writes nothing.</param>
    public void WriteIndent(int level, int size)
    {
        var total = level * size;
        if (total <= 0)
        {
            return;
        }

        var span = _writer.GetSpan(total)[..total];
        span.Fill((byte)' ');
        _writer.Advance(total);
    }
}

/// <summary>
/// UTF-8 decoding helpers for the format readers.
/// </summary>
public static class SecsUtf8
{
    /// <summary>Decodes a UTF-8 sequence to a string, without materialising the sequence first.</summary>
    /// <param name="source">The bytes.</param>
    /// <returns>The decoded text.</returns>
    public static string GetString(in ReadOnlySequence<byte> source)
    {
        if (source.IsEmpty)
        {
            return string.Empty;
        }

        if (source.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(source.FirstSpan);
        }

        var length = (int)source.Length;
        using var scratch = SpanOwner<byte>.Allocate(length);
        source.CopyTo(scratch.Span);
        return Encoding.UTF8.GetString(scratch.Span);
    }

    /// <summary>Trims ASCII whitespace, then an optional UTF-8 byte-order mark, from the front of a sequence.</summary>
    /// <param name="source">The bytes.</param>
    /// <returns>The trimmed sequence.</returns>
    /// <remarks>
    /// Files exported by Windows SECS tooling routinely start with a BOM; a reader that does not
    /// skip it fails on byte one with a message about an unexpected character nobody can see.
    /// </remarks>
    public static ReadOnlySequence<byte> TrimPreamble(in ReadOnlySequence<byte> source)
    {
        var result = source;
        if (result.Length >= 3)
        {
            Span<byte> first = stackalloc byte[3];
            result.Slice(0, 3).CopyTo(first);
            if (first is [0xEF, 0xBB, 0xBF])
            {
                result = result.Slice(3);
            }
        }

        return result;
    }
}
