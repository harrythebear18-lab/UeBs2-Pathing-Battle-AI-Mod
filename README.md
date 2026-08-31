# UEBS2 Pathing & Battle AI Mod

A BepInEx mod for **Ultimate Epic Battle Simulator 2** that overhauls pathfinding, team AI, and battle dynamics to make large-scale battles behave more like real warfare.

## Requirements

- **UEBS2** (Ultimate Epic Battle Simulator 2) on Steam
- **BepInEx 5.4.21 (Mono)** installed in the game folder
- .NET Framework 4.7.2+ (included with the game)

## Installation

1. Install BepInEx 5.4.21 Mono into your UEBS2 game folder (`steamapps/common/UEBS2/`)
2. Copy `UEBS2PathingMod.dll` into `UEBS2/BepInEx/plugins/`
3. Launch the game — the mod loads automatically
4. Press **Numpad+** in battle to open the tuning window

## Features

### 1. Smart Search Scheduling

The game's NavGrid processes pathfinding targets in round-robin order. This mod replaces that with **priority-based team cycling** — teams with more active combat and larger forces get more search slots, so critical pathing is computed first.

- Configurable aggressiveness scaling (auto-tunes based on total unit count)
- Per-team threat scoring that adjusts dynamically as battles evolve

### 2. Strategic Target Assignment

Instead of every army blindly pathing toward the nearest enemy, the mod calculates **threat-weighted centroids** for enemy armies and assigns strategic targets accordingly.

- Armies are directed toward the most threatening enemy cluster, not just the closest
- Re-assignment interval is configurable (1–20 seconds)

### 3. Adaptive NavGrid Tuning

Dynamically adjusts the game's GPU pathfinding parameters based on battle size:

- **MaxActiveTargets** — scales up for larger battles, capped to avoid GPU overload
- **GridObstacleUpdateSpeed** — controls how fast dynamic obstacles (units) propagate in the flow field
- **AcceleratePaths** — keeps path acceleration on for faster route computation

### 4. Sub-Group Fracture System

The centerpiece of the mod. Armies on the same team are **spatially clustered into sub-groups** every few seconds. Each sub-group evaluates its local tactical situation and can "fracture" from the player's blanket order to act autonomously — mimicking how real military units behave in the field.

#### Tactical Assessment

Each sub-group is evaluated for:

| Factor | How it's measured |
|--------|-------------------|
| **Engaged** | Nearest enemy sub-group within 100 units |
| **Outnumbered** | Enemy/ally ratio exceeds ~1.67:1 |
| **Isolated** | Nearest friendly sub-group beyond 250 units |
| **Winning** | Kill ratio exceeds 2:1 |
| **Flanked** | Enemy sub-groups detected on opposite sides |
| **Health ratio** | Remaining units / original army count |
| **Ranged vs melee** | Majority of armies have projectiles or long attack range |

#### Morale System

Every sub-group has a **morale score (0–1)** derived from its situation:

| Factor | Morale Effect |
|--------|---------------|
| Health ratio (casualties) | Base — dead men don't fight |
| Kill ratio (winning/losing) | ±0.15 to ±0.3 |
| Taking losses, inflicting none | −0.2 |
| Outnumbered | −0.15 |
| Flanked / encircled | −0.25 |
| Isolated (no support) | −0.1 |
| Height disadvantage | −0.1 |
| High ground | +0.1 |
| Has cover | +0.05 |
| Ranged units in the open | −0.1 |

#### Autonomous Behaviors

Sub-groups choose actions based on a priority matrix (applies to **both attacking and holding armies**):

**Survival layer** (overrides everything):
- **Rout** — morale below rout threshold → full panic retreat, 300 units away from enemy
- **Anti-flank reposition** — encirclement detected → move perpendicular to escape
- **Seek cover** — ranged units caught in the open under fire → move to nearby cover

**Tactical retreat layer**:
- **Retreat** — morale below retreat threshold AND engaged → fall back toward friendlies. Attacking armies that are losing will retreat instead of suicide-charging. Exception: if on high ground and not outnumbered, they hold.

**Terrain layer**:
- **Seek high ground** — fighting uphill and not winning → reposition to nearby high ground

**Support layer**:
- **Reinforce** — unengaged groups break hold orders to reinforce nearby losing allies

**Exploitation layer**:
- **Pursue** — winning + enemy retreating (low health or low morale) → chase aggressively
- **Aggression boost** — high morale (>0.7) + winning → forced pursuit even for hold-order armies

