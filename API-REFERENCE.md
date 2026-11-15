# Barbatos.SecsGem - API reference

One page per package, generated per release:

- [`Barbatos.SecsGem.Core`](src/Barbatos.SecsGem.Core/API-REFERENCE.md)
- [`Barbatos.SecsGem.Hsms`](src/Barbatos.SecsGem.Hsms/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Sml`](src/Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Json`](src/Barbatos.SecsGem.Serialization.Json/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Xml`](src/Barbatos.SecsGem.Serialization.Xml/API-REFERENCE.md)
- [`Barbatos.SecsGem.Serialization.Ini`](src/Barbatos.SecsGem.Serialization.Ini/API-REFERENCE.md)
- [`Barbatos.SecsGem.Mapper`](src/Barbatos.SecsGem.Mapper/API-REFERENCE.md)
- [`Barbatos.SecsGem.SourceGenerators`](src/Barbatos.SecsGem.SourceGenerators/API-REFERENCE.md)
- [`Barbatos.SecsGem.Catalog`](src/Barbatos.SecsGem.Catalog/API-REFERENCE.md)
- [`Barbatos.SecsGem.Gem`](src/Barbatos.SecsGem.Gem/API-REFERENCE.md)
- [`Barbatos.SecsGem.WaferMap`](src/Barbatos.SecsGem.WaferMap/API-REFERENCE.md)
- [`Barbatos.SecsGem`](src/Barbatos.SecsGem/API-REFERENCE.md) (metapackage - no API of its own)

## Namespaces

| Namespace | Assembly | Contents |
| --- | --- | --- |
| `Barbatos.SecsGem` | Core | `SecsItem`, `SecsMessage`, `SecsFormat`, `MessageHeader`, exceptions |
| `Barbatos.SecsGem.Serialization` | Core | `ISecsFormatReader`, `ISecsFormatWriter`, `SecsTranscoder` |
| `Barbatos.SecsGem.Hsms` | Hsms | connection, decoder, transactions, timers, routing |
| `Barbatos.SecsGem.Mapping` | Mapper | `SecsSerializer`, attributes, converters |
| `Barbatos.SecsGem.Catalog` | Catalog | `SecsCatalog`, stream/function and data item reference data |
| `Barbatos.SecsGem.Gem` | Gem | GEM state models, object model and equipment service |
| `Barbatos.SecsGem.WaferMap` | WaferMap | stream 12: setup data, the three map data formats, both ends |
| `Barbatos.SecsGem.Serialization.Sml` / `.Json` / `.Xml` / `.Ini` | the four spokes | format readers and writers |
| `Microsoft.Extensions.DependencyInjection` | Hsms, Gem, WaferMap, Mapper, spokes | `AddSecsGem`, `AddGemEquipment`, `AddWaferMapEquipment`, `AddSecsSml`, ... |