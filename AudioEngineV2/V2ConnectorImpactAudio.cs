using System;
using System.Collections.Generic;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2ConnectorImpactAudio
    {
        private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ImpactLifetime = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan ReplayGuard = TimeSpan.FromMilliseconds(750);
        private static readonly string[] LockCues = { "ImpMetalMetalCat5DistA", "ImpMetalMetalCat4DistA" };
        private static readonly HashSet<IMyEntity> EntityScratch = new HashSet<IMyEntity>();
        private static readonly HashSet<long> SeenThisScan = new HashSet<long>();
        private static readonly Dictionary<long, Sandbox.ModAPI.Ingame.MyShipConnectorStatus> LastStatuses = new Dictionary<long, Sandbox.ModAPI.Ingame.MyShipConnectorStatus>();
        private static readonly Dictionary<long, DateTime> LastPlayedUtc = new Dictionary<long, DateTime>();
        private static readonly List<ActiveImpact> ActiveImpacts = new List<ActiveImpact>();
        private static DateTime _lastScanUtc = DateTime.MinValue;

        public static void Update()
        {
            UpdateActiveImpacts();

            DateTime now = DateTime.UtcNow;
            if (now - _lastScanUtc < ScanInterval)
                return;

            _lastScanUtc = now;
            ScanConnectors(now);
        }

        public static void ResetRuntimeState()
        {
            for (int i = 0; i < ActiveImpacts.Count; i++)
                StopImpact(ActiveImpacts[i]);

            ActiveImpacts.Clear();
            LastStatuses.Clear();
            LastPlayedUtc.Clear();
            EntityScratch.Clear();
            SeenThisScan.Clear();
            _lastScanUtc = DateTime.MinValue;
        }

        private static void ScanConnectors(DateTime now)
        {
            if (MyAPIGateway.Entities == null)
                return;

            EntityScratch.Clear();
            SeenThisScan.Clear();
            MyAPIGateway.Entities.GetEntities(EntityScratch, IsConnectorEntity);

            foreach (IMyEntity entity in EntityScratch)
            {
                if (entity == null)
                    continue;

                long id = entity.EntityId;
                if (id == 0L)
                    id = entity.GetHashCode();

                SeenThisScan.Add(id);
                Sandbox.ModAPI.Ingame.IMyShipConnector connector = entity as Sandbox.ModAPI.Ingame.IMyShipConnector;
                if (connector == null)
                    continue;

                Sandbox.ModAPI.Ingame.MyShipConnectorStatus status = connector.Status;
                if (!LastStatuses.TryGetValue(id, out Sandbox.ModAPI.Ingame.MyShipConnectorStatus previous))
                {
                    LastStatuses[id] = status;
                    continue;
                }

                LastStatuses[id] = status;
                if (previous == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected
                    || status != Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected)
                {
                    continue;
                }

                if (!ShouldEmitForConnector(entity, connector))
                    continue;

                if (LastPlayedUtc.TryGetValue(id, out DateTime lastPlayed) && now - lastPlayed < ReplayGuard)
                    continue;

                LastPlayedUtc[id] = now;
                PlayLockImpact(entity, id, now);
            }

            RemoveStaleStatusEntries();
        }

        private static bool IsConnectorEntity(IMyEntity entity)
        {
            return entity is Sandbox.ModAPI.Ingame.IMyShipConnector;
        }

        private static bool ShouldEmitForConnector(IMyEntity entity, Sandbox.ModAPI.Ingame.IMyShipConnector connector)
        {
            MyCubeBlock block = entity as MyCubeBlock;
            if (block?.CubeGrid == null || !IsLargeMobileGrid(block.CubeGrid))
                return false;

            IMyShipConnector modConnector = connector as IMyShipConnector;
            IMyEntity otherEntity = modConnector?.OtherConnector as IMyEntity;
            MyCubeBlock otherBlock = otherEntity as MyCubeBlock;
            if (otherBlock?.CubeGrid != null && IsLargeMobileGrid(otherBlock.CubeGrid) && entity.EntityId > otherEntity.EntityId)
                return false;

            return true;
        }

        private static bool IsLargeMobileGrid(MyCubeGrid grid)
        {
            if (grid == null || grid.Physics == null)
                return false;

            try
            {
                if (grid.IsStatic)
                    return false;
            }
            catch
            {
                return false;
            }

            return !string.Equals(grid.GridSizeEnum.ToString(), "Small", StringComparison.OrdinalIgnoreCase);
        }

        private static void PlayLockImpact(IMyEntity entity, long connectorId, DateTime now)
        {
            MyEntity anchor = entity as MyEntity;
            if (anchor == null)
                return;

            Vector3D position = entity.GetPosition();
            if (position == Vector3D.Zero)
                position = anchor.WorldMatrix.Translation;

            if (position == Vector3D.Zero)
                return;

            MyEntity3DSoundEmitter emitter = new MyEntity3DSoundEmitter(anchor, false);
            emitter.Force2D = false;
            emitter.Force3D = true;
            emitter.SetPosition(position);
            AudioEngineV2Runtime.RegisterEmitter(emitter, V2FilterRoute.Hull, "v2-connector-lock");
            AudioEngineV2Runtime.SetEmitterPosition(emitter, position);

            float volume = CalculateImpactVolume(emitter);
            if (volume <= 0.001f)
            {
                AudioEngineV2Runtime.UnregisterEmitter(emitter);
                return;
            }

            emitter.VolumeMultiplier = volume;
            string cueName = SelectCue(connectorId, now);
            MySoundPair pair = new MySoundPair(cueName, false);
            MyEntity3DSoundEmitter.PreloadSound(pair);
            bool started = emitter.PlaySound(pair, true, false, false, false, false, true, false);
            if (!started)
            {
                AudioEngineV2Runtime.UnregisterEmitter(emitter);
                return;
            }

            emitter.VolumeMultiplier = volume;
            emitter.Update();
            emitter.FastUpdate(false);
            ActiveImpacts.Add(new ActiveImpact(anchor, emitter, cueName, now));
            AudioDiagnostics.RecordEmitter(emitter, "v2-connector-lock/hull", volume, 0f, volume, volume, position);
            AudioDiagnostics.RecordCueName(cueName, "v2-connector-lock/hull", volume, 0f, volume, volume, position);
            V2DebugLog.WriteEvent("connector-impact", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "connector={0} cue={1} volume={2:0.00} pos={3:0.0},{4:0.0},{5:0.0}",
                connectorId,
                cueName,
                volume,
                position.X,
                position.Y,
                position.Z));
        }

        private static string SelectCue(long connectorId, DateTime now)
        {
            int index = (int)Math.Abs((connectorId + now.Ticks) % LockCues.Length);
            return LockCues[index];
        }

        private static float CalculateImpactVolume(MyEntity3DSoundEmitter emitter)
        {
            if (V2EngineFilterModel.TryCalculateHullOnly(emitter, SettingsManager.Current, out V2EngineFilterSample sample))
                return Clamp(sample.DistanceGain * 1.2f, 0f, 4f);

            Vector3D position = AudioEngineV2Runtime.TryGetEmitterPosition(emitter, out Vector3D registered)
                ? registered
                : emitter.SourcePosition;
            return Clamp(V2EngineFilterModel.CalculateHullDistanceGain(position, SettingsManager.Current) * 1.2f, 0f, 4f);
        }

        private static void UpdateActiveImpacts()
        {
            if (ActiveImpacts.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            for (int i = ActiveImpacts.Count - 1; i >= 0; i--)
            {
                ActiveImpact impact = ActiveImpacts[i];
                if (now - impact.StartedUtc > ImpactLifetime)
                {
                    StopImpact(impact);
                    ActiveImpacts.RemoveAt(i);
                    continue;
                }

                try
                {
                    Vector3D position = impact.Anchor.WorldMatrix.Translation;
                    impact.Emitter.SetPosition(position);
                    AudioEngineV2Runtime.SetEmitterPosition(impact.Emitter, position);
                    impact.Emitter.VolumeMultiplier = CalculateImpactVolume(impact.Emitter);
                    impact.Emitter.Update();
                    impact.Emitter.FastUpdate(false);
                }
                catch
                {
                    StopImpact(impact);
                    ActiveImpacts.RemoveAt(i);
                }
            }
        }

        private static void StopImpact(ActiveImpact impact)
        {
            try
            {
                impact.Emitter.VolumeMultiplier = 0f;
                impact.Emitter.StopSound(false, false, false);
            }
            catch
            {
            }

            AudioEngineV2Runtime.UnregisterEmitter(impact.Emitter);
        }

        private static void RemoveStaleStatusEntries()
        {
            List<long> stale = null;
            foreach (long id in LastStatuses.Keys)
            {
                if (SeenThisScan.Contains(id))
                    continue;

                if (stale == null)
                    stale = new List<long>();
                stale.Add(id);
            }

            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
            {
                LastStatuses.Remove(stale[i]);
                LastPlayedUtc.Remove(stale[i]);
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value <= min)
                return min;

            return value >= max ? max : value;
        }

        private sealed class ActiveImpact
        {
            public readonly MyEntity Anchor;
            public readonly MyEntity3DSoundEmitter Emitter;
            public readonly string CueName;
            public readonly DateTime StartedUtc;

            public ActiveImpact(MyEntity anchor, MyEntity3DSoundEmitter emitter, string cueName, DateTime startedUtc)
            {
                Anchor = anchor;
                Emitter = emitter;
                CueName = cueName;
                StartedUtc = startedUtc;
            }
        }
    }
}
