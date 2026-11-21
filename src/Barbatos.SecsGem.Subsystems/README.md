# Barbatos.SecsGem.Subsystems

Subsystem control and data - SECS-II stream 18 - for **.NET 8, 9 and 10**: commanding one subsystem
inside a machine, reading and writing its variables, its status, the list of them, the events and
alarms it raises, and the 2D code condition read.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Subsystems
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 18 is E5 and no
part of E30, and the independence is the point: a subsystem controller - a mark reader, a pump
module, a gas box with a serial link of its own - need not implement GEM to be driven this way.

## Read this before you use it: stream 18 is GEM's message set, re-addressed

Six of stream 18's eight pairs are a GEM transaction with an `SSID` in front:

| stream 18 | the GEM row it mirrors | what changed |
| --- | --- | --- |
| S18F1 / F2 subsystem command | S2F41 / F42 host command | an `SSID` in front, same `HCACK` and `CPACK` |
| S18F3 / F4 subsystem status | — | the one state transaction of its own |
| S18F5 / F6 data read | S1F3 / F4 selected status | an `SSID`, and the `VID` echoed back |
| S18F7 / F8 data write | S2F15 / F16 new constant | **a write, with an acknowledgement of its own** |
| S18F9 / F10 subsystem list | S1F11 / F12 namelist | names and states instead of names and units |
| S18F11 / F12 event report | S6F11 / F12 | an `SSID`, a `TIME`, and **no report data** |
| S18F13 / F14 alarm report | S5F1 / F2 | an `SSID`, and `SSACK` in place of `ACKC5` |
| S18F15 / F16 2D code condition | — | nothing like it anywhere in the catalogue |

So if your machine implements **SEMI E39** and this stack's `Barbatos.SecsGem.Objects`, you already
have most of this by another route: the enhanced half of stream 2 commands one object with S2F49,
reads its status variables with S2F57, its data variables with S2F59 and its alarms with S2F61, all
addressed by an `OBJSPEC`. **Serve one or the other, not both** - a machine with two ways to be
asked the same question has two places for the answer to be wrong.

Which to prefer, when both fit, is a question the catalogue answers: **S2F49 and S2F50 are
definitions and every one of stream 18's sixteen rows is a representative subset.** E39's route is
on firmer ground. Stream 18 earns its place where E39's does not reach - a **variable write per
subsystem**, which the enhanced stream 2 has no setter for; a **list of subsystems with their
states**; and the **2D code read**, which is in no other stream at all.

## Why it is here at all, when stream 17 was refused

The chapter before last refused a stream outright. The measurement that separates the two is worth
stating, because the surface resemblance is close - both streams look like another stream's rows
with the letters changed.

Stream 17 failed two checks. Every row in its control job half was stream 16's process job set with
`PJ` rewritten to `CJ`, and its own key items - `CJID`, `CJACK`, `CJSTATE` - were defined nowhere,
while the two control job items the dictionary *does* define were named by no message at all. Two
halves of one reference disagreeing about one standard.

Stream 18 passes both. Its rows are drawn from **five different** GEM streams rather than copied
from one, which is a composition and not a template. And **`SSACK` is defined**, with a table of
four codes whose three refusals *name stream 18's own data-write transaction*: "unknown VID (data-
write context)", "value out of range (data-write context)", "read-only variable (data-write
context)". A stream nobody meant does not get an acknowledgement item of its own with its own
transaction written into the code descriptions. Seventeen of the twenty item names it uses are
defined.

## Both ends send primaries

Stream 18 is the second stream in this stack to work that way, after stream 3:

- the **equipment** answers six - command, status, read, write, list, code read;
- the **host** answers two - the event report and the alarm report.

The two sets are **disjoint**, which matters more than it looks. Elsewhere in this repository the
routing table refuses a second registration that claims a message, so calling both ends' setup on
one builder is caught. Here it would not be: nothing collides. What would be wrong is the machine
answering questions it is the one that asks, and there is no runtime check for that - so it is a
test instead.

## The host

