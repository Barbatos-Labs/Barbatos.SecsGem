// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog;

/// <summary>Which way a message travels.</summary>
public enum SecsMessageDirection
{
    /// <summary>Host to equipment.</summary>
    HostToEquipment,

    /// <summary>Equipment to host.</summary>
    EquipmentToHost,

    /// <summary>Either way. S1F1 and S1F13 are the common examples.</summary>
    Either,
}

/// <summary>One SECS-II stream: its number and what E5 calls it.</summary>
public sealed class SecsStreamInfo
{
    internal SecsStreamInfo(
        byte number,
        string name,
        IReadOnlyList<SecsMessageInfo> messages,
        SecsStreamProvenance provenance,
        SecsStreamAnchoring anchoring)
    {
        Number = number;
        Name = name;
        Messages = messages;
        Provenance = provenance;
        Anchoring = anchoring;
    }

    /// <summary>Gets the stream number.</summary>
    public byte Number { get; }

    /// <summary>Gets the stream's name, for example <c>Data Collection</c>.</summary>
    public string Name { get; }

    /// <summary>Gets every message in this stream, ordered by function.</summary>
    public IReadOnlyList<SecsMessageInfo> Messages { get; }

    /// <summary>Gets how much of this stream the source behind the catalogue actually documented.</summary>
    /// <remarks>
    /// The question to ask before implementing anything against a stream, and the reason this
    /// property exists: <see cref="Messages"/> presents every entry with the same confidence, and
    /// the entries do not have the same confidence.
    /// </remarks>
    public SecsStreamProvenance Provenance { get; }

    /// <summary>Gets where this stream's firm ground comes from: its own vocabulary or somebody else's.</summary>
    /// <remarks>
    /// <see cref="Provenance"/> says <em>how much</em> of the stream is documented.
    /// <see cref="SecsStreamAnchoring"/> says <em>whose</em> the documented part is, which is a
    /// different question and the one that separated the last five implementations here from the
    /// two refusals.
    /// </remarks>
    public SecsStreamAnchoring Anchoring { get; }

    /// <summary>Returns the stream and its name.</summary>
    /// <returns>For example <c>S6 Data Collection</c>.</returns>
    public override string ToString() => $"S{Number} {Name}";
}

/// <summary>
/// Where one stream's firm ground comes from: items it is the only one to name, or items it shares.
/// </summary>
/// <remarks>
/// <para>
/// The third provenance measure in this package, and the one every chapter of this repository ended
/// up computing by hand. <see cref="SecsStreamProvenance"/> answers "how much of this stream did
/// the source document". <see cref="SecsDataItemInfo.IsNamedByNoMessage"/> answers "does anything
/// use this definition". This answers the question in between: <strong>of the names this stream
/// uses, which are its own and which are borrowed, and are its gaps its own or everybody's?</strong>
/// </para>
/// <para>
/// Four counts, from two independent facts about each name - whether the dictionary defines it, and
/// whether any other stream names it:
/// </para>
/// <list type="table">
/// <item>
/// <term><see cref="OwnAnchors"/></term>
/// <description>defined, and named by this stream alone - the stream's own documented vocabulary</description>
/// </item>
/// <item>
/// <term><see cref="BorrowedAnchors"/></term>
/// <description>defined, and shared with other streams - firm ground, but not this stream's</description>
/// </item>
/// <item>
/// <term><see cref="SharedGaps"/></term>
/// <description>undefined, and named by other streams too - the source's habit rather than this stream's hole</description>
/// </item>
/// <item>
/// <term><see cref="OwnGaps"/></term>
/// <description>undefined, and named by this stream alone - nothing anywhere to read them against</description>
/// </item>
/// </list>
/// <para>
/// <strong>Both halves earned their place.</strong> The gap split is what told stream 13's chapter
/// that <c>BLKNUM</c> was a <em>shared</em> gap - one of seven block-transfer items the source never
/// defined anywhere - while <c>DSDATA</c> was an <em>own</em> gap, a hole in one row of a stream
/// whose siblings' payloads are defined. One of those is a vocabulary to leave alone and the other
/// is a field to carry untouched, and the counts say which is which. The anchor split is what
/// stream 19's chapter needed: that stream has <strong>no own anchor at all</strong>, because the
/// one defined item in its slot - <c>RCPSPEC</c> - belongs to streams 15 and 20, and stream 19
/// calls it <c>RCPNS</c>, which the dictionary does not define.
/// </para>
/// <para>
/// <strong>It is evidence and not a verdict</strong>, which is the same caveat the other two carry
/// and is worth stating because the numbers tempt. Streams 8 and 16 have no own anchor either and
/// both are implemented here: stream 8 because it is stream 7's design and its items read off
/// <c>PPID</c> and <c>PPBODY</c>, and stream 16 because E40 defines the behaviour its rows carry.
/// A stream with no vocabulary of its own needs somewhere else to stand; it does not follow that
/// there is nowhere.
/// </para>
/// </remarks>
public sealed class SecsStreamAnchoring
{
    internal SecsStreamAnchoring(
        byte stream,
        IReadOnlyList<string> ownAnchors,
        IReadOnlyList<string> borrowedAnchors,
        IReadOnlyList<string> sharedGaps,
        IReadOnlyList<string> ownGaps)
    {
        Stream = stream;
        OwnAnchors = ownAnchors;
        BorrowedAnchors = borrowedAnchors;
        SharedGaps = sharedGaps;
        OwnGaps = ownGaps;
    }

