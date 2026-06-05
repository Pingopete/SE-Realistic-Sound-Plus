using System;
using System.Collections.Generic;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class AudioEngineV2Runtime
    {
        private static readonly TimeSpan CensusInterval = TimeSpan.FromMilliseconds(50);
        private static readonly Dictionary<long, V2GridAudioState> GridStates = new Dictionary<long, V2GridAudioState>();
        private static readonly Dictionary<long, MyThrust> KnownThrusters = new Dictionary<long, MyThrust>();
        private static readonly HashSet<MyEntity3DSoundEmitter> V2Emitters = new HashSet<MyEntity3DSoundEmitter>();
        private static readonly HashSet<MyEntity3DSoundEmitter> UnfilteredV2Emitters = new HashSet<MyEntity3DSoundEmitter>();
        private static bool _loggedEnabled;
        private static bool _hasListener;
        private static V2AudioListenerState _listener;
        private static int _rawThrusterReports;
        private static int _acceptedThrusterReports;
        private static int _censusThrusterReports;
        private static int _fallbackRejectedThrusterReports;
        private static int _gridMismatchThrusterReports;
        private static int _lastCensusProcessed;
        private static int _lastCensusRemoved;
        private static DateTime _lastCensusUtc = DateTime.MinValue;

        public static V2AudioListenerState Listener => _listener;

        public static void ResetForSession(string reason)
        {
            StopAllEmitters();
            GridStates.Clear();
            KnownThrusters.Clear();
            V2Emitters.Clear();
            UnfilteredV2Emitters.Clear();
            _loggedEnabled = false;
            _hasListener = false;
            _listener = default(V2AudioListenerState);
            _rawThrusterReports = 0;
            _acceptedThrusterReports = 0;
            _censusThrusterReports = 0;
            _fallbackRejectedThrusterReports = 0;
            _gridMismatchThrusterReports = 0;
            _lastCensusProcessed = 0;
            _lastCensusRemoved = 0;
            _lastCensusUtc = DateTime.MinValue;
            VanillaShipEnvironment.Reset();
            V2AudioDebugState.Reset();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 audio runtime reset: " + reason);
        }

        public static void Update()
        {
            _listener = V2AudioListenerState.Capture();
            _hasListener = true;
            if (_listener.VanillaFallback)
            {
                StopAllEmitters();
                _lastCensusProcessed = 0;
                _lastCensusRemoved = 0;
            }
            else
            {
                RefreshKnownThrustersIfDue();
            }

            CleanupEmptyGridStates();
            V2AudioDebugState.Update(_listener, GridStates.Count, KnownThrusters.Count, _lastCensusProcessed, _lastCensusRemoved, CountActiveDetailSources(), CountActiveStateSources(), CountKnownSourceGroups(), CreateThrusterReportSnapshot());

            if (!_loggedEnabled)
            {
                _loggedEnabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio Engine V2 is the active ship-engine route. Six-direction detail/state emitter routing is active while the listener is inside a ship.");
            }
        }

        public static void ReportThruster(MyThrust thruster)
        {
            _rawThrusterReports = SaturatingIncrement(_rawThrusterReports);

            if (thruster == null || thruster.CubeGrid == null)
                return;

            RememberThruster(thruster);
            ProcessThruster(thruster, false);
        }

        private static void ProcessThruster(MyThrust thruster, bool fromCensus)
        {
            if (thruster == null || thruster.CubeGrid == null)
                return;

            if (!_hasListener)
            {
                _listener = V2AudioListenerState.Capture();
                _hasListener = true;
            }

            if (_listener.VanillaFallback)
            {
                if (!fromCensus)
                    _fallbackRejectedThrusterReports = SaturatingIncrement(_fallbackRejectedThrusterReports);
                return;
            }

            if (_listener.GridEntityId != 0L && thruster.CubeGrid.EntityId != _listener.GridEntityId)
            {
                if (!fromCensus)
                    _gridMismatchThrusterReports = SaturatingIncrement(_gridMismatchThrusterReports);
                return;
            }

            if (fromCensus)
                _censusThrusterReports = SaturatingIncrement(_censusThrusterReports);
            else
                _acceptedThrusterReports = SaturatingIncrement(_acceptedThrusterReports);

            V2GridAudioState state = GetOrCreateGridState(thruster.CubeGrid);
            state.ReportThruster(thruster, _listener);
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

            if (!_hasListener)
            {
                _listener = V2AudioListenerState.Capture();
                _hasListener = true;
            }

            if (_listener.VanillaFallback || !_listener.InsideShip)
                return false;

            if (IsV2Emitter(emitter))
                return false;

            if (CountActiveDetailSources() + CountActiveStateSources() <= 0)
                return false;

            if (!EngineAudioClassifier.IsKnownVanillaShipStateCue(cueName))
                return false;

            AudioDiagnostics.RecordCueName(cueName, "v2-vanilla-muted", emitter.VolumeMultiplier, 0f, 0f, 0f, emitter.SourcePosition);
            return true;
        }

        public static void RegisterEmitter(MyEntity3DSoundEmitter emitter, bool skipFilter)
        {
            if (emitter == null)
                return;

            V2Emitters.Add(emitter);
            if (skipFilter)
                UnfilteredV2Emitters.Add(emitter);
            else
                UnfilteredV2Emitters.Remove(emitter);

            ThrusterFilterPatch.MarkKnownEngineCueEmitter(emitter);
        }

        public static void UnregisterEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return;

            V2Emitters.Remove(emitter);
            UnfilteredV2Emitters.Remove(emitter);
        }

        public static bool IsV2Emitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && V2Emitters.Contains(emitter);
        }

        public static bool ShouldSkipEngineFilter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && UnfilteredV2Emitters.Contains(emitter);
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

                    ProcessThruster(thruster, true);
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

            if (stale == null)
                return;

            foreach (long id in stale)
                KnownThrusters.Remove(id);
        }

        private static void StopAllEmitters()
        {
            foreach (V2GridAudioState state in GridStates.Values)
                state.Stop();
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
}
