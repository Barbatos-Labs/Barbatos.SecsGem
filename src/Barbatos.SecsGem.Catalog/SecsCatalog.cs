// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Frozen;
using System.Globalization;

namespace Barbatos.SecsGem.Catalog;

/// <summary>
/// The SECS-II message set as reference data: 524 stream/function pairs and 317 data items.
/// </summary>
/// <remarks>
/// <para>
/// This is a lookup table, not a protocol. Nothing here sends, receives or validates anything -
/// it answers "what is S6F11 called", "which way does it go", "what does COMMACK 1 mean". That is
/// enough to turn a log line from <c>S6F11</c> into <c>S6F11 Event Report Send</c>, which is the
/// difference between a capture a person can read and one they have to look up.
/// </para>
/// <para>
/// Everything is loaded from one embedded 39 KB resource the first time any member is touched, into
/// frozen dictionaries. Referencing the package costs nothing until you ask it something.
/// </para>
/// <para>
/// <strong>On provenance.</strong> The table was built from a public SECS-II reference, not from
/// SEMI E5 itself, and that reference marks 405 of the 524 entries as a representative subset -
/// a flag carried through as <see cref="SecsMessageInfo.IsRepresentative"/>. All 524 rows and all
/// 375 dictionary entries have since been compared against that reference one by one, and 520 rows
/// and the whole dictionary agree; the four that do not are the S6F11 and S6F12 pair the reference
/// had reversed, and the S6F1 and S6F2 rows it displaced. That pair is corrected here and covered
/// by tests that fail if it is ever put back. Treat this as a good index into E5, not as a
/// substitute for it - the limit is not transcription but that the reference hedges 151 of its own
/// item entries, which the package README works through.
/// </para>
/// <para>
/// <see cref="SecsStreamInfo.Provenance"/> asks that question per stream, which is the form it
/// comes up in: how many of a stream's entries were definitions, and whether its bodies name
/// items this catalogue can resolve. Nine streams have no definitions at all and two of those are
/// implemented here anyway - the flag means "check this against something else", and the package
/// README works through the one stream that failed such a check.
/// </para>
/// </remarks>
public static class SecsCatalog
{
    private const string ResourceName = "Barbatos.SecsGem.Catalog.secs2-catalog.txt";

    private static readonly Loaded Data = Load();

    /// <summary>Gets every stream, ordered by number.</summary>
    public static IReadOnlyList<SecsStreamInfo> Streams => Data.Streams;

    /// <summary>Gets every message, ordered by stream and then function.</summary>
    public static IReadOnlyList<SecsMessageInfo> Messages => Data.Messages;

    /// <summary>Gets every data item, ordered by name.</summary>
    public static IReadOnlyList<SecsDataItemInfo> DataItems => Data.Items;

    /// <summary>Gets how much of the catalogue is anchored to the rest of it.</summary>
    /// <remarks>
    /// Ask this before quoting the reference at anybody, and
    /// <see cref="SecsStreamInfo.Provenance"/> before implementing against one stream. The two
    /// answer different questions, and this one cannot be assembled out of the other: whether a
    /// definition is named by any message at all is a fact about the whole table.
    /// </remarks>
    public static SecsCatalogProvenance Provenance => Data.Provenance;

    /// <summary>Finds a stream.</summary>
    /// <param name="stream">S.</param>
    /// <param name="info">The stream, when it is in the catalogue.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public static bool TryGetStream(byte stream, out SecsStreamInfo? info)
        => Data.ByStream.TryGetValue(stream, out info);

    /// <summary>Finds a message.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F.</param>
    /// <param name="info">The message, when it is in the catalogue.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public static bool TryGetMessage(byte stream, byte function, out SecsMessageInfo? info)
        => Data.ByMessage.TryGetValue(Key(stream, function), out info);

