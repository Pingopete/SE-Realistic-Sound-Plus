using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    // Classification of a contiguous stretch of the source->listener occlusion ray, used by the block
    // occlusion debug overlay to colour-code where the path accumulates its thickness value.
    internal enum ThicknessSegmentKind
    {
        Open,
        Structure,
        Voxel
    }

    // A raw blocked range along a ray, expressed as fractions (0..1) of the probed segment. Returned by
    // the thickness probe so it stays endpoint-agnostic: the overlay can re-project cached fractions onto
    // the live ray endpoints each frame without re-casting.
    internal struct ThicknessInterval
    {
        public float Start;
        public float End;

        public ThicknessInterval(float start, float end)
        {
            Start = start;
            End = end;
        }
    }

    // A drawable, world-space stretch of the occlusion ray with its thickness classification and length.
    internal struct ThicknessSegment
    {
        public Vector3D From;
        public Vector3D To;
        public ThicknessSegmentKind Kind;
        public float Meters;
    }
}
