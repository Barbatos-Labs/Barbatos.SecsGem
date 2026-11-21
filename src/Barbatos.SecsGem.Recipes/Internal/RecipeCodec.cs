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
/// Those nine lean on five items, and nothing in this file infers anything:
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
/// <strong>Why the other eighteen pairs are out, which is this chapter's finding.</strong> The
/// reference defines <strong>twenty-four</strong> items whose names begin <c>RCP</c> and its
/// messages use <strong>two</strong> of them. Every row this package leaves out reaches for a name
/// the dictionary does not define while a near-twin of that name sits in it unused:
/// <c>RCPSTATE</c> against the defined <c>RCPSTAT</c>, <c>RCPVER</c> against <c>RCPVERS</c>,
/// <c>RCPIDNEW</c> against <c>RCPNEWID</c> - which is an exact anagram, one of only three in the
/// whole reference. That is not a gap in a vocabulary. It is one vocabulary spelled two ways, and
/// a library that chose a spelling would be correcting the source. Get it backwards on a rename
/// and a recipe is written under a name nobody asked for.
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

    /// <summary>S15F9, S15F13, S15F31 and S15F53 - <c>L,2 { RCPSPEC, RCPID }</c>.</summary>
    /// <remarks>
    /// Four pairs share this body, distinguished only by the function number - the upload request,
    /// the delete, the select and the operation abort. Worth naming once: a reader that went by
    /// shape rather than by function would answer any of them with any other.
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

    // --------------------------------------------------------- acknowledgement codes

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
    /// The only number in the nine rows this package implements is a length, so a value too large
    /// for its field is clamped rather than refused - the same rule the other E5 packages here
    /// keep.
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
