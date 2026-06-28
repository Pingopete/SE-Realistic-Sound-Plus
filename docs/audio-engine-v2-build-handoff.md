# RSP Audio Engine — Build Handoff (acoustic-path / occlusion / reverb work)

> Self-contained handoff for continuing this work in a new chat thread. Branch `v2/live-audio-engine`.
> Generated 2026-06-24. All work below is **built (0 warnings) and deployed** to the local Pulsar folder
> (`C:\Users\Pete\Desktop\pulsar\Legacy\Local\RealisticSoundPlus\`). Not committed yet.

## What this effort is

A multi-stage build adding **acoustic realism** to RSP's block/environment occlusion and reverb, by leaning on
Space Engineers' own structural data (pressurisation rooms + per-cell airtightness) instead of pure raycasting,
plus perf/quality fixes to the live DSP filter. The thruster (engine air/hull) pipeline is **deliberately left
untouched and separate** throughout — these systems only affect the block/aux/env occlusion + reverb paths.

The deeper context lives in two companion docs (read these for the engine internals):
- `docs/audio-engine-v2-architecture.md` — whole-system architecture/interaction map.
- `docs/audio-engine-v2-handles-and-dsp.md` — engine audio-handle catalog, inline DSP filter mechanics, options.

**Hard engine facts that gate everything (do not violate):** SE runs **XAudio2 2.7 / SharpDX 4.0.1**; no one-pole
filters, no reverb SideDelay/DisableLateField; `MyXAudio2.SetReverbParameters` is a no-op; all XAudio2/SharpDX
voice mutation must be on the main `Update()` thread; **X3DAudio is geometry-blind** (not an occlusion sensor —
keep the raycast). Per-voice filter freq normalisation = `2·sin(π·fc/fs)`, max usable cutoff = `fs/6`.

---

## DONE this session (Stages 1–3, all built + deployed)

### Stage 1 — perf/filter
- **Filter fast-path** — `RspDynamicAudioFilters.TryApplyLiveFilterParameters` now does a direct typed
  `((SharpDX.XAudio2.Voice)voice).SetFilterParameters(params, 0)` instead of `MethodInfo.Invoke` (no per-frame
  boxing/`object[]`). Reflection kept as a defensive fallback.
- **Root-level de-zipper** — per-voice cutoff/Q is now smoothed toward target in **log-frequency** at the single
  filter choke point (`SmoothLiveFilter`), so the discrete jumps that caused the long-standing **pops/clicks**
  glide over ~3–4 frames. Covers both engine and aux writes; snaps cleanly on pooled-voice reuse.
  - Setting: `LiveFilterSmoothingMs` (default **45**; 0 = off).
  - NOTE: the fuller "unify / retire the effect-bank double-write" cleanup was deliberately deferred (the
    de-zipper sits at the final write so the pop is fixed regardless).

### Stage 2 — shared structure-reader primitive
`AudioEngineV2/V2GridStructureProbe.cs` (new) — the common reader all the tandem systems use:
- `IsCellAirtight(grid, cell)` — engine per-cell seal test, works on any grid (survives an open door).
- `TryGetRoomGeometry(grid, cell, out V2RoomGeometry)` — exact room size/shape from the cell-set
  (`IMyOxygenRoom.Blocks`): center, volume, equivalent radius, near/far wall extents. Cached by room identity.
- `TryFindAirPath(grid, sourceWorld, listenerWorld, out pathLengthMeters)` — **bounded 6-connected flood-fill**
  through traversable cells (empty + open doors), returns the shortest open-air detour length. Never consults
  the airtight flag (works door-open / unsealed). Budget-capped (`MaxAirPathCells=4096`, `AirPathBoundsPad=3`).
  Local per-call buffers (re-entrancy-safe — fixed per adversarial review).

### Stage 3 — the four tandem systems
1. **Cascade open-air diffraction leg** (`V2AuxSourceOcclusionTelemetry.cs`, `ProbePath`/`Calculate`): when a
   block's straight ray is blocked but `TryFindAirPath` finds a detour, the source also arrives **bright** via
   that route. Energy-blend of the two legs (muffled through-wall vs bright-but-longer air path), weighted by
   loudness. **Only ever brightens** (`Math.Min` on the muffle) → unobstructed/sealed sources unchanged; the
   binary single-ray flip dissolves. Flood-fill rides the existing 250 ms probe cache (gated on `mainRayBlocked`).
   - Tuning knobs (code constants for now): air-leg brightness floor `0.08f` in `Calculate`; `AirPathBoundsPad=3`
     in `V2GridStructureProbe` (raise/lower to allow more/less bending around corners).
2. **Sealed-room reverb geometry** (`V2PlayerEnvironmentTelemetry.CalculateRoomAcoustics`): in an airtight room,
   reverb size/wall-distances blend in the exact cell-set geometry (jitter-free) instead of pure ray-sampling.
   Clamped to an absolute 250 m ceiling (not rayLength), `near≤median` enforced (review fixes). Debug source
   string becomes `"sealed-geo"`.
   - Setting: `ReverbSealedGeometryWeight` (default **0.75**; 0 = pure ray / old behaviour, 1 = fully trust geometry).
3. **Thin-seal barrier loss** (per-source `ProbeSinglePath` + env `TraceRollingDirection`/`ProbeDirection`):
   a thin **sealed** face (glass canopy, thin metal plate) imposes a thickness-independent transmission floor
   (`Min`-ceiling) via `V2PlayerEnvironmentTelemetry.TryGetFirstGridHitFace` + `IsCellAirtight`. Thick walls,
   open gratings, and voxel terrain are untouched. **DEFAULT-OFF** (zero cost until enabled).
   - Settings: `PlayerFilterBlockSealedBarrierLoss` (default **0**, tune ~0.7), `PlayerEnvSealedBarrierLoss`
     (default **0**, tune ~0.7), `PlayerFilterSealedBarrierThinFactor` (default **0.6**).
4. **Persistent env occlusion map** (`V2PlayerEnvironmentTelemetry`, replaces the rolling probe): the
   time-windowed random-ray buffer is replaced by a **persistent Fibonacci-lattice directional cell map**
   (`ProbeEnvMapDirections`/`AggregateEnvMap`/`MergeEnvCell`/`PrepareEnvMapAccumulator`/`EnsureEnvMap`/`EnvMapCell`).
   Each cell remembers its last openness/thickness; a deterministic golden-stride sweep refreshes the cells (same
   ray budget). Fixes the **stationary sway** near openings. Review HIGH fixes applied: **binary `Confidence`
   inclusion gate** (weight=1, ratio stays stable while moving) + **min-coverage guard** (falls back to the
   coarse estimate until ≥ max(8, N/4) cells sampled, so resets don't cause a ~3 s muffling sweep); roomProbe is
   fed per-swept-ray (raw distances).
   - Settings: `PlayerEnvMapCellCount` (**96**, [32,192]), `PlayerEnvMapCellAlpha` (**0.5**), `PlayerEnvMapConfidenceDecayMeters` (**1.5**), `PlayerEnvMapResetMoveMeters` (**4.0**), `PlayerEnvMapRaysPerUpdate` (**16**, same budget as before).

**Emitter repositioning was verified feasible** (Harmony postfix on `MyEntity3DSoundEmitter.SourcePosition`
getter, gated to tracked emitters; `m_position` override persists, engine never stomps it) but is **NOT yet
implemented** — the current cascade only changes filter+volume (no directional move), which is the snap-free part.

---

## How to test what's in (IMPORTANT)

- **Most features are ON by default** except the thin-seal (loss=0). The de-zipper, cascade air-leg, reverb
  geometry, and persistent env map are all active in the deployed DLL.
- **The new settings have NO menu handles or chat commands yet** (that's Stage 5). To tune them now, edit the XML
  at `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and let it hot-reload (~5 s), or restart.
  - To **try the thin-seal**: set `PlayerFilterBlockSealedBarrierLoss` and `PlayerEnvSealedBarrierLoss` to ~0.7 in the XML.
- **What to listen for:**
  - De-zipper: the occasional pops/bangs on movement/pressure changes should be gone.
  - Cascade: a loud block around a corner / up a stairwell (the jukebox case) should stay audible/brighter
    instead of snapping to muffled; the muffle should no longer flicker as you cross a wall edge.
  - Reverb geometry: reverb size should be steady in sealed rooms (no jitter); debug source = `sealed-geo`.
  - Env map: standing still near a doorway in an otherwise-sealed base, the env muffle should hold steady (no sway).
- Existing debug overlay `/rsp auxpathdebug on` still shows the block-occlusion rays (the color-coded thickness
  overlay built earlier this branch). **No new debug visuals yet** (Stage 4).

---

## REMAINING work (in order)

1. **Stage 4 — debug visuals** (highest value for testing): a new env-map overlay (persistent sphere-cell
   openness/sealing) and an update to the block-occlusion overlay to show the flood-fill air path, sealing
   surfaces, and (if repositioning is added) the path/portal. Plumb the thin-seal/air-path debug flags.
2. **Stage 5 — menu pass**: add `RspSettingsMenu.cs` handles for the ~10 new settings, prune dead handles,
   reorganise for the tandem systems, and fix the layout (title↔handle vertical/horizontal alignment, the
   over-inset section headers, add section borders + colour-coding). Also add the settings clamps + console
   aliases (`sealbarrierblock`/`sealbarrierenv`/`sealbarrierthin`, env-map handles).
3. **Cleanup** — delete the now-DEAD rolling-probe code in `V2PlayerEnvironmentTelemetry.cs`:
   `ProbeRollingDirections`, `AggregateRollingProbeSamples`, `PrepareRollingProbeAccumulator`,
   `ResetRollingProbeAccumulator`, `StoreRollingProbeSample`, `PurgeRollingProbeSamples`,
   `CalculateRollingProbeAgeWeight`, `ResolveRollingProbeWindowSeconds`, `BuildSeededProbeDirection`,
   `BuildRollingProbeSeed`, `HashUInt`/`UnitFromHash` (V2ManagedDspReverbRuntime has its own HashUInt — safe),
   the `RollingProbeSamples` field, `_rollingProbe*` state, and the `RollingProbe*` constants. They're dead
   (call site + Reset repointed to the env map) but kept to de-risk the core-signal change — delete + rebuild.
4. **Debug logging** (explicitly requested, do LAST): add `V2DebugLog` entries across all the new functionality
   — air-path found/length + merge decisions, sealed-room reverb (source/radius/blend), thin-seal barrier hits
   (a counter is the cheapest), persistent env-map state (coverage/openFraction/cell count), de-zipper activity —
   so testing has a full diagnostic trail. Also consider surfacing the new `V2AuxSourceOcclusionSample.AirPath*`
   fields and a thin-seal count in the readouts.

## Method / approach notes

- Big or risky integrations were done via a **design+verify workflow** (parallel readers map exact integration
  points → spec → adversarial review) BEFORE editing. The reviews caught real issues each time (a flood-buffer
  re-entrancy blocker, a reverb clamp/ordering bug, the env-map confidence-drift + reset-sweep, roomProbe skew).
  Recommend the same for the env-map cleanup if unsure, and for repositioning when implemented.
- Engine decompilation: `ilspycmd` is installed; SharpDX ships XML docs next to its DLLs in the SE `Bin64`.
- Working notes / running status also live in the auto-memory file `next-build-pass-queue.md`.

## File map (what changed)

- `AudioEngineV2/V2GridStructureProbe.cs` — NEW (structure reader + flood-fill).
- `AudioEngineV2/RspDynamicAudioFilters.cs` — filter fast-path + de-zipper.
- `AudioEngineV2/V2AuxSourceOcclusionTelemetry.cs` — air-path leg + thin-seal (per-source) + sample fields.
- `AudioEngineV2/V2AuxSourceOcclusionSample.cs` — `AirPath*` fields.
- `AudioEngineV2/V2PlayerEnvironmentTelemetry.cs` — reverb geometry override, thin-seal (env), persistent env map,
  `TryGetFirstGridHitFace`; old rolling-probe code now dead.
- `RealisticSoundPlusSettings.cs` — new settings (Stage 1/3 listed above).
- `AudioEngineV2/AudioEngineV2Runtime.cs` — `V2GridStructureProbe.Reset()` wired into session reset.