    /// <summary>Gets the stream this is about.</summary>
    public byte Stream { get; }

    /// <summary>Gets the defined item names only this stream uses, ordered.</summary>
    /// <remarks>
    /// The stream's own documented vocabulary. Stream 12 has 27 of these and stream 19 has none.
    /// </remarks>
    public IReadOnlyList<string> OwnAnchors { get; }

    /// <summary>Gets the defined item names this stream shares with at least one other, ordered.</summary>
    /// <remarks>
    /// Firm ground that belongs to somebody else - <c>LENGTH</c>, <c>TIME</c>, <c>PPBODY</c> and
    /// the rest. A stream made entirely of these is reusing another stream's vocabulary, which may
    /// be exactly what it is for: stream 17's fourteen borrowed anchors and no own ones is what an
    /// extrapolation looks like counted.
    /// </remarks>
    public IReadOnlyList<string> BorrowedAnchors { get; }

    /// <summary>Gets the undefined item names this stream shares with at least one other, ordered.</summary>
    /// <remarks>
    /// A gap the source has everywhere rather than here - so it says nothing about this stream in
    /// particular. The seven block-transfer items are all of these.
    /// </remarks>
    public IReadOnlyList<string> SharedGaps { get; }

    /// <summary>Gets the undefined item names only this stream uses, ordered.</summary>
    /// <remarks>
    /// The ones with nothing anywhere to read them against: no sibling row, no other stream, no
    /// definition. <c>SSID</c> is one, <c>DSDATA</c> is one, and five of stream 19's six are.
    /// </remarks>
    public IReadOnlyList<string> OwnGaps { get; }

    /// <summary>Gets how many distinct item names this stream's bodies use.</summary>
    /// <remarks>
    /// Empty names are not counted, and that filter is <strong>the one line of this type no test
    /// reaches</strong> - the same line, for the same reason, as the one
    /// <see cref="SecsCatalogProvenance.NamedDataItemCount"/> documents: no row in the current
    /// data has a trailing comma in its item list, and the reader turns an empty item field into
    /// no items rather than one empty one. It stays because a refreshed reference with such a row
    /// would otherwise report <c>""</c> as a name a stream uses, and put it in a bucket.
    /// </remarks>
    public int DataItemCount => OwnAnchors.Count + BorrowedAnchors.Count + SharedGaps.Count + OwnGaps.Count;

