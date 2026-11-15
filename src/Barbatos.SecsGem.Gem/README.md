# Barbatos.SecsGem.Gem

The GEM (SEMI E30) equipment layer for **.NET 8, 9 and 10**: both state models, the standard
message set, and host-configured reporting - on top of an HSMS link that already works.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Gem
```

## A piece of GEM equipment, whole

```csharp
services
    .AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment((model, provider) =>
    {
        var chamber = provider.GetRequiredService<Chamber>();

        model
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(chamber.Pressure), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A(chamber.LotId))
            .AddEvent(1_001, "ProcessEnd")
            .AddReport(5_000, 2, 10)
            .AddAlarm(new GemAlarm
            {
                Id = 9_000,
                Text = "Chamber pressure outside the setpoint window",
                SetEventId = 2_000,
                ClearEventId = 2_001,
            })
            .AddRemoteCommand("START", (context, _) =>
            {
                chamber.Start(context.GetText("LOTID")!);

                // Accepted and not finished. Completion arrives as a collection event.
                return ValueTask.FromResult(GemHostCommandAcknowledgement.CompletionLater);
            });
    })
    .RunAsHostedService();
```

That is the whole of it. S1F13 establish-communications with a retry delay, the control state
model, S1F1, S1F3, S2F13/F15, S2F17/F31, S2F33/F35/F37, S2F41 and S5F3 are all answered; S6F11 and
S5F1 go out when you say so:

```csharp
await equipment.TriggerEventAsync(1_001);
await equipment.SetAlarmAsync(9_000);
```

`TriggerEventAsync` returns `false` rather than throwing when the event is disabled, unknown, or
there is nobody to tell. Every one of those is ordinary operation - a host that disabled an event
has said it does not want it - and ordinary operation should not look like a fault.

## A selected link is not a conversation

This is the distinction the whole layer exists to keep.

```csharp
connection.IsSelected      // the bytes can flow
equipment.IsCommunicating  // both sides have agreed to talk
```

HSMS Select means a socket is up and framed. GEM communication means both entities have exchanged
S1F13 and S1F14 and agreed they are ready for application messages. Equipment that reports an event
on the strength of the first is equipment a host is entitled to ignore - and nothing here will:
every outbound report checks `IsCommunicating` first.

| `GemCommunicationState` | |
| --- | --- |
| `Disabled` | Switched off. No S1F13 goes out; one that arrives is denied. |
| `WaitingToRetry` | Enabled, not communicating: waiting out the retry interval, or waiting for a link. |
| `WaitingForAcknowledge` | S1F13 is out and unanswered. E30's WAIT CRA. |
| `Communicating` | Agreed. Application messages may flow. |

The retry delay is why `WaitingToRetry` exists. Without it, two entities that come up together spend
the day exchanging denied requests, each one a transaction the other has to answer. It runs on
`TimeProvider`, so a test advances a clock instead of waiting thirty seconds.

## Off-line is two different states, and the difference matters

```csharp
equipment.ControlState    // EquipmentOffline, AttemptOnline, HostOffline, OnlineLocal, OnlineRemote
equipment.IsOnlineRemote  // may the host command this machine?
```

`EquipmentOffline` is the machine's own decision. `HostOffline` is the host having asked with S1F15.
Both refuse remote commands, and collapsing them would leave an operator with no way to find out
why the machine is not taking orders.

Two consequences you can observe:

- **S1F1 answers with a zero-length list while off-line**, not with the model name. E30 is specific
  about this: it is how a host learns the machine is alive but not available without asking a
  second question.
- **S1F17 is refused with ONLACK 1 while the equipment itself is off-line.** The machine's own
  decision is not the host's to overrule, and answering 0 would promise a transition that is not
  going to happen.

A remote command is gated on the control state by default:

```csharp
.AddRemoteCommand("START", handler)                                // needs OnlineRemote
.AddRemoteCommand("PP-SELECT", handler, requiresRemoteControl: false)  // a read is fine in local
```

## The host defines the reports, not you

This is the part of GEM that makes equipment usable by a host nobody wrote it for. You declare the
variables; the host says which ones it wants, grouped how it likes, attached to which events.

| Message | What the host is doing | Answered with |
| --- | --- | --- |
| S2F33 | Define reports - or delete them all with an empty list | DRACK |
| S2F35 | Link reports to a collection event | LRACK |
| S2F37 | Enable or disable events | ERACK |
| S5F3 | Enable or disable alarms | ACKC5 |

Those four live in `GemEquipmentModel`, and each one returns the acknowledgement code E30 says it
should. A batch that fails validation **changes nothing at all**, because a host told DRACK 4 is
entitled to assume its definition was rejected in full. Deleting a report also unlinks it, because
a link to a report that no longer exists is not a link.

The state is reset when communication is re-established, which `ResetLinkStateOnDisconnect`
controls. A host that reconnects has not re-sent its definitions and E30 never promised they
survived - keeping them would be inventing a guarantee, and it shows up as a host receiving a
report it never asked for.

## And it can be asked what it has

The reports above assume the host already knows the identifiers. These are how it finds them out
on a machine nobody wrote it for:

| Message | The host is asking | Answered with |
| --- | --- | --- |
| S1F11 | What status variables are there? | S1F12 - SVID, SVNAME, UNITS |
| S2F29 | What constants, and what may I set them to? | S2F30 - ECID, ECNAME, ECMIN, ECMAX, ECDEF, UNITS |
| S5F5 | What alarms are there, and which are in alarm now? | S5F6 - ALCD, ALID, ALTX |
| S5F7 | Which alarms are enabled? | S5F8 - the same shape, enabled only |
| S6F15 | Give me this event's reports now | S6F16 |
| S6F19 | Give me this one report now | S6F20 |

Three of those carry a decision worth knowing about:

```csharp
.AddConstant(new GemEquipmentConstant
{
    Id = 100, Name = "PressureSetpoint", Units = "mbar",
    Read = () => SecsItem.F8(chamber.Setpoint),
    Write = item => chamber.TrySetpoint(item.FirstValueOrDefault<double>()),

    // For S2F30 only. Write still has the last word on what is accepted.
    Minimum = () => SecsItem.F8(100d),
    Maximum = () => SecsItem.F8(2_000d),
    Default = () => SecsItem.F8(1_013.25d),
})
```

**A bound nobody documented is a zero-length item, not a zero.** Leave `Minimum` off and S2F30 says
so. The two are different answers, and a host acting on the second would refuse values the machine
would have taken. The bounds are for the namelist and nothing else - a setpoint may be limited by
what the chamber is doing right now, which no static minimum can express, so `Write` remains the
authority.

**Enabled and in-alarm are different questions.** S5F3 configures the first; S5F6 reports the
second, in ALCD's top bit:

```csharp
await equipment.SetAlarmAsync(9_000);
equipment.IsAlarmSet(9_000);                  // true - the chamber is out of range
equipment.Model.IsAlarmEnabled(9_000);        // whether the host wants to hear about it
```

The in-alarm state is tracked even when nothing can be reported, because the machine is in alarm
whether or not there is a host to tell. Collapsing the two would have listed every alarm as quiet
while the chamber was out of range.

**S6F15 answers a disabled event.** S2F37 governs what the equipment sends on its own; a host
asking directly has already said it wants this one now.

## Recipes, if the machine has any

Stream 7 moves process programs - recipes - onto the machine and off it. It is **opt-in**, and that
is the interesting part:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemProcessPrograms()                     // the in-memory store
    .RunAsHostedService();
```

