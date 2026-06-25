<!-- Generated 2026-06-24 by the rsp-deep-context workflow (run wf_636fb3e7-79d): 24 agents over decompiled VRage.Audio/SharpDX handles + online API research. Machine-generated context; curate as needed. -->

# Realistic Sound Plus — Audio Engine Extension Reference

> **Scope.** A single consolidated reference for extending the RSP audio engine. Covers (A) every reachable game-engine audio handle by type with [USED]/[UNUSED] markers, (B) how the inline DSP filter works today, (C) a prioritised catalogue of future features, and (D) annotated online references.
>
> **Hard environment facts (load-bearing across the whole document):**
> - SE constructs its engine as `new XAudio2(XAudio2Version.Version27)` → **XAudio2 2.7** (DirectX-SDK era), via **SharpDX.XAudio2 4.0.1** (`$(GameBin)\SharpDX.dll`, `SharpDX.XAudio2.dll`; `RealisticSoundPlus.csproj` lines 41–46), on **.NET Framework 4.x / x64**.
> - **2.7 forbids:** `FilterType.LowPassOnePoleFilter`/`HighPassOnePoleFilter`, reverb `SideDelay`, reverb `DisableLateField`. Stick to the four classic filter types and the 2.7-valid reverb fields.
> - Engine reverb (`EnableReverb`/`ApplyReverb` + cue-bank `Reverb`) is **permanently inert above `MyAudio.MAX_SAMPLE_RATE = 48000`** (the XAPO limitation); this is why RSP builds its own `Fx.Reverb` + `SubmixVoice` wet bus.
> - `MyXAudio2.SetReverbParameters(diffusion, roomSize)` is an **empty no-op stub** (single `ret`, IL `0x2A`; `IsNoOp` confirmed). Never route reverb params through it.
> - **Threading:** all XAudio2/SharpDX voice & effect mutation must happen on the main `Update()` thread (~16.6 ms/tick). Offload only ray/occlusion math via `MyAPIGateway.Parallel.StartBackground`, re-enter via `MyAPIGateway.Utilities.InvokeOnGameThread`. All RSP reflection caches and binding dictionaries are non-concurrent.
> - **RSP is unsandboxed** (client plugin, not a workshop mod): it references `Sandbox.Game`/`VRage.Audio`/SharpDX directly and reaches internals via `Type.GetType("VRage.Audio.…")` + reflection + Harmony. Every private name is unversioned — wrap in try/catch that latches a `_disabled` flag so a SE update fails soft.

---

## PART A — GAME-ENGINE AUDIO HANDLES CATALOG

Marker legend: **[USED]** touched by RSP today (direct or reflection); **[UNUSED]** available, not exploited; **[PARTIAL]** diagnostic/poc only, not production.

### A0. The most exploitable UNUSED handles (lead with these)

Ranked by immersion-payoff for RSP's stated direction (thruster realism, progressive environment muffling, ray-driven reverb wet bus):

1. **`X3DAudio.Calculate` → `DspSettings.{LpfDirectCoefficient, LpfReverbCoefficient, ReverbLevel, DopplerFactor}`**, unlocked by OR-ing `CalculateFlags.LpfDirect | LpfReverb | ReverbVolume` into `MyXAudio2.m_calculateFlags` (currently `Matrix | Doppler`). Hands you engine-grade, per-source, distance-driven low-pass + reverb-send for free — could subsume much of `V2EngineFilterModel`. **The single biggest unexploited lever.**
2. **`Voice.SetOutputFilterParameters(destVoice, FilterParameters, opSet)` + `VoiceSendFlags.UseFilter`** — filter the dry path and the reverb send *independently* from one voice ("dark dry + bright slap") without cloning effects.
3. **`MySourceVoice.SetOutputVoices` / `CurrentOutputVoices`** — re-route a live source to a parallel RSP reverb-wet `SubmixVoice` (the abandoned `SourceReverbVoiceRoutingEnabled` path). **Known-crashing on live voices** — see C-2.1.
4. **`SourceVoice.SetFrequencyRatio(float, opSet)` / `MySourceVoice.FrequencyRatio`** (cap 2.0, baked at pooled-voice ctor) — real-time pitch for thruster RPM/spool/Doppler. Must honor `MySoundData.DisablePitchEffects`.
5. **`MyXAudio2.ApplyEffect(...)` / `MyEffectBank.CreateEffect(...)`** — the *sanctioned, non-reflection* way to attach RSP's bank-registered filters to a voice.
6. **`MyEntity3DSoundEmitter.PlaySound(byte[], …, D3)` + `IMySourceVoice.SubmitBuffer`/`StartBuffered`** (queue cap 62) — inject RSP-synthesised PCM (reverb tails, tool loops) as positioned 3D voices, getting vanilla 3D/attenuation for free.
7. **`m_musicAudioVoice` / `m_hudAudioVoice` submixes + `MasteringVoice` effect chain** — independent reverb/EQ buses (RSP only touches the game submix) and a true master-bus insert point (`EnableMasterLimiter` shows the pattern).
8. **`Get3DSounds()` / `GetCurrentlyPlayedSounds()` / `MyCueBank.GetCue()`** — enumerate live voices and read cue metadata (`MaxDistance`, curve, `Loopable`, `UseOcclusion`, `Category`, `ModifiableByHelmetFilters`) instead of hooking `SetSound` and keeping a private cue catalog.
9. **The stock XAPOs** `Fx.Equalizer`, `Fx.Echo`, `Fx.MasteringLimiter`, `Fx.VolumeMeter` — chainable exactly like the working `XAudio2.Fx.Reverb` (availability on 2.7 **UNVERIFIED** — probe with a ctor fallback first).
10. **`MyEntity3DSoundEmitter.EmitterMethods` delegate hooks** (`CanHear`/`ShouldPlay2D`/`CueType`/`ImplicitEffect`) — override hearability/cue/effect selection *without* Harmony.

> **Dead handle to drop:** RSP currently reflects+invokes `SetReverbParameters` — it does nothing (empty stub). Remove the wasted reflection.

---

### A1. VRage.Audio core engine (`MyXAudio2`, cue bank, effect bank)

RSP never references `VRage.Audio.dll` at compile time; every handle is reached via `Type.GetType("VRage.Audio.…")` + `FieldInfo`/`MethodInfo`/`PropertyInfo`. Core anchors: `MyXAudio2.Instance` (static), `m_effectBank.m_effects`, per-voice `MySourceVoice.Voice`.

#### `VRage.Audio.MyXAudio2 : IMyAudio` — singleton engine (`public class`; `MyXAudio2.cs`, ~2007 lines)

**Identity / static**
| Member (real signature) | Marker | Notes |
|---|---|---|
| `internal static MyXAudio2 Instance;` | **[USED]** | RSP's primary anchor (`RspDynamicAudioFilters.XAudioInstanceField = GetField("Instance", Static\|NonPublic)`); also the `IMyAudio` behind `MyAudio.Static`. |
| `public static bool DEVICE_DETAILS_SUPPORTED = true;` | **[UNUSED]** | selects X3DAudio init path (channel-mask ctor vs forced `Version29`). |
| `public static MyStringId NO_RANDOM;` | **[UNUSED]** | music-transition sentinel. |

**Private fields RSP reflects into (the real DSP graph)**
| Member | Marker | Notes |
|---|---|---|
| `private XAudio2 m_audioEngine;` | **[USED]** | the SharpDX device; RSP grabs it (`GetField("m_audioEngine")`) in `V2ReverbDiagnosticPing`/`V2GlobalReverbRuntime` to build its own `Reverb` + `SubmixVoice` wet buses. |
| `private MasteringVoice m_masterVoice;` | **[USED indirectly]** | read via `SampleRate`; routing target only (never constructed). **Exploit:** attach a master-bus `EffectChain` (final compressor/EQ), mirroring `EnableMasterLimiter`. |
| `private SubmixVoice m_gameAudioVoice;` | **[USED, targeted]** | the submix ALL non-HUD/non-music voices route to; where vanilla reverb attaches. `V2GlobalReverbRuntime.EnsureGameSubmixReverbChain` reflects here. **Highest-leverage handle for global wet processing.** |
| `private SubmixVoice m_musicAudioVoice; / m_hudAudioVoice;` | **[UNUSED]** | independent buses excluding music/HUD. |
| `private MyCueBank m_cueBank;` | **[indirect]** | see A1 cue bank. |
| `private MyEffectBank m_effectBank;` | **[USED]** | RSP walks `m_effectBank.m_effects` to register custom filters. |
| `private X3DAudio m_x3dAudio;` | **[UNUSED]** | spatialiser used by `Apply3D`. |
| `private CalculateFlags m_calculateFlags = Matrix \| Doppler (\| RedirectToLfe);` | **[UNUSED]** | flags fed to `X3DAudio.Calculate`. **Exploit:** OR in `LpfDirect`/`LpfReverb`/`ReverbVolume` for engine-native distance LPF + reverb send. |
| `private Listener m_listener; private Emitter m_helperEmitter;` | **[UNUSED]** | reused X3DAudio structs (positions set each frame). |
| `private static readonly float[] m_outputMatrixMono/Stereo;` | **[UNUSED]** | non-3D fallback pan matrices (mono = `{0.5,0.5,0,0,0.4,0.4,0,0}`). |

**Reverb plumbing fields**
| Member | Marker | Notes |
|---|---|---|
| `private SharpDX.XAudio2.Fx.Reverb m_reverb;` | **[UNUSED directly]** | vanilla reverb, created lazily in `EnableReverb`. |
| `private bool m_applyReverb; / m_enableReverb; / m_reverbSet;` | **[USED]** | `V2GlobalReverbRuntime` reflects `_applyReverbField`/`_enableReverbField` (+ property forms). **Gated:** only inits when `m_masterVoice.VoiceDetails.InputSampleRate <= 48000`. |

**Public properties**
| Member | Marker | Notes |
|---|---|---|
| `public int SampleRate { get; }` → `m_masterVoice.VoiceDetails.InputSampleRate` (0 if device lost) | **[UNUSED]** | relevant: RSP normalises cutoffs against per-voice `InputSampleRate`. |
| `public bool ApplyReverb { get; set; }` | **[USED via field/property]** | getter guarded (false if `!m_enableReverb`/no cue bank/rate>48000); setter calls `m_gameAudioVoice.EnableEffect(0)`/`DisableEffect(0)` + sets `m_cueBank.ApplyReverb`. |
| `bool IMyAudio.EnableReverb { get; set; }` | **[USED]** | lazy init: `new Reverb(m_audioEngine)` → `EffectDescriptor` → `m_gameAudioVoice.SetEffectChain(...)` → `DisableEffect(0)`. |
| `bool IMyAudio.EnableDoppler { get; set; }` (default true) | **[UNUSED]** | toggles doppler in `Apply3D`. **Exploit:** disable/override engine doppler for RSP realism. |
| `bool IMyAudio.UseVolumeLimiter / UseSameSoundLimiter { get; set; }` | **[UNUSED]** | gate the mastering limiter / same-sound dedupe. |
| `public MySoundData SoloCue { get; set; }` | **[UNUSED]** | if set, `GetSound` plays ONLY that cue (debug/solo). |
| `GameSoundIsPaused / CanPlay / CacheLoaded / AudioPlatform` | **[UNUSED]** | state/diagnostics. |
| `VolumeGame/Music/Hud/VoiceChat`, `Mute`, `MusicAllowed`, `EnableVoiceChat`, `CanUseDebug` | **[UNUSED]** | mixer controls. |

