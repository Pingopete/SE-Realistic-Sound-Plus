using System;
using System.Globalization;
using System.Threading;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAPO;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal sealed class V2LiveReverbPocProcessor : CallbackBase, AudioProcessor
    {
        private static readonly Guid ProcessorId = new Guid("B7B1468C-8738-4E95-8C86-6D69062DCB7A");
        private const int DelayLineCount = 8;
        private const int EarlyTapCount = 6;
        private const float StartupRampSeconds = 0.35f;
        private const float InternalSampleLimit = 1.25f;
        private const float UnsafeSampleLimit = 8f;
        private const float OutputSampleLimit = 0.98f;

        private readonly RegistrationProperties _registration;
        private readonly bool _wetOnly;
        private readonly float[] _lateRead = new float[DelayLineCount];
        private readonly float[] _lateMixed = new float[DelayLineCount];
        private readonly float[] _lateDamp = new float[DelayLineCount];
        private readonly int[] _lateDelayFrames = new int[DelayLineCount];
        private readonly float[] _lateFeedback = new float[DelayLineCount];
        private readonly int[] _earlyTapFrames = new int[EarlyTapCount];
        private readonly float[] _echoDamp = new float[2];

        private float[][] _lateLines;
        private float[] _earlyLine;
        private float[] _echoLine;
        private int _sampleRate;
        private int _channels;
        private int _lateRingFrames;
        private int _earlyRingFrames;
        private int _echoRingFrames;
        private int _lateWriteFrame;
        private int _earlyWriteFrame;
        private int _echoWriteFrame;
        private int _echoDelayFrames;
        private int _bytesPerSample = 4;
        private bool _lockedFormatIsFloat = true;
        private float _diffusionMix;
        private float _lateInputGain;
        private float _earlyGain;
        private float _lateGain;
        private float _echoGain;
        private float _echoFeedback;
        private float _lowPassA;
        private float _wetScale;
        // Block dry/wet split: independent dry-passthrough and wet (reverb) gains. Default 1/1 leaves the inline
        // reverb instance untouched; the block reverb instance drives these from occlusion (dry down, wet up).
        private volatile float _blockDryGain = 1f;
        private volatile float _blockWetMix = 1f;
        private float _tailEnergy;
        private float _lastRoomSize;
        private float _lastRadius;
        private float _lastDecaySeconds;
        private float _lastPressure;
        private float _lastCutoffHz;
        private DateTime _lastParameterLogUtc = DateTime.MinValue;
        private string _lastParameterLogKey = string.Empty;
        private long _lockCount;
        private long _unlockCount;
        private long _resetCount;
        private long _processCount;
        private long _silentInputCount;
        private long _disabledProcessCount;
        private long _unsupportedFormatProcessCount;
        private long _unsafeSampleCount;
        private long _panicClearCount;
        private long _processedFramesSinceReset;
        private int _lastFrameCount;
        private int _lastInputFlags;
        private int _lastOutputFlags;
        private int _lastInputValid;
        private int _lastInputPointer;
        private int _lastOutputPointer;
        private float _lastInputPeak;
        private float _lastOutputPeak;
        private float _lastRamp;
        private string _lastFormatStatus = "fmt=float";

        public V2LiveReverbPocProcessor(int channels, int sampleRate)
            : this(channels, sampleRate, true)
        {
        }

        public V2LiveReverbPocProcessor(int channels, int sampleRate, bool wetOnly)
        {
            _wetOnly = wetOnly;
            _registration = new RegistrationProperties
            {
                Clsid = ProcessorId,
                FriendlyName = "RSP Live Room DSP",
                CopyrightInfo = "Realistic Sound Plus",
                MajorVersion = 0,
                MinorVersion = 2,
                Flags = PropertyFlags.ChannelsMustMatch | PropertyFlags.FrameRateMustMatch | PropertyFlags.InplaceSupported,
                MinInputBufferCount = 1,
                MaxInputBufferCount = 1,
                MinOutputBufferCount = 1,
                MaxOutputBufferCount = 1
            };

            Configure(Math.Max(1, channels), Math.Max(8000, sampleRate));
            UpdateFromSettings(SettingsManager.Current);
            V2DebugLog.WriteEvent("live-custom-dsp", "ctor " + Status);
        }

        public RegistrationProperties RegistrationProperties => _registration;

        public string Status
        {
            get
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "liveRoom={0}/{1}Hz/{2}ch room={3:0.00} rad={4:0.0}m decay={5:0.0}s wet={6:0.00} early={7:0.000} late={8:0.000} echo={9:0.000}/{10:0.00}@{11:0}ms cutoff={12:0}Hz p={13:0.00}",
                    _wetOnly ? "wet" : "inline",
                    _sampleRate,
                    _channels,
                    _lastRoomSize,
                    _lastRadius,
                    _lastDecaySeconds,
                    _wetScale,
                    _earlyGain,
                    _lateGain,
                    _echoGain,
                    _echoFeedback,
                    _sampleRate > 0 ? _echoDelayFrames * 1000f / _sampleRate : 0f,
                    _lastCutoffHz,
                    _lastPressure);
            }
        }

        public string DiagnosticStatus
        {
            get
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} lock={1} unlock={2} reset={3} proc={4} disabled={5} unsupported={6} silentIn={7} unsafe={8} panic={9} frames={10} inValid={11} ptr={12}/{13} flags={14}/{15} peak={16:0.0000}/{17:0.0000} tail={18:0.0000} ramp={19:0.00} {20}",
                    Status,
                    Interlocked.Read(ref _lockCount),
                    Interlocked.Read(ref _unlockCount),
                    Interlocked.Read(ref _resetCount),
                    Interlocked.Read(ref _processCount),
                    Interlocked.Read(ref _disabledProcessCount),
                    Interlocked.Read(ref _unsupportedFormatProcessCount),
                    Interlocked.Read(ref _silentInputCount),
                    Interlocked.Read(ref _unsafeSampleCount),
                    Interlocked.Read(ref _panicClearCount),
                    _lastFrameCount,
                    _lastInputValid,
                    _lastInputPointer,
                    _lastOutputPointer,
                    _lastInputFlags,
                    _lastOutputFlags,
                    _lastInputPeak,
                    _lastOutputPeak,
                    _tailEnergy,
                    _lastRamp,
                    _lastFormatStatus);
            }
        }

        // Sets the block dry/wet split gains (non-wet-only instances). dryGain scales the direct passthrough,
        // wetMix scales the reverb. Occluded -> low dry / high wet so the source reads mostly as its reverb tail.
        public void SetBlockDryWet(float dryGain, float wetMix)
        {
            _blockDryGain = dryGain < 0f ? 0f : dryGain;
            _blockWetMix = wetMix < 0f ? 0f : wetMix;
        }

        public void UpdateFromSettings(RealisticSoundPlusSettings settings)
        {
            V2LiveReverbParameters parameters = V2ManagedDspReverbRuntime.ResolveLiveParameters(settings);
            int sampleRate = Math.Max(8000, _sampleRate);
            float room = Clamp01(parameters.RoomSize);
            float density = Clamp(parameters.Density / 100f, 0f, 1f);
            float diffusion = Clamp01(parameters.Diffusion);
            float decay = Clamp(parameters.DecaySeconds, 0.25f, 24f);
            float pressure = Clamp01(parameters.AirPressure <= 0f ? 0f : parameters.AirPressure);
            float pressureCurve = (float)Math.Sqrt(pressure);
            float apertureEnclosure = 1f - Clamp01(parameters.ApertureFraction);
            float enclosure = Math.Max(apertureEnclosure, Clamp01(parameters.ClosedFraction));
            enclosure = Math.Max(enclosure, Clamp01(parameters.StructuralOcclusion * 0.75f + parameters.FinalMuffling * 0.25f));
            float wetSend = Clamp(parameters.WetSend, 0f, 4f);
            float pressureWet = 0.12f + pressureCurve * 0.88f;
            float wetScale = wetSend * (0.18f + enclosure * 0.82f) * pressureWet;
            float hfFactor = Clamp(DbToLinear(parameters.HighFrequencyDb), 0.04f, 1f);
            float cutoff = Clamp(parameters.ToneHz * (0.55f + 0.45f * hfFactor), 350f, 20000f);
            float lowPassA = (float)Math.Exp(-2.0 * Math.PI * cutoff / sampleRate);
            float earlyGain = DbToLinear(parameters.EarlyGainDb) * wetScale * 0.040f;
            float lateGain = DbToLinear(parameters.TailGainDb) * wetScale * 0.026f;
            float radiusBoost = Clamp01((parameters.EquivalentRadius - 4f) / 24f);
            float echoGain = wetScale * (0.010f + room * 0.060f + radiusBoost * 0.070f);
            float echoFeedback = Clamp((0.05f + room * 0.26f + decay * 0.018f) * (0.55f + pressureCurve * 0.45f), 0.02f, 0.58f);
            float echoDelayMs = Clamp(18f + parameters.EquivalentRadius * 6.5f + parameters.LateDelayMs * 0.35f, 24f, 280f);

            BuildLateDelayFrames(room, density, decay, sampleRate);
            BuildEarlyTapFrames(room, parameters.PredelayMs, sampleRate);

            _diffusionMix = Clamp(0.35f + diffusion * 0.65f, 0.30f, 0.985f);
            _lateInputGain = Clamp(0.20f + density * 0.52f, 0.12f, 0.82f);
            _earlyGain = Clamp(earlyGain, 0f, 1.2f);
            _lateGain = Clamp(lateGain, 0f, 1.2f);
            _echoGain = Clamp(echoGain, 0f, 0.75f);
            _echoFeedback = echoFeedback;
            _echoDelayFrames = ClampInt((int)Math.Round(echoDelayMs * sampleRate / 1000f), 1, Math.Max(1, _echoRingFrames - 1));
            _lowPassA = Clamp(lowPassA, 0f, 0.9999f);
            _wetScale = wetScale;
            _lastRoomSize = room;
            _lastRadius = parameters.EquivalentRadius;
            _lastDecaySeconds = decay;
            _lastPressure = pressure;
            _lastCutoffHz = cutoff;

            string key = string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.00}:{1:0.0}:{2:0.0}:{3:0.00}:{4:0}:{5:0.00}",
                room,
                parameters.EquivalentRadius,
                decay,
                pressure,
                cutoff,
                wetScale);
            DateTime now = DateTime.UtcNow;
            if (!string.Equals(key, _lastParameterLogKey, StringComparison.Ordinal) && now - _lastParameterLogUtc > TimeSpan.FromSeconds(1.5))
            {
                _lastParameterLogKey = key;
                _lastParameterLogUtc = now;
                V2DebugLog.WriteEvent("live-custom-dsp", "params " + Status + " " + parameters.ToStatus());
            }
        }

        public bool IsInputFormatSupported(WaveFormat outputFormat, WaveFormat requestedInputFormat, out WaveFormat supportedInputFormat)
        {
            supportedInputFormat = requestedInputFormat ?? outputFormat;
            return supportedInputFormat != null;
        }

        public bool IsOutputFormatSupported(WaveFormat inputFormat, WaveFormat requestedOutputFormat, out WaveFormat supportedOutputFormat)
        {
            supportedOutputFormat = requestedOutputFormat ?? inputFormat;
            return supportedOutputFormat != null;
        }

        public void Initialize(DataStream stream)
        {
            V2DebugLog.WriteEvent("live-custom-dsp", "initialize stream=" + (stream == null ? "null" : stream.Length.ToString(CultureInfo.InvariantCulture)));
        }

        public void Reset()
        {
            Interlocked.Increment(ref _resetCount);
            ClearBuffers();
            V2DebugLog.WriteEvent("live-custom-dsp", "reset " + Status);
        }

        public void LockForProcess(LockParameters[] inputLockedParameters, LockParameters[] outputLockedParameters)
        {
            Interlocked.Increment(ref _lockCount);
            WaveFormat format = null;
            if (outputLockedParameters != null && outputLockedParameters.Length > 0)
                format = outputLockedParameters[0].Format;
            if (format == null && inputLockedParameters != null && inputLockedParameters.Length > 0)
                format = inputLockedParameters[0].Format;

            int channels = Math.Max(1, format?.Channels ?? _channels);
            int sampleRate = Math.Max(8000, format?.SampleRate ?? _sampleRate);
            _bytesPerSample = Math.Max(1, (format?.BitsPerSample ?? 32) / 8);
            _lockedFormatIsFloat = IsFloatFormat(format);
            _lastFormatStatus = _lockedFormatIsFloat
                ? "fmt=float"
                : "fmt=unsupported-" + DescribeFormat(format);
            Configure(channels, sampleRate);
            UpdateFromSettings(SettingsManager.Current);
            V2DebugLog.WriteEvent(
                "live-custom-dsp",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "lock in={0}/{1} out={2}/{3} fmt={4} {5}",
                    inputLockedParameters == null ? 0 : inputLockedParameters.Length,
                    DescribeLock(inputLockedParameters),
                    outputLockedParameters == null ? 0 : outputLockedParameters.Length,
                    DescribeLock(outputLockedParameters),
                    DescribeFormat(format),
                    Status));
        }

        public void UnlockForProcess()
        {
            Interlocked.Increment(ref _unlockCount);
            V2DebugLog.WriteEvent("live-custom-dsp", "unlock " + DiagnosticStatus);
        }

        public unsafe void Process(BufferParameters[] inputProcessParameters, BufferParameters[] outputProcessParameters, bool isEnabled)
        {
            if (outputProcessParameters == null || outputProcessParameters.Length == 0)
                return;

            BufferParameters input = inputProcessParameters != null && inputProcessParameters.Length > 0
                ? inputProcessParameters[0]
                : default(BufferParameters);
            BufferParameters output = outputProcessParameters[0];
            int frames = output.ValidFrameCount > 0 ? output.ValidFrameCount : input.ValidFrameCount;
            int channels = Math.Max(1, _channels);
            int samples = Math.Max(0, frames * channels);
            Interlocked.Increment(ref _processCount);
            _lastFrameCount = frames;
            _lastInputFlags = (int)input.BufferFlags;
            _lastInputPointer = input.Buffer == IntPtr.Zero ? 0 : 1;
            _lastOutputPointer = output.Buffer == IntPtr.Zero ? 0 : 1;

            if (output.Buffer == IntPtr.Zero || samples == 0 || !isEnabled || !HasBuffers())
            {
                if (!isEnabled)
                    Interlocked.Increment(ref _disabledProcessCount);
                CopyDryOrSilence(input, ref outputProcessParameters[0], samples, _wetOnly);
                return;
            }

            if (!_lockedFormatIsFloat)
            {
                Interlocked.Increment(ref _unsupportedFormatProcessCount);
                CopyUnsupportedFormat(input, ref outputProcessParameters[0], frames);
                return;
            }

            float* outSamples = (float*)output.Buffer.ToPointer();
            float* inSamples = input.Buffer == IntPtr.Zero ? null : (float*)input.Buffer.ToPointer();
            bool inputValid = inSamples != null;
            bool audible = false;
            bool unsafeState = false;
            float inputPeak = 0f;
            float outputPeak = 0f;
            float energy = _tailEnergy * 0.985f;
            _lastInputValid = inputValid ? 1 : 0;
            if (!inputValid)
                Interlocked.Increment(ref _silentInputCount);

            int lateRingFrames = Math.Max(1, _lateRingFrames);
            int earlyRingFrames = Math.Max(1, _earlyRingFrames);
            int echoRingFrames = Math.Max(1, _echoRingFrames);
            int lateWrite = ClampInt(_lateWriteFrame, 0, lateRingFrames - 1);
            int earlyWrite = ClampInt(_earlyWriteFrame, 0, earlyRingFrames - 1);
            int echoWrite = ClampInt(_echoWriteFrame, 0, echoRingFrames - 1);
            float lowPassA = _lowPassA;
            float earlyGain = _earlyGain;
            float lateGain = _lateGain;
            float echoGain = _echoGain;
            float echoFeedback = _echoFeedback;
            float diffusionMix = _diffusionMix;
            float lateInputGain = _lateInputGain;
            float ramp = CalculateStartupRamp();
            _lastRamp = ramp;
            earlyGain *= ramp;
            lateGain *= ramp;
            echoGain *= ramp;
            echoFeedback *= ramp;
            lateInputGain *= ramp;

            for (int frame = 0; frame < frames; frame++)
            {
                int frameBase = frame * channels;
                float mono = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    float source = inputValid ? inSamples[frameBase + ch] : 0f;
                    source = SanitizeAudioSample(source, ref unsafeState);
                    mono += source;
                    float absInput = Math.Abs(source);
                    if (absInput > inputPeak)
                        inputPeak = absInput;
                }
                mono /= channels;

                float earlyL;
                float earlyR;
                ReadEarlyReflections(earlyWrite, earlyRingFrames, earlyGain, out earlyL, out earlyR);

                float lateL;
                float lateR;
                ProcessLateField(mono, lateWrite, lateRingFrames, lowPassA, diffusionMix, lateInputGain, lateGain, out lateL, out lateR);

                _earlyLine[earlyWrite] = mono;

                int echoRead = echoWrite - _echoDelayFrames;
                if (echoRead < 0)
                    echoRead += echoRingFrames;
                int echoWriteBase = echoWrite * channels;
                int echoReadBase = echoRead * channels;

                for (int ch = 0; ch < channels; ch++)
                {
                    float source = inputValid ? inSamples[frameBase + ch] : 0f;
                    source = SanitizeAudioSample(source, ref unsafeState);
                    float delayedEcho = SanitizeDelaySample(_echoLine[echoReadBase + ch], ref unsafeState);
                    int dampIndex = ch > 0 ? 1 : 0;
                    _echoDamp[dampIndex] = ClampInternal(delayedEcho * (1f - lowPassA) + _echoDamp[dampIndex] * lowPassA, ref unsafeState);
                    float echo = _echoDamp[dampIndex] * echoGain;
                    _echoLine[echoWriteBase + ch] = ClampInternal(source * 0.48f + _echoDamp[dampIndex] * echoFeedback, ref unsafeState);

                    float wet = channels == 1
                        ? (earlyL + earlyR + lateL + lateR) * 0.5f + echo
                        : (ch == 0 ? earlyL + lateL : earlyR + lateR) + echo;
                    float mixed = _wetOnly ? wet : _blockDryGain * source + _blockWetMix * wet;
                    mixed = ClampOutput(SoftLimit(mixed));
                    outSamples[frameBase + ch] = mixed;

                    float absOutput = Math.Abs(mixed);
                    if (absOutput > outputPeak)
                        outputPeak = absOutput;
                    if (absOutput + Math.Abs(wet) * 0.25f > 0.00002f)
                        audible = true;
                    if (absOutput + Math.Abs(wet) > energy)
                        energy = absOutput + Math.Abs(wet);
                }

                lateWrite++;
                if (lateWrite >= lateRingFrames)
                    lateWrite = 0;
                earlyWrite++;
                if (earlyWrite >= earlyRingFrames)
                    earlyWrite = 0;
                echoWrite++;
                if (echoWrite >= echoRingFrames)
                    echoWrite = 0;
            }

            _lateWriteFrame = lateWrite;
            _earlyWriteFrame = earlyWrite;
            _echoWriteFrame = echoWrite;
            Interlocked.Add(ref _processedFramesSinceReset, frames);
            if (unsafeState || !IsFinite(energy) || energy > UnsafeSampleLimit)
            {
                Interlocked.Increment(ref _panicClearCount);
                ClearBuffers();
                energy = 0f;
                if (unsafeState)
                    Interlocked.Increment(ref _unsafeSampleCount);
            }
            _tailEnergy = Clamp(energy, 0f, InternalSampleLimit);
            _lastInputPeak = inputPeak;
            _lastOutputPeak = outputPeak;
            outputProcessParameters[0].ValidFrameCount = frames;
            outputProcessParameters[0].BufferFlags = audible || energy > 0.00002f ? BufferFlags.Valid : BufferFlags.Silent;
            _lastOutputFlags = (int)outputProcessParameters[0].BufferFlags;
        }

        public int CalcInputFrames(int outputFrameCount)
        {
            return outputFrameCount;
        }

        public int CalcOutputFrames(int inputFrameCount)
        {
            return inputFrameCount;
        }

        private unsafe void CopyDryOrSilence(BufferParameters input, ref BufferParameters output, int samples, bool wetOnly)
        {
            bool copiedDry = false;
            if (!wetOnly && output.Buffer != IntPtr.Zero && input.Buffer != IntPtr.Zero && samples > 0)
            {
                float* dryOutSamples = (float*)output.Buffer.ToPointer();
                float* dryInSamples = (float*)input.Buffer.ToPointer();
                for (int i = 0; i < samples; i++)
                    dryOutSamples[i] = dryInSamples[i];
                copiedDry = true;
            }

            output.BufferFlags = copiedDry ? input.BufferFlags : BufferFlags.Silent;
            output.ValidFrameCount = output.ValidFrameCount > 0 ? output.ValidFrameCount : input.ValidFrameCount;
            _lastOutputFlags = (int)output.BufferFlags;
            _lastInputValid = copiedDry ? 1 : 0;
            _lastInputPeak = 0f;
            _lastOutputPeak = 0f;
        }

        private unsafe void CopyUnsupportedFormat(BufferParameters input, ref BufferParameters output, int frames)
        {
            bool copiedDry = false;
            int bytes = Math.Max(0, frames * Math.Max(1, _channels) * Math.Max(1, _bytesPerSample));
            if (! _wetOnly && output.Buffer != IntPtr.Zero && input.Buffer != IntPtr.Zero && bytes > 0)
            {
                if (output.Buffer != input.Buffer)
                    System.Buffer.MemoryCopy(input.Buffer.ToPointer(), output.Buffer.ToPointer(), bytes, bytes);
                copiedDry = true;
            }

            output.BufferFlags = copiedDry ? input.BufferFlags : BufferFlags.Silent;
            output.ValidFrameCount = frames > 0 ? frames : input.ValidFrameCount;
            _lastOutputFlags = (int)output.BufferFlags;
            _lastInputValid = copiedDry ? 1 : 0;
            _lastInputPeak = 0f;
            _lastOutputPeak = 0f;
            _lastRamp = 0f;
        }

        private void ReadEarlyReflections(int writeFrame, int ringFrames, float gain, out float left, out float right)
        {
            left = 0f;
            right = 0f;
            for (int i = 0; i < _earlyTapFrames.Length; i++)
            {
                int read = writeFrame - _earlyTapFrames[i];
                while (read < 0)
                    read += ringFrames;
                bool unsafeState = false;
                float tap = SanitizeDelaySample(_earlyLine[read], ref unsafeState) * gain * (1f - i * 0.105f);
                if (unsafeState)
                    Interlocked.Increment(ref _unsafeSampleCount);
                if ((i & 1) == 0)
                    left += tap;
                else
                    right += tap;
            }
        }

        private void ProcessLateField(float input, int writeFrame, int ringFrames, float lowPassA, float diffusionMix, float inputGain, float lateGain, out float left, out float right)
        {
            for (int i = 0; i < DelayLineCount; i++)
            {
                int read = writeFrame - _lateDelayFrames[i];
                while (read < 0)
                    read += ringFrames;
                bool unsafeState = false;
                float delayed = SanitizeDelaySample(_lateLines[i][read], ref unsafeState);
                _lateDamp[i] = ClampInternal(delayed * (1f - lowPassA) + _lateDamp[i] * lowPassA, ref unsafeState);
                _lateRead[i] = _lateDamp[i];
                if (unsafeState)
                    Interlocked.Increment(ref _unsafeSampleCount);
            }

            Hadamard8(_lateRead, _lateMixed);
            for (int i = 0; i < DelayLineCount; i++)
            {
                float spreadInput = input * inputGain * (i % 2 == 0 ? 0.85f : -0.73f) * (1f - i * 0.035f);
                float diffuse = _lateMixed[i] * diffusionMix + _lateRead[i] * (1f - diffusionMix);
                bool unsafeState = false;
                _lateLines[i][writeFrame] = ClampInternal(spreadInput + diffuse * _lateFeedback[i], ref unsafeState);
                if (unsafeState)
                    Interlocked.Increment(ref _unsafeSampleCount);
            }

            left = (_lateRead[0] - _lateRead[1] + _lateRead[2] * 0.82f - _lateRead[3] * 0.71f + _lateRead[4] * 0.64f - _lateRead[5] * 0.55f + _lateRead[6] * 0.48f - _lateRead[7] * 0.41f) * lateGain;
            right = (_lateRead[7] - _lateRead[6] + _lateRead[5] * 0.82f - _lateRead[4] * 0.71f + _lateRead[3] * 0.64f - _lateRead[2] * 0.55f + _lateRead[1] * 0.48f - _lateRead[0] * 0.41f) * lateGain;
        }

        private void Configure(int channels, int sampleRate)
        {
            channels = Math.Max(1, channels);
            sampleRate = Math.Max(8000, sampleRate);
            int lateRingFrames = Math.Max(512, (int)Math.Ceiling(sampleRate * 0.36f));
            int earlyRingFrames = Math.Max(512, (int)Math.Ceiling(sampleRate * 0.42f));
            int echoRingFrames = Math.Max(512, (int)Math.Ceiling(sampleRate * 0.75f));
            bool formatChanged = _lateLines == null
                || _lateRingFrames != lateRingFrames
                || _earlyRingFrames != earlyRingFrames
                || _echoRingFrames != echoRingFrames
                || _channels != channels;

            if (formatChanged)
            {
                _lateLines = new float[DelayLineCount][];
                for (int i = 0; i < _lateLines.Length; i++)
                    _lateLines[i] = new float[lateRingFrames];
                _earlyLine = new float[earlyRingFrames];
                _echoLine = new float[echoRingFrames * channels];
                _lateWriteFrame = 0;
                _earlyWriteFrame = 0;
                _echoWriteFrame = 0;
                _tailEnergy = 0f;
                Interlocked.Exchange(ref _processedFramesSinceReset, 0);
                Array.Clear(_lateDamp, 0, _lateDamp.Length);
                Array.Clear(_echoDamp, 0, _echoDamp.Length);
            }

            _channels = channels;
            _sampleRate = sampleRate;
            _lateRingFrames = lateRingFrames;
            _earlyRingFrames = earlyRingFrames;
            _echoRingFrames = echoRingFrames;
        }

        private void ClearBuffers()
        {
            if (_lateLines != null)
            {
                for (int i = 0; i < _lateLines.Length; i++)
                    if (_lateLines[i] != null)
                        Array.Clear(_lateLines[i], 0, _lateLines[i].Length);
            }
            if (_earlyLine != null)
                Array.Clear(_earlyLine, 0, _earlyLine.Length);
            if (_echoLine != null)
                Array.Clear(_echoLine, 0, _echoLine.Length);
            Array.Clear(_lateDamp, 0, _lateDamp.Length);
            Array.Clear(_echoDamp, 0, _echoDamp.Length);
            _lateWriteFrame = 0;
            _earlyWriteFrame = 0;
            _echoWriteFrame = 0;
            _tailEnergy = 0f;
            Interlocked.Exchange(ref _processedFramesSinceReset, 0);
        }

        private bool HasBuffers()
        {
            return _lateLines != null
                && _lateLines.Length == DelayLineCount
                && _earlyLine != null
                && _echoLine != null
                && _lateRingFrames > 0
                && _earlyRingFrames > 0
                && _echoRingFrames > 0;
        }

        private void BuildLateDelayFrames(float roomSize, float density, float decaySeconds, int sampleRate)
        {
            float[] baseMs = { 17.9f, 23.7f, 29.3f, 31.7f, 37.1f, 41.9f, 43.7f, 47.9f };
            float scale = 0.85f + roomSize * 3.35f;
            float densityScale = 0.82f + density * 0.25f;
            for (int i = 0; i < DelayLineCount; i++)
            {
                int delay = Math.Max(37, (int)Math.Round(baseMs[i] * scale * densityScale * sampleRate / 1000f) | 1);
                delay = ClampInt(delay, 1, Math.Max(1, _lateRingFrames - 1));
                _lateDelayFrames[i] = delay;
                _lateFeedback[i] = Clamp((float)Math.Pow(10.0, -3.0 * (delay / (double)sampleRate) / Math.Max(0.35f, decaySeconds)), 0.08f, 0.985f);
            }
        }

        private void BuildEarlyTapFrames(float roomSize, float predelayMs, int sampleRate)
        {
            float scale = 0.7f + roomSize * 2.4f;
            float[] taps = { 8f, 13f, 21f, 34f, 55f, 89f };
            for (int i = 0; i < EarlyTapCount; i++)
            {
                int delay = (int)Math.Round((Clamp(predelayMs, 0f, 220f) + taps[i] * scale) * sampleRate / 1000f);
                _earlyTapFrames[i] = ClampInt(delay, 1, Math.Max(1, _earlyRingFrames - 1));
            }
        }

        private static void Hadamard8(float[] input, float[] output)
        {
            float a0 = input[0] + input[1];
            float a1 = input[0] - input[1];
            float a2 = input[2] + input[3];
            float a3 = input[2] - input[3];
            float a4 = input[4] + input[5];
            float a5 = input[4] - input[5];
            float a6 = input[6] + input[7];
            float a7 = input[6] - input[7];
            float b0 = a0 + a2;
            float b1 = a1 + a3;
            float b2 = a0 - a2;
            float b3 = a1 - a3;
            float b4 = a4 + a6;
            float b5 = a5 + a7;
            float b6 = a4 - a6;
            float b7 = a5 - a7;
            const float norm = 0.3535533906f;
            output[0] = (b0 + b4) * norm;
            output[1] = (b1 + b5) * norm;
            output[2] = (b2 + b6) * norm;
            output[3] = (b3 + b7) * norm;
            output[4] = (b0 - b4) * norm;
            output[5] = (b1 - b5) * norm;
            output[6] = (b2 - b6) * norm;
            output[7] = (b3 - b7) * norm;
        }

        private static float DbToLinear(float db)
        {
            if (db <= -60f)
                return 0f;
            return (float)Math.Pow(10.0, db / 20.0);
        }

        private static float SoftLimit(float value)
        {
            if (!IsFinite(value))
                return 0f;
            float abs = Math.Abs(value);
            const float threshold = 1.05f;
            if (abs <= threshold)
                return value;

            float sign = value < 0f ? -1f : 1f;
            float over = abs - threshold;
            return sign * (threshold + over / (1f + over * 2.5f));
        }

        private float CalculateStartupRamp()
        {
            int sampleRate = Math.Max(8000, _sampleRate);
            long processed = Interlocked.Read(ref _processedFramesSinceReset);
            float rampFrames = Math.Max(1f, sampleRate * StartupRampSeconds);
            return Clamp01(processed / rampFrames);
        }

        private static float SanitizeAudioSample(float value, ref bool unsafeState)
        {
            if (!IsFinite(value))
            {
                unsafeState = true;
                return 0f;
            }

            if (Math.Abs(value) > UnsafeSampleLimit)
            {
                unsafeState = true;
                return 0f;
            }

            return Clamp(value, -InternalSampleLimit, InternalSampleLimit);
        }

        private static float SanitizeDelaySample(float value, ref bool unsafeState)
        {
            if (!IsFinite(value))
            {
                unsafeState = true;
                return 0f;
            }

            if (Math.Abs(value) > UnsafeSampleLimit)
            {
                unsafeState = true;
                return 0f;
            }

            return Clamp(value, -InternalSampleLimit, InternalSampleLimit);
        }

        private static float ClampInternal(float value, ref bool unsafeState)
        {
            if (!IsFinite(value))
            {
                unsafeState = true;
                return 0f;
            }

            if (Math.Abs(value) > UnsafeSampleLimit)
                unsafeState = true;
            return Clamp(value, -InternalSampleLimit, InternalSampleLimit);
        }

        private static float ClampOutput(float value)
        {
            if (!IsFinite(value))
                return 0f;
            return Clamp(value, -OutputSampleLimit, OutputSampleLimit);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFloatFormat(WaveFormat format)
        {
            if (format == null)
                return true;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                return true;
            return format.BitsPerSample == 32 && format.Encoding != WaveFormatEncoding.Pcm;
        }

        private static string DescribeLock(LockParameters[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return "none";

            LockParameters first = parameters[0];
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/max{1}",
                DescribeFormat(first.Format),
                first.MaxFrameCount);
        }

        private static string DescribeFormat(WaveFormat format)
        {
            if (format == null)
                return "fmt=null";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}Hz/{1}ch/{2}bit/{3}",
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                format.Encoding);
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value <= min)
                return min;
            return value >= max ? max : value;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value <= min)
                return min;
            return value >= max ? max : value;
        }
    }
}