    /// <summary>Gets a value indicating whether the stream has no defined item of its own.</summary>
    /// <remarks>
    /// True for six streams: 8, 15, 16, 17, 19 and 20. Read it as "this stream has to stand on
    /// somebody else's vocabulary, so find out whose" - two of the six are implemented here.
    /// </remarks>
    public bool HasNoOwnVocabulary => OwnAnchors.Count == 0;

    /// <summary>Returns the four counts in one line.</summary>
    /// <returns>For example <c>S19 5 borrowed anchor(s), 0 own, 1 shared gap(s), 5 own</c>.</returns>
    public override string ToString()
        => $"S{Stream} {BorrowedAnchors.Count} borrowed anchor(s), {OwnAnchors.Count} own, "
            + $"{SharedGaps.Count} shared gap(s), {OwnGaps.Count} own";
}

/// <summary>
/// A stream name that more than one stream in the catalogue carries.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one in the current data, and finding it is why this type exists:
/// <strong>streams 15 and 19 are both called "Recipe Management"</strong>. Twenty streams, one
/// collision.
/// </para>
/// <para>
/// A shared name is not in itself a fault - a reference may well describe one subject across two
/// streams. What makes this one worth a type is what the rest of the measurement found underneath
/// it: the two streams use <em>all twenty of the same function numbers</em>, six of them for the
/// same transaction and fourteen shifted, so S19F11 is a recipe delete where S15F11 is a recipe
/// verify. Neither stream has a single documented row. A library that picked one and guessed would
/// delete a recipe when a host asked to check one, which is the sharpest reason this repository has
/// found for leaving a stream alone.
/// </para>
/// </remarks>
public sealed class SecsStreamNameClash
{
    internal SecsStreamNameClash(string name, IReadOnlyList<byte> streams)
    {
        Name = name;
        Streams = streams;
    }

    /// <summary>Gets the name the streams share.</summary>
    /// <remarks>
    /// Names are matched without regard to case, which is the one comparison in this repository
    /// that is not ordinal: every other one is over an identifier, where <c>PUMP</c> and
    /// <c>pump</c> are two things, and this one is over prose from a reference's table of
    /// contents. The current data cannot show the difference - its one clash is an exact match -
    /// so this is a line no test reaches, kept for the reading rather than for the data.
    /// </remarks>
    public string Name { get; }

    /// <summary>Gets the streams carrying it, ordered.</summary>
    public IReadOnlyList<byte> Streams { get; }

    /// <summary>Returns the name and the streams.</summary>
    /// <returns>For example <c>"Recipe Management" is S15 and S19</c>.</returns>
    public override string ToString()
        => $"\"{Name}\" is {string.Join(" and ", Streams.Select(stream => $"S{stream}"))}";
}

/// <summary>
/// How much of one stream the source behind this catalogue actually documented.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> The catalogue is an index into SEMI E5 built from a public
/// reference, and that reference marks most of its entries as a representative subset rather than
/// a definition. Asking a stream how firm it is, before writing a codec against it, is the whole
/// use: a body you can check against a dictionary entry is a different thing from a name somebody
/// extrapolated.
/// </para>
/// <para>
/// <strong>What it is not.</strong> It is not a verdict. Nine of this catalogue's twenty streams
/// have no definitions at all, and two of them - stream 3 and stream 16 - are implemented in this
/// repository anyway, because they survived a check against something else: the items their
/// bodies name, and the behaviour the standards they belong to describe. So
/// <see cref="IsEntirelyRepresentative"/> means "check this against something before you trust
/// it", not "do not implement it". The one stream here that failed such a check is stream 17 -
/// see the package README.
/// </para>
/// </remarks>
public sealed class SecsStreamProvenance
{
    internal SecsStreamProvenance(
        byte stream,
        int messageCount,
        int representativeCount,
        IReadOnlyList<string> undefinedDataItems)
    {
        Stream = stream;
        MessageCount = messageCount;
        RepresentativeCount = representativeCount;
        UndefinedDataItems = undefinedDataItems;
    }

    /// <summary>Gets the stream this is about.</summary>
    public byte Stream { get; }

