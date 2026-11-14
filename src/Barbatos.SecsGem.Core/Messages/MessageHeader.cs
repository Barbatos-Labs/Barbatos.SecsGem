// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// The 10-byte HSMS message header, stored inline with no heap allocation.
/// </summary>
/// <remarks>
/// <para>
/// The struct <em>is</em> the wire format: <see cref="InlineArrayAttribute"/> makes it ten
/// contiguous bytes, and the language's implicit conversion to <see cref="Span{T}"/> means it
/// can be read from and written to a socket buffer with no copy and no marshalling:
/// </para>
/// <code>
/// ReadOnlySpan&lt;byte&gt; bytes = header;      // no allocation, no copy
/// sequence.Slice(0, 10).CopyTo(header);   // fills the struct in place
/// </code>
/// <para>Byte layout, per SEMI E37:</para>
/// <list type="table">
///   <item><term>0-1</term><description><see cref="SessionId"/> (device id)</description></item>
///   <item><term>2</term><description><see cref="ReplyExpected"/> (bit 7) and <see cref="Stream"/> (bits 0-6)</description></item>
///   <item><term>3</term><description><see cref="Function"/>, or the status byte of a control response</description></item>
///   <item><term>4</term><description><see cref="PType"/> - <c>0</c> means the body is SECS-II</description></item>
///   <item><term>5</term><description><see cref="MessageType"/> (SType)</description></item>
///   <item><term>6-9</term><description><see cref="SystemBytes"/></description></item>
/// </list>
/// <para>
/// Unlike the equivalent struct in secs4net, every property here is order-independent: setting
/// <see cref="Stream"/> after <see cref="ReplyExpected"/> preserves the W-bit and vice versa,
/// so an object initializer cannot silently drop it. <see cref="PType"/> is also mapped, which
/// is what lets the connection reject a non-SECS-II body instead of trying to decode it.
/// </para>
/// </remarks>
[InlineArray(Length)]
public struct MessageHeader
{
    /// <summary>The length of an HSMS message header in bytes.</summary>
    public const int Length = 10;

    private byte _element0;

    /// <summary>Gets or initialises the session id (the device id), header bytes 0-1.</summary>
    /// <remarks>Control messages use <c>0xFFFF</c>.</remarks>
    public ushort SessionId
    {
        get => BinaryPrimitives.ReadUInt16BigEndian(this[..2]);
        init => BinaryPrimitives.WriteUInt16BigEndian(this[..2], value);
    }

    /// <summary>Gets the 15-bit device id - <see cref="SessionId"/> with the high bit masked off.</summary>
    /// <remarks>
    /// SECS-I reserves bit 15 of the first header word as the R-bit (reverse bit). HSMS has no
    /// R-bit, but equipment that bridges the two still sets it, so compare device ids with this
    /// property and keep <see cref="SessionId"/> for the value that goes back on the wire.
    /// </remarks>
    public ushort DeviceId => (ushort)(SessionId & 0x7FFF);

    /// <summary>Gets or initialises the message stream (S), header byte 2 bits 0-6.</summary>
    /// <remarks>Writing this preserves the W-bit, so initialiser order does not matter.</remarks>
    public byte Stream
    {
        get => (byte)(this[2] & 0b0111_1111);
        init => this[2] = (byte)((this[2] & 0b1000_0000) | (value & 0b0111_1111));
    }

    /// <summary>Gets or initialises the W-bit - whether the sender expects a reply. Header byte 2 bit 7.</summary>
    /// <remarks>Writing this preserves <see cref="Stream"/>, so initialiser order does not matter.</remarks>
    public bool ReplyExpected
    {
        get => (this[2] & 0b1000_0000) != 0;
        init => this[2] = (byte)((this[2] & 0b0111_1111) | (value ? 0b1000_0000 : 0));
    }

