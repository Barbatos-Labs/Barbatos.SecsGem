// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Text;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// Every stream 15 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the provenance is.</strong> Not one of stream 15's fifty-four rows is a definition,
/// which is the figure that swept it into the previous chapter's refusal of stream 19 - and it is
/// not a veto, because stream 21 has the same zero. The dictionary is what decides, and there the
/// two streams are nothing alike: <strong>nine of stream 15's twenty-seven pairs have every field
/// of the primary and every field of the reply defined</strong>, against none of stream 19's ten.
/// </para>
/// <para>
/// Those nine lean on five items, and for them nothing in this file infers anything:
/// </para>
/// <code>
/// RCPSPEC   A[n]  defined, the namespace - streams 15 and 20
/// RCPID     A[n]  defined, the recipe    - streams 15, 19 and 20
/// LENGTH    U4    defined, named by eight streams, three of its carriers definitions
/// PPBODY    B     defined, STREAM 7'S OWN - four of its fifteen carriers are definitions
/// RMACK     B[1]  defined, WITH A TABLE: 0 accepted, 1 rejected, 2-63 reserved
/// </code>
/// <para>
/// <strong>The body needs no inference at all</strong>, which has not been true here before.
/// Stream 8 read <c>CPBODY</c> off <c>PPBODY</c>'s slot because stream 8 is stream 7's design;
/// streams 13 and 21 had no slot to read and carry an encoded item. Stream 15 names
/// <c>PPBODY</c> itself, in a row whose stream 7 siblings - S7F3, S7F6, S7F23, S7F26 - the
/// reference documented. So a recipe body is a <c>B</c> because the reference says so.
/// </para>
/// <para>
/// <strong>And four more pairs that carry one undefined name each</strong> - the rename, the copy,
/// the lock and the unlock. These were refused for six chapters on the ground that the names they
/// reach for are rival spellings of definitions nothing uses, so a library picking a spelling
/// would be correcting the source. <strong>The measure that said so reports two different
/// things.</strong> Of the eight rival spellings in the whole reference, four are a used name
/// against a definition <em>nothing</em> names - <c>RCPSTATE</c> against <c>RCPSTAT</c>,
/// <c>RCPVER</c> against <c>RCPVERS</c> - and four are a used name against a definition the
/// messages <strong>also use</strong>. Three of those four are here, and in all three the
/// definition is in the same body:
/// </para>
/// <code>
/// S15F19  RCPSPEC, RCPID, RCPIDNEW       RCPIDNEW extends RCPID, two positions earlier
/// S15F21  RCPSPEC, RCPID, RCPSPECDST, RCPIDDST    each extends the name two earlier
/// </code>
/// <para>
/// A name whose base is in the same message is a <strong>modifier, not a misspelling</strong>, and
/// its format is read off an item in the row rather than off a sibling row or a rival definition.
/// <c>RCPIDNEW</c> has both relations at once - it extends the used <c>RCPID</c> and is an exact
/// anagram of the unused <c>RCPNEWID</c> - and <strong>both readings are an <c>A[n]</c></strong>,
/// so the rivalry puts no byte in doubt. Item names do not travel: a SECS-II body is positional,
/// and the thing a wrong spelling could get wrong here is a comment.
/// </para>
/// <para>
/// <c>RCPLOCKID</c> is a third case again - no base, no anagram, no stem anywhere - so it is
/// carried as the item it arrived in. See <see cref="RecipeLockToken"/> for why that loses nothing
/// at a lock; it loses nothing at S15F49's owner either, which is a sentence this paragraph used
/// to end the other way round.
/// </para>
/// <para>
/// <strong>What keeps the other eight pairs out</strong> is one of two things now, and the
/// package README gives them row by row: an undefined name that <em>is</em> the message, so
/// carrying it untouched leaves the row uninterpretable (<c>RCPEXIST</c>, <c>AUTOSEL</c>,
/// <c>RCPSTATE</c>, <c>VALACK</c>, <c>CMPRESULT</c>); or a value the machine must mint for a host
/// to act on, which is <c>CHECKSUM</c> - never defined under any spelling in any of the three
/// streams that ask for it, and a computation rather than a field, since the machine has to
/// reproduce what the host produced - <c>SPACEAVAIL</c>, a quantity with no unit, and S15F48's
/// third column, a value with no name beside it.
/// </para>
/// <para>
/// <strong>A name whose two readings are different formats is no longer one of them.</strong> That
/// measure held <c>RCPATTRVAL</c> against the unused <c>RCPATTRDATA</c> and <c>RCPOWNER</c>
/// against <c>RCPOWCODE</c> - text against a list, a name against a flag - and it decides nothing
/// wherever nothing has to choose, because a SECS-II item carries its own format. The attribute
/// pair went in on that, then the namespace attribute and the owner.
/// </para>
/// <para>
/// <strong>Nesting is the one thing a flat item list cannot pin down</strong>, so the readers are
/// more generous than the writers.
/// </para>
/// </remarks>
internal static class RecipeCodec
{
    // ------------------------------------------------------------------ the namespaces

