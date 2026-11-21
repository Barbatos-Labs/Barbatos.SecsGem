# Barbatos.SecsGem.Gem

The GEM (SEMI E30) layer for **.NET 8, 9 and 10**: both state models, the standard message set,
and host-configured reporting - on top of an HSMS link that already works. Both ends of it:
`GemEquipment` is the machine, and [`GemHost`](#the-other-end-of-the-conversation) is the host
driving it.

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
| S1F21 | What data variables are there? | S1F22 - VID, DVVALNAME, UNITS |
| S1F23 | What collection events are there, and what is each about? | S1F24 - CEID, CENAME, VID |
| S2F29 | What constants, and what may I set them to? | S2F30 - ECID, ECNAME, ECMIN, ECMAX, ECDEF, UNITS |
| S5F5 | What alarms are there, and which are in alarm now? | S5F6 - ALCD, ALID, ALTX |
| S5F7 | Which alarms are enabled? | S5F8 - the same shape, enabled only |
| S6F15 | Give me this event's reports now | S6F16 |
| S6F19 | Give me this one report now | S6F20 |

**S1F11 and S1F21 are not each other.** E30 keeps status variables and data variables apart, and
the two namelists keep them apart too: a status variable asked for by S1F21 comes back named
nothing, exactly as an identifier the machine has never heard of does. That is not pedantry - a
host that read one list as the other would build reports out of variables the machine will not
report.

**S1F24 answers what an event is about, not what the host asked to be told.** The variables a
collection event names are the machine's own description of it, declared in the model; the reports
linked to it with S2F35 are the host's current subscription, and the two move independently.
Relink the event to a single-variable report and the namelist does not change. So the event's own
variable list is declared where the event is:

```csharp
.AddEvent(1_001, "ProcessEnd", [2u, 10u, 11u])   // what this event is about
.AddEvent(1_002, "Loaded")                       // an event about nothing in particular
```

An event with no variables is a legitimate answer and not a refusal: S1F24 has no way to say "I
decline to tell you", and a machine may well have an event that is simply an occurrence.

Three of the namelists carry a decision worth knowing about:

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

## A value every second, without asking every second

A trace is the answer to a host that wants a variable sampled on an interval. It names the
variables, the period, how many samples it wants and how many should travel together; the
equipment samples on its own clock and sends a group when it has one:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemTraces()                              // S2F23, and the S6F1 groups it produces
    .RunAsHostedService();
```

```csharp
// From the host: 600 samples of two variables, one a second, ten to a message.
await host.StartTraceAsync(new GemTraceRequest(1, TimeSpan.FromSeconds(1), 600, 10, [2, 3]));
```

Sixty messages rather than six hundred, which is the whole point of REPGSZ.

**The sampling happens on the equipment's own loop.** The timer does nothing but say which trace is
due; reading the variables and building the report happen where every other message is handled, so
a sample is never taken while a handler is halfway through changing the thing being sampled.

**Each sample is the value at its own moment.** A trace is not three reads of whatever the variable
holds when the group goes out - that would make the interval meaningless.

```csharp
_pressure = 100; // sample 1
_pressure = 200; // sample 2
_pressure = 300; // sample 3
// one S6F1 carrying 100, 200, 300
```

**SMPLN and STIME describe the last sample in the group**, which is what lets a host put a group
that arrived late back in order. A trace group goes out through the same path as an event report,
so one that cannot be delivered is spooled rather than lost.

**The last group goes even when it is short.** A trace of three samples in groups of two sends two
and then one: the host asked for three, and holding the third back for a group that will never fill
would lose it.

**TIAACK names the reason.** The five refusals are the five ways an S2F23 can be wrong, and each is
something a host can fix:

| Code | Means |
| --- | --- |
| 1 | More variables than `MaxTraceVariables` |
| 2 | As many traces already running as `MaxTraces` |
| 3 | DSPER was not a period this equipment can sample on |
| 4 | A variable it does not have - or a *data* variable, which only has a value inside the event it belongs to |
| 5 | REPGSZ does not fit the samples asked for |

One caveat worth knowing: E5's own table for TIAACK is these six codes, while
[`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) flags S2F24 as a representative
entry and lists only 0 and 1. Both agree on the only thing a host must get right - zero is accepted
- so a host that tests for zero is safe against either reading.

