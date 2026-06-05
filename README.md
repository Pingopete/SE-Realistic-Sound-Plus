# Realistic Sound Plus

Client-side Pulsar plugin for Space Engineers realistic audio mode.

## Branch Status

This branch is the V2 ship-audio rebuild: `v2/live-audio-engine`.

V2 is now the active ship-engine audio route on this branch. There is no `/rsp v2` command and no old `/rsp spatial` route running beside it.

## Current Test Build

The current build creates a replacement ship engine soundscape for listener states where RSP should own the ship audio:

- V2 takes over while the listener/camera is inside a ship or close to a controlled ship seat/cockpit.
- Third-person/outside-camera seat states should fall back to stock vanilla ship audio.
- Exterior fallback states currently leave stock vanilla ship audio alone.
- Each relevant grid can create up to six grouped engine-detail emitters, one for each thrust direction.
- Each relevant grid can create up to six grouped engine-state emitters using the same directional positions.
- Detail emitters use vanilla ship sound group thruster cues by detected thruster type, with idle cue fallback when a direction has engines but no thrust command.
- Detail intensity prefers the per-thruster `ThrustOverridePercentage` signal when it is nonzero. During direct movement input, V2 uses analog movement input when available, then falls back to movement-gated `CurrentThrustPercentage` for full-input states.
- Detail emitters fall back to vanilla thruster block `PrimarySound` cues if a thruster type cannot be classified.
- State emitters use confirmed vanilla ship sound group run-loop cues, classified as small/large by grid mass where available.
- Inside state emitters force Keen's paired D2/local cue variant while still playing from the six directional emitter positions; detail emitters remain 3D/filterable.
- V2-created 3D engine emitters use the shared filter/transmission path.
- Interior 2D/local state emitters are explicitly filter-exempt.
- When V2 owns the inside soundscape, confirmed vanilla ship-state cues are suppressed so the replacement emitters are not hidden under the stock centered mix.

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
| `state` | `on` | Enables grouped ship state/run-loop emitters. |
| `gain` | `2.00` | Overall V2 engine gain applied to detail and state layers. |
| `detailgain` | `2.00` | Extra gain for 3D engine-detail layer. |
| `stategain` | `2.00` | Extra gain for ship state/run-loop layer. |
| `dist` | `200` | Shared emitter hearing range in meters. |
| `distcurve` | `1.00` | Distance falloff curve inside `dist`. |
| `filter` | `Deep` | Low-pass effect for V2 3D engine emitters. |
| `sounds` | `on` | Center debug overlay starts enabled on this branch. |
| `log` | `on` | V2 debug log writes once per second. |

Most useful first commands:

```text
/rsp show
/rsp sounds on
/rsp logpath
/rsp dist 500
/rsp gain 4
/rsp detailgain 4
/rsp stategain 4
```

Overlay fields to watch:

| Field | Meaning | Good sign |
| --- | --- | --- |
| `mode` | V2 listener decision, such as `inside-seat`, `inside-room`, or fallback. | `inside-seat` or `inside-room` while testing inside. |
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
| `detail=on/gain/xN` | Detail layer toggle, gain, and active detail emitter count. | `xN` greater than `0` when thrusting. |
| `state=on/gain/xN` | State layer toggle, gain, and active state emitter count. | `xN` greater than `0` after groups are discovered. |
| `dist` | Shared hearing range. | Raise this if groups exist but no sound is heard. |
| `state2dpos` | Whether inside D2/local state cues are forced through positional emitters. | Default `on`; this is the intended inside state route. |

Marker colors:

| Marker | Meaning |
| --- | --- |
| Bright cyan | Active V2 engine-detail emitter. |
| Bright orange | Active V2 engine-state emitter. |
| Dim cyan/orange | Known source group, currently quiet or not actively playing. |

Cue list notes:

`UNCONTROLLED` beside a ship/engine cue means vanilla is playing that cue and RSP has not associated it with a V2-created emitter. V2-created cues should show an RSP route such as `v2-detail-*`, `v2-state-*`, or `filter`.

V2 detail routes include their current command source, for example `v2-detail-Down-active/move cmd=0.40`. `ovr` means `ThrustOverridePercentage`, `move` means analog movement input, and `cur` means movement-gated `CurrentThrustPercentage` for full-input states.

Debug log:

```text
%APPDATA%\SpaceEngineers\RealisticSoundPlus-v2-debug.log
```

