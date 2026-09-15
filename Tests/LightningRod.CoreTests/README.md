# Lightning Rod portable C# regression harness

This project links the **actual production C# files** for `CoreAudioPlaybackTiming`
and `OrreryLightningRodState<TTarget>`. It contains 12 regression groups and has no
third-party packages. It does not substitute a Python implementation of the rules.

It does **not** compile or run `CoreAudioRuntime` against Unity/Star Vortex and does
not test audio-device playback, the Zap prefab, damage routing or multiplayer.

From the repository root, with a .NET 8 SDK installed:

```sh
dotnet restore Tests/LightningRod.CoreTests/LightningRod.CoreTests.csproj --configfile Tests/LightningRod.CoreTests/NuGet.Config
dotnet run --project Tests/LightningRod.CoreTests --configuration Release --no-restore
```

The harness targets C# 7.3 and treats compiler warnings as errors. A successful run
prints the number of completed groups and assertions; do not infer success merely
from the presence of these test files.

## Static preservation checks

```sh
python Tests/LightningRod.CoreTests/verify_source.py
```

This separate script checks the pinned baseline blob, preserved audio lookup and
default constants, source boundaries, and test input files. Its delimiter checks
are lexical checks, **not C# compilation**. For an exported checkpoint without a
Git checkout, pass `--baseline-file` pointing to the exact original audio source.

## Validation in the authoring environment

The static checks were executed. The C# harness was **not executed**: no C#/.NET
compiler was available, and acquiring an SDK was unsuccessful. Native Unity build
and co-op playtests also remain outstanding.