    /// <summary>Gets how many messages the catalogue has for the stream.</summary>
    public int MessageCount { get; }

    /// <summary>Gets how many of them the source marked as a representative subset.</summary>
    /// <remarks>See <see cref="SecsMessageInfo.IsRepresentative"/>, which is the per-message flag this counts.</remarks>
    public int RepresentativeCount { get; }

    /// <summary>Gets how many of them the source presented as definitions.</summary>
    public int DefinedCount => MessageCount - RepresentativeCount;

    /// <summary>Gets the item names this stream's bodies use that the dictionary does not define, ordered.</summary>
    /// <remarks>
    /// The second half of the question, and often the more telling one: a stream whose bodies name
    /// items the same catalogue defines can be read against them, and a stream whose key items
    /// appear nowhere else cannot be read against anything. Stream 17's control job rows name
    /// <c>CJID</c>, <c>CJACK</c> and <c>CJSTATE</c>, none of which the dictionary has - while the
    /// two control job items it does define, <c>CTLJOBID</c> and <c>CTLJOBCMD</c>, are named by no
    /// message in the catalogue at all.
    /// </remarks>
    public IReadOnlyList<string> UndefinedDataItems { get; }

    /// <summary>Gets a value indicating whether the source documented none of this stream.</summary>
    /// <remarks>
    /// <para>
    /// True for nine streams: 3, 4, 15, 16, 17, 18, 19, 20 and 21. Read it as "check this against
    /// something else", not as a refusal - the class remarks say why.
    /// </para>
    /// <para>
    /// The <see cref="MessageCount"/> term is the one line of this type no test reaches, and it is
    /// here deliberately: every stream the catalogue lists has messages, so a stream of none
    /// cannot be built through the public surface to assert against. Without the term an empty
    /// stream would answer <see langword="true"/>, and there is nothing representative about
    /// having documented nothing because there was nothing to document.
    /// </para>
    /// </remarks>
    public bool IsEntirelyRepresentative => MessageCount > 0 && RepresentativeCount == MessageCount;

    /// <summary>Returns the counts in one line.</summary>
    /// <returns>For example <c>S16 28 message(s), 0 defined, 5 undefined item name(s)</c>.</returns>
    public override string ToString()
        => $"S{Stream} {MessageCount} message(s), {DefinedCount} defined, "
            + $"{UndefinedDataItems.Count} undefined item name(s)";
}

/// <summary>
/// How much of the catalogue is anchored to the rest of it, counted once for the whole table.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SecsStreamProvenance"/> answers "how firm is this stream", which is the question to
/// ask before implementing against one. This answers a different one, and it cannot be assembled
/// out of the per-stream measures: whether a definition is named by <em>any</em> message is a fact
/// about the whole table, and so is the full list of names used with no definition behind them.
/// </para>
/// <para>
/// The numbers are worth knowing before quoting this reference at anybody. Of 375 item
/// definitions, <strong>213 are named by no message</strong>; of the 236 item names messages
/// actually use, <strong>74 have no definition</strong>. So 162 items are both defined and used,
/// and both unanchored sets are larger than the anchored one. Every message count here is the
/// same arithmetic <see cref="SecsStreamProvenance"/> does per stream.
/// </para>
/// </remarks>
public sealed class SecsCatalogProvenance
{
    internal SecsCatalogProvenance(
        int streamCount,
        int messageCount,
        int representativeCount,
        int dataItemCount,
        int namedDataItemCount,
        IReadOnlyList<string> undefinedDataItems,
        IReadOnlyList<string> unusedDefinitions,
        IReadOnlyList<SecsStreamNameClash> streamNameClashes)
    {
        StreamCount = streamCount;
        MessageCount = messageCount;
        RepresentativeCount = representativeCount;
        DataItemCount = dataItemCount;
        NamedDataItemCount = namedDataItemCount;
        UndefinedDataItems = undefinedDataItems;
        UnusedDefinitions = unusedDefinitions;
        StreamNameClashes = streamNameClashes;
    }