**A request for zero samples is a request to stop.** E5 defines S2F23 as *initialising* a trace, so
re-initialising a running TRID to take none of them and cancelling it are the same request;
`StopTraceAsync` sends exactly that. The standard leaves room for reading zero as "run for ever"
instead, and this is the safer of the two: a host that meant "stop" and was understood as "for ever"
floods, while one that meant "for ever" and was understood as "stop" gets no data and asks again.

A re-initialised TRID needs no new slot, which is how a host changes a running trace's period. And
a trace does not survive a re-established link: it is host configuration, like a report definition,
and a host that has forgotten about one has no TRID left to cancel it with.

## A band, and a message when the value leaves it

The other way of not polling. The host says which band it cares about; the equipment reports a
collection event when a variable leaves it or comes back:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => model
        .AddStatusVariable(2, "Pressure", () => SecsItem.F8(chamber.Pressure), "mbar", limitEventId: 2_500)
        .AddEvent(2_500, "PressureLimitCrossed"))
    .AddGemLimits()                              // S2F45 and S2F47
    .RunAsHostedService();
```

```csharp
// From the host: tell me when the pressure leaves 900-1100.
await host.DefineVariableLimitsAsync([new(2, [new GemVariableLimit(1, 900, 1100)])]);
```

**The band is the host's and the event is the machine's.** S2F45 carries no CEID, so E30 leaves the
collection event to the equipment - which is why `limitEventId` is on the variable and the bounds
arrive over the wire.

**`CheckLimitsAsync` is called by the application, when a value changes.** Nothing in this layer
knows when a chamber's pressure moves - a variable is a delegate it reads on demand - so the
alternative was a timer on every monitored variable with a period nobody asked for. A machine that
already raises events when it does things has the right moment to hand:

```csharp
chamber.Drift();
await equipment.CheckLimitsAsync(PressureVariable);
```

**A crossing is a change of side, not a value outside.** The first check records which side the
value is on and reports nothing, because there is nothing to have crossed from. After that, only
leaving and returning are reported - a value that wobbles inside the band is silent, and so is one
that wanders further out. That is what E5 means by calling the bounds UPPERDB and LOWERDB: the band
*is* the deadband, and without it a value sitting on a threshold reports every wobble.

Both bounds are inclusive, which is the choice that makes a band of `[x, x]` mean "exactly x"
rather than nothing at all.

**Per variable, all or nothing; per band, a reason.** A variable with one unusable band keeps none
of them - a half-configured variable is the worse failure for a monitoring feature, since the host
would believe it is watching a band that is not there. S2F46 still answers per band, so a host that
sent twenty learns which one it got wrong: `4` for the band that was not a band, `1` for the others,
and `3` against every band on a variable the machine does not have.

**What is uncertain here, said once.** Of this capability's four messages, the catalogue flags
S2F45, S2F46 and S2F48 as representative entries rather than definitions - only the S2F47 request
is firm. The body shapes follow SEMI E5 and the catalogue's item lists corroborate them item for
item and in order, including the per-variable `LVACK` that sits beside the per-limit `LIMITACK`.
What the tests prove is that both ends of this library agree; what only a copy of E5 can tell you is
whether they agree with somebody else's host. The shapes are in one file, `GemLimitCodec`, so there
is one place to check.

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

**The answer goes first.** The transmit runs on the equipment's own loop, and it is started only
once the S6F24 has been flushed - so a host that reads frames in order never meets a report before
the acknowledgement that announced it. Queueing the transmit ahead of the answer left the two racing
for the link, and the loop could win. A request sent with W clear gets no S6F24 and its transmit all
the same, and if the S6F24 cannot be sent the messages stay held for the host's next S6F23.

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

## A word with the operator

Stream 10 puts text in front of whoever is standing at the machine, and brings text back from
them. **Opt-in**, like the rest:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddGemEquipment(model => { /* ... */ })
    .AddGemTerminal<ChamberHmi>()                // or AddGemTerminal() for the in-memory one
    .RunAsHostedService();
```

