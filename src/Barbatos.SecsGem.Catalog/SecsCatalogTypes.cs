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
        SecsStreamProvenance provenance)
    {
        Number = number;
        Name = name;
        Messages = messages;
        Provenance = provenance;
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

    /// <summary>Returns the stream and its name.</summary>
    /// <returns>For example <c>S6 Data Collection</c>.</returns>
    public override string ToString() => $"S{Number} {Name}";
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
        IReadOnlyList<SecsDataItemCode> codes)
    {
        Name = name;
        FormatToken = formatToken;
        Format = format;
        Length = length;
        Codes = codes;
    }

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
