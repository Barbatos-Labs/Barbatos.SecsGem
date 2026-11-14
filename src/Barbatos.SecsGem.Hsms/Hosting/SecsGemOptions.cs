// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.



namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Which side of the connection this entity is.
/// </summary>
public enum HsmsConnectionMode
{
    /// <summary>Passive: bind a port and wait to be connected to. The usual role for equipment.</summary>
    Passive,

    /// <summary>Active: connect out to the peer. The usual role for a host.</summary>
    Active,
}

/// <summary>
/// Everything a SECS/GEM connection is configured with. Bound from <c>Barbatos:SecsGem</c>.
/// </summary>
/// <remarks>
/// <para>
/// All five HSMS timers are here, and all five are real - see
/// <see cref="Barbatos.SecsGem.SecsTimer"/> for what each one measures. T8 in particular is
/// enforced by the decoder and scoped to the gap between chunks of a single message, never to an
/// idle link: a quiet connection is normal, a message that stops half-way is not.
/// </para>
/// <para>
/// Every timer is a <see cref="TimeSpan"/> rather than a count of milliseconds, so a
/// configuration file reads <c>"T3": "00:00:45"</c> and nobody has to remember the unit.
/// </para>
/// </remarks>
public sealed class SecsGemOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Barbatos:SecsGem";

    /// <summary>Gets or sets the device id - the HSMS session id. Defaults to 0.</summary>
    public ushort DeviceId { get; set; }

    /// <summary>Gets or sets whether this entity connects out or waits to be connected to. Defaults to <see cref="HsmsConnectionMode.Passive"/>.</summary>
    public HsmsConnectionMode Mode { get; set; } = HsmsConnectionMode.Passive;

    /// <summary>
    /// Gets or sets the address: the peer's address in <see cref="HsmsConnectionMode.Active"/>
    /// mode, the local address to bind in <see cref="HsmsConnectionMode.Passive"/> mode.
    /// Defaults to <c>127.0.0.1</c>.
    /// </summary>
    public string IpAddress { get; set; } = "127.0.0.1";

    /// <summary>Gets or sets the TCP port. Defaults to 5000.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// Gets or sets T3, the reply timeout: how long a sent primary message waits for its
    /// secondary. Defaults to 45 seconds.
    /// </summary>
    /// <remarks>On expiry the send fails with <see cref="SecsTimeoutException"/> and, if
    /// <see cref="SendS9F9OnReplyTimeout"/> is set, an S9F9 goes to the peer.</remarks>
    public TimeSpan T3 { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Gets or sets T5, the connect separation timeout: the minimum wait between two connection
    /// attempts. Defaults to 10 seconds.
    /// </summary>
    /// <remarks>
    /// A floor on reconnect attempts, not a delay before the first one. Equipment that is power
    /// cycling should not be hammered.
    /// </remarks>
    public TimeSpan T5 { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets T6, the control transaction timeout: how long a Select, Deselect or Linktest
    /// request waits for its response. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan T6 { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets T7, the not-selected timeout: how long the link may sit connected but
    /// unselected before being dropped. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan T7 { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets T8, the network inter-character timeout: how long a partially received
    /// message may stall. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// Armed by the decoder on the first byte of a message and disarmed on its last, so it
    /// never fires on an idle link. That scoping is the whole point of the timer: it detects a
    /// peer that died mid-message, which TCP alone will not report for minutes.
    /// </remarks>
    public TimeSpan T8 { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how often a Linktest is sent while selected. Defaults to 1 minute.</summary>
    /// <remarks><see cref="TimeSpan.Zero"/> disables it.</remarks>
    public TimeSpan LinkTestInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets a value indicating whether to send S9F9 when T3 expires. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// SEMI E5 says the entity whose transaction timer expired reports it. secs4net never sends
    /// this, which leaves the peer with no idea its reply was abandoned. Turn it off only for
    /// equipment that answers S9F9 with another S9F9.
    /// </remarks>
    public bool SendS9F9OnReplyTimeout { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to send S9F1 for an unknown device id. Defaults to <see langword="true"/>.</summary>
    public bool SendS9F1OnUnknownDeviceId { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to send S9F3/S9F5 for an unhandled stream or function. Defaults to <see langword="true"/>.</summary>
    public bool SendS9F3F5OnUnhandledMessage { get; set; } = true;

    /// <summary>Gets or sets the largest message this connection will accept, in bytes. Defaults to 16 MiB.</summary>
    /// <remarks>
    /// A message longer than this is answered with S9F11 (Data Too Long) and the connection is
    /// resynchronised. The length is a number the peer chose, so it is never used to size a
    /// buffer before this check.
    /// </remarks>
    public int MaxMessageLength { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the socket receive buffer size, in bytes. Defaults to 8192.</summary>
    public int SocketReceiveBufferSize { get; set; } = 8192;

    /// <summary>Gets or sets the initial size of a message's encode buffer, in bytes. Defaults to 4096.</summary>
    public int EncodeBufferInitialSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how many received primary messages may queue before the connection stops
    /// reading. Defaults to 256.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded queue turns a slow handler into unbounded memory growth;
    /// a bounded one turns it into back pressure, which TCP already knows how to express.
    /// </remarks>
    public int PrimaryMessageQueueCapacity { get; set; } = 256;

    /// <summary>Gets or sets the item payload size above which decoding rents from a pool. Defaults to 1024 bytes.</summary>
    public int PooledPayloadThreshold { get; set; } = 1024;

    /// <summary>Builds the codec options these settings imply.</summary>
    /// <returns>The codec options for this connection.</returns>
    public SecsCodecOptions CreateCodecOptions() => new()
    {
        PooledPayloadThreshold = PooledPayloadThreshold,
        MaxPayloadLength = MaxMessageLength,
        EncodeBufferInitialSize = EncodeBufferInitialSize,
    };
}
