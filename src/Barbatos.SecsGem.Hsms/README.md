# Barbatos.SecsGem.Hsms

HSMS-SS (SEMI E37) for **.NET 8, 9 and 10**: an actor-driven connection state machine, every timer
on `TimeProvider`, a dual-path `System.IO.Pipelines` codec whose T8 is scoped the way E37 scopes it,
automatic Stream 9 reporting, and transports that are nothing but an `IDuplexPipe`.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Hsms
```

## Getting a link up

```csharp
services.AddSecsGem(builder.Configuration, options =>
    {
        options.DeviceId = 7;
        options.Mode = HsmsConnectionMode.Passive;   // equipment. Active is the host role.
        options.Port = 5000;
    })
    .UseTcp()

    // The routing table: what this entity implements.
    .OnPrimary(1, 1, async (transaction, token) =>
    {
        using var reply = new SecsMessage(1, 2, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A("BRBTOS", 6), SecsItem.A("0.1.0")),
        };

        await transaction.ReplyAsync(reply, token);
    })
    .RunAsHostedService();
```

That is a working piece of SECS/GEM equipment. From host start to host stop the connection binds the
port, waits for a host, completes Select, answers Linktest, enforces T3 through T8, reports S9Fx and
reconnects after a drop no sooner than every T5. Nothing above had to ask for any of it.

To own the lifetime yourself, resolve `HsmsConnection` and call `RunAsync` instead of
`RunAsHostedService()`.

## What is here

| Type | What it owns |
| --- | --- |
| `HsmsConnection` | The state machine, the timers, the transactions, the Stream 9 reporting. |
| `SecsTransaction` | A received primary and the one thing needed to answer it. |
| `ISecsMessageRouter` | Which handler answers which primary - and what this entity claims to know. |
| `HsmsPipeDecoder` | Byte stream to frames. Two decode paths, T8 enforced. |
| `HsmsPipeEncoder` | Frames to bytes, length prefix computed rather than guessed. |
| `HsmsMessage` | One frame: a `MessageHeader` and, when it had one, a `SecsMessage`. |
| `S9Messages` | The Stream 9 error reports, built by hand when you need one. |
| `ISecsTransport` | Where the bytes come from. `TcpSecsTransport` and `InMemorySecsTransport` ship. |
| `SecsGemOptions` | Every setting, bound from `Barbatos:SecsGem`, validated at startup. |

## One loop owns the state

Everything that can change the connection's state is turned into an event and posted to a single
channel that one loop reads. The frame pump posts frames. The T7, T6 and Linktest timers post
expiries. A failing background Select posts a close. None of them touch the state themselves.

That is not ceremony. The alternative is what secs4net does: its `CommunicationStateChanging` calls
`Disconnect()` and then `Start()` from inside a `System.Threading.Timer` callback, so a timer firing
during a transition re-enters the transition it is already in the middle of. No lock fixes that,
because the problem is re-entrancy rather than concurrency. An actor has no re-entrancy to have.

There are three loops, and the split matters:

| Loop | Job | Why it is its own loop |
| --- | --- | --- |
| Pump | Decode frames, post them | Re-enters `DecodeAsync` after a recoverable decode failure, so one malformed body costs an S9F7 rather than the link. |
| Actor | Run the state machine, answer control messages | Must never block on user code, or a Linktest queues behind a slow report handler and the peer drops a link that was working. |
| Dispatch | Drain received primaries into handlers | Its queue is bounded at `PrimaryMessageQueueCapacity`, so a slow handler becomes back pressure rather than unbounded memory growth. |

### States

| State | What it means |
| --- | --- |
| `NotConnected` | No TCP connection. An active entity is retrying, no sooner than every T5. |
| `Connected` | Connected, not selected. T7 is running. Data messages are refused with `EntityNotSelected`. |
| `Selected` | Data messages flow, and Linktest keeps the link honest. |

`StateChanged` fires on the actor loop, so handlers see the transitions in order - and so a slow
handler delays the connection. Do real work elsewhere.

## Transactions

```csharp
// A primary with W set: waits for its secondary under T3.
using var request = SampleMessages.AreYouThere();
using var reply = await connection.SendAsync(request, cancellationToken);