The log records global audio state, the V2 overlay line, and `/rsp show` settings once per second. Use `/rsp logpath` in game to print the exact path.

## Debug Overlay

Use `/rsp sounds on|off`. The overlay is enabled by default on this live V2 test branch.

The centered overlay shows:

- Global listener state: atmosphere, altitude, controlled speed, inside state, active filter, and `route=v2`.
- V2 listener state: mode, vanilla room probe, inside state, active detail/state source counts, shared distance, curve, 2D positional test flag, and atmosphere.
- Current audio voices: cue name, voice count, volume score, engine-candidate marker, and RSP diagnostics when available.
- RSP diagnostics: route, transmission, scale, base volume, final multiplier, listener distance, and pressure.

Room readout note:

The `room=` value is currently a reflective probe from vanilla `MyShipSoundComponent`. If Keen does not expose a useful room name/id in that object for a given state, the overlay will report that the vanilla room id is unavailable.

## Debug Log

The V2 branch writes a lightweight debug log by default:

`%APPDATA%\SpaceEngineers\RealisticSoundPlus-v2-debug.log`

The log records one line per second with the global audio state, V2 route/debug line, and current settings. It rotates to `RealisticSoundPlus-v2-debug.log.old` at about 2 MB.

Controls:

- `/rsp log on|off` - toggles the V2 debug log.
- `/rsp logpath` - prints the current log path.

## Active Runtime Hooks

For clean V2 testing, this branch keeps the active Harmony surface intentionally narrow:

- `MyThrust.UpdateAfterSimulation` feeds thruster state into the V2 six-direction audio model.
- `MyShipSoundComponent.UpdateVolumes` reports vanilla inside/room state to the V2 listener model and overlay.
- `MyEntity3DSoundEmitter.PlaySound` and `PlaySoundWithDistance` suppress confirmed vanilla ship-state cues only while V2 owns the inside soundscape.
- `MyEntity3DSoundEmitter.SelectEffect` applies filters only to V2-registered emitters.
- `MyCharacterBreath.Update` suppresses the vanilla breathing loop while the helmet/visor is open.
- `MyShipSoundComponent.UpdateShouldPlay2D` and `UpdateSoundDimension` prevent vanilla seat/bed/desk states from forcing a different ship-audio dimension.

Old weapon, ambient, hydrogen-engine, continuous-power, and per-thruster-spatial patches are not active on this branch.

## Runtime Controls

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running.

### Layer Controls

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp detail on|off` | `/rsp enginedetail` | `on` | bool | Toggles the grouped 3D engine-detail layer. |
| `/rsp state on|off` | `/rsp enginestate`, `/rsp statemachine` | `on` | bool | Toggles the grouped ship state/run-loop layer. |
| `/rsp detailgain 2` | `/rsp v2detailgain` | `2.00` | `0..4` | Multiplies only the detail layer. |
| `/rsp stategain 2` | `/rsp v2stategain` | `2.00` | `0..4` | Multiplies only the state layer. |
| `/rsp state2dpos on|off` | `/rsp state2dposition`, `/rsp positional2d` | `on` | bool | Forces inside D2/local state cues through positional emitters. |

### Distance And Response

| Command | Aliases | Default | Range | Function |
| --- | --- | ---: | --- | --- |
| `/rsp dist 200` | `/rsp v2dist`, `/rsp emitterdist` | `200` | `1..1000` | Shared V2 emitter range in meters. Volume reaches zero at this distance. |
| `/rsp distcurve 1` | `/rsp v2distcurve` | `1.00` | `0.1..5` | Shapes distance falloff while respecting `dist`. Lower values stay louder longer; higher values drop faster near the source. |
| `/rsp gain 2` | `/rsp enginegain` | `2.00` | `0..4` | Overall V2 engine gain for detail and state layers. |
| `/rsp curve 1` | `/rsp exponent` | `1.00` | `0.25..10` | Shapes thrust output into volume. Less than `1` makes low thrust louder; greater than `1` makes low thrust quieter. |
| `/rsp smooth 100` | `/rsp smoothing` | `100` | `0..500` ms | Volume smoothing time. Higher values fade more slowly. |
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
| `/rsp filter deep` | none | `deep` | options | Selects low-pass effect for V2 3D engine emitters. Options: `off`, `helmet`, `cockpit`, `cockpitnooxy`, `realship`, `deep`. |

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
