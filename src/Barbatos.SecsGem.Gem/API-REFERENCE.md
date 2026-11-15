# Barbatos.SecsGem.Gem - API reference

**This package exports no public type yet.** It is a placeholder that reserves the package id and
the `Barbatos.SecsGem.Gem` namespace; the GEM (SEMI E30) layer is phase 7 of the
[roadmap](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).

Referencing it today compiles and does nothing. There is deliberately no partial implementation to
build against, because a half-written state model is worse than none: equipment that passes a
conformance test against it would be passing against something that is going to change.

## What is planned

| Area | What it will cover |
| --- | --- |
| Communication state | S1F13/S1F14 establish-communications, and the retry behaviour around it |
| Control state | Off-line, on-line local and on-line remote, with the transitions E30 allows |
| Collection events | Event definition, report linking, enable and disable |
| Status and constants | Status variables, equipment constants, data variables |
| Alarms | Enable, disable and report |
| Remote commands | S2F41 and the response codes |

`DeselectAsync` on `HsmsConnection` also lands with this layer: a deselect is only meaningful once
there is a control-state model with somewhere to go afterwards.

## What to use in the meantime

The protocol layer is complete and shipping. A GEM message is a `SecsMessage`, and the pieces to
send and answer one all exist:

| You want to | Use |
| --- | --- |
| Keep a link up, with every timer | [`HsmsConnection`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#hsmsconnection) |
| Declare which messages you answer | [`SecsMessageRouterBuilder`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsmessagerouterbuilder) |
| Reply to a primary | [`SecsTransaction`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secstransaction) |
| Map a GEM message to a C# object | [`Barbatos.SecsGem.Mapper`](../Barbatos.SecsGem.Mapper/README.md) |

The host and equipment samples already complete a GEM opening sequence - S1F13, S1F1, S1F2 and an
S6F11 event report - by hand, which is a fair picture of what this package will wrap:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```
