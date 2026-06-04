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
- Experimental per-thruster spatial audio can reposition individual thruster emitters at their actual block locations, scale each one by current thrust output, and suppress old grid-center ship spool/motion cues when the replacement system is active.
- Exterior weapon/explosion cues use the same vacuum, atmosphere, distance, and filter rules as exterior engine audio.

## Runtime Tuning

Settings are saved to `%APPDATA%\SpaceEngineers\RealisticSoundPlus.xml` and hot-reloaded every few seconds while the game is running. Use `/rsp save` after tuning if you want the values to persist.

### Core Commands

- `/rsp help` - prints the compact command list.
- `/rsp show` - prints the current live settings summary.
- `/rsp save` - writes current settings to XML.
- `/rsp reload` - reloads XML settings from disk and resets audio runtime state.
- `/rsp sounds [on|off]` - toggles the centered audio debug overlay. Alias: `/rsp audio`.

### Engine Loudness And Response

- `/rsp gain <0..4>` - global gain for the spatial thruster rumble layer. Default: `1.0`. This is the main rumble loudness control.
- `/rsp spatialgain <0..4>` - extra gain for the per-thruster spatial emitter layer after the normal engine curve. Default: `1.0`. Alias: `spatialemittergain`.
- `/rsp curve <0.25..10>` - exponent for thrust output to volume. Lower values make low/medium thrust audible sooner; higher values make engines stay quieter until output rises. Default: `1.0`. Alias: `exponent`.
- `/rsp control <0..1>` - blends control/autopilot demand into the audio response. `0` follows actual produced thrust only. Default: `0.0`. Alias: `controlinfluence`.
- `/rsp presence <0..1>` - minimum presence for smaller thrusters in the size scaling curve. Default: `0.35`. Aliases: `minpresence`.
- `/rsp quietlog <1..10>` - log10 thrust-force point treated as the quiet/small end. Default: `4.0`. Aliases: `quietforce`, `smallforce`.
- `/rsp loudlog <quietlog+0.1..12>` - log10 thrust-force point treated as the loud/large end. Default: `7.0`. Aliases: `loudforce`, `largeforce`.

### Muffling, Distance, And Atmosphere

- `/rsp muffling <0..1>` - strength of the extra RSP muffling/filter system. `0` disables extra muffling, `1` is full configured muffling. Default: `1.0`. Alias: `muffle`.
- `/rsp interior <0.05..1>` - baseline interior transmission when muffling is fully active. Lower is quieter/more muffled inside. Default: `0.20`. Alias: `interiorbase`.
- `/rsp near <0..far>` - distance in meters where distance attenuation starts. Default: `4.0`. Alias: `neardistance`.
- `/rsp far <0.1..500>` - distance in meters where distance attenuation reaches `fartransmission`. Default: `36.0`. Aliases: `range`, `distance`, `fardistance`.
- `/rsp fartransmission <0..1>` - volume multiplier at or beyond `far`. `0` allows complete distance fade, `1` disables distance fade. Default: `1.0`. Alias: `farvolume`.
- `/rsp atmfloor <0..1>` - how much of the configured muffling remains at full atmosphere while the listener is inside a ship. Default: `0.5`. Outside in full atmosphere, extra vacuum muffling fades to zero. Aliases: `atmospherefloor`, `atmosphericfloor`.

Distance attenuation is now a true gain layer shared by spatial rumble, directional spool, exterior weapon/explosion audio, and other routed exterior sounds. A hard range test is `/rsp near 0`, `/rsp far 1`, `/rsp fartransmission 0`.

### Filter Modes

- `/rsp filter off` - leaves vanilla effect selection unchanged for engine/exterior audio.
- `/rsp filter helmet` - uses Keen's `LowPassHelmet` effect.
- `/rsp filter cockpit` - uses Keen's `LowPassCockpit` effect.
- `/rsp filter cockpitnooxy` - uses Keen's `LowPassCockpitNoOxy` effect.
- `/rsp filter realship` - uses Keen's `realShipFilter` effect.
- `/rsp filter deep` - uses Keen's `LowPassNoHelmetNoOxy` effect.

Aliases: `none` -> `off`, `light` -> `helmet`, `medium` -> `cockpit`, `nooxy` or `heavy` -> `cockpitnooxy`, `ship` -> `realship`, `lowpass` -> `deep`.

### Spatial And Spool Systems

- `/rsp spatial <on|off>` - enables per-thruster spatial rumble. Default: `on`. Each thruster emitter is forced 3D, positioned at the block, scaled by current thrust output, and routed through the shared gain, curve, muffling, filter, atmosphere, and distance systems.
- `/rsp spool <on|off>` - enables experimental six-direction spool emitters. Default: `off`. Aliases: `dirspool`, `directionalspool`.
- `/rsp spoolgain <0..20>` - gain for directional spool loop and transition cues. Default: `0.35`. Aliases: `dirspoolgain`, `directionalspoolgain`.
- `/rsp smooth <0..500>` - de-click smoothing time in milliseconds for spatial volume changes. Default: `100`. Aliases: `smoothing`, `spatialsmooth`.
- `/rsp fade <0.001..0.25>` - soft fade width near zero thrust output. Default: `0.04`. Aliases: `softfade`, `spatialfade`.
- `/rsp spatialcenter <0..1>` - legacy XML compatibility setting. Current builds suppress the old centered spool layer rather than blending it as the source of truth. Aliases: `spatialcentral`, `spatialblend`.

