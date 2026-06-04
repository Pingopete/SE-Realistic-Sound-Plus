# Realistic Sound Plus

Client-side Pulsar plugin for Space Engineers players using realistic audio mode.

## Current Direction

Realistic Sound Plus is being rebuilt around a V2 ship audio engine. The V2 route is the only ship-engine route in this branch; there is no `/rsp v2` opt-in command and no old per-thruster spatial route running beside it.

The current test build focuses on ships:

- Up to six grouped engine-detail emitters per relevant grid, one per thrust direction.
- Up to six grouped engine-state emitters per relevant grid, sharing the same directional grouping.
- Detail emitters use vanilla thruster block `PrimarySound` cues where available.
- State emitters use confirmed vanilla ship sound group run-loop cues.
- V2 only takes over when the listener is inside/seated according to the current listener-environment model. Exterior fallback states leave stock vanilla ship audio alone for now.
- V2-created 3D emitters use the shared filter/transmission path. Interior 2D/local state emitters are explicitly filter-exempt.

## Runtime Tuning

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running.

In-game chat commands:

- `/rsp show` - prints the current runtime settings.
- `/rsp detail on|off` - toggles V2 engine-detail emitters.
- `/rsp state on|off` - toggles V2 engine-state emitters.
- `/rsp detailgain 1.0` - adjusts V2 detail emitter gain.
- `/rsp stategain 1.0` - adjusts V2 state emitter gain.
- `/rsp dist 36` - sets the shared V2 emitter distance range.
- `/rsp distcurve 1` - shapes the shared distance falloff while respecting `dist`.
- `/rsp state2dpos on|off` - test toggle for forcing 2D/local state cues through positional emitters.
- `/rsp gain 1.5` - overall V2 engine gain.
- `/rsp curve 0.65` - thrust-output curve shape.
- `/rsp presence 0.45` - minimum thruster-size presence.
- `/rsp quietlog 4` - log10 force treated as the quiet/small end of the thruster-size scale.
- `/rsp loudlog 8` - log10 force treated as the loud/large end of the thruster-size scale.
- `/rsp muffling 0.7` - shared V2 exterior/interior muffling strength.
- `/rsp interior 0.9` - baseline interior transmission.
- `/rsp far 0.6` - far-distance transmission amount.
- `/rsp smooth 100` - V2 volume smoothing time in milliseconds.
- `/rsp fade 0.04` - soft fade width near zero thrust output.
- `/rsp atmfloor 0.5` - how much configured muffling remains at full planetary air density while inside.
- `/rsp filter off|helmet|cockpit|cockpitnooxy|realship|deep` - V2 3D engine emitter filter mode.
- `/rsp speedfilter off|helmet|cockpit|cockpitnooxy|realship|deep` - reserved speed/ambient filter mode.
- `/rsp ambient on|off` - reserved ambient filter toggle.
- `/rsp sounds on|off` - centered debug overlay with live V2 route, room probe, source counts, cue list, and emitter diagnostics.
- `/rsp save` - writes current values to XML.
- `/rsp reload` - reloads XML config from disk.
