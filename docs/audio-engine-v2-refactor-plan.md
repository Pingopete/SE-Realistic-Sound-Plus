# Audio Engine V2 Refactor Plan

Branch: `audio-engine-v2-refactor`

## Project Goal

Rebuild Realistic Sound Plus around an explicit client-side ship audio engine that uses vanilla Space Engineers sounds as source material, but owns emitter placement, loop selection, state transitions, filtering, and listener-environment mixing.

The refactor should replace the vanilla grid-centered ship soundscape with a layered model:

- Six grouped positional thrust emitters per relevant ship grid, one for each natural thrust direction.
- Basic positional engine/detail audio played from those grouped emitters, using the same 3D source files inside and outside unless a tested 2D pairing proves better.
- Vanilla-style state-machine engine audio played from the same six grouped emitter positions, using confirmed vanilla state-machine cue families.
- Interior state-machine audio switches to the matching 2D/local file variants when the player camera/listener is inside the ship.
- Environment-aware filtering and gain based on listener/source position, enclosed room state, cockpit/seat state, atmosphere density, vacuum, and distance.
- Narrow suppression of confirmed vanilla centered ship cues only when the RSP replacement route is active.

The intended result is not just louder or more spatial audio. It is a coherent ship interior/exterior soundscape where the player can hear where thrust is coming from, whether they are inside a hull, whether atmosphere can carry the sound, and whether the ship is idling, spooling, maneuvering, or flying at speed.

## Workspace Status

The current branch was created from the existing checkout with the active experiment work preserved. The workspace already contains several useful research patches:

- `SpatialThrusterAudioPatch`: proves per-thruster 3D positioning, thrust scaling, smoothing, filtering, and distance/atmosphere transmission can work.
- `ShipInteriorMufflingPatch`: proves vanilla inside-ship state can be harvested from `MyShipSoundComponent`.
- `ThrusterFilterPatch`: proves vanilla low-pass effects can be applied to selected ship/exterior audio routes.
- `DirectionalSpoolAudioPatch`: current uncommitted experiment for six-direction grouped spool emitters.
- `CenteredSpoolSuppressionPatch`: current uncommitted experiment for blocking known grid-center spool cues.
- `AudioDebugOverlay` and `AudioDiagnostics`: proven tooling for seeing currently playing voices and RSP virtual emitters.

The user-provided workbook `SE RSP Sound Organization.xlsx` is present at the repo root. It is a single-sheet mockup with columns for sound category, engine type, engine size, state-driven status, spatial/local use, file name, and description. This plan treats it as user interpretation and desired design input, then compares it against confirmed vanilla mappings below.

## Confirmed Vanilla Definition Sources

Read-only game install sources inspected:

- `SE RSP Sound Organization.xlsx`
- `C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Data\ShipSoundGroups.sbc`
- `C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Data\Audio_shipSounds.sbc`
- `C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Data\Audio.sbc`
- `C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Data\CubeBlocks\CubeBlocks_Thrusters.sbc`

Assembly reflection against game DLLs crashed while loading dependencies, so this document treats XML definitions as confirmed and deeper runtime control flow as a later IL/decompile task.

## User Workbook Interpretation

`SE RSP Sound Organization.xlsx` broadly matches the sound families found in the vanilla definitions:

- large ship run/idle/engine/transition files have spatial `3d` and local `2d` variants
- small ship run/transition files have spatial `3d` and local `2d` variants
- atmospheric, hydrogen, and ion/prototech thruster files are grouped separately from ship run loops
- several 2D files are identified as likely interior/local/cockpit candidates
- several 3D files are identified as likely exterior/spatial candidates

The workbook's `Spacial` column is interpreted as spatial/3D use. Its `Local` rows are good candidate inputs for interior/listener-space layers.

Confirmed alignments:

