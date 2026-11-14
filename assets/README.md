# assets

Conformance fixtures shared by the serialization tests and the benchmarks.

| File | Purpose |
| --- | --- |
| `common.sml` | Large SML corpus - the round-trip baseline. |
| `common.json` | The same corpus in the JSON shape. |
| `gem/*.sml` | Hand-written GEM messages (S1F3, S2F33, S6F11, ...). |

The round-trip matrix in `tests/Barbatos.SecsGem.Serialization.UnitTests` reads every file here
and asserts `sml -> X -> sml` is byte-identical for X in { json, xml, ini, binary }.