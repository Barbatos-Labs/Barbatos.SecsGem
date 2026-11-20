// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The eight stream 3 transactions E87 drives, written and read in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why eight and not nine.</strong> Stream 3's carrier half is S3F17 to S3F34, nine
/// request-and-reply pairs. Three of them need an item the catalogue has no entry for -
/// <c>CARRIERSTATUS</c> on S3F20, <c>PORTSTATUS</c> on S3F22 and S3F32, <c>SLOTMAP</c> on S3F28 -
/// and two of those three are carried anyway, because this package already serves the same values
/// as stream 14 attributes and so had a reading to check against rather than one to invent. The
/// one left out is S3F19/F20: <c>CARRIERSTATUS</c> is a single item where a carrier has three
/// state machines, and nothing in E87 crowns one of them. <see cref="CarrierObjectProvider"/>
/// serves all three by name instead.
/// </para>
/// <code>
/// S3F17  L,3 { CARRIERACTION, CARRIERID, PTN }   the one that matters: every action, carrier and port
/// S3F18  L,3 { CAACK, ERRCODE, ERRTEXT }         accepted, or refused with the state that refused it
/// S3F21  PTN                  -&gt; S3F22  L,3 { PTN, PORTSTATUS, CARRIERID }
/// S3F23  CARRIERID            -&gt; S3F24  L,2 { CARRIERID, LOC }
/// S3F25  PTN                  -&gt; S3F26  L,2 { ACKC3, CARRIERID }
/// S3F27  CARRIERID            -&gt; S3F28  L,2 { CARRIERID, SLOTMAP }
/// S3F29  L,2 { CARRIERID, PTN } -&gt; S3F30  ACKC3
/// S3F31  (nothing)            -&gt; S3F32  L,n { L,3 { LOC, PORTSTATUS, CARRIERID } }
/// S3F33  CARRIERID            -&gt; S3F34  ACKC3
/// </code>
/// <para>
/// Not one row of stream 3 is marked as a definition in the catalogue rather than as part of a
/// representative subset, so these bodies are a careful reading of the item lists. What is firm is
/// the vocabulary: <c>CAACK</c> has its four codes, <c>ACKC3</c> its two, and
/// <c>CARRIERACTION</c> is an <c>A</c> item - free text - which is why the action names in
/// <see cref="CarrierActions"/> are strings and not an enum.
/// </para>
/// <para>
/// <strong>An absent port.</strong> S3F17 always carries three items, so an action that names no
/// port sends a zero-length <c>PTN</c> rather than a zero - port 0 is a port, and a machine that
/// read "no port" as "port 0" would reserve the wrong hardware. The reader turns a zero-length
/// item back into no port.
/// </para>
/// </remarks>
internal static class CarrierCodec
{
    /// <summary>S3F17 - Carrier Action Request.</summary>
    internal static SecsItem ActionBody(CarrierActionRequest request)
        => SecsItem.L(
            SecsItem.A(request.Action).Named("CARRIERACTION"),
            SecsItem.A(request.CarrierId).Named("CARRIERID"),
            (request.PortId is { } port ? SecsItem.U1(port) : SecsItem.U1()).Named("PTN"));

    /// <summary>Reads S3F17.</summary>
    internal static bool TryReadAction(SecsItem? body, out CarrierActionRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        var action = Text(body.Items[0]);

        if (action.Length == 0)
        {
            return false;
        }

        request = new CarrierActionRequest
        {
            Action = action,
            CarrierId = Text(body.Items[1]),
            PortId = body.Count > 2 ? Byte(body.Items[2]) : null,
        };

        return true;
    }

    /// <summary>S3F18 - Carrier Action Acknowledge.</summary>
    internal static SecsItem ActionResultBody(CarrierActionResult result)
        => SecsItem.L(
            SecsItem.U1((byte)result.Acknowledgement).Named("CAACK"),
            SecsItem.U4(result.ErrorCode).Named("ERRCODE"),
            SecsItem.A(result.ErrorText).Named("ERRTEXT"));

