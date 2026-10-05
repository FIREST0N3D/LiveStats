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
    [Info("LiveStatsEvents", "FiREST0N3D", "1.13.183")]
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
            public bool IsMain;
        }
        private readonly List<CachedRoad> _roadCache = new List<CachedRoad>();
        private readonly List<Vector3> _roadXings = new List<Vector3>();
        private readonly List<RoadEdge> _roadEdges = new List<RoadEdge>();
        private class RoadEdge
        {
            public int A;
            public int B;
            public float Len;
            public float Width;
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
        private int _maintPhase;

        // After a Chinook spawns, watch for locked crates it drops and announce their grid
        private float _chinookCrateWatchUntil = -1f;
        private string _lastHackableGrid = "";
        private float _suppressHackableAnnounceUntil = -1f;
        private readonly HashSet<ulong> _announcedCrateIds = new HashSet<ulong>();
        private readonly HashSet<int> _throughMonRoads = new HashSet<int>();
        private readonly Dictionary<string, float[]> _razorLocalXZ = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private bool _razorSeeded;
        private const string MonumentScanFile = "LiveStatsMonuments";
        private const string MonumentScanVersion = "monuments-v3.3";
        private const string BradleyPathFile = "LiveStatsBradleyPaths";
        private const string BradleyPathVersion = "paths-v3";
        private readonly List<MonumentScanEntry> _monumentScan = new List<MonumentScanEntry>();
        private bool _monumentScanReady;

        private class MonumentScanEntry
        {
            public int Id;
            public string Name = "";
            public string Prefab = "";
            public string Grid = "";
            public float X;
            public float Z;
            public float Yaw;
            public float Radius;
            public string Kind = "roadside";
            public string Family = "";
            public bool BradleySkip;
            public List<int> ThroughRoads = new List<int>();
            public List<int> SpurRoads = new List<int>();
            public int Blocks;
            public List<float> GateX = new List<float>();
            public List<float> GateZ = new List<float>();
            public List<float> LaneX = new List<float>();
            public List<float> LaneZ = new List<float>();
        }

        private class MonumentScanFileData
        {
            public string Map = "";
            public string Version = "";
            public List<MonumentScanEntry> Monuments = new List<MonumentScanEntry>();
        }

        private class BradleyPathPrefab
        {
            public string Prefab = "";
            public string Family = "";
            public string Kind = "";
            public bool Tour;
            public float Spacing = 14f;
            public List<float> LocalX = new List<float>();
            public List<float> LocalZ = new List<float>();
            public int Cells;
            public float Length;
            public string Via = "";
        }

        private class BradleyPathFileData
        {
            public string Map = "";
            public string Version = "";
            public Dictionary<string, BradleyPathPrefab> Prefabs = new Dictionary<string, BradleyPathPrefab>(StringComparer.OrdinalIgnoreCase);
        }

        private BradleyPathFileData _bradleyPaths;

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
            Puts("LiveStatsEvents v1.13.183 — announce/cargo/lights/index share a 5s clock");
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
        /// <summary>
        /// 5s clock. Announcements every fire. Cargo maintenance every 240s,
        /// night lights every 480s, live-index sanity every 1200s.
        /// Event roll is not on this clock.
        /// </summary>
        private void MaintenanceClock()
        {
            ProcessAnnouncements();
            _maintPhase++;
            if (_maintPhase % 48 == 0)
                RunMaintenanceTick();
            if (_maintPhase % 96 == 0)
                UpdateAllSpawnedNightLights();
            if (_maintPhase % 240 == 0)
                RebuildLiveIndex(force: false);
        }

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

            // Event roll stays on its own 45–180s timer. Announcements stay at 5s
            // (a counter on the event tick would delay "inbound in 1 minute").
            // Cargo 240s, night lights 480s, and live-index 1200s are phases of that 5s clock.
            float tick = Mathf.Clamp(config.CheckIntervalSeconds, 45f, 180f);
            _eventTimer = timer.Every(tick, EventTick);
            _cargoMonitorTimer?.Destroy();
            _cargoMonitorTimer = null;
            _liveIndexTimer?.Destroy();
            _liveIndexTimer = null;
            _nightLightsTimer?.Destroy();
            _nightLightsTimer = null;
            _maintPhase = 0;
            _announceTimer?.Destroy();
            _announceTimer = timer.Every(5f, MaintenanceClock);

            // Vehicle despawn owned exclusively by LiveStatsEventsVehicles (core inline removed)
            bool vehiclesModule = _registeredModules.Contains(ModuleVehicles) || LiveStatsEventsVehicles != null;
            if (vehiclesModule)
                Puts("[Events] Vehicle despawn owned by LiveStatsEventsVehicles module");
            else
                Puts("[Events] WARNING: LiveStatsEventsVehicles not loaded — vehicle events/despawn inactive");

            if (config.CargoShipLifecycle == null || config.CargoShipLifecycle.Enabled)
                Puts($"[Events] Cargo lifecycle monitor on (egress after {config.CargoShipLifecycle?.MaxEventMinutes ?? 55:F0}m, kill after egress + {config.CargoShipLifecycle?.ForceKillMinutesAfterEgress ?? 15:F0}m)");

            RebuildLiveIndex(force: true);
            ProtectExistingHackableCrates();
            Puts($"[Events] Live index: incremental spawn/kill + sanity rebuild every {LiveIndexMaxAge:F0}s (phased on the 5s clock)");

            CleanupStuckChinooks();

            _lastNightLightsIsNight = null;

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
                    int seated = CountPlayerChinooks();
                    if (seated >= maxCh47)
                    {
                        DebugLog($"AirfieldChinook blocked — seated players {seated} >= MaxInWorld {maxCh47}");
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
                            DebugLog($"CargoShip ingress hold at {PositionToGrid(cargo.transform.position)} (no path snap)");
                            var cargoHold = cargo;
                            var holdPos = pos;
                            timer.Once(18f, () =>
                            {
                                try
                                {
                                    if (cargoHold == null || cargoHold.IsDestroyed) return;
                                    typeof(CargoShip).GetMethod("RefreshCurrentPosition", flags)?.Invoke(cargoHold, null);
                                    DebugLog($"CargoShip joined ocean path at {PositionToGrid(cargoHold.transform.position)}");
                                }
                                catch { }
                            });
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
                if (r < halfMap * 0.88f || r > halfMap * 1.25f) continue;
                float water = 0f;
                try { if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p); } catch { }
                if (water < -20f || water > 80f) water = 0f;
                p.y = water + 1.5f;
                usable.Add(p);
            }
            if (usable.Count < 3) usable = pool;
            Vector3 best = usable[0];
            float bestR = -1f;
            for (int i = 0; i < usable.Count; i++)
            {
                float rr = Mathf.Sqrt(usable[i].x * usable[i].x + usable[i].z * usable[i].z);
                if (rr > bestR) { bestR = rr; best = usable[i]; }
            }
            Vector3 dir = new Vector3(best.x, 0f, best.z);
            if (dir.sqrMagnitude < 1f) dir = Vector3.forward;
            dir.Normalize();
            // Past the terrain square so the hull is not sitting on a Deep Sea map tile.
            float ax = Mathf.Abs(dir.x), az = Mathf.Abs(dir.z);
            float denom = Mathf.Max(ax, az);
            if (denom < 0.05f) denom = 1f;
            float margin = 640f;
            result = dir * ((halfMap + margin) / denom);
            if (Mathf.Abs(result.x) < halfMap + 400f && Mathf.Abs(result.z) < halfMap + 400f)
            {
                if (Mathf.Abs(result.x) >= Mathf.Abs(result.z))
                    result.x = Mathf.Sign(result.x == 0f ? 1f : result.x) * (halfMap + margin);
                else
                    result.z = Mathf.Sign(result.z == 0f ? 1f : result.z) * (halfMap + margin);
            }
            result.y = best.y > 0.5f ? best.y : 1.5f;
            DebugLog($"Cargo ocean PATH node {PositionToGrid(best)} -> offmap {PositionToGrid(result)} ({result.x:F0},{result.z:F0}) half={halfMap:F0} pool={usable.Count}/{pool.Count}");
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
                            // Ingress: stay off-map and fly toward interest. Do not snap inland.
                            ForceHeliLandInterest(heliAI, interest, forceMove: true);
                            DebugLog($"PatrolHeli ingress @{d:F1}s {PositionToGrid(p)} -> {PositionToGrid(interest)}");
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
        private Vector3 GetMapRimEntry(Vector3 toward, float cruiseAlt)
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }
            Vector3 flat = new Vector3(toward.x, 0f, toward.z);
            if (flat.sqrMagnitude < 1f)
                flat = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
            flat.Normalize();
            Vector3 rim = flat * (half * 0.96f);
            rim.y = Mathf.Max(GetHeliCruiseAltitude(rim, cruiseAlt), 130f);
            return rim;
        }

        /// <summary>
        /// True off-map approach on the SAME side as the interest, then fly/sail in.
        /// Inland 0.70–0.82 spawn made cargo/heli/chinook pop over the playable grid.
        /// </summary>
        private Vector3 GetMapEdgeApproach(Vector3 toward, float cruiseAlt)
        {
            float half = 2000f;
            try { half = TerrainMeta.Size.x * 0.5f; } catch { }

            Vector3 flat = new Vector3(toward.x, 0f, toward.z);
            if (flat.sqrMagnitude < 1f)
                flat = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));
            flat.Normalize();

            // Square edge, not radius. 1.22*half still lands in a corner (A21).
            float margin = UnityEngine.Random.Range(520f, 780f);
            float ax = Mathf.Abs(flat.x), az = Mathf.Abs(flat.z);
            float denom = Mathf.Max(ax, az);
            if (denom < 0.05f) denom = 1f;
            Vector3 edge = flat * ((half + margin) / denom);
            Vector3 perp = new Vector3(-flat.z, 0f, flat.x);
            edge += perp * UnityEngine.Random.Range(-160f, 160f);
            // Keep the exit axis outside the square after the lateral nudge.
            if (Mathf.Abs(edge.x) < half + 280f && Mathf.Abs(edge.z) < half + 280f)
            {
                if (Mathf.Abs(edge.x) >= Mathf.Abs(edge.z))
                    edge.x = Mathf.Sign(edge.x == 0f ? 1f : edge.x) * (half + margin);
                else
                    edge.z = Mathf.Sign(edge.z == 0f ? 1f : edge.z) * (half + margin);
            }
            edge.y = Mathf.Max(GetHeliCruiseAltitude(edge, cruiseAlt), 140f);
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

                    if (off && age < 80f)
                    {
                        // Still crossing the rim — do not teleport over the grid.
                    }
                    else if (off && (now - lastRecovery) >= 25f)
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
            pos.y = Mathf.Max(pos.y, cruise, 140f);

            DebugLog($"Chinook vanilla inbound at {PositionToGrid(pos)} ({pos.x:F0}, {pos.y:F0}, {pos.z:F0}) -> interest {PositionToGrid(flyToward)}");
            Vector3 chinookIngress = pos;

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
                // Off-map hull is the ingress — do not recenter over the grid.

                // Only respawn if the hull is truly in the void / under-map
                Vector3 spawnedAt = ch47.transform.position;
                if (spawnedAt.y < -30f || spawnedAt.y > 800f)
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
                var holdBird = ch47;
                var holdPos = chinookIngress;
                holdPos.y = Mathf.Max(holdPos.y, 140f);
                foreach (float d in new[] { 0.2f, 0.8f, 2.0f, 4.0f })
                {
                    float delay = d;
                    timer.Once(delay, () =>
                    {
                        try
                        {
                            if (holdBird == null || holdBird.IsDestroyed) return;
                            Vector3 now = holdBird.transform.position;
                            float halfH = 2000f;
                            try { halfH = TerrainMeta.Size.x * 0.5f; } catch { }
                            bool inside = Mathf.Abs(now.x) < halfH + 200f && Mathf.Abs(now.z) < halfH + 200f;
                            if (inside)
                            {
                                holdBird.transform.position = holdPos;
                                try { holdBird.TransformChanged(); } catch { }
                                try { holdBird.SetMoveTarget(flyToward); } catch { }
                                DebugLog($"Chinook ingress-hold @{delay:F1}s back to {PositionToGrid(holdPos)}");
                            }
                        }
                        catch { }
                    });
                }
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

            float halfSafe = 1600f;
            try { halfSafe = TerrainMeta.Size.x * 0.5f * 0.62f; } catch { }
            flyToward.x = Mathf.Clamp(flyToward.x, -halfSafe, halfSafe);
            flyToward.z = Mathf.Clamp(flyToward.z, -halfSafe, halfSafe);

            Vector3 pos = GetMapEdgeApproach(flyToward, cruiseY);
            pos.y = cruiseY;
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
                if ((hardVoid || pastWorld) && age >= 90f)
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

        /// <summary>
        /// Meters that should feel the same on a 2k or 4.5k procedural map.
        /// 3607 (this box) is the reference.
        /// </summary>
        private float ScaleOnMap(float metersAt3607)
        {
            float half = GetWorldHalf();
            if (half < 50f) return metersAt3607;
            return metersAt3607 * (half / 1803.5f);
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
                ScanBradleyMonumentCorridors();
                return;
            }
            BuildRoadCache();
        }

        /// <summary>
        /// Cache every road once at boot. TerrainMeta.Path.Roads -> Path.Points.
        /// Drops water nodes, near-duplicates, and steep/cliff segments.
        /// Result is also stored in static fields so soft reloads skip the rebuild.
        /// </summary>
        private void MarkMainRoads()
        {
            try
            {
                var mains = TerrainMeta.Path?.MainRoads;
                if (mains == null || mains.Count == 0) return;
                var anchors = new List<Vector3>(mains.Count * 2);
                foreach (var road in mains)
                {
                    if (road?.Path?.Points == null || road.Path.Points.Length < 2) continue;
                    anchors.Add(road.Path.Points[0]);
                    anchors.Add(road.Path.Points[road.Path.Points.Length - 1]);
                    int mid = road.Path.Points.Length / 2;
                    anchors.Add(road.Path.Points[mid]);
                }
                for (int i = 0; i < _roadCache.Count; i++)
                {
                    var r = _roadCache[i];
                    if (r.Points == null || r.Points.Count < 4) continue;
                    Vector3 a = r.Points[0], b = r.Points[r.Points.Count / 2], c = r.Points[r.Points.Count - 1];
                    for (int k = 0; k < anchors.Count; k++)
                    {
                        Vector3 p = anchors[k];
                        float d0 = (a.x - p.x) * (a.x - p.x) + (a.z - p.z) * (a.z - p.z);
                        float d1 = (b.x - p.x) * (b.x - p.x) + (b.z - p.z) * (b.z - p.z);
                        float d2 = (c.x - p.x) * (c.x - p.x) + (c.z - p.z) * (c.z - p.z);
                        if (d0 < 40f * 40f || d1 < 40f * 40f || d2 < 40f * 40f)
                        {
                            r.IsMain = true;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

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

                MarkMainRoads();
                _roadCache.Sort((a, b) =>
                {
                    if (a.IsMain != b.IsMain) return a.IsMain ? -1 : 1;
                    int c = b.Width.CompareTo(a.Width);
                    return c != 0 ? c : b.Length.CompareTo(a.Length);
                });

                Puts($"[Events] Road cache: {_roadCache.Count} roads " +
                     $"(best width={(_roadCache.Count > 0 ? _roadCache[0].Width : 0):F1}, " +
                     $"pts={(_roadCache.Count > 0 ? _roadCache[0].Points.Count : 0)}, " +
                     $"slope-filtered={slopeDropped}, height-filtered={heightDropped}, raw={rawTotal}, " +
                     $"map={GetMapIdentity()}, half={GetWorldHalf():F0})");

                // Persist for soft reloads (oxide.reload) on the same map
                _staticRoadCache = new List<CachedRoad>(_roadCache);
                _staticRoadMapIdentity = GetMapIdentity();
                ScanBradleyMonumentCorridors();
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
                // Monument / Building stay allowed — harbor, gas, supermarket roads must remain in the path.
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

        private bool IsMonumentRoadPoint(Vector3 p)
        {
            try
            {
                if (TerrainMeta.TopologyMap == null) return false;
                int topo = TerrainMeta.TopologyMap.GetTopology(p);
                return (topo & (int)TerrainTopology.Enum.Monument) != 0;
            }
            catch { return false; }
        }

        private static bool BradleyLotName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("harbor", StringComparison.Ordinal) >= 0
                || n.IndexOf("harbour", StringComparison.Ordinal) >= 0
                || n.IndexOf("fishing", StringComparison.Ordinal) >= 0
                || n.IndexOf("lighthouse", StringComparison.Ordinal) >= 0
                || n.IndexOf("junkyard", StringComparison.Ordinal) >= 0
                || n.IndexOf("powerplant", StringComparison.Ordinal) >= 0
                || n.IndexOf("power_plant", StringComparison.Ordinal) >= 0
                || n.IndexOf("sewer", StringComparison.Ordinal) >= 0
                || n.IndexOf("satellite", StringComparison.Ordinal) >= 0
                || n.IndexOf("compound", StringComparison.Ordinal) >= 0
                || n.IndexOf("warehouse", StringComparison.Ordinal) >= 0
                || n.IndexOf("trainyard", StringComparison.Ordinal) >= 0
                || n.IndexOf("train_yard", StringComparison.Ordinal) >= 0
                || n.IndexOf("water_treatment", StringComparison.Ordinal) >= 0
                || n.IndexOf("watertreatment", StringComparison.Ordinal) >= 0
                || n.IndexOf("launch_site", StringComparison.Ordinal) >= 0
                || n.IndexOf("airfield", StringComparison.Ordinal) >= 0
                || n.IndexOf("military_tunnel", StringComparison.Ordinal) >= 0
                || n.IndexOf("militarytunnel", StringComparison.Ordinal) >= 0
                || n.IndexOf("excavator", StringComparison.Ordinal) >= 0
                || n.IndexOf("dome", StringComparison.Ordinal) >= 0
                || n.IndexOf("sphere_tank", StringComparison.Ordinal) >= 0
                || n.IndexOf("radtown", StringComparison.Ordinal) >= 0
                || n.IndexOf("military_base", StringComparison.Ordinal) >= 0
                || n.IndexOf("arctic", StringComparison.Ordinal) >= 0
                || n.IndexOf("launchsite", StringComparison.Ordinal) >= 0
                || n.IndexOf("powerplant", StringComparison.Ordinal) >= 0
                || n.IndexOf("bandit", StringComparison.Ordinal) >= 0
                || n.IndexOf("outpost", StringComparison.Ordinal) >= 0
                || n.IndexOf("oilrig", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Facepunch autospawn prefab family (Monument Finder list + 2025/26 adds).
        /// skip = not a road tour. roadside = drive past. coastal = harbor/ferry/fishing/lighthouse
        /// (through only if a named road ENTERS AND LEAVES). yard = inner lot no-go.
        /// </summary>
        private static string MonumentPrefabFamily(string n)
        {
            if (string.IsNullOrEmpty(n)) return "skip";
            n = n.ToLowerInvariant();
            if (n.IndexOf("power_sub", StringComparison.Ordinal) >= 0 || n.IndexOf("powersub", StringComparison.Ordinal) >= 0)
                return "skip";
            if (n.IndexOf("cave_", StringComparison.Ordinal) >= 0 || n.IndexOf("/cave", StringComparison.Ordinal) >= 0)
                return "skip";
            if (n.IndexOf("ice_lake", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("swamp_", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("underwater_lab", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("oilrig", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("mining_quarry", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("water_well", StringComparison.Ordinal) >= 0 || n.IndexOf("waterwell", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("jungle", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("deepsea", StringComparison.Ordinal) >= 0) return "skip";
            if (n.IndexOf("island", StringComparison.Ordinal) >= 0) return "skip";

            if (n.IndexOf("gas_station", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("supermarket", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("bus_stop", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("busstop", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("stables", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("warehouse", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("radtown_small", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("entrance_bunker", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("apartment", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("outpost", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("compound", StringComparison.Ordinal) >= 0) return "roadside";
            if (n.IndexOf("junkyard", StringComparison.Ordinal) >= 0) return "yard";

            if (n.IndexOf("harbor_", StringComparison.Ordinal) >= 0 || n.IndexOf("harbour", StringComparison.Ordinal) >= 0)
                return "coastal";
            if (n.IndexOf("ferry_terminal", StringComparison.Ordinal) >= 0) return "coastal";
            if (n.IndexOf("fishing_village", StringComparison.Ordinal) >= 0) return "coastal";
            if (n.IndexOf("lighthouse", StringComparison.Ordinal) >= 0) return "coastal";

            if (n.IndexOf("airfield", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("launch_site", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("powerplant", StringComparison.Ordinal) >= 0 || n.IndexOf("power_plant", StringComparison.Ordinal) >= 0)
                return "yard";
            if (n.IndexOf("trainyard", StringComparison.Ordinal) >= 0 || n.IndexOf("train_yard", StringComparison.Ordinal) >= 0)
                return "yard";
            if (n.IndexOf("water_treatment", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("excavator", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("satellite", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("sphere_tank", StringComparison.Ordinal) >= 0 || n.IndexOf("dome", StringComparison.Ordinal) >= 0)
                return "yard";
            if (n.IndexOf("sewer", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("junkyard", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("military_tunnel", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("arctic_research", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("desert_military", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("missile_silo", StringComparison.Ordinal) >= 0 || n.IndexOf("nuclear_missile", StringComparison.Ordinal) >= 0)
                return "yard";
            if (n.IndexOf("compound", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("bandit", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("outpost", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("apartment", StringComparison.Ordinal) >= 0) return "yard";
            if (n.IndexOf("radtown", StringComparison.Ordinal) >= 0) return "yard";
            return "";
        }

        private static bool HarborLotName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("harbor", StringComparison.Ordinal) >= 0
                || n.IndexOf("harbour", StringComparison.Ordinal) >= 0
                || n.IndexOf("ferry_terminal", StringComparison.Ordinal) >= 0
                || n.IndexOf("fishing_village", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Dock-only coastal lots — never a Bradley through-tour.</summary>
        private static bool HarborPierOnlyName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("ferry_terminal", StringComparison.Ordinal) >= 0
                || n.IndexOf("fishing_village", StringComparison.Ordinal) >= 0;
        }

        private bool IsHarborLot(Vector3 p)
        {
            if (_monumentScanReady)
            {
                for (int i = 0; i < _monumentScan.Count; i++)
                {
                    var m = _monumentScan[i];
                    if (m == null || !HarborLotName(m.Name)) continue;
                    if (BradleyCanTourMonument(m)) continue;
                    if (string.Equals(m.Kind, "through", StringComparison.Ordinal)) continue;
                    float dx = p.x - m.X, dz = p.z - m.Z;
                    float rad = Mathf.Max(m.Radius, 140f);
                    if (dx * dx + dz * dz <= rad * rad) return true;
                }
            }
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons == null) return false;
                for (int i = 0; i < mons.Count; i++)
                {
                    var mon = mons[i];
                    if (mon == null) continue;
                    if (!HarborLotName((mon.name ?? "").ToLowerInvariant())) continue;
                    Vector3 mp = mon.transform.position;
                    float dx = mp.x - p.x, dz = mp.z - p.z;
                    if (dx * dx + dz * dz <= 140f * 140f) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool BradleySkipLotName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            return n.IndexOf("gas_station", StringComparison.Ordinal) >= 0
                || n.IndexOf("supermarket", StringComparison.Ordinal) >= 0
                || n.IndexOf("bus_stop", StringComparison.Ordinal) >= 0
                || n.IndexOf("busstop", StringComparison.Ordinal) >= 0
                || n.IndexOf("lighthouse", StringComparison.Ordinal) >= 0
                || n.IndexOf("warehouse", StringComparison.Ordinal) >= 0
                || n.IndexOf("water_well", StringComparison.Ordinal) >= 0
                || n.IndexOf("waterwell", StringComparison.Ordinal) >= 0
                || n.IndexOf("entrance_bunker", StringComparison.Ordinal) >= 0
                || n.IndexOf("power_sub", StringComparison.Ordinal) >= 0
                || n.IndexOf("powersub", StringComparison.Ordinal) >= 0
                || n.IndexOf("transformer", StringComparison.Ordinal) >= 0
                || n.IndexOf("swimming_pool", StringComparison.Ordinal) >= 0
                || n.IndexOf("swamp", StringComparison.Ordinal) >= 0
                || n.IndexOf("jungle", StringComparison.Ordinal) >= 0
                || n.IndexOf("stables", StringComparison.Ordinal) >= 0
                || n.IndexOf("cave", StringComparison.Ordinal) >= 0
                || n.IndexOf("underwater", StringComparison.Ordinal) >= 0
                || n.IndexOf("island", StringComparison.Ordinal) >= 0
                || n.IndexOf("apartment", StringComparison.Ordinal) >= 0
                || n.IndexOf("outpost", StringComparison.Ordinal) >= 0
                || n.IndexOf("compound", StringComparison.Ordinal) >= 0;
        }

        private string ClosestMonumentLabel(Vector3 p, out float dist)
        {
            dist = float.MaxValue;
            string label = "";
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons == null) return label;
                for (int i = 0; i < mons.Count; i++)
                {
                    var mon = mons[i];
                    if (mon == null) continue;
                    Vector3 mp = mon.transform.position;
                    float dx = mp.x - p.x, dz = mp.z - p.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d >= dist) continue;
                    dist = d;
                    string n = mon.name ?? "";
                    int slash = n.LastIndexOf('/');
                    if (slash >= 0 && slash + 1 < n.Length) n = n.Substring(slash + 1);
                    label = n.Replace(".prefab", "");
                }
            }
            catch { }
            return label;
        }

        /// <summary>
        /// Courtyard monuments the APC cannot path. Skirt the painted road outside
        /// the lot — do not snap through I12 / power / sewer / harbor yards.
        /// </summary>
        private bool IsBradleyNoGoLot(Vector3 p)
        {
            // Harbor / fishing pier is always a lot — never a through-tour.
            if (IsHarborLot(p)) return true;
            // Painted through-road across a monument is a tour, not a trap.
            if (OnMonumentThroughRoad(p)) return false;
            if (_monumentScanReady)
            {
                var hit = FindMonumentScanAt(p);
                if (hit == null) return false;
                if (hit.Kind == "skip" || hit.Kind == "roadside") return false;
                if (hit.Kind == "through")
                {
                    float dx = p.x - hit.X, dz = p.z - hit.Z;
                    float inner = hit.Radius * 0.55f;
                    return dx * dx + dz * dz <= inner * inner;
                }
                return true; // courtyard interior
            }
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons == null) return false;
                for (int i = 0; i < mons.Count; i++)
                {
                    var mon = mons[i];
                    if (mon == null) continue;
                    string n = (mon.name ?? "").ToLowerInvariant();
                    if (BradleySkipLotName(n)) continue;
                    float rad = 80f;
                    try
                    {
                        var b = mon.Bounds;
                        float ext = Mathf.Max(b.extents.x, b.extents.z);
                        if (!BradleyLotName(n) && ext < 40f) continue;
                        rad = Mathf.Clamp(ext + 16f, 55f, 160f);
                    }
                    catch
                    {
                        if (!BradleyLotName(n)) continue;
                        rad = 90f;
                    }
                    Vector3 mp = mon.transform.position;
                    float dx = mp.x - p.x, dz = mp.z - p.z;
                    if (dx * dx + dz * dz <= rad * rad) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// monuments-v1: one boot pass. Persist oxide/data/LiveStatsMonuments.json
        /// keyed by map identity. Through-road = enter AND leave bounds.
        /// Courtyard = large lot with no through-road (or only a spur).
        /// Bradley/NPC drive through-roads; inner lot / barricades stay no-go.
        /// </summary>
        private void ScanBradleyMonumentCorridors()
        {
            BuildMonumentScan();
        }

        private bool TryLoadMonumentScan()
        {
            _monumentScan.Clear();
            _monumentScanReady = false;
            _throughMonRoads.Clear();
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<MonumentScanFileData>(MonumentScanFile);
                if (data == null || data.Monuments == null || data.Monuments.Count == 0) return false;
                if (!string.Equals(data.Version, MonumentScanVersion, StringComparison.Ordinal)) return false;
                if (!string.Equals(data.Map, GetMapIdentity(), StringComparison.Ordinal)) return false;
                _monumentScan.AddRange(data.Monuments);
                for (int i = 0; i < _monumentScan.Count; i++)
                {
                    var m = _monumentScan[i];
                    if (m?.ThroughRoads == null) continue;
                    for (int r = 0; r < m.ThroughRoads.Count; r++)
                        _throughMonRoads.Add(m.ThroughRoads[r]);
                }
                int withLane = 0;
                for (int i = 0; i < _monumentScan.Count; i++)
                {
                    var m = _monumentScan[i];
                    if (m?.LaneX != null && m.LaneX.Count >= 4) withLane++;
                }
                if (withLane == 0)
                {
                    Puts($"[Events] {MonumentScanVersion} restored but lanes empty — rebuilding");
                    return false;
                }
                _monumentScanReady = true;
                Puts($"[Events] {data.Version} restored ({_monumentScan.Count} sites, lanes={withLane}, through-roads={_throughMonRoads.Count}, map={data.Map})");
                EnsureBradleyPathCatalog();
                ApplyPrefabTemplatesToScan();
                return true;
            }
            catch (Exception ex)
            {
                Puts("[Events] monuments-v1 load: " + ex.Message);
                return false;
            }
        }

        private void SaveMonumentScan()
        {
            try
            {
                var data = new MonumentScanFileData
                {
                    Map = GetMapIdentity(),
                    Version = MonumentScanVersion,
                    Monuments = _monumentScan
                };
                Interface.Oxide.DataFileSystem.WriteObject(MonumentScanFile, data);
            }
            catch (Exception ex)
            {
                Puts("[Events] monuments-v1 save: " + ex.Message);
            }
        }

        private static string ShortMonumentName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string n = raw;
            int slash = n.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < n.Length) n = n.Substring(slash + 1);
            return n.Replace(".prefab", "");
        }

        private void BuildMonumentScan()
        {
            if (TryLoadMonumentScan()) return;
            _monumentScan.Clear();
            _throughMonRoads.Clear();
            _monumentScanReady = false;
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons == null || mons.Count == 0)
                {
                    Puts("[Events] monuments-v1: no TerrainMeta.Path.Monuments");
                    return;
                }

                var blockHits = new List<Vector3>();
                try
                {
                    foreach (var bn in BaseNetworkable.serverEntities)
                    {
                        var ent = bn as BaseEntity;
                        if (ent == null || ent.IsDestroyed) continue;
                        string sn = (ent.ShortPrefabName ?? "").ToLowerInvariant();
                        if (sn.IndexOf("barricade", StringComparison.Ordinal) < 0
                            && sn.IndexOf("barrier", StringComparison.Ordinal) < 0
                            && sn.IndexOf("icewall", StringComparison.Ordinal) < 0
                            && sn.IndexOf("road_barrier", StringComparison.Ordinal) < 0
                            && sn.IndexOf("gates.external", StringComparison.Ordinal) < 0)
                            continue;
                        blockHits.Add(ent.transform.position);
                        if (blockHits.Count >= 400) break;
                    }
                }
                catch { }

                int id = 0;
                for (int i = 0; i < mons.Count; i++)
                {
                    var mon = mons[i];
                    if (mon == null || mon.transform == null) continue;
                    string raw = mon.name ?? "";
                    string n = raw.ToLowerInvariant();
                    Vector3 mp = mon.transform.position;
                    float rad = 70f;
                    try
                    {
                        var b = mon.Bounds;
                        rad = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z) + 16f, 40f, 220f);
                    }
                    catch { }
                    if (MonumentPrefabFamily(n) == "yard")
                        rad = Mathf.Max(rad, 130f);

                    float yaw = 0f;
                    try { yaw = mon.transform.eulerAngles.y; } catch { }
                    var entry = new MonumentScanEntry
                    {
                        Id = id++,
                        Name = ShortMonumentName(raw),
                        Prefab = ShortMonumentName(raw),
                        Grid = PositionToGrid(mp),
                        X = mp.x,
                        Z = mp.z,
                        Yaw = yaw,
                        Radius = rad
                    };

                    string family = MonumentPrefabFamily(n);
                    entry.Family = family;
                    // harbor_1 / harbor_2 can tour the LAND-SIDE road. Ferry + fishing stay skip.
                    entry.BradleySkip = BradleySkipLotName(n)
                        || family == "skip"
                        || HarborPierOnlyName(n)
                        || n.IndexOf("radtown_small", StringComparison.Ordinal) >= 0
                        || n.IndexOf("oilrig", StringComparison.Ordinal) >= 0
                        || n.IndexOf("deepsea", StringComparison.Ordinal) >= 0
                        || n.IndexOf("underwater", StringComparison.Ordinal) >= 0;
                    bool coastal = family == "coastal" || HarborLotName(n);

                    float radSq = rad * rad;
                    float clip = rad + 36f;
                    float clipSq = clip * clip;
                    for (int r = 0; r < _roadCache.Count; r++)
                    {
                        var pts = _roadCache[r].Points;
                        if (pts == null || pts.Count < 6) continue;
                        if (_roadCache[r].Width < 4.4f) continue;
                        int firstHit = -1, lastHit = -1;
                        bool coreHit = false;
                        for (int p = 0; p < pts.Count; p++)
                        {
                            float dx = pts[p].x - mp.x, dz = pts[p].z - mp.z;
                            float d2 = dx * dx + dz * dz;
                            if (d2 <= radSq) coreHit = true;
                            if (d2 <= clipSq)
                            {
                                if (firstHit < 0) firstHit = p;
                                lastHit = p;
                            }
                        }
                        if (firstHit < 0) continue;
                        bool enter = firstHit > 1;
                        bool leave = lastHit >= 0 && lastHit < pts.Count - 2;
                        if (enter && leave)
                        {
                            if (!entry.ThroughRoads.Contains(r)) entry.ThroughRoads.Add(r);
                            _throughMonRoads.Add(r);
                            entry.GateX.Add(pts[firstHit].x);
                            entry.GateZ.Add(pts[firstHit].z);
                            entry.GateX.Add(pts[lastHit].x);
                            entry.GateZ.Add(pts[lastHit].z);
                        }
                        else if (coreHit || enter || leave)
                        {
                            if (!entry.SpurRoads.Contains(r)) entry.SpurRoads.Add(r);
                            if (firstHit >= 0)
                            {
                                entry.GateX.Add(pts[firstHit].x);
                                entry.GateZ.Add(pts[firstHit].z);
                            }
                            if (leave && lastHit >= 0 && lastHit != firstHit)
                            {
                                entry.GateX.Add(pts[lastHit].x);
                                entry.GateZ.Add(pts[lastHit].z);
                            }
                        }
                    }
                    int blocks = 0;
                    for (int b = 0; b < blockHits.Count; b++)
                    {
                        float dx = blockHits[b].x - mp.x, dz = blockHits[b].z - mp.z;
                        if (dx * dx + dz * dz <= radSq) blocks++;
                    }
                    entry.Blocks = blocks;

                    bool twoGate = entry.GateX != null && entry.GateX.Count >= 2 && entry.GateZ != null && entry.GateZ.Count >= 2;
                    float gateSpan = 0f;
                    if (twoGate)
                    {
                        float gx = entry.GateX[0] - entry.GateX[entry.GateX.Count - 1];
                        float gz = entry.GateZ[0] - entry.GateZ[entry.GateZ.Count - 1];
                        gateSpan = Mathf.Sqrt(gx * gx + gz * gz);
                    }

                    if (family == "roadside" || (entry.BradleySkip && family != "yard" && family != "coastal"))
                        entry.Kind = family == "skip" ? "skip" : "roadside";
                    else if (entry.ThroughRoads.Count > 0)
                        entry.Kind = "through";
                    else if (coastal || family == "yard" || BradleyLotName(n) || rad >= 70f)
                        entry.Kind = "courtyard";
                    else
                        entry.Kind = "roadside";
                    if (entry.BradleySkip && entry.Kind == "courtyard")
                        entry.Kind = "roadside";

                    BuildMonumentLane(entry);
                    BakeMonumentSkeleton(entry);

                    _monumentScan.Add(entry);
                }

                _monumentScanReady = true;
                int thru = 0, yard = 0, skip = 0, road = 0, bskip = 0;
                for (int i = 0; i < _monumentScan.Count; i++)
                {
                    var e = _monumentScan[i];
                    if (e == null) continue;
                    if (e.BradleySkip) bskip++;
                    if (e.Kind == "through") thru++;
                    else if (e.Kind == "courtyard") yard++;
                    else if (e.Kind == "skip") skip++;
                    else if (e.Kind == "roadside") road++;
                }
                Puts($"[Events] {MonumentScanVersion} built sites={_monumentScan.Count} through={thru} courtyard={yard} roadside={road} skip={skip} bradleySkip={bskip} through-roads={_throughMonRoads.Count} map={GetMapIdentity()}");
                for (int i = 0; i < _monumentScan.Count; i++)
                {
                    var e = _monumentScan[i];
                    if (e == null) continue;
                    int tc = e.ThroughRoads != null ? e.ThroughRoads.Count : 0;
                    int sc = e.SpurRoads != null ? e.SpurRoads.Count : 0;
                    int lc = e.LaneX != null ? e.LaneX.Count : 0;
                    int gc = e.GateX != null ? e.GateX.Count : 0;
                    string flag = e.BradleySkip ? "bradley=skip" : "bradley=tour";
                    Puts($"[Events] {MonumentScanVersion} {e.Grid} {e.Name} kind={e.Kind} family={e.Family} {flag} thru={tc} spur={sc} gates={gc} lane={lc} r={e.Radius:F0} blocks={e.Blocks}");
                }
                SaveMonumentScan();
                EnsureBradleyPathCatalog();
                ApplyPrefabTemplatesToScan();
                SaveBradleyPaths();
            }
            catch (Exception ex)
            {
                Puts("[Events] monuments-v1 build: " + ex.Message);
                _monumentScanReady = false;
            }
        }

        private void BuildMonumentLane(MonumentScanEntry entry)
        {
            if (entry == null) return;
            entry.LaneX = entry.LaneX ?? new List<float>();
            entry.LaneZ = entry.LaneZ ?? new List<float>();
            entry.LaneX.Clear();
            entry.LaneZ.Clear();
            if (entry.Kind == "skip") return;

            Vector3 origin = new Vector3(entry.X, 0f, entry.Z);
            float rad = entry.Radius;
            float radSq = rad * rad;

            List<Vector3> best = null;
            float bestScore = -1f;
            var roadIds = new List<int>();
            if (entry.ThroughRoads != null)
            {
                for (int t = 0; t < entry.ThroughRoads.Count; t++)
                    if (!roadIds.Contains(entry.ThroughRoads[t])) roadIds.Add(entry.ThroughRoads[t]);
            }
            if (entry.SpurRoads != null)
            {
                for (int t = 0; t < entry.SpurRoads.Count; t++)
                    if (!roadIds.Contains(entry.SpurRoads[t])) roadIds.Add(entry.SpurRoads[t]);
            }
            // Any Path.Road that actually crosses the ring — not just "through".
            for (int r = 0; r < _roadCache.Count; r++)
            {
                if (roadIds.Contains(r)) continue;
                var pts = _roadCache[r].Points;
                if (pts == null || pts.Count < 4 || _roadCache[r].Width < 4.4f) continue;
                for (int p = 0; p < pts.Count; p++)
                {
                    float dx = pts[p].x - origin.x, dz = pts[p].z - origin.z;
                    if (dx * dx + dz * dz <= radSq)
                    {
                        roadIds.Add(r);
                        break;
                    }
                }
            }
            for (int t = 0; t < roadIds.Count; t++)
            {
                int rid = roadIds[t];
                if (rid < 0 || rid >= _roadCache.Count) continue;
                var pts = _roadCache[rid].Points;
                if (pts == null) continue;
                var clip = new List<Vector3>();
                float keepSq = (rad * 2.1f) * (rad * 2.1f);
                for (int p = 0; p < pts.Count; p++)
                {
                    float dx = pts[p].x - origin.x, dz = pts[p].z - origin.z;
                    if (dx * dx + dz * dz <= keepSq)
                        clip.Add(pts[p]);
                }
                if (clip.Count < 3 && pts.Count >= 3)
                {
                    int nearest = 0;
                    float bestD = float.MaxValue;
                    for (int p = 0; p < pts.Count; p++)
                    {
                        float dx = pts[p].x - origin.x, dz = pts[p].z - origin.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestD) { bestD = d2; nearest = p; }
                    }
                    int lo = Math.Max(0, nearest - 8);
                    int hi = Math.Min(pts.Count - 1, nearest + 8);
                    clip.Clear();
                    for (int p = lo; p <= hi; p++) clip.Add(pts[p]);
                }
                if (clip.Count < 3) continue;
                float span = Vector3.Distance(clip[0], clip[clip.Count - 1]);
                float score = span + clip.Count * 4f;
                if (entry.GateX != null && entry.GateX.Count >= 2 && entry.GateZ != null && entry.GateZ.Count >= 2)
                {
                    var g0 = new Vector3(entry.GateX[0], 0f, entry.GateZ[0]);
                    var g1 = new Vector3(entry.GateX[entry.GateX.Count - 1], 0f, entry.GateZ[entry.GateZ.Count - 1]);
                    float a0 = Vector3.Distance(clip[0], g0) + Vector3.Distance(clip[clip.Count - 1], g1);
                    float a1 = Vector3.Distance(clip[0], g1) + Vector3.Distance(clip[clip.Count - 1], g0);
                    float gateFit = Math.Min(a0, a1);
                    score += Math.Max(0f, 220f - gateFit);
                    if (span >= 90f) score += 80f;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = clip;
                }
            }

            if (best == null && entry.GateX != null && entry.GateX.Count >= 2 && entry.GateZ != null && entry.GateZ.Count >= 2)
            {
                var a = new Vector3(entry.GateX[0], 0f, entry.GateZ[0]);
                var b = new Vector3(entry.GateX[entry.GateX.Count - 1], 0f, entry.GateZ[entry.GateZ.Count - 1]);
                float span = Vector3.Distance(a, b);
                if (span >= 40f)
                {
                    best = new List<Vector3>();
                    int steps = Mathf.Clamp(Mathf.RoundToInt(span / 22f), 3, 16);
                    for (int s = 0; s <= steps; s++)
                    {
                        var p = Vector3.Lerp(a, b, s / (float)steps);
                        p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : 0f;
                        best.Add(p);
                    }
                }
            }

            if (best == null) return;
            for (int i = 0; i < best.Count; i++)
            {
                entry.LaneX.Add(best[i].x);
                entry.LaneZ.Add(best[i].z);
            }
        }

        private List<Vector3> ClipThroughRoadLane(MonumentScanEntry e)
        {
            var lane = new List<Vector3>();
            if (e == null || e.ThroughRoads == null || e.ThroughRoads.Count == 0) return lane;
            if (_roadCache == null || _roadCache.Count == 0) return lane;
            float keep = (e.Radius + 36f) * (e.Radius + 36f);
            List<Vector3> best = null;
            float bestSpan = 0f;
            for (int r = 0; r < e.ThroughRoads.Count; r++)
            {
                int rid = e.ThroughRoads[r];
                if (rid < 0 || rid >= _roadCache.Count) continue;
                var pts = _roadCache[rid].Points;
                if (pts == null || pts.Count < 8) continue;
                var clip = new List<Vector3>();
                for (int i = 0; i < pts.Count; i++)
                {
                    float dx = pts[i].x - e.X, dz = pts[i].z - e.Z;
                    if (dx * dx + dz * dz <= keep)
                        clip.Add(pts[i]);
                    else if (clip.Count >= 8)
                        break;
                    else if (clip.Count > 0)
                        clip.Clear();
                }
                if (clip.Count < 8) continue;
                float sx = clip[clip.Count - 1].x - clip[0].x;
                float sz = clip[clip.Count - 1].z - clip[0].z;
                float span = Mathf.Sqrt(sx * sx + sz * sz);
                if (span > bestSpan)
                {
                    bestSpan = span;
                    best = clip;
                }
            }
            if (best != null && bestSpan >= 80f)
                lane.AddRange(best);
            return lane;
        }


        private void SeedRazorMonumentLoops()
        {
            if (_razorSeeded) return;
            _razorSeeded = true;
            _razorLocalXZ.Clear();
            _razorLocalXZ["sphere_tank"] = new float[] { -44.6739f, -12.5042f, -39.9974f, 11.9221f, -36.5013f, 25.6112f, -24.593f, 34.7088f, -8.0997f, 45.7054f, 27.2346f, 55.8961f, 35.8948f, 54.4029f, 47.99f, 46.9288f, 50.9379f, 27.8135f, 41.9841f, 0.4928f, 30.0028f, -24.8955f, 13.5103f, -37.4542f, -29.664f, -40.0153f, -38.8212f, -34.256f, -43.9296f, -16.2887f };
            _razorLocalXZ["airfield_1"] = new float[] { -134.809f, -96.6487f, -132.645f, -85.7724f, -79.2341f, -84.0717f, -69.1614f, -72.1806f, -56.2533f, -62.3448f, -55.5956f, -44.6467f, -136.157f, -43.6402f, -112.031f, -43.6269f, -79.5491f, -43.806f, -80.4429f, -25.9524f, -111.916f, 0.6875f, -112.414f, 16.7153f, -91.7073f, 49.4918f, -113.054f, 16.1625f, -111.895f, -6.3126f, -134.455f, -11.3642f, -105.024f, -9.0365f, -45.8041f, -9.4762f, -28.4471f, -0.9524f, -29.7935f, 13.9385f, -6.8983f, 13.8315f, 1.8461f, 5.9658f, 7.5173f, 6.0202f, 37.9468f, 15.395f, 46.6009f, 16.0801f, 70.7774f, -10.0084f, 109.315f, -10.1113f, 137.109f, -12.1277f, 144.241f, -32.8632f, 112.457f, -43.9773f, 105.445f, -74.4724f, 86.5849f, -81.8528f, 66.8394f, -97.8916f, 34.037f, -99.5851f, 29.9134f, -72.9995f, 17.278f, -69.9363f, -0.6341f, -56.7683f, -33.2386f, -55.4344f, -57.7682f, -56.2654f, -76.6747f, -15.8266f, -33.9512f, -14.8613f, -24.2061f, -51.7436f, 7.4062f, -51.3809f, 61.8753f, -43.8552f, 88.2946f, -42.7828f, 90.6615f, 8.793f, 92.7296f, 21.5847f, 92.3238f, -9.2029f, 13.2239f, -10.1776f, -25.279f, -10.4813f, -69.5417f, -10.7361f, -82.8822f, -48.3826f, -84.4519f, -82.8863f, -132.377f, -85.8742f };
            _razorLocalXZ["harbor_1"] = new float[] { 100.546f, -21.7406f, 57.4827f, -21.7406f, 57.3444f, -55.9303f, 57.5958f, -21.7406f, 30.8955f, -21.7406f, 25.9937f, -24.617f, 24.4832f, -31.0493f, 18.7431f, -36.6939f, -12.9141f, -36.6408f, -45.3514f, -36.7605f, -54.7292f, -26.2215f, -51.8347f, -25.4547f, -51.8051f, 8.3647f, -51.3946f, -25.6509f, -63.313f, -36.6249f, -98.159f, -36.7487f, -103.263f, -40.4077f, -104.392f, -43.8071f, -105.683f, -52.1713f, -104.888f, -59.1651f, -99.5379f, -66.0376f, -60.4455f, -66.0376f, -38.6742f, -66.0376f, -33.3896f, -69.9757f, -29.3028f, -71.2384f, -16.8634f, -75.5062f, -11.872f, -76.1217f, 0.8109f, -75.1856f, 11.2345f, -71.6347f, 22.0154f, -64.141f, 29.3522f, -54.9685f, 34.424f, -44.3777f, 36.4792f, -33.4201f, 36.7437f, -21.7406f, 45.6758f, -21.7406f, 45.6758f, 11.5046f, 45.8461f, -21.7406f, 91.7711f, -21.7406f };
            _razorLocalXZ["harbor_2"] = new float[] { 22.6917f, -84.9832f, -34.1382f, -85.5831f, -82.8178f, -85.2745f, -83.2573f, -53.538f, -81.728f, -28.7895f, -15.8536f, -25.4362f, 6.6483f, -23.3717f, 41.0658f, -22.7482f, 40.1911f, 14.5554f, -30.9287f, 16.4043f, -67.093f, 16.7506f, -64.9412f, 35.4569f, -64.2664f, 91.8329f, -26.8558f, 99.9408f, -24.6042f, 71.5147f, 45.1959f, 69.4271f, -24.4145f, 71.3003f, -27.0754f, 95.9049f, -65.0782f, 93.4022f, -64.135f, 50.4864f, -64.4117f, 19.0653f, -66.65f, -24.1463f, -80.1478f, -27.1263f, -83.3122f, -51.1976f, -82.7666f, -84.801f, -1.8208f, -85.7778f };
            _razorLocalXZ["junkyard_1"] = new float[] { 31.6427f, 55.5016f, 30.1172f, 43.2185f, 25.5816f, 28.5134f, 35.2907f, 13.1193f, 31.2733f, 3.8099f, 5.1713f, -8.9497f, -12.4945f, -8.7882f, -21.686f, 1.293f, -35.5006f, 5.0632f, -48.9927f, 8.2906f, -70.8534f, 7.6986f, -74.0362f, -8.443f, -68.3481f, 9.7762f, -66.9337f, 27.5104f, -69.5865f, 38.8019f, -67.1333f, 27.3404f, -68.1916f, 11.5916f, -48.5619f, 8.6025f, -35.6937f, 5.3056f, -21.6746f, 2.9377f, 11.812f, 17.1711f, 24.8064f, 22.3881f, 37.5806f, 14.2117f, 43.4317f, 16.004f, 49.62f, 19.5105f, 58.417f, 9.5516f, 65.7243f, -2.8875f, 70.6566f, -26.5739f, 60.3374f, -39.1839f, 49.285f, -53.4582f, 34.6746f, -61.5134f, 50.0529f, -52.8052f, 65.3894f, -34.0593f, 72.406f, -26.2476f, 65.2051f, -3.0028f, 57.4449f, 9.4185f, 51.2203f, 17.7417f, 37.176f, 15.0586f, 26.8684f, 25.442f, 30.4267f, 43.9321f };
            _razorLocalXZ["trainyard_1"] = new float[] { 63.5246f, -2.3617f, 56.4243f, -2.6101f, 49.0622f, -2.7309f, 36.0476f, -14.614f, 22.716f, -14.6589f, 12.716f, -14.6589f, 12.716f, -22.75f, 12.716f, 18.6589f, 12.716f, -14.6589f, 22.1708f, -18.2999f, 22.071f, -74.7744f, -6.6125f, -74.2602f, -26.755f, -80.7569f, -38.5178f, -70.4946f, -38.7187f, -50.2889f, -22.0289f, -44.6155f, -37.1253f, -59.1627f, -41.5046f, -77.9461f, -90.5709f, -82.469f, -92.1006f, -78.9673f, -95.5848f, -61.5951f, -95.7291f, 25.4842f, -80.5431f, 30.8824f, -70.3922f, 30.9128f, -67.6463f, 23.0383f, -42.1062f, 22.7769f, -44.3335f, -12.3053f, -44.875f, -12.7851f, -38.44f, -14.6032f, -26.3947f, -14.4189f, -22.0062f, -13.2894f, -7.9719f, -13.1325f, -7.8039f, -11.7887f, -7.6659f, 18.289f, -7.6399f, 19.7966f, -7.5714f, -11.4036f, -10.8643f, -12.5277f, -11.1973f, -21.7159f, -13.1763f, -24.5886f, -11.753f, -40.0844f, -7.9497f, -45.0342f, -8.406f, -74.5499f, 22.071f, -74.7744f, 76.471f, -74.7744f, 80.3421f, -62.4771f, 80.3421f, -32.4771f, 75.4822f, -30.4771f, 75.4822f, -16.5914f, 75.4822f, -10.5914f, 70.4822f, -6.5914f, 65.5246f, -2.3617f };
            _razorLocalXZ["powerplant_1"] = new float[] { 18.3474f, 67.7239f, 18.3474f, 40.8174f, 67.4247f, 40.7106f, 4.392f, 40.7106f, 1.0467f, 0.0888f, 1.0467f, -40.6115f, -3.4869f, -46.2247f, 1.4433f, -58.2209f, 4.8886f, -61.3712f, 4.8186f, -66.1768f, 3.2686f, -69.4104f, 3.1889f, -93.8335f, 3.2686f, -69.4104f, 4.8186f, -66.1768f, 4.8886f, -61.3712f, 2.3069f, -58.0664f, 0.1142f, -51.9898f, -5.9127f, -44.5731f, -63.4544f, -44.5731f, -67.6941f, -48.2145f, -96.1015f, -48.3726f, -101.497f, -55.3947f, -101.909f, -87.3858f, -93.1647f, -91.7413f, -86.168f, -96.1535f, -70.8447f, -95.9745f, -67.8832f, -91.992f, -63.5f, -75.577f, -62.5f, -71.0111f, -62.5f, -32.4117f, -69.4082f, -24.5123f, -70.3849f, -21.5473f, -67.9369f, -16f, -65f, -13.4733f, -65f, -12.639f, -65f, 12.2136f, -65f, 63.877f, -61.6533f, 79.358f, -56.8955f, 86.6915f, -44.5865f, 92.6369f, -32.7438f, 99.9867f, -17.3018f, 105f, -13.5f, 105.5f, 30.5f, 105.5f, 50.9375f, 97.9088f, 66.1421f, 80.7127f, 70.75f, 63.8761f, 70.75f, 39.545f, 70.75f, -23.256f, 70.75f, 41.5318f, 18.3474f, 40.8174f };
            _razorLocalXZ["water_treatment_plant_1"] = new float[] { 94.2949f, 31.3285f, 59.6445f, 30.3015f, 56.2498f, -10.1183f, 48.0216f, -5.36f, 29.8862f, -5.4011f, 16.17f, -14.5343f, 15.5126f, -98.5449f, 14.1354f, -103.693f, 14.2544f, -125.046f, 17.4894f, -127.687f, 17.5812f, -135.969f, 16.0782f, -139.031f, 16.1378f, -149.436f, 15.8953f, -140.267f, 16.9246f, -138.745f, 17.262f, -125.614f, 12.8036f, -124.452f, -15.1641f, -124.753f, -23.3595f, -133.724f, -23.7182f, -137.287f, -24.1202f, -148.115f, -27.1412f, -153.928f, -28.7329f, -155.973f, -28.645f, -150.316f, -50.8209f, -150.588f, -28.7848f, -151.173f, -24.818f, -148.788f, -24.1909f, -130.521f, -28.5883f, -126.455f, -29.1837f, -124.908f, -28.955f, -112.06f, -23.6384f, -108.233f, -21.1923f, -106.922f, -21.426f, -99.8725f, -28.3303f, -91.6504f, -27.0522f, -51.7102f, -30.7065f, -44.5484f, -27.3839f, -12.3673f, -28.0514f, -31.8467f, -28.7509f, -38.1953f, -23.9973f, -40.6225f, -13.9552f, -40.1601f, -8.6511f, -42.8547f, -5.0758f, -42.557f, 2.9489f, -52.3519f, 14.8706f, -52.5641f, 15.8345f, -80.82f, -15.9486f, -80.82f, -22.0366f, -84.7204f, -23.7125f, -85.6363f, -23.9451f, -99.0342f, -19.6812f, -105.516f, -16.0274f, -109.726f, -8.1952f, -111.424f, -7.1045f, -113.89f, -6.8422f, -124.787f, 15.8345f, -124.787f, 15.8345f, -51.2806f, 18.238f, -11.4787f, 28.9972f, -5.8038f, 36.8947f, -5.3177f, 45.9107f, -5.4784f, 55.6798f, -9.3465f, 60.1711f, 31.095f };
            DebugLog($"Bradley Razor loops seeded={_razorLocalXZ.Count}");
        }

        private List<Vector3> GetRazorLaneWorld(MonumentScanEntry e)
        {
            var lane = new List<Vector3>();
            if (e == null) return lane;
            SeedRazorMonumentLoops();
            string key = ((e.Prefab ?? "") + " " + (e.Name ?? "")).ToLowerInvariant();
            float[] xz = null;
            foreach (var kv in _razorLocalXZ)
            {
                if (key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    xz = kv.Value;
                    break;
                }
            }
            if (xz == null || xz.Length < 16) return lane;
            for (int i = 0; i + 1 < xz.Length; i += 2)
            {
                float wx, wz;
                LocalToWorld(e, xz[i], xz[i + 1], out wx, out wz);
                var p = new Vector3(wx, 0f, wz);
                try { p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : e.X * 0f; }
                catch { }
                lane.Add(p);
            }
            return lane;
        }

        private List<Vector3> GetMonumentDriveLane(MonumentScanEntry e)
        {
            // Painted road through the lot beats a Razor/template ring (radtown via=tpl went around).
            var thru = ClipThroughRoadLane(e);
            if (LaneHasTwoGates(thru)) return thru;
            var razor = GetRazorLaneWorld(e);
            if (razor.Count >= 8) return razor;
            var tpl = GetPrefabTemplateLane(e);
            if (tpl.Count >= 8) return tpl;
            return GetMonumentLane(e);
        }

        private static int ClosestLaneIndex(List<Vector3> lane, Vector3 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < lane.Count; i++)
            {
                float dx = lane[i].x - p.x, dz = lane[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static List<Vector3> WalkLaneArc(List<Vector3> lane, int a, int b)
        {
            var arc = new List<Vector3>();
            if (lane == null || lane.Count == 0) return arc;
            a = Mathf.Clamp(a, 0, lane.Count - 1);
            b = Mathf.Clamp(b, 0, lane.Count - 1);
            if (a == b)
            {
                arc.Add(lane[a]);
                return arc;
            }
            float close = Vector3.Distance(
                new Vector3(lane[0].x, 0f, lane[0].z),
                new Vector3(lane[lane.Count - 1].x, 0f, lane[lane.Count - 1].z));
            bool loop = close < 45f;
            if (!loop)
            {
                if (a <= b)
                {
                    for (int i = a; i <= b; i++) arc.Add(lane[i]);
                }
                else
                {
                    for (int i = a; i >= b; i--) arc.Add(lane[i]);
                }
                return arc;
            }
            var fwd = new List<Vector3>();
            int n = lane.Count;
            int i2 = a;
            for (int k = 0; k <= n; k++)
            {
                fwd.Add(lane[i2]);
                if (i2 == b) break;
                i2 = (i2 + 1) % n;
            }
            var rev = new List<Vector3>();
            i2 = a;
            for (int k = 0; k <= n; k++)
            {
                rev.Add(lane[i2]);
                if (i2 == b) break;
                i2 = (i2 - 1 + n) % n;
            }
            return fwd.Count <= rev.Count ? fwd : rev;
        }

        /// <summary>
        /// Replace highway nodes inside a yard with the Razor/template loop
        /// clipped from the inbound gate to the outbound gate.
        /// </summary>
        private static bool BradleyHazardLotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.IndexOf("power_sub", StringComparison.Ordinal) >= 0
                || n.IndexOf("powersub", StringComparison.Ordinal) >= 0
                || n.IndexOf("water_well", StringComparison.Ordinal) >= 0
                || n.IndexOf("waterwell", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Path.Road often pins a node on the substation pad (N14 power_sub_big_1).
        /// Drop those pads; keep a jump to the next dry highway node.
        /// </summary>
        private int StripBradleyHazardPads(List<Vector3> route)
        {
            if (route == null || route.Count < 10 || !_monumentScanReady) return 0;
            var pads = new List<MonumentScanEntry>();
            for (int i = 0; i < _monumentScan.Count; i++)
            {
                var e = _monumentScan[i];
                if (e != null && BradleyHazardLotName(e.Prefab + " " + e.Name))
                    pads.Add(e);
            }
            if (pads.Count == 0) return 0;
            int removed = 0;
            for (int i = route.Count - 2; i >= 1; i--)
            {
                bool hit = false;
                for (int p = 0; p < pads.Count; p++)
                {
                    float dx = route[i].x - pads[p].X, dz = route[i].z - pads[p].Z;
                    float r = Mathf.Max(pads[p].Radius, 38f) + 8f;
                    if (dx * dx + dz * dz <= r * r) { hit = true; break; }
                }
                if (!hit) continue;
                route.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
                DebugLog($"Bradley strip-pads removed={removed} pts={route.Count}");
            return removed;
        }

        private void AdoptBradleyRoute(ref List<Vector3> route)
        {
            if (route == null || route.Count < 8) return;
            StripBradleyHazardPads(route);
            CutBradleyRouteGaps(route, 140f);
            SpliceBradleyMonumentCorridors(route);
            CutBradleyRouteGaps(route, 140f);
        }

        private static List<Vector3> WalkLaneThrough(List<Vector3> lane, int a, int b, float cx, float cz)
        {
            var fwd = new List<Vector3>();
            var rev = new List<Vector3>();
            if (lane == null || lane.Count == 0) return fwd;
            a = Mathf.Clamp(a, 0, lane.Count - 1);
            b = Mathf.Clamp(b, 0, lane.Count - 1);
            float close = Vector3.Distance(
                new Vector3(lane[0].x, 0f, lane[0].z),
                new Vector3(lane[lane.Count - 1].x, 0f, lane[lane.Count - 1].z));
            bool loop = close < 45f;
            if (!loop)
                return WalkLaneArc(lane, a, b);
            int n = lane.Count;
            int i2 = a;
            for (int k = 0; k <= n; k++)
            {
                fwd.Add(lane[i2]);
                if (i2 == b) break;
                i2 = (i2 + 1) % n;
            }
            i2 = a;
            for (int k = 0; k <= n; k++)
            {
                rev.Add(lane[i2]);
                if (i2 == b) break;
                i2 = (i2 - 1 + n) % n;
            }
            float Mean(List<Vector3> arc)
            {
                if (arc == null || arc.Count == 0) return 99999f;
                float sum = 0f;
                for (int i = 0; i < arc.Count; i++)
                {
                    float dx = arc[i].x - cx, dz = arc[i].z - cz;
                    sum += Mathf.Sqrt(dx * dx + dz * dz);
                }
                return sum / arc.Count;
            }
            if (fwd.Count < 8 && rev.Count >= 8) return rev;
            if (rev.Count < 8 && fwd.Count >= 8) return fwd;
            return Mean(fwd) <= Mean(rev) ? fwd : rev;
        }

        private int SpliceBradleyMonumentCorridors(List<Vector3> chain)
        {
            if (chain == null || chain.Count < 10) return 0;
            if (!_monumentScanReady || _monumentScan == null || _monumentScan.Count == 0) return 0;
            SeedRazorMonumentLoops();
            int spliced = 0;
            for (int m = 0; m < _monumentScan.Count; m++)
            {
                var mon = _monumentScan[m];
                if (mon == null || mon.BradleySkip) continue;
                bool yard = mon.Kind == "through" || mon.Kind == "courtyard"
                            || string.Equals(mon.Family, "yard", StringComparison.OrdinalIgnoreCase);
                if (!yard) continue;
                var lane = GetMonumentDriveLane(mon);
                if (lane == null || lane.Count < 6) continue;
                float r = Mathf.Max(mon.Radius + 28f, 90f);
                float r2 = r * r;
                int first = -1, last = -1;
                for (int i = 0; i < chain.Count; i++)
                {
                    float dx = chain[i].x - mon.X, dz = chain[i].z - mon.Z;
                    if (dx * dx + dz * dz > r2) continue;
                    if (first < 0) first = i;
                    last = i;
                }
                if (first < 0 || last <= first) continue;
                int inIdx = Mathf.Max(0, first - 1);
                int outIdx = Mathf.Min(chain.Count - 1, last + 1);
                int e0 = ClosestLaneIndex(lane, chain[inIdx]);
                int e1 = ClosestLaneIndex(lane, chain[outIdx]);
                if (e0 == e1)
                    e1 = (e0 + lane.Count / 2) % lane.Count;
                var arc = WalkLaneThrough(lane, e0, e1, mon.X, mon.Z);
                float span = 0f;
                if (arc.Count >= 2)
                {
                    float dx = arc[arc.Count - 1].x - arc[0].x;
                    float dz = arc[arc.Count - 1].z - arc[0].z;
                    span = Mathf.Sqrt(dx * dx + dz * dz);
                }
                // 156 trainyard I8→H8 lane=9 was the rim, not the inner road.
                string pref = (mon.Prefab ?? "") + " " + (mon.Name ?? "");
                bool blockedHighway = pref.IndexOf("radtown", StringComparison.OrdinalIgnoreCase) >= 0;
                bool blockedLot = pref.IndexOf("radtown", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("junkyard", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("excavator", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("apartment", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("desert_military", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("missile_silo", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("arctic", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("compound", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("bandit", StringComparison.OrdinalIgnoreCase) >= 0
                    || pref.IndexOf("outpost", StringComparison.OrdinalIgnoreCase) >= 0;
                if (blockedLot && last > first)
                {
                    int drop = chain.Count - first;
                    chain.RemoveRange(first, drop);
                    DebugLog($"Bradley highway-cut {mon.Prefab} ends {PositionToGrid(chain[Mathf.Max(0, chain.Count - 1)])} dropped={drop}");
                    continue;
                }
                DebugLog($"Bradley splice-skip {mon.Prefab} lane={arc.Count} span={span:F0}m (keep highway)");
                continue;
            }
            return spliced;
        }

        private List<Vector3> GetMonumentLane(MonumentScanEntry e)
        {
            var lane = new List<Vector3>();
            if (e == null) return lane;
            var tpl = GetPrefabTemplateLane(e);
            var thru = ClipThroughRoadLane(e);
            if (e.LaneX == null || e.LaneZ == null || e.LaneX.Count < 4)
                BuildMonumentLane(e);
            if (e.LaneX != null && e.LaneZ != null)
            {
                int n = Math.Min(e.LaneX.Count, e.LaneZ.Count);
                for (int i = 0; i < n; i++)
                {
                    var p = new Vector3(e.LaneX[i], 0f, e.LaneZ[i]);
                    p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : 0f;
                    lane.Add(p);
                }
            }
            var best = lane;
            float bestSpan = LaneSpanSq(lane);
            float thruSpan = LaneSpanSq(thru);
            float tplSpan = LaneSpanSq(tpl);
            // Named Path.Road through the lot still wins when it is clearly longer.
            if (thru.Count >= 8 && (thruSpan >= bestSpan + 20f * 20f || thru.Count > best.Count + 4))
            {
                best = thru;
                bestSpan = thruSpan;
            }
            // Prefab-local through-corridor beats a short baker stub.
            // Refuse near-closed rings (tiny first-last span) so a mid-chain
            // highway is never swapped for a yard loop.
            if (tpl.Count >= 8 && tplSpan >= 80f * 80f
                && (tplSpan >= bestSpan + 15f * 15f || (tplSpan >= bestSpan && tpl.Count >= best.Count)))
                return tpl;
            return best;
        }

        private static float LaneSpanSq(List<Vector3> lane)
        {
            if (lane == null || lane.Count < 2) return 0f;
            float dx = lane[lane.Count - 1].x - lane[0].x;
            float dz = lane[lane.Count - 1].z - lane[0].z;
            return dx * dx + dz * dz;
        }

        private List<Vector3> GetPrefabTemplateLane(MonumentScanEntry e)
        {
            var lane = new List<Vector3>();
            if (e == null || _bradleyPaths?.Prefabs == null) return lane;
            string key = string.IsNullOrEmpty(e.Prefab) ? e.Name : e.Prefab;
            BradleyPathPrefab rec;
            if (string.IsNullOrEmpty(key) || !_bradleyPaths.Prefabs.TryGetValue(key, out rec))
                return lane;
            if (rec == null || rec.LocalX == null || rec.LocalZ == null) return lane;
            int n = Math.Min(rec.LocalX.Count, rec.LocalZ.Count);
            if (n < 8 || !rec.Tour) return lane;
            for (int i = 0; i < n; i++)
            {
                float wx, wz;
                LocalToWorld(e, rec.LocalX[i], rec.LocalZ[i], out wx, out wz);
                var p = new Vector3(wx, 0f, wz);
                p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : 0f;
                lane.Add(p);
            }
            return lane;
        }

        private static bool NearMonumentLane(List<Vector3> lane, Vector3 pos, float maxDist)
        {
            if (lane == null || lane.Count == 0) return false;
            float maxSq = maxDist * maxDist;
            float d0x = lane[0].x - pos.x, d0z = lane[0].z - pos.z;
            if (d0x * d0x + d0z * d0z <= maxSq) return true;
            float dex = lane[lane.Count - 1].x - pos.x, dez = lane[lane.Count - 1].z - pos.z;
            if (dex * dex + dez * dez <= maxSq) return true;
            float best = float.MaxValue;
            for (int i = 0; i < lane.Count; i++)
            {
                float dx = lane[i].x - pos.x, dz = lane[i].z - pos.z;
                float sq = dx * dx + dz * dz;
                if (sq < best) best = sq;
            }
            return best <= (22f * 22f);
        }

        private static bool LaneHasTwoGates(List<Vector3> lane)
        {
            if (lane == null || lane.Count < 8) return false;
            float dx = lane[lane.Count - 1].x - lane[0].x;
            float dz = lane[lane.Count - 1].z - lane[0].z;
            return dx * dx + dz * dz >= 90f * 90f;
        }

        private static bool LaneHasTwoGatesWorld(MonumentScanEntry m)
        {
            if (m == null || m.LaneX == null || m.LaneZ == null) return false;
            int n = Math.Min(m.LaneX.Count, m.LaneZ.Count);
            if (n < 8) return false;
            float dx = m.LaneX[n - 1] - m.LaneX[0];
            float dz = m.LaneZ[n - 1] - m.LaneZ[0];
            return dx * dx + dz * dz >= 80f * 80f;
        }

        private static void WorldToLocal(MonumentScanEntry e, float wx, float wz, out float lx, out float lz)
        {
            float dx = wx - e.X, dz = wz - e.Z;
            float yaw = e.Yaw * Mathf.Deg2Rad;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            lx = dx * c + dz * s;
            lz = -dx * s + dz * c;
        }

        private static void LocalToWorld(MonumentScanEntry e, float lx, float lz, out float wx, out float wz)
        {
            float yaw = e.Yaw * Mathf.Deg2Rad;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            wx = e.X + lx * c - lz * s;
            wz = e.Z + lx * s + lz * c;
        }

        /// <summary>
        /// Current TerrainSplat.Enum is Dirt/Snow/Sand/Rock/Grass/Forest/Stones/Gravel.
        /// There is no Asphalt channel — painted roads are topology Road + gravel/stone splat.
        /// </summary>
        private bool CellPaved(Vector3 p)
        {
            try
            {
                if (TerrainMeta.SplatMap == null) return false;
                float gravel = TerrainMeta.SplatMap.GetSplat(p, (int)TerrainSplat.Enum.Gravel);
                float stones = TerrainMeta.SplatMap.GetSplat(p, (int)TerrainSplat.Enum.Stones);
                return gravel >= 0.32f || stones >= 0.45f;
            }
            catch { return false; }
        }

        private int CellTopology(Vector3 p)
        {
            try
            {
                if (TerrainMeta.TopologyMap == null) return 0;
                return TerrainMeta.TopologyMap.GetTopology(p);
            }
            catch { return 0; }
        }

        private bool CellWet(Vector3 p)
        {
            try
            {
                float ground = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : p.y;
                float water = ground;
                if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p);
                if (water - ground > 0.35f) return true;
                float depth = WaterLevel.GetOverallWaterDepth(p, true, true, null);
                return depth > 0.35f;
            }
            catch { return false; }
        }

        /// <summary>
        /// Boot-time skeleton baker. Samples asphalt + Road topology on a 3-4m grid,
        /// thins pads, scores two-gate corridors, writes world LaneX/Z and local
        /// oxide/data/LiveStatsBradleyPaths.json. Path.Road clip remains the fallback.
        /// </summary>
        private bool BakeMonumentSkeleton(MonumentScanEntry entry)
        {
            if (entry == null) return false;
            if (entry.Kind == "skip" || entry.Family == "skip") return false;
            if (entry.BradleySkip && entry.Family != "coastal") return false;
            if (HarborPierOnlyName(entry.Name) || HarborPierOnlyName(entry.Prefab)) return false;

            string key = string.IsNullOrEmpty(entry.Prefab) ? entry.Name : entry.Prefab;
            if (string.IsNullOrEmpty(key)) return false;

            try
            {
                float rad = Mathf.Clamp(entry.Radius, 40f, 230f);
                float step = rad >= 150f ? 4f : 3f;
                int half = Mathf.CeilToInt(rad / step);
                int dim = half * 2 + 1;
                int maxCells = dim * dim;
                if (maxCells > 16000)
                {
                    step = 5f;
                    half = Mathf.CeilToInt(rad / step);
                    dim = half * 2 + 1;
                    maxCells = dim * dim;
                }

                var walk = new bool[maxCells];
                var pad = new bool[maxCells];
                var wx = new float[maxCells];
                var wz = new float[maxCells];
                Vector3 origin = new Vector3(entry.X, 0f, entry.Z);
                float radSq = rad * rad;
                int roadMask = (int)TerrainTopology.Enum.Road | (int)TerrainTopology.Enum.Roadside;
                int railMask = (int)TerrainTopology.Enum.Rail;
                int oceanMask = (int)TerrainTopology.Enum.Ocean | (int)TerrainTopology.Enum.Offshore;
                bool coastal = entry.Family == "coastal" || HarborLotName(entry.Name);
                bool airfield = (entry.Name ?? "").IndexOf("airfield", StringComparison.OrdinalIgnoreCase) >= 0;
                bool plant = (entry.Name ?? "").IndexOf("powerplant", StringComparison.OrdinalIgnoreCase) >= 0
                    || (entry.Name ?? "").IndexOf("power_plant", StringComparison.OrdinalIgnoreCase) >= 0;
                bool junk = (entry.Name ?? "").IndexOf("junkyard", StringComparison.OrdinalIgnoreCase) >= 0;
                bool train = (entry.Name ?? "").IndexOf("trainyard", StringComparison.OrdinalIgnoreCase) >= 0
                    || (entry.Name ?? "").IndexOf("train_yard", StringComparison.OrdinalIgnoreCase) >= 0;
                bool launch = (entry.Name ?? "").IndexOf("launch", StringComparison.OrdinalIgnoreCase) >= 0;

                int accepted = 0;
                for (int iz = 0; iz < dim; iz++)
                {
                    for (int ix = 0; ix < dim; ix++)
                    {
                        int id = ix + iz * dim;
                        float x = entry.X + (ix - half) * step;
                        float z = entry.Z + (iz - half) * step;
                        wx[id] = x;
                        wz[id] = z;
                        float dx = x - origin.x, dz = z - origin.z;
                        if (dx * dx + dz * dz > radSq) continue;
                        var p = new Vector3(x, 0f, z);
                        try { if (TerrainMeta.HeightMap != null) p.y = TerrainMeta.HeightMap.GetHeight(p); } catch { }

                        int topo = CellTopology(p);
                        bool rail = (topo & railMask) != 0;
                        bool roadTopo = (topo & roadMask) != 0;
                        bool asphalt = CellPaved(p);
                        bool nearPath = false;
                        for (int r = 0; r < _roadCache.Count && !nearPath; r++)
                        {
                            var pts = _roadCache[r].Points;
                            if (pts == null || _roadCache[r].Width < 4.0f) continue;
                            for (int pi = 0; pi < pts.Count; pi += 2)
                            {
                                float rx = pts[pi].x - x, rz = pts[pi].z - z;
                                if (rx * rx + rz * rz <= 100f) { nearPath = true; break; }
                            }
                        }
                        bool shoreRoad = coastal && (roadTopo || nearPath);
                        if ((topo & oceanMask) != 0 && !shoreRoad) continue;
                        if (CellWet(p) && !shoreRoad) continue;
                        if (rail && !roadTopo && !asphalt && !nearPath) continue;
                        if (!asphalt && !roadTopo && !nearPath) continue;
                        if (coastal && !nearPath && !roadTopo && asphalt)
                        {
                            // Harbor pads / crane lots are gravel but not the land-side highway.
                            continue;
                        }
                        walk[id] = true;
                        accepted++;
                    }
                }
                if (accepted < 12)
                {
                    Puts($"[Events] baker skip {entry.Grid} {key} cells={accepted} reason=thin-mask");
                    return false;
                }

                // Thickness: 5x5 accepted count. Pads (runway / lot) score worse than 8-16m corridors.
                for (int iz = 1; iz < dim - 1; iz++)
                {
                    for (int ix = 1; ix < dim - 1; ix++)
                    {
                        int id = ix + iz * dim;
                        if (!walk[id]) continue;
                        int n = 0;
                        for (int oz = -2; oz <= 2; oz++)
                        {
                            int zz = iz + oz;
                            if (zz < 0 || zz >= dim) continue;
                            for (int ox = -2; ox <= 2; ox++)
                            {
                                int xx = ix + ox;
                                if (xx < 0 || xx >= dim) continue;
                                if (walk[xx + zz * dim]) n++;
                            }
                        }
                        pad[id] = n >= 16;
                    }
                }

                var gates = new List<int>();
                if (entry.GateX != null && entry.GateZ != null)
                {
                    int gn = Math.Min(entry.GateX.Count, entry.GateZ.Count);
                    for (int g = 0; g < gn; g++)
                    {
                        int best = -1;
                        float bestD = 32f * 32f;
                        for (int i = 0; i < maxCells; i++)
                        {
                            if (!walk[i]) continue;
                            float dx = wx[i] - entry.GateX[g], dz = wz[i] - entry.GateZ[g];
                            float d2 = dx * dx + dz * dz;
                            if (d2 < bestD) { bestD = d2; best = i; }
                        }
                        if (best >= 0 && !gates.Contains(best)) gates.Add(best);
                    }
                }
                // Ring anchors: walkable cells near the radius that sit on Path.Road.
                float ringInner = rad * 0.72f;
                float ringInnerSq = ringInner * ringInner;
                for (int i = 0; i < maxCells && gates.Count < 8; i++)
                {
                    if (!walk[i] || pad[i]) continue;
                    float dx = wx[i] - origin.x, dz = wz[i] - origin.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < ringInnerSq) continue;
                    bool nearPath = false;
                    for (int r = 0; r < _roadCache.Count && !nearPath; r++)
                    {
                        var pts = _roadCache[r].Points;
                        if (pts == null) continue;
                        for (int pi = 0; pi < pts.Count; pi += 3)
                        {
                            float rx = pts[pi].x - wx[i], rz = pts[pi].z - wz[i];
                            if (rx * rx + rz * rz <= 100f) { nearPath = true; break; }
                        }
                    }
                    if (!nearPath) continue;
                    bool far = true;
                    for (int g = 0; g < gates.Count; g++)
                    {
                        float gx = wx[i] - wx[gates[g]], gz = wz[i] - wz[gates[g]];
                        if (gx * gx + gz * gz < 40f * 40f) { far = false; break; }
                    }
                    if (far) gates.Add(i);
                }
                if (gates.Count < 2) return false;

                int[] prev = new int[maxCells];
                float[] dist = new float[maxCells];
                int[] open = new int[maxCells];
                int[] neigh = { 1, -1, dim, -dim, dim + 1, dim - 1, -dim + 1, -dim - 1 };

                List<int> bestPath = null;
                float bestScore = -1f;
                string via = "skeleton";

                for (int ga = 0; ga < gates.Count; ga++)
                {
                    for (int gb = ga + 1; gb < gates.Count; gb++)
                    {
                        int start = gates[ga], goal = gates[gb];
                        for (int i = 0; i < maxCells; i++) { dist[i] = 1e9f; prev[i] = -1; }
                        dist[start] = 0f;
                        int on = 0;
                        open[on++] = start;
                        while (on > 0)
                        {
                            int pick = 0;
                            float pickD = dist[open[0]];
                            for (int k = 1; k < on; k++)
                            {
                                if (dist[open[k]] < pickD) { pickD = dist[open[k]]; pick = k; }
                            }
                            int u = open[pick];
                            open[pick] = open[--on];
                            if (u == goal) break;
                            int ux = u % dim, uz = u / dim;
                            for (int k = 0; k < neigh.Length; k++)
                            {
                                int v = u + neigh[k];
                                if (v < 0 || v >= maxCells || !walk[v]) continue;
                                int vx = v % dim, vz = v / dim;
                                if (Math.Abs(vx - ux) > 1 || Math.Abs(vz - uz) > 1) continue;
                                float stepCost = (vx != ux && vz != uz) ? 1.41f : 1f;
                                if (pad[v]) stepCost += airfield ? 6f : 2.4f;
                                if (coastal && pad[v]) stepCost += 4f;
                                float odx = wx[v] - origin.x, odz = wz[v] - origin.z;
                                float od = Mathf.Sqrt(odx * odx + odz * odz);
                                if (plant && od < rad * 0.42f) stepCost += 1.6f;
                                if (junk && od < rad * 0.35f) stepCost += 1.4f;
                                if (launch && od < rad * 0.30f) stepCost += 1.2f;
                                if (train && pad[v]) stepCost += 2f;
                                float nd = dist[u] + stepCost;
                                if (nd + 0.01f >= dist[v]) continue;
                                dist[v] = nd;
                                prev[v] = u;
                                bool listed = false;
                                for (int t = 0; t < on; t++) if (open[t] == v) { listed = true; break; }
                                if (!listed && on < open.Length) open[on++] = v;
                            }
                        }
                        if (prev[goal] < 0 && start != goal) continue;
                        var chain = new List<int>();
                        for (int c = goal; c >= 0; c = prev[c])
                        {
                            chain.Add(c);
                            if (c == start) break;
                            if (chain.Count > 800) { chain.Clear(); break; }
                        }
                        if (chain.Count < 6) continue;
                        chain.Reverse();
                        float len = 0f;
                        for (int i = 1; i < chain.Count; i++)
                            len += Vector2.Distance(new Vector2(wx[chain[i]], wz[chain[i]]), new Vector2(wx[chain[i - 1]], wz[chain[i - 1]]));
                        float need = coastal ? 60f : 80f;
                        if (len < need) continue;
                        float score = len;
                        if (!pad[start] && !pad[goal]) score += 40f;
                        if (plant || junk || launch) score += 20f;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPath = chain;
                            via = "skeleton+" + entry.Family;
                        }
                    }
                }

                if (bestPath == null || bestPath.Count < 6)
                {
                    Puts($"[Events] baker skip {entry.Grid} {key} cells={accepted} gates={gates.Count} reason=no-two-gate");
                    return false;
                }

                var world = new List<Vector3>();
                float acc = 0f;
                world.Add(new Vector3(wx[bestPath[0]], 0f, wz[bestPath[0]]));
                for (int i = 1; i < bestPath.Count; i++)
                {
                    var a = new Vector3(wx[bestPath[i - 1]], 0f, wz[bestPath[i - 1]]);
                    var b = new Vector3(wx[bestPath[i]], 0f, wz[bestPath[i]]);
                    acc += Vector3.Distance(a, b);
                    if (acc >= 14f || i == bestPath.Count - 1)
                    {
                        world.Add(b);
                        acc = 0f;
                    }
                }
                if (world.Count < 4) return false;
                for (int i = 0; i < world.Count; i++)
                {
                    var p = world[i];
                    p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) : 0f;
                    world[i] = p;
                }

                entry.LaneX = entry.LaneX ?? new List<float>();
                entry.LaneZ = entry.LaneZ ?? new List<float>();
                entry.LaneX.Clear();
                entry.LaneZ.Clear();
                float pathLen = 0f;
                for (int i = 0; i < world.Count; i++)
                {
                    entry.LaneX.Add(world[i].x);
                    entry.LaneZ.Add(world[i].z);
                    if (i > 0) pathLen += Vector3.Distance(world[i], world[i - 1]);
                }

                if (!entry.BradleySkip && (pathLen >= 80f || LaneHasTwoGatesWorld(entry)))
                {
                    if (entry.Kind == "courtyard" || entry.Kind == "roadside" || string.IsNullOrEmpty(entry.Kind))
                        entry.Kind = "through";
                }

                RememberBradleyPath(entry, key, world, bestPath.Count, pathLen, via);
                Puts($"[Events] baker {entry.Grid} {key} cells={accepted} path={world.Count} len={pathLen:F0}m via={via} kind={entry.Kind}");
                return true;
            }
            catch (Exception bakeEx)
            {
                Puts("[Events] baker " + key + ": " + bakeEx.Message);
                return false;
            }
        }

        private void RememberBradleyPath(MonumentScanEntry entry, string key, List<Vector3> world, int cells, float len, string via)
        {
            if (entry == null || world == null || world.Count < 2) return;
            if (_bradleyPaths == null)
            {
                _bradleyPaths = new BradleyPathFileData
                {
                    Map = GetMapIdentity(),
                    Version = BradleyPathVersion,
                    Prefabs = new Dictionary<string, BradleyPathPrefab>(StringComparer.OrdinalIgnoreCase)
                };
            }
            var rec = new BradleyPathPrefab
            {
                Prefab = key,
                Family = entry.Family,
                Kind = entry.Kind,
                Tour = BradleyCanTourMonument(entry),
                Spacing = 14f,
                Cells = cells,
                Length = len,
                Via = via
            };
            for (int i = 0; i < world.Count; i++)
            {
                float lx, lz;
                WorldToLocal(entry, world[i].x, world[i].z, out lx, out lz);
                rec.LocalX.Add(lx);
                rec.LocalZ.Add(lz);
            }
            _bradleyPaths.Prefabs[key] = rec;
        }

        private void SaveBradleyPaths()
        {
            try
            {
                if (_bradleyPaths == null) _bradleyPaths = new BradleyPathFileData();
                _bradleyPaths.Map = GetMapIdentity();
                _bradleyPaths.Version = BradleyPathVersion;
                Interface.Oxide.DataFileSystem.WriteObject(BradleyPathFile, _bradleyPaths);
            }
            catch (Exception saveEx)
            {
                Puts("[Events] bradley-paths save: " + saveEx.Message);
            }
        }

        private void EnsureBradleyPathCatalog()
        {
            if (_bradleyPaths == null)
            {
                try
                {
                    _bradleyPaths = Interface.Oxide.DataFileSystem.ReadObject<BradleyPathFileData>(BradleyPathFile);
                }
                catch { _bradleyPaths = null; }
            }
            if (_bradleyPaths == null)
                _bradleyPaths = new BradleyPathFileData();
            if (_bradleyPaths.Prefabs == null)
                _bradleyPaths.Prefabs = new Dictionary<string, BradleyPathPrefab>(StringComparer.OrdinalIgnoreCase);
            _bradleyPaths.Map = GetMapIdentity();
            _bradleyPaths.Version = BradleyPathVersion;
            SeedBuiltInBradleyTemplates();
            SaveBradleyPaths();
        }

        private void SeedTpl(string prefab, string family, string kind, bool tour, float[] xz)
        {
            if (string.IsNullOrEmpty(prefab) || xz == null || xz.Length < 16) return;
            float newLen = 0f;
            for (int i = 2; i + 1 < xz.Length; i += 2)
            {
                float dx = xz[i] - xz[i - 2], dz = xz[i + 1] - xz[i - 1];
                newLen += Mathf.Sqrt(dx * dx + dz * dz);
            }
            BradleyPathPrefab existing;
            if (_bradleyPaths.Prefabs.TryGetValue(prefab, out existing)
                && existing != null && existing.LocalX != null && existing.LocalX.Count >= 8
                && existing.Length + 15f >= newLen
                && existing.LocalX.Count >= xz.Length / 2
                && existing.Via != null && existing.Via.IndexOf("template-v2", StringComparison.OrdinalIgnoreCase) < 0
                && existing.Via.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            var rec = new BradleyPathPrefab
            {
                Prefab = prefab,
                Family = family,
                Kind = kind,
                Tour = tour,
                Spacing = 14f,
                Via = "template-v3",
                Cells = xz.Length / 2
            };
            float len = 0f;
            float px = 0f, pz = 0f;
            for (int i = 0; i + 1 < xz.Length; i += 2)
            {
                rec.LocalX.Add(xz[i]);
                rec.LocalZ.Add(xz[i + 1]);
                if (i >= 2)
                {
                    float dx = xz[i] - px, dz = xz[i + 1] - pz;
                    len += Mathf.Sqrt(dx * dx + dz * dz);
                }
                px = xz[i];
                pz = xz[i + 1];
            }
            rec.Length = len;
            _bradleyPaths.Prefabs[prefab] = rec;
        }

        /// <summary>
        /// Prefab-local OPEN through-corridors (gate → opposite gate).
        /// Built from baker interior asphalt where it stays inside the prefab
        /// footprint, otherwise from overhead layout (C-perimeter / taxi / land-side
        /// / rim). Rotated by instance Yaw at runtime. Not closed rings, pads,
        /// docks, or runways. Procedural highway clips stay on ClipThroughRoadLane.
        /// </summary>
        private void SeedBuiltInBradleyTemplates()
        {
            // Power Plant — baker C-perimeter (south/west service road).
            SeedTpl("powerplant_1", "yard", "through", true, new float[] {
                114f,-61f, 98f,-56f, 82f,-52f, 66f,-47f, 52f,-47f, 36f,-42f, 26f,-54f, 18f,-65f,
                5f,-72f, -4f,-70f, -13f,-58f, -24f,-50f, -35f,-43f, -46f,-35f, -57f,-27f, -71f,-21f,
                -79f,-11f, -90f,-4f, -97f,10f, -97f,23f, -104f,36f, -115f,44f, -116f,57f, -114f,61f
            });
            // Dome / sphere — baker north rim (does not climb the sphere).
            SeedTpl("sphere_tank", "yard", "through", true, new float[] {
                129f,9f, 125f,24f, 121f,39f, 115f,53f, 105f,62f, 105f,74f, 95f,86f, 85f,97f,
                73f,103f, 58f,105f, 43f,107f, 28f,106f, 13f,106f, 1f,111f, -15f,116f, -30f,119f,
                -45f,118f, -59f,114f, -71f,105f, -83f,95f, -91f,86f, -100f,73f, -108f,61f, -119f,48f,
                -125f,36f, -125f,33f
            });
            // Radtown — baker east-west service road along the south of the buildings.
            SeedTpl("radtown_1", "yard", "through", true, new float[] {
                45f,122f, 45f,107f, 53f,95f, 62f,82f, 74f,70f, 86f,58f, 94f,46f, 91f,34f,
                82f,22f, 69f,13f, 60f,4f, 48f,2f, 33f,5f, 18f,3f, 6f,9f, -6f,3f,
                -21f,10f, -33f,4f, -48f,10f, -63f,5f, -69f,17f, -84f,14f, -96f,8f, -108f,3f,
                -120f,9f, -129f,15f
            });
            // Arctic research — baker south fence approach, SW gate to NE gate.
            SeedTpl("arctic_research_base_a", "yard", "through", true, new float[] {
                -22f,-128f, -5f,-129f, 10f,-129f, 23f,-126f, 36f,-122f, 49f,-119f, 63f,-112f, 76f,-105f,
                87f,-95f, 97f,-83f, 107f,-71f, 114f,-57f, 120f,-45f, 125f,-32f
            });
            // Launch Site — OUTER south/west service C. Not the inner Bradley ring.
            SeedTpl("launch_site_1", "yard", "through", true, new float[] {
                -175f,45f, -180f,5f, -175f,-40f, -160f,-90f, -130f,-130f, -85f,-155f, -30f,-170f,
                25f,-172f, 80f,-160f, 125f,-135f, 155f,-95f, 175f,-50f, 180f,-5f, 165f,40f, 140f,75f
            });
            // Airfield — south taxiway only (hangar row). Not the runway, not a closed apron.
            SeedTpl("airfield_1", "yard", "through", true, new float[] {
                -220f,-38f, -180f,-44f, -140f,-48f, -100f,-51f, -60f,-53f, -20f,-54f, 20f,-54f,
                60f,-52f, 100f,-49f, 140f,-44f, 180f,-36f, 215f,-24f
            });
            // Water Treatment — west spine + north cross (open C around the tanks).
            SeedTpl("water_treatment_plant_1", "yard", "through", true, new float[] {
                -72f,-118f, -78f,-85f, -82f,-50f, -80f,-15f, -74f,20f, -62f,55f, -42f,85f,
                -12f,105f, 22f,112f, 55f,100f, 78f,72f, 88f,38f
            });
            // Trainyard — inner south/west rim of the yard (prefab roads, not the procedural rail).
            SeedTpl("trainyard_1", "yard", "through", true, new float[] {
                -110f,-20f, -95f,-55f, -70f,-82f, -35f,-95f, 5f,-98f, 45f,-88f, 75f,-60f,
                92f,-25f, 88f,15f, 62f,50f, 25f,72f
            });
            // Junkyard — inner dirt rim around the pit (not the outside highway).
            SeedTpl("junkyard_1", "yard", "through", true, new float[] {
                -85f,-15f, -78f,-50f, -50f,-82f, -10f,-95f, 35f,-88f, 70f,-55f, 85f,-15f,
                78f,25f, 48f,58f, 10f,72f
            });
            // Giant Excavator — pit-rim service road, open C, stays off the bucket.
            SeedTpl("excavator_1", "yard", "through", true, new float[] {
                -130f,-10f, -120f,-55f, -90f,-100f, -40f,-125f, 20f,-130f, 75f,-110f, 115f,-70f,
                130f,-20f, 120f,30f, 85f,75f, 30f,105f
            });
            // Large harbor — LAND-SIDE warehouse road only. No docks, no bridge.
            SeedTpl("harbor_1", "coastal", "through", true, new float[] {
                95f,70f, 90f,35f, 78f,5f, 55f,-22f, 22f,-42f, -15f,-50f, -50f,-42f,
                -80f,-20f, -98f,10f, -100f,45f, -85f,75f
            });
            // Small harbor — land-side C.
            SeedTpl("harbor_2", "coastal", "through", true, new float[] {
                75f,40f, 70f,10f, 50f,-22f, 20f,-45f, -15f,-52f, -50f,-40f, -78f,-12f,
                -92f,20f, -85f,50f
            });
            // Abandoned military base — south gate through the compound to the north cut.
            SeedTpl("desert_military_base_a", "yard", "through", true, new float[] {
                -48f,-95f, -30f,-88f, -12f,-60f, 0f,-28f, 10f,5f, 22f,38f, 38f,70f, 50f,98f
            });
            SeedTpl("desert_military_base_b", "yard", "through", true, new float[] {
                -48f,-95f, -30f,-88f, -12f,-60f, 0f,-28f, 10f,5f, 22f,38f, 38f,70f, 50f,98f
            });
            SeedTpl("desert_military_base_c", "yard", "through", true, new float[] {
                -48f,-95f, -30f,-88f, -12f,-60f, 0f,-28f, 10f,5f, 22f,38f, 38f,70f, 50f,98f
            });
            SeedTpl("desert_military_base_d", "yard", "through", true, new float[] {
                -48f,-95f, -30f,-88f, -12f,-60f, 0f,-28f, 10f,5f, 22f,38f, 38f,70f, 50f,98f
            });
            // Satellite dish — driveway that nicks the south edge of the dishes.
            SeedTpl("satellite_dish", "yard", "through", true, new float[] {
                -70f,-25f, -50f,-38f, -25f,-42f, 0f,-38f, 25f,-28f, 48f,-10f, 62f,15f, 68f,42f
            });
            // Nuclear missile silo — fenced compound through-road (not the silo pit).
            SeedTpl("nuclear_missile_silo", "yard", "through", true, new float[] {
                -95f,-35f, -80f,-75f, -40f,-100f, 10f,-105f, 55f,-85f, 85f,-45f, 95f,0f,
                80f,45f, 40f,80f
            });
            // Military tunnels — SURFACE compound only (entrance road). No underground.
            SeedTpl("military_tunnel_1", "yard", "through", true, new float[] {
                -85f,-15f, -70f,-48f, -35f,-68f, 5f,-72f, 40f,-55f, 62f,-20f, 65f,20f, 45f,52f
            });
            SeedTpl("military_tunnels_1", "yard", "through", true, new float[] {
                -85f,-15f, -70f,-48f, -35f,-68f, 5f,-72f, 40f,-55f, 62f,-20f, 65f,20f, 45f,52f
            });
            // Sewer branch — surface road past the pipe buildings.
            SeedTpl("sewer_branch", "yard", "through", true, new float[] {
                -70f,-40f, -50f,-65f, -15f,-78f, 20f,-70f, 50f,-45f, 62f,-10f, 55f,25f, 25f,50f
            });
            Puts("[Events] bradley templates seeded prefabs=" + _bradleyPaths.Prefabs.Count);
        }

        private void ApplyPrefabTemplatesToScan()
        {
            if (!_monumentScanReady || _bradleyPaths?.Prefabs == null) return;
            int applied = 0;
            const float minTplSpan = 80f * 80f;
            const float keepBakerSpan = 120f * 120f;
            for (int i = 0; i < _monumentScan.Count; i++)
            {
                var e = _monumentScan[i];
                if (e == null || e.BradleySkip) continue;
                var tpl = GetPrefabTemplateLane(e);
                if (tpl.Count < 8) continue;
                float tplSpan = LaneSpanSq(tpl);
                // Closed rings have a tiny first-last span — never stamp those over a highway.
                if (tplSpan < minTplSpan) continue;
                float have = 0f;
                if (e.LaneX != null && e.LaneX.Count >= 2)
                {
                    float dx = e.LaneX[e.LaneX.Count - 1] - e.LaneX[0];
                    float dz = e.LaneZ[e.LaneZ.Count - 1] - e.LaneZ[0];
                    have = dx * dx + dz * dz;
                }
                // Baker already found a two-gate interior/through road — keep it.
                if (e.LaneX != null && e.LaneX.Count >= 8 && have >= keepBakerSpan)
                    continue;
                if (e.LaneX != null && e.LaneX.Count >= tpl.Count && have >= tplSpan)
                    continue;
                e.LaneX = new List<float>();
                e.LaneZ = new List<float>();
                for (int p = 0; p < tpl.Count; p++)
                {
                    e.LaneX.Add(tpl[p].x);
                    e.LaneZ.Add(tpl[p].z);
                }
                if (e.Kind != "through") e.Kind = "through";
                applied++;
            }
            if (applied > 0)
            {
                Puts("[Events] applied " + applied + " prefab path templates onto monument lanes");
                SaveMonumentScan();
            }
        }

        private void ForceRebuildMonumentBaker()
        {
            _monumentScanReady = false;
            _monumentScan.Clear();
            _throughMonRoads.Clear();
            _bradleyPaths = new BradleyPathFileData
            {
                Map = GetMapIdentity(),
                Version = BradleyPathVersion,
                Prefabs = new Dictionary<string, BradleyPathPrefab>(StringComparer.OrdinalIgnoreCase)
            };
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(MonumentScanFile, new MonumentScanFileData { Map = "force", Version = "force" });
            }
            catch { }
            EnsureRoadCache();
            if (!_monumentScanReady)
                BuildMonumentScan();
            SaveBradleyPaths();
        }


        private MonumentScanEntry FindMonumentScanAt(Vector3 p)
        {
            MonumentScanEntry best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < _monumentScan.Count; i++)
            {
                var m = _monumentScan[i];
                if (m == null) continue;
                float dx = p.x - m.X, dz = p.z - m.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 > m.Radius * m.Radius) continue;
                bool mRoad = string.Equals(m.Kind, "roadside", StringComparison.OrdinalIgnoreCase);
                bool bRoad = best != null && string.Equals(best.Kind, "roadside", StringComparison.OrdinalIgnoreCase);
                // Gas / supermarket on a ferry or plant approach must win over
                // the larger courtyard circle that overlaps the same asphalt.
                if (best == null
                    || (mRoad && !bRoad)
                    || (mRoad == bRoad && m.Radius < best.Radius)
                    || (mRoad == bRoad && Mathf.Abs(m.Radius - best.Radius) < 1f && d2 < bestD))
                {
                    bestD = d2;
                    best = m;
                }
            }
            return best;
        }

        /// <summary>
        /// Through-yards, baker two-gate lanes, land-side harbor corridors.
        /// Ferry / fishing stay BradleySkip.
        /// </summary>
        private static bool BradleyCanTourMonument(MonumentScanEntry m)
        {
            if (m == null || m.BradleySkip) return false;
            // Interior baker / template C-roads are traps (S6 powerplant,
            // launch ring). Only a named Path.Road through the lot is a tour,
            // and even then a live highway is never swapped for it.
            if (m.ThroughRoads == null || m.ThroughRoads.Count == 0) return false;
            if (m.LaneX == null || m.LaneX.Count < 8) return false;
            return string.Equals(m.Kind, "through", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Highway chain that clips a courtyard / skip lot (powerplant spur, gas).
        /// Advance dest to the first node past that ring. Hull stays put.
        /// </summary>
        private int SkipBradleyCourtyardAhead(List<Vector3> route, Vector3 pos, int from)
        {
            // Highway patrol stays on the painted road through a plant / airfield.
            // Skipping dest over S6 is how the tank left the asphalt.
            return from;
            if (route == null || from < 0 || from >= route.Count - 3) return from;
            MonumentScanEntry mon = null;
            int lookTo = Mathf.Min(from + 14, route.Count - 1);
            for (int i = from; i <= lookTo; i++)
            {
                var hit = FindMonumentScanAt(route[i]);
                if (hit == null) continue;
                // Highway past stables / supermarket / gas / warehouse — keep driving.
                if (hit.BradleySkip || hit.Kind == "roadside" || hit.Kind == "skip")
                    continue;
                // Only a monument that actually lists through-roads is a tour.
                if (hit.ThroughRoads != null && hit.ThroughRoads.Count > 0)
                    continue;
                if (BradleyCanTourMonument(hit))
                    continue;
                bool yardish = string.Equals(hit.Kind, "courtyard", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hit.Kind, "through", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hit.Family, "yard", StringComparison.OrdinalIgnoreCase);
                if (yardish)
                {
                    mon = hit;
                    break;
                }
            }
            if (mon == null) return from;
            float r = (mon.Radius + 28f) * (mon.Radius + 28f);
            for (int i = from + 1; i < route.Count; i++)
            {
                float dx = route[i].x - mon.X, dz = route[i].z - mon.Z;
                if (dx * dx + dz * dz > r)
                    return i;
            }
            return from;
        }

        /// <summary>
        /// S7 sits just outside powerplant_1's 130m circle. Still a courtyard approach.
        /// </summary>
        private bool IsCourtyardNearby(Vector3 p, float maxDist)
        {
            if (!_monumentScanReady || _monumentScan.Count == 0) return IsHarborLot(p) || IsBradleyNoGoLot(p);
            float maxSq = maxDist * maxDist;
            for (int i = 0; i < _monumentScan.Count; i++)
            {
                var m = _monumentScan[i];
                if (m == null) continue;
                if (m.BradleySkip || m.Kind == "skip" || m.Kind == "roadside") continue;
                bool yard = m.Kind == "courtyard" || m.Kind == "through"
                            || string.Equals(m.Family, "yard", StringComparison.OrdinalIgnoreCase);
                if (!yard) continue;
                float dx = p.x - m.X, dz = p.z - m.Z;
                if (dx * dx + dz * dz <= maxSq) return true;
            }
            float d;
            string lab = ClosestMonumentLabel(p, out d);
            if (d <= maxDist && YardMonumentName(lab)) return true;
            return IsHarborLot(p);
        }

        /// <summary>
        /// Keep a highway node only when it sits inside a monument that lists
        /// ThroughRoads. Global _throughMonRoads tags whole Path.Road ids, so
        /// road #1 was "through" at S6 even though powerplant_1.ThroughRoads=[].
        /// </summary>
        private bool PointIsMonumentThroughKeep(Vector3 p)
        {
            var m = FindMonumentScanAt(p);
            if (m == null) return false;
            if (m.ThroughRoads == null || m.ThroughRoads.Count == 0) return false;
            if (m.BradleySkip || m.Kind == "skip" || m.Kind == "roadside") return false;
            return true;
        }

        private static bool YardMonumentName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.IndexOf("powerplant", StringComparison.Ordinal) >= 0
                || n.IndexOf("power_plant", StringComparison.Ordinal) >= 0
                || n.IndexOf("launch_site", StringComparison.Ordinal) >= 0
                || n.IndexOf("launchsite", StringComparison.Ordinal) >= 0
                || n.IndexOf("airfield", StringComparison.Ordinal) >= 0
                || n.IndexOf("trainyard", StringComparison.Ordinal) >= 0
                || n.IndexOf("train_yard", StringComparison.Ordinal) >= 0
                || n.IndexOf("water_treatment", StringComparison.Ordinal) >= 0
                || n.IndexOf("watertreatment", StringComparison.Ordinal) >= 0
                || n.IndexOf("excavator", StringComparison.Ordinal) >= 0
                || n.IndexOf("sphere_tank", StringComparison.Ordinal) >= 0
                || n.IndexOf("military_tunnel", StringComparison.Ordinal) >= 0
                || n.IndexOf("missile_silo", StringComparison.Ordinal) >= 0
                || n.IndexOf("satellite", StringComparison.Ordinal) >= 0
                || n.IndexOf("junkyard", StringComparison.Ordinal) >= 0
                || n.IndexOf("sewer", StringComparison.Ordinal) >= 0
                || n.IndexOf("arctic_research", StringComparison.Ordinal) >= 0
                || n.IndexOf("desert_military", StringComparison.Ordinal) >= 0;
        }

        private bool OnMonumentThroughRoad(Vector3 p)
        {
            if (_throughMonRoads.Count == 0 || _roadCache.Count == 0) return false;
            const float keepSq = 22f * 22f;
            foreach (int rid in _throughMonRoads)
            {
                if (rid < 0 || rid >= _roadCache.Count) continue;
                var pts = _roadCache[rid].Points;
                if (pts == null) continue;
                for (int i = 0; i < pts.Count; i++)
                {
                    float dx = pts[i].x - p.x, dz = pts[i].z - p.z;
                    if (dx * dx + dz * dz <= keepSq) return true;
                }
            }
            return false;
        }

        /// <summary>NPC/core: "through" | "courtyard" | "roadside" | "skip" | "".</summary>
        public object API_MonumentKind(Vector3 pos)
        {
            if (!_monumentScanReady) return "";
            var m = FindMonumentScanAt(pos);
            return m == null ? "" : (m.Kind ?? "");
        }

        /// <summary>True when pos is on a painted through-road that crosses a monument.</summary>
        public object API_OnMonumentThroughRoad(Vector3 pos)
        {
            return OnMonumentThroughRoad(pos);
        }

        private bool BradleyPointNoRiver(Vector3 p)
        {
            try
            {
                float ground = p.y;
                if (TerrainMeta.HeightMap != null) ground = TerrainMeta.HeightMap.GetHeight(p);
                float water = ground;
                if (TerrainMeta.WaterMap != null) water = TerrainMeta.WaterMap.GetHeight(p);
                if (water - ground > 0.35f) return false;
                try
                {
                    float overall = WaterLevel.GetOverallWaterDepth(p, true, true, null);
                    if (overall > 0.35f) return false;
                }
                catch { }
                if (TerrainMeta.TopologyMap != null)
                {
                    int topo = TerrainMeta.TopologyMap.GetTopology(p);
                    if ((topo & (int)TerrainTopology.Enum.Ocean) != 0) return false;
                    if ((topo & (int)TerrainTopology.Enum.River) != 0) return false;
                }
            }
            catch { }
            return true;
        }

        private bool BradleySegmentDry(Vector3 a, Vector3 b)
        {
            if (!BradleyPointDry(a) || !BradleyPointDry(b)) return false;
            if (!BradleyPointNoRiver(a) || !BradleyPointNoRiver(b)) return false;
            int samples = (IsMonumentRoadPoint(a) || IsMonumentRoadPoint(b)) ? 5 : 3;
            for (int i = 1; i <= samples; i++)
            {
                float t = i / (float)(samples + 1);
                Vector3 m = new Vector3(a.x + (b.x - a.x) * t, 0f, a.z + (b.z - a.z) * t);
                if (!BradleyPointNoRiver(m)) return false;
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
                if (IsBradleyNoGoLot(p) && !OnMonumentThroughRoad(p))
                {
                    // Courtyard interior — keep the approach, do not jump the lot.
                    if (has && outp.Count >= 8) break;
                    continue;
                }
                if (!BradleyPointDry(p)) continue;
                try { p.y = TerrainMeta.HeightMap.GetHeight(p) + 1.0f; } catch { }
                if (has)
                {
                    float dx = p.x - last.x;
                    float dz = p.z - last.z;
                    float distSq = dx * dx + dz * dz;
                    bool mon = IsMonumentRoadPoint(last) || IsMonumentRoadPoint(p);
                    float minSp = mon ? 8f : 16f;
                    if (distSq < minSp * minSp) continue;
                    if (!BradleySegmentDry(last, p)) continue;
                    if (mon && distSq > 12f * 12f)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        int n = Mathf.Clamp(Mathf.FloorToInt(dist / 10f), 1, 6);
                        for (int k = 1; k <= n; k++)
                        {
                            Vector3 mid = new Vector3(
                                last.x + dx * (k / (float)(n + 1)),
                                last.y,
                                last.z + dz * (k / (float)(n + 1)));
                            try { mid.y = TerrainMeta.HeightMap.GetHeight(mid) + 1.0f; } catch { }
                            if (BradleyPointDry(mid) && BradleyPointNoRiver(mid))
                                outp.Add(mid);
                        }
                    }
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
                    _roadEdges.Add(new RoadEdge { A = aId, B = bId, Len = len, Width = road.Width, Pts = slice });
                }
            }
            Puts($"[Events] Road graph xings={_roadXings.Count} edges={_roadEdges.Count}");
        }

        private List<Vector3> BuildBradleyIntersectionRoute(Vector3 here, int maxPoints, Vector3? avoid = null)
        {
            EnsureRoadGraph();
            var path = new List<Vector3>();
            if (_roadEdges.Count == 0) return BuildLongAsphaltFromHere(here, maxPoints);

            int atXing = -1;
            float xBest = float.MaxValue;
            for (int i = 0; i < _roadXings.Count; i++)
            {
                float dx = _roadXings[i].x - here.x;
                float dz = _roadXings[i].z - here.z;
                float d = dx * dx + dz * dz;
                if (d < xBest) { xBest = d; atXing = i; }
            }
            if (atXing < 0 || xBest > 90f * 90f)
                return BuildLongAsphaltFromHere(here, maxPoints);

            var firstOpts = new List<(int e, int nxt, float len, float away)>();
            for (int e = 0; e < _roadEdges.Count; e++)
            {
                var ed = _roadEdges[e];
                if (ed.Pts == null || ed.Pts.Count < 2) continue;
                int nxt = -1;
                if (ed.A == atXing) nxt = ed.B;
                else if (ed.B == atXing) nxt = ed.A;
                else continue;
                Vector3 other = _roadXings[nxt];
                float away = 0f;
                if (avoid.HasValue)
                {
                    Vector3 av = avoid.Value;
                    float dHere = (here.x - av.x) * (here.x - av.x) + (here.z - av.z) * (here.z - av.z);
                    float dOth = (other.x - av.x) * (other.x - av.x) + (other.z - av.z) * (other.z - av.z);
                    if (dOth + 40f * 40f < dHere) continue;
                    away = dOth - dHere;
                }
                firstOpts.Add((e, nxt, ed.Len, away));
            }
            if (firstOpts.Count == 0) return BuildLongAsphaltFromHere(here, maxPoints);
            firstOpts.Sort((a, b) =>
            {
                if (Mathf.Abs(b.away - a.away) > 80f * 80f) return b.away.CompareTo(a.away);
                float wa = _roadEdges[a.e].Width, wb = _roadEdges[b.e].Width;
                if ((wa >= 5f) != (wb >= 5f)) return (wb >= 5f).CompareTo(wa >= 5f);
                return b.len.CompareTo(a.len);
            });
            var first = firstOpts[UnityEngine.Random.Range(0, Mathf.Min(3, firstOpts.Count))];

            var used = new HashSet<int>();
            int at = -1;
            {
                var ed = _roadEdges[first.e];
                used.Add(first.e);
                if (ed.A == atXing)
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
                opts.Sort((a, b) =>
                {
                    float wa = _roadEdges[a.e].Width;
                    float wb = _roadEdges[b.e].Width;
                    bool aa = wa >= 5f;
                    bool ab = wb >= 5f;
                    if (aa != ab) return ab.CompareTo(aa);
                    return b.len.CompareTo(a.len);
                });
                int pool = Mathf.Min(opts.Count, 3);
                var pick = opts[UnityEngine.Random.Range(0, pool)];
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
                string g0 = PositionToGrid(path[0]);
                string g1 = PositionToGrid(path[path.Count - 1]);
                float span = Vector3.Distance(
                    new Vector3(path[0].x, 0f, path[0].z),
                    new Vector3(path[path.Count - 1].x, 0f, path[path.Count - 1].z));
                DebugLog($"Bradley xing-route {g0} -> {g1} pts={path.Count} hops={used.Count} span={span:F0}m xings={_roadXings.Count}");
                // Same-grid stubs stall the tank. One long asphalt edge (hops=1) is fine if span is long.
                bool longSingle = used.Count == 1 && span >= 400f && path.Count >= 16;
                if (g0 == g1 || path.Count < 16 || span < 180f || (used.Count < 2 && !longSingle))
                    return new List<Vector3>();
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
            int maxPts = Mathf.Max(48, bp?.MaxWaypoints ?? 64);

            if (_roadCache.Count == 0) BuildRoadCache();
            int spawnRoad = -1;
            var route = BuildBradleySpawnChain(maxPts, out spawnRoad);
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
            try
            {
                entity.enableSaving = false;
                var apc0 = entity as BradleyAPC;
                if (apc0 != null)
                {
                    try { apc0.RoadSpawned = true; } catch { }
                }
            }
            catch { }
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
                    StartBradleyPatrolRoute(bradley, route, 1, spawnRoad);
                    AttachBradleyUnstick(bradley);
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

        private static List<Vector3> TrimBradleyRoadEnds(List<Vector3> pts, int drop)
        {
            if (pts == null || pts.Count < drop * 2 + 5) return pts ?? new List<Vector3>();
            var cut = new List<Vector3>(pts.Count - drop * 2);
            for (int i = drop; i < pts.Count - drop; i++) cut.Add(pts[i]);
            return cut;
        }

        private List<Vector3> PrepareNamedRoad(CachedRoad road, int maxPts)
        {
            if (road?.Points == null) return new List<Vector3>();
            // Keep the whole TerrainMeta polyline. Trim/straighten was ending the
            // path in the middle of a longer asphalt and the tank then reversed.
            return FilterBradleyRoad(road.Points, Mathf.Max(maxPts, 200));
        }

        private bool BradleyRoadFolds(List<Vector3> pts)
        {
            if (pts == null || pts.Count < 8) return false;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pts.Count; i++)
            {
                string g = null;
                try { g = PositionToGrid(pts[i]); } catch { continue; }
                if (string.IsNullOrEmpty(g)) continue;
                int last;
                if (seen.TryGetValue(g, out last) && i - last > 4)
                    return true;
                seen[g] = i;
            }
            return false;
        }

        private List<Vector3> PickNamedBradleyRoad(HashSet<int> used, Vector3 here, Vector3 avoid, bool nearHere, int maxPts, out int pickedId, float joinMeters = 70f)
        {
            pickedId = -1;
            if (_roadCache.Count == 0) BuildRoadCache();
            var pool = new List<(int id, List<Vector3> pts, float span, float width, float near)>();
            for (int i = 0; i < _roadCache.Count; i++)
            {
                if (used != null && used.Contains(i)) continue;
                var road = _roadCache[i];
                if (road.Points == null || road.Points.Count < 8) continue;
                if (road.Width < 4.5f && road.Length < 250f) continue;
                var dry = PrepareNamedRoad(road, maxPts);
                if (dry.Count < 5) continue;
                float span = BradleyRouteSpan(dry);
                if (span < 250f && road.Length < 300f) continue;
                float near = 0f;
                if (nearHere)
                {
                    float d0 = (dry[0].x - here.x) * (dry[0].x - here.x) + (dry[0].z - here.z) * (dry[0].z - here.z);
                    float d1 = (dry[dry.Count - 1].x - here.x) * (dry[dry.Count - 1].x - here.x)
                             + (dry[dry.Count - 1].z - here.z) * (dry[dry.Count - 1].z - here.z);
                    near = Mathf.Min(d0, d1);
                    // Must meet the hull — 180m was a full grid and let S5 pick an R5 dirt road.
                    float join = joinMeters > 20f ? joinMeters : 70f;
                    if (near > join * join) continue;
                    if (d1 < d0)
                    {
                        dry.Reverse();
                        span = BradleyRouteSpan(dry);
                    }
                    if (avoid != Vector3.zero)
                    {
                        float endAway = (dry[dry.Count - 1].x - avoid.x) * (dry[dry.Count - 1].x - avoid.x)
                                      + (dry[dry.Count - 1].z - avoid.z) * (dry[dry.Count - 1].z - avoid.z);
                        float hereAway = (here.x - avoid.x) * (here.x - avoid.x) + (here.z - avoid.z) * (here.z - avoid.z);
                        if (endAway + 20f * 20f < hereAway) continue;
                    }
                    if (road.Width < 5f) continue;
                }
                // Keep the arterial. Cut the courtyard/lot tail after pick.
                if (dry.Count > 0 && IsBradleyNoGoLot(dry[0]) && !nearHere)
                    continue;
                pool.Add((i, dry, span, road.Width, near));
            }
            if (pool.Count == 0) return new List<Vector3>();
            pool.Sort((a, b) =>
            {
                if (!nearHere)
                {
                    bool am = _roadCache[a.id].IsMain, bm = _roadCache[b.id].IsMain;
                    if (am != bm) return bm.CompareTo(am);
                }
                bool aa = a.width >= 5f, ab = b.width >= 5f;
                if (aa != ab) return ab.CompareTo(aa);
                if (nearHere)
                {
                    int n = a.near.CompareTo(b.near);
                    if (n != 0) return n;
                }
                return b.span.CompareTo(a.span);
            });
            // Seed: any qualifying arterial, not the same eight long ones.
            var pick = pool[0];
            if (!nearHere && pool.Count > 1)
            {
                var choices = new List<(int id, List<Vector3> pts, float span, float width, float near)>();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].id == _lastBradleySeedId) continue;
                    if (_roadCache[pool[i].id].IsMain == false && pool[i].width < 6f) continue;
                    choices.Add(pool[i]);
                }
                if (choices.Count == 0)
                {
                    for (int i = 0; i < pool.Count; i++)
                        if (pool[i].id != _lastBradleySeedId) choices.Add(pool[i]);
                }
                if (choices.Count == 0) choices.Add(pool[0]);
                pick = choices[UnityEngine.Random.Range(0, choices.Count)];
                _lastBradleySeedId = pick.id;
                if (UnityEngine.Random.value < 0.5f && pick.pts.Count > 8)
                    pick.pts.Reverse();
                if (pick.pts.Count > 16)
                {
                    int hi = Mathf.Max(2, (int)(pick.pts.Count * 0.65f));
                    int drop = UnityEngine.Random.Range(0, hi);
                    if (drop > 0)
                        pick.pts.RemoveRange(0, drop);
                }
            }
            pickedId = pick.id;
            DebugLog($"Bradley named-road #{pick.id} {PositionToGrid(pick.pts[0])} -> {PositionToGrid(pick.pts[pick.pts.Count - 1])} pts={pick.pts.Count} span={pick.span:F0}m width={pick.width:F1}");
            return pick.pts;
        }

        private List<Vector3> PickNamedBradleyRoadAt(Vector3 here, Vector3 avoid, HashSet<int> used, int maxPts, out int pickedId, float joinMeters = 70f)
        {
            return PickNamedBradleyRoad(used, here, avoid, true, maxPts, out pickedId, joinMeters);
        }

        /// <summary>
        /// One TerrainMeta road is ~20 pts / 800 m. Stitch unused forward asphalt
        /// into a single polyline and install that once at spawn.
        /// </summary>
        private List<Vector3> BuildBradleySpawnChain(int maxPts, out int seedId)
        {
            seedId = -1;
            var used = new HashSet<int>();
            var seed = PickNamedBradleyRoad(used, Vector3.zero, Vector3.zero, false, maxPts, out seedId);
            if (seed == null || seed.Count < 5) return seed ?? new List<Vector3>();
            if (seed.Count < 8) return new List<Vector3>();
            if (seedId >= 0) used.Add(seedId);

            var chain = new List<Vector3>(128);
            chain.AddRange(seed);
            int joins = 0;
            float wantSpan = Mathf.Clamp(ScaleOnMap(2800f), 700f, 3600f);
            int wantPts = GetWorldHalf() >= 1600f ? 96 : 64;
            int wantJoins = GetWorldHalf() >= 1600f ? 8 : 5;
            while (chain.Count < wantPts && BradleyRouteSpan(chain) < wantSpan && joins < wantJoins)
            {
                Vector3 end = chain[chain.Count - 1];
                Vector3 prev = chain[Math.Max(0, chain.Count - 2)];
                Vector3 heading = new Vector3(end.x - prev.x, 0f, end.z - prev.z);
                int nextId = -1;
                var next = PickForwardNamedRoad(end, heading, used, chain, out nextId);
                if (next == null || next.Count < 5) break;
                int before = chain.Count;
                float beforeSpan = BradleyRouteSpan(chain);
                if (!AppendBradleyJoin(chain, next, nextId))
                {
                    if (nextId >= 0) used.Add(nextId);
                    continue;
                }
                Vector3 newEnd = chain[chain.Count - 1];
                Vector3 step = new Vector3(newEnd.x - end.x, 0f, newEnd.z - end.z);
                float stepDot = step.sqrMagnitude > 1f ? Vector3.Dot(heading.normalized, step.normalized) : -1f;
                float newSpan = BradleyRouteSpan(chain);
                // #7 met G8 and ended at D7. Span fell 286→252 and the APC folded west
                // instead of taking the south highway. A join has to extend the chain.
                if (stepDot < 0.05f || newSpan < beforeSpan + 60f)
                {
                    if (chain.Count > before) chain.RemoveRange(before, chain.Count - before);
                    if (nextId >= 0) used.Add(nextId);
                    DebugLog($"Bradley reject fold-join #{nextId} at {PositionToGrid(end)} -> {PositionToGrid(newEnd)} dot={stepDot:F2} span {beforeSpan:F0}->{newSpan:F0}");
                    continue;
                }
                if (nextId >= 0) used.Add(nextId);
                joins++;
                DebugLog($"Bradley chain +road#{nextId} pts={chain.Count} span={newSpan:F0}m cut=0 -> {PositionToGrid(chain[chain.Count - 1])}");
            }
            StripBradleyHazardPads(chain);
            CutBradleyRouteGaps(chain, 140f);
            int spliced = SpliceBradleyMonumentCorridors(chain);
            CutBradleyRouteGaps(chain, 140f);
            _bradleyLastChainUsed.Clear();
            foreach (int id in used) _bradleyLastChainUsed.Add(id);
            DebugLog($"Bradley spawn-chain roads={joins + 1} pts={chain.Count} span={BradleyRouteSpan(chain):F0}m splice={spliced} {PositionToGrid(chain[0])} -> {PositionToGrid(chain[chain.Count - 1])}");
            return chain;
        }

        private float _bradleyClearAt;
        private float _bradleyNoJoinAt;

        private void ForceBradleyOneWay(BradleyAPC bradley)
        {
            if (bradley == null || bradley.IsDestroyed) return;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var loopField = typeof(BradleyAPC).GetField("pathLooping", flags);
                if (loopField != null && loopField.FieldType == typeof(bool))
                    loopField.SetValue(bradley, false);
            }
            catch { }
            BradleyClearPathObstacles(bradley);
        }

        /// <summary>
        /// Vanilla Bradley shoots threats, not parked cars. If something sits on
        /// the hull's forward cone, paint it as the main gun target so the tank
        /// can push through a road instead of idling.
        /// </summary>
        private void BradleyClearPathObstacles(BradleyAPC bradley)
        {
            if (bradley == null || bradley.IsDestroyed) return;
            if (Time.realtimeSinceStartup < _bradleyClearAt) return;
            _bradleyClearAt = Time.realtimeSinceStartup + 3.5f;
            try
            {
                Vector3 pos = bradley.transform.position;
                Vector3 fwd = bradley.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                var hits = new List<BaseEntity>();
                Vis.Entities(pos + fwd * 10f, 14f, hits);
                BaseCombatEntity pick = null;
                float pickD = 22f * 22f;
                for (int i = 0; i < hits.Count; i++)
                {
                    var e = hits[i];
                    if (e == null || e.IsDestroyed || e == bradley) continue;
                    if (e is BradleyAPC) continue;
                    if (e is BuildingBlock) continue;
                    if (e is BuildingPrivlidge) continue;
                    var combat = e as BaseCombatEntity;
                    if (combat == null || combat.IsDead()) continue;
                    bool ok = e is BaseVehicle
                           || e is ScientistNPC
                           || e is NPCPlayer
                           || e is Barricade
                           || e is RidableHorse;
                    var player = e as BasePlayer;
                    if (player != null && !player.IsNpc)
                        ok = !player.IsSleeping();
                    if (!ok) continue;
                    Vector3 to = e.transform.position - pos;
                    to.y = 0f;
                    if (to.sqrMagnitude > pickD) continue;
                    if (Vector3.Dot(fwd, to.normalized) < 0.25f) continue;
                    pick = combat;
                    pickD = to.sqrMagnitude;
                }
                if (pick == null) return;
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var gun = typeof(BradleyAPC).GetField("mainGunTarget", flags)
                       ?? typeof(BradleyAPC).GetField("target", flags);
                if (gun != null && typeof(BaseCombatEntity).IsAssignableFrom(gun.FieldType))
                    gun.SetValue(bradley, pick);
                DebugLog($"Bradley path-clear {PositionToGrid(pos)} -> {pick.ShortPrefabName}");
            }
            catch { }
        }

        private void HoldBradleyHere(BradleyAPC bradley, Vector3 pos)
        {
            if (bradley == null || bradley.IsDestroyed) return;
            ForceBradleyOneWay(bradley);
        }

        /// <summary>
        /// Dead-end next to a harbor / fishing village / gas / supermarket / etc.
        /// Main-road ends do not qualify — those must not reverse.
        /// </summary>
        private bool IsBradleyMonumentSpur(Vector3 pos, List<Vector3> route)
        {
            try
            {
                var mons = TerrainMeta.Path?.Monuments;
                if (mons != null)
                {
                    for (int i = 0; i < mons.Count; i++)
                    {
                        var mon = mons[i];
                        if (mon == null) continue;
                        Vector3 mp = mon.transform.position;
                        float dx = mp.x - pos.x, dz = mp.z - pos.z;
                        if (dx * dx + dz * dz > 90f * 90f) continue;
                        string n = (mon.name ?? "").ToLowerInvariant();
                        if (n.IndexOf("harbor", StringComparison.Ordinal) >= 0
                            || n.IndexOf("harbour", StringComparison.Ordinal) >= 0
                            || n.IndexOf("fishing", StringComparison.Ordinal) >= 0
                            || n.IndexOf("lighthouse", StringComparison.Ordinal) >= 0
                            || n.IndexOf("gas_station", StringComparison.Ordinal) >= 0
                            || n.IndexOf("supermarket", StringComparison.Ordinal) >= 0
                            || n.IndexOf("bus_stop", StringComparison.Ordinal) >= 0
                            || n.IndexOf("busstop", StringComparison.Ordinal) >= 0
                            || n.IndexOf("warehouse", StringComparison.Ordinal) >= 0
                            || n.IndexOf("apartment", StringComparison.Ordinal) >= 0
                            || n.IndexOf("outpost", StringComparison.Ordinal) >= 0
                            || n.IndexOf("compound", StringComparison.Ordinal) >= 0
                            || n.IndexOf("junkyard", StringComparison.Ordinal) >= 0)
                            return true;
                    }
                }
            }
            catch { }

            // A short highway is not a spur. Only a real monument lot with a
            // short inbound arm counts — that was the G8 U-turn.
            float span = BradleyRouteSpan(route);
            try
            {
                if (TerrainMeta.TopologyMap != null)
                {
                    int topo = TerrainMeta.TopologyMap.GetTopology(pos);
                    if ((topo & (int)TerrainTopology.Enum.Monument) != 0 && span > 0f && span < 280f)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void NoteBradleyUsedRoad(HashSet<int> used, int id)
        {
            if (used == null || id < 0) return;
            used.Add(id);
            if (used.Count <= 2) return;
            var keep = new List<int>(used);
            used.Clear();
            used.Add(keep[keep.Count - 2]);
            used.Add(keep[keep.Count - 1]);
        }

        private List<Vector3> PickForwardNamedRoad(Vector3 here, Vector3 heading, HashSet<int> used, List<Vector3> prev, out int pickedId)
        {
            var first = PickForwardNamedRoad(here, heading, used, prev, 0.05f, out pickedId);
            if (first != null) return first;
            var side = PickForwardNamedRoad(here, heading, used, prev, -0.35f, out pickedId);
            if (side != null) return side;
            // Sharp right/left at a T (E8 north fork). Only reject an exact U-turn.
            return PickForwardNamedRoad(here, heading, used, prev, -0.82f, out pickedId);
        }

        private List<Vector3> PickForwardNamedRoad(Vector3 here, Vector3 heading, HashSet<int> used, List<Vector3> prev, float minDot, out int pickedId)
        {
            pickedId = -1;
            if (_roadCache.Count == 0) BuildRoadCache();
            heading.y = 0f;
            if (heading.sqrMagnitude < 1f) heading = Vector3.forward;
            heading.Normalize();

            var prevGrids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (prev != null)
            {
                for (int i = 0; i < prev.Count; i += 3)
                    prevGrids.Add(PositionToGrid(prev[i]));
            }

            Vector3 origin = (prev != null && prev.Count > 0) ? prev[0] : here;
            origin.y = 0f;
            var pool = new List<(int id, List<Vector3> pts, float span, float cover, float dot)>();
            for (int i = 0; i < _roadCache.Count; i++)
            {
                if (used != null && used.Contains(i)) continue;
                var road = _roadCache[i];
                if (road.Width < 4.5f) continue;
                var dry = PrepareNamedRoad(road, 200);
                if (dry.Count < 6) continue;

                float d0 = (dry[0].x - here.x) * (dry[0].x - here.x) + (dry[0].z - here.z) * (dry[0].z - here.z);
                float d1 = (dry[dry.Count - 1].x - here.x) * (dry[dry.Count - 1].x - here.x)
                         + (dry[dry.Count - 1].z - here.z) * (dry[dry.Count - 1].z - here.z);
                // Procedural Path.Road points are often 70-100m apart. 56m missed
                // the south arm at G8. 110m stays inside one grid (~146m) so it
                // cannot repeat the 320m G8→H10 off-asphalt hop.
                const float joinSq = 110f * 110f;
                bool at0 = d0 <= joinSq;
                bool at1 = d1 <= joinSq;

                var oriented = new List<Vector3>();
                if (at0 || at1)
                {
                    oriented.AddRange(dry);
                    if (at1 && (!at0 || d1 < d0))
                        oriented.Reverse();
                }
                else
                {
                    // Same painted road, different TerrainMeta id — pick up mid-span.
                    int best = -1;
                    float bestSq = 110f * 110f;
                    for (int p = 0; p < dry.Count; p++)
                    {
                        float dx = dry[p].x - here.x, dz = dry[p].z - here.z;
                        float sq = dx * dx + dz * dz;
                        if (sq < bestSq) { bestSq = sq; best = p; }
                    }
                    if (best < 0) continue;
                    var fwd = new List<Vector3>();
                    var back = new List<Vector3>();
                    for (int p = best; p < dry.Count; p++) fwd.Add(dry[p]);
                    for (int p = best; p >= 0; p--) back.Add(dry[p]);
                    float fwdDot = 0f, backDot = 0f;
                    if (fwd.Count >= 2)
                    {
                        var t = new Vector3(fwd[fwd.Count - 1].x - here.x, 0f, fwd[fwd.Count - 1].z - here.z);
                        if (t.sqrMagnitude > 1f) fwdDot = Vector3.Dot(heading, t.normalized);
                    }
                    if (back.Count >= 2)
                    {
                        var t = new Vector3(back[back.Count - 1].x - here.x, 0f, back[back.Count - 1].z - here.z);
                        if (t.sqrMagnitude > 1f) backDot = Vector3.Dot(heading, t.normalized);
                    }
                    // Longer remaining arm wins if the first step is not a U-turn.
                    // Far-end dot dropped the south highway when it curved later.
                    float fwdStep = 0f, backStep = 0f;
                    if (fwd.Count >= 2)
                    {
                        var s = new Vector3(fwd[Mathf.Min(3, fwd.Count - 1)].x - here.x, 0f, fwd[Mathf.Min(3, fwd.Count - 1)].z - here.z);
                        if (s.sqrMagnitude > 1f) fwdStep = Vector3.Dot(heading, s.normalized);
                    }
                    if (back.Count >= 2)
                    {
                        var s = new Vector3(back[Mathf.Min(3, back.Count - 1)].x - here.x, 0f, back[Mathf.Min(3, back.Count - 1)].z - here.z);
                        if (s.sqrMagnitude > 1f) backStep = Vector3.Dot(heading, s.normalized);
                    }
                    float fwdLen = BradleyPolylineLength(fwd);
                    float backLen = BradleyPolylineLength(back);
                    if (fwdLen >= backLen && fwd.Count >= 6 && fwdStep >= -0.45f) oriented = fwd;
                    else if (back.Count >= 6 && backStep >= -0.45f) oriented = back;
                    else if (fwd.Count >= 6 && fwdStep >= -0.45f) oriented = fwd;
                    else continue;
                }
                if (oriented.Count < 6) continue;
                // Destination in a harbor lot is a trap. Start-in-lot is how we leave one.
                if (IsBradleyNoGoLot(oriented[oriented.Count - 1]))
                    continue;

                Vector3 far = oriented[oriented.Count - 1];
                Vector3 toFar = new Vector3(far.x - here.x, 0f, far.z - here.z);
                if (toFar.sqrMagnitude < 80f * 80f) continue;
                float dot = Vector3.Dot(heading, toFar.normalized);
                if (dot < minDot)
                    continue;
                float span = BradleyRouteSpan(oriented);
                if (span < 140f)
                {
                    DebugLog($"Bradley reject short-join #{i} span={span:F0}m");
                    continue;
                }
                float cover = Vector3.Distance(
                    new Vector3(origin.x, 0f, origin.z),
                    new Vector3(far.x, 0f, far.z));

                int hit = 0, tot = 0;
                for (int g = 0; g < oriented.Count; g += 3)
                {
                    tot++;
                    if (prevGrids.Contains(PositionToGrid(oriented[g]))) hit++;
                }
                // Junction grids overlap the inbound road. That is the highway
                // continuing, not a U-turn. Only reject when the far end is behind.
                if (tot > 0 && hit * 2 >= tot && dot < 0.15f)
                {
                    if (Time.realtimeSinceStartup >= _bradleyNoJoinAt)
                        DebugLog($"Bradley reject overlap-join #{i} {hit}/{tot} dot={dot:F2}");
                    continue;
                }

                pool.Add((i, oriented, span, cover, dot));
            }
            if (pool.Count == 0)
            {
                if (Time.realtimeSinceStartup >= _bradleyNoJoinAt)
                {
                    _bradleyNoJoinAt = Time.realtimeSinceStartup + 8f;
                    DebugLog($"Bradley no-join at {PositionToGrid(here)} used={used?.Count ?? 0}");
                }
                return null;
            }
            pool.Sort((a, b) =>
            {
                // Longest asphalt wins forward and in reverse. Cover-first
                // made a short spur beat the highway behind the hull.
                int s = b.span.CompareTo(a.span);
                if (s != 0) return s;
                return b.cover.CompareTo(a.cover);
            });
            pickedId = pool[0].id;
            return pool[0].pts;
        }

        /// <summary>
        /// True terminus (ferry spit / cul-de-sac): walk 180–850 m back along
        /// the current asphalt and pick a third arm at that junction. The tank
        /// drives back on the road it already used — no teleport, no full-route
        /// reverse, no off-road crawl.
        /// </summary>
        private bool TryBradleyBackupJunction(
            List<Vector3> route,
            Vector3 pos,
            HashSet<int> usedRoads,
            out List<Vector3> spliced,
            out int pickedId,
            out string viaGrid)
        {
            spliced = null;
            pickedId = -1;
            viaGrid = "";
            if (route == null || route.Count < 12) return false;

            int here = FindClosestRouteIndex(route, pos);
            if (here < 0) here = route.Count - 1;
            // Mid-chain stall: treat the hull as the terminus so we measure
            // "leave this lot", not "distance to the unused O12 tail".
            bool midStall = here < route.Count - 8;
            Vector3 deadEnd = midStall ? route[here] : route[route.Count - 1];
            string deadGrid = PositionToGrid(deadEnd);
            var driven = new List<Vector3>();
            for (int p = 0; p <= here && p < route.Count; p += 2)
                driven.Add(route[p]);

            int startI = Math.Min(here, route.Count - 5) - 3;
            for (int i = startI; i >= 8; i -= 3)
            {
                Vector3 junction = route[i];
                float back = Vector3.Distance(
                    new Vector3(junction.x, 0f, junction.z),
                    new Vector3(deadEnd.x, 0f, deadEnd.z));
                if (back < 180f) continue;
                if (back > 850f) break;

                int a = Math.Max(0, i - 2);
                int b = Math.Min(route.Count - 1, i + 2);
                Vector3 heading = new Vector3(route[b].x - route[a].x, 0f, route[b].z - route[a].z);
                if (heading.sqrMagnitude < 1f) continue;

                int id = -1;
                // Side arm first (3-way). Then a mild forward that is not the spit.
                var side = PickForwardNamedRoad(junction, heading, usedRoads, driven, -0.35f, out id);
                if (side == null)
                    side = PickForwardNamedRoad(junction, heading, usedRoads, driven, 0.05f, out id);
                if (side == null || side.Count < 6) continue;

                Vector3 far = side[side.Count - 1];
                if (string.Equals(PositionToGrid(far), deadGrid, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsBradleyNoGoLot(far) || IsHarborLot(far) || IsCourtyardNearby(far, 130f))
                    continue;
                float leaveSpit = Vector3.Distance(
                    new Vector3(far.x, 0f, far.z),
                    new Vector3(deadEnd.x, 0f, deadEnd.z));
                if (leaveSpit < 250f) continue;
                if (BradleyRouteSpan(side) < 200f) continue;

                var backPts = new List<Vector3>();
                if (here > i)
                {
                    for (int p = here; p >= i; p--)
                        backPts.Add(route[p]);
                }
                else
                {
                    backPts.Add(junction);
                }

                int skip = 0;
                if (side.Count > 0 && backPts.Count > 0)
                {
                    float sx = side[0].x - backPts[backPts.Count - 1].x;
                    float sz = side[0].z - backPts[backPts.Count - 1].z;
                    if (sx * sx + sz * sz < 25f * 25f) skip = 1;
                }

                var outR = new List<Vector3>(backPts.Count + side.Count);
                outR.AddRange(backPts);
                for (int p = skip; p < side.Count; p++)
                    outR.Add(side[p]);
                if (outR.Count < 8) continue;
                if (!BradleySegmentDry(outR[0], outR[Math.Min(3, outR.Count - 1)]))
                    continue;

                spliced = outR;
                pickedId = id;
                viaGrid = PositionToGrid(junction);
                return true;
            }
            return false;
        }

        private readonly Dictionary<ulong, Timer> _bradleyRouteTimers = new Dictionary<ulong, Timer>();
        private int _lastBradleySeedId = -1;
        private readonly HashSet<int> _bradleyLastChainUsed = new HashSet<int>();


        private static float BradleyPolylineLength(List<Vector3> pts)
        {
            if (pts == null || pts.Count < 2) return 0f;
            float n = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                float dx = pts[i].x - pts[i - 1].x;
                float dz = pts[i].z - pts[i - 1].z;
                n += Mathf.Sqrt(dx * dx + dz * dz);
            }
            return n;
        }

        /// <summary>Append only the part of next that meets the chain end. A 300 m gap is not a junction.</summary>
        private bool AppendBradleyJoin(List<Vector3> chain, List<Vector3> next, int nextId)
        {
            if (chain == null || chain.Count == 0 || next == null || next.Count < 5) return false;
            Vector3 end = chain[chain.Count - 1];
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < next.Count; i++)
            {
                float dx = next[i].x - end.x, dz = next[i].z - end.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            const float maxJoin = 110f;
            if (bestSq > maxJoin * maxJoin)
            {
                DebugLog($"Bradley reject gap-join #{nextId} at {PositionToGrid(end)} — {Mathf.Sqrt(bestSq):F0}m to {PositionToGrid(next[best])}");
                return false;
            }
            int start = best;
            float ndx = next[start].x - end.x, ndz = next[start].z - end.z;
            if (ndx * ndx + ndz * ndz < 18f * 18f) start++;
            if (start >= next.Count) return true;
            if (!BradleySegmentDry(end, next[start]))
            {
                DebugLog($"Bradley reject wet-join #{nextId} at {PositionToGrid(end)}");
                return false;
            }
            for (int i = start; i < next.Count; i++)
                chain.Add(next[i]);
            return true;
        }

        private int CutBradleyRouteGaps(List<Vector3> route, float maxGap)
        {
            if (route == null || route.Count < 3) return 0;
            float maxSq = maxGap * maxGap;
            for (int i = 1; i < route.Count; i++)
            {
                float dx = route[i].x - route[i - 1].x;
                float dz = route[i].z - route[i - 1].z;
                if (dx * dx + dz * dz <= maxSq) continue;
                int drop = route.Count - i;
                string nxt = PositionToGrid(route[i]);
                route.RemoveRange(i, drop);
                DebugLog($"Bradley gap-cut chain at {PositionToGrid(route[route.Count - 1])} — dropped {drop} pts (next {nxt} was {Mathf.Sqrt(dx * dx + dz * dz):F0}m)");
                return drop;
            }
            return 0;
        }

        /// <summary>
        /// Longest painted road that meets the hull. Both directions are scored,
        /// so a reverse still takes the long arm. 70 m join — no grid hop, no teleport.
        /// </summary>
        private List<Vector3> PickLongestMeetRoad(Vector3 here, HashSet<int> used, out int pickedId)
        {
            pickedId = -1;
            if (_roadCache.Count == 0) BuildRoadCache();
            List<Vector3> best = null;
            float bestLen = 0f;
            const float joinSq = 70f * 70f;
            for (int i = 0; i < _roadCache.Count; i++)
            {
                if (used != null && used.Contains(i)) continue;
                var road = _roadCache[i];
                if (road == null || road.Points == null || road.Width < 4.5f) continue;
                var dry = PrepareNamedRoad(road, 200);
                if (dry.Count < 6) continue;
                int near = -1;
                float nearSq = joinSq;
                for (int p = 0; p < dry.Count; p++)
                {
                    float dx = dry[p].x - here.x, dz = dry[p].z - here.z;
                    float sq = dx * dx + dz * dz;
                    if (sq < nearSq) { nearSq = sq; near = p; }
                }
                if (near < 0) continue;
                var fwd = new List<Vector3>();
                for (int p = near; p < dry.Count; p++) fwd.Add(dry[p]);
                var back = new List<Vector3>();
                for (int p = near; p >= 0; p--) back.Add(dry[p]);
                float fwdLen = BradleyPolylineLength(fwd);
                float backLen = BradleyPolylineLength(back);
                var arm = fwdLen >= backLen ? fwd : back;
                float len = Mathf.Max(fwdLen, backLen);
                if (arm.Count < 6 || len < 180f) continue;
                if (len <= bestLen) continue;
                bestLen = len;
                best = arm;
                pickedId = i;
            }
            if (best != null)
                DebugLog($"Bradley longest-road #{pickedId} {PositionToGrid(here)} -> {PositionToGrid(best[best.Count - 1])} len={bestLen:F0}m pts={best.Count}");
            return best;
        }

        private bool TakeLongestReverse(BradleyAPC bradley, ref List<Vector3> route, Vector3 pos, HashSet<int> usedRoads, out int hopIndex)
        {
            hopIndex = 1;
            int id;
            var longest = PickLongestMeetRoad(pos, usedRoads, out id);
            float curLen = BradleyPolylineLength(route);
            if (longest != null && longest.Count >= 6 && BradleyPolylineLength(longest) + 30f >= curLen)
            {
                route = longest;
                AdoptBradleyRoute(ref route);
                hopIndex = FirstAwayIndex(route, pos, 40f);
                if (id >= 0 && usedRoads != null) NoteBradleyUsedRoad(usedRoads, id);
                InstallBradleyFullPath(bradley, route, hopIndex);
                return true;
            }
            if (route != null && route.Count >= 4)
            {
                route.Reverse();
                hopIndex = FindNearestRouteIndex(route, pos);
                if (hopIndex > route.Count - 4) hopIndex = 0;
                InstallBradleyFullPath(bradley, route, hopIndex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Keepalive only — do NOT rebuild currentPath every tick (that causes
        /// ArgumentOutOfRangeException when AI advances currentPathIndex past a short path).
        /// Install once, clamp index, reinstall only if path was cleared.
        /// </summary>
        private void StartBradleyPatrolRoute(BradleyAPC bradley, List<Vector3> route, int startIndex, int startRoad = -1)
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
            bool pathEnded = false;
            bool spurReversed = false;
            bool yardMode = false;
            int yardMonId = -1;
            float yardEnterAt = 0f;
            float yardCooldownUntil = 0f;
            int yardCooldownMonId = -1;
            int lastPushHull = -1;
            int lastPushDest = -1;
            int stallEscalations = 0;
            int lastSnapWp = -1;
            int lastInstallStart = -1;
            int sameSnap = 0;
            float skipUntil = 0f;
            int backupJoins = 0;
            string lastMissGrid = "";
            float lastMissAt = 0f;
            string lastLotSkipHull = "";
            var recentEnds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedRoads = new HashSet<int>();
            if (startRoad >= 0) usedRoads.Add(startRoad);
            foreach (int id in _bradleyLastChainUsed)
                usedRoads.Add(id);

            DebugLog($"Bradley lifetime armed for {lifetimeMin:F0}m (road hops like heavies)");

            int hopIndex = Mathf.Clamp(startIndex, 0, Math.Max(0, route.Count - 1));
            Vector3 lastPos = Vector3.zero;
            float lastMovedAt = Time.realtimeSinceStartup;
            bool haveLast = false;
            int stuckSkips = 0;
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
                        if (haveLast)
                        {
                            float mx = pos.x - lastPos.x;
                            float mz = pos.z - lastPos.z;
                            if (mx * mx + mz * mz > 6f * 6f)
                                lastMovedAt = Time.realtimeSinceStartup;
                        }
                        else
                        {
                            lastMovedAt = Time.realtimeSinceStartup;
                            haveLast = true;
                        }
                        lastPos = pos;
                        string gridNow = PositionToGrid(pos);

                        // Highway still has tail (O12→K7 via S7): stay on it.
                        // 150 looped yard-escape S7 → road#1 K7 every 22s and never rolled.
                        float remainHwy = 0f;
                        if (route != null && hopIndex >= 0 && hopIndex < route.Count)
                            remainHwy = Vector3.Distance(
                                new Vector3(pos.x, 0f, pos.z),
                                new Vector3(route[route.Count - 1].x, 0f, route[route.Count - 1].z));
                        bool hwyAlive = route != null && route.Count >= 16
                                        && hopIndex < route.Count - 8
                                        && remainHwy > 160f;
                        if (!yardMode && !hwyAlive && Time.realtimeSinceStartup - lastMovedAt > 18f
                            && Time.realtimeSinceStartup >= skipUntil)
                        {
                            var trap = FindMonumentScanAt(pos);
                            bool trapYard = trap != null
                                && !trap.BradleySkip
                                && (trap.Kind == "through" || trap.Kind == "courtyard"
                                    || string.Equals(trap.Family, "yard", StringComparison.OrdinalIgnoreCase))
                                && (trap.ThroughRoads == null || trap.ThroughRoads.Count == 0);
                            if (trapYard)
                            {
                                Vector3 heading = bradley.transform.forward;
                                if (route != null && hopIndex + 1 < route.Count)
                                {
                                    var ha = route[Mathf.Clamp(hopIndex, 0, route.Count - 1)];
                                    var hb = route[Mathf.Min(hopIndex + 3, route.Count - 1)];
                                    heading = new Vector3(hb.x - ha.x, 0f, hb.z - ha.z);
                                }
                                int leaveId = -1;
                                var leave = PickForwardNamedRoad(pos, heading, usedRoads, route, -0.15f, out leaveId);
                                if (leave == null)
                                    leave = PickForwardNamedRoad(pos, heading, new HashSet<int>(), null, 0.10f, out leaveId);
                                if (leave != null && leave.Count >= 8)
                                {
                                    TrimBradleyYardTail(leave);
                                    StripBradleyYardInterior(leave);
                                    if (leave.Count >= 8)
                                    {
                                        route = leave;
                                        AdoptBradleyRoute(ref route);
                                        hopIndex = FirstAwayIndex(route, pos, 50f);
                                        if (leaveId >= 0) NoteBradleyUsedRoad(usedRoads, leaveId);
                                        sameSnap = 0;
                                        stallEscalations = 0;
                                        skipUntil = Time.realtimeSinceStartup + 22f;
                                        lastMovedAt = Time.realtimeSinceStartup;
                                        InstallBradleyFullPath(bradley, route, hopIndex);
                                        DebugLog($"Bradley yard-escape {gridNow} mon={trap.Prefab} road#{leaveId} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                        return;
                                    }
                                }
                                int skirtWp;
                                if (TakeLongestReverse(bradley, ref route, pos, usedRoads, out skirtWp))
                                {
                                    hopIndex = skirtWp;
                                    sameSnap = 0;
                                    stallEscalations = 0;
                                    skipUntil = Time.realtimeSinceStartup + 18f;
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    DebugLog($"Bradley yard-longest {gridNow} mon={trap.Prefab} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                    return;
                                }
                            }
                        }

                        // Yard mode: follow the monument lane. Road mode: named Path.Roads.
                        // Enter at a gate, exit at the other end, then pick the next road.
                        if (!yardMode)
                        {
                            var enterMon = FindMonumentScanAt(pos);
                            bool highwayAlive = route.Count >= 20 && hopIndex < route.Count - 8;
                            if (enterMon != null && BradleyCanTourMonument(enterMon)
                                && !highwayAlive
                                && !(enterMon.Id == yardCooldownMonId && Time.realtimeSinceStartup < yardCooldownUntil)
                                && !(enterMon.ThroughRoads != null && enterMon.ThroughRoads.Count > 0 && OnMonumentThroughRoad(pos)))
                            {
                                var lane = GetMonumentLane(enterMon);
                                if (LaneHasTwoGates(lane))
                                {
                                    float dStart = (lane[0].x - pos.x) * (lane[0].x - pos.x) + (lane[0].z - pos.z) * (lane[0].z - pos.z);
                                    float dEnd = (lane[lane.Count - 1].x - pos.x) * (lane[lane.Count - 1].x - pos.x)
                                               + (lane[lane.Count - 1].z - pos.z) * (lane[lane.Count - 1].z - pos.z);
                                    if (dEnd < dStart)
                                        lane.Reverse();
                                    // Launch_site r=216 swallows the ring road. Only enter
                                    // when the hull is on the lane / a gate — not "inside radius".
                                    if (NearMonumentLane(lane, pos, 55f))
                                    {
                                        route = lane;
                                        hopIndex = FirstAwayIndex(route, pos, 80f);
                                        yardMode = true;
                                        yardMonId = enterMon.Id;
                                        yardEnterAt = Time.realtimeSinceStartup;
                                        pathEnded = false;
                                        spurReversed = false;
                                        sameSnap = 0;
                                        skipUntil = Time.realtimeSinceStartup + 50f;
                                        int enterHull = FindClosestRouteIndex(route, pos);
                                        InstallBradleyFullPath(bradley, route, enterHull);
                                        DebugLog($"Bradley YARD enter {enterMon.Name} @{gridNow} lane={route.Count} hull={enterHull} -> {PositionToGrid(route[route.Count - 1])}");
                                        lastMovedAt = Time.realtimeSinceStartup;
                                        return;
                                    }
                                }
                            }
                        }
                        else
                        {
                            MonumentScanEntry yardEnt = null;
                            for (int yi = 0; yi < _monumentScan.Count; yi++)
                                if (_monumentScan[yi] != null && _monumentScan[yi].Id == yardMonId)
                                { yardEnt = _monumentScan[yi]; break; }
                            float ydx = yardEnt != null ? pos.x - yardEnt.X : 0f;
                            float ydz = yardEnt != null ? pos.z - yardEnt.Z : 0f;
                            float yRad = yardEnt != null ? yardEnt.Radius * 1.85f : 160f;
                            bool inYard = yardEnt != null && (ydx * ydx + ydz * ydz) <= yRad * yRad;
                            int needHops = Math.Max(5, route.Count / 4);
                            bool progressed = hopIndex >= needHops;
                            float exitDx = pos.x - route[route.Count - 1].x;
                            float exitDz = pos.z - route[route.Count - 1].z;
                            bool atFarGate = progressed && (exitDx * exitDx + exitDz * exitDz) < 45f * 45f;
                            bool leftYard = progressed && !inYard;
                            bool stallExit = Time.realtimeSinceStartup - lastMovedAt > 28f
                                && Time.realtimeSinceStartup - yardEnterAt > 28f;
                            if (atFarGate || leftYard || stallExit)
                            {
                                Vector3 heading = Vector3.forward;
                                if (route.Count >= 2)
                                {
                                    var a = route[route.Count - 2];
                                    var b = route[route.Count - 1];
                                    heading = new Vector3(b.x - a.x, 0f, b.z - a.z);
                                }
                                int nextId = -1;
                                var next = PickForwardNamedRoad(pos, heading, usedRoads, route, out nextId);
                                if (next == null)
                                    next = PickForwardNamedRoad(pos, heading, new HashSet<int>(), route, out nextId);
                                if (next != null && next.Count >= 5)
                                    TrimBradleyYardTail(next);
                                if (next != null && next.Count >= 5)
                                {
                                    route = next;
                                    hopIndex = FirstAwayIndex(route, pos, 40f);
                                    if (nextId >= 0) NoteBradleyUsedRoad(usedRoads, nextId);
                                    yardCooldownMonId = yardMonId;
                                    yardCooldownUntil = Time.realtimeSinceStartup + 95f;
                                    yardMode = false;
                                    yardMonId = -1;
                                    pathEnded = false;
                                    spurReversed = false;
                                    sameSnap = 0;
                                    skipUntil = Time.realtimeSinceStartup + 20f;
                                    InstallBradleyFullPath(bradley, route, hopIndex);
                                    DebugLog($"Bradley ROAD exit {gridNow} road#{nextId} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    return;
                                }
                            }
                        }

                        Vector3 dest = route[Mathf.Clamp(hopIndex, 0, route.Count - 1)];
                        float dx = pos.x - dest.x;
                        float dz = pos.z - dest.z;
                        bool inMonument = IsMonumentRoadPoint(pos);
                        float nearSq = inMonument ? 16f * 16f : 28f * 28f;
                        bool nearDest = dx * dx + dz * dz < nearSq;
                        if (!pathEnded && nearDest && hopIndex < route.Count - 1)
                        {
                            Vector3 gapNxt = route[hopIndex + 1];
                            float gap = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(gapNxt.x, 0f, gapNxt.z));
                            if (gap > 140f)
                            {
                                int drop = route.Count - (hopIndex + 1);
                                DebugLog($"Bradley gap-cut {gridNow} wp={hopIndex}/{route.Count} next={PositionToGrid(gapNxt)} gap={gap:F0}m — stay on asphalt");
                                route.RemoveRange(hopIndex + 1, drop);
                                pathEnded = false;
                                lastMovedAt = Time.realtimeSinceStartup;
                                InstallBradleyFullPath(bradley, route, hopIndex);
                                return;
                            }
                        }
                        // Closest node only. "Furthest within 80m" on a folded
                        // backup path (F2→F4 over the same asphalt) jumped to wp=15
                        // while the hull was still at F2, then snap installed a path
                        // that started at F4.
                        if (!pathEnded && Time.realtimeSinceStartup >= skipUntil)
                        {
                            int caught = FindClosestRouteIndex(route, pos);
                            float cx = route[caught].x - pos.x, cz = route[caught].z - pos.z;
                            if (caught > hopIndex + 1 && caught <= hopIndex + 3
                                && cx * cx + cz * cz < 40f * 40f)
                            {
                                hopIndex = caught;
                                dest = route[hopIndex];
                                lastMovedAt = Time.realtimeSinceStartup;
                                DebugLog($"Bradley hop-catch {gridNow} wp={hopIndex}/{route.Count}");
                            }
                        }
                        if (!pathEnded && nearDest && hopIndex < route.Count - 1)
                        {
                            Vector3 nxt = route[hopIndex + 1];
                            bool nxtYard = false;
                            int lookTo = Mathf.Min(hopIndex + 8, route.Count - 1);
                            for (int li = hopIndex + 1; li <= lookTo; li++)
                            {
                                var lookMon = FindMonumentScanAt(route[li]);
                                bool yardKind = BradleyCanTourMonument(lookMon);
                                if (yardKind)
                                { nxtYard = true; nxt = route[li]; break; }
                            }
                            if (nxtYard && !yardMode && !(route.Count >= 20 && hopIndex < route.Count - 8))
                            {
                                var yardMon = FindMonumentScanAt(nxt) ?? FindMonumentScanAt(pos);
                                if (yardMon != null && yardMon.BradleySkip) yardMon = null;
                                if (yardMon != null && yardMon.Id == yardCooldownMonId && Time.realtimeSinceStartup < yardCooldownUntil)
                                    yardMon = null;
                                if (yardMon != null && yardMon.ThroughRoads != null && yardMon.ThroughRoads.Count > 0 && OnMonumentThroughRoad(pos))
                                    yardMon = null;
                                var yardLane = yardMon != null ? GetMonumentLane(yardMon) : null;
                                if (yardLane != null && !NearMonumentLane(yardLane, pos, 55f))
                                    yardLane = null;
                                if (yardLane != null && LaneHasTwoGates(yardLane))
                                {
                                    float dStart = (yardLane[0].x - pos.x) * (yardLane[0].x - pos.x)
                                                 + (yardLane[0].z - pos.z) * (yardLane[0].z - pos.z);
                                    float dEnd = (yardLane[yardLane.Count - 1].x - pos.x) * (yardLane[yardLane.Count - 1].x - pos.x)
                                               + (yardLane[yardLane.Count - 1].z - pos.z) * (yardLane[yardLane.Count - 1].z - pos.z);
                                    if (dEnd < dStart) yardLane.Reverse();
                                    route = yardLane;
                                    hopIndex = FirstAwayIndex(route, pos, 80f);
                                    yardMode = true;
                                    yardMonId = yardMon.Id;
                                    yardEnterAt = Time.realtimeSinceStartup;
                                    pathEnded = false;
                                    spurReversed = false;
                                    sameSnap = 0;
                                    skipUntil = Time.realtimeSinceStartup + 50f;
                                    int lookHull = FindClosestRouteIndex(route, pos);
                                    InstallBradleyFullPath(bradley, route, lookHull);
                                    DebugLog($"Bradley YARD enter {yardMon.Name} @{gridNow} lane={route.Count} hull={lookHull} -> {PositionToGrid(route[route.Count - 1])}");
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    return;
                                }
                                // No lane on file: keep driving the chain through the gate.
                                hopIndex++;
                                dest = route[hopIndex];
                                DebugLog($"Bradley road HOP {hopIndex}/{route.Count} {gridNow} through-yard -> {PositionToGrid(dest)}");
                                lastMovedAt = Time.realtimeSinceStartup;
                            }
                            else
                            {
                                hopIndex++;
                                dest = route[hopIndex];
                                DebugLog($"Bradley road HOP {hopIndex}/{route.Count} {gridNow} -> {PositionToGrid(dest)}");
                                lastMovedAt = Time.realtimeSinceStartup;
                            }
                        }
                        else if (!pathEnded && hopIndex < route.Count - 1
                            && Time.realtimeSinceStartup - lastMovedAt > 20f)
                        {
                            if (Time.realtimeSinceStartup < skipUntil)
                                return;
                            if (yardMode && Time.realtimeSinceStartup - yardEnterAt < 45f)
                                return;
                            int snap = FindClosestRouteIndex(route, pos);
                            // Never rewind past a committed hop, but do not leap
                            // ahead of the hull on a folded backup path.
                            if (snap < hopIndex)
                                snap = hopIndex;
                            else if (snap > hopIndex + 3)
                                snap = hopIndex;
                            if (Time.realtimeSinceStartup < skipUntil)
                                return;
                            if (yardMode && Time.realtimeSinceStartup - yardEnterAt < 45f)
                                return;
                            hopIndex = snap;
                            lastMovedAt = Time.realtimeSinceStartup;
                            if (snap == lastSnapWp) sameSnap++;
                            else { sameSnap = 1; lastSnapWp = snap; }

                            float monDist;
                            string monName = ClosestMonumentLabel(pos, out monDist);
                            if ((monName ?? "").IndexOf("radtown", StringComparison.OrdinalIgnoreCase) >= 0
                                && monDist < 140f && sameSnap >= 2)
                            {
                                Vector3 away = bradley.transform.forward;
                                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                                int turnId;
                                var turned = PickForwardNamedRoad(pos, -away, usedRoads, route, -0.2f, out turnId);
                                if (turned != null && turned.Count >= 6)
                                {
                                    route = turned;
                                    hopIndex = 1;
                                    pathEnded = false;
                                    sameSnap = 0;
                                    if (turnId >= 0) NoteBradleyUsedRoad(usedRoads, turnId);
                                    InstallBradleyFullPath(bradley, route, 0);
                                    DebugLog($"Bradley gate-turn {gridNow} mon={monName} road#{turnId} -> {PositionToGrid(route[route.Count - 1])}");
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    return;
                                }
                                hopIndex = Mathf.Min(hopIndex + 4, route.Count - 1);
                                sameSnap = 0;
                                InstallBradleyFullPath(bradley, route, hopIndex);
                                DebugLog($"Bradley gate-pass {gridNow} mon={monName} wp={hopIndex}/{route.Count}");
                                lastMovedAt = Time.realtimeSinceStartup;
                                return;
                            }
                            bool roadside = BradleySkipLotName((monName ?? "").ToLowerInvariant());
                            if (roadside) monDist = 9999f;
                            bool harbor = !roadside && (IsHarborLot(pos) || HarborLotName((monName ?? "").ToLowerInvariant()));
                            // Inside the courtyard circle — not "powerplant 130m that way" from a substation.
                            var scanMon = FindMonumentScanAt(pos);
                            bool yard = !roadside && scanMon != null
                                && string.Equals(scanMon.Kind, "courtyard", StringComparison.OrdinalIgnoreCase);
                            bool onThrough = OnMonumentThroughRoad(pos);
                            bool inLot = (harbor || yard || (!roadside && (IsBradleyNoGoLot(pos) || (inMonument && monDist < 70f))))
                                         && !onThrough;
                            bool nearPathEnd = snap >= route.Count - 4;
                            bool midChain = snap < route.Count - 6 && route.Count >= 20;
                            // Lot-skip is a last resort when the hull is in a dead yard.
                            // S7 power_sub next to powerplant was matching IsCourtyardNearby(130)
                            // and then installing a path that started at Q5 while the tank sat still.
                            bool canSkip = inLot && !roadside && !onThrough && sameSnap >= 2
                                           && !midChain
                                           && lastLotSkipHull != gridNow;
                            if (canSkip)
                            {
                                int jump = -1;
                                for (int i = snap + 8; i < route.Count; i++)
                                {
                                    if (IsHarborLot(route[i]) || IsBradleyNoGoLot(route[i]))
                                        continue;
                                    var jm = FindMonumentScanAt(route[i]);
                                    if (jm != null && string.Equals(jm.Kind, "courtyard", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    float jx = route[i].x - pos.x, jz = route[i].z - pos.z;
                                    if (jx * jx + jz * jz < 80f * 80f) continue;
                                    if (jx * jx + jz * jz > 220f * 220f) continue;
                                    if (PositionToGrid(route[i]) == gridNow) continue;
                                    jump = i;
                                    break;
                                }
                                if (jump > snap)
                                {
                                    hopIndex = jump;
                                    sameSnap = 0;
                                    reinstalls = 0;
                                    lastLotSkipHull = gridNow;
                                    skipUntil = Time.realtimeSinceStartup + 35f;
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    int installAt = FindClosestRouteIndex(route, pos);
                                    InstallBradleyFullPath(bradley, route, installAt);
                                    DebugLog($"Bradley lot-skip {gridNow} -> {PositionToGrid(route[hopIndex])} wp={hopIndex}/{route.Count}");
                                    return;
                                }
                            }
                            // Mid-chain stall on a roadside prop (stables, power_sub, gas)
                            // is the same problem as a terminus: back up one junction.
                            // Mid-chain P6: remaining H8→O12 tail is 60+ nodes. A backup
                            // junction walks BACKWARD and U-turns onto road #11 (K7).
                            // Only splice a new road when this highway is actually dying.
                            float remain = 0f;
                            if (hopIndex >= 0 && hopIndex < route.Count - 1)
                                remain = Vector3.Distance(
                                    new Vector3(pos.x, 0f, pos.z),
                                    new Vector3(route[route.Count - 1].x, 0f, route[route.Count - 1].z));
                            bool highwayLeft = midChain && hopIndex < route.Count - 10 && remain > 220f;
                            bool subLot = roadside || (monName ?? "").IndexOf("power_sub", StringComparison.OrdinalIgnoreCase) >= 0
                                          || (monName ?? "").IndexOf("powersub", StringComparison.OrdinalIgnoreCase) >= 0
                                          || (monName ?? "").IndexOf("trainyard", StringComparison.OrdinalIgnoreCase) >= 0
                                          || (monName ?? "").IndexOf("warehouse", StringComparison.OrdinalIgnoreCase) >= 0;
                            int hullNow = FindClosestRouteIndex(route, pos);
                            bool destStuck = hopIndex <= hullNow + 2;
                            // Dest advancing 79→101 still I8: key off grid, not waypoint.
                            if (sameSnap >= 2 && hopIndex < route.Count - 5
                                && (destStuck || Time.realtimeSinceStartup - lastMovedAt > 16f))
                            {
                                int hullWp = FindClosestRouteIndex(route, pos);
                                int jump = Mathf.Min(hullWp + 12, route.Count - 1);
                                var pinMon = FindMonumentScanAt(pos);
                                if (pinMon != null)
                                {
                                    string pn = ((pinMon.Prefab ?? "") + " " + (pinMon.Name ?? "")).ToLowerInvariant();
                                    if (pn.Contains("swamp") || string.Equals(pinMon.Kind, "skip", StringComparison.OrdinalIgnoreCase))
                                        pinMon = null;
                                }
                                float need = 90f;
                                if (pinMon != null) need = Mathf.Max(need, pinMon.Radius + 24f);
                                for (int k = hullWp + 3; k < route.Count; k++)
                                {
                                    float jx = route[k].x - pos.x, jz = route[k].z - pos.z;
                                    if (jx * jx + jz * jz < need * need) continue;
                                    if (!BradleyPointDry(route[k])) continue;
                                    if (pinMon != null)
                                    {
                                        float mx = route[k].x - pinMon.X, mz = route[k].z - pinMon.Z;
                                        float rr = pinMon.Radius + 20f;
                                        if (mx * mx + mz * mz <= rr * rr) continue;
                                    }
                                    jump = k;
                                    break;
                                }
                                hopIndex = jump;
                                sameSnap = 0;
                                skipUntil = Time.realtimeSinceStartup + 16f;
                                lastMovedAt = Time.realtimeSinceStartup;
                                // Lighthouse, power_sub and swamp were warping the hull
                                // P18->O18, L14->M13, M12->K10. Only a blocked lot may teleport.
                                string lot = (monName ?? "").ToLowerInvariant();
                                bool blockedLot = lot.Contains("radtown") || lot.Contains("junkyard")
                                    || lot.Contains("excavator") || lot.Contains("desert_military");
                                if (blockedLot)
                                {
                                    Vector3 away = bradley.transform.forward;
                                    if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                                    int turnId;
                                    var turned = PickForwardNamedRoad(pos, -away, usedRoads, route, -0.2f, out turnId);
                                    if (turned != null && turned.Count >= 6)
                                    {
                                        route = turned;
                                        hopIndex = 1;
                                        if (turnId >= 0) NoteBradleyUsedRoad(usedRoads, turnId);
                                        InstallBradleyFullPath(bradley, route, 0);
                                        DebugLog($"Bradley gate-turn {gridNow} mon={monName} road#{turnId} -> {PositionToGrid(route[route.Count - 1])}");
                                    }
                                    else
                                    {
                                        int longWp;
                                        if (TakeLongestReverse(bradley, ref route, pos, usedRoads, out longWp))
                                        {
                                            hopIndex = longWp;
                                            DebugLog($"Bradley gate-turn longest {gridNow} mon={monName} -> {PositionToGrid(route[route.Count - 1])}");
                                        }
                                    }
                                }
                                else
                                {
                                    hopIndex = Mathf.Min(hullWp + 8, route.Count - 1);
                                    var shove = bradley.GetComponent<BradleyUnstick>();
                                    if (shove != null) shove.Kick(route[hopIndex]);
                                    InstallBradleyFullPath(bradley, route, hopIndex);
                                    DebugLog($"Bradley roadside-pass {gridNow} mon={monName} wp={hopIndex}/{route.Count}");
                                }
                                return;
                            }
                            if (sameSnap >= 3 && backupJoins < 3 && midChain && !highwayLeft)
                            {
                                List<Vector3> stuckBackup = null;
                                int stuckId = -1;
                                string stuckVia = "";
                                bool tryBackup = lastMissGrid != gridNow
                                    || (Time.realtimeSinceStartup - lastMissAt) > 90f;
                                if (tryBackup && TryBradleyBackupJunction(route, pos, usedRoads, out stuckBackup, out stuckId, out stuckVia)
                                    && stuckBackup != null && stuckBackup.Count >= 8)
                                {
                                    backupJoins++;
                                    route = stuckBackup;
                                    AdoptBradleyRoute(ref route);
                                    hopIndex = FirstAwayIndex(route, pos, 70f);
                                    if (stuckId >= 0) NoteBradleyUsedRoad(usedRoads, stuckId);
                                    spurReversed = false;
                                    pathEnded = false;
                                    sameSnap = 0;
                                    reinstalls = 0;
                                    skipUntil = Time.realtimeSinceStartup + 45f;
                                    InstallBradleyFullPath(bradley, route, hopIndex);
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    DebugLog($"Bradley stall-backup {gridNow} mon={monName} via {stuckVia} road#{stuckId} start={hopIndex} -> {PositionToGrid(route[route.Count - 1])}");
                                    return;
                                }
                                if (tryBackup)
                                {
                                    lastMissGrid = gridNow;
                                    lastMissAt = Time.realtimeSinceStartup;
                                    DebugLog($"Bradley stall-backup miss {gridNow} mon={monName} d={monDist:F0}");
                                }
                                if (midChain && hopIndex < route.Count - 4)
                                {
                                    int hullWp = FindClosestRouteIndex(route, pos);
                                    int pushDest = Mathf.Min(Mathf.Max(hopIndex, hullWp + 1), route.Count - 1);
                                    if (hullWp == lastPushHull && pushDest == lastPushDest)
                                    {
                                        stallEscalations++;
                                        Vector3 headingNow = Vector3.forward;
                                        if (hullWp + 1 < route.Count)
                                        {
                                            var ha = route[hullWp];
                                            var hb = route[Mathf.Min(hullWp + 2, route.Count - 1)];
                                            headingNow = new Vector3(hb.x - ha.x, 0f, hb.z - ha.z);
                                        }
                                        // 1) longer skip on this highway
                                        // 2) side road from the HULL (not a junction 300m back)
                                        // 3) 18m paved nudge so the APC leaves the pothole
                                        if (stallEscalations >= 3)
                                        {
                                            int sideId = -1;
                                            var side = PickForwardNamedRoad(pos, headingNow, usedRoads, route, -0.20f, out sideId);
                                            if (side == null)
                                                side = PickForwardNamedRoad(pos, headingNow, new HashSet<int>(), route, 0.15f, out sideId);
                                            if (side != null && side.Count >= 8 && BradleyRouteSpan(side) >= 160f)
                                            {
                                                TrimBradleyYardTail(side);
                                                route = side;
                                                hopIndex = FirstAwayIndex(route, pos, 50f);
                                                if (sideId >= 0) NoteBradleyUsedRoad(usedRoads, sideId);
                                                lastPushHull = -1;
                                                lastPushDest = -1;
                                                stallEscalations = 0;
                                                sameSnap = 0;
                                                skipUntil = Time.realtimeSinceStartup + 20f;
                                                lastMovedAt = Time.realtimeSinceStartup;
                                                InstallBradleyFullPath(bradley, route, 0);
                                                DebugLog($"Bradley stall-side {gridNow} road#{sideId} pts={route.Count} -> {PositionToGrid(route[route.Count - 1])}");
                                                return;
                                            }
                                        }
                                        if (stallEscalations >= 4)
                                        {
                                            var shove = bradley.GetComponent<BradleyUnstick>();
                                            if (shove != null) shove.Kick();
                                            skipUntil = Time.realtimeSinceStartup + 8f;
                                            lastMovedAt = Time.realtimeSinceStartup;
                                            DebugLog($"Bradley stall-shove {gridNow} wp={hopIndex}/{route.Count}");
                                            return;
                                        }
                                        int step = stallEscalations <= 1 ? 6 : 12;
                                        int jump = Mathf.Min(hullWp + step, route.Count - 1);
                                        int pick = jump;
                                        for (int j = hullWp + 3; j <= jump; j++)
                                        {
                                            if (j >= route.Count) break;
                                            if (!BradleyPointDry(route[j])) continue;
                                            pick = j;
                                        }
                                        hopIndex = Mathf.Max(pick, hullWp + 3);
                                        lastPushHull = hullWp;
                                        lastPushDest = hopIndex;
                                        sameSnap = 0;
                                        skipUntil = Time.realtimeSinceStartup + 16f;
                                        lastMovedAt = Time.realtimeSinceStartup;
                                        InstallBradleyFullPathSkipping(bradley, route, hullWp, hopIndex);
                                        DebugLog($"Bradley stall-skip {gridNow} e={stallEscalations} hull={hullWp} wp={hopIndex}/{route.Count} -> {PositionToGrid(route[hopIndex])}");
                                        return;
                                    }
                                    stallEscalations = 0;
                                    hopIndex = pushDest;
                                    lastPushHull = hullWp;
                                    lastPushDest = pushDest;
                                    sameSnap = 0;
                                    skipUntil = Time.realtimeSinceStartup + 16f;
                                    InstallBradleyFullPath(bradley, route, hullWp);
                                    DebugLog($"Bradley road-push {gridNow} hull={hullWp} dest={hopIndex}/{route.Count} -> {PositionToGrid(route[hopIndex])}");
                                    return;
                                }
                                if (!spurReversed && hopIndex >= 6 && !midChain)
                                {
                                    int longWp;
                                    if (TakeLongestReverse(bradley, ref route, pos, usedRoads, out longWp))
                                    {
                                        hopIndex = longWp;
                                        spurReversed = true;
                                        pathEnded = false;
                                        sameSnap = 0;
                                        reinstalls = 0;
                                        skipUntil = Time.realtimeSinceStartup + 45f;
                                        lastMovedAt = Time.realtimeSinceStartup;
                                        DebugLog($"Bradley stall-backup longest {gridNow} start={hopIndex} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                        return;
                                    }
                                }
                            }
                            bool leaveLot = nearPathEnd || ((harbor || yard) && !midChain) || (inLot && !midChain) || (sameSnap >= 4 && !midChain);
                            if (leaveLot)
                            {
                                Vector3 heading = bradley.transform.forward;
                                if (snap + 1 < route.Count)
                                {
                                    var a = route[snap];
                                    var b = route[Mathf.Min(snap + 3, route.Count - 1)];
                                    heading = new Vector3(b.x - a.x, 0f, b.z - a.z);
                                }
                                int exitId = -1;
                                // prev=route blocks inbound U-turn; usedRoads blocks the last arm.
                                List<Vector3> exit = PickForwardNamedRoad(pos, heading, usedRoads, route, -1f, out exitId);
                                if (exit == null)
                                    exit = PickForwardNamedRoad(pos, heading, usedRoads, route, out exitId);
                                if (exit != null && exit.Count >= 6)
                                {
                                    route = exit;
                                    hopIndex = 0;
                                    if (exitId >= 0) NoteBradleyUsedRoad(usedRoads, exitId);
                                    spurReversed = false;
                                    reinstalls = 0;
                                    sameSnap = 0;
                                    pathEnded = false;
                                    InstallBradleyFullPath(bradley, route, 0);
                                    DebugLog($"Bradley monument-exit {gridNow} mon={monName} d={monDist:F0} road#{exitId} -> {PositionToGrid(route[route.Count - 1])}");
                                }
                                else if (!spurReversed && !nearPathEnd)
                                {
                                    int longWp;
                                    if (TakeLongestReverse(bradley, ref route, pos, usedRoads, out longWp))
                                    {
                                        hopIndex = longWp;
                                        spurReversed = true;
                                        usedRoads.Clear();
                                        sameSnap = 0;
                                        reinstalls = 0;
                                        pathEnded = false;
                                        DebugLog($"Bradley lot-escape longest {gridNow} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                    }
                                }
                                else if (nearPathEnd)
                                {
                                    pathEnded = true;
                                    DebugLog($"Bradley monument hold {gridNow} — no exit road");
                                }
                                else
                                {
                                    hopIndex = Mathf.Min(snap + 1, route.Count - 1);
                                    InstallBradleyFullPath(bradley, route, hopIndex);
                                    DebugLog($"Bradley snap-continue {gridNow} wp={hopIndex}/{route.Count}");
                                }
                            }
                            else if (reinstalls < 8 && hopIndex >= 6)
                            {
                                int hullWp = FindClosestRouteIndex(route, pos);
                                hopIndex = Mathf.Max(hopIndex, hullWp);
                                int skipTo = SkipBradleyCourtyardAhead(route, pos, hopIndex);
                                if (skipTo > hopIndex + 2)
                                {
                                    hopIndex = skipTo;
                                    DebugLog($"Bradley skip-yard {gridNow} dest={hopIndex}/{route.Count} -> {PositionToGrid(route[hopIndex])}");
                                }
                                if (hopIndex <= hullWp)
                                    hopIndex = Mathf.Min(hullWp + 1, route.Count - 1);
                                if (hullWp == lastInstallStart)
                                    return;
                                reinstalls++;
                                lastInstallStart = hullWp;
                                lastSnapWp = hullWp;
                                InstallBradleyFullPath(bradley, route, hullWp);
                                DebugLog($"Bradley snap {gridNow} hull={hullWp} dest={hopIndex}/{route.Count} on-road mon={monName} d={monDist:F0}");
                            }
                        }

                        ForceBradleyOneWay(bradley);

                        Vector3 roadEnd = route[route.Count - 1];
                        float ex = pos.x - roadEnd.x, ez = pos.z - roadEnd.z;
                        bool atRealEnd = (ex * ex + ez * ez) < 40f * 40f;
                        if (!pathEnded && atRealEnd)
                        {
                            Vector3 heading = Vector3.forward;
                            if (route.Count >= 2)
                            {
                                var a = route[route.Count - 2];
                                heading = new Vector3(roadEnd.x - a.x, 0f, roadEnd.z - a.z);
                            }
                            int nextId = -1;
                            List<Vector3> next = null;
                            if (IsBradleyNoGoLot(pos))
                                next = PickForwardNamedRoad(pos, heading, new HashSet<int>(), null, -1f, out nextId);
                            if (next == null)
                                next = PickForwardNamedRoad(pos, heading, usedRoads, route, out nextId);
                            if (next == null)
                            {
                                // Keep prev=route so overlap rejects the inbound U-turn (J14→E9 on #3).
                                next = PickForwardNamedRoad(pos, heading, new HashSet<int>(), route, -0.82f, out nextId);
                            }
                            if (next != null && next.Count >= 5)
                            {
                                TrimBradleyYardTail(next);
                                if (next.Count < 5) next = null;
                            }
                            if (next != null && nextId >= 0 && usedRoads != null && usedRoads.Contains(nextId))
                                next = null;
                            if (next != null && next.Count >= 5)
                            {
                                route = next;
                                hopIndex = FirstAwayIndex(route, pos, 70f);
                                if (nextId >= 0) NoteBradleyUsedRoad(usedRoads, nextId);
                                spurReversed = false;
                                pathEnded = false;
                                DebugLog($"Bradley FORWARD road#{nextId} {gridNow} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                int hullFwd = FindClosestRouteIndex(route, pos);
                                InstallBradleyFullPath(bradley, route, hullFwd);
                                lastMovedAt = Time.realtimeSinceStartup;
                            }
                            else if (!spurReversed && IsBradleyMonumentSpur(pos, route))
                            {
                                int longWp;
                                if (TakeLongestReverse(bradley, ref route, pos, usedRoads, out longWp))
                                {
                                    hopIndex = longWp;
                                    spurReversed = true;
                                    DebugLog($"Bradley SPUR longest at {gridNow} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                    lastMovedAt = Time.realtimeSinceStartup;
                                }
                            }
                            else
                            {
                                List<Vector3> backup = null;
                                int backupId = -1;
                                string viaGrid = "";
                                if (backupJoins < 3
                                    && TryBradleyBackupJunction(route, pos, usedRoads, out backup, out backupId, out viaGrid)
                                    && backup != null && backup.Count >= 8)
                                {
                                    backupJoins++;
                                    route = backup;
                                    AdoptBradleyRoute(ref route);
                                    hopIndex = FirstAwayIndex(route, pos, 55f);
                                    if (backupId >= 0) NoteBradleyUsedRoad(usedRoads, backupId);
                                    spurReversed = false;
                                    pathEnded = false;
                                    sameSnap = 0;
                                    reinstalls = 0;
                                    skipUntil = Time.realtimeSinceStartup + 45f;
                                    InstallBradleyFullPath(bradley, route, 0);
                                    lastMovedAt = Time.realtimeSinceStartup;
                                    DebugLog($"Bradley backup-junction {gridNow} via {viaGrid} road#{backupId} -> {PositionToGrid(route[route.Count - 1])} pts={route.Count}");
                                }
                                else
                                {
                                    pathEnded = true;
                                    HoldBradleyHere(bradley, pos);
                                    DebugLog($"Bradley no forward road at {gridNow} — hold on asphalt (no off-road extend)");
                                }
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

        private static float BradleyRouteSpan(List<Vector3> route)
        {
            if (route == null || route.Count < 2) return 0f;
            return Vector3.Distance(
                new Vector3(route[0].x, 0f, route[0].z),
                new Vector3(route[route.Count - 1].x, 0f, route[route.Count - 1].z));
        }

        private static List<Vector3> StraightenBradleyRoute(List<Vector3> route)
        {
            if (route == null || route.Count < 4) return route ?? new List<Vector3>();
            var outPts = new List<Vector3>(route.Count);
            outPts.Add(route[0]);
            for (int i = 1; i < route.Count; i++)
            {
                var p = route[i];
                bool back = false;
                int start = Mathf.Max(0, outPts.Count - 8);
                for (int k = start; k < outPts.Count - 1; k++)
                {
                    float dx = outPts[k].x - p.x;
                    float dz = outPts[k].z - p.z;
                    if (dx * dx + dz * dz < 28f * 28f) { back = true; break; }
                }
                if (back) continue;
                if (outPts.Count >= 2)
                {
                    var a = outPts[outPts.Count - 2];
                    var b = outPts[outPts.Count - 1];
                    float vx = b.x - a.x, vz = b.z - a.z;
                    float wx = p.x - b.x, wz = p.z - b.z;
                    if (vx * wx + vz * wz < 0f)
                    {
                        float incoming = vx * vx + vz * vz;
                        float outgoing = wx * wx + wz * wz;
                        if (incoming > 20f * 20f && outgoing > 20f * 20f)
                            continue;
                    }
                }
                outPts.Add(p);
            }
            if (outPts.Count < 4) return route;
            return outPts;
        }

        private static int FindClosestRouteIndex(List<Vector3> route, Vector3 pos)
        {
            int nearest = 0;
            float best = float.MaxValue;
            if (route == null || route.Count == 0) return 0;
            for (int i = 0; i < route.Count; i++)
            {
                float dx = route[i].x - pos.x;
                float dz = route[i].z - pos.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; nearest = i; }
            }
            return nearest;
        }

        private static int FindNearestRouteIndex(List<Vector3> route, Vector3 pos)
        {
            int nearest = 0;
            float best = float.MaxValue;
            int along = 0;
            const float nearSq = 80f * 80f;
            for (int i = 0; i < route.Count; i++)
            {
                float dx = route[i].x - pos.x;
                float dz = route[i].z - pos.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; nearest = i; }
                if (d <= nearSq && i > along) along = i;
            }
            // Among nodes next to the hull, start at the furthest along the path so SWITCH does not step backward.
            if (along > nearest) nearest = along;
            if (nearest > route.Count - 3) nearest = Mathf.Max(0, route.Count - 3);
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
        /// <summary>
        /// Keep the arterial. Drop only trailing nodes that sit in a courtyard / no-go lot.
        /// Through-roads (launch, radtown) are left intact.
        /// </summary>
        private int StripBradleyYardInterior(List<Vector3> pts)
        {
            if (pts == null || pts.Count < 10) return 0;
            int removed = 0;
            for (int i = pts.Count - 2; i >= 1; i--)
            {
                float nd;
                string nl = ClosestMonumentLabel(pts[i], out nd);
                if (!IsCourtyardNearby(pts[i], 160f) && !(nd <= 170f && YardMonumentName(nl))) continue;
                if (PointIsMonumentThroughKeep(pts[i])) continue;
                pts.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        private bool WarpBradleyOutOfYard(BradleyAPC bradley, List<Vector3> route, MonumentScanEntry trap, int hullWp, out int destWp)
        {
            destWp = hullWp;
            return false;
            if (bradley == null || bradley.IsDestroyed || trap == null || route == null || route.Count < 4)
                return false;
            float r2 = (trap.Radius + 26f) * (trap.Radius + 26f);
            int start = Mathf.Clamp(hullWp, 0, route.Count - 1);
            int pick = -1;
            for (int i = start + 2; i < route.Count; i++)
            {
                float dx = route[i].x - trap.X, dz = route[i].z - trap.Z;
                if (dx * dx + dz * dz < r2) continue;
                if (!BradleyPointDry(route[i])) continue;
                pick = i;
                break;
            }
            if (pick < 0) return false;
            Vector3 dest = route[pick];
            try { dest.y = TerrainMeta.HeightMap.GetHeight(dest) + 0.8f; }
            catch { dest.y = bradley.transform.position.y; }
            Vector3 look = dest;
            if (pick + 1 < route.Count)
                look = route[pick + 1];
            Vector3 dir = new Vector3(look.x - dest.x, 0f, look.z - dest.z);
            try
            {
                var rb = bradley.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch { }
            // No hull teleport. Caller installs a skirt or a reverse join instead.
            destWp = pick;
            return false;
        }

        private List<Vector3> BuildOffroadSkirt(Vector3 entry, Vector3 exit, float cx, float cz, float radius)
        {
            var arc = new List<Vector3>();
            Vector2 a = new Vector2(entry.x - cx, entry.z - cz);
            Vector2 b = new Vector2(exit.x - cx, exit.z - cz);
            if (a.sqrMagnitude < 4f) a = Vector2.right * radius;
            if (b.sqrMagnitude < 4f) b = Vector2.left * radius;
            float angA = Mathf.Atan2(a.y, a.x);
            float angB = Mathf.Atan2(b.y, b.x);
            float sweep = Mathf.DeltaAngle(angA * Mathf.Rad2Deg, angB * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            if (Mathf.Abs(sweep) < 0.2f) sweep = sweep >= 0f ? 1.2f : -1.2f;
            // Prefer the shorter side. Flip if that side is wet.
            if (Mathf.Abs(sweep) > Mathf.PI) sweep -= Mathf.Sign(sweep) * Mathf.PI * 2f;
            int steps = 7;
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                float ang = angA + sweep * t;
                Vector3 p = new Vector3(cx + Mathf.Cos(ang) * radius, 0f, cz + Mathf.Sin(ang) * radius);
                if (!BradleyPointDry(p))
                {
                    ang = angA - sweep * t;
                    p = new Vector3(cx + Mathf.Cos(ang) * radius, 0f, cz + Mathf.Sin(ang) * radius);
                    if (!BradleyPointDry(p)) continue;
                }
                try { p.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(p) + 0.4f : entry.y; }
                catch { p.y = entry.y; }
                arc.Add(p);
            }
            return arc;
        }

        private int TrimBradleyYardTail(List<Vector3> pts)
        {
            if (pts == null || pts.Count < 4) return 0;
            int cut = 0;
            while (pts.Count > 8)
            {
                var last = pts[pts.Count - 1];
                // Powerplant / launch / trainyard used to keep Kind=through
                // so the seed highway was allowed to die inside S6.
                float td;
                string tl = ClosestMonumentLabel(last, out td);
                bool yard = (IsCourtyardNearby(last, 180f) || (td <= 180f && YardMonumentName(tl)))
                            && !PointIsMonumentThroughKeep(last);
                bool lot = IsBradleyNoGoLot(last) && !PointIsMonumentThroughKeep(last);
                if (!yard && !lot) break;
                pts.RemoveAt(pts.Count - 1);
                cut++;
            }
            return cut;
        }

        private static int FirstAwayIndex(List<Vector3> route, Vector3 pos, float minMeters)
        {
            if (route == null || route.Count < 2) return 0;
            float min2 = minMeters * minMeters;
            for (int i = 1; i < route.Count; i++)
            {
                float dx = route[i].x - pos.x;
                float dz = route[i].z - pos.z;
                if (dx * dx + dz * dz >= min2) return i;
            }
            return Mathf.Min(4, route.Count - 1);
        }

        /// <summary>
        /// Keep hull node 0, jump dest as node 1, then the rest of the highway.
        /// Avoids currentPath starting 80m ahead of a tank that cannot teleport.
        /// </summary>
        private void InstallBradleyFullPathSkipping(BradleyAPC bradley, List<Vector3> route, int hullWp, int destWp)
        {
            if (bradley == null || route == null || route.Count < 2) return;
            hullWp = Mathf.Clamp(hullWp, 0, route.Count - 1);
            destWp = Mathf.Clamp(destWp, hullWp, route.Count - 1);
            if (destWp <= hullWp + 1)
            {
                InstallBradleyFullPath(bradley, route, hullWp);
                return;
            }
            var slim = new List<Vector3>(route.Count - destWp + 2);
            slim.Add(route[hullWp]);
            slim.Add(route[destWp]);
            for (int i = destWp + 1; i < route.Count; i++)
                slim.Add(route[i]);
            InstallBradleyFullPath(bradley, slim, 0);
        }

        /// <summary>
        /// Slide the APC 18m along the painted highway when skip-ahead
        /// still leaves it sitting in a pothole / substation apron.
        /// </summary>
        private void AttachBradleyUnstick(BradleyAPC bradley)
        {
            if (bradley == null || bradley.IsDestroyed) return;
            try
            {
                var old = bradley.GetComponent<BradleyUnstick>();
                if (old != null) UnityEngine.Object.Destroy(old);
                var drive = bradley.gameObject.AddComponent<BradleyUnstick>();
                drive.Owner = this;
                drive.Apc = bradley;
            }
            catch (Exception ex)
            {
                DebugLog($"Bradley unstick attach: {ex.Message}");
            }
        }

        /// <summary>
        /// MonumentBradley recovery: one path, shove the hull. Do not rewrite the
        /// highway when a modular car / plant apron pins the tank.
        /// </summary>
        private class BradleyUnstick : MonoBehaviour
        {
            public LiveStatsEvents Owner;
            public BradleyAPC Apc;
            public void Kick()
            {
                Kick(transform.position + transform.forward * 24f);
            }
            public void Kick(Vector3 toward)
            {
                _aim = toward;
                _bursts = 0;
                _lastMove = 0f;
                IgnoreNearby();
                IgnoreBlockingContacts();
                _recoverUntil = Time.time + 6f;
                SetThrottle(1f);
                SetForce(_forceOrig * 2.2f);
            }
            private Vector3 _last;
            private Vector3 _aim;
            private float _lastMove;
            private float _recoverUntil;
            private float _reverseUntil;
            private float _nextScan;
            private int _bursts;
            private readonly List<Collider> _ignored = new List<Collider>();
            private readonly Dictionary<Collider, float> _blocking = new Dictionary<Collider, float>();
            private Collider[] _selfCols;
            private System.Reflection.FieldInfo _throttle;
            private System.Reflection.FieldInfo _force;
            private float _forceOrig = 2000f;
            private static readonly System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            private void Awake()
            {
                if (Apc == null) Apc = GetComponent<BradleyAPC>();
                _last = transform.position;
                _lastMove = Time.time;
                try
                {
                    _selfCols = GetComponentsInChildren<Collider>();
                    _throttle = typeof(BradleyAPC).GetField("throttle", BF);
                    _force = typeof(BradleyAPC).GetField("moveForceMax", BF);
                    if (_force != null && Apc != null)
                        _forceOrig = (float)_force.GetValue(Apc);
                }
                catch { }
            }

            private void OnDestroy()
            {
                RestoreCollisions();
                SetForce(_forceOrig);
            }

            // MonumentBradley: track what the hull is actually sitting on.
            private void OnCollisionStay(Collision collision)
            {
                var c = collision != null ? collision.collider : null;
                if (c == null || c.isTrigger || c is TerrainCollider) return;
                if (_selfCols != null)
                {
                    for (int i = 0; i < _selfCols.Length; i++)
                        if (_selfCols[i] == c) return;
                }
                _blocking[c] = Time.time;
            }

            private void OnCollisionEnter(Collision collision)
            {
                OnCollisionStay(collision);
                if (Apc == null || collision == null) return;
                try
                {
                    var go = collision.gameObject;
                    if (go == null) return;
                    var car = go.GetComponentInParent<ModularCar>();
                    if (car != null && !car.IsDestroyed)
                    {
                        var rb = car.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            if (rb.IsSleeping()) rb.WakeUp();
                            rb.AddForce((transform.forward + transform.right * 0.3f) * 80f, ForceMode.Acceleration);
                        }
                        try
                        {
                            var hit = new HitInfo(Apc, car, Rust.DamageType.Explosion, 80f, car.transform.position);
                            hit.damageTypes.Add(Rust.DamageType.AntiVehicle, 40f);
                            car.Hurt(hit);
                        }
                        catch { }
                        if (Owner != null)
                            Owner.DebugLog($"Bradley shove-car {Owner.PositionToGrid(transform.position)}");
                        return;
                    }
                    var loot = go.GetComponentInParent<LootContainer>();
                    if (loot != null && !(loot is HackableLockedCrate) && !(loot is SupplyDrop) && !loot.IsDestroyed)
                    {
                        var rb = loot.GetComponent<Rigidbody>();
                        if (rb != null) rb.AddForce(transform.forward * 50f, ForceMode.Acceleration);
                        else
                        {
                            try { loot.Die(new HitInfo(Apc, loot, Rust.DamageType.Blunt, 200f)); } catch { }
                        }
                    }
                }
                catch { }
            }

            private void OnCollisionExit(Collision collision)
            {
                if (collision != null && collision.collider != null)
                    _blocking.Remove(collision.collider);
            }

            private void FixedUpdate()
            {
                if (Apc == null || Apc.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                Vector3 pos = transform.position;
                try
                {
                    if (TerrainMeta.HeightMap != null)
                    {
                        float gy = TerrainMeta.HeightMap.GetHeight(pos);
                        if (pos.y < -20f)
                        {
                            pos.y = gy + 1.2f;
                            transform.position = pos;
                            var rbFix = Apc.GetComponent<Rigidbody>();
                            if (rbFix != null)
                            {
                                rbFix.linearVelocity = Vector3.zero;
                                rbFix.angularVelocity = Vector3.zero;
                            }
                            RestoreCollisions();
                            _recoverUntil = 0f;
                            _reverseUntil = 0f;
                            _bursts = 4;
                            if (Owner != null) Owner.DebugLog($"Bradley unstick-clamp {Owner.PositionToGrid(pos)} y={gy:F0}");
                        }
                    }
                }
                catch { }
                float dx = pos.x - _last.x, dz = pos.z - _last.z;
                if (dx * dx + dz * dz > 2.2f * 2.2f)
                {
                    _last = pos;
                    _lastMove = Time.time;
                    if (Time.time >= _recoverUntil && Time.time >= _reverseUntil)
                        _bursts = 0;
                }

                if (Time.time < _reverseUntil)
                {
                    SetThrottle(-1f);
                    PushBodies();
                    return;
                }

                if (Time.time < _recoverUntil)
                {
                    SetThrottle(1f);
                    SetForce(_forceOrig * 2.2f);
                    var rb = Apc.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic && rb.linearVelocity.magnitude < 4.5f)
                    {
                        Vector3 dir = _aim.sqrMagnitude > 1f
                            ? Vector3.ProjectOnPlane(_aim - transform.position, Vector3.up)
                            : Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                        if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
                        dir.Normalize();
                        rb.AddForce(dir * 8f, ForceMode.Acceleration);
                        if (dir.sqrMagnitude > 0.01f)
                            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), 0.35f);
                    }
                    PushBodies();
                    return;
                }

                if (Time.time >= _recoverUntil && _ignored.Count > 0)
                {
                    RestoreCollisions();
                    SetForce(_forceOrig);
                }

                if (_lastMove <= 0f) _lastMove = Time.time;
                if (Time.time - _lastMove < 20f) return;
                if (_bursts >= 4) return;
                _bursts++;
                _lastMove = Time.time;
                if ((_bursts % 2) == 0 && _aim.sqrMagnitude < 1f)
                {
                    _reverseUntil = Time.time + 2.4f;
                    SetThrottle(-1f);
                    if (Owner != null)
                        Owner.DebugLog($"Bradley unstick-reverse {Owner.PositionToGrid(pos)} burst={_bursts}");
                }
                else
                {
                    IgnoreNearby();
                    IgnoreBlockingContacts();
                    _recoverUntil = Time.time + 5f;
                    SetThrottle(1f);
                    SetForce(_forceOrig * 2.2f);
                    if (Owner != null)
                        Owner.DebugLog($"Bradley unstick-push {Owner.PositionToGrid(pos)} burst={_bursts}");
                }
            }

            private void SetThrottle(float v)
            {
                try
                {
                    if (_throttle != null && Apc != null)
                        _throttle.SetValue(Apc, v);
                    foreach (var n in new[] { "leftThrottle", "rightThrottle" })
                    {
                        var f = typeof(BradleyAPC).GetField(n, BF);
                        if (f != null) f.SetValue(Apc, v);
                    }
                }
                catch { }
            }

            private void SetForce(float v)
            {
                try
                {
                    if (_force != null && Apc != null)
                        _force.SetValue(Apc, v);
                }
                catch { }
            }

            private void PushBodies()
            {
                if (Time.time < _nextScan) return;
                _nextScan = Time.time + 0.35f;
                try
                {
                    var hits = Physics.OverlapSphere(transform.position, 8f, ~0, QueryTriggerInteraction.Ignore);
                    Vector3 fwd = transform.forward;
                    foreach (var h in hits)
                    {
                        if (h == null) continue;
                        var rb = h.attachedRigidbody;
                        if (rb == null || rb.transform == transform) continue;
                        if (rb.isKinematic) continue;
                        rb.AddForce((fwd + transform.right) * 40f, ForceMode.Acceleration);
                    }
                }
                catch { }
            }

            private void IgnoreNearby()
            {
                RestoreCollisions();
                if (_selfCols == null) return;
                try
                {
                    var hits = Physics.OverlapSphere(transform.position, 7f, ~0, QueryTriggerInteraction.Ignore);
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform.root == transform.root) continue;
                        string n = (h.name ?? "") + " " + (h.transform.root.name ?? "");
                        n = n.ToLowerInvariant();
                        bool terrain = n.IndexOf("terrain", StringComparison.Ordinal) >= 0
                            || n.IndexOf("water", StringComparison.Ordinal) >= 0
                            || n.IndexOf("ground", StringComparison.Ordinal) >= 0;
                        if (terrain) continue;
                        foreach (var c in _selfCols)
                        {
                            if (c == null) continue;
                            Physics.IgnoreCollision(c, h, true);
                        }
                        _ignored.Add(h);
                    }
                }
                catch { }
            }

            private void IgnoreBlockingContacts()
            {
                if (_selfCols == null) return;
                float now = Time.time;
                var keys = new List<Collider>(_blocking.Keys);
                foreach (var h in keys)
                {
                    if (h == null)
                    {
                        _blocking.Remove(h);
                        continue;
                    }
                    float t;
                    if (!_blocking.TryGetValue(h, out t) || now - t > 1.5f) continue;
                    foreach (var c in _selfCols)
                    {
                        if (c == null || c.isTrigger) continue;
                        try { Physics.IgnoreCollision(c, h, true); } catch { }
                    }
                    if (!_ignored.Contains(h)) _ignored.Add(h);
                }
            }

            private void RestoreCollisions()
            {
                if (_selfCols == null || _ignored.Count == 0)
                {
                    _ignored.Clear();
                    return;
                }
                foreach (var h in _ignored)
                {
                    if (h == null) continue;
                    foreach (var c in _selfCols)
                    {
                        if (c == null) continue;
                        try { Physics.IgnoreCollision(c, h, false); } catch { }
                    }
                }
                _ignored.Clear();
            }
        }

        private bool NudgeBradleyAlongRoute(BradleyAPC bradley, List<Vector3> route, int hullWp)
        {
            if (bradley == null || bradley.IsDestroyed || route == null || route.Count < 3)
                return false;
            hullWp = Mathf.Clamp(hullWp, 0, route.Count - 2);
            Vector3 pos = bradley.transform.position;
            Vector3 tgt = route[Mathf.Min(hullWp + 3, route.Count - 1)];
            Vector3 dir = new Vector3(tgt.x - pos.x, 0f, tgt.z - pos.z);
            if (dir.sqrMagnitude < 9f)
            {
                tgt = route[Mathf.Min(hullWp + 6, route.Count - 1)];
                dir = new Vector3(tgt.x - pos.x, 0f, tgt.z - pos.z);
            }
            if (dir.sqrMagnitude < 4f) return false;
            dir.Normalize();
            Vector3 dest = pos + dir * 18f;
            if (!BradleyPointDry(dest)) return false;
            try { dest.y = TerrainMeta.HeightMap.GetHeight(dest) + 0.7f; }
            catch { dest.y = pos.y; }
            try
            {
                var rb = bradley.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch { }
            // 18m position set was a teleport. Reinstall the path; do not move the hull.
            return false;
        }

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



        /// <summary>A real player sitting in a flyable CH47. NPC crews and empty hulls do not count.</summary>
        private int CountPlayerChinooks()
        {
            int n = 0;
            int seen = 0;
            try
            {
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    var heli = ent as CH47Helicopter;
                    if (heli == null || heli.IsDestroyed) continue;
                    seen++;
                    if (heli is CH47HelicopterAIController) continue;
                    string prefab = (heli.ShortPrefabName ?? "").ToLowerInvariant();
                    if (prefab.Contains("scientist") || prefab.Contains("ch47scientists")) continue;
                    if (!HasRealPlayerMounted(heli)) continue;
                    n++;
                    DebugLog($"AirfieldChinook seated CH47 {prefab} at {PositionToGrid(heli.transform.position)}");
                }
            }
            catch { }
            if (seen > 0 && n == 0)
                DebugLog($"AirfieldChinook CH47 hulls in world {seen}, seated players 0");
            return n;
        }

        private bool HasRealPlayerMounted(BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            try
            {
                var vehicle = ent as BaseVehicle;
                if (vehicle != null && vehicle.mountPoints != null)
                {
                    foreach (var mount in vehicle.mountPoints)
                    {
                        var seated = mount?.mountable?.GetMounted() as BasePlayer;
                        if (seated != null && !seated.IsNpc && seated.userID > 10000) return true;
                    }
                }
                if (ent.children != null)
                {
                    foreach (var child in ent.children)
                    {
                        var seat = child as BaseMountable;
                        var seated = seat?.GetMounted() as BasePlayer;
                        if (seated != null && !seated.IsNpc && seated.userID > 10000) return true;
                    }
                }
            }
            catch { }
            return false;
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
            if (sub != "reload" && sub != "status" && sub != "monbake" && sub != "bake" && !_ready)
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
                case "monbake":
                case "bake":
                    ForceRebuildMonumentBaker();
                    SendReply(player, "Monument skeleton baker rebuilt (" + MonumentScanVersion + " / " + BradleyPathVersion + "). Check oxide/data/LiveStatsMonuments.json and LiveStatsBradleyPaths.json.");
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
                ["UsageEvent"] = "Usage: /event <supplydrop|cargo|heli|chinook|bradley|attackheli|tugboat|balloon|heavies|hackablecrate|sub|scrapheli|mini|ambush|tunnel|subway|mine|peacekeepers|raid|airfield|f15|status|reload|monbake>",
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
