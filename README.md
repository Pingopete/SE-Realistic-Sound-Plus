# Realistic Sound Plus

Client-side Pulsar plugin for Space Engineers realistic audio mode.

## Branch Status

This branch is the V2 ship-audio rebuild: `v2/live-audio-engine`.

V2 is now the active ship-engine audio route on this branch. There is no `/rsp v2` command and no old `/rsp spatial` route running beside it.

## Current Test Build

The current build creates a replacement ship engine soundscape for listener states where RSP should own the ship audio:

- V2 takes over while the listener/camera is inside a ship or seated in a ship controller.
- Exterior fallback states currently leave stock vanilla ship audio alone.
- Each relevant grid can create up to six grouped engine-detail emitters, one for each thrust direction.
- Each relevant grid can create up to six grouped engine-state emitters using the same directional positions.
- Detail emitters use vanilla thruster block `PrimarySound` cues where available.
- State emitters use confirmed vanilla ship sound group run-loop cues.
- V2-created 3D engine emitters use the shared filter/transmission path.
- Interior 2D/local state emitters are explicitly filter-exempt.

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

## Debug Overlay

Use `/rsp sounds on|off`.

The centered overlay shows:

- Global listener state: atmosphere, altitude, controlled speed, inside state, active filter, ambient toggle, and `route=v2`.
- V2 listener state: mode, vanilla room probe, inside state, active detail/state source counts, shared distance, curve, 2D positional test flag, and atmosphere.
- Current audio voices: cue name, voice count, volume score, engine/ambient candidate markers, and RSP diagnostics when available.
- RSP diagnostics: route, transmission, scale, base volume, final multiplier, listener distance, and pressure.

Room readout note:

The `room=` value is currently a reflective probe from vanilla `MyShipSoundComponent`. If Keen does not expose a useful room name/id in that object for a given state, the overlay will report that the vanilla room id is unavailable.

## Runtime Controls

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running.

Layer controls:

- `/rsp detail on|off` - toggles V2 engine-detail emitters.
- `/rsp state on|off` - toggles V2 engine-state emitters.
- `/rsp detailgain 1.0` - adjusts V2 detail emitter gain.
- `/rsp stategain 1.0` - adjusts V2 state emitter gain.
- `/rsp state2dpos on|off` - test toggle for forcing 2D/local state cues through positional emitters.

Distance and response:

- `/rsp dist 36` - shared V2 emitter distance range.
- `/rsp distcurve 1` - shared distance falloff curve within `dist`.
- `/rsp gain 1.5` - overall V2 engine gain.
- `/rsp curve 0.65` - thrust-output curve shape.
- `/rsp smooth 100` - V2 volume smoothing time in milliseconds.
- `/rsp fade 0.04` - soft fade width near zero thrust output.

Ship scale:

- `/rsp presence 0.45` - minimum thruster-size presence.
- `/rsp quietlog 4` - log10 force treated as the quiet/small end of the thruster-size scale.
- `/rsp loudlog 8` - log10 force treated as the loud/large end of the thruster-size scale.

Filtering and transmission:

- `/rsp muffling 0.7` - shared V2 exterior/interior muffling strength.
- `/rsp interior 0.9` - baseline transmission floor used by the V2 muffling model; source range is controlled by `/rsp dist` and `/rsp distcurve`.
- `/rsp atmfloor 0.5` - amount of configured muffling retained at full planetary air density while inside.
- `/rsp filter off|helmet|cockpit|cockpitnooxy|realship|deep` - V2 3D engine emitter filter mode.
- `/rsp speedfilter off|helmet|cockpit|cockpitnooxy|realship|deep` - speed/ambient filter mode.
- `/rsp ambient on|off` - ambient filter toggle.

Utility:

- `/rsp show` - prints current runtime settings.
- `/rsp save` - writes current values to XML.
- `/rsp reload` - reloads XML config from disk.
- `/rsp help` - prints the short in-game command list.
