<!-- Generated 2026-06-24 by the rsp-deep-context workflow (run wf_636fb3e7-79d): 24 agents over decompiled VRage.Audio/SharpDX handles + online API research. Machine-generated context; curate as needed. -->

# Realistic Sound Plus — Architecture & Interaction Reference

> A Pulsar-loaded Space Engineers plugin that replaces vanilla ship/engine audio with a physically-modeled "V2 audio engine": directional thruster audio, progressive environmental muffling, and ray-driven reverb. This document synthesizes per-subsystem analyses into one architecture/interaction map. Concrete `file:line` references are preserved from the source analyses; where inputs were thin or conflicting it is called out explicitly.

---

## 1. System overview

### 1.1 The three areas

RSP V2 is organized around three physical-acoustic models that share one listener-state and environment-sensing layer:

1. **Area 1 — Thruster realism / engine two-path filter.** Every `MyThrust` on a grid is collapsed into ≤6 directional "voice slots" (+1 remote-collapsed slot), each driving three smoothed emitters (detail-idle, detail-active, state). A per-emitter **dual-path low-pass filter** models an *airborne* path (atmosphere-gated, occlusion-attenuated) blended with a *structure-borne hull* path (grid-coupling-gated), producing a final cutoff/Q/distance-gain pushed into XAudio2.
   - Core: `V2GridAudioState.cs`, `V2ThrusterAudioPatch.cs`, `V2CueCatalog.cs`, `V2EngineFilterModel.cs`, `ThrusterFilterPatch.cs`.
2. **Area 2 — Progressive environmental muffling.** A rolling 26-ray-equivalent probe around the listener fuses grid-physics hits + voxel terrain + the oxygen-room system into an occlusion/seal/wind estimate. A per-voice "aux" runtime walks every playing XAudio2 voice and applies low-pass + volume DSP classified as env/block/local, with per-block single-ray occlusion and range rewriting.
   - Core: `V2PlayerEnvironmentTelemetry.cs`, `V2PlayerFilterRuntime.cs`, `V2AuxSourceOcclusionTelemetry.cs`, `V2BlockRangeScaler.cs`, `EnvironmentAmbiencePatch.cs`.
3. **Area 3 — Ray-driven reverb.** Room geometry from the environment probe feeds an auto-reverb parameter set (`V2LiveReverbParameters`) into a custom FDN reverb. The shipped default attaches a custom XAPO (`V2LiveReverbPocProcessor`) in-place on the **master** voice, because the engine's stock `SetReverbParameters` is a verified no-op.
   - Core: `V2GlobalReverbRuntime.cs`, `V2ManagedDspReverbRuntime.cs`, `V2LiveReverbPocProcessor.cs`, `V2ReverbDiagnosticPing.cs`.

### 1.2 Plugin lifecycle

`Plugin` (`Plugin.cs`) is the single `IPlugin`/`IDisposable` Pulsar loads.

- **`Init`** (`Plugin.cs:20`): `SettingsManager.LoadOrCreate()` → `AudioPatchRuntime.ResetForSession("plugin init")` → `new Harmony(HarmonyId).PatchAll(...)`. **Reset precedes patching** so all static state is clean before any patched callback can fire.
- **`Dispose`** (`Plugin.cs:63`, guarded by `_disposed`): close menu → unregister chat commands → `UnpatchAll` → `ResetForSession("plugin dispose")`. **Unpatch precedes reset** so no patched callback runs mid-teardown. Whole body is try/caught (`:78-81`).
- **Session change** (`ResetAudioRuntimeIfSessionChanged`, `:53`): `MyAPIGateway.Session` is compared by **reference identity** (`ReferenceEquals`, `:56`); a new/`null` session triggers `AudioPatchRuntime.ResetForSession`.

`AudioPatchRuntime.ResetForSession(reason)` (`Patches/AudioPatchRuntime.cs:8`) is the **single reset funnel**, fanning out to ~14 subsystems in fixed order (`:10-22`), with the heavyweight `AudioEngineV2Runtime.ResetForSession` mid-list so patch re-enable happens after the orchestrator has cleared its dictionaries.

### 1.3 Per-frame `Update` pipeline order

`Plugin.Update()` (`Plugin.cs:33`) — **no top-level try/catch**; resilience comes from each subsystem's own self-disable latch:

1. `ResetAudioRuntimeIfSessionChanged()`
2. `SettingsCommands.TryRegister()` (idempotent late-bind)
3. **`AudioEngineV2Runtime.Update()`** — the V2 orchestrator tick
4. `V2ConnectorImpactAudio.Update()`, `AudioVoiceCatalog.Update()`, `V2DebugLog.Update()`
5. Overlay draws: `V2AuxOcclusionDebugOverlay.Draw()`, `V2PlayerEnvironmentTelemetry.DrawReverbRayDebug()`, `AudioDebugOverlay.Draw()`, `FilterDebugOverlay.Draw()`
6. Every 300 frames (~5 s @ 60 FPS) → `SettingsManager.ReloadIfChanged()`

Inside **`AudioEngineV2Runtime.Update()`** (`AudioEngineV2Runtime.cs:115-161`), strictly ordered:

```
RspDynamicAudioFilters.UpdateFromSettings(settings)         # push DSP filter params
reverb route arbitration  → UpdateGlobalBusRoute | SuppressLegacyReverbRoute
V2ManagedDspReverbRuntime.Update() ; V2ReverbDiagnosticPing.Update()
TrackEmitterBindingSignature()                              # live re-bind generation
_listener = V2AudioListenerState.Capture()                 # authoritative listener snapshot
V2PlayerEnvironmentTelemetry.Update(_listener)             # env probe consumes fresh listener
V2PlayerFilterRuntime.Update()                             # aux per-voice DSP
V2AuxSourceOcclusionTelemetry.Update()                     # range-scaler tick
LogListenerTransitionIfChanged(_listener)
if _listener.VanillaFallback:  silence V2 emitters + zero census counters
else:                          EnsureListenerGridThrustersKnown + RefreshKnownThrustersIfDue
CleanupEmptyGridStates()
V2AudioDebugState.Update(...)                              # snapshot for overlays
```

