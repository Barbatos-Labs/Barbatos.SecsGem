# Barbatos.SecsGem.Core

SECS-II object model for .NET: the SecsItem/SecsMessage abstract syntax tree, a zero-copy binary codec over ReadOnlySequence and IBufferWriter, and the Hub-and-Spoke serialization contracts (ISecsFormatReader / ISecsFormatWriter / SecsTranscoder). Contains no transport and no sockets, so it is safe to use in tooling, analyzers and WebAssembly.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Core
```

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).