    /// <summary>Reads S3F18.</summary>
    /// <param name="body">The reply body.</param>
    /// <returns>The result, or <see langword="null"/> when there is no acknowledgement code in it.</returns>
    /// <remarks>
    /// The code is the first item and the error pair follows it, flat. A machine that nests the
    /// error pair in a list - which some revisions of E87 write - still reads: the code is taken
    /// from the front and the text from the first text item anywhere after it.
    /// </remarks>
    internal static CarrierActionResult? TryReadActionResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (!body.IsList)
        {
            return ReadCode(body) is { } bare
                ? new CarrierActionResult { Acknowledgement = Acknowledgement(bare) }
                : null;
        }

        if (body.Count == 0 || ReadCode(body.Items[0]) is not { } code)
        {
            return null;
        }

        return new CarrierActionResult
        {
            Acknowledgement = Acknowledgement(code),
            ErrorCode = FindErrorCode(body, start: 1),
            ErrorText = FindErrorText(body, start: 1),
        };
    }

    /// <summary>S3F23 and S3F33 - one bare <c>CARRIERID</c>.</summary>
    internal static SecsItem CarrierIdBody(string carrierId) => SecsItem.A(carrierId).Named("CARRIERID");

    /// <summary>S3F25 - one bare <c>PTN</c>.</summary>
    internal static SecsItem PortBody(byte portId) => SecsItem.U1(portId).Named("PTN");

    /// <summary>S3F29 - Carrier Reservation Request.</summary>
    internal static SecsItem ReservationBody(string carrierId, byte portId)
        => SecsItem.L(
            SecsItem.A(carrierId).Named("CARRIERID"),
            SecsItem.U1(portId).Named("PTN"));

    /// <summary>Reads S3F29.</summary>
    internal static bool TryReadReservation(SecsItem? body, out string carrierId, out byte portId)
    {
        carrierId = string.Empty;
        portId = 0;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        carrierId = Text(body.Items[0]);

        if (Byte(body.Items[1]) is not { } port)
        {
            return false;
        }

        portId = port;

        return true;
    }

    /// <summary>S3F24 - Carrier Location Data.</summary>
    internal static SecsItem LocationBody(string carrierId, uint location)
        => SecsItem.L(
            SecsItem.A(carrierId).Named("CARRIERID"),
            SecsItem.U4(location).Named("LOC"));

    /// <summary>Reads S3F24. A zero-length list means the machine does not have that carrier.</summary>
    internal static bool TryReadLocation(SecsItem? body, out string carrierId, out uint? location)
    {
        carrierId = string.Empty;
        location = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 2)
        {
            return false;
        }

        carrierId = Text(body.Items[0]);
        location = UInt32(body.Items[1]);

        return true;
    }

    /// <summary>S3F22 - Storage Port Status Data.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="status">What <c>PORTSTATUS</c> carries - see the remarks.</param>
    /// <param name="carrierId">The carrier on it, or empty.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// <para>
    /// <c>PORTSTATUS</c> has no dictionary entry, so its format is this library's reading and the
    /// reading is not a guess: it is an <c>A</c> item carrying a
    /// <see cref="PortTransferState"/> by name, which is <strong>byte for byte the
    /// <c>TransferState</c> attribute this same package already serves over stream 14</strong>
    /// from <see cref="CarrierObjectProvider"/>. A test asserts the two agree, so the two ways a
    /// host can ask this machine what a port is doing cannot drift apart.
    /// </para>
    /// <para>
    /// Of a port's four state machines it is the transfer state because that is the one E87 calls
    /// what a material handler actually looks at, and because it is the one <c>CARRIERID</c>
    /// beside it is about: a port that can be unloaded has something on it.
    /// </para>
    /// </remarks>
    internal static SecsItem PortStatusBody(byte portId, string status, string carrierId)
        => SecsItem.L(
            SecsItem.U1(portId).Named("PTN"),
            SecsItem.A(status).Named("PORTSTATUS"),
            SecsItem.A(carrierId).Named("CARRIERID"));

    /// <summary>Reads S3F22. A zero-length list means the machine does not have that port.</summary>
    /// <param name="body">What came back.</param>
    /// <param name="status">What <c>PORTSTATUS</c> carried, as text, whatever it was.</param>
    /// <param name="portId">Which port it is about.</param>
    /// <param name="carrierId">The carrier on it.</param>
    /// <returns><see langword="true"/> when the body was one this reader understands.</returns>
    /// <remarks>
    /// The text comes back as it arrived rather than parsed into an enum here. An undefined item
    /// read through a tolerant reader must not turn an unfamiliar word into
    /// <see cref="PortTransferState.OutOfService"/> just because that is the zero of the enum -
    /// another vendor's port status would then arrive as a port nobody may use.
    /// </remarks>
    internal static bool TryReadPortStatus(
        SecsItem? body,
        out string status,
        out byte portId,
        out string carrierId)
    {
        status = string.Empty;
        portId = 0;
        carrierId = string.Empty;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 3)
        {
            return false;
        }

        portId = TryReadPort(body.Items[0]) ?? 0;
        status = Text(body.Items[1]);
        carrierId = Text(body.Items[2]);

        return true;
    }

    /// <summary>S3F28 - Slot Map Data.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="slotMap">One byte a slot.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// <c>SLOTMAP</c> has no dictionary entry either, and this one needed no choosing at all: a
    /// <c>B</c> item of one byte a slot is what <see cref="Carrier.SlotMap"/> already holds and
    /// what <see cref="CarrierObjectProvider"/> already serves as the <c>SlotMap</c> attribute
    /// over stream 14. The codes inside stay the equipment's, as they are everywhere else in this
    /// package.
    /// </remarks>
    internal static SecsItem SlotMapBody(string carrierId, ReadOnlyMemory<byte> slotMap)
        => SecsItem.L(
            SecsItem.A(carrierId).Named("CARRIERID"),
            SecsItem.B([.. slotMap.Span]).Named("SLOTMAP"));

    /// <summary>Reads S3F28. A zero-length list means the machine does not have that carrier.</summary>
    /// <param name="body">What came back.</param>
    /// <param name="carrierId">Which carrier it is about.</param>
    /// <param name="slotMap">The bytes, or empty.</param>
    /// <returns><see langword="true"/> when the body was one this reader understands.</returns>
    /// <remarks>
    /// The bytes are copied out rather than handed on, and that copy is the one line here no test
    /// can reach: the item's memory may be pooled and the caller disposes the reply before it
    /// looks, so handing on a view would read whatever the pool was used for next - which is a
    /// bug that passes a thousand times and then does not. A test could only catch it by relying
    /// on when the allocator reuses a buffer, which is the flaky kind this repository does not
    /// write.
    /// </remarks>
    internal static bool TryReadSlotMap(SecsItem? body, out string carrierId, out ReadOnlyMemory<byte> slotMap)
    {
        carrierId = string.Empty;
        slotMap = default;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 2)
        {
            return false;
        }

        carrierId = Text(body.Items[0]);

        // Copied out rather than handed on: the item's memory may be pooled and is gone when the
        // reply is disposed, and every other reader in this package hands back a value the caller
        // can keep.
        slotMap = body.Items[1].Format is SecsFormat.Binary
            ? (ReadOnlyMemory<byte>)body.Items[1].GetMemory<byte>().ToArray()
            : default;

        return true;
    }

    /// <summary>S3F32 - Storage Status Data.</summary>
    /// <param name="locations">Every storage location the machine has, ordered by <c>LOC</c>.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Plural, because the request names nothing.</strong> S3F31 has no items at all - it
    /// means <em>tell me about all of your storage locations</em> - so the three items of the
    /// reply have to repeat. An item list cannot say whether that is a list of three-element
    /// entries or a flat run, so this writes <c>L,n { L,3 { ... } }</c>, the shape
    /// <c>Barbatos.SecsGem.Material</c> settled on for S3F2 and S3F4 in this same stream for this
    /// same reason.
    /// </para>
    /// <para>
    /// <strong>A machine with no storage answers the empty list, and that is an answer.</strong>
    /// Everywhere else in this codec a zero-length list means <em>this machine will not say</em> -
    /// no such port, no such carrier, no agreed slot map. Here it means <em>I have no storage
    /// locations</em>, because the request named nothing that could be missing.
    /// </para>
    /// <para>
    /// <c>PORTSTATUS</c> is the item with no dictionary entry, and it is read here exactly as
    /// <see cref="PortStatusBody"/> reads it: an <c>A</c> item carrying a
    /// <see cref="PortTransferState"/> by name. One hop further from firm ground than S3F22 is,
    /// and worth saying out loud - S3F22's reading is checked against the <c>TransferState</c>
    /// attribute stream 14 serves, and there is no stream 14 attribute for a storage location to
    /// check this against. What there is instead is the item name: <c>PORTSTATUS</c> means in
    /// S3F32 what it means in S3F22, three transactions earlier in the same standard's half of
    /// the same stream, and a test asserts the two bodies carry the same bytes for the same state.
    /// </para>
    /// </remarks>
    internal static SecsItem StorageStatusBody(IReadOnlyList<(uint Location, string Status, string CarrierId)> locations)
    {
        var entries = new SecsItem[locations.Count];

        for (var i = 0; i < locations.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.U4(locations[i].Location).Named("LOC"),
                SecsItem.A(locations[i].Status).Named("PORTSTATUS"),
                SecsItem.A(locations[i].CarrierId).Named("CARRIERID"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S3F32. An empty list means the machine has no storage locations.</summary>
    /// <param name="body">What came back.</param>
    /// <param name="locations">The locations, in the order they arrived.</param>
    /// <returns><see langword="true"/> when the body was one this reader understands.</returns>
    /// <remarks>
    /// The status text comes back as it arrived as well as read into an enum, for the reason
    /// <see cref="TryReadPortStatus"/> gives: an undefined item must not turn another vendor's
    /// vocabulary into <see cref="PortTransferState.OutOfService"/> just because that is the zero
    /// of the enum.
    /// </remarks>
    internal static bool TryReadStorageStatus(SecsItem? body, out List<CarrierStorageStatus> locations)
    {
        locations = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in Entries(body, 3))
        {
            var status = Text(entry[1]);

            locations.Add(new CarrierStorageStatus
            {
                Location = UInt32(entry[0]) ?? 0,
                Status = status,
                TransferState = Enum.TryParse<PortTransferState>(status, out var parsed) ? parsed : null,
                CarrierId = Text(entry[2]),
            });
        }

        return true;
    }

    /// <summary>S3F26 - Carrier ID Read Acknowledge.</summary>
    internal static SecsItem IdReadBody(CarrierRequestAcknowledgement acknowledgement, string carrierId)
        => SecsItem.L(
            SecsItem.B((byte)acknowledgement).Named("ACKC3"),
            SecsItem.A(carrierId).Named("CARRIERID"));

    /// <summary>Reads S3F26.</summary>
    internal static bool TryReadIdRead(SecsItem? body, out CarrierRequestAcknowledgement acknowledgement, out string carrierId)
    {
        acknowledgement = CarrierRequestAcknowledgement.Denied;
        carrierId = string.Empty;

        if (body is null || !body.IsList || body.Count < 1 || ReadCode(body.Items[0]) is not { } code)
        {
            return false;
        }

        acknowledgement = code == 0 ? CarrierRequestAcknowledgement.Accepted : CarrierRequestAcknowledgement.Denied;

        if (body.Count > 1)
        {
            carrierId = Text(body.Items[1]);
        }

        return true;
    }

    /// <summary>S3F30 and S3F34 - one bare <c>ACKC3</c>.</summary>
    internal static SecsItem RequestResultBody(CarrierRequestAcknowledgement acknowledgement)
        => SecsItem.B((byte)acknowledgement).Named("ACKC3");

    /// <summary>Reads a bare <c>ACKC3</c>.</summary>
    internal static CarrierRequestAcknowledgement? TryReadRequestResult(SecsItem? body)
        => ReadCode(body) is { } code
            ? code == 0 ? CarrierRequestAcknowledgement.Accepted : CarrierRequestAcknowledgement.Denied
            : null;

    /// <summary>Turns a <c>CAACK</c> code into an acknowledgement, with anything unknown reading as a refusal.</summary>
    internal static CarrierAcknowledgement Acknowledgement(byte code)
        => code <= (byte)CarrierAcknowledgement.InvalidData
            ? (CarrierAcknowledgement)code
            : CarrierAcknowledgement.CannotPerformNow;

    /// <summary>Reads a bare text item, descending into a list of one.</summary>
    internal static string? TryReadText(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? TryReadText(item.Items[0]) : null;
        }

        return item.Format.IsText() ? Text(item) : null;
    }

    /// <summary>Reads a bare <c>PTN</c>, descending into a list of one.</summary>
    internal static byte? TryReadPort(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        return item.IsList
            ? item.Count == 1 ? TryReadPort(item.Items[0]) : null
            : Byte(item);
    }

    // ------------------------------------------------------------------ the pieces

    /// <summary>Finds <c>ERRCODE</c> after the acknowledgement, however deep it was nested.</summary>
    /// <param name="body">The list to search.</param>
    /// <param name="start">
    /// Where to start. One at the top level, because index nought there is the acknowledgement
    /// code itself and reading it as an error code would turn every refusal into error 2. Nought
    /// inside a nested list, where the first item is the error code - which is exactly what a
    /// machine that writes S3F18 as a code and a list of error pairs sends.
    /// </param>
    /// <returns>The code, or zero.</returns>
    private static uint FindErrorCode(SecsItem body, int start)
    {
        for (var i = start; i < body.Count; i++)
        {
            var item = body.Items[i];

            if (item.IsList)
            {
                var nested = FindErrorCode(item, start: 0);

                if (nested != 0)
                {
                    return nested;
                }

                continue;
            }

            if (!item.Format.IsText() && UInt32(item) is { } value && value != 0)
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>Finds <c>ERRTEXT</c> after the acknowledgement, however deep it was nested.</summary>
    /// <param name="body">The list to search.</param>
    /// <param name="start">As <see cref="FindErrorCode"/>: one at the top level, nought inside a nested list.</param>
    /// <returns>The text, or an empty string.</returns>
    private static string FindErrorText(SecsItem body, int start)
    {
        for (var i = start; i < body.Count; i++)
        {
            var item = body.Items[i];

            if (item.IsList)
            {
                var nested = FindErrorText(item, start: 0);

                if (nested.Length > 0)
                {
                    return nested;
                }

                continue;
            }

            if (item.Format.IsText() && item.Count > 0)
            {
                return Text(item);
            }
        }

        return string.Empty;
    }

    /// <summary>Splits a plural body into fixed-width entries, three ways.</summary>
    /// <param name="body">The list.</param>
    /// <param name="width">How many items an entry has.</param>
    /// <returns>One array of <paramref name="width"/> items per entry.</returns>
    /// <remarks>
    /// A list of lists is what this package writes; a flat run of fields repeated and a single
    /// bare entry are both things a machine may send instead, and an item list cannot say which
    /// was meant. Deliberately a copy of what <c>Barbatos.SecsGem.Material</c> does for S3F2
    /// rather than something shared: these two packages do not depend on each other, and a
    /// shared helper would be the first thread of a dependency between two standards that have
    /// nothing else to say to one another.
    /// </remarks>
    private static List<SecsItem[]> Entries(SecsItem body, int width)
    {
        List<SecsItem[]> entries = [];

        if (body.Count == 0)
        {
            return entries;
        }

        if (body.Items[0].IsList)
        {
            foreach (var entry in body.Items)
            {
                if (entry.IsList && entry.Count >= width)
                {
                    entries.Add([.. entry.Items[..width]]);
                }
            }

            return entries;
        }

        for (var i = 0; i + width <= body.Count; i += width)
        {
            entries.Add([.. body.Items[i..(i + width)]]);
        }

        return entries;
    }

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    /// <summary>Reads a port number, where a zero-length item means there is no port.</summary>
    private static byte? Byte(SecsItem item)
        => item.IsList || item.Count == 0
            ? null
            : UInt32(item) is { } value ? (byte)Math.Min(value, byte.MaxValue) : null;

    private static byte? ReadCode(SecsItem? item)
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

        return UInt32(item) is { } value && value <= byte.MaxValue ? (byte)value : null;
    }

    /// <summary>Reads an unsigned value in whatever numeric format arrived, or out of text.</summary>
    private static uint? UInt32(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return null;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>(),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>(),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>(),
            SecsFormat.U8 => NarrowUnsigned(item.FirstValueOrDefault<ulong>()),
            SecsFormat.I1 => Narrow(item.FirstValueOrDefault<sbyte>()),
            SecsFormat.I2 => Narrow(item.FirstValueOrDefault<short>()),
            SecsFormat.I4 => Narrow(item.FirstValueOrDefault<int>()),
            SecsFormat.I8 => Narrow(item.FirstValueOrDefault<long>()),
            SecsFormat.ASCII or SecsFormat.JIS8 =>
                uint.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };

        static uint? Narrow(long value) => value is >= 0 and <= uint.MaxValue ? (uint)value : null;

        static uint? NarrowUnsigned(ulong value) => value <= uint.MaxValue ? (uint)value : null;
    }
}