| Message | The host is doing | Answered with |
| --- | --- | --- |
| S7F1 | May I send you this program, this many bytes? | S7F2 (PPGNT) |
| S7F3 | Here it is | S7F4 (ACKC7) |
| S7F5 | Send me this one back | S7F6 - PPID and PPBODY, or a zero-length list |
| S7F17 | Delete these - or all of them, with an empty list | S7F18 (ACKC7) |
| S7F19 | What do you have? | S7F20 - the PPID list |

**Equipment that does not call `AddGemProcessPrograms` does not answer stream 7 at all.** The
routing table is the declaration of what an entity understands, so leaving it out means the
connection reports S9F3 - "I do not do stream 7" - which is true and useful. Declaring the messages
and then refusing every one with an acknowledgement code would be neither. That is why
`ProcessProgramMessages` is a separate list from `HandledMessages`.

**S7F1 exists so a refusal is cheap.** A recipe can be megabytes. The host names the identifier and
the length first, and the equipment answers on the strength of that:

```csharp
public ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken ct)
    => ValueTask.FromResult(length > _budget ? GemProcessProgramGrant.NoSpace : GemProcessProgramGrant.Ok);
```

Where recipes actually live is the application's business - a file, a database row, something a
controller already owns - so `IGemProcessProgramStore` is an interface with a usable default rather
than a storage decision baked in. `GemInMemoryProcessProgramStore` is enough for a simulator or for
recipes regenerated at start-up; anything that must survive a restart wants its own.

