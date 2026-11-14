// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using System.Text;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Sml;

/// <summary>
/// Reads SML into messages.
/// </summary>
/// <remarks>
/// <para>
/// Reading happens in two stages. A scanner consumes lines from the <see cref="PipeReader"/>
/// and works out where one message ends - tracking angle-bracket depth, and ignoring brackets,
/// dots and comment markers that appear inside a quoted string. Only a complete message's text
/// is then handed to the recursive-descent parser.
/// </para>
/// <para>
/// That split is what keeps memory bounded by the largest single message rather than by the
/// file, and it is why the parser itself needs no suspension logic: by the time it runs, it has
/// everything it needs.
/// </para>
/// </remarks>
public sealed class SmlFormatReader : ISecsFormatReader
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => SmlFormat.Descriptor;

    /// <inheritdoc/>
    public async IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= SecsReadOptions.Default;

        var lines = new SecsLineReader(input);
        var block = new StringBuilder();
        var depth = 0;
        var headerDone = false;
        var blockStartLine = 1;

        while (true)
        {
            var line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (block.Length == 0)
            {
                if (IsTrivia(line))
                {
                    continue;
                }

                blockStartLine = lines.LineNumber;
            }

            block.Append(line).Append('\n');

            var terminated = ScanLine(line, ref depth, headerDone);
            headerDone = true;

            if (!terminated)
            {
                continue;
            }

            yield return SmlParser.ParseMessage(block.ToString(), blockStartLine, options);
            block.Clear();
            depth = 0;
            headerDone = false;
        }

        if (block.Length > 0)
        {
            // A last message whose terminating '.' the writer left off.
            yield return SmlParser.ParseMessage(block.ToString(), blockStartLine, options);
        }
    }

    /// <summary>Parses every message in an SML document held in memory.</summary>
    /// <param name="sml">The document.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    /// <exception cref="SecsFormatException">The document is not valid SML.</exception>
    public static List<SecsMessage> Parse(string sml, SecsReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sml);
        options ??= SecsReadOptions.Default;

        var messages = new List<SecsMessage>();
        var block = new StringBuilder();
        var depth = 0;
        var headerDone = false;
        var lineNumber = 0;
        var blockStartLine = 1;

        foreach (var raw in sml.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');

            if (block.Length == 0)
            {
                if (IsTrivia(line))
                {
                    continue;
                }

                blockStartLine = lineNumber;
            }

            block.Append(line).Append('\n');
            var terminated = ScanLine(line, ref depth, headerDone);
            headerDone = true;

            if (!terminated)
            {
                continue;
            }

            messages.Add(SmlParser.ParseMessage(block.ToString(), blockStartLine, options));
            block.Clear();
            depth = 0;
            headerDone = false;
        }

        if (block.Length > 0)
        {
            messages.Add(SmlParser.ParseMessage(block.ToString(), blockStartLine, options));
        }

        return messages;
    }

    private static bool IsTrivia(string line)
    {
        var span = line.AsSpan().Trim();
        return span.IsEmpty || span.StartsWith("//") || span.StartsWith("#") || span.StartsWith(";");
    }

    /// <summary>
    /// Walks one line, updating the bracket depth and reporting whether the message ended.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="depth">The running angle-bracket depth.</param>
    /// <param name="headerDone">Whether the message's header line has already been consumed.</param>
    /// <returns><see langword="true"/> when this line ends the message.</returns>
    /// <remarks>
    /// Quotes matter: an ASCII item may legitimately contain <c>&lt;</c>, <c>&gt;</c>, <c>.</c> or
    /// <c>//</c>, and a scanner that counts those as structure will split a message in the middle
    /// of a string. The dot only terminates once the header is behind us, so a message named
    /// <c>Recipe.Download</c> is not mistaken for the end of one.
    /// </remarks>
    private static bool ScanLine(string line, ref int depth, bool headerDone)
    {
        var span = line.AsSpan();
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

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;
                case '/' when i + 1 < span.Length && span[i + 1] == '/':
                    return false;
                case '<':
                    depth++;
                    break;
                case '>':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case '.' when depth == 0 && headerDone:
                    return true;
            }
        }

        return false;
    }
}
