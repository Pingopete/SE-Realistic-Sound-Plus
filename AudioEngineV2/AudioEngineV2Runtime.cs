using System;
using System.Collections.Generic;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class AudioEngineV2Runtime
    {
        private static readonly TimeSpan CensusInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan EmptySourceDiscoveryInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan SuppressionBypassLogInterval = TimeSpan.FromSeconds(3);
        private const int MinimumKnownThrustersBeforeGridCensus = 6;
        private static readonly Dictionary<long, V2GridAudioState> GridStates = new Dictionary<long, V2GridAudioState>();
        private static readonly Dictionary<long, MyThrust> KnownThrusters = new Dictionary<long, MyThrust>();
        private static readonly HashSet<MyEntity3DSoundEmitter> V2Emitters = new HashSet<MyEntity3DSoundEmitter>();
        private static readonly HashSet<MyEntity3DSoundEmitter> UnfilteredV2Emitters = new HashSet<MyEntity3DSoundEmitter>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, V2FilterRoute> V2EmitterFilterRoutes = new Dictionary<MyEntity3DSoundEmitter, V2FilterRoute>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, string> V2EmitterDebugLabels = new Dictionary<MyEntity3DSoundEmitter, string>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, Vector3D> V2EmitterPositions = new Dictionary<MyEntity3DSoundEmitter, Vector3D>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, long> V2EmitterSourceGridIds = new Dictionary<MyEntity3DSoundEmitter, long>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, long> V2EmitterSourceEntityIds = new Dictionary<MyEntity3DSoundEmitter, long>();
        private static readonly HashSet<string> MutedVanillaCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedEnabled;
        private static bool _hasListener;
        private static V2AudioListenerState _listener;
        private static int _rawThrusterReports;
        private static int _acceptedThrusterReports;
        private static int _censusThrusterReports;
        private static int _fallbackRejectedThrusterReports;
        private static int _gridMismatchThrusterReports;
        private static int _remoteCollapsedThrusterReports;
        private static int _lastCensusProcessed;
        private static int _lastCensusRemoved;
        private static DateTime _lastCensusUtc = DateTime.MinValue;
        private static DateTime _lastEmptySourceDiscoveryUtc = DateTime.MinValue;
        private static DateTime _lastSuppressionBypassLogUtc = DateTime.MinValue;
        private static DateTime _lastThrusterReportFailureLogUtc = DateTime.MinValue;
        private static DateTime _lastGridAudioFailureLogUtc = DateTime.MinValue;
        private static long _lastEmptySourceDiscoveryGridId;
        private static int _emitterBindingGeneration;
        private static string _lastEmitterBindingSignature;
        private static string _lastLoggedListenerMode;
        private static string _lastLoggedContactSource;
        private static long _lastLoggedListenerGridId;
        private static long _lastLoggedContactGridId;
        private static bool _lastLoggedInsideShip;
        private static bool _lastLoggedVanillaFallback;
        private static bool _legacyReverbSuppressed;

        public static V2AudioListenerState Listener => _listener;

        public static int EmitterBindingGeneration => _emitterBindingGeneration;

        public static void ResetForSession(string reason)
        {
            StopAllEmitters();
            GridStates.Clear();
            KnownThrusters.Clear();
            V2Emitters.Clear();
            UnfilteredV2Emitters.Clear();
            V2EmitterFilterRoutes.Clear();
            V2EmitterDebugLabels.Clear();
            V2EmitterPositions.Clear();
            V2EmitterSourceGridIds.Clear();
            V2EmitterSourceEntityIds.Clear();
            MutedVanillaCues.Clear();
            _loggedEnabled = false;
            _hasListener = false;
            _listener = default(V2AudioListenerState);
            _rawThrusterReports = 0;
            _acceptedThrusterReports = 0;
            _censusThrusterReports = 0;
            _fallbackRejectedThrusterReports = 0;
            _gridMismatchThrusterReports = 0;
            _remoteCollapsedThrusterReports = 0;
            _lastCensusProcessed = 0;
            _lastCensusRemoved = 0;
            _lastCensusUtc = DateTime.MinValue;
            _lastEmptySourceDiscoveryUtc = DateTime.MinValue;
            _lastSuppressionBypassLogUtc = DateTime.MinValue;
            _lastThrusterReportFailureLogUtc = DateTime.MinValue;
            _lastGridAudioFailureLogUtc = DateTime.MinValue;
            _lastEmptySourceDiscoveryGridId = 0L;
            _emitterBindingGeneration = 0;
            _lastEmitterBindingSignature = null;
            _lastLoggedListenerMode = null;
            _lastLoggedContactSource = null;
            _lastLoggedListenerGridId = 0L;
            _lastLoggedContactGridId = 0L;
            _lastLoggedInsideShip = false;
            _lastLoggedVanillaFallback = false;
            VanillaShipEnvironment.Reset();
            V2AudioDebugState.Reset();
            V2EngineFilterTelemetry.Reset();
            V2PlayerEnvironmentTelemetry.Reset();
            V2AuxSourceOcclusionTelemetry.Reset();
            V2PlayerFilterRuntime.Reset();
            V2BlockSoundSourceResolver.Reset();
            V2GridStructureProbe.Reset();
            V2AudioListenerState.ResetStability();
            RspDynamicAudioFilters.ResetRuntimeState();
            V2GlobalReverbRuntime.ResetRuntimeState();
            V2ReverbDiagnosticPing.Reset();
            V2ManagedDspReverbRuntime.Reset();
            V2ConnectorImpactAudio.ResetRuntimeState();
            _legacyReverbSuppressed = false;

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 audio runtime reset: " + reason);
        }

        public static void Update()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            RspDynamicAudioFilters.UpdateFromSettings(settings);
            if (SettingsManager.IsGlobalReverbGlobalBusRoute(settings))
            {
                _legacyReverbSuppressed = false;
                V2GlobalReverbRuntime.UpdateGlobalBusRoute();
            }
            else
            {
                SuppressLegacyReverbRoute();
            }

            V2ManagedDspReverbRuntime.Update();
            V2ReverbDiagnosticPing.Update();
            TrackEmitterBindingSignature();
            _listener = V2AudioListenerState.Capture();
            _hasListener = true;
            V2PlayerEnvironmentTelemetry.Update(_listener);
            V2PlayerFilterRuntime.Update();
            V2AuxSourceOcclusionTelemetry.Update();
            LogListenerTransitionIfChanged(_listener);
            if (_listener.VanillaFallback)
            {
                if (SettingsManager.Current.V2RemoteGridCollapseDistance > 0f)
                    SilenceDirectionalEmitters();
                else
                    SilenceAllEmitters();
                _lastCensusProcessed = 0;
                _lastCensusRemoved = 0;
            }
            else
            {
                EnsureListenerGridThrustersKnown("update");
                RefreshKnownThrustersIfDue();
            }

            CleanupEmptyGridStates();
            V2AudioDebugState.Update(_listener, GridStates.Count, KnownThrusters.Count, _lastCensusProcessed, _lastCensusRemoved, CountActiveDetailSources(), CountActiveStateSources(), CountKnownSourceGroups(), CreateThrusterReportSnapshot());

            if (!_loggedEnabled)
            {
                _loggedEnabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio Engine V2 is the active ship-engine route. Six-direction detail/state emitter routing is active while the listener is inside or controlling a ship.");
            }
        }

        private static void SuppressLegacyReverbRoute()
        {
            if (_legacyReverbSuppressed)
                return;

            V2GlobalReverbRuntime.RestoreVanillaState("managed-dsp-route");
            _legacyReverbSuppressed = true;
            V2DebugLog.WriteEvent("global-reverb", "legacy XAudio effect route suppressed; managed DSP tail route active");
        }

        private static void LogListenerTransitionIfChanged(V2AudioListenerState listener)
        {
            if (string.Equals(_lastLoggedListenerMode, listener.ModeName, StringComparison.Ordinal)
                && string.Equals(_lastLoggedContactSource, listener.ContactSource, StringComparison.Ordinal)
                && _lastLoggedListenerGridId == listener.GridEntityId
                && _lastLoggedContactGridId == listener.ContactGridEntityId
                && _lastLoggedInsideShip == listener.InsideShip
                && _lastLoggedVanillaFallback == listener.VanillaFallback)
            {
                return;
            }

            _lastLoggedListenerMode = listener.ModeName;
            _lastLoggedContactSource = listener.ContactSource;
            _lastLoggedListenerGridId = listener.GridEntityId;
            _lastLoggedContactGridId = listener.ContactGridEntityId;
            _lastLoggedInsideShip = listener.InsideShip;
            _lastLoggedVanillaFallback = listener.VanillaFallback;

            V2DebugLog.WriteEvent("listener", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "mode={0} inside={1} fallback={2} grid={3} contact={4}/{5} char={6}",
                listener.ModeName ?? "?",
                listener.InsideShip ? "Y" : "N",
                listener.VanillaFallback ? "Y" : "N",
                listener.GridEntityId,
                listener.ContactSource ?? "-",
                listener.ContactGridEntityId,
                listener.CharacterMovementState ?? "?"));
        }

        public static void ReportThruster(MyThrust thruster)
        {
            _rawThrusterReports = SaturatingIncrement(_rawThrusterReports);

            if (thruster == null || thruster.CubeGrid == null)
                return;

            RememberThruster(thruster);
            ProcessThruster(thruster, false, true);
        }

        private static void ProcessThruster(MyThrust thruster, bool fromCensus, bool updateAudio)
        {
            if (thruster == null || thruster.CubeGrid == null)
                return;

            if (!_hasListener)
            {
                _listener = V2AudioListenerState.Capture();
                _hasListener = true;
            }

            bool remoteCollapsed = false;
            if (_listener.VanillaFallback)
            {
                if (!TryShouldProcessRemoteCollapsedThruster(thruster, _listener, out remoteCollapsed))
                {
                    if (!fromCensus)
                        _fallbackRejectedThrusterReports = SaturatingIncrement(_fallbackRejectedThrusterReports);
                    return;
                }
            }
            else if (_listener.GridEntityId != 0L && thruster.CubeGrid.EntityId != _listener.GridEntityId)
            {
                if (!TryShouldProcessRemoteCollapsedThruster(thruster, _listener, out remoteCollapsed))
                {
                    if (!fromCensus)
                        _gridMismatchThrusterReports = SaturatingIncrement(_gridMismatchThrusterReports);
                    return;
                }
            }

            if (remoteCollapsed && !fromCensus)
                _remoteCollapsedThrusterReports = SaturatingIncrement(_remoteCollapsedThrusterReports);

            if (fromCensus)
                _censusThrusterReports = SaturatingIncrement(_censusThrusterReports);
            else
                _acceptedThrusterReports = SaturatingIncrement(_acceptedThrusterReports);

            V2GridAudioState state = GetOrCreateGridState(thruster.CubeGrid);
            try
            {
                if (remoteCollapsed)
                    state.ReportRemoteThrusterCollapsed(thruster, _listener, updateAudio);
                else
                    state.ReportThruster(thruster, _listener, updateAudio);
            }
            catch (Exception ex)
            {
                LogThrusterReportFailure(thruster, fromCensus, updateAudio, ex);
            }
        }

        private static bool TryShouldProcessRemoteCollapsedThruster(MyThrust thruster, V2AudioListenerState listener, out bool remoteCollapsed)
        {
            remoteCollapsed = false;
            if (thruster == null || thruster.CubeGrid == null)
                return false;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float threshold = settings?.V2RemoteGridCollapseDistance ?? 0f;
            if (threshold <= 0f)
                return false;

            Vector3D listenerPosition = ResolveListenerPosition(listener);
            Vector3D gridCenter = ResolveGridCenter(thruster.CubeGrid, thruster.WorldMatrix.Translation);
            if (listenerPosition == Vector3D.Zero || gridCenter == Vector3D.Zero)
                return false;

            double distance = Vector3D.Distance(listenerPosition, gridCenter);
            if (distance < threshold)
                return false;

            remoteCollapsed = true;
            return true;
        }

        private static Vector3D ResolveListenerPosition(V2AudioListenerState listener)
        {
            if (listener.Position != Vector3D.Zero)
                return listener.Position;

            try
            {
                Vector3D cameraPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (cameraPosition != Vector3D.Zero)
                    return cameraPosition;
            }
            catch
            {
            }

            return Vector3D.Zero;
        }

        private static Vector3D ResolveGridCenter(MyCubeGrid grid, Vector3D fallback)
        {
            if (grid == null)
                return fallback;

            try
            {
                if (grid.PositionComp != null)
                {
                    Vector3D center = grid.PositionComp.WorldAABB.Center;
                    if (center != Vector3D.Zero)
                        return center;
                }
            }
            catch
            {
            }

            try
            {
                Vector3D origin = grid.WorldMatrix.Translation;
                if (origin != Vector3D.Zero)
                    return origin;
            }
            catch
            {
            }

            return fallback;
        }

        private static V2AudioDebugState.ThrusterReportSnapshot CreateThrusterReportSnapshot()
        {
            return new V2AudioDebugState.ThrusterReportSnapshot
            {
                PatchHits = V2ThrusterAudioPatch.PatchHits,
                PatchDisabled = V2ThrusterAudioPatch.Disabled,
                RawReports = _rawThrusterReports,
                AcceptedReports = _acceptedThrusterReports,
                CensusReports = _censusThrusterReports,
                FallbackRejectedReports = _fallbackRejectedThrusterReports,
                GridMismatchReports = _gridMismatchThrusterReports,
                RemoteCollapsedReports = _remoteCollapsedThrusterReports,
                RegisteredEmitters = V2Emitters.Count,
                UnfilteredEmitters = UnfilteredV2Emitters.Count,
                FilterHits = ThrusterFilterPatch.PatchHits,
                FilterDisabled = ThrusterFilterPatch.Disabled
            };
        }

        public static bool ShouldSuppressVanillaShipCue(MyEntity3DSoundEmitter emitter, string cueName)
        {
            if (emitter == null || string.IsNullOrWhiteSpace(cueName))
                return false;

            if (IsV2Emitter(emitter))
                return false;

            if (!EngineAudioClassifier.IsKnownVanillaShipStateCue(cueName))
                return false;

            _listener = V2AudioListenerState.Capture();
            _hasListener = true;
            LogListenerTransitionIfChanged(_listener);

            if (_listener.VanillaFallback)
                return false;

            if (!SettingsManager.Current.V2DetailEnabled && !SettingsManager.Current.V2StateEnabled)
                return false;

            EnsureListenerGridThrustersKnown("suppress");
            if (!HasReplacementSourcesReady())
            {
                LogSuppressionBypass(cueName, "no-v2-sources");
                return false;
            }

            AudioDiagnostics.RecordCueName(cueName, "v2-vanilla-muted", emitter.VolumeMultiplier, 0f, 0f, 0f, emitter.SourcePosition);
            return true;
        }

        public static bool MuteVanillaShipCueIfNeeded(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null || IsV2Emitter(emitter))
                return false;

            if (!TryGetSuppressibleVanillaCue(emitter, out string cueName))
                return false;

            if (!ShouldSuppressVanillaShipCue(emitter, cueName))
                return false;

            try
            {
                emitter.VolumeMultiplier = 0f;
                emitter.StopSound(false, false, false);
                if (MutedVanillaCues.Add(cueName))
                    V2DebugLog.WriteEvent("vanilla-muted", cueName);

                return true;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[RealisticSoundPlus] Failed to mute vanilla ship cue " + cueName + ": " + ex.Message);
                return false;
            }
        }

        public static void RegisterEmitter(MyEntity3DSoundEmitter emitter, bool skipFilter)
        {
            RegisterEmitter(emitter, skipFilter ? V2FilterRoute.None : V2FilterRoute.External);
        }

        public static void RegisterEmitter(MyEntity3DSoundEmitter emitter, V2FilterRoute filterRoute)
        {
            RegisterEmitter(emitter, filterRoute, null);
        }

        public static void RegisterEmitter(MyEntity3DSoundEmitter emitter, V2FilterRoute filterRoute, string debugLabel)
        {
            RegisterEmitter(emitter, filterRoute, debugLabel, 0L, 0L);
        }

        public static void RegisterEmitter(MyEntity3DSoundEmitter emitter, V2FilterRoute filterRoute, string debugLabel, long sourceGridId, long sourceEntityId)
        {
            if (emitter == null)
                return;

            V2Emitters.Add(emitter);
            V2EmitterFilterRoutes[emitter] = filterRoute;
            if (filterRoute == V2FilterRoute.None)
                UnfilteredV2Emitters.Add(emitter);
            else
                UnfilteredV2Emitters.Remove(emitter);

            if (!string.IsNullOrWhiteSpace(debugLabel))
                V2EmitterDebugLabels[emitter] = debugLabel;

            if (sourceGridId != 0L)
                V2EmitterSourceGridIds[emitter] = sourceGridId;
            else
                V2EmitterSourceGridIds.Remove(emitter);

            if (sourceEntityId != 0L)
                V2EmitterSourceEntityIds[emitter] = sourceEntityId;
            else
                V2EmitterSourceEntityIds.Remove(emitter);

            ThrusterFilterPatch.MarkKnownEngineCueEmitter(emitter);
        }

        public static void UnregisterEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return;

            V2Emitters.Remove(emitter);
            UnfilteredV2Emitters.Remove(emitter);
            V2EmitterFilterRoutes.Remove(emitter);
            V2EmitterDebugLabels.Remove(emitter);
            V2EmitterPositions.Remove(emitter);
            V2EmitterSourceGridIds.Remove(emitter);
            V2EmitterSourceEntityIds.Remove(emitter);
        }

        public static bool IsV2Emitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && V2Emitters.Contains(emitter);
        }

        public static bool ShouldSkipEngineFilter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && UnfilteredV2Emitters.Contains(emitter);
        }

        public static string GetEngineFilterEffectSubtype(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null && V2EmitterFilterRoutes.TryGetValue(emitter, out V2FilterRoute route))
            {
                switch (route)
                {
                    case V2FilterRoute.Internal:
                        return SettingsManager.GetInternalEngineFilterEffectSubtype();
                    case V2FilterRoute.Hull:
                    case V2FilterRoute.External:
                        return SettingsManager.GetEngineFilterEffectSubtype();
                    default:
                        return null;
                }
            }

            return SettingsManager.GetEngineFilterEffectSubtype();
        }

        public static string GetEngineFilterEffectSignature(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null && V2EmitterFilterRoutes.TryGetValue(emitter, out V2FilterRoute route))
            {
                switch (route)
                {
                    case V2FilterRoute.Internal:
                        return SettingsManager.GetInternalEngineFilterEffectSignature();
                    case V2FilterRoute.Hull:
                    case V2FilterRoute.External:
                        return SettingsManager.GetEngineFilterEffectSignature();
                    default:
                        return string.Empty;
                }
            }

            return SettingsManager.GetEngineFilterEffectSignature();
        }

        public static string GetEmitterFilterRouteName(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null && V2EmitterFilterRoutes.TryGetValue(emitter, out V2FilterRoute route))
                return route.ToString();

            if (IsV2Emitter(emitter))
                return "V2Unknown";

            return "Vanilla";
        }

        public static string GetEmitterDebugLabel(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null && V2EmitterDebugLabels.TryGetValue(emitter, out string label))
                return label;

            if (IsV2Emitter(emitter))
                return GetEmitterFilterRouteName(emitter);

            return "vanilla";
        }

        public static bool IsHullOnlyFilterRoute(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null
                && V2EmitterFilterRoutes.TryGetValue(emitter, out V2FilterRoute route)
                && route == V2FilterRoute.Hull;
        }

        public static void SetEmitterPosition(MyEntity3DSoundEmitter emitter, Vector3D position)
        {
            if (emitter == null || position == Vector3D.Zero)
                return;

            V2EmitterPositions[emitter] = position;
        }

        public static bool TryGetEmitterPosition(MyEntity3DSoundEmitter emitter, out Vector3D position)
        {
            position = Vector3D.Zero;
            return emitter != null && V2EmitterPositions.TryGetValue(emitter, out position) && position != Vector3D.Zero;
        }

        public static bool TryGetEmitterSourceIds(MyEntity3DSoundEmitter emitter, out long sourceGridId, out long sourceEntityId)
        {
            sourceGridId = 0L;
            sourceEntityId = 0L;
            if (emitter == null)
                return false;

            V2EmitterSourceGridIds.TryGetValue(emitter, out sourceGridId);
            V2EmitterSourceEntityIds.TryGetValue(emitter, out sourceEntityId);
            return sourceGridId != 0L || sourceEntityId != 0L;
        }

        private static bool TryGetSuppressibleVanillaCue(MyEntity3DSoundEmitter emitter, out string cueName)
        {
            cueName = null;
            if (emitter == null)
                return false;

            if (IsSuppressibleCueName(emitter.SoundId.ToString(), out cueName))
                return true;

            try
            {
                if (IsSuppressibleCueName(emitter.Sound?.CueEnum.ToString(), out cueName))
                    return true;
            }
            catch
            {
            }

            try
            {
                if (IsSuppressibleCueName(emitter.SecondarySound?.CueEnum.ToString(), out cueName))
                    return true;
            }
            catch
            {
            }

            cueName = null;
            return false;
        }

        private static bool IsSuppressibleCueName(string value, out string cueName)
        {
            cueName = null;
            if (string.IsNullOrWhiteSpace(value) || value == "NullOrEmpty")
                return false;

            string trimmed = value.Trim();
            if (!EngineAudioClassifier.IsKnownVanillaShipStateCue(trimmed))
                return false;

            cueName = trimmed;
            return true;
        }

        public static string FormatDebugLine()
        {
            return V2AudioDebugState.Format();
        }

        public static void DrawDebugMarkers()
        {
            foreach (V2GridAudioState state in GridStates.Values)
                state.DrawDebugMarkers();
        }

        private static V2GridAudioState GetOrCreateGridState(MyCubeGrid grid)
        {
            long id = grid.EntityId;
            if (GridStates.TryGetValue(id, out V2GridAudioState state))
                return state;

            state = new V2GridAudioState(grid);
            GridStates[id] = state;
            V2DebugLog.WriteEvent("grid", "created id=" + id);
            return state;
        }

        private static void RememberThruster(MyThrust thruster)
        {
            if (thruster == null)
                return;

            long id = thruster.EntityId;
            if (id == 0L)
                id = thruster.GetHashCode();

            KnownThrusters[id] = thruster;
        }

        private static void RefreshKnownThrustersIfDue()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastCensusUtc < CensusInterval)
                return;

            _lastCensusUtc = now;
            RefreshKnownThrusters();
        }

        private static void RefreshKnownThrusters()
        {
            if (KnownThrusters.Count == 0)
            {
                _lastCensusProcessed = 0;
                _lastCensusRemoved = 0;
                return;
            }

            List<long> stale = null;
            int processed = 0;
            foreach (KeyValuePair<long, MyThrust> pair in KnownThrusters)
            {
                MyThrust thruster = pair.Value;
                try
                {
                    if (thruster == null || thruster.CubeGrid == null)
                    {
                        if (stale == null)
                            stale = new List<long>();
                        stale.Add(pair.Key);
                        continue;
                    }

                    ProcessThruster(thruster, true, false);
                    processed++;
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[RealisticSoundPlus] Removing V2 known thruster after census error: " + ex.Message);
                    if (stale == null)
                        stale = new List<long>();
                    stale.Add(pair.Key);
                }
            }

            _lastCensusProcessed = processed;
            _lastCensusRemoved = stale == null ? 0 : stale.Count;
            UpdateListenerGridAudioOnce("known-refresh");

            if (stale == null)
                return;

            foreach (long id in stale)
                KnownThrusters.Remove(id);
        }

        private static void EnsureListenerGridThrustersKnown(string reason)
        {
            if (!_hasListener || _listener.VanillaFallback || _listener.GridEntityId == 0L)
                return;

            if (KnownThrusters.Count >= MinimumKnownThrustersBeforeGridCensus)
                return;

            DateTime now = DateTime.UtcNow;
            if (_lastEmptySourceDiscoveryGridId == _listener.GridEntityId && now - _lastEmptySourceDiscoveryUtc < EmptySourceDiscoveryInterval)
                return;

            _lastEmptySourceDiscoveryUtc = now;
            _lastEmptySourceDiscoveryGridId = _listener.GridEntityId;

            if (!TryGetGridById(_listener.GridEntityId, out MyCubeGrid grid))
            {
                V2DebugLog.WriteEvent("source-census", "reason=" + reason + " grid=" + _listener.GridEntityId + " result=grid-missing");
                return;
            }

            int found = 0;
            int failed = 0;
            int acceptedBefore = _censusThrusterReports;
            foreach (MyThrust thruster in grid.GetFatBlocks<MyThrust>())
            {
                try
                {
                    found++;
                    RememberThruster(thruster);
                    ProcessThruster(thruster, true, false);
                }
                catch (Exception ex)
                {
                    failed++;
                    V2DebugLog.WriteEvent("source-census-thruster-failed", "reason=" + reason + " grid=" + grid.EntityId + " " + ex.Message);
                }
            }

            bool audioUpdated = UpdateGridStateAudioFor(grid, reason);
            V2DebugLog.WriteEvent("source-census", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "reason={0} grid={1} found={2} accepted={3} failed={4} known={5} states={6} audio={7}",
                reason,
                grid.EntityId,
                found,
                _censusThrusterReports - acceptedBefore,
                failed,
                KnownThrusters.Count,
                GridStates.Count,
                audioUpdated ? "ok" : "failed"));
        }

        private static void UpdateListenerGridAudioOnce(string reason)
        {
            if (!_hasListener || _listener.VanillaFallback || _listener.GridEntityId == 0L)
                return;

            if (!TryGetGridById(_listener.GridEntityId, out MyCubeGrid grid))
                return;

            UpdateGridStateAudioFor(grid, reason);
        }

        private static bool UpdateGridStateAudioFor(MyCubeGrid grid, string reason)
        {
            if (grid == null)
                return false;

            try
            {
                V2GridAudioState state = GetOrCreateGridState(grid);
                state.Update(grid, _listener);
                return true;
            }
            catch (Exception ex)
            {
                LogGridAudioUpdateFailure(reason, grid.EntityId, ex);
                return false;
            }
        }

        internal static bool TryGetGridById(long gridId, out MyCubeGrid grid)
        {
            grid = null;
            if (gridId == 0L)
                return false;

            try
            {
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(gridId, out entity))
                    return false;

                grid = entity as MyCubeGrid;
                return grid != null;
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("source-census", "grid lookup failed id=" + gridId + " " + ex.Message);
                return false;
            }
        }

        private static void LogThrusterReportFailure(MyThrust thruster, bool fromCensus, bool updateAudio, Exception ex)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastThrusterReportFailureLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastThrusterReportFailureLogUtc = now;
            V2DebugLog.WriteEvent("thruster-report-failed", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "grid={0} thruster={1} census={2} audio={3} {4}",
                thruster != null && thruster.CubeGrid != null ? thruster.CubeGrid.EntityId : 0L,
                thruster != null ? thruster.EntityId : 0L,
                fromCensus ? "Y" : "N",
                updateAudio ? "Y" : "N",
                ex.Message));
        }

        private static void LogGridAudioUpdateFailure(string reason, long gridId, Exception ex)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastGridAudioFailureLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastGridAudioFailureLogUtc = now;
            V2DebugLog.WriteEvent("grid-audio-update-failed", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "reason={0} grid={1} {2}",
                reason ?? "?",
                gridId,
                ex.Message));
        }

        private static bool HasReplacementSourcesReady()
        {
            return KnownThrusters.Count > 0 || V2Emitters.Count > 0;
        }

        private static void TrackEmitterBindingSignature()
        {
            string signature = (SettingsManager.GetEngineFilterEffectSubtype() ?? string.Empty)
                + "|"
                + (SettingsManager.GetInternalEngineFilterEffectSubtype() ?? string.Empty);

            if (string.Equals(_lastEmitterBindingSignature, signature, StringComparison.Ordinal))
                return;

            string previous = _lastEmitterBindingSignature;
            _lastEmitterBindingSignature = signature;
            if (previous == null)
                return;

            _emitterBindingGeneration++;
            V2DebugLog.WriteEvent("emitter-bindings", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "generation={0} old={1} new={2}",
                _emitterBindingGeneration,
                string.IsNullOrEmpty(previous) ? "none" : previous,
                string.IsNullOrEmpty(signature) ? "none" : signature));
        }

        private static void LogSuppressionBypass(string cueName, string reason)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastSuppressionBypassLogUtc < SuppressionBypassLogInterval)
                return;

            _lastSuppressionBypassLogUtc = now;
            V2DebugLog.WriteEvent("vanilla-suppress-bypass", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "cue={0} reason={1} mode={2} grid={3} known={4} states={5} emit={6}",
                cueName ?? "?",
                reason ?? "?",
                _listener.ModeName ?? "?",
                _listener.GridEntityId,
                KnownThrusters.Count,
                GridStates.Count,
                V2Emitters.Count));
        }

        private static void StopAllEmitters()
        {
            foreach (V2GridAudioState state in GridStates.Values)
                state.Stop();
        }

        private static void SilenceAllEmitters()
        {
            foreach (V2GridAudioState state in GridStates.Values)
                state.Silence();
        }

        private static void SilenceDirectionalEmitters()
        {
            foreach (V2GridAudioState state in GridStates.Values)
                state.SilenceDirectionalEmitters();
        }

        private static void CleanupEmptyGridStates()
        {
            if (GridStates.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            List<long> stale = null;
            foreach (KeyValuePair<long, V2GridAudioState> pair in GridStates)
            {
                if (!pair.Value.IsEmpty(now))
                    continue;

                pair.Value.Stop();
                if (stale == null)
                    stale = new List<long>();
                stale.Add(pair.Key);
            }

            if (stale == null)
                return;

            foreach (long id in stale)
                GridStates.Remove(id);
        }

        private static int CountActiveDetailSources()
        {
            int count = 0;
            foreach (V2GridAudioState state in GridStates.Values)
                count += state.CountActiveDetailSources();

            return count;
        }

        private static int CountActiveStateSources()
        {
            int count = 0;
            foreach (V2GridAudioState state in GridStates.Values)
                count += state.CountActiveStateSources();

            return count;
        }

        private static int CountKnownSourceGroups()
        {
            DateTime now = DateTime.UtcNow;
            int count = 0;
            foreach (V2GridAudioState state in GridStates.Values)
                count += state.CountKnownSourceGroups(now);

            return count;
        }

        private static int SaturatingIncrement(int value)
        {
            return value == int.MaxValue ? value : value + 1;
        }
    }

    internal enum V2FilterRoute
    {
        None,
        External,
        Internal,
        Hull
    }
}
