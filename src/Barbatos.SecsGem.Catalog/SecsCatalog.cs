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
/// a flag carried through as <see cref="SecsMessageInfo.IsRepresentative"/>. Of the 39 messages
/// this repository implements, 38 matched it exactly; the one that did not, the S6F11 and S6F12
/// pair, was corrected from the implementation and is covered by a test that fails if the two ever
/// disagree again. Treat this as a good index into E5, not as a substitute for it.
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

        foreach (var (number, name) in streamNames.OrderBy(pair => pair.Key))
        {
            var ofStream = messages.Where(m => m.Stream == number).OrderBy(m => m.Function).ToArray();

            byStream.Add(new SecsStreamInfo(
                number,
                name,
                ofStream,
                new SecsStreamProvenance(
                    number,
                    ofStream.Length,
                    ofStream.Count(m => m.IsRepresentative),
                    [.. ofStream
                        .SelectMany(m => m.DataItems)
                        .Where(item => !defined.Contains(item))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)])));
        }

        return new Loaded(
            byStream,
            messages.OrderBy(m => m.Stream).ThenBy(m => m.Function).ToArray(),
            items.OrderBy(i => i.Name, StringComparer.Ordinal).ToArray(),
            byStream.ToFrozenDictionary(s => s.Number),
            messages.ToFrozenDictionary(m => Key(m.Stream, m.Function)),
            items.ToFrozenDictionary(i => i.Name, StringComparer.Ordinal));
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
        FrozenDictionary<string, SecsDataItemInfo> ByItem);
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