```csharp
services
    .AddSecsGem(configure: link =>
    {
        link.DeviceId = 0;
        link.Mode = HsmsConnectionMode.Active;
    })
    .UseTcpTransport("192.168.0.30", 5000)
    .AddSubsystemHost();
```

```csharp
var host = provider.GetRequiredService<SubsystemHost>();

host.AlarmReported += (_, args) => log.LogWarning("{Alarm}", args.Alarm);
host.EventReported += (_, args) => log.LogInformation("{Report}", args.Report);

foreach (var subsystem in await host.ListAsync(cancellationToken))
{
    log.LogInformation("{Subsystem}", subsystem);      // CHAMBER-A Process chamber A (Idle)
}

var pressure = await host.ReadAsync(SubsystemId.Named("CHAMBER-A"), 1001, cancellationToken);

var result = await host.CommandAsync(
    new SubsystemCommand
    {
        Subsystem = SubsystemId.Named("CHAMBER-A"),
        Command = "PUMP",
        Parameters = [new SubsystemParameter("TARGET", "4.0")],
    },
    cancellationToken);

if (!result.IsAccepted)
{
    // Two codes are successes: 0 means done and 4 means accepted and not yet finished.
}
```

## The equipment

```csharp
services
    .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
    .UseTcpTransport(listenPort: 5000)
    .AddSubsystemEquipment();                   // or AddSubsystemEquipment<MyStore>()
```

`ISubsystemStore` is six methods, one per primary: `CommandAsync`, `StatusAsync`, `ReadAsync`,
`WriteAsync`, `ListAsync`, `ReadCodeAsync`. Each is awaited inside the transaction the host is
waiting on, so none should be slow. There is no event beside them - an application that cares about
a request needs the request, and a store method already has it, can refuse it, and is awaited
before the reply goes back.

The two primaries this end sends are methods on `SubsystemEquipment`:

```csharp
await equipment.ReportEventAsync(
    new SubsystemEvent { Subsystem = chamber, EventId = 2001, Time = "2026112100000000" },
    cancellationToken);

await equipment.ReportAlarmAsync(
    new SubsystemAlarm
    {
        Subsystem = chamber,
        AlarmId = 3001,
        IsSet = true,
        Category = SubsystemAlarmCategory.EquipmentSafety,
        Text = "chamber pressure high",
    },
    cancellationToken);
```

Subsystems come from configuration, the way ports and modules do everywhere else here: stream 18
has no message that creates one, so a machine's subsystems are a fact about the machine.

## `SSID` has two readings and this library picks neither

`SSID` is the spine of the stream - eleven of its sixteen rows carry one - and it has **no
dictionary entry**. Unlike stream 13's payload it cannot just be passed through, because it is a
key, and a key this library cannot compare is of no use.

So the measurement, and why it does not settle it. Eight items in the catalogue are an identifier
beside a name:

```
CEID / CENAME        U4 / A
DSID / DSNAME        U4 / A
ECID / ECNAME        U4 / A
SPID / SPNAME        U4 / A
SVID / SVNAME        U4 / A
EQID / EQNAME        A  / A
RCPID / RCPNAME      A  / A
TRJOBID / TRJOBNAME  A  / A
```

Five against three is not a rule. Stream 18 does not settle it from inside either: `TARGETID`, the
only other identifier in the stream, is an `A`, and `ALID`, also in it, is a `U4`.

A wrong guess is total rather than partial - text cannot be read as a number or the other way
round - so a single choice would make this package useless against half the machines in the world.
`SubsystemId` therefore carries **either** reading. The caller says which, this library writes
exactly that, and either arrives back:

```csharp
SubsystemId.Named("CHAMBER-A")   // travels as A
SubsystemId.Numbered(2)          // travels as U4
```

**Equality is strict, and it is read by format rather than by content.** `Numbered(7)` and
`Named("7")` are two different subsystems, and an `A` carrying "7" arrives as the *name* "7".
Nothing in E5 says a machine means them as one, and a reader meaning to be generous is how
`Barbatos.SecsGem.Carriers` once put a load port into the wrong access mode.