**Key methods**
| Member (real signature) | Marker | Notes |
|---|---|---|
| `public void SetReverbParameters(float diffusion, float roomSize)` | **[USED but NO-OP]** | decompiled body is empty `{ }` (single `ret`). RSP's reflection+invoke accomplishes nothing — **drop it**. Real diffusion/roomSize go through RSP's own `Reverb.SetEffectParameters`. |
| `private MySourceVoice PlaySound(MyCueId, IMy3DSoundEmitter, MySoundDimensions, bool skipIntro, bool skipToEnd, bool isMusic)` and `IMyAudio.PlaySound/GetSound` wrappers | **[UNUSED directly]** | canonical play path; `GetSound` sets `voice.Emitter`, applies volume/pitch variation, calls `Apply3D` for D3. **Exploit:** `IMyAudio.GetSound(emitter, D3)` makes a *raw buffered* voice (`new WaveFormat(24000,16,1)`) for injecting synthesised/streamed PCM. |
| `bool SourceIsCloseEnoughToPlaySound(Vector3 sourcePosition, MyCueId cueId, float? customMaxDistance = 0f)` | **[USED indirectly]** | `len² ≤ (customMaxDistance \| cue.UpdateDistance \| cue.MaxDistance)²`. **Exploit:** pre-gate expensive per-source processing with the engine's own cull. |
| `public IMyAudioEffect ApplyEffect(IMySourceVoice input, MyStringHash effect, MyCueId[] cueIds = null, float? duration = null, bool musicEffect = false)` | **[USED]** (engine path) | the sanctioned way to attach a named bank effect (delegates to `m_effectBank.CreateEffect`); `SelectEffect→ApplyEffect` picks up RSP's injected `RSPEngineFilter`/`RSPAuxFilter`. RSP could call this instead of hand-applying. |
| `public void EnableMasterLimiter(bool enable)` | **[UNUSED]** | toggles master `MasteringLimiter` (only if `UseVolumeLimiter`). |
| `public void ChangeGlobalVolume(float level, float time)` | **[UNUSED]** | ducking ramp across all three submixes. |
| `public Vector3 GetListenerPosition()` | **[USED]** | listener world pos (also computed independently in `V2AudioListenerState`). |
| `public void Update(int stepSizeInMS, Vector3 listenerPos, up, front, velocity)` | **[UNUSED]** | per-frame pump; updates `m_listener`, ticks banks, runs `GlobalVolumeUpdate` + 3D reposition. |
| `Get3DSounds()` / `GetCurrentlyPlayedSounds()` / `GetUpdating3DSoundsCount()` / `GetSoundInstancesTotal2D()/3D()` | **[UNUSED]** | live-voice enumeration. **Exploit:** `Get3DSounds()` exposes every active 3D emitter. |
| `private float Apply3D(MySourceVoice, Listener, Emitter, int srcCh, int dstCh, CalculateFlags, float maxDistance, float freqRatio, bool silent, bool use3DCalculation=true, bool fullDoppler=true)` | **[UNUSED]** | calls `m_x3dAudio.Calculate`, swaps stereo coeffs, clamps `DopplerFactor` to **[0.9, 1.0]**, applies linear distance gain `1 - dist/maxDistance`, `voice.SetOutputMatrix(...)`. Engine does NOT set `LpfDirect`/reverb-send flags by default → distance rolloff is RSP's job. **Harmony hook here = strongest spatialisation lever** (read/mutate `DspSettings`). May be JIT-inlined — patch `Update3DCuesState`/`Update` one level up if so. |

#### `VRage.Audio.MyEffectBank` (`internal class`) — effect registry
| Member | Marker | Notes |
|---|---|---|
| `private Dictionary<MyStringHash, MyAudioEffect> m_effects;` | **[USED]** | RSP's whole custom-filter feature inserts/clones `MyAudioEffect` keyed by `MyStringHash` (`RSPEngineFilter`, `RSPAuxFilter`), cloning vanilla `realShipFilter`/`LowPassCockpit`. There is **no SBC `MyAudioEffectDefinition` wrapper** — registering = inserting into this dict. |
| `public MyEffectInstance CreateEffect(IMySourceVoice input, MyStringHash effect, MySourceVoice[] cues = null, float? duration = null)` | **[UNUSED]** | supported counterpart to manual application. |
| `private List<MyEffectInstance> m_activeEffects;` + `public void Update(int ms)` | **[UNUSED]** | ticks active effects, auto-removes `Finished`. |

