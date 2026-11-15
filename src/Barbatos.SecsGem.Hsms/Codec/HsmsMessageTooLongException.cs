// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Thrown when a peer announces a message longer than
/// <see cref="SecsGemOptions.MaxMessageLength"/>.
/// </summary>
/// <remarks>
/// <para>
/// The length prefix is a number the <em>peer</em> chose, so it is checked before it is used to
/// size anything. This exception is what the check throws, and it carries both numbers so the
/// connection can answer with <c>S9F11 (Data Too Long)</c> and log something an engineer can act
/// on.
/// </para>
/// <para>
/// The frame body is deliberately <em>not</em> skipped: doing so would mean trusting the very
/// length that was just rejected. The byte stream is therefore no longer synchronised when this
/// is thrown, and the connection must be torn down and re-established. That is the safe reading
/// of E37 - a receiver may discard a too-long message, but it cannot know where the next one
/// starts.
/// </para>
/// </remarks>
public sealed class HsmsMessageTooLongException : SecsException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="length">The length the peer announced, in bytes.</param>
    /// <param name="limit">The configured limit, in bytes.</param>
    public HsmsMessageTooLongException(long length, int limit)
        : base($"A peer announced a {length:N0}-byte message, past the {limit:N0}-byte limit. The link is no longer synchronised.")
    {
        Length = length;
        Limit = limit;
    }

    /// <summary>Gets the length the peer announced, in bytes.</summary>
    public long Length { get; }

    /// <summary>Gets the configured limit that was exceeded, in bytes.</summary>
    public int Limit { get; }

    /// <summary>Gets the header of the offending message, when it was already buffered.</summary>
    /// <remarks>
    /// <para>
    /// S9F11 has to carry MHEAD, so the decoder fills this in when the ten header bytes happen to
    /// have arrived along with the length prefix - which, for a peer sending a large message in
    /// one go, is the usual case.
    /// </para>
    /// <para>
    /// It deliberately does not wait for them. The frame is being rejected precisely because its
    /// length cannot be trusted, and blocking under T8 for more bytes of a message already
    /// condemned would turn a clear rejection into a timeout. When this is <see langword="null"/>
    /// the S9F11 goes out with ten zero bytes, which is still more use to the peer than silence.
    /// </para>
    /// </remarks>
    public MessageHeader? Header { get; init; }
}
