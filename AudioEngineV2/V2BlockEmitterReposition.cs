using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    // Repositions a blocked block-sound emitter to the PORTAL (the doorway its sound diffracts through) so it
    // localises to the opening instead of straight through the wall. Direction comes from the portal; the
    // air-path distance attenuation is carried separately by the aux gain (VolumeMultiplier), so the emitter
    // sits at the real doorway rather than a fictitious far point (Option B from the design discussion).
    //
    // Mechanism: per-frame SetPosition re-assert (the proven path RSP already uses for thruster/connector
    // emitters), slewed and zero-velocity for stability, with SetPosition(null) handing the emitter back to its
    // own entity the moment a source is no longer repositioned or its loop stops. Default OFF, fully separate
    // from thruster emitters (disjoint sets). MVP targets sustained sources on static grids; a source whose
    // entity moves every frame is corrected by the engine on release.
    internal static class V2BlockEmitterReposition
    {
        private sealed class State
        {
            public Vector3D Target;   // portal world position (direction anchor)
            public Vector3D Current;  // slewed world position actually written
            public DateTime LastRequestUtc;
            public bool Started;
        }

        private static readonly Dictionary<MyEntity3DSoundEmitter, State> Tracked =
            new Dictionary<MyEntity3DSoundEmitter, State>();
        private static readonly List<MyEntity3DSoundEmitter> Scratch = new List<MyEntity3DSoundEmitter>(16);
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(400);

        private static int _activeCount;
        private static long _appliedFrames;
        private static long _released;

        public static int ActiveCount => _activeCount;

        // Called from the aux apply path whenever a block source is (re)evaluated. When active + the portal is
        // valid it registers/refreshes the target; otherwise it releases any standing override on that emitter.
        public static void Request(MyEntity3DSoundEmitter emitter, Vector3D portalWorld, bool active, DateTime now)
        {
            if (emitter == null)
                return;

            if (!active)
            {
                Release(emitter);
                return;
            }

            if (!Tracked.TryGetValue(emitter, out State state))
            {
                state = new State { Target = portalWorld, Current = portalWorld, Started = false };
                Tracked[emitter] = state;
            }

            state.Target = portalWorld;
            state.LastRequestUtc = now;
        }

        public static void Release(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return;

            if (Tracked.TryGetValue(emitter, out State _))
            {
                RestoreSafe(emitter);
                Tracked.Remove(emitter);
                _released++;
            }
        }

        // Per-frame: re-assert the slewed portal position on every live tracked emitter, and release any that
        // went stale (no longer requested) or stopped playing.
        public static void Update()
        {
            if (Tracked.Count == 0)
            {
                _activeCount = 0;
                return;
            }

            DateTime now = DateTime.UtcNow;
            float slewMs = Math.Max(1f, SettingsManager.Current?.PlayerFilterBlockRepositionSlewMs ?? 120f);
            float alpha = Clamp01(1f - (float)Math.Exp(-16.6 / slewMs)); // ~one 60fps step toward target

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

                if (!state.Started)
                {
                    state.Current = state.Target; // snap on first frame: no audible swoop across the room
                    state.Started = true;
                }
                else
                {
                    state.Current = Vector3D.Lerp(state.Current, state.Target, alpha);
                }

                try
                {
                    emitter.SetPosition(state.Current);
                    emitter.SetVelocity(Vector3.Zero);
                    active++;
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

        // Hand the emitter back to its own entity (null override) so it resumes tracking its real block.
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