// A notification with W clear: no transaction, nothing to time out.
using var report = new SecsMessage(6, 11, replyExpected: false) { Item = ... };
await connection.PostAsync(report, cancellationToken);
```

Two methods rather than one, because the two cases fail differently and a caller always knows which
one it has. Passing the wrong shape of message is an `ArgumentException` that names the other method.

The system bytes are the connection's business. A received primary arrives as a `SecsTransaction`,
and `ReplyAsync` stamps the right header itself:

```csharp
.OnPrimary(2, 41, async (transaction, token) =>
{
    var command = transaction.Message.Item?[0].GetString();

    using var reply = SampleMessages.HostCommandAcknowledge(accepted: CanDo(command));
    await transaction.ReplyAsync(reply, token);
})
```

A handler is never handed a number to copy. Copying it wrong is one of the classic SECS bugs: the
peer waits out T3 while a correctly-formed reply sits on the wire under a transaction id nobody is
watching.

`ReplyAsync` refuses to answer a message sent with W clear, and refuses to answer twice. A handler
that returns without replying to a W=1 primary is logged as an error, because the symptom the peer
sees is a T3 expiry several seconds later and a long way from the cause.

## Automatic Stream 9

| Report | When | Carries |
| --- | --- | --- |
| S9F1 Unrecognized Device ID | A data message arrives for another device id | MHEAD |
| S9F3 Unrecognized Stream | No handler registers any function of that stream | MHEAD |
| S9F5 Unrecognized Function | The stream is registered, that function is not | MHEAD |
| S9F7 Illegal Data | A frame's body does not decode | MHEAD |
| S9F9 Transaction Timer Timeout | T3 expired on a primary this entity sent | SHEAD |
| S9F11 Data Too Long | A peer announced a message past `MaxMessageLength` | MHEAD |

Each one carries the ten header bytes of the message it is about, which is what makes it useful: a
peer with a dozen transactions in flight cannot act on "something you sent was wrong".

S9F3 and S9F5 are only possible because the routing table is also a declaration of what this entity
understands. That is why dispatch and error reporting are one mechanism here rather than two - and
why registering a catch-all with `OnUnhandledPrimary` turns them off. An entity that claims every
message cannot also claim not to recognise one.

An S9 message is never answered with another S9, and all of them go out with W clear. Both rules
exist to stop the loop where two entities report each other's reports until the link saturates.

Each report can be turned off: `SendS9F1OnUnknownDeviceId`, `SendS9F3F5OnUnhandledMessage`,
`SendS9F9OnReplyTimeout`.

## Timers

| Timer | Scope | What expiry means |
| --- | --- | --- |
| T3 | A sent primary waiting for its secondary | The send throws `SecsTimeoutException`, and S9F9 goes to the peer. |
| T5 | Between two connection attempts | Not an expiry: a floor on how often an active entity knocks. |
| T6 | A control request waiting for its response | Select fails and the link drops; an explicit `LinkTestAsync` throws. |
| T7 | Connected but not selected | The link is dropped rather than left half-open. |
| T8 | The gap between bytes of **one** message | The link is dropped. See below - this one is widely got wrong. |

Every one of them runs on `TimeProvider`, which is what makes a conformance test possible:

```csharp
var clock = new FakeTimeProvider();
var connection = new HsmsConnection(options, transport, router, clock);
// ...
clock.Advance(options.T3 + TimeSpan.FromMilliseconds(1));   // T3 fires here, instantly
```

## Two decode paths

An HSMS frame is a 4-byte big-endian length, a 10-byte header, then the item tree. Because the length
arrives before the body, the decoder can tell at a glance whether a frame is already here in full.

**Fast path** - the whole frame is in the reader's buffer. The item tree is built straight out of the
`ReadOnlySequence<byte>` with no staging copy, and T8 is never armed, because a frame that arrived
complete had no gap in it to measure. On a healthy link carrying ordinary GEM traffic this is very
nearly every message.

**Slow path** - the frame spans reads. The bytes are gathered into one pooled staging buffer,
*consuming from the reader as they arrive*, with T8 running for the whole gather.

The slow path copies on purpose. Leaving a partial frame unconsumed in the pipe would be one copy
cheaper and would deadlock: a `Pipe` pauses its writer once unconsumed data passes its pause
threshold - 64 KiB by default - so a decoder waiting for the rest of a larger message would be
waiting on a writer that is waiting on the decoder. Consuming as we go keeps back pressure working
and makes the decoder correct against *any* `IDuplexPipe`.

Which path a link is taking is not a guess:

```csharp
Console.WriteLine($"fast {connection.FastPathFrames}, slow {connection.SlowPathFrames}");
```

A link whose traffic sits mostly on the slow path has small TCP segments or a congested peer, and is
worth a look.

## T8 is an inter-character timer

That is not the same thing as a message timer, and the difference is the whole point of T8. SEMI E37
scopes it to the gap between bytes of **one** message, so:

| Situation | T8 |
| --- | --- |
| Link idle, no frame begun | **Never fires.** There is no gap to measure. A quiet link is normal - that is what Linktest is for. |
| Peer dribbling a byte every `T8/2` | **Never fires.** Every byte restarts it. Slow is not broken. |
| Frame begun, then silence - mid prefix, mid header or mid body | **Fires.** A peer died with a message in flight, which TCP alone will not report for minutes. |

Because T8 is expressed as a cancellation token that only exists while a frame is part way in, there
is no way to leave it armed across a frame boundary - and leaving it armed is exactly the mistake
that turns T8 into an idle-link timer. This is the behaviour secs4net documents and does not have:
its `StartT8Timer` has no callers at all.

Whether a message is part way in is observable, which is what lets a health check tell an idle link
from a stalled one:

```csharp
if (decoder.IsReceivingFrame)
{
    Console.WriteLine($"{decoder.PendingFrameBytes} byte(s) of a message are in flight");
}
```

## Encoding

The length covers the header and the item tree but not itself, so the number has to go on the wire
before the bytes it counts. The encoder builds the body first, into one pooled scratch buffer, and
only then writes the prefix and header - straight into the output, in one 14-byte span, with the
length already known. A frame with no body, which is every control message and a data message such
as S1F1, needs no scratch buffer at all.

Reserving the prefix in the output and back-filling it is not allowed: `IBufferWriter<T>` only
promises a returned span stays valid until the next `GetSpan` or `Advance`, and a `PipeWriter` really
does move to a fresh segment part way through a large message.

A message that would encode past `MaxMessageLength` throws `HsmsMessageTooLongException` locally
rather than making the peer answer S9F11 about it.

## When a frame is wrong

A frame whose body does not decode is still **consumed**, so the link stays synchronised, the
connection answers S9F7, and the next message is right where it should be. An async iterator cannot
resume past an exception, so the pump re-enters `DecodeAsync` on the same reader - which has not lost
its place. All of that is inside `HsmsConnection`; it is described here because it is the reason one
malformed message does not cost a link.

The one exception is `HsmsMessageTooLongException`. Skipping a too-long body would mean trusting the
very length that was just rejected, so the byte stream is left unsynchronised and the connection is
re-established. That is the safe reading of E37: a receiver may discard a too-long message, but it
cannot know where the next one starts.

A link that closes part way through a frame is a truncation and throws, because silently dropping
half a message is how a host ends up waiting out T3 for a reply that was already lost.

## Transports

The decoder, the encoder and the state machine all work against an `IDuplexPipe` and know nothing
about sockets. Three things fall out of that: the whole stack can be tested over an in-memory pipe
with no port and no timing flakiness; TLS or a vendor's wrapper is a transport rather than a change
to the protocol code; and SECS-I over RS-232 has somewhere to go later.

`TcpSecsTransport` uses `Socket.ReceiveAsync(Memory<byte>)` straight into pipe-owned memory and
`Socket.SendAsync(ReadOnlyMemory<byte>)` straight out - there is no `NetworkStream` anywhere in it.
A passive transport binds in `ConnectAsync` rather than in its constructor, with `Listen(backlog: 1)`,
because HSMS-SS permits one selected connection at a time.

`InMemorySecsTransport.CreatePair()` wires two transports to each other, which is how a host and a
piece of equipment run a full conformance test in one process:

```csharp
var (active, passive) = InMemorySecsTransport.CreatePair();
var clock = new FakeTimeProvider();

