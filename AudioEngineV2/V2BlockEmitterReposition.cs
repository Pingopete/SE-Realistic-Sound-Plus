using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    // Repositions a blocked block-sound emitter to the PORTAL (the doorway its sound diffracts through) so it
    // localises to the opening instead of straight through the wall. Direction comes from the portal; the
    // air-path distance attenuation is carried separately by the aux gain (VolumeMultiplier), so the emitter
    // sits at the real doorway (Option B from the design discussion), not a fictitious far point.
    //
    // Movement is smoothed in TWO stages so it never snaps:
    //   1. Portal AVERAGE - the raw portal jumps in whole grid cells (2.5 m on a large grid); an EMA turns those
    //      quantised jumps into a continuous averaged target (PlayerFilterBlockRepositionPortalAvgMs).
    //   2. Glide - the emitter eases toward that averaged target each frame (PlayerFilterBlockRepositionSlewMs).
    // The emitter is tracked for the whole life of the voice, NOT just while a portal is active: when the air
    // path briefly drops out the target eases back toward the real block instead of hard-releasing, so a one-
    // frame flicker no longer snaps the source between the doorway and the block. SetPosition(null) hands the
    // emitter back only once it has eased home and stayed inactive past a grace window (or the voice stops).
    // Disjoint from thruster emitters; static-base sources for now.
    internal static class V2BlockEmitterReposition
    {
        private sealed class State
        {
            public Vector3D RealSource;   // live block position (slide-home target / restore reference)
            public Vector3D RawPortal;    // last raw (grid-quantised) portal target
            public Vector3D AvgPortal;    // EMA-averaged portal target
            public bool HasAvg;
            public Vector3D Current;      // slewed world position actually written
            public bool Active;           // a portal was requested this refresh
            public DateTime LastActiveUtc;
            public DateTime LastRequestUtc;
        }

        private const double FrameMs = 1000.0 / 60.0;
        private const double ReleaseEpsilonSq = 0.05 * 0.05;          // 5 cm: "eased home" threshold
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan InactiveGrace = TimeSpan.FromMilliseconds(350);

        private static readonly Dictionary<MyEntity3DSoundEmitter, State> Tracked =
            new Dictionary<MyEntity3DSoundEmitter, State>();
        private static readonly List<MyEntity3DSoundEmitter> Scratch = new List<MyEntity3DSoundEmitter>(16);

        private static int _activeCount;
        private static long _appliedFrames;
        private static long _released;

        public static int ActiveCount => _activeCount;

        // Called from the aux apply path whenever a block source is (re)evaluated. realSource is the live block
        // position (slide-home / restore reference); active+portalWorld register the doorway target.
        public static void Request(MyEntity3DSoundEmitter emitter, Vector3D realSource, Vector3D portalWorld, bool active, DateTime now)
        {
            if (emitter == null)
                return;

            if (!Tracked.TryGetValue(emitter, out State state))
            {
                if (!active)
                    return; // nothing to do for an untracked, inactive source

                state = new State { Current = realSource };
                Tracked[emitter] = state;
            }

            state.RealSource = realSource;
            state.LastRequestUtc = now;
            state.Active = active;
            if (active)
            {
                state.RawPortal = portalWorld;
                state.LastActiveUtc = now;
            }
        }

        // Per-frame: average the portal, ease the emitter toward it (or back home when inactive), and release
        // any emitter that has eased home and stayed inactive, gone stale, or stopped playing.
        public static void Update()
        {
            if (Tracked.Count == 0)
            {
                _activeCount = 0;
                return;
            }

            DateTime now = DateTime.UtcNow;
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float portalAvgMs = Math.Max(1f, settings?.PlayerFilterBlockRepositionPortalAvgMs ?? 200f);
            float slewMs = Math.Max(1f, settings?.PlayerFilterBlockRepositionSlewMs ?? 120f);
            float portalAlpha = Clamp01(1f - (float)Math.Exp(-FrameMs / portalAvgMs));
            float slewAlpha = Clamp01(1f - (float)Math.Exp(-FrameMs / slewMs));

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

                Vector3D target;
                if (state.Active)
                {
                    // Stage 1: average the quantised portal jumps into a continuous target.
                    if (!state.HasAvg)
                    {
                        state.AvgPortal = state.RawPortal;
                        state.HasAvg = true;
                    }
                    else
                    {
                        state.AvgPortal = Vector3D.Lerp(state.AvgPortal, state.RawPortal, portalAlpha);
                    }
                    target = state.AvgPortal;
                    active++;
                }
                else
                {
                    // Eased back toward the real block; drop the average so the next activation re-seeds it.
                    target = state.RealSource;
                    state.HasAvg = false;

                    if (now - state.LastActiveUtc > InactiveGrace
                        && Vector3D.DistanceSquared(state.Current, state.RealSource) < ReleaseEpsilonSq)
                    {
                        Scratch.Add(emitter);
                        continue;
                    }
                }

                // Stage 2: ease the written position toward the target.
                state.Current = Vector3D.Lerp(state.Current, target, slewAlpha);

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

        // Debug: draw where the emitter is ACTUALLY placed (the glided Current position) plus the averaged
        // target it is easing toward, so the smoothing is visible rather than just the raw quantised portal.
        // Magenta sphere = applied position; faint blue sphere = averaged target; line = the glide chasing it.
        private static readonly Color AppliedActiveColor = new Color(255, 110, 230, 240);
        private static readonly Color AppliedInactiveColor = new Color(150, 150, 160, 160);
        private static readonly Color AveragedTargetColor = new Color(90, 180, 255, 160);

        public static void DrawActive()
        {
            if (Tracked.Count == 0)
                return;

            foreach (KeyValuePair<MyEntity3DSoundEmitter, State> pair in Tracked)
            {
                State s = pair.Value;
                Color applied = s.Active ? AppliedActiveColor : AppliedInactiveColor;
                MyRenderProxy.DebugDrawSphere(s.Current, 0.16f, applied, 1f, false, false, false, false);

                if (s.Active && s.HasAvg)
                {
                    MyRenderProxy.DebugDrawSphere(s.AvgPortal, 0.10f, AveragedTargetColor, 0.8f, false, false, false, false);
                    MyRenderProxy.DebugDrawLine3D(s.AvgPortal, s.Current, AveragedTargetColor, applied, false, false);
                }
            }
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
