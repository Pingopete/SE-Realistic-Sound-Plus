using RealisticSoundPlus.Patches;
using Sandbox.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2AudioListenerState
    {
        public Vector3D Position;
        public float Atmosphere;
        public bool InsideShip;
        public bool SeatedInShip;
        public bool VanillaFallback;
        public string RoomName;
        public long GridEntityId;
        public string ModeName;

        public static V2AudioListenerState Capture()
        {
            Vector3D position = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            bool seated = MyAPIGateway.Session?.ControlledObject is IMyShipController;
            bool vanillaInside = false;
            string roomName = "room=?";
            long gridEntityId = 0L;

            if (VanillaShipEnvironment.TryGetLatest(out VanillaShipEnvironment.Snapshot vanilla))
            {
                vanillaInside = vanilla.InsideShip;
                roomName = vanilla.RoomName;
                gridEntityId = vanilla.GridEntityId;
            }

            bool insideShip = vanillaInside || seated;
            string modeName = insideShip
                ? (seated ? "inside-seat" : "inside-room")
                : "vanilla-fallback";

            return new V2AudioListenerState
            {
                Position = position,
                Atmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(position),
                InsideShip = insideShip,
                SeatedInShip = seated,
                VanillaFallback = !insideShip,
                RoomName = roomName,
                GridEntityId = gridEntityId,
                ModeName = modeName
            };
        }
    }
}
