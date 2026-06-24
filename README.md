# Realistic Sound Plus

This project started off with the original goal of fixing some of the most eggregious audio bugs foound with the base game's realistic sound mode but has since expanded into many other areas to provide an much more immersive audio experience in se1.

Main 3 areas:
- thruster sound realism improvements: sound dmpening through ship walls and simulation of sound transmission of thrusters through atmosphere with varying pressure and through hull/structural probagation.

- progressive environmental audio muffling inside structures on planets: ambient enviromental audio effects no longer cut off when occlusion reached a binary on/off value, it instead persists and becomes increasingly more muffled, low passed, and volume reduced depending on the amount of structure between the player and the outside world - decending from the surface into a deep base gradually darkens external wind/rain/other sounds for a much more immersive on planet audio experience.

- in-game realistic reverb: a custom DSP inline/master reverb wet bus is being added which produces reverb for all sounds. Reverb parameters are driven by spherical raycast hit data aroound the player and constantly update to produce a realistic reverb audio enviroment greatly adding immersion to the player experience when inside structures.  





git pull test
Client-side Pulsar plugin for Space Engineers realistic audio mode.

## Branch Status

This branch is the V2 ship-audio rebuild: `v2/live-audio-engine`.

V2 is now the active ship-engine audio route on this branch. There is no `/rsp v2` command and no old `/rsp spatial` route running beside it.

## Current Test Build

The current build creates a replacement ship engine soundscape for listener states where RSP should own the ship audio:

- V2 takes over while the listener/camera is inside a ship, close to a controlled ship seat/cockpit, controlling a ship from an outside/third-person camera, or physically standing/walking on a ship grid.
- Controlled third-person/outside-camera ship states use the same six-direction V2 detail route with exterior/D3 source material.
- Free exterior fallback states such as flying/falling/jumping near a ship currently leave stock vanilla ship audio alone so Keen's realistic-audio vacuum/contact rules remain in charge.
- Each relevant grid can create up to six grouped engine-detail emitters, one for each thrust direction.
- Each relevant grid can create up to six grouped engine-state emitters using the same directional positions.
- Detail emitters use vanilla ship sound group thruster cues by detected thruster type, with idle cue fallback when a direction has engines but no thrust command.
- Inside detail emitters can force Keen's paired D2/local cue variants through the same six positional emitters. If an inside D2/local variant is not explicitly mapped, that detail cue is silent for easier debugging.
- Detail intensity prefers the per-thruster `ThrustOverridePercentage` signal when it is nonzero. During direct ship-control input, V2 uses analog movement input for a linear 0-100% response. With no ship-control input, V2 can fall back to actual `CurrentThrustPercentage` so inertial dampener thrust has an audio path.
- Detail emitters fall back to vanilla thruster block `PrimarySound` cues if a thruster type cannot be classified.
- State emitters use confirmed vanilla ship sound group run-loop cues, classified as small/large by grid mass where available.
- Inside state emitters force Keen's paired D2/local cue variant while still playing from the six directional emitter positions.
- V2-created engine emitters can use the dynamic `enginefilter`, which calculates a separate low-pass cutoff/Q for each emitter from listener distance, atmosphere, inside/contact state, and the air/hull path controls.
- The menu now separates engine audio controls, engine-filter controls/readouts, and preliminary player/aux filter telemetry into distinct sections.
- The player/aux section now applies a first-pass direct `auxfilter` to classified non-engine voices: environment/weather, physical block/world emitters, and player-local cues.
- Exterior detail emitters use `/rsp externalfilter`; inside/local detail and state emitters use `/rsp internalfilter`.
- When V2 owns the current ship soundscape, confirmed vanilla ship-state cues are suppressed so the replacement emitters are not hidden under the stock centered mix.

Debug marker colors:

- Cyan spheres: V2 engine-detail source groups.
- Orange spheres: V2 engine-state source groups.

## Quick Test Flow