One thing the interface insists on: **the body you are handed is only valid for the length of the
call.** It is a window onto the message that arrived, and that message is disposed as soon as the
handler returns. Copy what you keep - the in-memory store does.

## Identifiers are numbers, not U4s

```csharp
<L [1] <U4 [1] 2>>     // works
<L [1] <U1 [1] 2>>     // works
<L [1] <U8 [1] 2>>     // works
<L [1] <A  [1] '2'>>   // works
```

GEM says an SVID is "a number". Equipment that only accepts `U4` works with the host it was tested
against and fails with the next one, which is the single most common shape of SECS interop bug.
Every identifier this layer reads goes through one tolerant reader; the ones it writes are `U4`.

## The clock a host may set

S2F31 lets a host set the equipment clock. This implementation keeps an offset of its own and
applies it to the time it **reports**:

```csharp
equipment.Now    // TimeProvider.GetUtcNow() + whatever the host set
```

So a host that sets the clock changes what S2F18 answers and what timestamps reports carry, and
changes nothing else on the machine. Nothing here touches the operating system clock, which is not
a decision a SECS message should be able to make. Set `AcceptHostClockSet` to `false` to answer
TIACK 1 instead.

## Configuration

```json
{
  "Barbatos": {
    "Gem": {
      "ModelName": "BRBTOS-1",
      "SoftwareRevision": "0.1.0",
      "DefaultControlState": "OnlineRemote",
      "EstablishCommunicationsRetryInterval": "00:00:30",
      "EventReportsExpectReply": true
    }
  }
}
```

Validated on start, so a `ModelName` too long for MDLN fails the host rather than surfacing days
later as equipment a host refuses to talk to.

## Two actors, and the one place they overlap

The equipment is an actor, like the connection underneath it. Link transitions arrive on a channel
and one loop processes them - because `HsmsConnection.StateChanged` is raised *on the connection's
own actor loop*, so establishing communications from inside that handler would stall the link it is
establishing communications over.

Inbound handlers run on the connection's dispatch loop, which is single-reader, so they never run
concurrently with each other - only with the equipment's own loop. That one overlap is the only
place to be careful, and it is where the first version of this layer had a bug: an S1F13 from the
host while the equipment's own S1F13 was still in flight gave two writers to the communication
state, and the loser silently undid the winner. So the establish transition is **posted** to the
equipment's loop and applied there in order. Control transitions happen in the handler, under a
gate, because a host asking for off-line has to be answered with the state its request produced.

## What is not here yet

| Not implemented | Why it is not a one-line addition |
| --- | --- |
| Spooling (E30 §spooling) | A whole subsystem: durable storage, an overflow policy, S6F23 purge, and a conversation about what happens when the disk fills |
| The rest of stream 7 | The five unformatted pairs are here. The matrix messages (S7F11 through S7F16), verification (S7F9/F10) and formatted programs (S7F23/F24) are not |
| A host-side GEM helper | E30 models the *equipment*. A host sends messages and reads replies, which the HSMS layer already does - see the host sample |

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- The link underneath: [`Barbatos.SecsGem.Hsms`](../Barbatos.SecsGem.Hsms/README.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

A host and a piece of GEM equipment in one process, establishing communications, configuring
reports and exchanging an event report, with no port:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```

The same two over a socket:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
