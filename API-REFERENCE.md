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
- [`Barbatos.SecsGem.DataSets`](src/Barbatos.SecsGem.DataSets/API-REFERENCE.md)
- [`Barbatos.SecsGem.Subsystems`](src/Barbatos.SecsGem.Subsystems/API-REFERENCE.md)
- [`Barbatos.SecsGem.ControlPrograms`](src/Barbatos.SecsGem.ControlPrograms/API-REFERENCE.md)
- [`Barbatos.SecsGem.ItemTransfer`](src/Barbatos.SecsGem.ItemTransfer/API-REFERENCE.md)
- [`Barbatos.SecsGem.Recipes`](src/Barbatos.SecsGem.Recipes/API-REFERENCE.md)
- [`Barbatos.SecsGem.Material`](src/Barbatos.SecsGem.Material/API-REFERENCE.md)
- [`Barbatos.SecsGem.Objects`](src/Barbatos.SecsGem.Objects/API-REFERENCE.md)
- [`Barbatos.SecsGem.Carriers`](src/Barbatos.SecsGem.Carriers/API-REFERENCE.md)
- [`Barbatos.SecsGem.Substrates`](src/Barbatos.SecsGem.Substrates/API-REFERENCE.md)
- [`Barbatos.SecsGem.ControlJobs`](src/Barbatos.SecsGem.ControlJobs/API-REFERENCE.md)
- [`Barbatos.SecsGem.ProcessJobs`](src/Barbatos.SecsGem.ProcessJobs/API-REFERENCE.md)
- [`Barbatos.SecsGem.MaterialFlow`](src/Barbatos.SecsGem.MaterialFlow/API-REFERENCE.md)
- [`Barbatos.SecsGem.PerformanceTracking`](src/Barbatos.SecsGem.PerformanceTracking/API-REFERENCE.md)
- [`Barbatos.SecsGem.Productivity`](src/Barbatos.SecsGem.Productivity/API-REFERENCE.md)
- [`Barbatos.SecsGem.PerformanceFlow`](src/Barbatos.SecsGem.PerformanceFlow/API-REFERENCE.md)
- [`Barbatos.SecsGem`](src/Barbatos.SecsGem/API-REFERENCE.md) (metapackage - no API of its own)

## Namespaces

| Namespace | Assembly | Contents |
| --- | --- | --- |
| `Barbatos.SecsGem` | Core | `SecsItem`, `SecsMessage`, `SecsFormat`, `MessageHeader`, exceptions |
| `Barbatos.SecsGem.Serialization` | Core | `ISecsFormatReader`, `ISecsFormatWriter`, `SecsTranscoder` |
| `Barbatos.SecsGem.Hsms` | Hsms | connection, decoder, transactions, timers, routing |
| `Barbatos.SecsGem.Mapping` | Mapper | `SecsSerializer`, attributes, converters |
| `Barbatos.SecsGem.Catalog` | Catalog | `SecsCatalog`, stream/function and data item reference data, and how firm each stream's is |
| `Barbatos.SecsGem.Gem` | Gem | GEM state models, object model and equipment service |
| `Barbatos.SecsGem.WaferMap` | WaferMap | stream 12: setup data, the three map data formats, both ends |
| `Barbatos.SecsGem.DataSets` | DataSets | stream 13: open, send, get, directory, close and status, both ends - and the payload as the encoded item that carried it, because `DSDATA` is defined nowhere |
| `Barbatos.SecsGem.Subsystems` | Subsystems | stream 18: command, status, variable read and write, listing, events, alarms and the 2D code read, both ends - and `SubsystemId`, which carries either reading of an `SSID` the dictionary never defines |
| `Barbatos.SecsGem.ControlPrograms` | ControlPrograms | stream 8: the load inquire and grant, the boot program send, a named program fetched back, the directory, the abort and the revision - with `CPID` and `CPBODY` read off the slots `PPID` and `PPBODY` occupy in stream 7's identical design |
| `Barbatos.SecsGem.Recipes` | Recipes | stream 15: the namespace directory, create and delete, the recipe download in two transactions, the upload, the delete, the select and the operation abort - nine pairs that need no inference at all, with the recipe body as stream 7's own defined PPBODY |
| `Barbatos.SecsGem.ItemTransfer` | ItemTransfer | stream 21: the item directory, an item's attributes, the transfer request and grant, the data send, the abort and the delete - from **one end type**, because every row travels either way, and with the payload as the encoded item that carried it |
| `Barbatos.SecsGem.Material` | Material | stream 3: material status, location, transfer, movement, both ends |
| `Barbatos.SecsGem.Objects` | Objects | SEMI E39, both its streams: stream 14's GetAttr, SetAttr, namelists, create, delete and act - either direction - and the enhanced half of stream 2, where GEM's own questions carry an OBJSPEC |
| `Barbatos.SecsGem.Carriers` | Carriers | SEMI E87: the carrier and load port objects, their seven state machines, the actions |
| `Barbatos.SecsGem.Substrates` | Substrates | SEMI E90: the substrate and location objects and their state machines; no messages of its own |
| `Barbatos.SecsGem.ControlJobs` | ControlJobs | SEMI E94: the control job object, its state machine and the commands that drive it; no messages of its own |
| `Barbatos.SecsGem.ProcessJobs` | ProcessJobs | SEMI E40: the process job, its nine-state machine, and stream 16 at both ends |
| `Barbatos.SecsGem.MaterialFlow` | MaterialFlow | The joins between E87, E90, E40 and E94: a slot map becomes substrates, a job's ending reaches them, and a lot waits for its boxes |
| `Barbatos.SecsGem.PerformanceTracking` | PerformanceTracking | SEMI E116: a machine's modules, their three EPT states, the task a block keeps, and where the time went; no messages of its own |
| `Barbatos.SecsGem.Productivity` | Productivity | SEMI E10 and E79: the six equipment states, their roll-ups, the RAM metrics and OEE; no wire presence at all |
| `Barbatos.SecsGem.PerformanceFlow` | PerformanceFlow | The join between E116 and E10/E79: a module's EPT state drives the tool's six states, by way of the task type that records the intent |
| `Barbatos.SecsGem.Serialization.Sml` / `.Json` / `.Xml` / `.Ini` | the four spokes | format readers and writers |
| `Microsoft.Extensions.DependencyInjection` | Hsms, Gem, WaferMap, DataSets, Subsystems, ItemTransfer, Recipes, Material, Objects, Carriers, Substrates, ControlJobs, ProcessJobs, MaterialFlow, PerformanceTracking, Mapper, spokes | `AddSecsGem`, `AddGemEquipment`, `AddWaferMapEquipment`, `AddDataSetEquipment`, `AddDataSetHost`, `AddSubsystemEquipment`, `AddSubsystemHost`, `AddControlProgramEquipment`, `AddControlProgramHost`, `AddMaterialHost`, `AddControlJobs`, `AddProcessJobEquipment`, `AddCarrierSubstrateBridge`, `AddProcessJobSubstrateBridge`, `AddControlJobCarrierBridge`, `AddPerformanceTracking`, `AddEnhancedServices`, `AddProductivityTracking`, `AddPerformanceProductivityBridge`, `AddSecsSml`, ... |