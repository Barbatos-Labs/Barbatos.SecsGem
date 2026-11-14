# Barbatos.SecsGem - API reference

One page per package, generated per release:

- [`Barbatos.SecsGem.Core`](src/Barbatos.SecsGem.Core/API-REFERENCE.md)
- [`Barbatos.SecsGem.Hsms`](src/Barbatos.SecsGem.Hsms/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Sml`](src/Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Json`](src/Barbatos.SecsGem.Serialization.Json/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Xml`](src/Barbatos.SecsGem.Serialization.Xml/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Ini`](src/Barbatos.SecsGem.Serialization.Ini/API-REFERENCE.md)
- [`Barbatos.SecsGem.Mapper`](src/Barbatos.SecsGem.Mapper/API-REFERENCE.md)
- [`Barbatos.SecsGem.Gem`](src/Barbatos.SecsGem.Gem/API-REFERENCE.md)

## Namespaces

| Namespace | Assembly | Contents |
| --- | --- | --- |
| `Barbatos.SecsGem` | Core | `SecsItem`, `SecsMessage`, `SecsFormat`, `MessageHeader`, exceptions |
| `Barbatos.SecsGem.Serialization` | Core | `ISecsFormatReader`, `ISecsFormatWriter`, `SecsTranscoder` |
| `Barbatos.SecsGem.Hsms` | Hsms | connection, decoder, transactions, timers, routing |
| `Barbatos.SecsGem.Mapping` | Mapper | `SecsSerializer`, attributes, converters |
| `Barbatos.SecsGem.Gem` | Gem | GEM state models and services |
| `Microsoft.Extensions.DependencyInjection` | Hsms, Mapper, spokes | `AddSecsGem`, `AddSecsSml`, ... |