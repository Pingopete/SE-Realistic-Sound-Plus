using System;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal enum V2ThrustDirectionGroup
    {
        Forward = 0,
        Backward = 1,
        Left = 2,
        Right = 3,
        Up = 4,
        Down = 5
    }

    internal struct V2DirectionalSource
    {
        public bool Active;
        public V2ThrustDirectionGroup Direction;
        public Vector3D Position;
        public float Weight;
    }

    internal sealed class SixDirectionSourceModel
    {
        private readonly V2DirectionalSource[] _sources = new V2DirectionalSource[6];

        public SixDirectionSourceModel()
        {
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                _sources[i] = new V2DirectionalSource
                {
                    Direction = (V2ThrustDirectionGroup)i
                };
            }
        }

        public void AddWeightedSource(V2ThrustDirectionGroup direction, Vector3D position, float weight)
        {
            if (weight <= 0f)
                return;

            int index = (int)direction;
            if (index < 0 || index >= _sources.Length)
                return;

            V2DirectionalSource source = _sources[index];
            double combinedWeight = source.Weight + weight;
            source.Position = source.Active && combinedWeight > 0.0001
                ? (source.Position * source.Weight + position * weight) / combinedWeight
                : position;
            source.Weight = (float)combinedWeight;
            source.Active = true;
            _sources[index] = source;
        }

        public int CopyTo(V2DirectionalSource[] destination)
        {
            if (destination == null)
                return 0;

            int count = Math.Min(destination.Length, _sources.Length);
            for (int i = 0; i < count; i++)
                destination[i] = _sources[i];

            return count;
        }

        public static float EvaluateDistanceGain(float distance, float maxDistance, float curveExponent)
        {
            if (maxDistance <= 0f)
                return 0f;

            float normalized = Clamp01(distance / maxDistance);
            float linear = 1f - normalized;
            float exponent = Math.Max(0.1f, curveExponent);
            return (float)Math.Pow(linear, exponent);
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
