using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus
{
    internal sealed class RspSettingsMenu : MyGuiScreenBase
    {
        private const float Width = 0.82f;
        private const float Height = 0.86f;
        private const float ContentWidth = 0.76f;
        private const float ContentHeight = 7.32f;
        private const float ScrollHeight = 0.66f;

        private static readonly Vector4 Background = new Vector4(0.02f, 0.025f, 0.03f, 0.94f);
        private static readonly Vector4 SoftPanel = new Vector4(0.04f, 0.055f, 0.065f, 0.55f);
        private static readonly Vector4 EngineAudioPanel = new Vector4(0.09f, 0.17f, 0.23f, 0.55f);
        private static readonly Vector4 EngineFilterPanel = new Vector4(0.12f, 0.20f, 0.15f, 0.55f);
        private static readonly Vector4 AuxFilterPanel = new Vector4(0.18f, 0.14f, 0.23f, 0.55f);
        private static readonly Vector4 SharedPanel = new Vector4(0.18f, 0.18f, 0.13f, 0.55f);
        private static readonly Vector4 EnvironmentPanel = new Vector4(0.10f, 0.20f, 0.24f, 0.55f);
        private static readonly Vector4 BlockPanel = new Vector4(0.23f, 0.16f, 0.12f, 0.55f);
        private static readonly Vector4 ToggleOn = new Vector4(0.20f, 0.48f, 0.56f, 1f);
        private static readonly Vector4 ToggleOff = new Vector4(0.13f, 0.17f, 0.19f, 1f);

        private static RspSettingsMenu _open;
        private static float _lastScrollY;

        private readonly List<SliderBinding> _sliders = new List<SliderBinding>();
        private readonly List<ToggleBinding> _toggles = new List<ToggleBinding>();
        private readonly List<FilterBinding> _filters = new List<FilterBinding>();
        private readonly List<ReadoutBinding> _readouts = new List<ReadoutBinding>();
        private MyGuiControlScrollablePanel _scrollPanel;
        private Vector4 _currentAccent = EngineAudioPanel;
        private bool _syncing;

        public static bool IsOpen => _open != null && _open.State != MyGuiScreenState.CLOSED;

        public static void Toggle()
        {
            if (IsOpen)
            {
                _open.CloseScreen(false);
                return;
            }

            _open = new RspSettingsMenu();
            MyGuiSandbox.AddScreen(_open);
        }

        public static void CloseIfOpen()
        {
            if (IsOpen)
                _open.CloseScreen(false);
            _open = null;
        }

        private RspSettingsMenu()
            : base(new Vector2(0.5f, 0.5f), Background, new Vector2(Width, Height), true)
        {
            EnabledBackgroundFade = true;
            DrawMouseCursor = true;
            CanHideOthers = false;
            CloseButtonEnabled = true;
            CloseButtonOffset = new Vector2(-0.02f, 0.02f);
            RecreateControls(true);
        }

        public override string GetFriendlyName()
        {
            return "RspSettingsMenu";
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            SaveScrollPosition();
            Controls.Clear();
            _sliders.Clear();
            _toggles.Clear();
            _filters.Clear();
            _readouts.Clear();
            _scrollPanel = null;

            Add(Controls, Label("Realistic Sound+ V2 Settings", new Vector2(-0.395f, -0.395f), 0.92f, "Blue"));
            Add(Controls, Label("Runtime changes apply immediately. Use Save to write XML.", new Vector2(-0.395f, -0.358f), 0.62f, "White"));

            MyGuiControlParent content = new MyGuiControlParent(
                position: Vector2.Zero,
                size: new Vector2(ContentWidth, ContentHeight),
                backgroundColor: SoftPanel,
                toolTip: null);
            content.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;

            BuildScrollableContent(content);

            MyGuiControlScrollablePanel scroll = new MyGuiControlScrollablePanel(content);
            scroll.Position = new Vector2(0f, -0.02f);
            scroll.Size = new Vector2(ContentWidth + 0.02f, ScrollHeight);
            scroll.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
            scroll.ScrollbarVEnabled = true;
            scroll.ScrollbarHEnabled = false;
            scroll.RefreshInternals();
            scroll.SetVerticalScrollbarValue(_lastScrollY);
            scroll.PanelScrolled += OnPanelScrolled;
            _scrollPanel = scroll;
            Add(Controls, scroll);

            AddBottomButtons();
            SyncControlsFromSettings();
        }

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
            PollControls();
            return result;
        }

        protected override void OnClosed()
        {
            SaveScrollPosition();
            if (ReferenceEquals(_open, this))
                _open = null;
            base.OnClosed();
        }

        private void OnPanelScrolled(MyGuiControlScrollablePanel panel)
        {
            if (panel != null)
                _lastScrollY = Math.Max(0f, panel.ScrollbarVPosition);
        }

        private void SaveScrollPosition()
        {
            if (_scrollPanel != null)
                _lastScrollY = Math.Max(0f, _scrollPanel.ScrollbarVPosition);
        }

        private void BuildScrollableContent(MyGuiControlParent content)
        {
            float y = -ContentHeight * 0.5f + 0.035f;

            AddMajorSection(content, ref y, "Engine Audio", EngineAudioPanel);
            AddSection(content, ref y, "Routes");
            AddToggle(content, ref y, "Engine Detail", () => SettingsManager.Current.V2DetailEnabled, value => SettingsManager.Current.V2DetailEnabled = value, "Adds positional thruster detail emitters to the V2 engine mix.");
            AddToggle(content, ref y, "Detail Idle", () => SettingsManager.Current.V2DetailIdleEnabled, value => SettingsManager.Current.V2DetailIdleEnabled = value, "Keeps low-thrust idle detail audible; off quiets inactive engines faster.");
            AddToggle(content, ref y, "Detail 2D Positional", () => SettingsManager.Current.V2Detail2DPositionalTest, value => SettingsManager.Current.V2Detail2DPositionalTest = value, "Uses vanilla 2D detail cues from 3D emitters for route testing.");
            AddToggle(content, ref y, "Engine State", () => SettingsManager.Current.V2StateEnabled, value => SettingsManager.Current.V2StateEnabled = value, "Adds grouped ship-state layer; on thickens the engine bed.");
            AddToggle(content, ref y, "State 2D Positional", () => SettingsManager.Current.V2State2DPositionalTest, value => SettingsManager.Current.V2State2DPositionalTest = value, "Uses 2D state cues positionally for state-layer testing.");
            AddToggle(content, ref y, "Audio Overlay", () => AudioDebugOverlay.Enabled, AudioDebugOverlay.SetEnabled, "Shows live audio voices, routes, and gains on screen.");
            AddToggle(content, ref y, "Debug Log", () => SettingsManager.Current.V2DebugLogEnabled, value => SettingsManager.Current.V2DebugLogEnabled = value, "Writes V2 snapshots and events to the RSP debug log.");

            AddSection(content, ref y, "Gains And Curves");
            AddSlider(content, ref y, "Overall Gain", "gain", 0f, 4f, 2, () => SettingsManager.Current.EngineGain, "Master V2 engine gain; higher louder, lower more headroom.");
            AddSlider(content, ref y, "Detail Gain", "detailgain", 0f, 4f, 2, () => SettingsManager.Current.V2DetailGain, "Volume for thrust detail; higher more mechanical texture.");
            AddSlider(content, ref y, "Idle Gain", "idlegain", 0f, 4f, 2, () => SettingsManager.Current.V2DetailIdleGain, "Volume for idle detail; higher more standby hum.");
            AddSlider(content, ref y, "State Gain", "stategain", 0f, 4f, 2, () => SettingsManager.Current.V2StateGain, "Volume for state layer; higher fuller engine bed.");
            AddSlider(content, ref y, "Output Curve", "curve", 0.25f, 10f, 2, () => SettingsManager.Current.AudioCurveExponent, "Shapes thrust-to-volume; higher favors high thrust, lower raises low thrust.");
            AddSlider(content, ref y, "Presence Floor", "presence", 0f, 1f, 2, () => SettingsManager.Current.MinimumShipPresence, "Minimum small-thruster presence; higher keeps tiny thrusters audible.");

            AddSection(content, ref y, "Distance And Motion");
            AddSlider(content, ref y, "Legacy Emitter Distance", "dist", 1f, 1000f, 0, () => SettingsManager.Current.V2EmitterDistance, "Legacy fallback fade distance; engine V2 mainly uses air/hull ranges.", true);
            AddSlider(content, ref y, "Legacy Distance Curve", "distcurve", 0.1f, 5f, 2, () => SettingsManager.Current.V2DistanceCurve, "Legacy fallback fade shape; higher drops faster near range end.");
            AddSlider(content, ref y, "Command Smoothing", "cmdsmooth", 0f, 5000f, 0, () => SettingsManager.Current.V2DetailCommandSmoothingMs, "Smooths thrust input before detail audio; higher slower, lower snappier.");
            AddSlider(content, ref y, "Emitter Fade", "emitterfade", 0f, 1000f, 0, () => SettingsManager.Current.V2EmitterFadeInMs, "Fade-in for new emitters; higher softer starts.");
            AddSlider(content, ref y, "Volume Smoothing", "smooth", 0f, 500f, 0, () => SettingsManager.Current.V2SmoothingMs, "Smooths engine volume updates; higher steadier, lower more reactive.");
            AddSlider(content, ref y, "Soft Fade Width", "fade", 0.001f, 0.25f, 3, () => SettingsManager.Current.V2SoftFadeRatio, "Near-zero thrust crossfade; higher softer idle transitions.");

            AddMajorSection(content, ref y, "Engine Filter", EngineFilterPanel);
            AddSection(content, ref y, "Routing");
            AddFilterDropdown(content, ref y, "External Engine Route", () => SettingsManager.Current.EngineFilter, SettingsManager.TrySetFilter, "Filter applied to outside/contact engine emitters.");
            AddFilterDropdown(content, ref y, "Internal Engine Route", () => SettingsManager.Current.InternalEngineFilter, SettingsManager.TrySetInternalFilter, "Filter applied to inside-ship engine emitters.");
            AddToggle(content, ref y, "Dynamic Engine Filter", () => SettingsManager.Current.EngineFilterDynamic, value => SettingsManager.Current.EngineFilterDynamic = value, "Makes enginefilter follow distance, atmosphere, hull contact, and interior state.");
            AddReadout(content, ref y, "Static Fallback", FormatEngineStaticFallbackState, "Shows whether dynamic enginefilter is bypassing the static fallback.", 0.040f, 0.44f);
            AddCustomFilterTypeDropdown(content, ref y, "Fallback Static Type", () => SettingsManager.Current.Filter1Type, SettingsManager.TrySetFilter1Type, "Static enginefilter shape used only when dynamic mode is off.");
            AddSlider(content, ref y, "Fallback Static Freq", "enginefilterfreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.Filter1Frequency, "Static cutoff when dynamic is off; higher brighter, lower darker.", true);
            AddSlider(content, ref y, "Fallback Static Q", "enginefilterq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.Filter1Q, "Static resonance when dynamic is off; higher sharper, lower smoother.");
            AddFilterChart(content, ref y, "Live Engine Response", V2EngineFilterTelemetry.RepresentativeType, V2EngineFilterTelemetry.RepresentativeFrequency, V2EngineFilterTelemetry.RepresentativeQ, "Preview of current enginefilter frequency/Q, not an audio spectrum.");

            AddSection(content, ref y, "Engine Filter Live Inputs");
            AddToggle(content, ref y, "Atmosphere Override", () => SettingsManager.Current.V2AtmosphereOverrideEnabled, value => SettingsManager.Current.V2AtmosphereOverrideEnabled = value, "Forces V2 atmosphere input for enginefilter testing.");
            AddSlider(content, ref y, "External Atmosphere", "externalatm", 0f, 1f, 2, () => SettingsManager.Current.V2AtmosphereOverride, "Test pressure for enginefilter; 0 vacuum/dark, 1 air/bright.");
            AddReadout(content, ref y, "Environment", V2EngineFilterTelemetry.FormatEnvironment, "Live listener/source atmosphere and room data feeding enginefilter.", 0.050f);
            AddReadout(content, ref y, "Emitters", () => V2EngineFilterTelemetry.FormatEmitters(6), "Recent engine emitters and their dynamic filter outputs.", 0.145f, 0.36f);

            AddSection(content, ref y, "Engine Air Path");
            AddSlider(content, ref y, "Air Near Cutoff", "engineairnear", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterAirNearFrequency, "Brightest air-path cutoff near engines; higher clearer.", true);
            AddSlider(content, ref y, "Air Far Cutoff", "engineairfar", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterAirFarFrequency, "Darkest air-path cutoff at range; lower dulls distant engines.", true);
            AddSlider(content, ref y, "Air Filter Range", "engineairrange", 1f, 5000f, 0, () => SettingsManager.Current.EngineFilterAirRange, "Air-path distance span; higher carries brightness farther.", true);
            AddSlider(content, ref y, "Air Distance Curve", "engineaircurve", 0.1f, 5f, 2, () => SettingsManager.Current.EngineFilterAirDistanceCurve, "Air-path fade shape; higher delays high-frequency loss.");
            AddSlider(content, ref y, "Air Q", "engineairq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.EngineFilterAirQ, "Air-path resonance; higher sharper, lower smoother.");
            AddSlider(content, ref y, "Interior Air Blend", "engineinteriorair", 0f, 4f, 2, () => SettingsManager.Current.EngineFilterInteriorAirWeight, "Interior pressure contribution; higher makes sealed air less muffled.");

            AddSection(content, ref y, "Engine Hull Path");
            AddSlider(content, ref y, "Hull Near Cutoff", "enginehullnear", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterHullNearFrequency, "Structure path cutoff near engines; higher brighter hull sound.", true);
            AddSlider(content, ref y, "Hull Far Cutoff", "enginehullfar", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterHullFarFrequency, "Structure path cutoff far through grid; lower darker rumble.", true);
            AddSlider(content, ref y, "Hull Filter Range", "enginehullrange", 1f, 1000f, 0, () => SettingsManager.Current.EngineFilterHullRange, "Structure path distance span; higher carries hull sound farther.", true);
            AddSlider(content, ref y, "Hull Distance Curve", "enginehullcurve", 0.1f, 5f, 2, () => SettingsManager.Current.EngineFilterHullDistanceCurve, "Hull-path fade shape; higher delays darkening.");
            AddSlider(content, ref y, "Hull Q", "enginehullq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.EngineFilterHullQ, "Hull-path resonance; higher sharper metallic tone.");
            AddSlider(content, ref y, "Interior Cutoff Cap", "engineinteriorcutoff", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterInteriorMaxFrequency, "Max air-path cutoff indoors; lower makes interiors darker.", true);
            AddSlider(content, ref y, "Vacuum Contact Cutoff", "enginevacuumcutoff", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterVacuumContactFrequency, "Vacuum/contact fallback cutoff; lower deeper and duller.", true);

            AddMajorSection(content, ref y, "Player / Aux Filter", AuxFilterPanel);
            AddSection(content, ref y, "Aux Master Routes");
            AddToggle(content, ref y, "Player Filter", () => SettingsManager.Current.PlayerFilterEnabled, value => SettingsManager.Current.PlayerFilterEnabled = value, "Master switch for aux filters on env, blocks, and player-local sounds.");
            AddToggle(content, ref y, "Environment Bed", () => SettingsManager.Current.PlayerFilterEnvironmentEnabled, value => SettingsManager.Current.PlayerFilterEnvironmentEnabled = value, "Filters wind/weather ambience using env probe and pressure.");
            AddToggle(content, ref y, "Block Emitters", () => SettingsManager.Current.PlayerFilterBlockEnabled, value => SettingsManager.Current.PlayerFilterBlockEnabled = value, "Filters block/world sounds using source path, distance, and room state.");
            AddToggle(content, ref y, "Player Local", () => SettingsManager.Current.PlayerFilterLocalEnabled, value => SettingsManager.Current.PlayerFilterLocalEnabled = value, "Filters player-local sounds by pressure only.");
            AddToggle(content, ref y, "Block Ray Debug", () => SettingsManager.Current.PlayerFilterPathDebugEnabled, value => SettingsManager.Current.PlayerFilterPathDebugEnabled = value, "Draws block-source occlusion rays; green clear, red blocked.");

            _currentAccent = SharedPanel;
            AddSection(content, ref y, "Shared Aux Filter Shape");
            AddCustomFilterTypeDropdown(content, ref y, "Aux Type", () => SettingsManager.Current.Filter2Type, SettingsManager.TrySetFilter2Type, "Filter shape shared by env, block, and local aux routes.");
            AddSlider(content, ref y, "Aux Clear Cutoff", "auxfilterfreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.Filter2Frequency, "Clear aux cutoff before muffling; higher brighter baseline.", true);
            AddSlider(content, ref y, "Aux Q", "auxfilterq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.Filter2Q, "Shared aux resonance; higher sharper, lower smoother.");
            AddSlider(content, ref y, "Aux Smoothing", "auxsmooth", 0f, 5000f, 0, () => SettingsManager.Current.PlayerFilterSmoothingMs, "Smooths aux filter/volume changes; higher slower, lower snappier.");
            AddSlider(content, ref y, "Aux Occlusion Strength", "auxocclusionstrength", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterOcclusionStrength, "Global aux occlusion multiplier; higher more muffling.");
            AddFilterChart(content, ref y, "Aux Response", () => SettingsManager.Current.Filter2Type, () => SettingsManager.Current.Filter2Frequency, () => SettingsManager.Current.Filter2Q, "Preview of shared aux filter shape at clear cutoff/Q.");

            AddSection(content, ref y, "Shared Overrides And Sealed Rooms");
            AddToggle(content, ref y, "Aux Atmosphere Override", () => SettingsManager.Current.PlayerFilterAtmosphereOverrideEnabled, value => SettingsManager.Current.PlayerFilterAtmosphereOverrideEnabled = value, "Forces aux pressure input for env/block/local testing.");
            AddSlider(content, ref y, "Aux Sim Pressure", "auxatm", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterAtmosphereOverride, "Test pressure for aux; 0 vacuum/muffled, 1 air/clear.");
            AddSlider(content, ref y, "Sealed Environment Factor", "sealedenv", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentSealedFactor, "Extra wind muffling in airtight rooms; higher quieter sealed interiors.");
            AddSlider(content, ref y, "Sealed Blocks Factor", "sealedblock", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterBlockSealedFactor, "Extra block muffling outside sealed room; higher stronger door/wall contrast.");

            _currentAccent = EnvironmentPanel;
            AddSection(content, ref y, "Environment Occlusion Geometry");
            AddInlineReadout(content, ref y, V2PlayerFilterRuntime.FormatEnvironmentLiveReadout, "Live env output: covered sky, final muffling, and final volume.");
            AddSlider(content, ref y, "Probe Ray Length", "playerenvray", 5f, 1000f, 0, () => SettingsManager.Current.PlayerEnvRayLength, "Environment probe radius; higher samples farther openings.", true);
            AddSlider(content, ref y, "Env Structure Thickness", "envstructurethickness", 0.1f, 20f, 2, () => SettingsManager.Current.PlayerEnvStructureThicknessScale, "Wall thickness scale for env rays; higher lets thin cover leak more.");
            AddSlider(content, ref y, "Voxel Occlusion Weight", "voxelweight", 0f, 10f, 2, () => SettingsManager.Current.PlayerFilterVoxelOcclusionWeight, "Terrain/asteroid weight for env and block rays; higher more muffling, 0 off.");
            AddSlider(content, ref y, "Env Aperture Curve", "envaperturecurve", 0.1f, 10f, 2, () => SettingsManager.Current.PlayerEnvApertureCurve, "Shapes open-sky fraction; higher makes small openings count less.");

            AddSection(content, ref y, "Environment Bed");
            AddSlider(content, ref y, "Env Volume Muffle", "envvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentVolumeMuffleWeight, "Wind volume reduction from env muffle; higher fades harder, 0 tone only.");
            AddSlider(content, ref y, "Env Bed Minimum Gain", "envfloor", 0f, 0.5f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentMinGain, "Floor for RSP wind volume; higher keeps muffled wind audible.");
            AddSlider(content, ref y, "Env Muffled Cutoff", "envmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterEnvironmentMuffledFrequency, "Lowest wind cutoff under cover; higher keeps wind brighter.", true);
            AddReadout(content, ref y, "Environment Probe", V2PlayerEnvironmentTelemetry.FormatSummary, "Summary of env ray, voxel, pressure, and sealed-room output.", 0.060f, 0.38f);
            AddReadout(content, ref y, "Probe Details", V2PlayerEnvironmentTelemetry.FormatDetails, "Detailed env ray and oxygen-room data for sealed testing.", 0.088f, 0.34f);

            _currentAccent = BlockPanel;
            AddSection(content, ref y, "Block Emitters");
            AddSlider(content, ref y, "Block Structure Thickness", "blockstructurethickness", 0.1f, 20f, 2, () => SettingsManager.Current.PlayerFilterBlockStructureThicknessScale, "Wall thickness scale for block rays; higher reduces thin-obstacle muting.");
            AddSlider(content, ref y, "Block Occlusion Curve", "blockocclusioncurve", 0.1f, 5f, 2, () => SettingsManager.Current.PlayerFilterBlockOcclusionCurve, "Shapes block ray occlusion; higher forgives light blockage.");
            AddSlider(content, ref y, "Block Range Scale", "blockdistancescale", 0.1f, 100f, 2, () => SettingsManager.Current.PlayerFilterBlockRangeScale, "Multiplier for vanilla block cue range; higher carries sounds farther.");
            AddSlider(content, ref y, "Block Fallback Range", "blockrange", 1f, 1000f, 0, () => SettingsManager.Current.PlayerFilterBlockRange, "Range used only when vanilla cue distance is unknown.", true);
            AddSlider(content, ref y, "Block Travel Curve", "blockcurve", 0.1f, 5f, 2, () => SettingsManager.Current.PlayerFilterBlockDistanceCurve, "Distance fade over scaled range; higher stays loud then drops.");
            AddSlider(content, ref y, "Block Volume Muffle", "blockvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterBlockVolumeMuffleWeight, "Block volume cut from muffling; higher quieter through walls, 0 tone only.");
            AddSlider(content, ref y, "Block Muffled Cutoff", "blockmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterBlockMuffledFrequency, "Lowest block cutoff at heavy muffling; higher brighter.", true);
            AddReadout(content, ref y, "Block Range Gate", V2BlockRangeScaler.FormatStatus, "Shows vanilla distance-gate overrides for recent block/world cues.", 0.055f, 0.38f);
            AddReadout(content, ref y, "Block Candidates", V2AuxSourceOcclusionTelemetry.FormatSummary, "Summary of recent non-engine block/world source candidates.", 0.058f, 0.38f);
            AddReadout(content, ref y, "Block Source Detail", () => V2AuxSourceOcclusionTelemetry.FormatSources(6), "Per-source distance, room, seal, muffling, cutoff, and gain.", 0.150f, 0.34f);

            _currentAccent = SharedPanel;
            AddSection(content, ref y, "Player Local");
            AddSlider(content, ref y, "Local Volume Muffle", "localvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterLocalVolumeMuffleWeight, "Pressure-based volume cut for local sounds; higher quieter in vacuum.");
            AddSlider(content, ref y, "Local Muffled Cutoff", "auxmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterMuffledFrequency, "Cutoff for local pressure muffling; higher clearer, lower helmet-like.", true);

            AddSection(content, ref y, "Aux Applied Voices");
            AddReadout(content, ref y, "Applied Summary", V2PlayerFilterRuntime.FormatSummary, "Counts and strongest voice currently controlled by aux filter.", 0.060f, 0.38f);
            AddReadout(content, ref y, "Applied Detail", () => V2PlayerFilterRuntime.FormatSources(6), "Per-voice aux category, muffle, cutoff, gain, and range.", 0.150f, 0.34f);

            AddMajorSection(content, ref y, "Global Reverb Test", SoftPanel);
            AddToggle(content, ref y, "Global Reverb", () => SettingsManager.Current.GlobalReverbEnabled, value => SettingsManager.Current.GlobalReverbEnabled = value, "Enables experimental global XAudio reverb on routed game voices.");
            AddSlider(content, ref y, "Reverb Diffusion", "reverbdiffusion", 0f, 1f, 2, () => SettingsManager.Current.GlobalReverbDiffusion, "Reflection density; higher smoother/denser, lower sparse.");
            AddSlider(content, ref y, "Reverb Room Size", "reverbroomsize", 0f, 1f, 2, () => SettingsManager.Current.GlobalReverbRoomSize, "Reverb size; higher longer/larger, lower smaller/tighter.");
            AddReadout(content, ref y, "Reverb Runtime", V2GlobalReverbRuntime.FormatStatus, "Status of global reverb hook and parameter writes.", 0.055f, 0.40f);
            AddReadout(content, ref y, "Reverb Affected Voices", () => V2GlobalReverbRuntime.FormatAffectedVoices(8), "Live voices routed through the reverb submix.", 0.150f, 0.40f);

            AddSection(content, ref y, "Ship Scaling");
            AddSlider(content, ref y, "Quiet Force Log", "quietlog", 1f, 10f, 2, () => SettingsManager.Current.QuietShipForceLog10, "Thruster force treated as quiet baseline; higher makes small ships quieter.");
            AddSlider(content, ref y, "Loud Force Log", "loudlog", 1f, 12f, 2, () => SettingsManager.Current.LoudShipForceLog10, "Thruster force treated as loud baseline; higher needs more force to max.");
        }

        private void AddBottomButtons()
        {
            Add(Controls, Button("Save", new Vector2(-0.29f, 0.38f), "Write current runtime settings to XML.", button =>
            {
                SettingsManager.Save();
                Notify("Saved settings.");
                SyncControlsFromSettings();
            }));

            Add(Controls, Button("Reload", new Vector2(-0.095f, 0.38f), "Load XML settings and refresh this menu.", button =>
            {
                SettingsManager.Load();
                Notify("Reloaded settings.");
                SyncControlsFromSettings();
            }));

            Add(Controls, Button("Show", new Vector2(0.095f, 0.38f), "Print current runtime settings to chat.", button =>
            {
                Notify(SettingsManager.Summary());
            }));

            Add(Controls, Button("Log Path", new Vector2(0.29f, 0.38f), "Print RSP debug log path to chat.", button =>
            {
                Notify(V2DebugLog.Path);
            }));
        }

        private void AddSection(MyGuiControlParent content, ref float y, string title)
        {
            y += 0.027f;
            MyGuiControlParent panel = new MyGuiControlParent(
                position: new Vector2(0f, y),
                size: new Vector2(0.70f, 0.026f),
                backgroundColor: WithAlpha(_currentAccent, 0.16f),
                toolTip: null);
            panel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
            Add(content.Controls, panel);

            MyGuiControlParent stripe = new MyGuiControlParent(
                position: new Vector2(-0.351f, y),
                size: new Vector2(0.006f, 0.026f),
                backgroundColor: WithAlpha(_currentAccent, 0.80f),
                toolTip: null);
            stripe.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
            Add(content.Controls, stripe);

            Add(content.Controls, Label(title, new Vector2(-0.333f, y), 0.66f, "White"));
            y += 0.031f;
        }

        private void AddMajorSection(MyGuiControlParent content, ref float y, string title, Vector4 accent)
        {
            _currentAccent = accent;
            y += 0.040f;
            MyGuiControlParent panel = new MyGuiControlParent(
                position: new Vector2(0f, y),
                size: new Vector2(0.70f, 0.034f),
                backgroundColor: accent,
                toolTip: null);
            panel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER;
            Add(content.Controls, panel);

            MyGuiControlParent stripe = new MyGuiControlParent(
                position: new Vector2(-0.351f, y),
                size: new Vector2(0.008f, 0.034f),
                backgroundColor: WithAlpha(accent, 1f),
                toolTip: null);
            stripe.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
            Add(content.Controls, stripe);

            Add(content.Controls, Label(title.ToUpperInvariant(), new Vector2(-0.333f, y), 0.72f, "White"));
            y += 0.034f;
        }

        private void AddToggle(MyGuiControlParent content, ref float y, string label, Func<bool> getValue, Action<bool> setValue, string tooltip)
        {
            MyGuiControlButton button = ToggleButton(new Vector2(0.20f, y), tooltip);
            Add(content.Controls, button);

            MyGuiControlLabel labelControl = Label(label, new Vector2(-0.34f, y), 0.56f, "White");
            SetHint(labelControl, tooltip);
            Add(content.Controls, labelControl);

            ToggleBinding binding = new ToggleBinding(button, getValue, setValue);
            _toggles.Add(binding);
            y += 0.037f;
        }

        private void AddFilterDropdown(MyGuiControlParent content, ref float y, string label, Func<string> getValue, Func<string, bool> setValue, string tooltip)
        {
            string[] options = { "Off", "Helmet", "Cockpit", "CockpitNoOxy", "RealShip", "Deep", "EngineFilter", "AuxFilter" };
            AddOptionDropdown(content, ref y, label, options, getValue, setValue, tooltip);
        }

        private void AddCustomFilterTypeDropdown(MyGuiControlParent content, ref float y, string label, Func<string> getValue, Func<string, bool> setValue, string tooltip)
        {
            string[] options = { "LowPass", "HighPass", "BandPass", "Notch" };
            AddOptionDropdown(content, ref y, label, options, getValue, setValue, tooltip);
        }

        private void AddOptionDropdown(MyGuiControlParent content, ref float y, string label, string[] options, Func<string> getValue, Func<string, bool> setValue, string tooltip)
        {
            MyGuiControlLabel labelControl = Label(label, new Vector2(-0.34f, y), 0.56f, "White");
            SetHint(labelControl, tooltip);
            Add(content.Controls, labelControl);

            MyGuiControlCombobox combo = new MyGuiControlCombobox(
                position: new Vector2(0.125f, y),
                size: new Vector2(0.25f, 0.035f),
                openAreaItemsCount: 8,
                toolTip: tooltip,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            SetHint(combo, tooltip);

            for (int i = 0; i < options.Length; i++)
                combo.AddItem(i, options[i], null, null, false);

            Add(content.Controls, combo);
            _filters.Add(new FilterBinding(combo, options, getValue, setValue));
            y += 0.044f;
        }

        private void AddReadout(MyGuiControlParent content, ref float y, string label, Func<string> getText, string tooltip, float height, float scale = 0.42f)
        {
            MyGuiControlLabel labelControl = Label(label, new Vector2(-0.34f, y), 0.50f, "Blue");
            SetHint(labelControl, tooltip);
            Add(content.Controls, labelControl);

            MyGuiControlLabel valueControl = Label(string.Empty, new Vector2(-0.15f, y), scale, "White");
            valueControl.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
            SetHint(valueControl, tooltip);
            Add(content.Controls, valueControl);

            ReadoutBinding binding = new ReadoutBinding(valueControl, getText);
            _readouts.Add(binding);
            binding.Refresh();
            y += height;
        }

        private void AddInlineReadout(MyGuiControlParent content, ref float y, Func<string> getText, string tooltip)
        {
            MyGuiControlLabel valueControl = Label(string.Empty, new Vector2(-0.34f, y), 0.44f, "Blue");
            valueControl.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
            SetHint(valueControl, tooltip);
            Add(content.Controls, valueControl);

            ReadoutBinding binding = new ReadoutBinding(valueControl, getText);
            _readouts.Add(binding);
            binding.Refresh();
            y += 0.034f;
        }

        private void AddFilterChart(MyGuiControlParent content, ref float y, string label, Func<string> getType, Func<float> getFrequency, Func<float> getQ, string tooltip)
        {
            MyGuiControlLabel labelControl = Label(label, new Vector2(-0.34f, y + 0.005f), 0.50f, "Blue");
            SetHint(labelControl, tooltip);
            Add(content.Controls, labelControl);

            RspFilterResponseChart chart = new RspFilterResponseChart(
                position: new Vector2(0.095f, y + 0.057f),
                size: new Vector2(0.52f, 0.118f),
                getType: getType,
                getFrequency: getFrequency,
                getQ: getQ,
                tooltip: tooltip);
            Add(content.Controls, chart);
            y += 0.142f;
        }

        private void AddSlider(MyGuiControlParent content, ref float y, string label, string command, float min, float max, int decimals, Func<float> getValue, string tooltip, bool logarithmic = false)
        {
            string unit = GetSliderUnit(command, label);
            MyGuiControlLabel nameLabel = Label(label, new Vector2(-0.34f, y), 0.54f, "White");
            MyGuiControlLabel valueLabel = Label(string.Empty, new Vector2(0.31f, y), 0.50f, "White");
            valueLabel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER;
            SetHint(nameLabel, tooltip);
            SetHint(valueLabel, tooltip);
            Add(content.Controls, nameLabel);
            Add(content.Controls, valueLabel);

            MyGuiControlLabel minLabel = Label(FormatWithUnit(min, decimals, unit), new Vector2(-0.055f, y + 0.019f), 0.38f, "White");
            MyGuiControlLabel maxLabel = Label(FormatWithUnit(max, decimals, unit), new Vector2(0.315f, y + 0.019f), 0.38f, "White");
            maxLabel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER;
            SetHint(minLabel, tooltip);
            SetHint(maxLabel, tooltip);
            Add(content.Controls, minLabel);
            Add(content.Controls, maxLabel);

            float sliderMin = logarithmic ? 0f : min;
            float sliderMax = logarithmic ? 1f : max;
            float defaultValue = SettingsManager.TryGetDefault(command, out float resolvedDefault)
                ? Clamp(resolvedDefault, min, max)
                : Clamp(getValue(), min, max);
            float sliderDefault = logarithmic ? ToLogPosition(defaultValue, min, max) : defaultValue;

            MyGuiControlSlider slider = new MyGuiControlSlider(
                position: new Vector2(0.13f, y + 0.019f),
                minValue: sliderMin,
                maxValue: sliderMax,
                width: 0.30f,
                defaultValue: sliderDefault,
                toolTip: tooltip,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                showLabel: false);
            slider.BackgroundTexture = null;
            slider.BorderEnabled = false;
            slider.ColorMask = new Vector4(0.70f, 0.82f, 0.88f, 0.72f);
            slider.Size = new Vector2(0.30f, 0.018f);
            SetHint(slider, tooltip);
            Add(content.Controls, slider);

            _sliders.Add(new SliderBinding(slider, valueLabel, command, decimals, unit, getValue, min, max, defaultValue, logarithmic));
            y += 0.056f;
        }

        private void PollControls()
        {
            if (_syncing)
                return;

            bool changed = false;
            for (int i = 0; i < _sliders.Count; i++)
                changed |= _sliders[i].Poll();

            for (int i = 0; i < _toggles.Count; i++)
                changed |= _toggles[i].Poll();

            for (int i = 0; i < _filters.Count; i++)
                changed |= _filters[i].Poll();

            if (changed)
                RefreshLabels();
            else
                RefreshReadouts();
        }

        private void SyncControlsFromSettings()
        {
            _syncing = true;
            try
            {
                for (int i = 0; i < _sliders.Count; i++)
                    _sliders[i].Sync();

                for (int i = 0; i < _toggles.Count; i++)
                    _toggles[i].Sync();

                for (int i = 0; i < _filters.Count; i++)
                    _filters[i].Sync();
            }
            finally
            {
                _syncing = false;
            }

            RefreshLabels();
        }

        private void RefreshLabels()
        {
            for (int i = 0; i < _sliders.Count; i++)
                _sliders[i].RefreshLabel();

            RefreshReadouts();
        }

        private void RefreshReadouts()
        {
            for (int i = 0; i < _readouts.Count; i++)
                _readouts[i].Refresh();
        }

        private static MyGuiControlLabel Label(string text, Vector2 position, float scale, string font)
        {
            return new MyGuiControlLabel(
                position: position,
                text: text,
                textScale: scale,
                font: font,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
                isAutoScaleEnabled: true,
                minimumTextScale: 0.45f);
        }

        private static MyGuiControlButton Button(string text, Vector2 position, string tooltip, Action<MyGuiControlButton> click)
        {
            MyGuiControlButton button = new MyGuiControlButton(
                position: position,
                visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
                size: new Vector2(0.15f, 0.045f),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                toolTip: tooltip,
                text: new StringBuilder(text),
                textScale: 0.64f);
            SetHint(button, tooltip);
            button.ButtonClicked += click;
            return button;
        }

        private static MyGuiControlButton ToggleButton(Vector2 position, string tooltip)
        {
            MyGuiControlButton button = new MyGuiControlButton(
                position: position,
                visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
                size: new Vector2(0.075f, 0.028f),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                toolTip: tooltip,
                text: new StringBuilder("OFF"),
                textScale: 0.52f);
            SetHint(button, tooltip);
            return button;
        }

        private static void SetToggleVisual(MyGuiControlButton button, bool enabled)
        {
            button.Text = enabled ? "ON" : "OFF";
            button.ColorMask = enabled ? ToggleOn : ToggleOff;
        }

        private static Vector4 WithAlpha(Vector4 color, float alpha)
        {
            return new Vector4(color.X, color.Y, color.Z, alpha);
        }

        private static void Add(MyGuiControls controls, MyGuiControlBase control)
        {
            controls.Add(control, controls.Count);
        }

        private static void SetHint(MyGuiControlBase control, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                control.SetToolTip(text);
                control.TooltipDelay = 250;
            }
        }

        private static string Format(float value, int decimals)
        {
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static string FormatWithUnit(float value, int decimals, string unit)
        {
            string formatted = Format(value, decimals);
            return string.IsNullOrWhiteSpace(unit)
                ? formatted
                : formatted + " " + unit;
        }

        private static string GetSliderUnit(string command, string label)
        {
            string key = (command ?? string.Empty).Trim().ToLowerInvariant();
            string text = ((command ?? string.Empty) + " " + (label ?? string.Empty)).ToLowerInvariant();

            if (text.Contains("freq") || text.Contains("cutoff"))
                return "Hz";

            if (key == "smooth" || key == "cmdsmooth" || key == "auxsmooth" || key == "emitterfade" || text.Contains("smoothing"))
                return "ms";

            if (text.Contains("pressure") || text.Contains("atmosphere") || key.Contains("atm"))
                return "atm";

            if (key.EndsWith("q", StringComparison.Ordinal) || text.Contains(" q"))
                return "Q";

            if (text.Contains("thickness"))
                return "m";

            if (text.Contains("gain"))
                return "x";

            if (text.Contains("curve") || text.Contains("blend") || text.Contains("strength") || text.Contains("scale") || text.Contains("multiplier") || text.Contains("weight") || text.Contains("volume muffle"))
                return "x";

            if (text.Contains("threshold") || text.Contains("extra") || text.Contains("floor") || text.Contains("fade width") || key == "fade" || text.Contains("presence"))
                return "ratio";

            if (text.Contains("force log") || key.Contains("log"))
                return "log";

            if (key == "dist" || text.Contains("distance") || text.Contains("range") || text.Contains("ray"))
                return "m";

            return string.Empty;
        }

        private static string FormatEngineStaticFallbackState()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            return settings.EngineFilterDynamic
                ? "inactive: dynamic enginefilter owns live cutoff/Q"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "active: {0} {1:0}Hz Q{2:0.00}",
                    settings.Filter1Type,
                    settings.Filter1Frequency,
                    settings.Filter1Q);
        }

        private static float ToLogPosition(float value, float min, float max)
        {
            value = Clamp(value, min, max);
            double minLog = Math.Log(min);
            double maxLog = Math.Log(max);
            return (float)((Math.Log(value) - minLog) / (maxLog - minLog));
        }

        private static float FromLogPosition(float position, float min, float max)
        {
            position = Clamp(position, 0f, 1f);
            double minLog = Math.Log(min);
            double maxLog = Math.Log(max);
            return (float)Math.Exp(minLog + (maxLog - minLog) * position);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static void Notify(string message)
        {
            if (MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.ShowMessage("Realistic Sound+", message);
        }

        private sealed class ReadoutBinding
        {
            private readonly MyGuiControlLabel _label;
            private readonly Func<string> _getText;
            private string _lastText;

            public ReadoutBinding(MyGuiControlLabel label, Func<string> getText)
            {
                _label = label;
                _getText = getText;
            }

            public void Refresh()
            {
                string text = _getText != null ? _getText() : string.Empty;
                if (string.Equals(text, _lastText, StringComparison.Ordinal))
                    return;

                _lastText = text;
                _label.Text = text ?? string.Empty;
            }
        }

        private sealed class SliderBinding
        {
            private readonly MyGuiControlSlider _slider;
            private readonly MyGuiControlLabel _valueLabel;
            private readonly string _command;
            private readonly int _decimals;
            private readonly string _unit;
            private readonly Func<float> _getValue;
            private readonly float _min;
            private readonly float _max;
            private readonly float _defaultValue;
            private readonly bool _logarithmic;
            private float _lastValue;

            public SliderBinding(MyGuiControlSlider slider, MyGuiControlLabel valueLabel, string command, int decimals, string unit, Func<float> getValue, float min, float max, float defaultValue, bool logarithmic)
            {
                _slider = slider;
                _valueLabel = valueLabel;
                _command = command;
                _decimals = decimals;
                _unit = unit;
                _getValue = getValue;
                _min = min;
                _max = max;
                _defaultValue = Clamp(defaultValue, min, max);
                _logarithmic = logarithmic;
                _lastValue = getValue();
            }

            public bool Poll()
            {
                if (_slider.IsMouseOver && MyInput.Static != null && MyInput.Static.IsNewRightMousePressed())
                {
                    SettingsManager.TrySet(_command, _defaultValue);
                    Sync();
                    return true;
                }

                float value = _logarithmic ? FromLogPosition(_slider.Value, _min, _max) : _slider.Value;
                if (Math.Abs(value - _lastValue) < 0.0005f)
                    return false;

                SettingsManager.TrySet(_command, value);
                Sync();
                return true;
            }

            public void Sync()
            {
                _lastValue = _getValue();
                _slider.Value = _logarithmic ? ToLogPosition(_lastValue, _min, _max) : _lastValue;
                RefreshLabel();
            }

            public void RefreshLabel()
            {
                _valueLabel.Text = FormatWithUnit(_getValue(), _decimals, _unit);
            }
        }

        private sealed class ToggleBinding
        {
            private readonly MyGuiControlButton _button;
            private readonly Func<bool> _getValue;
            private readonly Action<bool> _setValue;
            private bool _lastValue;

            public ToggleBinding(MyGuiControlButton button, Func<bool> getValue, Action<bool> setValue)
            {
                _button = button;
                _getValue = getValue;
                _setValue = setValue;
                _lastValue = getValue();
                _button.ButtonClicked += OnClicked;
                Sync();
            }

            private void OnClicked(MyGuiControlButton button)
            {
                _setValue(!_getValue());
                Sync();
            }

            public bool Poll()
            {
                bool value = _getValue();
                if (value == _lastValue)
                    return false;

                Sync();
                return true;
            }

            public void Sync()
            {
                _lastValue = _getValue();
                SetToggleVisual(_button, _lastValue);
            }
        }

        private sealed class FilterBinding
        {
            private readonly MyGuiControlCombobox _combo;
            private readonly string[] _options;
            private readonly Func<string> _getValue;
            private readonly Func<string, bool> _setValue;
            private long _lastKey;

            public FilterBinding(MyGuiControlCombobox combo, string[] options, Func<string> getValue, Func<string, bool> setValue)
            {
                _combo = combo;
                _options = options;
                _getValue = getValue;
                _setValue = setValue;
            }

            public bool Poll()
            {
                long key = _combo.GetSelectedKey();
                if (key == _lastKey || key < 0 || key >= _options.Length)
                    return false;

                _setValue(_options[key]);
                _lastKey = key;
                return true;
            }

            public void Sync()
            {
                string value = _getValue();
                long key = 0;
                for (int i = 0; i < _options.Length; i++)
                {
                    if (string.Equals(_options[i], value, StringComparison.OrdinalIgnoreCase))
                    {
                        key = i;
                        break;
                    }
                }

                _lastKey = key;
                _combo.SelectItemByKey(key, false);
            }
        }

        private sealed class RspFilterResponseChart : MyGuiControlBase
        {
            private const int Samples = 88;
            private const float MinHz = 5f;
            private const float MaxHz = RspDynamicAudioFilters.MaxFilterFrequency;
            private const float MinDb = -48f;
            private const float MaxDb = 6f;
            private static readonly Color FrameColor = new Color(92, 122, 132, 210);
            private static readonly Color MajorGridColor = new Color(82, 104, 112, 145);
            private static readonly Color MinorGridColor = new Color(52, 64, 70, 75);
            private static readonly Color LabelColor = new Color(184, 204, 214, 225);
            private static readonly Color ResponseColor = new Color(118, 220, 255, 255);
            private static readonly Color ZeroDbColor = new Color(160, 176, 184, 185);
            private static readonly Color FillColor = new Color(55, 135, 180, 36);
            private static readonly float[] MajorFrequencies = { 5f, 10f, 20f, 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f, 8000f };
            private static readonly float[] MinorFrequencies = { 7.5f, 15f, 30f, 75f, 150f, 300f, 750f, 1500f, 3000f, 6500f };
            private static readonly float[] MajorDbTicks = { 0f, -6f, -12f, -18f, -24f, -36f, -48f };
            private static readonly float[] MinorDbTicks = { -3f, -9f, -15f, -21f, -30f, -42f };

            private readonly Func<string> _getType;
            private readonly Func<float> _getFrequency;
            private readonly Func<float> _getQ;

            public RspFilterResponseChart(Vector2 position, Vector2 size, Func<string> getType, Func<float> getFrequency, Func<float> getQ, string tooltip)
                : base(position, size, null, null, null, true, true, MyGuiControlHighlightType.NEVER, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER)
            {
                _getType = getType;
                _getFrequency = getFrequency;
                _getQ = getQ;
                SetHint(this, tooltip);
            }

            public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
            {
                base.Draw(transitionAlpha, backgroundTransitionAlpha);

                if (!Visible || !IsWithinDrawScissor)
                    return;

                Vector2 topLeft = GetPositionAbsoluteTopLeft();
                Vector2 size = Size;
                Vector2 bottomRight = topLeft + size;

                MyGuiManager.DrawRectangle(topLeft, size, new Color(8, 12, 15, 150));
                DrawRect(topLeft, bottomRight, FrameColor);
                DrawGrid(topLeft, size);
                DrawResponse(topLeft, size);
            }

            private void DrawGrid(Vector2 topLeft, Vector2 size)
            {
                for (int i = 0; i < MinorFrequencies.Length; i++)
                {
                    float x = topLeft.X + LogFrequencyPosition(MinorFrequencies[i]) * size.X;
                    DrawLine(new Vector2(x, topLeft.Y), new Vector2(x, topLeft.Y + size.Y), MinorGridColor);
                }

                for (int i = 0; i < MinorDbTicks.Length; i++)
                {
                    float y = topLeft.Y + DbToY(MinorDbTicks[i], size.Y);
                    DrawLine(new Vector2(topLeft.X, y), new Vector2(topLeft.X + size.X, y), MinorGridColor);
                }

                for (int i = 0; i < MajorFrequencies.Length; i++)
                {
                    float x = topLeft.X + LogFrequencyPosition(MajorFrequencies[i]) * size.X;
                    DrawLine(new Vector2(x, topLeft.Y), new Vector2(x, topLeft.Y + size.Y), MajorGridColor);
                    MyGuiDrawAlignEnum align = i == 0
                        ? MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP
                        : MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP;
                    Vector2 labelPos = new Vector2(x, topLeft.Y + size.Y - 0.019f);
                    if (i == 0)
                        labelPos.X += 0.003f;
                    DrawText(labelPos, FormatFrequency(MajorFrequencies[i]), 0.31f, LabelColor, align);
                }

                for (int i = 0; i < MajorDbTicks.Length; i++)
                {
                    float y = topLeft.Y + DbToY(MajorDbTicks[i], size.Y);
                    DrawLine(new Vector2(topLeft.X, y), new Vector2(topLeft.X + size.X, y), MajorDbTicks[i] == 0f ? ZeroDbColor : MajorGridColor);
                    DrawText(new Vector2(topLeft.X + 0.004f, y - 0.010f), MajorDbTicks[i].ToString("0", CultureInfo.InvariantCulture), 0.31f, LabelColor, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
                }
            }

            private void DrawResponse(Vector2 topLeft, Vector2 size)
            {
                string type = NormalizeType(_getType());
                float cutoff = Clamp(_getFrequency(), MinHz, MaxHz);
                float q = Clamp(_getQ(), RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ);

                Vector2 previous = Vector2.Zero;
                bool hasPrevious = false;
                for (int i = 0; i < Samples; i++)
                {
                    float t = Samples <= 1 ? 0f : (float)i / (Samples - 1);
                    float hz = FrequencyAt(t);
                    float db = MagnitudeDb(type, hz, cutoff, q);
                    Vector2 point = new Vector2(topLeft.X + t * size.X, topLeft.Y + DbToY(db, size.Y));

                    if (hasPrevious)
                    {
                        DrawLine(previous, point, ResponseColor);
                        DrawLine(new Vector2(previous.X, topLeft.Y + size.Y), previous, FillColor);
                    }

                    previous = point;
                    hasPrevious = true;
                }

                DrawText(topLeft + new Vector2(size.X - 0.006f, 0.004f), string.Format(CultureInfo.InvariantCulture, "{0} {1:0}Hz Q{2:0.00}", type, cutoff, q), 0.42f, ResponseColor, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP);
            }

            private static float FrequencyAt(float t)
            {
                double minLog = Math.Log(MinHz);
                double maxLog = Math.Log(MaxHz);
                return (float)Math.Exp(minLog + (maxLog - minLog) * Clamp(t, 0f, 1f));
            }

            private static float LogFrequencyPosition(float hz)
            {
                hz = Clamp(hz, MinHz, MaxHz);
                double minLog = Math.Log(MinHz);
                double maxLog = Math.Log(MaxHz);
                return (float)((Math.Log(hz) - minLog) / (maxLog - minLog));
            }

            private static float MagnitudeDb(string type, float hz, float cutoff, float q)
            {
                float ratio = Math.Max(0.0001f, hz / Math.Max(0.0001f, cutoff));
                double r2 = ratio * ratio;
                double damping = ratio / Math.Max(0.1f, q);
                double denominator = Math.Sqrt((1.0 - r2) * (1.0 - r2) + damping * damping);
                denominator = Math.Max(0.000001, denominator);

                double magnitude;
                switch (type)
                {
                    case "HighPass":
                        magnitude = r2 / denominator;
                        break;
                    case "BandPass":
                        magnitude = damping / denominator;
                        break;
                    case "Notch":
                        magnitude = Math.Abs(1.0 - r2) / denominator;
                        break;
                    default:
                        magnitude = 1.0 / denominator;
                        break;
                }

                double db = 20.0 * Math.Log10(Math.Max(0.000001, magnitude));
                return Clamp((float)db, MinDb, MaxDb);
            }

            private static float DbToY(float db, float height)
            {
                float normalized = (Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
                return (1f - normalized) * height;
            }

            private static string NormalizeType(string type)
            {
                switch ((type ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "highpass": return "HighPass";
                    case "bandpass": return "BandPass";
                    case "notch": return "Notch";
                    default: return "LowPass";
                }
            }

            private static string FormatFrequency(float frequency)
            {
                return frequency >= 1000f
                    ? (frequency / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                    : frequency.ToString("0", CultureInfo.InvariantCulture);
            }

            private static void DrawRect(Vector2 topLeft, Vector2 bottomRight, Color color)
            {
                DrawLine(topLeft, new Vector2(bottomRight.X, topLeft.Y), color);
                DrawLine(new Vector2(bottomRight.X, topLeft.Y), bottomRight, color);
                DrawLine(bottomRight, new Vector2(topLeft.X, bottomRight.Y), color);
                DrawLine(new Vector2(topLeft.X, bottomRight.Y), topLeft, color);
            }

            private static void DrawText(Vector2 normalizedPosition, string text, float scale, Color color, MyGuiDrawAlignEnum align)
            {
                Vector2 screen = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(normalizedPosition, false);
                MyRenderProxy.DebugDrawText2D(screen, text, color, scale, align, false);
            }

            private static void DrawLine(Vector2 normalizedStart, Vector2 normalizedEnd, Color color)
            {
                Vector2 screenStart = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(normalizedStart, false);
                Vector2 screenEnd = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(normalizedEnd, false);
                MyRenderProxy.DebugDrawLine2D(screenStart, screenEnd, color, color, null, false);
            }

            private static float Clamp(float value, float min, float max)
            {
                if (value < min)
                    return min;
                return value > max ? max : value;
            }
        }
    }
}
