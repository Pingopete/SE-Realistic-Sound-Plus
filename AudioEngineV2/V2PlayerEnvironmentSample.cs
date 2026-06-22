using System;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2PlayerEnvironmentSample
    {
        public DateTime UpdatedUtc;
        public bool Valid;
        public bool RaycastAvailable;
        public string RaycastMode;
        public float RayLength;
        public int RaysCast;
        public int OpenRays;
        public int BlockedRays;
        public float OpenRayWeight;
        public float TotalRayWeight;
        public float AverageBlockedMeters;
        public float WeightedBlockedMeters;
        public float VoxelBlockedMeters;
        public float OpenFraction;
        public float ApertureFraction;
        public float StructuralOcclusion;
        public float ContinuousMuffling;
        public bool VanillaInside;
        public bool SealedEstimate;
        public string SealedSource;
        public float SealedExtraMuffling;
        public float FinalMuffling;
        public float WindExposure;
        public float WindAudibility;
        public float LocalAtmosphere;
        public bool OxygenProbeAvailable;
        public bool OxygenRoomPresent;
        public bool OxygenRoomAirtight;
        public bool OxygenRoomDirty;
        public int OxygenRoomProbeCount;
        public int OxygenAirtightProbeCount;
        public float OxygenLevel;
        public float OxygenRoomLevel;
        public string OxygenProbeSource;
        public long OxygenGridEntityId;
        public string RoomName;
        public string ListenerMode;
    }
}
