using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    // Repositions a blocked block-sound emitter toward its weight-blended target (between the real block and the
    // doorway portal, computed in the aux telemetry) so it localises to where the sound actually arrives.
    // Direction comes from the target; the matching distance attenuation is carried by the aux gain
    // (VolumeMultiplier), so the emitter sits at a real point (Option B), not a fictitious far one.
    //
    // The position is TEMPORALLY SMOOTHED: Current eases toward the latest target by one EMA
    // (PlayerFilterBlockRepositionSlewMs) so the emitter glides between target positions instead of snapping in
    // grid cells. It is NOT a slide out from the block - the first placement snaps to the target, then only
    // subsequent target MOVEMENT is smoothed.
    //
    // Crucially the target is DEBOUNCED: a target is held for a window after the last active request, so a one-
    // frame drop in the air path (the cause of the old slide-to-source-and-pop churn) no longer releases the
    // emitter. Only a sustained loss eases it home and then hands it back with SetPosition(null). Disjoint from
    // thruster emitters; static-base sources for now.
    internal static class V2BlockEmitterReposition
    {
        private sealed class State
        {
            public Vector3D RealSource;     // live block position (home / restore reference)
            public Vector3D Target;         // latest blended target requested while active
            public Vector3D Current;        // smoothed world position actually written
            public bool Placed;             // Current has been seeded (snap-on-first)
            public DateTime LastTargetUtc;  // last active (valid-target) request
            public DateTime LastRequestUtc; // last time the voice was seen at all
        }

        private const double FrameMs = 1000.0 / 60.0;
        private const double ReleaseEpsilonSq = 0.05 * 0.05;            // 5 cm "eased home" threshold
        private static readonly TimeSpan TargetHold = TimeSpan.FromMilliseconds(400); // debounce window
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(800);  // voice gone
        private static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(250);

        private static readonly Dictionary<MyEntity3DSoundEmitter, State> Tracked =
            new Dictionary<MyEntity3DSoundEmitter, State>();
        private static readonly List<MyEntity3DSoundEmitter> Scratch = new List<MyEntity3DSoundEmitter>(16);

        private static int _activeCount;
        private static long _appliedFrames;
        private static long _released;

        public static int ActiveCount => _activeCount;

        // Called from the aux apply path whenever a block source is (re)evaluated. realSource is the live block
        // position; active+target register the blended reposition point.
        public static void Request(MyEntity3DSoundEmitter emitter, Vector3D realSource, Vector3D target, bool active, DateTime now)
        {
            if (emitter == null)
                return;

            if (!Tracked.TryGetValue(emitter, out State state))
            {
                if (!active)
                    return; // nothing to track for an untracked, inactive source
                state = new State();
                Tracked[emitter] = state;
            }

            state.RealSource = realSource;
            state.LastRequestUtc = now;
            if (active)
            {
                state.Target = target;
                state.LastTargetUtc = now;
            }
        }

        // Per-frame: ease each tracked emitter toward its held target (or home when the target has lapsed) and
        // release any that have eased home, gone stale, or stopped playing.
        public static void Update()
        {
            if (Tracked.Count == 0)
            {
                _activeCount = 0;
                return;
            }

            DateTime now = DateTime.UtcNow;
            float slewMs = Math.Max(1f, SettingsManager.Current?.PlayerFilterBlockRepositionSlewMs ?? 120f);
            float alpha = Clamp01(1f - (float)Math.Exp(-FrameMs / slewMs));

            Scratch.Clear();
            int active = 0;

            foreach (KeyValuePair<MyEntity3DSoundEmitter, State> pair in Tracked)
            {
                MyEntity3DSoundEmitter emitter = pair.Key;
                State state = pair.Value;

                if (emitter == null || !IsLive(emitter) || now - state.LastRequestUtc > StaleAfter)
                {
                    Scratch.Add(emitter);
                    continue;
                }

                bool hasTarget = now - state.LastTargetUtc <= TargetHold; // debounced: bridges brief dropouts
                Vector3D goal = hasTarget ? state.Target : state.RealSource;

                if (!state.Placed)
                {
                    state.Current = goal; // snap to the first placement (no slide out from the block)
                    state.Placed = true;
                }
                else
                {
                    state.Current = Vector3D.Lerp(state.Current, goal, alpha); // temporal smoothing
                }

                if (hasTarget)
                    active++;
                else if (now - state.LastTargetUtc > TargetHold + ReleaseGrace
                    && Vector3D.DistanceSquared(state.Current, state.RealSource) < ReleaseEpsilonSq)
                {
                    Scratch.Add(emitter); // eased home and target long gone -> release
                    continue;
                }

                try
                {
                    emitter.SetPosition(state.Current);
                    emitter.SetVelocity(Vector3.Zero);
                    _appliedFrames++;
                }
                catch
                {
                    Scratch.Add(emitter);
                }
            }

            for (int i = 0; i < Scratch.Count; i++)
            {
                MyEntity3DSoundEmitter emitter = Scratch[i];
                if (emitter == null)
                    continue;
                if (Tracked.Remove(emitter))
                {
                    RestoreSafe(emitter);
                    _released++;
                }
            }

            _activeCount = active;
        }

        // Debug: draw the portal symbol at the ACTUAL smoothed emitter position (where you hear it), so the
        // temporal smoothing is what the overlay shows - not the raw quantised target.
        private static readonly Color PortalSymbolColor = new Color(255, 110, 230, 240);

        public static void DrawActive()
        {
            if (Tracked.Count == 0)
                return;

            foreach (KeyValuePair<MyEntity3DSoundEmitter, State> pair in Tracked)
            {
                State s = pair.Value;
                if (!s.Placed)
                    continue;
                MyRenderProxy.DebugDrawSphere(s.Current, 0.20f, PortalSymbolColor, 0.95f, false, false, false, false);
            }
        }

        public static void Release(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return;
            if (Tracked.Remove(emitter))
            {
                RestoreSafe(emitter);
                _released++;
            }
        }

        public static void Reset()
        {
            Scratch.Clear();
            foreach (KeyValuePair<MyEntity3DSoundEmitter, State> pair in Tracked)
                Scratch.Add(pair.Key);
            for (int i = 0; i < Scratch.Count; i++)
                RestoreSafe(Scratch[i]);

            Tracked.Clear();
            Scratch.Clear();
            _activeCount = 0;
            _appliedFrames = 0;
            _released = 0;
        }

        public static string FormatSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "active={0} tracked={1} frames={2} released={3}",
                _activeCount,
                Tracked.Count,
                _appliedFrames,
                _released);
        }

        private static void RestoreSafe(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return;
            try
            {
                emitter.SetPosition(null);
                emitter.SetVelocity(null);
            }
            catch
            {
            }
        }

        private static bool IsLive(MyEntity3DSoundEmitter emitter)
        {
            try
            {
                return emitter.IsPlaying;
            }
            catch
            {
                return false;
            }
        }

        private static float Clamp01(float v)
        {
            if (v <= 0f)
                return 0f;
            return v >= 1f ? 1f : v;
        }
    }
}