    /// <summary>S15F2 - <c>L,n { RCPSPEC }</c>.</summary>
    internal static SecsItem NamespaceListBody(IReadOnlyList<string>? namespaces)
    {
        if (namespaces is null || namespaces.Count == 0)
        {
            return SecsItem.L();
        }

        var entries = new SecsItem[namespaces.Count];

        for (var i = 0; i < namespaces.Count; i++)
        {
            entries[i] = SecsItem.A(namespaces[i]).Named("RCPSPEC");
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S15F2, as a list or as one bare specification.</summary>
    internal static List<string> ReadNamespaceList(SecsItem? body)
    {
        List<string> namespaces = [];

        if (body is null)
        {
            return namespaces;
        }

        if (!body.IsList)
        {
            // One namespace, not bothered to wrap it. The same tolerance stream 21's directory
            // reader has, and for the same reason: a list of one and a bare item carry the same
            // answer.
            var only = Word(body);

            if (only.Length > 0)
            {
                namespaces.Add(only);
            }

            return namespaces;
        }

        foreach (var entry in body.Items)
        {
            // Descends one level, because a machine that wrapped each specification in a list of
            // one is writing the same thing.
            var read = entry.IsList
                ? entry.Count == 1 ? Word(entry.Items[0]) : string.Empty
                : Word(entry);

            if (read.Length > 0)
            {
                namespaces.Add(read);
            }
        }

        return namespaces;
    }

    /// <summary>S15F15 and S15F17 - a bare <c>RCPSPEC</c>.</summary>
    internal static SecsItem NamespaceBody(string recipeNamespace)
        => SecsItem.A(recipeNamespace).Named("RCPSPEC");

    /// <summary>Reads a bare <c>RCPSPEC</c>, descending into a list of one.</summary>
    internal static bool TryReadNamespace(SecsItem? item, out string recipeNamespace)
    {
        recipeNamespace = string.Empty;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadNamespace(item.Items[0], out recipeNamespace);
        }

        recipeNamespace = Word(item);
        return recipeNamespace.Length > 0;
    }

    // --------------------------------------------------------------------- the recipe

    /// <summary>S15F9, S15F13, S15F23, S15F31 and S15F53 - <c>L,2 { RCPSPEC, RCPID }</c>.</summary>
    /// <remarks>
    /// Five pairs share this body, distinguished only by the function number - the upload request,
    /// the delete, the lock, the select and the operation abort. Worth naming once: a reader that
    /// went by shape rather than by function would answer any of them with any other.
    /// </remarks>
    internal static SecsItem RecipeNameBody(string recipeNamespace, string recipeId)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"));

    /// <summary>Reads a namespace-and-identifier body.</summary>
    internal static bool TryReadRecipeName(SecsItem? body, out string recipeNamespace, out string recipeId)
    {
        recipeNamespace = string.Empty;
        recipeId = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        recipeNamespace = Word(body.Items[0]);
        recipeId = Word(body.Items[1]);

        // The identifier has to be there; the namespace may be empty, because a machine with one
        // unnamed namespace is the shape S15F1 would report as a single blank specification. The
        // store decides whether it knows that namespace.
        return recipeId.Length > 0;
    }

    /// <summary>S15F5 - <c>L,3 { RCPSPEC, RCPID, LENGTH }</c>.</summary>
    internal static SecsItem DownloadRequestBody(RecipeDownloadRequest request)
        => SecsItem.L(
            SecsItem.A(request.Namespace).Named("RCPSPEC"),
            SecsItem.A(request.Id).Named("RCPID"),
            SecsItem.U4(request.Length).Named("LENGTH"));

    /// <summary>Reads S15F5. A body short of its length reads as a length of zero.</summary>
    internal static bool TryReadDownloadRequest(SecsItem? body, out RecipeDownloadRequest? request)
    {
        request = null;

        if (!TryReadRecipeName(body, out var recipeNamespace, out var recipeId))
        {
            return false;
        }

        request = new RecipeDownloadRequest
        {
            Namespace = recipeNamespace,
            Id = recipeId,
            Length = body!.Count > 2 ? UInt32(body.Items[2]) : 0,
        };

        return true;
    }

    /// <summary>S15F7 - <c>L,3 { RCPSPEC, RCPID, PPBODY }</c>.</summary>
    internal static SecsItem DownloadSendBody(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            BodyItem(body));

    /// <summary>Reads S15F7.</summary>
    /// <remarks>
    /// Exactly three items, and deliberately no tolerance for two: the third is the recipe, and a
    /// body of two could be a name with no recipe or a namespace-less recipe. Guessing would store
    /// an identifier as somebody's process.
    /// </remarks>
    internal static bool TryReadDownloadSend(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out ReadOnlyMemory<byte> recipe)
    {
        recipe = default;

        if (!TryReadRecipeName(body, out recipeNamespace, out recipeId) || body!.Count < 3)
        {
            return false;
        }

        return TryReadRecipeBody(body.Items[2], out recipe);
    }

    /// <summary>S15F10 - <c>L,2 { RMACK, PPBODY }</c>.</summary>
    /// <remarks>
    /// <strong>The one reply in this package that carries both a code and data</strong>, which is
    /// what lets an upload be refused. Stream 8's S8F6 carries a body and no code at all, so a
    /// control program the machine does not have looks exactly like an empty one - this stream
    /// does not have that problem.
    /// </remarks>
    internal static SecsItem UploadBody(RecipeAcknowledgement acknowledgement, ReadOnlyMemory<byte> body)
        => SecsItem.L(
            CodeItem(acknowledgement),
            BodyItem(body));

    /// <summary>Reads S15F10.</summary>
    internal static bool TryReadUpload(
        SecsItem? body,
        out byte? code,
        out ReadOnlyMemory<byte> recipe)
    {
        code = null;
        recipe = default;

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        code = ReadCode(body.Items[0]);

        if (code is null)
        {
            return false;
        }

        // A refusal need not carry a body, and a machine that sent only the code has answered.
        return body.Count < 2 || TryReadRecipeBody(body.Items[1], out recipe);
    }

    // ----------------------------------------------------- the rename and the copy

    /// <summary>S15F19 - <c>L,3 { RCPSPEC, RCPID, RCPIDNEW }</c>.</summary>
    /// <remarks>
    /// One namespace and two identifiers, which is the row rather than a reading: S15F21 carries
    /// two namespaces where this carries one, so a rename stays where the recipe is.
    /// </remarks>
    internal static SecsItem RenameBody(string recipeNamespace, string recipeId, string newRecipeId)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            SecsItem.A(newRecipeId).Named("RCPIDNEW"));

