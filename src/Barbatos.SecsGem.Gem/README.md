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

## Holding reports for a host that is not there

A machine does not stop while the host is away. Spooling is E30's answer: hold what could not be
delivered, and let the host decide what to do with it.

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemSpooling()                            // bounded, in memory
    .RunAsHostedService();
```

```csharp
equipment.SpoolState;            // Inactive, Active, Full
equipment.SpoolCountActual;      // held now
equipment.SpoolCountTotal;       // held since spooling became active
equipment.SpoolCountDiscarded;   // lost because it was full
equipment.SpoolStartTime;        // how much history is on offer
```

Those five are E30's spool status variables, and wiring them as SVIDs is a line each - the
numbering is yours.

**It never pushes the backlog.** When communication returns, the equipment waits. The host sends
**S6F23** with RSDC 0 to ask for the spooled data or RSDC 1 to throw it away, and the equipment
answers S6F24. A host that has just come up may not want an hour of history, and that is its call
to make rather than something it has done to it.

Three things in here are decisions worth knowing:

**A held report carries the values from when the event happened.** The spool holds an *encoded*
message, not a note to go and read the variables later:

```csharp
_pressure = 500;
await equipment.TriggerEventAsync(ProcessEnd);   // held, at 500
_pressure = 1200;                                // the machine moves on
// ... the host returns, asks with S6F23, and receives 500
```

A report timestamped an hour ago carrying today's pressure is worse than no report at all.
Encoding at spool time also hands the pooled buffers straight back, which matters when a thousand
reports are waiting for a host that returns in an hour.

**`TriggerEventAsync` still returns `false` for a held report.** Nothing reached the host, so
saying it had would be a lie. `SpoolCountActual` is what distinguishes kept from dropped. An event
the host *disabled* is never held - putting it in the spool would only deliver it later, which is
not what disabling it meant.

**A full spool keeps the oldest by default.** A host replaying a backlog wants the beginning of
what it missed: the first events after the link dropped are the ones that explain the rest.
`GemSpoolOverflowPolicy.DiscardOldest` is there for equipment where the latest value matters more
than the history.

`MaxSpoolTransmit` caps one burst. Without it, a thousand transactions land the moment a link
recovers, which is how equipment knocks over the host that was waiting for it.

Equipment that calls neither `AddGemSpooling` nor `AddGemFileSpooling` does not spool, and answers
S9F5 for S6F23 - "I do not do that", which is a different answer from RSDA 1 and a more useful one
for a host deciding whether to wait.

### A spool that outlives the process

`AddGemSpooling` holds reports in memory, and loses them when the equipment is upgraded, crashes or
loses power - three of the more likely reasons a host was not there. `AddGemFileSpooling` writes
each held message to a directory instead:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemFileSpooling()                        // Barbatos:Gem:SpoolDirectory
    .RunAsHostedService();
```

Same `SpoolCapacity`, same `SpoolOverflow`, same S6F23. The difference is what is there when the
process comes back: `GemFileSpool` adopts the records still in the directory, and the equipment
adopts what the spool is holding - so `SpoolCountActual` is right, S6F23 Transmit is accepted rather
than refused, and **`SpoolStartTime` is the oldest held message's own timestamp rather than the time
the process started.** A host reads that variable to work out how much history it is being offered,
and the restart is not when the history began.

**One file per message, not one append-only log.** A log is the obvious shape and the wrong one: the
head has to be removed, so there is a read cursor that must be as durable as the log; the file grows
until something compacts it; and a record half written when the power went has to be found with a
checksum. A file per message replaces all three with a rename. The record is written under
`.spooling` and `File.Move`d into place, and a move within one directory is atomic - so a message is
either entirely a record or not a record at all. The price is a directory entry per held message,
which `SpoolCapacity` bounds.

```
spool/
  0000000000000000017.spool     a held message
  0000000000000000018.spool
```

The sequence, not a timestamp: three reports raised inside the same millisecond still have an
order, and a host replaying a spool is reading a sequence of events. A reopened spool carries on
from the highest number it finds, so a message appended after a restart files behind the ones
already held rather than in front of them.

**Writes go through to the disk.** `FileOptions.WriteThrough`, which costs a disk round trip per
held message and is the entire point: a buffered write the operating system has not flushed is
exactly what a power cut takes, and a spool that lost its contents to the failure it exists for
would be worse than no spool - the equipment would have reported the message held.