The listener is always captured **before** the environment probe and filter runtimes consume it in the same tick. `VanillaShipEnvironment` is fed *asynchronously* by the game's `MyShipSoundComponent.UpdateVolumes` callback (≤750 ms stale), so `Capture` reads the most-recent vanilla snapshot rather than a same-tick one.

---

## 2. Component-by-component map

### 2.1 Orchestration & lifecycle

**`AudioEngineV2Runtime`** (`AudioEngineV2/AudioEngineV2Runtime.cs`) — central static hub. Owns `GridStates` (gridId→`V2GridAudioState`), `KnownThrusters`, the V2-emitter registry (`V2Emitters`/`UnfilteredV2Emitters`/`V2EmitterFilterRoutes`), `MutedVanillaCues`, and diagnostic counters (saturating via `SaturatingIncrement`, `:970`). Public surface: `Listener` (`:55`), `EmitterBindingGeneration` (`:57`). Key methods: `ReportThruster`/`ProcessThruster` (acceptance gate, `:204-266`), census/force-discovery (`RefreshKnownThrusters`, `EnsureListenerGridThrustersKnown`, `:659-767`), vanilla-cue suppression predicates (`ShouldSuppressVanillaShipCue`/`MuteVanillaShipCueIfNeeded`, `:360-417`), emitter registry (`RegisterEmitter`/`UnregisterEmitter`, route→effect resolution, `:419-522`), `TrackEmitterBindingSignature` (`:857`), grid-state lifecycle (`:635-940`). Tuning constants `:14-17` (`CensusInterval=50ms`, `EmptySourceDiscoveryInterval=750ms`, `MinimumKnownThrustersBeforeGridCensus=6`).

**`AudioPatchRuntime`** (`Patches/AudioPatchRuntime.cs`) — the single `ResetForSession(reason)` fan-out. Engine touch-points: `IPlugin`/`IDisposable`, `MyAPIGateway.Session`, `HarmonyLib.Harmony`.

### 2.2 Listener & environment sensing

**`V2AudioListenerState`** (`V2AudioListenerState.cs`) — a `struct` snapshot of *who/what the listener is acoustically attached to*. `Capture()` (`:39`) runs three probes (`TryGetControlledShip` `:263`, `TryGetCharacterGridContact` `:160`, `IsFirstPersonCamera` `:251`), merges `VanillaShipEnvironment.TryGetLatest` room data, and resolves `VanillaFallback` = `!routeActive` (the master "stand down, let vanilla play" switch). `Stabilize` (`:94`) holds the last reliable state up to `StableListenerHold=800ms` within `StableListenerHoldRange=10m` to bridge single-frame glitches (mode suffixed `-hold`). `ModeName` is **load-bearing** (compared `Ordinal` downstream and string-matched in the hull gate). Heavily reflection-based; fails soft to vanilla.

**`V2PlayerEnvironmentSample`** (`V2PlayerEnvironmentSample.cs`) — ~70-field POD: raycast openness, sealing/muffling, oxygen-room probe, and `ReverbRoom*`/`ReverbAuto*` geometry+DSP params. `Valid`+`UpdatedUtc` gate a 2 s validity window.

**`V2PlayerEnvironmentTelemetry`** (`V2PlayerEnvironmentTelemetry.cs`) — the rolling-ray openness/voxel/oxygen/reverb probe. Cadence `ActiveUpdateInterval=500ms` / `StableUpdateInterval=1s`, with a 100 ms pressure-only refresh. Casts `RollingProbeRaysPerUpdate=16` deterministic seeded directions into a time-decayed ring (`RollingProbeMaxSamples=160`, 1–5 s window) — spreading raycast cost and giving temporal smoothing for free. Per-ray: physics multi-hit thickness (`EstimateBlockedLength`) + sky-ward voxel occlusion, converted to openness via exponential transmission `exp(-blocked/scale)`. Produces occlusion/aperture/seal/wind + auto-reverb (Sabine-sphere `RT60=0.0537·radius/absorption`). Also exposes `TryCompareOxygenRooms`, reusable ray primitives, and `DrawReverbRayDebug`.

### 2.3 Area 1 — thruster model & engine filter

**`V2ThrusterAudioPatch`** (`V2ThrusterAudioPatch.cs`) — Harmony Postfix on `MyThrust.UpdateAfterSimulation` (`:18`); forwards to `ReportThruster`. Self-disable latch on first throw.

