# Barbatos.SecsGem

The one reference that gets you a working HSMS link: **Core**, **Hsms** and **SML** in a single
package, for .NET 8, 9 and 10. Ships no assembly of its own.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem
```

## What comes with it

| Package | What it gives you |
| --- | --- |
| [`Barbatos.SecsGem.Core`](https://www.nuget.org/packages/Barbatos.SecsGem.Core) | `SecsItem`, `SecsMessage`, the binary codec, the serialization contracts. |
| [`Barbatos.SecsGem.Hsms`](https://www.nuget.org/packages/Barbatos.SecsGem.Hsms) | The HSMS-SS state machine, T3-T8, automatic S9Fx, hosting and DI. |
| [`Barbatos.SecsGem.Serialization.Sml`](https://www.nuget.org/packages/Barbatos.SecsGem.Serialization.Sml) | `.sml`, so a captured message is readable without a second tool. |

SML is in the set and the other three formats are not. That is a judgement rather than an
oversight: SML is the format a SECS engineer reads at three in the morning, and `message.ToSml()`
in a log line is worth more than any amount of structured output nobody can scan. JSON, XML and INI
are real work with real consumers - add them when you have one.

## Enough to talk to a tool

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
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

```json
{
  "Barbatos": {
    "SecsGem": {
      "Mode": "Passive",
      "Port": 5000,
      "DeviceId": 0,
      "T3": "00:00:45",
      "T8": "00:00:05"
    }
  }
}
```

The options are bound and **validated on start**, so a T8 longer than T3 fails the host rather than
the first stalled message.

## What is not in here

| Package | Add it when |
| --- | --- |
| `Barbatos.SecsGem.Serialization.Json` / `.Xml` / `.Ini` | Something that is not a SECS tool has to read your messages. |
| `Barbatos.SecsGem.Catalog` | You want `S6F11` in a log line to read `S6F11 W Event Report Send`, or need the standard name, direction and body items for any of 524 stream/function pairs. |
| `Barbatos.SecsGem.Mapper` | You would rather map C# objects than build item trees by hand. |
| `Barbatos.SecsGem.SourceGenerators` | You want the mapper's property order read from the syntax tree, and trim-safe. |
| `Barbatos.SecsGem.Gem` | You are writing equipment rather than a host, and want E30's state models, host-configured reporting, alarms and remote commands instead of answering S1F13 by hand. |

## Documentation

Each package documents itself. Start with the one you are using:

- [`Barbatos.SecsGem.Core`](../Barbatos.SecsGem.Core/README.md)
- [`Barbatos.SecsGem.Hsms`](../Barbatos.SecsGem.Hsms/README.md)
- [`Barbatos.SecsGem.Serialization.Sml`](../Barbatos.SecsGem.Serialization.Sml/README.md)
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)

Two runnable demos - a host and a piece of equipment selecting each other in one process, then the
same two over a real socket:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
