# Silent Fog — Client (source)

Client-side Harmony mod for 7 Days to Die 3.4.x. Version **1.8.2**.

## What it does

- dense fog outside;
- weak fog in POIs, houses, trader safe zones and closed vehicles;
- roof detection, so a room with a hole or balcony still counts as shelter;
- smooth indoor/outdoor transitions;
- optional ash storm in the burnt forest, tied to the vanilla storm;
- host settings menu (`NumPad5` / `F7`) with four density sliders;
- optional night fog softening when **DifRUST** is installed.

No gameplay changes: zombies, loot, POIs and progression are untouched.
No night lighting — that is a separate mod (DifRUST).

## Settings sync

The host menu applies its values to every player through the game's own
`NetPackageModifyCVar` packet (`EntityBuffs.SetCustomVarNetwork`), using
custom CVars with the `sf_` prefix. There is no custom network protocol,
no sockets, no HTTP requests and no credential handling. Clients without the
mod simply ignore the packets, and clients with the mod fall back to local
defaults when the server has no fog mod installed.

## Requirements

- .NET SDK 8/10 (for building)
- 7 Days to Die 3.4.x
- `0_TFP_Harmony`

## Build

From this folder:

```powershell
cd Source
.\build.ps1
```

Or with an explicit game path:

```powershell
dotnet build .\Source\SilentFogClient.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
```

Build output:

```text
Source\bin\Release\netstandard2.1\SilentFogClient.dll
```

## Install

```
%APPDATA%\7DaysToDie\Mods\SilentFogClient\ModInfo.xml
%APPDATA%\7DaysToDie\Mods\SilentFogClient\SilentFogClient.dll
```

Launch the game without EAC (DLL/Harmony mod).

## Source map

- `Source/SilentFogClient.cs` — Harmony patches, fog density logic, host menu.
- `Source/SilentFogClient.csproj` — build project.
- `Source/build.ps1` — build helper.
- `ModInfo.xml` — mod metadata.
- `CHANGELOG.md` — version history.

## Version

`1.8.2` — see `CHANGELOG.md` for the full history.
