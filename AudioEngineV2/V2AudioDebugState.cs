using System;
using System.Globalization;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AudioDebugState
    {
        private static Snapshot _snapshot;

        public static void Reset()
        {
            _snapshot = default(Snapshot);
        }

        public static void Update(V2AudioListenerState listener, int gridStates, int knownThrusters, int censusProcessed, int censusRemoved, int activeDetailSources, int activeStateSources, int knownSourceGroups, ThrusterReportSnapshot thrusters)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            _snapshot = new Snapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                DetailEnabled = settings.V2DetailEnabled,
                StateEnabled = settings.V2StateEnabled,
                State2DPositionalTest = settings.V2State2DPositionalTest,
                DetailGain = settings.V2DetailGain,
                StateGain = settings.V2StateGain,
                Distance = settings.V2EmitterDistance,
                DistanceCurve = settings.V2DistanceCurve,
                CommandSmoothingMs = settings.V2DetailCommandSmoothingMs,
                GridStates = gridStates,
                KnownThrusters = knownThrusters,
                CensusProcessed = censusProcessed,
                CensusRemoved = censusRemoved,
                ActiveDetailSources = activeDetailSources,
                ActiveStateSources = activeStateSources,
                KnownSourceGroups = knownSourceGroups,
                Thrusters = thrusters,
                Listener = listener
            };
        }

        public static string Format()
        {
            Snapshot snapshot = _snapshot;
            if (snapshot.UpdatedUtc == default(DateTime))
                return "v2=uninitialized";

            string room = Trim(snapshot.Listener.RoomName, 42);
            string move = snapshot.Listener.HasMoveInput
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", snapshot.Listener.MoveInput.X, snapshot.Listener.MoveInput.Y, snapshot.Listener.MoveInput.Z)
                : "-";
            return string.Format(
                CultureInfo.InvariantCulture,
                "route=v2 mode={0} room={1} inside={2} move={3} grids={4} groups={5} known={6} scan={7}/{8} thr={9}/{10}/{11}+{12} rej={13}/{14}{15} emit={16}/{17} flt={18}{19} detail={20}/{21:0.00}/x{22} state={23}/{24:0.00}/x{25} dist={26:0} curve={27:0.00} cmdsmooth={28:0} state2dpos={29} atm={30:0.00}",
                snapshot.Listener.ModeName,
                room,
                snapshot.Listener.InsideShip ? "Y" : "N",
                move,
                snapshot.GridStates,
                snapshot.KnownSourceGroups,
                snapshot.KnownThrusters,
                snapshot.CensusProcessed,
                snapshot.CensusRemoved,
                snapshot.Thrusters.PatchHits,
                snapshot.Thrusters.RawReports,
                snapshot.Thrusters.AcceptedReports,
                snapshot.Thrusters.CensusReports,
                snapshot.Thrusters.FallbackRejectedReports,
                snapshot.Thrusters.GridMismatchReports,
                snapshot.Thrusters.PatchDisabled ? " DISABLED" : string.Empty,
                snapshot.Thrusters.RegisteredEmitters,
                snapshot.Thrusters.UnfilteredEmitters,
                snapshot.Thrusters.FilterHits,
                snapshot.Thrusters.FilterDisabled ? " DISABLED" : string.Empty,
                snapshot.DetailEnabled ? "on" : "off",
                snapshot.DetailGain,
                snapshot.ActiveDetailSources,
                snapshot.StateEnabled ? "on" : "off",
                snapshot.StateGain,
                snapshot.ActiveStateSources,
                snapshot.Distance,
                snapshot.DistanceCurve,
                snapshot.CommandSmoothingMs,
                snapshot.State2DPositionalTest ? "on" : "off",
                snapshot.Listener.Atmosphere);
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        private struct Snapshot
        {
            public DateTime UpdatedUtc;
            public bool DetailEnabled;
            public bool StateEnabled;
            public bool State2DPositionalTest;
            public float DetailGain;
            public float StateGain;
            public float Distance;
            public float DistanceCurve;
            public float CommandSmoothingMs;
            public int GridStates;
            public int KnownThrusters;
            public int CensusProcessed;
            public int CensusRemoved;
            public int ActiveDetailSources;
            public int ActiveStateSources;
            public int KnownSourceGroups;
            public ThrusterReportSnapshot Thrusters;
            public V2AudioListenerState Listener;
        }

        public struct ThrusterReportSnapshot
        {
            public int PatchHits;
            public bool PatchDisabled;
            public int RawReports;
            public int AcceptedReports;
            public int CensusReports;
            public int FallbackRejectedReports;
            public int GridMismatchReports;
            public int RegisteredEmitters;
            public int UnfilteredEmitters;
            public int FilterHits;
            public bool FilterDisabled;
        }
    }
}
