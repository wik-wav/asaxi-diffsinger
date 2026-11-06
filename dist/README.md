# dist/

`ASAXI.DiffSinger.dll` — the built plugin, one file, ready to copy into OpenUtau's `Plugins`
folder. No .NET SDK required to use it.

## Verify it

The build is reproducible: `dotnet build -c Release` from the commit that ships this file produces
these exact bytes, from any directory on any machine. Debug information is switched off in the
project for that reason — a portable PDB embeds its own absolute path, which both breaks
reproducibility and leaks the builder's directory layout.

```powershell
cd dist
(Get-FileHash ASAXI.DiffSinger.dll -Algorithm SHA256).Hash.ToLower()
Get-Content SHA256SUMS
```

The two must match. To check it yourself against the source rather than trusting this directory:

```powershell
git clone https://github.com/wik-wav/asaxi-diffsinger
cd asaxi-diffsinger
dotnet build ref/OpenUtau.Core.Facade -c Release
dotnet build -c Release
(Get-FileHash bin/Release/ASAXI.DiffSinger.dll -Algorithm SHA256).Hash.ToLower()
```

Same hash means this file is exactly what the source in this repository builds.

## Which version

Built from the commit that added it — `git log --oneline -- dist/` names it. The plugin reports
`[DIFFS ASA] DiffSinger Asaxi Phonemizer` and `GetLangCode() == "asa"`.

A rebuild is needed only when the source changes, **not** when OpenUtau updates: the plugin is
compiled against the reference assembly in `ref/`, not against an installed OpenUtau. After an
OpenUtau update, run the conformance test rather than rebuilding — it tells you whether the host's
API still matches what the plugin overrides.
