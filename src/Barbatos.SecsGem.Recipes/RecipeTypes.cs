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
/// does, and it covers all nine pairs this package implements.
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
/// <c>VALACK</c> - are among the eighteen this package leaves out.
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