**High ground preservation**:
- Groups on high ground that are engaged will **not** abandon the position for anything less than a survival-level threat (rout/flank/retreat/cover)

#### GPU Flag Overrides

When a sub-group fractures, the mod temporarily overrides GPU compute flags on its armies so the flow field actually takes effect:

- `HoldPosition = false` — so the GPU reads the flow field instead of freezing units
- `WalkAttack = false` (for retreating/routing) — so the GPU stops seeking enemies
- `HoldGuard = false` (for retreating/routing) — so guard range doesn't trap units

Original values are restored after each GPU dispatch, preserving the player's orders.

### 5. Terrain Analysis

The mod uses the active Unity terrain and physics raycasts to evaluate the battlefield:

- **Height advantage** — `Terrain.SampleHeight()` at unit positions (CPU-only, no GPU readback)
- **Cover detection** — raycasts at unit height (1.5m) toward enemies; if terrain blocks LOS, there's cover
- **High ground search** — spiral-samples 16 points around a sub-group to find nearby elevated positions
- **Cover search** — spiral-samples 12 points to find the nearest position with terrain blocking enemy LOS

Terrain analysis is automatically disabled for battles with 100k+ units to save CPU (height advantage still works via a single cheap sample).

### 6. Dispersion System

Instead of units clumping into tight columns following a single flow-field line, the mod spreads them across a wider front:

- **Wider formation length** — boosts the GPU's `FormationLength` parameter by up to 4x
- **Multiple offset targets** — up to 5 targets placed perpendicular to the movement direction, creating a broad attractor basin in the flow field
- Scales with unit count — larger armies form proportionally wider fronts
- Configurable dispersion factor (0 = vanilla tight columns, 1 = very wide front) and max spread width

### 7. Cinematic Camera Improvement

Replaces the vanilla auto-cinematic camera's random target selection with **combat-weighted selection**:

- Scores every army by proximity to enemies (60%), kill count (30%), and remaining units (10%)
- Weighted-random pick blends combat score with uniform coverage based on bias strength
- `BiasStrength = 0` → fully random (vanilla), `1.0` → always the hottest clash
- Configurable focus interval (time between camera cuts)

## UI Window (Numpad+)

An in-game IMGUI window with live tuning for all features:

- **General** — mod enable, dynamic scaling, aggressiveness slider
- **Pathing** — smart scheduling, strategic targeting, re-assign interval
- **NavGrid** — adaptive tuning, max active targets, obstacle update speed
- **Cinematic Camera** — combat bias toggle, bias strength, focus interval
- **Sub-Group Fracture** — enable, cluster radius, reassess interval, individual behavior toggles, morale thresholds, aggression boost, dispersion controls
- **Live stats** — team priorities, active targets, sub-group states with color-coded morale, engagement flags, terrain info

The window captures the mouse cursor and suppresses camera mouse-look while open, so you can interact with sliders and toggles freely. Press **Numpad+** or **Escape** to close and return control to the game.

## Configuration

All settings are also available as BepInEx config entries (`BepInEx/config/UEBS2PathingMod.cfg`) and can be changed live via the UI window or edited before launch.

## Performance

- All CPU-side logic uses cheap operations (distance checks, terrain height sampling, a handful of raycasts per sub-group per cycle)
- Assessment intervals scale dynamically for huge battles (50k+ units → 6s, 150k+ units → 10s)
- Terrain cover/high-ground search is skipped for 100k+ unit battles
- GPU flow-field targets are injected via the game's existing `NavGrid.AddTarget` API — no GPU buffer manipulation
- Extra `FullSearch` dispatches for mod targets are scaled by aggressiveness to avoid GPU overload

## Technical Details

- **Engine**: Unity 2018.4.26f1
- **Mod loader**: BepInEx 5.4.21 Mono
- **Patching**: HarmonyX via BepInEx
- **Pathfinding**: GPU-based flow fields via `NavGpu` compute shader — the mod injects targets and tunes parameters but does not modify the compute shader itself
- **Safe patches**: All patches use Prefix/Postfix hooks on existing Update loops. Every patch is wrapped in try/catch to prevent mod errors from crashing the game's nav loop.

## Building from Source

```
dotnet build -c Release
```

Output: `bin/Release/UEBS2PathingMod.dll`

The `.csproj` expects BepInEx and game assemblies in the UEBS2 managed folder. Adjust `GameDir` in the `.csproj` if your install path differs.