**A record that cannot be read is dropped, and counted.** `GemFileSpool.Unreadable` and
`Discarded` are both observable. A spool that kept retrying a record it cannot read would never
deliver the messages behind it: every S6F23 would stop on the same file. The same goes for a record
whose body is not a decodable item tree - the spool stores bytes and cannot know, so the equipment
catches that one, drops it and carries on with the next.

**A filesystem fails in ways memory does not.** A full disk, a revoked permission, a volume that
went away: none of them takes the equipment down. A report that cannot be written is not reported
as held, and the spool state goes to `Full` - which is what a host needs to know, since from its
side the difference between "out of room" and "out of disk" is nothing it can act on differently.

One spool per directory. Two over the same one would hand the same host each other's messages and
fight over the sequence numbers.

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

**Equipment that calls neither `AddGemProcessPrograms` nor `AddGemFormattedProcessPrograms` does
not answer stream 7 at all.** The routing table is the declaration of what an entity understands,
so leaving both out means the connection reports S9F3 - "I do not do stream 7" - which is true and
useful. Declaring the messages and then refusing every one with an acknowledgement code would be
neither. That is why `ProcessProgramMessages` is a separate list from `HandledMessages`.

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

### Formatted recipes, the other half of stream 7

An unformatted process program is a PPBODY: bytes the equipment stores and hands back, whose
meaning is between the host and the machine. A **formatted** one is structure E5 defines, and S7F23
carries it as a list of four - PPID, the MDLN and SOFTREV it was written for, and a list of CCODE
commands each with its own parameters. It is a capability of its own:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemProcessPrograms()                     // S7F1, S7F3, S7F5, and S7F17/S7F19 with them
    .AddGemFormattedProcessPrograms()            // S7F23 and S7F25
    .RunAsHostedService();
```

| Message | The host is doing | Answered with |
| --- | --- | --- |
| S7F23 | Here is a structured recipe | S7F24 (ACKC7) |
| S7F25 | Send that one back | S7F26 - the same four items, or a zero-length list |

The two halves are independent: equipment may offer either, both, or neither. **S7F17 and S7F19
come with the unformatted half**, because a stream and function may be declared only once and
deleting or listing a program is not per-format. Register both and those two handlers cover both
stores: a formatted program appears in S7F20 beside the unformatted ones, an identifier that exists
in both is listed once, and an S7F17 naming a program only one store has is still ACKC7 0 - saying
"no such program" because the other half had not heard of it would be refusing something that just
happened.

A formatted program arrives as data rather than as a window onto the message, so
`IGemFormattedProcessProgramStore` has nothing to say about copying:

```csharp
public ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
    GemFormattedProcessProgram program, CancellationToken ct)
{
    foreach (var command in program.Commands)
    {
        _recipes.Add(program.Id, command.Code, command.Parameters);
    }

    return ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);
}
```

**A body this stack cannot represent is refused, not reshaped.** E5 types CCODE `U1[1]`, and a
PPARM here is text, so a command code that will not fit in a byte or a parameter that arrived as a
number is answered with ACKC7 5, `ModeUnsupported` - *I do formatted process programs, just not
that flavour of one*. The alternative is telling a host its recipe landed when what landed was
something else. A body that is not the shape E5 defines at all is ACKC7 2, the `LengthError` S7F3
already uses for a body it could not read; a host told "length error" for a numeric PPARM would go
looking for the wrong thing.

Nothing is trimmed on the way back out except PPID, MDLN and SOFTREV, which are fixed-width fields
in E5. A PPARM that arrived space-padded is returned padded, because S7F26 has to give back what
S7F23 sent and a trim is not reversible.

MDLN and SOFTREV arrive as the host sent them, and nothing here compares them against
`GemOptions.ModelName`. Whether a machine accepts a recipe written for another model is a fab
decision, and the store gets the whole program and can refuse it.

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
| A spool over a database | `GemFileSpool` holds reports across the equipment's own restart, which is the durability E30 does not require and most fabs want. A spool over a database - shared between two controllers, or backed up with everything else - is an `IGemSpool` of about a hundred lines, and the interface is the whole contract |
| The rest of stream 7 | Both halves E30 asks for are here: S7F1-S7F6 and S7F17-S7F20 unformatted, S7F23-S7F26 formatted. What is left - the directory pair S7F7/F8, verification S7F9/F10, the matrix messages S7F11 through S7F16, references S7F21/F22, and everything from S7F27 up - is outside the GEM capability, and every one of them is an entry [`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) flags as representative: a starting point for reading E5 rather than a definition to implement against. Implementing one means reading the standard first, not copying a table |
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
