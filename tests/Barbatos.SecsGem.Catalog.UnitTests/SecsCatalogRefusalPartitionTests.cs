// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Gem;

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Every primary this repository does not serve, and which measure decides each one.
/// </summary>
/// <remarks>
/// <para>
/// Fifty-nine of the reference's two hundred and seventy-four primaries are not declared anywhere
/// here. <strong>This file is that list, and it is a partition</strong>: every refused primary
/// appears exactly once, under the one measure that decides it. A row with two reasons is filed
/// under the one that would still refuse it if the other were answered - which is the lesson of the
/// refusals that fell, because in every one of those the weaker reason was doing the work.
/// </para>
/// <para>
/// <strong>One measure has been retired, and it held twenty-four of these rows.</strong> It was
/// "what declaring the stream claims": that serving any row of stream 17 or 20 puts the stream in a
/// routing table, so a host probing the rest gets S9F5 rather than S9F3. Reading its twenty-four
/// rows one at a time dissolved it. Every one of them has a reason about its own row - five of
/// stream 17's turn on <c>CJID</c>, which is undefined and addresses every control-job transaction
/// exactly as <c>RCPNS</c> addresses stream 19; eight have their job done already by a
/// better-documented row here, six of them item for item under stream 15's numbers; the rest turn
/// on a name the item measures already decide. The measure
/// could not be stated without a ratio - one row of a stream served almost entirely against one
/// pair of seven - and a refusal here states a name, not a figure.
/// </para>
/// <para>
/// So the two measures that replaced it are both names. <strong>A firmer row does the job</strong>
/// is the one the working agreement had stated for chapters without the partition having a box for
/// it, and it decides eight rows; <strong>the addition is unobservable</strong> decides the ninth,
/// S20F15, whose single transaction buys atomicity that <c>RMACK</c>'s one refusal code cannot
/// report.
/// </para>
/// <para>
/// <strong>What the partition shows at a glance.</strong> Only <strong>five</strong> of the
/// fifty-nine are gap-free, and the figure is measured here rather than asserted: the claim this
/// file used to make - twenty-five refused although every field of them reads - counted reason
/// labels and was never checked against an item. Four of the five gap-free rows are refused because
/// a better-documented row does their job and the fifth for a name whose object appears nowhere
/// else. So the frontier is not "rows nobody has got to"; it is rows whose fields this library
/// cannot honestly put on a wire, and rows that would say a second time what it already says.
/// </para>
/// </remarks>
public class SecsCatalogRefusalPartitionTests
{
    /// <summary>The one measure that decides a refusal.</summary>
    private enum Reason
    {
        /// <summary>The undefined name is what the message is for, so carrying it opaquely leaves the row unreadable.</summary>
        FieldTheRowTurnsOn,

        /// <summary>The undefined field is written by one end and must be understood by the other.</summary>
        ProducerAndReader,

        /// <summary>A rival or a twin gives the name a second reading, and the two put different bytes out.</summary>
        ReadingsDiffer,

        /// <summary>The body's nesting is settled by no served sibling - a list of entries, here.</summary>
        Shape,

        /// <summary>The name's object has no second instance anywhere, and the reply is only an acknowledgement.</summary>
        NameAlone,

        /// <summary>A row this repository already serves does the same job, from a better-documented source.</summary>
        FirmerRow,

        /// <summary>Every field reads and the job is new, but what it adds cannot be seen on the wire.</summary>
        UnobservableAddition,

        /// <summary>One undefined name addresses every transaction of the stream, or of the capability.</summary>
        AddressingMissing,
    }

    /// <summary>
    /// A refused primary, and the name or the row that decides it.
    /// </summary>
    /// <remarks>
    /// <paramref name="Decides"/> is an undefined item name for the four item measures, a
    /// designator like <c>S15F5</c> for <see cref="Reason.FirmerRow"/>, and a phrase for the other
    /// three. Each form is checked by its own test below, which is what keeps an entry here from
    /// being a label somebody typed.
    /// </remarks>
    private sealed record Refusal(byte Stream, byte Function, Reason Reason, string Decides);

