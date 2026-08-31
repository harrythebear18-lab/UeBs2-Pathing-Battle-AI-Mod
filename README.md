# UEBS2 Pathing & Battle AI Mod

A BepInEx mod for **Ultimate Epic Battle Simulator 2** that overhauls pathfinding, team AI, and battle dynamics to make large-scale battles behave more like real warfare. Includes a **two-stage AI battle agent** (Qwen VL vision + Qwen Coder) that watches the battlefield and proposes flow field manipulations in real time.

## Requirements

- **UEBS2** (Ultimate Epic Battle Simulator 2) on Steam
- **BepInEx 5.4.21 (Mono)** installed in the game folder
- .NET Framework 4.6.2+ (included with the game)
- **Optional**: [Ollama](https://ollama.ai) running locally with `qwen2.5vl:7b` (vision) and `qwen2.5-coder:7b` (coder) models for the AI battle agent

## Installation

1. Install BepInEx 5.4.21 Mono into your UEBS2 game folder
2. Copy `UEBS2PathingMod.dll` into `UEBS2/BepInEx/plugins/`
3. Launch the game — the mod loads automatically
4. Press **Numpad+** in battle to open the tuning window

### Optional: AI Battle Agent

1. Install [Ollama](https://ollama.ai) locally
2. Pull the models:
   ```
   ollama pull qwen2.5vl:7b
   ollama pull qwen2.5-coder:7b
   ```
3. Ensure Ollama is running (`ollama serve` or the desktop app)
4. In the mod's UI (Numpad+), enable "AI Battle Agent" and configure models/interval

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
- `HoldGuard = false` (for all fracturing armies) — so guard range doesn't trap units

Original values are restored after each GPU dispatch, preserving the player's orders.

### 5. Terrain Analysis

The mod uses the active Unity terrain and physics raycasts to evaluate the battlefield:

- **Height advantage** — `Terrain.SampleHeight()` at unit positions (CPU-only, no GPU readback)
- **Cover detection** — raycasts at unit height (1.5m) toward enemies; if terrain blocks LOS, there's cover
- **High ground search** — spiral-samples 16 points around a sub-group to find nearby elevated positions
- **Cover search** — spiral-samples 12 points to find the nearest position with terrain blocking enemy LOS

Terrain analysis is automatically disabled for battles with 100k+ units to save CPU (height advantage still works via a single cheap sample).

### 6. Fortification Analysis

Detects and analyzes fortified positions on the battlefield:

- Identifies defensive clusters (armies with `HoldGuard` + structures/walls)
- Calculates fortification radius, defender count, and structural integrity
- Provides **morale bonuses** to defenders (+0.2) and **penalties** to attackers (−0.15)
- Guides attacking sub-groups to **distributed flank points** instead of frontal assaults
- Multiple attacking teams coordinate to hit different flanks simultaneously

### 7. Dispersion System (Braided Wide Front)

Instead of units clumping into tight columns or parallel lateral lines, the mod creates a **wide front with braided mixing**:

- **Single wide target** with boosted `FormationLength` (up to 4x) — creates a broad attractor basin, not parallel channels
- **Braided zigzag obstacles** — alternating diagonal soft blockers along the march path that force the flow field to weave, causing units to shift laterally as they advance
- Scales with unit count — larger armies form proportionally wider fronts
- Configurable dispersion factor (0 = vanilla tight columns, 1 = very wide braided front) and max spread width

The braided obstacles are soft (strength ~0.3 × dispersion) — they redirect rather than block, so units still advance but weave side to side. The result is a wide front that mixes as it moves, like a real marching body.

### 8. Flow Field Modulation

The mod injects **soft obstacles** into the game's GPU `ObstacleGrid` (an R8 RenderTexture) before each compute dispatch, then restores the original grid afterward. This enables:

- **Block direct paths** — place walls between two points to force routing around
- **Create corridors** — two parallel walls that funnel units through a specific path
- **Retreat wavefields** — wall off the forward path toward the enemy, funnel units into a clean retreat corridor, and braid the rear path so retreating units mix backward instead of clumping
- **Fortification approach blocking** — block all direct approaches to a fort except the assigned flank
- **Braided mixing** — zigzag obstacles along advance paths for organic formation weaving

Retreat wavefields are integrated into the fracture system with escalating strength:
- **Retreat** (0.7) — tactical fallback with strong push-back
- **Rout** (0.9) — panic, hard forward wall prevents re-engagement
- **Regroup** (0.4) — light wall, shaky units reform without re-engaging prematurely

### 9. AI Battle Agent (Ollama)

A **two-stage AI pipeline** that watches the battlefield and proposes flow field manipulations:

```
Screenshot → Qwen 2.5 VL (eyes) → tactical assessment
                                        ↓
Battle state JSON ──────────→ Qwen 2.5 Coder (actor) → proposed commands
                                        ↓
                                 UI: approve / reject
                                        ↓
                           FlowFieldModulator executes
```

#### Stage 1: Eyes (Qwen 2.5 VL)

Captures a screenshot (downscaled to 640px), sends it to the Qwen 2.5 VL vision model, and receives a tactical assessment:
- Which side is winning or losing
- Formation quality (tight columns, spread lines, clumping, gaps)
- Flank threats or encirclement attempts
- Terrain features affecting the battle
- Units retreating, routing, or breaking formation

#### Stage 2: Engineer/Actor (Qwen 2.5 Coder)

Receives the vision assessment + structured battle state JSON and proposes a set of flow field commands:

| Command | Description |
|---------|-------------|
| `block_path` | Place a wall between two points to block direct movement |
| `corridor` | Create a corridor (two parallel walls) to funnel units |
| `retreat_wave` | Create a retreat wavefield that pushes a unit group backward |
| `set_param` | Adjust a mod parameter (dispersion, block strength, retreat/rout thresholds, aggression) |

The coder receives real world coordinates from the battle state:
```json
{
  "total_units": 15000,
  "current_params": {"dispersion": 0.5, "block_strength": 0.7, ...},
  "teams": [{"team": 0, "units": 8000, "centroid": [120, 0, 340]}, ...],
  "subgroups": [{"team": 0, "units": 3000, "morale": 0.3, "action": "Retreat", ...}],
  "fortifications": [{"team": 1, "center": [200, 0, 100], "radius": 80}]
}
```

#### Approval Workflow

By default, proposed commands are shown in the UI for **player approval** before execution:
- Agent state display (idle / capturing / vision analyzing / coder analyzing / awaiting approval)
- Vision assessment text
- Coder reasoning
- Numbered command list with human-readable descriptions
- **APPROVE** / **REJECT** buttons

An auto-apply mode is available (defaults off) for fully autonomous operation.

#### Controls

- **Numpad8** — trigger an immediate analysis cycle
- Configurable: vision model, coder model, interval (5–60s), auto-apply toggle

Both LLM calls run on **background threads** — the game never stutters during analysis.

### 10. Cinematic Camera Improvement

Replaces the vanilla auto-cinematic camera's random target selection with **combat-weighted selection**:

- Scores every army by proximity to enemies (60%), kill count (30%), and remaining units (10%)
- Weighted-random pick blends combat score with uniform coverage based on bias strength
- `BiasStrength = 0` → fully random (vanilla), `1.0` → always the hottest clash
- Configurable focus interval (time between camera cuts)

### 11. Paint Mode (Numpad9)

A separate battlefield paint mode for real-time flow field manipulation that coexists with the RTS camera:

- **Left-click** — place soft obstacle blockers at the mouse position
- **Right-click** — remove blockers
- **Corridor tool** — two-point wall placement to create unit channels
- **Brush size/strength** — adjustable controls
- Does not capture the mouse — you can paint and control the camera simultaneously

## UI Window (Numpad+)

An in-game IMGUI window with live tuning for all features:

- **General** — mod enable, dynamic scaling, aggressiveness slider
- **Pathing** — smart scheduling, strategic targeting, re-assign interval
- **NavGrid** — adaptive tuning, max active targets, obstacle update speed
- **Cinematic Camera** — combat bias toggle, bias strength, focus interval
- **Sub-Group Fracture** — enable, cluster radius, reassess interval, individual behavior toggles, morale thresholds, aggression boost, dispersion controls
- **Flow Field Modulation** — enable, block strength slider
- **AI Battle Agent** — enable, interval, vision/coder model selection, auto-apply toggle, live agent state, vision assessment, coder reasoning, proposed commands with approve/reject
- **Live stats** — team priorities, active targets, sub-group states with color-coded morale, engagement flags, terrain info, fortification data

The window captures the mouse cursor and suppresses camera mouse-look while open, so you can interact with sliders and toggles freely. Press **Numpad+** or **Escape** to close and return control to the game.

## Hotkeys

| Key | Action |
|-----|--------|
| **Numpad+** | Toggle settings window |
| **Numpad8** | Trigger AI battle analysis (manual) |
| **Numpad9** | Toggle paint mode |
| **Escape** | Close settings window |

## Configuration

All settings are also available as BepInEx config entries (`BepInEx/config/UEBS2PathingMod.cfg`) and can be changed live via the UI window or edited before launch.

## Performance

- All CPU-side logic uses cheap operations (distance checks, terrain height sampling, a handful of raycasts per sub-group per cycle)
- Assessment intervals scale dynamically for huge battles (50k+ units → 6s, 150k+ units → 10s)
- Terrain cover/high-ground search is skipped for 100k+ unit battles
- GPU flow-field targets are injected via the game's existing `NavGrid.AddTarget` API — no GPU buffer manipulation
- Extra `FullSearch` dispatches for mod targets are scaled by aggressiveness to avoid GPU overload
- Flow field modulation uses `Graphics.Blit` with additive blending — one render pass per frame, restored immediately after
- AI battle agent runs all LLM calls on background threads — zero game-thread impact

## Technical Details

- **Engine**: Unity 2018.4.26f1
- **Mod loader**: BepInEx 5.4.21 Mono
- **Patching**: HarmonyX via BepInEx
- **Pathfinding**: GPU-based flow fields via `NavGpu` compute shader — the mod injects targets and tunes parameters but does not modify the compute shader itself
- **Flow field modulation**: Soft obstacles rendered into `ObstacleGrid` (R8 RenderTexture) via `GL.Begin(GL.QUADS)` + additive `Graphics.Blit`, backed up and restored each frame
- **AI agent**: HTTP requests to Ollama REST API (`localhost:11434`), dependency-free JSON parsing (no Newtonsoft.Json required)
- **Safe patches**: All patches use Prefix/Postfix hooks on existing Update loops. Every patch is wrapped in try/catch to prevent mod errors from crashing the game's nav loop.

## Building from Source

```
dotnet build -c Release
```

Output: `bin/Release/UEBS2PathingMod.dll`

The `.csproj` expects BepInEx and game assemblies in the UEBS2 managed folder. Adjust `GameDir` in the `.csproj` if your install path differs.

## Source Files

| File | Purpose |
|------|---------|
| `PathingModPlugin.cs` | BepInEx plugin entry, config bindings, Harmony patches, strategic target assignment, smart scheduling |
| `SubGroupFracture.cs` | Sub-group clustering, tactical assessment, morale, autonomous behaviors, dispersion/braiding, retreat wavefield integration |
| `FlowFieldModulator.cs` | Soft obstacle injection into GPU ObstacleGrid: blockers, corridors, retreat wavefields, fortification blocking, braided mixing |
| `FortificationAnalysis.cs` | Fortification detection, morale bonuses/penalties, flank point calculation |
| `TerrainAnalysis.cs` | Height advantage, cover detection, high ground/cover search |
| `CinematicPatches.cs` | Combat-weighted cinematic camera target selection |
| `BattleAgent.cs` | Two-stage AI pipeline: Qwen VL vision + Qwen Coder command proposal with approval workflow |
| `OllamaClient.cs` | Dependency-free HTTP client for Ollama REST API |
| `BattleAnalyzer.cs` | Legacy single-stage analyzer (superseded by BattleAgent) |
| `PathingModUI.cs` | In-game tuning window with all settings + AI agent command approval |
| `PaintModeUI.cs` | Battlefield paint mode for real-time flow field manipulation |
