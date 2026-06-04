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

        public static void Update(V2AudioListenerState listener, int activeDetailSources, int activeStateSources)
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
                "route=v2 mode={0} room={1} inside={2} detail={3}/{4:0.00}/x{5} state={6}/{7:0.00}/x{8} dist={9:0} curve={10:0.00} state2dpos={11} atm={12:0.00}",
                snapshot.Listener.ModeName,
                room,
                snapshot.Listener.InsideShip ? "Y" : "N",
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
            public V2AudioListenerState Listener;
        }
    }
}