    private static readonly Refusal[] Refused =
    [
        // ---------------------------------------------------------- the field the row turns on
        new(2, 19, Reason.FieldTheRowTurnsOn, "RSTC"),
        new(6, 21, Reason.FieldTheRowTurnsOn, "CELLNAME"),
        new(6, 29, Reason.FieldTheRowTurnsOn, "NRED"),
        new(14, 23, Reason.FieldTheRowTurnsOn, "ENABLE"),
        new(15, 33, Reason.FieldTheRowTurnsOn, "RCPSTATE"),
        new(15, 37, Reason.FieldTheRowTurnsOn, "RCPEXIST"),
        new(15, 39, Reason.FieldTheRowTurnsOn, "SPACEAVAIL"),
        new(15, 41, Reason.FieldTheRowTurnsOn, "AUTOSEL"),
        new(15, 45, Reason.FieldTheRowTurnsOn, "CMPRESULT"),

        // A lock status whose lock state is undefined cannot tell locked from unlocked, which is
        // the whole row; OWNERID refuses it too and is filed where it decides something alone.
        new(20, 25, Reason.FieldTheRowTurnsOn, "RCPLOCK"),

        // The same shape as S14F23's ENABLE, one stream over: an enable-or-disable flag nobody
        // defined leaves the library unable to tell the two apart.
        new(20, 29, Reason.FieldTheRowTurnsOn, "NOTIFYENABLE"),

        // And a change report whose kind of change is undefined is a report of nothing in
        // particular.
        new(20, 31, Reason.FieldTheRowTurnsOn, "CHANGETYPE"),

        // S20F27 is S15F33 under an OBJSPEC, and its twin is filed on this measure for this name.
        new(20, 27, Reason.FieldTheRowTurnsOn, "RCPSTATE"),

        // ---------------------------------------------------------- producer and reader
        new(8, 7, Reason.ProducerAndReader, "BLKNUM"),
        new(8, 9, Reason.ProducerAndReader, "BLKNUM"),
        new(13, 9, Reason.ProducerAndReader, "BLKNUM"),
        new(15, 11, Reason.ProducerAndReader, "CHECKSUM"),
        new(15, 49, Reason.ProducerAndReader, "RCPOWNER"),
        new(21, 9, Reason.ProducerAndReader, "BLKSIZE"),
        new(21, 11, Reason.ProducerAndReader, "BLKNUM"),
        new(21, 13, Reason.ProducerAndReader, "BLKNUM"),
        new(21, 19, Reason.ProducerAndReader, "CHKSUM"),

        // S20F19 is S15F11 item for item, so it is refused for S15F11's name. A duplicate of a
        // refused row inherits that row's verdict, and a duplicate of a served one is filed under
        // FirmerRow - which is what stops the partition saying two things about one body.
        new(20, 19, Reason.ProducerAndReader, "CHECKSUM"),

        // An owner the host mints and the machine must parse, which is RCPOWNER's position at
        // S15F49 exactly. LOCKID beside it is the machine's own token and carries opaquely, the
        // way RCPLOCKID does in the pair this repository serves.
        new(20, 21, Reason.ProducerAndReader, "OWNERID"),

        // ---------------------------------------------------------- two readings, different bytes
        new(15, 27, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(15, 29, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(15, 43, Reason.ReadingsDiffer, "VALACK"),
        new(15, 51, Reason.ReadingsDiffer, "RCPATTRVAL"),

        // Stream 20's attribute rows are stream 15's, and carry the same twin: RCPATTRVAL against
        // RCPATTRDATA is a list against text. S20F1 is not a duplicate of the served S15F1,
        // because S15F2 returns namespaces where S20F2 returns namespaces with attributes.
        new(20, 1, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(20, 5, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(20, 7, Reason.ReadingsDiffer, "RCPATTRVAL"),

        // ---------------------------------------------------------- a shape
        new(15, 3, Reason.Shape, "a directory is a list of entries"),
        new(15, 47, Reason.Shape, "a history is a list of entries"),

        // S20F3 is S15F3's row with a lock column added, so it is the same list of entries and is
        // filed where its twin is. Both of its reasons survive the other being answered, and the
        // tie goes to the one its twin already carries.
        new(20, 3, Reason.Shape, "a directory is a list of entries"),

        // ---------------------------------------------------------- a name with no second instance
        new(7, 21, Reason.NameAlone, "Reference"),

        // ---------------------------------------------------------- a firmer row does the job
        //
        // The measure the working agreement had and the partition did not: where two rows do one
        // job, the better-documented source wins. Decides is the row that already does it.
        new(17, 9, Reason.FirmerRow, "S2F23"),
        new(17, 13, Reason.FirmerRow, "S2F23"),
        new(20, 9, Reason.FirmerRow, "S15F5"),
        new(20, 11, Reason.FirmerRow, "S15F7"),
        new(20, 13, Reason.FirmerRow, "S15F9"),
        new(20, 17, Reason.FirmerRow, "S15F13"),
        new(20, 23, Reason.FirmerRow, "S15F25"),
        new(20, 33, Reason.FirmerRow, "S15F15"),

        // ---------------------------------------------------------- the addition is unobservable
        new(20, 15, Reason.UnobservableAddition, "RMACK has one refusal code"),

        // ---------------------------------------------------------- the addressing is missing
        new(19, 1, Reason.AddressingMissing, "RCPNS"),
        new(19, 3, Reason.AddressingMissing, "RCPNS"),
        new(19, 5, Reason.AddressingMissing, "RCPNS"),
        new(19, 7, Reason.AddressingMissing, "RCPNS"),
        new(19, 9, Reason.AddressingMissing, "RCPNS"),
        new(19, 11, Reason.AddressingMissing, "RCPNS"),
        new(19, 13, Reason.AddressingMissing, "RCPNS"),
        new(19, 15, Reason.AddressingMissing, "RCPNS"),
        new(19, 17, Reason.AddressingMissing, "RCPNS"),
        new(19, 19, Reason.AddressingMissing, "RCPNS"),

        // Stream 17's control-job half, on the same measure and for the same kind of name. CJID is
        // undefined and named by seven rows of stream 17 and nothing else, so it addresses every
        // control-job transaction the way RCPNS addresses stream 19's. The stream is not one thing,
        // which is why this measure reaches a capability here and a whole stream there.
        new(17, 1, Reason.AddressingMissing, "CJID"),
        new(17, 3, Reason.AddressingMissing, "CJID"),
        new(17, 5, Reason.AddressingMissing, "CJID"),
        new(17, 11, Reason.AddressingMissing, "CJID"),

        // And the data collection plan, whose address is its whole body.
        new(17, 7, Reason.AddressingMissing, "DCID"),
    ];

    [Fact]
    public void Fifty_nine_of_the_references_primaries_are_not_served_and_each_is_here_once()
    {
        // The arithmetic first, measured off the catalogue rather than carried forward: the
        // reference has 274 primaries and this repository declares 215 of them.
        var primaries = SecsCatalog.Messages.Count(message => message.IsPrimary);

        Assert.Equal(274, primaries);
        Assert.Equal(59, Refused.Length);
        Assert.Equal(215, primaries - Refused.Length);

        // Exactly once each, which is what makes this a partition rather than a list of notes.
        Assert.Equal(
            Refused.Length,
            Refused.Select(refusal => (refusal.Stream, refusal.Function)).Distinct().Count());

        // And every one of them is a real row with a real reply, so a renumbering of the data
        // cannot leave an entry here pointing at nothing.
        foreach (var refusal in Refused)
        {
            Assert.True(
                SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var row),
                $"S{refusal.Stream}F{refusal.Function}");

            Assert.True(row!.IsPrimary);
            Assert.Equal((byte)(refusal.Function + 1), row.ReplyFunction);
            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out _));
        }
    }

    [Fact]
    public void The_fifty_nine_fall_in_eleven_streams_and_three_of_them_are_refused_whole()
    {
        // Per stream, pinned. The three streams nothing here serves at all are 17, 19 and 20 - and
        // 19 and 20 are the two the reference calls Recipe Management beside stream 15.
        Assert.Equal<byte[]>(
            [2, 6, 7, 8, 13, 14, 15, 17, 19, 20, 21],
            [.. Refused.Select(refusal => refusal.Stream).Distinct().Order()]);

        foreach (var (stream, functions) in new (byte Stream, byte[] Functions)[]
        {
            (2, [19]),
            (6, [21, 29]),
            (7, [21]),
            (8, [7, 9]),
            (13, [9]),
            (14, [23]),
            (15, [3, 11, 27, 29, 33, 37, 39, 41, 43, 45, 47, 49, 51]),
            (17, [1, 3, 5, 7, 9, 11, 13]),
            (19, [1, 3, 5, 7, 9, 11, 13, 15, 17, 19]),
            (20, [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33]),
            (21, [9, 11, 13, 19]),
        })
        {
            Assert.Equal<byte[]>(
                functions,
                [.. Refused.Where(refusal => refusal.Stream == stream).Select(refusal => refusal.Function).Order()]);
        }

        // And the other nine streams of the twenty are served whole, which is the figure worth
        // keeping beside the fifty-nine: the refusals are in eleven streams, not spread thin.
        Assert.Equal(20, SecsCatalog.Streams.Count);
        Assert.Equal(9, SecsCatalog.Streams.Count(stream => !Refused.Any(r => r.Stream == stream.Number)));

        // Streams 17, 19 and 20 are refused whole - which used to be described as the place a
        // routing table decides something, and decides nothing: each of their rows is refused for
        // itself, and the three streams come out empty because every row did, not the other way
        // round.
        foreach (byte stream in (byte[])[17, 19, 20])
        {
            Assert.True(SecsCatalog.TryGetStream(stream, out var info));

            Assert.Equal(
                info!.Messages.Count(message => message.IsPrimary),
                Refused.Count(refusal => refusal.Stream == stream));
        }

        // And the counter-example that retired it, which needs no package to see. Stream 15 has
        // twenty-seven primaries and thirteen of them are in this table, so a machine serving
        // stream 15 answers S9F5 for thirteen pairs - which is exactly what declaring one row of
        // stream 17 or 20 was said to claim. HsmsConnection chooses S9F5 over S9F3 on the stream
        // being known, so that is the dispatcher's behaviour and not a reading of it: a measure
        // true of a stream this repository serves cannot be what refuses another.
        Assert.True(SecsCatalog.TryGetStream(15, out var fifteen));
        Assert.Equal(27, fifteen!.Messages.Count(message => message.IsPrimary));
        Assert.Equal(13, Refused.Count(refusal => refusal.Stream == 15));
    }

    [Fact]
    public void Five_of_them_are_gap_free_and_the_measures_divide_the_other_fifty_four()
    {
        // Measured, not labelled. This test used to be called "twenty-five of them carry no field
        // this library cannot read" and counted two reason labels, never looking at an item: of the
        // twenty-four it counted under the retired stream measure, twenty carry an undefined name.
        // Five of the fifty-nine are gap-free and here they are.
        var gapFree = Refused
            .Where(refusal =>
                SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var primary)
                && SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out var reply)
                && primary!.DataItems.Concat(reply!.DataItems).All(name => SecsCatalog.TryGetDataItem(name, out _)))
            .OrderBy(refusal => refusal.Stream)
            .ThenBy(refusal => refusal.Function)
            .Select(refusal => $"S{refusal.Stream}F{refusal.Function}")
            .ToArray();

