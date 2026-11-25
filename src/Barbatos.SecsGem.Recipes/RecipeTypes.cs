// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// The answer to every primary this package sends - <c>RMACK</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Quoted, and that is worth saying after three chapters of inference.</strong>
/// <c>RMACK</c> has a dictionary entry: a <c>B[1]</c> with 0 meaning accepted, 1 meaning rejected,
/// and 2 to 63 reserved. Streams 13, 8 and 21 all had to read a zero off something else -
/// <c>ACKC8</c>, <c>ACKC21</c>, <c>ABTACK</c> and <c>DELACK</c> have no tables at all. This one
/// does, and it covers every one of the nineteen pairs this package implements.
/// </para>
/// <para>
/// <strong>So <see cref="Other"/> is honest here where it would not have been there.</strong> The
/// table names a reserved range, which means the reference anticipated codes it did not define -
/// exactly the shape <c>GRANT</c> has, and exactly why <c>ItemTransferGrant</c> has an
/// <c>Other</c> and <c>ItemAcknowledgement</c> deliberately does not. A third member is a
/// distinction the source makes, not one this library invented.
/// </para>
/// <para>
/// There is still only <em>one</em> refusal, so a rejected recipe comes back with no reason. The
/// rows that would have carried one - the verify data's <c>VFACK</c>, the validate data's
/// <c>VALACK</c> - are among the eight this package leaves out.
/// </para>
/// </remarks>
public enum RecipeAcknowledgement : byte
{
    /// <summary>0 - accepted.</summary>
    Accepted = 0,

    /// <summary>1 - rejected, or an error; the item has no way to say which.</summary>
    Rejected = 1,

    /// <summary>
    /// 2 - anything in the reserved range, which the table names and does not define.
    /// </summary>
    /// <remarks>
    /// Codes 2 to 63 are "reserved / equipment specific". A machine that sends one has said
    /// something this reference cannot interpret, so it reads as a refusal and the code goes to
    /// the log. The value is 2 because that is where the reserved range starts.
    /// </remarks>
    Other = 2,
}

/// <summary>
/// One recipe: which namespace it is in, what it is called, and its body.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The body is plain bytes</strong>, because stream 15 carries it in <c>PPBODY</c> - stream
/// 7's own process-program body, a defined <c>B</c>. That is firmer ground than any payload in this
/// repository has had: stream 8 had to read <c>CPBODY</c> off <c>PPBODY</c>'s <em>slot</em> in a
/// copied design, and streams 13 and 21 could read nothing off anything and carry an encoded item
/// instead. Here the item <em>is</em> <c>PPBODY</c>, in a row whose siblings in stream 7 the
/// reference documented.
/// </para>
/// <para>
/// So a recipe here is the same shape as a control program and not the same shape as a data set or
/// a transferred item - the thing most easily got wrong by anybody using more than one of these
/// four packages.
/// </para>
/// </remarks>
public sealed class Recipe
{
    /// <summary>Gets which namespace it lives in - <c>RCPSPEC</c>.</summary>
    public required string Namespace { get; init; }

    /// <summary>Gets what it is called - <c>RCPID</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the body - <c>PPBODY</c>, a defined <c>B</c>.</summary>
    /// <remarks>Plain bytes, not an encoded item. See the type's remarks.</remarks>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Gets whether there is no body.</summary>
    /// <remarks>
    /// An honest question here, unlike in <c>ItemPayload</c>: the bytes are the recipe, so an empty
    /// body is an empty recipe. No <c>Count</c> is needed to tell the two apart.
    /// </remarks>
    public bool IsEmpty => Body.IsEmpty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The namespace, the identifier and the size.</returns>
    public override string ToString() => $"{Namespace}/{Id} ({Body.Length} bytes)";
}

/// <summary>
/// What S15F5 asks permission for.
/// </summary>
/// <remarks>
/// The cheap place to say no: after the grant, the next message is the whole recipe. Unlike stream
/// 8's load inquire, this one <strong>names what is coming</strong> - S8F1 carries only a
/// <c>LENGTH</c>, because a machine has one boot program and it is anonymous, where a recipe has a
/// namespace and an identifier.
/// </remarks>
public sealed class RecipeDownloadRequest
{
    /// <summary>Gets which namespace it is going into - <c>RCPSPEC</c>.</summary>
    public required string Namespace { get; init; }

