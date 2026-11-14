// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// A SECS-II message: a stream and function number, whether a reply is expected, and an
/// optional item tree.
/// </summary>
/// <remarks>
/// <para>
/// A message owns its item tree: disposing the message disposes the tree, which returns any
/// pooled buffers the decoder rented. Dispose every message you receive.
/// </para>
/// <para>
/// The transaction id (the HSMS system bytes) is deliberately absent. It belongs to the
/// connection that sends the message, not to the message itself, which is what lets the same
/// <see cref="SecsMessage"/> be written to a file, replayed, or sent over two links.
/// </para>
/// </remarks>
public sealed class SecsMessage : IDisposable, IEquatable<SecsMessage>
{
    private readonly SecsItem? _item;
    private bool _disposed;

    /// <summary>Initialises a new message.</summary>
    /// <param name="s">The stream number. Must be 127 or less - bit 7 of that header byte is the W-bit.</param>
    /// <param name="f">The function number.</param>
    /// <param name="replyExpected">Whether the sender expects a reply (the W-bit).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="s"/> is greater than 127.</exception>
    public SecsMessage(byte s, byte f, bool replyExpected = true)
    {
        if (s > 0b0111_1111)
        {
            throw new ArgumentOutOfRangeException(
                nameof(s),
                s,
                "A SECS-II stream number cannot exceed 127: bit 7 of that header byte carries the W-bit.");
        }

        S = s;
        F = f;
        ReplyExpected = replyExpected;
    }

    /// <summary>Gets the stream number (S).</summary>
    public byte S { get; }

    /// <summary>Gets the function number (F).</summary>
    public byte F { get; }

    /// <summary>Gets or sets a value indicating whether a reply is expected (the W-bit).</summary>
    /// <remarks>
    /// Settable because a secondary message never carries the W-bit: the engine clears it when
    /// it sends a reply, whatever the caller built. Nothing else should write to it.
    /// </remarks>
    public bool ReplyExpected { get; set; }

    /// <summary>Gets or initialises an optional name, carried by SML, JSON and XML but never by the wire format.</summary>
    public string? Name { get; init; }

    /// <summary>Gets or initialises the root item, or <see langword="null"/> for a message with an empty body.</summary>
    /// <remarks>
    /// <see langword="null"/> and <c>&lt;L[0]&gt;</c> are different messages on the wire: the
    /// first has no body at all, the second has an empty list.
    /// </remarks>
    public SecsItem? Item
    {
        get => _item;
        init => _item = value;
    }

    /// <summary>Gets a value indicating whether this is a primary message (an odd function number).</summary>
    public bool IsPrimary => (F & 1) != 0;

    /// <summary>Gets the S9Fx error number when this is an error message, otherwise <c>0</c>.</summary>
    /// <remarks>
    /// <c>S9F1</c> unrecognised device id, <c>F3</c> unrecognised stream, <c>F5</c> unrecognised
    /// function, <c>F7</c> illegal data, <c>F9</c> transaction timer timeout, <c>F11</c> data too
    /// long, <c>F13</c> conversation timeout.
    /// </remarks>
    public byte S9ErrorFunction => S == 9 ? F : (byte)0;

    /// <summary>Releases the item tree, returning any pooled buffers.</summary>
    /// <remarks>Safe to call more than once.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _item?.Dispose();
    }

    /// <summary>Compares two messages by S, F, the W-bit, <see cref="Name"/> and the whole item tree.</summary>
    /// <param name="other">The message to compare with.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    /// <remarks>
    /// <see cref="Name"/> is part of equality because the round-trip tests assert that a message
    /// survives a conversion through every text format, and the name is part of what those
    /// formats carry.
    /// </remarks>
    public bool Equals(SecsMessage? other)
        => other is not null
        && S == other.S
        && F == other.F
        && ReplyExpected == other.ReplyExpected
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && (_item is null ? other._item is null : _item.Equals(other._item));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SecsMessage);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(S, F, ReplyExpected, Name);

    /// <summary>Returns the SML-style header line of this message.</summary>
    /// <returns>For example <c>GetEquipmentStatus: 'S1F3' W</c>.</returns>
    public override string ToString()
    {
        var name = string.IsNullOrEmpty(Name) ? string.Empty : $"{Name}: ";
        var w = ReplyExpected ? " W" : string.Empty;
        return $"{name}'S{S}F{F}'{w}";
    }
}
