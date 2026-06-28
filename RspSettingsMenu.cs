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

            AddMajorSection(content, ref y, "Thruster Audio", EngineAudioPanel);
            AddSection(content, ref y, "Source Layers");
            AddToggle(content, ref y, "Thruster Detail", () => SettingsManager.Current.V2DetailEnabled, value => SettingsManager.Current.V2DetailEnabled = value, "Adds positional thruster texture to the engine mix.");
            AddToggle(content, ref y, "Idle Layer", () => SettingsManager.Current.V2DetailIdleEnabled, value => SettingsManager.Current.V2DetailIdleEnabled = value, "Keeps low-thrust standby texture audible.");
            AddToggle(content, ref y, "State Layer", () => SettingsManager.Current.V2StateEnabled, value => SettingsManager.Current.V2StateEnabled = value, "Adds grouped ship-state body to the engine bed.");

            AddSection(content, ref y, "Level And Motion");
            AddSlider(content, ref y, "Overall Gain", "gain", 0f, 4f, 2, () => SettingsManager.Current.EngineGain, "Master V2 engine gain; higher louder, lower more headroom.");
            AddSlider(content, ref y, "Detail Gain", "detailgain", 0f, 4f, 2, () => SettingsManager.Current.V2DetailGain, "Volume for thrust detail; higher more mechanical texture.");
            AddSlider(content, ref y, "Idle Gain", "idlegain", 0f, 4f, 2, () => SettingsManager.Current.V2DetailIdleGain, "Volume for idle detail; higher more standby hum.");
            AddSlider(content, ref y, "State Gain", "stategain", 0f, 4f, 2, () => SettingsManager.Current.V2StateGain, "Volume for state layer; higher fuller engine bed.");
            AddSlider(content, ref y, "Output Curve", "curve", 0.25f, 10f, 2, () => SettingsManager.Current.AudioCurveExponent, "Shapes thrust-to-volume; higher favors high thrust, lower raises low thrust.");
            AddSlider(content, ref y, "Presence Floor", "presence", 0f, 1f, 2, () => SettingsManager.Current.MinimumShipPresence, "Minimum small-thruster presence; higher keeps tiny thrusters audible.");
            AddSlider(content, ref y, "Command Smoothing", "cmdsmooth", 0f, 5000f, 0, () => SettingsManager.Current.V2DetailCommandSmoothingMs, "Smooths thrust input before detail audio; higher slower, lower snappier.");
            AddSlider(content, ref y, "Emitter Fade", "emitterfade", 0f, 1000f, 0, () => SettingsManager.Current.V2EmitterFadeInMs, "Fade-in for new emitters; higher softer starts.");
            AddSlider(content, ref y, "Volume Smoothing", "smooth", 0f, 500f, 0, () => SettingsManager.Current.V2SmoothingMs, "Smooths engine volume updates; higher steadier, lower more reactive.");
            AddSlider(content, ref y, "Soft Fade Width", "fade", 0.001f, 0.25f, 3, () => SettingsManager.Current.V2SoftFadeRatio, "Near-zero thrust crossfade; higher softer idle transitions.");
            AddSlider(content, ref y, "Remote Collapse Range", "remotecollapse", 0f, 5000f, 0, () => SettingsManager.Current.V2RemoteGridCollapseDistance, "Remote ships beyond this range use one grid-centered V2 thruster source; 0 disables the remote aggregate.");

            AddMajorSection(content, ref y, "Thruster Propagation", EngineFilterPanel);
            AddSection(content, ref y, "Filter And Readouts");
            AddFilterDropdown(content, ref y, "Thruster Filter", () => SettingsManager.Current.EngineFilter, SetUnifiedEngineFilterRoute, "Filter route used for both atmospheric and hull thruster propagation.");
            AddToggle(content, ref y, "Dynamic Propagation", () => SettingsManager.Current.EngineFilterDynamic, value => SettingsManager.Current.EngineFilterDynamic = value, "Makes thruster tone follow distance, atmosphere, hull contact, and occlusion.");
            AddSlider(content, ref y, "Filter Smoothing", "livefiltersmooth", 0f, 200f, 0, () => SettingsManager.Current.LiveFilterSmoothingMs, "Smooths live filter cutoff changes (ms) to remove pops/zipper; higher = more lag, 0 = off.");
            AddFilterChart(content, ref y, "Thruster Response", V2EngineFilterTelemetry.RepresentativeType, V2EngineFilterTelemetry.RepresentativeFrequency, V2EngineFilterTelemetry.RepresentativeQ, "Preview of the current thruster filter frequency/Q.");
            AddReadout(content, ref y, "Environment", V2EngineFilterTelemetry.FormatEnvironment, "Live listener/source atmosphere and room data feeding thruster propagation.", 0.050f);
            AddReadout(content, ref y, "Thrusters", () => V2EngineFilterTelemetry.FormatEmitters(6), "Recent thruster emitters and their dynamic filter outputs.", 0.145f, 0.36f);

            AddSection(content, ref y, "Atmospheric Path");
            AddSlider(content, ref y, "Air Near Cutoff", "engineairnear", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterAirNearFrequency, "Brightest air-path cutoff near engines; higher clearer.", true);
            AddSlider(content, ref y, "Air Far Cutoff", "engineairfar", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterAirFarFrequency, "Darkest air-path cutoff at range; lower dulls distant engines.", true);
            AddSlider(content, ref y, "Air Filter Range", "engineairrange", 1f, 5000f, 0, () => SettingsManager.Current.EngineFilterAirRange, "Air-path distance span; higher carries brightness farther.", true);
            AddSlider(content, ref y, "Air Distance Curve", "engineaircurve", 0.1f, 5f, 2, () => SettingsManager.Current.EngineFilterAirDistanceCurve, "Air-path fade shape; higher delays high-frequency loss.");
            AddSlider(content, ref y, "Air Q", "engineairq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.EngineFilterAirQ, "Air-path resonance; higher sharper, lower smoother.");
            AddSlider(content, ref y, "Air Env Occlusion", "engineenvocclusion", 0f, 1f, 2, () => SettingsManager.Current.EngineFilterAirEnvironmentOcclusionContribution, "How strongly the local environment/ambience occlusion reduces atmospheric thruster sound; 0 ignores it, 1 follows it fully.");

            AddSection(content, ref y, "Hull Path");
            AddSlider(content, ref y, "Hull Near Cutoff", "enginehullnear", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterHullNearFrequency, "Structure path cutoff near engines; higher brighter hull sound.", true);
            AddSlider(content, ref y, "Hull Far Cutoff", "enginehullfar", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.EngineFilterHullFarFrequency, "Structure path cutoff far through grid; lower darker rumble.", true);
            AddSlider(content, ref y, "Hull Filter Range", "enginehullrange", 1f, 1000f, 0, () => SettingsManager.Current.EngineFilterHullRange, "Structure path distance span; higher carries hull sound farther.", true);
            AddSlider(content, ref y, "Hull Distance Curve", "enginehullcurve", 0.1f, 5f, 2, () => SettingsManager.Current.EngineFilterHullDistanceCurve, "Hull-path fade shape; higher delays darkening.");
            AddSlider(content, ref y, "Hull Q", "enginehullq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.EngineFilterHullQ, "Hull-path resonance; higher sharper metallic tone.");

            AddMajorSection(content, ref y, "Player / Aux Filter", AuxFilterPanel);
            AddSection(content, ref y, "Aux Master Routes");
            AddToggle(content, ref y, "Player Filter", () => SettingsManager.Current.PlayerFilterEnabled, value => SettingsManager.Current.PlayerFilterEnabled = value, "Master switch for aux filters on env, blocks, and player-local sounds.");
            AddToggle(content, ref y, "Environment Bed", () => SettingsManager.Current.PlayerFilterEnvironmentEnabled, value => SettingsManager.Current.PlayerFilterEnvironmentEnabled = value, "Filters wind/weather ambience using env probe and pressure.");
            AddToggle(content, ref y, "Block Emitters", () => SettingsManager.Current.PlayerFilterBlockEnabled, value => SettingsManager.Current.PlayerFilterBlockEnabled = value, "Filters block/world sounds using source path, distance, and room state.");
            AddToggle(content, ref y, "Player Local", () => SettingsManager.Current.PlayerFilterLocalEnabled, value => SettingsManager.Current.PlayerFilterLocalEnabled = value, "Filters player-local sounds by pressure only.");

            _currentAccent = SharedPanel;
            AddSection(content, ref y, "Shared Aux Filter Shape");
            AddCustomFilterTypeDropdown(content, ref y, "Aux Type", () => SettingsManager.Current.Filter2Type, SettingsManager.TrySetFilter2Type, "Filter shape shared by env, block, and local aux routes.");
            AddSlider(content, ref y, "Aux Clear Cutoff", "auxfilterfreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.Filter2Frequency, "Clear aux cutoff before muffling; higher brighter baseline.", true);
            AddSlider(content, ref y, "Aux Q", "auxfilterq", RspDynamicAudioFilters.MinFilterQ, RspDynamicAudioFilters.MaxFilterQ, 2, () => SettingsManager.Current.Filter2Q, "Shared aux resonance; higher sharper, lower smoother.");
            AddSlider(content, ref y, "Aux Smoothing", "auxsmooth", 0f, 5000f, 0, () => SettingsManager.Current.PlayerFilterSmoothingMs, "Smooths aux filter/volume changes; higher slower, lower snappier.");
            AddSlider(content, ref y, "Aux Occlusion Strength", "auxocclusionstrength", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterOcclusionStrength, "Global aux occlusion multiplier; higher more muffling.");
            AddFilterChart(content, ref y, "Aux Response", () => SettingsManager.Current.Filter2Type, () => SettingsManager.Current.Filter2Frequency, () => SettingsManager.Current.Filter2Q, "Preview of shared aux filter shape at clear cutoff/Q.");

            AddSection(content, ref y, "Sealed Rooms");
            AddSlider(content, ref y, "Sealed Environment Factor", "sealedenv", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentSealedFactor, "Extra wind muffling in airtight rooms; higher quieter sealed interiors.");
            AddSlider(content, ref y, "Sealed Blocks Factor", "sealedblock", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterBlockSealedFactor, "Extra block muffling when you are OUTSIDE the source's sealed room; higher = stronger door/wall contrast.");
            AddSlider(content, ref y, "Thin Wall Muffle", "thinsealmuffle", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterBlockSealedBarrierLoss, "How much THIN sealed walls/roof (armour/glass/plate) seal the OUTSIDE WIND out. 1 = thin shell fully blocks wind (default); LOWER and the wind leaks through the thin shell - brighter, airier. Scales with how much of the sky-dome is a thin sealed shell, so it has real two-way authority sealed or unsealed. Also muffles block sources behind thin sealed faces. Open gratings unaffected.");
            AddSlider(content, ref y, "Thin Wall Max Span", "sealbarrierthin", 1f, 6f, 1, () => SettingsManager.Current.PlayerFilterSealedBarrierThinFactor, "How many blocks thick a SEALED wall can be and still get the Thin Wall Muffle bonus. 1 = single layer only; raise to also muffle 2-3 block sealed walls.");

            _currentAccent = EnvironmentPanel;
            // ENV MUFFLE PIPELINE: rays are cast over the sky dome; each direction is open (sky) or blocked (by
            // structure of a given thickness); the persistent directional map stores those open/blocked readings;
            // the OPEN-SKY FRACTION (shaped by the aperture curve) becomes the ambient muffle. The map persists
            // and re-probes in place as you move, so the muffle stays stable rather than re-evaluating from scratch.
            AddSection(content, ref y, "Environment Sealing (ambient muffle)");
            AddInlineReadout(content, ref y, V2PlayerFilterRuntime.FormatEnvironmentLiveReadout, "Live env output: covered sky, final muffling, and final volume.");
            AddSlider(content, ref y, "Sky Probe Range", "envreverbray", 5f, 1000f, 0, () => SettingsManager.Current.PlayerEnvRayLength, "How far the sky-sealing rays reach (m). Longer detects farther openings (a distant skylight still lets ambient in); shorter only checks nearby cover. Also sizes the reverb room.", true);
            AddSlider(content, ref y, "Cover Thickness To Seal", "envstructurethickness", 0.1f, 20f, 2, () => SettingsManager.Current.PlayerEnvStructureThicknessScale, "How much structure between you and the sky is needed to fully muffle: HIGHER = thick cover required (thin roofs/walls leak ambient in); LOWER = even thin cover seals you off.");
            AddSlider(content, ref y, "Opening Sensitivity", "envaperturecurve", 0.1f, 10f, 2, () => SettingsManager.Current.PlayerEnvApertureCurve, "How a PARTIAL opening maps to loudness: HIGHER = a small gap barely brightens (stays muffled until a big opening); LOWER = any gap lets ambient through quickly.");
            AddSlider(content, ref y, "Env Voxel Weight", "voxelmuffle", 0f, 10f, 2, () => SettingsManager.Current.PlayerFilterVoxelOcclusionWeight, "Adds terrain/asteroid voxels (upper hemisphere, planets only) as sky-sealing alongside grid structure; higher = terrain overhead muffles more, 0 = grid only. (Block sounds have their own 'Block Voxel Weight'.)");

            AddSection(content, ref y, "Sealing Map (resolution & response)");
            AddSlider(content, ref y, "Map Detail", "envmapcells", 32f, 192f, 0, () => SettingsManager.Current.PlayerEnvMapCellCount, "Number of directions in the persistent sealing map. MORE = finer detail (a narrow skylight registers), slightly more cost; fewer = coarser but cheaper.");
            AddSlider(content, ref y, "Map Response", "envmapalpha", 0.1f, 1f, 2, () => SettingsManager.Current.PlayerEnvMapCellAlpha, "How fast a direction adopts a new reading when re-probed. LOWER = steadier/slower to change; HIGHER = snappier. This is how quickly the muffle reacts when geometry around you changes.");
            AddSlider(content, ref y, "Map Refresh Rate", "envmaprays", 4f, 32f, 0, () => SettingsManager.Current.PlayerEnvMapRaysPerUpdate, "Directions re-probed per update. HIGHER = the map adapts faster after you walk to a new area (full sweep = Map Detail / this). Does not affect a stationary reading.");
            AddToggle(content, ref y, "Show Sealing Map", () => SettingsManager.Current.PlayerEnvMapDebugEnabled, value => SettingsManager.Current.PlayerEnvMapDebugEnabled = value, "Debug overlay: draws the directional sealing map as flat tiles on a dome - green = open sky, red = sealed/blocked.");

            AddSection(content, ref y, "Environment Bed");
            AddSlider(content, ref y, "Env Volume Muffle", "envvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentVolumeMuffleWeight, "Wind volume reduction from env muffle; higher fades harder, 0 tone only.");
            AddSlider(content, ref y, "Env Bed Minimum Gain", "envfloor", 0f, 0.5f, 2, () => SettingsManager.Current.PlayerFilterEnvironmentMinGain, "Floor for RSP wind volume; higher keeps muffled wind audible.");
            AddSlider(content, ref y, "Env Muffled Cutoff", "envmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterEnvironmentMuffledFrequency, "Lowest wind cutoff under cover; higher keeps wind brighter.", true);
            AddReadout(content, ref y, "Env/Reverb Probe", V2PlayerEnvironmentTelemetry.FormatSummary, "Summary of shared env/reverb rays, voxel, pressure, and sealed-room output.", 0.060f, 0.38f);

            _currentAccent = BlockPanel;
            // Block emitters now resolve along TWO legs: the DIRECT line straight through structure (muffled by
            // thickness -> low cutoff) and the AIR detour around corners (stays bright -> high cutoff). They are
            // energy-blended into one low-pass; the air leg can only ever brighten the direct result, and only
            // when the direct line is blocked AND an open path exists.
            AddSection(content, ref y, "Block - Direct Path (through walls)");
            AddSlider(content, ref y, "Wall Thickness Muffle", "blockstructurethickness", 0.1f, 20f, 2, () => SettingsManager.Current.PlayerFilterBlockStructureThicknessScale, "DIRECT leg: how much solid grid thickness on the straight line to the source muffles it. The main through-wall knob; higher = thin walls muffle less.");
            AddSlider(content, ref y, "Direct Occlusion Curve", "blockocclusioncurve", 0.1f, 5f, 2, () => SettingsManager.Current.PlayerFilterBlockOcclusionCurve, "DIRECT leg: shapes the straight-line occlusion response; higher forgives light blockage.");
            AddSlider(content, ref y, "Block Voxel Weight", "blockvoxelweight", 0f, 10f, 2, () => { float w = SettingsManager.Current.PlayerFilterBlockVoxelOcclusionWeight; return w < 0f ? SettingsManager.Current.PlayerFilterVoxelOcclusionWeight : w; }, "BLOCK occlusion only: how much terrain/asteroid voxels muffle a block source's direct line; higher = more muffling through terrain, 0 = off. Separate from the wind 'Env Voxel Weight'.");
            AddSlider(content, ref y, "Direct Muffled Cutoff", "blockmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterBlockMuffledFrequency, "DIRECT leg: the LOW cutoff a source collapses to through a thick wall (the low-frequency-only floor). The bright end is 'Aux Clear Cutoff'.", true);

            AddSection(content, ref y, "Block - Air Path (around corners)");
            // --- Air leg TONE (how the detour sounds) ---
            AddSlider(content, ref y, "Air Path Brightness", "blockairbright", 0f, 1f, 2, () => SettingsManager.Current.PlayerFilterBlockAirBrightness, "AIR leg tone: brightness FLOOR - how bright a SHORT detour stays (muffle at zero length). Lower = brighter / more high end. Muffle then accumulates with length (next slider).");
            AddSlider(content, ref y, "Air Path Length Muffle", "blockairlengthmuffle", 0f, 0.1f, 3, () => SettingsManager.Current.PlayerFilterBlockAirLengthMuffle, "AIR leg tone: extra muffle accumulated PER METRE of detour (high-frequency loss over distance) - a longer around-the-corner path arrives progressively duller. 0 = length ignored (flat brightness).");
            // --- Which ROUTE the air path takes (pathfinding) ---
            AddSlider(content, ref y, "Air Path Reach", "blockairpathreach", 1f, 16f, 0, () => SettingsManager.Current.PlayerFilterBlockAirPathReach, "ROUTE: how far (grid cells) the around-corner flood-fill searches beyond the direct line. Raise for longer detours - multi-flight/switchback staircases need more reach. Higher = more CPU per blocked source.");
            AddToggle(content, ref y, "Air Path Through Open Blocks", () => SettingsManager.Current.PlayerFilterBlockAirPathThroughBlocks, value => SettingsManager.Current.PlayerFilterBlockAirPathThroughBlocks = value, "ROUTE: sound goes where air goes - let the detour pass through any non-sealing block (stairs, catwalks, gratings, railings), not just empty cells + open doors. Needed for stairwells packed with stair blocks. Full-armour walls/floors and closed doors still block.");
            AddSlider(content, ref y, "Route Open-Air Bias", "blockairopenbias", 0f, 30f, 0, () => SettingsManager.Current.PlayerFilterBlockAirPathOpenBias, "ROUTE choice only: how strongly the path AVOIDS grated/non-sealing blocks when picking its route (open stairwell vs a hop through a grated floor). Only matters when more than one route exists. Does NOT move the emitter - that's 'Emitter Portal Pull' below.");
            // --- Where the EMITTER sits (localisation / repositioning) ---
            AddToggle(content, ref y, "Reposition To Doorway", () => SettingsManager.Current.PlayerFilterBlockRepositionEnabled, value => SettingsManager.Current.PlayerFilterBlockRepositionEnabled = value, "EXPERIMENTAL: when a source is blocked but an open detour exists, move its emitter toward the doorway/stairwell portal so it localises to the opening. The detour attenuation is carried by gain. Static-base sources only for now.");
            AddSlider(content, ref y, "Emitter Portal Pull", "blockrepoairbias", 0.1f, 10f, 1, () => SettingsManager.Current.PlayerFilterBlockRepositionAirBias, "EMITTER position: how far the emitter moves toward the air-path portal vs staying at the source. 1 = physical loudness blend; raise to localise harder to the opening. DIFFERENT from 'Route Open-Air Bias' (that picks the route; this moves the emitter along it).");
            AddSlider(content, ref y, "Reposition Smoothing", "blockreposeslew", 1f, 2000f, 0, () => SettingsManager.Current.PlayerFilterBlockRepositionSlewMs, "Temporal smoothing (ms) of the emitter's position as its blended target moves. Higher = smoother glide. Snaps once on first placement, no slide out from the block.");

            AddSection(content, ref y, "Block - Range & Output (both paths)");
            AddSlider(content, ref y, "Block Sound Range", "blockrange", 1f, 150f, 0, () => SettingsManager.Current.PlayerFilterBlockMaxRange, "Absolute block cue range for discovery and vanilla distance extension; lower reduces large-base voices.", true);
            AddSlider(content, ref y, "Block Travel Curve", "blockcurve", 0.1f, 5f, 2, () => SettingsManager.Current.PlayerFilterBlockDistanceCurve, "Block volume distance-falloff SHAPE: higher = holds volume then drops steeply near max range; lower = fades evenly with distance. (Both paths use it, so a steeper curve also makes a long air detour fade more than the short direct line - that's a side effect, not a separate control.)");
            AddSlider(content, ref y, "Block Volume Muffle", "blockvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterBlockVolumeMuffleWeight, "Volume cut from muffling (applies to the blended result); higher quieter through walls, 0 tone only.");
            AddSlider(content, ref y, "Block Occlusion Smoothing", "blockocclusionsmooth", 0f, 2000f, 0, () => SettingsManager.Current.PlayerFilterBlockOcclusionSmoothingMs, "Temporal smoothing of the block occlusion; higher steadier and less flicker, lower more responsive.");
            AddReadout(content, ref y, "Block Candidates", V2AuxSourceOcclusionTelemetry.FormatSummary, "Summary of recent non-engine block/world source candidates.", 0.058f, 0.38f);
            AddReadout(content, ref y, "Block Source Detail", () => V2AuxSourceOcclusionTelemetry.FormatSources(6), "Per-source distance, room, seal, muffling, cutoff, gain, and air-path length/merge.", 0.150f, 0.34f);
            AddToggle(content, ref y, "Block Occlusion Rays", () => SettingsManager.Current.PlayerFilterPathDebugEnabled, value => SettingsManager.Current.PlayerFilterPathDebugEnabled = value, "Debug overlay: draws the listener->block rays colour-coded by where thickness is gained (green open, orange structure, amber voxel, dim skipped). Air-path length/merge shown as text per source.");

            _currentAccent = SharedPanel;
            AddSection(content, ref y, "Player Local");
            AddSlider(content, ref y, "Local Volume Muffle", "localvolmuffle", 0f, 4f, 2, () => SettingsManager.Current.PlayerFilterLocalVolumeMuffleWeight, "Pressure-based volume cut for local sounds; higher quieter in vacuum.");
            AddSlider(content, ref y, "Local Muffled Cutoff", "auxmufflefreq", RspDynamicAudioFilters.MinFilterFrequency, RspDynamicAudioFilters.MaxFilterFrequency, 0, () => SettingsManager.Current.PlayerFilterMuffledFrequency, "Cutoff for local pressure muffling; higher clearer, lower helmet-like.", true);

            AddSection(content, ref y, "Aux Applied Voices");
            AddReadout(content, ref y, "Applied Summary", V2PlayerFilterRuntime.FormatSummary, "Counts and strongest voice currently controlled by aux filter.", 0.060f, 0.38f);
            AddReadout(content, ref y, "Applied Detail", () => V2PlayerFilterRuntime.FormatSources(6), "Per-voice aux category, muffle, cutoff, gain, and range.", 0.150f, 0.34f);

            AddMajorSection(content, ref y, "Environmental Reverb", SoftPanel);
            AddToggle(content, ref y, "Reverb", () => SettingsManager.Current.GlobalReverbEnabled, value => SettingsManager.Current.GlobalReverbEnabled = value, "Enables live environmental reflections from the current room and pressure.");
            AddToggle(content, ref y, "Global Mix", IsLiveReverbMasterRoute, SetLiveReverbMasterRoute, "Off uses World mix for in-game audio; on uses Global mix for the full game mix including UI.");
            AddReadout(content, ref y, "Reverb Mix", FormatLiveReverbMix, "World follows in-game audio; Global follows the full game mix including UI.", 0.030f, 0.38f);

            AddSection(content, ref y, "Room Driven Modifiers");
            AddSlider(content, ref y, "Sealed Room Geometry", "reverbsealedgeo", 0f, 1f, 2, () => SettingsManager.Current.ReverbSealedGeometryWeight, "Blend exact cell-set room geometry into reverb size in sealed rooms; 0 = ray-only (old), 1 = fully trust geometry (steadier).");
            AddAutoReverbSlider(content, ref y, "Reverb Room Size", "room", "reverbroommod", 0.25f, 2f, 2, () => SettingsManager.Current.GlobalReverbRoomSizeModifier, "Multiplier on ray-calculated room size; 1 uses the auto value.");
            AddAutoReverbSlider(content, ref y, "Reverb Diffusion", "diffusion", "reverbdiffmod", 0.25f, 2f, 2, () => SettingsManager.Current.GlobalReverbDiffusionModifier, "Multiplier on auto diffusion; higher smoother, lower more distinct.");
            AddAutoReverbSlider(content, ref y, "Decay Time", "decay", "reverbdecaymod", 0.25f, 2.5f, 2, () => SettingsManager.Current.GlobalReverbDecayModifier, "Multiplier on auto RT60 decay; higher longer tail.");
            AddAutoReverbSlider(content, ref y, "Early Reflections", "early", "reverbearlyoffsetdb", -12f, 12f, 1, () => SettingsManager.Current.GlobalReverbEarlyGainOffsetDb, "dB offset on auto first reflections; higher stronger onset.");
            AddAutoReverbSlider(content, ref y, "Late Tail", "tail", "reverbtailoffsetdb", -12f, 12f, 1, () => SettingsManager.Current.GlobalReverbTailGainOffsetDb, "dB offset on auto tail level; higher more sustained room.");
            AddAutoReverbSlider(content, ref y, "Pre Delay", "predelay", "reverbpremod", 0.25f, 2.5f, 2, () => SettingsManager.Current.GlobalReverbPredelayModifier, "Multiplier on auto first-bounce delay; higher feels farther.");
            AddAutoReverbSlider(content, ref y, "Late Delay", "latedelay", "reverblatemod", 0.25f, 2.5f, 2, () => SettingsManager.Current.GlobalReverbLateDelayModifier, "Multiplier on auto late-field delay; higher separates tail.");
            AddAutoReverbSlider(content, ref y, "Tail Density", "density", "reverbdensemod", 0.5f, 1.5f, 2, () => SettingsManager.Current.GlobalReverbDensityModifier, "Multiplier on auto tail density; higher smoother wash.");
            AddAutoReverbSlider(content, ref y, "Tone Cutoff", "tone", "reverbtonemod", 0.5f, 2f, 2, () => SettingsManager.Current.GlobalReverbToneModifier, "Multiplier on auto damping cutoff; higher brighter tail.");
            AddAutoReverbSlider(content, ref y, "HF Damping", "hf", "reverbhfoffsetdb", -12f, 12f, 1, () => SettingsManager.Current.GlobalReverbHighFrequencyOffsetDb, "dB offset on auto high-frequency damping; higher crisper.");

            AddSection(content, ref y, "Live Output");
            AddSlider(content, ref y, "Reverb Amount", "reverbwet", 0f, 4f, 2, () => SettingsManager.Current.GlobalReverbWetSend, "Overall live reverb level; 0 disables the reflected field.");
            AddReadout(content, ref y, "Live DSP", V2GlobalReverbRuntime.FormatStatus, "Live reverb route and processor status.", 0.070f, 0.40f);

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

            Add(content.Controls, Label(title, new Vector2(-0.34f, y), 0.66f, "White"));
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

            Add(content.Controls, Label(title.ToUpperInvariant(), new Vector2(-0.34f, y), 0.72f, "White"));
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

        private void AddAutoReverbSlider(MyGuiControlParent content, ref float y, string label, string autoKey, string command, float min, float max, int decimals, Func<float> getValue, string tooltip, bool logarithmic = false)
        {
            AddSlider(
                content,
                ref y,
                label,
                command,
                min,
                max,
                decimals,
                getValue,
                tooltip,
                logarithmic,
                "Green",
                () => label + "  " + V2ManagedDspReverbRuntime.FormatAutoValue(autoKey));
        }

        private void AddSlider(MyGuiControlParent content, ref float y, string label, string command, float min, float max, int decimals, Func<float> getValue, string tooltip, bool logarithmic = false, string labelFont = "White", Func<string> getDynamicLabel = null)
        {
            // One aligned row: [name] .... [slider] [value], with the min/max range under the slider ends.
            string unit = GetSliderUnit(command, label);
            MyGuiControlLabel nameLabel = Label(getDynamicLabel != null ? getDynamicLabel() : label, new Vector2(-0.34f, y), 0.54f, labelFont);
            MyGuiControlLabel valueLabel = Label(string.Empty, new Vector2(0.345f, y), 0.50f, "White");
            valueLabel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER;
            SetHint(nameLabel, tooltip);
            SetHint(valueLabel, tooltip);
            Add(content.Controls, nameLabel);
            Add(content.Controls, valueLabel);

            MyGuiControlLabel minLabel = Label(FormatWithUnit(min, decimals, unit), new Vector2(-0.02f, y + 0.020f), 0.36f, "White");
            minLabel.OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER;
            MyGuiControlLabel maxLabel = Label(FormatWithUnit(max, decimals, unit), new Vector2(0.28f, y + 0.020f), 0.36f, "White");
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

            RspSlider slider = new RspSlider(
                position: new Vector2(0.13f, y),
                minValue: sliderMin,
                maxValue: sliderMax,
                width: 0.30f,
                defaultValue: sliderDefault,
                toolTip: tooltip,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
            slider.Size = new Vector2(0.30f, 0.020f);
            SetHint(slider, tooltip);
            Add(content.Controls, slider);

            _sliders.Add(new SliderBinding(slider, nameLabel, valueLabel, label, getDynamicLabel, command, decimals, unit, getValue, min, max, defaultValue, logarithmic));
            y += 0.052f;
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

            if (key.Contains("offset") || key.Contains("db") || text.Contains("damping"))
                return "dB";

            if (key.Contains("mod"))
                return "x";

            if (key == "smooth" || key == "cmdsmooth" || key == "auxsmooth" || key == "emitterfade" || text.Contains("smoothing"))
                return "ms";

            if (text.Contains("pressure") || text.Contains("atmosphere") || key.Contains("atm"))
                return "atm";

            if (key.EndsWith("q", StringComparison.Ordinal) || text.Contains(" q"))
                return "Q";

            if (text.Contains("thickness"))
                return "m";

            if (key.Contains("wet") || text.Contains("wet send") || text.Contains("gain"))
                return "x";

            if (text.Contains("decay time"))
                return "s";

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

        private static bool SetUnifiedEngineFilterRoute(string value)
        {
            bool external = SettingsManager.TrySetFilter(value);
            bool internalRoute = SettingsManager.TrySetInternalFilter(value);
            return external && internalRoute;
        }

        private static bool IsLiveReverbMasterRoute()
        {
            return SettingsManager.IsGlobalReverbCustomMasterRoute(SettingsManager.Current);
        }

        private static void SetLiveReverbMasterRoute(bool master)
        {
            SettingsManager.TrySetGlobalReverbRoute(master ? "custommaster" : "custominline");
            V2DebugLog.WriteEvent("menu", "reverb mix " + FormatLiveReverbMix() + " route=" + SettingsManager.Current.GlobalReverbRoute);
        }

        private static string FormatLiveReverbMix()
        {
            string route = SettingsManager.NormalizeGlobalReverbRoute(SettingsManager.Current.GlobalReverbRoute);
            if (string.Equals(route, "custommaster", StringComparison.OrdinalIgnoreCase))
                return "Global";
            if (string.Equals(route, "custominline", StringComparison.OrdinalIgnoreCase))
                return "World";
            if (string.Equals(route, "managed", StringComparison.OrdinalIgnoreCase))
                return "Managed";
            if (string.Equals(route, "custombus", StringComparison.OrdinalIgnoreCase))
                return "Bus Test";
            if (string.Equals(route, "globalbus", StringComparison.OrdinalIgnoreCase))
                return "XAudio Bus";
            return route ?? "Managed";
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
            private readonly MyGuiControlLabel _nameLabel;
            private readonly MyGuiControlLabel _valueLabel;
            private readonly string _baseLabel;
            private readonly Func<string> _getDynamicLabel;
            private readonly string _command;
            private readonly int _decimals;
            private readonly string _unit;
            private readonly Func<float> _getValue;
            private readonly float _min;
            private readonly float _max;
            private readonly float _defaultValue;
            private readonly bool _logarithmic;
            private float _lastValue;
            private string _lastNameText;

            public SliderBinding(MyGuiControlSlider slider, MyGuiControlLabel nameLabel, MyGuiControlLabel valueLabel, string baseLabel, Func<string> getDynamicLabel, string command, int decimals, string unit, Func<float> getValue, float min, float max, float defaultValue, bool logarithmic)
            {
                _slider = slider;
                _nameLabel = nameLabel;
                _valueLabel = valueLabel;
                _baseLabel = baseLabel;
                _getDynamicLabel = getDynamicLabel;
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
                if (_getDynamicLabel != null)
                {
                    string nameText = _getDynamicLabel() ?? _baseLabel;
                    if (!string.Equals(nameText, _lastNameText, StringComparison.Ordinal))
                    {
                        _lastNameText = nameText;
                        _nameLabel.Text = nameText;
                    }
                }

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

        // Slider with a clean, flat custom track instead of the engine's default grey rail box. Because the
        // visual is fully custom, the VALUE is also mapped from our own geometry in HandleInput - otherwise the
        // base maps clicks over its internal rail (offset from our full-width draw) and the grab point sits to
        // the side of the drawn thumb.
        private sealed class RspSlider : MyGuiControlSlider
        {
            private static readonly Color TrackColor = new Color(120, 150, 165, 110);
            private static readonly Color FillColor = new Color(150, 200, 222, 190);
            private static readonly Color ThumbColor = new Color(214, 234, 242, 255);
            private const float ThumbWidth = 0.006f;
            private const float ThumbHeightFraction = 0.5f; // shorter than the full control height
            private bool _dragging;

            public RspSlider(Vector2 position, float minValue, float maxValue, float width, float defaultValue, string toolTip, MyGuiDrawAlignEnum originAlign)
                : base(position: position, minValue: minValue, maxValue: maxValue, width: width, defaultValue: defaultValue, toolTip: toolTip, originAlign: originAlign, showLabel: false)
            {
            }

            public override MyGuiControlBase HandleInput()
            {
                MyGuiControlBase captured = base.HandleInput(); // keep base focus / hover / tooltip / keyboard
                if (MyInput.Static == null)
                    return captured;

                if (IsMouseOver && MyInput.Static.IsNewLeftMousePressed())
                    _dragging = true;
                if (!MyInput.Static.IsLeftMousePressed())
                    _dragging = false;

                if (_dragging)
                {
                    Vector2 topLeft = GetPositionAbsoluteTopLeft();
                    float w = Size.X;
                    if (w > 1e-4f)
                    {
                        float t = (MyGuiManager.MouseCursorPosition.X - topLeft.X) / w; // OUR geometry == the drawn track
                        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                        float v = MinValue + t * (MaxValue - MinValue);
                        if (Math.Abs(v - Value) > 1e-6f)
                            Value = v; // override the base's offset mapping
                        captured = this;
                    }
                }
                return captured;
            }

            public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
            {
                // Intentionally NOT calling base.Draw: that is what paints the grey rail box.
                if (!Visible)
                    return;

                Vector2 topLeft = GetPositionAbsoluteTopLeft();
                Vector2 size = Size;
                float midY = topLeft.Y + size.Y * 0.5f;
                float trackH = 0.0016f;
                float trackY = midY - trackH * 0.5f;

                float range = MaxValue - MinValue;
                float t = range > 1e-6f ? (Value - MinValue) / range : 0f;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                float fillW = size.X * t;

                MyGuiManager.DrawRectangle(new Vector2(topLeft.X, trackY), new Vector2(size.X, trackH), ApplyAlpha(TrackColor, transitionAlpha));
                if (fillW > 0f)
                    MyGuiManager.DrawRectangle(new Vector2(topLeft.X, trackY), new Vector2(fillW, trackH), ApplyAlpha(FillColor, transitionAlpha));

                float thumbH = size.Y * ThumbHeightFraction;
                float thumbY = topLeft.Y + (size.Y - thumbH) * 0.5f; // centred -> shorter handle
                MyGuiManager.DrawRectangle(new Vector2(topLeft.X + fillW - ThumbWidth * 0.5f, thumbY), new Vector2(ThumbWidth, thumbH), ApplyAlpha(ThumbColor, transitionAlpha));
            }

            private static Color ApplyAlpha(Color color, float transitionAlpha)
            {
                float a = transitionAlpha < 0f ? 0f : (transitionAlpha > 1f ? 1f : transitionAlpha);
                return new Color(color.R, color.G, color.B, (byte)(color.A * a));
            }
        }

        private sealed class RspFilterResponseChart : MyGuiControlBase
        {
            private const int Samples = 88;
            private const float MinHz = 5f;
            private const float MaxHz = RspDynamicAudioFilters.MaxFilterFrequency;
            private const float MinDb = -48f;
            private const float MaxDb = 6f;
            private static readonly Color FrameColor = new Color(130, 165, 182, 235);
            private static readonly Color MajorGridColor = new Color(120, 152, 168, 210);
            private static readonly Color MinorGridColor = new Color(78, 98, 110, 140);
            private static readonly Color LabelColor = new Color(190, 210, 220, 235);
            private static readonly Color ResponseColor = new Color(120, 222, 255, 255);
            private static readonly Color ZeroDbColor = new Color(170, 188, 198, 210);
            private static readonly Color FillColor = new Color(60, 140, 188, 48);
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

                // No filled backdrop: the solid panel obscured visibility and was clipped by the menu top.
                // The frame, grid, labels and response curve carry the chart on the transparent menu behind it.
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