    /// <summary>Reads S15F19.</summary>
    /// <remarks>
    /// Exactly three items, with no tolerance for two: two is the delete's body, and a rename read
    /// out of it would be a rename to nothing - which this reader refuses rather than reporting as
    /// an empty new name.
    /// </remarks>
    internal static bool TryReadRename(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out string newRecipeId)
    {
        newRecipeId = string.Empty;

        if (!TryReadRecipeName(body, out recipeNamespace, out recipeId) || body!.Count < 3)
        {
            return false;
        }

        newRecipeId = Word(body.Items[2]);

        return newRecipeId.Length > 0;
    }

    /// <summary>S15F21 - <c>L,4 { RCPSPEC, RCPID, RCPSPECDST, RCPIDDST }</c>.</summary>
    /// <remarks>
    /// The source pair then the destination pair, each in the order the four other rows put
    /// <c>RCPSPEC</c> and <c>RCPID</c> in. Nothing here is a choice: each undefined name extends
    /// the defined one two positions earlier.
    /// </remarks>
    internal static SecsItem CopyBody(
        string sourceNamespace,
        string sourceId,
        string destinationNamespace,
        string destinationId)
        => SecsItem.L(
            SecsItem.A(sourceNamespace).Named("RCPSPEC"),
            SecsItem.A(sourceId).Named("RCPID"),
            SecsItem.A(destinationNamespace).Named("RCPSPECDST"),
            SecsItem.A(destinationId).Named("RCPIDDST"));