    /// <summary>Gets what it will be called - <c>RCPID</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets how large the sender says it is - <c>LENGTH</c>.</summary>
    /// <remarks>
    /// <strong>And this one can be checked against what arrives</strong>, because <c>PPBODY</c> is
    /// a <c>B</c> and therefore has a byte count. <c>Barbatos.SecsGem.ControlPrograms</c> can do
    /// the same and for the same reason; <c>Barbatos.SecsGem.DataSets</c> and
    /// <c>Barbatos.SecsGem.ItemTransfer</c> cannot, because their payloads have no format to
    /// measure.
    /// </remarks>
    public uint Length { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The namespace, the identifier and the declared length.</returns>
    public override string ToString() => $"{Namespace}/{Id}, {Length} bytes declared";
}

/// <summary>
/// A lock on a recipe - <c>RCPLOCKID</c> - as the encoded item that carried it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type exists because <c>RCPLOCKID</c> is undefined and is a spelling of
/// nothing.</strong> The other three names this package carries without a definition each have a
/// base name sitting beside them in the same body - <c>RCPIDNEW</c> and <c>RCPIDDST</c> extend
/// <c>RCPID</c>, <c>RCPSPECDST</c> extends <c>RCPSPEC</c> - so their format is read off an item in
/// the same message. This one has no base, no anagram and no stem anywhere in the reference, so
/// there is nothing to read it off and it is carried as the item it arrived in. That is what
/// <c>Barbatos.SecsGem.DataSets</c> does with <c>DSDATA</c> and
/// <c>Barbatos.SecsGem.ItemTransfer</c> with <c>ITEMDATA</c>.
/// </para>
/// <para>
/// <strong>And carrying it opaquely loses nothing here, which is the plainest case of it in the
/// package.</strong> A lock identifier is minted by the machine in S15F24 and handed straight back
/// to it in S15F25: <em>the end that has to read it is the end that wrote it</em>, so nothing in
/// between needs to know what it is.
/// </para>
/// <para>
/// This used to end "which is why the lock went in where S15F49's owner did not", and the owner is
/// served now. An owner does travel the other way, and its rival <c>RCPOWCODE</c> is a
/// <c>BOOLEAN</c> where a name would be text - two readings, different bytes. What that argument
/// was missing is that <strong>nothing reads an owner</strong>: <c>RCPOWNER</c> is named by exactly
/// one row in the reference, so there is no message anywhere whose parser the format would have to
/// satisfy, and the store keeps whichever item arrived. The lock is still the cleaner case, because
/// the end that reads it is named in the standard rather than absent from it.
/// </para>
/// </remarks>
public sealed class RecipeLockToken
{
    private RecipeLockToken(ReadOnlyMemory<byte> encoded, SecsFormat? format, int? count)
    {
        Encoded = encoded;
        Format = format;
        Count = count;
    }

    /// <summary>Gets the encoded item: header and payload, as it crossed the wire.</summary>
    /// <remarks>Empty for a lock reply that carried no token, which is what a refused lock looks like.</remarks>
    public ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>Gets the format of the item that arrived, when it is known.</summary>
    /// <remarks>
    /// <see langword="null"/> only for a token built from bytes by <see cref="FromEncoded"/>
    /// without one being given - the format is in those bytes, and reading it would mean decoding
    /// them.
    /// </remarks>
    public SecsFormat? Format { get; }

    /// <summary>Gets how many values the item carried, when it is known.</summary>
    /// <remarks>Characters for text, numbers for anything numeric. <see langword="null"/> under the same condition as <see cref="Format"/>.</remarks>
    public int? Count { get; }

    /// <summary>Gets whether there is no token here.</summary>
    /// <remarks>
    /// True for no bytes and for an item that carried no values. <strong>Not the same as having no
    /// bytes</strong>: an encoded empty item still has a format byte and a length byte in front of
    /// it, so an empty <c>A</c> is two bytes and not none - the distinction that cost stream 13's
    /// chapter a bug on its first run.
    /// </remarks>
    public bool IsEmpty => Encoded.IsEmpty || Count == 0;

    /// <summary>Keeps an item by encoding it.</summary>
    /// <param name="item">The item. Stays the caller's to dispose; this does not keep it.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <remarks>One copy, because the item borrows pooled memory and is released with its message - the same copy <see cref="Recipe"/> makes of a body.</remarks>
    public static RecipeLockToken From(SecsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new RecipeLockToken(item.ToByteArray(), item.Format, item.Count);
    }