| Workbook file/family | Workbook interpretation | Confirmed vanilla cue role |
| --- | --- | --- |
| `ArcShipLargeIdle2d/3d.wav` | large idle | `ShipLargeIdle`, `Large:ShipIdle` |
| `ArcShipLargeRunLoop2d/3d.wav` | large state-driven run loop | `ShipLargeRunLoop`, `Large:MainLoopSlow/Medium/Fast` |
| `ArcShipLargeEngine2d/3d.wav` | high throttle/rattle/wind-like engine loop | `ShipLargeEngine`, `Large:ShipEngine` |
| `ArcShipLargeStart2d/3d.wav` | thrust/power added | `ShipLargeStart`, `Large:EnginesStart` |
| `ArcShipLargeEnd2d/3d.wav` | thrust/power removed | `ShipLargeEnd`, `Large:EnginesEnd` |
| `ArcShipLargeSpeedUp2d/3d.wav` | turn on / rising state | `ShipLargeSpeedUp`, `Large:EnginesSpeedUp` |
| `ArcShipLargeSpeedDown2d/3d.wav` | turn off / falling state | `ShipLargeSpeedDown`, `Large:EnginesSpeedDown` |
| `ArcShipSmallRunSlow/Medium/FastLoop2d/3d01.wav` | small low/mid/high run states | `ShipSmallRunSlow/Medium/Fast`, `Small:MainLoopSlow/Medium/Fast` |
| `ArcShipSmallSpeedUp/Down2d/3d*.wav` | small thrust input added/removed | `ShipSmallSpeedUp/Down`, `Small:EnginesSpeedUp/Down` |
| `ArcShipThrusterHydrogen2d/3d.wav` | raw/meaty hydrogen thrust layer | `ShipSmallThrusterHydrogen` and `ShipLargeThrusterHydrogen` |
| `ArcShipThrusterHydroNoPower2d/3d.wav` | hydrogen idle/no-power layer | `ShipLargeThrusterHydrogenIdle` |
| `ArcShipThrusterAtmoFast2d/3d.wav` | atmospheric fast layer | `ShipSmallThrusterAtmosphericFast` and `ShipLargeThrusterAtmosphericFast` |
| `ArcShipThrusterAtmoNoPower2d/3d.wav` | atmospheric idle/no-power layer | `ShipSmallThrusterAtmosphericIdle` and `ShipLargeThrusterAtmosphericIdle` |

Important corrections and clarifications:

- The workbook groups many large/small ship run files under hydrogen, but vanilla defines them as ship sound group cues. They are not hydrogen-specific in `ShipSoundGroups.sbc`; they are grid-level ship run/idle/transition cues. The explicitly hydrogen-specific thruster cues are `ShipSmallThrusterHydrogen`, `ShipLargeThrusterHydrogen`, their idle variants, and hydrogen push cues.
- The workbook's interpretation of `ShipLargeEngine` and `ShipSmallEngine` as rattle/high-speed/wind-like loops is consistent with previous in-game testing. In vanilla definitions they are `ShipEngine` role cues, separate from `MainLoop*` and thruster-type loops.
- `ArcShipThrusterAtmoMedium2d/3d.wav` appears in defined medium cues, but the active ship sound group maps `AtmoThrustersMedium` to the `AtmosphericSlow` cue for both small and large groups. The medium files still appear because `ShipSmallThrusterAtmosphericSlow` and `ShipLargeThrusterAtmosphericSlow` use the medium wave files.
- `ArcShipThrusterAtmoNoPower2d.3.wav` appears in the workbook but was not found in `Audio_shipSounds.sbc`. The confirmed local no-power file is `ArcShipThrusterAtmoNoPower2d.wav`.
- Some workbook transition descriptions use "turn on/off" where vanilla roles distinguish `EnginesStart/End` from `EnginesSpeedUp/SpeedDown`. RSP V2 should keep these separate: start/end for engine enable state, speed-up/down for acceleration or rising/falling ship sound state.

Design impact:

- Treat the workbook as the first RSP cue-catalog sketch.
- Store both user-intended role and confirmed vanilla role in `CueCatalog`.
- Use workbook `Local` entries as prime candidates for the inside-camera state-machine variant.
- Use workbook spatial `3d` entries as candidates for exterior state-machine, engine-detail, and hull-transmitted layers.
- Keep hydrogen-specific and ship-run cue families separate in code, even if the first subjective listening pass groups them as "engine" sounds.