    /// <summary>Reads S15F21.</summary>
    /// <remarks>
    /// Exactly four items. A destination namespace may be blank for the reason a source one may -
    /// a machine with one unnamed namespace - but a destination identifier may not, because there
    /// would be nothing to call the copy.
    /// </remarks>
    internal static bool TryReadCopy(
        SecsItem? body,
        out string sourceNamespace,
        out string sourceId,
        out string destinationNamespace,
        out string destinationId)
    {
        destinationNamespace = string.Empty;
        destinationId = string.Empty;

        if (!TryReadRecipeName(body, out sourceNamespace, out sourceId) || body!.Count < 4)
        {
            return false;
        }

        destinationNamespace = Word(body.Items[2]);
        destinationId = Word(body.Items[3]);

        return destinationId.Length > 0;
    }

    // ------------------------------------------------------------------- the lock

    /// <summary>S15F24 - <c>L,2 { RMACK, RCPLOCKID }</c>.</summary>
    /// <remarks>
    /// <strong>S15F10's body, item for item</strong> - a code and then a payload the dictionary
    /// does not define - which is what settles the nesting here rather than leaving it a guess.
    /// This package already writes and reads that pair, including the case the catalogue's item
    /// list cannot show: a refusal that carries no payload at all.
    /// </remarks>
    internal static SecsItem LockReplyBody(RecipeAcknowledgement acknowledgement, RecipeLockToken? token)
        => SecsItem.L(
            CodeItem(acknowledgement),
            (token?.Decode() ?? SecsItem.L()).Named("RCPLOCKID"));

    /// <summary>Reads S15F24.</summary>
    internal static bool TryReadLockReply(SecsItem? body, out byte? code, out RecipeLockToken? token)
    {
        code = null;
        token = null;

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        code = ReadCode(body.Items[0]);

        if (code is null)
        {
            return false;
        }

        if (body.Count > 1)
        {
            var carried = body.Items[1];

            // An empty list is how this end writes "no token", and a refusal need not carry the
            // item at all - so both read as no lock rather than as a lock with no identity. A
            // token that arrived as something else, a list included, is kept as it came: what it
            // is inside is the machine's business and not this library's.
            if (!carried.IsList || carried.Count > 0)
            {
                token = RecipeLockToken.From(carried);
            }
        }

        return true;
    }

    /// <summary>S15F36 - <c>L,2 { RCPID, RCPVER }</c>, or <c>L,0</c> for nothing selected.</summary>
    /// <remarks>
    /// <strong>Flat, and read off S7F6.</strong> The reference records this reply as two items and
    /// no nesting, and the only served row anywhere here with the same recording is the process
    /// program request's reply - an identifier and a second field, flat. The empty list is the
    /// whole of "nothing is selected", which this row has no acknowledgement item to say any other
    /// way: S7F6, S7F26, S6F16, S6F20 and S6F28 all refuse the same way.
    /// </remarks>
    internal static SecsItem CurrentReplyBody(string recipeId, RecipeVersion? version)
        => SecsItem.L(
            SecsItem.A(recipeId).Named("RCPID"),
            (version?.Decode() ?? SecsItem.L()).Named("RCPVER"));

    /// <summary>Reads S15F36.</summary>
    /// <remarks>
    /// A zero-length body is nothing selected and reads as <see langword="false"/> with no
    /// identifier - not as a selection with an empty name, which would tell a host the machine is
    /// set up to run something called "". A body with an identifier and no second item reads as a
    /// selection with no version, because a machine that keeps no versions has none to send and
    /// S15F36 has no code to say so with.
    /// </remarks>
    internal static bool TryReadCurrentReply(SecsItem? body, out string? recipeId, out RecipeVersion? version)
    {
        recipeId = null;
        version = null;

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        var first = body.Items[0];

        if (!first.Format.IsText())
        {
            return false;
        }

        recipeId = first.Count == 0 ? string.Empty : first.GetString().TrimEnd();

        if (body.Count > 1)
        {
            var carried = body.Items[1];

            // The same reading the lock token gets: an empty list is how this end writes "no
            // version", and anything else is kept as it came. What a version is inside is the
            // machine's business.
            if (!carried.IsList || carried.Count > 0)
            {
                version = RecipeVersion.From(carried);
            }
        }

        return true;
    }