        Assert.Equal<string[]>(["S7F21", "S17F9", "S17F13", "S20F17", "S20F33"], gapFree);

        // Four of the five are refused because a firmer row does their job and the fifth for a
        // name, so no gap-free row anywhere here is refused on a field or on a shape.
        Assert.Equal(
            4,
            Refused.Count(refusal =>
                refusal.Reason == Reason.FirmerRow
                && gapFree.Contains($"S{refusal.Stream}F{refusal.Function}", StringComparer.Ordinal)));

        foreach (var (reason, count) in new (Reason Reason, int Count)[]
        {
            (Reason.FieldTheRowTurnsOn, 13),
            (Reason.ProducerAndReader, 11),
            (Reason.ReadingsDiffer, 7),
            (Reason.Shape, 3),
            (Reason.NameAlone, 1),
            (Reason.FirmerRow, 8),
            (Reason.UnobservableAddition, 1),
            (Reason.AddressingMissing, 15),
        })
        {
            Assert.Equal(count, Refused.Count(refusal => refusal.Reason == reason));
        }

        // The eight measures account for every row and nothing is left over.
        Assert.Equal(Refused.Length, Enum.GetValues<Reason>().Sum(reason => Refused.Count(r => r.Reason == reason)));

        // And the figure that retired the stream measure, pinned so the paragraph above cannot
        // drift: of the twenty-four rows it held - every primary of streams 17 and 20 - twenty
        // carry an undefined name, so "every field of these rows reads" was wrong about all but
        // four of them.
        var held = Refused.Where(refusal => refusal.Stream is 17 or 20).ToArray();

