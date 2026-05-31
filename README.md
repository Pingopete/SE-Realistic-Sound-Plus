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
- Interior thruster muffling applies an extra distance-based transmission reduction to active thruster emitters when the listener is inside the ship. Optional ambient muffling can also include ship motion loops and selected interior block ambience.
- Cockpit/control-seat mode is forced to keep ship-engine emitters spatial instead of switching to vanilla louder 2D ship audio.
- Experimental per-thruster spatial audio can reposition individual thruster emitters at their actual block locations, scale each one by its current thrust output, and reduce the old grid-center thruster layer with a blend control.
- Exterior weapon/explosion cues use the same vacuum, atmosphere, distance, and filter rules as exterior engine audio.
## Runtime Tuning

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running.

In-game chat commands:

- `/rsp show` - prints the current runtime settings.
- `/rsp gain 1.5` - scales the overall thruster/engine loudness after the thrust curve is calculated. This is the main control for making active engine thrust louder or quieter while keeping the same curve and muffling behavior.
- `/rsp muffling 0.7` - controls extra interior muffling for thruster-family emitters only. `0` disables extra engine/ambient low-pass override and interior attenuation for matched cues; `1` is maximum extra muffling. Values are clamped to `0..1`.
- `/rsp curve 0.65` - changes the shape of the thrust-to-volume curve. Lower values make low and medium thrust become audible sooner; higher values keep engines quieter until thrust output is higher.
- `/rsp control 0.4` - blends player/autopilot control demand into the audio response. `0` follows only actual produced thrust; higher values make the sound react more immediately to input while still being constrained by real thrust output.
- `/rsp presence 0.45` - sets the minimum ship-size presence. Higher values make small ships less quiet relative to large ships; lower values make tiny ships more subtle.
- `/rsp interior 0.9` - sets the baseline interior transmission for thruster muffling. Higher values are less muffled/louder inside; lower values are more muffled/quieter inside.
- `/rsp far 0.6` - sets how much thruster sound transmits at far interior distances. Higher values keep distant engines louder/clearer; lower values reduce distant engines more strongly.
- `/rsp ambient on` - also applies the current muffling/filter behavior to ambient ship-motion and interior block loops currently identified as ship rattle, medical bay, air vent, oxygen generator, and gravity generator audio. Use `/rsp ambient off` to leave those ambience cues vanilla.
- `/rsp spatial on` - enables experimental per-thruster spatial audio. Each thruster emitter is forced 3D, moved to that thruster block location, scaled by that thruster's actual output, and passed through the same XML-backed gain, curve, muffling, distance, and filter settings.
- `/rsp spatialgain 1.2` - adjusts only the individual thruster emitter layer after the normal engine gain/curve. Raise this if the new localized thrusters are too subtle; lower it if individual nozzles dominate the mix.
- `/rsp spatialcenter 0.25` - controls how much of Keen's original grid-center thruster layer remains while spatial audio is enabled. `0` removes the centered layer; `1` leaves it at the normal muffled level.
- `/rsp atmfloor 0.5` - sets how much of the configured muffling remains at full planetary air density while the listener is inside a ship. `0.5` means full atmosphere cuts the extra exterior muffling strength in half inside; outside in full atmosphere, the extra muffling fades completely to zero. Vacuum still uses the full configured muffling value.
- `/rsp save` - writes the current values to the XML config.
- `/rsp filter off` - leaves vanilla effect selection unchanged for thruster sounds. Filter modes target grouped ship thruster audio, hydrogen jet cues, hydrogen engine block emitters, and individual thruster block emitters.
- `/rsp filter helmet` - forces Keen's `LowPassHelmet` effect on known thruster emitters. This is the lightest low-pass test mode.
- `/rsp filter cockpit` - forces Keen's `LowPassCockpit` effect on known thruster emitters. This is the first recommended muffling test mode.
- `/rsp filter cockpitnooxy` - forces Keen's `LowPassCockpitNoOxy` effect on known thruster emitters. This is a heavier cockpit low-pass.
- `/rsp filter realship` - forces Keen's `realShipFilter` effect on known thruster emitters. This is very muffled and uses a 300 Hz low-pass.
- `/rsp filter deep` - forces Keen's immediate `LowPassNoHelmetNoOxy` effect on known engine emitters. This is the most aggressive/deep test mode.
- `/rsp reload` - reloads the XML config from disk.

- `/rsp sounds` - toggles a centered live overlay of currently playing audio cue names, grouped by sound/music/HUD source voices. The `eng` and `amb` columns mark engine-filter and ambient-muffling candidates. Use `/rsp sounds off` to hide it.
