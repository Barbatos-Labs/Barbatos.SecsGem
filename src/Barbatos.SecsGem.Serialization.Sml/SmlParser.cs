// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Sml;

/// <summary>
/// Turns the text of one complete SML message into a <see cref="SecsMessage"/>.
/// </summary>
/// <remarks>
/// Recursive descent over a string, because by the time this runs the scanner has already
/// established that the text holds exactly one message. Keeping the "where does a message end"
/// question out of the parser is what keeps the parser this short.
/// </remarks>
internal static class SmlParser
{
    internal static SecsMessage ParseMessage(string block, int startLine, SecsReadOptions options)
    {
        var newline = block.IndexOf('\n', StringComparison.Ordinal);
        var headerLine = newline < 0 ? block : block[..newline];
        var body = newline < 0 ? string.Empty : block[(newline + 1)..];

        var (name, s, f, replyExpected) = ParseHeader(headerLine, startLine);

        var cursor = new Cursor(body, startLine + 1);
        cursor.SkipTrivia();

        SecsItem? root = null;
        if (cursor.Peek() == '<')
        {
            root = ParseItem(cursor, options);
            cursor.SkipTrivia();
        }

        if (cursor.Peek() == '.')
        {
            cursor.Next();
            cursor.SkipTrivia();
        }

        if (!cursor.Eof && options.Strict)
        {
            root?.Dispose();
            throw new SecsFormatException(
                SecsFormatIds.Sml,
                $"Unexpected '{cursor.Peek()}' after the end of the message.",
                cursor.Line);
        }

        return new SecsMessage(s, f, replyExpected)
        {
            Name = name,
            Item = root,
        };
    }

    private static (string? Name, byte S, byte F, bool ReplyExpected) ParseHeader(string headerLine, int lineNumber)
    {
        var span = headerLine.AsSpan().Trim();

        var comment = IndexOfComment(span);
        if (comment >= 0)
        {
            span = span[..comment].TrimEnd();
        }

        if (span.IsEmpty)
        {
            throw new SecsFormatException(SecsFormatIds.Sml, "Expected a message header such as \"'S1F3' W\".", lineNumber);
        }

        // An optional name precedes the S/F token:
        //   GetEquipmentStatus: 'S1F3' W
        //
        // The split is found from the quote, not from the first 'S'. A name like
        // "EventReportSend" contains an S of its own, and a name like "Recipe.Download" contains
        // a dot - either will fool a scan that looks for the stream marker first. The quote that
        // opens the S/F token is unambiguous; only when there is none does the last colon decide.
        string? name = null;
        var quote = span.IndexOfAny('\'', '"');
        var split = quote >= 0 ? quote : span.LastIndexOf(':') + 1;

        if (split > 0)
        {
            name = span[..split].Trim().TrimEnd(':').Trim().ToString();
            span = span[split..].Trim();
        }

        var i = 0;
        if (i < span.Length && span[i] is '\'' or '"')
        {
            i++;
        }

        if (i >= span.Length || (span[i] != 'S' && span[i] != 's'))
        {
            throw new SecsFormatException(SecsFormatIds.Sml, $"Expected 'S' in the message header, found \"{span}\".", lineNumber);
        }

        i++;
        var s = ReadByte(span, ref i, 'S', lineNumber);

        if (i >= span.Length || (span[i] != 'F' && span[i] != 'f'))
        {
            throw new SecsFormatException(SecsFormatIds.Sml, $"Expected 'F' in the message header, found \"{span}\".", lineNumber);
        }

        i++;
        var f = ReadByte(span, ref i, 'F', lineNumber);

        var tail = span[i..];
        var replyExpected = tail.IndexOfAny('W', 'w') >= 0;

        return (string.IsNullOrEmpty(name) ? null : name, s, f, replyExpected);

        static byte ReadByte(ReadOnlySpan<char> span, ref int i, char which, int lineNumber)
        {
            var start = i;
            while (i < span.Length && char.IsAsciiDigit(span[i]))
            {
                i++;
            }

            if (start == i)
            {
                throw new SecsFormatException(SecsFormatIds.Sml, $"Expected a number after '{which}'.", lineNumber);
            }

            if (!byte.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new SecsFormatException(SecsFormatIds.Sml, $"'{span[start..i]}' is not a valid {which} number.", lineNumber);
            }

            return value;
        }
    }

