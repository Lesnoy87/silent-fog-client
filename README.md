# Silent Fog — Client (source)

Client-side Harmony mod for 7 Days to Die 3.4.x. Version **1.13.3**.

## What it does

- **distance-based fog**: a post-effect with a depth buffer, so the fog starts
  at a set distance and never reaches objects closer than that;
- indoor spaces stay clear while the fog is still visible through windows;
- **biome fog colour**: desert is warm sand, snow is cold blue, wasteland is
  ochre, radiated is toxic green, burnt forest is ash; pine forest, plains,
  city and caves keep the vanilla colour;
- roof detection, so a room with a hole or balcony still counts as shelter;
- smooth indoor/outdoor transitions;
- optional ash storm in the burnt forest, tied to the vanilla storm;
- host settings menu (`NumPad5` / `F7`);
- optional night fog softening when **DifRUST** is installed.

No gameplay changes: zombies, loot, POIs and progression are untouched.
No night lighting — that is a separate mod (DifRUST).

## How the fog works

The game's own fog is exponential and dimensionless: it always clings to the
camera. This mod replaces it with a camera image effect that reads the depth
buffer and blends the fog colour with `smoothstep(start, end, depth)`.

The shader lives in an AssetBundle (`AssetBundles/silentfogfog`) built with
**Unity 2022.3.62f2** — the exact engine version of the game. If the bundle is
missing or fails to load, the mod falls back to the vanilla exponential fog
instead of breaking.

Vanilla fog density is forced to zero while the effect is active, so the two
never stack.

## Settings sync

The host menu applies its values to every player through the game's own
`NetPackageModifyCVar` packet (`EntityBuffs.SetCustomVarNetwork`), using
custom CVars with the `sf_` prefix. There is no custom network protocol,
no sockets, no HTTP requests and no credential handling. Clients without the
mod simply ignore the packets, and clients with the mod fall back to local
defaults when the server has no fog mod installed.

Everything in the menu is synced, including fog start/end distance, biome tint
and the distance-fog toggle:

| CVar | Setting |
|---|---|
| `sf_fog_on` | fog master switch |
| `sf_fog_out` / `sf_fog_in` | outdoor / indoor strength |
| `sf_shelter_on` | use roof detection |
| `sf_fog_veh_closed` / `sf_fog_veh_open` | closed vehicle / bike strength |
| `sf_storm_on` / `sf_storm_density` | ash storm |
| `sf_night_fog` | night softening (with DifRUST) |
| `sf_distance_on` | distance-based fog (own shader) |
| `sf_distance_start` / `sf_distance_end` | fog start / end, metres |
| `sf_biome_tint` | biome colour strength |
| `sf_live_on` | fog breathing (experimental) |
| `sf_live_amount` | breathing amplitude, 0–1 |
| `sf_horde_on` | horde night fog (red, denser) |
| `sf_horde_fog` | horde fog strength, 0–1 |

The client menu is **read-only**: every slider is greyed out, values arrive from
the host and the panel says «Настройки задаёт хост». Only the host can change
them. Values are sent on change and refreshed every few seconds, so a player
who joins later still gets the current settings.

Settings are stored in `SilentFog.cfg` next to the game saves.

## Menu

`NumPad5` or `F7`. Current rows:

- preset buttons — «Лёгкий», «Средний», «Плотный», «Шторм»;
- «Сила тумана» — overall fog strength;
- «Начало тумана, м» — where the fog starts (default 12 m);
- «Конец тумана, м» — where it becomes solid (default 80 m);
- «Оттенок биома» — how strongly the biome palette is applied (0–100%);
- «Туман в ночь орды» — from 18:00 on blood moon the fog reddens (burgundy by
  22:00), comes 6 m closer and thickens up to +35%; strength slider below;
- «Защита помещения / сейф-зоны» — how far the start moves under a roof;
- closed vehicle / bike / motorcycle strengths;
- «Туман по расстоянию (свой шейдер)» — turn the post-effect off to fall back
  to the vanilla exponential fog (handy for troubleshooting);
- «ДЫХАНИЕ ТУМАНА» — toggle plus amplitude: the fog edge drifts up to ±8 m and
  density breathes both ways, ±25% of the set strength, so the fog thickens and
  thins on its own;
- ash storm toggle and density;
- night fog softening (only with DifRUST).

All rows are host-only; for clients the whole list is disabled.

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

### Shader bundle

The shaders are built separately:

```powershell
cd UnityShader
.\build-bundle.ps1
```

The script creates a Unity project if needed, builds
`AssetBundles/silentfogfog`, verifies the file hash and installs it into the
mod folder. The game must be closed: AssetBundle files are locked while the
game is running.

## Install

```text
%APPDATA%\7DaysToDie\Mods\SilentFogClient\ModInfo.xml
%APPDATA%\7DaysToDie\Mods\SilentFogClient\SilentFogClient.dll
%APPDATA%\7DaysToDie\Mods\SilentFogClient\AssetBundles\silentfogfog
```

The `AssetBundles` folder is required for the distance fog. Without it the mod
still works, but on the vanilla exponential fog.

Launch the game without EAC (DLL/Harmony mod).

## Source map

- `Source/SilentFogClient.cs` — Harmony patches, fog density logic, host menu.
- `Source/DistanceFog.cs` — the image effect and its material.
- `Source/BiomeFog.cs` — biome palette and blending.
- `Source/SilentFogClient.csproj` — build project.
- `Source/build.ps1` — build helper.
- `UnityShader/Assets/*.shader` — fog and particle shaders.
- `UnityShader/build-bundle.ps1` — AssetBundle builder.
- `ModInfo.xml` — mod metadata.
- `CHANGELOG.md` — version history.

## Version

`1.13.3` — see `CHANGELOG.md` for the full history.

## License

Starting with **1.15.0**, Silent Fog is **All Rights Reserved /
source-available**, not MIT.

- Free to download, install, play, and use on free-to-join servers.
- Free to include the **unmodified** mod in free non-commercial modpacks,
  server packs and Nexus collections with credit to **Lesnoy87 / Silent Fog**
  and a link to the original project.
- No selling, paywalls, ad-gating, reuploads as your own work, modified
  redistribution, or integration into other mods/products without written
  permission.

The authoritative text is `LICENSE` (English). A Russian summary is in
`LICENSE-RU.md`. Versions **1.13.3 and earlier** remain under MIT; those
rights cannot be revoked.

