using System;
using System.Reflection;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2AudioListenerState
    {
        private const double SeatInteriorCameraRange = 12.0;
        private const double StableListenerHoldRange = 10.0;
        private static readonly TimeSpan StableListenerHold = TimeSpan.FromMilliseconds(800);
        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static V2AudioListenerState _lastReliableState;
        private static DateTime _lastReliableUtc = DateTime.MinValue;

        public Vector3D Position;
        // The listener position expressed in its OWN grid's local frame (grid-relative). On a moving/flying grid the
        // world Position changes every frame even while seated still; GridLocalPosition stays constant, so movement
        // detection (env stability, air-path recompute) can ask "did I move RELATIVE to the ship?" instead of
        // "did my world coordinate change?" - which is what stops the constant re-probe/flood storm on moving grids.
        // Equals Position when the listener is not on a grid (on foot in the world).
        public Vector3D GridLocalPosition;
        public float Atmosphere;
        public bool InsideShip;
        public bool SeatedInShip;
        public bool VanillaFallback;
        public string RoomName;
        public long GridEntityId;
        public string ModeName;
        public Vector3 MoveInput;
        public bool HasMoveInput;
        public long ContactGridEntityId;
        public string ContactSource;
        public string CharacterMovementState;

        public static void ResetStability()
        {
            _lastReliableState = default(V2AudioListenerState);
            _lastReliableUtc = DateTime.MinValue;
        }

        public static V2AudioListenerState Capture()
        {
            Vector3D position = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            bool controlledShip = TryGetControlledShip(out long controlledGridId, out Vector3D controlledPosition, out Vector3 moveInput, out bool hasMoveInput);
            bool characterGridContact = TryGetCharacterGridContact(out long contactGridId, out string contactSource, out string characterMovementState);
            bool firstPersonCamera = IsFirstPersonCamera();
            bool seatedInteriorCamera = controlledShip && firstPersonCamera && IsCameraNearControlledShip(position, controlledPosition);
            bool vanillaInside = false;
            string roomName = "room=?";
            long gridEntityId = 0L;

            if (VanillaShipEnvironment.TryGetLatest(out VanillaShipEnvironment.Snapshot vanilla))
            {
                vanillaInside = vanilla.InsideShip;
                roomName = vanilla.RoomName;
                gridEntityId = vanilla.GridEntityId;
            }

            if (controlledGridId != 0L)
                gridEntityId = controlledGridId;
            else if (contactGridId != 0L)
                gridEntityId = contactGridId;

            if (characterGridContact && !controlledShip && vanillaInside && !HasUsableVanillaRoom(roomName))
                vanillaInside = false;

            if (controlledShip && !firstPersonCamera)
                vanillaInside = false;

            bool insideShip = vanillaInside || seatedInteriorCamera;
            bool routeActive = insideShip || controlledShip || characterGridContact;
            string modeName = insideShip
                ? (seatedInteriorCamera ? "inside-seat" : "inside-room")
                : (controlledShip ? "outside-seat-camera" : (characterGridContact ? "outside-grid-contact-" + contactSource : "vanilla-fallback"));

            Vector3D gridLocalPosition = ResolveGridLocal(gridEntityId, position);

            V2AudioListenerState raw = new V2AudioListenerState
            {
                Position = position,
                GridLocalPosition = gridLocalPosition,
                Atmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(position),
                InsideShip = insideShip,
                SeatedInShip = controlledShip,
                VanillaFallback = !routeActive,
                RoomName = roomName,
                GridEntityId = gridEntityId,
                ModeName = modeName,
                MoveInput = moveInput,
                HasMoveInput = hasMoveInput,
                ContactGridEntityId = contactGridId,
                ContactSource = contactSource,
                CharacterMovementState = characterMovementState
            };

            return Stabilize(raw);
        }

        // Transform a world position into the given grid's local frame. A point fixed to the grid (e.g. a seated
        // listener, or a block) has constant local coordinates regardless of how fast the grid flies/rotates, so a
        // grid-local comparison answers "moved relative to the ship?" rather than "world coordinate changed?".
        private static Vector3D ResolveGridLocal(long gridEntityId, Vector3D world)
        {
            if (gridEntityId == 0L)
                return world;

            try
            {
                return ToGridLocal(MyAPIGateway.Entities?.GetEntityById(gridEntityId), world);
            }
            catch
            {
                return world;
            }
        }

        // Public so the block-occlusion probe can re-use the same grid-local transform against a source grid it has
        // already resolved (a source may be on a different grid than the listener). Returns world unchanged if null.
        public static Vector3D ToGridLocal(IMyEntity grid, Vector3D world)
        {
            if (grid == null)
                return world;

            try
            {
                return Vector3D.Transform(world, grid.WorldMatrixNormalizedInv);
            }
            catch
            {
                return world;
            }
        }

        private static V2AudioListenerState Stabilize(V2AudioListenerState raw)
        {
            DateTime now = DateTime.UtcNow;
            if (IsReliableListenerState(raw))
            {
                _lastReliableState = raw;
                _lastReliableUtc = now;
                return raw;
            }

            if (!CanReuseLastReliableState(raw, now))
                return raw;

            V2AudioListenerState stable = raw;
            stable.InsideShip = _lastReliableState.InsideShip;
            stable.SeatedInShip = _lastReliableState.SeatedInShip || raw.SeatedInShip;
            stable.VanillaFallback = false;
            stable.RoomName = _lastReliableState.RoomName;
            stable.GridEntityId = _lastReliableState.GridEntityId;
            stable.ContactGridEntityId = raw.ContactGridEntityId != 0L ? raw.ContactGridEntityId : _lastReliableState.ContactGridEntityId;
            stable.ContactSource = string.IsNullOrWhiteSpace(raw.ContactSource) ? _lastReliableState.ContactSource : raw.ContactSource;
            stable.ModeName = string.IsNullOrWhiteSpace(_lastReliableState.ModeName)
                ? "grid-hold"
                : _lastReliableState.ModeName + "-hold";
            return stable;
        }

        private static bool IsReliableListenerState(V2AudioListenerState state)
        {
            if (state.VanillaFallback || state.GridEntityId == 0L)
                return false;

            return state.InsideShip
                || state.SeatedInShip
                || !string.IsNullOrWhiteSpace(state.ContactSource);
        }

        private static bool CanReuseLastReliableState(V2AudioListenerState raw, DateTime now)
        {
            if (_lastReliableUtc == DateTime.MinValue)
                return false;

            if (now - _lastReliableUtc > StableListenerHold)
                return false;

            if (_lastReliableState.GridEntityId == 0L)
                return false;

            if (!raw.VanillaFallback && raw.GridEntityId != 0L)
                return false;

            if (raw.Position == Vector3D.Zero || _lastReliableState.Position == Vector3D.Zero)
                return true;

            return Vector3D.DistanceSquared(raw.Position, _lastReliableState.Position) <= StableListenerHoldRange * StableListenerHoldRange;
        }

        private static bool HasUsableVanillaRoom(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return false;

            return roomName.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) < 0
                && roomName.IndexOf("room=?", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool TryGetCharacterGridContact(out long gridEntityId, out string source, out string movementState)
        {
            gridEntityId = 0L;
            source = null;
            movementState = null;

            object controlled = MyAPIGateway.Session?.ControlledObject;
            if (controlled == null)
                return false;

            object entity = TryReadMember(controlled, "Entity") ?? controlled;
            movementState = ReadCharacterMovementState(entity);
            if (!IsPhysicalCharacterContactStateName(movementState))
                return false;

            object topGrid = TryReadMember(entity, "m_topGrid");
            if (TryResolveGridEntityId(topGrid, out gridEntityId))
            {
                source = "topgrid";
                return true;
            }

            object relative = TryReadMember(entity, "RelativeDampeningEntity") ?? TryReadMember(entity, "m_relativeDampeningEntity");
            if (TryResolveGridEntityId(relative, out gridEntityId))
            {
                source = "relative";
                return true;
            }

            return false;
        }

        private static string ReadCharacterMovementState(object entity)
        {
            object state = TryReadMember(entity, "CurrentMovementState") ?? TryInvokeParameterless(entity, "GetWalkingState");
            return state?.ToString();
        }

        private static bool IsPhysicalCharacterContactStateName(string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName))
                return false;

            switch (stateName.ToLowerInvariant())
            {
                case "flying":
                case "falling":
                case "jump":
                case "died":
                    return false;
                default:
                    return true;
            }
        }

        private static bool TryResolveGridEntityId(object candidate, out long gridEntityId)
        {
            gridEntityId = 0L;
            if (candidate == null)
                return false;

            if (candidate is MyCubeGrid grid)
            {
                gridEntityId = grid.EntityId;
                return gridEntityId != 0L;
            }

            object cubeGrid = TryReadMember(candidate, "CubeGrid");
            gridEntityId = TryReadEntityId(cubeGrid);
            if (gridEntityId != 0L)
                return true;

            object topMostParent = TryInvokeParameterless(candidate, "GetTopMostParent");
            if (topMostParent != null && !ReferenceEquals(topMostParent, candidate))
                return TryResolveGridEntityId(topMostParent, out gridEntityId);

            object parent = TryReadMember(candidate, "Parent");
            if (parent != null && !ReferenceEquals(parent, candidate))
                return TryResolveGridEntityId(parent, out gridEntityId);

            return false;
        }

        private static bool IsCameraNearControlledShip(Vector3D cameraPosition, Vector3D controlledPosition)
        {
            if (cameraPosition == Vector3D.Zero || controlledPosition == Vector3D.Zero)
                return true;

            return Vector3D.DistanceSquared(cameraPosition, controlledPosition) <= SeatInteriorCameraRange * SeatInteriorCameraRange;
        }

        private static bool IsFirstPersonCamera()
        {
            try
            {
                return MyAPIGateway.Session?.CameraController?.IsInFirstPersonView ?? true;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryGetControlledShip(out long gridEntityId, out Vector3D controlledPosition, out Vector3 moveInput, out bool hasMoveInput)
        {
            gridEntityId = 0L;
            controlledPosition = Vector3D.Zero;
            moveInput = Vector3.Zero;
            hasMoveInput = false;

            object controlled = MyAPIGateway.Session?.ControlledObject;
            if (controlled == null)
                return false;

            if (controlled is IMyShipController controller)
            {
                hasMoveInput = TryReadMoveInput(controller, out moveInput);
                gridEntityId = TryReadEntityId(controller.CubeGrid);
                controlledPosition = TryReadPosition(controller);
                return gridEntityId != 0L;
            }

            object grid = TryReadMember(controlled, "CubeGrid")
                ?? TryReadMember(TryReadMember(controlled, "Entity"), "CubeGrid");
            gridEntityId = TryReadEntityId(grid);
            controlledPosition = TryReadPosition(controlled);
            return gridEntityId != 0L;
        }

        private static bool TryReadVector3(object instance, string name, out Vector3 value)
        {
            value = Vector3.Zero;
            object raw = TryReadMember(instance, name);
            if (raw == null)
                return false;

            if (raw is Vector3 vector)
            {
                value = vector;
                return true;
            }

            if (raw is Vector3D vectorD)
            {
                value = new Vector3((float)vectorD.X, (float)vectorD.Y, (float)vectorD.Z);
                return true;
            }

            return false;
        }

        private static bool TryReadMoveInput(object controlled, out Vector3 moveInput)
        {
            moveInput = Vector3.Zero;

            try
            {
                if (controlled is Sandbox.ModAPI.Ingame.IMyShipController controller)
                {
                    moveInput = controller.MoveIndicator;
                    return true;
                }
            }
            catch
            {
            }

            return TryReadVector3(controlled, "MoveIndicator", out moveInput);
        }

        private static object TryReadMember(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(name, InstanceMembers);
                if (property != null)
                    return property.GetValue(instance, null);

                FieldInfo field = instance.GetType().GetField(name, InstanceMembers);
                return field?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokeParameterless(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                MethodInfo method = instance.GetType().GetMethod(name, InstanceMembers, null, Type.EmptyTypes, null);
                return method?.Invoke(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private static long TryReadEntityId(object instance)
        {
            if (instance == null)
                return 0L;

            try
            {
                object value = TryReadMember(instance, "EntityId");
                if (value == null)
                    return 0L;

                return Convert.ToInt64(value);
            }
            catch
            {
                return 0L;
            }
        }

        private static Vector3D TryReadPosition(object instance)
        {
            if (instance == null)
                return Vector3D.Zero;

            try
            {
                MethodInfo method = instance.GetType().GetMethod("GetPosition", InstanceMembers, null, Type.EmptyTypes, null);
                object result = method?.Invoke(instance, null);
                if (result is Vector3D directPosition)
                    return directPosition;
            }
            catch
            {
            }

            try
            {
                object worldMatrix = TryReadMember(instance, "WorldMatrix");
                if (worldMatrix is MatrixD matrix)
                    return matrix.Translation;
            }
            catch
            {
            }

            object entity = TryReadMember(instance, "Entity");
            if (entity != null && !ReferenceEquals(entity, instance))
                return TryReadPosition(entity);

            return Vector3D.Zero;
        }
    }
}
