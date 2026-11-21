// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// <c>GRANT</c> - whether a control program may be sent, and the codes are the reference's own.
/// </summary>
/// <remarks>
/// <para>
/// Quoted, and from a row that is a definition rather than a representative subset. <c>GRANT</c> is
/// named by seven messages across six streams - S2F2, S2F40, S3F16, S4F26, S8F2, S21F6 and S21F10 -
/// and <strong>S8F2 is the only one of the seven the reference documented</strong>. So the table
/// below is anchored on this stream's own row, which is unusual enough to be worth saying.
/// </para>
/// <para>
/// <c>Barbatos.SecsGem.Material</c> already serves the same item for S3F16, and these five names
/// and numbers match <c>MaterialGrant</c> member for member. One item cannot mean two things in one
/// repository, so a test asserts that rather than leaving it to the eye. They are two types because
/// this package does not depend on that one.
/// </para>
/// <para>
/// 4 to 63 are the table's own reserved range and read as <see cref="Other"/> with the arriving
/// code logged - never as the zero of this enum, which would turn somebody else's refusal into
/// permission to send firmware.
/// </para>
/// </remarks>
public enum ControlProgramGrant : byte
{
    /// <summary>Send it.</summary>
    Accepted = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>No space available for it.</summary>
    NoSpace = 2,

    /// <summary>The identifier was a duplicate, or not one this end recognises.</summary>
    DuplicateOrInvalid = 3,

    /// <summary>Some other refusal: the table reserves 4 to 63.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>
/// <c>ACKC8</c> - the answer to a send or an abort.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This item has no dictionary entry and no table of values</strong>, so these two names are
/// the one reading in this package that is not quoted from anywhere. What makes it a small
/// inference rather than a guess is a family measurement: the catalogue defines <strong>eight</strong>
/// items named <c>ACKCn</c> - <c>ACKC3</c>, <c>ACKC5</c>, <c>ACKC6</c>, <c>ACKC7</c>,
/// <c>ACKC7A</c>, <c>ACKC10</c>, <c>ACKC13</c> and <c>ACKC15</c> - and <strong>every one of them
/// has 0 = accepted</strong>. Eight for eight. Three are undefined, and they belong to streams 2, 8
/// and 21.
/// </para>
/// <para>
/// So zero is accepted and that is as far as the evidence goes. <strong>Nothing names anything
/// above it</strong>, so every non-zero code reads as <see cref="Denied"/> with the arriving code
/// logged - a refusal that carries no reason, the same shape stream 3's <c>ACKC3</c> has for the
/// same lack of anything better. There is deliberately no <c>Other</c> member here: with only one
/// named code, a third member would be inventing a distinction between kinds of silence.
/// </para>
/// </remarks>
public enum ControlProgramAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Refused, with no reason given - the item has no table to carry one.</summary>
    /// <remarks>Every code from 1 upwards reads as this one, and the arriving code is logged.</remarks>
    Denied = 1,
}

/// <summary>
/// A control program: an identifier and a body.
/// </summary>
/// <remarks>
/// <para>
/// Both items are undefined in the dictionary, and <strong>both are read off stream 7</strong>.
/// Stream 8 is stream 7's transfer design applied to firmware instead of recipes - the load inquire
/// and grant, the send and its acknowledgement, the request and its data, the directory, all in the
/// same order with the same shapes - so <c>CPID</c> sits where <c>PPID</c> sits and <c>CPBODY</c>
/// sits where <c>PPBODY</c> sits. <c>PPID</c> is an <c>A[120]</c> and <c>PPBODY</c> is a <c>B</c>,
/// and those are the formats this package writes.
/// </para>
/// <para>
/// <strong>That is a different answer from the one stream 13 got, and the difference is the
/// template.</strong> <c>DSDATA</c> is also an undefined payload, and
/// <c>Barbatos.SecsGem.DataSets</c> carries it as the encoded item that arrived - because stream 13
/// is nobody's copy, so there was no slot to read a format off and the honest move was to choose
/// none. Here there is one. A control program is therefore plain bytes, which is also exactly what
/// <c>Barbatos.SecsGem.Gem</c>'s process program store takes, for the same reason one step closer
/// to the source.
/// </para>
/// <para>
/// <strong>Do not confuse <c>CPID</c> with <c>CPNAME</c>.</strong> Those two letters mean
/// <em>control program</em> here and <em>command parameter</em> in S2F41 and S18F1, and the
/// catalogue defines <c>CPNAME</c> and <c>CPVAL</c> while leaving <c>CPID</c> and <c>CPBODY</c>
/// undefined. They are unrelated items that happen to share a prefix.
/// </para>
/// </remarks>
public sealed class ControlProgram
{
    /// <summary>Gets what the program is called - <c>CPID</c>.</summary>
    /// <remarks>
    /// Written as an <c>A</c>, as <c>PPID</c> is. Empty for the boot program, which S8F3 sends
    /// without an identifier at all - see <see cref="IControlProgramStore.SaveAsync"/>.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>Gets the program itself - <c>CPBODY</c>.</summary>
    /// <remarks>
    /// <para>
    /// Written as a <c>B</c>, as <c>PPBODY</c> is. These are the bytes of the program and not an
    /// encoded item, which is the thing most easily got wrong by anybody coming from
    /// <c>Barbatos.SecsGem.DataSets</c>, where the same C# type holds an item tree.
    /// </para>
    /// <para>
    /// A body read off the wire is <strong>copied</strong> before it reaches a store, because the
    /// item it arrived in borrows pooled memory and is released with its message.
    /// </para>
    /// </remarks>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Gets whether there is a program here.</summary>
    /// <remarks>
    /// An empty body is how S8F6 says the machine has no such program: that reply carries a
    /// <c>CPID</c> and a <c>CPBODY</c> and <strong>no acknowledgement code</strong>, so there is
    /// nowhere to put a refusal.
    /// </remarks>
    public bool IsEmpty => Body.IsEmpty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifier and how many bytes it carries.</returns>
    public override string ToString()
        => Id.Length == 0
            ? $"the boot program, {Body.Length} byte(s)"
            : $"{Id}, {Body.Length} byte(s)";
}