    private static SecsItem ParseItem(Cursor cursor, SecsReadOptions options)
    {
        cursor.SkipTrivia();
        cursor.Expect('<');
        cursor.SkipTrivia();

        var formatToken = cursor.ReadFormatToken();
        if (!SecsFormatExtensions.TryParse(formatToken, out var format))
        {
            throw new SecsFormatException(SecsFormatIds.Sml, $"'{formatToken}' is not a SECS-II item format.", cursor.Line);
        }

        var declaredCount = cursor.TryReadDeclaredCount();

        SecsItem item;
        if (format == SecsFormat.List)
        {
            var children = new List<SecsItem>();
            try
            {
                cursor.SkipTrivia();
                while (cursor.Peek() == '<')
                {
                    children.Add(ParseItem(cursor, options));
                    cursor.SkipTrivia();
                }

                cursor.Expect('>');
            }
            catch
            {
                foreach (var child in children)
                {
                    child.Dispose();
                }

                throw;
            }

            item = SecsItem.L(children);
        }
        else
        {
            var valueText = cursor.ReadValueText();
            cursor.Expect('>');
            try
            {
                item = SecsItemText.Parse(format, valueText);
            }
            catch (FormatException ex)
            {
                throw new SecsFormatException(SecsFormatIds.Sml, $"Bad {format} value: {ex.Message}", ex);
            }
            catch (OverflowException ex)
            {
                throw new SecsFormatException(SecsFormatIds.Sml, $"A {format} value is out of range: {ex.Message}", ex);
            }
        }

        // A declared count is a cross-check, not the source of truth. Text is excluded: the
        // bracket holds the BYTE count, while Count is characters, and under a multi-byte JIS-8
        // encoding those differ legitimately.
        if (options.Strict && declaredCount is int declared && !format.IsText() && declared != item.Count)
        {
            item.Dispose();
            throw new SecsFormatException(
                SecsFormatIds.Sml,
                $"A {format.ToSmlToken()} item declares [{declared}] but holds {item.Count} element(s).",
                cursor.Line);
        }

        if (options.ReadItemLabels && cursor.TryReadTrailingComment(out var label))
        {
            item.Label = label;
        }

        return item;
    }

    private static int IndexOfComment(ReadOnlySpan<char> span)
    {
        var quote = '\0';
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < span.Length && span[i + 1] == quote)
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < span.Length && span[i + 1] == '/')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A position in the message text, with just enough bookkeeping to report a line number.</summary>
    private sealed class Cursor(string text, int firstLine)
    {
        private int _position;

        public int Line { get; private set; } = firstLine;

        public bool Eof => _position >= text.Length;

        public char Peek() => _position < text.Length ? text[_position] : '\0';

        public char Next()
        {
            var c = text[_position++];
            if (c == '\n')
            {
                Line++;
            }

            return c;
        }

        public void Expect(char expected)
        {
            SkipTrivia();
            if (Peek() != expected)
            {
                throw new SecsFormatException(
                    SecsFormatIds.Sml,
                    Eof ? $"Expected '{expected}' but the message ended." : $"Expected '{expected}' but found '{Peek()}'.",
                    Line);
            }

            Next();
        }

        /// <summary>Skips whitespace and whole-line comments.</summary>
        public void SkipTrivia()
        {
            while (!Eof)
            {
                var c = Peek();
                if (char.IsWhiteSpace(c))
                {
                    Next();
                    continue;
                }

                if (c == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    SkipToEndOfLine();
                    continue;
                }

                return;
            }
        }

        public string ReadFormatToken()
        {
            var start = _position;
            while (!Eof && char.IsAsciiLetterOrDigit(Peek()))
            {
                Next();
            }

            return text[start.._position];
        }

        /// <summary>Reads an optional <c>[n]</c> after the format token.</summary>
        public int? TryReadDeclaredCount()
        {
            SkipInlineWhitespace();
            if (Peek() != '[')
            {
                return null;
            }

            Next();
            SkipInlineWhitespace();

            var start = _position;
            while (!Eof && char.IsAsciiDigit(Peek()))
            {
                Next();
            }

            var digits = text[start.._position];
            SkipInlineWhitespace();
            Expect(']');

            return digits.Length == 0
                ? null
                : int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a value item's text, up to the closing angle bracket.</summary>
        /// <returns>The raw text between the header and the <c>&gt;</c>.</returns>
        /// <remarks>
        /// Quote-aware, because an ASCII item is allowed to contain a <c>&gt;</c>:
        /// <c>&lt;A [3] 'a&gt;b'&gt;</c> must not end at the first bracket it meets.
        /// </remarks>
        public string ReadValueText()
        {
            var start = _position;
            var quote = '\0';

            while (!Eof)
            {
                var c = Peek();
                if (quote != '\0')
                {
                    Next();
                    if (c == quote)
                    {
                        if (Peek() == quote)
                        {
                            Next();
                            continue;
                        }

                        quote = '\0';
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    Next();
                    continue;
                }

                if (c == '>')
                {
                    break;
                }

                Next();
            }

            return text[start.._position];
        }

        /// <summary>Reads a comment that sits on the same line as the item that just closed.</summary>
        /// <param name="comment">The comment text.</param>
        /// <returns><see langword="true"/> when one was there.</returns>
        public bool TryReadTrailingComment(out string? comment)
        {
            comment = null;
            var save = _position;

            SkipInlineWhitespace();
            if (Eof || Peek() != '/' || _position + 1 >= text.Length || text[_position + 1] != '/')
            {
                _position = save;
                return false;
            }

            Next();
            Next();
            SkipInlineWhitespace();

            var start = _position;
            while (!Eof && Peek() != '\n')
            {
                Next();
            }

            comment = text[start.._position].TrimEnd();
            return comment.Length > 0;
        }

        private void SkipInlineWhitespace()
        {
            while (!Eof && Peek() is ' ' or '\t')
            {
                Next();
            }
        }

        private void SkipToEndOfLine()
        {
            while (!Eof && Peek() != '\n')
            {
                Next();
            }
        }
    }
}
