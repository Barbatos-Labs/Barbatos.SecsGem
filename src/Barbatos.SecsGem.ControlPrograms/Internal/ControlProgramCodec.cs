// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// Every stream 8 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Both ends go through this class, so a change to a body is a change to both ends or it does not
/// compile.
/// </para>
/// <para>
/// <strong>What the provenance is.</strong> Four of stream 8's sixteen rows are definitions and
/// twelve are a representative subset - and the split is not arbitrary. S8F1 to S8F4 are the
/// <em>boot</em> program, which carries no identifier because there is one of it, and those four
/// are exactly the four the reference documented. Everything that names a <c>CPID</c> is
/// representative.
/// </para>
/// <code>
/// LENGTH    U4    defined
/// GRANT     B[1]  defined, four codes and a reserved range - and S8F2 is the one firm row of the
///                 seven messages in the catalogue that name GRANT
/// SOFTREV   A[20] defined
/// CPID      --    undefined - written A, where PPID sits in the same design and is an A[120]
/// CPBODY    --    undefined - written B, where PPBODY sits in the same design and is a B
/// ACKC8     --    undefined, and with no table either: zero is accepted because all eight defined
///                 ACKCn items say so, and nothing names anything above it
/// BLKNUM    --    undefined, and belongs to S8F7 to S8F10, which this package leaves out
/// BLKEND    --    undefined, likewise
/// </code>
/// <para>
/// The blocked transfer stays out on a measurement rather than a shrug: every one of the
/// catalogue's seven block-transfer items is undefined - <c>BLKNUM</c>, <c>BLKSIZE</c>,
/// <c>BLKACK</c>, <c>BLKEND</c>, <c>CHECKSUM</c>, <c>CHKSUM</c> and <c>CKSUM</c> - which is the
/// same measurement that kept S13F9 and S13F10 out of <c>Barbatos.SecsGem.DataSets</c>.
/// </para>
/// <para>
/// <strong>Nesting is the one thing a flat item list cannot pin down</strong>, so the readers are
/// more generous than the writers: each body is written one way and read in every shape a machine
/// might plausibly send it in.
/// </para>
/// </remarks>
internal static class ControlProgramCodec
{
    /// <summary>What <c>SOFTREV</c> is capped at in the dictionary.</summary>
    private const int RevisionLength = 20;

    /// <summary>What <c>PPID</c> is capped at, and the length this package writes a <c>CPID</c> to.</summary>
    private const int IdLength = 120;

    // ------------------------------------------------------------- the boot program

    /// <summary>S8F1 - a bare <c>LENGTH</c>.</summary>
    internal static SecsItem InquireBody(uint length) => SecsItem.U4(length).Named("LENGTH");

    /// <summary>Reads S8F1, descending into a list of one.</summary>
    internal static bool TryReadInquire(SecsItem? item, out uint length)
    {
        length = 0;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadInquire(item.Items[0], out length);
        }

        if (!TryReadUnsigned(item, out var value))
        {
            return false;
        }

