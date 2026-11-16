// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material;

/// <summary>The answer to anything in stream 3 that is acknowledged - <c>ACKC3</c>.</summary>
/// <remarks>
/// Two codes, and E5 gives this stream no third. A denial carries no reason with it - there is no
/// error code anywhere in the material half of stream 3 - so a machine that needs to explain
/// itself does so through an alarm in stream 5 or an error report in stream 9, and this package
/// has nothing to offer in between.
/// </remarks>
public enum MaterialAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Denied.</summary>
    Denied = 1,
}

/// <summary>The answer to a multi-block inquire - <c>GRANT</c>.</summary>
/// <remarks>
/// The same shape as the grant in every other multi-block handshake in E5: a large body is offered
/// before it is sent, so the receiver can refuse it for the price of two small messages.
/// </remarks>
public enum MaterialGrant : byte
{
    /// <summary>Send it.</summary>
    Accepted = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>No space available for it.</summary>
    NoSpace = 2,

    /// <summary>The data identifier was a duplicate, or not one this end recognises.</summary>
    DuplicateOrInvalid = 3,

    /// <summary>Some other refusal: E5 reserves 4 to 63 for the equipment to define.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>Material, and where it is - <c>MID</c> and <c>LOC</c>.</summary>
/// <remarks>
/// The pair that most of stream 3 is made of. A location is a number whose meaning belongs to the
/// machine: a load port, a slot, a stocker shelf. Nothing here interprets it.
/// </remarks>
public sealed class MaterialLocation
{
    /// <summary>What the material is called - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where it is - <c>LOC</c>.</summary>
    public uint Location { get; init; }
}

/// <summary>Material, where it is, and how much of it - <c>MID</c>, <c>LOC</c> and <c>QUA</c>.</summary>
public sealed class MaterialStatus
{
    /// <summary>What the material is called - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where it is - <c>LOC</c>.</summary>
    public uint Location { get; init; }

    /// <summary>How much of it is there - <c>QUA</c>.</summary>
    /// <remarks>
    /// One byte, which is what the catalogue gives it, and no code values with it. E5 uses
    /// <c>QUA</c> for the quantity at the location; what a quantity counts - wafers in a cassette,
    /// cassettes on a shelf - belongs to the machine.
    /// </remarks>
    public byte Quantity { get; init; }
}

/// <summary>Material, where it is, and how long it has left - <c>MID</c>, <c>LOC</c> and <c>TTC</c>.</summary>
public sealed class MaterialCompletion
{
    /// <summary>What the material is called - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where it is - <c>LOC</c>.</summary>
    public uint Location { get; init; }

    /// <summary>How long until it is done - <c>TTC</c>.</summary>
    /// <remarks>
    /// Not a <see cref="TimeSpan"/>, and that is deliberate. <c>TTC</c> is a <c>U4</c> and the
    /// catalogue gives it no unit; implementations in the field generally mean seconds, but a
    /// property typed as a duration would be this library asserting that, and a machine that
    /// counts in minutes would be read wrong by a factor of sixty with nothing to show it.
    /// </remarks>
    public uint TimeToCompletion { get; init; }
}

/// <summary>Where material should go - <c>MID</c> and <c>DESTLOC</c>.</summary>
/// <remarks>
/// <strong>On <c>DESTLOC</c>.</strong> The catalogue has no dictionary entry for it, so its format
/// is inferred: it travels as the <c>U4</c> that <c>LOC</c> is, because it is a location and the
/// two appear beside each other in S3F35. That is the safest inference in this package and still
/// an inference. See the remarks on <see cref="MaterialStatusChange.Status"/> for the other one,
/// and the codec for where to change either.
/// </remarks>
public sealed class MaterialTransfer
{
    /// <summary>What to move - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where to move it to - <c>DESTLOC</c>.</summary>
    public uint Destination { get; init; }
}

/// <summary>A change in what material is doing - <c>MID</c>, <c>LOC</c> and <c>MATLSTATUS</c>.</summary>
public sealed class MaterialStatusChange
{
    /// <summary>What the material is called - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where it is - <c>LOC</c>.</summary>
    public uint Location { get; init; }

    /// <summary>What it is now - <c>MATLSTATUS</c>.</summary>
    /// <remarks>
    /// <strong>The weakest inference in this package.</strong> The catalogue has no dictionary
    /// entry for <c>MATLSTATUS</c> - no format and no code values - so this is a byte, written as
    /// the <c>B[1]</c> that every other single-byte field in stream 3 uses, and read out of any
    /// numeric format or out of text that parses as a number. The codes are the machine's: nothing
    /// here names them, because there is nothing to name them from.
    /// </remarks>
    public byte Status { get; init; }
}

/// <summary>Material moving - <c>MID</c>, <c>LOC</c> and <c>DESTLOC</c>.</summary>
public sealed class MaterialMovement
{
    /// <summary>What moved - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>Where it came from - <c>LOC</c>.</summary>
    public uint Location { get; init; }

    /// <summary>Where it went - <c>DESTLOC</c>.</summary>
    public uint Destination { get; init; }
}

/// <summary>Material found, or material lost - S3F5 and S3F7.</summary>
/// <remarks>
/// A handler may set <see cref="Acknowledgement"/>. Everything it does not set is already the
/// right answer: <see cref="MaterialAcknowledgement.Accepted"/>, since a host that is told where
/// something is has no grounds to deny being told.
/// </remarks>
public sealed class MaterialLocationEventArgs : EventArgs
{
    /// <summary>What was found or lost, and where.</summary>
    /// <remarks>Valid for the life of the handler. Anything kept past it must be copied.</remarks>
    public required MaterialLocation Material { get; init; }

    /// <summary>Gets or sets what goes back as <c>ACKC3</c>.</summary>
    public MaterialAcknowledgement Acknowledgement { get; set; } = MaterialAcknowledgement.Accepted;
}

/// <summary>A material status change that arrived - S3F13.</summary>
public sealed class MaterialStatusChangeEventArgs : EventArgs
{
    /// <summary>What changed.</summary>
    /// <remarks>Valid for the life of the handler.</remarks>
    public required MaterialStatusChange Change { get; init; }

    /// <summary>Gets or sets what goes back as <c>ACKC3</c>.</summary>
    public MaterialAcknowledgement Acknowledgement { get; set; } = MaterialAcknowledgement.Accepted;
}

/// <summary>A material movement event that arrived - S3F35.</summary>
public sealed class MaterialMovementEventArgs : EventArgs
{
    /// <summary>What moved.</summary>
    /// <remarks>Valid for the life of the handler.</remarks>
    public required MaterialMovement Movement { get; init; }

    /// <summary>Gets or sets what goes back as <c>ACKC3</c>.</summary>
    public MaterialAcknowledgement Acknowledgement { get; set; } = MaterialAcknowledgement.Accepted;
}
