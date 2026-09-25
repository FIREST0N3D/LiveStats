// Requires: LiveStatsWorld
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("LiveStatsEvents", "FiREST0N3D", "1.13.59")]
    [Description("Core event scheduler + classic world events (Cargo, Airdrop, Heli, Chinook, Bradley, F-15, Hackable). Requires LiveStatsWorld. NPC events require LiveStatsEventsNPC. Vehicle events+despawn require LiveStatsEventsVehicles. Configs: core owns schedule + classic events; LiveStatsEventsNPC owns NPC behavior; LiveStatsEventsVehicles owns vehicle limits/despawn.")]
    class LiveStatsEvents : RustPlugin
    {
        // Hard dependency — Oxide resolves this after LiveStatsWorld is loaded
        [PluginReference]
        private Plugin LiveStatsWorld;

        private ConfigData config;
        private Timer _eventTimer;
        private Timer _announceTimer;

        private readonly Dictionary<string, DateTime> _lastEventTime = new Dictionary<string, DateTime>();
        private readonly List<PendingAnnouncement> _pendingAnnouncements = new List<PendingAnnouncement>();
        private const string DataFileName = "livestats_events_data";

        /// <summary>UTC time after which the next world event may fire (global spacing between any two events).</summary>
        private DateTime _nextEventAllowedUtc = DateTime.MinValue;
        /// <summary>After boot/reload the first successful world event must be Cargo Ship.</summary>
        private bool _forceFirstEventCargo = true;
        private bool _loggedCooldownSkip;
        private float _lastCooldownSkipLog = -999f;

        private const string AdminPermission = "livestatsevents.admin";
        // These belong to other plugins – we only CHECK them, never register them.
        private const string LiveStatsAdminPermission = "livestats.admin";
        private const string LiveStatsWorldAdminPermission = "livestatsworld.admin";

        // ===== Option A modules (companion plugins) =====
        [PluginReference] private Plugin LiveStatsEventsVehicles;
        [PluginReference] private Plugin LiveStatsEventsNPC;
        private string _lastModuleSpawnGrid;
        private readonly HashSet<string> _registeredModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string ModuleVehicles = "vehicles";
        private const string ModuleNpc = "npc";

        // Short-term cache for entity counts to avoid repeated full-world scans
        private readonly Dictionary<string, CachedCount> _entityCountCache = new Dictionary<string, CachedCount>();
        private const float EntityCountCacheSeconds = 180f; // longer TTL — pairs with shared live index (perf)
        private bool? _lastNightLightsIsNight;
        private float _lastTrackingPrune = -999f;

        // ---- Shared live-entity index (one world pass, many cheap lookups) ----
        // Rebuilt on a slow timer; counts / near-checks use these sets instead of
        // rescanning all ~50k serverEntities. NetIds only — Find on demand.
        private readonly HashSet<ulong> _idxCargo = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxChinook = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxPatrolHeli = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxBradley = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxTug = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxSub = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxBalloon = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxCargoPlane = new HashSet<ulong>();
        /// <summary>Cargo planes we spawned — vanilla boot planes are killed if not in this set.</summary>
        private readonly HashSet<ulong> _pluginCargoPlaneIds = new HashSet<ulong>();
        private bool _pendingF15Relocate;
        private Vector3 _pendingF15Pos;
        private Quaternion _pendingF15Rot;
        private float _pendingF15Until;
        private ulong _airfieldChinookId;
        private float _airfieldChinookUntil;
        private readonly HashSet<ulong> _airfieldGuardIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxAttackHeli = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxMini = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxScrap = new HashSet<ulong>();
        private readonly HashSet<ulong> _idxCrate = new HashSet<ulong>();
        private float _liveIndexBuiltAt = -999f;
        // Sanity full-world rebuild only. Spawn/kill hooks keep the index current between rebuilds.
        private const float LiveIndexMaxAge = 1200f; // 20 min sanity pass — spawn/kill keep index hot
        private Timer _liveIndexTimer;

        // Road cache survives soft reloads (oxide.reload) when map identity is unchanged
        private static List<CachedRoad> _staticRoadCache;
        private static string _staticRoadMapIdentity;

        // Idle / lifetime tracking for player-controllable vehicles (tug, balloon, mini, etc.)
        // netID -> realtimeSinceStartup of last time the vehicle had an occupant (or was first seen)
        // netID -> realtimeSinceStartup when we first observed the vehicle (absolute lifetime)
        private readonly Dictionary<ulong, float> _vehicleFirstSeen = new Dictionary<ulong, float>();
        // Scientist NPCs spawned by this plugin only (never vanilla oil-rig / monument guards)
        private readonly HashSet<ulong> _trackedScientistIds = new HashSet<ulong>();
        // Bradleys we spawn — vanilla Launch Site APC is suppressed separately
        private readonly HashSet<ulong> _pluginBradleyIds = new HashSet<ulong>();
        private float _suppressVanillaBradleyUntil = -1f;

        // Shared road graph — built once, used by Bradley + future scientist patrols
        private class CachedRoad
        {
            public List<Vector3> Points = new List<Vector3>();
            public float Width;
            public float Length; // sum of segment lengths
        }
        private readonly List<CachedRoad> _roadCache = new List<CachedRoad>();
        private readonly List<Vector3> _roadXings = new List<Vector3>();
        private readonly List<RoadEdge> _roadEdges = new List<RoadEdge>();
        private readonly List<string> _recentBradleyKeys = new List<string>(8);
        private const int RecentBradleyLimit = 4;
        private class RoadEdge
        {
            public int A;
            public int B;
            public float Len;
            public List<Vector3> Pts = new List<Vector3>();
        }
        // Scientist agent / navmesh snap live in LiveStatsEventsNPC.

        // Cargo ship lifecycle — force egress when path ends / ship sits idle in deep ocean
        private class CargoTrack
        {
            public float FirstSeen;
            public float LastMoveTime;
            public Vector3 LastPos;
            public bool EgressStarted;
            public float EgressStartedAt;
            /// <summary>SpawnGroups disabled + riders killed so Facepunch does not respawn scientists.</summary>
            public bool ScientistsCleared;
            public float LastScientistClearAt;
        }
        private readonly HashSet<ulong> _eventProtectedCrateIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _eventProtectedCrateUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, CargoTrack> _cargoTracks = new Dictionary<ulong, CargoTrack>();
        private Timer _cargoMonitorTimer;
        private Timer _nightLightsTimer;

        // After a Chinook spawns, watch for locked crates it drops and announce their grid
        private float _chinookCrateWatchUntil = -1f;
        private string _lastHackableGrid = "";
        private float _suppressHackableAnnounceUntil = -1f;
        private readonly HashSet<ulong> _announcedCrateIds = new HashSet<ulong>();

        // Weather snapshot cache (avoids disk + Climate reads on every event roll)
        private int _cachedWipeDay;
        private float _cachedWipeDayAt = -999f;
        private const float WipeDayCacheSeconds = 60f;

        private float _weatherCacheTime = -999f;
        private float _weatherRain, _weatherWind, _weatherFog, _weatherThunder;
        private string _weatherProfile = "clear";
        private const float WeatherCacheSeconds = 30f;

        // Reused buffers for cleanup pass (avoid per-tick List allocations)
        private readonly List<(BaseEntity ent, string label)> _despawnKillBuffer = new List<(BaseEntity, string)>(16);
        private readonly HashSet<ulong> _despawnAliveBuffer = new HashSet<ulong>();
        private readonly HashSet<ulong> _occupiedVehicleIds = new HashSet<ulong>();
        private readonly List<ulong> _pruneIdBuffer = new List<ulong>(64);
        private const int MaxTrackedVehicles = 64; // hard cap — prevents dict growth adopting every map vehicle
        private int _despawnPassCounter;

        private class CachedCount
        {
            public int Count;
            public float Time;
        }

        private class PendingAnnouncement
        {
            public string EventType;
            public string Message;
            public int BuildUpMinutes;
            public bool IsBuildUp;
            public float ExecuteAt;
        }

        // ==================== REFLECTION HELPERS (isolated / fragile Facepunch APIs) ====================
        /// <summary>
        /// Invoke a private/public instance method by name. Returns true on success.
        /// Facepunch private APIs can break between patches — always treat failure as non-fatal.
        /// </summary>
        private static bool TryInvokeInstance(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrEmpty(methodName)) return false;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var type = target.GetType();
                System.Reflection.MethodInfo method = null;
                if (args == null || args.Length == 0)
                    method = type.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                else
                {
                    var argTypes = new Type[args.Length];
                    for (int i = 0; i < args.Length; i++)
                        argTypes[i] = args[i]?.GetType() ?? typeof(object);
                    method = type.GetMethod(methodName, flags, null, argTypes, null);
                }
                if (method == null)
                    method = type.GetMethod(methodName, flags); // last resort (may be ambiguous)
                if (method == null) return false;
                method.Invoke(target, args == null || args.Length == 0 ? null : args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryInvokeInstance(object target, Type declaringType, string methodName, params object[] args)
        {
            if (target == null || declaringType == null || string.IsNullOrEmpty(methodName)) return false;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                System.Reflection.MethodInfo method;
                if (args == null || args.Length == 0)
                    method = declaringType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                else
                {
                    var argTypes = new Type[args.Length];
                    for (int i = 0; i < args.Length; i++)
                        argTypes[i] = args[i]?.GetType() ?? typeof(object);
                    method = declaringType.GetMethod(methodName, flags, null, argTypes, null);
                }
                if (method == null) return false;
                method.Invoke(target, args == null || args.Length == 0 ? null : args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region Oxide Hooks

        void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
            // Do NOT register livestats.admin / livestatsworld.admin – those belong to other plugins.
            LoadDefaultMessages();
            Puts("LiveStatsEvents v1.13.59 — Bradley random xing hops, ban last 4 ends");
            // Earliest possible vanilla gate — EventSchedule may queue before OnServerInitialized
            EarlyVanillaSuppress();
        }

        private bool _ready = false;

        void OnServerInitialized(bool initial)
        {
            LoadEventData();
            RestartEventSystem();
        }

        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "LiveStatsWorld" && !_ready && config != null && config.Enabled)
            {
                Puts("LiveStatsWorld loaded after LiveStatsEvents - initializing.");
                RestartEventSystem();
            }
            if (plugin.Name == "LiveStatsEventsVehicles")
            {
                LiveStatsEventsVehicles = plugin;
                _registeredModules.Add(ModuleVehicles);
                Puts("[Events] LiveStatsEventsVehicles detected — vehicle module registered.");
            }
            if (plugin.Name == "LiveStatsEventsNPC")
            {
                LiveStatsEventsNPC = plugin;
                _registeredModules.Add(ModuleNpc);
                Puts("[Events] LiveStatsEventsNPC detected — NPC module registered.");
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "LiveStatsWorld")
            {
                Puts("LiveStatsWorld unloaded - pausing all event timers.");
                _ready = false;
                _eventTimer?.Destroy();
                _eventTimer = null;
                _announceTimer?.Destroy();
                _announceTimer = null;
                _cargoMonitorTimer?.Destroy();
                _cargoMonitorTimer = null;
            }
            if (plugin.Name == "LiveStatsEventsVehicles")
            {
                LiveStatsEventsVehicles = null;
                API_UnregisterModule(ModuleVehicles);
            }
            if (plugin.Name == "LiveStatsEventsNPC")
            {
                LiveStatsEventsNPC = null;
                API_UnregisterModule(ModuleNpc);
            }
        }

        void Unload()
        {
            SaveEventData();
            _eventTimer?.Destroy();
            _announceTimer?.Destroy();
            _cargoMonitorTimer?.Destroy();
            _nightLightsTimer?.Destroy();
            foreach (var t in _heliRouteTimers.Values)
                t?.Destroy();
            _heliRouteTimers.Clear();
            foreach (var t in _heliAltitudeSmoothTimers.Values)
                t?.Destroy();
            _heliAltitudeSmoothTimers.Clear();
            foreach (var t in _chinookTourTimers.Values)
                t?.Destroy();
            _chinookTourTimers.Clear();
            _chinookDroppedIds.Clear();
            foreach (var t in _bradleyRouteTimers.Values)
                t?.Destroy();
            _bradleyRouteTimers.Clear();
                                    _entityCountCache.Clear();
            _vehicleFirstSeen.Clear();
            _trackedScientistIds.Clear();
            _pluginBradleyIds.Clear();
            _cargoTracks.Clear();
            _announcedCrateIds.Clear();
            _pluginCargoPlaneIds.Clear();
            _roadCache.Clear();
            _chinookCrateWatchUntil = -1f;
            _suppressHackableAnnounceUntil = -1f;
            _despawnKillBuffer.Clear();
            _despawnAliveBuffer.Clear();
            _occupiedVehicleIds.Clear();
            _eventProtectedCrateIds.Clear();
            _eventProtectedCrateUntil.Clear();
            _airfieldGuardIds.Clear();
            _weatherCacheTime = -999f;
            _liveIndexTimer?.Destroy();
            _liveIndexTimer = null;
            ClearLiveIndex();
        }

        /// <summary>
        /// (Re)starts or stops the event timers based on the current config.
        /// Safe to call after LoadConfig / when LiveStatsWorld appears / on server init.
        /// Destroys existing timers first so CheckIntervalSeconds and Enabled changes take effect.
        /// </summary>
        private void RestartEventSystem()
        {
            // Tear down any existing timers so interval / Enabled changes take effect
            _eventTimer?.Destroy();
            _eventTimer = null;
            _announceTimer?.Destroy();
            _announceTimer = null;
            _cargoMonitorTimer?.Destroy();
            _cargoMonitorTimer = null;
            _nightLightsTimer?.Destroy();
            _nightLightsTimer = null;
            _liveIndexTimer?.Destroy();
            _liveIndexTimer = null;

            foreach (var t in _heliRouteTimers.Values)
                t?.Destroy();
            _heliRouteTimers.Clear();
            foreach (var t in _heliAltitudeSmoothTimers.Values)
                t?.Destroy();
            _heliAltitudeSmoothTimers.Clear();
            foreach (var t in _chinookTourTimers.Values)
                t?.Destroy();
            _chinookTourTimers.Clear();
            foreach (var t in _bradleyRouteTimers.Values)
                t?.Destroy();
            _bradleyRouteTimers.Clear();
                        // Drop caches on restart so memory from prior session is released
            _entityCountCache.Clear();
            _announcedCrateIds.Clear();
            _pendingAnnouncements.Clear();
            _lastNightLightsIsNight = null;

            if (config == null || !config.Enabled)
            {
                _ready = false;
                Puts("[Events] Plugin disabled in config.");
                return;
            }

            // Hard dependency: LiveStatsWorld must be present
            if (LiveStatsWorld == null)
                LiveStatsWorld = plugins.Find("LiveStatsWorld");

            if (LiveStatsWorld == null)
            {
                PrintError("LiveStatsWorld is required. LiveStatsEvents will not start until LiveStatsWorld is installed and loaded.");
                _ready = false;
                return;
            }

            _ready = true;
            Puts("LiveStatsWorld detected - event system starting.");

            // Auto-register companions already loaded (reload order often loads core first)
            if (LiveStatsEventsNPC == null)
                LiveStatsEventsNPC = plugins.Find("LiveStatsEventsNPC");
            if (LiveStatsEventsVehicles == null)
                LiveStatsEventsVehicles = plugins.Find("LiveStatsEventsVehicles");
            if (LiveStatsEventsNPC != null)
                _registeredModules.Add(ModuleNpc);
            if (LiveStatsEventsVehicles != null)
                _registeredModules.Add(ModuleVehicles);

            // Build road graph once — skip rebuild on soft reload when map identity matches
            EnsureRoadCache();
            _heliInterestCache?.Clear(); // rebuild on first heli event
            Puts("[Events] NPC+Vehicle event modules required (no core inline spawns)");

            // Disable vanilla event systems so we fully own them
            if (config.DisableVanillaEvents)
            {
                DisableVanillaEventSystems();
            }

            // Cold boot: reserve first event as Cargo after 8–18 min.
            // Soft reload (recent persisted times, or a cargo already on the map): do not
            // reset that reservation — it was blocking the whole pool for 20–40 minutes.
            DateTime nowUtc = DateTime.UtcNow;
            bool cargoAlreadyOut = false;
            try { cargoAlreadyOut = !CanSpawnEvent("cargoship"); } catch { }

            bool recentSchedule = false;
            if (_lastEventTime != null)
            {
                foreach (var kv in _lastEventTime)
                {
                    if (kv.Value != DateTime.MinValue && (nowUtc - kv.Value.ToUniversalTime()).TotalMinutes < 90.0)
                    {
                        recentSchedule = true;
                        break;
                    }
                }
            }

            if (recentSchedule || cargoAlreadyOut)
            {
                _forceFirstEventCargo = false;
                if (_nextEventAllowedUtc < nowUtc)
                    _nextEventAllowedUtc = nowUtc.AddMinutes(2.0);
                Puts($"[Events] Soft reload: schedule kept (cargoOut={cargoAlreadyOut}, recent={recentSchedule}). Next event after {_nextEventAllowedUtc:u}");
            }
            else
            {
                _forceFirstEventCargo = true;
                if (_nextEventAllowedUtc < nowUtc)
                {
                    float bootDelay = UnityEngine.Random.Range(8f, 18f);
                    _nextEventAllowedUtc = nowUtc.AddMinutes(bootDelay);
                    Puts($"[Events] Startup cooldown: first event no sooner than {bootDelay:F0} minutes (Cargo Ship reserved)");
                }
                else
                {
                    Puts("[Events] First event after cooldown will be Cargo Ship");
                }
            }

            // Event tick: default ~75s, clamp 45–180s (was 30–120)
            float tick = Mathf.Clamp(config.CheckIntervalSeconds, 45f, 180f);
            _eventTimer = timer.Every(tick, EventTick);
            _announceTimer = timer.Every(5f, ProcessAnnouncements);

            // Vehicle despawn owned exclusively by LiveStatsEventsVehicles (core inline removed)
            bool vehiclesModule = _registeredModules.Contains(ModuleVehicles) || LiveStatsEventsVehicles != null;
            if (vehiclesModule)
                Puts("[Events] Vehicle despawn owned by LiveStatsEventsVehicles module");
            else
                Puts("[Events] WARNING: LiveStatsEventsVehicles not loaded — vehicle events/despawn inactive");

            // Combined maintenance — slower tick reduces GC from world walks
            _cargoMonitorTimer?.Destroy();
            _cargoMonitorTimer = timer.Every(240f, RunMaintenanceTick);
            if (config.CargoShipLifecycle == null || config.CargoShipLifecycle.Enabled)
                Puts($"[Events] Cargo lifecycle monitor on (egress after {config.CargoShipLifecycle?.MaxEventMinutes ?? 55:F0}m, kill after egress + {config.CargoShipLifecycle?.ForceKillMinutesAfterEgress ?? 15:F0}m)");

            // Shared live-entity index — one world pass every ~3 min feeds counts / near-checks / despawn
            _liveIndexTimer?.Destroy();
            RebuildLiveIndex(force: true);
            ProtectExistingHackableCrates();
            _liveIndexTimer = timer.Every(LiveIndexMaxAge, () => RebuildLiveIndex(force: false));
            Puts($"[Events] Live index: incremental spawn/kill + sanity rebuild every {LiveIndexMaxAge:F0}s");

            CleanupStuckChinooks();

            // Night lights only on day↔night transition
            _nightLightsTimer?.Destroy();
            _lastNightLightsIsNight = null;
            _nightLightsTimer = timer.Every(480f, UpdateAllSpawnedNightLights); // 8 min

            var mod = config.Modules ?? new ModuleConfig();
            Puts($"[Events] Started (mode: {(config.UseScheduledEvents ? "Scheduled" : "Random")}, tick every {tick:F0}s, min players {config.MinPlayersOnline}, live-index {LiveIndexMaxAge:F0}s)");
            Puts($"[Events] Modules: RequireCompanion={mod.RequireCompanionPlugins}, Vehicles={mod.Vehicles}, NPC={mod.Npc}, registered=[{string.Join(",", _registeredModules)}]");
            if (config.MinPlayersOnline <= 0)
                Puts("[Events] WARNING: MinPlayersOnline is 0 — events will fire on an empty server (set to 2+ in config for lower CPU)");

            // Log enabled events so admins can verify the config is being read
            var enabled = config.Events?.Where(e => e.Enabled).Select(e => e.Type).ToList() ?? new List<string>();
            Puts($"[Events] Enabled events ({enabled.Count}): {string.Join(", ", enabled)}");
            if (config.F15Flyby != null && config.F15Flyby.Enabled)
                Puts("[Events] F15Flyby: Enabled");
            else
                Puts("[Events] F15Flyby: Disabled");
        }

        /// <summary>
        /// Single maintenance tick: cargo lifecycle, stuck chinooks, tracking dict prune.
        /// Avoids multiple overlapping full-world scans.
        /// </summary>
        private void RunMaintenanceTick()
        {
            if (!_ready) return;
            try { MonitorCargoShips(); } catch (Exception ex) { DebugLog($"MonitorCargoShips: {ex.Message}"); }
            try { CleanupStuckChinooks(); } catch (Exception ex) { DebugLog($"CleanupStuckChinooks: {ex.Message}"); }
            try { PruneStaleTracking(); } catch (Exception ex) { DebugLog($"PruneStaleTracking: {ex.Message}"); }
            try { PruneEventProtectedCrates(); } catch (Exception ex) { DebugLog($"PruneEventProtectedCrates: {ex.Message}"); }
            try { PruneRouteTimers(); } catch (Exception ex) { DebugLog($"PruneRouteTimers: {ex.Message}"); }
        }

        /// <summary>
        /// Drop dead netIds — only probes OUR keys via Find (O(tracked)), never builds a 75k-entity HashSet.
        /// </summary>
        private void PruneStaleTracking()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastTrackingPrune < 240f) return;
            _lastTrackingPrune = now;

            PruneDictByLookup(_vehicleFirstSeen);
            PruneDictByLookup(_cargoTracks);
            PruneHashByLookup(_trackedScientistIds);
            PruneHashByLookup(_pluginBradleyIds);
            PruneHashByLookup(_pluginCargoPlaneIds);
            PruneHashByLookup(_airfieldGuardIds);
            PruneHashByLookup(_eventProtectedCrateIds);
            PruneHashByLookup(_idxCargo);
            PruneHashByLookup(_idxChinook);
            PruneHashByLookup(_idxPatrolHeli);
            PruneHashByLookup(_idxBradley);
            PruneHashByLookup(_idxCargoPlane);
            PruneHashByLookup(_idxCrate);
            PruneHashByLookup(_idxAttackHeli);
            // Vehicle-module types: drop ghosts but do not force a world rebuild
            PruneHashByLookup(_idxTug);
            PruneHashByLookup(_idxSub);
            PruneHashByLookup(_idxBalloon);
            PruneHashByLookup(_idxMini);
            PruneHashByLookup(_idxScrap);

            if (_announcedCrateIds.Count > 16)
                _announcedCrateIds.Clear();
            if (_entityCountCache.Count > 24)
                _entityCountCache.Clear();

            if (_eventProtectedCrateUntil.Count > 0)
            {
                _pruneIdBuffer.Clear();
                foreach (var kv in _eventProtectedCrateUntil)
                {
                    if (kv.Value <= now || !_eventProtectedCrateIds.Contains(kv.Key))
                        _pruneIdBuffer.Add(kv.Key);
                }
                for (int i = 0; i < _pruneIdBuffer.Count; i++)
                    _eventProtectedCrateUntil.Remove(_pruneIdBuffer[i]);
                _pruneIdBuffer.Clear();
            }
        }

        /// <summary>Drop route timers whose vehicle is gone (shot down / despawned mid-route).</summary>
        private void PruneRouteTimers()
        {
            PruneTimerDict(_heliRouteTimers);
            PruneTimerDict(_heliAltitudeSmoothTimers);
            PruneTimerDict(_chinookTourTimers);
            PruneTimerDict(_bradleyRouteTimers);
        }

        private void PruneTimerDict(Dictionary<ulong, Timer> dict)
        {
            if (dict == null || dict.Count == 0) return;
            _pruneIdBuffer.Clear();
            foreach (var kv in dict)
            {
                if (!IsNetAlive(kv.Key))
                    _pruneIdBuffer.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
            {
                ulong id = _pruneIdBuffer[i];
                if (dict.TryGetValue(id, out var t))
                    t?.Destroy();
                dict.Remove(id);
            }
            _pruneIdBuffer.Clear();
        }

        private static bool IsLootCrateEntity(BaseEntity ent)
        {
            if (ent == null) return false;
            if (ent is HackableLockedCrate) return true;
            string s = (ent.ShortPrefabName ?? ent.PrefabName ?? "");
            if (s.IndexOf("crate", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (s.IndexOf("hackable", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void SafeKill(BaseEntity ent)
        {
            if (ent == null) return;
            try
            {
                if (ent.IsDestroyed) return;
                if (IsLootCrateEntity(ent)) return;
                ent.Kill();
            }
            catch { }
        }

        private bool IsNetAlive(ulong id)
        {
            try
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id));
                return ent != null && !ent.IsDestroyed;
            }
            catch
            {
                return false;
            }
        }

        private void PruneDictByLookup<T>(Dictionary<ulong, T> dict)
        {
            if (dict == null || dict.Count == 0) return;
            _pruneIdBuffer.Clear();
            foreach (var id in dict.Keys)
            {
                if (!IsNetAlive(id))
                    _pruneIdBuffer.Add(id);
            }
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
                dict.Remove(_pruneIdBuffer[i]);
            _pruneIdBuffer.Clear();
        }

        private void PruneHashByLookup(HashSet<ulong> set)
        {
            if (set == null || set.Count == 0) return;
            _pruneIdBuffer.Clear();
            foreach (var id in set)
            {
                if (!IsNetAlive(id))
                    _pruneIdBuffer.Add(id);
            }
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
                set.Remove(_pruneIdBuffer[i]);
            _pruneIdBuffer.Clear();
        }

        #endregion

        #region Core Event Logic

        private void LoadEventData()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(DataFileName);
                _lastEventTime.Clear();
                if (data == null) return;

                foreach (var kv in data)
                {
                    if (DateTime.TryParse(kv.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
                        _lastEventTime[kv.Key] = dt.ToUniversalTime();
                }
                Puts($"[Events] Loaded {_lastEventTime.Count} persisted event timestamps");
            }
            catch (Exception ex)
            {
                Puts($"[Events] Failed to load event data: {ex.Message}");
            }
        }

        private void SaveEventData()
        {
            try
            {
                var data = new Dictionary<string, string>();
                foreach (var kv in _lastEventTime)
                    data[kv.Key] = kv.Value.ToUniversalTime().ToString("o");
                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, data);
            }
            catch (Exception ex)
            {
                Puts($"[Events] Failed to save event data: {ex.Message}");
            }
        }

        private void EventTick()
        {
            if (!_ready || !config.Enabled) return;
            if (LiveStatsWorld == null) return;
            if (BasePlayer.activePlayerList == null || BasePlayer.activePlayerList.Count < config.MinPlayersOnline)
                return;

            DateTime nowUtc = DateTime.UtcNow;

            // Global spacing: do not start another event until the random gap has elapsed
            if (nowUtc < _nextEventAllowedUtc)
            {
                // Throttle skip spam — log at most once per ~60s
                double remain = (_nextEventAllowedUtc - nowUtc).TotalMinutes;
                if (!_loggedCooldownSkip || (Time.realtimeSinceStartup - _lastCooldownSkipLog) > 60f)
                {
                    DebugLog($"Event tick waiting on global cooldown ({remain:F1} min left)");
                    _loggedCooldownSkip = true;
                    _lastCooldownSkipLog = Time.realtimeSinceStartup;
                }
                return;
            }
            _loggedCooldownSkip = false;

            // Cache time-of-day once per tick (not once per event)
            float hour = GetCurrentHour();
            bool isNight = hour < 6f || hour >= 20f;
            bool isDeepNight = hour < 5f || hour >= 22f;
            float localHour = config.UseScheduledEvents ? GetLocalHour() : hour;
            bool anyFired = false;

            // Ensure weather snapshot is fresh once per tick if we need it
            EnsureWeatherCache();

            var events = config.Events;
            if (events == null || events.Count == 0) return;

            // —— First event after boot/reload is always Cargo Ship ——
            if (_forceFirstEventCargo)
            {
                if (TryForceFirstCargoShip(nowUtc))
                {
                    anyFired = true;
                    SaveEventData();
                }
                // Whether cargo spawned or was skipped (already exists / disabled),
                // clear the flag so the normal random schedule resumes next tick.
                // If spawn failed only because CanSpawn failed, keep trying until success or clear below.
                if (!_forceFirstEventCargo)
                {
                    if (config.F15Flyby != null && config.F15Flyby.Enabled)
                        TryF15Flyby();
                    return;
                }
                // Still forced (e.g. CargoShip event disabled) — fall through to normal
            }

            // Shuffle order each tick so the same high-weight event does not always win the slot
            var order = new List<int>(events.Count);
            for (int i = 0; i < events.Count; i++) order.Add(i);
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                int tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
            }

            for (int oi = 0; oi < order.Count; oi++)
            {
                var evt = events[order[oi]];
                if (evt == null || !evt.Enabled) continue;

                string key = evt.Type != null ? evt.Type.ToLowerInvariant() : "";
                if (!_lastEventTime.TryGetValue(key, out DateTime last))
                    last = DateTime.MinValue;
                double minutesSince = last == DateTime.MinValue ? 99999.0 : (nowUtc - last).TotalMinutes;

                bool shouldFire = false;

                if (config.UseScheduledEvents && evt.Schedule != null && evt.Schedule.Count > 0)
                {
                    foreach (var window in evt.Schedule)
                    {
                        if (IsInWindow(localHour, window.StartHour, window.EndHour))
                        {
                            float minGap = Mathf.Max(30f, evt.MinIntervalMinutes);
                            if (minutesSince >= minGap)
                            {
                                shouldFire = true;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    float minInterval = Mathf.Max(10f, evt.MinIntervalMinutes);
                    float maxInterval = Mathf.Max(minInterval + 1f, evt.MaxIntervalMinutes);

                    if (minutesSince < minInterval) continue;

                    float waited = (float)minutesSince;
                    float chance = Mathf.Clamp01((waited - minInterval) / (maxInterval - minInterval));
                    chance *= evt.Weight;
                    chance *= GetTimeOfDayChanceMultiplier(key, isNight, isDeepNight);
                    chance *= GetWeatherModifierCached(key);
                    chance *= GetWipeDayChanceMultiplier(key);

                    // Starvation: events not seen for a long time get a real chance to fire
                    // (16+ event pool + global gap made rare types wait 8–12h otherwise)
                    float starveMul = 1f;
                    if (waited >= 360f) starveMul = 4.5f;       // 6h+
                    else if (waited >= 240f) starveMul = 3f;     // 4h+
                    else if (waited >= 180f) starveMul = 2f;     // 3h+
                    chance *= starveMul;

                    // Base roll scale (was 0.08 — too harsh with large event pool)
                    float rollScale = 0.12f;
                    if (UnityEngine.Random.value < chance * rollScale)
                        shouldFire = true;
                }

                if (shouldFire)
                {
                    if (!TriggerEvent(evt))
                    {
                        DebugLog($"{evt.Type} skipped - cannot spawn (no announce, no cooldown)");
                        continue;
                    }

                    _lastEventTime[key] = nowUtc;
                    anyFired = true;

                    // Hold the slot through buildup so a 3m announce cannot free
                    // the cadence before this event actually hits the map.
                    float hold = Mathf.Max(0f, evt.AnnounceMinutesBefore);
                    ScheduleNextEventGap(nowUtc.AddMinutes(hold));
                    break;
                }
            }

            // Single disk write per tick if anything fired (was once per event)
            if (anyFired)
                SaveEventData();

            if (config.F15Flyby != null && config.F15Flyby.Enabled)
                TryF15Flyby();
        }

        /// <summary>
        /// After an event fires, block further events for a random duration in [Min, Max] minutes.
        /// </summary>
        private void ScheduleNextEventGap(DateTime fromUtc)
        {
            float minM = config != null ? Mathf.Max(8f, config.MinMinutesBetweenAnyEvent) : 8f;
            float maxM = config != null ? Mathf.Max(minM, config.MaxMinutesBetweenAnyEvent) : 20f;
            float gapMinutes = UnityEngine.Random.Range(minM, maxM);
            _nextEventAllowedUtc = fromUtc.AddMinutes(gapMinutes);
            DebugLog($"Global event cooldown: next event allowed in {gapMinutes:F1} minutes (at {_nextEventAllowedUtc:u})");
        }

        /// <summary>
        /// Force Cargo Ship as the first world event after boot/reload.
        /// Returns true if an event was triggered (announce or spawn).
        /// Clears _forceFirstEventCargo when cargo is handled or cannot run.
        /// </summary>
        private bool TryForceFirstCargoShip(DateTime nowUtc)
        {
            EventDefinition cargoEvt = null;
            if (config?.Events != null)
            {
                foreach (var e in config.Events)
                {
                    if (e == null || !e.Enabled) continue;
                    string t = (e.Type ?? "").ToLowerInvariant();
                    if (t == "cargoship" || t == "cargo")
                    {
                        cargoEvt = e;
                        break;
                    }
                }
            }

            if (cargoEvt == null)
            {
                Puts("[Events] First-event Cargo Ship skipped — event disabled or missing in config");
                _forceFirstEventCargo = false;
                return false;
            }

            if (!CanSpawnEvent(cargoEvt.Type))
            {
                Puts("[Events] First-event Cargo Ship skipped — already on the map. Opening the random pool (no extra gap).");
                _forceFirstEventCargo = false;
                return false;
            }

            if (!TriggerEvent(cargoEvt))
            {
                DebugLog("First-event Cargo Ship trigger aborted — will retry next tick");
                return false; // keep _forceFirstEventCargo true
            }

            _lastEventTime["cargoship"] = nowUtc;
            _forceFirstEventCargo = false;
            ScheduleNextEventGap(nowUtc);
            Puts("[Events] First event: Cargo Ship (forced after startup)");
            DebugLog("First event forced -> CargoShip");
            return true;
        }

        private float GetTimeOfDayChanceMultiplier(string key, bool isNight, bool isDeepNight)
        {
            switch (key)
            {
                case "bradley":
                case "tank":
                    return isNight ? 3.5f : 0.35f;
                case "patrolheli":
                case "heli":
                case "patrolhelicopter":
                    return isNight ? 1.8f : 0.75f;
                case "chinook":
                case "ch47":
                    return isNight ? 1.5f : 0.85f;
                case "cargoship":
                case "cargo":
                    return isDeepNight ? 0.45f : (isNight ? 0.75f : 1.15f);
                case "supplydrop":
                case "airdrop":
                    return isDeepNight ? 0.50f : (isNight ? 0.80f : 1.20f);
                case "attackheli":
                case "attackhelicopter":
                    return isNight ? 2.0f : 0.7f;
                case "heavyscientists":
                case "heavies":
                    return isNight ? 2.2f : 0.6f;
                case "tugboat":
                case "submarine":
                case "sub":
                    return isDeepNight ? 0.5f : (isNight ? 0.75f : 1.2f);
                case "hotairballoon":
                case "balloon":
                    return isNight ? 0.4f : 1.3f;
                case "hackablecrate":
                case "lockedcrate":
                    return isNight ? 1.3f : 1.0f;
                case "roadambush":
                case "ambush":
                    return isNight ? 2.0f : 0.7f;
                case "tunnelsquad":
                case "tunnel":
                    return isNight ? 1.8f : 0.8f;
                case "subwaypatrol":
                case "subway":
                case "metro":
                    return isNight ? 1.9f : 0.75f;
                case "mineguard":
                case "mine":
                case "mines":
                case "caveguard":
                    return isNight ? 2.0f : 0.7f;
                case "peacekeeperpatrol":
                case "peacekeepers":
                case "peacekeeper":
                    return isNight ? 0.5f : 1.4f;
                case "airfieldchinook":
                case "airfieldch47":
                case "stealchinook":
                case "stealinook":
                {
                    float h = 12f;
                    try
                    {
                        if (TOD_Sky.Instance != null && TOD_Sky.Instance.Cycle != null)
                            h = TOD_Sky.Instance.Cycle.Hour;
                    }
                    catch { }
                    var ac = config?.AirfieldChinook;
                    float dawnA = ac?.SunriseStartHour ?? 5.0f;
                    float dawnB = ac?.SunriseEndHour ?? 7.5f;
                    float duskA = ac?.SunsetStartHour ?? 18.0f;
                    float duskB = ac?.SunsetEndHour ?? 20.5f;
                    if ((h >= dawnA && h <= dawnB) || (h >= duskA && h <= duskB))
                        return 3.2f;
                    return 0f;
                }
                case "baseraid":
                case "npcraid":
                case "raid":
                {
                    // Strong preference for dusk & dawn; moderate at night; low during full day
                    float h = 12f;
                    try
                    {
                        if (TOD_Sky.Instance != null && TOD_Sky.Instance.Cycle != null)
                            h = TOD_Sky.Instance.Cycle.Hour;
                    }
                    catch { }
                    if ((h >= 5.0f && h <= 7.5f) || (h >= 18.0f && h <= 20.5f))
                        return 2.8f;
                    if (isNight) return 1.35f;
                    return 0.40f;
                }
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// Wipe-day scaling for late-stage pressure events.
        /// BaseRaid: gated until MinWipeDay, then ramps Min->Peak chance.
        /// PatrolHeli / F-15E: always eligible, milder early, stronger at PeakWipeDay.
        /// </summary>
        private float GetWipeDayChanceMultiplier(string key)
        {
            key = (key ?? "").ToLowerInvariant();

            bool isBaseRaid = key == "baseraid" || key == "npcraid" || key == "raid";
            bool isPatrolHeli = key == "patrolheli" || key == "heli" || key == "patrolhelicopter";
            bool isF15 = key == "f15" || key == "flyby" || key == "f15e";

            if (!isBaseRaid && !isPatrolHeli && !isF15)
                return 1.0f;

            var cfg = config?.BaseRaid;
            int wipeDay = GetWipeDayFromData();
            // Shared peak day (default 28). BaseRaid also has its own MinWipeDay gate.
            int peakDay = Mathf.Max(2, cfg?.PeakWipeDay ?? 28);

            if (isBaseRaid)
            {
                if (cfg == null || !cfg.Enabled) return 0f;
                int minDay = Mathf.Max(1, cfg.MinWipeDay);
                if (wipeDay < minDay) return 0f;

                float t = Mathf.Clamp01((float)(wipeDay - minDay) / Mathf.Max(1, peakDay - minDay));
                t = t * t * (3f - 2f * t); // smoothstep
                float minChance = Mathf.Clamp(cfg.ChanceAtMinWipeDay, 0.05f, 1f);
                float maxChance = Mathf.Clamp(cfg.ChanceAtPeakWipeDay, minChance, 3f);
                return Mathf.Lerp(minChance, maxChance, t);
            }

            // Patrol Heli & F-15E: available all wipe, ramp from early -> peak
            // Day 1 ≈ low multiplier, PeakWipeDay+ ≈ high multiplier
            float early = isPatrolHeli ? 0.70f : 0.60f;
            float peak = isPatrolHeli ? 1.65f : 1.85f;
            if (cfg != null)
            {
                // Optional overrides if present on BaseRaid-style late-wipe pressure
                if (isPatrolHeli)
                {
                    early = Mathf.Clamp(cfg.PatrolHeliChanceEarly, 0.2f, 1.5f);
                    peak = Mathf.Clamp(cfg.PatrolHeliChancePeak, early, 3f);
                }
                else
                {
                    early = Mathf.Clamp(cfg.F15ChanceEarly, 0.2f, 1.5f);
                    peak = Mathf.Clamp(cfg.F15ChancePeak, early, 3f);
                }
            }

            float u = Mathf.Clamp01((float)(wipeDay - 1) / Mathf.Max(1, peakDay - 1));
            u = u * u * (3f - 2f * u); // smoothstep — slow early, steeper late
            return Mathf.Lerp(early, peak, u);
        }

        /// <summary>
        /// Returns false when an event cannot spawn due to existing entities / vehicle limits.
        /// Checked before TriggerEvent so we never announce or broadcast a success message for a blocked event.
        /// </summary>

        private static bool CoerceBool(object r, bool defaultIfUnknown)
        {
            if (r == null) return defaultIfUnknown;
            if (r is bool b) return b;
            if (r is int n) return n != 0;
            if (r is long l) return l != 0;
            string s = r.ToString();
            if (string.Equals(s, "True", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
            if (string.Equals(s, "False", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            return defaultIfUnknown;
        }

        private bool ModuleCanSpawn(Plugin mod, string apiType, bool failOpen)
        {
            if (mod == null) return failOpen;
            try
            {
                var r = mod.Call("API_CanSpawn", apiType);
                bool ok = CoerceBool(r, failOpen);
                DebugLog($"module API_CanSpawn {apiType} -> {r} ({(r == null ? "null" : r.GetType().Name)}) ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                DebugLog($"module API_CanSpawn {apiType} threw {ex.GetType().Name} — failOpen={failOpen}");
                return failOpen;
            }
        }

        private bool CanSpawnEvent(string type)
        {
            type = (type ?? "").ToLowerInvariant();
            if (!IsEventTypeAllowed(type))
            {
                DebugLog($"{type} blocked by module gates (Vehicles/NPC companion or Modules config)");
                return false;
            }
            switch (type)
            {
                case "cargoship":
                case "cargo":
                    return CountCargoShips() <= 0;

                case "patrolheli":
                case "heli":
                case "patrolhelicopter":
                    return CountPatrolHelis() <= 0;

                case "bradley":
                case "tank":
                    return CountBradleys() <= 0;

                case "chinook":
                case "ch47":
                {
                    int before = CountChinooks();
                    int max = config.VehicleLimits?.MaxChinooks ?? 1;
                    return before < max;
                }

                case "attackheli":
                case "attackhelicopter":
                {
                    int before = CountAttackHelis();
                    int max = config.VehicleLimits?.MaxAttackHelis ?? 1;
                    if (config.AttackHeliSpawn != null && config.AttackHeliSpawn.AllowMultiple)
                        max = Mathf.Max(max, 3);
                    return before < max;
                }

                case "tugboat":
                    return ModuleCanSpawn(LiveStatsEventsVehicles, "tugboat", true);

                case "submarine":
                case "sub":
                    return ModuleCanSpawn(LiveStatsEventsVehicles, "submarine", true);

                case "scrapheli":
                case "scraptransport":
                case "scraptransporthelicopter":
                    return ModuleCanSpawn(LiveStatsEventsVehicles, "scrapheli", true);

                case "minicopter":
                case "mini":
                    return ModuleCanSpawn(LiveStatsEventsVehicles, "minicopter", true);

                case "airfieldchinook":
                case "airfieldch47":
                case "stealchinook":
                case "stealinook":
                {
                    if (config?.AirfieldChinook != null && !config.AirfieldChinook.Enabled)
                        return false;
                    if (!TryFindAirfield(out _))
                    {
                        DebugLog("AirfieldChinook skipped — no airfield on this map");
                        return false;
                    }
                    int maxCh47 = Mathf.Max(1, config?.AirfieldChinook?.MaxInWorld ?? 1);
                    int liveCh47 = CountPlayerChinooks();
                    if (liveCh47 >= maxCh47)
                    {
                        DebugLog($"AirfieldChinook blocked — player CH47s in world {liveCh47} >= MaxInWorld {maxCh47}");
                        return false;
                    }
                    int wipeDay = GetWipeDayFromData();
                    int minDay = config?.AirfieldChinook?.MinWipeDay ?? 10;
                    int maxDay = config?.AirfieldChinook?.MaxWipeDay ?? 24;
                    if (wipeDay > 0 && (wipeDay < minDay || wipeDay > maxDay))
                        return false;
                    int need = config?.AirfieldChinook?.MinPlayersOnline ?? 1;
                    if ((BasePlayer.activePlayerList?.Count ?? 0) < need)
                        return false;
                    return true;
                }

                case "hotairballoon":
                case "balloon":
                    return false;

                case "heavyscientists":
                case "heavies":
                case "heavyscientific":
                case "roadambush":
                case "ambush":
                case "tunnelsquad":
                case "tunnel":
                case "subwaypatrol":
                case "subway":
                case "metro":
                case "mineguard":
                case "mine":
                case "mines":
                case "caveguard":
                case "railpatrol":
                case "rail":
                case "trainpatrol":
                case "surfacerail":
                case "peacekeeperpatrol":
                case "peacekeepers":
                case "peacekeeper":
                case "baseraid":
                case "npcraid":
                case "raid":
                    return ModuleCanSpawn(LiveStatsEventsNPC, type, true);

                default:
                    return true; // no hard vehicle/entity limit
            }
        }

        /// <summary>
        /// Returns true only when the event was committed (spawned now, or announce + delayed spawn armed).
        /// Never announces if CanSpawnEvent is false.
        /// </summary>
        private bool TriggerEvent(EventDefinition evt)
        {
            if (evt == null || string.IsNullOrEmpty(evt.Type)) return false;

            // Must pass before any player-facing message
            if (!CanSpawnEvent(evt.Type))
            {
                DebugLog($"{evt.Type} skipped in TriggerEvent - limit/exists reached (no announce)");
                return false;
            }

            string eventType = evt.Type;
            float delay = Mathf.Max(0f, evt.AnnounceMinutesBefore) * 60f;

            // Immediate spawn path — no buildup
            if (delay <= 0.5f)
            {
                if (!SpawnEvent(eventType))
                {
                    DebugLog($"{eventType} spawn failed immediately (no announce)");
                    return false;
                }
                BroadcastLocalized(uid => GetSpawnMessage(eventType, uid));
                NotifyEventFeed(eventType, "spawn");
                return true;
            }

            // Build-up announcements (only after can-spawn passed)
            int mins = Mathf.Max(1, Mathf.RoundToInt(evt.AnnounceMinutesBefore));
            ScheduleBuildUp(eventType, mins, Time.realtimeSinceStartup);
            if (mins >= 5)
                ScheduleBuildUp(eventType, 2, Time.realtimeSinceStartup + (mins - 2) * 60f);

            // Actual spawn after delay — re-check; cancel publicly if no longer possible
            timer.Once(delay, () =>
            {
                if (!CanSpawnEvent(eventType))
                {
                    DebugLog($"{eventType} cancelled at spawn time - limit/exists during announce delay");
                    ClearPendingAnnouncements(eventType);
                    BroadcastLocalized(uid => GetCancelMessage(eventType, uid));
                    return;
                }

                if (SpawnEvent(eventType))
                {
                    BroadcastLocalized(uid => GetSpawnMessage(eventType, uid));
                    NotifyEventFeed(eventType, "spawn");
                    ScheduleNextEventGap(DateTime.UtcNow);
                }
                else
                {
                    DebugLog($"{eventType} spawn failed after announce");
                    ClearPendingAnnouncements(eventType);
                    BroadcastLocalized(uid => GetCancelMessage(eventType, uid));
                }
            });

            return true;
        }

        private void ClearPendingAnnouncements(string eventType)
        {
            if (string.IsNullOrEmpty(eventType) || _pendingAnnouncements.Count == 0) return;
            string key = eventType.ToLowerInvariant();
            for (int i = _pendingAnnouncements.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_pendingAnnouncements[i].EventType, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_pendingAnnouncements[i].EventType, eventType, StringComparison.OrdinalIgnoreCase))
                    _pendingAnnouncements.RemoveAt(i);
            }
        }

        private string GetCancelMessage(string type, string userId = null)
        {
            string name = GetEventDisplayName(type, userId);
            string t = (type ?? "").ToLowerInvariant();
            if (t == "baseraid" || t == "raid" || t == "baseraidsquad")
                return Msg("EventCancelledNoTarget", userId, name);
            return Msg("EventCancelled", userId, name);
        }

        /// <summary>
        /// Attempts to spawn the event. Returns false when the spawn was blocked (limit/exists)
        /// or failed hard; true when the spawn path ran.
        /// </summary>
        private bool SpawnEvent(string type)
        {
            type = type.ToLower();
            try
            {
                switch (type)
                {
                    case "supplydrop":
                    case "airdrop":
                        SpawnSupplyDrop();
                        return true;
                    case "cargoship":
                    case "cargo":
                        return SpawnCargoShip();
                    case "patrolheli":
                    case "heli":
                    case "patrolhelicopter":
                        return SpawnPatrolHeli();
                    case "chinook":
                    case "ch47":
                        return SpawnChinook();
                    case "bradley":
                    case "tank":
                        return SpawnBradley();
                    case "attackheli":
                    case "attackhelicopter":
                        return SpawnAttackHeli();
                    case "tugboat":
                        return TryModuleSpawn(ModuleVehicles, "tugboat") ?? false;
                    case "hotairballoon":
                    case "balloon":
                        DebugLog("HotAirBalloon spawn event disabled — despawn-only");
                        return false;
                    case "heavyscientists":
                    case "heavyscientific":
                    case "heavies":
                        return TryModuleSpawn(ModuleNpc, "heavyscientists") ?? false;
                    case "hackablecrate":
                    case "lockedcrate":
                    case "hackcrate":
                        SpawnHackableCrate();
                        return true;
                    case "submarine":
                    case "sub":
                        return TryModuleSpawn(ModuleVehicles, "submarine") ?? false;
                    case "scrapheli":
                    case "scraptransport":
                    case "scraptransporthelicopter":
                        return TryModuleSpawn(ModuleVehicles, "scrapheli") ?? false;
                    case "minicopter":
                    case "mini":
                        return TryModuleSpawn(ModuleVehicles, "minicopter") ?? false;
                    case "roadambush":
                    case "ambush":
                        return TryModuleSpawn(ModuleNpc, "roadambush") ?? false;
                    case "tunnelsquad":
                    case "tunnel":
                        return TryModuleSpawn(ModuleNpc, "tunnelsquad") ?? false;
                    case "subwaypatrol":
                    case "subway":
                    case "metro":
                        return TryModuleSpawn(ModuleNpc, "subwaypatrol") ?? false;
                    case "mineguard":
                    case "mine":
                    case "mines":
                    case "caveguard":
                        return TryModuleSpawn(ModuleNpc, "mineguard") ?? false;
                    case "railpatrol":
                    case "rail":
                    case "trainpatrol":
                    case "surfacerail":
                        return TryModuleSpawn(ModuleNpc, "railpatrol") ?? false;
                    case "peacekeeperpatrol":
                    case "peacekeepers":
                    case "peacekeeper":
                        return TryModuleSpawn(ModuleNpc, "peacekeeperpatrol") ?? false;
                    case "baseraid":
                    case "npcraid":
                    case "raid":
                        return TryModuleSpawn(ModuleNpc, "baseraid") ?? false;
                    case "f15":
                    case "flyby":
                        DoF15Flyby();
                        return true;
                    case "airfieldchinook":
                    case "airfieldch47":
                    case "stealchinook":
                    case "stealinook":
                        return SpawnAirfieldChinook();
                    default:
                        Puts($"[Events] Unknown event type: {type}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Puts($"[Events] Failed to spawn {type}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Vanilla Event Spawns

        // Robust spawn helpers with verification + optional debug logging

        private void DebugLog(string message)
        {
            // Gate before any caller-side work when possible; message is still allocated by $"" callers
            if (config != null && config.Debug)
                Puts($"[Events][Debug] {message}");
        }

        private void ClearLiveIndex()
        {
            _idxCargo.Clear();
            _idxChinook.Clear();
            _idxPatrolHeli.Clear();
            _idxBradley.Clear();
            _idxTug.Clear();
            _idxSub.Clear();
            _idxBalloon.Clear();
            _idxCargoPlane.Clear();
            _idxAttackHeli.Clear();
            _idxMini.Clear();
            _idxScrap.Clear();
            _idxCrate.Clear();
            _liveIndexBuiltAt = -999f;
        }

        /// <summary>
        /// Single full-world pass that fills the live netId index. Most Count* / IsNear*
        /// helpers then use O(tracked) Find lookups instead of O(world) scans.
        /// </summary>
        private void RebuildLiveIndex(bool force = false)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && (now - _liveIndexBuiltAt) < LiveIndexMaxAge * 0.9f)
                return;

            _idxCargo.Clear();
            _idxChinook.Clear();
            _idxPatrolHeli.Clear();
            _idxBradley.Clear();
            _idxTug.Clear();
            _idxSub.Clear();
            _idxBalloon.Clear();
            _idxCargoPlane.Clear();
            _idxAttackHeli.Clear();
            _idxMini.Clear();
            _idxScrap.Clear();
            _idxCrate.Clear();

            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null || be.IsDestroyed) continue;
                    string prefab = be.ShortPrefabName;
                    if (string.IsNullOrEmpty(prefab)) continue;
                    ulong id = 0;
                    try { id = be.net?.ID.Value ?? 0; } catch { }
                    if (id == 0) continue;
                    IndexLiveEntity(id, prefab, be);
                }
            }
            catch (Exception ex)
            {
                if (config != null && config.Debug)
                    Puts($"[Events][Debug] RebuildLiveIndex: {ex.Message}");
            }

            _liveIndexBuiltAt = now;
            SyncLiveIndexCountCache(now);
        }

        /// <summary>Add one netId to the correct live-index set. Used by rebuild and OnEntitySpawned.</summary>
        private void IndexLiveEntity(ulong id, string prefab, BaseEntity be)
        {
            if (id == 0 || string.IsNullOrEmpty(prefab)) return;

                    if (prefab.IndexOf("cargoshiptest.prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (prefab.IndexOf("cargoship", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0 &&
                         prefab.IndexOf("worldmodel", StringComparison.OrdinalIgnoreCase) < 0 &&
                         prefab.IndexOf("marker", StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        if (IsLiveCargoShip(be))
                            _idxCargo.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("ch47scientists", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxChinook.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0 &&
                        prefab.IndexOf("marker", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxPatrolHeli.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("bradleyapc", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxBradley.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("cargo_plane", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prefab.IndexOf("cargoplane", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _idxCargoPlane.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("attackhelicopter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxAttackHeli.Add(id);
                        return;
                    }
                    if (IsHotAirBalloonPrefab(prefab))
                    {
                        _idxBalloon.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("minicopter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxMini.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("scraptransport", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxScrap.Add(id);
                        return;
                    }
                    if ((prefab.IndexOf("codelockedhackablecrate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prefab.IndexOf("hackablecrate", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        prefab.IndexOf("signal", StringComparison.OrdinalIgnoreCase) < 0 &&
                        prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        _idxCrate.Add(id);
                        return;
                    }
                    // Root tug / sub only
                    if (prefab.IndexOf("subents", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prefab.IndexOf("/seats/", StringComparison.OrdinalIgnoreCase) >= 0)
                        return;
                    if ((prefab.IndexOf("tugboat.prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prefab.IndexOf("tugboat.entity", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _idxTug.Add(id);
                        return;
                    }
                    if (prefab.IndexOf("submarinesolo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prefab.IndexOf("submarineduo", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _idxSub.Add(id);
                    }
        }

        private void SyncLiveIndexCountCache(float now)
        {
            _entityCountCache["cargoship_live"] = new CachedCount { Count = _idxCargo.Count, Time = now };
            _entityCountCache["ch47scientists_live"] = new CachedCount { Count = _idxChinook.Count, Time = now };
            _entityCountCache["patrolhelicopter_live"] = new CachedCount { Count = _idxPatrolHeli.Count, Time = now };
            _entityCountCache["bradleyapc_live"] = new CachedCount { Count = _idxBradley.Count, Time = now };
            _entityCountCache["tugboat_root"] = new CachedCount { Count = _idxTug.Count, Time = now };
            _entityCountCache["submarine_root"] = new CachedCount { Count = _idxSub.Count, Time = now };
            _entityCountCache["hotairballoon_live"] = new CachedCount { Count = _idxBalloon.Count, Time = now };
            _entityCountCache["cargo_plane"] = new CachedCount { Count = _idxCargoPlane.Count, Time = now };
            _entityCountCache["attackhelicopter_live"] = new CachedCount { Count = _idxAttackHeli.Count, Time = now };
            _entityCountCache["minicopter_live"] = new CachedCount { Count = _idxMini.Count, Time = now };
            _entityCountCache["scraptransport_live"] = new CachedCount { Count = _idxScrap.Count, Time = now };
            _entityCountCache["hackablecrate_live"] = new CachedCount { Count = _idxCrate.Count, Time = now };
        }

        private void EnsureLiveIndex()
        {
            if ((Time.realtimeSinceStartup - _liveIndexBuiltAt) >= LiveIndexMaxAge)
                RebuildLiveIndex(force: true);
        }

        /// <summary>Count live entities from the shared index (rebuilds if stale).</summary>
        private int CountFromIndex(HashSet<ulong> set)
        {
            EnsureLiveIndex();
            if (set == null || set.Count == 0) return 0;
            // Opportunistic prune of dead ids without a full world scan
            if (set.Count <= 12)
            {
                _pruneIdBuffer.Clear();
                foreach (var id in set)
                {
                    if (!IsNetAlive(id))
                        _pruneIdBuffer.Add(id);
                }
                for (int i = 0; i < _pruneIdBuffer.Count; i++)
                    set.Remove(_pruneIdBuffer[i]);
                _pruneIdBuffer.Clear();
            }
            return set.Count;
        }

        private BaseEntity FindIndexedEntity(ulong id)
        {
            try
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent != null && !ent.IsDestroyed) return ent;
            }
            catch { }
            return null;
        }


        private int CountPrefabsContaining(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return 0;

            // Prefer shared index for high-traffic event prefabs
            if (fragment.IndexOf("cargo_plane", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fragment.IndexOf("cargoplane", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxCargoPlane);
            if (fragment.IndexOf("ch47scientists", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxChinook);
            if (fragment.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxPatrolHeli);
            if (fragment.IndexOf("bradleyapc", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxBradley);
            if (fragment.IndexOf("cargoship", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxCargo);
            if (fragment.IndexOf("minicopter", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxMini);
            if (fragment.IndexOf("scraptransport", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxScrap);
            if (fragment.IndexOf("hackablecrate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fragment.IndexOf("codelockedhackablecrate", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxCrate);
            if (fragment.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxTug);
            if (fragment.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxSub);
            if (fragment.IndexOf("hotairballoon", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxBalloon);
            if (fragment.IndexOf("attackhelicopter", StringComparison.OrdinalIgnoreCase) >= 0)
                return CountFromIndex(_idxAttackHeli);

            float now = Time.realtimeSinceStartup;
            if (_entityCountCache.TryGetValue(fragment, out var cached) && (now - cached.Time) < EntityCountCacheSeconds)
                return cached.Count;

            int count = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                string prefab = be.ShortPrefabName ?? be.PrefabName;
                if (prefab == null) continue;
                if (prefab.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    count++;
            }

            _entityCountCache[fragment] = new CachedCount { Count = count, Time = now };
            return count;
        }

        /// <summary>
        /// True for the root hot-air-balloon prefab (not gibs/effects).
        /// Kept in core for live-index classification and boot purge only — spawn is disabled.
        /// </summary>
        private static bool IsHotAirBalloonPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            if (prefab.IndexOf("hotairballoon", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }


        /// <summary>
        /// Root submarine hull only — subents (fuel/torpedo/item storage, seats) also contain "submarine".
        /// </summary>

        /// <summary>
        /// Root tugboat only — privilege/fuel/storage subents also contain "tugboat".
        /// </summary>

        /// <summary>
        /// Counts live CH47 scientist chinooks only (not empty ch47.entity).
        /// </summary>
        private int CountChinooks()
        {
            return CountFromIndex(_idxChinook);
        }

        private void InvalidateEntityCache(string fragment = null)
        {
            if (string.IsNullOrEmpty(fragment))
            {
                _entityCountCache.Clear();
                // Force next EnsureLiveIndex to rebuild
                _liveIndexBuiltAt = -999f;
            }
            else
            {
                _entityCountCache.Remove(fragment);
                if (fragment.IndexOf("ch47", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("ch47scientists_live");
                if (fragment.IndexOf("cargo", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("cargoship_live");
                if (fragment.IndexOf("hotair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fragment.IndexOf("balloon", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("hotairballoon_live");
                if (fragment.IndexOf("bradley", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("bradleyapc_live");
                if (fragment.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fragment.IndexOf("patrolheli", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("patrolhelicopter_live");
                if (fragment.IndexOf("attackhelicopter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fragment.IndexOf("attackheli", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("attackhelicopter_live");
                if (fragment.IndexOf("tugboat", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("tugboat_root");
                if (fragment.IndexOf("submarine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fragment.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0)
                    _entityCountCache.Remove("submarine_root");
                // Partial invalidation still ages the index so next count is accurate
                _liveIndexBuiltAt = Mathf.Min(_liveIndexBuiltAt, Time.realtimeSinceStartup - LiveIndexMaxAge);
            }
        }

        private void DebugListMatching(string fragment)
        {
            if (config == null || !config.Debug) return;

            int shown = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                string prefab = be.ShortPrefabName ?? be.PrefabName ?? "";
                if (prefab.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Vector3 p = be.transform.position;
                Puts($"[Events][Debug] FOUND {prefab} at ({p.x:F0}, {p.y:F0}, {p.z:F0}) netid={be.net?.ID}");
                shown++;
                if (shown >= 8) break;
            }
            if (shown == 0)
                Puts($"[Events][Debug] No live entities matching '{fragment}'");
        }

        private bool TrySpawnPrefab(string fullPath, Vector3 pos)
        {
            try
            {
                var entity = GameManager.server.CreateEntity(fullPath, pos);
                if (entity == null)
                {
                    DebugLog($"CreateEntity returned null for {fullPath}");
                    return false;
                }
                entity.Spawn();
                DebugLog($"CreateEntity+Spawn OK: {fullPath} at ({pos.x:F0},{pos.y:F0},{pos.z:F0})");
                InvalidateEntityCache(); // counts changed

                // Start idle / lifetime clocks for vehicles, crates, and scientist NPCs we spawn
                string pathLower = (fullPath ?? "").ToLowerInvariant();
                bool isPlayerVehicle =
                    pathLower.Contains("tugboat") || pathLower.Contains("hotairballoon") ||
                    pathLower.Contains("minicopter") || pathLower.Contains("scraptransport") ||
                    pathLower.Contains("submarine") || pathLower.Contains("attackhelicopter");
                if (isPlayerVehicle ||
                    pathLower.Contains("hackablecrate") || pathLower.Contains("codelockedhackablecrate") ||
                    pathLower.Contains("scientistnpc"))
                {
                    // Notify Vehicles module so its idle-despawn will not kill Core inline event spawns
                    if (isPlayerVehicle)
                        NotifyVehiclesModuleProtect(entity, 120f);
                }

                // Night lights for anything with lamps / searchlights / headlights
                if (EntityHasLights(pathLower))
                {
                    ApplyEntityNightLights(entity);
                    timer.Once(2f, () => ApplyEntityNightLights(entity));
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"CreateEntity exception for {fullPath}: {ex.Message}");
                return false;
            }
        }

        private void TryConsoleSpawn(string shortName)
        {
            DebugLog($"Running: spawn {shortName}");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, $"spawn {shortName}");
            InvalidateEntityCache();
        }

        private void SpawnSupplyDrop()
        {
            int before = CountPrefabsContaining("cargo_plane");
            DebugLog($"SupplyDrop start - cargo planes before: {before}");

            // Prefer CreateEntity with an inland drop target — bare "spawn cargo_plane"
            // lands at (0,0,0) with "Look rotation viewing vector is zero" and can park
            // the plane at extreme off-map coords.
            if (!TrySpawnCargoPlane())
            {
                DebugLog("SupplyDrop CreateEntity path failed — console fallback");
                TryConsoleSpawn("cargo_plane");
            }

            if (config != null && config.Debug)
            {
                timer.Once(3f, () =>
                {
                    InvalidateEntityCache("cargo_plane");
                    int after = CountPrefabsContaining("cargo_plane");
                    DebugLog($"SupplyDrop check - cargo planes after: {after} (was {before})");
                    DebugListMatching("cargo_plane");
                });
            }
        }

        /// <summary>
        /// Spawn cargo plane with a real inland drop position (CargoPlane.InitDropPosition when present).
        /// </summary>
        private bool TrySpawnCargoPlane()
        {
            try
            {
                float half = 2000f;
                try { half = TerrainMeta.Size.x * 0.5f; } catch { }

                // Drop over inland playable area
                Vector3 drop = Vector3.zero;
                for (int i = 0; i < 12; i++)
                {
                    float x = UnityEngine.Random.Range(-half * 0.65f, half * 0.65f);
                    float z = UnityEngine.Random.Range(-half * 0.65f, half * 0.65f);
                    float y = 0f;
                    try { y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0f, z)); } catch { }
                    if (y < -5f) continue;
                    drop = new Vector3(x, y, z);
                    break;
                }
                if (drop.sqrMagnitude < 1f)
                    drop = new Vector3(UnityEngine.Random.Range(-200f, 200f), 20f, UnityEngine.Random.Range(-200f, 200f));

                // Start outside the rim on a line that crosses the drop
                Vector3 flat = new Vector3(drop.x, 0f, drop.z);
                if (flat.sqrMagnitude < 1f) flat = Vector3.forward;
                flat.Normalize();
                // Approach from a random side so the path isn't always the same radial line
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector3 approach = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 start = approach * (half + 400f);
                start.y = 750f;

                const string prefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
                var entity = GameManager.server.CreateEntity(prefab, start, Quaternion.LookRotation((drop - start).normalized), true);
                if (entity == null)
                {
                    DebugLog("CargoPlane CreateEntity returned null");
                    return false;
                }

                // InitDropPosition is the supported API on CargoPlane when available
                bool inited = false;
                try
                {
                    var plane = entity as CargoPlane;
                    if (plane != null)
                    {
                        var m = typeof(CargoPlane).GetMethod("InitDropPosition",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic);
                        if (m != null)
                        {
                            m.Invoke(plane, new object[] { drop });
                            inited = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"CargoPlane.InitDropPosition: {ex.Message}");
                }

                // Field fallbacks used on some builds
                if (!inited)
                {
                    try
                    {
                        var flags = System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic;
                        var t = entity.GetType();
                        foreach (var name in new[] { "dropPosition", "dropTarget", "_dropPosition", "targetPos" })
                        {
                            var f = t.GetField(name, flags);
                            if (f != null && f.FieldType == typeof(Vector3))
                            {
                                f.SetValue(entity, drop);
                                inited = true;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                entity.Spawn();
                try
                {
                    ulong pid = entity.net?.ID.Value ?? 0;
                    if (pid != 0)
                    {
                        _pluginCargoPlaneIds.Add(pid);
                        if (_pluginCargoPlaneIds.Count > 8)
                        {
                            // keep recent only
                            var keep = _pluginCargoPlaneIds.ToList();
                            _pluginCargoPlaneIds.Clear();
                            for (int i = Math.Max(0, keep.Count - 4); i < keep.Count; i++)
                                _pluginCargoPlaneIds.Add(keep[i]);
                        }
                    }
                }
                catch { }
                InvalidateEntityCache("cargo_plane");
                DebugLog($"CargoPlane spawned start=({start.x:F0},{start.y:F0},{start.z:F0}) drop={PositionToGrid(drop)} ({drop.x:F0},{drop.y:F0},{drop.z:F0}) init={inited}");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"TrySpawnCargoPlane: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Live cargo ships only — excludes map markers, gibs, and worldmodels that also contain "cargoship" in the path
        /// (those were falsely blocking new spawns via CountPrefabsContaining).
        /// </summary>
        private int CountCargoShips()
        {
            return CountFromIndex(_idxCargo);
        }

        private static bool IsLiveCargoShip(BaseEntity be)
        {
            if (be == null || be.IsDestroyed) return false;
            // Prefer typed check when available
            try
            {
                if (be is CargoShip)
                {
                    // Still reject under-world wrecks
                    if (be.transform.position.y < -50f) return false;
                    return true;
                }
            }
            catch { /* type may differ across builds */ }

            string prefab = be.ShortPrefabName ?? be.PrefabName ?? "";
            if (prefab.IndexOf("cargoship", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (prefab.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (prefab.IndexOf("worldmodel", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (prefab.IndexOf("cargo_plane", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (be.transform.position.y < -50f) return false;
            return true;
        }

        private bool SpawnCargoShip()
        {
            CleanupInvalidCargoShips();

            int before = CountCargoShips();
            DebugLog($"CargoShip start - live ships before: {before}");

            if (before > 0)
            {
                DebugLog("CargoShip skipped - one already exists");
                BroadcastKey("SpawnCargoExists");
                return false;
            }

            // Must spawn in deep ocean near the map edge — (0,0,0) is invalid and gets culled.
            // Coastal shallows (tug/sub band) are too shallow for the cargo ship hull.
            if (!TryGetCargoOceanSpawnPosition(out Vector3 pos))
            {
                Puts("[Events] CargoShip aborted — no deep-ocean spawn point found on the outer map ring.");
                return false;
            }

            DebugLog($"CargoShip ocean spawn at {PositionToGrid(pos)} ({pos.x:F0}, {pos.y:F0}, {pos.z:F0})");

            const string cargoPrefab = "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab";
            bool ok = false;
            BaseEntity spawned = null;
            try
            {
                // Face toward map center so the ship sails inward along the usual cargo route
                Vector3 flat = new Vector3(pos.x, 0f, pos.z);
                Quaternion rot = flat.sqrMagnitude > 1f
                    ? Quaternion.LookRotation((-flat).normalized, Vector3.up)
                    : Quaternion.identity;

                spawned = GameManager.server.CreateEntity(cargoPrefab, pos, rot, true);
                if (spawned != null)
                {
                    spawned.Spawn();
                    ok = true;
                    InvalidateEntityCache();
                    DebugLog("CargoShip CreateEntity+Spawn OK");
                    ApplyEntityNightLights(spawned);
                    timer.Once(3f, () => ApplyEntityNightLights(spawned));

                    try
                    {
                        var cargo = spawned as CargoShip;
                        if (cargo != null)
                        {
                            cargo.transform.position = pos;
                            var flags = System.Reflection.BindingFlags.Instance
                                      | System.Reflection.BindingFlags.Public
                                      | System.Reflection.BindingFlags.NonPublic;
                            typeof(CargoShip).GetMethod("RefreshCurrentPosition", flags)?.Invoke(cargo, null);
                            DebugLog($"CargoShip snapped to {PositionToGrid(pos)} for ocean path");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"CargoShip path nudge skipped: {ex.Message}");
                    }
                }
                else
                    DebugLog("CargoShip CreateEntity returned null");
            }
            catch (Exception ex)
            {
                DebugLog($"CargoShip CreateEntity exception: {ex.Message}");
            }

            if (!ok)
            {
                // Console spawn with explicit coordinates (same ocean point)
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string cmd = string.Format(inv, "spawn cargoship {0:F1} {1:F1} {2:F1}", pos.x, pos.y, pos.z);
                DebugLog($"CargoShip falling back to: {cmd}");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd);
                InvalidateEntityCache();
                ok = true;
            }

            timer.Once(10f, () =>
            {
                CleanupInvalidCargoShips();
                InvalidateEntityCache();
                int after = CountCargoShips();
                DebugLog($"CargoShip check - live ships after: {after} (was {before})");
                if (config != null && config.Debug)
                    DebugListMatching("cargoship");
                if (after <= 0)
                    Puts("[Events] CargoShip failed to stay in the world after ocean spawn — check server log for Invalid Position, or map cargo path.");
            });

            return ok;
        }

        /// <summary>
        /// Ocean spawn for cargo ships. Uses several detection strategies because WaterMap
        /// behavior differs across procedural / custom maps:
        /// 1) WaterMap depth on outer ring
        /// 2) Terrain below sea level (Y≈0) treated as ocean
        /// 3) Wider radius + lower depth threshold as last resort
        /// </summary>

        private List<Vector3> CollectOceanPatrolNodes()
        {
            var pts = new List<Vector3>();
            try
            {
                var pathObj = TerrainMeta.Path;
                if (pathObj != null)
                {
                    var t = pathObj.GetType();
                    var flags = System.Reflection.BindingFlags.Instance
                              | System.Reflection.BindingFlags.Public
                              | System.Reflection.BindingFlags.NonPublic
                              | System.Reflection.BindingFlags.Static;
                    string[] names = { "OceanPatrolFar", "OceanPatrolClose", "OceanPatrolPath", "OceanPatrol" };
                    foreach (string name in names)
                    {
                        object raw = null;
                        try { raw = t.GetField(name, flags)?.GetValue(pathObj); } catch { }
                        if (raw == null)
                            try { raw = t.GetProperty(name, flags)?.GetValue(pathObj, null); } catch { }
                        int before = pts.Count;
                        AppendOceanNodes(raw, pts);
                        if (pts.Count > before)
                        {
                            DebugLog($"Cargo path source Path.{name} +{pts.Count - before} (total {pts.Count})");
                            if (pts.Count >= 8) return pts;
                        }
                    }
                    if (pts.Count < 8)
                    {
                        var hinted = new List<string>();
                        foreach (var m in t.GetFields(flags))
                            if (m.Name.IndexOf("ocean", System.StringComparison.OrdinalIgnoreCase) >= 0
                                || m.Name.IndexOf("patrol", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                hinted.Add("F:" + m.Name + ":" + m.FieldType.Name);
                        foreach (var m in t.GetProperties(flags))
                            if (m.Name.IndexOf("ocean", System.StringComparison.OrdinalIgnoreCase) >= 0
                                || m.Name.IndexOf("patrol", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                hinted.Add("P:" + m.Name);
                        DebugLog("Cargo path Path members: " + (hinted.Count == 0 ? "(none)" : string.Join(",", hinted)));
                    }
                }
                else
                    DebugLog("Cargo path: TerrainMeta.Path is null");

                if (pts.Count < 8)
                {
                    try
                    {
                        var m = typeof(BaseBoat).GetMethod("GenerateOceanPatrolPath",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (m != null)
                        {
                            object gen = null;
                            var ps = m.GetParameters();
                            if (ps.Length == 0) gen = m.Invoke(null, null);
                            else if (ps.Length == 2)
                                gen = m.Invoke(null, new object[] { 200f, 200f });
                            AppendOceanNodes(gen, pts);
                            DebugLog($"Cargo path GenerateOceanPatrolPath n={pts.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog("Cargo path generate: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog("Cargo path collect: " + ex.Message);
            }
            return pts;
        }

        private static void AppendOceanNodes(object raw, List<Vector3> pts)
        {
            if (raw == null || pts == null) return;
            var seq = raw as System.Collections.IEnumerable;
            if (seq == null || raw is string) return;
            foreach (var item in seq)
            {
                if (item == null) continue;
                Vector3 p;
                if (item is Vector3)
                    p = (Vector3)item;
                else
                {
                    try
                    {
                        var it = item.GetType();
                        object pv = it.GetField("position")?.GetValue(item)
                                 ?? it.GetProperty("position")?.GetValue(item, null)
                                 ?? it.GetField("Position")?.GetValue(item)
                                 ?? it.GetProperty("Position")?.GetValue(item, null);
                        if (pv is List<Vector3>)
                        {
                            var inner = (List<Vector3>)pv;
                            for (int i = 0; i < inner.Count; i++) pts.Add(inner[i]);
                            continue;
                        }
                        if (!(pv is Vector3)) continue;
                        p = (Vector3)pv;
                    }
                    catch { continue; }
                }
                if (float.IsNaN(p.x) || float.IsNaN(p.z)) continue;
                pts.Add(p);
            }
        }

        private bool TryCargoOceanPathNode(float halfMap, out Vector3 result)
        {
            result = Vector3.zero;
            var pool = CollectOceanPatrolNodes();
            if (pool.Count < 3)
            {
                DebugLog($"Cargo ocean PATH miss nodes={pool.Count}");
                return false;
            }
            var usable = new List<Vector3>();
            for (int i = 0; i < pool.Count; i++)
            {
                Vector3 p = pool[i];
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                if (r < halfMap * 0.50f || r > halfMap * 1.15f) continue;
                float water = 0f;
                try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                if (water < -20f || water > 80f) water = 0f;
                p.y = water + 1.5f;
                usable.Add(p);
            }
            if (usable.Count < 3) usable = pool;
            result = usable[UnityEngine.Random.Range(0, usable.Count)];
            DebugLog($"Cargo ocean PATH node {PositionToGrid(result)} pool={usable.Count}/{pool.Count}");
            return true;
        }

        private bool TryGetCargoOceanSpawnPosition(out Vector3 result)
        {
            result = Vector3.zero;
            if (TerrainMeta.HeightMap == null)
                return false;

            float halfMap = TerrainMeta.Size.x * 0.5f;
            if (halfMap < 50f)
                halfMap = 1000f;

            // Prefer a node on Facepunch's ocean patrol ring so the ship follows that path
            // instead of dropping at the same deep-south cell and sliding off the map.
            if (TryCargoOceanPathNode(halfMap, out result))
                return true;

            var acceptedPts = new List<Vector3>();
            Vector3 best = Vector3.zero;
            float bestScore = -1f;
            int accepted = 0;
            int sampled = 0;

            // Passes: far offshore -> mid -> wider fallback (depth threshold relaxes each pass)
            float[][] rings =
            {
                new[] { 0.78f, 0.98f },
                new[] { 0.60f, 0.90f },
                new[] { 0.45f, 0.99f }
            };
            float[] minDepths = { 12f, 8f, 5f };

            for (int pass = 0; pass < rings.Length; pass++)
            {
                float minR = halfMap * rings[pass][0];
                float maxR = halfMap * rings[pass][1];
                float minDepth = minDepths[pass];

                for (int attempt = 0; attempt < 80; attempt++)
                {
                    sampled++;
                    float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    float radius = UnityEngine.Random.Range(minR, maxR);
                    Vector3 candidate = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                    float terrain;
                    float water;
                    try
                    {
                        terrain = TerrainMeta.HeightMap.GetHeight(candidate);
                        water = 0f;
                        bool hasWater = false;
                        try
                        {
                            if (TerrainMeta.WaterMap != null)
                            {
                                water = TerrainMeta.WaterMap.GetHeight(candidate);
                                hasWater = true;
                            }
                        }
                        catch { hasWater = false; }

                        // Sea level on most Rust maps is ~0. If WaterMap is missing or
                        // returns garbage, treat ocean surface as Y=0.
                        if (!hasWater || float.IsNaN(water) || float.IsInfinity(water) ||
                            water < -20f || water > 80f)
                        {
                            water = 0f;
                        }

                        if (float.IsNaN(terrain) || float.IsInfinity(terrain))
                            continue;
                    }
                    catch { continue; }

                    float depth = water - terrain;

                    // Alternate signal: submerged terrain even if WaterMap is flat/wrong
                    if (depth < minDepth && terrain < -2f && water >= -1f && water <= 5f)
                        depth = Mathf.Max(depth, -terrain);

                    if (depth < minDepth)
                        continue;

                    float surfaceY = water + 1.5f;
                    if (surfaceY < -5f || surfaceY > 50f)
                        continue;

                    accepted++;
                    var pt = new Vector3(candidate.x, surfaceY, candidate.z);
                    acceptedPts.Add(pt);
                    float score = depth + radius * 0.01f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = pt;
                    }
                }

                if (acceptedPts.Count >= 4)
                {
                    result = acceptedPts[UnityEngine.Random.Range(0, acceptedPts.Count)];
                    DebugLog($"Cargo ocean OK pass={pass} depth pool={acceptedPts.Count} at {PositionToGrid(result)} (sampled={sampled}, accepted={accepted})");
                    return true;
                }

                if (bestScore > 0f)
                {
                    result = best;
                    DebugLog($"Cargo ocean OK pass={pass} score={bestScore:F1} at {PositionToGrid(result)} (sampled={sampled}, accepted={accepted})");
                    return true;
                }
            }

            // Last resort: 40 fixed angles on outer ring, terrain-only heuristic
            for (int attempt = 0; attempt < 40; attempt++)
            {
                float angle = attempt * (Mathf.PI * 2f / 40f);
                float radius = halfMap * 0.9f;
                Vector3 candidate = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                float terrain;
                try { terrain = TerrainMeta.HeightMap.GetHeight(candidate); }
                catch { continue; }
                if (terrain < 1f)
                {
                    result = new Vector3(candidate.x, 1.5f, candidate.z);
                    DebugLog($"Cargo ocean LAST-RESORT terrain={terrain:F1} at {PositionToGrid(result)}");
                    return true;
                }
            }

            Puts($"[Events] Cargo ocean search failed (sampled={sampled}). HeightMap/WaterMap may be unavailable.");
            return false;
        }

        private void CleanupInvalidCargoShips()
        {
            EnsureLiveIndex();
            var doomed = new List<BaseEntity>();
            foreach (var id in _idxCargo)
            {
                var be = FindIndexedEntity(id);
                if (be == null || be.IsDestroyed) continue;
                Vector3 p = be.transform.position;
                // Only cull ships clearly under the world. Do NOT kill at origin immediately —
                // CargoShip often spawns at (0,0,0) then relocates onto its ocean path.
                if (p.y < -50f)
                {
                    DebugLog($"Removing invalid cargoship at {p}");
                    doomed.Add(be);
                }
            }
            foreach (var be in doomed)
            {
                try { be.Kill(); } catch { }
            }
            if (doomed.Count > 0)
            {
                InvalidateEntityCache("cargoship");
                InvalidateEntityCache("cargoship_live");
            }
        }

        /// <summary>
        /// Cargo ships that finish their path often park forever in deep ocean and never leave the grid.
        /// This monitor: (1) starts egress after MaxEventMinutes or when stuck, (2) hard-kills after egress timeout.
        /// </summary>
        /// <summary>
        /// Kill cargo-riding scientists near a ship before egress / deep-sea despawn so they
        /// do not spam Invalid NavAgent / Invalid Position at z≈±4000.
        /// </summary>
        /// <summary>
        /// Facepunch cargo ships respawn scientists via child SpawnGroup components after Kill().
        /// Clear + disable those groups first or NPCs come right back.
        /// </summary>
        private void DisableCargoScientistRespawn(BaseEntity cargo)
        {
            if (cargo == null || cargo.IsDestroyed) return;
            try
            {
                var groups = cargo.GetComponentsInChildren<SpawnGroup>(true);
                if (groups == null) return;
                for (int i = 0; i < groups.Length; i++)
                {
                    var g = groups[i];
                    if (g == null) continue;
                    try { g.Clear(); } catch { }
                    try { g.enabled = false; } catch { }
                }
                DebugLog($"Cargo SpawnGroups disabled on ship at {PositionToGrid(cargo.transform.position)} (n={groups.Length})");
            }
            catch (Exception ex)
            {
                DebugLog($"DisableCargoScientistRespawn: {ex.Message}");
            }
        }


        private void ClearCargoScientists(BaseEntity cargo, Vector3 origin, float radius = 250f)
        {
            if (cargo != null && !cargo.IsDestroyed)
                DisableCargoScientistRespawn(cargo);
            KillNearbyCargoScientists(origin, radius);
        }


        private void KillNearbyCargoScientists(Vector3 origin, float radius = 250f)
        {
            // No live cargo in index -> nothing to clear (avoids full-world scientist scan)
            EnsureLiveIndex();
            if (_idxCargo.Count == 0 && radius < 500f)
                return;

            // Disable respawn on any cargo in range first
            try
            {
                foreach (var cargoId in _idxCargo)
                {
                    var ship = FindIndexedEntity(cargoId);
                    if (ship == null || ship.IsDestroyed) continue;
                    try
                    {
                        Vector3 sp = ship.transform.position - origin;
                        if (sp.sqrMagnitude > radius * radius) continue;
                    }
                    catch { continue; }
                    DisableCargoScientistRespawn(ship);
                }
            }
            catch { }

            var riders = new List<BaseEntity>();
            float r2 = radius * radius;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null || be.IsDestroyed) continue;
                    string p = (be.PrefabName ?? be.ShortPrefabName ?? "").ToLowerInvariant();
                    bool isNpc = p.IndexOf("scientist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     p.IndexOf("npcplayer", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isCorpse = p.IndexOf("scientist_corpse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    p.IndexOf("item_drop_backpack", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isNpc && !isCorpse) continue;
                    // Never touch military tunnel / mine / road ambush NPCs far inland by accident —
                    // require proximity to cargo origin (already radius checked)
                    try
                    {
                        Vector3 d = be.transform.position - origin;
                        if (d.sqrMagnitude > r2) continue;
                    }
                    catch { continue; }
                    riders.Add(be);
                }
            }
            catch { }

            if (riders.Count == 0)
                return;

            const float riderGap = 0.08f;
            for (int i = 0; i < riders.Count; i++)
            {
                var rider = riders[i];
                if (rider == null) continue;
                float delay = i * riderGap;
                if (delay <= 0.001f)
                {
                    try { if (!rider.IsDestroyed) rider.Kill(); } catch { }
                }
                else
                {
                    timer.Once(delay, () =>
                    {
                        try { if (rider != null && !rider.IsDestroyed) rider.Kill(); } catch { }
                    });
                }
            }
            DebugLog($"Pre-egress scheduled kill of {riders.Count} cargo scientist(s)/corpse(s) near {PositionToGrid(origin)} (gap={riderGap:F2}s)");
        }

        private void MonitorCargoShips()
        {
            if (!_ready || config == null) return;
            var life = config.CargoShipLifecycle;
            if (life != null && !life.Enabled) return;

            float maxEventSec = Mathf.Max(10f, (life?.MaxEventMinutes ?? 55f)) * 60f;
            float egressGraceSec = Mathf.Max(5f, (life?.ForceKillMinutesAfterEgress ?? 15f)) * 60f;
            float stuckSec = Mathf.Max(30f, life?.StuckSeconds ?? 120f);
            float now = Time.realtimeSinceStartup;
            float halfMap = TerrainMeta.Size.x * 0.5f;
            if (halfMap < 50f) halfMap = 1000f;

            var seen = new HashSet<ulong>();
            var toKill = new List<BaseEntity>();

            EnsureLiveIndex();
            foreach (var cargoId in _idxCargo)
            {
                var be = FindIndexedEntity(cargoId);
                if (be == null || be.IsDestroyed) continue;
                if (!IsLiveCargoShip(be)) continue;

                ulong netId = cargoId;
                seen.Add(netId);

                Vector3 pos = be.transform.position;
                if (!_cargoTracks.TryGetValue(netId, out var track))
                {
                    track = new CargoTrack
                    {
                        FirstSeen = now,
                        LastMoveTime = now,
                        LastPos = pos,
                        EgressStarted = false,
                        EgressStartedAt = 0f
                    };
                    _cargoTracks[netId] = track;
                    DebugLog($"Cargo track start net={netId} at {PositionToGrid(pos)}");
                    continue;
                }

                float moved = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(track.LastPos.x, 0f, track.LastPos.z));
                if (moved > 8f)
                {
                    track.LastMoveTime = now;
                    track.LastPos = pos;
                }

                // Natural harbor / port stops are intentional — never treat as stuck, never force egress/kill for idle
                bool atHarbor = IsNearCargoHarbor(pos, 180f);
                if (atHarbor)
                {
                    track.LastMoveTime = now;
                    track.LastPos = pos;
                }

                float age = now - track.FirstSeen;
                float idle = now - track.LastMoveTime;
                float radial = new Vector3(pos.x, 0f, pos.z).magnitude;
                // Outer ocean only — harbors sit on the island coast, not deep outer ring
                bool deepOceanEdge = radial > halfMap * 0.75f && !atHarbor;

                // Already egressing — hard kill if it never leaves (still safe at harbor: egress already started)
                if (track.EgressStarted)
                {
                    // Do not kill while still at a natural port stop (players may still be looting)
                    if (atHarbor)
                        continue;

                    // Clear once (disable SpawnGroup + kill). Re-check rare residuals every 45s only.
                    // Continuous Kill without disabling SpawnGroup made Facepunch respawn the squad.
                    if (!track.ScientistsCleared || (now - track.LastScientistClearAt) > 45f)
                    {
                        try { ClearCargoScientists(be, pos, 250f); } catch { }
                        track.ScientistsCleared = true;
                        track.LastScientistClearAt = now;
                    }

                    if (now - track.EgressStartedAt >= egressGraceSec)
                    {
                        DebugLog($"Cargo force-kill after egress timeout at {PositionToGrid(pos)} (age {age / 60f:F0}m)");
                        try { ClearCargoScientists(be, pos, 250f); } catch { }
                        toKill.Add(be);
                    }
                    continue;
                }

                // Force egress only when event is actually ending — do NOT strip scientists early
                // on outer ocean or the ship sits undefended for the rest of the route.
                bool timeUp = age >= maxEventSec && !atHarbor;
                bool stuckAtEnd = !atHarbor && deepOceanEdge && idle >= stuckSec && age >= maxEventSec * 0.5f;
                bool stuckDeepOcean = !atHarbor && deepOceanEdge && idle >= stuckSec * 2f && age >= maxEventSec * 0.75f;

                // Optional last-moment clear: only in the final ~2 minutes of the event window
                // AND already on outer ring — still leaves most of the voyage fully guarded.
                float endWindowSec = Mathf.Min(120f, maxEventSec * 0.05f);
                bool nearEventEnd = age >= (maxEventSec - endWindowSec) && deepOceanEdge && !atHarbor;
                if (nearEventEnd && !track.ScientistsCleared)
                {
                    DebugLog($"Cargo pre-egress NPC clear (near end of track) age={age / 60f:F1}m at {PositionToGrid(pos)}");
                    try { ClearCargoScientists(be, pos, 250f); } catch { }
                    track.ScientistsCleared = true;
                    track.LastScientistClearAt = now;
                }

                if (timeUp || stuckAtEnd || stuckDeepOcean)
                {
                    DebugLog($"Cargo start egress (timeUp={timeUp} stuckEnd={stuckAtEnd} stuckDeep={stuckDeepOcean} atHarbor={atHarbor}) age={age / 60f:F1}m idle={idle:F0}s at {PositionToGrid(pos)}");
                    // Strip riders only as egress begins — ship was defended for the full route until now
                    if (!track.ScientistsCleared)
                    {
                        try { ClearCargoScientists(be, pos, 250f); } catch { }
                        track.ScientistsCleared = true;
                        track.LastScientistClearAt = now;
                    }
                    if (TryStartCargoEgress(be))
                    {
                        track.EgressStarted = true;
                        track.EgressStartedAt = now;
                    }
                    else if (!atHarbor)
                    {
                        // API missing — only kill away from harbors so port stops are never wiped
                        DebugLog("Cargo egress API unavailable — killing ship (not at harbor)");
                        toKill.Add(be);
                    }
                }
            }

            // Prune tracking for ships that left the world
            if (_cargoTracks.Count > 0)
            {
                List<ulong> dead = null;
                foreach (var id in _cargoTracks.Keys)
                {
                    if (seen.Contains(id)) continue;
                    if (dead == null) dead = new List<ulong>(4);
                    dead.Add(id);
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                        _cargoTracks.Remove(dead[i]);
                }
            }

            for (int i = 0; i < toKill.Count; i++)
            {
                try
                {
                    ulong id = 0;
                    try { id = toKill[i].net?.ID.Value ?? 0; } catch { }
                    if (id != 0) _cargoTracks.Remove(id);
                    toKill[i].Kill();
                }
                catch { }
            }
            if (toKill.Count > 0)
                InvalidateEntityCache("cargoship");
        }

        /// <summary>
        /// True when the ship is near a Harbor monument (natural port stop on the cargo route).
        /// Those pauses must never count as "stuck" and must not force egress or kill.
        /// </summary>
        private bool IsNearCargoHarbor(Vector3 pos, float radius)
        {
            try
            {
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments == null) return false;
                float r2 = radius * radius;
                for (int i = 0; i < monuments.Count; i++)
                {
                    var mon = monuments[i];
                    if (mon == null) continue;
                    string n = (mon.name ?? "").ToLowerInvariant();
                    // Harbor / port monuments only — not fishing villages or underwater labs
                    if (n.IndexOf("harbor", StringComparison.Ordinal) < 0 &&
                        n.IndexOf("harbour", StringComparison.Ordinal) < 0)
                        continue;
                    Vector3 mp = mon.transform.position;
                    float dx = pos.x - mp.x;
                    float dz = pos.z - mp.z;
                    if (dx * dx + dz * dz <= r2)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private bool TryStartCargoEgress(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return false;

            // 1) Typed CargoShip methods (names vary by Facepunch build)
            try
            {
                var cargo = entity as CargoShip;
                if (cargo != null)
                {
                    string[] methodNames = {
                        "StartEgress", "BeginEgress", "StartEgressing", "DoEgress",
                        "Egress", "TriggerEgress", "SetToEgress"
                    };
                    var flags = System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic;
                    foreach (var name in methodNames)
                    {
                        var m = typeof(CargoShip).GetMethod(name, flags, null, Type.EmptyTypes, null);
                        if (m == null) continue;
                        m.Invoke(cargo, null);
                        DebugLog($"Cargo egress via CargoShip.{name}()");
                        return true;
                    }

                    // Bool / state field flip as last reflection attempt
                    string[] fieldNames = { "egressing", "isEgressing", "HasStartedEgress", "shouldEgress" };
                    foreach (var fname in fieldNames)
                    {
                        var f = typeof(CargoShip).GetField(fname, flags);
                        if (f == null || f.FieldType != typeof(bool)) continue;
                        f.SetValue(cargo, true);
                        DebugLog($"Cargo egress via field {fname}=true");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"CargoShip reflection egress failed: {ex.Message}");
            }

            // 2) Server console (PC / some console builds expose this)
            try
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "cargoships.startegressing");
                DebugLog("Cargo egress via cargoships.startegressing");
                return true;
            }
            catch { }

            try
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "cargo.startegress");
                DebugLog("Cargo egress via cargo.startegress");
                return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Counts live Patrol Helicopters only (excludes markers / gibs).
        /// </summary>
        private int CountPatrolHelis()
        {
            return CountFromIndex(_idxPatrolHeli);
        }

        // Active route timers keyed by heli net ID so we can cancel on death/unload
        private readonly Dictionary<ulong, Timer> _heliRouteTimers = new Dictionary<ulong, Timer>();
        // In-progress smooth altitude corrections (prevent stacking + cancel on unload)
        private readonly Dictionary<ulong, Timer> _heliAltitudeSmoothTimers = new Dictionary<ulong, Timer>();
        private readonly Dictionary<ulong, Timer> _chinookTourTimers = new Dictionary<ulong, Timer>();
        private readonly HashSet<ulong> _chinookDroppedIds = new HashSet<ulong>();

        private bool SpawnPatrolHeli()
        {
            return SpawnPatrolHeliAt(GetRandomMonumentInterest());
        }

        /// <summary>
        /// Spawn (or, if one already exists, retarget) a Patrol Heli toward a specific interest point.
        /// Used by normal PatrolHeli events and by BaseRaid support.
        /// </summary>
        private bool SpawnPatrolHeliAt(Vector3 interestPoint)
        {
            int before = CountPatrolHelis();
            DebugLog($"PatrolHeli start - helis before: {before}");

            float cruise = Mathf.Max(80f, config.HeliPatrol?.CruiseAltitude ?? 80f);
            Vector3 interest = interestPoint;
            interest.y = GetHeliCruiseAltitude(interest, cruise);

            // If a heli is already live, retarget it instead of spawning a second one
            if (before > 0)
            {
                var existing = FindFirstPatrolHeliAI();
                if (existing != null)
                {
                    try
                    {
                        existing.SetInitialDestination(interest, 0.25f);
                        ForceHeliLandInterest(existing, interest, forceMove: true);
                        TryExtendHeliLifetime(existing);
                        DebugLog($"PatrolHeli retargeted -> {PositionToGrid(interest)}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"PatrolHeli retarget failed: {ex.Message}");
                    }
                }
                DebugLog("PatrolHeli skipped - one already exists and retarget failed");
                BroadcastKey("SpawnPatrolHeliExists");
                return false;
            }

            // Inland approach + interest; never spawn on the deep-sea ocean path fringe
            Vector3 spawnAt = GetMapEdgeApproach(interest, cruise);

            const string heliPrefab = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
            BaseEntity entity = null;
            try
            {
                entity = GameManager.server.CreateEntity(heliPrefab, spawnAt, Quaternion.identity, true);
            }
            catch (Exception ex)
            {
                DebugLog($"PatrolHeli CreateEntity exception: {ex.Message}");
            }

            if (entity == null)
            {
                DebugLog("PatrolHeli CreateEntity failed - falling back to console spawn");
                TryConsoleSpawn("patrolhelicopter");
                timer.Once(5f, () =>
                {
                    var ai = FindFirstPatrolHeliAI();
                    if (ai != null)
                    {
                        try
                        {
                            // Pull console-spawned heli inland immediately
                            SnapHeliToMapEdgeToward(ai, interest, cruise);
                            ai.SetInitialDestination(interest, 0.25f);
                            ForceHeliLandInterest(ai, interest, forceMove: true);
                        }
                        catch { }
                        StartHeliLifetimeAndLights(ai, interest);
                    }
                });
                return true;
            }

            var heliAI = entity.GetComponent<PatrolHelicopterAI>();

            // Lock transform before and after Spawn — AI/ocean path can move the entity on first ticks
            entity.transform.position = spawnAt;
            entity.Spawn();
            try
            {
                entity.transform.position = spawnAt;
                entity.TransformChanged();
            }
            catch { }
            InvalidateEntityCache();

            DebugLog($"PatrolHeli spawnAt {PositionToGrid(spawnAt)} ({spawnAt.x:F0},{spawnAt.y:F0},{spawnAt.z:F0}) -> interest {PositionToGrid(interest)} ({interest.x:F0},{interest.y:F0},{interest.z:F0})");

            if (heliAI != null)
            {
                try
                {
                    heliAI.SetInitialDestination(interest, 0.25f);
                    ForceHeliLandInterest(heliAI, interest, forceMove: true);
                }
                catch (Exception ex)
                {
                    DebugLog($"SetInitialDestination/ForceHeliLandInterest failed: {ex.Message}");
                }

                // Early locks: ocean-path AI often teleports off-map within 1–5s of spawn
                ScheduleHeliSpawnLocks(heliAI, interest, cruise, spawnAt);

                StartHeliLifetimeAndLights(heliAI, interest);
            }
            else
            {
                DebugLog("PatrolHeliAI missing — applying lights on entity only");
                ApplyPatrolHeliNightLights(entity);
                timer.Once(2f, () => ApplyPatrolHeliNightLights(entity));
            }

            timer.Once(5f, () =>
            {
                InvalidateEntityCache();
                int after = CountPatrolHelis();
                DebugLog($"PatrolHeli check - helis after: {after} (was {before})");
                if (heliAI != null && heliAI.helicopterBase != null && !heliAI.helicopterBase.IsDestroyed)
                {
                    Vector3 p = heliAI.helicopterBase.transform.position;
                    DebugLog($"PatrolHeli live position: {PositionToGrid(p)} ({p.x:F0}, {p.y:F0}, {p.z:F0})");
                }
                if (config != null && config.Debug)
                    DebugListMatching("patrolhelicopter");
            });
            return true;
        }

        /// <summary>
        /// Re-assert position + interest during the first seconds after spawn.
        /// PatrolHelicopterAI frequently jumps onto far ocean patrol path nodes otherwise.
        /// </summary>
        private void ScheduleHeliSpawnLocks(PatrolHelicopterAI heliAI, Vector3 interest, float cruise, Vector3 spawnAt)
        {
            if (heliAI == null) return;
            // Dense first-10s locks — AI often jumps to ocean path within 0.5–2s of Spawn()
            float[] delays = { 0.5f, 1.0f, 1.5f, 2.5f, 4.0f, 6.0f, 8.0f, 10.0f };
            foreach (float delay in delays)
            {
                float d = delay;
                timer.Once(d, () =>
                {
                    try
                    {
                        if (heliAI == null || heliAI.helicopterBase == null || heliAI.helicopterBase.IsDestroyed)
                            return;
                        Vector3 p = heliAI.helicopterBase.transform.position;
                        bool off = IsDeepSeaOrOffMap(p);
                        bool low = p.y < 100f;
                        // Also treat near-rim (still on playable edge) as needing interest push
                        // so AI does not commit to a far ocean-path node during the lock window.
                        float half = 2000f;
                        try { half = TerrainMeta.Size.x * 0.5f; } catch { }
                        float radial = new Vector3(p.x, 0f, p.z).magnitude;
                        bool nearRim = radial > half * 0.90f;

                        if (!off && !low && !nearRim)
                        {
                            TryExtendHeliLifetime(heliAI);
                            ForceHeliLandInterest(heliAI, interest, forceMove: false);
                            return;
                        }

                        if (off || nearRim)
                        {
                            SnapHeliToMapEdgeToward(heliAI, interest, cruise);
                            Vector3 after = heliAI.helicopterBase.transform.position;
                            if (IsDeepSeaOrOffMap(after) || new Vector3(after.x, 0f, after.z).magnitude > half * 0.90f)
                            {
                                Vector3 fix = spawnAt;
                                fix.y = Mathf.Max(GetHeliCruiseAltitude(fix, cruise), 120f);
                                heliAI.helicopterBase.transform.position = fix;
                                try { heliAI.helicopterBase.TransformChanged(); } catch { }
                            }
                            ForceHeliLandInterest(heliAI, interest, forceMove: true);
                            DebugLog($"PatrolHeli spawn-lock @{d:F1}s {(off ? "OFFMAP" : "RIM")} -> {PositionToGrid(heliAI.helicopterBase.transform.position)}");
                        }
                        else if (low)
                        {
                            float wantY = Mathf.Max(GetHeliCruiseAltitude(p, cruise), 120f);
                            // Smooth climb — hard Y teleport looked like a pop every lock tick
                            SmoothAdjustHeliAltitude(heliAI, wantY, 2.5f);
                            // Also push AI destination so native flight assists the climb
                            ForceHeliLandInterest(heliAI, interest, forceMove: false);
                            TryExtendHeliLifetime(heliAI);
                            DebugLog($"PatrolHeli spawn-lock @{d:F1}s smooth altitude -> Y={wantY:F0}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"PatrolHeli spawn-lock @{d:F1}s failed: {ex.Message}");
                    }
                });
            }
        }

        // Cached major inland monuments for heli pathing (rebuilt with road cache / on demand)
        private List<Vector3> _heliInterestCache = new List<Vector3>();

        /// <summary>
        /// Major inland monuments for heli interest. Excludes oil rigs / edge harbors that
        /// pull the AI into deep sea. Rebuilds cache lazily.
        /// </summary>
        private Vector3 GetRandomMonumentInterest()
        {
            EnsureHeliInterestCache();
            if (_heliInterestCache.Count == 0)
                return GetInlandMonumentTarget();
            return _heliInterestCache[UnityEngine.Random.Range(0, _heliInterestCache.Count)];
        }

        private void EnsureHeliInterestCache()
        {
            if (_heliInterestCache != null && _heliInterestCache.Count > 0)
                return;

            _heliInterestCache = new List<Vector3>();
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            float inland = half * 0.78f;

            try
            {
                if (TerrainMeta.Path?.Monuments == null) return;
                foreach (var mon in TerrainMeta.Path.Monuments)
                {
                    if (mon == null) continue;
                    string n = (mon.name ?? "").ToLowerInvariant();
                    if (n.Contains("harvestable") || n.Contains("tiny") || n.Contains("small") ||
                        n.Contains("underwater") || n.Contains("swamp") || n.Contains("ice_lake") ||
                        n.Contains("oasis") || n.Contains("oilrig") || n.Contains("oil_rig") ||
                        n.Contains("fishing_village") || n.Contains("underwater_lab"))
                        continue;

                    bool major =
                        n.Contains("launch") || n.Contains("airfield") || n.Contains("trainyard") ||
                        n.Contains("powerplant") || n.Contains("water_treatment") || n.Contains("military") ||
                        n.Contains("radtown") || n.Contains("dome") || n.Contains("satellite") ||
                        n.Contains("excavator") || n.Contains("arctic") || n.Contains("junkyard") ||
                        n.Contains("sphere") || n.Contains("harbor") || n.Contains("lighthouse") ||
                        n.Contains("supermarket") || n.Contains("gas_station") || n.Contains("mining") ||
                        n.Contains("warehouse") || n.Contains("ferry") || n.Contains("missile") ||
                        n.Contains("nuclear") || n.Contains("desert");

                    if (!major) continue;

                    Vector3 p = mon.transform.position;
                    if (Mathf.Abs(p.x) > inland || Mathf.Abs(p.z) > inland)
                        continue;

                    float water = 0f, terrain = p.y;
                    try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                    try { terrain = TerrainMeta.HeightMap.GetHeight(p); } catch { }
                    if (terrain < water + 1f) continue;

                    _heliInterestCache.Add(p);
                }
            }
            catch { }

            if (_heliInterestCache.Count == 0)
            {
                // Fallback: any inland GetInlandMonumentTarget samples
                for (int i = 0; i < 6; i++)
                    _heliInterestCache.Add(GetInlandMonumentTarget());
            }
        }

        /// <summary>
        /// Spawn just outside the map boundary on the SAME side as the interest.
        /// Opposite-side approach forces a full-map deep-sea crossing (heli never arrives).
        /// </summary>
        private Vector3 GetMapEdgeApproach(Vector3 toward, float cruiseAlt)
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }

            Vector3 flat = new Vector3(toward.x, 0f, toward.z);
            if (flat.sqrMagnitude < 1f)
                flat = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
            flat.Normalize();

            // Spawn further inland so AI has less chance to latch onto ocean-path nodes.
            // 0.82–0.90 still produced frequent 1s OFFMAP snaps in 1.7.3 logs.
            float edgeDist = half * UnityEngine.Random.Range(0.70f, 0.82f);
            Vector3 edge = flat * edgeDist;

            // Slight lateral offset so approach isn't a perfect radial line
            Vector3 perp = new Vector3(-flat.z, 0f, flat.x);
            edge += perp * UnityEngine.Random.Range(-180f, 180f);

            // Clamp well inside the playable rim
            float limit = half * 0.80f;
            edge.x = Mathf.Clamp(edge.x, -limit, limit);
            edge.z = Mathf.Clamp(edge.z, -limit, limit);

            edge.y = GetHeliCruiseAltitude(edge, cruiseAlt);
            edge.y = Mathf.Max(edge.y, 120f);
            return edge;
        }

        /// <summary>
        /// Root vehicle / event entity only — never seats, storage, markers, subents.
        /// </summary>
        private static bool EntityHasLights(string pathLower)
        {
            if (string.IsNullOrEmpty(pathLower)) return false;
            if (pathLower.Contains("marker") || pathLower.Contains("alarm") ||
                pathLower.Contains("reinforcement") || pathLower.Contains("gib") ||
                pathLower.Contains("servergibs") || pathLower.Contains("subents") ||
                pathLower.Contains("/seats/") || pathLower.Contains("storage") ||
                pathLower.Contains("privilege") || pathLower.Contains("fuel") ||
                pathLower.Contains("torpedo") || pathLower.Contains("driver") ||
                pathLower.Contains("itemstorage"))
                return false;

            if (pathLower.Contains("patrolhelicopter.prefab")) return true;
            if (pathLower.Contains("ch47scientists")) return true;
            if (pathLower.Contains("bradleyapc")) return true;
            if (pathLower.Contains("attackhelicopter") && !pathLower.Contains("module")) return true;
            if (pathLower.Contains("minicopter.entity") || pathLower.EndsWith("minicopter.prefab")) return true;
            if (pathLower.Contains("scraptransporthelicopter")) return true;
            if (pathLower.Contains("tugboat.prefab")) return true;
            if (pathLower.Contains("submarinesolo") || pathLower.Contains("submarineduo")) return true;
            if (pathLower.Contains("cargoshiptest.prefab")) return true;
            if (pathLower.Contains("hotairballoon.prefab")) return true;
            return false;
        }

        private float _nightCheckCacheTime = -999f;
        private bool _nightCheckCacheValue;

        private bool IsNightTime()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _nightCheckCacheTime < 30f)
                return _nightCheckCacheValue;

            float hour = -1f;
            bool night = false;
            try
            {
                if (TOD_Sky.Instance != null)
                {
                    try
                    {
                        var prop = TOD_Sky.Instance.GetType().GetProperty("IsNight");
                        if (prop != null && prop.PropertyType == typeof(bool) &&
                            (bool)prop.GetValue(TOD_Sky.Instance, null))
                            night = true;
                    }
                    catch { }

                    if (!night && TOD_Sky.Instance.Cycle != null)
                        hour = TOD_Sky.Instance.Cycle.Hour;
                }
            }
            catch { }

            if (!night)
            {
                if (hour < 0f)
                    hour = GetCurrentHour();
                night = hour < 8.5f || hour >= 18f;
            }

            _nightCheckCacheTime = now;
            _nightCheckCacheValue = night;
            return night;
        }

        /// <summary>
        /// Networked lights only — Unity Light.enabled does NOT replicate to clients.
        /// Patrol heli uses its native night spotlight (no deployable mesh).
        /// Attack heli may still parent a searchlight; CH47 hierarchy is never touched.
        /// </summary>
        private void ApplyEntityNightLights(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return;
            bool night = IsNightTime();
            string prefab = (entity.PrefabName ?? entity.ShortPrefabName ?? "").ToLowerInvariant();

            try
            {
                bool isPatrolHeli = prefab.Contains("patrolhelicopter");
                bool isChinook = prefab.Contains("ch47scientists") || prefab.Contains("ch47");
                bool isAttackHeli = prefab.Contains("attackhelicopter");

                // CH47: never parent extra entities — breaks mount/dismount -> Invalid Position corpses
                if (isChinook)
                {
                    entity.SendNetworkUpdateImmediate();
                    return;
                }

                // Patrol heli: strip any leftover deployable searchlights and rely on the
                // vanilla AI spotlight (wiki-documented night beam). Attached mesh looked wrong.
                if (isPatrolHeli)
                {
                    RemoveAttachedSearchLights(entity);
                    entity.SendNetworkUpdateImmediate();
                    DebugLog($"NightLights patrol heli — native spotlight only (night={night})");
                    return;
                }

                // Attack heli (player vehicle): optional attach remains available
                if (isAttackHeli)
                {
                    EnsureAttachedSearchLight(entity);
                    try
                    {
                        if (entity.children != null)
                        {
                            foreach (var child in entity.children)
                            {
                                if (child == null || child.IsDestroyed) continue;
                                string cn = (child.ShortPrefabName ?? "").ToLowerInvariant();
                                if (cn.Contains("searchlight"))
                                    PowerEntityLight(child, true);
                            }
                        }
                    }
                    catch { }
                    entity.SendNetworkUpdateImmediate();
                    DebugLog($"NightLights attack heli OK night={night} {entity.ShortPrefabName}");
                    return;
                }

                // Ground vehicles: headlight network flags only
                if (night)
                {
                    SafeSetEntityFlag(entity, BaseEntity.Flags.Reserved5, true, false, true);
                    try
                    {
                        var vehicle = entity as BaseVehicle;
                        if (vehicle != null)
                            SafeSetEntityFlag(vehicle, BaseEntity.Flags.Reserved5, true, false, true);
                    }
                    catch { }
                }

                entity.SendNetworkUpdateImmediate();
            }
            catch (Exception ex)
            {
                DebugLog($"ApplyEntityNightLights: {ex.Message}");
            }
        }


        /// <summary>
        /// Sep 2026 (protocol 2633) removed BaseEntity.SetFlag.
        /// Use StartSetFlags disposable API when present, else reflect old SetFlag.
        /// </summary>
        private static void SafeSetEntityFlag(BaseEntity ent, BaseEntity.Flags flag, bool on, bool recursive = false, bool networkupdate = true)
        {
            if (ent == null || ent.IsDestroyed) return;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var start = ent.GetType().GetMethod("StartSetFlags", flags);
                if (start == null)
                    start = typeof(BaseEntity).GetMethod("StartSetFlags", flags);
                if (start != null)
                {
                    object token = null;
                    try
                    {
                        var pars = start.GetParameters();
                        object[] args = null;
                        if (pars.Length == 1 && pars[0].ParameterType.IsEnum)
                        {
                            object mode = Activator.CreateInstance(pars[0].ParameterType);
                            foreach (var name in System.Enum.GetNames(pars[0].ParameterType))
                            {
                                if (name.IndexOf("SendNetworkUpdate", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    mode = System.Enum.Parse(pars[0].ParameterType, name);
                                    break;
                                }
                            }
                            args = new[] { mode };
                        }
                        else if (pars.Length == 0)
                            args = System.Array.Empty<object>();
                        token = start.Invoke(ent, args);
                        if (token != null)
                        {
                            var set = token.GetType().GetMethod("Set", flags,
                                null, new[] { typeof(BaseEntity.Flags), typeof(bool) }, null)
                                ?? token.GetType().GetMethod("Set", flags);
                            if (set != null)
                            {
                                var sp = set.GetParameters();
                                if (sp.Length >= 2)
                                    set.Invoke(token, new object[] { flag, on });
                            }
                        }
                    }
                    finally
                    {
                        try { (token as System.IDisposable)?.Dispose(); } catch { }
                    }
                    return;
                }

                var m = ent.GetType().GetMethod("SetFlag", flags)
                        ?? typeof(BaseEntity).GetMethod("SetFlag", flags);
                if (m != null)
                    m.Invoke(ent, new object[] { flag, on, recursive, networkupdate });
            }
            catch { }
        }

        private void PowerEntityLight(BaseEntity lightEnt, bool on)
        {
            if (lightEnt == null || lightEnt.IsDestroyed) return;
            try
            {
                SafeSetEntityFlag(lightEnt, BaseEntity.Flags.On, on, false, true);
                SafeSetEntityFlag(lightEnt, BaseEntity.Flags.Reserved8, on, false, true); // HasPower
                SafeSetEntityFlag(lightEnt, BaseEntity.Flags.Reserved5, on, false, true);

                var io = lightEnt as IOEntity;
                if (io != null)
                {
                    try
                    {
                        var update = typeof(IOEntity).GetMethod("UpdateHasPower",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic);
                        if (update != null)
                            update.Invoke(io, new object[] { on ? 100 : 0, 0 });
                    }
                    catch { }

                    // Force IOEntity currentEnergy-style fields when present
                    try
                    {
                        var flags = System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic;
                        foreach (var name in new[] { "currentEnergy", "currentEnergyConsumption", "desiredPower" })
                        {
                            var f = typeof(IOEntity).GetField(name, flags);
                            if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(float)))
                            {
                                if (f.FieldType == typeof(int)) f.SetValue(io, on ? 100 : 0);
                                else f.SetValue(io, on ? 100f : 0f);
                            }
                        }
                    }
                    catch { }
                }

                try
                {
                    var sl = lightEnt as SearchLight;
                    if (sl != null)
                    {
                        var aim = typeof(SearchLight).GetField("aimDir",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic);
                        if (aim != null && aim.FieldType == typeof(Vector3))
                            aim.SetValue(sl, (Vector3.down + Vector3.forward * 0.25f).normalized);

                        // Turn the motor on so the beam sweeps (vanilla search behaviour)
                        try
                        {
                            var m = typeof(SearchLight).GetMethod("SetFlag",
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic);
                        }
                        catch { }
                    }
                }
                catch { }

                lightEnt.SendNetworkUpdateImmediate();
            }
            catch (Exception ex)
            {
                DebugLog($"PowerEntityLight: {ex.Message}");
            }
        }

        private void EnsureAttachedSearchLight(BaseEntity parent)
        {
            if (parent == null || parent.IsDestroyed) return;
            try
            {
                if (parent.children != null)
                {
                    foreach (var child in parent.children)
                    {
                        if (child == null || child.IsDestroyed) continue;
                        string cn = (child.ShortPrefabName ?? "").ToLowerInvariant();
                        if (cn.Contains("searchlight"))
                        {
                            PowerEntityLight(child, true);
                            return;
                        }
                    }
                }

                const string path = "assets/prefabs/deployable/search light/searchlight.deployed.prefab";
                var lightEnt = GameManager.server.CreateEntity(path, parent.transform.position, parent.transform.rotation, true);
                if (lightEnt == null)
                {
                    DebugLog("SearchLight CreateEntity failed");
                    return;
                }

                lightEnt.enableSaving = false;
                lightEnt.SetParent(parent, worldPositionStays: false);
                lightEnt.transform.localPosition = new Vector3(0f, -2f, 4f);
                lightEnt.transform.localRotation = Quaternion.Euler(70f, 0f, 0f);
                lightEnt.Spawn();
                PowerEntityLight(lightEnt, true);
                DebugLog($"Attached+powered SearchLight under {parent.ShortPrefabName}");
            }
            catch (Exception ex)
            {
                DebugLog($"EnsureAttachedSearchLight: {ex.Message}");
            }
        }

        private void RemoveAttachedSearchLights(BaseEntity parent)
        {
            if (parent == null || parent.IsDestroyed || parent.children == null) return;
            try
            {
                var toKill = new List<BaseEntity>();
                foreach (var child in parent.children)
                {
                    if (child == null || child.IsDestroyed) continue;
                    string cn = (child.ShortPrefabName ?? child.PrefabName ?? "").ToLowerInvariant();
                    if (cn.Contains("searchlight") || cn.Contains("search light"))
                        toKill.Add(child);
                }
                for (int i = 0; i < toKill.Count; i++)
                {
                    try { toKill[i].Kill(); } catch { }
                }
            }
            catch { }
        }

        private void ApplyPatrolHeliNightLights(BaseEntity entity)
        {
            ApplyEntityNightLights(entity);
        }

        /// <summary>
        /// Only runs a full pass when day/night actually flips — avoids scanning 70k+ ents every minute.
        /// Spawn paths still call ApplyEntityNightLights directly.
        /// </summary>
        private void UpdateAllSpawnedNightLights()
        {
            if (!_ready) return;
            bool night = IsNightTime();
            if (_lastNightLightsIsNight.HasValue && _lastNightLightsIsNight.Value == night)
                return; // no transition — skip expensive world scan
            _lastNightLightsIsNight = night;

            int applied = 0;
            const int maxPerPass = 40; // hard cap per transition
            EnsureLiveIndex();

            void TryLight(ulong id, bool always)
            {
                if (applied >= maxPerPass) return;
                var be = FindIndexedEntity(id);
                if (be == null || be.IsDestroyed) return;
                string prefab = (be.PrefabName ?? be.ShortPrefabName ?? "").ToLowerInvariant();
                if (!EntityHasLights(prefab)) return;
                if (!always && !_vehicleFirstSeen.ContainsKey(id)) return;
                ApplyEntityNightLights(be);
                applied++;
            }

            // Always-lit event vehicles from live index
            foreach (var id in _idxPatrolHeli) TryLight(id, true);
            foreach (var id in _idxChinook) TryLight(id, true);
            foreach (var id in _idxBradley) TryLight(id, true);
            foreach (var id in _idxCargo) TryLight(id, true);
            // Tracked player vehicles (tug / sub / attack / balloon …)
            foreach (var id in _vehicleFirstSeen.Keys)
            {
                if (applied >= maxPerPass) break;
                if (_idxPatrolHeli.Contains(id) || _idxChinook.Contains(id) ||
                    _idxBradley.Contains(id) || _idxCargo.Contains(id))
                    continue;
                TryLight(id, false);
            }

            if (config != null && config.Debug)
                DebugLog($"NightLights transition night={night} applied={applied}");
        }

        /// <summary>
        /// Insert cruise-altitude points so no hop exceeds maxHop metres.
        /// </summary>
        private List<Vector3> InsertHeliIntermediatePoints(List<Vector3> raw, float maxHop, float cruiseAlt)
        {
            if (raw == null || raw.Count < 2) return raw ?? new List<Vector3>();
            var result = new List<Vector3> { raw[0] };
            for (int i = 1; i < raw.Count; i++)
            {
                Vector3 a = result[result.Count - 1];
                Vector3 b = raw[i];
                float dist = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
                if (dist > maxHop)
                {
                    int steps = Mathf.CeilToInt(dist / maxHop);
                    for (int s = 1; s < steps; s++)
                    {
                        Vector3 mid = Vector3.Lerp(a, b, s / (float)steps);
                        mid.y = GetHeliCruiseAltitude(mid, cruiseAlt);
                        result.Add(mid);
                    }
                }
                b.y = GetHeliCruiseAltitude(b, cruiseAlt);
                result.Add(b);
            }
            return result;
        }

        /// <summary>
        /// Cruise Y above terrain AND water — coastal/ocean monuments return low HeightMap
        /// values (even negative), which previously put the heli at Y≈10–30.
        /// </summary>
        private static float GetHeliCruiseAltitude(Vector3 pos, float cruiseAbove)
        {
            float ground = 0f;
            float water = 0f;
            try { ground = TerrainMeta.HeightMap.GetHeight(pos); } catch { }
            try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(pos); } catch { }
            float surface = Mathf.Max(ground, water);
            // Absolute floor so we never skim the waves even if maps report odd heights
            return Mathf.Max(surface + cruiseAbove, 100f);
        }

        /// <summary>
        /// Smoothly raise/lower heli altitude over durationSec instead of a one-frame teleport.
        /// Used for inland low-altitude corrections so climb/descent looks natural.
        /// Hard snaps remain only for true off-map recovery.
        /// </summary>
        private void SmoothAdjustHeliAltitude(PatrolHelicopterAI heliAI, float targetY, float durationSec = 3.5f)
        {
            if (heliAI?.helicopterBase == null || heliAI.helicopterBase.IsDestroyed) return;
            var heli = heliAI.helicopterBase;
            float startY = heli.transform.position.y;
            if (Mathf.Abs(startY - targetY) < 8f) return;

            ulong netId = 0;
            try { netId = heli.net?.ID.Value ?? 0; } catch { }

            // Cancel any in-progress smooth on this heli so they don't fight
            if (netId != 0 && _heliAltitudeSmoothTimers.TryGetValue(netId, out var existing))
            {
                existing?.Destroy();
                _heliAltitudeSmoothTimers.Remove(netId);
            }

            // Cap duration so large deltas still finish in a reasonable time (~25 m/s vertical)
            float delta = Mathf.Abs(targetY - startY);
            durationSec = Mathf.Clamp(Mathf.Max(durationSec, delta / 25f), 1.5f, 6f);

            float startedAt = Time.realtimeSinceStartup;
            const float tick = 0.12f;
            Timer smooth = null;
            smooth = timer.Every(tick, () =>
            {
                if (heli == null || heli.IsDestroyed)
                {
                    smooth?.Destroy();
                    if (netId != 0) _heliAltitudeSmoothTimers.Remove(netId);
                    return;
                }

                float u = Mathf.Clamp01((Time.realtimeSinceStartup - startedAt) / durationSec);
                // Smoothstep — ease in/out so climb does not look linear/robotic
                float s = u * u * (3f - 2f * u);
                Vector3 p = heli.transform.position;
                p.y = Mathf.Lerp(startY, targetY, s);
                heli.transform.position = p;
                try { heli.TransformChanged(); } catch { }

                if (u >= 1f)
                {
                    smooth?.Destroy();
                    if (netId != 0) _heliAltitudeSmoothTimers.Remove(netId);
                }
            });

            if (netId != 0)
                _heliAltitudeSmoothTimers[netId] = smooth;
        }

        private PatrolHelicopterAI FindFirstPatrolHeliAI()
        {
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                string prefab = (be.PrefabName ?? "").ToLowerInvariant();
                if (!prefab.Contains("patrolhelicopter") || prefab.Contains("marker") || prefab.Contains("gib"))
                    continue;
                var ai = be.GetComponent<PatrolHelicopterAI>();
                if (ai != null) return ai;
            }
            return null;
        }

        /// <summary>
        /// Lifetime + night lights + deep-sea recovery. If the heli drifts off-map or stays
        /// in Deep Sea during the patrol window, force interest / snap inland.
        /// </summary>
        private void StartHeliLifetimeAndLights(PatrolHelicopterAI heliAI, Vector3? spawnInterest = null)
        {
            if (heliAI == null) return;

            ulong netId = 0;
            try { netId = heliAI.helicopterBase?.net?.ID.Value ?? 0; } catch { }

            if (netId != 0 && _heliRouteTimers.TryGetValue(netId, out var old))
            {
                old?.Destroy();
                _heliRouteTimers.Remove(netId);
            }

            TryExtendHeliLifetime(heliAI);

            float lifetimeMin = config.HeliPatrol != null
                ? Mathf.Clamp(config.HeliPatrol.PatrolLifetimeMinutes, 8f, 45f)
                : 15f;
            float lifetimeSec = lifetimeMin * 60f;
            float cruise = Mathf.Max(80f, config.HeliPatrol?.CruiseAltitude ?? 80f);
            float startedAt = Time.realtimeSinceStartup;
            float lastPosLog = 0f;
            float lastRecovery = -999f;
            float lastLights = -999f;
            int offMapRecoveryCount = 0;
            bool retired = false;
            Vector3 interest = spawnInterest ?? Vector3.zero;
            if (interest.sqrMagnitude < 1f)
            {
                try { interest = GetRandomMonumentInterest(); } catch { interest = Vector3.zero; }
            }

            // Lights at spawn + one follow-up only (monitor throttles further refreshes)
            try
            {
                var baseEnt = heliAI.helicopterBase as BaseEntity;
                if (baseEnt != null)
                {
                    ApplyPatrolHeliNightLights(baseEnt);
                    timer.Once(12f, () => { if (baseEnt != null && !baseEnt.IsDestroyed) ApplyPatrolHeliNightLights(baseEnt); });
                }
            }
            catch { }

            DebugLog($"PatrolHeli lifetime+lights armed for {lifetimeMin:F0}m (off-map recovery, lights throttled)");

            Timer mon = null;
            mon = timer.Every(30f, () =>
            {
                if (heliAI == null || heliAI.helicopterBase == null || heliAI.helicopterBase.IsDestroyed)
                {
                    mon?.Destroy();
                    if (netId != 0) _heliRouteTimers.Remove(netId);
                    return;
                }

                Vector3 pos = heliAI.helicopterBase.transform.position;
                float age = Time.realtimeSinceStartup - startedAt;
                float now = Time.realtimeSinceStartup;

                // Night lights at most every ~100s (was every 20s — major CPU/log spam)
                if (now - lastLights >= 100f)
                {
                    lastLights = now;
                    try
                    {
                        var be = heliAI.helicopterBase as BaseEntity;
                        if (be != null) ApplyPatrolHeliNightLights(be);
                    }
                    catch { }
                }

                if (now - lastPosLog > 90f)
                {
                    lastPosLog = now;
                    DebugLog($"PatrolHeli roaming {PositionToGrid(pos)} ({pos.x:F0},{pos.y:F0},{pos.z:F0}) age={age / 60f:F1}m");
                }

                // --- Recovery during active patrol ---
                // OFF-MAP: always snap + force interest (no hard cap — AI keeps trying ocean path)
                // LOW only (still inland): raise altitude only — do NOT ForceHeliLandInterest
                if (!retired && age < lifetimeSec * 0.90f)
                {
                    bool off = IsDeepSeaOrOffMap(pos);
                    bool low = pos.y < 70f;

                    if (off && (now - lastRecovery) >= 25f)
                    {
                        lastRecovery = now;
                        offMapRecoveryCount++;
                        try
                        {
                            Vector3 target = interest;
                            if (target.sqrMagnitude < 1f || IsDeepSeaOrOffMap(target))
                                target = PickHeliInterestAwayFrom(pos);
                            interest = target;

                            TryExtendHeliLifetime(heliAI);
                            SnapHeliToMapEdgeToward(heliAI, target, cruise);
                            ForceHeliLandInterest(heliAI, target, forceMove: true);

                            DebugLog($"PatrolHeli OFFMAP recovery #{offMapRecoveryCount} -> {PositionToGrid(target)} age={age / 60f:F1}m");
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"PatrolHeli OFFMAP recovery failed: {ex.Message}");
                        }
                    }
                    else if (!off && low && (now - lastRecovery) >= 60f)
                    {
                        lastRecovery = now;
                        try
                        {
                            float wantY = Mathf.Max(GetHeliCruiseAltitude(pos, cruise), 110f);
                            if (pos.y < wantY - 15f)
                            {
                                // Gradual climb instead of one-frame Y teleport
                                SmoothAdjustHeliAltitude(heliAI, wantY, 3.5f);
                                // Let AI also seek cruise altitude at current interest
                                Vector3 elevInterest = interest;
                                if (elevInterest.sqrMagnitude < 1f)
                                    elevInterest = pos;
                                elevInterest.y = wantY;
                                ForceHeliLandInterest(heliAI, elevInterest, forceMove: false);
                                DebugLog($"PatrolHeli smooth altitude -> Y={wantY:F0} at {PositionToGrid(pos)} age={age / 60f:F1}m");
                            }
                        }
                        catch { }
                    }
                }

                if (!retired && age >= lifetimeSec)
                {
                    retired = true;
                    DebugLog($"PatrolHeli patrol time up ({lifetimeMin:F0}m) — Retire()");
                    RetirePatrolHeliToDeepSea(heliAI);
                }

                // After retire, clean up if still lingering far off-map
                if (retired && age >= lifetimeSec + 180f)
                {
                    if (IsDeepSeaOrOffMap(pos) || age >= lifetimeSec + 300f)
                    {
                        try
                        {
                            DebugLog("PatrolHeli post-retire cleanup Kill()");
                            heliAI.helicopterBase.Kill();
                        }
                        catch { }
                        mon?.Destroy();
                        if (netId != 0) _heliRouteTimers.Remove(netId);
                    }
                }
            });

            if (netId != 0)
                _heliRouteTimers[netId] = mon;
        }

        /// <summary>
        /// Pick an inland monument 500–1200 m away. Rejects edge / deep-sea targets.
        /// </summary>
        private Vector3 PickHeliInterestAwayFrom(Vector3 from)
        {
            EnsureHeliInterestCache();
            if (_heliInterestCache == null || _heliInterestCache.Count == 0)
                return GetRandomMonumentInterest();

            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            float inlandLimit = half * 0.78f;

            Vector3 best = from;
            float bestScore = -1f;
            int samples = Mathf.Min(16, _heliInterestCache.Count);
            for (int i = 0; i < samples; i++)
            {
                Vector3 c = _heliInterestCache[UnityEngine.Random.Range(0, _heliInterestCache.Count)];
                if (Mathf.Abs(c.x) > inlandLimit || Mathf.Abs(c.z) > inlandLimit)
                    continue;
                if (IsDeepSeaOrOffMap(c))
                    continue;

                float d = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(c.x, 0f, c.z));
                // Peak score in 600–1100 m band; hard reject outside 450–1300
                if (d < 450f || d > 1300f) continue;
                float score = d < 600f ? d : (d > 1100f ? (1300f - d) : 1000f + (800f - Mathf.Abs(d - 850f)));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            if (bestScore < 0f)
            {
                // Fallback: any inland cache point not deep
                for (int i = 0; i < _heliInterestCache.Count; i++)
                {
                    Vector3 c = _heliInterestCache[i];
                    if (Mathf.Abs(c.x) <= inlandLimit && Mathf.Abs(c.z) <= inlandLimit && !IsDeepSeaOrOffMap(c))
                        return c;
                }
                return GetRandomMonumentInterest();
            }
            return best;
        }


        /// <summary>
        /// True if position is outside the playable grid or labelled Deep Sea.
        /// </summary>
        private bool IsDeepSeaOrOffMap(Vector3 pos)
        {
            try
            {
                float half = GetWorldHalf();
                // Beach / first water cell is playable. Only the true void
                // (well past worldsize/2) is off-map. z=-1818 on 3611 aborted a live tour.
                float limit = half + 200f;
                if (Mathf.Abs(pos.x) > limit || Mathf.Abs(pos.z) > limit)
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Force AI into Move toward an inland interest (HeliControl / HeliSignals pattern).
        /// ExitCurrentState + State_Move_Enter is required — SetTargetDestination alone is ignored in Orbit.
        /// </summary>
        private void ForceHeliLandInterest(PatrolHelicopterAI heliAI, Vector3 interest, bool forceMove = true)
        {
            if (heliAI == null) return;
            try
            {
                float cruise = Mathf.Max(80f, config.HeliPatrol?.CruiseAltitude ?? 80f);
                interest.y = GetHeliCruiseAltitude(interest, cruise);

                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var t = heliAI.GetType();

                // 1) Leave Orbit / Strafe / stuck Move + clear chase targets
                if (forceMove)
                {
                    try
                    {
                        var exit = t.GetMethod("ExitCurrentState", flags, null, Type.EmptyTypes, null);
                        exit?.Invoke(heliAI, null);
                    }
                    catch { }

                    foreach (var name in new[] { "ClearAimTarget", "CancelOrbit" })
                    {
                        try
                        {
                            var m = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
                            m?.Invoke(heliAI, null);
                        }
                        catch { }
                    }

                    try
                    {
                        var fList = t.GetField("_targetList", flags) ?? t.GetField("targetList", flags);
                        if (fList != null)
                        {
                            var list = fList.GetValue(heliAI) as System.Collections.IList;
                            list?.Clear();
                        }
                    }
                    catch { }
                }

                // 2) Interest zone (orbit center once it arrives)
                try
                {
                    var fOrig = t.GetField("interestZoneOrigin", flags);
                    if (fOrig != null && fOrig.FieldType == typeof(Vector3))
                        fOrig.SetValue(heliAI, interest);
                    var fHas = t.GetField("hasInterestZone", flags);
                    if (fHas != null && fHas.FieldType == typeof(bool))
                        fHas.SetValue(heliAI, true);
                }
                catch { }

                // 3) Destination fields
                foreach (var name in new[] { "destination", "_destination", "targetDestination" })
                {
                    try
                    {
                        var f = t.GetField(name, flags);
                        if (f != null && f.FieldType == typeof(Vector3))
                            f.SetValue(heliAI, interest);
                    }
                    catch { }
                }

                // 4) Force Move state (strongest)
                if (forceMove)
                {
                    try
                    {
                        var moveEnter = t.GetMethod("State_Move_Enter", flags, null, new[] { typeof(Vector3) }, null);
                        if (moveEnter != null)
                        {
                            moveEnter.Invoke(heliAI, new object[] { interest });
                        }
                        else
                        {
                            // Some builds use State_Move_Enter with no args after destination is set
                            moveEnter = t.GetMethod("State_Move_Enter", flags, null, Type.EmptyTypes, null);
                            moveEnter?.Invoke(heliAI, null);
                        }
                    }
                    catch { }
                }

                // 5) SetTargetDestination only — NEVER SetInitialDestination mid-flight
                try { heliAI.SetTargetDestination(interest, 0.35f); } catch { }

                // Cruise throttle — 1.0 is full combat speed and looks unnatural on patrol
                try
                {
                    var fThr = t.GetField("targetThrottleSpeed", flags);
                    if (fThr != null && fThr.FieldType == typeof(float))
                        fThr.SetValue(heliAI, 0.45f);
                }
                catch { }
            }
            catch (Exception ex)
            {
                DebugLog($"ForceHeliLandInterest: {ex.Message}");
            }
        }

        /// <summary>
        /// Last-resort recovery: teleport heli firmly inland toward the interest at cruise altitude.
        /// </summary>
        private void SnapHeliToMapEdgeToward(PatrolHelicopterAI heliAI, Vector3 interest, float cruise)
        {
            if (heliAI?.helicopterBase == null || heliAI.helicopterBase.IsDestroyed) return;
            try
            {
                float half = 2000f;
                try { half = TerrainMeta.Size.x * 0.5f; } catch { }

                Vector3 flat = new Vector3(interest.x, 0f, interest.z);
                if (flat.sqrMagnitude < 1f) flat = Vector3.forward;
                flat.Normalize();

                // Firmly inland (0.72 * half) — not on the deep-sea rim
                Vector3 edge = flat * (half * 0.72f);
                Vector3 toInterest = interest - edge;
                toInterest.y = 0f;
                if (toInterest.sqrMagnitude > 1f)
                    edge += toInterest.normalized * Mathf.Min(200f, toInterest.magnitude * 0.3f);

                float limit = half * 0.85f;
                edge.x = Mathf.Clamp(edge.x, -limit, limit);
                edge.z = Mathf.Clamp(edge.z, -limit, limit);
                edge.y = GetHeliCruiseAltitude(edge, cruise);

                var heli = heliAI.helicopterBase;
                heli.transform.position = edge;
                try { heli.TransformChanged(); } catch { }
                // Do not set Rigidbody velocity — kinematic body (1.6.65 console spam)

                DebugLog($"PatrolHeli snapped inland {PositionToGrid(edge)} ({edge.x:F0},{edge.y:F0},{edge.z:F0})");
            }
            catch (Exception ex)
            {
                DebugLog($"SnapHeliToMapEdgeToward: {ex.Message}");
            }
        }

        /// <summary>
        /// Send heli to deep ocean past the map edge, then kill once it's far out.
        /// Matches vanilla "leaves the area" behaviour instead of vanishing over land.
        /// </summary>
        private void RetirePatrolHeliToDeepSea(PatrolHelicopterAI heliAI)
        {
            if (heliAI == null || heliAI.helicopterBase == null || heliAI.helicopterBase.IsDestroyed)
                return;

            BaseEntity heli = heliAI.helicopterBase;
            Vector3 exit = GetDeepSeaExitPoint(heli.transform.position);
            float cruise = Mathf.Max(80f, config.HeliPatrol?.CruiseAltitude ?? 80f);
            exit.y = GetHeliCruiseAltitude(exit, cruise);

            // Prefer native Retire() when present
            bool retired = false;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var m = heliAI.GetType().GetMethod("Retire", flags);
                if (m != null && m.GetParameters().Length == 0)
                {
                    m.Invoke(heliAI, null);
                    retired = true;
                    DebugLog("PatrolHeli AI.Retire() called");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"PatrolHeli Retire() failed: {ex.Message}");
            }

            // Always push a deep-sea destination so it flies out even if Retire is a no-op
            try
            {
                heliAI.SetTargetDestination(exit, 0.25f);
                DebugLog($"PatrolHeli egress -> {PositionToGrid(exit)} ({exit.x:F0},{exit.y:F0},{exit.z:F0})");
            }
            catch (Exception ex)
            {
                DebugLog($"PatrolHeli egress dest failed: {ex.Message}");
            }

            // Keep reasserting egress for a few minutes, then force-kill if still on map
            int ticks = 0;
            Timer egressTimer = null;
            egressTimer = timer.Every(20f, () =>
            {
                ticks++;
                if (heli == null || heli.IsDestroyed)
                {
                    egressTimer?.Destroy();
                    return;
                }

                Vector3 p = heli.transform.position;
                float half = TerrainMeta.Size.x * 0.5f;
                bool offMap = Mathf.Abs(p.x) > half + 50f || Mathf.Abs(p.z) > half + 50f;

                if (offMap || ticks >= 18) // ~6 minutes max
                {
                    DebugLog($"PatrolHeli left map (offMap={offMap}) — removing");
                    try { heli.Kill(); } catch { }
                    egressTimer?.Destroy();
                    InvalidateEntityCache();
                    return;
                }

                // Reassert deep-sea target so AI doesn't turn back
                try
                {
                    if (heliAI != null && !heli.IsDestroyed)
                        heliAI.SetTargetDestination(exit, 0.25f);
                }
                catch { }
            });
        }

        /// <summary>
        /// Point past the map boundary in the direction the heli is already heading (or away from center).
        /// </summary>
        private Vector3 GetDeepSeaExitPoint(Vector3 from)
        {
            float half = TerrainMeta.Size.x * 0.5f;
            Vector3 outward = new Vector3(from.x, 0f, from.z);
            if (outward.sqrMagnitude < 1f)
                outward = Vector3.forward;
            outward.Normalize();

            // Well past the edge so it reads as deep sea
            float dist = half + 400f;
            return new Vector3(outward.x * dist, 0f, outward.z * dist);
        }

        /// <summary>
        /// Keep the patrol heli alive long enough for a full route (vanilla often retires early).
        /// Called at spawn and every route tick.
        /// </summary>
        private void TryExtendHeliLifetime(PatrolHelicopterAI heliAI)
        {
            if (heliAI == null) return;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var type = heliAI.GetType();

                // Cancel retirement if already triggered
                foreach (var name in new[] { "isRetiring", "retiring", "_isRetiring" })
                {
                    var f = type.GetField(name, flags);
                    if (f != null && f.FieldType == typeof(bool))
                        f.SetValue(heliAI, false);
                }

                foreach (var name in new[]
                {
                    "maxTimeOnMap", "timeOnMap", "retirementRate", "destination_min_time",
                    "interestZoneLifetime", "maxOrbitDuration", "deathTime", "lastSeenPlayerTime",
                    "spawnTime", "timeSinceSpawn", "lastEphemeronVisibleTime", "noTargetTime"
                })
                {
                    var f = type.GetField(name, flags);
                    if (f == null) continue;
                    try
                    {
                        if (f.FieldType == typeof(float))
                        {
                            if (name.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0)
                                f.SetValue(heliAI, 2400f); // 40 min
                            else if (name.IndexOf("retire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("death", StringComparison.OrdinalIgnoreCase) >= 0)
                                f.SetValue(heliAI, 0f);
                            else if (name.IndexOf("lastSeen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("Ephemeron", StringComparison.OrdinalIgnoreCase) >= 0)
                                f.SetValue(heliAI, Time.realtimeSinceStartup);
                            else if (name.IndexOf("timeOnMap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("timeSince", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("noTarget", StringComparison.OrdinalIgnoreCase) >= 0)
                                f.SetValue(heliAI, 0f);
                        }
                    }
                    catch { }
                }

                try
                {
                    var hasZone = type.GetField("hasInterestZone", flags);
                    if (hasZone != null && hasZone.FieldType == typeof(bool))
                        hasZone.SetValue(heliAI, true);
                    var origin = type.GetField("_interestZoneOrigin", flags)
                                 ?? type.GetField("interestZoneOrigin", flags);
                    if (origin != null && origin.FieldType == typeof(Vector3) && heliAI.helicopterBase != null)
                        origin.SetValue(heliAI, heliAI.helicopterBase.transform.position);
                }
                catch { }

                // Also poke the PatrolHelicopter entity for leave-timers if present
                try
                {
                    var heli = heliAI.helicopterBase;
                    if (heli != null && !heli.IsDestroyed)
                    {
                        var ht = heli.GetType();
                        foreach (var name in new[] { "timeSinceDeployed", "spawnTime", "deathTime" })
                        {
                            var f = ht.GetField(name, flags);
                            if (f != null && f.FieldType == typeof(float))
                            {
                                if (name.IndexOf("death", StringComparison.OrdinalIgnoreCase) >= 0)
                                    f.SetValue(heli, 0f);
                                else
                                    f.SetValue(heli, 0f);
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                DebugLog($"TryExtendHeliLifetime: {ex.Message}");
            }
        }

        private bool SpawnChinook()
        {
            // Vanilla-style CH47: spawn the scientists entity and let CH47AIBrain + CH47PathFinder
            // own pathing and native DropCrate. No forced waypoints, no forced crates, no DelayedKill.
            CleanupStuckChinooks();

            int before = CountChinooks();
            int maxChinook = config.VehicleLimits?.MaxChinooks ?? 1;
            DebugLog($"Chinook start - ch47 before: {before} (max {maxChinook})");
            if (before >= maxChinook)
            {
                DebugLog("Chinook skipped - limit reached");
                BroadcastKey("SpawnVehicleLimit", "Chinook", maxChinook);
                return false;
            }

            Vector3 flyToward;
            GetChinookInboundPosition(out flyToward);
            float cruise = Mathf.Max(90f, config.ChinookPatrol != null ? config.ChinookPatrol.CruiseAltitude : 110f);
            Vector3 pos = GetMapEdgeApproach(flyToward, cruise);
            try
            {
                Vector3 inward = flyToward;
                inward.y = 0f;
                if (inward.sqrMagnitude < 1f) inward = Vector3.forward;
                inward.Normalize();
                // Opposite rim from the interest so the bird flies across the map
                float half = GetWorldHalf();
                Vector3 edge = -inward * (half * 0.92f);
                for (int i = 0; i < 8; i++)
                {
                    float r = half * (0.92f - i * 0.03f);
                    Vector3 cand = -inward * r;
                    if (IsDeepSeaOrOffMap(cand)) continue;
                    if (IsOverWater(cand) || i >= 5)
                    {
                        edge = cand;
                        break;
                    }
                }
                float limit = half * 0.94f;
                edge.x = Mathf.Clamp(edge.x, -limit, limit);
                edge.z = Mathf.Clamp(edge.z, -limit, limit);
                edge.y = GetHeliCruiseAltitude(edge, cruise);
                pos = edge;
            }
            catch { }

            DebugLog($"Chinook vanilla inbound at {PositionToGrid(pos)} ({pos.x:F0}, {pos.y:F0}, {pos.z:F0}) -> interest {PositionToGrid(flyToward)}");

            CH47HelicopterAIController ch47 = null;
            try
            {
                Vector3 flat = flyToward - pos;
                flat.y = 0f;
                Quaternion rot = flat.sqrMagnitude > 1f
                    ? Quaternion.LookRotation(flat.normalized)
                    : Quaternion.identity;

                var ent = GameManager.server.CreateEntity(
                    "assets/prefabs/npc/ch47/ch47scientists.entity.prefab", pos, rot, true);
                if (ent == null)
                {
                    DebugLog("Chinook CreateEntity failed (ch47scientists.entity)");
                    return false;
                }

                ch47 = ent as CH47HelicopterAIController;
                if (ch47 == null)
                {
                    DebugLog("Chinook cast to CH47HelicopterAIController failed");
                    try { ent.Kill(); } catch { }
                    return false;
                }

                ch47.Spawn();
                InvalidateEntityCache();
                Vector3 hull = ch47.transform.position;
                Puts($"[Events] Chinook ingress {PositionToGrid(hull)} ({hull.x:F0},{hull.z:F0}) -> {PositionToGrid(flyToward)}");
                if (IsDeepSeaOrOffMap(hull))
                {
                    Puts("[Events] Chinook hull opened off-map — killing and using map-center spawn");
                    try { ch47.Kill(); } catch { }
                    ch47 = null;
                    float halfC = 2000f;
                    try { halfC = TerrainMeta.Size.x * 0.5f; } catch { }
                    pos = new Vector3(0f, 0f, 0f);
                    pos.y = GetHeliCruiseAltitude(pos, cruise);
                    var ent2 = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/ch47/ch47scientists.entity.prefab", pos, Quaternion.identity, true);
                    ch47 = ent2 as CH47HelicopterAIController;
                    if (ch47 == null)
                    {
                        try { if (ent2 != null) ent2.Kill(); } catch { }
                        return false;
                    }
                    ch47.Spawn();
                    hull = ch47.transform.position;
                    Puts($"[Events] Chinook retry hull {PositionToGrid(hull)} ({hull.x:F0},{hull.z:F0})");
                    if (IsDeepSeaOrOffMap(hull))
                    {
                        Puts("[Events] Chinook retry still off-map — abort spawn");
                        try { ch47.Kill(); } catch { }
                        return false;
                    }
                }

                // Only respawn if the hull is truly in the void / under-map
                Vector3 spawnedAt = ch47.transform.position;
                if (IsDeepSeaOrOffMap(spawnedAt) || spawnedAt.y < -30f || spawnedAt.y > 800f)
                {
                    DebugLog($"Chinook void spawn at {PositionToGrid(spawnedAt)} — border respawn");
                    try { ch47.Kill(); } catch { }
                    pos = GetChinookBorderSpawnPosition(flyToward);
                    flat = flyToward - pos; flat.y = 0f;
                    rot = flat.sqrMagnitude > 1f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
                    ent = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/ch47/ch47scientists.entity.prefab", pos, rot, true);
                    ch47 = ent as CH47HelicopterAIController;
                    if (ch47 == null)
                    {
                        try { ent?.Kill(); } catch { }
                        return false;
                    }
                    ch47.Spawn();
                    InvalidateEntityCache();
                    DebugLog($"Chinook border respawn OK at {PositionToGrid(pos)}");
                }

                // Hold the crate until MinTourMinutesBeforeDrop so the tour is visible
                try { ch47.numCrates = 1; } catch { }

                try { ch47.SetMoveTarget(flyToward); } catch { }
                DebugLog("Chinook vanilla AI — one inbound target, then brain owns flight");
            }
            catch (Exception ex)
            {
                DebugLog($"Chinook spawn exception: {ex.Message}");
                return false;
            }

            if (ch47 == null || ch47.IsDestroyed)
                return false;

            _chinookCrateWatchUntil = Time.realtimeSinceStartup + 20f * 60f;
            DebugLog("Chinook native-drop watch armed for 20 minutes");
            timer.Once(90f, CleanupStuckChinooks);
            timer.Once(300f, CleanupStuckChinooks);

            bool oilRig = false;
            try { oilRig = ch47.ShouldLand(); } catch { }
            if (!oilRig && (config.ChinookPatrol == null || config.ChinookPatrol.Enabled))
                StartChinookLifetimeAndTour(ch47, flyToward);
            else
                DebugLog("Chinook tour skipped (oil-rig lander or ChinookPatrol disabled)");

            var ch47Capture = ch47;
            timer.Once(5f, () =>
            {
                InvalidateEntityCache();
                int after = CountChinooks();
                DebugLog($"Chinook check - ch47scientists after: {after} (was {before})");
                if (config != null && config.Debug)
                    DebugListMatching("ch47scientists");
                if (ch47Capture != null && !ch47Capture.IsDestroyed)
                    DebugLog($"Chinook live altitude Y={ch47Capture.transform.position.y:F0} at {PositionToGrid(ch47Capture.transform.position)}");
                CleanupInvalidByName("servergibs_ch47", -50f);
                CleanupInvalidByName("oilfireball", -50f);
            });
            return true;
        }

        private Vector3 GetChinookInboundPosition(out Vector3 flyToward)
        {
            // Prefer inland major monuments so drops stay on the playable grid
            flyToward = GetInlandMonumentTarget();

            // Shared cruise band for spawn + target so CH47 AI climbs/descends gradually
            // (large Y deltas between inbound spawn and target caused abrupt elevation changes)
            float groundTarget = 0f;
            try { groundTarget = TerrainMeta.HeightMap.GetHeight(flyToward); } catch { }
            float cruiseY = Mathf.Clamp(Mathf.Max(groundTarget + 110f, 140f), 140f, 260f);
            flyToward.y = cruiseY;

            Vector3 offset = new Vector3(
                UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
            if (offset.sqrMagnitude < 0.01f) offset = Vector3.forward;
            offset.Normalize();

            float halfSafe = 1600f;
            try { halfSafe = TerrainMeta.Size.x * 0.5f * 0.62f; } catch { }
            flyToward.x = Mathf.Clamp(flyToward.x, -halfSafe, halfSafe);
            flyToward.z = Mathf.Clamp(flyToward.z, -halfSafe, halfSafe);

            Vector3 pos = flyToward + offset * UnityEngine.Random.Range(280f, 420f);
            pos.x = Mathf.Clamp(pos.x, -halfSafe, halfSafe);
            pos.z = Mathf.Clamp(pos.z, -halfSafe, halfSafe);
            float ground = 0f;
            try { ground = TerrainMeta.HeightMap.GetHeight(pos); } catch { }
            // Spawn near the same cruise altitude as the fly target (±20m) so the first
            // leg is mostly lateral, not a vertical pop.
            float spawnY = Mathf.Clamp(Mathf.Max(ground + 110f, cruiseY + UnityEngine.Random.Range(-15f, 20f)), 140f, 280f);
            pos.y = spawnY;
            return pos;
        }

        /// <summary>
        /// Monument well inside the map (not harbors / edge junk) for Chinook flight target.
        /// </summary>
        private Vector3 GetInlandMonumentTarget()
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            float inlandLimit = half * 0.72f; // stay away from outer 28% ring

            try
            {
                if (TerrainMeta.Path?.Monuments != null)
                {
                    var mons = TerrainMeta.Path.Monuments
                        .Where(m => m != null)
                        .OrderBy(_ => UnityEngine.Random.value)
                        .ToList();
                    foreach (var mon in mons)
                    {
                        string n = (mon.name ?? "").ToLowerInvariant();
                        if (n.Contains("harvestable") || n.Contains("underwater") || n.Contains("swamp") ||
                            n.Contains("small") || n.Contains("tiny") || n.Contains("power_sub") ||
                            n.Contains("harbor") || n.Contains("fishing") || n.Contains("oilrig") ||
                            n.Contains("oil_rig"))
                            continue;
                        Vector3 p = mon.transform.position;
                        if (Mathf.Abs(p.x) > inlandLimit || Mathf.Abs(p.z) > inlandLimit)
                            continue;
                        float water = 0f, terrain = p.y;
                        try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                        try { terrain = TerrainMeta.HeightMap.GetHeight(p); } catch { }
                        if (terrain < water + 1f) continue;
                        return p;
                    }
                }
            }
            catch { }

            // Fallback: random inland ground (not coast)
            for (int i = 0; i < 20; i++)
            {
                Vector3 c = GetRandomMapPosition(false);
                if (Mathf.Abs(c.x) <= inlandLimit && Mathf.Abs(c.z) <= inlandLimit)
                    return c;
            }
            return GetRandomMapPosition(false);
        }

        /// <summary>
        /// CH47 AI sometimes flings the heli to Y=100k–600k (invisible, blocks MaxChinooks).
        /// Normal cruise/climb is often Y=100–900 — do NOT treat that as stuck.
        /// Only kill true stratosphere / under-map / far off-grid root vehicles.
        /// </summary>
        private bool IsChinookInLivableArea(Vector3 p)
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            const float margin = 100f;
            if (Mathf.Abs(p.x) > half - margin || Mathf.Abs(p.z) > half - margin)
                return false;
            if (p.y < -30f || p.y > 800f)
                return false;
            return true;
        }

        private Vector3 GetChinookBorderSpawnPosition(Vector3 toward)
        {
            float half = 1600f;
            try { half = TerrainMeta.Size.x * 0.5f * 0.72f; } catch { }
            Vector3 inward = toward;
            inward.y = 0f;
            if (inward.sqrMagnitude < 1f) inward = Vector3.forward;
            inward.Normalize();
            Vector3 pos = -inward * (half * 0.85f);
            pos.x = Mathf.Clamp(pos.x, -half, half);
            pos.z = Mathf.Clamp(pos.z, -half, half);
            float ground = 0f;
            try { ground = TerrainMeta.HeightMap.GetHeight(pos); } catch { }
            pos.y = Mathf.Clamp(Mathf.Max(ground + 100f, 130f), 130f, 220f);
            return pos;
        }

        private List<Vector3> BuildChinookInlandWaypoints(Vector3 primary, int maxPts)
        {
            var list = new List<Vector3>(maxPts);
            float halfSafe = 1600f;
            try { halfSafe = TerrainMeta.Size.x * 0.5f * 0.58f; } catch { }

            void AddClamped(Vector3 p)
            {
                p.x = Mathf.Clamp(p.x, -halfSafe, halfSafe);
                p.z = Mathf.Clamp(p.z, -halfSafe, halfSafe);
                float g = 0f;
                try { g = TerrainMeta.HeightMap.GetHeight(p); } catch { }
                p.y = Mathf.Clamp(Mathf.Max(g + 100f, 130f), 130f, 220f);
                for (int i = 0; i < list.Count; i++)
                {
                    Vector3 d = list[i] - p; d.y = 0f;
                    if (d.sqrMagnitude < 80f * 80f) return;
                }
                list.Add(p);
            }

            AddClamped(primary);
            try
            {
                if (TerrainMeta.Path?.Monuments != null)
                {
                    foreach (var mon in TerrainMeta.Path.Monuments.Where(m => m != null).OrderBy(_ => UnityEngine.Random.value).Take(24))
                    {
                        if (list.Count >= maxPts) break;
                        string n = (mon.name ?? "").ToLowerInvariant();
                        if (n.Contains("underwater") || n.Contains("oilrig") || n.Contains("oil_rig")) continue;
                        if (n.Contains("fishing") || n.Contains("harbor") || n.Contains("harbour")) continue;
                        Vector3 p = mon.transform.position;
                        if (Mathf.Abs(p.x) > halfSafe * 1.05f || Mathf.Abs(p.z) > halfSafe * 1.05f) continue;
                        AddClamped(p);
                    }
                }
            }
            catch { }

            int guard = 0;
            while (list.Count < Mathf.Min(5, maxPts) && guard++ < 8)
                AddClamped(primary + new Vector3(UnityEngine.Random.Range(-180f, 180f), 0f, UnityEngine.Random.Range(-180f, 180f)));
            return list;
        }

        private void StopChinookTour(ulong netId)
        {
            if (netId == 0) return;
            Timer tm;
            if (_chinookTourTimers.TryGetValue(netId, out tm))
            {
                tm?.Destroy();
                _chinookTourTimers.Remove(netId);
            }
            _chinookDroppedIds.Remove(netId);
        }

        /// <summary>
        /// Vanilla CH47: one inbound SetMoveTarget, numCrates=1 already set at spawn.
        /// Do not hop, do not abort beaches, do not force egress. Brain flies and drops.
        /// We only despawn after lifetime or a true void (y<-30 or |xz| > worldsize/2+250 for 30s).
        /// </summary>
        private void StartChinookLifetimeAndTour(CH47HelicopterAIController chinook, Vector3 firstInterest)
        {
            if (chinook == null || chinook.IsDestroyed) return;
            try { if (chinook.ShouldLand()) return; } catch { }

            ulong netId = 0;
            try { netId = chinook.net != null ? chinook.net.ID.Value : 0UL; } catch { }
            StopChinookTour(netId);

            var cfg = config != null ? config.ChinookPatrol : null;
            float lifetimeMin = cfg != null ? Mathf.Clamp(cfg.PatrolLifetimeMinutes, 10f, 30f) : 15f;
            float lifetimeSec = lifetimeMin * 60f;
            float cruise = cfg != null ? Mathf.Max(80f, cfg.CruiseAltitude) : 110f;

            Vector3 interest = firstInterest;
            if (interest.sqrMagnitude < 1f)
                interest = GetRandomMonumentInterest();
            interest.y = GetHeliCruiseAltitude(interest, cruise);
            TrySetChinookMoveTarget(chinook, interest);

            float startedAt = Time.realtimeSinceStartup;
            float voidSince = -1f;
            bool dropped = false;

            Puts($"[Events] Chinook vanilla AI {lifetimeMin:F0}m -> {PositionToGrid(interest)}");

            Timer mon = null;
            mon = timer.Every(10f, () =>
            {
                if (chinook == null || chinook.IsDestroyed)
                {
                    mon?.Destroy();
                    StopChinookTour(netId);
                    return;
                }

                try { if (chinook.ShouldLand()) { mon?.Destroy(); StopChinookTour(netId); return; } }
                catch { }

                Vector3 p = chinook.transform.position;
                float now = Time.realtimeSinceStartup;
                float age = now - startedAt;

                if (!dropped && _chinookDroppedIds.Contains(netId))
                    dropped = true;

                bool hardVoid = p.y < -30f || p.y > 800f;
                float half = GetWorldHalf();
                bool pastWorld = Mathf.Abs(p.x) > half + 250f || Mathf.Abs(p.z) > half + 250f;
                if (hardVoid || pastWorld)
                {
                    if (voidSince < 0f) voidSince = now;
                    if (now - voidSince >= 30f)
                    {
                        Puts($"[Events] Chinook void despawn at {PositionToGrid(p)} ({p.x:F0},{p.z:F0})");
                        FinishChinookInland(chinook, "void");
                        mon?.Destroy();
                        StopChinookTour(netId);
                    }
                    return;
                }
                voidSince = -1f;

                if (age >= lifetimeSec)
                {
                    Puts($"[Events] Chinook lifetime {lifetimeMin:F0}m at {PositionToGrid(p)} drop={dropped}");
                    FinishChinookInland(chinook, dropped ? "lifetime" : "lifetime-empty");
                    mon?.Destroy();
                    StopChinookTour(netId);
                }
            });

            if (netId != 0)
                _chinookTourTimers[netId] = mon;
        }

        private float GetWorldHalf()
        {
            float half = 0f;
            try { half = ConVar.Server.worldsize * 0.5f; } catch { }
            if (half < 50f)
            {
                try { half = TerrainMeta.Size.x * 0.5f; } catch { half = 2000f; }
            }
            return half;
        }

        private bool IsOverWater(Vector3 p)
        {
            try
            {
                if (TerrainMeta.TopologyMap != null)
                {
                    int topo = TerrainMeta.TopologyMap.GetTopology(p);
                    if ((topo & TerrainTopology.OCEAN) != 0) return true;
                }
            }
            catch { }
            try
            {
                float ground = TerrainMeta.HeightMap.GetHeight(p);
                return ground < 0.75f;
            }
            catch { }
            return false;
        }

        private float GetChinookSoftRim()
        {
            float half = GetWorldHalf();
            if (half < 50f)
            {
                try { half = ConVar.Server.worldsize * 0.5f; } catch { half = 2000f; }
            }
            // Must stay near the real edge. 0.78*half (~1408 on 3611) flagged
            // V14/Y12 as "rim" and aborted playable coastal tours.
            return half + 40f;
        }

        private Vector3 ClampChinookInland(Vector3 p, float frac)
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            if (half < 50f)
            {
                try { half = ConVar.Server.worldsize * 0.5f; } catch { half = 2000f; }
            }
            float lim = half * Mathf.Clamp(frac, 0.35f, 0.75f);
            p.x = Mathf.Clamp(p.x, -lim, lim);
            p.z = Mathf.Clamp(p.z, -lim, lim);
            return p;
        }

        private Vector3 GetChinookEgressPoint(Vector3 from, float cruise)
        {
            float half = GetWorldHalf();
            Vector3 flat = new Vector3(from.x, 0f, from.z);
            float radial = flat.magnitude;
            // Already on the beach (O23 / L0): stay. Pushing along +from flies into the void
            // (1.13.28 armed at O23 then died at z=-2059).
            if (radial > half * 0.78f)
            {
                Vector3 stay = from;
                stay.y = GetHeliCruiseAltitude(from, cruise);
                return stay;
            }
            if (flat.sqrMagnitude < 1f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 edge = flat * (half * 0.78f);
            float limit = half * 0.80f;
            edge.x = Mathf.Clamp(edge.x, -limit, limit);
            edge.z = Mathf.Clamp(edge.z, -limit, limit);
            edge.y = GetHeliCruiseAltitude(edge, cruise);
            return edge;
        }

        private void FinishChinookInland(CH47HelicopterAIController chinook, string reason)
        {
            if (chinook == null || chinook.IsDestroyed) return;
            Puts($"[Events] Chinook finish ({reason}) at {PositionToGrid(chinook.transform.position)}");
            StripChinookCrew(chinook);
            var hull = chinook;
            timer.Once(0.6f, () =>
            {
                try
                {
                    if (hull == null || hull.IsDestroyed) return;
                    StripChinookCrew(hull);
                    SafeKill(hull);
                }
                catch { }
            });
        }

        /// <summary>
        /// NPCs are seats, not "players". DismountAllPlayers misses them; DelayedKill
        /// then logs "Scientist was killed by ch47scientists.entity".
        /// </summary>
        private void StripChinookCrew(CH47HelicopterAIController chinook)
        {
            if (chinook == null || chinook.IsDestroyed) return;
            try { chinook.DismountAllPlayers(); } catch { }

            var doomed = new List<BaseEntity>();
            try
            {
                var veh = chinook as BaseVehicle;
                if (veh != null)
                {
                    try { veh.DismountAllPlayers(); } catch { }
                    try
                    {
                        if (veh.mountPoints != null)
                        {
                            for (int i = 0; i < veh.mountPoints.Count; i++)
                            {
                                try
                                {
                                    var mp = veh.mountPoints[i];
                                    BasePlayer seated = null;
                                    if (mp.mountable != null) seated = mp.mountable.GetMounted();
                                    if (seated != null && !seated.IsDestroyed) doomed.Add(seated);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                if (chinook.children != null)
                {
                    for (int i = 0; i < chinook.children.Count; i++)
                    {
                        var c = chinook.children[i];
                        if (c == null || c.IsDestroyed) continue;
                        doomed.Add(c);
                    }
                }
            }
            catch { }

            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    var be = ent as BaseEntity;
                    if (be == null || be.IsDestroyed || be == chinook) continue;
                    try
                    {
                        var parent = be.GetParentEntity();
                        if (parent == chinook)
                            doomed.Add(be);
                    }
                    catch { }
                    try
                    {
                        var npc = be as BasePlayer;
                        if (npc != null && npc.GetMountedVehicle() == chinook)
                            doomed.Add(npc);
                    }
                    catch { }
                }
            }
            catch { }

            for (int i = 0; i < doomed.Count; i++)
            {
                var e = doomed[i];
                if (e == null || e.IsDestroyed) continue;
                try { e.SetParent(null, true, false); } catch { }
                try { e.Kill(); } catch { }
            }
        }

        private void StartChinookGuidance(CH47HelicopterAIController chinook, List<Vector3> waypoints)
        {
            // Legacy entry — hybrid tour owns pathing now
            if (chinook == null || chinook.IsDestroyed) return;
            Vector3 first = (waypoints != null && waypoints.Count > 0) ? waypoints[0] : chinook.transform.position;
            StartChinookLifetimeAndTour(chinook, first);
        }

        private void TrySetChinookMoveTarget(CH47HelicopterAIController chinook, Vector3 dest)
        {
            if (chinook == null || chinook.IsDestroyed) return;
            try { chinook.SetMoveTarget(dest); return; } catch { }
            try
            {
                var t = chinook.GetType();
                foreach (var name in new[] { "SetMoveTarget", "SetDestination", "SetTargetDestination" })
                {
                    try
                    {
                        var m = t.GetMethod(name, new[] { typeof(Vector3) });
                        if (m == null) continue;
                        m.Invoke(chinook, new object[] { dest });
                        return;
                    }
                    catch { }
                }
            }
            catch { }
        }



        private void ProtectExistingHackableCrates()
        {
            int n = 0;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    var hc = ent as HackableLockedCrate;
                    if (hc == null || hc.IsDestroyed || hc.net == null) continue;
                    Vector3 p = hc.transform.position;
                    bool hacking = false;
                    try { hacking = hc.IsBeingHacked(); } catch { }
                    bool offshore = Mathf.Abs(p.x) > World.Size * 0.43f || Mathf.Abs(p.z) > World.Size * 0.43f;
                    if (!hacking && !offshore) continue;
                    RegisterEventProtectedCrate(hc, 55f);
                    n++;
                }
            }
            catch { }
            if (n > 0)
                Puts($"[Events] Protected {n} existing hackable crate(s) from despawn");
        }

        private void RegisterEventProtectedCrate(BaseEntity ent, float minutes = 45f)
        {
            if (ent == null || ent.IsDestroyed || ent.net == null) return;
            ulong id = ent.net.ID.Value;
            if (id == 0) return;
            _eventProtectedCrateIds.Add(id);
            _eventProtectedCrateUntil[id] = Time.realtimeSinceStartup + Mathf.Max(5f, minutes) * 60f;
            DebugLog($"Event crate protected net={id} for {minutes:F0}m at {PositionToGrid(ent.transform.position)}");
        }


        private void PruneEventProtectedCrates()
        {
            float now = Time.realtimeSinceStartup;
            _pruneIdBuffer.Clear();
            foreach (var kv in _eventProtectedCrateUntil)
            {
                if (kv.Value <= now) _pruneIdBuffer.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIdBuffer.Count; i++)
            {
                ulong id = _pruneIdBuffer[i];
                _eventProtectedCrateIds.Remove(id);
                _eventProtectedCrateUntil.Remove(id);
            }
        }


        /// <summary>
        /// Tell LiveStatsEventsVehicles to skip idle-despawn for this netId (Core inline event spawns).
        /// Safe no-op when the module is not loaded.
        /// </summary>
        private void NotifyVehiclesModuleProtect(BaseEntity entity, float minutes = 120f)
        {
            if (entity == null || entity.IsDestroyed || entity.net == null) return;
            ulong id = entity.net.ID.Value;
            if (id == 0) return;
            try
            {
                if (LiveStatsEventsVehicles == null || !LiveStatsEventsVehicles.IsLoaded)
                    LiveStatsEventsVehicles = plugins.Find("LiveStatsEventsVehicles");
                if (LiveStatsEventsVehicles == null || !LiveStatsEventsVehicles.IsLoaded) return;
                LiveStatsEventsVehicles.Call("API_ProtectEventVehicle", id, minutes);
                DebugLog($"Notified Vehicles module: protect net={id} for {minutes:F0}m");
            }
            catch (Exception ex)
            {
                DebugLog($"NotifyVehiclesModuleProtect: {ex.Message}");
            }
        }

        /// <summary>Vehicles module / others: do not despawn crates spawned by events.</summary>
        public object API_RegisterEventProtectedCrate(ulong netId, float minutes = 45f)
        {
            if (netId == 0) return false;
            _eventProtectedCrateIds.Add(netId);
            _eventProtectedCrateUntil[netId] = Time.realtimeSinceStartup + Mathf.Max(5f, minutes) * 60f;
            Puts($"[Events] Event crate protected (API) net={netId} for {minutes:F0}m");
            return true;
        }


        public object API_IsEventProtectedCrate(ulong netId)
        {
            if (netId == 0) return false;
            PruneEventProtectedCrates();
            return _eventProtectedCrateIds.Contains(netId);
        }




        private void OnHelicopterDropCrate(CH47HelicopterAIController heli)
        {
            if (heli == null || heli.IsDestroyed) return;
            try { if (heli.ShouldLand()) return; } catch { }
            try
            {
                ulong hid = heli.net != null ? heli.net.ID.Value : 0UL;
                if (hid != 0) _chinookDroppedIds.Add(hid);
            }
            catch { }
            // Grid comes from the crate entity in OnEntitySpawned (1.5s later).
            // Heli xz and crate landing cell often differ by one grid.
            Puts("[Events] Chinook DropCrate — waiting for crate entity grid");

            // Protect the native drop from Vehicles idle despawn
            try
            {
                Vector3 hp = heli.transform.position;
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    var hc = ent as HackableLockedCrate;
                    if (hc == null || hc.net == null) continue;
                    Vector3 d = hc.transform.position - hp;
                    if (d.sqrMagnitude > 80f * 80f) continue;
                    RegisterEventProtectedCrate(hc, 90f);
                }
            }
            catch { }
        }


        private void CleanupStuckChinooks()

        {
            // Broken cases were Y≈200000+. CH47 AI often climbs to 2k–8k while pathing.
            // Only treat true stratosphere as stuck (Y=5968 was incorrectly killed before).
            // Only true void / stratosphere — soft edge (pathing overshoot) is handled by StartChinookGuidance
            const float maxValidY = 20000f;
            const float minValidY = -80f;
            float half = 0f;
            try { half = TerrainMeta.Size.x * 0.5f + 900f; } catch { half = 6000f; }

            EnsureLiveIndex();
            var doomed = new List<BaseEntity>();
            foreach (var id in _idxChinook)
            {
                var be = FindIndexedEntity(id);
                if (be == null) continue;
                Vector3 p = be.transform.position;
                bool badY = p.y > maxValidY || p.y < minValidY;
                bool badXZ = half > 0f && (Mathf.Abs(p.x) > half || Mathf.Abs(p.z) > half);
                if (badY || badXZ)
                {
                    doomed.Add(be);
                    if (config != null && config.Debug)
                        DebugLog($"Stuck Chinook removed at ({p.x:F0},{p.y:F0},{p.z:F0}) prefab={be.ShortPrefabName}");
                }
            }
            for (int i = 0; i < doomed.Count; i++)
            {
                ulong doomedId = 0;
                try { doomedId = doomed[i]?.net?.ID.Value ?? 0; } catch { }
                SafeKill(doomed[i]);
                if (doomedId != 0)
                {
                    _idxChinook.Remove(doomedId);
                    if (_heliRouteTimers.TryGetValue(doomedId, out var ht))
                    {
                        ht?.Destroy();
                        _heliRouteTimers.Remove(doomedId);
                    }
                    if (_heliAltitudeSmoothTimers.TryGetValue(doomedId, out var at))
                    {
                        at?.Destroy();
                        _heliAltitudeSmoothTimers.Remove(doomedId);
                    }
                }
            }
            if (doomed.Count > 0)
            {
                InvalidateEntityCache();
                Puts($"[Events] Removed {doomed.Count} stuck/invisible Chinook entity(ies)");
            }
        }

        /// <summary>
        /// Chinook crates are dropped by vanilla CH47 AI, not by SpawnHackableCrate.
        /// Announce grid when a locked crate appears during the post-Chinook watch window.
        /// </summary>

        /// <summary>
        /// HOT HOOK — early-out unless this netId is in one of our tracked sets.
        /// Keeps the live index and route timers coherent without a world scan.
        /// </summary>
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null) return;
            ulong id = entity.net.ID.Value;
            if (id == 0) return;

            bool known =
                _idxCargo.Remove(id) |
                _idxChinook.Remove(id) |
                _idxPatrolHeli.Remove(id) |
                _idxBradley.Remove(id) |
                _idxCargoPlane.Remove(id) |
                _idxCrate.Remove(id) |
                _idxAttackHeli.Remove(id) |
                _idxTug.Remove(id) |
                _idxSub.Remove(id) |
                _idxBalloon.Remove(id) |
                _idxMini.Remove(id) |
                _idxScrap.Remove(id) |
                _pluginBradleyIds.Remove(id) |
                _pluginCargoPlaneIds.Remove(id) |
                _eventProtectedCrateIds.Remove(id) |
                _announcedCrateIds.Remove(id) |
                _airfieldGuardIds.Remove(id) |
                _trackedScientistIds.Remove(id);

            if (_eventProtectedCrateUntil.Count > 0)
                _eventProtectedCrateUntil.Remove(id);
            if (_cargoTracks.Count > 0)
                _cargoTracks.Remove(id);
            if (_vehicleFirstSeen.Count > 0)
                _vehicleFirstSeen.Remove(id);

            if (_heliRouteTimers.Count > 0 && _heliRouteTimers.TryGetValue(id, out var ht))
            {
                ht?.Destroy();
                _heliRouteTimers.Remove(id);
                known = true;
            }
            if (_chinookTourTimers.Count > 0 && _chinookTourTimers.TryGetValue(id, out var ct))
            {
                ct?.Destroy();
                _chinookTourTimers.Remove(id);
                known = true;
            }
            if (_chinookDroppedIds.Count > 0)
                _chinookDroppedIds.Remove(id);
            if (_heliAltitudeSmoothTimers.Count > 0 && _heliAltitudeSmoothTimers.TryGetValue(id, out var at))
            {
                at?.Destroy();
                _heliAltitudeSmoothTimers.Remove(id);
                known = true;
            }
            if (_bradleyRouteTimers.Count > 0 && _bradleyRouteTimers.TryGetValue(id, out var bt))
            {
                bt?.Destroy();
                _bradleyRouteTimers.Remove(id);
                known = true;
            }

            if (known && _airfieldChinookId == id)
            {
                _airfieldChinookId = 0;
                _airfieldChinookUntil = -1f;
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null) return;
            var be = entity as BaseEntity;
            if (be == null || be.IsDestroyed) return;

            string prefab = be.ShortPrefabName ?? "";
            if (prefab.Length == 0) return;

            ulong spawnId = 0;
            try { spawnId = be.net?.ID.Value ?? 0; } catch { }
            if (spawnId != 0)
                IndexLiveEntity(spawnId, prefab, be);

            if (_pendingF15Relocate && Time.realtimeSinceStartup <= _pendingF15Until &&
                (IsF15PrefabName(prefab) || IsF15PrefabName(be.ShortPrefabName)))
            {
                NextTick(() => TryRelocatePendingF15(be));
                return;
            }

            // Kill vanilla cargo planes even before _ready (boot EventSchedule race)
            if (config != null && config.DisableVanillaEvents &&
                prefab.IndexOf("cargo_plane", StringComparison.OrdinalIgnoreCase) >= 0 &&
                prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
            {
                ulong planeId = 0;
                try { planeId = be.net?.ID.Value ?? 0; } catch { }
                if (planeId == 0 || !_pluginCargoPlaneIds.Contains(planeId))
                {
                    NextTick(() =>
                    {
                        if (be == null || be.IsDestroyed) return;
                        ulong id2 = 0;
                        try { id2 = be.net?.ID.Value ?? 0; } catch { }
                        if (id2 != 0 && _pluginCargoPlaneIds.Contains(id2)) return;
                        try
                        {
                            Puts($"[Events] Removed unowned cargo plane (vanilla/boot) at {PositionToGrid(be.transform.position)}");
                            SafeKill(be);
                        }
                        catch { }
                    });
                    return;
                }
            }

            if (!_ready) return;

            // Suppress vanilla Launch Site Bradley when we own event scheduling
            if (config != null && config.DisableVanillaEvents &&
                prefab.IndexOf("bradleyapc", StringComparison.OrdinalIgnoreCase) >= 0 &&
                prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) < 0)
            {
                ulong id = 0;
                try { id = be.net?.ID.Value ?? 0; } catch { }
                if (id != 0 && _pluginBradleyIds.Contains(id))
                    return;
                // Brief window after our SpawnBradley so we don't race-kill our own entity
                if (Time.realtimeSinceStartup < _suppressVanillaBradleyUntil)
                    return;

                NextTick(() =>
                {
                    if (be == null || be.IsDestroyed) return;
                    ulong id2 = 0;
                    try { id2 = be.net?.ID.Value ?? 0; } catch { }
                    if (id2 != 0 && _pluginBradleyIds.Contains(id2)) return;
                    try
                    {
                        DebugLog($"Vanilla Bradley blocked at {PositionToGrid(be.transform.position)}");
                        SafeKill(be);
                    }
                    catch { }
                });
                return;
            }

            if (prefab.IndexOf("codelockedhackablecrate", StringComparison.OrdinalIgnoreCase) < 0 &&
                prefab.IndexOf("hackablecrate", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            if (prefab.IndexOf("signal", StringComparison.OrdinalIgnoreCase) >= 0) return;

            // Our direct SpawnHackableCrate already broadcasts — skip double announce
            if (Time.realtimeSinceStartup < _suppressHackableAnnounceUntil)
                return;

            // Only announce crates tied to a recent Chinook event (not oil-rig / random / cargo crates)
            if (Time.realtimeSinceStartup > _chinookCrateWatchUntil)
                return;

            ulong netId = 0;
            try { netId = be.net?.ID.Value ?? 0; } catch { }
            if (netId != 0)
            {
                if (_announcedCrateIds.Contains(netId)) return;
                _announcedCrateIds.Add(netId);
                if (_announcedCrateIds.Count > 32)
                    _announcedCrateIds.Clear();
            }

            timer.Once(1.5f, () =>
            {
                if (be == null || be.IsDestroyed) return;
                Vector3 p = be.transform.position;
                if (!IsInlandPlayableCratePosition(p, out string reason))
                {
                    DebugLog($"Chinook crate ignored ({reason}) at ({p.x:F0},{p.y:F0},{p.z:F0})");
                    return;
                }
                // Plugin-forced event crates are pre-registered — always announce
                ulong crateId = 0;
                try { crateId = be.net?.ID.Value ?? 0; } catch { }
                bool eventCrate = crateId != 0 && _eventProtectedCrateIds.Contains(crateId);
                if (!eventCrate && !IsNearLiveChinook(p, 350f))
                {
                    if (IsNearLiveCargoShip(p, 200f))
                        DebugLog($"Chinook crate ignored (cargo-ship crate) at {PositionToGrid(p)}");
                    else
                        DebugLog($"Chinook crate ignored (no nearby CH47) at {PositionToGrid(p)} ({p.x:F0},{p.y:F0},{p.z:F0})");
                    return;
                }
                string grid = PositionToGrid(p);
                Puts($"[Events] Chinook crate at {grid} ({p.x:F0},{p.z:F0})");
                BroadcastKey("SpawnHackableNear", grid);
                NotifyEventFeed("HackableCrate", "spawn", grid);
            });
        }

        /// <summary>True if any live ch47scientists is within radius of pos (index-backed).</summary>
        private bool IsNearLiveChinook(Vector3 pos, float radius)
        {
            EnsureLiveIndex();
            float r2 = radius * radius;
            foreach (var id in _idxChinook)
            {
                var be = FindIndexedEntity(id);
                if (be == null) continue;
                try
                {
                    Vector3 d = be.transform.position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude <= r2) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>True if any live cargo ship hull is within radius of pos (index-backed).</summary>
        private bool IsNearLiveCargoShip(Vector3 pos, float radius)
        {
            EnsureLiveIndex();
            float r2 = radius * radius;
            foreach (var id in _idxCargo)
            {
                var be = FindIndexedEntity(id);
                if (be == null) continue;
                try
                {
                    Vector3 d = be.transform.position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude <= r2) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Reject ocean / off-map / under-water crates so we don't announce Deep Sea loot.
        /// </summary>
        private bool IsInlandPlayableCratePosition(Vector3 p, out string reason)
        {
            reason = null;
            if (p.y < -20f)
            {
                reason = "under-map";
                return false;
            }
            if (IsDeepSeaOrOffMap(p))
            {
                reason = "deep-sea/off-map";
                return false;
            }
            try
            {
                float water = 0f;
                float terrain = p.y;
                try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                try { terrain = TerrainMeta.HeightMap.GetHeight(p); } catch { }
                // Crate sitting in open ocean (well below water surface, terrain underwater)
                if (terrain < water - 1f && p.y < water + 1f)
                {
                    reason = "open-water";
                    return false;
                }
            }
            catch { }
            string grid = PositionToGrid(p);
            if (string.Equals(grid, "Deep Sea", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Deep Sea grid";
                return false;
            }
            return true;
        }

        private void CleanupInvalidByName(string fragment, float minY)
        {
            var doomed = new List<BaseEntity>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                string prefab = be.ShortPrefabName ?? be.PrefabName;
                if (prefab == null) continue;
                if (prefab.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (be.transform.position.y < minY)
                    doomed.Add(be);
            }
            for (int i = 0; i < doomed.Count; i++)
            {
                if (IsLootCrateEntity(doomed[i])) continue;
                try { doomed[i].Kill(); } catch { }
            }
            if (doomed.Count > 0)
                InvalidateEntityCache();
        }

        private int CountBradleys()
        {
            return CountFromIndex(_idxBradley);
        }

        private int CountAttackHelis()
        {
            return CountFromIndex(_idxAttackHeli);
        }

        // Bradley / scientist road nodes — reject cliffs, steep embankments, buried/floating points
        private const float RoadNodeMinSpacing = 8f;
        private const float RoadMaxSlopeDegrees = 25f;
        private const float RoadMaxHeightJump = 4.5f;
        private const float RoadGroundSnapTolerance = 2.5f;
        private const float RoadBodyClearance = 2.2f;
        private const float RoadBodyRadius = 1.4f;
        private const float RoadHeightCacheCell = 4f; // quantize raycache to 4 m cells

        private static int _roadCollisionMask = -1;
        private static readonly RaycastHit[] _roadRayHits = new RaycastHit[4];
        // Quantized height-check cache: key -> (valid? packed with groundY)
        private static readonly Dictionary<long, float> _roadHeightCache = new Dictionary<long, float>(1024);
        // Negative sentinel = invalid node (stored as NaN-adjacent via separate set is heavier; use bitmask dict)
        private static readonly HashSet<long> _roadHeightInvalid = new HashSet<long>();

        private static int RoadCollisionMask
        {
            get
            {
                if (_roadCollisionMask < 0)
                {
                    try
                    {
                        // Terrain + World only for bulk path build — much cheaper than full mask
                        _roadCollisionMask = LayerMask.GetMask("Terrain", "World", "Default");
                    }
                    catch
                    {
                        _roadCollisionMask = ~0;
                    }
                    if (_roadCollisionMask == 0)
                        _roadCollisionMask = ~0;
                }
                return _roadCollisionMask;
            }
        }

        private static long RoadHeightKey(float x, float z)
        {
            // 4 m cells — adjacent nodes share one raycast
            int ix = Mathf.FloorToInt(x / RoadHeightCacheCell);
            int iz = Mathf.FloorToInt(z / RoadHeightCacheCell);
            return ((long)ix << 32) ^ (uint)iz;
        }

        private static void ClearRoadHeightCache()
        {
            _roadHeightCache.Clear();
            _roadHeightInvalid.Clear();
        }

        /// <summary>
        /// True if a -> b is traversable for Bradley / walking NPCs.
        /// Pure math — no physics.
        /// </summary>
        private static bool IsTraversableRoadSegment(Vector3 a, Vector3 b,
            float maxSlopeDeg = RoadMaxSlopeDegrees, float maxHeightJump = RoadMaxHeightJump)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
            float vertical = Mathf.Abs(b.y - a.y);

            if (horizontal < 0.5f)
                return vertical < 0.75f;

            if (vertical > maxHeightJump)
                return false;

            float slopeDeg = Mathf.Atan2(vertical, horizontal) * Mathf.Rad2Deg;
            return slopeDeg <= maxSlopeDeg;
        }

        /// <summary>
        /// HeightMap-only ground Y (no raycast). Used for hot paths.
        /// </summary>
        private static float GetTerrainGroundY(Vector3 pos)
        {
            try { return TerrainMeta.HeightMap.GetHeight(pos) + 0.5f; }
            catch { return pos.y; }
        }

        /// <summary>
        /// Height-based ground validation with quantized raycast cache + RaycastNonAlloc.
        /// checkBody: expensive CheckSphere — only enable during cache build for suspicious nodes.
        /// </summary>
        private static bool IsValidRoadNodeHeight(Vector3 pos, out float groundY, bool checkBody = false)
        {
            groundY = pos.y;
            long key = RoadHeightKey(pos.x, pos.z);

            if (_roadHeightInvalid.Contains(key))
                return false;
            if (_roadHeightCache.TryGetValue(key, out float cachedY))
            {
                groundY = cachedY;
                return true;
            }

            try
            {
                float terrainY = pos.y;
                try { terrainY = TerrainMeta.HeightMap.GetHeight(pos); } catch { }

                Vector3 origin = new Vector3(pos.x, Mathf.Max(pos.y, terrainY) + 12f, pos.z);
                const float rayLen = 30f;

                int hits = Physics.RaycastNonAlloc(
                    origin, Vector3.down, _roadRayHits, rayLen,
                    RoadCollisionMask, QueryTriggerInteraction.Ignore);

                if (hits <= 0)
                {
                    float water = 0f;
                    try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(pos); } catch { }
                    if (terrainY < water + 0.5f)
                    {
                        _roadHeightInvalid.Add(key);
                        return false;
                    }
                    groundY = terrainY + 0.5f;
                    _roadHeightCache[key] = groundY;
                    return true;
                }

                // Closest hit (NonAlloc order is not guaranteed sorted on all Unity versions)
                float bestDist = float.MaxValue;
                float bestY = terrainY;
                for (int i = 0; i < hits; i++)
                {
                    float d = _roadRayHits[i].distance;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestY = _roadRayHits[i].point.y;
                    }
                }

                groundY = bestY + 0.5f;
                float delta = bestY - terrainY;

                if (delta > RoadGroundSnapTolerance + 1.5f || delta < -RoadGroundSnapTolerance)
                {
                    _roadHeightInvalid.Add(key);
                    return false;
                }

                // Body clearance only when requested AND ray delta is suspicious (saves most CheckSphere cost)
                if (checkBody && Mathf.Abs(delta) > 1.0f)
                {
                    Vector3 bodyHigh = new Vector3(pos.x, groundY + RoadBodyClearance, pos.z);
                    if (Physics.CheckSphere(bodyHigh, RoadBodyRadius * 0.6f, RoadCollisionMask, QueryTriggerInteraction.Ignore))
                    {
                        _roadHeightInvalid.Add(key);
                        return false;
                    }
                }

                _roadHeightCache[key] = groundY;
                return true;
            }
            catch
            {
                groundY = pos.y;
                return true;
            }
        }

        /// <summary>
        /// Mid-segment ditch/hump check. HeightMap first; raycast only if borderline.
        /// </summary>
        private static bool IsSegmentHeightClear(Vector3 a, Vector3 b)
        {
            try
            {
                Vector3 mid = new Vector3((a.x + b.x) * 0.5f, 0f, (a.z + b.z) * 0.5f);
                float midTerrain = GetTerrainGroundY(mid);
                float avgEnd = (a.y + b.y) * 0.5f;
                float delta = midTerrain - avgEnd;

                // Clear by HeightMap alone
                if (Mathf.Abs(delta) <= RoadMaxHeightJump * 0.7f)
                    return true;

                // Hard reject without physics
                if (Mathf.Abs(delta) > RoadMaxHeightJump * 1.5f)
                    return false;

                // Borderline -> one cached raycast
                if (!IsValidRoadNodeHeight(mid, out float midY, checkBody: false))
                    return false;
                if (midY < avgEnd - RoadMaxHeightJump) return false;
                if (midY > avgEnd + RoadMaxHeightJump) return false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Map identity used to decide whether the static road cache can be reused after a soft reload.
        /// </summary>
        private static string GetMapIdentity()
        {
            try
            {
                return $"{ConVar.Server.level}|{ConVar.Server.seed}|{ConVar.Server.worldsize}";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Reuse static road cache across oxide.reload when seed/size/level match; otherwise rebuild.
        /// </summary>
        private void EnsureRoadCache()
        {
            string identity = GetMapIdentity();
            if (_staticRoadCache != null && _staticRoadCache.Count > 0 &&
                string.Equals(_staticRoadMapIdentity, identity, StringComparison.Ordinal))
            {
                _roadCache.Clear();
                _roadCache.AddRange(_staticRoadCache);
                Puts($"[Events] Road cache restored from previous load ({_roadCache.Count} roads, map={identity})");
                return;
            }
            BuildRoadCache();
        }

        /// <summary>
        /// Cache every road once at boot. TerrainMeta.Path.Roads -> Path.Points.
        /// Drops water nodes, near-duplicates, and steep/cliff segments.
        /// Result is also stored in static fields so soft reloads skip the rebuild.
        /// </summary>
        private void BuildRoadCache()
        {
            _roadCache.Clear();
            _roadXings.Clear();
            _roadEdges.Clear();
            ClearRoadHeightCache();
            try
            {
                var roads = TerrainMeta.Path?.Roads;
                if (roads == null || roads.Count == 0)
                {
                    Puts("[Events] Road cache: no TerrainMeta.Path.Roads");
                    _staticRoadCache = null;
                    _staticRoadMapIdentity = null;
                    return;
                }

                int rawTotal = 0;
                int slopeDropped = 0;
                int heightDropped = 0;

                foreach (var road in roads)
                {
                    if (road == null) continue;
                    if (!TryGetRoadPoints(road, out Vector3[] raw) || raw == null || raw.Length < 4)
                        continue;

                    float width = 0f;
                    try
                    {
                        var wField = road.GetType().GetField("Width") ?? road.GetType().GetField("width");
                        if (wField != null) width = Convert.ToSingle(wField.GetValue(road));
                        else
                        {
                            var wProp = road.GetType().GetProperty("Width") ?? road.GetType().GetProperty("width");
                            if (wProp != null) width = Convert.ToSingle(wProp.GetValue(road, null));
                        }
                    }
                    catch { }

                    var cached = new CachedRoad { Width = width };
                    Vector3 last = Vector3.zero;
                    bool hasLast = false;

                    for (int i = 0; i < raw.Length; i++)
                    {
                        Vector3 p = raw[i];
                        p.y = TerrainMeta.HeightMap.GetHeight(p) + 0.5f;
                        rawTotal++;

                        float water = 0f;
                        try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                        if (p.y < water + 0.5f) continue;

                        // Height-based collision (cached raycast; body check only if suspicious)
                        if (!IsValidRoadNodeHeight(p, out float groundY, checkBody: true))
                        {
                            heightDropped++;
                            continue;
                        }
                        p.y = groundY;

                        if (hasLast)
                        {
                            float seg = Vector3.Distance(
                                new Vector3(p.x, 0f, p.z), new Vector3(last.x, 0f, last.z));
                            if (seg < RoadNodeMinSpacing) continue;

                            if (!IsTraversableRoadSegment(last, p))
                            {
                                slopeDropped++;
                                continue;
                            }
                            if (!IsSegmentHeightClear(last, p))
                            {
                                heightDropped++;
                                continue;
                            }
                        }

                        cached.Points.Add(p);
                        last = p;
                        hasLast = true;
                    }

                    // Second pass: slope + mid-segment height between kept neighbors
                    if (cached.Points.Count >= 3)
                    {
                        var cleaned = new List<Vector3>(cached.Points.Count) { cached.Points[0] };
                        for (int i = 1; i < cached.Points.Count; i++)
                        {
                            Vector3 prev = cleaned[cleaned.Count - 1];
                            Vector3 cur = cached.Points[i];
                            if (!IsTraversableRoadSegment(prev, cur))
                            {
                                slopeDropped++;
                                continue;
                            }
                            if (!IsSegmentHeightClear(prev, cur))
                            {
                                heightDropped++;
                                continue;
                            }
                            cleaned.Add(cur);
                        }
                        cached.Points = cleaned;
                    }

                    if (cached.Points.Count < 8) continue;

                    float length = 0f;
                    for (int i = 1; i < cached.Points.Count; i++)
                    {
                        length += Vector3.Distance(
                            new Vector3(cached.Points[i].x, 0f, cached.Points[i].z),
                            new Vector3(cached.Points[i - 1].x, 0f, cached.Points[i - 1].z));
                    }
                    cached.Length = length;
                    _roadCache.Add(cached);
                }

                _roadCache.Sort((a, b) =>
                {
                    int c = b.Width.CompareTo(a.Width);
                    return c != 0 ? c : b.Length.CompareTo(a.Length);
                });

                Puts($"[Events] Road cache: {_roadCache.Count} roads " +
                     $"(best width={(_roadCache.Count > 0 ? _roadCache[0].Width : 0):F1}, " +
                     $"pts={(_roadCache.Count > 0 ? _roadCache[0].Points.Count : 0)}, " +
                     $"slope-filtered={slopeDropped}, height-filtered={heightDropped}, raw={rawTotal})");

                // Persist for soft reloads (oxide.reload) on the same map
                _staticRoadCache = new List<CachedRoad>(_roadCache);
                _staticRoadMapIdentity = GetMapIdentity();
            }
            catch (Exception ex)
            {
                Puts($"[Events] Road cache failed: {ex.Message}");
                _staticRoadCache = null;
                _staticRoadMapIdentity = null;
            }
        }

        /// <summary>
        /// Bradley route from a full asphalt road (RoadBradley style).
        /// Does NOT random-slice mid-road — walks the entire road from one end.
        /// Spacing ~12–15 m; slope ≤ 25° and height jumps already filtered in cache + here.
        /// maxPoints only caps extremely long highways (takes a prefix from the start end).
        /// </summary>
        private bool BradleyPointDry(Vector3 p)
        {
            float ground = p.y;
            try { if (TerrainMeta.HeightMap != null) ground = TerrainMeta.HeightMap.GetHeight(p); } catch { }
            float water = ground;
            try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
            float depth = water - ground;
            try
            {
                float overall = WaterLevel.GetOverallWaterDepth(p, true, true, null);
                if (overall > depth) depth = overall;
            }
            catch { }
            if (depth > 0.28f) return false;
            if (ground < 0.5f && depth > 0.08f) return false;
            if (NearPlayerCompound(p)) return false;
            try
            {
                if (TerrainMeta.TopologyMap == null) return depth <= 0.28f;
                int topo = TerrainMeta.TopologyMap.GetTopology(p);
                if ((topo & (int)TerrainTopology.Enum.Ocean) != 0) return false;
                if ((topo & (int)TerrainTopology.Enum.Monument) != 0) return false;
                if ((topo & (int)TerrainTopology.Enum.Building) != 0) return false;
            }
            catch { }
            return true;
        }

        private bool NearPlayerCompound(Vector3 p)
        {
            try
            {
                var tcs = new List<BuildingPrivlidge>();
                Vis.Entities(p, 18f, tcs, Layers.Mask.Deployed);
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
            try
            {
                var blocks = new List<BuildingBlock>();
                Vis.Entities(p, 6f, blocks, Layers.Mask.Construction);
                for (int i = 0; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    if (b == null || b.IsDestroyed) continue;
                    string sn = b.ShortPrefabName ?? "";
                    if (sn.IndexOf("foundation", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private bool BradleySegmentDry(Vector3 a, Vector3 b)
        {
            if (!BradleyPointDry(a) || !BradleyPointDry(b)) return false;
            for (int i = 1; i <= 3; i++)
            {
                float t = i / 4f;
                Vector3 m = new Vector3(a.x + (b.x - a.x) * t, 0f, a.z + (b.z - a.z) * t);
                if (!BradleyPointDry(m)) return false;
            }
            return true;
        }

        private List<Vector3> FilterBradleyRoad(List<Vector3> raw, int maxPoints)
        {
            var outp = new List<Vector3>();
            if (raw == null) return outp;
            Vector3 last = Vector3.zero;
            bool has = false;
            for (int i = 0; i < raw.Count; i++)
            {
                Vector3 p = raw[i];
                if (!BradleyPointDry(p)) continue;
                try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 1.0f; } catch { }
                if (has)
                {
                    float dx = p.x - last.x;
                    float dz = p.z - last.z;
                    if (dx * dx + dz * dz < 36f * 36f) continue;
                    if (!BradleySegmentDry(last, p)) continue;
                }
                outp.Add(p);
                last = p;
                has = true;
                if (outp.Count >= maxPoints) break;
            }
            return outp;
        }

        private List<Vector3> BuildLongAsphaltFromHere(Vector3 here, int maxPoints)
        {
            if (_roadCache.Count == 0) BuildRoadCache();
            var cands = new List<(List<Vector3> pts, float span, float near)>();
            foreach (var road in _roadCache)
            {
                if (road.Points == null || road.Points.Count < 16) continue;
                if (road.Width < 4.5f && road.Length < 220f) continue;
                var dry = FilterBradleyRoad(road.Points, 80);
                if (dry.Count < 8) continue;
                int nearest = 0;
                float best = float.MaxValue;
                for (int i = 0; i < dry.Count; i++)
                {
                    float dx = dry[i].x - here.x;
                    float dz = dry[i].z - here.z;
                    float d = dx * dx + dz * dz;
                    if (d < best) { best = d; nearest = i; }
                }
                if (best > 140f * 140f) continue;
                var slice = new List<Vector3>();
                // Prefer the longer remaining direction — never wrap.
                int forward = dry.Count - nearest;
                int back = nearest + 1;
                if (forward >= back)
                {
                    for (int i = nearest; i < dry.Count; i++) slice.Add(dry[i]);
                }
                else
                {
                    for (int i = nearest; i >= 0; i--) slice.Add(dry[i]);
                }
                if (slice.Count < 6) continue;
                float span = Vector3.Distance(
                    new Vector3(slice[0].x, 0f, slice[0].z),
                    new Vector3(slice[slice.Count - 1].x, 0f, slice[slice.Count - 1].z));
                if (span < 120f) continue;
                cands.Add((slice, span, Mathf.Sqrt(best)));
            }
            if (cands.Count == 0) return new List<Vector3>();
            cands.Sort((a, b) => b.span.CompareTo(a.span));
            int pool = Mathf.Min(cands.Count, 4);
            var pick = cands[UnityEngine.Random.Range(0, pool)];
            DebugLog($"Bradley next-road {PositionToGrid(pick.pts[0])} -> {PositionToGrid(pick.pts[pick.pts.Count - 1])} pts={pick.pts.Count} span={pick.span:F0}m");
            return pick.pts;
        }

        private void EnsureRoadGraph()
        {
            if (_roadEdges.Count > 0 && _roadXings.Count > 0) return;
            if (_roadCache.Count == 0) BuildRoadCache();
            BuildRoadGraph();
        }

        private int GetOrAddXing(Vector3 p)
        {
            for (int i = 0; i < _roadXings.Count; i++)
            {
                float dx = _roadXings[i].x - p.x;
                float dz = _roadXings[i].z - p.z;
                if (dx * dx + dz * dz < 26f * 26f) return i;
            }
            _roadXings.Add(p);
            return _roadXings.Count - 1;
        }

        private void BuildRoadGraph()
        {
            _roadXings.Clear();
            _roadEdges.Clear();
            const float JOIN = 24f;
            for (int r = 0; r < _roadCache.Count; r++)
            {
                var road = _roadCache[r];
                if (road.Points == null || road.Points.Count < 6) continue;
                var pts = road.Points;
                int n = pts.Count;
                var splits = new List<int> { 0, n - 1 };
                for (int i = 2; i < n - 2; i += 2)
                {
                    Vector3 a = pts[i];
                    bool hit = false;
                    for (int o = 0; o < _roadCache.Count && !hit; o++)
                    {
                        if (o == r) continue;
                        var other = _roadCache[o].Points;
                        if (other == null || other.Count < 4) continue;
                        for (int j = 0; j < other.Count; j += 3)
                        {
                            float dx = a.x - other[j].x;
                            float dz = a.z - other[j].z;
                            if (dx * dx + dz * dz < JOIN * JOIN)
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                    if (hit) splits.Add(i);
                }
                splits.Sort();
                for (int s = 0; s < splits.Count - 1; s++)
                {
                    int ia = splits[s];
                    int ib = splits[s + 1];
                    if (ib - ia < 2) continue;
                    var slice = new List<Vector3>();
                    float len = 0f;
                    Vector3 prev = Vector3.zero;
                    bool has = false;
                    for (int k = ia; k <= ib; k++)
                    {
                        Vector3 p = pts[k];
                        if (!BradleyPointDry(p)) continue;
                        try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 1.0f; } catch { }
                        if (has && !BradleySegmentDry(prev, p))
                        {
                            // water gap — start a new slice later
                            continue;
                        }
                        if (has) len += Vector3.Distance(prev, p);
                        slice.Add(p);
                        prev = p;
                        has = true;
                    }
                    if (slice.Count < 3 || len < 40f) continue;
                    int aId = GetOrAddXing(slice[0]);
                    int bId = GetOrAddXing(slice[slice.Count - 1]);
                    if (aId == bId) continue;
                    _roadEdges.Add(new RoadEdge { A = aId, B = bId, Len = len, Pts = slice });
                }
            }
            Puts($"[Events] Road graph xings={_roadXings.Count} edges={_roadEdges.Count}");
        }

        private string BradleyPairKey(Vector3 a, Vector3 b)
        {
            return PositionToGrid(a) + ">" + PositionToGrid(b);
        }

        private bool IsRecentBradleyKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < _recentBradleyKeys.Count; i++)
                if (string.Equals(_recentBradleyKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void RememberBradleyRoute(Vector3 a, Vector3 b)
        {
            string key = BradleyPairKey(a, b);
            string rev = BradleyPairKey(b, a);
            _recentBradleyKeys.RemoveAll(k =>
                string.Equals(k, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, rev, StringComparison.OrdinalIgnoreCase));
            _recentBradleyKeys.Add(key);
            while (_recentBradleyKeys.Count > RecentBradleyLimit)
                _recentBradleyKeys.RemoveAt(0);
        }

        private List<Vector3> BuildBradleyIntersectionRoute(Vector3 here, int maxPoints)
        {
            EnsureRoadGraph();
            var path = new List<Vector3>();
            if (_roadEdges.Count == 0) return BuildLongAsphaltFromHere(here, maxPoints);

            var starts = new List<(int e, float d)>();
            for (int e = 0; e < _roadEdges.Count; e++)
            {
                var ed = _roadEdges[e];
                if (ed.Pts == null || ed.Pts.Count < 2) continue;
                float bestLocal = float.MaxValue;
                for (int i = 0; i < ed.Pts.Count; i += 2)
                {
                    float dx = ed.Pts[i].x - here.x;
                    float dz = ed.Pts[i].z - here.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestLocal) bestLocal = d;
                }
                if (bestLocal < 220f * 220f)
                    starts.Add((e, bestLocal));
            }
            if (starts.Count == 0)
            {
                int bestE0 = -1;
                float bestD0 = float.MaxValue;
                for (int e = 0; e < _roadEdges.Count; e++)
                {
                    var ed = _roadEdges[e];
                    if (ed.Pts == null || ed.Pts.Count < 2) continue;
                    float dx = ed.Pts[0].x - here.x;
                    float dz = ed.Pts[0].z - here.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD0) { bestD0 = d; bestE0 = e; }
                }
                if (bestE0 < 0) return BuildLongAsphaltFromHere(here, maxPoints);
                starts.Add((bestE0, bestD0));
            }
            starts.Sort((a, b) => a.d.CompareTo(b.d));
            var freshStarts = new List<(int e, float d)>();
            for (int i = 0; i < starts.Count && freshStarts.Count < 8; i++)
            {
                var ed = _roadEdges[starts[i].e];
                string k = BradleyPairKey(ed.Pts[0], ed.Pts[ed.Pts.Count - 1]);
                if (!IsRecentBradleyKey(k)) freshStarts.Add(starts[i]);
            }
            if (freshStarts.Count == 0) freshStarts.Add(starts[0]);
            int takeStart = Mathf.Min(freshStarts.Count, 5);
            int bestE = freshStarts[UnityEngine.Random.Range(0, takeStart)].e;
            int bestEnd = 0;

            var used = new HashSet<int>();
            int at = -1;
            // Walk the first edge away from here.
            {
                var ed = _roadEdges[bestE];
                used.Add(bestE);
                float dA = (ed.Pts[0].x - here.x) * (ed.Pts[0].x - here.x) + (ed.Pts[0].z - here.z) * (ed.Pts[0].z - here.z);
                float dB = (ed.Pts[ed.Pts.Count - 1].x - here.x) * (ed.Pts[ed.Pts.Count - 1].x - here.x)
                         + (ed.Pts[ed.Pts.Count - 1].z - here.z) * (ed.Pts[ed.Pts.Count - 1].z - here.z);
                if (dA <= dB)
                {
                    path.AddRange(ed.Pts);
                    at = ed.B;
                }
                else
                {
                    for (int i = ed.Pts.Count - 1; i >= 0; i--) path.Add(ed.Pts[i]);
                    at = ed.A;
                }
            }

            for (int hop = 0; hop < 10 && path.Count < maxPoints; hop++)
            {
                var opts = new List<(int e, int nxt, float len)>();
                for (int e = 0; e < _roadEdges.Count; e++)
                {
                    if (used.Contains(e)) continue;
                    var ed = _roadEdges[e];
                    if (ed.A == at) opts.Add((e, ed.B, ed.Len));
                    else if (ed.B == at) opts.Add((e, ed.A, ed.Len));
                }
                if (opts.Count == 0)
                {
                    // Dead end: allow unused reverse only if nothing else.
                    break;
                }
                opts.Sort((a, b) => b.len.CompareTo(a.len));
                var freshOpts = new List<(int e, int nxt, float len)>();
                for (int i = 0; i < opts.Count; i++)
                {
                    Vector3 nxtP = _roadXings[Mathf.Clamp(opts[i].nxt, 0, _roadXings.Count - 1)];
                    string k = BradleyPairKey(path[path.Count - 1], nxtP);
                    if (!IsRecentBradleyKey(k)) freshOpts.Add(opts[i]);
                }
                if (freshOpts.Count == 0) freshOpts.AddRange(opts);
                int pool = Mathf.Min(freshOpts.Count, 4);
                var pick = freshOpts[UnityEngine.Random.Range(0, pool)];
                used.Add(pick.e);
                var edge = _roadEdges[pick.e];
                if (edge.A == at)
                {
                    for (int i = 1; i < edge.Pts.Count; i++) path.Add(edge.Pts[i]);
                    at = edge.B;
                }
                else
                {
                    for (int i = edge.Pts.Count - 2; i >= 0; i--) path.Add(edge.Pts[i]);
                    at = edge.A;
                }
            }
            if (path.Count >= 4)
            {
                RememberBradleyRoute(path[0], path[path.Count - 1]);
                DebugLog($"Bradley xing-route {PositionToGrid(path[0])} -> {PositionToGrid(path[path.Count - 1])} pts={path.Count} hops={used.Count} xings={_roadXings.Count}");
            }
            return path;
        }

        private List<Vector3> BuildLongAsphaltRoute(int maxPoints)
        {
            var cands = new List<(List<Vector3> pts, float span)>();
            if (_roadCache.Count == 0) BuildRoadCache();
            foreach (var road in _roadCache)
            {
                if (road.Points == null || road.Points.Count < 16) continue;
                if (road.Width < 4.5f && road.Length < 220f) continue;
                var spaced = FilterBradleyRoad(road.Points, maxPoints);
                if (spaced.Count < 8) continue;
                float span = Vector3.Distance(
                    new Vector3(spaced[0].x, 0f, spaced[0].z),
                    new Vector3(spaced[spaced.Count - 1].x, 0f, spaced[spaced.Count - 1].z));
                if (span < 180f) continue;
                cands.Add((spaced, span));
            }
            if (cands.Count == 0) return new List<Vector3>();
            cands.Sort((a, b) => b.span.CompareTo(a.span));
            int pool = Mathf.Min(cands.Count, 5);
            var pick = cands[UnityEngine.Random.Range(0, pool)];
            var best = pick.pts;
            if (UnityEngine.Random.value > 0.5f) best.Reverse();
            DebugLog($"Bradley long-road {PositionToGrid(best[0])} -> {PositionToGrid(best[best.Count - 1])} pts={best.Count} span={pick.span:F0}m pool={pool}/{cands.Count}");
            return best;
        }

        private List<Vector3> BuildRoadPatrolRoute(int maxPoints, float minSpacing = 14f)
        {
            var route = new List<Vector3>();
            if (_roadCache.Count == 0)
                BuildRoadCache();
            if (_roadCache.Count == 0)
                return route;

            // Prefer asphalt (width > 5), longest first — already sorted by cache
            var asphalt = _roadCache.Where(r => r.Width > 5f && r.Points.Count >= 12 && r.Length >= 150f).ToList();
            var candidates = asphalt.Count > 0
                ? asphalt
                : _roadCache.Where(r => r.Points.Count >= 10 && r.Length >= 120f).ToList();
            if (candidates.Count == 0)
                return route;

            // Pick among the better half so we don't always use the single longest road
            int pool = Mathf.Min(candidates.Count, Mathf.Max(3, candidates.Count / 2));
            int attempts = Mathf.Min(pool, 8);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var road = candidates[UnityEngine.Random.Range(0, pool)];
                var points = road.Points;
                if (points.Count < 8) continue;

                // Full road from one end — optional reverse of the whole road, never a mid-slice
                bool reverse = UnityEngine.Random.value > 0.5f;
                route.Clear();
                Vector3 last = Vector3.zero;
                bool hasLast = false;
                float spacing = Mathf.Clamp(minSpacing, 12f, 15f);

                int count = points.Count;
                for (int step = 0; step < count; step++)
                {
                    int idx = reverse ? (count - 1 - step) : step;
                    Vector3 p = points[idx];

                    if (hasLast)
                    {
                        float dx = p.x - last.x;
                        float dz = p.z - last.z;
                        if (dx * dx + dz * dz < spacing * spacing)
                            continue;

                        // Slope > 25° or large height jump between neighbors
                        if (!IsTraversableRoadSegment(last, p, RoadMaxSlopeDegrees, RoadMaxHeightJump))
                            continue;
                        if (!IsSegmentHeightClear(last, p))
                            continue;
                    }

                    // Cache already validated nodes — HeightMap snap only (no extra raycast)
                    p.y = GetTerrainGroundY(p);

                    route.Add(p);
                    last = p;
                    hasLast = true;

                    if (route.Count >= maxPoints)
                        break;
                }

                if (route.Count < 12)
                {
                    route.Clear();
                    continue;
                }

                float span = Vector3.Distance(
                    new Vector3(route[0].x, 0f, route[0].z),
                    new Vector3(route[route.Count - 1].x, 0f, route[route.Count - 1].z));
                if (span < 150f)
                {
                    route.Clear();
                    continue;
                }

                DebugLog($"BuildRoadPatrolRoute: FULL road {route.Count} pts width={road.Width:F1} " +
                         $"spacing~{spacing:F0}m {PositionToGrid(route[0])} -> {PositionToGrid(route[route.Count - 1])} " +
                         $"span={span:F0}m roadLen={road.Length:F0}m reverse={reverse}");
                return route;
            }
            return route;
        }

        /// <summary>
        /// Random point on a cached road that has a valid humanoid navmesh sample.
        /// Used for scientist spawn / patrol anchors.
        /// </summary>

        /// <summary>
        /// Builds ground-level route points for Bradley / Attack Heli.
        /// Prefer Launch Site, then monuments, then config waypoints, then random solid ground.
        /// </summary>
        
        // Navmesh / Path.Roads helpers (Bradley road cache; NPC spawns live in LiveStatsEventsNPC)




        private bool TryGetRoadPoints(object road, out Vector3[] points)
        {
            points = null;
            if (road == null) return false;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                // PathList.Path -> PathInterpolator / PathList data with .Points or .points
                object pathObj = null;
                var pathField = road.GetType().GetField("Path", flags) ?? road.GetType().GetField("path", flags);
                if (pathField != null) pathObj = pathField.GetValue(road);
                if (pathObj == null)
                {
                    var pathProp = road.GetType().GetProperty("Path") ?? road.GetType().GetProperty("path");
                    if (pathProp != null) pathObj = pathProp.GetValue(road, null);
                }
                if (pathObj == null) pathObj = road;

                // Direct points array
                var pointsField = pathObj.GetType().GetField("Points", flags) ?? pathObj.GetType().GetField("points", flags);
                if (pointsField != null)
                {
                    points = pointsField.GetValue(pathObj) as Vector3[];
                    if (points != null && points.Length > 0) return true;
                }
                var pointsProp = pathObj.GetType().GetProperty("Points") ?? pathObj.GetType().GetProperty("points");
                if (pointsProp != null)
                {
                    points = pointsProp.GetValue(pathObj, null) as Vector3[];
                    if (points != null && points.Length > 0) return true;
                }

                // PathInterpolator.GetPoints(List) or similar
                var getPoints = pathObj.GetType().GetMethod("GetPoints", flags);
                if (getPoints != null)
                {
                    var list = new List<Vector3>();
                    var pars = getPoints.GetParameters();
                    if (pars.Length == 1)
                    {
                        getPoints.Invoke(pathObj, new object[] { list });
                        if (list.Count > 0)
                        {
                            points = list.ToArray();
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

private List<Vector3> BuildGroundPatrolRoute(string preferMonumentFragment, int maxPoints, List<HeliWaypoint> customWaypoints)
        {
            var route = new List<Vector3>();

            // 1) Explicit config waypoints
            if (customWaypoints != null && customWaypoints.Count > 0)
            {
                foreach (var wp in customWaypoints)
                {
                    Vector3 p = new Vector3(wp.X, 0f, wp.Z);
                    p.y = TerrainMeta.HeightMap.GetHeight(p) + 2f;
                    route.Add(p);
                }
                return route;
            }

            // 2) Prefer a specific monument type first (e.g. "launch" for Bradley)
            try
            {
                if (TerrainMeta.Path != null && TerrainMeta.Path.Monuments != null)
                {
                    var monuments = TerrainMeta.Path.Monuments.Where(m => m != null).ToList();

                    if (!string.IsNullOrEmpty(preferMonumentFragment))
                    {
                        foreach (var mon in monuments)
                        {
                            string n = (mon.name ?? "").ToLowerInvariant();
                            if (n.Contains(preferMonumentFragment.ToLowerInvariant()))
                            {
                                Vector3 p = mon.transform.position + new Vector3(
                                    UnityEngine.Random.Range(-30f, 30f), 0f, UnityEngine.Random.Range(-30f, 30f));
                                p.y = TerrainMeta.HeightMap.GetHeight(p) + 2f;
                                if (NearPlayerCompound(p)) continue;
                                route.Add(p);
                                break;
                            }
                        }
                    }

                    // Other monuments as further waypoints
                    foreach (var mon in monuments.OrderBy(_ => UnityEngine.Random.value))
                    {
                        if (route.Count >= maxPoints) break;
                        string n = (mon.name ?? "").ToLowerInvariant();
                        if (n.Contains("harvestable") || n.Contains("tiny") || n.Contains("small"))
                            continue;
                        Vector3 p = mon.transform.position + new Vector3(
                            UnityEngine.Random.Range(-25f, 25f), 0f, UnityEngine.Random.Range(-25f, 25f));
                        float height = TerrainMeta.HeightMap.GetHeight(p);
                        float water = 0f;
                        try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                        if (height <= water + 2f) continue;
                        p.y = height + 2f;
                        if (NearPlayerCompound(p)) continue;
                        route.Add(p);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Ground route monument build failed: {ex.Message}");
            }

            // 3) Pad with random solid ground
            int guard = 0;
            while (route.Count < Mathf.Max(2, maxPoints) && guard++ < 40)
            {
                Vector3 candidate = GetRandomMapPosition(false);
                float height = TerrainMeta.HeightMap.GetHeight(candidate);
                float water = 0f;
                try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(candidate); } catch { }
                if (height > water + 2f && height > 5f)
                {
                    Vector3 pad = new Vector3(candidate.x, height + 2f, candidate.z);
                    if (!BradleyPointDry(pad) || NearPlayerCompound(pad)) continue;
                    route.Add(pad);
                }
            }

            return route;
        }

        private bool SpawnBradley()
        {
            int before = CountBradleys();
            DebugLog($"Bradley start - bradleys before: {before}");

            if (before > 0)
            {
                DebugLog("Bradley skipped - one already exists");
                BroadcastKey("SpawnBradleyExists");
                return false;
            }

            var bp = config.BradleyPatrol;
            // Full asphalt road, dense 12–15 m nodes (cap only for extreme highway length)
            int maxPts = Mathf.Max(48, bp?.MaxWaypoints ?? 64);

            Vector3 seedHere = Vector3.zero;
            if (_roadCache.Count == 0) BuildRoadCache();
            EnsureRoadGraph();
            if (_roadXings.Count > 0)
                seedHere = _roadXings[UnityEngine.Random.Range(0, _roadXings.Count)];
            var route = BuildBradleyIntersectionRoute(seedHere, maxPts);
            if (route.Count < 8)
                route = BuildLongAsphaltRoute(maxPts);
            if (route.Count < 8)
                route = BuildRoadPatrolRoute(maxPts, minSpacing: 18f);
            if (route.Count < 3 && bp?.Waypoints != null && bp.Waypoints.Count > 0)
                route = BuildGroundPatrolRoute("launch", maxPts, bp.Waypoints);
            if (route.Count < 3)
                route = BuildGroundPatrolRoute("launch", maxPts, null);

            if (route.Count == 0)
            {
                DebugLog("Bradley spawn aborted - no valid road/ground route");
                return false;
            }

            Vector3 pos = route[0];
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 1f;
            DebugLog($"Bradley spawn at {PositionToGrid(pos)} ({pos.x:F0}, {pos.y:F0}, {pos.z:F0}), route points: {route.Count}");

            BaseEntity entity = null;
            try
            {
                entity = GameManager.server.CreateEntity("assets/prefabs/npc/m2bradley/bradleyapc.prefab", pos);
            }
            catch (Exception ex)
            {
                DebugLog($"Bradley CreateEntity exception: {ex.Message}");
            }

            if (entity == null)
            {
                DebugLog("Bradley CreateEntity failed");
                return false;
            }

            entity.Spawn();
            InvalidateEntityCache("bradleyapc"); // also clears bradleyapc_live via fragment rules
            // Mark as ours so KillVanillaBradleys / OnEntitySpawned leave it alone
            try
            {
                ulong bid = entity.net?.ID.Value ?? 0;
                if (bid != 0) _pluginBradleyIds.Add(bid);
            }
            catch { }
            _suppressVanillaBradleyUntil = Time.realtimeSinceStartup + 15f;
            ApplyEntityNightLights(entity);
            timer.Once(2f, () => ApplyEntityNightLights(entity));

            // Install the full road path once — Bradley follows currentPath natively
            if (route.Count > 1 && (bp == null || bp.Enabled))
            {
                var bradley = entity as BradleyAPC;
                if (bradley != null)
                {
                    InstallBradleyFullPath(bradley, route, 0);
                    // Keepalive: reassert path if AI clears it while fighting
                    StartBradleyPatrolRoute(bradley, route, 1);
                }
                else
                    DebugLog("BradleyAPC cast failed - using vanilla AI only");
            }

            timer.Once(3f, () =>
            {
                InvalidateEntityCache("bradleyapc");
                int after = CountBradleys();
                DebugLog($"Bradley check - bradleys after: {after} (was {before})");
                DebugListMatching("bradleyapc");
            });
            return true;
        }

        private readonly Dictionary<ulong, Timer> _bradleyRouteTimers = new Dictionary<ulong, Timer>();

        /// <summary>
        /// Keepalive only — do NOT rebuild currentPath every tick (that causes
        /// ArgumentOutOfRangeException when AI advances currentPathIndex past a short path).
        /// Install once, clamp index, reinstall only if path was cleared.
        /// </summary>
        private void StartBradleyPatrolRoute(BradleyAPC bradley, List<Vector3> route, int startIndex)
        {
            if (bradley == null || route == null || route.Count == 0) return;

            ulong netId = 0;
            try { netId = bradley.net?.ID.Value ?? 0; } catch { }

            if (netId != 0 && _bradleyRouteTimers.TryGetValue(netId, out var old))
            {
                old?.Destroy();
                _bradleyRouteTimers.Remove(netId);
            }

            // Continuous path rewriting caused NullReferenceException spam in Bradley AI Update.
            // Install path once at spawn; this timer only enforces lifetime + rare path restore.
            float lifetimeMin = 25f;
            try
            {
                if (config?.BradleyPatrol != null)
                    lifetimeMin = Mathf.Clamp(config.BradleyPatrol.PatrolLifetimeMinutes, 12f, 40f);
            }
            catch { }
            float lifetimeSec = lifetimeMin * 60f;
            float startedAt = Time.realtimeSinceStartup;
            int reinstalls = 0;

            DebugLog($"Bradley lifetime armed for {lifetimeMin:F0}m (road hops like heavies)");

            int hopIndex = Mathf.Clamp(startIndex, 0, Math.Max(0, route.Count - 1));
            Timer routeTimer = null;
            routeTimer = timer.Every(4f, () =>
            {
                try
                {
                    if (bradley == null || bradley.IsDestroyed)
                    {
                        routeTimer?.Destroy();
                        if (netId != 0) _bradleyRouteTimers.Remove(netId);
                        return;
                    }

                    if (Time.realtimeSinceStartup - startedAt >= lifetimeSec)
                    {
                        DebugLog($"Bradley patrol time up ({lifetimeMin:F0}m) — removing");
                        try { bradley.Kill(); } catch { }
                        routeTimer?.Destroy();
                        if (netId != 0)
                        {
                            _bradleyRouteTimers.Remove(netId);
                            _pluginBradleyIds.Remove(netId);
                        }
                        return;
                    }

                    // Heavy-style hop: advance along the same road when close to the current node.
                    if (route.Count >= 2)
                    {
                        Vector3 pos = bradley.transform.position;
                        Vector3 dest = route[Mathf.Clamp(hopIndex, 0, route.Count - 1)];
                        float dx = pos.x - dest.x;
                        float dz = pos.z - dest.z;
                        if (dx * dx + dz * dz < 28f * 28f)
                        {
                            if (hopIndex < route.Count - 1)
                            {
                                string here = PositionToGrid(pos);
                                hopIndex++;
                                while (hopIndex < route.Count - 1
                                    && (!BradleyPointDry(route[hopIndex])
                                        || PositionToGrid(route[hopIndex]) == here))
                                    hopIndex++;
                                dest = route[hopIndex];
                                DebugLog($"Bradley road HOP {hopIndex}/{route.Count} {PositionToGrid(pos)} -> {PositionToGrid(dest)}");
                            }
                            else
                            {
                                var next = BuildBradleyIntersectionRoute(pos, 64);
                                if (next != null && next.Count >= 6)
                                {
                                    route = next;
                                    hopIndex = 0;
                                    DebugLog($"Bradley SWITCH {PositionToGrid(pos)} pts={route.Count}");
                                    InstallBradleyFullPath(bradley, route, 0);
                                }
                            }
                        }
                    }

                    // Only reinstall if currentPath was wiped (combat / stuck)
                    if (reinstalls < 4 && route.Count >= 2)
                    {
                        var flags = System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic;
                        var pathField = typeof(BradleyAPC).GetField("currentPath", flags);
                        var list = pathField?.GetValue(bradley) as List<Vector3>;
                        if (list == null || list.Count < 2)
                        {
                            InstallBradleyFullPath(bradley, route, 0);
                            reinstalls++;
                            DebugLog($"Bradley path restored (#{reinstalls})");
                        }
                        else
                        {
                            // Clamp index only — do not rewrite destinations every tick
                            var idxField = typeof(BradleyAPC).GetField("currentPathIndex", flags);
                            if (idxField != null && idxField.FieldType == typeof(int))
                            {
                                int idx = (int)idxField.GetValue(bradley);
                                if (idx < 0 || idx >= list.Count)
                                    idxField.SetValue(bradley, 0);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Bradley lifetime tick: {ex.Message}");
                }
            });

            if (netId != 0)
                _bradleyRouteTimers[netId] = routeTimer;
        }

        private static int FindNearestRouteIndex(List<Vector3> route, Vector3 pos)
        {
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < route.Count; i++)
            {
                float d = Vector3.Distance(
                    new Vector3(pos.x, 0f, pos.z),
                    new Vector3(route[i].x, 0f, route[i].z));
                if (d < best) { best = d; nearest = i; }
            }
            return nearest;
        }

        /// <summary>
        /// From nearIndex, walk forward (wrapping) until a node is slope/height-reachable from pos.
        /// Falls back to nearest+skip if none pass filters.
        /// </summary>

        /// <summary>
        /// Short-hop follow with direction: advance nearest±1 along pathDir.
        /// At path ends reverse direction instead of wrapping (avoids 600m hops to node 0).
        /// </summary>

        /// <summary>
        /// Install road path the way RoadBradley does:
        /// 1) RuntimePath + linked nodes + InstallPatrolPath when available
        /// 2) currentPath list + index + throttle + moveForceMax
        /// </summary>
        private void InstallBradleyFullPath(BradleyAPC bradley, List<Vector3> route, int startIndex)
        {
            if (bradley == null || bradley.IsDestroyed || route == null || route.Count == 0) return;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                // One-way only. Never wrap start..end back to index 0 (that is a loop).
                int start = Mathf.Clamp(startIndex, 0, route.Count - 1);
                var built = new List<Vector3>(route.Count - start);
                for (int i = start; i < route.Count; i++)
                {
                    Vector3 p = route[i];
                    if (!BradleyPointDry(p)) continue;
                    try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 1.0f; } catch { p.y = GetTerrainGroundY(p); }
                    built.Add(p);
                }
                if (built.Count < 2) return;

                // Prefer native InstallPatrolPath (linked AI path nodes)
                bool installedNative = false;
                try { installedNative = TryInstallBradleyPatrolPath(bradley, built); } catch { }

                var pathField = typeof(BradleyAPC).GetField("currentPath", flags);
                if (pathField != null)
                {
                    try { pathField.SetValue(bradley, new List<Vector3>(built)); }
                    catch
                    {
                        var list = pathField.GetValue(bradley) as List<Vector3>;
                        if (list != null)
                        {
                            list.Clear();
                            list.AddRange(built);
                        }
                    }
                }

                var idxField = typeof(BradleyAPC).GetField("currentPathIndex", flags);
                if (idxField != null && idxField.FieldType == typeof(int))
                    idxField.SetValue(bradley, 0);

                // Default off — linear roads; keepalive timer sets true only if endpoints < 40m
                var loopField = typeof(BradleyAPC).GetField("pathLooping", flags);
                if (loopField != null && loopField.FieldType == typeof(bool))
                    loopField.SetValue(bradley, false);

                foreach (var name in new[] { "throttle", "leftThrottle", "rightThrottle" })
                {
                    var f = typeof(BradleyAPC).GetField(name, flags);
                    if (f != null && f.FieldType == typeof(float))
                        f.SetValue(bradley, 1f);
                }

                var force = typeof(BradleyAPC).GetField("moveForceMax", flags);
                if (force != null && force.FieldType == typeof(float))
                    force.SetValue(bradley, 3500f);

                // finalDestination -> next node so AI has a concrete goal
                if (built.Count > 1)
                {
                    Vector3 next = built[Mathf.Min(1, built.Count - 1)];
                    foreach (var name in new[] { "finalDestination", "_finalDestination" })
                    {
                        var f = typeof(BradleyAPC).GetField(name, flags);
                        if (f != null && f.FieldType == typeof(Vector3))
                            f.SetValue(bradley, next);
                    }
                }

                DebugLog($"Bradley full path installed: {built.Count} road nodes (startIdx={start}" +
                         (installedNative ? ", InstallPatrolPath=OK" : "") + ")");
            }
            catch (Exception ex)
            {
                DebugLog($"InstallBradleyFullPath: {ex.Message}");
            }
        }

        /// <summary>
        /// Build RuntimePath / RuntimePathNode chain and call BradleyAPC.InstallPatrolPath.
        /// Returns false if types/method missing (older/newer assembly).
        /// </summary>
        private bool TryInstallBradleyPatrolPath(BradleyAPC bradley, List<Vector3> points)
        {
            if (bradley == null || points == null || points.Count < 2) return false;
            try
            {
                var pathType = typeof(BradleyAPC).Assembly.GetType("RuntimePath")
                               ?? typeof(BaseEntity).Assembly.GetType("RuntimePath");
                var nodeType = typeof(BradleyAPC).Assembly.GetType("RuntimePathNode")
                               ?? typeof(BaseEntity).Assembly.GetType("RuntimePathNode");
                var interestType = typeof(BradleyAPC).Assembly.GetType("RuntimeInterestNode")
                                   ?? typeof(BaseEntity).Assembly.GetType("RuntimeInterestNode");
                if (pathType == null || nodeType == null) return false;

                object runtimePath = Activator.CreateInstance(pathType);
                var nodesArr = Array.CreateInstance(nodeType, points.Count);

                object prev = null;
                var addLink = nodeType.GetMethod("AddLink");
                for (int i = 0; i < points.Count; i++)
                {
                    object node = Activator.CreateInstance(nodeType, points[i]);
                    if (prev != null && addLink != null)
                    {
                        addLink.Invoke(node, new[] { prev });
                        addLink.Invoke(prev, new[] { node });
                    }
                    nodesArr.SetValue(node, i);
                    prev = node;
                }

                var nodesProp = pathType.GetProperty("Nodes") ?? pathType.GetField("Nodes") as object;
                if (nodesProp is System.Reflection.PropertyInfo pi)
                    pi.SetValue(runtimePath, nodesArr, null);
                else if (nodesProp is System.Reflection.FieldInfo fi)
                    fi.SetValue(runtimePath, nodesArr);

                if (interestType != null)
                {
                    var addInterest = pathType.GetMethod("AddInterestNode");
                    if (addInterest != null)
                    {
                        object i0 = Activator.CreateInstance(interestType, points[0]);
                        object i1 = Activator.CreateInstance(interestType, points[points.Count - 1]);
                        addInterest.Invoke(runtimePath, new[] { i0 });
                        addInterest.Invoke(runtimePath, new[] { i1 });
                    }
                }

                var install = typeof(BradleyAPC).GetMethod("InstallPatrolPath",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (install == null) return false;
                install.Invoke(bradley, new[] { runtimePath });
                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"TryInstallBradleyPatrolPath: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Bradley only drives currentPath — SetDestination alone often does nothing.
        /// Build a short lerp path, reset index, force throttle.
        /// </summary>

        private bool SpawnAttackHeli()
        {
            int before = CountAttackHelis();
            int max = config.VehicleLimits?.MaxAttackHelis ?? 1;
            if (config.AttackHeliSpawn != null && config.AttackHeliSpawn.AllowMultiple)
                max = Mathf.Max(max, 3); // AllowMultiple raises the cap
            DebugLog($"AttackHeli start - before: {before} (max {max})");
            if (before >= max)
            {
                DebugLog("AttackHeli skipped - limit reached");
                BroadcastKey("SpawnAttackHeliExists");
                return false;
            }

            var asp = config.AttackHeliSpawn;
            int maxPts = asp?.MaxSpawnPoints ?? 6;
            var route = BuildGroundPatrolRoute(null, maxPts, asp?.Waypoints);

            if (route.Count == 0)
            {
                DebugLog("AttackHeli aborted - no valid ground");
                return false;
            }

            // Pick a random route point as the drop location
            Vector3 pos = route[UnityEngine.Random.Range(0, route.Count)];
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 5f;
            DebugLog($"AttackHeli spawning at {PositionToGrid(pos)} ({pos.x:F0}, {pos.y:F0}, {pos.z:F0})");

            bool ok = TrySpawnPrefab("assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab", pos);
            if (!ok)
                ok = TrySpawnPrefab("assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab", pos);

            if (ok)
                BroadcastKey("SpawnAttackHeliNear", PositionToGrid(pos));

            timer.Once(3f, () =>
            {
                InvalidateEntityCache("attackhelicopter");
                int after = CountAttackHelis();
                DebugLog($"AttackHeli check - after: {after} (was {before})");
                DebugListMatching("attackhelicopter.entity");
            });
            return true;
        }
        private void SpawnHackableCrate()
        {
            Vector3 pos = GetRandomMapPosition(false);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos) + 1f;
            DebugLog($"HackableCrate at ({pos.x:F0}, {pos.y:F0}, {pos.z:F0})");

            // Suppress OnEntitySpawned announce — we broadcast ourselves below
            _suppressHackableAnnounceUntil = Time.realtimeSinceStartup + 8f;

            string[] paths = {
                "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab",
                "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate_oilrig.prefab"
            };
            bool ok = false;
            foreach (var path in paths)
            {
                if (TrySpawnPrefab(path, pos)) { ok = true; break; }
            }
            if (!ok)
            {
                TryConsoleSpawn("codelockedhackablecrate");
                TryConsoleSpawn("hackablecrate");
            }

            // Protect from Vehicles idle despawn for the event window
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    var hc = ent as HackableLockedCrate;
                    if (hc == null) continue;
                    Vector3 d = hc.transform.position - pos;
                    if (d.sqrMagnitude > 25f) continue; // ~5m
                    RegisterEventProtectedCrate(hc, 60f);
                    break;
                }
            }
            catch { }

            string grid = PositionToGrid(pos);
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    var hc = ent as HackableLockedCrate;
                    if (hc == null) continue;
                    Vector3 d = hc.transform.position - pos;
                    if (d.sqrMagnitude > 25f) continue;
                    grid = PositionToGrid(hc.transform.position);
                    break;
                }
            }
            catch { }
            _lastHackableGrid = grid;
            Puts($"[Events] HackableCrate event (ground, not Chinook) at {grid}");
            // Chat grid comes from GetSpawnMessage(SpawnHackable) — do not use SpawnHackableNear (Chinook).
        }
        // Server-owned roadblock props (barricades) for RoadAmbush — cleaned after lifetime

        // Wipe-day snapshot (EventTick / CanSpawn BaseRaid gate)
        private class LiveStatsSnapshot
        {
            public int wipeDay = 0;
        }

        private int GetWipeDayFromData()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _cachedWipeDayAt < WipeDayCacheSeconds)
                return _cachedWipeDay;
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<LiveStatsSnapshot>("live_stats");
                _cachedWipeDay = data?.wipeDay ?? 0;
            }
            catch
            {
                _cachedWipeDay = 0;
            }
            _cachedWipeDayAt = now;
            return _cachedWipeDay;
        }

        /// <summary>Companion plugins may Call this instead of reading live_stats.json themselves.</summary>
        public object API_GetWipeDay() => GetWipeDayFromData();

        /// <summary>
        /// Live entity netIds from the existing index (no world scan).
        /// Keys: cargoship, chinook, heli, bradley, hackable, supplyplane,
        /// attackheli, tugboat, submarine, minicopter, scrapheli.
        /// Values: list of netId strings.
        /// </summary>
        public object API_GetLiveIndex()
        {
            var d = new Dictionary<string, List<string>>(12);
            AddIndex("cargoship", _idxCargo, d);
            AddIndex("chinook", _idxChinook, d);
            AddIndex("heli", _idxPatrolHeli, d);
            AddIndex("bradley", _idxBradley, d);
            AddIndex("hackable", _idxCrate, d);
            AddIndex("supplyplane", _idxCargoPlane, d);
            AddIndex("attackheli", _idxAttackHeli, d);
            AddIndex("tugboat", _idxTug, d);
            AddIndex("submarine", _idxSub, d);
            AddIndex("minicopter", _idxMini, d);
            AddIndex("scrapheli", _idxScrap, d);
            return d;
        }

        private static void AddIndex(string key, HashSet<ulong> set, Dictionary<string, List<string>> d)
        {
            var list = new List<string>(set != null ? set.Count : 0);
            if (set != null)
            {
                foreach (var id in set)
                    list.Add(id.ToString());
            }
            d[key] = list;
        }


        private bool IsF15PrefabName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("f15", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TryRelocatePendingF15(BaseEntity jet)
        {
            if (jet == null || jet.IsDestroyed) return;
            if (!_pendingF15Relocate) return;
            try
            {
                jet.transform.SetPositionAndRotation(_pendingF15Pos, _pendingF15Rot);
                jet.TransformChanged();
                jet.SendNetworkUpdateImmediate();
            }
            catch { }
            _pendingF15Relocate = false;
            Vector3 a = jet.transform.position;
            Puts($"[Events] F-15E relocated to {PositionToGrid(a)} ({a.x:F1}, {a.y:F1}, {a.z:F1})");
        }



        /// <summary>Player-flyable CH47s only (ch47.entity). Scientist crate Chinooks are excluded.</summary>
        private int CountPlayerChinooks()
        {
            int n = 0;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null || be.IsDestroyed) continue;
                    string prefab = (be.PrefabName ?? be.ShortPrefabName ?? "").ToLowerInvariant();
                    if (prefab.IndexOf("ch47", StringComparison.Ordinal) < 0) continue;
                    if (prefab.IndexOf("scientist", StringComparison.Ordinal) >= 0) continue;
                    if (prefab.IndexOf("gib", StringComparison.Ordinal) >= 0) continue;
                    n++;
                }
            }
            catch { }
            return n;
        }

        private bool TryFindAirfield(out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons == null) return false;
                for (int i = 0; i < mons.Count; i++)
                {
                    var mon = mons[i];
                    if (mon == null) continue;
                    string n = (mon.name ?? "").ToLowerInvariant();
                    if (n.IndexOf("airfield", StringComparison.Ordinal) < 0 &&
                        n.IndexOf("airport", StringComparison.Ordinal) < 0)
                        continue;
                    pos = mon.transform.position;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool IsVehicleOccupied(BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            try
            {
                var vehicle = ent as BaseVehicle;
                if (vehicle != null && vehicle.mountPoints != null)
                {
                    foreach (var mount in vehicle.mountPoints)
                    {
                        if (mount != null && mount.mountable != null && mount.mountable.GetMounted() != null)
                            return true;
                    }
                }
            }
            catch { }
            try
            {
                if (ent.children != null)
                {
                    foreach (var child in ent.children)
                    {
                        var seat = child as BaseMountable;
                        if (seat != null && seat.GetMounted() != null)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void FillChinookFuel(BaseEntity ent, int amount)
        {
            if (ent == null || amount <= 0) return;
            try
            {
                if (ent.children == null) return;
                foreach (var child in ent.children)
                {
                    var storage = child as StorageContainer;
                    if (storage == null || storage.inventory == null) continue;
                    string sn = (storage.ShortPrefabName ?? "").ToLowerInvariant();
                    if (sn.IndexOf("fuel", StringComparison.Ordinal) < 0) continue;
                    var fuel = ItemManager.CreateByName("lowgradefuel", amount);
                    if (fuel != null)
                        fuel.MoveToContainer(storage.inventory);
                    return;
                }
            }
            catch { }
        }

        private bool SpawnAirfieldChinook()
        {
            var cfg = config?.AirfieldChinook ?? new AirfieldChinookConfig();
            if (!cfg.Enabled)
            {
                Puts("[Events] AirfieldChinook disabled in config");
                return false;
            }
            if (!TryFindAirfield(out Vector3 fieldPos))
            {
                Puts("[Events] AirfieldChinook aborted — no airfield monument on this map");
                return false;
            }

            Vector3 spawn = fieldPos + Vector3.up * 9f;
            try
            {
                float gy = TerrainMeta.HeightMap.GetHeight(fieldPos);
                spawn = new Vector3(fieldPos.x, gy + 8.5f, fieldPos.z);
            }
            catch { }

            const string prefab = "assets/prefabs/npc/ch47/ch47.entity.prefab";
            BaseEntity heli = null;
            try
            {
                heli = GameManager.server.CreateEntity(prefab, spawn, Quaternion.identity, true);
            }
            catch (Exception ex)
            {
                Puts($"[Events] AirfieldChinook CreateEntity: {ex.Message}");
                return false;
            }
            if (heli == null)
            {
                Puts("[Events] AirfieldChinook CreateEntity returned null (ch47.entity)");
                return false;
            }
            try { heli.Spawn(); }
            catch (Exception ex)
            {
                Puts($"[Events] AirfieldChinook Spawn: {ex.Message}");
                try { heli.Kill(); } catch { }
                return false;
            }

            ulong hid = 0;
            try { hid = heli.net?.ID.Value ?? 0; } catch { }
            _airfieldChinookId = hid;
            FillChinookFuel(heli, Mathf.Max(50, cfg.FuelAmount));

            float lifeMin = Mathf.Max(15f, cfg.LifetimeMinMinutes);
            float lifeMax = Mathf.Max(lifeMin + 1f, cfg.LifetimeMaxMinutes);
            float life = UnityEngine.Random.Range(lifeMin, lifeMax);
            _airfieldChinookUntil = Time.realtimeSinceStartup + life * 60f;

            try
            {
                if (LiveStatsEventsVehicles != null && hid != 0)
                    LiveStatsEventsVehicles.Call("API_ProtectEventVehicle", hid, life);
            }
            catch { }

            int guards = Mathf.Clamp(cfg.GuardCount, 0, 8);
            if (guards > 0 && LiveStatsEventsNPC != null)
            {
                try { LiveStatsEventsNPC.Call("API_SpawnGuardsAt", fieldPos, guards); }
                catch (Exception ex) { DebugLog($"AirfieldChinook guards: {ex.Message}"); }
            }

            string grid = PositionToGrid(fieldPos);
            Puts($"[Events] AirfieldChinook spawned at {grid} life={life:F0}m guards={guards} net={hid}");

            Vector3 fieldCapture = fieldPos;
            timer.Once(life * 60f, () =>
            {
                try
                {
                    BaseEntity live = null;
                    if (_airfieldChinookId != 0)
                    {
                        try { live = BaseNetworkable.serverEntities.Find(new NetworkableId(_airfieldChinookId)) as BaseEntity; }
                        catch { }
                    }
                    if (live != null && !live.IsDestroyed)
                    {
                        Vector3 p = live.transform.position;
                        float dx = p.x - fieldCapture.x;
                        float dz = p.z - fieldCapture.z;
                        bool stillAtField = (dx * dx + dz * dz) <= 80f * 80f;
                        if (!stillAtField)
                            Puts($"[Events] AirfieldChinook window ended — aircraft left the airfield ({PositionToGrid(p)}), leaving it");
                        else if (IsVehicleOccupied(live))
                            Puts("[Events] AirfieldChinook window ended — still at airfield but occupied, leaving it");
                        else
                        {
                            Puts("[Events] AirfieldChinook window ended — still at airfield and unclaimed, despawning");
                            try { live.Kill(); } catch { }
                        }
                    }
                    _airfieldChinookId = 0;
                }
                catch { }
            });
            return true;
        }

        private void DoF15Flyby(Vector3? position = null)
        {
            // F-15E Strike Eagle — CreateEntity at altitude with non-zero facing.
            // Console "spawn f15e x y z" ignores coords on current builds and drops at (0,0,0),
            // which also triggers "Look rotation viewing vector is zero".
            BroadcastKey("F15Incoming");
            BroadcastKey("F15HeadsDown");

            Vector3 pos;
            Vector3 toward;
            if (position.HasValue)
            {
                pos = position.Value;
                if (pos.y < 80f) pos.y = 180f;
                toward = Vector3.zero - pos;
            }
            else
            {
                // Start on the playable rim and fly across map center (visible over land)
                float half = 1500f;
                try
                {
                    if (TerrainMeta.Size.x > 100f)
                        half = TerrainMeta.Size.x * 0.42f;
                    else if (World.Size > 100f)
                        half = World.Size * 0.42f;
                }
                catch { }
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                pos = new Vector3(Mathf.Cos(ang) * half, UnityEngine.Random.Range(160f, 200f), Mathf.Sin(ang) * half);
                toward = Vector3.zero - pos; // through center to the opposite shore
            }
            toward.y = 0f;
            if (toward.sqrMagnitude < 1f)
                toward = Vector3.forward;
            toward.Normalize();
            Quaternion rot = Quaternion.LookRotation(toward, Vector3.up);

            // This Facepunch build has no loadable f15e prefab path. CreateEntity() only
            // spams "Prefab not found". Use shortname spawn (always origin) then relocate.
            try
            {
                _pendingF15Pos = pos;
                _pendingF15Rot = rot;
                _pendingF15Relocate = true;
                _pendingF15Until = Time.realtimeSinceStartup + 3f;
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "spawn f15e");
                Puts("[Events] F-15E spawn issued (shortname) — relocate when entity appears");
            }
            catch (Exception ex)
            {
                _pendingF15Relocate = false;
                Puts($"[Events] F-15E spawn FAILED: {ex.Message}");
            }
        }

        /// <summary>
        /// Console: livestats.spawnf15e [x] [y] [z]
        /// If no coordinates are given it uses the normal random flyby.
        /// </summary>
        [ConsoleCommand("livestats.spawnf15e")]
        private void ConSpawnF15E(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player() != null && !HasAdmin(arg.Player()))
            {
                arg.ReplyWith(Msg("PermissionDenied"));
                return;
            }

            if (arg.Args != null && arg.Args.Length >= 3 &&
                float.TryParse(arg.Args[0].ToString(), out float x) &&
                float.TryParse(arg.Args[1].ToString(), out float y) &&
                float.TryParse(arg.Args[2].ToString(), out float z))
            {
                DoF15Flyby(new Vector3(x, y, z));
                arg.ReplyWith(Msg("F15SpawnedCoords", null, x, y, z));
            }
            else
            {
                DoF15Flyby();
                arg.ReplyWith(Msg("F15DefaultPath"));
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Converts a world position to the standard Rust map grid (e.g. "G12", "AA4").
        /// Identical to LiveStats.PositionToGrid — MapHelper + Deep Sea bounds + same fallback.
        /// </summary>
        private void NotifyEventFeed(string eventType, string phase, string grid = null, int etaMinutes = 0)
        {
            string t = (eventType ?? "").ToLowerInvariant();
            bool inbound = string.Equals(phase, "inbound", StringComparison.OrdinalIgnoreCase);
            bool npc =
                t.IndexOf("heavy", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("ambush", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("tunnel", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("subway", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("mine", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("peace", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("raid", StringComparison.Ordinal) >= 0 ||
                t.IndexOf("rail", StringComparison.Ordinal) >= 0;
            // Build-up / cargo / heli have no square yet — do not reuse the last NPC grid.
            if (inbound || !npc)
            {
                if (string.IsNullOrEmpty(grid)) grid = "";
            }
            else if (string.IsNullOrEmpty(grid))
            {
                try
                {
                    if (LiveStatsEventsNPC != null)
                    {
                        var g = LiveStatsEventsNPC.Call("API_GetLastSpawnGrid");
                        if (g != null) grid = g.ToString();
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(grid)) grid = _lastModuleSpawnGrid;
            }
            try { Interface.CallHook("OnLiveStatsEventFeed", eventType, phase, grid ?? "", etaMinutes); }
            catch { }
        }

        private string PositionToGrid(Vector3 pos)
        {
            if (pos == Vector3.zero) return "???";

            float worldSize = ConVar.Server.worldsize;
            float halfSize = worldSize / 2f;

            // Outside the playable map
            if (Mathf.Abs(pos.x) > halfSize || Mathf.Abs(pos.z) > halfSize)
                return "Deep Sea";

            // Facepunch official helper (same as LiveStats / RaidableBases / in-game map)
            try
            {
                string grid = MapHelper.PositionToString(pos);
                if (!string.IsNullOrEmpty(grid) && grid != "Unknown")
                    return grid;
            }
            catch
            {
                // Fallback if MapHelper is unavailable
            }

            // Manual fallback (matches LiveStats / MapHelper logic)
            const float cellSize = 146.3f;
            int gridX = Mathf.FloorToInt((pos.x + halfSize) / cellSize);
            int gridZ = Mathf.FloorToInt((halfSize - pos.z) / cellSize);

            string letter;
            if (gridX < 26)
                letter = ((char)('A' + gridX)).ToString();
            else
            {
                int first = (gridX / 26) - 1;
                int second = gridX % 26;
                letter = ((char)('A' + first)).ToString() + ((char)('A' + second)).ToString();
            }

            return letter + gridZ;
        }

        private Vector3 GetRandomMapPosition(bool highAir)
        {
            float mapSize = TerrainMeta.Size.x * 0.4f;
            Vector3 pos = Vector3.zero;
            for (int i = 0; i < 20; i++)
            {
                pos = new Vector3(
                    UnityEngine.Random.Range(-mapSize, mapSize),
                    0f,
                    UnityEngine.Random.Range(-mapSize, mapSize));

                float height = TerrainMeta.HeightMap.GetHeight(pos);
                if (height > 0f && height < 100f) // avoid deep water / extreme mountains
                {
                    pos.y = highAir ? 120f : height + 2f;
                    if (!highAir && (NearPlayerCompound(pos) || !BradleyPointDry(pos)))
                        continue;
                    return pos;
                }
            }
            pos.y = highAir ? 150f : 5f;
            return pos;
        }
        /// <summary>
        /// Returns a multiplier based on current weather conditions.
        /// Tries to read from LiveStatsWorld / Climate if available.
        /// </summary>
        private void EnsureWeatherCache()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _weatherCacheTime < WeatherCacheSeconds)
                return;

            _weatherCacheTime = now;
            _weatherRain = 0f;
            _weatherWind = 0f;
            _weatherFog = 0f;
            _weatherThunder = 0f;
            _weatherProfile = "clear";

            try
            {
                if (LiveStatsWorld != null)
                {
                    var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, object>>("world_stats");
                    if (data != null)
                    {
                        if (data.ContainsKey("rainIntensity")) _weatherRain = Convert.ToSingle(data["rainIntensity"]);
                        if (data.ContainsKey("windIntensity")) _weatherWind = Convert.ToSingle(data["windIntensity"]);
                        if (data.ContainsKey("fogIntensity")) _weatherFog = Convert.ToSingle(data["fogIntensity"]);
                        if (data.ContainsKey("thunderIntensity")) _weatherThunder = Convert.ToSingle(data["thunderIntensity"]);
                        if (data.ContainsKey("weather"))
                            _weatherProfile = data["weather"]?.ToString()?.ToLowerInvariant() ?? "clear";
                    }
                }

                var climate = SingletonComponent<Climate>.Instance;
                if (climate != null)
                {
                    if (_weatherRain < 0.01f) _weatherRain = climate.Overrides.Rain;
                    if (_weatherWind < 0.01f) _weatherWind = climate.Overrides.Wind;
                    if (_weatherFog < 0.01f) _weatherFog = climate.Overrides.Fog;
                }
            }
            catch { /* keep last/neutral values */ }
        }

        /// <summary>Uses the cached weather snapshot from EnsureWeatherCache() — no disk I/O.</summary>
        private float GetWeatherModifierCached(string eventKey)
        {
            float rain = _weatherRain, wind = _weatherWind, fog = _weatherFog, thunder = _weatherThunder;
            string profile = _weatherProfile ?? "clear";

            bool isStormy = thunder > 0.35f || profile.IndexOf("storm", StringComparison.Ordinal) >= 0 || (rain > 0.55f && wind > 0.4f);
            bool isRainy  = rain > 0.25f || profile.IndexOf("rain", StringComparison.Ordinal) >= 0;
            bool isFoggy  = fog > 0.4f || profile.IndexOf("fog", StringComparison.Ordinal) >= 0;
            bool isClear  = rain < 0.08f && fog < 0.15f && thunder < 0.1f &&
                            (profile.IndexOf("clear", StringComparison.Ordinal) >= 0 || profile.IndexOf("partly", StringComparison.Ordinal) >= 0);

            // Combat events love bad weather
            if (eventKey == "bradley" || eventKey == "tank")
            {
                if (isStormy) return 2.2f;
                if (isRainy)  return 1.5f;
                if (isFoggy)  return 1.3f;
                if (isClear)  return 0.7f;
                return 1.0f;
            }

            if (eventKey == "patrolheli" || eventKey == "heli" || eventKey == "patrolhelicopter")
            {
                if (isStormy) return 1.9f;
                if (isRainy)  return 1.4f;
                if (isFoggy)  return 1.25f;
                if (isClear)  return 0.85f;
                return 1.0f;
            }

            if (eventKey == "chinook" || eventKey == "ch47")
            {
                if (isStormy) return 1.6f;
                if (isRainy)  return 1.25f;
                if (isFoggy)  return 1.15f;
                return 1.0f;
            }

            // Visibility-dependent events prefer clear weather
            if (eventKey == "supplydrop" || eventKey == "airdrop")
            {
                if (isClear)  return 1.45f;
                if (isStormy) return 0.55f;
                if (isFoggy)  return 0.65f;
                if (isRainy)  return 0.80f;
                return 1.0f;
            }

            if (eventKey == "cargoship" || eventKey == "cargo")
            {
                if (isClear)  return 1.30f;
                if (isStormy) return 0.60f;
                if (isFoggy)  return 0.70f;
                if (isRainy)  return 0.85f;
                return 1.0f;
            }

            if (eventKey == "attackheli" || eventKey == "attackhelicopter")
            {
                if (isStormy) return 1.8f;
                if (isRainy)  return 1.3f;
                if (isClear)  return 0.8f;
                return 1.0f;
            }

            if (eventKey == "heavyscientists" || eventKey == "heavies")
            {
                if (isStormy) return 1.7f;
                if (isFoggy)  return 1.4f;
                if (isClear)  return 0.75f;
                return 1.0f;
            }

            if (eventKey == "hotairballoon" || eventKey == "balloon")
            {
                if (isClear)  return 1.6f;
                if (isStormy || isRainy) return 0.3f;
                if (isFoggy)  return 0.5f;
                return 1.0f;
            }

            if (eventKey == "tugboat" || eventKey == "submarine" || eventKey == "sub")
            {
                if (isClear)  return 1.25f;
                if (isStormy) return 0.7f;
                return 1.0f;
            }

            if (eventKey == "hackablecrate" || eventKey == "lockedcrate")
            {
                if (isFoggy || isStormy) return 1.2f;
                return 1.0f;
            }

            if (eventKey == "roadambush" || eventKey == "ambush")
            {
                if (isStormy || isFoggy) return 1.6f;
                if (isClear) return 0.8f;
                return 1.0f;
            }

            if (eventKey == "tunnelsquad" || eventKey == "tunnel")
            {
                if (isStormy) return 1.5f;
                if (isFoggy) return 1.3f;
                return 1.0f;
            }

            if (eventKey == "peacekeeperpatrol" || eventKey == "peacekeepers")
            {
                if (isClear) return 1.4f;
                if (isStormy) return 0.5f;
                return 1.0f;
            }

            return 1.0f; // neutral for anything else (including F-15)
        }

        private float GetCurrentHour()
        {
            try
            {
                if (TOD_Sky.Instance != null && TOD_Sky.Instance.Cycle != null)
                    return TOD_Sky.Instance.Cycle.Hour;
            }
            catch { }
            return (float)DateTime.Now.TimeOfDay.TotalHours;
        }

        private float GetLocalHour()
        {
            // Prefer LiveStatsWorld real local time if available
            if (LiveStatsWorld != null)
            {
                // Fallback to server local time
            }
            return (float)DateTime.Now.TimeOfDay.TotalHours;
        }

        private bool IsInWindow(float hour, float start, float end)
        {
            if (start <= end)
                return hour >= start && hour <= end;
            // overnight window
            return hour >= start || hour <= end;
        }

        private void ScheduleAnnouncement(string eventType, string message, float executeAt)
        {
            _pendingAnnouncements.Add(new PendingAnnouncement
            {
                EventType = eventType,
                Message = message,
                ExecuteAt = executeAt
            });
        }

        private void ScheduleBuildUp(string eventType, int minutes, float executeAt)
        {
            _pendingAnnouncements.Add(new PendingAnnouncement
            {
                EventType = eventType,
                IsBuildUp = true,
                BuildUpMinutes = minutes,
                ExecuteAt = executeAt
            });
        }

        private void ProcessAnnouncements()
        {
            if (_pendingAnnouncements.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            for (int i = _pendingAnnouncements.Count - 1; i >= 0; i--)
            {
                if (now >= _pendingAnnouncements[i].ExecuteAt)
                {
                    var pending = _pendingAnnouncements[i];
                    if (pending.IsBuildUp)
                    {
                        string type = pending.EventType;
                        int mins = pending.BuildUpMinutes;
                        BroadcastLocalized(uid => GetBuildUpMessage(type, mins, uid));
                        NotifyEventFeed(type, "inbound", null, mins);
                    }
                    else
                    {
                        BroadcastRaw(pending.Message);
                    }
                    _pendingAnnouncements.RemoveAt(i);
                }
            }
        }

        private void BroadcastRaw(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                player.ChatMessage(message);
            }
            Puts($"[Events] {message}");
        }

        private void Broadcast(string message) => BroadcastRaw(message);

        private void BroadcastLocalized(System.Func<string, string> factory)
        {
            if (factory == null) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                string msg = factory(player.UserIDString);
                if (!string.IsNullOrEmpty(msg))
                    player.ChatMessage(msg);
            }
            string log = factory(null);
            if (!string.IsNullOrEmpty(log))
                Puts($"[Events] {log}");
        }

        private void BroadcastKey(string key, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return;
            BroadcastLocalized(uid => Msg(key, uid, args));
        }

        public void API_BroadcastKey(string key, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (arg0 == null && arg1 == null && arg2 == null)
                BroadcastKey(key);
            else if (arg1 == null && arg2 == null)
                BroadcastKey(key, arg0);
            else if (arg2 == null)
                BroadcastKey(key, arg0, arg1);
            else
                BroadcastKey(key, arg0, arg1, arg2);
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, userId);
            if (args != null && args.Length > 0)
            {
                try { return string.Format(message, args); }
                catch { return message; }
            }
            return message;
        }

        private string GetEventDisplayName(string type, string userId = null)
        {
            type = type.ToLower();
            if (type == "supplydrop" || type == "airdrop") return Msg("NameSupplyDrop", userId);
            if (type == "cargoship" || type == "cargo") return Msg("NameCargo", userId);
            if (type == "patrolheli" || type == "heli" || type == "patrolhelicopter") return Msg("NamePatrolHeli", userId);
            if (type == "chinook" || type == "ch47") return Msg("NameChinook", userId);
            if (type == "bradley" || type == "tank") return Msg("NameBradley", userId);
            if (type == "attackheli" || type == "attackhelicopter") return Msg("NameAttackHeli", userId);
            if (type == "tugboat") return Msg("NameTugboat", userId);
            if (type == "hotairballoon" || type == "balloon") return Msg("NameBalloon", userId);
            if (type == "heavyscientists" || type == "heavies") return Msg("NameHeavies", userId);
            if (type == "hackablecrate" || type == "lockedcrate") return Msg("NameHackable", userId);
            if (type == "submarine" || type == "sub") return Msg("NameSub", userId);
            if (type == "scrapheli" || type == "scraptransport") return Msg("NameScrapHeli", userId);
            if (type == "minicopter" || type == "mini") return Msg("NameMini", userId);
            if (type == "f15" || type == "flyby") return Msg("NameF15", userId);
            if (type == "airfieldchinook" || type == "airfieldch47" || type == "stealchinook" || type == "stealinook")
                return Msg("NameAirfieldChinook", userId);
            if (type == "roadambush" || type == "ambush") return Msg("NameAmbush", userId);
            if (type == "tunnelsquad" || type == "tunnel") return Msg("NameTunnel", userId);
            if (type == "subwaypatrol" || type == "subway" || type == "metro") return Msg("NameSubway", userId);
            if (type == "mineguard" || type == "mine" || type == "mines" || type == "caveguard") return Msg("NameMineGuard", userId);
            if (type == "railpatrol" || type == "rail" || type == "trainpatrol" || type == "surfacerail") return Msg("NameRailPatrol", userId);
            if (type == "peacekeeperpatrol" || type == "peacekeepers" || type == "peacekeeper") return Msg("NamePeacekeepers", userId);
            if (type == "baseraid" || type == "npcraid" || type == "raid") return Msg("NameBaseRaid", userId);
            return type;
        }

        private string GetBuildUpMessage(string type, int minutes, string userId = null)
        {
            string name = GetEventDisplayName(type, userId);
            if (minutes >= 10)
                return Msg("BuildUpIntel", userId, name, minutes);
            if (minutes >= 3)
                return Msg("BuildUpWarning", userId, name, minutes);
            return Msg("BuildUpImminent", userId, name.ToUpper());
        }

        private string TryGetNpcModuleGrid()
        {
            if (!string.IsNullOrEmpty(_lastModuleSpawnGrid))
                return _lastModuleSpawnGrid;
            try
            {
                if (LiveStatsEventsNPC == null || !LiveStatsEventsNPC.IsLoaded)
                    LiveStatsEventsNPC = plugins.Find("LiveStatsEventsNPC");
                object r = LiveStatsEventsNPC?.Call("API_GetLastSpawnGrid");
                if (r is string s && !string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return null;
        }

        private string GetSpawnMessage(string type, string userId = null)
        {
            type = type.ToLower();
            if (type == "supplydrop" || type == "airdrop") return Msg("SpawnSupplyDrop", userId);
            if (type == "cargoship" || type == "cargo") return Msg("SpawnCargo", userId);
            if (type == "patrolheli" || type == "heli" || type == "patrolhelicopter") return Msg("SpawnPatrolHeli", userId);
            if (type == "chinook" || type == "ch47")
            {
                // Never mention crates at spawn — vanilla CH47 often tours with no drop.
                // Dirty oxide/lang files used to say "Locked crates incoming!" here.
                string tour = Msg("SpawnChinookTour", userId);
                if (string.IsNullOrEmpty(tour) || tour == "SpawnChinookTour")
                    tour = Msg("SpawnChinook", userId);
                if (!string.IsNullOrEmpty(tour) &&
                    tour.IndexOf("crate", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "<color=#c0c0c0>Chinook inbound!</color>";
                return tour;
            }
            if (type == "bradley" || type == "tank") return Msg("SpawnBradley", userId);
            if (type == "attackheli" || type == "attackhelicopter") return Msg("SpawnAttackHeli", userId);
            if (type == "tugboat") return Msg("SpawnTugboat", userId);
            if (type == "hotairballoon" || type == "balloon") return Msg("SpawnBalloon", userId);
            if (type == "heavyscientists" || type == "heavies") return Msg("SpawnHeavies", userId);
            if (type == "hackablecrate" || type == "lockedcrate")
                return string.IsNullOrEmpty(_lastHackableGrid)
                    ? Msg("SpawnHackable", userId)
                    : Msg("SpawnHackable", userId, _lastHackableGrid);
            if (type == "submarine" || type == "sub") return Msg("SpawnSub", userId);
            if (type == "scrapheli" || type == "scraptransport") return Msg("SpawnScrapHeli", userId);
            if (type == "minicopter" || type == "mini") return Msg("SpawnMini", userId);
            if (type == "f15" || type == "flyby") return Msg("SpawnF15", userId);
            if (type == "roadambush" || type == "ambush") return Msg("SpawnAmbush", userId);
            if (type == "tunnelsquad" || type == "tunnel") return Msg("SpawnTunnel", userId);
            if (type == "subwaypatrol" || type == "subway" || type == "metro")
            {
                string g = TryGetNpcModuleGrid();
                return Msg("SpawnSubway", userId, string.IsNullOrEmpty(g) ? "?" : g);
            }
            if (type == "mineguard" || type == "mine" || type == "mines" || type == "caveguard")
            {
                string g = TryGetNpcModuleGrid();
                return Msg("SpawnMineGuard", userId, string.IsNullOrEmpty(g) ? "?" : g);
            }
            if (type == "railpatrol" || type == "rail" || type == "trainpatrol" || type == "surfacerail")
            {
                string g = TryGetNpcModuleGrid();
                return Msg("SpawnRailPatrol", userId, string.IsNullOrEmpty(g) ? "?" : g);
            }
            if (type == "peacekeeperpatrol" || type == "peacekeepers") return Msg("SpawnPeacekeepers", userId);
            if (type == "baseraid" || type == "npcraid" || type == "raid") return Msg("SpawnBaseRaid", userId);
            if (type == "airfieldchinook" || type == "airfieldch47" || type == "stealchinook" || type == "stealinook")
                return Msg("SpawnAirfieldChinook", userId);
            return Msg("SpawnGeneric", userId, type);
        }


        /// <summary>
        /// Run as early as Init so Facepunch EventSchedule does not free-fire a cargo plane
        /// before OnServerInitialized applies full DisableVanillaEventSystems.
        /// </summary>
        private void EarlyVanillaSuppress()
        {
            try
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.events false");
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "cargoship.event_enabled false");
            }
            catch { }
        }

        private void DisableVanillaEventSystems()
        {
            // Fully own scheduled world events — plugin fires cargo/heli/chinook/airdrop itself.
            // server.events gates plane + CH47 + patrol heli; cargoship is a separate convar.
            // Skip set_event_enabled with guessed names — Facepunch logs "Unknown event".
            var cmds = new[]
            {
                "server.events false",
                "cargoship.event_enabled false",
                // Do NOT set heli.lifetimeminutes to 0 — PatrolHelicopterAI reads the ConVar and
                // immediately retires (flies to deep sea). Vanilla schedule is already off via server.events.
                "heli.lifetimeminutes 99999",
                "bradley.enabled false",
                "bradley.respawndelayminutes 99999",
                // Vanilla HAB population must be 0 or idle-despawn fights endless respawns
                "hotairballoon.population 0",
            };
            foreach (var cmd in cmds)
            {
                try { ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd); }
                catch { }
            }

            Puts("[Events] Vanilla suppression: server.events=0, cargoship.event_enabled=0, bradley.enabled=0, heli.lifetime=99999, hotairballoon.population=0");

            timer.Once(3f, () => PurgeVanillaHotAirBalloons("boot+3s"));
            timer.Once(25f, () => PurgeVanillaHotAirBalloons("boot+25s"));
            timer.Once(5f, KillVanillaBradleys);
            timer.Once(30f, KillVanillaBradleys);
            timer.Once(90f, KillVanillaBradleys);
            // Boot EventSchedule can still queue one wave — strip residual non-plugin vehicles / planes
            timer.Once(2f, CleanupBootVanillaEvents);
            timer.Once(8f, CleanupBootVanillaEvents);
            timer.Once(20f, CleanupBootVanillaEvents);
            timer.Once(45f, CleanupBootVanillaEvents);
            timer.Once(90f, CleanupBootVanillaEvents);
            timer.Once(150f, CleanupBootVanillaEvents);
        }

        /// <summary>
        /// Remove cargo planes / CH47 / patrol helis / cargo ships that vanilla queued at boot
        /// before our convars stuck. Never touches plugin-tracked Bradleys or scientists.
        /// Cargo ships must be torn down carefully or their AI + scientists spam NRE every frame.
        /// </summary>
        private void CleanupBootVanillaEvents()
        {
            if (config == null || !config.DisableVanillaEvents) return;
            // Long enough to catch delayed EventSchedule cargo planes after map load
            if (Time.realtimeSinceStartup > 300f) return;

            // Collect first — never Kill while iterating serverEntities
            var toKill = new List<BaseEntity>();
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null) continue;
                    var be = ent as BaseEntity;
                    if (be == null || be.IsDestroyed) continue;
                    string prefab = (be.PrefabName ?? be.ShortPrefabName ?? "").ToLowerInvariant();

                    bool isVanillaEvent =
                        (prefab.Contains("cargo_plane") && !prefab.Contains("gib")) ||
                        (prefab.Contains("ch47scientists") && !prefab.Contains("gib")) ||
                        (prefab.Contains("patrolhelicopter") && !prefab.Contains("marker") && !prefab.Contains("gib")) ||
                        (prefab.Contains("cargoship") && !prefab.Contains("gib") && !prefab.Contains("subents"));

                    if (!isVanillaEvent) continue;

                    try
                    {
                        ulong id = be.net?.ID.Value ?? 0;
                        if (id != 0 && (_heliRouteTimers.ContainsKey(id) || _pluginBradleyIds.Contains(id) || _cargoTracks.ContainsKey(id) || _pluginCargoPlaneIds.Contains(id)))
                            continue;
                    }
                    catch { }

                    toKill.Add(be);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"CleanupBootVanillaEvents scan: {ex.Message}");
            }

            int killed = 0;
            foreach (var be in toKill)
            {
                if (be == null || be.IsDestroyed) continue;
                try
                {
                    string name = be.ShortPrefabName ?? "entity";
                    DebugLog($"Boot vanilla event removed: {name} at {PositionToGrid(be.transform.position)}");
                    SafeKillWorldEvent(be);
                    killed++;
                }
                catch (Exception ex)
                {
                    DebugLog($"Boot kill failed: {ex.Message}");
                }
            }

            if (killed > 0)
            {
                InvalidateEntityCache();
                Puts($"[Events] Removed {killed} boot-queued vanilla event entit(y/ies)");
            }
        }

        /// <summary>
        /// Tear down cargo / heli / CH47 without leaving orphan AI that NRE-spams.
        /// </summary>
        private void SafeKillWorldEvent(BaseEntity root)
        {
            if (root == null || root.IsDestroyed) return;

            string prefab = (root.PrefabName ?? root.ShortPrefabName ?? "").ToLowerInvariant();
            Vector3 origin = root.transform != null ? root.transform.position : Vector3.zero;

            // 1) Kill nearby scientists that rode on this vehicle (cargo / CH47)
            if (prefab.Contains("cargoship") || prefab.Contains("ch47"))
            {
                var riders = new List<BaseEntity>();
                try
                {
                    foreach (var ent in BaseNetworkable.serverEntities)
                    {
                        if (ent == null) continue;
                        var be = ent as BaseEntity;
                        if (be == null || be.IsDestroyed || be == root) continue;
                        string p = (be.PrefabName ?? "").ToLowerInvariant();
                        if (p.IndexOf("scientist", StringComparison.OrdinalIgnoreCase) < 0 &&
                            p.IndexOf("npcplayer", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        try
                        {
                            if (Vector3.Distance(be.transform.position, origin) > 120f) continue;
                        }
                        catch { continue; }
                        riders.Add(be);
                    }
                }
                catch { }

                foreach (var r in riders)
                {
                    try
                    {
                        if (r != null && !r.IsDestroyed)
                            r.Kill();
                    }
                    catch { }
                }
            }

            // 2) Prefer CargoShip.StartEgress when available (cleaner than hard Kill)
            if (prefab.Contains("cargoship"))
            {
                var cargo = root as CargoShip;
                if (cargo != null && TryInvokeInstance(cargo, typeof(CargoShip), "StartEgress"))
                {
                    // Hard kill shortly after so it doesn't linger mid-ocean
                    timer.Once(8f, () =>
                    {
                        try
                        {
                            if (cargo != null && !cargo.IsDestroyed)
                                cargo.Kill();
                        }
                        catch { }
                    });
                    return;
                }
            }

            // 3) Hard kill root — never crates
            if (IsLootCrateEntity(root)) return;
            try { root.Kill(); } catch { }
        }


        /// <summary>
        /// After hotairballoon.population is forced to 0, remove existing world HABs so
        /// despawn control is not fighting a leftover vanilla population.
        /// Skips occupied balloons (player mounted).
        /// </summary>
        private void PurgeVanillaHotAirBalloons(string reason)
        {
            int killed = 0;
            var list = new List<BaseEntity>();
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    string prefab = ent.ShortPrefabName ?? ent.PrefabName ?? "";
                    if (prefab.IndexOf("hotairballoon", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var be = ent as BaseEntity;
                    if (be != null)
                        list.Add(be);
                }
            }
            catch { }

            for (int i = 0; i < list.Count; i++)
            {
                var be = list[i];
                if (be == null || be.IsDestroyed) continue;
                try
                {
                    bool occupied = false;
                    try
                    {
                        var mountables = be.GetComponentsInChildren<BaseMountable>();
                        if (mountables != null)
                        {
                            for (int m = 0; m < mountables.Length; m++)
                            {
                                var mt = mountables[m];
                                if (mt != null && mt.GetMounted() != null)
                                {
                                    occupied = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                    if (occupied) continue;

                    be.Kill();
                    killed++;
                }
                catch { }
            }

            if (killed > 0)
            {
                try { InvalidateEntityCache(); } catch { }
                Puts($"[Events] Purged {killed} hot air balloon(s) after population=0 ({reason})");
            }
            else
                DebugLog($"HAB purge ({reason}): none removed");
        }


        private void KillVanillaBradleys()
        {
            if (config == null || !config.DisableVanillaEvents) return;
            int killed = 0;
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                string prefab = be.ShortPrefabName ?? be.PrefabName ?? "";
                if (prefab.IndexOf("bradleyapc", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (prefab.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                ulong id = 0;
                try { id = be.net?.ID.Value ?? 0; } catch { }
                if (id != 0 && _pluginBradleyIds.Contains(id)) continue;

                try
                {
                    be.Kill();
                    killed++;
                }
                catch { }
            }
            if (killed > 0)
            {
                InvalidateEntityCache("bradleyapc");
                Puts($"[Events] Removed {killed} vanilla Bradley(s) (Launch Site / residual)");
            }
        }

        private void TryF15Flyby()
        {
            string key = "f15";
            DateTime nowUtc = DateTime.UtcNow;
            DateTime last = _lastEventTime.ContainsKey(key) ? _lastEventTime[key] : DateTime.MinValue;
            double daysSince = last == DateTime.MinValue ? 999.0 : (nowUtc - last).TotalDays;

            float minDays = Mathf.Max(1f, config.F15Flyby.MinDaysBetween);
            float maxDays = Mathf.Max(minDays, config.F15Flyby.MaxDaysBetween);

            if (daysSince < minDays) return;

            float chance = Mathf.Clamp01((float)((daysSince - minDays) / (maxDays - minDays))) * 0.15f;
            // Late-wipe pressure: F-15E more likely toward PeakWipeDay
            chance *= GetWipeDayChanceMultiplier("f15");
            if (UnityEngine.Random.value < chance)
            {
                // Special wipe-day style build-up
                BroadcastKey("F15MilitaryAlert");
                BroadcastKey("F15Radar");

                timer.Once(30f, () => BroadcastKey("F15Confirm"));
                timer.Once(90f, () =>
                {
                    DoF15Flyby();
                    _lastEventTime[key] = DateTime.UtcNow;
                    SaveEventData();
                });
            }
        }

        #endregion

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return true; // console
            try
            {
                if (player.net?.connection != null && player.net.connection.authLevel >= 2)
                    return true;
            }
            catch { }
            string id = player.UserIDString;
            return permission.UserHasPermission(id, AdminPermission)
                || permission.UserHasPermission(id, LiveStatsAdminPermission)
                || permission.UserHasPermission(id, LiveStatsWorldAdminPermission);
        }

        /// <summary>
        /// Scans the world for live event entities and reports name + grid to the admin.
        /// </summary>
        private void SendActiveEventEntities(BasePlayer player)
        {
            // label -> list of grid strings (may have multiple of some types)
            var found = new List<(string label, string grid, Vector3 pos)>();

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent == null) continue;
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;

                string prefab = (be.PrefabName ?? be.ShortPrefabName ?? "").ToLowerInvariant();
                string label = null;

                if (prefab.Contains("patrolhelicopter") && !prefab.Contains("marker") && !prefab.Contains("gib"))
                    label = "Patrol Heli";
                else if (prefab.Contains("cargoship") && !prefab.Contains("gib"))
                    label = "Cargo Ship";
                else if (prefab.Contains("ch47scientists") || (prefab.Contains("ch47") && prefab.Contains("scientists")))
                    label = "Chinook";
                else if (prefab.Contains("bradleyapc") && !prefab.Contains("gib"))
                    label = "Bradley";
                else if (prefab.Contains("attackhelicopter") && !prefab.Contains("gib"))
                    label = "Attack Heli";
                else if (prefab.Contains("cargo_plane") || prefab.EndsWith("cargo_plane.prefab"))
                    label = "Supply Plane";
                else if (prefab.Contains("supply_drop") && !prefab.Contains("signal"))
                    label = "Supply Drop";
                else if (prefab.Contains("codelockedhackablecrate") || prefab.Contains("hackablecrate.entity"))
                    label = "Hackable Crate";
                else if (prefab.Contains("tugboat") && !prefab.Contains("gib"))
                    label = "Tugboat";
                else if (prefab.Contains("hotairballoon") && !prefab.Contains("gib"))
                    label = "Hot Air Balloon";
                else if (prefab.Contains("submarine") && !prefab.Contains("gib") && !prefab.Contains("module"))
                    label = "Submarine";
                else if (prefab.Contains("scraptransporthelicopter") && !prefab.Contains("entity"))
                    label = "Scrap Heli";
                else if (prefab.Contains("minicopter.entity"))
                    label = "Minicopter";
                else if (prefab.Contains("f15e") || prefab.Contains("f15"))
                    label = "F-15";

                if (label == null) continue;

                Vector3 p = be.transform.position;
                // Only skip true void/under-map entities. Cave MineGuard crates sit around y=-20..-80
                // and used to be hidden by a y < -50 filter (false "no crate at C22").
                bool isHackable = label == "Hackable Crate";
                if (!isHackable && p.y < -200f) continue;
                if (isHackable && p.y < -400f) continue;

                found.Add((label, PositionToGrid(p), p));
            }

            // Also surface event-protected crate nets that might have been missed by prefab scan
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_eventProtectedCrateUntil != null)
                {
                    foreach (var kv in _eventProtectedCrateUntil)
                    {
                        if (kv.Value < now) continue;
                        // try resolve entity for grid
                        BaseNetworkable bn = null;
                        try { bn = BaseNetworkable.serverEntities.Find(new NetworkableId(kv.Key)); } catch { }
                        if (bn == null || bn.IsDestroyed) continue;
                        var be2 = bn as BaseEntity;
                        if (be2 == null) continue;
                        Vector3 p2 = be2.transform.position;
                        string g2 = PositionToGrid(p2);
                        bool already = false;
                        for (int i = 0; i < found.Count; i++)
                        {
                            if (found[i].label == "Hackable Crate" && found[i].grid == g2)
                            {
                                already = true;
                                break;
                            }
                        }
                        if (!already)
                            found.Add(("Hackable Crate", g2, p2));
                    }
                }
            }
            catch { }

            string uid = player?.UserIDString;
            if (found.Count == 0)
            {
                SendReply(player, Msg("StatusNoneActive", uid));
                return;
            }

            // Compact: one line per type. Built inline so stale lang files cannot FormatException.
            var groups = found.GroupBy(f => f.label).OrderBy(g => g.Key).ToList();
            SendReply(player, $"Active: {found.Count} entities ({groups.Count} types)");

            const int maxGridsPerType = 6;
            foreach (var group in groups)
            {
                var grids = group.Select(x => x.grid).Distinct().ToList();
                string gridText = grids.Count <= maxGridsPerType
                    ? string.Join(", ", grids)
                    : string.Join(", ", grids.Take(maxGridsPerType)) + $" +{grids.Count - maxGridsPerType}";

                SendReply(player, $"  {group.Key} ({group.Count()}): <color=#55ff55>{gridText}</color>");
            }

            // Protected event crates summary
            try
            {
                int prot = 0;
                float now2 = Time.realtimeSinceStartup;
                if (_eventProtectedCrateUntil != null)
                {
                    foreach (var kv in _eventProtectedCrateUntil)
                        if (kv.Value >= now2) prot++;
                }
                if (prot > 0)
                    SendReply(player, $"  Event-protected crates: <color=#ffaa00>{prot}</color>");
            }
            catch { }
        }



        /// <summary>
        /// Ask a companion module to spawn. Returns null if module missing / declined (caller uses inline).
        /// Returns true/false if module handled the spawn.
        /// </summary>
        private bool? TryModuleSpawn(string moduleName, string type)
        {
            _lastModuleSpawnGrid = null;
            Plugin mod = ResolveModulePlugin(moduleName);
            if (mod == null || !mod.IsLoaded)
            {
                DebugLog($"{type}: soft mode inline fallback (module '{moduleName}' missing or not loaded)");
                return null;
            }

            try
            {
                object result = InvokeModuleSpawn(mod, moduleName, type);
                if (result == null)
                {
                    DebugLog($"{type}: soft mode inline fallback (module '{mod.Name}' spawn returned null)");
                    return null;
                }

                if (result is int iv)
                {
                    DebugLog($"{type}: module '{mod.Name}' spawn -> {iv}");
                    if (iv != 0) RequestStationaryNpcPin(type);
                    return iv != 0;
                }
                if (result is long lng)
                {
                    DebugLog($"{type}: module '{mod.Name}' spawn -> {lng}");
                    return lng != 0;
                }
                if (result is bool b)
                {
                    DebugLog($"{type}: module '{mod.Name}' spawn -> {b}");
                    if (b) RequestStationaryNpcPin(type);
                    return b;
                }
                if (result is string s)
                {
                    if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.Length >= 2 && s.Length <= 8)
                    {
                        _lastModuleSpawnGrid = s;
                        DebugLog($"{type}: module '{mod.Name}' spawn grid -> {s}");
                        RequestStationaryNpcPin(type);
                        return true;
                    }
                }

                DebugLog($"{type}: module '{mod.Name}' spawn unexpected {result.GetType().Name}={result}");
            }
            catch (Exception ex)
            {
                DebugLog($"TryModuleSpawn {moduleName}/{type}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Moving squads pin after they walk. Stationary events (mine crate,
        /// ambush camp, tunnel hold) should pin at the spawn grid immediately.
        /// </summary>
        private void RequestStationaryNpcPin(string type)
        {
            if (string.IsNullOrEmpty(type)) return;
            string t = type.ToLowerInvariant().Trim();
            bool stationary =
                t == "mineguard" || t == "mine" || t == "mines" || t == "caveguard" ||
                t == "roadambush" || t == "ambush" ||
                t == "tunnelsquad" || t == "tunnel";
            if (!stationary) return;
            try
            {
                if (LiveStatsEventsNPC == null)
                    LiveStatsEventsNPC = plugins.Find("LiveStatsEventsNPC");
                if (LiveStatsEventsNPC == null) return;
                if (string.IsNullOrEmpty(_lastModuleSpawnGrid))
                {
                    try
                    {
                        object g = LiveStatsEventsNPC.Call("API_GetLastSpawnGrid");
                        if (g is string gs && !string.IsNullOrEmpty(gs))
                            _lastModuleSpawnGrid = gs;
                    }
                    catch { }
                }
                LiveStatsEventsNPC.Call("API_ForceNpcLivePin", t);
                DebugLog($"{t}: requested stationary live pin grid={_lastModuleSpawnGrid}");
            }
            catch (Exception ex)
            {
                DebugLog($"RequestStationaryNpcPin: {ex.Message}");
            }
        }


        private Plugin ResolveModulePlugin(string moduleName)
        {
            Plugin mod = null;
            if (moduleName == ModuleVehicles) mod = LiveStatsEventsVehicles;
            else if (moduleName == ModuleNpc) mod = LiveStatsEventsNPC;

            if (mod == null)
            {
                try
                {
                    if (moduleName == ModuleVehicles)
                        mod = LiveStatsEventsVehicles = plugins.Find("LiveStatsEventsVehicles");
                    else if (moduleName == ModuleNpc)
                        mod = LiveStatsEventsNPC = plugins.Find("LiveStatsEventsNPC");
                }
                catch { }
            }
            return mod;
        }


        /// <summary>
        /// uMod-compliant module invoke: Plugin.Call + Interface.CallHook only (no System.Reflection).
        /// Companions must return int 1/0 from API_Spawn — never null, avoid raw bool (some Oxide builds coerce false->null).
        /// </summary>
        private object InvokeModuleSpawn(Plugin mod, string moduleName, string type)
        {
            if (mod == null || string.IsNullOrEmpty(type)) return null;
            string t = type.ToLowerInvariant().Trim();

            // 1) Standard uMod inter-plugin API
            try
            {
                object result = mod.Call("API_Spawn", t);
                if (result != null)
                {
                    DebugLog($"{t}: module Plugin.Call API_Spawn -> {result} (no reflection)");
                    return result;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"{t}: Call API_Spawn failed: {ex.Message}");
            }

            // 2) Dedicated per-event APIs (still Plugin.Call — no reflection)
            string dedicated = null;
            if (moduleName == ModuleNpc)
            {
                if (t == "mineguard" || t == "mine" || t == "mines" || t == "caveguard")
                    dedicated = "API_SpawnMineGuard";
                else if (t == "subwaypatrol" || t == "subway" || t == "metro")
                    dedicated = "API_SpawnSubwayPatrol";
                else if (t == "railpatrol" || t == "rail" || t == "trainpatrol" || t == "surfacerail")
                    dedicated = "API_SpawnRailPatrol";
                else if (t == "heavyscientists" || t == "heavies" || t == "heavyscientific")
                    dedicated = "API_SpawnHeavyScientists";
                else if (t == "roadambush" || t == "ambush")
                    dedicated = "API_SpawnRoadAmbush";
                else if (t == "tunnelsquad" || t == "tunnel")
                    dedicated = "API_SpawnTunnelSquad";
                else if (t == "peacekeeperpatrol" || t == "peacekeepers" || t == "peacekeeper")
                    dedicated = "API_SpawnPeacekeeperPatrol";
                else if (t == "baseraid" || t == "npcraid" || t == "raid")
                    dedicated = "API_SpawnBaseRaid";
            }
            else if (moduleName == ModuleVehicles)
            {
                if (t == "tugboat") dedicated = "API_SpawnTugboat";
                else if (t == "submarine" || t == "sub") dedicated = "API_SpawnSubmarine";
                else if (t == "scrapheli" || t == "scraptransport" || t == "scraptransporthelicopter")
                    dedicated = "API_SpawnScrapHeli";
                else if (t == "minicopter" || t == "mini") dedicated = "API_SpawnMinicopter";
            }

            if (dedicated != null)
            {
                try
                {
                    object result = mod.Call(dedicated);
                    if (result != null)
                    {
                        DebugLog($"{t}: module Call {dedicated} -> {result}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"{t}: Call {dedicated} failed: {ex.Message}");
                }
            }

            // 3) Global hook — companions implement OnLiveStatsEventsModuleSpawn
            try
            {
                object result = Interface.CallHook("OnLiveStatsEventsModuleSpawn", moduleName, t);
                if (result != null)
                {
                    DebugLog($"{t}: CallHook OnLiveStatsEventsModuleSpawn -> {result}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"{t}: CallHook failed: {ex.Message}");
            }

            return null;
        }

        // ==================== OPTION A MODULE API (companion plugins) ====================
        /// <summary>Called by LiveStatsEventsVehicles / LiveStatsEventsNPC on load.</summary>
        public void API_RegisterModule(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return;
            _registeredModules.Add(moduleName.Trim().ToLowerInvariant());
            Puts($"[Events] Module registered: {moduleName}");
        }

        public void API_UnregisterModule(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return;
            _registeredModules.Remove(moduleName.Trim().ToLowerInvariant());
            Puts($"[Events] Module unregistered: {moduleName}");
        }

        public bool API_IsModuleRegistered(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return false;
            return _registeredModules.Contains(moduleName.Trim().ToLowerInvariant());
        }

        public bool API_IsReady() => _ready && config != null && config.Enabled;

        /// <summary>True when this event type is allowed under current module gates.</summary>
        public bool API_IsEventTypeAllowed(string type)
        {
            return IsEventTypeAllowed(type);
        }

        private bool IsEventTypeAllowed(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            string key = type.ToLowerInvariant();
            var mod = config?.Modules ?? new ModuleConfig();

            bool isVehicle =
                key == "tugboat" || key == "submarine" || key == "sub" || key == "minicopter" || key == "mini"
                || key == "scrapheli" || key == "scraptransport" || key == "scraptransporthelicopter"
                || key == "hotairballoon" || key == "balloon";
            bool isNpc =
                key == "heavyscientists" || key == "heavies" || key == "heavyscientific"
                || key == "roadambush" || key == "ambush" || key == "tunnelsquad" || key == "tunnel"
                || key == "subwaypatrol" || key == "subway" || key == "metro"
                || key == "mineguard" || key == "mine" || key == "mines" || key == "caveguard"
                || key == "railpatrol" || key == "rail" || key == "trainpatrol" || key == "surfacerail"
                || key == "peacekeeperpatrol" || key == "peacekeepers" || key == "peacekeeper"
                || key == "baseraid" || key == "npcraid" || key == "raid";

            if (isVehicle)
            {
                if (!mod.Vehicles) return false;
                if (mod.RequireCompanionPlugins && !_registeredModules.Contains(ModuleVehicles))
                    return false;
                return true;
            }
            if (isNpc)
            {
                if (!mod.Npc) return false;
                if (mod.RequireCompanionPlugins && !_registeredModules.Contains(ModuleNpc))
                    return false;
                return true;
            }
            return true; // core types always allowed when events enabled
        }

        /// <summary>Companion plugins may ask Core to note that an event fired (global cooldown + persistence).</summary>
        public void API_NotifyEventFired(string type)
        {
            if (string.IsNullOrEmpty(type)) return;
            string key = type.ToLowerInvariant();
            _lastEventTime[key] = DateTime.UtcNow;
            ScheduleNextEventGap(DateTime.UtcNow);
            SaveEventData();
        }


        public object API_SpawnPatrolHeliAt(Vector3 interest)
        {
            try { return SpawnPatrolHeliAt(interest); }
            catch (Exception ex) { DebugLog($"API_SpawnPatrolHeliAt: {ex.Message}"); return false; }
        }

        public object API_CountPatrolHelis()
        {
            try { return CountPatrolHelis(); }
            catch { return 0; }
        }

        public string API_PositionToGrid(Vector3 pos) => PositionToGrid(pos);

        public void API_Broadcast(string message) => Broadcast(message);

        public void API_DebugLog(string message) => DebugLog(message);


        #region Commands

        private bool SpawnEventAndFeed(string type)
        {
            bool ok = SpawnEvent(type);
            if (ok)
            {
                NotifyEventFeed(type, "spawn");
                ScheduleNextEventGap(DateTime.UtcNow);
            }
            return ok;
        }

        [ChatCommand("event")]
        private void CmdEvent(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player))
            {
                player.ChatMessage(Msg("NoPermission", player.UserIDString));
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendReply(player, Msg("UsageEvent", player?.UserIDString));
                return;
            }

            string sub = args[0].ToLower().Replace("_", "").Replace("-", "");

            // Normalize aliases
            if (sub == "supply" || sub == "drop" || sub == "airdrop" || sub == "plane" || sub == "cargoplane")
                sub = "supplydrop";
            else if (sub == "ship")
                sub = "cargo";
            else if (sub == "helicopter" || sub == "patrol" || sub == "patrolheli")
                sub = "heli";
            else if (sub == "ch47" || sub == "ch47scientists")
                sub = "chinook";
            else if (sub == "tank")
                sub = "bradley";
            else if (sub == "flyby")
                sub = "f15";

            // reload + status must work even when the event system is not running
            // (e.g. Enabled=false or LiveStatsWorld missing) so admins can fix it
            if (sub != "reload" && sub != "status" && !_ready)
            {
                SendReply(player, Msg("WorldRequired", player?.UserIDString));
                return;
            }

            switch (sub)
            {
                case "status":
                    SendReply(player, Msg("StatusEnabled", player?.UserIDString, config != null && config.Enabled));
                    if (config != null)
                    {
                        SendReply(player, Msg("StatusMode", player?.UserIDString, config.UseScheduledEvents ? "Scheduled" : "Random"));
                        SendReply(player, Msg("StatusPlayers", player?.UserIDString, BasePlayer.activePlayerList?.Count ?? 0, config.MinPlayersOnline));
                    }
                    if (!_ready)
                    {
                        if (config == null || !config.Enabled)
                            SendReply(player, Msg("StatusStopped", player?.UserIDString));
                        else
                            SendReply(player, Msg("WorldRequired", player?.UserIDString));
                    }
                    else
                        SendActiveEventEntities(player);
                    break;
                case "reload":
                    LoadConfig();
                    RestartEventSystem();
                    if (!_ready)
                    {
                        if (config == null || !config.Enabled)
                            SendReply(player, Msg("ConfigReloadedDisabled", player?.UserIDString));
                        else
                            SendReply(player, Msg("WorldRequired", player?.UserIDString));
                    }
                    else
                    {
                        SendReply(player, Msg("ConfigReloaded", player?.UserIDString));
                        SendReply(player, Msg("StatusMode", player?.UserIDString, config.UseScheduledEvents ? "Scheduled" : "Random"));
                        SendReply(player, Msg("StatusPlayers", player?.UserIDString, BasePlayer.activePlayerList?.Count ?? 0, config.MinPlayersOnline));
                    }
                    break;
                case "supplydrop":
                    SpawnEventAndFeed("supplydrop");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "supplydrop"));
                    break;
                case "cargo":
                case "cargoship":
                    SpawnEventAndFeed("cargoship");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "cargoship"));
                    break;
                case "heli":
                case "patrolheli":
                case "patrolhelicopter":
                    SpawnEventAndFeed("patrolheli");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "patrolheli"));
                    break;
                case "chinook":
                    SpawnEventAndFeed("chinook");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "chinook"));
                    break;
                case "bradley":
                    SpawnEventAndFeed("bradley");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "bradley"));
                    break;
                case "attackheli":
                case "attackhelicopter":
                    SpawnEventAndFeed("attackheli");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "attackheli"));
                    break;
                case "tugboat":
                    SpawnEventAndFeed("tugboat");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "tugboat"));
                    break;
                case "hotairballoon":
                case "balloon":
                    SendReply(player, "Hot Air Balloon spawn events are disabled (despawn-only).");
                    break;
                case "heavyscientists":
                case "heavies":
                    SpawnEventAndFeed("heavyscientists");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "heavyscientists"));
                    break;
                case "hackablecrate":
                case "lockedcrate":
                    SpawnEventAndFeed("hackablecrate");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "hackablecrate"));
                    break;
                case "submarine":
                case "sub":
                    SpawnEventAndFeed("submarine");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "submarine"));
                    break;
                case "scrapheli":
                case "scraptransport":
                    SpawnEventAndFeed("scrapheli");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "scrapheli"));
                    break;
                case "minicopter":
                case "mini":
                    SpawnEventAndFeed("minicopter");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "minicopter"));
                    break;
                case "roadambush":
                case "ambush":
                    SpawnEventAndFeed("roadambush");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "roadambush"));
                    break;
                case "tunnelsquad":
                case "tunnel":
                    SpawnEventAndFeed("tunnelsquad");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "tunnelsquad"));
                    break;
                case "subwaypatrol":
                case "subway":
                case "metro":
                    SpawnEventAndFeed("subwaypatrol");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "subwaypatrol"));
                    break;
                case "mineguard":
                case "mine":
                case "mines":
                case "caveguard":
                    SpawnEventAndFeed("mineguard");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "mineguard"));
                    break;
                case "railpatrol":
                case "rail":
                case "trainpatrol":
                    SpawnEventAndFeed("railpatrol");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "railpatrol"));
                    break;
                case "peacekeeperpatrol":
                case "peacekeepers":
                case "peacekeeper":
                    SpawnEventAndFeed("peacekeeperpatrol");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "peacekeeperpatrol"));
                    break;
                case "baseraid":
                case "npcraid":
                case "raid":
                    SpawnEventAndFeed("baseraid");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "baseraid"));
                    break;
                case "airfieldchinook":
                case "airfieldch47":
                case "stealchinook":
                case "stealinook":
                case "airfield":
                    SpawnEventAndFeed("airfieldchinook");
                    SendReply(player, Msg("ForcedEvent", player?.UserIDString, "airfieldchinook"));
                    break;
                case "f15":
                {
                    float fx, fy, fz;
                    if (args.Length >= 4 &&
                        float.TryParse(args[1], out fx) &&
                        float.TryParse(args[2], out fy) &&
                        float.TryParse(args[3], out fz))
                    {
                        DoF15Flyby(new Vector3(fx, fy, fz));
                        SendReply(player, Msg("F15SpawnedCoords", player?.UserIDString, fx, fy, fz));
                    }
                    else
                    {
                        DoF15Flyby();
                        SendReply(player, Msg("F15Started", player?.UserIDString));
                    }
                    break;
                }
                default:
                    SendReply(player, Msg("UnknownEvent", player?.UserIDString));
                    break;
            }
        }

        // Convenience aliases so /supplydrop works directly
        [ChatCommand("supplydrop")]
        private void CmdSupplyDrop(BasePlayer player, string command, string[] args)
        {
            CmdEvent(player, command, new[] { "supplydrop" });
        }

        [ChatCommand("airdrop")]
        private void CmdAirDrop(BasePlayer player, string command, string[] args)
        {
            CmdEvent(player, command, new[] { "supplydrop" });
        }

        [ConsoleCommand("livestats.event")]
        private void ConEvent(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player() != null && !HasAdmin(arg.Player()))
            {
                arg.ReplyWith(Msg("PermissionDenied"));
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith(Msg("UsageEvent"));
                return;
            }
            string eventType = arg.Args[0].ToString();
            SpawnEvent(eventType);
            arg.ReplyWith(Msg("ForcedEvent", null, eventType));
        }

        private void SendReply(BasePlayer player, string msg)
        {
            if (player != null) player.ChatMessage(msg);
            else Puts(msg);
        }

        #endregion

        #region Config

        public class ConfigData
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            /// <summary>When true, disables/kills vanilla event systems so this plugin fully owns cargo/heli/Bradley/etc. Default false for coexistence with other event plugins.</summary>
            [JsonProperty("DisableVanillaEvents")] public bool DisableVanillaEvents { get; set; } = false;

            /// <summary>
            /// Module gates. Vehicle/NPC event types only run when the companion plugin is loaded
            /// and has registered (and Modules.Vehicles / Modules.Npc are true).
            /// </summary>
            [JsonProperty("Modules")] public ModuleConfig Modules { get; set; } = new ModuleConfig();

            [JsonProperty("MinPlayersOnline")] public int MinPlayersOnline { get; set; } = 2;
            [JsonProperty("CheckIntervalSeconds")] public float CheckIntervalSeconds { get; set; } = 75f;
            [JsonProperty("Debug")] public bool Debug { get; set; } = false;

            /// <summary>Minimum minutes that must pass between any two world events (global, not per-type).</summary>
            [JsonProperty("MinMinutesBetweenAnyEvent")] public float MinMinutesBetweenAnyEvent { get; set; } = 5f;
            /// <summary>Maximum minutes for the random gap after an event fires. Actual wait is uniform in [Min, Max].</summary>
            [JsonProperty("MaxMinutesBetweenAnyEvent")] public float MaxMinutesBetweenAnyEvent { get; set; } = 20f;

            [JsonProperty("UseScheduledEvents")]
            public bool UseScheduledEvents { get; set; } = false; // false = pure random (default)

            // IMPORTANT: do NOT pre-populate this list with defaults in the initializer.
            // Newtonsoft will APPEND JSON items onto a pre-filled list (ObjectCreationHandling.Reuse),
            // which doubles every event and ignores Enabled flags from the config file.
            [JsonProperty("Events", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<EventDefinition> Events { get; set; } = new List<EventDefinition>();

            [JsonProperty("F15Flyby")]
            public F15Config F15Flyby { get; set; } = new F15Config();

            [JsonProperty("HeliPatrol")]
            public HeliPatrolConfig HeliPatrol { get; set; } = new HeliPatrolConfig();

            [JsonProperty("ChinookPatrol")]
            public ChinookPatrolConfig ChinookPatrol { get; set; } = new ChinookPatrolConfig();

            [JsonProperty("BradleyPatrol")]
            public BradleyPatrolConfig BradleyPatrol { get; set; } = new BradleyPatrolConfig();

            [JsonProperty("AttackHeliSpawn")]
            public AttackHeliSpawnConfig AttackHeliSpawn { get; set; } = new AttackHeliSpawnConfig();

            /// <summary>
            /// Caps for core-owned vehicles only (Chinook, Attack Heli).
            /// Player vehicles (tug/sub/mini/scrap) limits live in LiveStatsEventsVehicles.json -> Limits.
            /// </summary>
            [JsonProperty("VehicleLimits")]
            public VehicleLimitsConfig VehicleLimits { get; set; } = new VehicleLimitsConfig();

            /// <summary>
            /// Force cargo ships that finish their path (or sit idle in deep ocean) to egress / despawn
            /// so they do not park forever outside the playable grid.
            /// </summary>
            [JsonProperty("CargoShipLifecycle")]
            public CargoShipLifecycleConfig CargoShipLifecycle { get; set; } = new CargoShipLifecycleConfig();

            /// <summary>
            /// Late-wipe chance scaling only (schedule weight multipliers for BaseRaid / Patrol Heli / F-15).
            /// Squad size, rockets, offline targeting, heli-with-raid etc. live in LiveStatsEventsNPC.json -> BaseRaid.
            /// </summary>
            [JsonProperty("BaseRaid")]
            public BaseRaidConfig BaseRaid { get; set; } = new BaseRaidConfig();
            [JsonProperty("AirfieldChinook")]
            public AirfieldChinookConfig AirfieldChinook { get; set; } = new AirfieldChinookConfig();
        }

        /// <summary>
        /// Core-only late-wipe chance multipliers used by the event scheduler.
        /// Full BaseRaid squad / targeting settings live in LiveStatsEventsNPC.
        /// Extra JSON keys from old configs are ignored by Newtonsoft (no error).
        /// </summary>
        public class BaseRaidConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("MinWipeDay")] public int MinWipeDay { get; set; } = 15;
            /// <summary>Wipe day at which BaseRaid / late pressure reaches full chance multiplier.</summary>
            [JsonProperty("PeakWipeDay")] public int PeakWipeDay { get; set; } = 28;
            /// <summary>Chance multiplier on MinWipeDay (lowest). 0.15 = rare.</summary>
            [JsonProperty("ChanceAtMinWipeDay")] public float ChanceAtMinWipeDay { get; set; } = 0.15f;
            /// <summary>Chance multiplier at/after PeakWipeDay (highest). 1.8 = frequent.</summary>
            [JsonProperty("ChanceAtPeakWipeDay")] public float ChanceAtPeakWipeDay { get; set; } = 1.8f;
            /// <summary>Patrol Heli chance multiplier early in the wipe (day 1).</summary>
            [JsonProperty("PatrolHeliChanceEarly")] public float PatrolHeliChanceEarly { get; set; } = 0.70f;
            /// <summary>Patrol Heli chance multiplier at/after PeakWipeDay.</summary>
            [JsonProperty("PatrolHeliChancePeak")] public float PatrolHeliChancePeak { get; set; } = 1.65f;
            /// <summary>F-15E chance multiplier early in the wipe (day 1).</summary>
            [JsonProperty("F15ChanceEarly")] public float F15ChanceEarly { get; set; } = 0.60f;
            /// <summary>F-15E chance multiplier at/after PeakWipeDay.</summary>
            [JsonProperty("F15ChancePeak")] public float F15ChancePeak { get; set; } = 1.85f;
        }

        public class CargoShipLifecycleConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            /// <summary>Minutes after first sighting before we force egress (vanilla event is ~50m).</summary>
            [JsonProperty("MaxEventMinutes")] public float MaxEventMinutes { get; set; } = 55f;
            /// <summary>Seconds of near-zero movement (near map edge, mid-life+) before we force egress early.</summary>
            [JsonProperty("StuckSeconds")] public float StuckSeconds { get; set; } = 120f;
            /// <summary>If the ship is still on the map this many minutes after egress started, hard-kill it.</summary>
            [JsonProperty("ForceKillMinutesAfterEgress")] public float ForceKillMinutesAfterEgress { get; set; } = 15f;
        }

        public class ModuleConfig
        {
            /// <summary>
            /// true (default) = vehicle types require LiveStatsEventsVehicles; NPC types require LiveStatsEventsNPC.
            /// Core has no inline vehicle/NPC spawn fallback.
            /// </summary>
            [JsonProperty("RequireCompanionPlugins")] public bool RequireCompanionPlugins { get; set; } = true;
            /// <summary>Allow vehicle event types when LiveStatsEventsVehicles is loaded and registered.</summary>
            [JsonProperty("Vehicles")] public bool Vehicles { get; set; } = true;
            /// <summary>Allow scientist/ambush/tunnel/peacekeeper/base-raid when LiveStatsEventsNPC is loaded and registered.</summary>
            [JsonProperty("Npc")] public bool Npc { get; set; } = true;
        }

        public class EventDefinition
        {
            [JsonProperty("Type")] public string Type { get; set; }
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("MinIntervalMinutes")] public float MinIntervalMinutes { get; set; } = 60f;
            [JsonProperty("MaxIntervalMinutes")] public float MaxIntervalMinutes { get; set; } = 180f;
            [JsonProperty("Weight")] public float Weight { get; set; } = 1f;
            [JsonProperty("AnnounceMinutesBefore")] public float AnnounceMinutesBefore { get; set; } = 3f;

            // Only used when UseScheduledEvents = true
            [JsonProperty("Schedule")] public List<TimeWindow> Schedule { get; set; } = new List<TimeWindow>();
        }

        public class TimeWindow
        {
            [JsonProperty("StartHour")] public float StartHour { get; set; }
            [JsonProperty("EndHour")] public float EndHour { get; set; }
        }

        public class AirfieldChinookConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("MinWipeDay")] public int MinWipeDay { get; set; } = 10;
            [JsonProperty("MaxWipeDay")] public int MaxWipeDay { get; set; } = 24;
            [JsonProperty("MinPlayersOnline")] public int MinPlayersOnline { get; set; } = 1;
            [JsonProperty("SunriseStartHour")] public float SunriseStartHour { get; set; } = 5.0f;
            [JsonProperty("SunriseEndHour")] public float SunriseEndHour { get; set; } = 7.5f;
            [JsonProperty("SunsetStartHour")] public float SunsetStartHour { get; set; } = 18.0f;
            [JsonProperty("SunsetEndHour")] public float SunsetEndHour { get; set; } = 20.5f;
            [JsonProperty("LifetimeMinMinutes")] public float LifetimeMinMinutes { get; set; } = 45f;
            [JsonProperty("LifetimeMaxMinutes")] public float LifetimeMaxMinutes { get; set; } = 180f;
            [JsonProperty("GuardCount")] public int GuardCount { get; set; } = 4;
            [JsonProperty("FuelAmount")] public int FuelAmount { get; set; } = 200;
            /// <summary>Event will not fire while this many player ch47.entity already exist anywhere.</summary>
            [JsonProperty("MaxInWorld")] public int MaxInWorld { get; set; } = 1;
        }

        public class F15Config
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("MinDaysBetween")] public float MinDaysBetween { get; set; } = 2.5f;
            [JsonProperty("MaxDaysBetween")] public float MaxDaysBetween { get; set; } = 5.5f;
            [JsonProperty("CustomPrefab")] public string CustomPrefab { get; set; } = ""; // optional custom F-15 prefab path
        }

        public class ChinookPatrolConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("PatrolLifetimeMinutes")] public float PatrolLifetimeMinutes { get; set; } = 18f;
            [JsonProperty("CruiseAltitude")] public float CruiseAltitude { get; set; } = 110f;
            [JsonProperty("MaxMonumentWaypoints")] public int MaxMonumentWaypoints { get; set; } = 8;
            [JsonProperty("WaypointIntervalSeconds")] public float WaypointIntervalSeconds { get; set; } = 75f;
            [JsonProperty("MinTourMinutesBeforeDrop")] public float MinTourMinutesBeforeDrop { get; set; } = 8f;
            [JsonProperty("ForceDropIfHoveringSeconds")] public float ForceDropIfHoveringSeconds { get; set; } = 25f;
            [JsonProperty("EgressSeconds")] public float EgressSeconds { get; set; } = 40f;
            [JsonProperty("EgressKillSeconds")] public float EgressKillSeconds { get; set; } = 180f;
        }

        public class HeliPatrolConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            /// <summary>How long the heli roams under vanilla AI before deep-sea retire (minutes).</summary>
            [JsonProperty("PatrolLifetimeMinutes")] public float PatrolLifetimeMinutes { get; set; } = 15f;
            [JsonProperty("CruiseAltitude")] public float CruiseAltitude { get; set; } = 80f;
            // Legacy fields kept so old configs still deserialize without error
            [JsonProperty("UseMonuments")] public bool UseMonuments { get; set; } = true;
            [JsonProperty("MaxMonumentWaypoints")] public int MaxMonumentWaypoints { get; set; } = 6;
            [JsonProperty("MinWaypoints")] public int MinWaypoints { get; set; } = 4;
            [JsonProperty("WaypointIntervalSeconds")] public float WaypointIntervalSeconds { get; set; } = 75f;
            [JsonProperty("LoopRoute")] public bool LoopRoute { get; set; } = false;
            [JsonProperty("MaxRouteLoops")] public int MaxRouteLoops { get; set; } = 1;
            [JsonProperty("Waypoints")] public List<HeliWaypoint> Waypoints { get; set; } = new List<HeliWaypoint>();
        }

        public class HeliWaypoint
        {
            [JsonProperty("X")] public float X { get; set; }
            [JsonProperty("Z")] public float Z { get; set; }
        }

        public class BradleyPatrolConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            /// <summary>Max nodes on a full-road patrol (safety cap). Dense 12–15 m spacing.</summary>
            [JsonProperty("MaxWaypoints")] public int MaxWaypoints { get; set; } = 64;
            [JsonProperty("WaypointIntervalSeconds")] public float WaypointIntervalSeconds { get; set; } = 120f;
            [JsonProperty("LoopRoute")] public bool LoopRoute { get; set; } = false;
            /// <summary>How long a plugin Bradley stays before despawn (minutes).</summary>
            [JsonProperty("PatrolLifetimeMinutes")] public float PatrolLifetimeMinutes { get; set; } = 25f;
            [JsonProperty("Waypoints")] public List<HeliWaypoint> Waypoints { get; set; } = new List<HeliWaypoint>();
        }

        public class AttackHeliSpawnConfig
        {
            [JsonProperty("AllowMultiple")] public bool AllowMultiple { get; set; } = false;
            [JsonProperty("MaxSpawnPoints")] public int MaxSpawnPoints { get; set; } = 6;
            [JsonProperty("Waypoints")] public List<HeliWaypoint> Waypoints { get; set; } = new List<HeliWaypoint>();
        }

        /// <summary>
        /// Hard caps for player-controlled / stackable vehicles so the plugin does not flood the map.
        /// </summary>
        /// <summary>
        /// Caps for vehicles spawned by this core plugin only.
        /// Tug / sub / mini / scrap limits: LiveStatsEventsVehicles.json -> Limits.
        /// </summary>
        public class VehicleLimitsConfig
        {
            [JsonProperty("MaxAttackHelis")] public int MaxAttackHelis { get; set; } = 1;
            [JsonProperty("MaxChinooks")] public int MaxChinooks { get; set; } = 1;
        }

        /// <summary>
        /// Default event definitions used only when creating a brand-new config.
        /// Kept out of the property initializer so Newtonsoft does not append onto them.
        /// </summary>
        private static List<EventDefinition> CreateDefaultEvents()
        {
            return new List<EventDefinition>
            {
                new EventDefinition
                {
                    Type = "SupplyDrop",
                    Enabled = true,
                    MinIntervalMinutes = 75,
                    MaxIntervalMinutes = 180,
                    Weight = 1.2f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "CargoShip",
                    Enabled = true,
                    MinIntervalMinutes = 120,
                    MaxIntervalMinutes = 270,
                    Weight = 0.9f,
                    AnnounceMinutesBefore = 5
                },
                new EventDefinition
                {
                    Type = "PatrolHeli",
                    Enabled = true,
                    MinIntervalMinutes = 90,
                    MaxIntervalMinutes = 210,
                    Weight = 1.0f,
                    AnnounceMinutesBefore = 2
                },
                new EventDefinition
                {
                    Type = "Chinook",
                    Enabled = true,
                    MinIntervalMinutes = 140,
                    MaxIntervalMinutes = 300,
                    Weight = 0.8f,
                    AnnounceMinutesBefore = 4
                },
                new EventDefinition
                {
                    Type = "Bradley",
                    Enabled = false,
                    MinIntervalMinutes = 240,
                    MaxIntervalMinutes = 480,
                    Weight = 0.5f,
                    AnnounceMinutesBefore = 5
                },
                new EventDefinition
                {
                    Type = "AttackHeli",
                    Enabled = true,
                    MinIntervalMinutes = 180,
                    MaxIntervalMinutes = 360,
                    Weight = 0.7f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "Tugboat",
                    Enabled = true,
                    MinIntervalMinutes = 150,
                    MaxIntervalMinutes = 320,
                    Weight = 0.8f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    // Natural / player balloons only — despawn recycles locations; do not event-spawn
                    Type = "HotAirBalloon",
                    Enabled = false,
                    MinIntervalMinutes = 120,
                    MaxIntervalMinutes = 280,
                    Weight = 0.9f,
                    AnnounceMinutesBefore = 2
                },
                new EventDefinition
                {
                    Type = "HeavyScientists",
                    Enabled = true,
                    MinIntervalMinutes = 160,
                    MaxIntervalMinutes = 340,
                    Weight = 0.7f,
                    AnnounceMinutesBefore = 4
                },
                new EventDefinition
                {
                    Type = "HackableCrate",
                    Enabled = true,
                    MinIntervalMinutes = 100,
                    MaxIntervalMinutes = 240,
                    Weight = 1.0f,
                    AnnounceMinutesBefore = 2
                },
                new EventDefinition
                {
                    Type = "Submarine",
                    Enabled = true,
                    MinIntervalMinutes = 180,
                    MaxIntervalMinutes = 400,
                    Weight = 0.6f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "ScrapHeli",
                    Enabled = false,
                    MinIntervalMinutes = 200,
                    MaxIntervalMinutes = 420,
                    Weight = 0.5f,
                    AnnounceMinutesBefore = 2
                },
                new EventDefinition
                {
                    Type = "Minicopter",
                    Enabled = false,
                    MinIntervalMinutes = 150,
                    MaxIntervalMinutes = 300,
                    Weight = 0.6f,
                    AnnounceMinutesBefore = 1
                },
                new EventDefinition
                {
                    Type = "RoadAmbush",
                    Enabled = true,
                    MinIntervalMinutes = 140,
                    MaxIntervalMinutes = 300,
                    Weight = 0.9f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "TunnelSquad",
                    Enabled = true,
                    MinIntervalMinutes = 160,
                    MaxIntervalMinutes = 320,
                    Weight = 0.8f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "SubwayPatrol",
                    Enabled = true,
                    MinIntervalMinutes = 150,
                    MaxIntervalMinutes = 310,
                    Weight = 0.85f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "MineGuard",
                    Enabled = true,
                    MinIntervalMinutes = 100,
                    MaxIntervalMinutes = 220,
                    Weight = 1.15f,
                    AnnounceMinutesBefore = 3
                },
                new EventDefinition
                {
                    Type = "PeacekeeperPatrol",
                    Enabled = true,
                    MinIntervalMinutes = 120,
                    MaxIntervalMinutes = 260,
                    Weight = 0.85f,
                    AnnounceMinutesBefore = 2
                },
                new EventDefinition
                {
                    Type = "BaseRaid",
                    Enabled = true,
                    MinIntervalMinutes = 180,
                    MaxIntervalMinutes = 420,
                    Weight = 0.55f,
                    AnnounceMinutesBefore = 4
                },
                new EventDefinition
                {
                    Type = "AirfieldChinook",
                    Enabled = true,
                    MinIntervalMinutes = 240,
                    MaxIntervalMinutes = 540,
                    Weight = 0.55f,
                    AnnounceMinutesBefore = 4
                }
            };
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData
            {
                Events = CreateDefaultEvents()
            };
        }

        protected override void LoadConfig()
        {
            // Fully recover from corrupt / partially written JSON so the plugin still starts.
            try
            {
                base.LoadConfig();
                config = Config.ReadObject<ConfigData>();
            }
            catch (Exception ex)
            {
                PrintError($"Config load failed: {ex.Message}");
                Puts("[Events] Config file is invalid or corrupt - writing a fresh default config.");
                Puts("[Events] Your previous oxide/config/LiveStatsEvents.json was NOT kept. Re-apply any custom values after this load.");
                config = null;
            }

            if (config == null)
            {
                LoadDefaultConfig();
                SaveConfig();
                Puts("[Events] Wrote new default config");
                return;
            }

            // Only fill truly missing sections. Never strip or rewrite user Events.
            bool changed = false;

            if (config.Events == null || config.Events.Count == 0)
            {
                config.Events = CreateDefaultEvents();
                changed = true;
                Puts("[Events] Events section was null/empty - inserted defaults (other settings kept)");
            }
            else
            {
                // Safety net: if an older build already duplicated the list, keep the last entry per Type
                // (config-file values are last when Newtonsoft appended onto defaults).
                int before = config.Events.Count;
                var deduped = new List<EventDefinition>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = config.Events.Count - 1; i >= 0; i--)
                {
                    var evt = config.Events[i];
                    if (evt == null || string.IsNullOrEmpty(evt.Type)) continue;
                    string key = evt.Type.Trim();
                    if (!seen.Add(key)) continue;
                    deduped.Add(evt);
                }
                deduped.Reverse();
                if (deduped.Count != before)
                {
                    config.Events = deduped;
                    changed = true;
                    Puts($"[Events] Removed {before - deduped.Count} duplicate event entries from config");
                }
            }

            // Soft-add BaseRaid if an older config is missing it
            if (config.Events != null)
            {
                bool hasBaseRaid = false;
                foreach (var evt in config.Events)
                {
                    if (evt?.Type != null &&
                        (evt.Type.Equals("BaseRaid", StringComparison.OrdinalIgnoreCase) ||
                         evt.Type.Equals("NpcRaid", StringComparison.OrdinalIgnoreCase) ||
                         evt.Type.Equals("Raid", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasBaseRaid = true;
                        break;
                    }
                }
                if (!hasBaseRaid)
                {
                    config.Events.Add(new EventDefinition
                    {
                        Type = "BaseRaid",
                        Enabled = true,
                        MinIntervalMinutes = 180,
                        MaxIntervalMinutes = 420,
                        Weight = 0.55f,
                        AnnounceMinutesBefore = 4
                    });
                    changed = true;
                    Puts("[Events] Soft-added BaseRaid event definition (new late-wipe feature)");
                }

                bool hasSubway = false, hasMine = false;
                foreach (var evt in config.Events)
                {
                    if (evt?.Type == null) continue;
                    if (evt.Type.Equals("SubwayPatrol", StringComparison.OrdinalIgnoreCase) ||
                        evt.Type.Equals("Subway", StringComparison.OrdinalIgnoreCase) ||
                        evt.Type.Equals("Metro", StringComparison.OrdinalIgnoreCase))
                        hasSubway = true;
                    if (evt.Type.Equals("MineGuard", StringComparison.OrdinalIgnoreCase) ||
                        evt.Type.Equals("Mine", StringComparison.OrdinalIgnoreCase) ||
                        evt.Type.Equals("CaveGuard", StringComparison.OrdinalIgnoreCase))
                        hasMine = true;
                }
                if (!hasSubway)
                {
                    config.Events.Add(new EventDefinition
                    {
                        Type = "SubwayPatrol",
                        Enabled = true,
                        MinIntervalMinutes = 150,
                        MaxIntervalMinutes = 310,
                        Weight = 0.85f,
                        AnnounceMinutesBefore = 3
                    });
                    changed = true;
                    Puts("[Events] Soft-added SubwayPatrol event definition (dungeon metro patrol)");
                }
                if (!hasMine)
                {
                    config.Events.Add(new EventDefinition
                    {
                        Type = "MineGuard",
                        Enabled = true,
                        MinIntervalMinutes = 100,
                        MaxIntervalMinutes = 220,
                        Weight = 1.15f,
                        AnnounceMinutesBefore = 3
                    });
                    changed = true;
                    Puts("[Events] Soft-added MineGuard event definition (cave/mine protected crate)");
                }

                // Rebalance existing MineGuard if still on legacy rare schedule (prevents 8–12h droughts)
                if (config.Events != null)
                {
                    foreach (var evt in config.Events)
                    {
                        if (evt == null || evt.Type == null) continue;
                        if (!evt.Type.Equals("MineGuard", StringComparison.OrdinalIgnoreCase) &&
                            !evt.Type.Equals("Mine", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (evt.MinIntervalMinutes > 150f || evt.Weight < 0.9f)
                        {
                            evt.MinIntervalMinutes = Mathf.Min(evt.MinIntervalMinutes, 100f);
                            if (evt.MaxIntervalMinutes > 240f || evt.MaxIntervalMinutes < evt.MinIntervalMinutes + 30f)
                                evt.MaxIntervalMinutes = 220f;
                            if (evt.Weight < 1.0f) evt.Weight = 1.15f;
                            Puts($"[Events] Rebalanced MineGuard schedule -> min={evt.MinIntervalMinutes:F0} max={evt.MaxIntervalMinutes:F0} weight={evt.Weight:F2}");
                        }
                        break;
                    }
                }

                bool hasRail = config.Events != null && config.Events.Exists(e =>
                    e != null && e.Type != null &&
                    (e.Type.Equals("RailPatrol", StringComparison.OrdinalIgnoreCase) ||
                     e.Type.Equals("TrainPatrol", StringComparison.OrdinalIgnoreCase)));
                if (!hasRail)
                {
                    if (config.Events == null) config.Events = new List<EventDefinition>();
                    config.Events.Add(new EventDefinition
                    {
                        Type = "RailPatrol",
                        Enabled = true,
                        MinIntervalMinutes = 160,
                        MaxIntervalMinutes = 330,
                        Weight = 0.7f,
                        AnnounceMinutesBefore = 3
                    });
                    Puts("[Events] Soft-added RailPatrol event definition (surface rail network)");
                }

                bool hasAirCh47 = config.Events != null && config.Events.Exists(e =>
                    e != null && e.Type != null &&
                    (e.Type.Equals("AirfieldChinook", StringComparison.OrdinalIgnoreCase) ||
                     e.Type.Equals("AirfieldCh47", StringComparison.OrdinalIgnoreCase) ||
                     e.Type.Equals("StealChinook", StringComparison.OrdinalIgnoreCase)));
                if (!hasAirCh47)
                {
                    if (config.Events == null) config.Events = new List<EventDefinition>();
                    config.Events.Add(new EventDefinition
                    {
                        Type = "AirfieldChinook",
                        Enabled = true,
                        MinIntervalMinutes = 240,
                        MaxIntervalMinutes = 540,
                        Weight = 0.55f,
                        AnnounceMinutesBefore = 4
                    });
                    changed = true;
                    Puts("[Events] Soft-added AirfieldChinook event definition (stealable CH47)");
                }
            }

            // Hot air balloons: despawn-only — never event-spawn (force off even if config Enabled)
            if (config.Events != null)
            {
                foreach (var evt in config.Events)
                {
                    if (evt == null || string.IsNullOrEmpty(evt.Type)) continue;
                    string t = evt.Type.Trim();
                    if (t.Equals("HotAirBalloon", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("Balloon", StringComparison.OrdinalIgnoreCase))
                    {
                        if (evt.Enabled)
                        {
                            evt.Enabled = false;
                            changed = true;
                            Puts("[Events] HotAirBalloon forced Disabled (despawn-only policy)");
                        }
                    }
                }
            }

            if (config.F15Flyby == null)
            {
                config.F15Flyby = new F15Config();
                changed = true;
            }

            if (config.HeliPatrol == null)
            {
                config.HeliPatrol = new HeliPatrolConfig();
                changed = true;
            }

            if (config.ChinookPatrol == null)
            {
                config.ChinookPatrol = new ChinookPatrolConfig();
                changed = true;
            }
            else
            {
                // 1.13.17 defaults were too short (12m / 5 hops / 50s + kill 18s after drop)
                if (config.ChinookPatrol.PatrolLifetimeMinutes <= 12.01f)
                {
                    config.ChinookPatrol.PatrolLifetimeMinutes = 18f;
                    changed = true;
                }
                if (config.ChinookPatrol.MaxMonumentWaypoints <= 5)
                {
                    config.ChinookPatrol.MaxMonumentWaypoints = 8;
                    changed = true;
                }
                if (config.ChinookPatrol.WaypointIntervalSeconds <= 50.01f)
                {
                    config.ChinookPatrol.WaypointIntervalSeconds = 75f;
                    changed = true;
                }
                if (config.ChinookPatrol.MinTourMinutesBeforeDrop < 1f)
                {
                    config.ChinookPatrol.MinTourMinutesBeforeDrop = 8f;
                    changed = true;
                }
            }

            if (config.BradleyPatrol == null)
            {
                config.BradleyPatrol = new BradleyPatrolConfig();
                changed = true;
            }

            if (config.AttackHeliSpawn == null)
            {
                config.AttackHeliSpawn = new AttackHeliSpawnConfig();
                changed = true;
            }

            if (config.VehicleLimits == null)
            {
                config.VehicleLimits = new VehicleLimitsConfig();
                changed = true;
            }

            if (config.CargoShipLifecycle == null)
            {
                config.CargoShipLifecycle = new CargoShipLifecycleConfig();
                changed = true;
            }

            if (config.BaseRaid == null)
            {
                config.BaseRaid = new BaseRaidConfig();
                changed = true;
            }

            if (config.Modules == null)
            {
                config.Modules = new ModuleConfig();
                changed = true;
            }

            if (config.AirfieldChinook == null)
            {
                config.AirfieldChinook = new AirfieldChinookConfig();
                changed = true;
            }

            if (changed)
                SaveConfig();
        }

        protected override void SaveConfig()
        {
            if (config != null)
                Config.WriteObject(config, true);
        }

        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // Permissions / general
                ["NoPermission"] = "You do not have permission to use this command.",
                ["PermissionDenied"] = "Permission denied",
                ["WorldRequired"] = "LiveStatsWorld is required and not loaded.",
                ["ConfigReloaded"] = "Config reloaded and event system restarted.",
                ["ConfigReloadedDisabled"] = "Config reloaded. Events are disabled in config (Enabled = false).",
                ["StatusStopped"] = "Event system is stopped (Enabled = false in config). Use /event reload after enabling.",
                ["UnknownEvent"] = "Unknown event type. See /event for full list.",
                ["UsageEvent"] = "Usage: /event <supplydrop|cargo|heli|chinook|bradley|attackheli|tugboat|balloon|heavies|hackablecrate|sub|scrapheli|mini|ambush|tunnel|subway|mine|peacekeepers|raid|airfield|f15|status|reload>",
                ["ForcedEvent"] = "Forced event: {0}",
                ["StatusEnabled"] = "Events Enabled: {0}",
                ["StatusMode"] = "Mode: {0}",
                ["StatusPlayers"] = "Online players: {0} (min {1})",
                ["StatusNoneActive"] = "No active event entities found.",
                ["StatusActiveHeader"] = "Active: {0} entities ({1} types)",
                ["StatusActiveLine"] = "  {0} ({1}): <color=#55ff55>{2}</color>",
                ["F15SpawnedCoords"] = "F-15E spawned at {0} {1} {2}",
                ["F15Started"] = "F-15E flyby started",
                ["F15DefaultPath"] = "F-15E flyby started (default path)",

                // Build-up announcements
                ["BuildUpIntel"] = "<color=#ffaa00>INTEL: {0} activity expected in approximately {1} minutes.</color>",
                ["BuildUpWarning"] = "<color=#ff6600>WARNING: {0} inbound in {1} minutes!</color>",
                ["BuildUpImminent"] = "<color=#ff3333><size=16>{0} IS APPROACHING</size></color>",

                // Spawn / event messages
                ["SpawnSupplyDrop"] = "<color=#55ff55>Supply Drop has been deployed!</color>",
                ["SpawnCargo"] = "<color=#55aaff>Cargo Ship has entered the grid!</color>",
                ["SpawnCargoExists"] = "<color=#55aaff>Cargo Ship is already in the grid.</color>",
                ["SpawnPatrolHeli"] = "<color=#ff5555>⚠️ Patrol Helicopter is in the air!</color>",
                ["SpawnPatrolHeliExists"] = "<color=#ff5555>Patrol Helicopter is already in the air.</color>",
                ["SpawnChinook"] = "<color=#c0c0c0>Chinook inbound!</color>",
                ["SpawnChinookTour"] = "<color=#c0c0c0>Chinook inbound!</color>",
                ["SpawnBradley"] = "<color=#ff3333>Bradley APC has been deployed!</color>",
                ["SpawnBradleyExists"] = "<color=#ff3333>Bradley APC is already deployed.</color>",
                ["SpawnAttackHeli"] = "<color=#ff5555>Attack Helicopter is in the air!</color>",
                ["SpawnAttackHeliExists"] = "<color=#ff5555>Attack Helicopter is already available.</color>",
                ["SpawnAttackHeliNear"] = "<color=#ff5555>Attack Helicopter dropped near {0}!</color>",
                ["SpawnVehicleLimit"] = "<color=#aaaaaa>{0} limit reached ({1} already in the world).</color>",
                ["SpawnTugboat"] = "<color=#55aaff>Tugboat spotted offshore!</color>",
                ["SpawnBalloon"] = "<color=#ffcc55>Hot Air Balloon drifting in!</color>",
                ["SpawnHeavies"] = "<color=#ff3333>Heavy Scientist squad has deployed!</color>",
                ["SpawnHackable"] = "<color=#c0c0c0>Locked crate on the ground near </color><color=#ffaa00>{0}</color>",
                ["SpawnHackableNear"] = "<color=#c0c0c0>Chinook dropped a locked crate near </color><color=#ffaa00>{0}</color>",
                ["SpawnSub"] = "<color=#5599ff>Submarine detected near the coast!</color>",
                ["SpawnScrapHeli"] = "<color=#aaaaaa>Scrap Transport Heli available!</color>",
                ["SpawnMini"] = "<color=#99ff99>Minicopter dropped into the world!</color>",
                ["SpawnF15"] = "<color=#ff0000><size=18>F-15 FLYBY COMPLETE</size></color>",
                ["SpawnAmbush"] = "<color=#ff5555>Road Ambush</color><color=#c0c0c0> — scientists are sealing a route!</color>",
                ["SpawnTunnel"] = "<color=#ff5555>Military Tunnel squad has deployed!</color>",
                ["SpawnSubway"] = "<color=#ff5555>Subway / metro patrol</color><color=#c0c0c0> is active in the tunnels near </color><color=#ffaa00>{0}</color>",
                ["SpawnMineGuard"] = "<color=#ff3333><size=16>⚠ MINE GUARD</size></color><color=#c0c0c0> High-value crate under protection near </color><color=#ffaa00>{0}</color>",
                ["SpawnRailPatrol"] = "<color=#ffaa00>Rail patrol</color><color=#c0c0c0> is moving along the tracks near </color><color=#ffaa00>{0}</color>",
                ["SpawnPeacekeepers"] = "<color=#88cc88>Peacekeeper patrol is on the move.</color>",
                ["SpawnBaseRaid"] = "<color=#ff3333><size=16>⚠ MILITARY RAID SQUAD</size></color><color=#c0c0c0> is moving on a player base!</color>",
                ["SpawnAirfieldChinook"] = "<color=#ffaa00><size=16>⚠ HIGH VALUE AIRCRAFT</size></color><color=#c0c0c0> deployed at the </color><color=#ffaa00>Airfield</color><color=#c0c0c0> — guarded Chinook, steal it if you can.</color>",
                ["SpawnGeneric"] = "Event spawned: {0}",
                ["EventCancelled"] = "<color=#ffaa00>⚠ {0} was cancelled</color><color=#c0c0c0> (area busy or limit reached).</color>",
                ["EventCancelledNoTarget"] = "<color=#ffaa00>⚠ {0} was cancelled</color><color=#c0c0c0> — no valid tool cupboard (online player with TC, or offline 3–5 day base).</color>",

                // F-15 special sequence
                ["F15Incoming"] = "<color=#ff3333><size=18>⚠️ INCOMING F-15E FLYBY</size></color>",
                ["F15HeadsDown"] = "<color=#ffaa00>Military jet reported inbound - keep your heads down!</color>",
                ["F15MilitaryAlert"] = "<color=#ff0000><size=18>⚠️ MILITARY ALERT </size></color>",
                ["F15Radar"] = "<color=#ffaa00>Unidentified high-speed aircraft detected on long-range radar...</color>",
                ["F15Confirm"] = "<color=#ff6600>Confirmation: F-15 signature inbound. ETA 90 seconds.</color>",

                // Display names (used by build-up)
                ["NameSupplyDrop"] = "Supply Drop",
                ["NameCargo"] = "Cargo Ship",
                ["NamePatrolHeli"] = "Patrol Helicopter",
                ["NameChinook"] = "Chinook",
                ["NameBradley"] = "Bradley APC",
                ["NameAttackHeli"] = "Attack Helicopter",
                ["NameTugboat"] = "Tugboat",
                ["NameBalloon"] = "Hot Air Balloon",
                ["NameHeavies"] = "Heavy Scientists",
                ["NameHackable"] = "Hackable Crate",
                ["NameSub"] = "Submarine",
                ["NameScrapHeli"] = "Scrap Transport Heli",
                ["NameMini"] = "Minicopter",
                ["NameF15"] = "F-15 Flyby",
                ["NameAmbush"] = "Road Ambush",
                ["NameTunnel"] = "Tunnel Squad",
                ["NameSubway"] = "Subway Patrol",
                ["NameMineGuard"] = "Mine Guard",
                ["NameRailPatrol"] = "Rail Patrol",
                ["NamePeacekeepers"] = "Peacekeeper Patrol",
                ["NameBaseRaid"] = "Base Raid Squad",
                ["NameAirfieldChinook"] = "Airfield Chinook"
            }, this);
        }

        #endregion
    }
}