## Base V2 Sound Model Decision

The first stable implementation should use a single scalable positional model: up to six active audio source positions per relevant ship grid, one for each thrust direction. This replaces the earlier per-thruster default, which sounded good but can become too expensive on large player ships.

Each direction group should be positioned at a weighted average of active thrusters in that direction:

- start with all working thrusters assigned to the direction group
- weight by produced thrust and/or max force
- bias toward the region currently contributing the most thrust
- keep stale/idle groups alive only long enough to fade cleanly
- cap the normal runtime to six grouped positions per grid

Those six positions are shared by two base sound roles:

- `EngineDetail`: basic positional engine/thruster sound, using 3D sound files by default
- `EngineState`: vanilla-style state-machine run/idle/start/end/speed-up/speed-down sound, using the confirmed vanilla ship sound cues

The goal is not to create many independent sound systems. The goal is a small number of coherent logical emitters whose cue, volume, filter, and 2D/3D variant are controlled by RSP.

### Inside And Outside Variant Rules

For `EngineDetail`:

- outside ship: use the selected 3D detail sound files
- inside ship: keep the same selected 3D files initially and apply the shared interior deep low-pass/hull filter
- if a paired 2D detail variant is later proven better for interior use, allow that as a catalog-level switch

For `EngineState`:

- walking on/touching the ship exterior in vacuum: use the vanilla 3D state-machine files through the RSP grouped emitter model
- inside ship: switch the same logical six grouped sources to the matching 2D/local file variants
- do not apply the shared deep interior filter to the 2D state-machine route, because those files already appear authored for internal/local ambience
- keep the source positions for grouping, proximity, diagnostics, and possible pseudo-local weighting even when the audio route selects a 2D/local variant

Variant switching should be explicit and simple. A tiny fade is acceptable to avoid clicks, but the base design should not require long-running duplicate 2D and 3D crossfade layers for every cue.

### Vanilla Fallback Boundary

For the first stable pass, RSP should not take over ship engine audio when the listener is simply outside the ship with no physical relationship to it.

Fallback states:

- third-person camera hovering outside the ship
- player floating outside the ship in vacuum and not physically touching/walking on the grid
- any exterior listener state where vanilla realistic audio would normally use its stock ship engine presentation

In those states:

- restore or allow stock vanilla ship engine sounds
- do not suppress vanilla centered ship audio
- stop or mute RSP replacement engine emitters for that grid
- keep diagnostics available so the fallback decision can be verified

RSP replacement audio should initially be active only for inside-ship/cockpit/seat states, walking on or physically contacting the ship, and explicit debug test modes.

### Shared Interior Filter Rule

RSP should have one coherent filter/transmission decision for external/spatial engine audio:

- apply it to 3D `EngineDetail`
- apply it to 3D `EngineState` while the listener is inside the ship
- do not apply it to 2D/local `EngineState` while the listener is inside the ship

This keeps the interior ambience adjustable as one system while preserving the authored interior tone of the 2D state-machine files.

## Vanilla Ship Sound System

`ShipSoundGroups.sbc` defines the grid-level variable-driven ship sound system.

Global system values:

- `MaxUpdateRange`: 50 m
- `FullSpeed`: 96 m/s, used for ship sound volume calculations
- `LargeShipDetectionRadius`: 15 m, max distance from a large-grid block to play 2D sounds
- `WheelStartUpdateRange`: 500 m
- `WheelStopUpdateRange`: 750 m

There are two ship sound groups:

- `Small`: `MinWeight=3500`, allowed on small and large grids
- `Large`: `MinWeight=500000`, allowed on small and large grids

Both groups define:

- `EnginePitchRangeInSemitones=4`
- `EngineTimeToTurnOn=4`
- `EngineTimeToTurnOff=3`
- engine volume curve from speed/load 0 to 1
- speed-up and speed-down ducking values
- `ThrusterPitchRangeInSemitones=4`
- thruster volume curve at 0, 0.25, 0.5, 0.75, 1
- thruster composition blending:
  - `ThrusterCompositionMinVolume=0.4`
  - `ThrusterCompositionChangeSpeed=0.025`
- wheel volume/pitch values

This confirms vanilla is already a state machine, not just a static sound. It selects loops and transitions by ship group, speed/load, active thruster types, and wheel state.

## Vanilla State Roles

Small group mapping:

| Role | Cue |
| --- | --- |
| MainLoopSlow | `ShipSmallRunSlow` |
| MainLoopMedium | `ShipSmallRunMedium` |
| MainLoopFast | `ShipSmallRunFast` |
| EnginesStart | `ShipSmallStart` |
| EnginesEnd | `ShipSmallEnd` |
| EnginesSpeedUp | `ShipSmallSpeedUp` |
| EnginesSpeedDown | `ShipSmallSpeedDown` |
| ShipEngine | `ShipSmallEngine` |
| IonThrusters | `ShipSmallThrusterIon` |
| IonThrustersIdle | `ShipSmallThrusterIonIdle` |
| HydrogenThrusters | `ShipSmallThrusterHydrogen` |
| HydrogenThrustersIdle | `ShipSmallThrusterHydrogenIdle` |
| AtmoThrustersSlow | `ShipSmallThrusterAtmosphericSlow` |
| AtmoThrustersMedium | `ShipSmallThrusterAtmosphericSlow` |
| AtmoThrustersFast | `ShipSmallThrusterAtmosphericFast` |
| AtmoThrustersIdle | `ShipSmallThrusterAtmosphericIdle` |
| IonThrusterPush | `ShipSmallThrusterIonPush` |
| HydrogenThrusterPush | `ShipSmallThrusterHydroPush` |
| PrototechThrusters | `ShipThrusterPrototech` |
| PrototechThrusterPush | `ShipThrusterPrototechPush` |
| PrototechThrustersIdle | `ShipLargeThrusterIonIdle` |

Large group mapping:

| Role | Cue |
| --- | --- |
| MainLoopSlow | `ShipLargeRunLoop` |
| MainLoopMedium | `ShipLargeRunLoop` |
| MainLoopFast | `ShipLargeRunLoop` |
| EnginesStart | `ShipLargeStart` |
| EnginesEnd | `ShipLargeEnd` |
| EnginesSpeedUp | `ShipLargeSpeedUp` |
| EnginesSpeedDown | `ShipLargeSpeedDown` |
| ShipEngine | `ShipLargeEngine` |
| ShipIdle | `ShipLargeIdle` |
| IonThrusters | `ShipLargeThrusterIon` |
| IonThrustersIdle | `ShipLargeThrusterIonIdle` |
| HydrogenThrusters | `ShipLargeThrusterHydrogen` |
| HydrogenThrustersIdle | `ShipLargeThrusterHydrogenIdle` |
| AtmoThrustersSlow | `ShipLargeThrusterAtmosphericSlow` |
| AtmoThrustersMedium | `ShipLargeThrusterAtmosphericSlow` |
| AtmoThrustersFast | `ShipLargeThrusterAtmosphericFast` |
| AtmoThrustersIdle | `ShipLargeThrusterAtmosphericIdle` |
| IonThrusterPush | `ShipThrusterIonPush` |
| HydrogenThrusterPush | `ShipThrusterHydrogenPush` |
| PrototechThrusters | `ShipThrusterPrototech` |
| PrototechThrusterPush | `ShipThrusterPrototechPush` |
| PrototechThrustersIdle | `ShipLargeThrusterIonIdle` |

Interpretation:

- The large ship main loop uses one cue, `ShipLargeRunLoop`, across slow/medium/fast and likely relies on pitch/volume modulation.
- The small ship main loop uses distinct slow/medium/fast cues.
- `ShipLargeEngine` and `ShipSmallEngine` are additional loops, not the main thrust loops. In previous testing, `ShipLargeEngine` behaved like speed/wind ambience.
- Thruster type loops are separate from ship run loops.
- Push cues are loopable definitions in XML even though the comments describe them as first speed-up push sounds. RSP should test whether vanilla starts/stops these as short lived loops or treats them as transient loop cues.

## Confirmed 2D And 3D Cue Pairings

`Audio_shipSounds.sbc` confirms that most ship system cues have `D2` and `D3` wave entries.

Examples:

| Cue | Confirmed files |
| --- | --- |
| `ShipSmallRunSlow` | `ArcShipSmallRunSlowLoop2d01.wav`, `ArcShipSmallRunSlowLoop3d01.wav` |
| `ShipSmallRunMedium` | `ArcShipSmallRunMediumLoop2d01.wav`, `ArcShipSmallRunMediumLoop3d01.wav` |
| `ShipSmallRunFast` | `ArcShipSmallRunFastLoop2d01.wav`, `ArcShipSmallRunFastLoop3d01.wav` |
| `ShipLargeIdle` | `ArcShipLargeIdle2d.wav`, `ArcShipLargeIdle3d.wav` |
| `ShipLargeRunLoop` | `ArcShipLargeRunLoop2d.wav`, `ArcShipLargeRunLoop3d.wav` |
| `ShipLargeEngine` | `ArcShipLargeEngine2d.wav`, `ArcShipLargeEngine3d.wav` |
| `ShipLargeThrusterHydrogen` | `ArcShipThrusterHydrogen2d.wav`, `ArcShipThrusterHydrogen3d.wav` |
| `ShipLargeThrusterAtmosphericFast` | `ArcShipThrusterAtmoFast2d.wav`, `ArcShipThrusterAtmoFast3d.wav` |
| `ShipLargeThrusterIon` | `ArcShipThrusterIon2d.wav`, `ArcShipThrusterIon3d.wav` |

Important exceptions:

- `ShipSmallEngine` has a `D2` wave only; the `D3` wave is commented out as `ArcEmpty3d.wav`.
- `ShipSmallThrusterHydrogenIdle` resolves to a `D3` idle file in the parsed XML, with the earlier generic hydro no-power pair appearing commented or structurally unusual in the file.
- `ShipThrusterHydrogenPush` has a `D2` wave only; its `D3` wave is commented out.
- `ShipSmallThrusterHydroPush` has a `D2` wave only.
- `ShipThrusterPrototechPush` has a `D2` wave only.

Design implication:

The 2D files are not throwaway duplicates. They are likely authored as cockpit/listener-space beds or interior-friendly equivalents. The 3D files should be the default source material for physically located exterior emitters, while the 2D files should be treated as a separate interior layer rather than ignored.

## Block-Level Thruster Primary Sounds

`CubeBlocks_Thrusters.sbc` confirms individual thruster block definitions still have `PrimarySound` values, and every base thruster inspected has:

- `SilenceableByShipSoundSystem=true`
- a `ThrusterType` of `Ion`, `Hydrogen`, or `Atmospheric`
- a `ForceMagnitude`
- a block-level `PrimarySound`

Examples:

| Block subtype | Type | Force | PrimarySound |
| --- | --- | ---: | --- |
| `SmallBlockSmallThrust` | Ion | 14400 | `SmShipSmJet` |
| `SmallBlockLargeThrust` | Ion | 172800 | `SmShipLrgJet` |
| `LargeBlockSmallThrust` | Ion | 345600 | `LrgShipSmJet` |
| `LargeBlockLargeThrust` | Ion | 4320000 | `LrgShipLrgJet` |
| `LargeBlockLargeHydrogenThrust` | Hydrogen | 7200000 | `LrgShipLrgJetHydrogen` |
| `LargeBlockSmallHydrogenThrust` | Hydrogen | 1080000 | `LrgShipSmJetHydrogen` |
| `SmallBlockLargeHydrogenThrust` | Hydrogen | 480000 | `SmShipLrgJetHydrogen` |
| `SmallBlockSmallHydrogenThrust` | Hydrogen | 98400 | `SmShipSmJetHydrogen` |
| `LargeBlockLargeAtmosphericThrust` | Atmospheric | 6480000 | `LrgShipSmJetAtmo` |
| `LargeBlockSmallAtmosphericThrust` | Atmospheric | 648000 | `LrgShipSmJetAtmo` |
| `SmallBlockLargeAtmosphericThrust` | Atmospheric | 576000 | `SmShipLrgJetAtmo` |
| `SmallBlockSmallAtmosphericThrust` | Atmospheric | 96000 | `SmShipSmJetAtmo` |