    /// <summary>Gets the stream names that more than one stream carries, ordered.</summary>
    /// <remarks>
    /// One entry in the current data - "Recipe Management", which is both stream 15 and stream 19 -
    /// and it is the headline of the one refusal this measure was added for. See
    /// <see cref="SecsStreamNameClash"/> for what else was underneath it.
    /// </remarks>
    public IReadOnlyList<SecsStreamNameClash> StreamNameClashes { get; }

    /// <summary>Gets how many streams the catalogue has.</summary>
    public int StreamCount { get; }

    /// <summary>Gets how many messages it has.</summary>
    public int MessageCount { get; }

    /// <summary>Gets how many of those the source marks as a representative subset rather than a definition.</summary>
    public int RepresentativeCount { get; }

    /// <summary>Gets how many it documents as definitions.</summary>
    public int DefinedCount => MessageCount - RepresentativeCount;

    /// <summary>Gets how many items the dictionary defines.</summary>
    public int DataItemCount { get; }

    /// <summary>Gets how many distinct item names the messages use.</summary>
    /// <remarks>
    /// <para>
    /// Not a subset of <see cref="DataItemCount"/> and not a superset: the two overlap by 162.
    /// </para>
    /// <para>
    /// Empty names are not counted, and that filter is <strong>one of the two lines here no test
    /// reaches</strong>: no row in the current data has a trailing comma in its item list, so
    /// there is no empty name to drop. It stays because a refreshed reference with one would
    /// otherwise report <c>""</c> as a name a message uses, and
    /// <see cref="SecsCatalog.TryGetDataItem"/> would then be asked for an item called nothing.
    /// </para>
    /// </remarks>
    public int NamedDataItemCount { get; }

    /// <summary>Gets the item names messages use that the dictionary does not define, ordered.</summary>
    /// <remarks>
    /// The union of every stream's <see cref="SecsStreamProvenance.UndefinedDataItems"/>, and a
    /// test asserts it is exactly that. <c>PORTSTATUS</c>, <c>SLOTMAP</c> and <c>CARRIERSTATUS</c>
    /// are in here, which is why the packages that carry them say what each reading was checked
    /// against.
    /// </remarks>
    public IReadOnlyList<string> UndefinedDataItems { get; }

    /// <summary>Gets the definitions no message names, ordered.</summary>
    /// <remarks>
    /// <para>
    /// The other direction, and the one nothing else in this package could ask. See
    /// <see cref="SecsDataItemInfo.IsNamedByNoMessage"/> for what it does and does not prove.
    /// </para>
    /// <para>
    /// The ordering here is <strong>the second line no test reaches</strong>: the reference lists
    /// its dictionary alphabetically already, so sorting this list again cannot be observed from
    /// the current data. The assertion is still sound rather than vacuous - removing the same sort
    /// from <see cref="UndefinedDataItems"/> does fail it, because that list comes out of a hash
    /// set and arrives in no order at all.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> UnusedDefinitions { get; }

    /// <summary>Returns the counts.</summary>
    /// <returns>For example <c>20 streams, 524 messages (405 representative), 375 items: 213 unused, 74 undefined</c>.</returns>
    public override string ToString()
        => $"{StreamCount} streams, {MessageCount} messages ({RepresentativeCount} representative), "
            + $"{DataItemCount} items: {UnusedDefinitions.Count} unused, {UndefinedDataItems.Count} undefined";
}

/// <summary>One stream and function: what it is called, which way it goes, and what its body holds.</summary>
public sealed class SecsMessageInfo
{
    internal SecsMessageInfo(
        byte stream,
        byte function,
        string name,
        SecsMessageDirection direction,
        bool isPrimary,
        byte? replyFunction,
        IReadOnlyList<string> dataItems,
        bool isRepresentative)
    {
        Stream = stream;
        Function = function;
        Name = name;
        Direction = direction;
        IsPrimary = isPrimary;
        ReplyFunction = replyFunction;
        DataItems = dataItems;
        IsRepresentative = isRepresentative;
    }

