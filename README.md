# Silent Fog — Client (source)

Client-side Harmony mod for 7 Days to Die 3.4.x.

## What it does

- dense fog outside;
- weak fog in POIs, houses and Safe Zones;
- no night lighting;
- no network, credential, file-system or EAC-related code.

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

The compiled DLL is copied to `SilentFogClient.dll` by `build.ps1`.

## Install

```
%APPDATA%\7DaysToDie\Mods\SilentFogClient\ModInfo.xml
%APPDATA%\7DaysToDie\Mods\SilentFogClient\SilentFogClient.dll
```

Launch the game without EAC (DLL/Harmony mod).

## Source map

- `Source/SilentFogClient.cs` — Harmony patches and fog density logic.
- `Source/SilentFogClient.csproj` — build project.
- `Source/build.ps1` — build helper.
- `ModInfo.xml` — mod metadata.