`Audio.sbc` defines matching `Arc...` and sometimes `Real...` audio definitions for those primary sound names. For example:

- `SmShipSmJet` corresponds to `ArcSmShipSmJet` and `RealSmShipSmJet`.
- `SmShipLrgJet` corresponds to `ArcSmShipLrgJet` and `RealSmShipLrgJet`.
- `LrgShipSmJet` corresponds to `ArcLrgShipSmJet`.
- `LrgShipLrgJet` corresponds to `ArcLrgShipLrgJet`.
- Hydrogen and atmospheric variants are mostly 3D loop definitions under `Arc...Hydrogen` and `Arc...Atmo`.

Design implication:

Vanilla has two related layers:

- block-level thruster primary sounds, which are silenceable by the ship sound system
- grid-level ship sound groups, which synthesize ship-scale run/thruster/transition loops

RSP V2 should not blindly use both at full volume. It should choose roles:

- block-level primary sounds or ship-system thruster 3D cues can supply close exterior nozzle detail
- ship-system run loops and 2D variants can supply interior/cockpit mass and bed layers
- vanilla grid-center ship sound emitters should be suppressed once RSP owns the replacement route

## Proposed RSP V2 Architecture

### `AudioWorldState`

Collects player/listener state once per frame or at a fixed cadence:

- camera/listener position
- controlled object and controlled grid
- cockpit/seat state
- inside ship/station signal from vanilla where available
- atmosphere density at listener and source
- likely room/enclosure state
- realistic audio mode assumptions
- helmet/cockpit state where accessible

### `GridAudioModel`

One model per relevant grid:

- grid entity id
- mass/size/sound group selection
- speed normalized against vanilla `FullSpeed=96`
- active thruster blocks
- active force by type: ion, hydrogen, atmospheric, prototech
- active force by direction
- six weighted thrust-direction source positions
- atmospheric availability at grid/source
- wheel state later, if desired

### `CueCatalog`

A static catalog of selected vanilla cues and their intended RSP roles:

- six-direction engine detail cues
- six-direction state-machine cues
- outside 3D state-machine variants
- inside 2D/local state-machine variants
- start/end/spool transition cues
- fallback cues for missing 2D or missing 3D variants

The catalog should explicitly distinguish confirmed vanilla role from RSP role. That way the mod can intentionally repurpose a beautiful 2D file without confusing that with vanilla behavior.

### `EmitterPool`

Owns all RSP-created emitters:

- keyed by grid id, layer, cue, thruster id, direction, or virtual interior source
- handles creation, preload, update, fade, stop, and cleanup
- avoids unbounded emitter creation
- stores base gains and current smoothed state
- records diagnostics

### `AudioStateMachines`

Owns replacement behavior instead of relying on vanilla grid-center behavior:

- `SixDirectionSourceModel`: computes the six weighted source positions shared by engine-detail and state-machine audio
- `EngineDetailLayer`: basic positional engine/thruster loops using 3D files by default and shared interior filtering when inside
- `EngineStateLayer`: vanilla-style run/idle/start/end/speed-up/speed-down state-machine audio using 3D variants outside and matching 2D/local variants inside
- `TransitionLayer`: start/end/speed-up/speed-down/push cues routed through the same six-position model
- `VanillaSuppressionLayer`: blocks only known centered cues while replacement is active; it must allow vanilla audio in fallback exterior/contactless states

