# Realistic Sound Plus

Client-side Pulsar plugin for Space Engineers players using realistic audio mode.

## Goals

- Keep vanilla realistic audio and vacuum-silence behavior as the baseline.
- Make ship/thruster audio intensity follow actual thrust output instead of a binary on/off value.
- Prevent seated cockpit/control-seat audio from jumping to louder, less filtered ship sounds.
- Add conservative muffling rules for interior ship audio after the core thrust behavior is stable.

## First Milestone

The first implementation should be intentionally small:

1. Load through Pulsar as a local plugin.
2. Log plugin startup and current configuration.
3. Patch `Sandbox.Game.EntityComponents.MyShipSoundComponent.UpdateSpeedBasedShipSound`.
4. Replace Keen's coarse `m_shipCurrentPowerTarget` with a continuous thrust scalar.
5. Preserve realistic-mode and vacuum behavior by only adjusting ship sounds vanilla already allows to play.
## Current Test Build

- Ship engine power now blends actual final thrust with control/autopilot demand.
- Overall engine presence is scaled by available thrust so very small ships should not sound as large as heavy ships at the same throttle percentage.
- Interior ship-engine muffling applies an extra distance-based transmission reduction to vanilla ship/thruster emitters when the listener is inside the ship.
- Cockpit/control-seat mode is forced to keep ship-engine emitters spatial instead of switching to vanilla louder 2D ship audio.
## Runtime Tuning

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running.

In-game chat commands:

- `/rsp show`
- `/rsp gain 1.5`
- `/rsp muffling 0.7`
- `/rsp curve 0.65`
- `/rsp control 0.4`
- `/rsp presence 0.45`
- `/rsp interior 0.9`
- `/rsp far 0.6`
- `/rsp save`
- `/rsp reload`
