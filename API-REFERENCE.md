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
- [`Barbatos.SecsGem.Material`](src/Barbatos.SecsGem.Material/API-REFERENCE.md)
- [`Barbatos.SecsGem.Objects`](src/Barbatos.SecsGem.Objects/API-REFERENCE.md)
- [`Barbatos.SecsGem.Carriers`](src/Barbatos.SecsGem.Carriers/API-REFERENCE.md)
- [`Barbatos.SecsGem.Substrates`](src/Barbatos.SecsGem.Substrates/API-REFERENCE.md)
- [`Barbatos.SecsGem.ControlJobs`](src/Barbatos.SecsGem.ControlJobs/API-REFERENCE.md)
- [`Barbatos.SecsGem.ProcessJobs`](src/Barbatos.SecsGem.ProcessJobs/API-REFERENCE.md)
- [`Barbatos.SecsGem.MaterialFlow`](src/Barbatos.SecsGem.MaterialFlow/API-REFERENCE.md)
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
| `Barbatos.SecsGem.Material` | Material | stream 3: material status, location, transfer, movement, both ends |
| `Barbatos.SecsGem.Objects` | Objects | stream 14: GetAttr, SetAttr, the three namelists, and create, delete and act - either direction |
| `Barbatos.SecsGem.Carriers` | Carriers | SEMI E87: the carrier and load port objects, their seven state machines, the actions |
| `Barbatos.SecsGem.Substrates` | Substrates | SEMI E90: the substrate and location objects and their state machines; no messages of its own |
| `Barbatos.SecsGem.ControlJobs` | ControlJobs | SEMI E94: the control job object, its state machine and the commands that drive it; no messages of its own |
| `Barbatos.SecsGem.ProcessJobs` | ProcessJobs | SEMI E40: the process job, its nine-state machine, and stream 16 at both ends |
| `Barbatos.SecsGem.MaterialFlow` | MaterialFlow | The E87-E90 join: a slot map becomes substrates, and the convention that does it |
| `Barbatos.SecsGem.Serialization.Sml` / `.Json` / `.Xml` / `.Ini` | the four spokes | format readers and writers |
| `Microsoft.Extensions.DependencyInjection` | Hsms, Gem, WaferMap, Material, Objects, Carriers, Substrates, ControlJobs, ProcessJobs, MaterialFlow, Mapper, spokes | `AddSecsGem`, `AddGemEquipment`, `AddWaferMapEquipment`, `AddMaterialHost`, `AddControlJobs`, `AddProcessJobEquipment`, `AddCarrierSubstrateBridge`, `AddSecsSml`, ... |