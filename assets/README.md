# assets

## `conformance/`

The same three messages written in all five formats, committed byte for byte.

| Fixture | What it is for |
| --- | --- |
| `names-and-labels.*` | A realistic S6F11, with a message name and labels at four depths. What each format looks like when it is carrying everything. |
| `every-format.*` | One item of each of the fifteen SECS-II formats, in one message. |
| `text-escaping.*` | Strings full of the characters each format has to escape - SML's quote, JSON's backslash, XML's angle brackets, INI's `=` and `;`. |

Each exists in `.sml`, `.json`, `.xml`, `.ini` and `.secs2`.

`ConformanceFixtureTests` in `tests/Barbatos.SecsGem.Serialization.UnitTests` asserts twice over
each file: that every writer still produces it byte for byte, and that every reader still parses it
back to the message it came from.

**Why byte for byte.** The round-trip matrix next door converts all twenty-five ordered pairs of
formats and proves the stack agrees with itself. It cannot catch a change to a writer that also
changes its reader - that passes every round trip and still breaks everything outside this
repository: the host that greps a capture, the vendor tool that reads the recipe, the fixture in
somebody else's test suite. These files are the record of what shipped.

They are also documentation that cannot go stale. To see what a real SECS message looks like in
INI, open `conformance/names-and-labels.ini`.

**Line endings.** `.gitattributes` marks this directory `-text`, so git does not normalise it. The
writers emit CRLF; normalising would make the comparison pass on Windows and fail on Linux.

**When a fixture changes.** The test writes the new output beside the old as `.actual` and says so.
Diff the two, and answer the question it is really asking: was the format change meant? If it was,
replace the fixture and say why in the commit. If it was not, the test has just saved somebody a
long afternoon.