    /// <summary>Gets the stream.</summary>
    public byte Stream { get; }

    /// <summary>Gets the function.</summary>
    public byte Function { get; }

    /// <summary>Gets the message's name, for example <c>Event Report Send</c>.</summary>
    public string Name { get; }

    /// <summary>Gets which way the message travels.</summary>
    public SecsMessageDirection Direction { get; }

    /// <summary>Gets a value indicating whether this is a primary - an odd function.</summary>
    public bool IsPrimary { get; }

    /// <summary>Gets the function that answers this one, when there is one.</summary>
    /// <remarks>
    /// Present on a primary that has a defined reply and on a secondary pointing back at its
    /// primary. A Stream 9 report has none, which is why it is posted rather than sent.
    /// </remarks>
    public byte? ReplyFunction { get; }

    /// <summary>Gets the data item names the body uses, in the order they appear.</summary>
    /// <remarks>
    /// A list of names, <em>not</em> a structural template: it does not record nesting, repetition
    /// or which items are optional. It answers "what appears in an S2F41 body" and not "what shape
    /// is an S2F41 body", and a reader that treated it as the second would build the wrong message.
    /// </remarks>
    public IReadOnlyList<string> DataItems { get; }

    /// <summary>Gets a value indicating whether the source marked this entry as a representative subset.</summary>
    /// <remarks>
    /// <para>
    /// True for 405 of the 524 entries, concentrated in the higher streams. The reference this
    /// catalogue was built from flags those itself, and the flag is carried through rather than
    /// dropped: a catalogue that presented every entry with equal confidence would be claiming more
    /// than it knows.
    /// </para>
    /// <para>
    /// Treat a representative entry as a starting point for reading SEMI E5, not as a definition to
    /// implement against.
    /// </para>
    /// </remarks>
    public bool IsRepresentative { get; }

    /// <summary>Returns the designator and the name.</summary>
    /// <returns>For example <c>S6F11 Event Report Send</c>.</returns>
    public override string ToString() => $"S{Stream}F{Function} {Name}";
}

/// <summary>One standard value a coded data item may carry.</summary>
public sealed class SecsDataItemCode
{
    internal SecsDataItemCode(string value, string meaning)
    {
        Value = value;
        Meaning = meaning;
    }

    /// <summary>Gets the value, as text - sometimes a range such as <c>7-63</c>.</summary>
    public string Value { get; }

    /// <summary>Gets what the value means.</summary>
    public string Meaning { get; }

    /// <summary>Returns the value and its meaning.</summary>
    /// <returns>For example <c>0 = Accepted</c>.</returns>
    public override string ToString() => $"{Value} = {Meaning}";
}

/// <summary>One data item: its name, the format it is carried in, and any standard code values.</summary>
public sealed class SecsDataItemInfo
{
    internal SecsDataItemInfo(
        string name,
        string formatToken,
        SecsFormat? format,
        string length,
        IReadOnlyList<SecsDataItemCode> codes,
        bool isNamedByNoMessage = false,
        IReadOnlyList<byte>? streams = null)
    {
        Name = name;
        FormatToken = formatToken;
        Format = format;
        Length = length;
        Codes = codes;
        IsNamedByNoMessage = isNamedByNoMessage;
        Streams = streams ?? [];
    }

    /// <summary>Gets the streams whose messages name this item, ordered. Empty when none do.</summary>
    /// <remarks>
    /// <para>
    /// The most-reused measurement in this repository's history, finally a property. Before
    /// implementing against an item it is the first thing worth knowing: <c>GRANT</c> is named by
    /// seven messages across <strong>six</strong> streams, which is why two packages here serve it
    /// and a test makes them agree; <c>DATAID</c> is named across eight, which gave stream 13 a
    /// route for an item its own dictionary entry could not settle; and <c>SSID</c> and
    /// <c>RCPNS</c> are named by <strong>one</strong> stream each, which is why one needed a type
    /// that carries both readings and the other helped refuse a stream.
    /// </para>
    /// <para>
    /// Empty exactly when <see cref="IsNamedByNoMessage"/> is true, and a test asserts the two
    /// agree item for item rather than trusting that they must.
    /// </para>
    /// </remarks>
    public IReadOnlyList<byte> Streams { get; }

