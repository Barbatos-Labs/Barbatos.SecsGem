# Barbatos.SecsGem.Hsms - API reference

Namespace `Barbatos.SecsGem.Hsms`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [Connection](#connection)
  - [`HsmsConnection`](#hsmsconnection)
  - [`HsmsConnectionState`](#hsmsconnectionstate)
  - [`HsmsConnectionStateChangedEventArgs`](#hsmsconnectionstatechangedeventargs)
  - [`SecsTransaction`](#secstransaction)
- [Routing](#routing)
  - [`SecsPrimaryHandler`](#secsprimaryhandler)
  - [`ISecsMessageRouter`](#isecsmessagerouter)
  - [`SecsMessageRouter`](#secsmessagerouter)
  - [`SecsMessageRouterBuilder`](#secsmessagerouterbuilder)
- [Stream 9](#stream-9)
  - [`S9Messages`](#s9messages)
- [Codec](#codec)
  - [`HsmsPipeDecoder`](#hsmspipedecoder)
  - [`HsmsPipeEncoder`](#hsmspipeencoder)
  - [`HsmsMessage`](#hsmsmessage)
  - [`HsmsMessageTooLongException`](#hsmsmessagetoolongexception)
- [Transports](#transports)
  - [`ISecsTransport`](#isecstransport)
  - [`ISecsTransportConnection`](#isecstransportconnection)
  - [`TcpSecsTransport`](#tcpsecstransport)
  - [`InMemorySecsTransport`](#inmemorysecstransport)
  - [`DuplexPipe`](#duplexpipe)
- [Hosting](#hosting)
  - [`SecsGemOptions`](#secsgemoptions)
  - [`SecsGemOptionsValidator`](#secsgemoptionsvalidator)
  - [`HsmsConnectionMode`](#hsmsconnectionmode)
  - [`AddSecsGem`](#addsecsgem)
  - [`ISecsGemBuilder`](#isecsgembuilder)

---

## Connection

### `HsmsConnection`

`public sealed class HsmsConnection : IAsyncDisposable`

The state machine, the timers, the transactions and the automatic Stream 9 reporting. One instance
keeps one link up; call `RunAsync` once.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `HsmsConnection(SecsGemOptions options, ISecsTransport transport, ISecsMessageRouter router, TimeProvider? timeProvider = null, ILogger<HsmsConnection>? logger = null)` | `null` provider means `TimeProvider.System`; `null` logger discards diagnostics. |
| `RunAsync` | `Task RunAsync(CancellationToken cancellationToken = default)` | Connects, selects, serves, and on losing the link waits T5 and retries - until cancelled. Throws `InvalidOperationException` on a second call. |
| `SendAsync` | `ValueTask<SecsMessage> SendAsync(SecsMessage primary, CancellationToken cancellationToken = default)` | W must be set. Waits under T3. The caller owns the returned secondary. |
| `PostAsync` | `ValueTask PostAsync(SecsMessage message, CancellationToken cancellationToken = default)` | W must be clear. No transaction is opened. |
| `LinkTestAsync` | `ValueTask LinkTestAsync(CancellationToken cancellationToken = default)` | Sends a Linktest and waits for the response under T6. |
| `State` | `HsmsConnectionState { get; }` | |
| `IsSelected` | `bool { get; }` | Whether data messages can flow right now. |
| `DeviceId` | `ushort { get; }` | |
| `StateChanged` | `event EventHandler<HsmsConnectionStateChangedEventArgs>?` | Raised on the actor loop, so handlers see transitions in order and a slow one delays the link. |
| `FastPathFrames` | `long { get; }` | Frames that arrived whole, accumulated across reconnections. |
| `SlowPathFrames` | `long { get; }` | Frames that spanned reads, accumulated across reconnections. |
| `OpenTransactions` | `int { get; }` | Sent primaries still waiting for a secondary. |
| `DisposeAsync` | `ValueTask DisposeAsync()` | Closes any live link and fails every open transaction. |

**`SendAsync` throws**

| Exception | When |
| --- | --- |
| `ArgumentException` | `primary` has W clear. Use `PostAsync`. |
| `SecsException` | The connection is not `Selected`, or the link closed before the reply arrived. |
| `SecsTimeoutException` (`Timer == SecsTimer.T3`) | No reply within T3. S9F9 goes to the peer first when `SendS9F9OnReplyTimeout` is set. |

**What it does on its own**

| Event | Response |
| --- | --- |
| Select.req | Select.rsp `Success`, then `Selected`; or `AlreadyActive` when already selected. |
| Arriving at `Connected` as the active entity | Sends Select.req under T6. |
| Deselect.req | Deselect.rsp, back to `Connected`, T7 running again. |
| Linktest.req | Linktest.rsp. |
| Separate.req | Closes the link, with no response. E37 gives it none. |
| An unknown SType | Reject.req `StypeNotSupported`. |
| A data message with a non-zero PType | Reject.req `PtypeNotSupported`. |
| A data message while not `Selected` | Reject.req `EntityNotSelected`. |
| A data message for another device id | S9F1. |
| A primary no handler claims | S9F3 or S9F5, per the routing table. |
| A body that does not decode | S9F7, and the link survives. |
| A message past `MaxMessageLength` | S9F11, then the link drops. |
| T7 or T8 expiry, or a failed Linktest | Drops the link; `RunAsync` reconnects after T5. |

### `HsmsConnectionState`

`public enum HsmsConnectionState { NotConnected, Connected, Selected }`

`Connected` is E37's CONNECTED / NOT SELECTED: T7 is running and data messages are refused.

### `HsmsConnectionStateChangedEventArgs`

`public sealed class HsmsConnectionStateChangedEventArgs : EventArgs`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `HsmsConnectionStateChangedEventArgs(HsmsConnectionState previous, HsmsConnectionState current, string? reason = null)` | |
| `Previous` | `HsmsConnectionState { get; }` | |
| `Current` | `HsmsConnectionState { get; }` | |
| `Reason` | `string? { get; }` | For logs. Branch on `Current`, not on this. |
| `ToString` | `override string ToString()` | `Connected -> Selected (Select.req from the peer)`. |

### `SecsTransaction`

`public sealed class SecsTransaction : IDisposable`

A received primary and the means to answer it. Owns `Message`; the dispatch loop disposes the
transaction once the handler returns, so a handler that wants to keep the message copies what it
needs out of it.

| Member | Signature | Notes |
| --- | --- | --- |
| `Message` | `SecsMessage { get; }` | The primary. |
| `ReplyAsync` | `ValueTask ReplyAsync(SecsMessage secondary, CancellationToken cancellationToken = default)` | Stamps the session id and system bytes itself. The caller keeps ownership of `secondary`. |
| `SystemBytes` | `int { get; }` | |
| `SessionId` | `ushort { get; }` | |
| `ReplyExpected` | `bool { get; }` | `false` for a notification - handle it and return. |
| `IsReplied` | `bool { get; }` | |
| `S` | `byte { get; }` | |
| `F` | `byte { get; }` | |
| `Dispose` | `void Dispose()` | Disposes `Message`. Idempotent. |

`ReplyAsync` throws `InvalidOperationException` when the peer asked for no reply, when one has
already gone out, or when the link it arrived on has closed.

---

## Routing

### `SecsPrimaryHandler`

```csharp
public delegate ValueTask SecsPrimaryHandler(SecsTransaction transaction, CancellationToken cancellationToken);
```

Runs on the dispatch loop, not the actor loop, so a slow handler does not delay control messages. It
does apply back pressure: primaries queue in a bounded channel of
`SecsGemOptions.PrimaryMessageQueueCapacity`, and once it fills the connection stops reading.

### `ISecsMessageRouter`

`public interface ISecsMessageRouter`

Which handler answers which primary, and which streams this entity claims to understand at all. The
second half is what makes automatic S9F3 and S9F5 possible.

| Member | Signature | Notes |
| --- | --- | --- |
| `IsEmpty` | `bool { get; }` | Nothing registered. The connection logs a warning at startup, because every primary will then be answered with S9F5. |
| `TryGetHandler` | `bool TryGetHandler(byte stream, byte function, out SecsPrimaryHandler? handler)` | |
| `IsKnownStream` | `bool IsKnownStream(byte stream)` | The S9F3 / S9F5 fork: an unknown stream is S9F3, a known stream with an unknown function is S9F5. |
| `CatchAll` | `SecsPrimaryHandler? { get; }` | When set, turns off automatic S9F3 and S9F5 entirely. |

Replace the registration to resolve handlers from a container instead:
`builder.Services.AddSingleton<ISecsMessageRouter, MyRouter>()`.

### `SecsMessageRouter`

`public sealed class SecsMessageRouter : ISecsMessageRouter`

`SecsMessageRouter(IEnumerable<KeyValuePair<(byte Stream, byte Function), SecsPrimaryHandler>> handlers, SecsPrimaryHandler? catchAll = null)`

A frozen table, built once at startup. Handlers cannot be added later, deliberately: a routing table
that changes under a running link makes S9F5 unreproducible.

### `SecsMessageRouterBuilder`

`public sealed class SecsMessageRouterBuilder`

| Member | Signature | Notes |
| --- | --- | --- |
| `OnPrimary` | `SecsMessageRouterBuilder OnPrimary(byte stream, byte function, SecsPrimaryHandler handler)` | `stream` at most 127, `function` odd. Throws `ArgumentException` on a duplicate. |
| `OnUnhandledPrimary` | `SecsMessageRouterBuilder OnUnhandledPrimary(SecsPrimaryHandler handler)` | One only. |
| `CatchAll` | `SecsPrimaryHandler? { get; }` | |
| `Build` | `ISecsMessageRouter Build()` | |

Not thread-safe, because configuration is not concurrent.

---

## Stream 9

### `S9Messages`

`public static class S9Messages`

Builds the SEMI E5 error reports. `HsmsConnection` sends these on its own; the factories are public
because a GEM layer, a test and a capture tool all have reason to build one by hand. Every one is
built with W clear.

| Member | Signature | Body |
| --- | --- | --- |
| `UnrecognizedDeviceId` | `static SecsMessage UnrecognizedDeviceId(in MessageHeader offending)` | S9F1, `<B[10]>` MHEAD |
| `UnrecognizedStream` | `static SecsMessage UnrecognizedStream(in MessageHeader offending)` | S9F3, MHEAD |
| `UnrecognizedFunction` | `static SecsMessage UnrecognizedFunction(in MessageHeader offending)` | S9F5, MHEAD |
| `IllegalData` | `static SecsMessage IllegalData(in MessageHeader offending)` | S9F7, MHEAD |
| `TransactionTimerTimeout` | `static SecsMessage TransactionTimerTimeout(in MessageHeader sent)` | S9F9, `<B[10]>` **SHEAD** - this one reports a message that left, not one that arrived |
| `DataTooLong` | `static SecsMessage DataTooLong(in MessageHeader offending)` | S9F11, MHEAD |
| `ConversationTimeout` | `static SecsMessage ConversationTimeout(string expectedMessage, string errorDescription)` | S9F13, `<L[2] <A MEXP> <A EDID>>` - a conversation has no single header to point at |
| `IsErrorReport` | `static bool IsErrorReport(SecsMessage message)` | Worth checking before reporting an error about an error. |

---

## Codec

### `HsmsPipeDecoder`

`public sealed class HsmsPipeDecoder`

Turns the byte stream of an HSMS link into frames, enforcing T8 while it does. One decoder drives
one `PipeReader` from one loop; `DecodeAsync` is not reentrant. The diagnostic counters are safe to
read from another thread.

| Member | Signature | Notes |
| --- | --- | --- |
| `LengthPrefixSize` | `const int` | `4`. The big-endian length that precedes every frame. |
| ctor | `HsmsPipeDecoder(SecsGemOptions options, TimeProvider? timeProvider = null)` | Reads T8, `MaxMessageLength` and the codec options from `options`. |
| `DecodeAsync` | `IAsyncEnumerable<HsmsMessage> DecodeAsync(PipeReader reader, CancellationToken cancellationToken = default)` | The caller owns and must dispose every frame. |
| `FastPathFrames` | `long { get; }` | Frames that arrived whole and were decoded in place. |
| `SlowPathFrames` | `long { get; }` | Frames that spanned reads and were gathered into a staging buffer. |
| `PendingFrameBytes` | `int { get; }` | Bytes of an incomplete frame currently held. Zero at a frame boundary, which is also exactly when T8 is not armed. |
| `IsReceivingFrame` | `bool { get; }` | `PendingFrameBytes > 0`. The difference between an idle link and a stalled one. |

**`DecodeAsync` throws**

| Exception | When |
| --- | --- |
| `SecsTimeoutException` (`Timer == SecsTimer.T8`) | A frame began arriving and then stalled for longer than T8. |
| `SecsDecodeException` | A frame announces fewer bytes than a header; a body has bytes left over after its root item; a body is not a valid item tree; or the link closed part way through a frame. |
| `HsmsMessageTooLongException` | A peer announced a message past `SecsGemOptions.MaxMessageLength`. |
| `OperationCanceledException` | `cancellationToken` was cancelled. Never reported as a T8. |

The enumeration **ends** rather than throwing when the link closes cleanly at a frame boundary.

**Synchronisation after a failure.** A `SecsDecodeException` whose `Header` is set came from a frame
that was read in full and consumed, so the reader is positioned at the next one and the caller can
re-enter `DecodeAsync` after answering S9F7. Any other failure means the framing itself was wrong and
the link must be dropped.

### `HsmsPipeEncoder`

`public sealed class HsmsPipeEncoder`

Writes frames onto an HSMS link. Disposes nothing, so a message can be encoded twice - which is what
a T3 retry and a report fanned out to two hosts both need.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `HsmsPipeEncoder(SecsGemOptions options)` | Reads the codec options, `EncodeBufferInitialSize` and `MaxMessageLength`. |
| `WriteAsync` | `ValueTask<FlushResult> WriteAsync(PipeWriter output, HsmsMessage message, CancellationToken cancellationToken = default)` | Writes and flushes. |
| `WriteAsync` | `ValueTask<FlushResult> WriteAsync(PipeWriter output, MessageHeader header, SecsMessage? data, CancellationToken cancellationToken = default)` | The overload a transaction manager wants: no ownership transfer. |
| `Encode` | `int Encode(IBufferWriter<byte> output, MessageHeader header, SecsMessage? data)` | Encodes without flushing. Returns the bytes written, prefix included. |

Throws `ArgumentException` when `data` has a body but `header` is a control message, and
`HsmsMessageTooLongException` when the frame would exceed `MaxMessageLength` - before anything
reaches the wire.

### `HsmsMessage`

`public sealed class HsmsMessage : IDisposable`

One complete frame. Deliberately not a `SecsMessage`: S, F and the item tree are E5's business, while
the session id, system bytes and SType belong to the connection carrying them, which is E37's.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `HsmsMessage(MessageHeader header, SecsMessage? data = null)` | Takes ownership of `data`. |
| `FromData` | `static HsmsMessage FromData(SecsMessage data, ushort sessionId, int systemBytes)` | Stamps a data-message header. Takes ownership. |
| `Control` | `static HsmsMessage Control(MessageType type, int systemBytes, ushort sessionId = 0xFFFF, byte status = 0)` | `status` is header byte 3. Throws `ArgumentOutOfRangeException` for `MessageType.DataMessage`. |
| `Header` | `MessageHeader { get; }` | |
| `Data` | `SecsMessage? { get; }` | `null` for every frame whose body was not decoded - see below. |
| `DetachData` | `SecsMessage? DetachData()` | Moves ownership of the body out of the frame, so disposing the frame no longer disposes it. |
| `IsControl` | `bool { get; }` | |
| `SystemBytes` | `int { get; }` | |
| `SessionId` | `ushort { get; }` | |
| `Dispose` | `void Dispose()` | Disposes `Data`, unless it was detached. Idempotent. |
| `ToString` | `override string ToString()` | |

**When `Data` is `null`**

| Case | How to tell |
| --- | --- |
| A control message. | `IsControl` |
| A data message whose `PType` is not zero. | `!Header.IsDataMessage && !IsControl` |
| A control message that arrived carrying a body, which E37 does not allow. | `IsControl`, frame longer than 10 bytes |
| The body was detached. | After a `DetachData` call |

### `HsmsMessageTooLongException`

`public sealed class HsmsMessageTooLongException : SecsException`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `HsmsMessageTooLongException(long length, int limit)` | |
| `Length` | `long { get; }` | The length the peer announced, or the frame the encoder produced. |
| `Limit` | `int { get; }` | The `MaxMessageLength` that was exceeded. |
| `Header` | `MessageHeader? { get; init; }` | MHEAD for the S9F11, when the header was already buffered. Never waited for. |

When the decoder throws it, the byte stream is **no longer synchronised**, so the connection must be
re-established.

---

## Transports

### `ISecsTransport`

`public interface ISecsTransport : IAsyncDisposable`

| Member | Signature |
| --- | --- |
| `IsActive` | `bool { get; }` |
| `Description` | `string { get; }` |
| `ConnectAsync` | `ValueTask<ISecsTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)` |

### `ISecsTransportConnection`

`public interface ISecsTransportConnection : IAsyncDisposable`

| Member | Signature | Notes |
| --- | --- | --- |
| `Pipe` | `IDuplexPipe { get; }` | `Input` carries bytes in, `Output` carries them out. |
| `RemoteDescription` | `string { get; }` | For logs. |

### `TcpSecsTransport`

`public sealed class TcpSecsTransport : ISecsTransport`

Socket async throughout - `Socket.ReceiveAsync(Memory<byte>)` into pipe-owned memory and
`Socket.SendAsync(ReadOnlyMemory<byte>)` out of it. No `NetworkStream`. Active or passive follows
`SecsGemOptions.Mode`. A passive transport binds in `ConnectAsync` rather than in its constructor,
with `Listen(backlog: 1)`, because HSMS-SS permits a single selected connection.

### `InMemorySecsTransport`

`public sealed class InMemorySecsTransport : ISecsTransport`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `InMemorySecsTransport(bool isActive = true)` | A pair of pipes of its own, for when the test plays the peer. |
| `CreatePair` | `static (InMemorySecsTransport Active, InMemorySecsTransport Passive) CreatePair()` | Two transports wired to each other, for two real connections. |
| `LocalPipe` | `IDuplexPipe { get; }` | The end this transport presents. |
| `PeerPipe` | `IDuplexPipe { get; }` | The other end. For the single-transport case only. |

Not a mock: both halves are real `Pipe` instances, so a decoder sees the same segmented,
partially-filled reads it sees on a socket.

### `DuplexPipe`

`public sealed class DuplexPipe : IDuplexPipe`

`DuplexPipe(PipeReader input, PipeWriter output)` - pairs a reader and a writer.

---

## Hosting

### `SecsGemOptions`

`public sealed class SecsGemOptions`

| Member | Type | Default | Notes |
| --- | --- | --- | --- |
| `SectionName` | `const string` | `"Barbatos:SecsGem"` | |
| `DeviceId` | `ushort` | `0` | The HSMS session id. A data message for any other is answered with S9F1. |
| `Mode` | `HsmsConnectionMode` | `Passive` | |
| `IpAddress` | `string` | `"127.0.0.1"` | Peer address when active, local bind when passive. |
| `Port` | `int` | `5000` | |
| `T3` | `TimeSpan` | 45 s | Reply timeout. |
| `T5` | `TimeSpan` | 10 s | Connect separation. |
| `T6` | `TimeSpan` | 5 s | Control transaction. |
| `T7` | `TimeSpan` | 10 s | Not-selected. |
| `T8` | `TimeSpan` | 5 s | Network inter-character. Enforced by `HsmsPipeDecoder`. |
| `LinkTestInterval` | `TimeSpan` | 1 min | `TimeSpan.Zero` disables. |
| `SendS9F9OnReplyTimeout` | `bool` | `true` | |
| `SendS9F1OnUnknownDeviceId` | `bool` | `true` | |
| `SendS9F3F5OnUnhandledMessage` | `bool` | `true` | A registered catch-all turns these off regardless. |
| `MaxMessageLength` | `int` | 16 MiB | Checked before the length sizes any buffer. |
| `SocketReceiveBufferSize` | `int` | `8192` | |
| `EncodeBufferInitialSize` | `int` | `4096` | |
| `PrimaryMessageQueueCapacity` | `int` | `256` | Bounded on purpose: back pressure rather than unbounded memory growth. |
| `PooledPayloadThreshold` | `int` | `1024` | |
| `CreateCodecOptions()` | `SecsCodecOptions` | | The codec options these settings imply. |

### `SecsGemOptionsValidator`

`public sealed class SecsGemOptionsValidator : IValidateOptions<SecsGemOptions>`

Registered with `ValidateOnStart` by `AddSecsGem`. Beyond range checks on every numeric bound it
enforces the cross-field rules: `IpAddress` must parse, **T8 must be shorter than T3**, T6 should be
shorter than T3, `LinkTestInterval` must exceed T6, `MaxMessageLength` must be at least a header, and
`EncodeBufferInitialSize` must not exceed `MaxMessageLength`.

Hand-written rather than DataAnnotations-driven on purpose: the DataAnnotations validator reflects
over the options type, which costs the package its trim and Native AOT story.

### `HsmsConnectionMode`

`public enum HsmsConnectionMode { Passive, Active }`

`Passive` binds a port and waits - the usual role for equipment. `Active` connects out and sends
Select.req - the usual role for a host.

### `AddSecsGem`

Namespace `Microsoft.Extensions.DependencyInjection`.

```csharp
public static ISecsGemBuilder AddSecsGem(
    this IServiceCollection services,
    IConfiguration? configuration = null,
    Action<SecsGemOptions>? configure = null)
```

Binds `Barbatos:SecsGem` and *then* applies `configure`, so code wins over file. Registers
`SecsGemOptionsValidator` with `ValidateOnStart`, `TimeProvider.System` if nothing else has, the
codec options derived from `SecsGemOptions`, the routing table, `HsmsConnection`, and everything
`AddSecsSerialization()` registers.

### `ISecsGemBuilder`

| Member | Signature | Notes |
| --- | --- | --- |
| `Services` | `IServiceCollection { get; }` | |
| `UseTcp` | `ISecsGemBuilder UseTcp()` | |
| `UseInMemoryTransport` | `ISecsGemBuilder UseInMemoryTransport(bool isActive = true)` | |
| `UseTransport<TTransport>` | `ISecsGemBuilder UseTransport<TTransport>() where TTransport : class, ISecsTransport` | Replaces rather than tries to add, so the last call wins. |
| `OnPrimary` | `ISecsGemBuilder OnPrimary(byte stream, byte function, SecsPrimaryHandler handler)` | Also the declaration that makes automatic S9F3 and S9F5 possible. |
| `OnUnhandledPrimary` | `ISecsGemBuilder OnUnhandledPrimary(SecsPrimaryHandler handler)` | Turns off automatic S9F3 and S9F5. |
| `RunAsHostedService` | `ISecsGemBuilder RunAsHostedService()` | Runs the connection from host start to host stop. Omit it to call `HsmsConnection.RunAsync` yourself. |

Format spokes extend `IServiceCollection` rather than this builder - `AddSecsSml()`, `AddSecsJson()`
and the rest - because a spoke cannot reference this assembly. That is what makes adding a format a
new assembly rather than an edit to `Barbatos.SecsGem.Core`.