    /// <summary>Gets or initialises the message function (F), header byte 3.</summary>
    /// <remarks>
    /// On a control response this same byte carries the status - read it through
    /// <see cref="SelectStatus"/>, <see cref="DeselectStatus"/> or <see cref="RejectReason"/>.
    /// </remarks>
    public byte Function
    {
        get => this[3];
        init => this[3] = value;
    }

    /// <summary>Gets or initialises the presentation type, header byte 4. <c>0</c> means the body is SECS-II.</summary>
    /// <remarks>
    /// A non-zero PType is a body this stack cannot decode; the connection answers it with
    /// <see cref="MessageType.RejectRequest"/> and <see cref="RejectReason.PtypeNotSupported"/>
    /// rather than attempting to parse it.
    /// </remarks>
    public byte PType
    {
        get => this[4];
        init => this[4] = value;
    }

    /// <summary>Gets or initialises the HSMS message type (SType), header byte 5.</summary>
    public MessageType MessageType
    {
        get => (MessageType)this[5];
        init => this[5] = (byte)value;
    }

    /// <summary>Gets or initialises the system bytes - the transaction id - header bytes 6-9.</summary>
    public int SystemBytes
    {
        get => BinaryPrimitives.ReadInt32BigEndian(this[6..]);
        init => BinaryPrimitives.WriteInt32BigEndian(this[6..], value);
    }

    /// <summary>Gets the select status of a <see cref="MessageType.SelectResponse"/>.</summary>
    public SelectStatus SelectStatus => (SelectStatus)this[3];

    /// <summary>Gets the deselect status of a <see cref="MessageType.DeselectResponse"/>.</summary>
    public DeselectStatus DeselectStatus => (DeselectStatus)this[3];

    /// <summary>Gets the reason of a <see cref="MessageType.RejectRequest"/>.</summary>
    public RejectReason RejectReason => (RejectReason)this[3];

    /// <summary>Gets a value indicating whether this header describes a SECS-II data message with a decodable body.</summary>
    public bool IsDataMessage => MessageType == MessageType.DataMessage && PType == 0;

    /// <summary>Gets a value indicating whether this header describes a primary message (an odd function).</summary>
    public bool IsPrimary => (Function & 1) != 0;

    /// <summary>Reads a header from the first <see cref="Length"/> bytes of <paramref name="source"/>.</summary>
    /// <param name="source">The bytes to read.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> holds fewer than <see cref="Length"/> bytes.</exception>
    public static MessageHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.Length, $"An HSMS header needs {Length} bytes.");
        }

        MessageHeader header = default;
        source[..Length].CopyTo(header);
        return header;
    }

    /// <summary>Reads a header from the first <see cref="Length"/> bytes of <paramref name="source"/>.</summary>
    /// <param name="source">The sequence to read, typically a slice straight off a <see cref="System.IO.Pipelines.PipeReader"/>.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> holds fewer than <see cref="Length"/> bytes.</exception>
    public static MessageHeader Parse(in ReadOnlySequence<byte> source)
    {
        if (source.Length < Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source.Length, $"An HSMS header needs {Length} bytes.");
        }

        MessageHeader header = default;
        source.Slice(0, Length).CopyTo(header);
        return header;
    }

    /// <summary>Copies this header into <paramref name="destination"/>.</summary>
    /// <param name="destination">A span of at least <see cref="Length"/> bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is too short.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), destination.Length, $"An HSMS header needs {Length} bytes.");
        }

        ((ReadOnlySpan<byte>)this).CopyTo(destination);
    }

    /// <summary>Returns a diagnostic rendering of the header.</summary>
    /// <returns>For a data message, <c>S1F3 W [0x0000002A]</c>; for a control message, its type.</returns>
    public override string ToString()
        => MessageType == MessageType.DataMessage
            ? $"S{Stream}F{Function}{(ReplyExpected ? " W" : string.Empty)} [0x{SystemBytes:X8}]"
            : $"{MessageType} [0x{SystemBytes:X8}]";
}