| Message | The host is doing | Answered with |
| --- | --- | --- |
| S10F3 | Show this on terminal *n* | S10F4 (ACKC10) |
| S10F5 | Show these blocks on terminal *n* | S10F6 (ACKC10) |
| S10F9 | Show this on all of them | S10F10 (ACKC10) |

The whole content of this capability is outside the library, so `IGemTerminal` is one method:

```csharp
public ValueTask<GemTerminalAcknowledgement> DisplayAsync(GemTerminalMessage message, CancellationToken ct)
{
    _hmi.Show(message.Lines, message.IsBroadcast);
    return ValueTask.FromResult(GemTerminalAcknowledgement.Accepted);
}
```

**It runs on the dispatch loop, so it must not wait for a human.** An implementation that blocks
until somebody presses a key has stopped every other message on the link. Accept the message,
return, and show it in your own time.

**Three acknowledgement codes, and the difference is the host's to act on.** Accepted means taken
for display, not read by anybody. `WillNotDisplay` is a decision the machine made.
`TerminalNotAvailable` is a fault - and it is the only one of the three worth a host retrying.
That is also why the capability is opt-in: equipment with no screen answers S9F3 for the stream,
which is a fourth answer again, and the one that tells a host to stop asking.

The other direction is a method rather than an interface, because the application starts it -
nothing calls into an operator:

```csharp
var ackc10 = await equipment.SendTerminalRequestAsync("cassette jammed in load port 2");
```

`null` means there was nobody to tell. **It is not spooled**, and that is deliberate: a message
from an operator is a prompt for a person at the host, and handing it over an hour later when both
of them have gone home is worse than saying it did not go. Reports and alarms are spooled because
a host reads those as history.

## Exceptions, bare notifications and two diagnostics

The messages that are nobody's capability in particular. Each is a definition in the reference
rather than an illustration, and each was added by the sweep that closed the last of those.

| Message | What it is for | Answered with |
| --- | --- | --- |
| S5F9 | An exception the machine wants on the record | S5F10 (ACKC5) |
| S6F3 | An event happened; no values attached | S6F4 (ACKC6) |
| S6F5 | May I send something too big for one block? | S6F6 (GRANT6) |
| S2F21 | A remote command with no parameters | S2F22 (CMDA) |
| S2F25 | Send these bytes back to me | S2F26 (ABS) |

```csharp
await equipment.PostExceptionAsync("PRESSURE-HIGH", "ALARM", "Chamber pressure above limit");
await equipment.SendDiscreteVariableDataAsync(1_001);

if (await equipment.InquireMultiBlockAsync(70_000) == GemMultiBlockGrant.Ok)
{
    await equipment.TriggerEventAsync(1_001);
}
```

**An exception is not an alarm.** An alarm is a *condition*: it has a numeric ALID in the model, a
host enables or disables it with S5F3, and S5F6 serves the set of alarms that are on right now. An
exception is an *occurrence*, named by free text, and nothing in the documented half of stream 5
will ever mention it again - the recovery pairs S5F11 through S5F18 are rows the reference
illustrates rather than defines. So `PostExceptionAsync` takes its three text fields as arguments
instead of an identifier into a table, there is nothing to enable, and `EXTYPE` stays a string: it
has no code table, and inventing a set of type names would be the kind of guess that is hardest to
take back. The post is spooled, like a report, for exactly the reason that nothing else will
repeat it.