Directional spool uses one averaged emitter for each thrust direction on a grid, up to six active direction groups. Large-grid cues are `ShipLargeRunLoop`, `ShipLargeSpeedUp`, and `ShipLargeSpeedDown`. Small-grid cues are `ShipSmallRunLoop`, `ShipSmallSpeedUp`, and `ShipSmallSpeedDown`.

### Ambient And Speed Wind

- `/rsp ambient <on|off>` - enables the controlled ambient pass. Default: `off`. When off, the speed-wind cue is still suppressed in vacuum so it does not rattle in space.
- `/rsp speedfilter <mode>` - selects the filter used for controlled speed-wind ambience when ambient is enabled. Alias: `ambientfilter`.

The current speed-wind cue is `ShipLargeEngine`. Its volume is `ship speed / world max ship speed * atmospheric density`, so it fades out at low speed and in vacuum.

### Debug Overlay Definitions

`/rsp sounds` displays currently playing source voices plus RSP virtual emitters.

- `type` - `S` sound, `M` music, `H` HUD, `R` RSP virtual/debug emitter.
- `eng` - cue matches the current engine/exterior-audio classifier.
- `amb` - cue matches the current ambient classifier.
- `count` - number of matching active source voices.
- `volume` - average source voice volume times volume multiplier.
- `route` - RSP path that last touched the cue, such as `spatial`, `dirspool`, `filter`, `speedwind`, `weapon`, or `center-spool-muted`.
- `tr` - final transmission multiplier from distance, atmosphere, and muffling.
- `sc` - source scale before final multiplication.
- `base` - base volume/multiplier before the final route adjustment.
- `fin` - final volume/multiplier applied by the route.
- `d` - distance from camera/listener to the recorded source position.
- `p` - maximum atmospheric pressure at listener/source positions.
- `UNCONTROLLED` - the cue is recognized as an engine candidate but has not been routed by an RSP patch on that frame.

## Active Sound Definitions

### Engine Candidates

Engine filtering/routing currently recognizes:

- Any cue starting with `ArcBlockHydrogenEngine`.
- Any cue containing `HydrogenEngine`.
- Any cue containing `JetHydrogen`.
- Any cue containing both `Hydrogen` and `Ship`.
- Any cue containing `Thruster` or the misspelled `Thuster`.
- Exact cues `ShipLargeEngine` and `ShipSmallEngine`.
- Ship motion/spool cues listed below.
- Emitters attached to `MyThrust` blocks.
- Emitters attached to `MyHydrogenEngine` blocks.
- Emitters already marked by the hydrogen engine patch or ship interior thruster patch.

### Centered Ship Spool Cues

These are treated as old vanilla grid-center ship spool/motion cues and are suppressed or replaced when the spatial/spool system is active:

- `ShipLargeRunLoop`
- `ShipLargeSpeedUp`
- `ShipLargeSpeedDown`
- `ShipSmallRunLoop`
- `ShipSmallRunSlow`
- `ShipSmallRunMedium`
- `ShipSmallRunFast`
- `ShipSmallSpeedUp`
- `ShipSmallSpeedDown`

### Ship Motion Cues

These cues are part of the broader engine/ship-motion family:

- `ShipLargeIdle`
- `ShipLargeRunLoop`
- `ShipLargeSpeedUp`
- `ShipLargeSpeedDown`
- `ShipSmallRunLoop`
- `ShipSmallRunSlow`
- `ShipSmallRunMedium`
- `ShipSmallRunFast`
- `ShipSmallSpeedUp`
- `ShipSmallSpeedDown`

### Ambient Candidates

Ambient classification currently includes ship motion cues plus:

- `ArcBlockMedical`
- `ArcBlockAirVentIdle`
- `BlockOxyGenIdle`
- `ArcBlockGravityGen`
- Any cue containing `Medical`, `AirVent`, `OxyGen`, `OxygenGenerator`, or `GravityGen`.

### Exterior Weapon And Explosion Candidates

Exterior weapon/explosion muffling currently recognizes cues that start with `ArcWep` or `RealWep`, or contain:

- `Missile`
- `Gatling`
- `Autocannon`
- `Railgun`
- `Calibre`
- `Warhead`
- `Explosion`
- `Expl`

Cues containing `NoAmmo` or `Reload` are ignored by this weapon classifier. Non-explosion weapon cues must come from a cube block emitter; explosion, `Expl`, and `Warhead` cues can be routed without that cube-block check.