The default value is the **empty name**, not the number zero. That is deliberate and it is the
mistake this type made first: with the discriminator inferred from a null name, an unset
`SubsystemId` addressed subsystem number 0 - and some machine somewhere numbers its subsystems from
zero. No machine has a subsystem called nothing.

## `SSSTATE` is written as text, and that is an inference this package owns

`SSSTATE` has no dictionary entry either - and neither does any other item a message uses whose
name ends in `STATE`: `CJSTATE`, `JOBSTATE` and `RCPSTATE` are all undefined too, **four for four**,
so the gap is the source's habit rather than anything about stream 18. (`PRSTATE` *is* defined, with
ten codes, and no message in the catalogue names it.)

Text, for two reasons about this item rather than about taste: it sits beside `SSNAME` in the one
row carrying both, which is text; and E5 names no states for a subsystem, so there is no table for a
number to be a code *of*. Reading is tolerant - a machine that sends a code arrives here as that
code's digits - and only the equipment end originates a state, so only it has to write one.

## What can be refused, and what cannot

The differences are E5's, not this package's, and they are sharper here than anywhere else in the
stack:

- **S18F2** carries an `HCACK`, so a command is refused with a reason - including
  `NoSuchSubsystem`, which is more than most of this stack gets. An unreadable body is answered
  `InvalidParameter`, not `InvalidCommand`: the command may well exist and what went wrong was the
  body.
- **S18F8** carries an `SSACK`, so a write is refused with a reason.
- **S18F4, S18F6 and S18F10** carry **no code at all**. A status, a variable and a listing are data
  and nothing else, so a subsystem that does not exist and a variable that does not exist both come
  back as an empty value - and `StatusAsync` and `ReadAsync` return `null` for one.
- **S18F16** carries an `SSACK` whose named codes are all about writing a variable, so a failed
  code read is `Other`. Inventing a meaning for 1, 2 or 3 would put a write's vocabulary on a
  reader.

The asymmetry worth remembering: **a write can be refused and a read of the same variable cannot.**

## `SSACK` serves four replies and its table was written for one

`SSACK` is the whole of S18F12 and S18F14, half of S18F8 and part of S18F16. Its three refusals -
unknown variable, value out of range, read-only variable - only mean anything in a write.
Acknowledging an event report with "value out of range" would be noise, so in the other three
transactions the honest answers are `Accepted` and `Other`, and that is what `SubsystemHost`
offers.

A host that cannot make sense of a report **still acknowledges it**. `SSACK` has no code for "that
made no sense", and leaving the machine unanswered leaves it on its T3 - which a negative control
demonstrated by hanging the test suite rather than failing it.

`CPACK` has a quirk of its own: **its table starts at 1 and has no code zero**, because the list in
S18F2 carries the parameters that were *wrong* and not the ones that were fine. So
`SubsystemParameterAcknowledgement` has no success member, deliberately, and a machine that sends a
zero is logged and read as `Other`.

## It closed a hole in the GEM package

`HCACK` is one dictionary entry, and this package is the second to quote it. That made a
discrepancy visible: `GemHostCommandAcknowledgement` stopped at 6 while the table reserves 7 to 63,
and `GemHost` cast the arriving byte straight to the enum - so an equipment-specific
acknowledgement used to come back as a value with no name on it. It was never the dangerous
direction, since an unnamed code is not a success either way, but one item cannot be read two ways
in one repository. `GemHostCommandAcknowledgement.Other` now exists and both packages read the
range the same way.

## What is not here yet

- **Nothing of stream 18 is missing.** All sixteen rows are implemented at both ends.
- A store that outlives the process, and deliberately no snapshot: a subsystem's state is something
  the machine observes about its own hardware, and the rule across this repository is that what the
  machine observed does not come back from a snapshot. A restart re-reads the hardware.
- Any bridge to E39. A subsystem is not an E39 object - it has no `OBJTYPE`, no attributes and no
  stream 14 presence - and a package that mapped one onto the other would be inventing the
  correspondence rather than implementing it.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
