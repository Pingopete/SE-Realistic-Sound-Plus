using System.Globalization;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2BlockSoundSourceResolver
    {
        private static long _physicalEmitterBlocks;
        private static long _resolvedBlockAttempts;

        public static void Reset()
        {
            _physicalEmitterBlocks = 0L;
            _resolvedBlockAttempts = 0L;
        }

        public static void RecordPhysicalEmitterBlock()
        {
            _physicalEmitterBlocks = SaturatingIncrement(_physicalEmitterBlocks);
        }

        public static void RecordResolvedBlockAttempt()
        {
            _resolvedBlockAttempts = SaturatingIncrement(_resolvedBlockAttempts);
        }

        public static string FormatPerfSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "resolver directPhys={0} cueFallback={1} scan=disabled cache=disabled {2}",
                _physicalEmitterBlocks,
                _resolvedBlockAttempts,
                RspDynamicAudioFilters.FormatEmitterBindingSummary());
        }

        private static long SaturatingIncrement(long value)
        {
            return value == long.MaxValue ? value : value + 1L;
        }
    }
}
