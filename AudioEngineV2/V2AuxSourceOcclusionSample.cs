using System;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2AuxSourceOcclusionSample
    {
        public DateTime UpdatedUtc;
        public string CueName;
        public string Kind;
        public string ClassName;
        public float Score;
        public Vector3D SourcePosition;
        public Vector3D ListenerPosition;
        public Vector3D FirstBlockedPosition;
        public float Distance;
        public float PathLength;
        public bool MainRayBlocked;
        public float EstimatedBlockedLength;
        public float VanillaMaxDistance;
        public float EffectiveRange;
        public bool CustomRangeApplied;
        public float VanillaDistanceGain;
        public float DesiredDistanceGain;
        public float RangeScale;
        public int RaysCast;
        public int OpenRays;
        public int BlockedRays;
        public float OpenFraction;
        public float Occlusion;
        public float ContinuousMuffling;
        public float DistanceFactor;
        public float PathMuffling;
        public float NearFieldScale;
        public bool RoomComparisonAvailable;
        public bool SameOxygenRoom;
        public string RoomComparison;
        public bool SealedExtraApplied;
        public float FinalMuffling;
        public float EstimatedGain;
        public float EstimatedCutoff;
        public float EstimatedQ;
        public float LocalAtmosphere;
    }
}
