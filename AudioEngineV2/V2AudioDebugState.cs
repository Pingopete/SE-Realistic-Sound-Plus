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
                DetailIdleEnabled = settings.V2DetailIdleEnabled,
                Detail2DPositionalTest = settings.V2Detail2DPositionalTest,
                StateEnabled = settings.V2StateEnabled,
                State2DPositionalTest = settings.V2State2DPositionalTest,
                DetailGain = settings.V2DetailGain,
                DetailIdleGain = settings.V2DetailIdleGain,
                StateGain = settings.V2StateGain,
                Distance = settings.V2EmitterDistance,
                DistanceCurve = settings.V2DistanceCurve,
                CommandSmoothingMs = settings.V2DetailCommandSmoothingMs,
                EmitterFadeMs = settings.V2EmitterFadeInMs,
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
            string sealedStatus = FormatSealedStatus();
            return string.Format(
                CultureInfo.InvariantCulture,
                "route=v2 mode={0} room={1} inside={2} sealed={39} grid={3} char={4} contact={5}/{6} move={7} grids={8} groups={9} known={10} scan={11}/{12} thr={13}/{14}/{15}+{16} rej={17}/{18}{19} emit={20}/{21} flt={22}{23} detail={24}/{25:0.00}/x{26} idle={27}/{28:0.00} detail2dpos={29} state={30}/{31:0.00}/x{32} dist={33:0} curve={34:0.00} cmdsmooth={35:0} emitfade={36:0} state2dpos={37} atm={38:0.00}",
                snapshot.Listener.ModeName,
                room,
                snapshot.Listener.InsideShip ? "Y" : "N",
                ShortId(snapshot.Listener.GridEntityId),
                Trim(snapshot.Listener.CharacterMovementState, 12),
                snapshot.Listener.ContactSource ?? "-",
                ShortId(snapshot.Listener.ContactGridEntityId),
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
                snapshot.DetailIdleEnabled ? "on" : "off",
                snapshot.DetailIdleGain,
                snapshot.Detail2DPositionalTest ? "on" : "off",
                snapshot.StateEnabled ? "on" : "off",
                snapshot.StateGain,
                snapshot.ActiveStateSources,
                snapshot.Distance,
                snapshot.DistanceCurve,
                snapshot.CommandSmoothingMs,
                snapshot.EmitterFadeMs,
                snapshot.State2DPositionalTest ? "on" : "off",
                snapshot.Listener.Atmosphere,
                sealedStatus);
        }

        public static string[] FormatCompactLines()
        {
            Snapshot snapshot = _snapshot;
            if (snapshot.UpdatedUtc == default(DateTime))
                return new[] { "V2: uninitialized" };

            string room = Trim(snapshot.Listener.RoomName, 40);
            string grid = ShortId(snapshot.Listener.GridEntityId);
            string contact = (snapshot.Listener.ContactSource ?? "-") + "/" + ShortId(snapshot.Listener.ContactGridEntityId);
            string move = snapshot.Listener.HasMoveInput
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", snapshot.Listener.MoveInput.X, snapshot.Listener.MoveInput.Y, snapshot.Listener.MoveInput.Z)
                : "-";
            string sealedStatus = FormatSealedStatus();
            string envStatus = FormatEnvironmentStatus();

            return new[]
            {
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Route: mode={0} room={1} inside={2} sealed={3} {4}",
                    snapshot.Listener.ModeName ?? "?",
                    room,
                    snapshot.Listener.InsideShip ? "Y" : "N",
                    sealedStatus,
                    envStatus),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Grid: id={0} contact={1} char={2} move={3}",
                    grid,
                    contact,
                    Trim(snapshot.Listener.CharacterMovementState, 12),
                    move),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Engine sources: grids={0} groups={1} thrusters={2} scan={3}/{4} emit={5}/{6}",
                    snapshot.GridStates,
                    snapshot.KnownSourceGroups,
                    snapshot.KnownThrusters,
                    snapshot.CensusProcessed,
                    snapshot.CensusRemoved,
                    snapshot.Thrusters.RegisteredEmitters,
                    snapshot.Thrusters.UnfilteredEmitters),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Detail: {0} gain={1:0.00} active={2} idle={3}/{4:0.00} d2pos={5}",
                    snapshot.DetailEnabled ? "on" : "off",
                    snapshot.DetailGain,
                    snapshot.ActiveDetailSources,
                    snapshot.DetailIdleEnabled ? "on" : "off",
                    snapshot.DetailIdleGain,
                    snapshot.Detail2DPositionalTest ? "on" : "off"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "State: {0} gain={1:0.00} active={2} d2pos={3}",
                    snapshot.StateEnabled ? "on" : "off",
                    snapshot.StateGain,
                    snapshot.ActiveStateSources,
                    snapshot.State2DPositionalTest ? "on" : "off")
            };
        }

        private static string ShortId(long id)
        {
            if (id == 0L)
                return "0";

            long positive = id < 0L ? -id : id;
            return (positive % 1000000L).ToString(CultureInfo.InvariantCulture);
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        private static string FormatSealedStatus()
        {
            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "?";

            return (sample.SealedEstimate ? "Y" : "N") + "/" + (sample.SealedSource ?? "none");
        }

        private static string FormatEnvironmentStatus()
        {
            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "env=?";

            return string.Format(
                CultureInfo.InvariantCulture,
                "env open={0:0.00} ap={1:0.00} muff={2:0.00}",
                sample.OpenFraction,
                sample.ApertureFraction,
                sample.FinalMuffling);
        }

        private struct Snapshot
        {
            public DateTime UpdatedUtc;
            public bool DetailEnabled;
            public bool DetailIdleEnabled;
            public bool Detail2DPositionalTest;
            public bool StateEnabled;
            public bool State2DPositionalTest;
            public float DetailGain;
            public float DetailIdleGain;
            public float StateGain;
            public float Distance;
            public float DistanceCurve;
            public float CommandSmoothingMs;
            public float EmitterFadeMs;
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
