# Silent Fog — Client (source)

Client-side Harmony mod for 7 Days to Die 3.4.x. Version **1.15.0**.

## What it does

- **distance-based fog**: a world-space cylinder of clear air that follows the
  player with a 4 m trigger, so the fog trails behind him instead of being glued
  to the camera; it starts at 8 m (about 4 m ahead / 12 m behind while moving);
- **dynamic fog banks**: layered, domain-warped 3D FBM noise makes the fog break
  into soft wisps and patches that drift with the wind; the noise slowly sinks
  downwards and the fog is denser near the ground;
- **unified fog field**: instead of switching between «outdoor / house / trader»
  modes, one continuous layer measures how much of the camera ray passed inside
  up to 6 obstacle boxes; the interior stays clear and the fog outside the walls
  stays dense;
- **base protection**: masks are built from **all players' land claims** with a
  3 m outward pad, so the fog does not creep behind base walls;
- **zenith fix**: a depth-based far-fog term fills the vertical above the fog
  centre (the horizontal cylinder left it almost clear); it is suppressed inside
  masks, so trader/POI/base interiors stay clean;
- **host lock**: the host can force the fog on for every client (`sf_force`);
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
camera. This mod replaces it with a world-space fog field: the shader reads the
depth buffer, reconstructs the world position of each pixel and blends the fog
by `smoothstep(start, end, path length outside the clear boxes)`. The field
centre follows the player with a 4 m trigger, so the fog trails behind him
instead of being glued to the camera.

The shader lives in an AssetBundle (`AssetBundles/silentfogfog`) built with
**Unity 2022.3.62f2** — the exact engine version of the game. If the bundle is
missing or fails to load, the mod falls back to the vanilla exponential fog
instead of breaking.

Vanilla fog density is forced to zero while the effect is active, so the two
never stack.

Inside a house or quest POI the shader receives up to 6 world-space boxes
(trader prefab, compact POI, large POI anchor, player bases). For every pixel it
measures the share of the camera ray that passed inside those boxes, and that
share suppresses the fog: rooms, trader compounds and claimed bases stay clear
while the air outside the walls is still dense. Box edges use a 4–6 m noisy
falloff instead of a hard wall. Large POIs are anchored with a static box, not a
per-frame ray scan.

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
| `sf_force` | host lock: clients cannot disable the fog |
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
| `sf_dynamic_on` | world-space fog banks (wind) |
| `sf_dynamic_amount` | bank amplitude, 0–1 (±22% at 100%) |
| `sf_dynamic_speed` | wind speed, 0–1 |

The client menu is **read-only**: every slider is greyed out, values arrive from
the host and the panel says «Настройки задаёт хост». Only the host can change
them. Values are sent on change and refreshed every few seconds, so a player
who joins later still gets the current settings.

The host lock is on by default. The server re-sends `sf_force`, `sf_fog_on`,
`sf_distance_on` and `sf_outdoor` every 1.5 s and performs a full settings sync
every 5 s, so a client cannot turn the fog off through the menu or a local
`setcvar`. Clients can still remove the mod entirely; the lock covers the menu
and local overrides, not the files on their disk.

Settings are stored in `SilentFog.cfg` next to the game saves.

## Menu

`NumPad5` or `F7`. Current rows:

- preset buttons — «Лёгкий», «Средний», «Плотный», «Шторм»;
- «Сила тумана» — overall fog strength;
- «Клиенты не могут отключить туман» — host lock (`sf_force`, on by default);
- «Начало тумана, м» — where the fog starts from the world centre (default 8 m;
  about 4 m ahead / 12 m behind while moving);
- «Конец тумана, м» — where it becomes solid (default 35 m);
- «Оттенок биома» — how strongly the biome palette is applied (0–100%);
- «Туман в ночь орды» — from 18:00 on blood moon the fog reddens (burgundy by
  22:00), comes 6 m closer and thickens up to +35%; strength slider below;
- «Защита помещения / сейф-зоны» — how far the start moves under a roof;
- closed vehicle / bike / motorcycle strengths;
- «Туман по расстоянию (свой шейдер)» — turn the post-effect off to fall back
  to the vanilla exponential fog (handy for troubleshooting);
- world follow: the fog centre trails the player by up to 4 m and stays put when
  he stops, so the clear area is larger behind him; inside houses/POIs it
  envelops the building instead;
- «ДЫХАНИЕ ТУМАНА» — toggle plus amplitude: the fog edge drifts up to ±8 m and
  density breathes both ways, ±25% of the set strength, so the fog thickens and
  thins on its own;
- «ДИНАМИКА ТУМАНА» — toggle plus amplitude and wind speed: layered
  world-space banks that drift with the wind, ±22% density at 100%;
- ash storm toggle and density;
- night fog softening (only with DifRUST);
- status line: `одеяло: внутри (торговец/дом/база) N%` or `одеяло: объёмов N`,
  plus `туман 0–35` inside / `8–43` outside;
- «Отладка: зенит (2)» — local diagnostic overlay (`_FogDebug=2`), never sent
  to clients.

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

### Server (optional, for the host lock)

Install `SilentFogServer` (XML only, no DLL) on the host or dedicated server.
It broadcasts the host settings, keeps clients from disabling the fog and gives
players without the client mod a denser vanilla fog. The client mod works
without it; only the host lock and the forced sync require the server part.

## Source map

- `Source/SilentFogClient.cs` — Harmony patches, fog density logic, host menu.
- `Source/DistanceFog.cs` — the image effect and its material.
- `Source/FogObstacles.cs` — world-space masks (trader, POI, all-claim bases).
- `Source/BiomeFog.cs` — biome palette and blending.
- `Source/SilentFogClient.csproj` — build project.
- `Source/build.ps1` — build helper.
- `UnityShader/Assets/*.shader` — fog and particle shaders.
- `UnityShader/build-bundle.ps1` — AssetBundle builder.
- `ModInfo.xml` — mod metadata.
- `CHANGELOG.md` — version history.

## Version

`1.15.0` — see `CHANGELOG.md` for the full history.

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