#### `VRage.Audio.MyEffectInstance : IMyAudioEffect` (`internal class`) — a running effect
- nested `MyAudioEffect.SoundEffect` carries `Duration`, `VolumeCurve`, `Filter`, `Frequency`, `OneOverQ`, `StopAfter` — **[USED]** RSP reflects every field when building/cloning.
- `UpdateFilter` builds `new FilterParameters { Frequency, OneOverQ, Type = (FilterType)effect.Filter }` → `sound.Voice.SetFilterParameters(...)` — **the exact native call RSP replicates**; the cast proves `MyAudioEffect.FilterType` maps 1:1 onto SharpDX `FilterType` ordinals.
- `m_defaultFilter = { LowPassFilter, Frequency=1f, OneOverQ=1f }` — the "filter off" reset (matches RSP's `MaxXAudioFilterFrequency = 1f`).
- `OutputSound`, `Finished`, `AutoUpdate { get; set; }`, `SetPosition(float ms)`/`SetPositionRelative(float 0..1)`, `event Action<MyEffectInstance> OnEffectEnded` — **[UNUSED]**. **Exploit:** `AutoUpdate=false` + `SetPositionRelative` drives a filter sweep frame-by-frame from RSP's model.

#### `VRage.Audio.MyCueBank` (`public class`) — cue/voice pool + reverb owner
| Member | Marker | Notes |
|---|---|---|
| `public bool ApplyReverb { get; set; }` | **[indirect USED]** | flipped by `MyXAudio2.ApplyReverb`. |
| `private Reverb m_reverb;` | **[UNUSED]** | a *second* `Fx.Reverb` instance the cue bank holds. |
| `internal MySourceVoice GetVoice(MyCueId, out int waveNumber, MySoundDimensions, int tryIgnoreWaveNumber, MyVoicePoolType)` | **[UNUSED]** | pool allocator. |
| `public MySoundData GetCue(MyCueId)` | **[UNUSED]** | cue-definition lookup. **Exploit:** per-cue `MaxDistance`/`VolumeCurve`/`Loopable`/`DisablePitchEffects` without a private catalog. |
| `public void SetAudioEngine(XAudio2, gameDesc, hudDesc, musicDesc)` | **[UNUSED]** | re-binds pools on device reset. |
| `CueDefinitions`, `GetCategories()`, `GetCurrentlyPlayedSounds()`, static `LastSounds`/`LastSoundIndex` | **[UNUSED]** | enumeration/debug. |

#### Supporting types
- `static MyDistanceCurves` (`internal`) — `CURVE_LINEAR/QUADRATIC/INVQUADRATIC/CURVE_CUSTOM_1` + `DistanceCurve[] Curves` (indexed by `MyCurveType`). **[UNUSED]**. **Exploit:** documents exact attenuation shapes so RSP's distance models match vanilla.
- `static X3DAudioExtensions` (`public`) — `Emitter.UpdateValuesOmni(position, velocity, cue/maxDistance, channelsCount, customMaxDistance, dopplerScaler)` (+ overload), `SetDefaultValues`. **[UNUSED]**. Sets `InnerRadius=0.5f`/`InnerRadiusAngle=0.785398f (π/4)` only when output > 2 channels. **Exploit:** install custom distance `VolumeCurve`s / inner-radius spreads per emitter.
- `static VoiceExtensions` (`internal`) — `bool SourceVoice.IsValid()` (`!IsDisposed && NativePointer != Zero`). **[UNUSED]** (RSP re-implements this).
- enums/structs: `MyVoicePoolType { Sound, Hud, Music }`, `MyCueBank.CuePart { Start, Loop, End }`, `CalculateFlags`, `MySoundDimensions { D2, D3 }`.

---

### A2. Source voices, cues & effect instances

#### `VRage.Audio.IMySourceVoice` (`public`, in **VRage.dll**)
The public handle every `MyEntity3DSoundEmitter.Sound`/`SecondarySound` exposes — RSP's primary entry into a playing sound.

| Member | Marker | Notes |
|---|---|---|
| `bool IsValid { get; }` | **[USED]** | guards in `RspDynamicAudioFilters` + reverb runtimes. |
| `bool IsPlaying { get; }` | **[USED]** | |
| `MyCueId CueEnum { get; }` | **[USED]** | V2 classifiers read it for engine/aux/tool routing. |
| `bool IsLoopable { get; }` | **[USED]** | emitter-owns-voice checks. |
| `float FrequencyRatio { get; set; }` (clamped to 2.0) | **[UNUSED for control]** | settable for Doppler/RPM pitch. |
| `float Volume { get; }`, `float VolumeMultiplier { get; set; }` | **[PARTIAL]** | `VolumeMultiplier` is a clean per-voice post-gain hook RSP does NOT use — ideal for env muffling/void attenuation. |
| `bool IsBuffered`, `bool IsPaused` | **[UNUSED]** | |
| `Action<IMySourceVoice> StoppedPlaying { get; set; }` | **[UNUSED]** | end-of-playback callback; could replace RSP's 120 s TTL purge with deterministic teardown. |
| `Start(bool skipIntro, bool skipToEnd=false)` / `Stop(bool force=false)` / `StartBuffered()` / `SubmitBuffer(byte[])` / `Pause()` / `Resume()` / `SetVolume(float)` / `Destroy()` | **[UNUSED]** | `SubmitBuffer`/`StartBuffered` push RSP's own PCM through an engine-managed voice (queue cap 62) — supported path for custom DSP tails / synthesized impacts without authoring SBC cues. |

#### `VRage.Audio.MySourceVoice` (`internal`, in **VRage.Audio.dll**) — reached by reflection
- `public SourceVoice Voice => m_voice;` — **[USED, central]** the raw SharpDX voice. `RspDynamicAudioFilters.ResolveSourceVoice` reflects the `Voice` property (cached per wrapper type); all DSP hangs off this. **Pooled ctor:** `new SourceVoice(device, fmt, VoiceFlags.UseFilter, 2f, …)` — `UseFilter` is why live `SetFilterParameters` works and why max `FrequencyRatio` is 2.0. **Non-pooled (buffered) ctor does NOT pass `UseFilter`** — injected buffered voices cannot carry a per-voice filter.
- `public IMy3DSoundEmitter Emitter;` (public field) — **[USED]** back-ref; `ResolveSourceVoiceEmitterMember` searches `Emitter`/`m_emitter`/`SoundEmitter`/`m_soundEmitter` then any "emitter"-named member.
- `public VoiceSendDescriptor[] CurrentOutputVoices => m_currentDescriptor;` — **[UNUSED]** read-first-then-append so you preserve the engine's existing sends instead of clobbering.
- `public void SetOutputVoices(VoiceSendDescriptor[] descriptors)` — **[USED]** (reverb runtimes mostly call the native form; managed wrapper caches the descriptor).
- `public int GetOutputChannels()` — **[UNUSED]** for sizing `SetOutputMatrix` arrays.
- `public float DistanceToListener { get; set; }` — **[UNUSED]** engine-maintained per-voice distance; free signal for distance-driven muffling/wet without recomputing geometry.
- `public MyCueId CueEnum`, `FrequencyRatio`, `Volume`/`VolumeMultiplier`, `Silent`, transport methods — **[partly UNUSED]** as above.
- `internal SubmitSourceBuffer(MyCueId, MyInMemoryWave, MyCueBank.CuePart)` — internal three-part buffer model (`m_loopBuffers[3]`); explains why custom PCM goes through `SubmitBuffer`, not this.

#### `SharpDX.XAudio2.SourceVoice` / base `Voice` (the `.Voice` payload) — see Part A4 for the full SharpDX surface.

#### `VRage.Data.Audio.MyAudioEffect` (`public`, in **VRage.dll**) — timed multi-stage effect data model
```csharp
public class MyAudioEffect {
    public enum FilterType { LowPass, BandPass, HighPass, Notch, None }   // order != SharpDX FilterType
    public struct SoundEffect {
        public Curve VolumeCurve;   // VRageMath.Curve, evaluated 0..1 over Duration
        public float Duration;      // seconds; 0 = static/instant
        public FilterType Filter;
        public float Frequency;     // XAudio2 NORMALIZED freq (2*sin(pi*fc/fs)), NOT Hz
        public bool  StopAfter;     // stop the voice when this stage ends
        public float OneOverQ;      // 1/Q
    }
    public int ResultEmitterIdx;                  // which sound is the "output"
    public List<List<SoundEffect>> SoundsEffects; // [emitterIdx][stageIdx]
    public MyStringHash EffectId;
}
```
- **[USED, heavily]** RSP reflects every field/nested member to clone a template and write `Frequency`/`OneOverQ`/`Filter`.
- **[UNUSED capability]** `VolumeCurve` + non-zero `Duration` give *time-varying* filter/volume envelopes natively (RSP only ever sets static `Duration=0`).
- **Caveat:** `MyAudioEffect.FilterType` enum order differs from SharpDX's; `UpdateFilter` casts `(FilterType)effect.Filter` directly, so use the engine enum names here (RSP does, via `Enum.Parse`).

#### `VRage.Data.Audio.MySoundData` (`public`, in **VRage.dll**) — the cue definition behind a `MyCueId`
Reached via `MyAudio.Static.GetCue(MyCueId)`. DSP-relevant:
- `MyStringHash RealisticFilter` / `ArcadeFilter` — **the effect ids the engine auto-applies to a cue** (where stock cues name `realShipFilter`). **[UNUSED]** — *reading* tells you which filter a cue already carries; *setting* would let RSP attach `RSPEngineFilter`/`RSPAuxFilter` declaratively instead of intercepting live.
- `bool ModifiableByHelmetFilters`, `CanBeSilencedByVoid`, `UseOcclusion`, `DisablePitchEffects` (blocks `FrequencyRatio` writes — RSP's pitch hooks MUST honor this), `float RealisticVolumeChange`, `MyStringId Category`, `bool Loopable`, `bool StreamSound`, `float MaxDistance`/`UpdateDistance` — **[UNUSED]**. `Category` + `UseOcclusion` + `ModifiableByHelmetFilters` are a ready-made per-cue policy layer (more reliable than name-matching).

#### `VRage.Audio.MyCueId` (`public struct`, in **VRage.dll**)
`MyStringHash Hash;` `bool IsNull;` `ctor(MyStringHash)`; `==`/`!=`/`Equals`; `MyCueId.Comparer`. **[USED]** RSP builds `MyStringHash.GetOrCompute(subtype)` keys and matches `voice.CueEnum`; cheap to key per-voice dictionaries on.

> **Absent handles:** no `MyInMemoryWave`/`MyWaveBank` exploitation (raw decoded PCM behind a voice — `WaveFormat`, `Buffer (AudioBuffer)`, `Stream`); **[UNUSED]**, relevant only to replace decoded PCM directly rather than via `SubmitBuffer`.

---

### A3. 3D emitters & spatialisation

#### `Sandbox.Game.Entities.MyEntity3DSoundEmitter : IMy3DSoundEmitter` (in **Sandbox.Game.dll**)
The per-entity emitter RSP resolves filters against; `SelectEffect` is Harmony-patched.

**Position / motion / spatial inputs**
| Member | Marker | Notes |
|---|---|---|
| `Vector3D SourcePosition { get; }` (= `WorldAABB.Center - MainCamera.Position`) | **[USED]** | read for muffling/occlusion distance gating. |
| `void SetPosition(Vector3D? position)` | **[UNUSED]** | fake placement for occlusion/reverb distancing. |
| `Vector3 Velocity { get; }` / `void SetVelocity(Vector3? velocity)` | **[UNUSED]** | custom Doppler / wind-shift. |
| `float DopplerScaler { get; private set; }` (set in ctor `(MyEntity, bool useStaticList=false, float dopplerScaler=1f)`) | **[UNUSED]** | fixed at construction. |
| `MyEntity Entity { get; set; }` | **[USED]** | `IsEmitterUsable`/`Entity.Closed`. |
| `bool Force3D / Force2D { get; set; }`, `bool Plays2D { get; }` | **[UNUSED]** | control whether spatialisation runs. |
| `int SourceChannels { get; set; }` | **[UNUSED]** | settable to alter matrix dimensions. |
| `float? CustomMaxDistance { get; set; }` | **[UNUSED]** | strong lever — scales `CurveDistanceScaler`, compress/extend audible range per emitter. |
| `float? CustomVolume { get; set; }` / `float VolumeMultiplier { get; set; }` (pushes to `Sound.VolumeMultiplier`) | **[USED]** | `VolumeMultiplier` read in diagnostics; not written. |

**Voice access**
| Member | Marker | Notes |
|---|---|---|
| `IMySourceVoice Sound { get; }` / `SecondarySound { get; }` | **[USED]** | central to `EmitterOwnsVoice`, emitter↔voice binding, live filter targeting. |
| `void SetSound(IMySourceVoice, [CallerMemberName] string caller=null)` | **[UNUSED by RSP]** | engine splices in `ApplyEffect` output; **RSP Harmony-patches this** (`BlockRangeScalePatch`) to call `RecordEmitterVoiceBinding`. |
| `MyCueId SoundId { get; set; }`, `MySoundData LastSoundData { get; }`, `MySoundPair SoundPair { get; }` | **[USED]** | cue identity/classification. |
| `bool IsPlaying`, `bool Loop { get; private set; }` | **[USED partial]** | |

**Playback / effects entry points**
| Member | Marker | Notes |
|---|---|---|
| `bool PlaySound(MySoundPair, bool stopPrevious=false, bool skipIntro=false, bool force2D=false, bool alwaysHearOnRealistic=false, bool skipToEnd=false, bool? force3D=null, bool forcePlaySound=false)` + `PlaySingleSound(...)` | **[UNUSED]** | patch targets to inject/replace sounds. |
| `bool PlaySoundWithDistance(MyCueId, …, bool? force3D=null)` | **[UNUSED]** | distant-sound crossfade then play. |
| `void PlaySound(byte[] buffer, float volume=1f, float maxDistance=0f, MySoundDimensions dimension=MySoundDimensions.D3)` | **[UNUSED]** | **raw PCM through a 3D emitter** (`Sound.SubmitBuffer`/`StartBuffered`) — high-value for synthesized/streamed spatialised audio. |
| `StopSound(bool forced, bool cleanUp=true, bool cleanupSound=false)`, `Cleanup()`, `ClearSecondaryCue()` | **[UNUSED]** | |
| `void Update()` / `static void UpdateEntityEmitters(bool,bool,bool)` | **[USED indirectly]** | drives `SelectCue`/`SelectEffect`; RSP rides the `SelectEffect` hook. |
| `MyStringHash SelectEffect()` (private) | **[USED]** | chooses helmet/cockpit low-pass; **RSP's `ThrusterFilterPatch` postfixes this** to substitute `RSPEngineFilter`/`RSPAuxFilter`. |
| `enum MethodsEnum { CanHear, ShouldPlay2D, CueType, ImplicitEffect }` + `Dictionary<int, ConcurrentCachingList<Delegate>> EmitterMethods` | **[UNUSED]** | register `Func<bool>`/`Func<MyStringHash>` to override hearability/cue/effect **without patching `SelectEffect`**. |
| `event Action<MyEntity3DSoundEmitter> StoppedPlaying` | **[UNUSED]** | clean voice-lifecycle cleanup hook. |

#### `VRage.Audio.IMy3DSoundEmitter`
Members RSP treats generically (`CanHold3DEmitter`): `MyCueId SoundId`, `IMySourceVoice Sound`/`SecondarySound`, `Vector3D SourcePosition`, `Vector3 Velocity`, `float DopplerScaler`, `float? CustomMaxDistance`/`CustomVolume`, `bool Realistic`, `bool Force3D`, `bool Plays2D`, `int SourceChannels`/`LastPlayedWaveNumber`, `object DebugData`/`SyncRoot`, `void SetSound(...)`. **[USED]** — `Sound`/`SecondarySound`/`SourcePosition` are load-bearing.

#### `VRage.Audio.X3DAudioExtensions` (`public static`)
`Emitter.UpdateValuesOmni(...)` (+ overload), `Emitter.SetDefaultValues()`, `Listener.SetDefaultValues()` — **[UNUSED]**; sets `Position/Velocity/DopplerScaler/CurveDistanceScaler/VolumeCurve` and (>2 channels) `InnerRadius=0.5f`/`InnerRadiusAngle=π/4`. Patchable to install custom distance curves / inner-radius spreads.

> The spatialisation core (`MyXAudio2.m_x3dAudio`, `m_listener`, `m_helperEmitter`, `m_calculateFlags`, `Apply3D`, `Update3DCuesState`/`Update3DCuePosition`/`Update3DVoicePosition`) is catalogued under A1. **[UNUSED by RSP]** — RSP does its own geometry and feeds results into VRage's emitter system; never calls X3DAudio directly.

---

### A4. SharpDX surface (XAudio2 / X3DAudio / XAPO / filters / sends / reverb)

Reached via reflection over `MySourceVoice.Voice`/`m_audioEngine`, and via direct typed calls (`V2ReverbDiagnosticPing`, `V2ManagedDspReverbRuntime`, the managed XAPO `V2LiveReverbPocProcessor`). Signatures from `Bin64/SharpDX.XAudio2.dll` + sibling `.xml`.

#### `SharpDX.XAudio2.Voice` (abstract base of SourceVoice / SubmixVoice / MasteringVoice)
| Member (real signature) | Marker | Notes |
|---|---|---|
| `void SetFilterParameters(FilterParameters parametersRef, int operationSet = 0)` | **[USED]** | per-voice biquad; `TryApplyLiveFilterParameters` invokes by reflection. |
| `void GetFilterParameters(out FilterParameters parametersRef)` | **[USED]** | readback (`DescribeCurrentFilter`). |
| `void SetOutputFilterParameters(Voice destinationVoiceRef, FilterParameters parametersRef, int operationSet = 0)` | **[UNUSED]** | **per-send filter** to a *specific* destination — true wet/dry filtering without cloning effects. |
| `void GetOutputFilterParameters(Voice destinationVoiceRef, out FilterParameters parametersRef)` | **[UNUSED]** | |
| `void SetOutputMatrix(Voice destinationVoiceRef, int sourceChannels, int destinationChannels, float[] levelMatrixRef, int operationSet = 0)` (+3 overloads) | **[PARTIAL]** | only in `V2ReverbDiagnosticPing.TrySetOutputMatrix`/`BuildOutputMatrix` (not production). Per-channel gain/pan, mono→stereo spread, channel muting, wet-bus panning. |
| `void SetVolume(float volume, int operationSet = 0)` / `GetVolume(out float)` | **[USED]** | wet-send level on bus/source voices. |
| `void SetChannelVolumes(int channels, float[] volumesRef, int operationSet = 0)` / `GetChannelVolumes` | **[UNUSED]** | per-channel trim — directional ducking / stereo collapse without a full matrix. |
| `void SetEffectChain(params EffectDescriptor[] effectDescriptors)` | **[USED]** | installs reverb XAPO on submix; `SetEffectChain(null)` tears down. |
| `void EnableEffect(int idx, int opSet=0)` / `DisableEffect(...)` / `IsEffectEnabled(int, out RawBool)` | **[USED]** | bus reverb enable + status. |
| `void SetEffectParameters<T>(int idx, T param, int opSet=0) where T:struct` (+ byte[]/non-generic) | **[USED]** | strongly-typed `ReverbParameters` push. `GetEffectParameters<T>` — **[UNUSED]**. |
| `float Volume` (property), `VoiceDetails VoiceDetails` (property) | **[USED]** | `VoiceDetails.InputSampleRate` drives filter normalization; `InputChannelCount` sizes the wet bus. |

#### `SharpDX.XAudio2.SourceVoice : Voice`
| Member | Marker | Notes |
|---|---|---|
| ctor `SourceVoice(XAudio2 device, WaveFormat sourceFormat)` (+ `VoiceFlags`/`maxFrequencyRatio`/`VoiceCallback`/`EffectDescriptor[]` overloads) | **[USED]** | throwaway voices to pump reverb tail / play wet cues. The `maxFrequencyRatio`/`effectDescriptors` overloads **[UNUSED]**. |
| `void SetOutputVoices(VoiceSendDescriptor[] ...)` | **[USED]** | (reflection) reroute a playing voice's sends to a custom reverb bus + restore. Backbone of the "global bus." |
| `SubmitSourceBuffer(AudioBuffer, uint[] decodedXMWAPacketInfo)`, `Start(int opSet=0)`, `Stop(PlayFlags, int opSet=0)`, `FlushSourceBuffers()` | **[USED]** | burst/silence tail-pump voices. |
| `void SetFrequencyRatio(float ratio, int opSet=0)` / `GetFrequencyRatio(out float)` | **[UNUSED]** | real-time pitch/resample — Doppler, thruster/tool spin-up/down without swapping cues. |
| Events `ProcessingPassStart`, `BufferStart`/`BufferEnd` (with callbacks) | **[UNUSED]** | sample-accurate scheduling / gapless crossfades. |
| `VoiceState State { get; }` (`BuffersQueued`, `SamplesPlayed`) | **[UNUSED]** | `SamplesPlayed` = sample-accurate clock for syncing DSP/impact tails. |

#### `SharpDX.XAudio2.SubmixVoice : Voice`
ctors `SubmixVoice(XAudio2 device, int inputChannels, int inputSampleRate)` and `(…, SubmixVoiceFlags flags, int processingStage, EffectDescriptor[])` — **[USED]** (3-arg) for wet/reverb buses. `processingStage` (submix ordering; higher = later) and `SubmixVoiceFlags` **[UNUSED]** — deterministic multi-bus chains (early-reflection → late-tail → master).

#### `SharpDX.XAudio2.MasteringVoice : Voice`
ctors `MasteringVoice(XAudio2 device, int inputChannels, int inputSampleRate)`; `GetChannelMask(out int)` — **[UNUSED]** (engine owns the single mastering voice; RSP reflects `m_masterVoice` as a routing target only). **Exploit:** master-bus limiter/EQ chain for global tone shaping.

#### `SharpDX.XAudio2.FilterParameters` (struct) + `FilterType` (enum)
- `FilterParameters { FilterType Type; float Frequency; float OneOverQ; }` — **[USED]** built by reflection (`CreateSharpFilterParameters`). `Frequency` = normalized `2·sin(π·fc/fs)`; `OneOverQ` = 1/Q. RSP clamps to (0.0001…1) and (0.0001…1.5).
- `FilterType { LowPassFilter, BandPassFilter, HighPassFilter, NotchFilter, LowPassOnePoleFilter, HighPassOnePoleFilter }` — **[USED]** first four. **[UNUSED & UNAVAILABLE on 2.7]:** `LowPassOnePoleFilter`/`HighPassOnePoleFilter` (XAudio2.9-only — do not rely on them; the settings/menu strings referencing one-pole are non-functional and should carry a code comment).

#### `SharpDX.XAudio2.EffectDescriptor` (class)
`EffectDescriptor(AudioProcessor effect)` / `(AudioProcessor, int outputChannelCount)`; fields `AudioProcessor Effect`, `RawBool InitialState`, `int OutputChannelCount` — **[USED]** wraps built-in `Fx.Reverb` and RSP's managed XAPO. **[UNUSED]:** `InitialState` (start pre-enabled), channel-changing `OutputChannelCount`. **Constraint:** `OutputChannelCount` must match the host voice; a later `SetEffectChain` must preserve channel counts.

#### `SharpDX.XAudio2.VoiceSendDescriptor` (struct) + `VoiceSendFlags`
`VoiceSendDescriptor(Voice outputVoice)` / `(VoiceSendFlags flags, Voice outputVoice)`; fields `VoiceSendFlags Flags`, `Voice OutputVoice` — **[USED]** (`AppendVoiceSendDescriptor`). **[UNUSED]: `VoiceSendFlags.UseFilter`** — enables the per-send `SetOutputFilterParameters` path (true filtered sends).
`VoiceDetails { VoiceFlags CreationFlags; int ActiveFlags; int InputChannelCount; int InputSampleRate; }` — **[USED]** (`InputSampleRate`, `InputChannelCount`).

#### `SharpDX.XAudio2.Fx.Reverb` + `Fx.ReverbParameters` (the full XAudio2FX reverb)
- `class Reverb : AudioProcessorParamNative<ReverbParameters>`; ctors `Reverb(XAudio2)`, `Reverb(XAudio2, bool isUsingDebuggingFeatures)` — **[USED]** RSP tries `new Reverb(engine,false)` → `(true)` → `(engine)` (`CreateReverbEffect`). `Min*/Max*` const limits **[UNUSED]** as clamps (RSP hardcodes).
- `ReverbParameters` — **[USED extensively]** (`CreateParameters`): `WetDryMix, ReflectionsDelay, ReverbDelay, RearDelay, SideDelay, PositionLeft/Right, PositionMatrixLeft/Right, EarlyDiffusion, LateDiffusion, LowEQGain, LowEQCutoff, HighEQGain, HighEQCutoff, RoomFilterFreq, RoomFilterMain, RoomFilterHF, ReflectionsGain, ReverbGain, DecayTime, Density, RoomSize, RawBool DisableLateField`. RSP leaves `RoomFilterMain=0`, pins `RearDelay/SideDelay=5`. **Room geometry asymmetry (Rear/Side delay, position matrices) barely exploited** — could encode corridor vs dome. **2.7 caveat:** `DisableLateField` unsupported; `SideDelay` Win10/2.9-only.
- `static explicit operator ReverbParameters(ReverbI3DL2Parameters)` + `ReverbI3DL2Parameters.Presets.{Arena, Auditorium, Cave, ConcertHall, LargeHall, SewerPipe, StoneRoom, Hangar, …}` — **[USED]** (`CreatePresetParameters`); remaining presets (Bathroom, Forest, ParkingLot, UnderWater, …) **[UNUSED]**.

#### `SharpDX.XAPO.Fx.Reverb` + `XAPO.Fx.ReverbParameters` (the simple XAPO reverb)
`class Reverb`, ctor `Reverb(XAudio2)`; `ReverbParameters { float Diffusion; float RoomSize; }` — **[KNOWN-INCOMPATIBLE]**. RSP has `CreateSimpleXapoParameters`/`PlayXapo` wired but `PlayXapo()` hard-returns `"unsupported: XAPO.Fx.Reverb does not support XAudio2.9"`. Effectively dead; the full `XAudio2.Fx.Reverb` is the working path.

#### `SharpDX.XAPO.AudioProcessor` (interface) — custom managed DSP
RSP's most ambitious handle: `V2LiveReverbPocProcessor : CallbackBase, AudioProcessor` (and the SharpDX C# base is `AudioProcessorBase<T>`).
- `RegistrationProperties { get; }`, `IsInputFormatSupported`/`IsOutputFormatSupported`, `Initialize(DataStream)`, `Reset()`, `LockForProcess(LockParameters[], LockParameters[])`, `UnlockForProcess()`, `CalcInputFrames(int)`/`CalcOutputFrames(int)` — **[USED]**.
- `void Process(BufferParameters[] inputs, BufferParameters[] outputs, bool isEnabled)` — **[USED]** the RT callback. RSP implements a full FDN reverb (8 delay lines, Hadamard feedback, early taps, damped echo, NaN/denormal panic-clear) on raw `float*` buffers. **The live engine's core.**
- `struct RegistrationProperties { Guid Clsid; <FriendlyName/CopyrightInfo>; int Major/MinorVersion; PropertyFlags Flags; int Min/MaxInput/OutputBufferCount; }` — **[USED]**.
- `enum PropertyFlags { ChannelsMustMatch=1, FrameRateMustMatch=2, BitspersampleMustMatch=4, BufferCountMustMatch=8, InplaceSupported=0x10, InplaceRequired=0x20, Default=0x1F }` — **[USED]** RSP sets `ChannelsMustMatch | FrameRateMustMatch | InplaceSupported`. **[UNUSED]: `BitspersampleMustMatch`** (RSP handles non-float at runtime in `CopyUnsupportedFormat`).
- `BufferParameters { IntPtr Buffer; BufferFlags BufferFlags; int ValidFrameCount; }`, `enum BufferFlags { Silent=0, Valid=1 }`, `LockParameters { WaveFormat Format; int MaxFrameCount; }` — **[USED]**. Audio is interleaved float32 (ch0,ch1,…,chN per frame); propagate `ValidFrameCount`/`BufferFlags`. **RT contract: no alloc/locks/IO in `Process`** — pre-allocate in `Initialize`/`LockForProcess`.

**Other XAPO `Fx` processors — all [UNUSED]** (chainable like the reverb; **availability on 2.7 UNVERIFIED — probe with a ctor fallback before shipping**):
- `Fx.Equalizer` + `EqualizerParameters { FrequencyCenter0..3, Gain0..3, Bandwidth0..3 }` — 4-band parametric EQ (hull-material coloration).
- `Fx.Echo` + `EchoParameters { float WetDryMix; float Feedback; float Delay; }` — slapback for hangars/canyons.
- `Fx.MasteringLimiter` — brickwall limiter to tame additive wet-bus stacking.
- `Fx.VolumeMeter` (`XAudio2.Fx`) — RMS/peak metering to drive ducking / debug overlays.

#### `SharpDX.X3DAudio.*` — **entirely [UNUSED]**
RSP does its own geometry and feeds results into VRage's emitter system.
- `class X3DAudio` — ctor `X3DAudio(Speakers speakers, float speedOfSound = 343.5f, X3DAudioVersion = Default)`; `DspSettings Calculate(Listener, Emitter, CalculateFlags, int sourceChannelCount, int destinationChannelCount)` (+ in-place overload). `const float SpeedOfSound = 343.5f`.
- `class Emitter { RawVector3 Position, Velocity, OrientFront, OrientTop; float InnerRadius, InnerRadiusAngle, ChannelRadius, CurveDistanceScaler, DopplerScaler; int ChannelCount; Cone Cone; float[] ChannelAzimuths; CurvePoint[] VolumeCurve, LfeCurve, LpfDirectCurve, LpfReverbCurve, ReverbCurve; }` — programmable per-emitter distance curves + directivity `Cone`.
- `class Listener { Position, Velocity, OrientFront, OrientTop; Cone Cone; }`.
- `class DspSettings { float[] MatrixCoefficients; float[] DelayTimes; float LpfDirectCoefficient; float LpfReverbCoefficient; float ReverbLevel; float DopplerFactor; float EmitterToListenerAngle/Distance; float Emitter/ListenerVelocityComponent; }`.
- `enum CalculateFlags { Matrix, Delay, LpfDirect, LpfReverb, Reverb, Doppler, EmitterAngle, ZeroCenter, RedirectToLfe }`.

**Exploit:** `X3DAudio.Calculate` directly produces the send matrix, per-send LPF coefficients (engine-grade occlusion filtering), `ReverbLevel` (auto wet-send by distance), and `DopplerFactor` — feed straight into `SetOutputMatrix` + `SetOutputFilterParameters` + reverb send level. **The single biggest unexploited capability for the ray-driven reverb / progressive-muffling goals.**

#### Supporting `SharpDX` core (`SharpDX.dll` / `SharpDX.Multimedia`)
- `WaveFormat.CreateIeeeFloatWaveFormat(int sampleRate, int channels)`; props `SampleRate, Channels, BitsPerSample, WaveFormatEncoding Encoding (IeeeFloat/Pcm)` — **[USED]** for voice formats + XAPO format-gating.
- `DataStream(int sizeInBytes, bool canRead, bool canWrite)`, `WriteRange<T>`, `Position`; `AudioBuffer(DataStream)` + `BufferFlags.EndOfStream` — **[USED]** burst/silence buffers.
- `SharpDX.Mathematics.Interop.RawBool` / `RawVector3` — **[USED]** (`RawBool` for `DisableLateField`/effect-enabled readback; `RawVector3` needed for any X3DAudio adoption).
- `CallbackBase` (COM lifetime base) — **[USED]** base of the managed XAPO.

---

## PART B — INLINE DSP FILTER (current mechanics)

RSP runs a **per-voice XAudio2 biquad** recomputed and re-applied every audio update, layered over (and overriding) the game's effect-bank filter. Two cooperating layers. Central files: `AudioEngineV2/RspDynamicAudioFilters.cs`, `Patches/LiveCustomFilterPatch.cs`.

### B1. Effect-bank registration (`MyAudioEffect` / `MyEffectBank`)
`RspDynamicAudioFilters.UpdateFromSettings(settings)` (line 86) is the registrar — gated by `_disabled` + `HasRequiredReflection()` (625), builds a `BuildSettingsSignature` (894) and **early-returns if unchanged** (98), so the bank is rewritten only when a slider moves. Called per-frame from `AudioEngineV2Runtime.UpdateFromSettings` and on-demand from `ThrusterFilterPatch.Postfix` (61) just before it sets `__result = MyStringHash.GetOrCompute(effectSubtype)` (ThrusterFilterPatch:69); on registration failure the postfix falls back to `__result = MyStringHash.NullOrEmpty` (63) — no effect rather than a broken one.

Bank reached by `TryGetEffectDictionary` (428): `MyXAudio2.Instance` (35) → `m_effectBank` → `m_effects` (`IDictionary` keyed by `MyStringHash`). Two effects registered — `RSPEngineFilter` and `RSPAuxFilter` — by `RegisterOrReplace` (446):
- Key exists → `TryUpdateExistingEffect` (466) reconfigures **only slot 0** of the nested `List<List<SoundEffect>>`.
- Else `TryCreateFromTemplate` (495) clones vanilla `realShipFilter` → `LowPassCockpit` via `CloneSoundEffectsList` (554), copying `ResultEmitterIdx`/curves/shape, overwriting slot-0 coefficients, appending a filter stage if the template had none.
- Last resort `CreateEffect`/`CreateSoundEffectsList` (526/535) synthesizes a one-stage list via `Activator.CreateInstance` + `MakeGenericType`, `Duration=0`, `StopAfter=false`, **`VolumeCurve` left null** (latent compat risk if a sound mod strips both templates).

`ConfigureFilter` (616) writes each stage: `Duration=0`, `Filter = Enum.Parse(FilterField.FieldType, NormalizeCustomFilterType(type))` → the **Keen** `MyAudioEffect.SoundEffect.Filter` enum (distinct from SharpDX's). `Frequency`/`OneOverQ` written here are **already-normalized XAudio coefficients**, computed against `DefaultXAudioFilterSampleRate = 44100` (single-arg `ToXAudioFrequency`, 944), because at registration time there is no specific voice.

### B2. Live voice interception (Harmony)
`LiveCustomFilterPatch` (TargetMethod 15) patches `VRage.Audio.MyEffectInstance.UpdateFilter` — the engine's own per-voice filter push, called every audio update for any voice carrying a running effect:
- **`Prefix(object __0, object __1)`** → `TryPrepareLiveCustomFilterEffect(soundData, soundEffect, settings)` (199): resolves emitter from `soundData` (`ResolveEmitterFromSoundData` → `SoundData.Sound` field, 687/700), confirms a live target, computes dynamic params, and `ConfigureFilter`s the `SoundEffect` the engine is about to apply (closes the one-frame gap).
- **`Postfix(object __0)`** → `TryApplyLiveCustomFilterFromSoundData(soundData, settings)` (219): resolves the native source voice from `soundData.Sound`, gets the emitter, confirms target + custom subtype, writes the freshly-computed coefficients onto the running voice. **The authoritative per-frame write** — deliberately runs *after* the engine's own filter write so RSP wins.

Both halves try/catch and **latch `_disabled = true`** on first throw (`live-filter-patch-failed`) so one reflection/engine change self-disables and falls back to vanilla. Args are positional `object __0/__1` to survive Keen signature changes.

Selection/binding via siblings: `ThrusterFilterPatch.Postfix` patches `SelectEffect` (substitutes the RSP subtype hash); `BlockRangeScalePatch` patches `SetSound(IMySourceVoice, string)` and calls `RecordEmitterVoiceBinding(__instance, __0)` (25) to seed the emitter↔voice cache. Live overrides gated by `IsLiveCustomFilterTarget` (250): **only** V2 emitters (`IsV2Emitter`) or classified engine-audio emitters (`ThrusterFilterPatch.IsEngineAudioEmitter`).

### B3. Per-voice coefficient math and dynamics
`TryApplyLiveFilterParameters(voice, filterType, frequency, q)` (158) is the lowest-level writer. `ResolveSourceVoice` (656) unwraps `IMySourceVoice` → native `SourceVoice` via `Voice` (cached per wrapper type). `ResolveSetFilterMethod` (833) finds `SetFilterParameters(FilterParameters, int)` by signature match (param0==`FilterParameters`, param1==`int`), with hit/miss caching. Then:
```
parameters = CreateSharpFilterParameters(filterType, frequency, q, sourceVoice)
setFilter.Invoke(sourceVoice, new[] { parameters, 0 })   // operationSet 0 = apply immediately
```
`CreateSharpFilterParameters` (647):
- `Type = ToSharpFilterType(filterType)` → `Enum.Parse(SharpDX FilterType, normalized + "Filter")` e.g. `LowPassFilter` (1098). `NormalizeCustomFilterType` already supports HighPass/BandPass/Notch (engine model currently emits only LowPass).
- `Frequency = ToXAudioFrequency(freq, ResolveSourceVoiceInputSampleRate(sourceVoice))`. `ToXAudioFrequency` (949) = `2·sin(π·cutoff/sampleRate)`. Cutoff clamped to `GetMaxCutoffForSampleRate = sampleRate/6` (958), result clamped to `[0.0001, MaxXAudioFilterFrequency = 1]`. `sampleRate < 6000` invalid → falls back to 44100 (952/960/1016). Per-voice `InputSampleRate` (964, via cached `VoiceDetails.InputSampleRate`) makes the live path more accurate than the static bank.
- `OneOverQ = ToXAudioOneOverQ(q)` (1025): `1/q` clamped to `[0.0001, MaxXAudioOneOverQ = 1.5]`. So user Q∈[0.1,10] → OneOverQ∈[0.1,1.5].

**Dynamics dispatch** `GetFilterParametersForEmitter` (1040): for `RSPEngineFilter` with `EngineFilterDynamic` it tries `V2EngineFilterModel.TryCalculateHullOnly` (when `IsHullOnlyFilterRoute`) then `TryCalculate`, taking `LowPass` at `sample.FinalCutoff/FinalQ` and recording `V2EngineFilterTelemetry`. If neither resolves (or dynamic off), it **gracefully degrades to static `Filter1*`/`Filter2*` slider values** (1069). The physical model (`V2EngineFilterModel`: air vs structure/hull path, energy-domain cutoff blend, vacuum collapse) supplies per-frame continuity.

### B4. Threading & timing
**No thread-safety machinery anywhere** — all reflection caches (`SourceVoiceProperties`, `SetFilterMethods`, …), the `EmitterVoiceBindings` dict, and diagnostic state are non-concurrent; every write is synchronous on the calling Harmony thread. The design assumes Keen drives `UpdateFilter`/`SelectEffect`/`SetSound` from a single audio-update thread. Order: `SelectEffect` (RSP injects subtype) → `SetSound` (RSP records binding) → `UpdateFilter` Prefix (RSP pre-loads `SoundEffect`) → engine applies → Postfix (RSP overwrites live voice). If Keen ever multithreads `UpdateFilter`, the shared dictionaries can corrupt.

### B5. Emitter↔voice resolution
`TryResolveEmitter` (257) is three-tier with per-tier counters: (1) registered binding (`TryResolveRegisteredEmitter`; validated by 120 s lifetime + `IsEmitterUsable` + `EmitterOwnsVoice`); (2) the wrapper object directly; (3) the native voice's reflected emitter member (`ResolveSourceVoiceEmitterMember`, 752 — `Emitter/m_emitter/SoundEmitter/m_soundEmitter`, then any "emitter"-named member holding a `MyEntity3DSoundEmitter`/`IMy3DSoundEmitter`). Every candidate confirmed via `EmitterOwnsVoice` (387: `emitter.Sound == voice || emitter.SecondarySound == voice`) to reject stale recycled bindings. Cache bounded at `MaxEmitterVoiceBindings = 1024` and **`Clear()`s wholesale on overflow** (322–323) — under heavy churn can briefly degrade to the slower reflective tiers.

### B6. Reverb DSP POC — active vs inactive (`V2GlobalReverbRuntime.cs`)
Five mutually-exclusive routes:
- **Live default `custommaster`** (`EnsureGlobalBusRoute` → `EnsureCustomInlineRoute(..., "master")`, 906): custom XAPO `V2LiveReverbPocProcessor` attached **in-place to the master voice** via reflected `SetEffectChain` + `EnableEffect(0)`; `processor.UpdateFromSettings` pushed each frame. `custominline` (909) = same on the game submix. Real-time FDN (8 delay lines + Hadamard + early taps + echo), `StartupRampSeconds = 0.35f`, **panic-clear** (NaN/Inf or |x|>8 → `ClearBuffers`, `_panicClearCount`) makes whole-master routing acceptable.
- **Inactive paths** (compile-time `false`, lines 20–22): `CompatGameSubmixRoutingEnabled`; `SourceReverbVoiceRoutingEnabled` (3D source-voice routing crashed XAudio2); `GlobalBusDirectSourceRoutingEnabled` (`SetOutputMatrix`/`SetOutputVoices` on live voices crashed — `srcRoute=disabled-setmatrix-crash`). The managed tail-copy path (`V2ManagedDspReverbRuntime`, `managed` route) and streaming `Fx.Reverb` global-bus path build but auto-wet send has **no callers** (dormant).
- **`SetReverbParameters` no-op confirmed in source.** `Resolve` caches it as no-op via `IsNoOp` (3773): `il != null && il.Length == 1 && il[0] == 0x2A` (single `ret`). Surfaced as `wrapperParams=noop` (514). This is the raison d'être of the custom-XAPO pivot; the legacy `Update()` path that would invoke it has no live callers.

### B7. Correctness / perf pitfalls
- **No smoothing, no rate-limit.** Every write uses operationSet `0` (immediate) → **zipper noise** on rapid distance/pressure changes. The only smoothing (`V2PlayerFilterRuntime.SmoothFilterParameters`, 799) is a *separate* runtime; the engine-filter dynamic path relies on the model's per-frame continuity.
- **fs/6 vs 8000 Hz mismatch.** Sliders advertise `MaxFilterFrequency = 8000` but effective cutoff is capped at `sampleRate/6` (~7350 Hz @ 44.1k) — the top of the range is never honored.
- **`Invoke` per voice per frame.** `setFilter.Invoke(..., new[] { parameters, 0 })` allocates an `object[]` and boxes the int every call. Handles are cached, but it's still `MethodInfo.Invoke`, not a compiled delegate — per-voice, per-frame allocation + slow-path call on the audio thread.
- **Brittle hard-coded private names** (`m_effectBank`, `m_effects`, `SoundData.Sound`, `MyEffectInstance.UpdateFilter`, every `SoundEffect` field). A Keen rename silently self-disables (latched `_disabled`/`_loggedReflectionFailure`) → **silent feature loss**.
- **Template coupling.** `TryCreateFromTemplate` depends on vanilla `realShipFilter`/`LowPassCockpit`; if a sound mod removes both, the hand-built fallback (`VolumeCurve` null) may not match engine expectations.

---

## PART C — FUTURE DSP/AUDIO FEATURE OPTIONS

Prioritised by immersion-payoff-per-risk. Effort: **S** ≈ hours, **M** ≈ a day or two, **L** ≈ multi-session.

### TIER 1 — High payoff, low/known risk, on the existing live path

**C-1.1 Per-send filtered reverb (independent dry vs wet tone)** — *S on RSP-owned voices; M if tied to live routing*
- **Payoff:** Today the engine-filter biquad low-passes the *whole* voice, so a muffled engine feeds a muffled reverb. Real spaces send a dark *dry* but a bright early-reflection *wet*. `SetOutputFilterParameters(destVoice, …)` on the reverb-bus send filters dry and wet independently from one voice — without a second XAPO. **Biggest realism win on the path RSP already drives.**
- **Handles:** `Voice.SetOutputFilterParameters` (UNUSED) + `VoiceSendFlags.UseFilter` on the `VoiceSendDescriptor` (UNUSED). Needs per-source bus routing first (C-2.1).
- **Integrate:** alongside `TryApplyLiveFilterParameters` (cs:158) and `V2GlobalReverbRuntime.AppendVoiceSendDescriptor` / `V2ManagedDspReverbRuntime.TryPlayWetTail`. Reuse `ToXAudioFrequency`/`ToXAudioOneOverQ`.
- **Risk:** per-source live-voice routing is exactly what crashed. **Pursue first on the replay/tail voices RSP already owns** (zero crash surface).

**C-1.2 One-pole low-pass — REJECTED, document the limit** — *N/A (cut)*
- `FilterType.LowPassOnePoleFilter` is **XAudio2.9-only; SE runs 2.7** — won't exist. Keep the four classic types. For ring-free muffling, *ramp the biquad `Frequency` smoothly* and hold `OneOverQ` near 1.0 (low resonance) — see C-1.3. Add a one-line code comment so the one-pole strings in settings/menu/docs aren't mistaken for working options.

**C-1.3 Coefficient smoothing / de-zipper on the engine-filter live path** — *S, near-pure-win*
- **Payoff:** Eliminate zipper noise; the only existing smoothing is the separate `V2PlayerFilterRuntime.SmoothFilterParameters` (cs:799). Add a per-emitter target→current lerp (log-frequency) before the write.
- **Handles:** none new. Optionally batch multi-voice writes with a non-zero `operationSet` + a single `XAudio2.CommitChanges(set)` (currently always `0`).
- **Integrate:** `GetFilterParametersForEmitter` (cs:1040) / `TryApplyLiveFilterParameters` (cs:158); store last-applied coefficients in the emitter-binding cache.
- **Risk:** trivial; honors the "no pops" changelog direction. Keep per-emitter state in the existing single-threaded dictionaries.

**C-1.4 Native pitch shaping for thruster realism (`SetFrequencyRatio`)** — *M*
- **Payoff:** RSP's #1 direction. Real-time resample (cap 2.0) for RPM/spool-up/spin-down on thruster & tool loops, driven by the V2 model — Doppler-on-throttle and load-based pitch authors never baked in.
- **Handles:** `SourceVoice.SetFrequencyRatio(float, int)` (UNUSED); `MySourceVoice.FrequencyRatio` setter honors `cue.DisablePitchEffects`. **Must read `MySoundData.DisablePitchEffects`** via `MyCueBank.GetCue` before writing.
- **Integrate:** thruster path (`ThrusterFilterPatch` / V2 thruster runtime), fed by the throttle/velocity signals the filter model uses; resolve native voice via the existing resolver.
- **Risk:** un-smoothed ratio changes warble (reuse C-1.3 smoothing). Engine clamps its own `DopplerFactor` to [0.9,1.0] in `Apply3D`, so RSP pitch stacks on top — tune conservatively.

**C-1.5 Master/game-bus limiter to tame additive wet stacking** — *S in-XAPO; M with stock limiter*
- **Payoff:** RSP sums wet tails + the custom master XAPO; transients can stack to a deafening blast. A brickwall limiter lets you push wet send harder safely.
- **Handles:** engine-native `MyXAudio2.EnableMasterLimiter(bool)` (UNUSED, gated behind `UseVolumeLimiter`); or `Fx.MasteringLimiter` XAPO via `SetEffectChain`. **Caution:** `custommaster` already owns the master chain slot 0 — adding a limiter means a 2-slot chain (reverb→limiter), channel counts matched. Cheapest: bake soft-limiting into `V2LiveReverbPocProcessor.Process` (already has `SoftLimit`/`ClampOutput`) — zero new handles.
- **Integrate:** `V2GlobalReverbRuntime.TryAttachCustomInlineEffect` (cs:1459) — build a 2-element `EffectDescriptor[]`.
- **Risk:** `Fx.MasteringLimiter` availability on 2.7 **UNVERIFIED** — probe with a `V2ReverbDiagnosticPing`-style ctor fallback. In-XAPO path has none.

### TIER 2 — High payoff, real research/risk (the routing & X3DAudio frontier)

**C-2.1 True per-voice reverb sends (finish the abandoned global bus)** — *L*
- **Payoff:** Route each emitter to a parallel wet `SubmixVoice` at a per-voice send gain so reverb depth tracks each source's distance/occlusion — instead of one master-wide wet.
- **Handles:** `MySourceVoice.SetOutputVoices`/`CurrentOutputVoices` (read first to preserve sends), `VoiceSendDescriptor`, a 48 kHz wet `SubmixVoice`, `SetOutputMatrix`. All present; all `*Enabled=false`.
- **Integrate:** re-enable `GlobalBusDirectSourceRoutingEnabled`/`SourceReverbVoiceRoutingEnabled` in `V2GlobalReverbRuntime` (cs:21–22, 999, 2345).
- **Risk: the known-crashing path** (`srcRoute=disabled-setmatrix-crash`) — mutating *currently-playing* voices fights Keen's `Apply3D` each frame. De-risk by: (a) only route voices RSP itself spawns (managed-replay already does this safely); (b) hook `SetSound`/`PlaySound` to add the send *at voice creation* before the engine drives it; (c) read `CurrentOutputVoices` and **append** rather than replace. Still RT-fragile across SE updates.

**C-2.2 X3DAudio-driven occlusion / reverb-send / Doppler** — *L*
- **Payoff:** The single biggest *unexploited* lever. OR `CalculateFlags.LpfDirect | LpfReverb | ReverbVolume` into `m_calculateFlags` (currently `Matrix | Doppler`) and `X3DAudio.Calculate` hands you per-source `DspSettings.LpfDirectCoefficient` (engine-grade distance LPF), `LpfReverbCoefficient`, `ReverbLevel` (auto wet-send by distance). Could subsume large parts of `V2EngineFilterModel`.
- **Handles:** `m_calculateFlags` (UNUSED, reflected), `Apply3D` (private, Harmony-patchable to read `DspSettings` post-calc), `X3DAudio.Calculate`, `DspSettings.{LpfDirectCoefficient,LpfReverbCoefficient,ReverbLevel,DopplerFactor}`, emitter `Cone` for directional occlusion.
- **Integrate:** Harmony postfix on `MyXAudio2.Apply3D`, or mutate `m_calculateFlags` once at init.
- **Risk: high.** `Apply3D` may be JIT-inlined (patch `Update3DCuesState`/`Update` one level up if so). Mutating `m_calculateFlags` changes engine-wide behavior. `ReverbLevel` only means something if there's a reverb *send* — couples to C-2.1. If it lands, the most physically correct, lowest-per-frame-cost occlusion possible.

**C-2.3 Convolution / true early-reflection reverb vs the FDN POC** — *L, lower ROI*
- **Payoff:** Convolution against per-material IRs for hyper-realistic hangars/corridors.
- **Handles:** still a custom `AudioProcessor` (RSP owns one). Convolution = FFT overlap-add in `Process`.
- **Risk:** RT-`Process` forbids alloc/locks/IO — an FFT convolver needs partitioned pre-allocated buffers in `LockForProcess`, materially more CPU on the master bus, and a NaN blasts the whole mix (panic-clear is the only net). IRs must be authored/shipped. The current FDN already sounds plausible and is ray-parameterised — **high-effort for marginal gain. Defer.**

### TIER 3 — Genuine immersion FX, moderate effort, additive

**C-3.1 Bit-crush / comms-radio FX for voice/intercom** — *S band-pass / M full XAPO*
- Sample-rate reduction + band-pass + light distortion for radio/intercom/damaged comms. Doable in a custom XAPO, or the cheap path via the existing `FilterType.BandPassFilter` (supported by `ToSharpFilterType`, just not produced) + decimation. Integrate as a comms submix (cues classified via `V2AuxCueClassifier`/`MyCueId`) or a `BandPass` branch in `GetFilterParametersForEmitter`. No live-voice routing crash surface if applied as a submix.

**C-3.2 Saturation / overdrive for damage & engine overload** — *M*
- Soft-clip/waveshaper increasing with thruster overload / low-health / damage. `V2LiveReverbPocProcessor` already has `SoftClip`/`SoftLimit` to model from. Better as a per-source submix than master-bus drive (which colors everything). RT-safe; smooth the drive parameter to avoid clicks.

**C-3.3 Multi-band parametric EQ for hull-material tone** — *M*
- Per-material/room coloration beyond a single biquad. `SharpDX.XAPO.Fx.Equalizer` + `EqualizerParameters` (4-band, UNUSED, chainable like reverb) — **availability on 2.7 UNVERIFIED, probe first.** Safer fallback: biquad cascade in the owned XAPO (guaranteed). Couples to ray/material classification.

**C-3.4 Sidechain / dynamic ducking** — *M*
- Duck ambience/music under explosions/alarms. XAudio2 has **no native sidechain** — build it: `Fx.VolumeMeter` (UNUSED, **probe**) on a key bus → drive `MySourceVoice.VolumeMultiplier` (clean post-multiply, UNUSED) or `SetVolume` on the ducked bus from `AudioEngineV2Runtime.Update`. RSP already enumerates live voices. Cross-thread fine since volume writes are on the main thread; attack/release hand-rolled.

**C-3.5 Discrete echo/slapback for canyons & large hangars** — *S*
- Pre-delay slap distinct from the diffuse tail. `SharpDX.XAPO.Fx.Echo` + `EchoParameters` (UNUSED), **or** it's already partially present — `V2LiveReverbPocProcessor` has a cross-fed echo line. **Easiest:** widen/scale that existing echo line by ray-measured `EquivalentRadius` (already does `18 + radius*6.5`) — no new handle, no probe risk.

### TIER 4 — Foundational / enabling

**C-4.1 Time-varying filter/volume envelopes via the engine effect system** — *M*
- Engine spool-up sweeps, startup-pop fades, scheduled envelopes — *offloaded to the engine*. Populate `MyAudioEffect.SoundEffect.VolumeCurve` (`VRageMath.Curve`) + non-zero `Duration` (RSP only writes `Duration=0`); drive via `IMyAudio.ApplyEffect`/`MyEffectInstance` with `AutoUpdate`/`SetPositionRelative`/`OnEffectEnded` (all UNUSED). Extend `ConfigureFilter` (cs:616). Risk: trusting the engine scheduler; partial migration risks two systems writing the same voice's filter. A robustness play more than a new sound.

**C-4.2 Inject synthesised PCM as positioned 3D voices** — *M*
- Play RSP-generated tails / procedural layers / impacts as spatialised engine voices (vanilla 3D/attenuation for free). `MyEntity3DSoundEmitter.PlaySound(byte[], volume, maxDistance, D3)` + `IMySourceVoice.SubmitBuffer`/`StartBuffered` (cap 62), or `IMyAudio.GetSound(emitter, D3)`. Alternative playback for `V2ManagedDspReverbRuntime`'s wet tails (currently private SourceVoices). **Caveat:** the buffered ctor does NOT set `VoiceFlags.UseFilter` — injected voices can't carry a per-voice filter (fine for finished tails).

**C-4.3 Spectral / FFT analysis** — *S (VolumeMeter) / M (FFT)*
- Drive ducking/visualisation/adaptive EQ from real output spectrum. FFT inside a pre-allocated custom XAPO, or `Fx.VolumeMeter` for cheap RMS/peak. Mostly *enables* other features (C-3.4, debug overlays). Lower priority.

### Cross-cutting constraints for ALL of the above
- **No-op `SetReverbParameters`:** never route reverb params through it; use `SetEffectParameters` on the owned XAPO / stock effect.
- **XAudio 2.7 / SharpDX 4.0.1:** no one-pole filters, no reverb `SideDelay`/`DisableLateField`; reverb framerate 20–48 kHz, all time params referenced to 48 kHz (**build wet buses at 48 kHz**). Stock `XAPO.Fx.Reverb` unavailable (proven); assume any *other* stock XAPO (`Equalizer`/`Echo`/`MasteringLimiter`/`VolumeMeter`) is "unknown until probed."
- **RT `Process` contract:** zero alloc/locks/IO; pre-allocate in `LockForProcess`/`Initialize`; anything on the master bus keeps the panic-clear/clamp net.
- **Single audio/main thread:** non-concurrent caches/dictionaries; apply XAudio2 mutations from the main `Update()`; offload only ray/occlusion math via `Parallel` + `InvokeOnGameThread`.
- **Reflection fragility:** every private name is unversioned; new handles need the same try/catch-latch-to-disabled pattern. Cache `MethodInfo`→delegate, never `Invoke` per frame.

**Recommended sequencing:** C-1.3 (de-zipper) + C-1.5-in-XAPO first (pure-win, no probe) → C-1.4 (thruster pitch, core direction) → C-1.1 on RSP-owned voices → C-3.5/C-3.1 (cheap audible FX) → then the L-tier frontier C-2.1 → C-2.2 (together unlock per-source occlusion + reverb the right way). Defer C-2.3 convolution and C-4.x until the routing crashes (C-2.1) are solved.

---

## PART D — CURATED ONLINE REFERENCES

> Annotated, grouped, with key takeaways. UNVERIFIED items flagged. Era caveat throughout: **SharpDX.XAudio2 4.0.1 over XAudio2 2.7**, .NET Framework 4.x / x64, in-process unsandboxed plugin.

### D1. SE / VRage / Keen modding API + Pulsar plugin loader

- **PluginLoader `PluginInstance.cs`** — https://raw.githubusercontent.com/sepluginloader/PluginLoader/main/PluginLoader/PluginInstance.cs
  *Primary (loader source; archived June 2025, Pulsar is the live fork).* A plugin = any `VRage.Plugins.IPlugin` impl; loader reflects `IsAssignableFrom`, `Activator.CreateInstance` (parameterless ctor required), optional `as IHandleInputPlugin`, optional reflected `OpenConfigDialog()`/`LoadAssets(string)` via `AccessTools`, auto-registers `[MySessionComponentDescriptor]` types.
- **PluginLoader `Main.cs`** — https://raw.githubusercontent.com/sepluginloader/PluginLoader/main/PluginLoader/Main.cs
  *Primary.* Lifecycle: **ctor** very early (splash up, `MyAPIGateway.Session` null — cheap setup only); **`Init(object gameInstance)`** = the `MySandboxGame` instance, **apply Harmony patches here** (no live session yet); **`Update()`** every tick on the main thread (~60 Hz / 16.6 ms — drive the V2 state machine here, **guard it**: a `MemberAccessException` from your assembly auto-disables the plugin and invalidates its cache → fails soft after a SE rename); **`HandleInput()`** (main thread, keybinds); **`Dispose()`** on shutdown (unpatch Harmony, dispose XAudio2 voices/effects).
- **`VRage.Plugins.IPlugin` ref** — https://fresc81.github.io/SpaceEngineers/interface_v_rage_1_1_plugins_1_1_i_plugin.html — `void Init(object)`, `void Update()`; `Dispose()` from `IDisposable`.
- **Pulsar** — https://github.com/SpaceGT/Pulsar — *Primary, current loader (Pete's).* Same `IPlugin` contract; ships **Legacy / Interim / Modern** executables for different .NET runtimes/SE versions — **target the runtime matching the Pulsar variant the user runs.** Dev docs live in its Discord.
- **SE Wiki — Plugins** — https://spaceengineers.wiki.gg/wiki/Plugins — *Official.* Plugins are global (whole install), launcher-enabled, **unrestricted process access** (filesystem, network, private engine internals) unlike sandboxed workshop mods; clientside-only plugins work in MP for the local player without server install.
- **`MyAPIGateway.cs`** — https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/master/Sources/Sandbox.Common/ModAPI/MyAPIGateway.cs — useful fields once a session is live: `Session`, `Utilities` (`InvokeOnGameThread`, config IO), `Input`, `Parallel`. **All null until a world loads;** ~3-tick window where `Session.Player` is null for MP clients — null-guard.
- **Keen "Parallel Modding Guide"** — https://forum.keenswh.com/threads/parallel-modding-guide.7396636/ — *Official.* `Update()` is single main thread, 16.6 ms budget. Thread-safe off-thread: raycasts, value reads, physics force adds, voxel reads. **Main-thread-only:** world/entity/inventory/block mutation. `InvokeOnGameThread(Action)` returns to the game thread next tick (keep tiny). `MyAPIGateway.Parallel` `Do`/`Start`/`StartBackground`; **callbacks fire only on the main thread.** **Never call XAudio2/SharpDX voice mutation from a background thread.**

### D2. Audio engine internals (what RSP patches)

- **`VRage.Audio/MyXAudio2.cs`** — https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/master/Sources/VRage.Audio/MyXAudio2.cs — *Primary (the engine RSP hooks).* Imports `SharpDX.XAudio2.Fx`, `SharpDX.XAPO.Fx`. Engine = `new XAudio2(XAudio2Version.Version27)`. Topology: one `MasteringVoice` + `m_gameAudioVoice`/`m_musicAudioVoice`/`m_hudAudioVoice`. A `Fx.Reverb m_reverb` attaches to the **game submix** via `SetEffectChain`, toggled `EnableEffect(0)`/`DisableEffect(0)`. `SetReverbParameters(diffusion, roomSize)` is **commented out / empty** → RSP must drive params itself. Candidate Harmony targets: `PlaySound(MyCueId)`, `Update(...)`, `Update3DCuesPositions()`, voice `EnableEffect/DisableEffect`.
- **`VRage.Audio/NativeSourceVoice.cs`** — https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/master/Sources/VRage.Audio/NativeSourceVoice.cs — *Primary.* Per-voice surface: `SetFrequencyRatio`, `SetOutputMatrix`, `EnableEffect/DisableEffect`, `SetOutputFilterParameters`/`GetOutputFilterParameters`. **Caveat:** the wrapper's `FilterParameters` property **throws `NotImplementedException`**, and `SetFilterParameters` **requires the voice created with `VoiceSendFlags.UseFilter`** — if absent, go through the underlying SharpDX `SourceVoice` (reflected out) or a submix chain. (Reconciles with RSP using `MySourceVoice.Voice` directly.)
- **SharpDX.XAudio2 4.0.1 NuGet** — https://www.nuget.org/packages/SharpDX.XAudio2/4.0.1 — confirms the exact managed binding SE bundles; 4.0.1 maps to XAudio2 2.7.
- **SE shipped binding `SharpDX.XAudio2.xml`** — https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/master/3rd/SharpDX/SharpDX.XAudio2.xml — confirms SE ships SharpDX 4.x; `RealisticSoundPlus.csproj` (lines 41–46) references `$(GameBin)\SharpDX.dll` + `SharpDX.XAudio2.dll`.

### D3. Microsoft Learn — authoritative DSP parameter references

- **XAudio2 Audio Effects** — https://learn.microsoft.com/en-us/windows/win32/xaudio2/xaudio2-audio-effects — Effect chains via `SetEffectChain`; toggle `EnableEffect`/`DisableEffect`; retune live with `SetEffectParameters` **without audio interruption** (avoids restart pops). Effects consume/produce **FLOAT32 at the voice's sample rate**; only channel count may change. Built-ins: **Reverb** + Volume Meter only.
- **`XAUDIO2_FILTER_PARAMETERS`** — https://learn.microsoft.com/en-us/windows/win32/api/xaudio2/ns-xaudio2-xaudio2_filter_parameters — `{ Type; float Frequency; float OneOverQ; }`. **`Frequency = 2·sin(π·cutoffHz/sampleRate)`**, range [0, 1.0]; max usable cutoff = `sampleRate/6`. `OneOverQ` in (0, 1.5]. **Bypass = (1.0, 1.0, LowPass)** — ramp toward it to avoid pops. No `XAUDIO2_HELPER_FUNCTIONS` in managed land — compute the radian formula yourself. State-variable recurrence (Yl/Yb/Yh/Yn) documented for offline modeling.
- **`XAUDIO2_FILTER_TYPE`** — https://learn.microsoft.com/en-us/windows/win32/api/xaudio2/ne-xaudio2-xaudio2_filter_type — `LowPass/BandPass/HighPass/Notch` + (2.9-only) `LowPassOnePole/HighPassOnePole`. **Don't rely on one-pole on SE.**
- **`XAUDIO2FX_REVERB_PARAMETERS`** — https://learn.microsoft.com/en-us/windows/win32/api/xaudio2fx/ns-xaudio2fx-xaudio2fx_reverb_parameters — full ranges: `WetDryMix` 0–100; `ReflectionsDelay` 0–300 ms; `ReverbDelay` 0–85 ms; `RearDelay`/`SideDelay` 0–5 ms; `PositionLeft/Right`+matrices 0–30; `Early/LateDiffusion` 0–15; `LowEQGain` 0–12/`LowEQCutoff` 0–9 (50–500 Hz); `HighEQGain` 0–8/`HighEQCutoff` 0–14 (1–8 kHz); `RoomFilterFreq` 20–20000 Hz; `RoomFilterMain`/`RoomFilterHF` −100..0 dB; `Reflections/ReverbGain` −100..+20 dB; `DecayTime` ≥0.1 s; `Density` 0–100%; `RoomSize` 1–100 ft; `DisableLateField` BOOL. **All time params referenced to 48 kHz — build the reverb submix at 48 kHz to avoid silent rescaling.** **2.7 caveat: `DisableLateField` unsupported, `SideDelay` Win10/2.9-only.** Map ray/occlusion data onto `RoomSize`+`DecayTime`+`WetDryMix`+`RoomFilterFreq`/`RoomFilterHF`.
- **I3DL2 params + `ReverbConvertI3DL2ToNative`** — https://learn.microsoft.com/en-us/windows/win32/api/xaudio2fx/ns-xaudio2fx-xaudio2fx_reverb_i3dl2_parameters , https://learn.microsoft.com/en-us/windows/win32/api/xaudio2fx/nf-xaudio2fx-reverbconverti3dl2tonative — pick a preset → fill I3DL2 struct → convert → `SetEffectParameters`. Recommended over hand-tuning 23 native fields. *(Note: the 7.1 `sevenDotOneReverb` variant is 2.9-only; the basic conversion is fine on 2.7.)*
- **`XAUDIO2FX_I3DL2_PRESET`** — https://learn.microsoft.com/en-us/windows/win32/xaudio2/xaudio2fx-i3dl2-preset — full preset list. For SE interiors/corridors, **HANGAR / STONECORRIDOR / HALLWAY / SMALLROOM** are the natural anchors to interpolate between by ray-derived room size.
- **How to: Create an Effect Chain** — https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/xaudio2/how-to--create-an-effect-chain.md — attach pattern + the channel-count match constraint on chain updates.
- **How to: Create an XAPO** — https://learn.microsoft.com/en-us/windows/win32/xaudio2/how-to--create-an-xapo — RT DSP contract: allocate all buffers in `LockForProcess`; `Process` is non-blocking RT (no alloc/locks/IO); interleaved float32; honor `XAPO_BUFFER_VALID`/`SILENT`, propagate `ValidFrameCount`/`BufferFlags`.

### D4. SharpDX source (the managed wrappers in use)

- **`AudioProcessorBase<T>`** — https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.XAudio2/XAPO/AudioProcessorBase.cs — *Primary (the base RSP's XAPO derives from).* Generic `T` = param struct (`Parameters` marshals via `Utilities.Read/Write`). Override `Process(BufferParameters[] inputs, outputs, bool isEnabled)` (+ optional `LockForProcess`/`Initialize`/`Reset`/`IsInputFormatSupported`/`CalcInput/OutputFrames`/`GetRegistrationProperties`). `BufferParameters.Buffer` is an `IntPtr` to interleaved float32 — unsafe pointers, **no alloc in `Process`.** Attach via voice ctor `effectDescriptors:` or `SetEffectChain(new EffectDescriptor(processor){ OutputChannelCount=n, InitialState=true })`, then `SetEffectParameters`/`EnableEffect`.
- **`Voice.cs` / `SubmixVoice.cs`** — https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.XAudio2/Voice.cs , https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.XAudio2/SubmixVoice.cs — confirms managed signatures: `SetFilterParameters`, `SetEffectChain`, `SetEffectParameters`, `EnableEffect/DisableEffect`, `SetOutputVoices`. `SubmixVoice(device, inputChannels, inputSampleRate, flags, processingStage, effectDescriptors)` — `processingStage` orders submixes (higher = later) for source→submix→submix chains.
- **Voices overview / submix voices** — https://learn.microsoft.com/en-us/windows/win32/xaudio2/voices — graph = source → (optional) submix → mastering; sends/output matrix let a source feed both a dry path and a wet reverb submix at independent gains (the standard dry+wet architecture).

### D5. Harmony patching + reflection performance

- **Harmony — Edge Cases** — https://harmony.pardeike.net/articles/patching-edgecases.html — *Official.* **Inlined methods can't be patched** (patch one level up). **Native/P-Invoke methods have no IL** — patch the managed wrapper (`SetFilterParameters`/`SetEffectChain`), not the native call. **Uncaught exceptions in a patch crash the game** — wrap every per-frame body in try/catch, fail silent. Patch *after* audio init, not at assembly load. `base.Method()` from a patch resolves to the override — use reverse patches for the original.
- **Harmony — Postfix patterns** — https://harmony.pardeike.net/articles/patching-postfix.html — `__result`, `__instance`, `___fieldName` (private field inject), original param names. **Prefer postfix** (always runs, most mod-compatible) — exactly RSP's `SelectEffect`/`UpdateFilter` postfix approach.
- **Harmony — Transpiler / perf** — https://harmony.pardeike.net/articles/patching-transpiler.html (+ issue https://github.com/pardeike/Harmony/issues/453) — keep prefix/postfix bodies tiny, no LINQ/allocs in the per-frame audio loop; reserve transpilers for unavoidable IL edits.
- **Optimize C# Reflection with Delegates** — https://www.automatetheplanet.com/optimize-csharp-reflection-using-delegates/ — *Secondary (practitioner).* Cache `MethodInfo`/`FieldInfo` once; `MethodInfo.CreateDelegate` → ~direct-call speed (vs ~30× slower `Invoke`); compiled `Expression` getters/setters for fields; `[UnsafeAccessor]` on .NET 8+ (Pulsar "Modern"). **Never `Invoke`/`GetValue` in the audio loop** — directly relevant to RSP's B7 `setFilter.Invoke` per-frame allocation pitfall.
- **Harmony version (UNVERIFIED — not fetched this session)** — PluginLoader/Pulsar pin **Harmony `2.3.3.0`** and warn on mismatch. Construct your own `Harmony` in `Init`, `UnpatchAll(yourId)` in `Dispose`; use `AccessTools`; prefer prefixes/postfixes over transpilers so the `MemberAccessException` soft-disable can locate your assembly.

### D6. XAudio2 versions & community infrastructure

- **XAudio2 Versions** — https://learn.microsoft.com/en-us/windows/win32/xaudio2/xaudio2-versions — *Official.* Confirms the era constraints: `SideDelay` + 7.1 reverb are 2.9-only; `DisableLateField` not supported on DirectX-SDK 2.7. Keep reverb/filter code to the 2.7 subset and guard newer fields.
- **Pulsar / SEHarmonyWrapper / SE performance plugin** — https://github.com/SpaceGT/Pulsar , https://github.com/790/SEHarmonyWrapper , https://github.com/viktor-ferenczi/se-performance-improvements — *Community (primary repos).* Pulsar = maintained PluginLoader fork (Legacy/Interim/Modern). SEHarmonyWrapper distributes `0Harmony.dll` to `Bin64`. viktor-ferenczi's plugin is a well-regarded example of Harmony-patching SE internals (patch lifecycle / target selection).
- **No prior community art** for per-voice XAudio2 filter/reverb/occlusion via Harmony. Closest artifacts are general sound-content mods (e.g. SlimmestCognito/Space-Engineers-Sound-Mod) and FAudio stutter threads — neither touches the DSP layer. **RSP is effectively novel territory;** lean on Keen `VRage.Audio` source + MS Learn, not prior art.

### D7. Synthesis — key takeaways for RSP

1. **Lifecycle:** implement `VRage.Plugins.IPlugin` (+ `IHandleInputPlugin` for hotkeys). Cheap ctor → patch in `Init(gameInstance)` → per-tick logic in `Update()` (main thread, 16.6 ms) → unpatch + dispose XAudio2 in `Dispose()`. Optionally expose `OpenConfigDialog()`/`LoadAssets(string)` by convention.
2. **Thread discipline:** all XAudio2/SharpDX mutation on the main `Update()` thread. Offload only ray/occlusion math via `Parallel.StartBackground` → `InvokeOnGameThread`. Null-guard `MyAPIGateway.Session` (+ the 3-tick `Session.Player` MP gap).
3. **Unsandboxed:** hook `MyXAudio2`'s private submixes / `m_reverb` via Harmony+reflection — no public audio ModAPI. Treat all private surface as breakable; the `MemberAccessException` soft-disable is the net (but null-check too).
4. **DSP seams (no pops):** retune `ReverbParameters` and per-voice `SetFilterParameters` *live* (`SetEffectParameters` updates without interruption — no chain rebuild). Stay FLOAT32 at the voice rate; honor the 2.7 limits (no `DisableLateField`/`SideDelay`/one-pole; reverb 20–48 kHz, time params referenced to 48 kHz; one stateful reverb APO per source).
5. **Reflection discipline:** resolve `MethodInfo`/`FieldInfo` once at init, cache `CreateDelegate`/compiled-`Expression` accessors, never `Invoke`/`GetValue` per frame (addresses RSP's current per-frame boxing in `TryApplyLiveFilterParameters`).