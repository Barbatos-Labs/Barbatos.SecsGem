// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.IO.Pipelines;
using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Ini;

/// <summary>
/// Reads path-addressed INI into messages.
/// </summary>
/// <remarks>
/// Streams a section at a time, so memory is bounded by the largest message rather than by the
/// file. A section's keys are collected, and only then is the tree rebuilt by following the
/// paths: <c>item.1</c>, then <c>item.1.1</c>, <c>item.1.2</c> and so on, stopping at the first
/// index that is not there. That makes the format order-independent - the keys may appear in any
/// order in the file.
/// </remarks>
public sealed class IniFormatReader : ISecsFormatReader
{
    /// <inheritdoc/>
    public SecsFormatDescriptor Descriptor => IniFormat.Descriptor;

    /// <inheritdoc/>
    public async IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= SecsReadOptions.Default;

        var lines = new SecsLineReader(input);
        var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sectionStartLine = 0;
        var inSection = false;

        while (true)
        {
            var line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] is ';' or '#')
            {
                continue;
            }

            if (span[0] == '[')
            {
                if (inSection)
                {
                    yield return Build(section, sectionStartLine, options);
                    section.Clear();
                }

                inSection = true;
                sectionStartLine = lines.LineNumber;
                continue;
            }

            var separator = span.IndexOf('=');
            if (separator < 0)
            {
                if (options.Strict)
                {
                    throw new SecsFormatException(SecsFormatIds.Ini, $"Expected 'key = value', found \"{span}\".", lines.LineNumber);
                }

                continue;
            }

            var key = span[..separator].Trim().ToString();
            var value = span[(separator + 1)..].Trim().ToString();
            section[key] = value;
            inSection = true;
        }

        if (inSection && section.Count > 0)
        {
            yield return Build(section, sectionStartLine, options);
        }
    }

    /// <summary>Parses an INI document held in memory.</summary>
    /// <param name="ini">The document.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    /// <exception cref="SecsFormatException">The document is not valid for this format.</exception>
    public static List<SecsMessage> Parse(string ini, SecsReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ini);
        options ??= SecsReadOptions.Default;

        var messages = new List<SecsMessage>();
        var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        var lineNumber = 0;
        var sectionStartLine = 0;

        foreach (var raw in ini.Split('\n'))
        {
            lineNumber++;
            var span = raw.AsSpan().Trim().TrimStart('﻿');
            if (span.IsEmpty || span[0] is ';' or '#')
            {
                continue;
            }

            if (span[0] == '[')
            {
                if (inSection)
                {
                    messages.Add(Build(section, sectionStartLine, options));
                    section.Clear();
                }

                inSection = true;
                sectionStartLine = lineNumber;
                continue;
            }

            var separator = span.IndexOf('=');
            if (separator < 0)
            {
                if (options.Strict)
                {
                    throw new SecsFormatException(SecsFormatIds.Ini, $"Expected 'key = value', found \"{span}\".", lineNumber);
                }

                continue;
            }

            section[span[..separator].Trim().ToString()] = span[(separator + 1)..].Trim().ToString();
            inSection = true;
        }

        if (inSection && section.Count > 0)
        {
            messages.Add(Build(section, sectionStartLine, options));
        }

        return messages;
    }

    private static SecsMessage Build(Dictionary<string, string> section, int lineNumber, SecsReadOptions options)
    {
        var name = section.GetValueOrDefault(IniFormat.NameKey);
        var s = ReadByte(section, IniFormat.StreamKey, lineNumber);
        var f = ReadByte(section, IniFormat.FunctionKey, lineNumber);
        var replyExpected = ReadBoolean(section, IniFormat.ReplyExpectedKey);

        var item = BuildItem(section, IniFormat.RootPath, lineNumber, options, depth: 0);

        return new SecsMessage(s, f, replyExpected)
        {
            Name = string.IsNullOrEmpty(name) ? null : name,
            Item = item,
        };
    }

    private static SecsItem? BuildItem(
        Dictionary<string, string> section,
        string path,
        int lineNumber,
        SecsReadOptions options,
        int depth)
    {
        if (depth > SecsItem.MaxDepth)
        {
            throw new SecsFormatException(SecsFormatIds.Ini, $"Item nesting is deeper than the {SecsItem.MaxDepth}-level limit.", lineNumber);
        }

        if (!section.TryGetValue(IniFormat.ItemPrefix + path, out var raw))
        {
            return null;
        }

        var (format, declaredCount, values) = ParseValueLine(raw, lineNumber);

        SecsItem item;
        if (format == SecsFormat.List)
        {
            var children = new List<SecsItem>();
            try
            {
                for (var index = 1; ; index++)
                {
                    var child = BuildItem(section, $"{path}.{index}", lineNumber, options, depth + 1);
                    if (child is null)
                    {
                        break;
                    }

                    children.Add(child);
                }
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
            try
            {
                item = SecsItemText.Parse(format, values);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new SecsFormatException(SecsFormatIds.Ini, $"Bad {format} value at {IniFormat.ItemPrefix}{path}: {ex.Message}", ex);
            }
        }

        if (options.Strict && declaredCount is int declared && !format.IsText() && declared != item.Count)
        {
            item.Dispose();
            throw new SecsFormatException(
                SecsFormatIds.Ini,
                $"{IniFormat.ItemPrefix}{path} declares [{declared}] but holds {item.Count} element(s).",
                lineNumber);
        }

        if (options.ReadItemLabels
            && section.TryGetValue(IniFormat.ItemPrefix + path + IniFormat.LabelSuffix, out var label)
            && !string.IsNullOrEmpty(label))
        {
            item = item.Named(label);
        }

        return item;
    }

    /// <summary>Splits <c>U4[2] 10 20</c> into its format, declared count and values.</summary>
    private static (SecsFormat Format, int? DeclaredCount, string Values) ParseValueLine(string raw, int lineNumber)
    {
        var span = raw.AsSpan().Trim();
        if (span.IsEmpty)
        {
            throw new SecsFormatException(SecsFormatIds.Ini, "An item line has no format.", lineNumber);
        }

        // The format token runs to the first '[', space or tab - so A[6], A [6] and A all work.
        var end = 0;
        while (end < span.Length && char.IsAsciiLetterOrDigit(span[end]))
        {
            end++;
        }

        var token = span[..end];
        if (!SecsFormatExtensions.TryParse(token, out var format))
        {
            throw new SecsFormatException(SecsFormatIds.Ini, $"'{token}' is not a SECS-II item format.", lineNumber);
        }

        var rest = span[end..].TrimStart();
        int? declaredCount = null;

        if (rest.Length > 0 && rest[0] == '[')
        {
            var close = rest.IndexOf(']');
            if (close < 0)
            {
                throw new SecsFormatException(SecsFormatIds.Ini, "An item length is missing its closing ']'.", lineNumber);
            }

            var digits = rest[1..close].Trim();
            if (!digits.IsEmpty)
            {
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                {
                    throw new SecsFormatException(SecsFormatIds.Ini, $"'{digits}' is not an item length.", lineNumber);
                }

                declaredCount = count;
            }

            rest = rest[(close + 1)..];
        }

        return (format, declaredCount, rest.Trim().ToString());
    }

    private static byte ReadByte(Dictionary<string, string> section, string key, int lineNumber)
    {
        if (!section.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return 0;
        }

        return byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SecsFormatException(SecsFormatIds.Ini, $"{key} = {raw} is not a number from 0 to 255.", lineNumber);
    }

    private static bool ReadBoolean(Dictionary<string, string> section, string key)
    {
        if (!section.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return false;
        }

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "W", StringComparison.OrdinalIgnoreCase);
    }
}
