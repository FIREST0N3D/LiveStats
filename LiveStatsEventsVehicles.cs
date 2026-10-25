// Requires: LiveStatsEvents
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("LiveStatsEventsVehicles", "FiREST0N3D", "1.0.40")]
    [Description("Option A vehicle module for LiveStatsEvents: player-vehicle/crate despawn + tug/sub/mini/scrap event types. Registers with LiveStatsEvents core. Requires LiveStatsEvents.")]
    class LiveStatsEventsVehicles : RustPlugin
    {
        [PluginReference] private Plugin LiveStatsEvents;
        [PluginReference] private Plugin LiveStatsWorld;

        private ConfigData config;
        private Timer _despawnTimer;
        private bool _registered;

        private const string AdminPermission = "livestatseventsvehicles.admin";
        private const string ModuleName = "vehicles";

        // Vehicle tracking — netIds only (never hold BaseEntity references long-term)
        private readonly Dictionary<ulong, float> _vehicleLastOccupied = new Dictionary<ulong, float>(64);
        private readonly Dictionary<ulong, float> _vehicleFirstSeen = new Dictionary<ulong, float>(64);
        private readonly HashSet<ulong> _occupiedVehicleIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _playerOwnedIds = new HashSet<ulong>();
        // Kill buffer stores netId + label + pos — no BaseEntity pinning (Oxide memory attribution)
        private readonly List<(ulong id, string label, Vector3 pos)> _despawnKillBuffer = new List<(ulong, string, Vector3)>(16);
        private readonly HashSet<ulong> _despawnAliveBuffer = new HashSet<ulong>();
        private readonly HashSet<ulong> _pendingDespawnIds = new HashSet<ulong>();
        private readonly List<ulong> _pruneIdBuffer = new List<ulong>(64);
        private readonly List<ulong> _habPurgeIds = new List<ulong>(16);
        private const int MaxTrackedVehicles = 64;
        private int _despawnPassCounter;

        // Event-spawned vehicles (module or Core inline) — never idle-despawn until expiry
        private readonly Dictionary<ulong, float> _eventProtectedUntil = new Dictionary<ulong, float>(32);
        private const float DefaultEventProtectMinutes = 55f;

        // Per-pass cargo positions collected DURING the single entity walk (no second scan)
        private readonly List<Vector3> _passCargoPositions = new List<Vector3>(8);
        // Deep-sea monuments are fixed for the map — build once
        private readonly List<Vector3> _mapDeepSeaMonuments = new List<Vector3>(16);
        private bool _mapMonumentsBuilt;
        private float _passHalfMap = 0f;
        private readonly Dictionary<string, (int count, float time)> _prefabCountCache = new Dictionary<string, (int, float)>(8);

        void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
            Puts("LiveStatsEventsVehicles v1.0.40 — core owns spawn chat; player-owned hulls");
        }

        void OnServerInitialized(bool initial)
        {
            // Clear vanilla HAB population so idle-despawn actually controls count
            try
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "hotairballoon.population 0");
                Puts("[Vehicles] Vanilla hotairballoon.population forced to 0 (module owns HAB lifecycle)");
            }
            catch { }

            BuildMapMonumentCache();
            // One HAB purge after entities settle (was two full-world walks)
            timer.Once(8f, () => PurgeHotAirBalloons("init+8s"));
            // Wait for sleepers + reconnects before sweeping leftover event hulls.
            timer.Once(45f, SweepLeftoverEventVehicles);
            timer.Once(120f, SweepLeftoverEventVehicles);

            TryRegisterWithCore();
            StartDespawnTimer();
        }

        /// <summary>Monuments never move — cache once for crate legitimacy (no per-pass entity walk).</summary>
        private void BuildMapMonumentCache()
        {
            if (_mapMonumentsBuilt) return;
            _mapDeepSeaMonuments.Clear();
            try { _passHalfMap = TerrainMeta.Size.x * 0.5f * 0.95f; }
            catch { _passHalfMap = ConVar.Server.worldsize * 0.5f * 0.95f; }

            try
            {
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments != null)
                {
                    for (int i = 0; i < monuments.Count; i++)
                    {
                        var m = monuments[i];
                        if (m == null || m.transform == null) continue;
                        string name = (m.name ?? "").ToLowerInvariant();
                        try
                        {
                            if (m.displayPhrase != null && !string.IsNullOrEmpty(m.displayPhrase.english))
                                name += " " + m.displayPhrase.english.ToLowerInvariant();
                        }
                        catch { }
                        bool deepSea =
                            name.Contains("oilrig") || name.Contains("oil_rig") || name.Contains("oil rig") ||
                            name.Contains("underwater") || name.Contains("under_water") ||
                            name.Contains("sealab") || name.Contains("sea_lab") || name.Contains("sea lab") ||
                            name.Contains("waterlab") || name.Contains("water_lab") ||
                            name.Contains("offshore") || name.Contains("underwaterlab");
                        if (deepSea)
                            _mapDeepSeaMonuments.Add(m.transform.position);
                    }
                }
            }
            catch { }
            _mapMonumentsBuilt = true;
            Puts($"[Vehicles] Monument cache: {_mapDeepSeaMonuments.Count} deep-sea sites, halfMap={_passHalfMap:F0}");
        }

        private void PurgeHotAirBalloons(string reason)
        {
            // Collect netIds only — never hold BaseEntity across the walk
            _habPurgeIds.Clear();
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed || ent.net == null) continue;
                    string p = ent.ShortPrefabName ?? "";
                    if (p.IndexOf("hotairballoon", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _habPurgeIds.Add(ent.net.ID.Value);
                }
            }
            catch { }

            int killed = 0;
            for (int i = 0; i < _habPurgeIds.Count; i++)
            {
                try
                {
                    var be = FindEntityByNetId(_habPurgeIds[i]);
                    if (be == null || be.IsDestroyed) continue;
                    if (IsPlayerVehicleOccupied(be)) continue;
                    if (!IsLootCrateEntity(be)) be.Kill();
                    killed++;
                }
                catch { }
            }
            _habPurgeIds.Clear();

            if (killed > 0)
                Puts($"[Vehicles] Purged {killed} hot air balloon(s) ({reason})");
        }

        private static BaseEntity FindEntityByNetId(ulong netId)
        {
            if (netId == 0) return null;
            try
            {
                var bn = BaseNetworkable.serverEntities.Find(new NetworkableId(netId));
                return bn as BaseEntity;
            }
            catch { return null; }
        }

        private static bool IsLootCrateEntity(BaseEntity ent)
        {
            if (ent == null) return false;
            if (ent is HackableLockedCrate) return true;
            string s = ent.ShortPrefabName ?? "";
            return s.IndexOf("crate", System.StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("hackable", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }


        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "LiveStatsEvents")
            {
                LiveStatsEvents = plugin;
                TryRegisterWithCore();
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "LiveStatsEvents")
            {
                _registered = false;
                LiveStatsEvents = null;
            }
        }

        void Unload()
        {
            if (_registered && LiveStatsEvents != null)
            {
                try { LiveStatsEvents.Call("API_UnregisterModule", ModuleName); } catch { }
                _registered = false;
            }
            _despawnTimer?.Destroy();
            _despawnTimer = null;
            _vehicleLastOccupied.Clear();
            _vehicleFirstSeen.Clear();
            _eventProtectedUntil.Clear();
            _pendingDespawnIds.Clear();
            _passCargoPositions.Clear();
            _despawnKillBuffer.Clear();
            _despawnAliveBuffer.Clear();
            _habPurgeIds.Clear();
        }

        private void TryRegisterWithCore()
        {
            if (LiveStatsEvents == null)
                LiveStatsEvents = plugins.Find("LiveStatsEvents");
            if (LiveStatsEvents == null)
            {
                PrintWarning("LiveStatsEvents core not loaded yet — vehicle module will register when core appears.");
                return;
            }
            try
            {
                LiveStatsEvents.Call("API_RegisterModule", ModuleName);
                _registered = true;
                Puts("[Vehicles] Registered module 'vehicles' with LiveStatsEvents core.");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to register with core: {ex.Message}");
            }
        }

        private static bool IsSweepVehiclePrefab(string sn)
        {
            return IsPrimaryWaterVehicle(sn);
        }

        /// <summary>
        /// Tug/sub are multi-entity. Only the root hull counts as one vehicle.
        /// Seats, modules, and child pieces must not increment Max=1 or get idle-killed.
        /// </summary>
        private static bool IsPrimaryWaterVehicle(string sn)
        {
            if (string.IsNullOrEmpty(sn)) return false;
            if (sn.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (sn.IndexOf("module", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (sn.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (sn.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0)
                return sn.Equals("tugboat", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("tugboat.entity", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("tugboat.prefab", StringComparison.OrdinalIgnoreCase);
            if (sn.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0)
                return sn.IndexOf("submarinesolo", StringComparison.OrdinalIgnoreCase) >= 0
                    || sn.IndexOf("submarineduo", StringComparison.OrdinalIgnoreCase) >= 0
                    || sn.Equals("submarine.entity", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("submarine", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static bool IsWaterVehiclePart(string sn)
        {
            if (string.IsNullOrEmpty(sn)) return false;
            return sn.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Tug/sub leftovers keep CanSpawn at the cap (Max=1) for hours. On load, remove
        /// unoccupied ones with no player nearby so the next scheduled event can fire.
        /// </summary>
        private void SweepLeftoverEventVehicles()
        {
            _eventProtectedUntil.Clear();
            var ids = new List<ulong>(8);
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed || ent.net == null) continue;
                    string sn = ent.ShortPrefabName ?? "";
                    if (!IsSweepVehiclePrefab(sn)) continue;
                    var be = ent as BaseEntity;
                    if (be == null) continue;
                    try { if (be.GetParentEntity() != null) continue; } catch { }
                    if (!IsPrimaryWaterVehicle(sn)) continue;
                    Vector3 pos;
                    try { pos = be.transform.position; } catch { continue; }
                    bool voidHull = pos.y < -20f;
                    if (!voidHull && (IsPlayerOwned(be) || PlayerOnOrNear(be, 200f)))
                    {
                        if (IsPlayerOwned(be)) continue;
                        ProtectOccupiedHull(be, 55f);
                        continue;
                    }
                    ids.Add(ent.net.ID.Value);
                }
            }
            catch (Exception ex) { Puts("[Vehicles] leftover scan: " + ex.Message); }

            int n = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];
                timer.Once(i * 0.15f, () =>
                {
                    try
                    {
                        var be = FindEntityByNetId(id);
                        if (be == null || be.IsDestroyed) return;
                        if (IsPlayerVehicleOccupied(be)) return;
                        if (!IsLootCrateEntity(be)) be.Kill();
                    }
                    catch { }
                });
                n++;
            }
            if (n > 0)
            {
                InvalidatePrefabCountCache();
                Puts($"[Vehicles] load sweep queued {n} leftover tug/sub (unoccupied)");
            }
            else
                Puts("[Vehicles] load sweep: no leftover tug/sub");
        }

        private void StartDespawnTimer()
        {
            _despawnTimer?.Destroy();
            if (config?.Despawn == null || !config.Despawn.Enabled)
            {
                Puts("[Vehicles] Player vehicle despawn disabled in config.");
                return;
            }
            // Floor at 300s — full-world walks are expensive; Oxide attributes touch-cost to this plugin
            float tick = Mathf.Clamp(config.Despawn.CheckIntervalSeconds, 300f, 900f);
            _despawnTimer = timer.Every(tick, ProcessVehicleDespawn);
            Puts($"[Vehicles] Despawn timer every {tick:F0}s (idle {config.Despawn.IdleMinutesBeforeDespawn:F0}m, crates={config.Despawn.DespawnHackableCrates})");
        }

        // ---------- Core may Call these ----------
        /// <summary>Core SpawnEvent delegates vehicle types here when module is preferred.</summary>
        /// <summary>
        /// Core invokes via reflection / Call. Always returns int 1 (ok) or 0 (fail) — never null/bool.
        /// </summary>
        public object API_Spawn(string type)
        {
            Puts($"[Vehicles] API_Spawn ENTER type='{type}'");
            try
            {
                if (string.IsNullOrEmpty(type)) return 0;
                type = type.ToLowerInvariant().Trim();
                bool ok;
                switch (type)
                {
                    case "tugboat":
                        ok = SpawnTugboat();
                        break;
                    case "submarine":
                    case "sub":
                        ok = SpawnSubmarine();
                        break;
                    case "scrapheli":
                    case "scraptransport":
                    case "scraptransporthelicopter":
                        ok = SpawnScrapHeli();
                        break;
                    case "minicopter":
                    case "mini":
                        ok = SpawnMinicopter();
                        break;
                    case "hotairballoon":
                    case "balloon":
                        DebugLog("HotAirBalloon spawn disabled — despawn-only");
                        ok = false;
                        break;
                    default:
                        Puts($"[Vehicles] API_Spawn unknown type '{type}'");
                        ok = false;
                        break;
                }
                Puts($"[Vehicles] API_Spawn EXIT type='{type}' ok={ok} → returning int {(ok ? 1 : 0)}");
                return ok ? 1 : 0;
            }
            catch (Exception ex)
            {
                PrintError($"API_Spawn({type}): {ex.Message}");
                return 0;
            }
        }

        public object API_SpawnTugboat()
        {
            try { return SpawnTugboat() ? 1 : 0; } catch { return 0; }
        }
        public object API_SpawnSubmarine()
        {
            try { return SpawnSubmarine() ? 1 : 0; } catch { return 0; }
        }
        public object API_SpawnScrapHeli()
        {
            try { return SpawnScrapHeli() ? 1 : 0; } catch { return 0; }
        }
        public object API_SpawnMinicopter()
        {
            try { return SpawnMinicopter() ? 1 : 0; } catch { return 0; }
        }

        /// <summary>
        /// Oxide CallHook path from core: OnLiveStatsEventsModuleSpawn("vehicles", type).
        /// Mirrors LiveStatsEventsNPC — required because some Oxide builds return null from
        /// Plugin.Call on companion plugins even when the public API_Spawn method exists.
        /// </summary>
        private object OnLiveStatsEventsModuleSpawn(string moduleName, string type)
        {
            if (!string.Equals(moduleName, ModuleName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(moduleName, "vehicles", StringComparison.OrdinalIgnoreCase))
                return null; // not for us
            Puts($"[Vehicles] OnLiveStatsEventsModuleSpawn type='{type}'");
            return API_Spawn(type);
        }

        /// <summary>Core (or this module) marks an event-spawned vehicle so idle despawn skips it.</summary>
        public object API_ProtectEventVehicle(ulong netId, float minutes = 0f)
        {
            if (netId == 0) return 0;
            if (minutes <= 0f)
                minutes = config?.Despawn?.EventProtectMinutes > 0f
                    ? Mathf.Min(config.Despawn.EventProtectMinutes, 55f)
                    : DefaultEventProtectMinutes;
            float until = Time.realtimeSinceStartup + minutes * 60f;
            _eventProtectedUntil[netId] = until;
            // Ensure tracking exists so prune logic stays coherent
            float now = Time.realtimeSinceStartup;
            if (!_vehicleFirstSeen.ContainsKey(netId) && _vehicleFirstSeen.Count < MaxTrackedVehicles)
            {
                _vehicleFirstSeen[netId] = now;
                _vehicleLastOccupied[netId] = now;
            }
            else
            {
                _vehicleLastOccupied[netId] = now;
            }
            Puts($"[Vehicles] Event vehicle protected net={netId} for {minutes:F0}m");
            return 1;
        }

        public object API_IsEventProtectedVehicle(ulong netId)
        {
            if (netId == 0) return false;
            PruneEventProtectedVehicles();
            return _eventProtectedUntil.ContainsKey(netId);
        }

        private void PruneEventProtection()
        {
            PruneEventProtectedVehicles();
        }

        private void PruneEventProtectedVehicles()
        {
            if (_eventProtectedUntil.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            _pruneIdBuffer.Clear();
            foreach (var kv in _eventProtectedUntil)
            {
                if (kv.Value <= now) _pruneIdBuffer.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
                _eventProtectedUntil.Remove(_pruneIdBuffer[i]);
        }

        private bool IsEventProtectedVehicle(ulong netId)
        {
            if (netId == 0) return false;
            if (_playerOwnedIds.Contains(netId)) return true;
            if (_eventProtectedUntil.TryGetValue(netId, out float until))
            {
                if (until > Time.realtimeSinceStartup) return true;
                _eventProtectedUntil.Remove(netId);
            }
            return false;
        }

        public object API_CanSpawn(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            type = type.ToLowerInvariant();
            int max;
            switch (type)
            {
                case "tugboat":
                    max = Mathf.Max(1, config?.Limits?.MaxTugboats ?? 1);
                    int tugEv = CountEventVehicles("tugboat");
                    Puts($"[Vehicles] CanSpawn tugboat event={tugEv}/{max} (player hulls ignored)");
                    return tugEv < max;
                case "submarine":
                case "sub":
                    max = Mathf.Max(1, config?.Limits?.MaxSubmarines ?? 1);
                    int subEv = CountEventVehicles("submarine");
                    Puts($"[Vehicles] CanSpawn submarine event={subEv}/{max} (player hulls ignored)");
                    return subEv < max;
                case "scrapheli":
                case "scraptransport":
                case "scraptransporthelicopter":
                    max = Mathf.Max(1, config?.Limits?.MaxScrapHelis ?? 1);
                    int scrapEv = CountEventVehicles("scrap");
                    if (scrapEv == 0) scrapEv = CountEventVehicles("scraptransport");
                    Puts($"[Vehicles] CanSpawn scrapheli event={scrapEv}/{max}");
                    return scrapEv < max;
                case "minicopter":
                case "mini":
                    max = Mathf.Max(1, config?.Limits?.MaxMinicopters ?? 1);
                    int miniEv = CountEventVehicles("minicopter");
                    Puts($"[Vehicles] CanSpawn minicopter event={miniEv}/{max}");
                    return miniEv < max;
                case "hotairballoon":
                case "balloon":
                    return false;
                default:
                    return false;
            }
        }

        // ---------- Spawns (self-contained) ----------
        private static readonly string[] TugboatPrefabs = {
            "assets/content/vehicles/boats/tugboat/tugboat.prefab",
            "assets/content/vehicles/boats/tugboat/tugboat.entity.prefab"
        };
        private static readonly string[] SubPrefabs = {
            "assets/content/vehicles/submarine/submarinesolo.entity.prefab",
            "assets/content/vehicles/submarine/submarineduo.entity.prefab"
        };

        private bool SpawnTugboat()
        {
            int before = CountEventVehicles("tugboat");
            int max = config?.Limits?.MaxTugboats ?? 1;
            Puts($"[Vehicles] Tugboat spawn check event={before}/{max}");
            if (before >= max)
            {
                Puts($"[Vehicles] Tugboat aborted — event limit reached ({before}/{max})");
                return false;
            }

            if (!TryGetCoastalWaterPosition(out Vector3 pos, forSubmarine: false))
            {
                Puts("[Vehicles] Tugboat aborted — no valid coastal water position (shore-walk + polar samples failed)");
                return false;
            }

            if (pos.y < -8f || pos.y > 12f)
            {
                Puts($"[Vehicles] Tugboat rejected void/height Y={pos.y:F1} at {Grid(pos)}");
                if (!TryGetCoastalWaterPosition(out pos, forSubmarine: false) || pos.y < -8f || pos.y > 12f)
                    return false;
            }
            BaseEntity ent = TryCreateVehicle(TugboatPrefabs, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (ent == null && TryGetCoastalWaterPosition(out Vector3 pos2, forSubmarine: false) && pos2.y >= -8f && pos2.y <= 12f)
            {
                pos = pos2;
                ent = TryCreateVehicle(TugboatPrefabs, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            }
            if (ent == null)
            {
                Puts($"[Vehicles] Tugboat aborted — CreateEntity failed at {Grid(pos)}");
                return false;
            }
            try
            {
                float y = ent.transform.position.y;
                if (y < -20f)
                {
                    Puts($"[Vehicles] Tugboat void-killed after spawn Y={y:F1} at {Grid(ent.transform.position)}");
                    if (!IsLootCrateEntity(ent)) ent.Kill();
                    return false;
                }
            }
            catch { }

            TrackEventVehicle(ent);
            Puts($"[Vehicles] Tugboat spawned at {Grid(pos)} ({pos.x:F0},{pos.y:F0},{pos.z:F0})");
            return true;
        }

        private bool SpawnSubmarine()
        {
            int before = CountEventVehicles("submarine");
            int max = config?.Limits?.MaxSubmarines ?? 1;
            Puts($"[Vehicles] Submarine spawn check event={before}/{max} world={CountPrefabs("submarine")}");
            if (before >= max)
            {
                Puts($"[Vehicles] Submarine aborted — event limit reached ({before}/{max})");
                return false;
            }

            if (!TryGetCoastalWaterPosition(out Vector3 pos, forSubmarine: true))
            {
                Puts("[Vehicles] Submarine aborted — no valid coastal water position");
                return false;
            }

            pos.y -= 1.5f;
            BaseEntity ent = TryCreateVehicle(SubPrefabs, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (ent == null)
            {
                Puts($"[Vehicles] Submarine aborted — CreateEntity failed at {Grid(pos)}");
                return false;
            }

            TrackEventVehicle(ent);
            Puts($"[Vehicles] Submarine spawned at {Grid(pos)} ({pos.x:F0},{pos.y:F0},{pos.z:F0})");
            return true;
        }

        private bool SpawnScrapHeli()
        {
            int before = CountEventVehicles("scraptransport");
            int max = config?.Limits?.MaxScrapHelis ?? 1;
            if (before >= max)
            {
                Puts($"[Vehicles] ScrapHeli aborted — limit reached ({before}/{max})");
                return false;
            }

            Vector3 pos = FindLandSpawn();
            if (pos == Vector3.zero)
            {
                Puts("[Vehicles] ScrapHeli aborted — no valid land position");
                return false;
            }
            try { pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 5f; } catch { pos.y += 5f; }
            var ent = GameManager.server.CreateEntity(
                "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab",
                pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (ent == null)
            {
                Puts("[Vehicles] ScrapHeli aborted — CreateEntity null");
                return false;
            }
            ent.Spawn();
            TrackEventVehicle(ent);
            Puts($"[Vehicles] ScrapHeli spawned at {Grid(pos)}");
            return true;
        }

        private bool SpawnMinicopter()
        {
            int before = CountEventVehicles("minicopter");
            int max = config?.Limits?.MaxMinicopters ?? 1;
            if (before >= max)
            {
                Puts($"[Vehicles] Minicopter aborted — limit reached ({before}/{max})");
                return false;
            }

            Vector3 pos = FindLandSpawn();
            if (pos == Vector3.zero)
            {
                Puts("[Vehicles] Minicopter aborted — no valid land position");
                return false;
            }
            try { pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 3f; } catch { pos.y += 3f; }
            var ent = GameManager.server.CreateEntity(
                "assets/content/vehicles/minicopter/minicopter.entity.prefab",
                pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (ent == null)
            {
                Puts("[Vehicles] Minicopter aborted — CreateEntity null");
                return false;
            }
            ent.Spawn();
            TrackEventVehicle(ent);
            Puts($"[Vehicles] Minicopter spawned at {Grid(pos)}");
            return true;
        }

        private BaseEntity TryCreateVehicle(string[] paths, Vector3 pos, Quaternion rot)
        {
            if (paths == null) return null;
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    var ent = GameManager.server.CreateEntity(paths[i], pos, rot);
                    if (ent == null) continue;
                    ent.Spawn();
                    return ent;
                }
                catch (Exception ex)
                {
                    DebugLog($"CreateEntity {paths[i]}: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Coastal water finder. Walks from dry land toward the ocean (outward),
        /// then uses WaterLevel depth + hull clearance instead of WaterMap-only.
        /// </summary>
        private bool TryGetCoastalWaterPosition(out Vector3 result, bool forSubmarine)
        {
            result = Vector3.zero;
            if (TerrainMeta.HeightMap == null) return false;

            float halfMap = 1500f;
            try { halfMap = TerrainMeta.Size.x * 0.48f; }
            catch { try { halfMap = ConVar.Server.worldsize * 0.48f; } catch { } }

            float minDepth = forSubmarine ? 1.6f : 1.8f;
            float maxDepth = forSubmarine ? 120f : 90f;

            if (TryOceanPatrolWater(minDepth, maxDepth, forSubmarine, out result))
                return true;
            if (TryVanillaOceanNode(forSubmarine, out result))
                return true;

            // A: dry land → walk seaward (the walk itself proves coast)
            for (int attempt = 0; attempt < 28; attempt++)
            {
                Vector3 land = new Vector3(
                    UnityEngine.Random.Range(-halfMap * 0.78f, halfMap * 0.78f),
                    0f,
                    UnityEngine.Random.Range(-halfMap * 0.78f, halfMap * 0.78f));
                try { if (TerrainMeta.HeightMap.GetHeight(land) < 0.5f) continue; } catch { continue; }
                if (WaterDepthAt(land) > 0.35f) continue;

                Vector3 outDir = new Vector3(land.x, 0f, land.z);
                if (outDir.sqrMagnitude < 4f)
                    outDir = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
                outDir.Normalize();

                if (WalkSeaward(land, outDir, minDepth, maxDepth, forSubmarine, out result))
                    return true;

                float baseAng = Mathf.Atan2(outDir.z, outDir.x);
                for (int k = -1; k <= 1; k++)
                {
                    if (k == 0) continue;
                    float a = baseAng + k * 0.55f;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    if (WalkSeaward(land, dir, minDepth, maxDepth, forSubmarine, out result))
                        return true;
                }
            }

            // B: polar ring
            for (int attempt = 0; attempt < 48; attempt++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float radius = UnityEngine.Random.Range(halfMap * 0.70f, halfMap * 0.93f);
                Vector3 candidate = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (TryValidateCoastalSample(candidate, minDepth, maxDepth, forSubmarine, requireCoast: false, out result))
                    return true;
            }

            // C: WaterMap-only last resort so the event still fires
            for (int attempt = 0; attempt < 36; attempt++)
            {
                Vector3 candidate = new Vector3(
                    UnityEngine.Random.Range(-halfMap * 0.92f, halfMap * 0.92f),
                    0f,
                    UnityEngine.Random.Range(-halfMap * 0.92f, halfMap * 0.92f));
                if (TryValidateCoastalSample(candidate, 1.6f, 90f, forSubmarine, requireCoast: false, out result))
                    return true;
            }

            Puts($"[Vehicles] coastal finder failed sub={forSubmarine} half={halfMap:F0}");
            return false;
        }



        /// <summary>Last-chance: Facepunch ocean-patrol ring + water surface. Ignores WaterMap holes.</summary>
        private bool TryVanillaOceanNode(bool forSubmarine, out Vector3 result)
        {
            result = Vector3.zero;
            List<Vector3> path = null;
            try { path = TerrainMeta.Path?.OceanPatrolClose; } catch { }
            if (path == null || path.Count < 8) return false;
            int n = path.Count;
            int start = UnityEngine.Random.Range(0, n);
            for (int i = 0; i < Mathf.Min(n, 30); i++)
            {
                Vector3 raw = path[(start + i * 7) % n];
                Vector3 inland = new Vector3(-raw.x, 0f, -raw.z);
                if (inland.sqrMagnitude < 4f) continue;
                inland.Normalize();
                Vector3 sample = raw + inland * 80f;
                float ground;
                try { ground = TerrainMeta.HeightMap.GetHeight(sample); } catch { continue; }
                float surface = 0.6f;
                try { surface = WaterLevel.GetWaterSurface(sample, false, false, null); } catch { surface = 0.6f; }
                if (surface < -20f || surface > 40f) surface = 0.6f;
                float depth = surface - ground;
                if (depth < (forSubmarine ? 3f : 1.6f)) continue;
                if (depth > 200f) continue;
                sample.y = forSubmarine ? surface - 1.1f : surface + 0.35f;
                result = sample;
                Puts("[Vehicles] vanilla ocean node " + Grid(result) + " y=" + result.y.ToString("F1") + " depth=" + depth.ToString("F1") + " sub=" + forSubmarine);
                return true;
            }
            return false;
        }

        private bool TryOceanPatrolWater(float minDepth, float maxDepth, bool forSubmarine, out Vector3 result)
        {
            result = Vector3.zero;
            List<Vector3> path = null;
            try { path = TerrainMeta.Path?.OceanPatrolClose; } catch { }
            if (path == null || path.Count < 8) return false;

            int n = path.Count;
            int start = UnityEngine.Random.Range(0, n);
            for (int i = 0; i < Mathf.Min(n, 40); i++)
            {
                Vector3 a = path[(start + i) % n];
                Vector3 b = path[(start + i + 1) % n];
                Vector3 along = b - a;
                along.y = 0f;
                if (along.sqrMagnitude < 4f) continue;
                along.Normalize();
                Vector3 perp = new Vector3(-along.z, 0f, along.x);
                // offset off the cargo lane toward open water and toward shore
                float[] offs = { 90f, -90f, 140f, -140f, 50f };
                for (int o = 0; o < offs.Length; o++)
                {
                    Vector3 candidate = a + perp * offs[o];
                    if (TryValidateCoastalSample(candidate, minDepth, maxDepth, forSubmarine, requireCoast: false, out result))
                    {
                        Puts($"[Vehicles] ocean-patrol water {Grid(result)} y={result.y:F1} sub={forSubmarine}");
                        return true;
                    }
                }
            }
            return false;
        }

        private bool WalkSeaward(
            Vector3 land, Vector3 dir, float minDepth, float maxDepth,
            bool forSubmarine, out Vector3 result)
        {
            result = Vector3.zero;
            for (float step = 24f; step <= 280f; step += 22f)
            {
                Vector3 candidate = land + dir * step;
                if (TryValidateCoastalSample(candidate, minDepth, maxDepth, forSubmarine, requireCoast: false, out result))
                    return true;
            }
            return false;
        }

        private static readonly string[] VehicleBlockNames =
        {
            "rock", "formation", "cliff", "iceberg", "ice_sheet", "ice_berg"
        };

        private static readonly Collider[] _vehicleOverlap = new Collider[12];

        private float WaterDepthAt(Vector3 p)
        {
            float mapDepth = 0f;
            try
            {
                float ground = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : p.y;
                float water = TerrainMeta.WaterMap != null ? TerrainMeta.WaterMap.GetHeight(p) : ground;
                mapDepth = water - ground;
            }
            catch { }
            try
            {
                float overall = WaterLevel.GetOverallWaterDepth(p, true, true, null);
                if (overall > mapDepth) mapDepth = overall;
            }
            catch { }
            return mapDepth;
        }

        private bool TryValidateCoastalSample(
            Vector3 candidate, float minDepth, float maxDepth,
            bool forSubmarine, bool requireCoast, out Vector3 result)
        {
            result = Vector3.zero;
            try
            {
                float half = 1800f;
                try { half = TerrainMeta.Size.x * 0.48f; } catch { }
                if (Mathf.Abs(candidate.x) > half || Mathf.Abs(candidate.z) > half)
                    return false;

                float ground = TerrainMeta.HeightMap.GetHeight(candidate);
                float water = 0.6f;
                try { water = WaterLevel.GetWaterSurface(candidate, false, false, null); } catch { water = 0.6f; }
                if (water < -20f || water > 40f)
                {
                    try
                    {
                        float wm = TerrainMeta.WaterMap != null ? TerrainMeta.WaterMap.GetHeight(candidate) : 0.6f;
                        if (wm > -20f && wm < 40f) water = wm;
                        else water = 0.6f;
                    }
                    catch { water = 0.6f; }
                }
                float depth = water - ground;
                try
                {
                    float overall = WaterLevel.GetOverallWaterDepth(candidate, false, true, null);
                    if (overall > 0.5f && overall < 200f && overall > depth) depth = overall;
                }
                catch { }
                if (depth < minDepth || depth > maxDepth) return false;

                try
                {
                    if (TerrainMeta.TopologyMap != null)
                    {
                        int topo = TerrainMeta.TopologyMap.GetTopology(candidate);
                        bool river = (topo & (int)TerrainTopology.Enum.River) != 0
                                  || (topo & (int)TerrainTopology.Enum.Riverside) != 0
                                  || (topo & (int)TerrainTopology.Enum.Lake) != 0;
                        if (river) return false;
                    }
                }
                catch { }

                if (requireCoast && !HasNearbyLand(candidate, 30f, 220f))
                    return false;

                if (NearPlayerCompound(candidate, 18f)) return false;
                if (NearCargoLane(candidate, 280f)) return false;
                if (LooksLikeRiverChannel(candidate)) return false;

                if (ground < -120f) return false;

                result = candidate;
                result.y = forSubmarine ? 0.2f : 0.6f;

                if (HullBlocked(result, forSubmarine ? 1.6f : 2.4f))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }



        /// <summary>True when dry land sits on BOTH banks within 90m (river / inlet).</summary>
        private bool LooksLikeRiverChannel(Vector3 waterPos)
        {
            Vector3 along = new Vector3(-waterPos.z, 0f, waterPos.x);
            if (along.sqrMagnitude < 1f) along = Vector3.right;
            along.Normalize();
            Vector3 across = new Vector3(-along.z, 0f, along.x);
            return DryWithin(waterPos, across, 90f) && DryWithin(waterPos, -across, 90f);
        }

        private bool DryWithin(Vector3 origin, Vector3 dir, float maxMeters)
        {
            for (float d = 18f; d <= maxMeters; d += 24f)
            {
                Vector3 p = origin + dir * d;
                try
                {
                    float ground = TerrainMeta.HeightMap.GetHeight(p);
                    if (ground > 1.0f && WaterDepthAt(p) < 0.35f) return true;
                }
                catch { }
            }
            return false;
        }

        private bool NearCargoLane(Vector3 pos, float meters)
        {
            float sq = meters * meters;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    string sn = ent.ShortPrefabName ?? "";
                    if (sn.IndexOf("cargoship", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var be = ent as BaseEntity;
                    if (be == null) continue;
                    Vector3 c;
                    try { c = be.transform.position; } catch { continue; }
                    float dx = c.x - pos.x, dz = c.z - pos.z;
                    if (dx * dx + dz * dz < sq) return true;
                }
            }
            catch { }
            return false;
        }

        private bool HasNearbyLand(Vector3 waterPos, float minMeters, float maxMeters)
        {
            Vector3 inland = new Vector3(-waterPos.x, 0f, -waterPos.z);
            if (inland.sqrMagnitude < 1f) inland = Vector3.forward;
            inland.Normalize();
            for (float d = minMeters; d <= maxMeters; d += 24f)
            {
                Vector3 p = waterPos + inland * d;
                if (WaterDepthAt(p) <= 0.25f) return true;
            }
            // also test a couple side offsets so a cape still counts
            Vector3 side = new Vector3(-inland.z, 0f, inland.x);
            Vector3 a = waterPos + inland * minMeters + side * 30f;
            Vector3 b = waterPos + inland * minMeters - side * 30f;
            return WaterDepthAt(a) <= 0.25f || WaterDepthAt(b) <= 0.25f;
        }

        private bool NearPlayerCompound(Vector3 p, float range)
        {
            try
            {
                var tcs = new List<BuildingPrivlidge>();
                Vis.Entities(p, range, tcs, Layers.Mask.Deployed);
                for (int i = 0; i < tcs.Count; i++)
                {
                    var tc = tcs[i];
                    if (tc == null || tc.IsDestroyed) continue;
                    try
                    {
                        if (tc.authorizedPlayers == null || tc.authorizedPlayers.Count == 0) continue;
                    }
                    catch { }
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool HullBlocked(Vector3 p, float radius)
        {
            try
            {
                int n = Physics.OverlapSphereNonAlloc(p, radius, _vehicleOverlap, Layers.Mask.World | Layers.Mask.Construction, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = _vehicleOverlap[i];
                    if (col == null) continue;
                    string name = col.name ?? "";
                    string go = col.gameObject != null ? col.gameObject.name : "";
                    for (int k = 0; k < VehicleBlockNames.Length; k++)
                    {
                        if (name.IndexOf(VehicleBlockNames[k], StringComparison.OrdinalIgnoreCase) >= 0 ||
                            go.IndexOf(VehicleBlockNames[k], StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private Vector3 FindLandSpawn()
        {
            float half = 1400f;
            try { half = ConVar.Server.worldsize * 0.4f; } catch { }
            for (int i = 0; i < 80; i++)
            {
                var p = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                try
                {
                    float ground = TerrainMeta.HeightMap.GetHeight(p);
                    if (WaterDepthAt(p) > 0.30f) continue;
                    if (NearPlayerCompound(p, 20f)) continue;
                    if (HullBlocked(p + Vector3.up * 1.2f, 1.6f)) continue;
                    p.y = ground;
                    return p;
                }
                catch { }
            }
            return Vector3.zero;
        }

        private void TrackPlayerVehicle(BaseEntity ent)
        {
            if (ent == null || ent.net == null) return;
            ulong id = ent.net.ID.Value;
            float now = Time.realtimeSinceStartup;
            if (!_vehicleFirstSeen.ContainsKey(id) && _vehicleFirstSeen.Count >= MaxTrackedVehicles)
            {
                // Drop oldest first-seen entry to make room
                ulong drop = 0;
                float oldest = float.MaxValue;
                foreach (var kv in _vehicleFirstSeen)
                {
                    if (kv.Value < oldest) { oldest = kv.Value; drop = kv.Key; }
                }
                if (drop != 0)
                {
                    _vehicleFirstSeen.Remove(drop);
                    _vehicleLastOccupied.Remove(drop);
                }
            }
            _vehicleFirstSeen[id] = now;
            _vehicleLastOccupied[id] = now;
        }

        /// <summary>Event spawn path — track + protect from idle despawn for DefaultEventProtectMinutes.</summary>
        private void TrackEventVehicle(BaseEntity ent)
        {
            InvalidatePrefabCountCache();
            TrackPlayerVehicle(ent);
            if (ent?.net == null) return;
            float mins = config?.Despawn?.EventProtectMinutes > 0f
                ? Mathf.Min(config.Despawn.EventProtectMinutes, 55f)
                : DefaultEventProtectMinutes;
            float until = Time.realtimeSinceStartup + mins * 60f;
            ProtectVehicleFamily(ent, until);
            DebugLog($"Event vehicle protected net={ent.net.ID.Value} for {mins:F0}m");
        }

        private void ProtectVehicleFamily(BaseEntity root, float until)
        {
            if (root == null) return;
            void Protect(BaseEntity be)
            {
                if (be == null || be.net == null) return;
                ulong id = be.net.ID.Value;
                if (id == 0) return;
                _eventProtectedUntil[id] = until;
                TrackPlayerVehicle(be);
            }
            Protect(root);
            try
            {
                var kids = root.GetComponentsInChildren<BaseEntity>(true);
                if (kids != null)
                {
                    for (int i = 0; i < kids.Length; i++)
                        Protect(kids[i]);
                }
            }
            catch { }
            try
            {
                Vector3 origin = root.transform.position;
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed || ent.net == null) continue;
                    string sn = ent.ShortPrefabName ?? "";
                    if (!IsWaterVehiclePart(sn)) continue;
                    var be = ent as BaseEntity;
                    if (be == null) continue;
                    try
                    {
                        float dx = be.transform.position.x - origin.x;
                        float dz = be.transform.position.z - origin.z;
                        if (dx * dx + dz * dz > 22f * 22f) continue;
                    }
                    catch { continue; }
                    Protect(be);
                }
            }
            catch { }
        }

        // ---------- Despawn ----------
        /// <summary>
        /// Single full-world walk per tick. Collects cargo anchors in the same pass when
        /// crate despawn is enabled. Kill buffer stores netIds only (no BaseEntity refs).
        /// </summary>
        private void ProcessVehicleDespawn()
        {
            if (config?.Despawn == null || !config.Despawn.Enabled) return;
            var cfg = config.Despawn;
            float now = Time.realtimeSinceStartup;
            _despawnKillBuffer.Clear();
            _despawnAliveBuffer.Clear();
            _passCargoPositions.Clear();
            BuildOccupiedVehicleSet();
            PruneEventProtectedVehicles();
            if (!_mapMonumentsBuilt) BuildMapMonumentCache();
            if (_passHalfMap < 1f)
            {
                try { _passHalfMap = TerrainMeta.Size.x * 0.5f * 0.95f; }
                catch { _passHalfMap = ConVar.Server.worldsize * 0.5f * 0.95f; }
            }

            bool wantCrates = false; // never despawn crates — oil rig / deep sea / event
            // Precompute cheap short-name checks once
            bool wantAny =
                cfg.DespawnAttackHelis || cfg.DespawnMinicopters || cfg.DespawnScrapHelis ||
                cfg.DespawnTugboats || cfg.DespawnRhibs || cfg.DespawnSubmarines || cfg.DespawnHotAirBalloons || wantCrates;
            if (!wantAny) return;

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null || ent.IsDestroyed || ent.net == null) continue;

                // ShortPrefabName only — avoid PrefabName string allocs on 50k entities
                string sn = ent.ShortPrefabName ?? "";
                if (sn.Length == 0) continue;

                // Opportunistic cargo anchor collection in THIS pass (no second world walk)
                if (wantCrates && sn.IndexOf("cargoship", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var cargoBe = ent as BaseEntity;
                        if (cargoBe != null) _passCargoPositions.Add(cargoBe.transform.position);
                    }
                    catch { }
                }

                if (sn.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sn.IndexOf("hackable", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                string label = ClassifyDespawnLabelFast(sn, cfg);
                if (label == null) continue;

                ulong id = ent.net.ID.Value;
                var be = ent as BaseEntity;
                if (be == null) continue;

                _despawnAliveBuffer.Add(id);

                Vector3 pos;
                try { pos = be.transform.position; }
                catch { continue; }

                // Cap adoption
                if (!_vehicleFirstSeen.ContainsKey(id))
                {
                    if (_vehicleFirstSeen.Count >= MaxTrackedVehicles && !IsEventProtectedVehicle(id))
                    {
                        // Void-ghost crates only — no permanent track
                        if (label == "RHIB" && (pos.y < -200f || IsVoidMapEdge(pos)))
                        {
                            if (!_pendingDespawnIds.Contains(id))
                                _despawnKillBuffer.Add((id, label, pos));
                        }
                        continue;
                    }
                    _vehicleFirstSeen[id] = now;
                }
                if (!_vehicleLastOccupied.ContainsKey(id))
                    _vehicleLastOccupied[id] = now;

                if (IsEventProtectedVehicle(id))
                    continue;

                if (_occupiedVehicleIds.Contains(id) || IsPlayerVehicleOccupied(be) || PlayerOnOrNear(be, 120f))
                {
                    _vehicleLastOccupied[id] = now;
                    continue;
                }

                float idleMin = cfg.IdleMinutesBeforeDespawn;
                float lifeMin = cfg.MaxLifetimeMinutes;
                float last = _vehicleLastOccupied[id];
                float first = _vehicleFirstSeen[id];
                bool idleTooLong = idleMin > 0 && (now - last) >= idleMin * 60f;
                bool lifeTooLong = lifeMin > 0 && (now - first) >= lifeMin * 60f;
                if (!idleTooLong && !lifeTooLong) continue;
                if (_pendingDespawnIds.Contains(id)) continue;

                if (label == "Hackable Crate")
                {
                    if (pos.y < -400f)
                    {
                        // void-fallen ghost — always eligible
                    }
                    else if (!cfg.DespawnHackableCrates)
                    {
                        continue;
                    }
                    else if (IsOffshoreCrate(pos))
                    {
                        continue;
                    }
                    else if (IsEventProtectedCrate(id))
                    {
                        continue;
                    }
                    else
                    {
                        var hc = be as HackableLockedCrate;
                        bool hacking = false;
                        try { if (hc != null && hc.IsBeingHacked()) hacking = true; } catch { }
                        if (hacking) continue;
                    }
                }

                float prox = cfg.RequireNoNearbyPlayersMeters;
                if (prox > 0f && PlayersNear(pos, prox)) continue;

                _despawnKillBuffer.Add((id, label, pos));
            }

            // prune dead tracking
            _pruneIdBuffer.Clear();
            foreach (var id in _vehicleFirstSeen.Keys)
                if (!_despawnAliveBuffer.Contains(id)) _pruneIdBuffer.Add(id);
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
            {
                ulong id = _pruneIdBuffer[i];
                _vehicleFirstSeen.Remove(id);
                _vehicleLastOccupied.Remove(id);
                _pendingDespawnIds.Remove(id);
                _eventProtectedUntil.Remove(id);
            }

            int max = cfg.MaxDespawnsPerPass > 0 ? cfg.MaxDespawnsPerPass : 8;
            float gap = Mathf.Max(0.1f, cfg.SecondsBetweenDespawns);
            int n = 0;
            for (int i = 0; i < _despawnKillBuffer.Count && n < max; i++)
            {
                var (id, label, pos) = _despawnKillBuffer[i];
                if (id == 0 || !_pendingDespawnIds.Add(id)) continue;

                float delay = n * gap;
                ulong captureId = id;
                string lab = label;
                Vector3 capturePos = pos;
                bool broadcast = cfg.BroadcastDespawn;
                timer.Once(delay, () =>
                {
                    try
                    {
                        var be = FindEntityByNetId(captureId);
                        if (be != null && !be.IsDestroyed)
                        {
                            try { capturePos = be.transform.position; } catch { }
                            try { if (!IsLootCrateEntity(be)) be.Kill(); } catch { }
                            if (broadcast)
                                BroadcastKey("VehicleDespawned", lab, Grid(capturePos));
                            DebugLog($"Despawned {lab} at {Grid(capturePos)}");
                        }
                    }
                    finally
                    {
                        _pendingDespawnIds.Remove(captureId);
                        _vehicleFirstSeen.Remove(captureId);
                        _vehicleLastOccupied.Remove(captureId);
                        _eventProtectedUntil.Remove(captureId);
                    }
                });
                n++;
            }
            _despawnKillBuffer.Clear();
            _passCargoPositions.Clear();
            _despawnPassCounter++;
        }

        private bool IsVoidMapEdge(Vector3 cp)
        {
            float half = _passHalfMap > 1f ? _passHalfMap : 1500f;
            return Mathf.Abs(cp.x) >= half || Mathf.Abs(cp.z) >= half;
        }

        /// <summary>
        /// True for crates that must never be idle-despawned.
        /// Uses map-lifetime monument cache + cargo positions collected in the same pass.
        /// </summary>
        private bool IsLegitimateHackableCrateLocation(Vector3 cp)
        {
            if (cp.y < -200f) return false;

            float half = _passHalfMap > 1f ? _passHalfMap : 1500f;
            if (Mathf.Abs(cp.x) < half && Mathf.Abs(cp.z) < half)
                return true;

            for (int i = 0; i < _passCargoPositions.Count; i++)
            {
                Vector3 d = _passCargoPositions[i] - cp;
                d.y = 0f;
                if (d.sqrMagnitude < 120f * 120f) return true;
            }

            for (int i = 0; i < _mapDeepSeaMonuments.Count; i++)
            {
                Vector3 d = _mapDeepSeaMonuments[i] - cp;
                if (d.sqrMagnitude < 220f * 220f) return true;
            }

            return false;
        }

        /// <summary>Classify using ShortPrefabName only (no PrefabName string work).</summary>
        private string ClassifyDespawnLabelFast(string sn, DespawnConfig cfg)
        {
            if (string.IsNullOrEmpty(sn) || cfg == null) return null;
            // Fast rejects for common non-targets
            if (sn.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (sn.IndexOf("bradley", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (sn.IndexOf("ch47", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (sn.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            if (cfg.DespawnAttackHelis && sn.IndexOf("attackhelicopter", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Attack Heli";
            if (cfg.DespawnMinicopters && sn.IndexOf("minicopter", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Minicopter";
            if (cfg.DespawnScrapHelis && sn.IndexOf("scraptransport", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Scrap Heli";
            if (cfg.DespawnTugboats && IsPrimaryWaterVehicle(sn) &&
                sn.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Tugboat";
            if (cfg.DespawnRhibs && sn.IndexOf("rhib", StringComparison.OrdinalIgnoreCase) >= 0)
                return "RHIB";
            if (cfg.DespawnSubmarines && IsPrimaryWaterVehicle(sn) &&
                sn.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Submarine";
            if (IsWaterVehiclePart(sn) && !IsPrimaryWaterVehicle(sn))
                return null;
            if (cfg.DespawnHotAirBalloons && sn.IndexOf("hotairballoon", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hot Air Balloon";
            if (sn.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sn.IndexOf("hackable", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;
            return null;
        }


        private bool IsOffshoreCrate(Vector3 pos)
        {
            float half = _passHalfMap > 100f ? _passHalfMap : (TerrainMeta.Size.x * 0.5f);
            if (Mathf.Abs(pos.x) > half * 0.86f || Mathf.Abs(pos.z) > half * 0.86f)
                return true;
            if (_mapDeepSeaMonuments != null)
            {
                for (int i = 0; i < _mapDeepSeaMonuments.Count; i++)
                {
                    Vector3 m = _mapDeepSeaMonuments[i];
                    float dx = pos.x - m.x, dz = pos.z - m.z;
                    if (dx * dx + dz * dz < 90f * 90f) return true;
                }
            }
            return false;
        }

        private bool IsEventProtectedCrate(ulong netId)
        {
            if (netId == 0) return false;
            try
            {
                if (LiveStatsEvents == null || !LiveStatsEvents.IsLoaded)
                    LiveStatsEvents = plugins.Find("LiveStatsEvents");
                object r = LiveStatsEvents?.Call("API_IsEventProtectedCrate", netId);
                if (r is bool b && b) return true;
            }
            catch { }
            return false;
        }


        private void BuildOccupiedVehicleSet()
        {
            _occupiedVehicleIds.Clear();
            void Track(BasePlayer player)
            {
                if (player == null || player.IsDestroyed) return;
                try
                {
                    var mount = player.GetMountedVehicle();
                    if (mount != null && mount.net != null)
                        _occupiedVehicleIds.Add(mount.net.ID.Value);
                }
                catch { }
                try
                {
                    var parent = player.GetParentEntity();
                    int hops = 0;
                    while (parent != null && hops++ < 6)
                    {
                        if (parent.net != null)
                            _occupiedVehicleIds.Add(parent.net.ID.Value);
                        parent = parent.GetParentEntity();
                    }
                }
                catch { }
            }
            if (BasePlayer.activePlayerList != null)
                foreach (var p in BasePlayer.activePlayerList) Track(p);
            if (BasePlayer.sleepingPlayerList != null)
                foreach (var p in BasePlayer.sleepingPlayerList) Track(p);
        }

        private bool IsPlayerVehicleOccupied(BaseEntity be)
        {
            if (be == null) return false;
            if (be.net != null && _occupiedVehicleIds.Contains(be.net.ID.Value)) return true;
            try
            {
                var vehicle = be as BaseVehicle;
                if (vehicle != null && vehicle.AnyMounted()) return true;
            }
            catch { }
            return false;
        }

        private bool PlayersNear(Vector3 pos, float meters)
        {
            float sq = meters * meters;
            bool Near(BasePlayer p)
            {
                if (p == null || p.IsDestroyed) return false;
                try { return (p.transform.position - pos).sqrMagnitude <= sq; }
                catch { return false; }
            }
            if (BasePlayer.activePlayerList != null)
            {
                foreach (var p in BasePlayer.activePlayerList)
                    if (Near(p)) return true;
            }
            if (BasePlayer.sleepingPlayerList != null)
            {
                foreach (var p in BasePlayer.sleepingPlayerList)
                    if (Near(p)) return true;
            }
            return false;
        }


        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null) return;
            ulong id = entity.net.ID.Value;
            _playerOwnedIds.Remove(id);
            _eventProtectedUntil.Remove(id);
            _occupiedVehicleIds.Remove(id);
            _vehicleFirstSeen.Remove(id);
            _vehicleLastOccupied.Remove(id);
            _pendingDespawnIds.Remove(id);
        }

        private void OnEntityMounted(BaseMountable mount, BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            MarkPlayerOwned(ResolveMountedHull(mount, player), player);
        }

        private object OnEngineStarted(BaseVehicle vehicle, BasePlayer driver)
        {
            if (driver != null && !driver.IsNpc)
                MarkPlayerOwned(vehicle, driver);
            return null;
        }

        private BaseEntity ResolveMountedHull(BaseMountable mount, BasePlayer player)
        {
            try
            {
                var v = player != null ? player.GetMountedVehicle() as BaseEntity : null;
                if (v != null) return v;
            }
            catch { }
            if (mount == null) return null;
            try
            {
                var parent = mount.GetParentEntity();
                if (parent != null) return parent;
            }
            catch { }
            return mount;
        }

        private void MarkPlayerOwned(BaseEntity hull, BasePlayer player)
        {
            if (hull == null || hull.IsDestroyed || hull.net == null) return;
            string sn = hull.ShortPrefabName ?? "";
            if (!IsClaimableVehicle(sn)) return;
            ulong id = hull.net.ID.Value;
            if (id == 0) return;
            bool fresh = _playerOwnedIds.Add(id);
            _eventProtectedUntil[id] = Time.realtimeSinceStartup + 24f * 60f * 60f;
            if (fresh)
            {
                string who = player != null ? player.displayName : "?";
                Puts("[Vehicles] player-owned " + sn + " net=" + id + " by " + who + " — no despawn until destroyed");
                try { Interface.CallHook("OnLiveStatsVehicleClaimed", id, ClaimType(sn)); } catch { }
            }
        }

        private static bool IsClaimableVehicle(string sn)
        {
            if (string.IsNullOrEmpty(sn)) return false;
            if (sn.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (sn.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return sn.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("minicopter", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("scraptransport", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("attackhelicopter", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("rhib", StringComparison.OrdinalIgnoreCase) >= 0
                || sn.IndexOf("rowboat", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ClaimType(string sn)
        {
            if (sn.IndexOf("tug", StringComparison.OrdinalIgnoreCase) >= 0) return "tugboat";
            if (sn.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0) return "submarine";
            if (sn.IndexOf("mini", StringComparison.OrdinalIgnoreCase) >= 0) return "minicopter";
            if (sn.IndexOf("scrap", StringComparison.OrdinalIgnoreCase) >= 0) return "scrapheli";
            if (sn.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0) return "attackheli";
            return "vehicle";
        }

        private bool IsPlayerOwned(BaseEntity be)
        {
            if (be == null || be.net == null) return false;
            return _playerOwnedIds.Contains(be.net.ID.Value);
        }

        private bool PlayerOnOrNear(BaseEntity be, float meters)
        {
            if (be == null || be.IsDestroyed) return false;
            if (IsPlayerVehicleOccupied(be)) return true;
            Vector3 pos;
            try { pos = be.transform.position; } catch { return false; }
            return PlayersNear(pos, meters);
        }

        private void ProtectOccupiedHull(BaseEntity be, float minutes)
        {
            if (be == null || be.net == null) return;
            ulong id = be.net.ID.Value;
            float until = Time.realtimeSinceStartup + minutes * 60f;
            float have;
            if (_eventProtectedUntil.TryGetValue(id, out have) && have >= until) return;
            _eventProtectedUntil[id] = until;
            Puts("[Vehicles] keep hull " + (be.ShortPrefabName ?? "?") + " net=" + id + " — player on/near");
        }

        private int CountPrefabs(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return 0;
            float now = Time.realtimeSinceStartup;
            if (_prefabCountCache.TryGetValue(fragment, out var cached) && now - cached.time < 12f)
                return cached.count;

            int n = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null || ent.IsDestroyed) continue;
                var be = ent as BaseEntity;
                if (be == null || be.GetParentEntity() != null) continue;
                string p = be.ShortPrefabName ?? "";
                if (p.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (be as BaseVehicle == null) continue;
                bool water = fragment.IndexOf("tug", StringComparison.OrdinalIgnoreCase) >= 0
                          || fragment.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0;
                if (water && !IsPrimaryWaterVehicle(p)) continue;
                n++;
            }
            if (fragment.IndexOf("tug", StringComparison.OrdinalIgnoreCase) >= 0
                || fragment.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // unparented hull pieces still share one boat — cluster 20m
                n = CountWaterVehicleGroups(fragment);
            }
            _prefabCountCache[fragment] = (n, now);
            return n;
        }

        private int CountWaterVehicleGroups(string fragment)
        {
            var pts = new List<Vector3>(8);
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null || ent.IsDestroyed) continue;
                var be = ent as BaseEntity;
                if (be == null || be.GetParentEntity() != null) continue;
                string p = be.ShortPrefabName ?? "";
                if (p.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (be as BaseVehicle == null) continue;
                try { pts.Add(be.transform.position); } catch { }
            }
            int groups = 0;
            var used = new bool[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                if (used[i]) continue;
                groups++;
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (used[j]) continue;
                    float dx = pts[i].x - pts[j].x;
                    float dz = pts[i].z - pts[j].z;
                    if (dx * dx + dz * dz < 20f * 20f)
                        used[j] = true;
                }
            }
            return groups;
        }


        private int CountLiveWaterHulls(string fragment)
        {
            int n = 0;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed || ent.net == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null) continue;
                    try { if (be.GetParentEntity() != null) continue; } catch { }
                    string sn = be.ShortPrefabName ?? "";
                    if (!IsPrimaryWaterVehicle(sn)) continue;
                    if (sn.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Vector3 pos;
                    try { pos = be.transform.position; } catch { continue; }
                    if (pos.y < -20f) continue;
                    n++;
                }
            }
            catch { }
            return n;
        }

        private void KillVoidWaterHulls(string fragment)
        {
            var ids = new List<ulong>(8);
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed || ent.net == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null) continue;
                    string sn = be.ShortPrefabName ?? "";
                    if (!IsPrimaryWaterVehicle(sn)) continue;
                    if (sn.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Vector3 pos;
                    try { pos = be.transform.position; } catch { continue; }
                    if (pos.y >= -20f) continue;
                    if (IsPlayerVehicleOccupied(be)) continue;
                    ids.Add(ent.net.ID.Value);
                }
            }
            catch { }
            int k = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                try
                {
                    var be = FindEntityByNetId(ids[i]);
                    if (be == null || be.IsDestroyed) continue;
                    if (!IsLootCrateEntity(be)) be.Kill();
                    k++;
                }
                catch { }
            }
            if (k > 0)
            {
                InvalidatePrefabCountCache();
                Puts("[Vehicles] void-killed " + k + " " + fragment + " hull(s)");
            }
        }

        private int CountEventVehicles(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return 0;
            PruneEventProtectedVehicles();
            int n = 0;
            foreach (var kv in _eventProtectedUntil)
            {
                if (kv.Value <= Time.realtimeSinceStartup) continue;
                var be = FindEntityByNetId(kv.Key);
                if (be == null || be.IsDestroyed) continue;
                if (be.GetParentEntity() != null) continue;
                string p = be.ShortPrefabName ?? "";
                if (p.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!IsPrimaryWaterVehicle(p) &&
                    (fragment.IndexOf("tug", StringComparison.OrdinalIgnoreCase) >= 0
                     || fragment.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                n++;
            }
            return n;
        }

        private void InvalidatePrefabCountCache()
        {
            _prefabCountCache.Clear();
        }

        // ---------- Helpers / messaging ----------
        private void DebugLog(string message)
        {
            if (config != null && config.Debug)
                Puts($"[Vehicles] {message}");
            try { LiveStatsEvents?.Call("API_DebugLog", $"[Vehicles] {message}"); } catch { }
        }

        private void Broadcast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                player.ChatMessage(message);
            }
            Puts($"[Vehicles] {message}");
        }

        private void BroadcastKey(string key, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                string msg = Msg(key, player.UserIDString, args);
                if (!string.IsNullOrEmpty(msg))
                    player.ChatMessage(msg);
            }
            string log = Msg(key, null, args);
            if (!string.IsNullOrEmpty(log))
                Puts($"[Vehicles] {log}");
        }

        private string Grid(Vector3 pos)
        {
            try
            {
                var r = LiveStatsEvents?.Call("API_PositionToGrid", pos);
                if (r is string s && !string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return $"{pos.x:F0},{pos.z:F0}";
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            string text = lang.GetMessage(key, this, userId);
            if (args != null && args.Length > 0)
            {
                try { return string.Format(text, args); }
                catch { return text; }
            }
            return text;
        }

        [ChatCommand("lsev")]
        private void CmdModule(BasePlayer player, string command, string[] args)
        {
            if (player != null && !permission.UserHasPermission(player.UserIDString, AdminPermission)
                && !permission.UserHasPermission(player.UserIDString, "livestatsevents.admin"))
            {
                player?.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }
            string status = lang.GetMessage(_registered ? "ModuleRegistered" : "ModuleNotRegistered", this, player?.UserIDString);
            string msg = string.Format(lang.GetMessage("ModuleStatus", this, player?.UserIDString),
                status, config?.Despawn?.Enabled == true, config?.Despawn?.DespawnHackableCrates, _vehicleFirstSeen.Count, MaxTrackedVehicles, _eventProtectedUntil.Count, LiveStatsEvents != null);
            if (player != null) player.ChatMessage(msg); else Puts(msg);
        }

        #region Config
        private class ConfigData
        {
            [JsonProperty("Debug")] public bool Debug { get; set; } = false;
            [JsonProperty("Limits")] public LimitsConfig Limits { get; set; } = new LimitsConfig();
            [JsonProperty("Despawn")] public DespawnConfig Despawn { get; set; } = new DespawnConfig();
        }

        private class LimitsConfig
        {
            [JsonProperty("MaxTugboats")] public int MaxTugboats { get; set; } = 1;
            [JsonProperty("MaxSubmarines")] public int MaxSubmarines { get; set; } = 1;
            [JsonProperty("MaxScrapHelis")] public int MaxScrapHelis { get; set; } = 1;
            [JsonProperty("MaxMinicopters")] public int MaxMinicopters { get; set; } = 1;
        }

        private class DespawnConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("CheckIntervalSeconds")] public float CheckIntervalSeconds { get; set; } = 300f;
            [JsonProperty("IdleMinutesBeforeDespawn")] public float IdleMinutesBeforeDespawn { get; set; } = 45f;
            [JsonProperty("MaxLifetimeMinutes")] public float MaxLifetimeMinutes { get; set; } = 55f;
            [JsonProperty("EventProtectMinutes")] public float EventProtectMinutes { get; set; } = 55f;
            [JsonProperty("RequireNoNearbyPlayersMeters")] public float RequireNoNearbyPlayersMeters { get; set; } = 80f;
            [JsonProperty("DespawnAttackHelis")] public bool DespawnAttackHelis { get; set; } = true;
            [JsonProperty("DespawnMinicopters")] public bool DespawnMinicopters { get; set; } = true;
            [JsonProperty("DespawnScrapHelis")] public bool DespawnScrapHelis { get; set; } = true;
            [JsonProperty("DespawnTugboats")] public bool DespawnTugboats { get; set; } = true;
            [JsonProperty("DespawnRhibs")] public bool DespawnRhibs { get; set; } = true;
            [JsonProperty("DespawnSubmarines")] public bool DespawnSubmarines { get; set; } = true;
            [JsonProperty("DespawnHotAirBalloons")] public bool DespawnHotAirBalloons { get; set; } = true;
            [JsonProperty("DespawnHackableCrates")] public bool DespawnHackableCrates { get; set; } = false;
            [JsonProperty("BroadcastDespawn")] public bool BroadcastDespawn { get; set; } = false;
            [JsonProperty("SecondsBetweenDespawns")] public float SecondsBetweenDespawns { get; set; } = 0.25f;
            [JsonProperty("MaxDespawnsPerPass")] public int MaxDespawnsPerPass { get; set; } = 12;
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null) throw new Exception("null config");
            }
            catch
            {
                Puts("[Vehicles] Config parse failed — writing a clean default. Set DespawnHackableCrates to true or false (no quotes).");
                LoadDefaultConfig();
                if (config.Despawn == null) config.Despawn = new DespawnConfig();
                config.Despawn.DespawnHackableCrates = true;
                SaveConfig();
                return;
            }
            if (config.Limits == null) config.Limits = new LimitsConfig();
            if (config.Despawn == null) config.Despawn = new DespawnConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["SpawnTugboat"] = "<color=#55aaff>Tugboat spotted offshore!</color>",
                ["SpawnSub"] = "<color=#5599ff>Submarine detected near the coast!</color>",
                ["SpawnScrapHeli"] = "<color=#aaaaaa>Scrap Transport Heli available!</color>",
                ["SpawnMini"] = "<color=#99ff99>Minicopter dropped into the world!</color>",
                ["VehicleDespawned"] = "<color=#aaaaaa>{0} at {1} was removed (idle / lifetime cleanup).</color>",
                ["NoPermission"] = "You do not have permission to use this command.",
                ["ModuleRegistered"] = "registered with core",
                ["ModuleNotRegistered"] = "NOT registered",
                ["ModuleStatus"] = "[Vehicles] {0} | despawn={1} crates={2} tracked={3}/{4} protected={5} core={6}"
            }, this);
        }
        #endregion
    }
}