    /// <summary>S15F25 - <c>L,3 { RCPSPEC, RCPID, RCPLOCKID }</c>.</summary>
    /// <remarks>S15F7's body, with the token where the recipe goes.</remarks>
    internal static SecsItem UnlockBody(string recipeNamespace, string recipeId, RecipeLockToken token)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            (token.Decode() ?? SecsItem.L()).Named("RCPLOCKID"));

    /// <summary>Reads S15F25.</summary>
    /// <remarks>
    /// Exactly three items, like the download send and for the same reason. An item carrying
    /// nothing reads as an empty token rather than as a body this end could not understand: it is
    /// a token that will match no lock, which the store answers, and keeping the two apart is what
    /// lets the log say which happened.
    /// </remarks>
    internal static bool TryReadUnlock(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out RecipeLockToken? token)
    {
        token = null;

        if (!TryReadRecipeName(body, out recipeNamespace, out recipeId) || body!.Count < 3)
        {
            return false;
        }

        token = RecipeLockToken.From(body.Items[2]);

        return true;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>S15F27 - <c>L,3 { RCPSPEC, RCPID, RCPATTRNAME }</c>.</summary>
    /// <remarks>
    /// <c>RCPATTRNAME</c> is undefined and its format is read off <c>RCPATTRID</c>, the
    /// dictionary's own recipe attribute identifier - a defined <c>A[n]</c> that no row names, which
    /// is the route <c>CJID</c>'s format was read by.
    /// </remarks>
    internal static SecsItem AttributeGetBody(string recipeNamespace, string recipeId, string attributeName)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            SecsItem.A(attributeName).Named("RCPATTRNAME"));

    /// <summary>Reads S15F27.</summary>
    /// <returns>Whether all three items were there.</returns>
    internal static bool TryReadAttributeGet(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out string attributeName)
    {
        attributeName = string.Empty;

        if (!TryReadRecipeName(body, out recipeNamespace, out recipeId) || body!.Count < 3)
        {
            return false;
        }

        attributeName = Word(body.Items[2]);

        return attributeName.Length > 0;
    }

    /// <summary>S15F29 - <c>L,4 { RCPSPEC, RCPID, RCPATTRNAME, RCPATTRVAL }</c>.</summary>
    /// <remarks>
    /// The value goes on the wire as the item it was given, which is the whole reason this row is
    /// served: <c>RCPATTRVAL</c> has two candidate readings - the RCP family's own
    /// <c>RCPATTRDATA</c>, an <c>A[n]</c>, and E39's <c>ATTRDATA</c>, an <c>L[n]</c> - and nothing
    /// here chooses between them, because a SECS-II item carries its own format and the host that
    /// reads one back is the host that wrote it.
    /// </remarks>
    internal static SecsItem AttributeSetBody(
        string recipeNamespace,
        string recipeId,
        string attributeName,
        SecsItem value)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            SecsItem.A(attributeName).Named("RCPATTRNAME"),
            value.Named("RCPATTRVAL"));

    /// <summary>Reads S15F29.</summary>
    /// <returns>Whether all four items were there.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The value is copied out of the arriving item</strong>, for the reason
    /// <see cref="TryReadRecipeBody"/> copies a body: a payload of
    /// <see cref="SecsCodecOptions.PooledPayloadThreshold"/> bytes or more borrows a buffer the
    /// codec hands back to its pool when the message is disposed, and a store keeps what it is
    /// given. So the item here outlives the transaction and is the store's.
    /// </para>
    /// <para>
    /// Text never took that path - an <c>A[n]</c> decodes into a string - which is why the first
    /// draft of the attribute pair looked right: every test of it set text. An attribute is
    /// whatever item a host sent, and a <c>B[n]</c> of a kilobyte is one of them.
    /// </para>
    /// </remarks>
    internal static bool TryReadAttributeSet(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out string attributeName,
        out SecsItem value)
    {
        value = SecsItem.L();

        if (!TryReadAttributeGet(body, out recipeNamespace, out recipeId, out attributeName) || body!.Count < 4)
        {
            return false;
        }

        value = SecsItem.Decode(body.Items[3].ToByteArray());

        return true;
    }

    /// <summary>S15F28 and S15F52 - <c>L,2 { RCPATTRNAME, RCPATTRVAL }</c>.</summary>
    /// <remarks>
    /// <para>
    /// A flat body of two items, which is the shape S15F36 has and the reason neither row needed a
    /// nesting decided for it: the request named one attribute, so the reply carries that one. An
    /// attribute nobody set comes back as a zero-length value, the way five served rows in three
    /// packages say no without an acknowledgement item.
    /// </para>
    /// <para>
    /// <strong>The value is copied in, because an item handed to a message becomes that
    /// message's.</strong> Every other body here is built from fresh items - a version and a lock
    /// token each decode a new one out of their bytes - and this was the one place a store's own
    /// item went straight onto the wire, where the reply disposed it on its way out. The hazard was
    /// invisible for a chapter because every test set <em>text</em>, and text decodes into a string
    /// that owns nothing; a <c>B[n]</c> of a kilobyte owns a pooled buffer.
    /// </para>
    /// </remarks>
    internal static SecsItem AttributeBody(string attributeName, SecsItem? value)
        => SecsItem.L(
            SecsItem.A(attributeName).Named("RCPATTRNAME"),
            (value is null ? SecsItem.L() : SecsItem.Decode(value.ToByteArray())).Named("RCPATTRVAL"));

    // -------------------------------------------------------------------- the owner

    /// <summary>S15F49 - <c>L,3 { RCPSPEC, RCPID, RCPOWNER }</c>.</summary>
    /// <remarks>
    /// <para>
    /// The owner goes on the wire as the item it was given, which is the whole reason this row is
    /// served and the reason it was refused for seven chapters. <c>RCPOWNER</c> is undefined and
    /// <em>does</em> have a rival the measure can see - <c>RCPOWCODE</c>, a defined
    /// <c>BOOLEAN[1]</c> that no row names - so the two readings differ as sharply as any pair in
    /// the reference: a name against a flag. They decide nothing here, because
    /// <strong>nothing in this library ever has to choose</strong>. A host that sends text stores
    /// text and a host that sends a boolean stores a boolean.
    /// </para>
    /// <para>
    /// What had been said about this row is that an owner travels host to machine, so the machine
    /// must parse what the host chose. Read against the stream it is in, it does not: <strong>no
    /// row anywhere in the reference reads a recipe owner back</strong>, and nothing in E5 says
    /// what owning a recipe prevents. So the store keeps the item and the application decides what
    /// ownership means - which is less invention than the recipe lock this package already ships,
    /// where E5 defines the transaction and never says what it blocks either.
    /// </para>
    /// </remarks>
    internal static SecsItem OwnerSetBody(string recipeNamespace, string recipeId, SecsItem owner)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(recipeId).Named("RCPID"),
            owner.Named("RCPOWNER"));

    /// <summary>Reads S15F49.</summary>
    /// <returns>Whether all three items were there.</returns>
    /// <remarks>
    /// The owner is copied out of the arriving item for the reason
    /// <see cref="TryReadAttributeSet"/> copies a value: a store keeps what it is given, and a
    /// payload of <see cref="SecsCodecOptions.PooledPayloadThreshold"/> bytes or more borrows a
    /// buffer the codec hands back when the message is disposed.
    /// </remarks>
    internal static bool TryReadOwnerSet(
        SecsItem? body,
        out string recipeNamespace,
        out string recipeId,
        out SecsItem owner)
    {
        owner = SecsItem.L();

        if (!TryReadRecipeName(body, out recipeNamespace, out recipeId) || body!.Count < 3)
        {
            return false;
        }

        owner = SecsItem.Decode(body.Items[2].ToByteArray());

        return true;
    }

    // ------------------------------------------------------ the namespace attribute

    /// <summary>S15F51 - <c>L,2 { RCPSPEC, RCPATTRNAME }</c>.</summary>
    /// <remarks>
    /// S15F27 with the recipe taken out, which is what the row is: the same two undefined names
    /// read the same two ways - <c>RCPATTRNAME</c> off the dictionary's own <c>RCPATTRID</c>, and
    /// <c>RCPATTRVAL</c> off nothing, because a SECS-II item carries its own format. The reply is
    /// <see cref="AttributeBody"/>, shared with S15F28 so the two rows cannot disagree.
    /// </remarks>
    internal static SecsItem NamespaceAttributeGetBody(string recipeNamespace, string attributeName)
        => SecsItem.L(
            SecsItem.A(recipeNamespace).Named("RCPSPEC"),
            SecsItem.A(attributeName).Named("RCPATTRNAME"));

    /// <summary>Reads S15F51.</summary>
    /// <returns>Whether both items were there.</returns>
    internal static bool TryReadNamespaceAttributeGet(
        SecsItem? body,
        out string recipeNamespace,
        out string attributeName)
    {
        recipeNamespace = string.Empty;
        attributeName = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        // The namespace may be empty here for the reason it may be empty in a recipe body: a
        // machine with one unnamed namespace is what S15F1 reports as a single blank
        // specification. TryReadNamespace is no use for this row - it descends into a list of
        // ONE, which is what S15F15 and S15F17 carry.
        recipeNamespace = Word(body.Items[0]);
        attributeName = Word(body.Items[1]);

        return attributeName.Length > 0;
    }

    // --------------------------------------------------------------------- the directory

    /// <summary>
    /// S15F4 - <c>L,n { L,4 { RCPID, RCPVER, LENGTH, L,m { L,2 { RCPATTRNAME, RCPATTRVAL } } } }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read off S7F36, which is the same part of the same kind of pair.</strong> The
    /// formatted process program directory is a list of entries, one per program, and each entry is
    /// its row's items in order ending in a nested list of the group that repeats. This is that,
    /// with a recipe's columns - and the pair at the end is written by
    /// <see cref="AttributeBody"/>, so S15F4 and S15F28 cannot disagree about which of
    /// <c>RCPATTRNAME</c> and <c>RCPATTRVAL</c> comes first.
    /// </para>
    /// <para>
    /// A namespace with no recipes, and a namespace that is not there, are both the zero-length
    /// body: S15F4 carries no acknowledgement item, so this row says no the way S7F6, S7F26, S6F16,
    /// S6F20 and S6F28 do. S15F1 is where a host learns which namespaces exist.
    /// </para>
    /// </remarks>
    internal static SecsItem DirectoryReplyBody(IReadOnlyList<RecipeDirectoryEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return SecsItem.L();
        }

        var rows = new List<SecsItem>(entries.Count);

        foreach (var entry in entries)
        {
            var attributes = new List<SecsItem>(entry.Attributes.Count);

            foreach (var attribute in entry.Attributes)
            {
                attributes.Add(AttributeBody(attribute.Name, attribute.Value));
            }

            rows.Add(SecsItem.L(
                SecsItem.A(entry.Id).Named("RCPID"),
                (entry.Version?.Decode() ?? SecsItem.L()).Named("RCPVER"),
                SecsItem.U4(entry.Length).Named("LENGTH"),
                SecsItem.L(attributes)));
        }

        return SecsItem.L(rows);
    }

    /// <summary>Reads S15F4.</summary>
    /// <returns>The entries, which is empty for a zero-length body.</returns>
    /// <remarks>
    /// <para>
    /// Leniently in one direction only: an entry with fewer items than four is read for what it has,
    /// because a machine that keeps no versions, no lengths or no attributes has nothing to put in
    /// those slots and the row has no acknowledgement item to say so with. An entry with no
    /// identifier is <em>dropped</em>, because a directory row that does not name a recipe is not a
    /// row about anything - the reading S15F36 gives an empty <c>RCPID</c>.
    /// </para>
    /// <para>
    /// <strong>Every value is copied out of the reply.</strong> A payload of
    /// <see cref="SecsCodecOptions.PooledPayloadThreshold"/> bytes or more is decoded into a buffer
    /// the codec returns to its pool when the message is disposed, and a directory is meant to be
    /// held - so the items here are fresh ones, decoded from the bytes that arrived, and are the
    /// caller's to dispose. The version is kept the way every opaque item in this package is kept,
    /// as its encoded bytes.
    /// </para>
    /// </remarks>
    internal static List<RecipeDirectoryEntry> ReadDirectoryReply(SecsItem? body)
    {
        List<RecipeDirectoryEntry> entries = [];

        if (body is null || !body.IsList)
        {
            return entries;
        }

        foreach (var row in body.Items)
        {
            if (!row.IsList || row.Count == 0)
            {
                continue;
            }

            var id = Word(row.Items[0]);

            if (id.Length == 0)
            {
                continue;
            }

            RecipeVersion? version = null;

            if (row.Count > 1 && row.Items[1] is var carried && (!carried.IsList || carried.Count > 0))
            {
                version = RecipeVersion.From(carried);
            }

            var length = row.Count > 2 ? UInt32(row.Items[2]) : 0u;

            List<RecipeAttribute> attributes = [];

            if (row.Count > 3 && row.Items[3].IsList)
            {
                foreach (var pair in row.Items[3].Items)
                {
                    if (pair is not { IsList: true, Count: >= 2 })
                    {
                        continue;
                    }

                    var name = Word(pair.Items[0]);

                    if (name.Length > 0)
                    {
                        attributes.Add(new RecipeAttribute
                        {
                            Name = name,
                            Value = SecsItem.Decode(pair.Items[1].ToByteArray()),
                        });
                    }
                }
            }

            entries.Add(new RecipeDirectoryEntry
            {
                Id = id,
                Version = version,
                Length = length,
                Attributes = attributes,
            });
        }

        return entries;
    }

    /// <summary>Writes an <c>RMACK</c>.</summary>
    internal static SecsItem CodeItem(RecipeAcknowledgement acknowledgement)
        => SecsItem.B((byte)acknowledgement).Named("RMACK");

    /// <summary>Reads an acknowledgement code, descending into a list of one if that is what arrived.</summary>
    /// <returns>The code, or <see langword="null"/> when there is none to read.</returns>
    internal static byte? ReadCode(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? ReadCode(item.Items[0]) : null;
        }

        if (item.Count == 0)
        {
            // An item with no value in it has said nothing, and zero means accepted here - so
            // falling through to a default would turn silence into a yes.
            return null;
        }

        if (item.Format is SecsFormat.Binary or SecsFormat.U1)
        {
            return item.FirstValueOrDefault<byte>();
        }

        return TryReadUnsigned(item, out var value) && value <= byte.MaxValue ? (byte)value : null;
    }

    // ------------------------------------------------------------------- the pieces

    /// <summary>Writes a recipe body as the <c>B</c> the dictionary says <c>PPBODY</c> is.</summary>
    internal static SecsItem BodyItem(ReadOnlyMemory<byte> body)
        => SecsItem.B(body.ToArray()).Named("PPBODY");

    /// <summary>Reads a recipe body, copying it out of the arriving item.</summary>
    /// <remarks>
    /// Copied, because the item borrows pooled memory and is released with its message. Text is
    /// accepted as well as binary: a machine that wrote a recipe as an <c>A</c> is being loose
    /// rather than meaning something else, since <c>PPBODY</c> is defined as a <c>B</c>.
    /// </remarks>
    internal static bool TryReadRecipeBody(SecsItem? item, out ReadOnlyMemory<byte> body)
    {
        body = default;

        if (item is null || item.IsList)
        {
            return false;
        }

        if (item.Count == 0)
        {
            // An empty recipe is a legitimate thing to send: the store decides whether to take it.
            return true;
        }

        if (!item.Format.IsText() && item.Format is not SecsFormat.Binary)
        {
            return false;
        }

        body = Bytes(item);
        return true;
    }

    private static ReadOnlyMemory<byte> Bytes(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return default;
        }

        if (item.Format is SecsFormat.Binary && item.TryGetSpan<byte>(out var bytes))
        {
            return bytes.ToArray();
        }

        return item.Format.IsText()
            ? Encoding.ASCII.GetBytes(item.GetString())
            : default;
    }

    /// <summary>Reads a text field, taking a number as the digits it is written with.</summary>
    private static string Word(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return string.Empty;
        }

        return item.Format.IsText()
            ? item.GetString().TrimEnd()
            : TryReadUnsigned(item, out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
    }

    private static uint UInt32(SecsItem item)
        => TryReadUnsigned(item, out var value) ? (uint)Math.Min(value, uint.MaxValue) : 0;

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// The only number in the nineteen rows this package implements is a length, so a value
    /// too large for its field is clamped rather than refused - the same rule the other E5
    /// packages here keep.
    /// </remarks>
    private static bool TryReadUnsigned(SecsItem item, out ulong value)
    {
        value = 0;

        if (item.IsList || item.Count == 0)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.Binary:
            case SecsFormat.U1:
                value = item.FirstValueOrDefault<byte>();
                return true;
            case SecsFormat.U2:
                value = item.FirstValueOrDefault<ushort>();
                return true;
            case SecsFormat.U4:
                value = item.FirstValueOrDefault<uint>();
                return true;
            case SecsFormat.U8:
                value = item.FirstValueOrDefault<ulong>();
                return true;
            case SecsFormat.I1:
                return NonNegative(item.FirstValueOrDefault<sbyte>(), out value);
            case SecsFormat.I2:
                return NonNegative(item.FirstValueOrDefault<short>(), out value);
            case SecsFormat.I4:
                return NonNegative(item.FirstValueOrDefault<int>(), out value);
            case SecsFormat.I8:
                return NonNegative(item.FirstValueOrDefault<long>(), out value);
            default:
                return false;
        }
    }

    private static bool NonNegative(long signed, out ulong value)
    {
        value = signed < 0 ? 0 : (ulong)signed;
        return signed >= 0;
    }
}
