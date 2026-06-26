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
        public Vector3D ProbeFrom;
        public Vector3D ProbeTo;
        public bool AirPathAvailable;     // an open-air detour to the listener was found (flood-fill)
        public float AirPathLength;       // open-air detour length (m)
        public bool MergedFromAirPath;    // the air leg actually reduced muffle/gain
        public float PreAirPathMuffling;  // through-structure FinalMuffling before the air-path merge
        public Vector3D PortalWorld;      // doorway the listener localises the air leg to (reposition anchor)
        public bool PortalValid;          // a usable portal distinct from the listener was found
        public bool RepositionApplied;    // the emitter is being moved to the portal this sample
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
