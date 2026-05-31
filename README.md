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