    /// <summary>Gets the item's name, for example <c>COMMACK</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the format as the reference writes it, which is not always an E5 token.</summary>
    public string FormatToken { get; }

    /// <summary>Gets the E5 format, when <see cref="FormatToken"/> names one.</summary>
    /// <remarks>
    /// <para>
    /// Nullable as a contract rather than because the current data needs it: every one of the 375
    /// items carries a real E5 token, so this is never <see langword="null"/> today. It stays
    /// nullable because a refreshed reference could give something that is not a single format -
    /// a choice of several, or a note - and <see cref="FormatToken"/> would still carry it.
    /// </para>
    /// <para>
    /// A test asserts both halves: that nothing is untyped, and that the typing agrees with
    /// <c>SecsFormatExtensions.TryParse</c> item for item.
    /// </para>
    /// </remarks>
    public SecsFormat? Format { get; }

    /// <summary>Gets the length as the reference writes it: a count, <c>n</c>, or an empty string.</summary>
    /// <remarks>
    /// Empty for 58 of the 375 items - <c>SVID</c> and <c>CEID</c> among them - where the
    /// reference documents no length at all. Empty means "not stated", not "zero".
    /// </remarks>
    public string Length { get; }

    /// <summary>Gets the standard values this item may carry, or an empty list when it is not coded.</summary>
    public IReadOnlyList<SecsDataItemCode> Codes { get; }

    /// <summary>Gets a value indicating whether this item has a table of standard values.</summary>
    public bool IsCoded => Codes.Count > 0;

    /// <summary>Gets a value indicating whether no message in the catalogue names this item.</summary>
    /// <remarks>
    /// <para>
    /// True for <strong>213 of the 375</strong> definitions, which is the dictionary's half of the
    /// provenance question and the half no per-stream measure can show: an item nothing names is
    /// invisible from every stream at once. <see cref="SecsStreamProvenance.UndefinedDataItems"/>
    /// asks the other direction - names a message uses with no definition behind them - and the
    /// two together say how much of this reference is anchored to the rest of it. Only 162
    /// definitions are both defined and named.
    /// </para>
    /// <para>
    /// <strong>It is evidence, not a verdict.</strong> A definition no message names may be an
    /// item this particular reference documented without documenting its transactions, and
    /// several are exactly that: <c>CARRIERSPEC</c>, <c>CATTRID</c>, <c>CATTRDATA</c>,
    /// <c>PORTACTION</c> and <c>PODID</c> are E87's object items, and E87 is implemented here on
    /// other grounds. What it does rule out is reading such an item <em>in context</em>, because
    /// the reference supplies no context: there is no message to see its neighbours in, and no
    /// direction, and no reply. That is what sank a storage service standard in this repository -
    /// <c>LOCID</c> and <c>SLOTID</c> are defined, and nothing names either.
    /// </para>
    /// </remarks>
    public bool IsNamedByNoMessage { get; }

    /// <summary>Finds the meaning of a value.</summary>
    /// <param name="value">The value, as text.</param>
    /// <returns>The meaning, or <see langword="null"/> when the item is not coded or the value is not listed.</returns>
    /// <remarks>Exact match only: a value inside a documented range such as <c>7-63</c> is not found.</remarks>
    public string? MeaningOf(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var code in Codes)
        {
            if (string.Equals(code.Value, value, StringComparison.Ordinal))
            {
                return code.Meaning;
            }
        }

        return null;
    }

    /// <summary>Returns the name and format.</summary>
    /// <returns>For example <c>COMMACK B[1]</c>.</returns>
    public override string ToString() => $"{Name} {FormatToken}[{Length}]";
}