1. Load the local Pulsar plugin build.
2. Enter a powered ship with working thrusters.
3. Run `/rsp sounds on`.
4. Check the vertical route/status block at the top of the source list.
5. Run `/rsp menu` if you want clickable sliders, toggles, and filter dropdowns.
6. Run `/rsp filters on` for the separate filter-controller overlay.
7. Run `/rsp catalog` after moving through a test area to print/log the unique session sounds RSP has seen.
8. Toggle `/rsp detail off` and `/rsp state off` independently to isolate each layer.
9. Tune `/rsp dist`, `/rsp distcurve`, `/rsp detailgain`, and `/rsp stategain` while listening from inside the ship.

The settings menu is organized as `Engine Audio`, `Engine Filter`, and `Player / Aux Filter`. It includes live response charts for `enginefilter` and `auxfilter`. `enginefilter` can run dynamically per emitter; the chart shows the most recent/nearest calculated engine filter curve. `auxfilter` is reserved for future non-engine/block ambience filtering. These charts are not live audio spectrum displays yet.

Each custom filter slot currently applies one XAudio source-voice filter shape at a time. Stacking low-pass, high-pass, band-pass, and notch stages on the same sound would require a different multi-stage route, such as submix/effect-chain research or extra parallel voices, and is not part of the current safe test path.

## Second Monitor Test Reference

Current live V2 test defaults:

| Setting | Default | Purpose |
| --- | ---: | --- |
| `detail` | `on` | Enables grouped 3D engine-detail emitters. |
| `idle` | `on` | Enables the detail idle layer while thrusters are powered and not firing. |
| `detail2dpos` | `on` | Uses mapped D2/local detail cue variants while inside, still through positional emitters. |
| `state` | `off` | Enables grouped ship state/run-loop emitters. |
| `gain` | `2.00` | Overall V2 engine gain applied to detail and state layers. |
| `detailgain` | `2.00` | Extra gain for 3D engine-detail layer. |
| `idlegain` | `1.00` | Extra gain for only the detail idle layer. |
| `stategain` | `2.00` | Extra gain for ship state/run-loop layer. |
| `dist` | `200` | Shared emitter hearing range in meters. |
| `distcurve` | `1.00` | Distance falloff curve inside `dist`. |
| `cmdsmooth` | `2000` | Detail command smoothing time in milliseconds. |
| `emitterfade` | `120` | Short fade after cue, dimension, filter, or route rebinds. |
| `externalfilter` | `EngineFilter` | Filter route for outside/contact V2 engine emitters. |
| `internalfilter` | `EngineFilter` | Filter route for inside/local V2 engine emitters. |
| `enginefilter` | `LowPass / dynamic on` | Per-emitter engine filter driven by distance, atmosphere, interior/contact state, and air/hull controls. |
| `engineinteriorair` | `0.35` | How strongly pressurized interior air contributes to the engine filter. Raise this if full-atmosphere interiors are too muffled. |
| `playerEnv` | `ray 120m / seal +0.30` | Wind/environment muffling probe based on local openness plus vanilla inside-room state. |
| `playerfilter` | `on` | Applies aux filtering to classified non-engine voices. |
| `auxCandidates` | readout + applied | Per-source occlusion estimate for physical non-engine audio emitters. Block voices use this to drive auxfilter. |
| `auxAtmOverride` | `off / 0.00` | Optional player/aux-only pressure simulation for block, environment, and player-local filters. |
| `auxfilter` | `LowPass / 1200 Hz / Q 0.70` | Shared player/aux filter shape. Clear cutoff is `auxfilterfreq`; muffled cutoffs are split by category: `envmufflefreq`, `blockmufflefreq`, and `auxmufflefreq` for local/player sounds. |
| `atmoverride` | `off / 0.00` | Optional test override that makes V2 atmosphere reads return the chosen pressure. |
| `reverb` | `off / diff 0.45 / room 0.35` | Experimental global XAudio reverb test. Applies broadly to game audio while enabled and restores vanilla reverb state when disabled. |
| `sounds` | `on` | Center debug overlay starts enabled on this branch. |
| `log` | `on` | V2 debug log writes once per second. |

Most useful first commands:

```text
/rsp menu
/rsp show
/rsp sounds on
/rsp logpath
/rsp dist 500
/rsp cmdsmooth 2000
/rsp emitterfade 120
/rsp gain 4
/rsp detailgain 4
/rsp detail2dpos on
/rsp externalfilter enginefilter
/rsp internalfilter enginefilter
/rsp enginefilterdynamic on
/rsp atmoverride on
/rsp externalatm 0.5
/rsp engineairnear 6500
/rsp engineairfar 800
/rsp engineinteriorair 1.0
/rsp playerenvray 120
/rsp playersealedextra 0.3
/rsp playerfilter on
/rsp envfilter on
/rsp blockfilter on
/rsp localfilter on
/rsp auxatmoverride on
/rsp auxatm 0.5
/rsp envmufflefreq 900
/rsp blockmufflefreq 120
/rsp auxmufflefreq 120
/rsp blockdistancescale 4
/rsp enginehullnear 250
/rsp enginehullfar 50
/rsp auxfiltertype lowpass
/rsp auxfilterfreq 1200
/rsp auxfilterq 0.7
/rsp reverb on
/rsp reverbdiffusion 0.45
/rsp reverbroomsize 0.35
/rsp idle off
/rsp idlegain 0.25
/rsp stategain 4
```

Overlay fields to watch:

| Field | Meaning | Good sign |
| --- | --- | --- |
| `mode` | V2 listener decision, such as `inside-seat`, `inside-room`, or fallback. | `inside-seat` or `inside-room` while testing inside. |
| `grid` | Short id of the listener grid V2 is trying to own. | Nonzero while seated, inside, or standing/walking on a ship grid. |
| `char` | Character movement state used by the on-hull contact probe. | `Standing`, `Walking`, `Running`, etc. while on the hull; `Flying`/`Falling` should fall back. |
| `contact=source/id` | Character grid contact source and short grid id. | `topgrid/...` or `relative/...` while physically on a grid. |
| `inside` | Whether V2 thinks the listener is inside the ship. | `Y` inside the ship. |
| `move` | Controlled ship movement input read from the active ship controller. | Values change while pressing thrust keys; `-` means input was unavailable. |
| `grids` | V2 grid audio models currently retained. | Greater than `0` while V2 owns a ship. |
| `groups` | Known six-direction source groups discovered from thrusters. | Greater than `0`; up to `6` for full direction coverage. |
| `known` | Cached thrusters discovered by the hook and kept for the V2 census. | Greater than `0`; should stay stable after world load. |
| `scan=used/removed` | Cached thrusters evaluated this frame, and stale cached thrusters removed. | `used` greater than `0` while inside a discovered ship. |
| `thr=patch/raw/accepted+census` | Hook hits, hook reports received, hook reports accepted, plus cached census reports. | `census` climbs while inside after discovery. |
| `rej=fallback/grid` | Reports rejected by fallback state or grid mismatch. | Low or zero while inside the controlled ship. |
| `emit=registered/unfiltered` | V2-created emitters currently registered with diagnostics/filtering. | Greater than `0` once V2 audio is actually alive. |
| `flt=hits` | Number of times the low-pass filter hook has controlled an emitter. | Climbs when V2 3D emitters are active and filterable. |
| `detail=on/gain/xN` | Detail layer toggle, gain, and active detail emitter count. | `xN` greater than `0` when thrusting or when V2 is holding silent emitters for suppression. |
| `idle=on/gain` | Detail idle layer toggle and gain. | Use `/rsp idle off` to test whether a no-thrust hum is RSP idle. |
| `detail2dpos` | Whether inside D2/local detail cues are forced through positional emitters. | Default `on`; inside unmapped cues stay silent and appear as `missing-d2` in detail routes/logs. |
| `state=off/gain/xN` | State layer toggle, gain, and active state emitter count. | Keep `off` while detail-only testing; turn on when testing state emitters. |
| `dist` | Shared hearing range. | Raise this if groups exist but no sound is heard. |
| `emitfade` | Fade time used after V2 emitter starts/rebinds. | `120` by default; raise slightly if recontact or D3/D2 transitions click. |
| `state2dpos` | Whether inside D2/local state cues are forced through positional emitters. | Default `on`; this is the intended inside state route. |