        length = (uint)Math.Min(value, uint.MaxValue);
        return true;
    }

    /// <summary>S8F3 - a bare <c>CPBODY</c>, with no identifier at all.</summary>
    /// <remarks>
    /// The shape is E5's and it is worth a second look: the <em>send</em> cannot name what it
    /// sends, while the <em>request</em> three functions later must. S8F1 does not name it either -
    /// it carries only a length. So the boot program is anonymous by design, because a machine has
    /// one of it.
    /// </remarks>
    internal static SecsItem BodyItem(ReadOnlyMemory<byte> body)
        => SecsItem.B(body.ToArray()).Named("CPBODY");

    /// <summary>Reads S8F3 - a bare <c>CPBODY</c>.</summary>
    /// <param name="item">The body.</param>
    /// <param name="body">Its bytes, copied.</param>
    /// <returns><see langword="false"/> when the item carries no bytes this end can take.</returns>
    /// <remarks>
    /// A zero-length program reads as <see langword="true"/> with nothing in it, because a host is
    /// entitled to send nothing and an <c>ACKC8</c> is the place to object - while a <em>list</em>
    /// reads as <see langword="false"/>, since S8F3's body is one item and a list is a machine
    /// sending some other message.
    /// </remarks>
    internal static bool TryReadProgramBody(SecsItem? item, out ReadOnlyMemory<byte> body)
    {
        body = default;

        if (item is null || item.IsList)
        {
            return false;
        }

        if (item.Count == 0)
        {
            return true;
        }

        if (!item.Format.IsText() && item.Format is not SecsFormat.Binary)
        {
            return false;
        }

        body = Bytes(item);
        return true;
    }

    // ------------------------------------------------------- named control programs

    /// <summary>S8F5, S8F13 and S8F15 - a bare <c>CPID</c>.</summary>
    internal static SecsItem IdBody(string controlProgramId)
        => SecsItem.A(Truncate(controlProgramId, IdLength)).Named("CPID");

    /// <summary>Reads a bare <c>CPID</c>, descending into a list of one.</summary>
    internal static bool TryReadId(SecsItem? item, out string controlProgramId)
    {
        controlProgramId = string.Empty;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadId(item.Items[0], out controlProgramId);
        }

        if (item.Count == 0)
        {
            return false;
        }

        controlProgramId = Word(item);
        return true;
    }

    /// <summary>S8F6 - <c>L,2 { CPID, CPBODY }</c>.</summary>
    /// <param name="controlProgramId">The identifier, echoed from the request.</param>
    /// <param name="body">
    /// The program. Empty writes a zero-length <c>B</c>, which is how S8F6 says the machine has no
    /// such program - the reply has no acknowledgement code to say it with.
    /// </param>
    internal static SecsItem ProgramBody(string controlProgramId, ReadOnlyMemory<byte> body)
        => SecsItem.L(
            IdBody(controlProgramId),
            BodyItem(body));

    /// <summary>Reads S8F6.</summary>
    internal static bool TryReadProgram(SecsItem? body, out string controlProgramId, out ReadOnlyMemory<byte> program)
    {
        controlProgramId = string.Empty;
        program = default;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        controlProgramId = Word(body.Items[0]);
        program = Bytes(body.Items[1]);

        return true;
    }

    /// <summary>S8F12 - <c>L,n { CPID }</c>.</summary>
    /// <remarks>
    /// Written as a list of items, the way every repeating body in this stack is. Stream 7's
    /// directory - S7F8 - has the identical shape, which is one more place the two designs agree.
    /// </remarks>
    internal static SecsItem DirectoryBody(IReadOnlyList<string>? controlProgramIds)
    {
        if (controlProgramIds is null || controlProgramIds.Count == 0)
        {
            return SecsItem.L();
        }

        var items = new SecsItem[controlProgramIds.Count];

        for (var i = 0; i < controlProgramIds.Count; i++)
        {
            items[i] = IdBody(controlProgramIds[i]);
        }

        return SecsItem.L(items);
    }

    /// <summary>Reads S8F12, nested or flat.</summary>
    /// <remarks>
    /// Three shapes: a list of identifiers, which is what this library writes; a list of lists of
    /// one, which is a machine that wrapped each of them; and a bare identifier, which is a machine
    /// with one program that did not bother with a list. An entry that is not text is skipped
    /// rather than read as empty - an empty identifier is the boot program here, and a directory
    /// that claimed one would be saying something E5 does not.
    /// </remarks>
    internal static List<string> ReadDirectory(SecsItem? body)
    {
        List<string> controlProgramIds = [];

        if (body is null || body.Count == 0)
        {
            return controlProgramIds;
        }

        if (!body.IsList)
        {
            if (TryReadId(body, out var bare) && bare.Length > 0)
            {
                controlProgramIds.Add(bare);
            }

            return controlProgramIds;
        }

        foreach (var entry in body.Items)
        {
            if (TryReadId(entry, out var id) && id.Length > 0)
            {
                controlProgramIds.Add(id);
            }
        }

        return controlProgramIds;
    }

    /// <summary>S8F16 - <c>L,2 { CPID, SOFTREV }</c>.</summary>
    internal static SecsItem RevisionBody(string controlProgramId, string revision)
        => SecsItem.L(
            IdBody(controlProgramId),
            SecsItem.A(Truncate(revision, RevisionLength)).Named("SOFTREV"));

    /// <summary>Reads S8F16.</summary>
    internal static bool TryReadRevision(SecsItem? body, out string controlProgramId, out string revision)
    {
        controlProgramId = string.Empty;
        revision = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        controlProgramId = Word(body.Items[0]);
        revision = Word(body.Items[1]);

        return true;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body - <c>GRANT</c> or <c>ACKC8</c>.</summary>
    internal static SecsItem CodeBody(byte code, string name) => SecsItem.B(code).Named(name);

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
            return null;
        }

        if (item.Format is SecsFormat.Binary or SecsFormat.U1)
        {
            return item.FirstValueOrDefault<byte>();
        }

        return TryReadUnsigned(item, out var value) && value <= byte.MaxValue ? (byte)value : null;
    }

    // ------------------------------------------------------------------- the pieces

    /// <summary>Reads a <c>CPBODY</c> out of whatever item carried it, copying the bytes.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The copy is the point.</strong> An item decoded off the wire borrows pooled memory
    /// and is released when its message is disposed, so a store that kept the span would be holding
    /// a buffer the next message is about to write into. Stream 13 met the same problem and needed
    /// a whole type for it, because its payload has no format; here the format is known, so a
    /// <c>byte[]</c> is the whole of the answer.
    /// </para>
    /// <para>
    /// A <c>B</c> is read as itself, which is what this package writes. A text item is read as its
    /// characters - a machine that sends firmware as <c>A</c> is unusual but is still saying
    /// something, and refusing it would throw away a program over its envelope. A list carries no
    /// program and reads as empty.
    /// </para>
    /// </remarks>
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
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
    }

    private static string Truncate(string? value, int length)
        => value is null ? string.Empty : value.Length <= length ? value : value[..length];

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// The only numbers in the twelve rows this package implements are a length and a code, so a
    /// value too large for its field is clamped rather than refused - the same rule the other E5
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