**S6F3 carries DATAID and CEID and nothing else**, which is what this reference's row names. E5 as
it is usually quoted has the discrete variable data send carrying values, nested under `DSID`,
`DVNAME` and `DVVAL` - and in the source behind
[`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) the last two of those are
defined and then named by **none** of its 524 rows. A gap the whole reference shares is a
vocabulary it never wrote down, so the row is implemented as given rather than filled in from
memory, and [a test pins the
measurement](../../tests/Barbatos.SecsGem.Catalog.UnitTests/SecsCatalogStream6Tests.cs). What it
is good for is an event whose values are expensive to gather: the host hears that it happened and
asks for what it wants with S6F15.

Because the message is thinner, it is easy to confuse with an S6F11 for an event that has no
reports linked to it - both arrive carrying nothing. `GemEventReportEventArgs.Function` is how a
host tells them apart, and the reason that property exists.

**S6F5 is a question, so it is never spooled.** Everything else the machine sends is news, and news
keeps; permission nobody is there to give is not permission, and a grant delivered when the host
comes back would answer a send that was abandoned an hour ago. With no link it answers `NoSpace`,
because `GRANT6` has no code for "nobody was listening" and `Busy` would invite an immediate retry
against the same absent host. Nothing here sends the large message afterwards or measures one to
decide whether it needs asking about: how many bytes fit in a block is a property of the link, and
whether a report is worth the extra round trip is the application's call.

**S2F21 is S2F41 with the detail taken out.** Both run the same command table and both obey the
same control-state rule - the shorter message is not the safer one - but a handler answers in
`HCACK`'s seven codes and S2F22 carries `CMDA`'s three. So `Accepted` becomes `Performed`,
`InvalidCommand` becomes `DoesNotExist`, and everything else, "will complete later" included,
becomes `CannotPerformNow`. That loss is the message's and not this library's: a host that wants
the fuller answer sends S2F41 with an empty parameter list.

**S2F25 is the one message here that changes nothing**, which makes it the right first thing to
send when a link is behaving oddly - it proves a body crosses in both directions without touching
the machine. It is answered **off-line as well as on**, deliberately: refusing it would make the
link untestable in exactly the state where a host most wants to test it. And it goes both ways, so
`GemHost` answers it too.

```csharp
await host.LoopbackAsync([0x00, 0x7F, 0xFF]);     // true when the echo matched exactly
await host.LoopbackDataAsync([0x00, 0x7F, 0xFF]); // the bytes, for a caller that wants to look
```

## The other end of the conversation

E30 models the *equipment*, so everything above is the machine's side. `GemHost` is the host's:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()                                    // link.Mode = Active, for a host
    .AddGemHost(builder.Configuration);
```

```csharp
var host = provider.GetRequiredService<GemHost>();

await host.EstablishCommunicationsAsync();
await host.DeleteAllReportsAsync();
await host.DefineReportsAsync([(6_000, [2, 10])]);
await host.LinkEventsAsync([(1_001, [6_000])]);
await host.EnableEventsAsync(enable: true, [1_001]);

host.EventReportReceived += (_, e) =>
    Console.WriteLine($"CEID {e.EventId}: {e.Reports[0].Values[0].FirstValueOrDefault<double>()}");

var hcack = await host.SendCommandAsync("START", [("LOTID", "LOT-1")]);
```

**It is not a second state machine.** A host has no control state to defend, nothing to spool and
no reports of its own, so there is no loop to run and nothing to configure but an identity. E30
gives the *equipment* the duty of retrying until communications are established; a host decides
when it wants to talk.

**What it is, is the acknowledgement codes.** `HCACK` 4 means accepted and `HCACK` 1 means
refused. `DRACK` 0, `LRACK` 0, `ERACK` 0, `PPGNT` 0 and `ACKC7` 0 all mean yes, and
`GemOnlineAcknowledgement.AlreadyOnline` is not a failure. A host written against
`reply.Item?.FirstValueOrDefault<byte>()` is one typo from treating a refusal as a success, and no
test it writes will say so. Every method here returns the enum.

**Every primary the equipment answers has a method**, and [a test asserts
that](../../tests/Barbatos.SecsGem.Gem.UnitTests/GemHostTests.cs) rather than this paragraph
claiming it: the equipment records what it is asked, the test calls every host method once, and
the recorded set has to cover `HandledMessages`, `ProcessProgramMessages`,
`FormattedProcessProgramMessages` and `SpoolMessages` together.

| Reply | What you get back |
| --- | --- |
| An acknowledgement code | The enum - `GemDefineReportAcknowledgement`, `GemHostCommandAcknowledgement`, `ACKC7`, and the rest |
| A shape E5 pins down | Decoded: `GemEquipmentIdentity`, `GemVariableDescription`, `GemConstantDescription`, `GemAlarmReport`, `GemFormattedProcessProgram`, a `DateTimeOffset`, a list of PPIDs |
| Equipment data - S1F4, S2F14, S6F16, S6F20 | The `SecsMessage`, which **the caller disposes**. A typed wrapper over "whatever this machine calls a value" would be a guess with an API attached |

What the machine says unprompted arrives as an event: `EventReportReceived` (S6F11, S6F13 and
S6F3), `AlarmReceived` (S5F1), `ExceptionReceived` (S5F9), `TraceDataReceived` (S6F1),
`MultiBlockInquired` (S6F5) and `ErrorReported` (stream 9). Each carries a settable
acknowledgement, because whether a host could store what it was sent is the one decision about an
inbound report that cannot be made for it.

Two of those have a default worth knowing. `MultiBlockInquired` **grants** unless a handler says
otherwise, because a handshake whose only purpose is to let a host refuse should not refuse by
silence - a host with nothing wired up would otherwise stop a machine reporting at all. And
`ExceptionReceived` arrives already set to `Error` when the body did not read, which is stricter
than the alarm handler on purpose: S5F6 will serve an alarm condition again, and nothing will ever
repeat an exception post.

**Stream 9 is declared, and that matters.** An entity that has not declared stream 9 answers an
incoming S9F5 with another S9F5, and the two ends then explain the problem to each other until
something gives out. The hand-written host this replaced had that bug.

One thing to know about the event arguments: the values in a report are a **view into the message
that arrived**, and it is disposed the moment the handler returns. Read what you need inside the
handler - the host sample does.

`GemHost` covers the equipment side of this library and no more. A host that needs stream 3,
stream 10 or stream 14 still has the connection it was built over: this is a layer over the link,
not a wall in front of it.

## What is not here yet

| Not implemented | Why it is not a one-line addition |
| --- | --- |
| A spool over a database | `GemFileSpool` holds reports across the equipment's own restart, which is the durability E30 does not require and most fabs want. A spool over a database - shared between two controllers, or backed up with everything else - is an `IGemSpool` of about a hundred lines, and the interface is the whole contract |
| The rest of stream 7 | Both halves E30 asks for are here: S7F1-S7F6 and S7F17-S7F20 unformatted, S7F23-S7F26 formatted. What is left - the directory pair S7F7/F8, verification S7F9/F10, the matrix messages S7F11 through S7F16, references S7F21/F22, and everything from S7F27 up - is outside the GEM capability, and every one of them is an entry [`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) flags as representative: a starting point for reading E5 rather than a definition to implement against. Implementing one means reading the standard first, not copying a table |
| A documented range per status variable | E5 has S2F48 report the variable's own LIMITMIN and LIMITMAX beside the bands in force, and a `GemVariable` carries no range - so this equipment reports a zero-length item, which is E5's way of saying there is no documented bound. The host side reads one when another machine sends it |
| The rest of streams 1, 2, 5 and 6 | Nothing, now, of what the reference *documents*: every primary in those four streams that it defines rather than illustrates is implemented here, the last seven of them in one sweep - S1F21, S1F23, S2F21, S2F25, S5F9, S6F3 and S6F5. What is left in them is representative rows, and `s.Provenance` on [`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) says which: a starting point for reading E5, not a table to copy. S5F11-S5F18, the exception recovery flow, is the largest single group |
| A trace that outlives a reconnect | Deliberate, and the only part of tracing with no test of its own. Every trace is cancelled where the host's report definitions are reset, on a re-establish - and an in-memory transport pair cannot re-select after a deselect, because the select is driven by the connection's own state machine and nothing public asks it to. The test covers the other side: that a second S1F13 on a live conversation resets nothing |
| Streams this library does not implement at either end | Stream 3 material status, stream 12 wafer mapping, stream 14 object services. `GemHost` covers what `GemEquipment` answers, by construction and by test, so the way to add one of these is to add it to the machine's side first - a host method for a message no equipment here answers would be an API with nothing to talk to |

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