Marker colors:

| Marker | Meaning |
| --- | --- |
| Bright cyan | Active V2 engine-detail emitter. |
| Bright orange | Active V2 engine-state emitter. |
| Dim cyan/orange | Known source group, currently quiet or not actively playing. |

Cue list notes:

`UNCONTROLLED` beside a ship/engine cue means vanilla is playing that cue and RSP has not associated it with a V2-created emitter. V2-created cues should show an RSP route such as `v2-detail-*`, `v2-state-*`, or `filter`.

V2 detail routes include their source-material mode and current command source, for example `v2-detail-Down-active/d2pos/move cmd=0.40` or `v2-detail-Down-active/d3/move cmd=0.40`. `missing-d2` means the listener is inside with `detail2dpos` enabled, but that cue is not explicitly mapped to a D2/local variant, so RSP leaves that detail cue silent. `ovr` means `ThrustOverridePercentage`, `move` means analog ship-controller input, `dmp` means seated dampener/current-output thrust, and `out` means current-output thrust above the non-seated threshold. Character walking input should show `move=-` and should not drive engine-detail audio directly.

Detail active volume follows smoothed `cmd` linearly. Cue routes show `raw` for the immediate detected command and `cmd` for the post-smoothing command sent to volume, idle/firing crossfade, and pitch. Active detail attempts a linear pitch shift from `0.5` at the lowest firing command to `1.5` at full command; idle detail is not pitch-shifted. Idle detail now fades fully out as firing detail fades in.

With debug logging enabled, `detail` log lines show the current idle target, active target, distance gain, distance, and pitch target for each direction once per second.

Debug log:

```text
%APPDATA%\SpaceEngineers\RealisticSoundPlus-v2-debug.log
```

The log records global audio state, the V2 overlay line, `/rsp show` settings, current top source voices, and per-direction detail diagnostics once per second. It also writes `event=listener` route-change lines with mode, grid id, character movement state, and contact source/grid so on-hull routing issues are easier to diagnose. Use `/rsp logpath` in game to print the exact path.

## Debug Overlay

Use `/rsp sounds on|off`. The overlay is enabled by default on this live V2 test branch.

The centered overlay shows:

- Global listener state: atmosphere, altitude, controlled speed, inside state, active exterior/internal filters, and `route=v2`.
- V2 listener state: mode, vanilla room probe, inside state, active detail/state source counts, shared distance, curve, D2 positional test flags, and atmosphere.
- Current audio voices: cue name, voice count, volume score, engine-candidate marker, and RSP diagnostics when available.
- RSP diagnostics: route, transmission, scale, base volume, final multiplier, listener distance, and pressure.

Room readout note:

The `room=` value is currently a reflective probe from vanilla `MyShipSoundComponent`. If Keen does not expose a useful room name/id in that object for a given state, the overlay will report that the vanilla room id is unavailable.

## Debug Log

The V2 branch writes a lightweight debug log by default:

`%APPDATA%\SpaceEngineers\RealisticSoundPlus-v2-debug.log`

The log records one line per second with the global audio state, V2 route/debug line, current settings, top source voices, and V2 detail diagnostics. It also writes one-time `pitch-member` events showing whether the active game audio objects expose a writable pitch/frequency member. It rotates to `RealisticSoundPlus-v2-debug.log.old` at about 2 MB.

Controls:

- `/rsp log on|off` - toggles the V2 debug log.
- `/rsp logpath` - prints the current log path.

## Active Runtime Hooks

For clean V2 testing, this branch keeps the active Harmony surface intentionally narrow:

- `MyThrust.UpdateAfterSimulation` feeds thruster state into the V2 six-direction audio model.
- `MyShipSoundComponent.UpdateVolumes` reports vanilla inside/room state to the V2 listener model and overlay.
- `MyEntity3DSoundEmitter.PlaySound` and `PlaySoundWithDistance` suppress confirmed vanilla ship-state cues only while V2 owns the current ship soundscape.
- `MyEntity3DSoundEmitter.SelectEffect` applies the exterior or internal filter route only to V2-registered emitters.
- `MyCharacterBreath.Update` suppresses the vanilla breathing loop while the helmet/visor is open.
- `MyShipSoundComponent.UpdateShouldPlay2D` and `UpdateSoundDimension` prevent vanilla seat/bed/desk states from forcing a different ship-audio dimension.

Old weapon, ambient, hydrogen-engine, continuous-power, and per-thruster-spatial patches are not active on this branch.

## Runtime Controls

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running. Saved XML values are also respected on the next world load; branch defaults only apply when a setting is missing or a new config is created.

### Layer Controls

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp detail on|off` | `/rsp enginedetail` | `on` | bool | Toggles the grouped 3D engine-detail layer. |
| `/rsp idle on|off` | `/rsp detailidle` | `on` | bool | Toggles only the V2 detail idle loop. This is useful for identifying no-thrust hums without letting vanilla ship-state audio return. |
| `/rsp state on|off` | `/rsp enginestate`, `/rsp statemachine` | `off` | bool | Toggles the grouped ship state/run-loop layer. |
| `/rsp detailgain 2` | `/rsp v2detailgain` | `2.00` | `0..4` | Multiplies only the detail layer. |
| `/rsp idlegain 1` | `/rsp detailidlegain`, `/rsp v2idlegain` | `1.00` | `0..4` | Multiplies only the detail idle layer. |
| `/rsp stategain 2` | `/rsp v2stategain` | `2.00` | `0..4` | Multiplies only the state layer. |
| `/rsp detail2dpos on|off` | `/rsp detail2dposition`, `/rsp detailpositional2d` | `on` | bool | Forces mapped inside D2/local detail cues through positional emitters. Unmapped inside detail cues stay silent. |
| `/rsp state2dpos on|off` | `/rsp state2dposition`, `/rsp positional2d` | `on` | bool | Forces inside D2/local state cues through positional emitters. |

### Distance And Response

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp dist 200` | `/rsp v2dist`, `/rsp emitterdist` | `200` | `1..1000` | Shared V2 emitter range in meters. Volume reaches zero at this distance. |
| `/rsp distcurve 1` | `/rsp v2distcurve` | `1.00` | `0.1..5` | Shapes distance falloff while respecting `dist`. Lower values stay louder longer; higher values drop faster near the source. |
| `/rsp gain 2` | `/rsp enginegain` | `2.00` | `0..4` | Overall V2 engine gain for detail and state layers. |
| `/rsp statecurve 1` | `/rsp curve`, `/rsp exponent`, `/rsp outputcurve` | `1.00` | `0.25..10` | Shapes thrust output for non-detail/state layers. Detail active volume follows smoothed `cmd` linearly, so this should usually be ignored during detail-only testing. |
| `/rsp smooth 100` | `/rsp smoothing` | `100` | `0..500` ms | Volume smoothing time. Higher values fade more slowly. |
| `/rsp cmdsmooth 2000` | `/rsp commandsmooth`, `/rsp inputsmooth`, `/rsp thrustsmooth` | `2000` | `0..5000` ms | Linear input-to-detail-output slide. A value of `2000` means a full 0-to-100% keyboard command takes about two seconds to reach full detail volume and pitch. |
| `/rsp emitterfade 120` | `/rsp emitterfadein`, `/rsp transitionfade`, `/rsp routefade`, `/rsp contactfade` | `120` | `0..1000` ms | Short fade after V2 emitter starts or rebinds to a new cue, D2/D3 dimension, or filter. This is intended to hide transition pops without slowing the contact gate itself. |
| `/rsp fade 0.04` | `/rsp softfade` | `0.040` | `0.001..0.25` | Soft fade width near zero thrust output. |