### `TransmissionAndFilters`

Centralizes all gain/filter decisions:

- distance falloff
- atmosphere/vacuum propagation
- source inside vs outside
- listener inside vs outside
- room/enclosure transmission
- cockpit/helmet low-pass selection
- hull muffling
- per-layer gain curves

## Interior Audio Direction

The first implementation should keep the interior model tied to the same six positional source groups as exterior audio. This gives the player a consistent sense of where thrust is happening without creating one emitter per real thruster.

Base interior behavior:

- `EngineDetail` keeps using 3D detail files and receives the shared deep interior/hull filter.
- `EngineState` switches from 3D state-machine files to matching 2D/local variants when the camera/listener is inside the ship.
- `EngineState` 2D/local variants do not receive the shared deep filter.
- seated and walking-inside states use the same RSP listener model, avoiding the vanilla cockpit/seat sound jump.
- source positions remain the same six weighted direction groups for diagnostics and proximity logic.

Candidate tests:

1. 2D/local state-machine files played from six logical grouped emitters:
   - preferred base test
   - keeps the state-machine layout scalable
   - lets us hear whether the authored 2D ambience works when tied to directional source groups

2. 3D state-machine files inside with deep filter:
   - fallback or comparison route
   - likely useful for hull-conducted exterior sound
   - should be filter-controlled as part of the shared 3D engine ambience

3. Forced 3D playback of D2 files:
   - required experiment for V2
   - force selected 2D/local state-machine files through positional emitters
   - compare against normal 2D/local playback and filtered 3D playback
   - may sound wrong if the 2D files are strongly stereo/listener-authored
   - useful to test before ruling out localized interior stereo behavior

## Simple Debug UI

The V2 debug UI should stay small and practical. Previous builds proved that a detailed overlay can become noisy quickly, so the first pass should show only the information needed to validate source placement, cue selection, filtering, and gain.

Required debug visuals:

- 3D debug circles at the six `EngineDetail` source positions.
- 3D debug circles at the six `EngineState` source positions.
- Use two distinct colors so basic positional engine audio and state-machine audio can be distinguished at a glance.
- Keep circle size stable and avoid labels unless a short toggle is added later.

Required centered audio list:

- currently playing RSP-controlled cue/file names
- route/layer name: `detail`, `state-3d`, `state-2d`, `transition`, or `vanilla-muted`
- source group direction
- 2D/3D route mode
- filter/effect applied by RSP, if any
- final volume or gain multiplier controlled by RSP
- transmission/filter strength value controlled by RSP
- inside/outside listener state
- current vanilla room/enclosure identity for the player/listener position, if available

Required debug/tuning controls:

- `detail on|off`: toggles basic 3D engine/detail audio.
- `state on|off`: toggles state-machine emitter audio.
- `detailgain <value>`: gain for basic 3D engine/detail audio.
- `stategain <value>`: gain for state-machine emitter audio.
- `dist <meters>`: shared total distance over which both `EngineDetail` and `EngineState` RSP emitter volume fades from full to silent.
- `distcurve <value>`: shared curve shaping for both `EngineDetail` and `EngineState` inside the `dist` range while still respecting the same maximum distance. Values below neutral should hold volume longer before dropping; values above neutral should taper earlier. The implementation should start as a simple exponent curve and can later add a bell/S-curve mode if listening tests need it.
- `state2dpos on|off`: toggles the required test mode that forces selected 2D/local state-machine files through positional emitters.
- per-direction layer toggles, for example `detail forward off` or `state up on`, once the six source groups are visible and stable.
- per-direction layer gains, for example `detailgain forward 0.5` or `stategain back 1.25`, for balancing and diagnosing individual thrust groups.

The first control pass can apply toggles and gains globally per layer, then add per-direction overrides as soon as group placement is validated.

Debug non-goals:

- no large multi-page overlay
- no attempt to list every vanilla sound if RSP is not touching it
- no complex in-game editor until the base routing is stable