    /// <summary>Finds a data item by name.</summary>
    /// <param name="name">The item's name, for example <c>COMMACK</c>. Case-sensitive, as E5 writes them.</param>
    /// <param name="info">The item, when it is in the catalogue.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public static bool TryGetDataItem(string name, out SecsDataItemInfo? info)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Data.ByItem.TryGetValue(name, out info);
    }

    /// <summary>Gets a message's standard name.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F.</param>
    /// <returns>The name, or <see langword="null"/> when the pair is not in the catalogue.</returns>
    /// <remarks>
    /// Handy for filling in <see cref="SecsMessage.Name"/>, which SML, JSON and XML all carry:
    /// <c>new SecsMessage(6, 11) { Name = SecsCatalog.NameOf(6, 11) }</c>.
    /// </remarks>
    public static string? NameOf(byte stream, byte function)
        => TryGetMessage(stream, function, out var info) ? info!.Name : null;

    /// <summary>Describes a stream and function in one line.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F.</param>
    /// <returns>For example <c>S6F11 Event Report Send</c>, or just <c>S6F11</c> when unknown.</returns>
    public static string Describe(byte stream, byte function)
        => TryGetMessage(stream, function, out var info)
            ? info!.ToString()
            : $"S{stream}F{function}";

    private static ushort Key(byte stream, byte function) => (ushort)((stream << 8) | function);

    private static Loaded Load()
    {
        using var input = typeof(SecsCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The catalogue resource '{ResourceName}' is missing from the assembly.");

        using var reader = new StreamReader(input);

        var streamNames = new Dictionary<byte, string>();
        var messages = new List<SecsMessageInfo>();
        var items = new List<SecsDataItemInfo>();
        var section = string.Empty;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                section = line;
                continue;
            }

            var fields = line.Split('|');

            switch (section)
            {
                case "#streams":
                    streamNames[byte.Parse(fields[0], CultureInfo.InvariantCulture)] = fields[1];
                    break;

                case "#messages":
                    messages.Add(ReadMessage(fields));
                    break;

                case "#items":
                    items.Add(ReadItem(fields));
                    break;

                default:
                    break;
            }
        }

        var byStream = new List<SecsStreamInfo>();

        // One pass over the dictionary, reused for every stream: the provenance of a stream is
        // half "how many of these did the source define" and half "can its bodies be read against
        // anything", and the second half needs the item table that was just loaded.
        var defined = items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        // And one pass the other way, which no per-stream measure could do: an item that no
        // message names is invisible from every stream at once, so the names are collected across
        // the whole table and every item is rebuilt knowing whether anything names it. Empty
        // names are dropped - a trailing comma in an item list is a gap in the row, not an item.
        var used = messages
            .SelectMany(message => message.DataItems)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // And one pass that answers the question in between the other two: which streams name each
        // item. Every chapter of this repository computed this by hand before it was a property -
        // it is what tells an undefined item with a sibling elsewhere apart from one with nothing
        // anywhere to read it against.
        var itemStreams = new Dictionary<string, SortedSet<byte>>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var name in message.DataItems)
            {
                // Unreachable with the current data and kept anyway, the same way the other two
                // empty-name filters in this file are: ReadMessage turns an empty item field into
                // no items, so there is no empty name to skip. A refreshed reference with a
                // trailing comma would otherwise give "" a stream list of its own.
                if (name.Length == 0)
                {
                    continue;
                }

                if (!itemStreams.TryGetValue(name, out var carrying))
                {
                    carrying = [];
                    itemStreams[name] = carrying;
                }

                carrying.Add(message.Stream);
            }
        }

        items = [.. items.Select(item => new SecsDataItemInfo(
            item.Name,
            item.FormatToken,
            item.Format,
            item.Length,
            item.Codes,
            isNamedByNoMessage: !used.Contains(item.Name),
            streams: itemStreams.TryGetValue(item.Name, out var naming) ? [.. naming] : []))];

        // And the fourth pass, which is the overlap between the other two directions: a name a
        // message uses and no definition covers, beside a definition that plainly means the same
        // thing. Neither list alone can show this - UndefinedDataItems and UnusedDefinitions are
        // both long, and what matters is which entries of one are the other's under a different
        // spelling.
        var rivals = Rivals(used, defined, items, itemStreams);

        foreach (var (number, name) in streamNames.OrderBy(pair => pair.Key))
        {
            var ofStream = messages.Where(m => m.Stream == number).OrderBy(m => m.Function).ToArray();

            // Each name this stream uses is two independent facts: does the dictionary define it,
            // and does any other stream name it. Four buckets, and which bucket an undefined name
            // falls in is the difference between a vocabulary to leave alone and a field to carry
            // untouched.
            var names = ofStream
                .SelectMany(m => m.DataItems)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            List<string> ownAnchors = [];
            List<string> borrowedAnchors = [];
            List<string> sharedGaps = [];
            List<string> ownGaps = [];

            foreach (var item in names)
            {
                var shared = itemStreams.TryGetValue(item, out var carrying) && carrying.Count > 1;

                (defined.Contains(item)
                    ? shared ? borrowedAnchors : ownAnchors
                    : shared ? sharedGaps : ownGaps).Add(item);
            }

            byStream.Add(new SecsStreamInfo(
                number,
                name,
                ofStream,
                new SecsStreamProvenance(
                    number,
                    ofStream.Length,
                    ofStream.Count(m => m.IsRepresentative),
                    [.. names.Where(item => !defined.Contains(item))]),
                new SecsStreamAnchoring(number, ownAnchors, borrowedAnchors, sharedGaps, ownGaps))
            {
                // The stream's own gaps, with whichever definitions rival them. Filtered from the
                // whole-table list rather than recomputed, so the two can never disagree.
                RivalSpellings = [.. rivals.Where(rival => rival.Streams.Contains(number))],
            });
        }

        return new Loaded(
            byStream,
            messages.OrderBy(m => m.Stream).ThenBy(m => m.Function).ToArray(),
            items.OrderBy(i => i.Name, StringComparer.Ordinal).ToArray(),
            byStream.ToFrozenDictionary(s => s.Number),
            messages.ToFrozenDictionary(m => Key(m.Stream, m.Function)),
            items.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal),
            new SecsCatalogProvenance(
                byStream.Count,
                messages.Count,
                messages.Count(m => m.IsRepresentative),
                items.Count,
                used.Count,
                [.. used.Where(name => !defined.Contains(name)).Order(StringComparer.Ordinal)],
                [.. items.Where(item => item.IsNamedByNoMessage).Select(item => item.Name).Order(StringComparer.Ordinal)],

                // One line, and it is the headline of a refusal: two streams in this reference
                // carry the same name, and underneath that they claim the same twenty function
                // numbers for different transactions.
                // Grouped without regard to case, which is the one comparison in this repository
                // that is not Ordinal - and deliberately. Every Ordinal comparison here is over an
                // identifier, where PUMP and pump are two things; a stream's name is prose from a
                // reference's table of contents, where "Recipe Management" and "Recipe management"
                // would be one subject typed twice. The current data cannot tell the two readings
                // apart, since its one clash is an exact match, so a control on this finds nothing.
                [.. streamNames
                    .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new SecsStreamNameClash(
                        group.Key,
                        [.. group.Select(pair => pair.Key).Order()]))])
            {
                RivalSpellings = rivals,

                // A bare unused name might be an editing leftover. One with its values enumerated
                // is a capability the source specified in full and wired to nothing, which is a
                // different claim and the one worth quoting.
                DetailedUnusedDefinitions =
                [
                    .. items
                        .Where(item => item.IsNamedByNoMessage && item.Codes.Count > 0)
                        .Select(item => item.Name)
                        .Order(StringComparer.Ordinal),
                ],
            });
    }

    /// <summary>
    /// Pairs each name a message uses and no definition covers with any definition that plainly
    /// means the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two relations, and both are deliberately textual: this measure reports resemblances between
    /// <em>names</em> and takes no view on meaning, because a measure that guessed at meaning would
    /// be the very inference it exists to warn about.
    /// </para>
    /// <para>
    /// <strong>Four bounds keep it from being noise, and only one of them bites.</strong> That is
    /// worth stating plainly, because the obvious thing to write about a set of thresholds is that
    /// each was chosen against the data, and here three of the four were not needed by it:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong><c>ShortestStem</c>, the four-character floor on a prefix relation, is the one that
    /// does the work.</strong> Without it <c>LOC</c> rivals <c>LOCKID</c> - a location against a
    /// lock - and a one-character item name the reference carries rivals seven definitions at once.
    /// </description></item>
    /// <item><description>
    /// The <c>Shortest</c> floors on each name are <strong>unreachable with the current data</strong>:
    /// the only short names are one used name of a single character and two definitions of two, and
    /// a prefix relation already needs a stem of four, so a short name could only ever appear as an
    /// anagram - and none of them is an anagram of anything. Kept because a refreshed reference
    /// with a two-character pair would otherwise report it.
    /// </description></item>
    /// <item><description>
    /// <c>LongestSuffix</c> is <strong>unreachable too</strong>: no prefix relation in the current
    /// data has a stem of four and an extra part longer than three. Kept for the same reason.
    /// </description></item>
    /// </list>
    /// <para>
    /// A test asserts that unreachability rather than leaving it as a claim, so a refresh that
    /// makes one of the three matter fails it and says which. Every pair the recipe chapter relied
    /// on survives all four.
    /// </para>
    /// </remarks>
    private static List<SecsDataItemRival> Rivals(
        HashSet<string> used,
        HashSet<string> defined,
        IReadOnlyList<SecsDataItemInfo> items,
        Dictionary<string, SortedSet<byte>> itemStreams)
    {
        const int Shortest = 3;
        const int ShortestStem = 4;
        const int LongestSuffix = 3;

        List<SecsDataItemRival> found = [];

        foreach (var usedName in used.Where(name => !defined.Contains(name) && name.Length >= Shortest))
        {
            var sorted = Sorted(usedName);

            foreach (var item in items)
            {
                if (item.Name.Length < Shortest)
                {
                    continue;
                }

                SecsNameRelation? relation =
                    Sorted(item.Name) == sorted ? SecsNameRelation.Anagram
                    : Extends(usedName, item.Name) ? SecsNameRelation.UsedExtendsDefinition
                    : Extends(item.Name, usedName) ? SecsNameRelation.DefinitionExtendsUsed
                    : null;

                if (relation is null)
                {
                    continue;
                }

                found.Add(new SecsDataItemRival(
                    usedName,
                    item.Name,
                    relation.Value,
                    itemStreams.TryGetValue(usedName, out var carrying) ? [.. carrying] : [],
                    isDefinitionUsedElsewhere: used.Contains(item.Name)));
            }
        }

        return
        [
            .. found
                .OrderBy(rival => rival.UsedName, StringComparer.Ordinal)
                .ThenBy(rival => rival.DefinedName, StringComparer.Ordinal),
        ];

        static bool Extends(string longer, string shorter)
            => shorter.Length >= ShortestStem
                && longer.Length > shorter.Length
                && longer.Length - shorter.Length <= LongestSuffix
                && longer.StartsWith(shorter, StringComparison.Ordinal);

        static string Sorted(string name) => string.Concat(name.Order());
    }

    private static SecsMessageInfo ReadMessage(string[] fields)
    {
        var direction = fields[3] switch
        {
            "H" => SecsMessageDirection.HostToEquipment,
            "E" => SecsMessageDirection.EquipmentToHost,
            _ => SecsMessageDirection.Either,
        };

        return new SecsMessageInfo(
            byte.Parse(fields[0], CultureInfo.InvariantCulture),
            byte.Parse(fields[1], CultureInfo.InvariantCulture),
            fields[2],
            direction,
            fields[4] == "P",
            fields[5].Length == 0 ? null : byte.Parse(fields[5], CultureInfo.InvariantCulture),
            fields[7].Length == 0 ? [] : fields[7].Split(','),
            fields[6] == "1");
    }

    private static SecsDataItemInfo ReadItem(string[] fields)
    {
        var codes = new SecsDataItemCode[fields.Length - 3];

        for (var i = 3; i < fields.Length; i++)
        {
            var tab = fields[i].IndexOf('\t', StringComparison.Ordinal);
            codes[i - 3] = tab < 0
                ? new SecsDataItemCode(fields[i], string.Empty)
                : new SecsDataItemCode(fields[i][..tab], fields[i][(tab + 1)..]);
        }

        return new SecsDataItemInfo(
            fields[0],
            fields[1],
            SecsFormatExtensions.TryParse(fields[1], out var format) ? format : null,
            fields[2],
            codes);
    }

    private sealed record Loaded(
        IReadOnlyList<SecsStreamInfo> Streams,
        IReadOnlyList<SecsMessageInfo> Messages,
        IReadOnlyList<SecsDataItemInfo> Items,
        FrozenDictionary<byte, SecsStreamInfo> ByStream,
        FrozenDictionary<ushort, SecsMessageInfo> ByMessage,
        FrozenDictionary<string, SecsDataItemInfo> ByItem,
        SecsCatalogProvenance Provenance);
}

/// <summary>Reads a message's catalogue entry straight off the message.</summary>
public static class SecsCatalogExtensions
{
    /// <summary>Describes a message in one line, with its standard name and the W bit.</summary>
    /// <param name="message">The message.</param>
    /// <returns>For example <c>S6F11 W Event Report Send</c>.</returns>
    /// <remarks>
    /// What a log line wants. <see cref="SecsMessage.ToString"/> gives <c>S6F11 W</c>, which is
    /// correct and says nothing about what happened.
    /// </remarks>
    public static string Describe(this SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var name = SecsCatalog.NameOf(message.S, message.F);
        var w = message.ReplyExpected ? " W" : string.Empty;

        return name is null
            ? $"S{message.S}F{message.F}{w}"
            : $"S{message.S}F{message.F}{w} {name}";
    }

    /// <summary>Finds a message's catalogue entry.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The entry, or <see langword="null"/> when the pair is not in the catalogue.</returns>
    public static SecsMessageInfo? Catalogued(this SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return SecsCatalog.TryGetMessage(message.S, message.F, out var info) ? info : null;
    }
}