    /// <summary>Wraps bytes that are already an encoded item, without copying them.</summary>
    /// <param name="encoded">The encoded item. Must stay valid for as long as the token is used.</param>
    /// <param name="format">The item's format, when the caller knows it.</param>
    /// <param name="count">How many values it carries, when the caller knows it.</param>
    /// <returns>The token.</returns>
    /// <remarks>The way back in for a store that wrote its locks to disk: those bytes are already the store's own.</remarks>
    public static RecipeLockToken FromEncoded(
        ReadOnlyMemory<byte> encoded,
        SecsFormat? format = null,
        int? count = null)
        => new(encoded, format, count);

    /// <summary>Mints a token from text.</summary>
    /// <param name="token">What to call the lock.</param>
    /// <returns>The token, as an encoded <c>A[n]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <strong>Text is the store's choice and not the reference's</strong>, which defines
    /// <c>RCPLOCKID</c> nowhere. Here for the stores that want a readable lock without building an
    /// item by hand; nothing in this package depends on the format, because the token goes back
    /// out as whatever came in.
    /// </remarks>
    public static RecipeLockToken FromText(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        using var item = SecsItem.A(token);

        return From(item);
    }

    /// <summary>Decodes the token back into an item.</summary>
    /// <returns>A fresh item, or <see langword="null"/> when there are no bytes to decode.</returns>
    /// <exception cref="SecsException">The bytes are not a well-formed item.</exception>
    /// <remarks>The item is the caller's to dispose, like anything else out of <c>SecsItem.Decode</c>.</remarks>
    public SecsItem? Decode()
        => Encoded.IsEmpty ? null : SecsItem.Decode(Encoded);

    /// <summary>Gets whether another token is this one.</summary>
    /// <param name="other">The one to compare. <see langword="null"/> is never a match.</param>
    /// <returns>Whether the encoded bytes are identical.</returns>
    /// <remarks>
    /// <strong>The bytes are the whole comparison, because there is no defined format to
    /// canonicalise.</strong> A host that hands back the item it was given compares equal; one
    /// that read the value out and re-encoded it in another format does not - and that is the
    /// right answer rather than a shortcoming, since nothing in the reference says the two mean the
    /// same thing. A store with its own idea of equality overrides nothing: it holds its own locks
    /// and compares them its own way.
    /// </remarks>
    public bool Matches(RecipeLockToken? other)
        => other is not null && Encoded.Span.SequenceEqual(other.Encoded.Span);

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The format and the encoded length.</returns>
    public override string ToString()
        => Count is { } count
            ? $"{(Format is { } known ? known.ToString() : "unknown")}[{count}], {Encoded.Length} encoded bytes"
            : $"{(Format is { } format ? format.ToString() : "unknown")}, {Encoded.Length} encoded bytes";
}

/// <summary>
/// A recipe's version, as the item it arrived in - <c>RCPVER</c>, which the reference never defines.
/// </summary>
/// <remarks>
/// <para>
/// The second type in this package of <see cref="RecipeLockToken"/>'s shape, and deliberately a
/// second rather than a shared one: a lock token and a version are different things to a host, and
/// a type called a lock that carried a version would be worse than two types that each say what
/// they hold. What they share is the reason for being opaque.
/// </para>
/// <para>
/// <strong><c>RCPVER</c> is undefined and its only rival is shared.</strong> The dictionary has one
/// version item, <c>RCPVERS</c>, an <c>A[n]</c> that no message names - and S15F45 needs
/// <em>two</em> distinct version formats from it, which is why the revision compare stays out. Here
/// there is one version and one rival, so a format could be read off it; this type carries the item
/// untouched anyway, because a version is the machine's own string and nothing in this library has
/// any use for its value.
/// </para>
/// <para>
/// <strong>Carried opaquely, which is what a label is for.</strong> The machine writes this and the
/// host reads it: a host shows it, or compares it with <see cref="Matches"/> for equality, and
/// neither needs a format. That is <c>V</c>'s position in stream 6, which
/// <c>Barbatos.SecsGem.Gem</c> carries untouched in six rows.
/// </para>
/// <para>
/// This paragraph used to contrast the version with S15F49's owner, which travels the other way
/// and was out at the time. Both are carried untouched now, and for one reason rather than two:
/// <strong>nothing in either direction has to parse the value</strong>. The difference the
/// contrast was reaching for survives in a different place - a value the machine must MINT needs a
/// format the far end can read, which is what keeps <c>SPACEAVAIL</c> and S15F48's nameless
/// column out, and a version is not minted but reported.
/// </para>
/// <para>
/// Which is also why there is no <c>FromText</c> here where the lock has one: a store that mints a
/// lock has to put <em>something</em> on the wire, and a store reporting a version it does not have
/// reports none - <see cref="IRecipeStore.CurrentAsync"/> answers with a null version and the row
/// carries a zero-length item for it.
/// </para>
/// </remarks>
public sealed class RecipeVersion
{
    private RecipeVersion(ReadOnlyMemory<byte> encoded, SecsFormat? format, int? count)
    {
        Encoded = encoded;
        Format = format;
        Count = count;
    }

