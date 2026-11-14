# build

Packaging assets. Everything here is optional - the build and `dotnet pack` both work without it.

| File | Purpose |
| --- | --- |
| `nuget.png` | Shared fallback package icon (128x128 PNG). |
| `secsgem-core-logo.png` | Per-package icon, selected with `<PackageIconSourceFile>`. |
| `barbatos.snk` | Strong-name key. **Git-ignored** - signing turns itself on only when this file is present. |

Generate a key with:

```
dotnet tool install -g dotnet-strong-name   # or use sn.exe from a VS developer prompt
sn -k build\barbatos.snk
```