### Ship Scale

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp presence 0.35` | `/rsp minpresence` | `0.35` | `0..1` | Minimum presence for smaller thrusters so small ships are not inaudible. |
| `/rsp quietlog 4` | `/rsp quietforce`, `/rsp smallforce` | `4.00` | `1..10` | `log10(force)` treated as the small/quiet end of thruster scaling. |
| `/rsp loudlog 7` | `/rsp loudforce`, `/rsp largeforce` | `7.00` | `quietlog+0.1..12` | `log10(force)` treated as the large/loud end of thruster scaling. |

### Engine Filter

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp externalfilter enginefilter` | `/rsp filter`, `/rsp extfilter` | `enginefilter` | options | Selects the exterior/contact filter route for V2 engine emitters. Options: `off`, `helmet`, `cockpit`, `cockpitnooxy`, `realship`, `deep`, `enginefilter`, `auxfilter`. Old `filter1/filter2` names still work as aliases. |
| `/rsp internalfilter enginefilter` | `/rsp intfilter`, `/rsp insidefilter` | `enginefilter` | options | Selects the inside/local filter route for V2 engine emitters. |
| `/rsp enginefilterdynamic on` | `/rsp enginedynamic`, `/rsp dynamicfilter` | `on` | bool | Makes `enginefilter` calculate a unique low-pass cutoff/Q for each emitter from distance, atmosphere, inside/contact state, and air/hull path settings. |
| `/rsp enginefiltertype lowpass` | `/rsp filter1type`, `/rsp f1type` | `lowpass` | options | Static fallback shape for `enginefilter` when dynamic mode is off. Inactive while dynamic mode is on. |
| `/rsp enginefilterfreq 300` | `/rsp filter1freq`, `/rsp f1freq` | `300` | `5..7350` Hz | Static fallback cutoff/center frequency for `enginefilter`. Inactive while dynamic mode is on. |
| `/rsp enginefilterq 0.7` | `/rsp filter1q`, `/rsp f1q` | `0.70` | `0.1..10` | Static fallback Q for `enginefilter`. Inactive while dynamic mode is on. |
| `/rsp engineairnear 6500` | `/rsp airnear` | `6500` | `5..7350` Hz | Brightest airborne cutoff for a nearby emitter in atmosphere. |
| `/rsp engineairfar 800` | `/rsp airfar` | `800` | `5..7350` Hz | Airborne cutoff at the far end of the air filter range. |
| `/rsp engineairrange 1000` | `/rsp airrange` | `1000` | `1..5000` m | Distance over which airborne high frequencies fade. |
| `/rsp engineaircurve 1` | `/rsp aircurve` | `1.00` | `0.1..5` | Curve shaping for the airborne distance-to-cutoff response. |
| `/rsp engineairq 0.75` | `/rsp airq` | `0.75` | `0.1..10` | Q used when airborne propagation dominates. |
| `/rsp engineinteriorair 0.35` | `/rsp interiorair`, `/rsp insideair`, `/rsp airblend` | `0.35` | `0..4` | Weight of the airborne path while inside the ship. Higher values make pressurized interiors less muffled. |
| `/rsp enginehullnear 250` | `/rsp hullnear` | `250` | `5..7350` Hz | Structure-borne cutoff when close to an engine path. |
| `/rsp enginehullfar 50` | `/rsp hullfar` | `50` | `5..7350` Hz | Structure-borne cutoff farther from the active engine path. |
| `/rsp enginehullrange 80` | `/rsp hullrange` | `80` | `1..1000` m | Distance over which hull/contact sound gets darker. |
| `/rsp enginehullcurve 1` | `/rsp hullcurve` | `1.00` | `0.1..5` | Curve shaping for the hull distance-to-cutoff response. |
| `/rsp enginehullq 1.15` | `/rsp hullq` | `1.15` | `0.1..10` | Q used when structure-borne hull transmission dominates. |
| `/rsp engineinteriorcutoff 700` | `/rsp interiorcutoff` | `700` | `5..7350` Hz | Maximum airborne cutoff allowed when the listener is inside the ship. |
| `/rsp enginevacuumcutoff 120` | `/rsp vacuumcutoff` | `120` | `5..7350` Hz | Low cutoff used for vacuum/contact structural transmission. |
| `/rsp atmoverride on|off` | `/rsp atmosphereoverride` | `off` | bool | Forces V2 atmosphere reads to use the test pressure below. Useful for testing atmosphere transitions without moving the ship. |
| `/rsp externalatm 0.5` | `/rsp testatm` | `0.00` | `0..1` | Test external atmosphere pressure used while override is on. |

