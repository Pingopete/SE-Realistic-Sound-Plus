# Realistic Sound Plus

A client-side [Pulsar](https://github.com/sepluginloader) plugin for Space Engineers that replaces and extends the game's "realistic sound" experience with a custom, spatialized, physically-modeled audio engine.

RSP started as a fix for the most egregious bugs in Space Engineers' built-in realistic-audio mode, then grew into a broader immersion project. It uses vanilla SE sounds as raw material but takes ownership of emitter placement, cue selection, state transitions, filtering, and listener-environment mixing.

## The Three Areas

The mod (code and in-game menu) is organized around three goals:

1. **Thruster sound realism** — thruster audio is dampened through ship walls and modeled as two physical transmission paths: an **airborne** path through the surrounding atmosphere (brighter at higher pressure, darker with distance, near-silent in vacuum) and a **structure-borne hull** path that survives vacuum whenever the listener is physically coupled to the grid. The result changes realistically as you move between a pressurized interior, a depressurized bridge, and open vacuum.

2. **Progressive environmental muffling** — on planets, ambient world audio (wind, rain, weather) no longer snaps off at a binary occlusion threshold. It **persists and becomes progressively more muffled, low-passed, and quieter** the more structure sits between the player and the open sky. Descending from the surface into a deep base gradually darkens the outside world instead of cutting it. This requires RSP to keep the vanilla ambient/weather voice alive (an "Environment Bed") so it can be filtered rather than silenced.

3. **Realistic environmental reverb** — a custom DSP reverb wet path that adds reflections to game audio, with reverb parameters driven by a spherical raycast probe around the player and updated continuously. Small sealed rooms, large hangars, caves, and open exteriors each produce a distinct, live reverberant character.

## Branch Status

Active branch: **`v2/live-audio-engine`**. The V2 engine is the *only* ship-engine route on this branch — there is no `/rsp v2` opt-in and no legacy spatial route running beside it. Reference snapshots of the pre-V2 build live on `reference/*` branches.

The original V2 design document is [docs/audio-engine-v2-refactor-plan.md](docs/audio-engine-v2-refactor-plan.md); it also contains the confirmed-vanilla cue/thruster reference tables. This README reflects the current build; where the two disagree, the code and the in-game menu are authoritative.

## How It Works

### Plugin lifecycle

[Plugin.cs](Plugin.cs) is a VRage `IPlugin`. `Init()` loads settings and applies all Harmony patches; `Update()` runs every frame, driving the V2 runtime, debug overlays, and the file log, and hot-reloads the settings XML every ~300 frames. Built against .NET Framework 4.8; the project auto-deploys the DLL to the local Pulsar plugin folder on build (see [RealisticSoundPlus.csproj](RealisticSoundPlus.csproj)).

### Ship-engine pipeline (Area 1)

The orchestrator is [AudioEngineV2Runtime.cs](AudioEngineV2/AudioEngineV2Runtime.cs). Each frame it:

1. Captures listener state ([V2AudioListenerState.cs](AudioEngineV2/V2AudioListenerState.cs)) — classifies the listener as `inside-seat`, `inside-room`, `outside-seat-camera`, `outside-grid-contact-*`, or `vanilla-fallback`, with an 800 ms stability hold to prevent mode flicker.
2. If `vanilla-fallback` (no physical relationship to a ship), it silences RSP emitters and leaves stock vanilla ship audio alone.
3. Otherwise, it runs a thruster census into a per-grid model ([V2GridAudioState.cs](AudioEngineV2/V2GridAudioState.cs)) that collapses thrusters into **up to six weighted direction groups** (Forward/Back/Left/Right/Up/Down).
4. Each group drives up to three `MyEntity3DSoundEmitter`s: detail-idle, detail-active, and state. Cues come from [V2CueCatalog.cs](AudioEngineV2/V2CueCatalog.cs) by thruster type (Ion/Hydrogen/Atmospheric/Prototech) and grid size, with 2D/local vs 3D/spatial variants chosen from listener position.
5. Each emitter gets a per-emitter dynamic filter ([V2EngineFilterModel.cs](AudioEngineV2/V2EngineFilterModel.cs)) computed from the air-path + hull-path model described above.
6. Confirmed vanilla centered ship cues are suppressed while V2 owns the soundscape.

Detail intensity is read in priority order: per-thruster `ThrustOverridePercentage` → analog ship-controller move input → `CurrentThrustPercentage` (inertial dampeners) → raw force. The smoothed command drives volume linearly and pitches active detail from 0.5×→1.5×; idle detail crossfades out as firing fades in.

### Environmental muffling (Area 2)

A 26-ray spherical openness probe at the listener ([V2PlayerEnvironmentTelemetry.cs](AudioEngineV2/V2PlayerEnvironmentTelemetry.cs)) measures how much structure/voxel separates the player from open sky, producing a continuous occlusion/aperture value rather than a binary in/out flag. The player/aux filter ([V2PlayerFilterRuntime.cs](AudioEngineV2/V2PlayerFilterRuntime.cs)) uses this to low-pass and attenuate three categories of non-engine voice — **environment** (wind/weather), **block** (machinery, per-source occlusion rays), and **local** (footsteps/breath/body, pressure only). To keep wind audible-but-muffled in deep interiors instead of cut, [EnvironmentAmbiencePatch.cs](Patches/EnvironmentAmbiencePatch.cs) keeps the vanilla planet-ambient and weather voice carriers alive so RSP can filter them.

### Environmental reverb (Area 3)

A custom managed-DSP reverb ([V2ManagedDspReverbRuntime.cs](AudioEngineV2/V2ManagedDspReverbRuntime.cs)) and a streaming SharpDX XAPO processor ([V2LiveReverbPocProcessor.cs](AudioEngineV2/V2LiveReverbPocProcessor.cs)), with `SubmixVoice` wet-bus plumbing in [V2ReverbDiagnosticPing.cs](AudioEngineV2/V2ReverbDiagnosticPing.cs). Reverb parameters (room size, decay, diffusion, tone) are derived live from the spherical ray probe. This replaces the older approach of driving Keen's `SetReverbParameters` (a no-op in the tested build). The reverb **route** selects how the wet path is applied (see *Reverb routes* below); the default is a custom master/inline route intended to reflect the whole in-game mix.

### Game-engine integration (Harmony + reflection)

RSP reaches into the engine's private XAudio2/VRage audio internals through Harmony patches and heavy reflection. Current active hooks, by purpose:

| Purpose | Patched member |
| --- | --- |
| Feed thruster state to V2 | `MyThrust.UpdateAfterSimulation` (postfix) |
| Harvest vanilla inside/room state | `MyShipSoundComponent.UpdateVolumes` (postfix) |
| Keep ship sounds in the shared 3D route | `MyShipSoundComponent.UpdateShouldPlay2D` (prefix), `UpdateSoundDimension` (postfix) |
| Suppress vanilla centered ship cues | `MyEntity3DSoundEmitter.PlaySound` / `PlaySoundWithDistance` (prefix), `Update` / `FastUpdate` (pre/post) |
| Apply engine/aux filter route to a voice | `MyEntity3DSoundEmitter.SelectEffect` (postfix), `SetSound` (postfix) |
| Apply live custom filter parameters | `VRage.Audio.MyEffectInstance.UpdateFilter` (pre/post) |
| Scale block-sound audible range | `VRage.Audio.MyXAudio2.SourceIsCloseEnoughToPlaySound` (prefix) |
| Suppress breathing when helmet is open | `MyCharacterBreath.Update` (pre/post) |
| Keep planet/weather ambient carriers alive (Area 2) | `MySessionComponentPlanetAmbientSounds.SetAmbientOff` (prefix) / `UpdateAfterSimulation` (postfix), `MySectorWeatherComponent.ApplySound` (postfix) |

Filters are runtime `VRage.Data.Audio.MyAudioEffect` definitions registered into the engine's effect bank; live per-voice parameters are pushed through `SharpDX.XAudio2.SourceVoice.SetFilterParameters` ([RspDynamicAudioFilters.cs](AudioEngineV2/RspDynamicAudioFilters.cs)).

## Quick Test Flow

1. Build the plugin (auto-deploys to the local Pulsar folder) and launch SE with Pulsar.
2. Enter a powered ship with working thrusters.
3. `/rsp menu` for the clickable settings UI, or `/rsp sounds on` for the centered debug overlay.
4. `/rsp show` prints the live settings summary (the source of truth for current values).
5. Fly inside vs. outside, pressurize/depressurize a room, and descend into a planet base to exercise all three areas.
6. `/rsp catalog` after moving through an area prints the unique vanilla cues RSP has seen.
7. `/rsp logpath` prints the debug-log path for offline inspection.

## In-Game Menu

`/rsp menu` opens the settings UI ([RspSettingsMenu.cs](RspSettingsMenu.cs)). Runtime values apply immediately; **Save** writes the XML. It is organized into four major sections matching the architecture:

- **Thruster Audio** — source layers (detail / idle / state toggles) and level/motion (gains, smoothing, distance).
- **Thruster Propagation** — the engine filter route, live response chart and readouts, and the **Atmospheric Path** / **Hull Path** controls (Area 1).
- **Player / Aux Filter** — aux master routes, shared aux filter shape, sealed-room and geometry controls, the **Environment Bed**, block-emitter and player-local controls, and an applied-voices readout (Area 2).
- **Environmental Reverb** — reverb toggle, route/mix readout, ray-driven room-size and diffusion modifiers, and live wet output (Area 3). Ship-scaling controls live at the end.

Frequency sliders use a logarithmic response. The filter charts preview the current filter shape; they are not live spectrum displays.

## Chat Commands

Almost every menu control also has a `/rsp` chat command, and most numeric settings accept several short aliases (the alias map lives in [RealisticSoundPlusSettings.cs](RealisticSoundPlusSettings.cs)). Rather than enumerate every alias here (that list is large and changes), the most useful commands are grouped below. Run `/rsp show` to see all current values, and `/rsp help` for a short in-game list.

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded a few seconds after the file changes. Saved values persist across world loads; branch defaults only apply when a setting is missing.

### Utility

| Command | Function |
| --- | --- |
| `/rsp menu` | Toggle the settings UI. |
| `/rsp show` | Print all current runtime settings. |
| `/rsp save` / `/rsp reload` | Write / re-read the settings XML. |
| `/rsp sounds on\|off` | Toggle the centered audio debug overlay. |
| `/rsp filters on\|off` | Toggle the filter-controller overlay. |
| `/rsp catalog` | Print/log unique session cues with category guesses. |
| `/rsp log on\|off`, `/rsp logpath` | Toggle / locate the V2 debug log. |
| `/rsp help` | Short in-game command list. |

### Area 1 — Thruster audio & propagation

| Command | Function |
| --- | --- |
| `/rsp detail on\|off`, `/rsp idle on\|off`, `/rsp state on\|off` | Toggle the detail, detail-idle, and state layers. |
| `/rsp detailgain`, `/rsp idlegain`, `/rsp stategain`, `/rsp gain` | Per-layer and overall engine gain (0–4). |
| `/rsp dist`, `/rsp distcurve` | Shared emitter hearing range (m) and falloff curve. |
| `/rsp cmdsmooth`, `/rsp smooth`, `/rsp emitterfade` | Command/volume smoothing and rebind fade (ms). |
| `/rsp detail2dpos on\|off`, `/rsp state2dpos on\|off` | Force mapped 2D/local cue variants through the positional emitters while inside. |
| `/rsp externalfilter`, `/rsp internalfilter` `<route>` | Filter route for outside/contact vs inside/local engine emitters. Routes: `off, helmet, cockpit, cockpitnooxy, realship, deep, enginefilter, auxfilter`. |
| `/rsp enginefilterdynamic on\|off` | Per-emitter dynamic cutoff/Q from distance/atmosphere/contact (vs a static fallback shape). |
| `/rsp airnear`, `/rsp airfar`, `/rsp airrange`, `/rsp aircurve`, `/rsp airq`, `/rsp interiorair` | Airborne (atmospheric) path: near/far cutoff (Hz), distance range/curve, Q, and interior air weight. |
| `/rsp hullnear`, `/rsp hullfar`, `/rsp hullrange`, `/rsp hullcurve`, `/rsp hullq` | Structure-borne hull path: near/far cutoff (Hz), distance range/curve, and Q. |
| `/rsp interiorcutoff`, `/rsp vacuumcutoff` | Max interior airborne cutoff; vacuum/contact structural cutoff (Hz). |
| `/rsp atmoverride on\|off`, `/rsp externalatm <0..1>` | Force the engine atmosphere reads to a test pressure. |

### Area 2 — Player / aux filtering (environmental muffling)

| Command | Function |
| --- | --- |
| `/rsp playerfilter on\|off` | Master switch for aux filtering (engine audio is routed separately). |
| `/rsp envfilter`, `/rsp blockfilter`, `/rsp localfilter` `on\|off` | Per-category toggles: wind/weather, machinery/blocks, player-local. |
| `/rsp playerenvray`, `/rsp aperturecurve`, `/rsp envcurve` | Openness-probe ray length (m) and aperture/occlusion curves. |
| `/rsp occlusionstrength` | Global aux occlusion multiplier. |
| `/rsp envthickness`, `/rsp blockthickness`, `/rsp voxelweight` | Structure/voxel occlusion thickness weighting per category. |
| `/rsp sealedextra`, `/rsp sealedenv`, `/rsp sealedblock` | Extra muffling for sealed rooms (combined / env / block). |
| `/rsp auxfilterfreq`, `/rsp auxfilterq`, `/rsp auxfiltertype` | Shared clear cutoff (Hz), Q, and filter shape (`lowpass, highpass, bandpass, notch`). |
| `/rsp envmufflefreq`, `/rsp blockmufflefreq`, `/rsp auxmufflefreq` | Fully-muffled cutoff per category (Hz). |
| `/rsp envfloor` | Minimum gain floor so the RSP wind bed stays faintly alive. |
| `/rsp blockrange`, `/rsp blockcurve` | Block-emitter distance range (m) and falloff curve. |
| `/rsp auxsmooth` | Aux filter/volume smoothing (ms). |
| `/rsp auxatmoverride on\|off`, `/rsp auxatm <0..1>` | Player/aux-only pressure simulation (independent of the engine filter). |
| `/rsp auxpathdebug on\|off`, `/rsp reverbraydebug on\|off` | Visualize block-occlusion paths / the spherical ray probe. |

### Area 3 — Environmental reverb

| Command | Function |
| --- | --- |
| `/rsp reverb on\|off` | Master reverb toggle. |
| `/rsp reverbroute <world\|global\|managed\|globalbus\|custombus>` | Select the wet-path route (see below). Setting a route also enables reverb. |
| `/rsp reverbwet` | Overall wet level (0 disables the reflected field). |
| `/rsp reverbroommod`, `/rsp reverbdiffmod` | Multipliers on the ray-calculated room size and diffusion (1 = use the auto value). |
| `/rsp reverbdecay`, `/rsp reverbpredelay`, `/rsp reverblatedelay`, `/rsp reverbdensity`, `/rsp reverbtone` | Core tail shaping: decay (s), pre/late delay (ms), density (%), tone (Hz). |
| `/rsp reverbearlydb`, `/rsp reverbtaildb`, `/rsp reverbhfdb` | Early/tail gain and high-frequency damping (dB). |
| `/rsp reverbdiag`, `/rsp reverbvoices` | Print reverb route status and the live voices routed through the wet path. |
| `/rsp reverbping`, `/rsp reverbcue [cue]`, `/rsp dspdiag [cue]` | Play a DSP impulse / a wet-rendered cue / diagnose a cue's wet tail. |

**Reverb routes** (`/rsp reverbroute`): `world` (custom inline DSP over in-game audio), `global`/`master` (custom master/inline over the full mix including UI — the default `custommaster`), `managed` (managed-DSP per-cue tail copies), `globalbus` (legacy XAudio submix), `custombus` (custom submix POC).

## Debug Overlay, Log, and Markers

`/rsp sounds on` shows the centered overlay: global listener state, V2 listener mode and room probe, current audio voices with RSP route/diagnostics, and per-emitter filter readouts. `/rsp filters on` shows a separate filter-controller overlay.

Debug spheres mark V2 source groups: **bright cyan** = active engine-detail emitter, **bright orange** = active engine-state emitter; dim = known but quiet. A cue marked `UNCONTROLLED` is being played by vanilla and not yet associated with a V2 emitter; RSP-owned cues show routes like `v2-detail-*`, `v2-state-*`, or `filter`.

The V2 debug log writes one line per second (global state, listener line, settings, top voices, per-direction detail diagnostics, and `event=listener` route changes), rotating at ~2 MB:

```text
%APPDATA%\SpaceEngineers\RealisticSoundPlus-v2-debug.log
```

Use `/rsp logpath` in game to print the exact path.
