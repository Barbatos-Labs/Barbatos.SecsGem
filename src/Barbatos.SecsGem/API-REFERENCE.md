# Barbatos.SecsGem - API reference

**This package has no API.** It is a metapackage: `IncludeBuildOutput` is `false`, so it ships no
assembly, exports no type and adds nothing to compile against. Referencing it is exactly equivalent
to referencing its three dependencies by hand.

The API you get is theirs:

| Package | Reference | Namespaces |
| --- | --- | --- |
| `Barbatos.SecsGem.Core` | [`API-REFERENCE.md`](../Barbatos.SecsGem.Core/API-REFERENCE.md) | `Barbatos.SecsGem`, `Barbatos.SecsGem.Serialization` |
| `Barbatos.SecsGem.Hsms` | [`API-REFERENCE.md`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md) | `Barbatos.SecsGem.Hsms` |
| `Barbatos.SecsGem.Serialization.Sml` | [`API-REFERENCE.md`](../Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md) | `Barbatos.SecsGem.Serialization.Sml` |

Concepts, the set it installs and what it deliberately leaves out: [`README.md`](README.md).

## Where to start looking

| You want to | Go to |
| --- | --- |
| Build or read an item tree | [`SecsItem`](../Barbatos.SecsGem.Core/API-REFERENCE.md#secsitem) |
| Build a message | [`SecsMessage`](../Barbatos.SecsGem.Core/API-REFERENCE.md#secsmessage) |
| Keep a link up and answer messages | [`HsmsConnection`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#hsmsconnection) |
| Reply to a primary you were handed | [`SecsTransaction`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secstransaction) |
| Declare what your entity understands | [`SecsMessageRouterBuilder`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsmessagerouterbuilder) |
| Configure timers and the transport | [`SecsGemOptions`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsgemoptions) |
| Wire it into a host | [`AddSecsGem`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#addsecsgem) |
| Print a message a person can read | [`SmlConvert`](../Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md#smlconvert) |
