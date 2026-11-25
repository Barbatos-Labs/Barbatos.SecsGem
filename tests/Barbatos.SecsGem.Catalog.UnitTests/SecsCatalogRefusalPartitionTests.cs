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
/// Fifty-six of the reference's two hundred and seventy-four primaries are not declared anywhere
/// here. <strong>This file is that list, and it is a partition</strong>: every refused primary
/// appears exactly once, under the one measure that decides it. A row with two reasons is filed
/// under the one that would still refuse it if the other were answered - which is the lesson of the
/// refusals that fell, because in every one of those the weaker reason was doing the work.
/// </para>
/// <para>
/// It was fifty-nine until the chapter that re-read <strong>"the field the row turns on"</strong>,
/// and S6F29 was the first row to leave: <c>NRED</c> is the flag that row exists to carry, which is what
/// the measure says, and it stands in the slot S2F37's defined <c>BOOLEAN CEED</c> stands in - same
/// two items, same order, same direction, and S2F37 served here. A flag read as a boolean needs no
/// code table to be told from its opposite, so <c>Barbatos.SecsGem.Gem</c> serves the row.
/// </para>
/// <para>
/// <strong>Two measures have been read as measures rather than as labels, and between them they
/// held thirty-eight of these rows.</strong> Both shrank to nothing or nearly nothing, and both for
/// the same reason: they were verdicts about a stream, applied to rows nobody had read one at a
/// time.
/// </para>
/// <para>
/// The first was <strong>"what declaring the stream claims"</strong> - that serving any row of
/// stream 17 or 20 puts the stream in a routing table, so a host probing the rest gets S9F5 rather
/// than S9F3. It held twenty-four rows and is gone. It was true of every partially served stream
/// here, stream 15 included, and it could not be stated without a ratio where a refusal states a
/// name.
/// </para>
/// <para>
/// The second was <strong>"the addressing is missing"</strong>, which held fifteen and holds one.
/// <c>RCPNS</c> is undefined and addresses all ten of stream 19's pairs, and <c>CJID</c> does the
/// same for stream 17's control-job four - both true, and neither a reason, because each has a
/// defined item to be read against. The reference writes <strong>six row names in both stream 15
/// and stream 19</strong>, and in every one of the six <c>RCPNS</c> stands exactly where the
/// defined <c>RCPSPEC</c> stands; <c>CTLJOBID</c> is the dictionary's own control-job identifier,
/// a defined <c>A[n]</c> that no row names. Reading a format off a slot is what stream 8 did for
/// <c>CPBODY</c>, and the working agreement says to check it before inventing anything. Only
/// <c>DCID</c> has neither a twin row nor a definition, and S17F7 is the one row left on the
/// measure.
/// </para>
/// <para>
/// Three measures replaced them and the working agreement had already stated all three.
/// <strong>A firmer row does the job</strong> decides twelve, four of them stream 19 rows that are
/// stream 15's under other numbers; <strong>the addition is unobservable</strong> decides S20F15,
/// whose single transaction buys atomicity that <c>RMACK</c>'s one refusal code cannot report; and
/// <strong>the standard is contradicted</strong> decides stream 17's control-job four, which build
/// a control job from a <c>PJID</c> where E94 builds one from its material.
/// </para>
/// <para>
/// <strong>And a third measure is retired outright: "two readings, different bytes".</strong> It
/// held seven rows and holds none. Its claim was true of <c>RCPATTRVAL</c> - the RCP family's own
/// <c>RCPATTRDATA</c> is a defined <c>A[n]</c> and E39's <c>ATTRDATA</c> a defined <c>L[n]</c> -
/// and it decides nothing wherever this library never has to choose, because <strong>a SECS-II item
/// carries its own format</strong>. The value a host sets at S15F29 comes back at S15F27 as the
/// item it was sent as, so those two are served; the name beside it read off <c>RCPATTRID</c>, a
/// defined <c>A[n]</c> that names no row. Stream 20's pair follows stream 15's to a firmer row,
/// S15F51 and S20F1 move to producer and reader because a namespace attribute has no set and the
/// machine would be minting the value, and S15F43's <c>VALACK</c> moves to the field the row turns
/// on - a code with no values is a number whatever its format, which is the one kind of gap
/// self-description cannot rescue.
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

        /// <summary>The body's nesting is settled by no served sibling - a list of entries, here.</summary>
        Shape,

        /// <summary>The name's object has no second instance anywhere, and the reply is only an acknowledgement.</summary>
        NameAlone,

        /// <summary>A row this repository already serves does the same job, from a better-documented source.</summary>
        FirmerRow,

        /// <summary>Every field reads and the job is new, but what it adds cannot be seen on the wire.</summary>
        UnobservableAddition,

        /// <summary>The row describes a transaction the standard it names does not have.</summary>
        StandardContradicted,

        /// <summary>One undefined name addresses the capability, and nothing anywhere reads it.</summary>
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
        // CELLNAME is the one case in the repository where the slot question has a NEGATIVE answer
        // and that is itself the reason. S6F21 is S6F11 item for item - DATAID, CEID, then a name,
        // then V - so there IS a defined item in the slot: RPTID, a U4. A U4 is not what a thing
        // called CELLNAME can be, so the slot gives a format that contradicts the item's own name,
        // and a reading the row refutes is no reading at all. S6F29's NRED went the other way and
        // is served.
        new(6, 21, Reason.FieldTheRowTurnsOn, "CELLNAME"),
        new(14, 23, Reason.FieldTheRowTurnsOn, "ENABLE"),
        new(15, 33, Reason.FieldTheRowTurnsOn, "RCPSTATE"),
        new(15, 37, Reason.FieldTheRowTurnsOn, "RCPEXIST"),
        new(15, 41, Reason.FieldTheRowTurnsOn, "AUTOSEL"),
        new(15, 45, Reason.FieldTheRowTurnsOn, "CMPRESULT"),

        // A lock status whose lock state is undefined cannot tell locked from unlocked, which is
        // the whole row; OWNERID refuses it too and is filed where it decides something alone.
        new(20, 25, Reason.FieldTheRowTurnsOn, "RCPLOCK"),

        // S20F27 is S15F33 under an OBJSPEC, and its twin is filed on this measure for this name.
        new(20, 27, Reason.FieldTheRowTurnsOn, "RCPSTATE"),

        // Stream 19's validate and status rows report the same undefined state. S19F17 is named
        // identically to S15F43, whose own reason is VALACK - a name S19F18 does not carry - so it
        // is filed on the name it does carry, which is the one its reply turns on.
        // The validate verdict, moved here when the two-readings measure was retired. VALACK has a
        // true rival - VLAACK is an anagram of it and a defined B[1] with two codes - and that is
        // exactly why it is no use: S2F46 names VLAACK and this repository serves that row, so the
        // definition belongs to the variable limit acknowledgement and not to a recipe validate.
        // A rival whose definition the messages themselves use is a modifier and not a spelling.
        // What is left is a code with no meanings, which is the one kind of undefined field a
        // self-describing item cannot rescue: a B[1] whose values nobody wrote down is a number.
        new(15, 43, Reason.FieldTheRowTurnsOn, "VALACK"),
        new(19, 17, Reason.FieldTheRowTurnsOn, "RCPSTATE"),
        new(19, 19, Reason.FieldTheRowTurnsOn, "RCPSTATE"),

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

        // And the third spelling of the recipe checksum. S15F11 is CHECKSUM, S20F19 is CHECKSUM,
        // S19F9 is CKSUM, S21F19 is CHKSUM; the reference defines none of the four, and all four
        // rows are refused for the same reason under four names.
        new(19, 9, Reason.ProducerAndReader, "CKSUM"),

        // The namespace space, moved here from the field measure because the field measure was
        // answerable and this is not. RMSPACE - the RM family's own space, a defined U4 that names
        // no row - gives SPACEAVAIL and SPACETOTAL a format by the route CTLJOBID gives CJID one.
        // What it does not give is a UNIT, and that is fatal in a way stream 21's LENGTH was not:
        // there the store reports a count it was handed and says so, where here the machine mints
        // two numbers the host does arithmetic with - it compares the space left against the size
        // of what it wants to send. A quantity whose unit only one end knows is the producer and
        // reader problem with the field defined.
        new(15, 39, Reason.ProducerAndReader, "SPACEAVAIL"),

        // And the two attribute rows that only REPORT, which is where the retired measure had a
        // point. S15F27 and S15F29 are a matched get and set, so the value is the host's own data
        // stored whole and handed back - and those two are served now. A NAMESPACE attribute has no
        // set anywhere in the reference, so the machine would be minting a value the host must
        // parse, and the two readings RCPATTRVAL has really do have to be chosen between. That is
        // RCPOWNER's position at S15F49, the other way round.
        new(15, 51, Reason.ProducerAndReader, "RCPATTRVAL"),
        new(20, 1, Reason.ProducerAndReader, "RCPATTRVAL"),

        // ---------------------------------------------------------- two readings, different bytes

        // ---------------------------------------------------------- a shape
        new(15, 3, Reason.Shape, "a directory is a list of entries"),
        new(15, 47, Reason.Shape, "a history is a list of entries"),

        // S20F3 is S15F3's row with a lock column added, so it is the same list of entries and is
        // filed where its twin is. Both of its reasons survive the other being answered, and the
        // tie goes to the one its twin already carries.
        new(20, 3, Reason.Shape, "a directory is a list of entries"),

        // Stream 19's directory is S15F3's row under S15F3's own name, so it goes where S15F3
        // goes; and its parameter rows are the same question one noun over. S19F13 names no
        // parameter in the request, so its reply is every parameter the recipe has, and S19F15
        // may be one pair or a list of them - neither settled by a served sibling, because the
        // sibling that plays the same part is S15F27, refused.
        new(19, 3, Reason.Shape, "a directory is a list of entries"),
        new(19, 13, Reason.Shape, "parameters are a list of entries"),
        new(19, 15, Reason.Shape, "parameters are a list of entries"),

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

        // And four of stream 19's, which is where the addressing measure went. The reference writes
        // six row names in both stream 15 and stream 19, and in all six RCPNS stands exactly where
        // the defined RCPSPEC stands - so these are stream 15 rows under other numbers, with the
        // namespace spelled the way the dictionary declines to.
        new(19, 1, Reason.FirmerRow, "S15F1"),
        new(19, 5, Reason.FirmerRow, "S15F9"),
        new(19, 7, Reason.FirmerRow, "S15F7"),
        new(19, 11, Reason.FirmerRow, "S15F13"),

        // Two of stream 20's, and the first of them cites a row this repository served in the very
        // chapter that moved it. S20F29 is a notification enable keyed by a CEID, which is S6F29's
        // two items with the order swapped; S20F31 is a change report keyed by a CEID, which is an
        // event report, and S6F11 carries any event with any reports. Both were filed on an
        // undefined name - NOTIFYENABLE and CHANGETYPE - and both names turned out to be readable:
        // the first off S2F37's and S6F29's defined BOOLEAN, the second off RMCHGTYPE, the RM
        // family's own change type, a defined U1 with four code values that names no row.
        // Stream 20's attribute pair, now that stream 15's is served. S20F7 is S15F29 item for item,
        // reply included; S20F5 adds an RMACK that S15F28 lacks, and that is not the S1F19
        // difference - S1F20 could say WHY where S14F2 said nothing at all, where RMACK has one
        // refusal code and says only THAT, which S15F28 already says with a zero-length value.
        new(20, 5, Reason.FirmerRow, "S15F27"),
        new(20, 7, Reason.FirmerRow, "S15F29"),
        new(20, 29, Reason.FirmerRow, "S6F29"),
        new(20, 31, Reason.FirmerRow, "S6F11"),

        // ---------------------------------------------------------- the addition is unobservable
        new(20, 15, Reason.UnobservableAddition, "RMACK has one refusal code"),

        // ---------------------------------------------------------- the standard is contradicted
        //
        // Stream 17's control-job half. These were filed on CJID for one chapter and the name was
        // not the reason: CTLJOBID is a defined A[n] and the dictionary's own control-job
        // identifier, so a format was there to read. What refuses them is that the rows are not
        // E94. E94 builds a control job from its material - a carrier input specification - and
        // S17F1 builds one from a PJID, which is backwards; the rows are stream 16's with PJ
        // rewritten to CJ; and the only two control-job items the dictionary defines, CTLJOBID and
        // CTLJOBCMD, are named by no row at all. Barbatos.SecsGem.ControlJobs drives E94 over
        // stream 14 as object services, which is how E94 says a control job is created, commanded
        // and read - so a codec for these rows would be this library inventing a wire and calling
        // it E94.
        new(17, 1, Reason.StandardContradicted, "E94 builds a control job from material"),
        new(17, 3, Reason.StandardContradicted, "E94 builds a control job from material"),
        new(17, 5, Reason.StandardContradicted, "E94 builds a control job from material"),
        new(17, 11, Reason.StandardContradicted, "E94 builds a control job from material"),

        // ---------------------------------------------------------- the addressing is missing
        //
        // One row, and it is the measure's whole remaining claim. DCID is the entire body of
        // S17F7, the reference never defines it, no other row anywhere names it, and - unlike
        // RCPNS and CJID, which both turned out to have a defined item in their slot - there is no
        // data collection plan identifier in the dictionary under any spelling.
        new(17, 7, Reason.AddressingMissing, "DCID"),
    ];

    [Fact]
    public void Fifty_nine_of_the_references_primaries_are_not_served_and_each_is_here_once()
    {
        // The arithmetic first, measured off the catalogue rather than carried forward: the
        // reference has 274 primaries and this repository declares 215 of them.
        var primaries = SecsCatalog.Messages.Count(message => message.IsPrimary);

        Assert.Equal(274, primaries);
        Assert.Equal(56, Refused.Length);
        Assert.Equal(218, primaries - Refused.Length);

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
    public void The_fifty_six_fall_in_eleven_streams_and_three_of_them_are_refused_whole()
    {
        // Per stream, pinned. The three streams nothing here serves at all are 17, 19 and 20 - and
        // 19 and 20 are the two the reference calls Recipe Management beside stream 15.
        Assert.Equal<byte[]>(
            [2, 6, 7, 8, 13, 14, 15, 17, 19, 20, 21],
            [.. Refused.Select(refusal => refusal.Stream).Distinct().Order()]);

        foreach (var (stream, functions) in new (byte Stream, byte[] Functions)[]
        {
            (2, [19]),
            (6, [21]),
            (7, [21]),
            (8, [7, 9]),
            (13, [9]),
            (14, [23]),
            (15, [3, 11, 33, 37, 39, 41, 43, 45, 47, 49, 51]),
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
        // twenty-seven primaries and eleven of them are in this table, so a machine serving
        // stream 15 answers S9F5 for eleven pairs - which is exactly what declaring one row of
        // stream 17 or 20 was said to claim. HsmsConnection chooses S9F5 over S9F3 on the stream
        // being known, so that is the dispatcher's behaviour and not a reading of it: a measure
        // true of a stream this repository serves cannot be what refuses another.
        Assert.True(SecsCatalog.TryGetStream(15, out var fifteen));
        Assert.Equal(27, fifteen!.Messages.Count(message => message.IsPrimary));
        Assert.Equal(11, Refused.Count(refusal => refusal.Stream == 15));
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
            (Reason.FieldTheRowTurnsOn, 12),
            (Reason.ProducerAndReader, 15),
            (Reason.Shape, 6),
            (Reason.NameAlone, 1),
            (Reason.FirmerRow, 16),
            (Reason.UnobservableAddition, 1),
            (Reason.StandardContradicted, 4),
            (Reason.AddressingMissing, 1),
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
        // The measure grew to fifteen and is down to one, and this is the assertion that keeps the
        // one honest. An addressing name has to be named by nothing outside its own stream, and the
        // pairs it reaches have to be exactly the ones this file files under it - counted by pair
        // rather than by primary, because a request can carry the name only in its reply.
        foreach (var (name, stream, pairs) in new (string Name, byte Stream, byte[] Pairs)[]
        {
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

        // And the one row left on it is the only one of the three names this measure has held whose
        // slot has no defined item in it. The other two were read off a definition: RCPNS off
        // RCPSPEC, which stands in its place in six identically named rows, and CJID off CTLJOBID,
        // the dictionary's own control-job identifier, defined and named by no row.
        Assert.True(SecsCatalog.TryGetDataItem("RCPSPEC", out var specification));
        Assert.Equal("A", specification!.FormatToken);

        Assert.True(SecsCatalog.TryGetDataItem("CTLJOBID", out var controlJob));
        Assert.Equal("A", controlJob!.FormatToken);
        Assert.True(controlJob.IsNamedByNoMessage);

        // DCID has neither. It is undefined, no row outside stream 17 names it, and - the part that
        // separates it from RCPNS - no row anywhere else carries either of its row names, so there
        // is no twin row to read a slot off the way stream 15's six gave RCPNS one.
        Assert.False(SecsCatalog.TryGetDataItem("DCID", out _));
        Assert.Equal<byte[]>([17], [.. StreamsNaming("DCID")]);

        foreach (var designator in new[] { "S17F7", "S17F8" })
        {
            var name = NameOf(designator);

            Assert.Equal<byte[]>(
                [17],
                [..
                    SecsCatalog.Messages
                        .Where(message => string.Equals(message.Name, name, StringComparison.Ordinal))
                        .Select(message => message.Stream)
                        .Distinct(),
                ]);
        }

        // Which is the whole of what the measure now says: one row, addressed by a name nothing
        // defines and nothing else writes down in any form.
        Assert.Equal(1, Refused.Count(refusal => refusal.Reason == Reason.AddressingMissing));
    }

    [Fact]
    public void The_reference_writes_six_row_names_twice_and_puts_the_defined_namespace_in_both()
    {
        // The finding that emptied the addressing measure, and the one thing in this file that had
        // been asserted and read the other way round. SecsCatalogAnchoringTests records that the
        // dictionary defines RCPSPEC and not RCPNS, and concluded from it that stream 19 has no
        // anchor of its own - which is true, and is a fact about anchoring rather than a reason to
        // refuse a row.
        //
        // Read for a format instead, the same fact says the opposite: the reference writes six row
        // names in BOTH stream 15 and stream 19, and in every one of the six the undefined RCPNS
        // stands in exactly the position the defined RCPSPEC stands in. That is how CPBODY's format
        // was read off PPBODY's slot in stream 8, and the working agreement says to check it before
        // inventing anything. Nobody had checked it here.
        foreach (var (nineteen, fifteen) in new (string Nineteen, string Fifteen)[]
        {
            ("S19F3", "S15F3"),
            ("S19F5", "S15F9"),
            ("S19F7", "S15F7"),
            ("S19F9", "S15F11"),
            ("S19F11", "S15F13"),
            ("S19F17", "S15F43"),
        })
        {
            Assert.Equal(NameOf(nineteen), NameOf(fifteen));
            Assert.Equal(0, Items(nineteen).ToList().IndexOf("RCPNS"));
            Assert.Equal(0, Items(fifteen).ToList().IndexOf("RCPSPEC"));
        }

        // So nothing about the bytes is in doubt: an A[n] either way, in the same slot, and a
        // SECS-II body is positional - item names never travel, so the spelling mislabels a
        // comment. What keeps these rows out is therefore which row does the job, not which name
        // the dictionary happened to define.
        Assert.True(SecsCatalog.TryGetDataItem("RCPSPEC", out var specification));
        Assert.Equal("A", specification!.FormatToken);
        Assert.False(SecsCatalog.TryGetDataItem("RCPNS", out _));

        Assert.Equal<Reason[]>(
            [Reason.FirmerRow, Reason.Shape, Reason.FirmerRow, Reason.FirmerRow, Reason.ProducerAndReader,
             Reason.FirmerRow, Reason.Shape, Reason.Shape, Reason.FieldTheRowTurnsOn, Reason.FieldTheRowTurnsOn],
            [.. Refused.Where(refusal => refusal.Stream == 19).OrderBy(refusal => refusal.Function).Select(refusal => refusal.Reason)]);

        Assert.DoesNotContain(Refused, refusal => refusal.Decides == "RCPNS");
    }

    [Fact]
    public void The_two_readings_measure_is_retired_because_a_value_item_carries_its_own_format()
    {
        // The measure held seven rows and holds none, which is the third stream-or-name-level
        // reading to come apart when somebody read its rows one at a time.
        //
        // Its claim was true: RCPATTRVAL is undefined and has two candidate readings that disagree
        // - the RCP family's own RCPATTRDATA, a defined A[n], against E39's ATTRDATA, a defined
        // L[n] that ten rows name. Both halves are asserted here.
        Assert.False(SecsCatalog.TryGetDataItem("RCPATTRVAL", out _));

        Assert.True(SecsCatalog.TryGetDataItem("RCPATTRDATA", out var familyReading));
        Assert.Equal("A", familyReading!.FormatToken);
        Assert.True(familyReading.IsNamedByNoMessage);

        Assert.True(SecsCatalog.TryGetDataItem("ATTRDATA", out var objectReading));
        Assert.Equal("L", objectReading!.FormatToken);
        Assert.Equal<byte[]>([1, 14, 21], [.. objectReading.Streams]);

        // And it decides nothing where the library never has to choose, because a SECS-II item
        // carries its own format: the value a host sets at S15F29 comes back at S15F27 as the item
        // it was sent as. Those two are served now, and so is the pair's whole family - the name
        // reads off RCPATTRID, a defined A[n] that names no row, the way CJID read off CTLJOBID.
        Assert.True(SecsCatalog.TryGetDataItem("RCPATTRID", out var name));
        Assert.Equal("A", name!.FormatToken);
        Assert.True(name.IsNamedByNoMessage);

        Assert.DoesNotContain(Refused, refusal => refusal is { Stream: 15, Function: 27 or 29 });

        // What survives is the kind of field self-description cannot rescue. S15F43's VALACK is a
        // code, and a code with no values is a number whatever its format: it is filed on the field
        // the row turns on. And the two rows that only report an attribute are filed on producer
        // and reader, because a namespace attribute has no set and the machine would be minting the
        // value.
        Assert.Contains(Refused, refusal => refusal is { Stream: 15, Function: 43, Reason: Reason.FieldTheRowTurnsOn });

        Assert.Equal<byte[]>(
            [51],
            [..
                Refused
                    .Where(refusal => refusal.Stream == 15 && refusal.Decides == "RCPATTRVAL")
                    .Select(refusal => refusal.Function),
            ]);

        Assert.All(
            Refused.Where(refusal => refusal.Decides == "RCPATTRVAL"),
            refusal => Assert.Equal(Reason.ProducerAndReader, refusal.Reason));
    }

    [Fact]
    public void A_flag_in_a_defined_booleans_slot_is_readable_and_a_name_in_a_U4s_slot_is_not()
    {
        // The chapter that re-read "the field the row turns on", in one test: the slot question put
        // to a flag, with an answer each way.
        //
        // S6F29 is S2F37's two items in S2F37's order, travelling the same way, and S2F37 is served
        // here - so NRED stands where the defined BOOLEAN CEED stands, and a boolean tells enable
        // from disable without a code table. That row is served and is not in this table.
        Assert.Equal<string[]>(["NRED", "CEID"], [.. Items("S6F29")]);
        Assert.Equal<string[]>(["CEED", "CEID"], [.. Items("S2F37")]);

        Assert.False(SecsCatalog.TryGetDataItem("NRED", out _));
        Assert.True(SecsCatalog.TryGetDataItem("CEED", out var enabled));
        Assert.Equal("BOOLEAN", enabled!.FormatToken);

        Assert.True(SecsCatalog.TryGetMessage(6, 29, out var notificationEnable));
        Assert.True(SecsCatalog.TryGetMessage(2, 37, out var eventEnable));
        Assert.Equal(eventEnable!.Direction, notificationEnable!.Direction);

        Assert.DoesNotContain(Refused, refusal => refusal is { Stream: 6, Function: 29 });

        // And the other way, which is the one case here where the slot's answer is itself the
        // refusal. S6F21 is S6F11 item for item with a name where the report identifier goes, so
        // there IS a defined item in CELLNAME's slot - RPTID, a U4 - and a U4 is not what a thing
        // called CELLNAME can be. A slot that contradicts the item's own name has told you nothing.
        Assert.Equal<string[]>(["DATAID", "CEID", "CELLNAME", "V"], [.. Items("S6F21")]);
        Assert.Equal<string[]>(["DATAID", "CEID", "RPTID", "V"], [.. Items("S6F11")]);

        Assert.True(SecsCatalog.TryGetDataItem("RPTID", out var reportId));
        Assert.Equal("U4", reportId!.FormatToken);
        Assert.False(SecsCatalog.TryGetDataItem("CELLNAME", out _));

        Assert.Contains(Refused, refusal => refusal is { Stream: 6, Function: 21, Reason: Reason.FieldTheRowTurnsOn });
    }

    [Fact]
    public void The_control_job_rows_are_refused_for_what_they_are_and_not_for_their_identifier()
    {
        // The new measure, and every part of it is in the catalogue rather than in a reading of
        // E94 - which matters, because a measure nothing can check is a measure that drifts.
        //
        // First: the rows are stream 16's with the nouns swapped. Same function numbers, and
        // S17F1's body is a control job identifier and a PROCESS job identifier, which is E94
        // backwards - a control job is built from its material, and process jobs are made from a
        // control job's material rather than the other way round.
        Assert.Equal<string[]>(["CJID", "PJID"], [.. Items("S17F1")]);
        Assert.Equal<string[]>(["PJID", "PPID", "MTRLID"], [.. Items("S16F1")]);

        // Second: the only two control-job items the dictionary defines are named by no row at all,
        // so the half of the reference that writes definitions and the half that writes messages
        // disagree about this standard's own vocabulary.
        foreach (var name in new[] { "CTLJOBID", "CTLJOBCMD" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
            Assert.True(item!.IsNamedByNoMessage, name);
        }

        // And third, the rows this file files under the measure are exactly stream 17's
        // control-job four - not S17F7, which is a data collection plan, and not the two tracing
        // rows, which are E5's own.
        Assert.Equal<byte[]>(
            [1, 3, 5, 11],
            [..
                Refused
                    .Where(refusal => refusal.Reason == Reason.StandardContradicted)
                    .Select(refusal => refusal.Function)
                    .Order(),
            ]);

        Assert.All(
            Refused.Where(refusal => refusal.Reason == Reason.StandardContradicted),
            refusal => Assert.Equal(17, refusal.Stream));
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

        // And stream 19's four, where the containment only holds once the namespace is spelled the
        // way stream 19 spells it - which is the finding, made mechanical. S15F1 and S19F1 are both
        // empty requests, so the relation there is between their replies.
        foreach (var (refused, firmer) in new (string Refused, string Firmer)[]
        {
            ("S19F5", "S15F9"),
            ("S19F7", "S15F7"),
            ("S19F11", "S15F13"),
        })
        {
            Assert.Subset(
                new HashSet<string>(Items(refused), StringComparer.Ordinal),
                new HashSet<string>(
                    Items(firmer).Select(name => name == "RCPSPEC" ? "RCPNS" : name),
                    StringComparer.Ordinal));
        }

        Assert.Empty(Items("S19F1"));
        Assert.Empty(Items("S15F1"));
        Assert.Equal<string[]>(["RCPNS"], [.. Items("S19F2")]);
        Assert.Equal<string[]>(["RCPSPEC"], [.. Items("S15F2")]);
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

        // And the rows that are GEM's own subject matter, named so a sweep can see which three the
        // cross-check actually bites on. It was four until S6F29 went in - and that row is the
        // reason this assertion earns its place: a chapter that serves a row and forgets to take
        // it out of the table is caught here rather than by a reader noticing.
        Assert.Equal<string[]>(
            ["S2F19", "S6F21", "S7F21"],
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

    // ----------------------------------------------------------------- reading the table

    private static SecsMessageInfo Row(string designator)
    {
        var span = designator.AsSpan(1);
        var split = span.IndexOf('F');

        Assert.True(
            SecsCatalog.TryGetMessage(byte.Parse(span[..split]), byte.Parse(span[(split + 1)..]), out var row),
            designator);

        return row!;
    }

    private static IReadOnlyList<string> Items(string designator) => Row(designator).DataItems;

    private static string NameOf(string designator) => Row(designator).Name;

    private static IEnumerable<byte> StreamsNaming(string item) =>
        SecsCatalog.Messages
            .Where(message => message.DataItems.Contains(item, StringComparer.Ordinal))
            .Select(message => message.Stream)
            .Distinct();
}
