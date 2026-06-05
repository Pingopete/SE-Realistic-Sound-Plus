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

        public static void Update(V2AudioListenerState listener, int activeDetailSources, int activeStateSources, int knownSourceGroups, ThrusterReportSnapshot thrusters)
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
            return string.Format(
                CultureInfo.InvariantCulture,
                "route=v2 mode={0} room={1} inside={2} groups={3} thr={4}/{5}/{6} rej={7}/{8}{9} emit={10}/{11} flt={12}{13} detail={14}/{15:0.00}/x{16} state={17}/{18:0.00}/x{19} dist={20:0} curve={21:0.00} state2dpos={22} atm={23:0.00}",
                snapshot.Listener.ModeName,
                room,
                snapshot.Listener.InsideShip ? "Y" : "N",
                snapshot.KnownSourceGroups,
                snapshot.Thrusters.PatchHits,
                snapshot.Thrusters.RawReports,
                snapshot.Thrusters.AcceptedReports,
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
            public int FallbackRejectedReports;
            public int GridMismatchReports;
            public int RegisteredEmitters;
            public int UnfilteredEmitters;
            public int FilterHits;
            public bool FilterDisabled;
        }
    }
}