## First Milestones

### Milestone 1: Definition Map And Catalog

- Add a `CueCatalog` with the confirmed vanilla ship sound group roles.
- Add RSP role annotations for each cue.
- Add diagnostic output that shows selected group, selected cues, and source state.
- Incorporate `SE RSP Sound Organization.xlsx` as the initial user-intent cue catalog, with confirmed vanilla roles beside the workbook descriptions.

### Milestone 2: Six-Source Grid Model

- Build the six weighted thrust-direction source positions.
- Use produced thrust and max force to weight each direction group.
- Cap normal runtime to six active source groups per relevant grid.
- Draw simple 3D debug circles at each source position.
- Add the shared `dist` and `distcurve` controls for both `EngineDetail` and `EngineState` emitter falloff.

### Milestone 3: Engine Detail Layer

- Route basic positional engine/thruster detail through the six source groups.
- Use selected 3D files inside and outside at first.
- Apply the shared deep interior/hull filter when the camera/listener is inside the ship.
- Preserve vanilla realistic-audio vacuum behavior where possible.
- Add `detail` toggle and `detailgain`, with per-direction overrides after placement is stable.

### Milestone 4: Engine State Layer

- Route vanilla-style state-machine audio through the same six source groups.
- Use confirmed vanilla 3D state-machine files outside.
- Switch to matching 2D/local variants when the camera/listener is inside.
- Do not apply the shared deep filter to inside 2D/local state-machine variants.
- Keep start/end/speed-up/speed-down/push cues separate in the catalog.
- Add `state` toggle and `stategain`, with per-direction overrides after placement is stable.
- Add `state2dpos` test mode for forcing selected 2D/local files through positional emitters.

### Milestone 5: Listener Environment And Seat Consistency

- Replace one-off inside/cockpit checks with a listener/source/environment model.
- Track listener pressure, source pressure, controlled grid pressure, inside-room state, and ship enclosure.
- Surface the current vanilla room/enclosure identity in the debug readout and use the same signal to drive RSP listener-state decisions where possible.
- Ensure seated and walking-inside states produce the same RSP-controlled audio environment.
- Detect exterior/contactless listener states and fall back to stock vanilla ship engine audio.
- Ensure vanilla centered ship audio suppression is disabled whenever fallback is active.
- Decide how each base layer responds to vacuum, atmosphere, cockpit, helmet, and hull separation.

## Open Questions

- Does vanilla select `Small` vs `Large` sound groups by mass, grid size, or both when both groups allow both grid sizes?
- Does vanilla treat push cues as looped emitters with manual stop, or as effectively short one-shot loops?
- Which private fields in `MyShipSoundComponent` map to the vanilla emitter indexes currently patched?
- Can we cleanly access the vanilla `MyShipSoundComponent` selected sound group at runtime, or should RSP classify independently?
- What exact API/private field exposes the vanilla room/enclosure name or id for the current player position?
- Do D2 files sound acceptable when forced through 3D emitters, or should they remain listener-space/interior-only?
- What is the best interior model for engine-room proximity: true 3D detail, proximity-weighted 2D bed, or both?
- Is the workbook entry `ArcShipThrusterAtmoNoPower2d.3.wav` a typo, an unlisted loose audio file, or a desired alternate local atmo idle candidate?
- Should `distcurve` stay as a simple exponent, or should it become a selectable curve family such as exponent, S-curve, or bell-shaped taper?

## Non-Goals For The First Refactor Pass

- Rewriting weapon/explosion muffling beyond preserving existing behavior.
- Fully replacing wheel audio.
- Adding a dedicated speed/wind/rattle/high-g atmospheric layer. Atmosphere still informs filtering/transmission, but extra flight-speed ambience should wait until the base engine system is stable.
- Supporting every DLC/prototech nuance before the base thruster model is stable.
- Preserving every old `/rsp` tuning command if a smaller V2 command surface is clearer.
- Chasing exact vanilla parity. V2 should use vanilla facts as source material, then make deliberate RSP choices.