    /// <summary>Gets the encoded item: header and payload, as it crossed the wire.</summary>
    public ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>Gets the format of the item that arrived, when it is known.</summary>
    /// <remarks><see langword="null"/> for a version built from bytes without one being given.</remarks>
    public SecsFormat? Format { get; }

    /// <summary>Gets how many values the item carried, when it is known.</summary>
    public int? Count { get; }

    /// <summary>Gets whether there is no version here.</summary>
    /// <remarks>
    /// True for no bytes and for an item that carried no values - the second of which is what a
    /// machine reporting a recipe it keeps no version for puts on the wire.
    /// </remarks>
    public bool IsEmpty => Encoded.IsEmpty || Count == 0;

    /// <summary>Keeps an item by encoding it.</summary>
    /// <param name="item">The item. Stays the caller's to dispose; this does not keep it.</param>
    /// <returns>The version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public static RecipeVersion From(SecsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new RecipeVersion(item.ToByteArray(), item.Format, item.Count);
    }

    /// <summary>Wraps bytes that are already an encoded item, without copying them.</summary>
    /// <param name="encoded">The encoded item. Must stay valid for as long as the version is used.</param>
    /// <param name="format">The item's format, when the caller knows it.</param>
    /// <param name="count">How many values it carries, when the caller knows it.</param>
    /// <returns>The version.</returns>
    /// <remarks>The way back in for a store that keeps its versions as bytes.</remarks>
    public static RecipeVersion FromEncoded(
        ReadOnlyMemory<byte> encoded,
        SecsFormat? format = null,
        int? count = null)
        => new(encoded, format, count);

    /// <summary>Keeps a version given as text.</summary>
    /// <param name="version">The version string.</param>
    /// <returns>The version, as an encoded <c>A[n]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <strong>Text is the store's choice and not the reference's.</strong> It is the reading the
    /// dictionary's own unused <c>RCPVERS</c> would give, which makes it a small choice rather than
    /// a free one - but nothing in this package depends on it, because the item goes out as
    /// whatever came in.
    /// </remarks>
    public static RecipeVersion FromText(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        using var item = SecsItem.A(version);

        return From(item);
    }

    /// <summary>Decodes the version back into an item.</summary>
    /// <returns>A fresh item, or <see langword="null"/> when there are no bytes to decode.</returns>
    /// <exception cref="SecsException">The bytes are not a well-formed item.</exception>
    /// <remarks>The item is the caller's to dispose.</remarks>
    public SecsItem? Decode()
        => Encoded.IsEmpty ? null : SecsItem.Decode(Encoded);

    /// <summary>Gets whether another version is this one.</summary>
    /// <param name="other">The one to compare. <see langword="null"/> is never a match.</param>
    /// <returns>Whether the encoded bytes are identical.</returns>
    /// <remarks>
    /// The bytes are the whole comparison, because there is no defined format to canonicalise -
    /// <see cref="RecipeLockToken.Matches"/> says the same thing at more length.
    /// </remarks>
    public bool Matches(RecipeVersion? other)
        => other is not null && Encoded.Span.SequenceEqual(other.Encoded.Span);

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The text when it is text, and the format and length when it is not.</returns>
    /// <remarks>
    /// Unlike a lock token, a version is something a person reads - so when it arrived as text this
    /// prints the text. A lock token prints its shape because its value means nothing to anybody.
    /// </remarks>
    public override string ToString()
    {
        if (Format is { } format && format.IsText() && !Encoded.IsEmpty)
        {
            using var item = Decode();

            if (item is not null)
            {
                return item.Count == 0 ? string.Empty : item.GetString();
            }
        }

        return Count is { } count
            ? $"{(Format is { } known ? known.ToString() : "unknown")}[{count}], {Encoded.Length} encoded bytes"
            : $"{(Format is { } other ? other.ToString() : "unknown")}, {Encoded.Length} encoded bytes";
    }
}