**`V2GridAudioState`** (`V2GridAudioState.cs`, the bulk) — six-axis collapse + emitter driver. `DirectionState[6]` (by `V2ThrustDirectionGroup`) + one `_remoteCollapsed` slot. `ReportThruster` (`:48`) buckets by `DirectionFromVector` (note deliberate thrust-vs-nozzle inversion `:408,411`), selects cues via `V2CueCatalog`, computes log-scaled `presence` and the `CalculateCommandLoad` detail-intensity priority chain (`off→ovr→move→force→idle`, `:248-286`). `BuildSnapshot` (`:629`) does weighted-centroid collapse with cue fallback chains. `DirectionState.Update` (`:510`) derives the three emitter targets (detail-idle/active/state) with linear command smoothing, idle/active crossfade, throttle→pitch, and 2D/3D variant selection (`Internal` vs `External` filter route). `LayerEmitter` (`:1079`) wraps one `MyEntity3DSoundEmitter`, rebinding on cue/route/**binding-generation** change with a smoothstep fade-in to suppress restart pops. Throttled to `DirectionUpdateInterval=50ms`.

**`SixDirectionSourceModel`** (`SixDirectionSourceModel.cs`) — the `V2ThrustDirectionGroup` enum (load-bearing) and static `EvaluateDistanceGain` (`(1-norm)^exp`, used widely). The `SixDirectionSourceModel` *class itself is dead code* — never instantiated; the refactor plan describes it as the intended shared-source unification (`docs/audio-engine-v2-refactor-plan.md:559`) but the engine reimplements its centroid inline.

**`V2CueCatalog`** (`V2CueCatalog.cs`) — stateless thruster→cue mapping. `SelectDetailActiveCue/IdleCue`, `SelectStateLoopCue`, and `HasDetailLocalVariant` (hardcoded D2 whitelist, the lynchpin for inside-ship 2D-positional routing). Mass-first size classifier `SelectShipSoundGroup` (`LargeShipMinWeight=500000`), `VanillaFullSpeed=96` for speed buckets.

**`V2AudioDefinitionCatalog`** (`V2AudioDefinitionCatalog.cs`) — lazy cache parsing Keen `Audio*.sbc` for per-cue `MaxDistance`/`Volume`/`Category`; skips `MaxDistance<=0`. Consumed by `V2BlockRangeScaler`.

**`V2ToolLoopWaveCatalog`** (`V2ToolLoopWaveCatalog.cs`) — cue→`.wav` resolver for the reverb path; prefers D3 over D2 with D2→D3 filename rewriting (`Convert2dNameTo3d`), `.xwm`→`.wav` rewrite, Arc↔Real prefix pairing.

**`EngineAudioClassifier`** (`Patches/EngineAudioClassifier.cs`) — authoritative engine/ship-motion cue string-matcher. `IsKnownEngineCue` (handles the `Thuster` typo, excludes hydrogen-engine *blocks*), `IsKnownVanillaShipStateCue` (explicit allow-list driving suppression), `IsKnownShipMotionCue`.

**`V2EngineFilterModel`** (`V2EngineFilterModel.cs`) — the two-path filter physics (stateless). `TryCalculate` (`:16`), `TryCalculateHullOnly` (`:94`), `CalculateDistanceGain` (`:149`), `CalculateHullDistanceGain` (`:176`). Air weight = `pressure·transmission`; hull weight = `contact?1:0` via `IsHullPathViable`/`AreGridsAudioCoupled` (reflected `IsSameConstructAs`/`IsInSameLogicalGroupAs`/`IsInSamePhysicalGroupAs`). Cutoffs blended in energy domain (`BlendCutoffs`, RMS of f²·w). Vacuum collapses air to `EngineFilterVacuumContactFrequency`. `V2EngineFilterSample` (23-field carrier) → `V2EngineFilterTelemetry` (16-entry ring, display-only).

**`ThrusterFilterPatch`** (`Patches/ThrusterFilterPatch.cs`) — Postfix on `MyEntity3DSoundEmitter.SelectEffect`; gates *which* emitters get a filter effect subtype by overwriting `ref __result`. Does not compute cutoff/Q.

### 2.4 Area 2 — aux filter, occlusion, range

**`V2PlayerFilterRuntime`** (`V2PlayerFilterRuntime.cs`) — per-frame driver (50 ms throttle). `Update()` (58) pulls `MyAudio.Static.GetCurrentlyPlayedSounds()`, classifies each voice (env/block/local) via `V2AuxCueClassifier`, computes muffle/cutoff/gain, and applies via `RspDynamicAudioFilters.TryApplyLiveFilterParameters` + `VolumeMultiplier`. Per-voice caches keyed by `IMySourceVoice` reference: filter/volume signatures (idempotency), log-frequency smoothing states, base-multiplier restore, and the raw-volume reflection "env-carrier" hack. Gain limits: 6× block, 1× env/local.

**`V2PlayerFilterSample`** (`V2PlayerFilterSample.cs`) — debug/telemetry record (2 s cache); DSP is applied to the live voice, not from the struct.

**`V2AuxCueClassifier`** (`V2AuxCueClassifier.cs`) — pure-string cue taxonomy (env/block/local/non-world/engine + reverb-routing predicates). Ordered carve-outs (non-world/engine/local first).

**`V2AuxSourceOcclusionTelemetry`** (`V2AuxSourceOcclusionTelemetry.cs`) — the **single-ray + temporal-smoothing** block occlusion model. `RecordVoice` (65), three `TryCalculate` overloads, `Calculate` (298). Casts exactly one source→listener ray (trimmed by source-clearance + listener skips), thickness→`exp(-blocked/scale)` transmission, near-field limiter, sealed bonus, occlusion-strength curve, then **EMA smoothing keyed by block entity id** as the temporal substitute for spatial multi-ray averaging. Path-probe cache (250 ms re-probe, 8 s lifetime). `V2AuxSourceOcclusionSample` (38-field struct, `ProbeFrom/ProbeTo` for the overlay).

**`V2BlockRangeScaler`** (`V2BlockRangeScaler.cs`) — rewrites `emitter.CustomMaxDistance`. `TryPrimeEmitter`/`TryPrimeDistanceGate` (the by-ref XAudio2 gate is the preferred, churn-free hook), `ResolveVanillaMaxDistance` (Arc/Real-paired max). **Note:** `ResolveEffectiveRange` currently ignores vanilla range and returns a flat `PlayerFilterBlockMaxRange` — a clear future-feature hook.

**`V2BlockSoundSourceResolver`** (`V2BlockSoundSourceResolver.cs`) — vestigial counter shim (`scan=disabled cache=disabled`); source resolution is now direct via `emitter.Entity`.

**`V2ThicknessRaySegment`** (`V2ThicknessRaySegment.cs`) — overlay-only data types (fraction-based intervals, re-projectable without re-casting).

**`BlockRangeScalePatch`** (`Patches/BlockRangeScalePatch.cs`) — four patch classes: PlaySoundWithDistance Prefix, SetSound Postfix (records emitter↔voice binding), `MyXAudio2.SourceIsCloseEnoughToPlaySound` Prefix (the active range hook), and a disabled `IsCloseEnough` fallback.

### 2.5 Environment bed & vanilla integration/suppression

**`EnvironmentAmbiencePatch`** (`Patches/EnvironmentAmbiencePatch.cs`) — keeps the vanilla planet-ambient + weather carrier voices alive at audible volume so RSP's filter has a signal to shape. Three Harmony patches (planet hard-off bypass, planet carrier postfix, weather carrier postfix); `ShouldOwnEnvironmentAmbience` gates ownership; one-strike reflection self-disable.

**`V2ShipEnvironmentPatch`** (`V2ShipEnvironmentPatch.cs`) — Postfix observer on `MyShipSoundComponent.UpdateVolumes` reading private `m_insideShip`, fanning to `VanillaShipEnvironment`, `ExteriorSoundTransmission`, `AudioDiagnostics`.

**`VanillaShipEnvironment`** (`VanillaShipEnvironment.cs`) — 750 ms interior snapshot store with a reflective room-name probe (fields matching `room|oxygen|pressur|enclos`); best-effort labeling only, never load-bearing for DSP. Feeds `V2AudioListenerState.Capture`.

**`V2VanillaShipCueSuppressionPatch`** (`V2VanillaShipCueSuppressionPatch.cs`) — Prefixes on `PlaySound`/`PlaySoundWithDistance` (cancel) + Pre/Post on `Update`/`FastUpdate` (mute) for `MyEntity3DSoundEmitter`. Suppression is **conditional on `HasReplacementSourcesReady()`** so the player is never left in silence; `ResetRuntimeState` is a no-op.

**`ShipSeatAudioPatch`** (`Patches/ShipSeatAudioPatch.cs`) — forces vanilla ship audio out of 2D (centered) mode while seated so it flows through the spatial/filter path (`UpdateShouldPlay2D` Prefix pins false; `UpdateSoundDimension` Postfix forces `Force3D`).

**`CharacterBreathPatch`** (`Patches/CharacterBreathPatch.cs`) — silences the breath loop when the helmet is open (two OR'd helmet signals).

### 2.6 DSP & dynamic filters (the XAudio2 apply layer)

**`RspDynamicAudioFilters`** (`RspDynamicAudioFilters.cs`) — central DSP hub. Two layers: (1) **static effect-bank registration** (`UpdateFromSettings`, `RegisterOrReplace`, template-clone from vanilla `realShipFilter`/`LowPassCockpit`) registering `RSPEngineFilter`/`RSPAuxFilter`; (2) **live per-voice override** via reflected SharpDX `SourceVoice.SetFilterParameters` (`TryApplyLiveFilterParameters`, `:158`). Coefficient math: `ToXAudioFrequency = 2·sin(π·cutoff/sampleRate)` clamped to fs/6; `ToXAudioOneOverQ = 1/q` clamped to 1.5. `GetFilterParametersForEmitter` dispatches to `V2EngineFilterModel.TryCalculate(HullOnly)`. Owns the emitter↔voice binding cache (`RecordEmitterVoiceBinding`/`TryResolveEmitter` with `EmitterOwnsVoice` staleness rejection).

**`LiveCustomFilterPatch`** (`Patches/LiveCustomFilterPatch.cs`) — Harmony Pre/Post on `MyEffectInstance.UpdateFilter`. Prefix pre-loads dynamic coefficients into the `SoundEffect`; Postfix overwrites the live voice via `SetFilterParameters`. One-strike `_disabled` latch.

### 2.7 Area 3 — reverb runtimes

**`V2GlobalReverbRuntime`** (`V2GlobalReverbRuntime.cs`, ~3946 lines) — master submix/bus reverb orchestrator. Five routes (`managed`/`globalbus`/`custombus`/`custominline`/`custommaster`); **`custommaster` is the shipped default** — `EnsureCustomInlineRoute` attaches a `V2LiveReverbPocProcessor` in-place to the master voice. Three compile-time kill-switches (`CompatGameSubmixRoutingEnabled`, `SourceReverbVoiceRoutingEnabled`, `GlobalBusDirectSourceRoutingEnabled`, all `false`). The legacy `Update()` path and `SetSourceVoiceTarget`/`TryApplyLiveSourceReverbSend` are **dead/stubbed**. **Central finding:** `MyXAudio2.SetReverbParameters` is detected as a no-op via IL probe (`il.Length==1 && il[0]==0x2A`, single `ret`) — the reason for the entire custom-DSP pivot.

**`V2ManagedDspReverbRuntime`** (`V2ManagedDspReverbRuntime.cs`) — the `managed` route: a pure C# FDN reverb that decodes a cue's PCM, renders a wet tail offline (8 delay lines, Hadamard8 mixing, RT60 feedback), and replays it as a fresh `SourceVoice`. Also owns the shared parameter bridge `ResolveLiveParameters` → `V2LiveReverbParameters`. `TryPlayAutomaticWetSend` has **no callers** (dormant; only manual `/rsp` commands exercise it).

**`V2LiveReverbPocProcessor`** (`V2LiveReverbPocProcessor.cs`) — the real-time XAPO (`SharpDX.XAPO.AudioProcessor`) for the live default. Streaming FDN mirror of the managed math with audio-thread safety: 0.35 s startup ramp, three-tier sample clamping + **panic-clear** on NaN/divergence, `BufferFlags.Silent` culling, non-float bypass. Pressure/enclosure model: `wetScale = wetSend·(0.18+enclosure·0.82)·(0.12+√pressure·0.88)`.

**`V2LiveReverbParameters`** (`V2LiveReverbParameters.cs`) — DTO decoupling the ray/environment model from the DSP; the four spatial fields (`ApertureFraction`/`StructuralOcclusion`/`FinalMuffling`/`ClosedFraction`) are the ray-driven-reverb hook.

**`V2ReverbDiagnosticPing`** (`V2ReverbDiagnosticPing.cs`) — diagnostic harness that empirically validated the kill-switches: stock `Fx.Reverb` creation tests, `PlayXapo` hard-fail (`XAPO.Fx.Reverb does not support XAudio2.9`), shared-bus cue re-routing with tail-pump keep-alive. `IsOwnedWetVoice` lets other RSP code skip its wet voices.

### 2.8 Settings, menu, commands

**`RealisticSoundPlusSettings` / `SettingsManager`** (`RealisticSoundPlusSettings.cs`) — ~110-property XML POCO + static manager. `Current` is the polled singleton; `ReloadIfChanged` is mtime-gated (~5 s poll). `Clamp()` encodes invariants and resolves inherit-sentinels (`<0`/`≤0` means "inherit master knob"). Alias router = two hand-maintained parallel `switch` blocks (`TryReadFloat`/`TrySet`). Route normalization (`NormalizeGlobalReverbRoute`) and `GetFilterEffectSubtype`/`GetEngineFilterEffectSignature` are the engine hookups.

**`SettingsCommands`** (`SettingsCommands.cs`) — `/rsp` chat dispatcher; ~50-case verb switch with a generic `SetValue→TrySet` fall-through, so any alias is chat-tunable without a dedicated case. Reverb-diag commands force-enable reverb.

**`RspSettingsMenu`** (`RspSettingsMenu.cs`) — immediate-mode `MyGuiScreenBase`. Four binding types (slider/toggle/dropdown/readout). `PollControls` writes user edits into `Current`; `_syncing` guards the read-back loop. Readouts are live telemetry windows into the V2 runtime. Includes a biquad magnitude preview chart and auto-reverb modifier sliders.

### 2.9 Observability + connector impact

**Overlays** — `AudioDebugOverlay` (left voice table; also *feeds* occlusion telemetry via `RecordVoice`), `FilterDebugOverlay` (right controller dump), `V2AuxOcclusionDebugOverlay` (3D occlusion rays, re-probes its own intervals without mutating audio), `V2AudioDebugState` (single-snapshot V2 summary store). All use `MyRenderProxy.DebugDraw*`, self-disable on exception, share a `1920×1080` viewport fallback.

**`V2DebugLog`** — rotating 2 MB text log; one summary line/sec + `WriteEvent`. IO error permanently latches `_disabled` for the session.

**`AudioVoiceCatalog`** — session-long census of distinct `kind:cue` voices with classification (the discovery tool for expanding cue coverage); 500 ms poll guarantees a baseline feed to occlusion telemetry even with overlays off.

**`AudioDiagnostics`** (`Patches/AudioDiagnostics.cs`) — per-cue + global diagnostic cache (2 s cue lifetime), written by the audio path, read by the voice overlay.

**`V2ConnectorImpactAudio`** — the one bespoke *audio-emitting* feature here: plays a metallic "clunk" on a connector's not-Connected→Connected edge, registered on the **Hull** filter route so it gets engine-style muffling. Polls connectors every 100 ms; deterministic higher-`EntityId`-suppresses dedup; 6 s tracked tail with live position/volume.

---

## 3. Interaction / data-flow graph

### 3.1 Per-frame data flow (who feeds whom)

```
                         SettingsManager.Current  (polled singleton; written by menu/chat)
                                   │
                                   ▼
   ┌───────────────────────────────────────────────────────────────────────────┐
   │ AudioEngineV2Runtime.Update()                                               │
   │                                                                            │
   │  RspDynamicAudioFilters.UpdateFromSettings ─► (registers RSP effect bank)  │
   │                                                                            │
   │  reverb route arbitration ─► V2GlobalReverbRuntime.UpdateGlobalBusRoute    │
   │        │                         └─► V2LiveReverbPocProcessor (master XAPO) │
   │        │                                 ▲                                  │
   │        │     V2ManagedDspReverbRuntime.ResolveLiveParameters ──────────────┤
   │        │                                 │  (V2LiveReverbParameters)        │
   │        │                                 │                                  │
   │  ┌─────▼─────────────────┐               │  reads env sample (radius,       │
   │  │ V2AudioListenerState  │               │   aperture, occlusion, pressure) │
   │  │   .Capture()          │◄── VanillaShipEnvironment (≤750ms async snapshot)│
   │  └─────┬─────────────────┘                                                  │
   │        │ _listener                                                          │
   │        ├──────────────► V2PlayerEnvironmentTelemetry.Update(_listener)      │
   │        │                     │ produces V2PlayerEnvironmentSample           │
   │        │                     │  (rays, voxel, oxygen-room, auto-reverb)     │
   │        │                     ▼                                              │
   │        ├──────────────► V2PlayerFilterRuntime.Update()  ──► live voices     │
   │        │                     │ (env/block/local DSP via RspDynamicAudio…)   │
   │        │                     └─ drives V2AuxSourceOcclusionTelemetry (block) │
   │        │                                                                    │
   │        ├──────────────► V2AuxSourceOcclusionTelemetry.Update() (range tick) │
   │        │                     └─► V2BlockRangeScaler (CustomMaxDistance)      │
   │        │                                                                    │
   │        ├─ if VanillaFallback: SilenceAll/DirectionalEmitters                │
   │        └─ else: EnsureListenerGridThrustersKnown + RefreshKnownThrusters    │
   │                          │                                                  │
   │                          ▼                                                  │
   │             GridStates[gid] = V2GridAudioState                              │
   │                 ├ 6× DirectionState ─► detail-idle / detail-active / state  │
   │                 │      emitters (LayerEmitter → MyEntity3DSoundEmitter)     │
   │                 └ registers emitters into AudioEngineV2Runtime registry     │
   │                          │ (route: Internal/External/Hull)                  │
   │                          ▼                                                  │
   │   ThrusterFilterPatch (SelectEffect) ─► chooses RSP effect subtype          │
   │   LiveCustomFilterPatch (UpdateFilter) ─► V2EngineFilterModel.TryCalculate  │
   │        consumes: listener + ExteriorSoundTransmission.GetAtmosphericPressure│
   │                  + V2PlayerEnvironmentSample (aperture/muffling)            │
   │                  + grid-coupling reflection                                 │
   │        produces: FinalCutoff/FinalQ ─► SetFilterParameters (XAudio2 voice)  │
   └───────────────────────────────────────────────────────────────────────────┘
```

**Thruster ingestion** is event-driven and out-of-band relative to `Update`: `MyThrust.UpdateAfterSimulation` (≈60 Hz) → `V2ThrusterAudioPatch.AfterSimulation` → `ReportThruster` → `ProcessThruster` acceptance gate (own-grid-only, with remote-collapse escape hatch). The 50 ms census (`RefreshKnownThrusters`) supplements sparse patch reports for *liveness*; the live path provides *responsiveness*.

**Vanilla suppression** is two-stage and out-of-band: `PlaySound`/`PlaySoundWithDistance` prefixes cancel new vanilla ship cues, and `Update`/`FastUpdate` pre+post hooks re-mute re-armed loops — both gated on `HasReplacementSourcesReady()`.

### 3.2 Harmony patch surface

| Patched member | Kind | Patch class (file) | Drives |
|---|---|---|---|
| `MyThrust.UpdateAfterSimulation` | Postfix | `V2ThrusterAudioPatch` (`:18`) | `ReportThruster` → grid model |
| `MyEntity3DSoundEmitter.SelectEffect` | Postfix | `ThrusterFilterPatch` (`:10/:24`) | injects RSP filter subtype (`ref __result`) |
| `MyEffectInstance.UpdateFilter` | Pre+Post | `LiveCustomFilterPatch` (`:15`) | live `SetFilterParameters` dynamic cutoff/Q |
| `MyEntity3DSoundEmitter.PlaySound(MySoundPair, bool×6, bool?)` | Prefix | `V2VanillaShipCueSuppressionPatch` (`:11`) | cancel vanilla ship cue |
| `MyEntity3DSoundEmitter.PlaySoundWithDistance(MyCueId,…)` | Prefix | `V2VanillaShipCueSuppressionPatch` (`:18`) | cancel vanilla ship cue |
| `MyEntity3DSoundEmitter.Update()` | Pre+Post | `V2VanillaShipCueSuppressionPatch` (`:25`) | re-mute re-armed ship cue |
| `MyEntity3DSoundEmitter.FastUpdate(bool)` | Pre+Post | `V2VanillaShipCueSuppressionPatch` (`:33`) | re-mute re-armed ship cue |
| `MyEntity3DSoundEmitter.PlaySoundWithDistance(MyCueId, bool×6, bool?)` | Prefix | `BlockRangeScalePlaySoundPatch` (`:12`) | prime `CustomMaxDistance` |
| `MyEntity3DSoundEmitter.SetSound(IMySourceVoice,string)` | Postfix | `SoundEmitterVoiceBindingPatch` (`:21`) | record emitter↔voice binding |
| `VRage.Audio.MyXAudio2.SourceIsCloseEnoughToPlaySound` | Prefix (reflected) | `BlockRangeScaleSourceGatePatch` (`:29`) | rewrite `ref float? customMaxDistance` |
| `MyEntity3DSoundEmitter.IsCloseEnough` | Prefix (`Prepare=false`) | `BlockRangeScaleIsCloseEnoughPatch` (`:79`) | disabled fallback |
| `MyShipSoundComponent.UpdateVolumes` | Postfix | `V2ShipEnvironmentPatch` (`:9`) | reads `m_insideShip` → env/diag fan-out |
| `MySessionComponentPlanetAmbientSounds.SetAmbientOff` | Prefix | `PlanetAmbientHardOffPatch` | bypass hard-off (keep carrier) |
| `…PlanetAmbientSounds.UpdateAfterSimulation` | Postfix | `PlanetAmbientCarrierPatch` | re-assert carrier target |
| `MySectorWeatherComponent.ApplySound` | Postfix | `WeatherAmbientCarrierPatch` | keep weather carrier audible |
| `MyShipSoundComponent.UpdateShouldPlay2D` | Prefix | `ShipSeatAudioPatch` (`:20`) | force ship audio 3D when seated |
| `MyShipSoundComponent.UpdateSoundDimension` | Postfix | `ShipSeatAudioPatch` (`:46`) | force per-emitter `Force3D` |
| `MyCharacterBreath.Update` | Pre+Post | `CharacterBreathPatch` (`:20/:41`) | silence breath when helmet open |

`V2GlobalReverbRuntime`, `V2ManagedDspReverbRuntime`, `V2LiveReverbPocProcessor`, `V2ReverbDiagnosticPing`, and the three observability files use **no Harmony patches** — they are reflection/ModAPI consumers or XAPO implementations.

---

## 4. Cross-cutting inferred-logic themes

### 4.1 Stability holds & temporal smoothing (single-ray + temporal model)

The dominant design move is **trading spatial sampling for temporal smoothing**:

- **Listener hold** (`V2AudioListenerState.Stabilize`): an unreliable frame reuses the last reliable grid/inside/room for ≤800 ms within 10 m, forcing `VanillaFallback=false`. Prevents single-frame `ControlledObject` nulls (seat entry/exit) from popping all emitters to vanilla.
- **Rolling probe** (`V2PlayerEnvironmentTelemetry`): instead of casting a fixed 26-direction sphere per frame, 16 deterministic-seeded rays/frame accumulate into a 1–5 s time-decayed ring. A stationary listener converges to a stable openness estimate; cost spreads across frames; the seed is deterministic so it doesn't jitter.
- **Block occlusion** (`V2AuxSourceOcclusionTelemetry`): casts exactly **one** source→listener ray (binary block), then EMA-smooths (keyed by block entity id) to convert wall-edge/doorway flips into a graded result. This is the explicit engine-behaviour assumption: a single ray is cheap but flips binary as the listener crosses occluders; temporal smoothing is the substitute for averaging many rays. With smoothing set to 0 ms the flips are fully exposed.
- **Reverb smoothing** (`SmoothReverbRoomSample`, `V2LiveReverbPocProcessor` startup ramp): exponential lerp toward new room estimates + 0.35 s gain ramp on (re)attach to avoid reverb pops.

### 4.2 Caching & idempotency

Per-voice filter/volume *signatures* (skip redundant XAudio2 writes), settings-*signature* guards (skip effect-bank rewrite unless a slider moved), per-cue path-probe caches (250 ms), reflection member caches with negative-result miss-sets (avoid re-probing absent members every frame), and the emitter↔voice binding cache (with staleness rejection via `EmitterOwnsVoice`). The `EmitterBindingGeneration` counter is a live-retune cache-invalidation key — changing a filter subtype in settings re-binds all emitter→effect bindings without a full session reset.

### 4.3 Fallback philosophy ("fail soft to vanilla, never throw on the audio thread")

Every reflection access and patch body is wrapped in broad try/catch returning safe defaults, and most patches carry a **one-strike `_disabled` latch** (three-strike for the room probe; 5-error for raycast; 3-error for atmosphere) that permanently self-disables on first throw until a session reset. The master `VanillaFallback` switch hands the whole engine back to vanilla when the listener can't be classified. Suppression is conditional on having a replacement (`HasReplacementSourcesReady`), and carrier keep-alive is optimistic during telemetry warm-up. The consistent failure mode is **silent feature degradation**, not a crash — at the cost that users get no on-screen signal when something self-disables.

### 4.4 Air/hull energy model

Engine sound is modeled as two independent energy paths (`V2EngineFilterModel`):

- **Air path**: gated by `min(listenerAtmosphere, sourceAtmosphere)` (pressure required at *both* ends) × environment transmission (1 − max(aperture-occlusion, final-muffling)); long range (~5000 m); cutoff falls 8 kHz→~17.6 Hz over distance.
- **Hull path**: binary, gated by physical grid coupling (`IsSameConstructAs`/logical/physical groups); short range (~271 m); already-low cutoff (~22 Hz→5 Hz) — structure-borne sound is bass even up close.
- **Blend**: cutoffs combined in energy (f²) domain (RMS), biasing toward the brighter/louder path; distance gains combined probabilistically (`1−(1−air)(1−hull)`). Vacuum pins the air cutoff to the contact-rumble frequency — the core "engines in space heard through the hull" effect. The same air/hull split feeds reverb wet scaling via the enclosure/pressure model.

**Engine-behaviour assumptions**: atmosphere comes from `MyPlanet.GetAirDensity`; grid coupling reflects across subgrids/connected constructs; environment occlusion (Area 2) additionally attenuates *only* the air path, never the hull path.

---

## 5. Consolidated risks / fragilities

### 5.1 Reflection & version coupling (highest-frequency risk)

- **Private-name dependencies** pervade: `m_insideShip`, `m_shipGrid`, `m_topGrid`, `RelativeDampeningEntity`, `CurrentMovementState`/`GetWalkingState`, `m_effectBank`/`m_effects`, `SoundData.Sound`, `MyEffectInstance.UpdateFilter`, `MyAudioEffect.SoundEffect.*`, `m_gameAudioVoice`/`m_masterVoice`/`m_audioEngine`, `m_cueBank`/`m_waveBank`, `CalculateNaturalGravityAt`, `GetOxygenInPoint`, the `CastRay` overload zoo, pitch members, raw-voice volume members. Any Keen/VRage rename silently degrades to fallbacks/vanilla — **silent feature loss**, not a hard error.
- **Harmony targets by string** (`UpdateAfterSimulation`, `SelectEffect`, `UpdateFilter`, pinned overload `Type[]` signatures) break on rename; only the self-disable latch guards them.
- **String-literal cue names** (`ShipLargeThrusterIon`, the `Thuster` typo, `BlockHydrogenEngine`, lock cues, vanilla effect subtypes `realShipFilter`/`LowPassCockpit`) and **manually-maintained whitelists** (`HasDetailLocalVariant`, `IsKnownVanillaShipStateCue`) misroute or go silent on Keen content changes with no error. `HasDetailLocalVariant` is a *second, independent* notion of "has a 2D variant" from `V2ToolLoopWaveCatalog`'s actual D2 detection — they can disagree.
- **`ModeName` string matching** is load-bearing in the hull gate (`IsOutsideSeatCamera`) and telemetry stability — a mode-name refactor silently breaks hull coupling.
- **IL no-op probe** (`SetReverbParameters` single-`ret`) would mis-read a future wrapper that adds a debug log as "functional," re-enabling the dead direct reverb path.

### 5.2 Threading

- All reflection caches, binding dictionaries, smoothing-state dictionaries, and the shared static scratch buffers (`GridSearchScratch`, `RollingProbeSamples`, `ThicknessRayHits`) are **plain non-concurrent collections** written from Harmony callbacks/XAPO, assuming a single audio/sim thread. Concurrent access (e.g. debug-overlay thickness probe vs `Update`, or any future Keen multithreading of `UpdateFilter`) could corrupt buffers.
- **`ShouldSuppressVanillaShipCue` re-captures the listener inline** (incl. reflection) on the `PlaySound` prefix call path per gated cue — a hot path mitigated only by the stability cache.
- `V2LiveReverbPocProcessor` is `unsafe` pointer DSP on the audio thread mixing the **entire master bus** — a logic error blasts all audio; mitigated by panic-clear + triple clamp + startup ramp.

### 5.3 Version coupling & contract drift (RSP-internal)

- **Three parallel hand-maintained alias tables** (`TryReadFloat`, `TrySet`, menu `command` strings) already drift: read/write asymmetries (`blockcurve`, sealed/atm/reverb setters without readers) silently break `TryGetDefault`-based menu reset.
- **`Summary()` positional `string.Format`** (indices to `{85}+`, out of order) desyncs trivially on field insertion.
- **Collapsed/deprecated-but-serialized fields**: `PlayerFilterBlockRange`/`RangeScale` (force-overwritten each `Clamp`), orphan `MufflingStrength`/`InteriorBaseTransmission`/`AtmosphericMufflingFloor`.
- **Dormant/dead code** is a maintenance hazard: `SixDirectionSourceModel` class, `V2BlockSoundSourceResolver`, `V2GlobalReverbRuntime.Update()` + all source-voice reverb routing (3 kill-switches), both `TryPlayAutomaticWetSend` auto paths (no callers), `TryApplyLiveSourceReverbSend` (stubbed). Someone may "fix" or assume these are wired.
- **`ResolveEffectiveRange` ignores its `vanillaRange` argument** — all scalable block sounds forced to one flat range; the Arc/Real vanilla-baseline computation is discarded for range (used only for gain).
- **Settings declared but unconsumed**: `EngineFilterInteriorAirWeight`, `EngineFilterInteriorMaxFrequency` — tuning them has no effect (dormant hooks).
- **Hardcoded Steam install paths** + top-directory-only `Audio*.sbc` enumeration break on non-default installs and miss mod audio in subfolders.

### 5.4 Frame/wall-clock coupling & magic numbers

- `DateTime.UtcNow`-based smoothing/throttles everywhere (no game-time) drift with real wall-clock; pause/slow-mo behaves oddly. The 300-frame settings poll is frame-rate-coupled (low FPS lengthens reload latency).
- `VanillaFullSpeed=96` assumes the ~100 m/s vanilla cap — speed-mod servers misbucket everything as "fast"/large-speed.
- The `fs/6` cutoff clamp (~7350 Hz @ 44.1 k) silently caps the advertised 8000 Hz max.
- Large hand-tuned constant clusters (near-field 4/12/24 m, `MinimumKnownThrustersBeforeGridCensus=6`, `StableListenerHold=800ms`/`10m`, voxel `0.35m`/`0.25`, hit-merge `0.035m`, `LargeShipMinWeight=500000`, the `0.25+gain·0.1875` detail-gain curve) are undocumented and require a rebuild to retune.

### 5.5 Other notable fragilities

- **Session-by-`ReferenceEquals`** assumes Keen mints a fresh Session per world — a pooled/reused instance would skip the reset.
- **`RememberThruster` hash fallback** on `EntityId==0` can collide and leak a stale thruster until census prunes it; the same `EntityId==0` issue affects connector-impact dedup keys.
- **Stale `SourcePosition`**: mitigated by trusting `entity.PositionComp.GetPosition()`, but a valid entity returning `Vector3D.Zero` silently drops the sample (disables occlusion for that source).
- **Self-disabling overlays/log fail closed and silent** — no on-screen indication after a transient error; only a `MyLog` line.
- **Occlusion telemetry is populated as a side effect of voice polling** — if both the sounds overlay is off and the catalog poll hasn't fired, `FormatSources` shows stale data (the 500 ms catalog poll is the baseline guard).
- **Where inputs were thin/conflicting:** two divergent `EvaluateDistanceGain` definitions exist (`SixDirectionSourceModel`: `(1-norm)^exp` vs `V2AuxSourceOcclusionTelemetry`: `1-norm^curve`) — mixing them in future edits silently changes falloff shape. The exact cadence relationship between the async `VanillaShipEnvironment` feed and the audio tick is "most-recent ≤750 ms," not synchronized.

---

## 6. Extension points / future-feature hooks

Deliberate seams identified across the codebase:

- **Live retune without reset**: `EmitterBindingGeneration` + `TrackEmitterBindingSignature` re-bind emitter→effect on filter-subtype change; `GetEngineFilterEffectSignature` is a ready cache-key for new dynamic-filter params.
- **Remote-grid collapse**: `V2RemoteGridCollapseDistance` (already wired, threshold-gated) collapses distant foreign grids into a single point source.
- **Reverb routing**: the 5-route normalizer + `IsGlobalReverb*Route` family, the `wetOnly` XAPO ctor flag, the four spatial fields in `V2LiveReverbParameters` (currently collapsed into one `enclosure` scalar), `TryApplyLiveSourceReverbSend`/`SetSourceVoiceTarget` (stubbed per-source reverb), and the three `*Enabled=false` kill-switches (intended single-line re-enables once XAudio2 routing crashes are solved).
- **Non-lowpass filters**: `NormalizeCustomFilterType`/`ToSharpFilterType` already support HighPass/BandPass/Notch though only LowPass is currently produced; the `Internal`/`Hull`/`External` route switch leaves room for a distinct internal-engine filter.
- **Interior air shaping**: `EngineFilterInteriorAirWeight`/`EngineFilterInteriorMaxFrequency` (declared, inert), and `AirEnvironmentOcclusionContribution` as the explicit Area 1↔Area 2 coupling knob.
- **Per-cue range scaling**: replacing the flat `ResolveEffectiveRange` is the obvious next step; the vanilla-baseline + Arc/Real pairing infrastructure is already present.
- **Same-room acoustic routing**: `RoomKey`/`TryCompareOxygenRooms` is a generic same-room predicate beyond its current aux-occlusion use.
- **Cue coverage growth**: `AudioVoiceCatalog` is explicitly the discovery tool; `V2ThrusterKind`/`V2ShipSoundGroup` enums + Prototech special-casing + Arc/Real prefix pairing are clean extension points for new thruster families and cue namespaces.
- **Shared 6-source unification**: `SixDirectionSourceModel` class is the intended (unadopted) extension point per the refactor plan; `DirectionState.BuildSnapshot`'s inline centroid is what it would replace.
- **Visualization scaffolding**: `V2ThicknessRaySegment` + `TryProbeThicknessIntervals` + `ProbeFrom`/`ProbeTo` form a complete fraction-based, re-projectable overlay extension that already mirrors the audible primitives; `PlanetEnvironmentAvailable`/`NaturalGravityStrength` are surfaced for future planet-vs-space acoustic differentiation.
- **Carrier/room-aware extensions**: `EnvironmentAmbiencePatch.SetPlanetAmbientTarget`'s split target/modifier + `forceModifierFloor` (RSP-driven environment level), and `VanillaShipEnvironment.Snapshot.RoomName`/`GridEntityId`/`ListenerPosition` (room-aware reverb/occlusion not yet consumed for DSP).

---

> **Note on documentation lag:** per the project memory index, `README.md` and `docs/audio-engine-v2-refactor-plan.md` lag the actual refactor — this document reflects the *code as analyzed*, and several plan-described constructs (notably the shared `SixDirectionSourceModel`) are not wired into the live engine.