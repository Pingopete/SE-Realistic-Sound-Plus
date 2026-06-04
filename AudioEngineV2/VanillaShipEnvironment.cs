using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class VanillaShipEnvironment
    {
        private static readonly TimeSpan ReportLifetime = TimeSpan.FromMilliseconds(750);
        private static readonly FieldInfo ShipGridField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipGrid");
        private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags InstanceProperties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo[] _roomCandidateFields;
        private static bool _roomProbeInitialized;
        private static int _roomProbeErrors;
        private static Snapshot _latest;

        public static void Reset()
        {
            _roomProbeErrors = 0;
            _latest = default(Snapshot);
        }

        public static void ReportShipSoundComponent(MyShipSoundComponent component, bool insideShip)
        {
            if (component == null)
                return;

            MyCubeGrid grid = TryGetGrid(component);
            string roomName = TryReadRoomName(component);
            if (string.IsNullOrWhiteSpace(roomName))
                roomName = insideShip ? "inside: vanilla room id unavailable" : "outside";

            _latest = new Snapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                InsideShip = insideShip,
                RoomName = roomName,
                GridEntityId = grid != null ? grid.EntityId : 0L,
                Source = "MyShipSoundComponent",
                ListenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero
            };
        }

        public static bool TryGetLatest(out Snapshot snapshot)
        {
            snapshot = _latest;
            return snapshot.UpdatedUtc != default(DateTime)
                && DateTime.UtcNow - snapshot.UpdatedUtc <= ReportLifetime;
        }

        private static MyCubeGrid TryGetGrid(MyShipSoundComponent component)
        {
            try
            {
                return ShipGridField?.GetValue(component) as MyCubeGrid;
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadRoomName(MyShipSoundComponent component)
        {
            if (_roomProbeErrors >= 3)
                return null;

            try
            {
                EnsureRoomProbeInitialized();
                if (_roomCandidateFields == null || _roomCandidateFields.Length == 0)
                    return null;

                foreach (FieldInfo field in _roomCandidateFields)
                {
                    object value = field.GetValue(component);
                    string formatted = FormatRoomValue(field.Name, value);
                    if (!string.IsNullOrWhiteSpace(formatted))
                        return formatted;
                }
            }
            catch (Exception ex)
            {
                if (++_roomProbeErrors >= 3)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 room probe disabled after error: " + ex);
            }

            return null;
        }

        private static void EnsureRoomProbeInitialized()
        {
            if (_roomProbeInitialized)
                return;

            _roomProbeInitialized = true;
            List<FieldInfo> candidates = new List<FieldInfo>();
            FieldInfo[] fields = typeof(MyShipSoundComponent).GetFields(InstanceFields);
            foreach (FieldInfo field in fields)
            {
                if (IsRoomLikeName(field.Name))
                    candidates.Add(field);
            }

            _roomCandidateFields = candidates.ToArray();
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 vanilla room probe found " + _roomCandidateFields.Length + " room-like MyShipSoundComponent fields.");
        }

        private static bool IsRoomLikeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string normalized = name.ToLowerInvariant();
            return normalized.Contains("room")
                || normalized.Contains("oxygen")
                || normalized.Contains("pressur")
                || normalized.Contains("enclos");
        }

        private static string FormatRoomValue(string fieldName, object value)
        {
            if (value == null)
                return null;

            string text = value as string;
            if (!string.IsNullOrWhiteSpace(text))
                return fieldName + "=" + text;

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
                return fieldName + "=" + value;

            string namedValue = TryReadNamedProperty(value, "RoomName")
                ?? TryReadNamedProperty(value, "Name")
                ?? TryReadNamedProperty(value, "DisplayName")
                ?? TryReadNamedProperty(value, "DisplayNameText")
                ?? TryReadNamedProperty(value, "Id")
                ?? TryReadNamedField(value, "RoomName")
                ?? TryReadNamedField(value, "Name")
                ?? TryReadNamedField(value, "Id");

            if (!string.IsNullOrWhiteSpace(namedValue))
                return fieldName + "=" + namedValue;

            text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text) && text != type.FullName)
                return fieldName + "=" + text;

            return fieldName + "=" + type.Name;
        }

        private static string TryReadNamedProperty(object value, string name)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, InstanceProperties);
                object result = property?.GetValue(value, null);
                return result?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadNamedField(object value, string name)
        {
            try
            {
                FieldInfo field = value.GetType().GetField(name, InstanceFields);
                object result = field?.GetValue(value);
                return result?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public struct Snapshot
        {
            public DateTime UpdatedUtc;
            public bool InsideShip;
            public string RoomName;
            public long GridEntityId;
            public string Source;
            public Vector3D ListenerPosition;
        }
    }
}