        Assert.Equal(24, held.Length);
        Assert.Equal(
            20,
            held.Count(refusal => !gapFree.Contains($"S{refusal.Stream}F{refusal.Function}", StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_name_an_item_measure_decides_on_is_undefined_and_carried_by_the_row_it_decides()
    {
        // What makes each entry checkable rather than a label. For the four item measures the
        // deciding name has to be a name the reference never defines, and it has to be in the row
        // it is cited for - either in the primary or in the reply, since a reply's gap refuses the
        // pair just as a primary's does.
        foreach (var refusal in Refused.Where(refusal =>
            refusal.Reason is Reason.FieldTheRowTurnsOn
                or Reason.ProducerAndReader
                or Reason.ReadingsDiffer
                or Reason.AddressingMissing))
        {
            Assert.False(
                SecsCatalog.TryGetDataItem(refusal.Decides, out _),
                $"{refusal.Decides} is defined, so S{refusal.Stream}F{refusal.Function} cannot be refused for it");

            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var primary));
            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out var reply));

            Assert.True(
                primary!.DataItems.Contains(refusal.Decides, StringComparer.Ordinal)
                    || reply!.DataItems.Contains(refusal.Decides, StringComparer.Ordinal),
                $"S{refusal.Stream}F{refusal.Function} does not carry {refusal.Decides}");
        }
    }

    [Fact]
    public void An_addressing_name_is_carried_by_every_row_of_the_capability_it_addresses()
    {
        // The measure that grew by five, and the assertion that stops it growing by taking anybody's
        // word. An addressing name has to be named by nothing outside its own stream, and the pairs
        // it reaches have to be exactly the ones this file files under it - counted by pair rather
        // than by primary, because S19F1 asks for the list of namespaces and carries RCPNS only in
        // its reply.
        foreach (var (name, stream, pairs) in new (string Name, byte Stream, byte[] Pairs)[]
        {
            ("RCPNS", 19, [1, 3, 5, 7, 9, 11, 13, 15, 17, 19]),
            ("CJID", 17, [1, 3, 5, 11]),
            ("DCID", 17, [7]),
        })
        {
            var carrying = SecsCatalog.Messages
                .Where(message => message.DataItems.Contains(name, StringComparer.Ordinal))
                .ToArray();

            Assert.Equal<byte[]>([stream], [.. carrying.Select(message => message.Stream).Distinct()]);

            // A reply's function less one is its primary's, which is pinned next door.
            Assert.Equal<byte[]>(
                pairs,
                [..
                    carrying
                        .Select(message => message.IsPrimary ? message.Function : (byte)(message.Function - 1))
                        .Distinct()
                        .Order(),
                ]);

            Assert.Equal<byte[]>(
                pairs,
                [..
                    Refused
                        .Where(refusal => refusal.Reason == Reason.AddressingMissing && refusal.Decides == name)
                        .Select(refusal => refusal.Function)
                        .Order(),
                ]);
        }

        // Which is where stream 19 and stream 17 differ, and the difference is why this measure
        // says "of the stream, or of the capability": RCPNS reaches every one of stream 19's ten
        // pairs, where CJID reaches four of stream 17's seven because the stream is three
        // capabilities wearing one number.
        Assert.True(SecsCatalog.TryGetStream(19, out var nineteen));
        Assert.True(SecsCatalog.TryGetStream(17, out var seventeen));

        Assert.Equal(10, nineteen!.Messages.Count(message => message.IsPrimary));
        Assert.Equal(7, seventeen!.Messages.Count(message => message.IsPrimary));

        // And the tenth of stream 19 is the one that proves the pair reading rather than weakening
        // it: the request carries nothing and the reply is the list.
        Assert.True(SecsCatalog.TryGetMessage(19, 1, out var namespaceRequest));
        Assert.Empty(namespaceRequest!.DataItems);
    }

    [Fact]
    public void Every_row_refused_to_a_firmer_row_names_a_row_that_exists_and_does_its_job()
    {
        // The new measure, made checkable. The cited row has to be a real primary with a real
        // reply, in a different stream, and it must not itself be refused here - a row cannot be
        // kept out by one this repository does not serve either.
        foreach (var refusal in Refused.Where(refusal => refusal.Reason == Reason.FirmerRow))
        {
            var designator = refusal.Decides.AsSpan(1);
            var split = designator.IndexOf('F');

            var stream = byte.Parse(designator[..split]);
            var function = byte.Parse(designator[(split + 1)..]);

            Assert.True(SecsCatalog.TryGetMessage(stream, function, out var firmer), refusal.Decides);
            Assert.True(firmer!.IsPrimary, refusal.Decides);
            Assert.NotNull(firmer.ReplyFunction);
            Assert.NotEqual(refusal.Stream, stream);

            Assert.DoesNotContain(Refused, other => other.Stream == stream && other.Function == function);
        }

        // Five of the eight are the firmer row's body plus zero or more items, which is the
        // strongest form the relation takes: S20F9, S20F11 and S20F13 add RCPVER to stream 15's
        // download and upload, and S20F17 and S20F33 are item for item the delete and the
        // namespace create.
        foreach (var (refused, firmer) in new (string Refused, string Firmer)[]
        {
            ("S17F9", "S2F23"),
            ("S20F9", "S15F5"),
            ("S20F11", "S15F7"),
            ("S20F13", "S15F9"),
            ("S20F17", "S15F13"),
            ("S20F33", "S15F15"),
        })
        {
            Assert.Subset(
                new HashSet<string>(Items(refused), StringComparer.Ordinal),
                new HashSet<string>(Items(firmer), StringComparer.Ordinal));
        }

        // S17F9 is the sharpest of those - the five items of S2F23 in S2F23's order - and the two
        // that are not containments are named so nobody has to find out by failing an assertion.
        // S17F13 resets a trace, which S2F23 does by asking for no samples; S20F23 unlocks with
        // LOCKID where the served S15F25 unlocks with RCPLOCKID, the machine's own token either
        // way.
        Assert.Equal<string[]>([.. Items("S2F23")], [.. Items("S17F9")]);
        Assert.Equal<string[]>(["TRID"], [.. Items("S17F13")]);
        Assert.Contains("RCPLOCKID", Items("S15F25"));
        Assert.Contains("LOCKID", Items("S20F23"));

        static IReadOnlyList<string> Items(string designator)
        {
            var span = designator.AsSpan(1);
            var split = span.IndexOf('F');

            Assert.True(SecsCatalog.TryGetMessage(
                byte.Parse(span[..split]),
                byte.Parse(span[(split + 1)..]),
                out var row));

            return row!.DataItems;
        }
    }

    [Fact]
    public void The_trace_reset_loses_its_tie_break_to_an_acknowledgement_and_not_to_a_ratio()
    {
        // The row the retired measure was really carrying, and the one place this chapter had to
        // find a name to replace a figure.
        //
        // S17F13 is gap-free and adds a reason: ACKA with ERRCODE and ERRTEXT where the capability
        // this repository already has answers with TIAACK. That is the S1F19 argument, which did
        // let a row in - and the discriminator is not how much of the stream is served. It is that
        // S14F2, the row S1F19 duplicates, carries no acknowledgement item at all, so a failed get
        // there had nothing to say.
        Assert.True(SecsCatalog.TryGetMessage(14, 2, out var getAttributeData));
        Assert.Equal<string[]>(["OBJSPEC", "ATTRID", "ATTRDATA"], [.. getAttributeData!.DataItems]);
        Assert.DoesNotContain("ERRCODE", getAttributeData.DataItems);

        Assert.True(SecsCatalog.TryGetMessage(1, 20, out var attributeData));
        Assert.Contains("ERRCODE", attributeData!.DataItems);
        Assert.Contains("ERRTEXT", attributeData.DataItems);

        // S2F24 does carry one, and in the reading this repository puts on a wire it carries six
        // codes with five distinguishable refusals - E30's, not the two the dictionary shows for an
        // entry the reference says it inferred from the item's name. So S17F13 would add free text
        // over five named refusals, not an answer over silence.
        Assert.True(SecsCatalog.TryGetMessage(2, 24, out var traceAcknowledge));
        Assert.Equal<string[]>(["TIAACK"], [.. traceAcknowledge!.DataItems]);
        Assert.Equal(6, Enum.GetValues<GemTraceAcknowledgement>().Length);
        Assert.Equal((byte)0, (byte)GemTraceAcknowledgement.Accepted);
    }

    [Fact]
    public void Nothing_in_the_one_package_this_project_can_see_declares_any_of_them()
    {
        // The other half of "not served" is asserted package by package - each one has a test that
        // its own leave-outs are unrouted. This project can see Barbatos.SecsGem.Gem, so the four
        // rows of GEM's own streams get the cross-check here rather than on trust.
        List<(byte Stream, byte Function)> declared =
        [
            .. GemEquipment.HandledMessages,
            .. GemEquipment.ProcessProgramMessages,
            .. GemEquipment.FormattedProcessProgramMessages,
            .. GemEquipment.ProcessProgramMatrixMessages,
            .. GemEquipment.SpoolMessages,
            .. GemEquipment.TerminalMessages,
            .. GemEquipment.ServiceProgramMessages,
            .. GemEquipment.TraceMessages,
            .. GemEquipment.LimitMessages,
            .. GemHost.HandledMessages,
        ];

        foreach (var refusal in Refused)
        {
            Assert.DoesNotContain((refusal.Stream, refusal.Function), declared);
        }

        // And the rows that are GEM's own subject matter, named so a sweep can see which four the
        // cross-check actually bites on.
        Assert.Equal<string[]>(
            ["S2F19", "S6F21", "S6F29", "S7F21"],
            [
                .. Refused
                    .Where(refusal => refusal.Stream is 2 or 6 or 7)
                    .Select(refusal => $"S{refusal.Stream}F{refusal.Function}"),
            ]);

        // The two rows the firmer-row measure cites in GEM's own streams are declared, which is
        // what that measure claims: S2F23 is routed here, so S17F9 and S17F13 are kept out by a
        // row this repository actually answers.
        Assert.Contains(((byte)2, (byte)23), declared);
    }
}
