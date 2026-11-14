// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// How a SECS-II value looks as text, in one place.
/// </summary>
/// <remarks>
/// <para>
/// SML, INI and XML all have to answer the same questions - how is a <see cref="SecsFormat.U4"/>
/// array spelled, how is a quote inside an ASCII item escaped, is a boolean <c>T</c> or
/// <c>true</c> - and they must answer them identically or a value will not survive a round trip
/// through all three.
/// </para>
/// <para>
/// Everything is parsed and written with <see cref="CultureInfo.InvariantCulture"/>. A recipe
/// parameter written on a machine whose decimal separator is a comma has to read back the same
/// on one where it is a dot.
/// </para>
/// </remarks>
public static class SecsItemText
{
    /// <summary>
    /// Parses the value text of a non-list item.
    /// </summary>
    /// <param name="format">The item format. Must not be <see cref="SecsFormat.List"/>.</param>
    /// <param name="text">
    /// The value text: a quoted string for ASCII and JIS-8, otherwise whitespace- or
    /// comma-separated elements.
    /// </param>
    /// <returns>The item.</returns>
    /// <exception cref="FormatException">An element is not valid for <paramref name="format"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="SecsFormat.List"/>.</exception>
    public static SecsItem Parse(SecsFormat format, ReadOnlySpan<char> text) => format switch
    {
        SecsFormat.List => throw new ArgumentOutOfRangeException(nameof(format), format, "A list has children, not value text."),
        SecsFormat.ASCII => SecsItem.A(ParseString(text)),
        SecsFormat.JIS8 => SecsItem.J(ParseString(text)),
        SecsFormat.Boolean => SecsItem.Boolean(ParseBooleans(text)),
        SecsFormat.Binary => SecsItem.B(ParseBytes(text)),
        SecsFormat.U1 => SecsItem.U1(ParseBytes(text)),
        SecsFormat.I1 => SecsItem.I1(ParseElements<sbyte>(text)),
        SecsFormat.I2 => SecsItem.I2(ParseElements<short>(text)),
        SecsFormat.I4 => SecsItem.I4(ParseElements<int>(text)),
        SecsFormat.I8 => SecsItem.I8(ParseElements<long>(text)),
        SecsFormat.U2 => SecsItem.U2(ParseElements<ushort>(text)),
        SecsFormat.U4 => SecsItem.U4(ParseElements<uint>(text)),
        SecsFormat.U8 => SecsItem.U8(ParseElements<ulong>(text)),
        SecsFormat.F4 => SecsItem.F4(ParseElements<float>(text)),
        SecsFormat.F8 => SecsItem.F8(ParseElements<double>(text)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), (int)format, "Not a SECS-II item format."),
    };

    /// <summary>Writes the value text of a non-list item.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="item">The item. Must not be a list.</param>
    /// <param name="options">Formatting options.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="item"/> is a list.</exception>
    public static void WriteValues(SecsUtf8Writer writer, SecsItem item, SecsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(item);
        options ??= SecsWriteOptions.Default;

        switch (item.Format)
        {
            case SecsFormat.List:
                throw new ArgumentOutOfRangeException(nameof(item), item.Format, "A list has children, not value text.");

            case SecsFormat.ASCII:
            case SecsFormat.JIS8:
                WriteQuoted(writer, item.GetString());
                break;

            case SecsFormat.Boolean:
                WriteSeparated(writer, item.GetSpan<bool>(), static (w, v) => w.WriteBoolean(v));
                break;

            case SecsFormat.Binary:
            case SecsFormat.U1:
                WriteSeparated(writer, item.GetSpan<byte>(), static (w, v) =>
                {
                    w.Write("0x");
                    w.WriteHex(v);
                });
                break;

            case SecsFormat.I1:
                WriteSeparated(writer, item.GetSpan<sbyte>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.I2:
                WriteSeparated(writer, item.GetSpan<short>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.I4:
                WriteSeparated(writer, item.GetSpan<int>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.I8:
                WriteSeparated(writer, item.GetSpan<long>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.U2:
                WriteSeparated(writer, item.GetSpan<ushort>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.U4:
                WriteSeparated(writer, item.GetSpan<uint>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.U8:
                WriteSeparated(writer, item.GetSpan<ulong>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.F4:
                WriteSeparated(writer, item.GetSpan<float>(), static (w, v) => w.WriteValue(v));
                break;
            case SecsFormat.F8:
                WriteSeparated(writer, item.GetSpan<double>(), static (w, v) => w.WriteValue(v));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item), (int)item.Format, "Not a SECS-II item format.");
        }
    }

    /// <summary>Writes a string in SML quoting: single quotes, with an embedded quote doubled.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The text.</param>
    public static void WriteQuoted(SecsUtf8Writer writer, string? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write('\'');
        if (!string.IsNullOrEmpty(value))
        {
            var remaining = value.AsSpan();
            while (true)
            {
                var quote = remaining.IndexOf('\'');
                if (quote < 0)
                {
                    writer.Write(remaining);
                    break;
                }

                writer.Write(remaining[..quote]);
                writer.Write("''");
                remaining = remaining[(quote + 1)..];
            }
        }

        writer.Write('\'');
    }

    /// <summary>Unquotes a string written by <see cref="WriteQuoted"/>, accepting single or double quotes.</summary>
    /// <param name="text">The quoted - or bare - text.</param>
    /// <returns>The text.</returns>
    public static string ParseString(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return trimmed.ToString();
        }

        var quote = trimmed[0];
        if ((quote != '\'' && quote != '"') || trimmed[^1] != quote)
        {
            return trimmed.ToString();
        }

        var inner = trimmed[1..^1];
        var doubled = $"{quote}{quote}";
        return inner.Contains(doubled, StringComparison.Ordinal)
            ? inner.ToString().Replace(doubled, quote.ToString(), StringComparison.Ordinal)
            : inner.ToString();
    }

    private static void WriteSeparated<T>(SecsUtf8Writer writer, ReadOnlySpan<T> values, Action<SecsUtf8Writer, T> write)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(' ');
            }

            write(writer, values[i]);
        }
    }

    private static bool[] ParseBooleans(ReadOnlySpan<char> text)
    {
        var values = new List<bool>();
        foreach (var token in new TokenEnumerator(text))
        {
            values.Add(token switch
            {
                "T" or "t" or "1" => true,
                "F" or "f" or "0" => false,
                _ => bool.TryParse(token, out var parsed)
                    ? parsed
                    : throw new FormatException($"'{token}' is not a boolean. Use T, F, 1, 0, true or false."),
            });
        }

        return [.. values];
    }

    private static byte[] ParseBytes(ReadOnlySpan<char> text)
    {
        var values = new List<byte>();
        foreach (var token in new TokenEnumerator(text))
        {
            var span = token.AsSpan();
            // 0x1F, 1F and 31 are all accepted; a bare decimal is only unambiguous without the
            // prefix, so the prefix decides the base.
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(byte.Parse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            else if (byte.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue))
            {
                values.Add(decimalValue);
            }
            else
            {
                values.Add(byte.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        return [.. values];
    }

    private static T[] ParseElements<T>(ReadOnlySpan<char> text)
        where T : ISpanParsable<T>
    {
        var values = new List<T>();
        foreach (var token in new TokenEnumerator(text))
        {
            values.Add(T.Parse(token, CultureInfo.InvariantCulture));
        }

        return [.. values];
    }

    /// <summary>
    /// Walks whitespace- and comma-separated tokens of a span.
    /// </summary>
    /// <remarks>
    /// A struct enumerator over the span rather than <c>string.Split</c>: a 10,000-element U4
    /// item would otherwise allocate 10,000 strings plus the array holding them. Tokens
    /// materialise as strings only where the parse genuinely needs one.
    /// </remarks>
    private ref struct TokenEnumerator(ReadOnlySpan<char> text)
    {
        private static readonly char[] Separators = [' ', '\t', ',', '\r', '\n'];

        private ReadOnlySpan<char> _remaining = text;

        public string Current { get; private set; } = string.Empty;

        public TokenEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (!_remaining.IsEmpty)
            {
                var start = _remaining.IndexOfAnyExcept(Separators);
                if (start < 0)
                {
                    _remaining = default;
                    return false;
                }

                _remaining = _remaining[start..];
                var end = _remaining.IndexOfAny(Separators);
                if (end < 0)
                {
                    Current = _remaining.ToString();
                    _remaining = default;
                    return true;
                }

                Current = _remaining[..end].ToString();
                _remaining = _remaining[end..];
                return true;
            }

            return false;
        }
    }
}