### Player / Aux Filter

First-pass player/aux filter math:

- Environment/weather voices use the player-facing environment probe: `muffle = occlusion + remaining * vacuum`. `envfloor` only keeps the RSP-owned wind/weather bed faintly alive; it does not affect block emitters or player-local sounds.
- Block/world emitters use source-to-listener rays plus distance: `muffle = sourceOcclusion + remaining * distance + remaining * sealedExtra + remaining * vacuum`. The same resolved distance also scales block voice volume so machinery can fade out over `blockrange`.
- Player-local voices use local atmosphere only: `muffle = 1 - localAtmosphere`.
- The resulting muffling amount log-blends from `auxfilterfreq` to a category-specific muffled cutoff: `envmufflefreq` for wind/weather, `blockmufflefreq` for machinery/block emitters, and `auxmufflefreq` for player-local cues. All share `auxfilterq`.
- `auxatmoverride` and `auxatm` simulate player/aux pressure only. They do not change the engine filter's atmosphere input.

Important wind limitation: this pass filters wind/rain/weather voices only while vanilla is still playing them. If vanilla fully stops a wind voice in a room, RSP cannot make that stopped voice audible through filtering alone. Persistent muffled wind will require a later RSP-owned environment bed after we identify the correct vanilla wind/weather cue family.

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp playerfilter on|off` | `/rsp auxfilterroute` | `on` | bool | Master switch for direct player/aux filtering. Engine sounds are not routed through this. |
| `/rsp envfilter on|off` | `/rsp environmentfilter`, `/rsp windfilter` | `on` | bool | Filters wind/rain/weather-style voices when they are currently playing. |
| `/rsp blockfilter on|off` | `/rsp auxblockfilter`, `/rsp machinefilter` | `on` | bool | Filters and distance-attenuates resolved non-engine emitters such as machinery/block ambience using source path occlusion and distance. |
| `/rsp localfilter on|off` | `/rsp playerlocalfilter`, `/rsp playersoundfilter` | `on` | bool | Filters player-local cues such as footsteps/body/breathing by local atmosphere only. |
| `/rsp auxatmoverride on|off` | `/rsp auxpressureoverride`, `/rsp playerfilteratmoverride` | `off` | bool | Enables simulated pressure for player/aux filtering only. |
| `/rsp auxatm 0.5` | `/rsp auxpressure`, `/rsp auxvacuum` | `0.00` | `0..1` | Simulated player/aux pressure used while aux override is enabled. Setting this by command also enables override. |
| `/rsp playerenvray 120` | `/rsp envray`, `/rsp occlusionray` | `120` | `5..1000` m | Ray length for the player wind/environment occlusion probe. |
| `/rsp playerenvcurve 1` | `/rsp envcurve`, `/rsp occlusioncurve` | `1.00` | `0.1..5` | Curve applied to structural occlusion. Higher values make partial cover less muffled. |
| `/rsp playersealedextra 0.3` | `/rsp sealedextra`, `/rsp sealextra` | `0.30` | `0..1` | Extra muffling added when vanilla inside-room state and low open-ray fraction suggest a sealed room. |
| `/rsp playersealthreshold 0.12` | `/rsp sealthreshold`, `/rsp sealopen` | `0.12` | `0..1` | Open-ray fraction below which an inside room is treated as sealed by the preliminary probe. |
| `/rsp auxfiltertype lowpass` | `/rsp filter2type`, `/rsp f2type` | `lowpass` | options | Shape for `auxfilter`, reserved for future non-engine/block ambience routes. |
| `/rsp auxfilterfreq 1200` | `/rsp filter2freq`, `/rsp f2freq` | `1200` | `5..7350` Hz | Clear/bright aux cutoff used when calculated muffling is low. |
| `/rsp envmufflefreq 900` | `/rsp windmufflefreq`, `/rsp envmuffledfreq` | `900` | `5..7350` Hz | Dark cutoff used for wind/weather when environment muffling is strongest. |
| `/rsp blockmufflefreq 120` | `/rsp blockmuffledfreq`, `/rsp blockcutoff` | `120` | `5..7350` Hz | Dark cutoff used for block/machinery emitters when block muffling is strongest. |
| `/rsp auxmufflefreq 120` | `/rsp playerfiltermufflefreq`, `/rsp mufflefreq` | `120` | `5..7350` Hz | Dark cutoff used for player-local pressure muffling. |
| `/rsp auxfilterq 0.7` | `/rsp filter2q`, `/rsp f2q` | `0.70` | `0.1..10` | Shared aux filter Q. |
| `/rsp blockdistancescale 4` | `/rsp blockscale`, `/rsp blockdist` | `1.00` | `0.1..100` | Multiplies each vanilla block cue's own MaxDistance while preserving cue-to-cue range differences. |
| `/rsp blockrange 80` | `/rsp auxblockrange`, `/rsp playerfilterblockrange` | `80` | `1..1000` m | Fallback range only used when a block cue has no vanilla MaxDistance definition. |
| `/rsp blockcurve 1` | `/rsp auxblockcurve`, `/rsp playerfilterblockcurve` | `1.00` | `0.1..5` | Shape of block emitter volume/frequency falloff over `blockrange`. |

### Global Reverb Test

This is an experimental global XAudio route, not an enginefilter or auxfilter stage. Keen's exposed `SetReverbParameters` wrapper is a no-op in the tested build, so RSP enables the game-audio submix reverb and applies the parameter block directly. The menu's `Reverb Affected Voices` readout lists live source voices currently routed through that game-audio submix; HUD and music use separate routes.

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp reverb on|off` | `/rsp globalreverb`, `/rsp reverbtest` | `off` | bool | Toggles the global reverb experiment. When turned off, RSP attempts to restore the vanilla reverb state it captured before enabling the test. |
| `/rsp reverbdiffusion 0.45` | `/rsp reverbdiff`, `/rsp globalreverbdiff` | `0.45` | `0..1` | Direct XAudio reverb diffusion. Higher values make reflections denser/smoother. |
| `/rsp reverbroomsize 0.35` | `/rsp reverbroom`, `/rsp globalreverbroom` | `0.35` | `0..1` | Direct XAudio room-size/decay shaping. Higher values should sound larger/longer. |
| `/rsp reverbvoices` | `/rsp reverbsounds`, `/rsp reverbaffected` | n/a | n/a | Prints/logs live source voices currently routed through the game-audio submix that the global reverb effect modifies. |

### Debug And Utility

| Command | Aliases | Default | Function |
| --- | --- | ---: | --- |
| `/rsp menu` | `/rsp ui` | n/a | Toggles the in-game V2 settings menu. Runtime values apply immediately; Save writes XML. Frequency sliders use logarithmic response. |
| `/rsp show` | none | n/a | Prints current runtime settings. |
| `/rsp sounds on|off` | `/rsp audio` | `off` | Toggles centered audio overlay. Saved setting persists after `/rsp save`. |
| `/rsp filters on|off` | `/rsp filteroverlay`, `/rsp controllers` | `off` | Toggles the separate filter-controller overlay. |
| `/rsp catalog` | `/rsp soundcatalog`, `/rsp voicecatalog` | n/a | Prints/logs unique sound cues encountered this session with category guesses. |
| `/rsp log on|off` | `/rsp debuglog` | `on` | Toggles V2 file logging. |
| `/rsp logpath` | none | n/a | Prints the V2 debug log path. |
| `/rsp save` | none | n/a | Writes current settings to XML. |
| `/rsp reload` | none | n/a | Reloads settings from XML. |
| `/rsp help` | `/rsp ?` | n/a | Prints the short in-game command list. |