/// <summary>
/// One attribute of a recipe: the name a host chose and the item it set - <c>RCPATTRNAME</c> and
/// <c>RCPATTRVAL</c>.
/// </summary>
/// <remarks>
/// <para>
/// A pair rather than a dictionary entry because S15F4 carries them <em>in order</em>, as a list of
/// pairs inside the recipe's own entry - and a host diffing two directories wants the same order
/// back. <see cref="IRecipeStore.ListRecipesAsync"/> says which order a store gives.
/// </para>
/// <para>
/// <strong>The value is the item, never a chosen reading of it.</strong> That is what let the
/// attribute rows be served at all: <c>RCPATTRVAL</c> has two candidate readings - an <c>A[n]</c>
/// from the RCP family's own <c>RCPATTRDATA</c> and an <c>L[n]</c> from E39's <c>ATTRDATA</c> - and
/// a SECS-II item carries its own format, so neither has to be picked. See
/// <see cref="IRecipeStore.GetAttributeAsync"/>.
/// </para>
/// <para>
/// Whose item it is to dispose depends on where the pair came from, and both ends say so: a store
/// hands out its own, and <see cref="RecipeHost.RequestDirectoryAsync"/> hands out copies decoded
/// out of the reply, because a value of a kilobyte or more arrives in a buffer the codec returns to
/// its pool when the message is disposed.
/// </para>
/// </remarks>
public sealed class RecipeAttribute
{
    /// <summary>Gets the attribute's name - <c>RCPATTRNAME</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the value, as the item it was set with - <c>RCPATTRVAL</c>.</summary>
    public required SecsItem Value { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The name, and the value's format and length.</returns>
    /// <remarks>
    /// The value's shape rather than its text, because an attribute can be a list and this has to
    /// be one line in a log either way.
    /// </remarks>
    public override string ToString() => $"{Name} = {Value.Format}[{Value.Count}]";
}

/// <summary>
/// One recipe's row in a namespace's directory - S15F4.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shape came from a served sibling, which is the whole reason this row went in.</strong>
/// Every item of S15F4 was already carried in this package - <c>RCPID</c> everywhere, <c>RCPVER</c>
/// untouched at S15F36, <c>LENGTH</c> read at S15F5, the attribute pair at S15F27 and S15F29 - so
/// the nesting was the only thing left, and a nesting here has never come from the catalogue's item
/// list. It comes from a row this repository already serves.
/// </para>
/// <para>
/// That row is <strong>S7F36</strong>, the formatted process program directory: a list of entries,
/// one per program, each entry being its row's items in order and <em>ending in a nested list</em>
/// of the group that repeats - <c>PPID</c>, <c>MDLN</c>, <c>SOFTREV</c>, then the commands. This is
/// that shape with a recipe's columns: an identifier, a version, a length, then the attribute pairs.
/// The pair's own order is <c>RecipeCodec.AttributeBody</c>'s, which is S15F28's, so the two rows
/// cannot drift apart.
/// </para>
/// <para>
/// The other reading would put one attribute pair in each entry and repeat a recipe once per
/// attribute. S7F36 is the answer to that too: a reading that makes a row say the same thing several
/// times is a reading to doubt.
/// </para>
/// </remarks>
public sealed class RecipeDirectoryEntry
{
    /// <summary>Gets what the recipe is called - <c>RCPID</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets its version, or <see langword="null"/> when the store keeps none - <c>RCPVER</c>.</summary>
    /// <remarks>
    /// Nothing in stream 15 ever <em>sets</em> a version - S15F7 is a namespace, an identifier and a
    /// body - so neither store here has one and the row sends a zero-length item. See
    /// <see cref="RecipeVersion"/>, and <see cref="IRecipeStore.CurrentAsync"/>, which reports one
    /// the same way.
    /// </remarks>
    public RecipeVersion? Version { get; init; }

    /// <summary>Gets how large the body is, in bytes - <c>LENGTH</c>.</summary>
    /// <remarks>
    /// Bytes, and that needs no inference: <c>PPBODY</c> is a defined <c>B</c>, so a recipe has a
    /// byte count - which is the same quantity S15F5 declares and this is why that row could check
    /// it. <c>SPACEAVAIL</c> at S15F39 is the one that has no unit and stays refused.
    /// </remarks>
    public uint Length { get; init; }

    /// <summary>Gets the recipe's attributes, in the order the store gave them.</summary>
    /// <remarks>Empty when it has none, which is a zero-length list on the wire.</remarks>
    public IReadOnlyList<RecipeAttribute> Attributes { get; init; } = [];

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifier, the size and how many attributes it has.</returns>
    public override string ToString()
        => $"{Id} ({Length} bytes, {Attributes.Count} attribute(s))";
}