await using var host = new HsmsConnection(options, active, hostRoutes, clock);
await using var equipment = new HsmsConnection(options, passive, equipmentRoutes, clock);

_ = host.RunAsync(token);
_ = equipment.RunAsync(token);
// They select each other. Nobody has to arrange it.
```

Both halves are real `Pipe` instances, so each connection sees the same segmented,
partially-filled reads it sees on a socket. Nothing is stubbed; the only thing missing is the network.

## Configuration

Bound from `Barbatos:SecsGem`, and validated at startup rather than at the first stalled message:

```json
{
  "Barbatos": {
    "SecsGem": {
      "DeviceId": 7,
      "Mode": "Passive",
      "IpAddress": "0.0.0.0",
      "Port": 5000,
      "T3": "00:00:45",
      "T5": "00:00:10",
      "T6": "00:00:05",
      "T7": "00:00:10",
      "T8": "00:00:05",
      "LinkTestInterval": "00:01:00",
      "MaxMessageLength": 16777216
    }
  }
}
```

Every timer is a `TimeSpan`, so the file reads `"T3": "00:00:45"` and nobody has to remember the
unit. `AddSecsGem` binds the section and then applies the code delegate, so code wins over file.

`SecsGemOptionsValidator` runs with `ValidateOnStart`, so a T8 longer than T3 fails the host at
startup instead of producing a puzzling disconnection a week later.

## Not here yet

| Phase | Scope |
| --- | --- |
| 7 | The GEM (E30) layer on top - `Barbatos.SecsGem.Gem`: control state, equipment constants, status variables, event and alarm reporting. |

`DeselectAsync` is also absent: this package answers Deselect.req correctly but does not initiate
one. HSMS-SS interop rarely needs it - a host that wants the link down sends Separate.req or simply
closes - and it will arrive with the GEM layer, which has a reason to.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

A host and a piece of equipment talking in one process, with no port:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```

The same two over a real socket:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
