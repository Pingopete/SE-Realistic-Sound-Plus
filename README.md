# Realistic Sound Plus

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
- V2-created 3D engine emitters use the shared filter/transmission path.
- Exterior detail emitters use `/rsp filter`; inside/local detail and state emitters use `/rsp internalfilter`.
- When V2 owns the current ship soundscape, confirmed vanilla ship-state cues are suppressed so the replacement emitters are not hidden under the stock centered mix.

Debug marker colors:

- Cyan spheres: V2 engine-detail source groups.
- Orange spheres: V2 engine-state source groups.

## Quick Test Flow

1. Load the local Pulsar plugin build.
2. Enter a powered ship with working thrusters.
3. Run `/rsp sounds on`.
4. Check that the overlay line starts with `route=v2`.
5. Toggle `/rsp detail off` and `/rsp state off` independently to isolate each layer.
6. Tune `/rsp dist`, `/rsp distcurve`, `/rsp detailgain`, and `/rsp stategain` while listening from inside the ship.

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
| `filter` | `Deep` | Low-pass effect for V2 3D engine emitters. |
| `internalfilter` | `Off` | Independent low-pass effect for inside/local detail and state emitters. |
| `filter1` | `300 Hz / Q 0.70` | Custom low-pass test filter selectable by exterior or internal route. |
| `filter2` | `1200 Hz / Q 0.70` | Second custom low-pass test filter selectable by exterior or internal route. |
| `sounds` | `on` | Center debug overlay starts enabled on this branch. |
| `log` | `on` | V2 debug log writes once per second. |

Most useful first commands:

```text
/rsp show
/rsp sounds on
/rsp logpath
/rsp dist 500
/rsp cmdsmooth 2000
/rsp emitterfade 120
/rsp gain 4
/rsp detailgain 4
/rsp detail2dpos on
/rsp filter filter1
/rsp filter1freq 300
/rsp filter1q 0.7
/rsp internalfilter off
/rsp internalfilter filter2
/rsp filter2freq 1200
/rsp filter2q 0.7
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

### Filtering And Transmission

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp muffling 1` | `/rsp muffle` | `1.00` | `0..1` | Strength of the shared V2 muffling/filter transmission model. |
| `/rsp interior 0.2` | `/rsp interiorbase` | `0.20` | `0.05..1` | Baseline transmission floor while muffled. Source range is still controlled by `dist` and `distcurve`. |
| `/rsp atmfloor 0.5` | `/rsp atmospherefloor`, `/rsp atmosphericfloor` | `0.50` | `0..1` | Amount of muffling retained at full planetary air density while inside. |
| `/rsp filter deep` | `/rsp externalfilter`, `/rsp extfilter` | `deep` | options | Selects the exterior low-pass effect for outside/on-hull V2 detail emitters. Options: `off`, `helmet`, `cockpit`, `cockpitnooxy`, `realship`, `deep`, `filter1`, `filter2`. |
| `/rsp internalfilter off` | `/rsp intfilter`, `/rsp insidefilter` | `off` | options | Selects the independent inside/local low-pass effect for inside D2/local detail and state emitters. Same options as `/rsp filter`. |
| `/rsp filter1freq 300` | `/rsp filter1frequency`, `/rsp f1freq` | `300` | `20..7350` Hz | Sets custom low-pass filter 1 cutoff frequency. Emitters using `filter1` rebind live. Values above the runtime-safe range are clamped. |
| `/rsp filter1q 0.7` | `/rsp f1q` | `0.70` | `0.1..10` | Sets custom low-pass filter 1 Q value. |
| `/rsp filter2freq 1200` | `/rsp filter2frequency`, `/rsp f2freq` | `1200` | `20..7350` Hz | Sets custom low-pass filter 2 cutoff frequency. Emitters using `filter2` rebind live. Values above the runtime-safe range are clamped. |
| `/rsp filter2q 0.7` | `/rsp f2q` | `0.70` | `0.1..10` | Sets custom low-pass filter 2 Q value. |

### Debug And Utility

| Command | Aliases | Default | Function |
| --- | --- | ---: | --- |
| `/rsp show` | none | n/a | Prints current runtime settings. |
| `/rsp sounds on|off` | `/rsp audio` | `on` | Toggles centered audio overlay. |
| `/rsp log on|off` | `/rsp debuglog` | `on` | Toggles V2 file logging. |
| `/rsp logpath` | none | n/a | Prints the V2 debug log path. |
| `/rsp save` | none | n/a | Writes current settings to XML. |
| `/rsp reload` | none | n/a | Reloads settings from XML. |
| `/rsp help` | `/rsp ?` | n/a | Prints the short in-game command list. |
