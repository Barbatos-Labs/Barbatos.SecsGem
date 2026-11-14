// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Declares that a type <em>is</em> a particular SECS-II message, so it can be sent and received
/// without the caller ever naming a stream and function.
/// </summary>
/// <remarks>
/// <para>
/// Optional, like every attribute in this namespace. Without it a type still maps to an item
/// tree through <see cref="SecsSerializer.Serialize{T}"/>; with it the type also maps to a whole
/// message:
/// </para>
/// <code>
/// [SecsMessage(S = 1, F = 3, ReplyExpected = true, Name = "GetEquipmentStatus")]
/// public sealed record SelectedEquipmentStatusRequest(uint[] Svids);
///
/// var message = SecsSerializer.ToMessage(new SelectedEquipmentStatusRequest([1, 2, 3]));
/// // 'S1F3' W  &lt;L [1] &lt;U4 [3] 1 2 3&gt;&gt;
/// </code>
/// <para>
/// <see cref="SecsSerializer.FromMessage{T}"/> checks the incoming S and F against this
/// attribute, so a handler registered for S1F3 cannot silently deserialise an S1F5.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class SecsMessageAttribute : Attribute
{
    /// <summary>Initialises a new instance.</summary>
    public SecsMessageAttribute()
    {
    }

    /// <summary>Initialises a new instance for the given stream and function.</summary>
    /// <param name="s">The stream number.</param>
    /// <param name="f">The function number.</param>
    public SecsMessageAttribute(byte s, byte f)
    {
        S = s;
        F = f;
    }

    /// <summary>Gets or sets the stream number.</summary>
    public byte S { get; set; }

    /// <summary>Gets or sets the function number.</summary>
    public byte F { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a reply is expected. Defaults to
    /// <see langword="true"/> for a primary message - an odd <see cref="F"/> - and
    /// <see langword="false"/> otherwise.
    /// </summary>
    /// <remarks>
    /// Left unset, the default follows the protocol rather than the language: a secondary
    /// message never carries the W-bit, so defaulting it to <see langword="true"/> for every
    /// type would be wrong more often than it was right.
    /// </remarks>
    public bool? ReplyExpected { get; set; }

    /// <summary>Gets or sets the message name carried by SML, JSON and XML.</summary>
    /// <remarks>Defaults to the type's name when not set.</remarks>
    public string? Name { get; set; }

    /// <summary>Gets whether a reply is expected, applying the default for this function number.</summary>
    /// <returns><see cref="ReplyExpected"/> when set, otherwise whether <see cref="F"/> is odd.</returns>
    public bool GetReplyExpected() => ReplyExpected ?? (F & 1) != 0;
}
