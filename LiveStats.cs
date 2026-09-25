using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using System.Linq;
using Rust;

namespace Oxide.Plugins
{
    [Info("LiveStats", "FiREST0N3D", "1.9.816")]
    [Description("Comprehensive player/NPC/animal stats, killfeed + optional idle kick. Dual wipe detection. Kill streak tracking (players/NPCs/animals) + isPlayerDead. Optional LiveStatsWorld extension for time/weather. CONFLICTS: none by default; idle kick is opt-in. Pair with LiveStatsWorld/LiveStatsEvents as a suite.")]
    class LiveStats : RustPlugin
    {
        private Timer _timer;
        private Timer _idleTimer;
        private readonly List<DeathEntry> recentDeaths = new List<DeathEntry>(16);
        private readonly Dictionary<string, PlayerStats> playerStats = new Dictionary<string, PlayerStats>(1024);
        private readonly Dictionary<string, NpcStats> npcStats = new Dictionary<string, NpcStats>(128);
        private readonly Dictionary<string, AnimalStats> animalStats = new Dictionary<string, AnimalStats>(64);
        private Dictionary<string, int> environmentalDeaths = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> loginTimes = new Dictionary<string, DateTime>(256);   // last bank point (for crash-safe incremental saves)
        private readonly Dictionary<string, DateTime> sessionStarts = new Dictionary<string, DateTime>(256); // true start of current continuous session
        private readonly HashSet<ulong> processedDeaths = new HashSet<ulong>();
        // Prevents NPCs from being awarded multiple kills for the same victim death
        // (guards against rare double OnEntityDeath firings that slip past the main dedupe window)
        private readonly HashSet<ulong> processedNpcKills = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> lastEnvironmentalCause = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> lastNpcAttacker = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> lastPlayerAttacker = new Dictionary<ulong, string>(); // display name
        private readonly Dictionary<ulong, string> lastPlayerAttackerId = new Dictionary<ulong, string>(); // Steam UserIDString
        // Last animal that damaged this entity (fallback when HitInfo.Initiator is null at death)
        private readonly Dictionary<ulong, string> lastAnimalAttacker = new Dictionary<ulong, string>();

        // Idle / AFK tracking
        private readonly Dictionary<string, float> lastActivity = new Dictionary<string, float>(256);
        private readonly HashSet<string> idleWarned = new HashSet<string>();

        // Cap + prune for damage-tracking dicts (written on every hit; removed mainly on death)
        private const int MaxDamageTrackEntries = 512;
        private readonly List<ulong> _damageTrackPruneBuffer = new List<ulong>(64);
        private float _lastDamageTrackPrune = -999f;

        private ConfigData config;

        // Optional LiveStatsWorld extension (world time, moon/sun, Open-Meteo weather)
        [PluginReference]
        private Plugin LiveStatsWorld;
        private bool worldExtensionReady = false;


        private bool _initialized = false;
        private bool _playerStatsDirty;
        private bool _npcStatsDirty;
        private bool _animalStatsDirty;
        private bool _envStatsDirty;
        private Timer _statsFlushTimer;

        private const string AdminPermission = "livestats.admin";

        // Dual wipe detection: OnNewSave (primary) + persistent map identity (seed|size|level) fallback
        private string _lastMapIdentity = null;
        private DateTime _wipeStartTime = DateTime.UtcNow; // used for "day of wipe" on PvP kills
        private const string WipeDataFile = "livestats_wipe";

        private class WipeIdentity
        {
            public string Identity = "";
            public string LastWipeDetected = "";
        }

        class PlayerStats
        {
            public int kills = 0;
            public int deaths = 0;
            public int headshots = 0;
            public int playersKilled = 0;
            public int npcsKilled = 0;
            public int animalsKilled = 0;
            public int playerHeadshots = 0;
            public int npcHeadshots = 0;
            // Animals have no conventional headshot hitboxes — field kept for data compatibility only
            public int animalHeadshots = 0;
            public int deathsByPlayer = 0;
            public int deathsByNPC = 0;
            public int deathsByAnimal = 0;
            public int deathsByRadiationPoisoning = 0;
            public int suicides = 0;
            public int deathsByEnvironment = 0;
            public int totalMinutesPlayed = 0;
            public string lastName = "";
            public DateTime lastSeen = DateTime.UtcNow;

            // Kill streak: consecutive kills (players + NPCs + animals) without dying.
            // currentKillStreak / isOnKillStreak are session-scoped (reset on connect & disconnect).
            // bestKillStreak is persistent across sessions.
            public int currentKillStreak = 0;
            public int bestKillStreak = 0;
            public bool isOnKillStreak = false;  // true when currentKillStreak >= KillStreakStart

            // True only while the player is dead and has not yet respawned
            public bool isPlayerDead = false;

            // Per-animal tracking for hunters (K/D vs specific animals)
            public Dictionary<string, int> animalsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> deathsByAnimalType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Per-NPC tracking (K/D vs specific NPC types, e.g. Heavy Scientist, Tunnel Dweller)
            public Dictionary<string, int> npcsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> deathsByNpcType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Personal PvP kill feed (who this player killed / who killed them)
            public List<PvpEntry> recentPvpKills = new List<PvpEntry>();
            public List<PvpEntry> recentPvpDeaths = new List<PvpEntry>();
        }

        class NpcStats
        {
            public int kills = 0;
            public int deaths = 0;
            public string lastPlayerToKillIt = "";
            public string lastNpcThatKilledIt = "";
            public string prefabName = "";
            public DateTime lastEvent = DateTime.UtcNow;
        }

        class AnimalStats
        {
            public int kills = 0;
            public int deaths = 0;
            public string lastPlayerToKillIt = "";
            public string lastAnimalThatKilledIt = "";
            public string lastNpcThatKilledIt = "";
            public DateTime lastEvent = DateTime.UtcNow;
        }

        struct DeathEntry
        {
            public string victim;
            public string killer;
            public string time;
            public bool headshot;
            public string grid;
        }

        /// <summary>Personal PvP history entry stored on each player's stats.</summary>
        class PvpEntry
        {
            public string opponent = "";
            public string time = "";
            public bool headshot = false;
            public string grid = "";
        }

        class ConfigData
        {
            [JsonProperty("MaxDeaths")] public int MaxDeaths { get; set; } = 8;
            [JsonProperty("DeathDedupeWindow")] public float DeathDedupeWindow { get; set; } = 3f;
            [JsonProperty("PlayerStatsCleanupDays")] public int PlayerStatsCleanupDays { get; set; } = 21;
            [JsonProperty("PlayerStatsCleanupIntervalMinutes")] public int PlayerStatsCleanupIntervalMinutes { get; set; } = 60;
            [JsonProperty("UpdateInterval")] public float UpdateInterval { get; set; } = 10f;
            [JsonProperty("EnableAnimalStats")] public bool EnableAnimalStats { get; set; } = true;
            [JsonProperty("EnableRecentDeaths")] public bool EnableRecentDeaths { get; set; } = true;

            [JsonProperty("DebugMode")] public bool DebugMode { get; set; } = false;
            // Language: Oxide stores a separate per-player code (default en). SetLanguage maps
            // oxide/lang/<code>/LiveStats.json. Leave ForceLanguage empty to use the Rust client.
            [JsonProperty("LanguageSyncFromClient")] public bool LanguageSyncFromClient { get; set; } = true;
            [JsonProperty("LanguageForce")] public string LanguageForce { get; set; } = "";
            [JsonProperty("LanguageLog")] public bool LanguageLog { get; set; } = true;
            [JsonProperty("LiveStatsCommandEnabled")] public bool LiveStatsCommandEnabled { get; set; } = true;
            [JsonProperty("UseCleanKillerNames")] public bool UseCleanKillerNames { get; set; } = true;

            [JsonProperty("AnnouncePlayerDeaths")] public bool AnnouncePlayerDeaths { get; set; } = true;
            [JsonProperty("AnnounceAnimalKills")] public bool AnnounceAnimalKills { get; set; } = true;
            [JsonProperty("AnnounceAnimalDeaths")] public bool AnnounceAnimalDeaths { get; set; } = true;
            [JsonProperty("AnnounceNpcDeaths")] public bool AnnounceNpcDeaths { get; set; } = true;
            [JsonProperty("AnnounceEnvironmentalDeaths")] public bool AnnounceEnvironmentalDeaths { get; set; } = true;
            // When true: only real-player environmental deaths are scored into environmental_stats (/envstats)
            // and only player env deaths are announced. NPC/animal env deaths are ignored for both.
            [JsonProperty("PlayersOnlyEnv")] public bool PlayersOnlyEnv { get; set; } = true;

            [JsonProperty("KillFeedCommandEnabled")] public bool KillFeedCommandEnabled { get; set; } = true;
            [JsonProperty("PvpFeedCommandEnabled")] public bool PvpFeedCommandEnabled { get; set; } = true;
            [JsonProperty("MaxPvpFeedEntries")] public int MaxPvpFeedEntries { get; set; } = 12;
            // Kill streak: consecutive kills of players, NPCs, and animals without dying. "Starts" at KillStreakStart.
            [JsonProperty("EnableKillStreaks")] public bool EnableKillStreaks { get; set; } = true;
            [JsonProperty("KillStreakStart")] public int KillStreakStart { get; set; } = 5;
            [JsonProperty("AnnounceKillStreaks")] public bool AnnounceKillStreaks { get; set; } = true;
            [JsonProperty("MyStatsCommandEnabled")] public bool MyStatsCommandEnabled { get; set; } = true;
            [JsonProperty("TopCommandEnabled")] public bool TopCommandEnabled { get; set; } = true;
            [JsonProperty("NpcStatsCommandEnabled")] public bool NpcStatsCommandEnabled { get; set; } = true;
            [JsonProperty("AnimalStatsCommandEnabled")] public bool AnimalStatsCommandEnabled { get; set; } = true;
            [JsonProperty("EnvStatsCommandEnabled")] public bool EnvStatsCommandEnabled { get; set; } = true;
            [JsonProperty("ClearStatsCommandEnabled")] public bool ClearStatsCommandEnabled { get; set; } = true;

            [JsonProperty("KillFeedEnabled")] public bool KillFeedEnabled { get; set; } = true;
            [JsonProperty("KillFeedShowHeadshots")] public bool KillFeedShowHeadshots { get; set; } = true;
            [JsonProperty("KillFeedShowGrid")] public bool KillFeedShowGrid { get; set; } = true;

            [JsonProperty("ClearStatsOnWipe")] public bool ClearStatsOnWipe { get; set; } = true;

            // Wipe schedule (Facepunch-style first Thursday @ configured local time)
            // WipeTimezone: Windows ID "Eastern Standard Time" (handles EST/EDT). Linux: "America/New_York".
            [JsonProperty("WipeTimezone")] public string WipeTimezone { get; set; } = "Eastern Standard Time";
            [JsonProperty("WipeHour")] public int WipeHour { get; set; } = 14;
            [JsonProperty("WipeMinute")] public int WipeMinute { get; set; } = 15;

            // Idle / AFK kick
            [JsonProperty("EnableIdleKick")] public bool EnableIdleKick { get; set; } = false;
            [JsonProperty("IdleWarningMinutes")] public float IdleWarningMinutes { get; set; } = 10f;
            [JsonProperty("IdleKickMinutes")] public float IdleKickMinutes { get; set; } = 15f;
            [JsonProperty("IdleCheckIntervalSeconds")] public float IdleCheckIntervalSeconds { get; set; } = 30f;
            [JsonProperty("IdleBypassPermission")] public string IdleBypassPermission { get; set; } = "livestats.idle.bypass";

            [JsonProperty("CustomKillerNames")]
            public Dictionary<string, string> CustomKillerNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "scientistnpc_ch47_gunner", "CH47 Gunner" },
                { "scientistnpc_cargo_turret_any", "Cargo Turret Scientist" },
                { "scientistnpc_bradley", "Bradley Scientist" },
                { "scientistnpc_ptboat", "PT Boat Scientist" },
                { "scientistnpc_rhib", "RHIB Scientist" },
                { "ch47scientists.entity", "CH47 Scientist" },
                { "scientistnpc_heavy", "Heavy Scientist" },
                { "scientistnpc_oilrig", "Oil Rig Scientist" },
                { "scientistnpc_patrol", "Patrol Scientist" },
                { "scientistnpc_roam", "Roaming Scientist" },
                { "scientistnpc_junkpile", "Junkpile Scientist" },
                { "scientistnpc_bandit", "Bandit Scientist" },
                { "scientistnpc", "Scientist" },
                { "scientist", "Scientist" },
                { "tunneldweller", "Tunnel Dweller" },
                { "bear", "Bear" },
                { "polar", "Polar Bear" },
                { "polarbear", "Polar Bear" },
                { "wolf", "Wolf" },
                { "boar", "Boar" },
                { "chicken", "Chicken" },
                { "stag", "Deer" },
                { "horse", "Horse" },
                // Livestock (Facepunch livestock2026 / AUX01): cattle + sheep family, plus goat
                { "cow", "Cow" },
                { "bull", "Bull" },
                { "calf", "Calf" },
                { "cattle", "Cow" },
                { "sheep", "Sheep" },
                { "lamb", "Lamb" },
                { "ewe", "Sheep" },
                { "ram", "Sheep" },
                { "goat", "Goat" },
                { "kid", "Goat" },
                { "babygoat", "Goat" },
                // AUX01 critters merged from livestock branch (Sep 2026)
                { "rabbit", "Rabbit" },
                { "bunny", "Rabbit" },
                { "squirrel", "Squirrel" },
                { "frog", "Frog" },
                { "toad", "Frog" },
                { "seaturtle", "Sea Turtle" },
                { "sea_turtle", "Sea Turtle" },
                { "turtle", "Sea Turtle" },
                { "jellyfish", "Jellyfish" },
                { "jelly", "Jellyfish" },
                { "seagull", "Seagull" },
                { "gull", "Seagull" },
                { "snake", "Snake" },
                { "shark", "Shark" },
                { "simpleshark", "Shark" },
                { "crocodile", "Crocodile" },
                { "croc", "Crocodile" },
                { "alligator", "Crocodile" },
                { "panther", "Panther" },
                { "tiger", "Tiger" },
                { "bee", "Bees" },
                { "beehive", "Bee Hive" },
                { "beehive.natural", "Bee Hive" },
                { "beeswarm", "Bees" },
                { "oilfireball", "Fire" },
                { "oilfireballsmall", "Fire" },
                { "loot-barrel", "Loot Barrel" },
                { "oil-barrel", "Oil Barrel" },
                { "sentry.bandit.static", "Bandit Sentry" },
                { "patrolhelicopter", "Patrol Helicopter" },
                { "bradleyapc", "Bradley APC" },
                { "bradley", "Bradley APC" },
                { "ch47", "CH47" },
                { "minicopter", "Minicopter" },
                { "scraptransporthelicopter", "Scrap Heli" },
                { "modularcar", "Modular Car" },
                { "2module_car", "Car" },
                { "3module_car", "Car" },
                { "4module_car", "Car" },
                { "hotairballoon", "Hot Air Balloon" },
                { "rowboat", "Rowboat" },
                { "rhib", "RHIB" },
                { "tugboat", "Tugboat" },
                { "submarine", "Submarine" },
                { "snowmobile", "Snowmobile" },
                { "motorbike", "Motorbike" },
                { "bicycle", "Bicycle" },
                { "pedaltrike", "Pedal Trike" },
                { "kayak", "Kayak" },
                { "workcart", "Workcart" },
                { "train", "Train" },
                { "wagon", "Train Wagon" }
            };
        }

        void Init()
        {
            // Config is loaded automatically by Oxide via the overridden LoadConfig()
            permission.RegisterPermission(AdminPermission, this);
            // Guard: config can be null if LoadConfig failed before defaults were applied
            string idlePerm = config?.IdleBypassPermission;
            if (!string.IsNullOrEmpty(idlePerm))
                permission.RegisterPermission(idlePerm, this);
            LoadDefaultMessages();
            LogLanguagePackInventory();

            Puts("LiveStats v1.9.816 loaded (critters: rabbit/squirrel/frog/turtle + livestock)");
            LoadWipeIdentity();
            LoadStats();
            LoadNpcStats();
            SeedNpcTypes();
            LoadAnimalStats();
            SeedAnimalTypes();
            LoadEnvironmentalStats();
            SeedEnvironmentalStats();
        }

        void OnServerInitialized(bool initial)
        {
            if (_initialized) return;
            _initialized = true;

            // Detect optional LiveStatsWorld extension (world stats + real-world weather)
            if (LiveStatsWorld == null)
                LiveStatsWorld = plugins.Find("LiveStatsWorld");
            worldExtensionReady = LiveStatsWorld != null;
            if (worldExtensionReady)
                Puts("LiveStatsWorld extension detected — world stats & weather handled by extension.");
            else
                Puts("LiveStatsWorld extension not installed — world stats & local weather unavailable.");

            // Dual wipe detection: OnNewSave (when Oxide reports a new .sav) + map identity
            // fallback (seed|worldsize|level) for cases where OnNewSave was missed.
            CheckForWipe("OnServerInitialized");

            // Main timer: live stats save (world stats handled by LiveStatsWorld extension)
            // NOTE: Do NOT call SaveAllPlayerTimes() here. Session display uses sessionStarts
            // (true connect time) while banking uses loginTimes (last bank point).
            _timer = timer.Every(config.UpdateInterval, () =>
            {
                SaveLiveStats();
            });

            // Persist playtime every 5 minutes so long sessions survive a crash.
            // Only the banking point (loginTimes) is advanced; sessionStarts stays put
            // so the displayed "Current session" continues to grow without resetting.
            timer.Every(300f, () =>
            {
                SaveAllPlayerTimes();
            });

            // Debounce the large player/npc/animal/env JSON writes (deaths mark dirty).
            // Live dashboard (live_stats.json) still uses UpdateInterval.
            _statsFlushTimer = timer.Every(20f, FlushDirtyStats);

            // Player stats cleanup
            if (config.PlayerStatsCleanupDays > 0 && config.PlayerStatsCleanupIntervalMinutes > 0)
            {
                float cleanupInterval = config.PlayerStatsCleanupIntervalMinutes * 60f;
                timer.Every(cleanupInterval, CleanupOldPlayerStats);
                timer.Once(30f, CleanupOldPlayerStats);
            }


            // Idle / AFK kick system — only subscribe the hot input hook while the feature is on
            if (config.EnableIdleKick)
            {
                Subscribe(nameof(OnPlayerInput));
                float idleInterval = Mathf.Max(15f, config.IdleCheckIntervalSeconds);
                _idleTimer = timer.Every(idleInterval, CheckIdlePlayers);
                Puts($"Idle kick enabled (warn @ {config.IdleWarningMinutes}m, kick @ {config.IdleKickMinutes}m)");
            }
            else
            {
                Unsubscribe(nameof(OnPlayerInput));
            }

            // Periodic prune of damage-tracking dictionaries (prevents unbounded growth)
            timer.Every(180f, PruneDamageTracking);

            foreach (var player in BasePlayer.activePlayerList)
                SyncOxideLanguage(player);

            SaveLiveStats();
        }


        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch
            {
                Puts("Config file is missing or corrupt – generating a new default config.");
                LoadDefaultConfig();
            }

            // Ensure any newly-added fields have sensible defaults (e.g. after plugin updates)
            if (config.CustomKillerNames == null)
                config.CustomKillerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Always write the config so a missing file is created and new defaults are persisted
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        void Unload()
        {
            // Mark offline first so the dashboard shows the offline overlay
            // immediately, even if timer teardown or other saves take time.
            MarkServerOffline();

            // Stop timers so a late SaveLiveStats() cannot overwrite the offline marker
            _timer?.Destroy();
            _timer = null;
            _idleTimer?.Destroy();
            _idleTimer = null;
            _statsFlushTimer?.Destroy();
            _statsFlushTimer = null;

            SaveAllPlayerTimes();
            FlushDirtyStats(force: true);
        }

        // Also fire on full server shutdown (more reliable than Unload alone in some hosts)
        void OnServerShutdown()
        {
            _timer?.Destroy();
            _timer = null;
            _idleTimer?.Destroy();
            _idleTimer = null;
            _statsFlushTimer?.Destroy();
            _statsFlushTimer = null;

            FlushDirtyStats(force: true);
            MarkServerOffline();
        }

        /// <summary>
        /// Writes live_stats.json with serverRunning = false so the Node dashboard
        /// immediately shows the offline overlay (matches the check in serverssl-improved.js).
        /// </summary>

        private void MarkServerOffline()
        {
            try
            {
                var offlineData = new
                {
                    serverRunning = false,
                    serverName = ConVar.Server.hostname ?? "Rust Server",
                    map = ConVar.Server.level ?? "",
                    mapSeed = ConVar.Server.seed.ToString(),
                    mapSize = ConVar.Server.worldsize.ToString(),
                    playersOnline = 0,
                    maxPlayers = ConVar.Server.maxplayers,
                    players = new List<object>(),
                    recentDeaths = new List<DeathEntry>(),
                    timestamp = DateTime.UtcNow.ToString("o"),
                    lastUpdateUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                Interface.Oxide.DataFileSystem.WriteObject("live_stats", offlineData);
                Puts("Marked server offline in live_stats.json");
            }
            catch (Exception ex)
            {
                Puts($"Failed to write offline marker: {ex.Message}");
            }
        }


        private void SeedNpcTypes()
        {
            if (config.CustomKillerNames == null) return;

            string[] npcKeywords = { "scientist", "npc", "bandit", "sentry", "dweller", "heavy", "patrol", "bradley", "ch47", "cargo", "turret", "rhib", "ptboat", "tunnel" };

            foreach (var kvp in config.CustomKillerNames)
            {
                string cleanName = kvp.Value;
                string key = kvp.Key.ToLowerInvariant();
                bool isNpcType = npcKeywords.Any(k => key.Contains(k) || cleanName.ToLowerInvariant().Contains(k));

                if (isNpcType && !npcStats.ContainsKey(cleanName))
                {
                    npcStats[cleanName] = new NpcStats { prefabName = cleanName };
                }
            }

            if (npcStats.Count > 0) SaveNpcStats();
        }

        private void SeedAnimalTypes()
        {
            // Pre-populate known animals so /animalstats always shows them (even with 0 kills/deaths)
            string[] animals = {
                "Bear", "Polar Bear", "Wolf", "Boar", "Chicken", "Deer", "Horse",
                "Cow", "Bull", "Calf", "Sheep", "Lamb", "Goat",
                "Rabbit", "Squirrel", "Frog", "Sea Turtle", "Jellyfish", "Seagull",
                "Snake", "Shark", "Crocodile", "Panther", "Tiger", "Bees", "Bee Hive"
            };

            bool added = false;
            foreach (string name in animals)
            {
                if (!animalStats.ContainsKey(name))
                {
                    animalStats[name] = new AnimalStats();
                    added = true;
                }
            }

            if (added) SaveAnimalStats();
        }

        private void SeedEnvironmentalStats()
        {
            // Pre-populate common environmental / suicide causes so /envstats always lists them
            string[] causes = {
                "Starvation",
                "Dehydration",
                "Freezing",
                "Radiation Poisoning",
                "Heat",
                "Drowning",
                "Poison",
                "Fall Damage",
                "Bleeding Out",
                "Explosion",
                "Electric Shock",
                "Fire",
                "Suicide"
            };

            bool added = false;
            foreach (string cause in causes)
            {
                if (!environmentalDeaths.ContainsKey(cause))
                {
                    environmentalDeaths[cause] = 0;
                    added = true;
                }
            }

            if (added) SaveEnvironmentalStats();
        }


        private void LoadStats()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PlayerStats>>("player_stats");
                if (data == null || data.Count == 0) return;

                playerStats.Clear();
                foreach (var kvp in data)
                {
                    var s = kvp.Value;
                    // Ensure new per-type dictionaries / lists exist for older save data
                    if (s.animalsKilledByType == null)
                        s.animalsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (s.deathsByAnimalType == null)
                        s.deathsByAnimalType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (s.npcsKilledByType == null)
                        s.npcsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (s.deathsByNpcType == null)
                        s.deathsByNpcType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (s.recentPvpKills == null)
                        s.recentPvpKills = new List<PvpEntry>();
                    if (s.recentPvpDeaths == null)
                        s.recentPvpDeaths = new List<PvpEntry>();
                    playerStats[kvp.Key] = s;
                }
            }
            catch (Exception ex)
            {
                Puts($"Failed to load player_stats: {ex.Message}");
            }
        }


        private void SaveStats()
        {
            _playerStatsDirty = true;
        }

        private void SaveStatsImmediate()
        {
            _playerStatsDirty = false;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("player_stats", playerStats);
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"SaveStats error: {ex.Message}");
            }
        }


        private void LoadNpcStats()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, NpcStats>>("npc_stats");
                if (data == null || data.Count == 0) return;

                npcStats.Clear();
                foreach (var kvp in data)
                    npcStats[kvp.Key] = kvp.Value;
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to load npc_stats: {ex.Message}");
            }
        }


        private void SaveNpcStats()
        {
            _npcStatsDirty = true;
        }

        private void SaveNpcStatsImmediate()
        {
            _npcStatsDirty = false;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("npc_stats", npcStats);
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to save npc_stats: {ex.Message}");
            }
        }


        private void LoadAnimalStats()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, AnimalStats>>("animal_stats");
                if (data == null || data.Count == 0) return;

                animalStats.Clear();
                foreach (var kvp in data)
                    animalStats[kvp.Key] = kvp.Value;
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to load animal_stats: {ex.Message}");
            }
        }


        private void SaveAnimalStats()
        {
            _animalStatsDirty = true;
        }

        private void SaveAnimalStatsImmediate()
        {
            _animalStatsDirty = false;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("animal_stats", animalStats);
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to save animal_stats: {ex.Message}");
            }
        }


        private void LoadEnvironmentalStats()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, int>>("environmental_stats");
                if (data != null)
                    environmentalDeaths = data;
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to load environmental_stats: {ex.Message}");
            }
        }


        private void SaveEnvironmentalStats()
        {
            _envStatsDirty = true;
        }

        private void SaveEnvironmentalStatsImmediate()
        {
            _envStatsDirty = false;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("environmental_stats", environmentalDeaths);
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"Failed to save environmental_stats: {ex.Message}");
            }
        }

        private void FlushDirtyStats()
        {
            FlushDirtyStats(force: false);
        }

        private void FlushDirtyStats(bool force)
        {
            if (force || _playerStatsDirty) SaveStatsImmediate();
            if (force || _npcStatsDirty) SaveNpcStatsImmediate();
            if (force || _animalStatsDirty) SaveAnimalStatsImmediate();
            if (force || _envStatsDirty) SaveEnvironmentalStatsImmediate();
        }


        private void CleanupOldPlayerStats()
        {
            if (config.PlayerStatsCleanupDays <= 0) return;

            DateTime cutoff = DateTime.UtcNow.AddDays(-config.PlayerStatsCleanupDays);
            var toRemove = new List<string>();

            foreach (var kvp in playerStats)
            {
                if (kvp.Value.lastSeen < cutoff)
                    toRemove.Add(kvp.Key);
            }

            if (toRemove.Count == 0) return;

            foreach (string id in toRemove)
            {
                playerStats.Remove(id);
                loginTimes.Remove(id);
                sessionStarts.Remove(id);
                lastActivity.Remove(id);
                idleWarned.Remove(id);
            }

            if (config.DebugMode)
                Puts($"Cleaned up {toRemove.Count} inactive player stats (>{config.PlayerStatsCleanupDays} days old)");

            SaveStats();
        }

        /// <summary>
        /// Oxide lang files are keyed by lang.GetLanguage(userId), which defaults to "en"
        /// and is NOT the Rust client flag. Copy the client language onto the player so
        /// oxide/lang/de/LiveStats.json (and the other suite files) are actually used.
        /// </summary>
        private void SyncOxideLanguage(BasePlayer player, string hint = null)
        {
            if (player == null || !player.userID.IsSteamId()) return;

            string current = null;
            try { current = lang.GetLanguage(player.UserIDString); } catch { }
            if (string.IsNullOrEmpty(current)) current = "en";

            string rawConn = null;
            string rawInfo = null;
            try { rawConn = player.net?.connection?.language; } catch { }
            try { rawInfo = player.net?.connection?.info?.GetString("global.language", null); } catch { }

            string target;
            if (config != null && !string.IsNullOrWhiteSpace(config.LanguageForce))
                target = ResolveLangFolder(config.LanguageForce.Trim());
            else if (config != null && !config.LanguageSyncFromClient)
                target = current;
            else
            {
                // hint = OnPlayerLanguageChanged (live UI). connection.language never
                // updates. connection.info is one change behind the hook.
                string source = FirstNonEmpty(hint, rawInfo, rawConn, current, "en");
                target = ResolveLangFolder(source);
            }

            if (string.IsNullOrEmpty(target)) target = "en";

            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                lang.SetLanguage(target, player.UserIDString);

            if (config == null || config.LanguageLog || config.DebugMode)
            {
                bool pack = LangFileExists(Name, target);
                Puts($"[Lang] {player.displayName} hint={hint ?? "-"} conn={rawConn ?? "-"} info={rawInfo ?? "-"} oxide={current} -> {target} pack={(pack ? "yes" : "MISSING oxide/lang/" + target + "/" + Name + ".json")}");
            }
        }

        public void API_SyncOxideLanguage(BasePlayer player) => SyncOxideLanguage(player);

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private string ResolveLangFolder(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "en";
            code = code.Trim();

            var candidates = new List<string>();
            void add(string c)
            {
                if (string.IsNullOrWhiteSpace(c)) return;
                c = c.Trim();
                foreach (var existing in candidates)
                {
                    if (string.Equals(existing, c, StringComparison.OrdinalIgnoreCase)) return;
                }
                candidates.Add(c);
            }

            add(code);
            string lower = code.ToLowerInvariant();
            if (lower == "pirate" || lower == "en-pt" || lower == "en_pt")
            {
                add("en-pt");
                add("en-PT");
                add("pirate");
            }
            else if (lower == "zh" || lower == "zh-cn" || lower == "zh_cn" || lower == "zh-hans" || lower == "cn")
            {
                add("zh-CN");
                add("zh-cn");
            }
            else if (lower == "zh-tw" || lower == "zh_tw" || lower == "zh-hant" || lower == "tw")
            {
                add("zh-TW");
                add("zh-tw");
            }
            else if (lower == "pt" || lower == "pt-br" || lower == "pt_br")
            {
                add("pt-BR");
                add("pt-br");
                add("pt");
            }
            else if (lower == "pt-pt" || lower == "pt_pt")
            {
                add("pt-PT");
                add("pt-pt");
                add("pt");
            }
            else if (lower.StartsWith("es"))
            {
                add("es");
                add("es-ES");
                add("es-419");
            }
            else if (lower == "nb" || lower == "nn")
            {
                add("no");
            }
            else if (lower == "sv" || lower == "sv-se" || lower == "sv_se")
            {
                add("sv-SE");
                add("sv");
            }
            else if (lower == "en-pt" || lower == "en_pt")
            {
                add("en-PT");
                add("en-pt");
                add("pirate");
            }

            int dash = code.IndexOf('-');
            int under = code.IndexOf('_');
            int cut = dash > 0 ? dash : under;
            if (cut > 0) add(code.Substring(0, cut));

            foreach (var c in candidates)
            {
                if (LangFileExists(Name, c)) return c;
            }

            return candidates.Count > 0 ? candidates[0] : "en";
        }

        private bool LangFileExists(string pluginName, string code)
        {
            if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(code)) return false;
            try
            {
                string dir = Interface.Oxide.LangDirectory;
                if (string.IsNullOrEmpty(dir)) return false;
                string path = Path.Combine(dir, code, pluginName + ".json");
                if (File.Exists(path)) return true;
                // Windows is case-insensitive; still try common casings
                string parent = Path.Combine(dir, code);
                if (!Directory.Exists(parent)) return false;
                foreach (var file in Directory.GetFiles(parent, "*.json"))
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(file), pluginName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void LogLanguagePackInventory()
        {
            try
            {
                string dir = Interface.Oxide.LangDirectory;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Puts("[Lang] oxide/lang folder not found — translations cannot load.");
                    return;
                }

                var found = new List<string>();
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string code = Path.GetFileName(sub);
                    if (LangFileExists(Name, code))
                        found.Add(code);
                }
                found.Sort(StringComparer.OrdinalIgnoreCase);

                if (found.Count == 0)
                    Puts($"[Lang] No {Name}.json packs under {dir} — only RegisterMessages English will be used. Copy lang/<code>/{Name}.json there.");
                else
                    Puts($"[Lang] Packs on disk for {Name}: {string.Join(", ", found)}");

                if (found.Count == 1 && string.Equals(found[0], "en", StringComparison.OrdinalIgnoreCase))
                    Puts("[Lang] WARNING: only English exists. Dutch/Russian/Chinese/etc. will not appear until you copy those folders into oxide/lang/");
            }
            catch (Exception ex)
            {
                Puts($"[Lang] inventory failed: {ex.Message}");
            }
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            SyncOxideLanguage(player);
            timer.Once(2f, () =>
            {
                if (player != null && player.IsConnected)
                    SyncOxideLanguage(player);
            });
            string id = player.UserIDString;
            DateTime now = DateTime.UtcNow;
            loginTimes[id] = now;       // banking point
            sessionStarts[id] = now;    // true continuous session start (never reset until disconnect)
            if (!playerStats.ContainsKey(id))
                playerStats[id] = new PlayerStats();
            playerStats[id].lastName = player.displayName;
            playerStats[id].lastSeen = now;
            // Sync death state from the live entity (e.g. reconnecting while still dead)
            playerStats[id].isPlayerDead = player.IsDead();

            // Kill streaks are session-scoped: never carry over from a previous login
            if (config.EnableKillStreaks)
            {
                playerStats[id].currentKillStreak = 0;
                playerStats[id].isOnKillStreak = false;
            }

            if (config.EnableIdleKick)
            {
                lastActivity[id] = Time.realtimeSinceStartup;
                idleWarned.Remove(id);
            }
        }

        private void OnPlayerLanguageChanged(BasePlayer player, string newLang)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            Puts($"[Lang] {player.displayName} client UI language changed -> {newLang ?? "-"}");
            SyncOxideLanguage(player, newLang);
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null || !player.userID.IsSteamId()) return;

            // Bank only this player's session – do not touch other online players
            SavePlayerTime(player.UserIDString);

            // End any active kill streak on disconnect (streaks do not span sessions)
            if (config.EnableKillStreaks && playerStats.TryGetValue(player.UserIDString, out var stats))
            {
                if (stats.currentKillStreak > 0 || stats.isOnKillStreak)
                {
                    if (config.DebugMode && stats.isOnKillStreak)
                        Puts($"[KillStreak] {player.displayName} session ended with streak of {stats.currentKillStreak}");
                    stats.currentKillStreak = 0;
                    stats.isOnKillStreak = false;
                    SaveStats();
                }
            }

            if (config.EnableIdleKick)
            {
                string id = player.UserIDString;
                lastActivity.Remove(id);
                idleWarned.Remove(id);
            }
        }

        /// <summary>
        /// Player has respawned — clear the dead flag so isPlayerDead is only true
        /// between death and respawn.
        /// </summary>
        void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            string id = player.UserIDString;
            if (!playerStats.TryGetValue(id, out var stats))
                stats = playerStats[id] = new PlayerStats();
            stats.isPlayerDead = false;
            stats.lastSeen = DateTime.UtcNow;
            if (config.DebugMode)
                Puts($"[isPlayerDead] {player.displayName} respawned");
        }

        /// <summary>
        /// Bank the remaining unbanked minutes for a single player into totalMinutesPlayed.
        /// Called on disconnect. Removes both banking point and true session start.
        /// </summary>
        private void SavePlayerTime(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            DateTime now = DateTime.UtcNow;
            bool updated = false;

            // Bank remaining time since last bank point
            if (loginTimes.TryGetValue(id, out DateTime loginTime))
            {
                int sessionMinutes = (int)(now - loginTime).TotalMinutes;
                if (sessionMinutes > 0)
                {
                    if (!playerStats.TryGetValue(id, out var stats))
                        stats = playerStats[id] = new PlayerStats();
                    stats.totalMinutesPlayed += sessionMinutes;
                    stats.lastSeen = now;
                    updated = true;
                }
            }

            if (updated) SaveStats();

            // Cleanup both dictionaries
            loginTimes.Remove(id);
            sessionStarts.Remove(id);
        }

        /// <summary>
        /// Bank session time for every currently tracked player (used by the 5-minute
        /// persistence timer and on Unload). After banking, only the banking point
        /// (loginTimes) is reset. The true session start (sessionStarts) is left alone
        /// so the displayed "current session" continues to grow without resetting to 0.
        /// </summary>
        private void SaveAllPlayerTimes()
        {
            DateTime now = DateTime.UtcNow;
            bool any = false;

            foreach (var kvp in new List<KeyValuePair<string, DateTime>>(loginTimes))
            {
                string id = kvp.Key;
                DateTime lastBankTime = kvp.Value;
                if (!playerStats.TryGetValue(id, out var stats))
                    stats = playerStats[id] = new PlayerStats();

                int sessionMinutes = (int)(now - lastBankTime).TotalMinutes;
                if (sessionMinutes > 0)
                {
                    stats.totalMinutesPlayed += sessionMinutes;
                    loginTimes[id] = now;   // only reset the banking point
                    stats.lastSeen = now;
                    any = true;
                }
            }

            if (any) SaveStats();
        }

        // ==================== IDLE / AFK KICK ====================
        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!config.EnableIdleKick || player == null || !player.userID.IsSteamId()) return;
            if (input == null) return;

            // Any meaningful input counts as activity
            if (input.current.buttons != 0 ||
                Mathf.Abs(input.current.mouseDelta.x) > 0.05f ||
                Mathf.Abs(input.current.mouseDelta.y) > 0.05f)
            {
                string id = player.UserIDString;
                lastActivity[id] = Time.realtimeSinceStartup;
                if (idleWarned.Count > 0)
                    idleWarned.Remove(id);
            }
        }

        private void CheckIdlePlayers()
        {
            if (!config.EnableIdleKick) return;

            float now = Time.realtimeSinceStartup;
            float warnSeconds = config.IdleWarningMinutes * 60f;
            float kickSeconds = config.IdleKickMinutes * 60f;

            // Safety: kick time must be greater than warning time
            if (kickSeconds <= warnSeconds)
                kickSeconds = warnSeconds + 60f;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.userID.IsSteamId()) continue;
                if (player.IsSleeping()) continue; // intentional sleepers are left alone
                if (permission.UserHasPermission(player.UserIDString, config.IdleBypassPermission)) continue;

                string id = player.UserIDString;

                if (!lastActivity.TryGetValue(id, out float last))
                {
                    lastActivity[id] = now;
                    continue;
                }

                float idle = now - last;

                if (idle >= kickSeconds)
                {
                    string msg = lang.GetMessage("IdleKickMessage", this, id);
                    player.Kick(msg);
                    lastActivity.Remove(id);
                    idleWarned.Remove(id);

                    if (config.DebugMode)
                        Puts($"[Idle Kick] Kicked {player.displayName} for being idle ({idle / 60f:F1} minutes)");
                    continue;
                }

                if (idle >= warnSeconds && !idleWarned.Contains(id))
                {
                    idleWarned.Add(id);
                    int remaining = Mathf.Max(1, Mathf.CeilToInt((kickSeconds - idle) / 60f));
                    string msg = string.Format(lang.GetMessage("IdleWarningMessage", this, id), remaining);
                    player.ChatMessage(msg);

                    if (config.DebugMode)
                        Puts($"[Idle Kick] Warned {player.displayName} for idle ({idle / 60f:F1} minutes)");
                }
            }
        }
        // ==================== END IDLE / AFK KICK ====================


        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "LiveStatsWorld")
            {
                LiveStatsWorld = plugin;
                worldExtensionReady = true;
                Puts("LiveStatsWorld extension loaded.");
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "LiveStatsWorld")
            {
                LiveStatsWorld = null;
                worldExtensionReady = false;
                Puts("LiveStatsWorld extension unloaded.");
            }
        }

        // ==================== DUAL WIPE DETECTION ====================
        // Primary: OnNewSave (Oxide fires when a new .sav is created).
        // Fallback: persistent map identity (seed|worldsize|level). Catches wipes where
        // OnNewSave was missed (plugin/Oxide not loaded on first boot of the new save,
        // soft wipes, host save renaming, etc.).

        private string GetCurrentMapIdentity()
        {
            return $"{ConVar.Server.seed}|{ConVar.Server.worldsize}|{ConVar.Server.level ?? ""}";
        }

        private void LoadWipeIdentity()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<WipeIdentity>(WipeDataFile);
                _lastMapIdentity = data?.Identity ?? "";
                if (!string.IsNullOrEmpty(data?.LastWipeDetected) &&
                    DateTime.TryParse(data.LastWipeDetected, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var wipeTime))
                {
                    _wipeStartTime = wipeTime;
                }
                else
                {
                    _wipeStartTime = DateTime.UtcNow;
                }
            }
            catch
            {
                _lastMapIdentity = "";
                _wipeStartTime = DateTime.UtcNow;
            }
        }

        private void SaveWipeIdentity(string identity)
        {
            try
            {
                // On a real wipe, LastWipeDetected is refreshed; on first-run identity record it is set once.
                DateTime wipeTime = DateTime.UtcNow;
                _wipeStartTime = wipeTime;
                Interface.Oxide.DataFileSystem.WriteObject(WipeDataFile, new WipeIdentity
                {
                    Identity = identity,
                    LastWipeDetected = wipeTime.ToString("o")
                });
                _lastMapIdentity = identity;
            }
            catch (Exception ex)
            {
                Puts($"Failed to save wipe identity: {ex.Message}");
            }
        }

        /// <summary>
        /// Compare current map identity against the last stored one.
        /// First run only records identity (does not clear). Subsequent map changes clear stats.
        /// </summary>
        private void CheckForWipe(string reason)
        {
            if (!config.ClearStatsOnWipe) return;

            string current = GetCurrentMapIdentity();
            if (string.IsNullOrEmpty(_lastMapIdentity))
            {
                // First run ever – record current map, do NOT clear
                SaveWipeIdentity(current);
                Puts($"First run – recorded map identity: {current}");
                return;
            }

            if (current != _lastMapIdentity)
            {
                Puts($"Wipe detected ({reason}) – old: {_lastMapIdentity} -> new: {current}");
                ClearAllStatsForWipe();
                SaveWipeIdentity(current);
            }
            else if (config.DebugMode)
            {
                Puts($"No wipe – map identity unchanged: {current}");
            }
        }

        // Standard uMod wipe detection – fired by Oxide when a new .sav is created (map wipe).
        void OnNewSave(string filename)
        {
            if (!config.ClearStatsOnWipe) return;

            Puts($"OnNewSave fired ({filename}) – clearing stats...");
            ClearAllStatsForWipe();
            SaveWipeIdentity(GetCurrentMapIdentity());
        }

        private void ClearAllStatsForWipe()
        {
            playerStats.Clear();
            npcStats.Clear();
            animalStats.Clear();
            environmentalDeaths.Clear();
            recentDeaths.Clear();
            lastEnvironmentalCause.Clear();
            lastNpcAttacker.Clear();
            lastPlayerAttacker.Clear();
            lastPlayerAttackerId.Clear();
            lastAnimalAttacker.Clear();
            loginTimes.Clear();
            sessionStarts.Clear();
            lastActivity.Clear();
            idleWarned.Clear();
            processedDeaths.Clear();
            processedNpcKills.Clear();

            SeedNpcTypes();
            SeedAnimalTypes();
            SeedEnvironmentalStats();

            FlushDirtyStats(force: true);

            Puts("Stats automatically cleared due to map wipe.");
        }
        // ==================== END DUAL WIPE DETECTION ====================

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null || info == null || info.damageTypes == null) return null;
            // Ignore buildings, deployables, barrels, corpses — only players / NPCs / animals are scored.
            if (!IsStatsRelevantVictim(entity)) return null;
            ulong netId = entity.net.ID.Value;

            if (info.damageTypes.Has(Rust.DamageType.Hunger)) lastEnvironmentalCause[netId] = "Starvation";
            else if (info.damageTypes.Has(Rust.DamageType.Thirst)) lastEnvironmentalCause[netId] = "Dehydration";
            else if (info.damageTypes.Has(Rust.DamageType.Cold)) lastEnvironmentalCause[netId] = "Freezing";
            else if (info.damageTypes.Has(Rust.DamageType.Radiation)) lastEnvironmentalCause[netId] = "Radiation Poisoning";
            else if (info.damageTypes.Has(Rust.DamageType.Heat)) lastEnvironmentalCause[netId] = "Heat";
            else if (info.damageTypes.Has(Rust.DamageType.Drowned)) lastEnvironmentalCause[netId] = "Drowning";
            else if (info.damageTypes.Has(Rust.DamageType.Poison)) lastEnvironmentalCause[netId] = "Poison";
            else if (info.damageTypes.Has(Rust.DamageType.Fall)) lastEnvironmentalCause[netId] = "Fall Damage";

            // Exclusive last-attacker: only the most recent damager type is kept so NPC,
            // animal, and player attribution stay consistent (true last-hit wins).
            // Never record the victim as their own PvP attacker — env ticks and self-shots
            // often set InitiatorPlayer to the victim, which previously became a fake PvP kill.
            if (info.InitiatorPlayer != null && info.InitiatorPlayer.userID.IsSteamId()
                && !IsSamePlayer(entity, info.InitiatorPlayer))
            {
                lastPlayerAttacker[netId] = info.InitiatorPlayer.displayName;
                lastPlayerAttackerId[netId] = info.InitiatorPlayer.UserIDString;
                lastNpcAttacker.Remove(netId);
                lastAnimalAttacker.Remove(netId);
            }
            else if (info.Initiator != null)
            {
                string initShort = info.Initiator.ShortPrefabName.ToLowerInvariant();
                if (initShort.Contains("scientist") || initShort.Contains("ch47scientists") || initShort.Contains("npc") || initShort.Contains("raid") || initShort.Contains("bandit") || initShort.Contains("sentry") || initShort.Contains("cargo") || initShort.Contains("bradley") || initShort.Contains("tunneldweller"))
                {
                    lastNpcAttacker[netId] = GetKillerName(info.Initiator);
                    lastPlayerAttacker.Remove(netId);
                    lastPlayerAttackerId.Remove(netId);
                    lastAnimalAttacker.Remove(netId);
                }
                else if (IsValidAnimal(info.Initiator))
                {
                    // Same pattern as NPCs — covers crocodile drag/bite and other cases
                    // where HitInfo.Initiator is null on the killing blow.
                    lastAnimalAttacker[netId] = GetKillerName(info.Initiator);
                    lastPlayerAttacker.Remove(netId);
                    lastPlayerAttackerId.Remove(netId);
                    lastNpcAttacker.Remove(netId);
                }
            }

            // Soft size cap — full prune runs on a timer
            if (lastEnvironmentalCause.Count > MaxDamageTrackEntries ||
                lastNpcAttacker.Count > MaxDamageTrackEntries ||
                lastPlayerAttacker.Count > MaxDamageTrackEntries ||
                lastPlayerAttackerId.Count > MaxDamageTrackEntries ||
                lastAnimalAttacker.Count > MaxDamageTrackEntries)
            {
                PruneDamageTracking();
            }
            return null;
        }

        /// <summary>
        /// Drop stale netIds from damage-tracking dictionaries so they cannot grow without bound
        /// for entities that take damage but never die (or for recycled netIds).
        /// </summary>
        private void PruneDamageTracking()
        {
            float now = Time.realtimeSinceStartup;
            // Avoid re-entry storms from the soft-cap path
            if (now - _lastDamageTrackPrune < 5f) return;
            _lastDamageTrackPrune = now;

            PruneDamageDict(lastEnvironmentalCause);
            PruneDamageDict(lastNpcAttacker);
            PruneDamageDict(lastPlayerAttacker);
            PruneDamageDict(lastPlayerAttackerId);
            PruneDamageDict(lastAnimalAttacker);
        }

        private void PruneDamageDict(Dictionary<ulong, string> dict)
        {
            if (dict == null || dict.Count == 0) return;
            _damageTrackPruneBuffer.Clear();
            foreach (var id in dict.Keys)
            {
                try
                {
                    var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id));
                    if (ent == null || ent.IsDestroyed)
                        _damageTrackPruneBuffer.Add(id);
                }
                catch
                {
                    _damageTrackPruneBuffer.Add(id);
                }
            }
            for (int i = 0; i < _damageTrackPruneBuffer.Count; i++)
                dict.Remove(_damageTrackPruneBuffer[i]);

            // Hard emergency trim if still over cap (oldest-ish keys via enumeration order)
            if (dict.Count > MaxDamageTrackEntries)
            {
                _damageTrackPruneBuffer.Clear();
                int excess = dict.Count - (MaxDamageTrackEntries / 2);
                foreach (var id in dict.Keys)
                {
                    _damageTrackPruneBuffer.Add(id);
                    if (_damageTrackPruneBuffer.Count >= excess) break;
                }
                for (int i = 0; i < _damageTrackPruneBuffer.Count; i++)
                    dict.Remove(_damageTrackPruneBuffer[i]);
            }
            _damageTrackPruneBuffer.Clear();
        }

        object OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (config.DebugMode && entity != null && entity.net != null)
                Puts($"[Debug] OnEntityDeath called: {entity.ShortPrefabName} | NetID: {entity.net.ID.Value}");

            if (entity == null || entity.net == null) return null;
            if (entity is BaseCorpse) return null;

            string shortName = entity.ShortPrefabName.ToLowerInvariant();
            ulong netId = entity.net.ID.Value;

            if (ShouldIgnoreDeath(entity, shortName, info, netId))
                return null;

            if (processedDeaths.Contains(netId)) return null;
            processedDeaths.Add(netId);
            timer.Once(config.DeathDedupeWindow, () => processedDeaths.Remove(netId));

            var classification = ClassifyDeath(entity, shortName, info);

            Vector3 pos = entity.transform?.position ?? Vector3.zero;
            string grid = PositionToGrid(pos);

            string victimName = ResolveVictimName(classification.IsRealPlayer, classification.VictimPlayer, entity);

            var details = ResolveDeathDetails(entity, info, shortName, netId, classification);

            lastEnvironmentalCause.Remove(netId);
            lastNpcAttacker.Remove(netId);
            lastPlayerAttacker.Remove(netId);
            lastPlayerAttackerId.Remove(netId);
            lastAnimalAttacker.Remove(netId);

            if (details.Cause == "Decay")
                return null;

            if (config.DebugMode)
            {
                Puts($"[Debug] Processed Death: {victimName} | Killer: {details.KillerNameRaw} | Grid: {grid}");
            }

            TrackDeathStats(classification, details, victimName, netId, grid);

            if (config.EnableRecentDeaths)
            {
                RecordRecentDeathAndAnnounce(victimName, details, grid, shortName, classification);
            }

            return null;
        }

        private bool ShouldIgnoreDeath(BaseCombatEntity entity, string shortName, HitInfo info, ulong netId)
        {
            // Never drop animal deaths (wildlife + livestock).
            if (IsValidAnimal(entity) || IsAnimalPrefabName(shortName))
                return false;

            if (shortName.Contains("loot-barrel") || shortName.Contains("oil-barrel") || shortName.Contains("barrel"))
                return true;

            bool isScientistDeath = shortName.Contains("scientist") || shortName.Contains("ch47scientists");

            if (!isScientistDeath && (shortName.Contains("debris") || shortName.Contains("foundation") || shortName.Contains("wall") ||
                shortName.Contains("floor") || shortName.Contains("pillar") || shortName.Contains("roof") || shortName.Contains("stair") ||
                shortName.Contains("frame") || shortName.Contains("door") || shortName.Contains("window") || shortName.Contains("block") ||
                shortName.Contains("ramp") || shortName.Contains("barricade") || shortName.Contains("workbench") || shortName.Contains("chair") ||
                shortName.Contains("generator") || shortName.Contains("splitter") || shortName.Contains("combiner") ||
                shortName.Contains("electrical") || shortName.Contains("sign") || shortName.Contains("light") || shortName.Contains("shelf") ||
                shortName.Contains("locker") || shortName.Contains("vending") || shortName.Contains("fridge") || shortName.Contains("box") ||
                shortName.Contains("storage") || shortName.Contains("cupboard") || shortName.Contains("turret") || shortName.Contains("sam") ||
                shortName.Contains("trap")))
                return true;

            if (!isScientistDeath && (IsDeployedItem(entity) || IsPlantOrCrop(entity)))
                return true;

            bool isHorse = shortName.Contains("horse");
            bool victimIsVehicle = IsVehicle(entity) && !isHorse;
            BaseEntity initiator = info?.Initiator;
            bool killerIsVehicle = initiator != null && IsVehicle(initiator) && !initiator.ShortPrefabName.ToLowerInvariant().Contains("horse");

            if (victimIsVehicle && killerIsVehicle) return true;
            if (!isScientistDeath && victimIsVehicle) return true;

            return false;
        }

        /// <summary>
        /// True when the damager/killer is the same Steam player as the victim.
        /// Used so self-inflicted deaths cannot be scored as PvP.
        /// </summary>
        private static bool IsSamePlayer(BaseCombatEntity victim, BasePlayer other)
        {
            if (victim == null || other == null || !other.userID.IsSteamId()) return false;
            if (ReferenceEquals(victim, other) || victim == other) return true;
            var vp = victim as BasePlayer;
            return vp != null && vp.userID.IsSteamId() && vp.userID == other.userID;
        }

        private static bool IsSamePlayerId(string victimSteamId, string killerSteamId)
        {
            return !string.IsNullOrEmpty(victimSteamId)
                && !string.IsNullOrEmpty(killerSteamId)
                && string.Equals(victimSteamId, killerSteamId, StringComparison.Ordinal);
        }

        private DeathClassification ClassifyDeath(BaseCombatEntity entity, string shortName, HitInfo info)
        {
            BasePlayer victimPlayer = entity as BasePlayer;
            BasePlayer killerPlayer = info?.InitiatorPlayer;

            bool isRealPlayer = victimPlayer != null && victimPlayer.userID.IsSteamId();
            bool isAnimal = IsValidAnimal(entity);
            bool isNpcEntity = false;

            if (!isRealPlayer && !isAnimal)
            {
                if (shortName.Contains("scientist") || shortName.Contains("ch47scientists") || shortName.Contains("npc") ||
                    shortName.Contains("raid") || shortName.Contains("bandit") || shortName.Contains("sentry") ||
                    shortName.Contains("cargo") || shortName.Contains("turret") || shortName.Contains("tunneldweller") ||
                    entity.net.ID.Value.ToString().StartsWith("534922"))
                {
                    isNpcEntity = true;
                }
            }

            bool killerIsRealPlayer = killerPlayer != null && killerPlayer.userID.IsSteamId();

            return new DeathClassification
            {
                IsRealPlayer = isRealPlayer,
                IsAnimal = isAnimal,
                IsNpcEntity = isNpcEntity,
                VictimPlayer = victimPlayer,
                KillerPlayer = killerPlayer,
                KillerIsRealPlayer = killerIsRealPlayer
            };
        }

        private string ResolveVictimName(bool isRealPlayer, BasePlayer victimPlayer, BaseCombatEntity entity)
        {
            if (isRealPlayer && victimPlayer != null)
            {
                string name = victimPlayer.displayName;
                if (string.IsNullOrWhiteSpace(name) || name.ToLowerInvariant() == "player")
                {
                    if (playerStats.TryGetValue(victimPlayer.UserIDString, out var storedStats) &&
                        !string.IsNullOrWhiteSpace(storedStats.lastName))
                        return storedStats.lastName;
                    return victimPlayer.UserIDString;
                }
                return name;
            }
            return GetKillerName(entity);
        }

        private DeathDetails ResolveDeathDetails(BaseCombatEntity entity, HitInfo info, string shortName, ulong netId,
            DeathClassification classification)
        {
            BaseEntity initiator = info?.Initiator;
            bool isHeadshot = info?.isHeadshot == true;
            bool isSuicide = false;

            string killerNameRaw = "Environment";
            string npcTypeKey = classification.IsNpcEntity ? GetKillerName(entity) : null;
            string npcKillerTypeKey = null;
            string cause = GetRefinedCause(info);

            // Always resolve animal names for per-player hunter tracking.
            // Global animalStats counters remain gated by EnableAnimalStats further below.
            string animalVictim = classification.IsAnimal ? GetKillerName(entity) : null;
            string animalKiller = null;
            string killerPlayerId = null;

            // --- Direct initiator classification (mirrors NPC / animal / player equally) ---
            // Ignore initiator == victim. Rust often sets HitInfo.Initiator to the dying
            // animal (croc/bear/etc.), which used to look like the animal killed itself
            // and blocked the player's animalsKilled credit.
            if (initiator != null && initiator != entity)
            {
                string initShort = initiator.ShortPrefabName.ToLowerInvariant();
                if (initShort.Contains("scientist") || initShort.Contains("ch47scientists") || initShort.Contains("npc") ||
                    initShort.Contains("raid") || initShort.Contains("bandit") || initShort.Contains("sentry") ||
                    initShort.Contains("cargo") || initShort.Contains("bradley") || initShort.Contains("tunneldweller"))
                {
                    npcKillerTypeKey = GetKillerName(initiator);
                    killerNameRaw = npcKillerTypeKey;
                }
                else if (IsValidAnimal(initiator))
                {
                    animalKiller = GetKillerName(initiator);
                    killerNameRaw = animalKiller;
                }
            }

            if (lastEnvironmentalCause.TryGetValue(netId, out var lastCause))
            {
                cause = lastCause;
                lastEnvironmentalCause.Remove(netId);
                // Keep attacker fallbacks — env damage type may coexist with a real
                // animal/NPC/player initiator (e.g. drowning while being dragged by a croc).
            }

            // Fallbacks when HitInfo.Initiator was null at death (exclusive last-attacker
            // means at most one of these dicts will have an entry).
            if ((killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw)) &&
                lastNpcAttacker.TryGetValue(netId, out var lastNpc))
            {
                killerNameRaw = lastNpc;
                npcKillerTypeKey = lastNpc;
                lastNpcAttacker.Remove(netId);
            }

            if ((killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw)) &&
                lastAnimalAttacker.TryGetValue(netId, out var lastAnimal) &&
                !string.IsNullOrEmpty(lastAnimal))
            {
                animalKiller = lastAnimal;
                killerNameRaw = lastAnimal;
                lastAnimalAttacker.Remove(netId);
            }

            string victimSteamIdEarly = classification.IsRealPlayer && classification.VictimPlayer != null
                ? classification.VictimPlayer.UserIDString
                : null;

            // Player fallback: name + Steam ID together (for kill credit when InitiatorPlayer is null).
            // Skip the victim themselves — bleed/fall/drown ticks often set InitiatorPlayer = victim
            // even after another player was the last real attacker.
            if (classification.KillerPlayer != null && classification.KillerPlayer.userID.IsSteamId()
                && !IsSamePlayer(entity, classification.KillerPlayer))
            {
                killerPlayerId = classification.KillerPlayer.UserIDString;
            }
            else if (lastPlayerAttackerId.TryGetValue(netId, out var lastPid) && !string.IsNullOrEmpty(lastPid)
                     && !IsSamePlayerId(victimSteamIdEarly, lastPid))
            {
                killerPlayerId = lastPid;
                lastPlayerAttackerId.Remove(netId);

                if (lastPlayerAttacker.TryGetValue(netId, out var storedName) && !string.IsNullOrEmpty(storedName))
                {
                    if (killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw))
                        killerNameRaw = storedName;
                    lastPlayerAttacker.Remove(netId);
                }
                else if (killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw))
                {
                    if (playerStats.TryGetValue(lastPid, out var ps) && !string.IsNullOrEmpty(ps.lastName))
                        killerNameRaw = ps.lastName;
                    else
                        killerNameRaw = lastPid;
                }
            }
            else if ((killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw)) &&
                     lastPlayerAttacker.TryGetValue(netId, out var lastPlayerOnly))
            {
                // Name without ID (legacy / edge case) — ignore if it is the victim's own name
                bool ownName = classification.VictimPlayer != null
                    && !string.IsNullOrEmpty(classification.VictimPlayer.displayName)
                    && string.Equals(lastPlayerOnly, classification.VictimPlayer.displayName, StringComparison.OrdinalIgnoreCase);
                if (!ownName)
                    killerNameRaw = lastPlayerOnly;
                lastPlayerAttacker.Remove(netId);
            }

            if (killerNameRaw == "Fire")
                cause = "Fire";

            bool isEnvSelfDamage = false;
            try
            {
                if (info?.damageTypes != null &&
                    (info.damageTypes.Has(Rust.DamageType.Drowned) ||
                     info.damageTypes.Has(Rust.DamageType.Fall) ||
                     info.damageTypes.Has(Rust.DamageType.Hunger) ||
                     info.damageTypes.Has(Rust.DamageType.Thirst) ||
                     info.damageTypes.Has(Rust.DamageType.Cold) ||
                     info.damageTypes.Has(Rust.DamageType.Heat) ||
                     info.damageTypes.Has(Rust.DamageType.Radiation) ||
                     info.damageTypes.Has(Rust.DamageType.Poison)))
                    isEnvSelfDamage = true;
            }
            catch { }
            if (!isEnvSelfDamage && !string.IsNullOrEmpty(cause))
            {
                string c = cause.ToLowerInvariant();
                if (c.Contains("drown") || c.Contains("fall") || c.Contains("starv") ||
                    c.Contains("dehydrat") || c.Contains("freez") || c.Contains("radiation") ||
                    c.Contains("poison") || c == "heat" || c == "fire")
                    isEnvSelfDamage = true;
            }

            string victimSteamId = victimSteamIdEarly;

            bool initiatorIsVictim = IsSamePlayer(entity, info?.InitiatorPlayer)
                || (classification.VictimPlayer != null && info?.Initiator == classification.VictimPlayer)
                || info?.Initiator == entity;

            bool killerIdIsVictim = IsSamePlayerId(victimSteamId, killerPlayerId);
            bool otherPlayerKiller = !string.IsNullOrEmpty(killerPlayerId) && !killerIdIsVictim;
            bool otherCombatKiller = otherPlayerKiller
                || !string.IsNullOrEmpty(npcKillerTypeKey)
                || !string.IsNullOrEmpty(animalKiller);

            bool suicideDamage = false;
            try
            {
                if (info?.damageTypes != null && info.damageTypes.Has(Rust.DamageType.Suicide))
                    suicideDamage = true;
            }
            catch { }

            // Self-inflicted: F1 /kill, shooting yourself, own grenade, or env ticks
            // where Rust sets InitiatorPlayer to the victim. Never score these as PvP
            // unless another player / NPC / animal was already identified as the killer.
            if (classification.IsRealPlayer && !otherCombatKiller
                && (initiatorIsVictim || killerIdIsVictim || suicideDamage))
            {
                killerPlayerId = null;
                animalKiller = null;
                npcKillerTypeKey = null;

                if (isEnvSelfDamage)
                {
                    isSuicide = false;
                    if (string.IsNullOrEmpty(cause) || cause == "Unknown" || cause == "Death")
                        cause = "Environment";
                    if (cause.IndexOf("drown", StringComparison.OrdinalIgnoreCase) >= 0)
                        killerNameRaw = "Drowning";
                    else
                        killerNameRaw = cause;
                }
                else
                {
                    isSuicide = true;
                    killerNameRaw = "Suicide";
                    cause = "Suicide";
                }
            }
            else if (info != null)
            {
                if (classification.KillerPlayer != null && !IsSamePlayer(entity, classification.KillerPlayer))
                {
                    killerNameRaw = classification.KillerPlayer.displayName ?? "Unknown";
                    killerPlayerId = classification.KillerPlayer.UserIDString;
                }
                else if (initiator != null && (killerNameRaw == "Environment" || string.IsNullOrEmpty(killerNameRaw)))
                {
                    killerNameRaw = GetKillerName(initiator);
                    if (string.IsNullOrEmpty(animalKiller) && IsValidAnimal(initiator))
                        animalKiller = killerNameRaw;
                }
            }

            return new DeathDetails
            {
                KillerNameRaw = killerNameRaw,
                Cause = cause,
                IsHeadshot = isHeadshot,
                IsSuicide = isSuicide,
                NpcTypeKey = npcTypeKey,
                NpcKillerTypeKey = npcKillerTypeKey,
                AnimalVictim = animalVictim,
                AnimalKiller = animalKiller,
                KillerPlayerId = killerPlayerId
            };
        }

        private void TrackDeathStats(DeathClassification classification, DeathDetails details, string victimName, ulong netId, string grid = "")
        {
            // Player kill credit: prefer live InitiatorPlayer, else lastPlayerAttackerId fallback
            // (same reliability as NPC/animal last-attacker fallbacks when Initiator is null at death).
            string killerPlayerId = null;
            if (classification.KillerPlayer != null && classification.KillerPlayer.userID.IsSteamId()
                && !IsSamePlayer(classification.VictimPlayer, classification.KillerPlayer))
                killerPlayerId = classification.KillerPlayer.UserIDString;
            else if (!string.IsNullOrEmpty(details.KillerPlayerId))
                killerPlayerId = details.KillerPlayerId;

            string victimSteamId = classification.IsRealPlayer && classification.VictimPlayer != null
                ? classification.VictimPlayer.UserIDString
                : null;
            bool selfKill = details.IsSuicide || IsSamePlayerId(victimSteamId, killerPlayerId);
            if (selfKill)
                killerPlayerId = null;

            // If the "animal killer" is the victim itself, it is not a real animal-on-animal kill.
            bool animalSelfInitiator = classification.IsAnimal
                && !string.IsNullOrEmpty(details.AnimalKiller)
                && !string.IsNullOrEmpty(details.AnimalVictim)
                && string.Equals(details.AnimalKiller, details.AnimalVictim, StringComparison.OrdinalIgnoreCase);
            if (animalSelfInitiator)
                details.AnimalKiller = null;

            bool creditedPlayerKill = false;
            // Player vs animal: do not require AnimalKiller to be empty. Rust sometimes
            // leaves the dying animal as HitInfo.Initiator even when a player dealt the kill.
            if (!selfKill && !string.IsNullOrEmpty(killerPlayerId)
                && details.NpcKillerTypeKey == null
                && (string.IsNullOrEmpty(details.AnimalKiller) || classification.IsAnimal))
            {
                if (!playerStats.TryGetValue(killerPlayerId, out var kStats))
                    kStats = playerStats[killerPlayerId] = new PlayerStats();

                kStats.kills++;
                creditedPlayerKill = true;

                string killerDisplayName = classification.KillerPlayer?.displayName
                    ?? (!string.IsNullOrEmpty(details.KillerNameRaw) && details.KillerNameRaw != "Environment"
                        ? details.KillerNameRaw
                        : (kStats.lastName ?? killerPlayerId));

                // Kill streak: consecutive kills of players, NPCs, and animals without dying
                if (config.EnableKillStreaks)
                {
                    kStats.currentKillStreak++;
                    if (kStats.currentKillStreak > kStats.bestKillStreak)
                        kStats.bestKillStreak = kStats.currentKillStreak;

                    int startAt = Mathf.Max(1, config.KillStreakStart);
                    kStats.isOnKillStreak = kStats.currentKillStreak >= startAt;
                }

                if (details.IsHeadshot)
                {
                    kStats.headshots++;
                    if (classification.IsNpcEntity) kStats.npcHeadshots++;
                    // Animals have no conventional headshot hitboxes — do not count animalHeadshots
                    else if (classification.IsRealPlayer) kStats.playerHeadshots++;
                }

                if (classification.IsNpcEntity)
                {
                    kStats.npcsKilled++;
                    // Track specific NPC type for per-type K/D
                    string npcType = details.NpcTypeKey ?? "Unknown";
                    if (kStats.npcsKilledByType == null)
                        kStats.npcsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (!kStats.npcsKilledByType.ContainsKey(npcType))
                        kStats.npcsKilledByType[npcType] = 0;
                    kStats.npcsKilledByType[npcType]++;
                }
                else if (classification.IsAnimal)
                {
                    kStats.animalsKilled++;
                    // Track specific animal type for hunter K/D
                    string animalType = details.AnimalVictim ?? "Unknown";
                    if (kStats.animalsKilledByType == null)
                        kStats.animalsKilledByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (!kStats.animalsKilledByType.ContainsKey(animalType))
                        kStats.animalsKilledByType[animalType] = 0;
                    kStats.animalsKilledByType[animalType]++;
                }
                else if (classification.IsRealPlayer)
                {
                    kStats.playersKilled++;
                    // Personal PvP kill feed — who this player killed
                    if (kStats.recentPvpKills == null)
                        kStats.recentPvpKills = new List<PvpEntry>();
                    RecordPvpEntry(kStats.recentPvpKills, victimName, details.IsHeadshot, grid);
                }

                SaveStats();
            }

            if (classification.IsRealPlayer && classification.VictimPlayer != null)
            {
                if (!playerStats.TryGetValue(classification.VictimPlayer.UserIDString, out var vStats))
                    vStats = playerStats[classification.VictimPlayer.UserIDString] = new PlayerStats();

                vStats.deaths++;

                if (details.IsSuicide)
                {
                    vStats.suicides++;
                }
                // Player killer: live InitiatorPlayer, or lastPlayerAttackerId fallback (no NPC/animal claim).
                // Self-kills are never PvP — they were already classified as suicide or env above.
                else if (!selfKill
                    && (classification.KillerIsRealPlayer || creditedPlayerKill || !string.IsNullOrEmpty(killerPlayerId))
                    && details.NpcKillerTypeKey == null
                    && string.IsNullOrEmpty(details.AnimalKiller)
                    && !IsSamePlayer(classification.VictimPlayer, classification.KillerPlayer)
                    && !IsSamePlayerId(victimSteamId, killerPlayerId))
                {
                    vStats.deathsByPlayer++;
                    // Personal PvP death feed — who killed this player
                    string killerName = classification.KillerPlayer?.displayName
                        ?? (details.KillerNameRaw ?? "Unknown");
                    if (vStats.recentPvpDeaths == null)
                        vStats.recentPvpDeaths = new List<PvpEntry>();
                    RecordPvpEntry(vStats.recentPvpDeaths, killerName, details.IsHeadshot, grid);
                }
                else if (details.NpcKillerTypeKey != null)
                {
                    vStats.deathsByNPC++;
                    // Track specific NPC type that killed this player (for per-type K/D)
                    string npcType = details.NpcKillerTypeKey ?? "Unknown";
                    if (vStats.deathsByNpcType == null)
                        vStats.deathsByNpcType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (!vStats.deathsByNpcType.ContainsKey(npcType))
                        vStats.deathsByNpcType[npcType] = 0;
                    vStats.deathsByNpcType[npcType]++;
                }
                else if (!string.IsNullOrEmpty(details.AnimalKiller))
                {
                    vStats.deathsByAnimal++;
                    // Track specific animal that killed this player (for hunter K/D)
                    string animalType = details.AnimalKiller;
                    if (vStats.deathsByAnimalType == null)
                        vStats.deathsByAnimalType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (!vStats.deathsByAnimalType.ContainsKey(animalType))
                        vStats.deathsByAnimalType[animalType] = 0;
                    vStats.deathsByAnimalType[animalType]++;
                }
                else if (details.Cause == "Radiation Poisoning")
                    vStats.deathsByRadiationPoisoning++;
                else
                {
                    // Other environmental or uncategorized deaths (e.g. fall, drowning, starvation, etc.)
                    string killerRaw = details.KillerNameRaw ?? "";
                    if (killerRaw == "Environment" || killerRaw == "Fire" || killerRaw == "Heat" ||
                        killerRaw == "Burning" || killerRaw == "Freezing" || killerRaw == "Drowning" ||
                        killerRaw == "Drowned" ||
                        killerRaw == "Starvation" || killerRaw == "Dehydration" || killerRaw == "Poison" ||
                        killerRaw == "FallDamage" || killerRaw == "Fall Damage")
                    {
                        vStats.deathsByEnvironment++;
                    }
                }

                // Mark player as dead until respawn; end any active kill streak
                vStats.isPlayerDead = true;
                if (config.EnableKillStreaks && (vStats.currentKillStreak > 0 || vStats.isOnKillStreak))
                {
                    if (config.DebugMode && vStats.isOnKillStreak)
                        Puts($"[KillStreak] {victimName} ended a streak of {vStats.currentKillStreak}");
                    vStats.currentKillStreak = 0;
                    vStats.isOnKillStreak = false;
                }

                vStats.lastSeen = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(victimName) && victimName != classification.VictimPlayer.UserIDString)
                    vStats.lastName = victimName;

                SaveStats();
            }

            if (details.NpcTypeKey != null)
            {
                if (!npcStats.TryGetValue(details.NpcTypeKey, out var nStats))
                    nStats = npcStats[details.NpcTypeKey] = new NpcStats { prefabName = details.NpcTypeKey };

                nStats.deaths++;
                nStats.lastEvent = DateTime.UtcNow;

                if (classification.KillerPlayer != null)
                    nStats.lastPlayerToKillIt = classification.KillerPlayer.displayName;
                else if (!string.IsNullOrEmpty(killerPlayerId))
                {
                    // Player kill credited via lastPlayerAttackerId fallback
                    if (playerStats.TryGetValue(killerPlayerId, out var ps) && !string.IsNullOrEmpty(ps.lastName))
                        nStats.lastPlayerToKillIt = ps.lastName;
                    else if (!string.IsNullOrEmpty(details.KillerNameRaw) && details.KillerNameRaw != "Environment")
                        nStats.lastPlayerToKillIt = details.KillerNameRaw;
                }
                else if (details.NpcKillerTypeKey != null)
                    nStats.lastNpcThatKilledIt = details.NpcKillerTypeKey;

                SaveNpcStats();
            }

            if (details.NpcKillerTypeKey != null)
            {
                // Guard against double-counting: the same victim netId must only award
                // one kill to an NPC type, even if OnEntityDeath fires more than once.
                if (processedNpcKills.Contains(netId))
                {
                    if (config.DebugMode)
                        Puts($"[Debug] Skipped duplicate NPC kill award for {details.NpcKillerTypeKey} (victim netId {netId})");
                }
                else
                {
                    processedNpcKills.Add(netId);
                    timer.Once(config.DeathDedupeWindow, () => processedNpcKills.Remove(netId));

                    if (!npcStats.TryGetValue(details.NpcKillerTypeKey, out var nStats))
                        nStats = npcStats[details.NpcKillerTypeKey] = new NpcStats { prefabName = details.NpcKillerTypeKey };

                    nStats.kills++;
                    nStats.lastEvent = DateTime.UtcNow;

                    SaveNpcStats();
                }
            }

            if (config.EnableAnimalStats && details.AnimalVictim != null)
            {
                if (!animalStats.TryGetValue(details.AnimalVictim, out var aStats))
                    aStats = animalStats[details.AnimalVictim] = new AnimalStats();

                aStats.deaths++;
                aStats.lastEvent = DateTime.UtcNow;

                if (classification.KillerPlayer != null)
                    aStats.lastPlayerToKillIt = classification.KillerPlayer.displayName;
                else if (!string.IsNullOrEmpty(killerPlayerId))
                {
                    // Player kill credited via lastPlayerAttackerId fallback (same as NPC path)
                    if (playerStats.TryGetValue(killerPlayerId, out var ps) && !string.IsNullOrEmpty(ps.lastName))
                        aStats.lastPlayerToKillIt = ps.lastName;
                    else if (!string.IsNullOrEmpty(details.KillerNameRaw) && details.KillerNameRaw != "Environment")
                        aStats.lastPlayerToKillIt = details.KillerNameRaw;
                }
                else if (!string.IsNullOrEmpty(details.AnimalKiller))
                    aStats.lastAnimalThatKilledIt = details.AnimalKiller;
                else if (details.NpcKillerTypeKey != null)
                    aStats.lastNpcThatKilledIt = details.NpcKillerTypeKey;

                SaveAnimalStats();
            }

            if (config.EnableAnimalStats && !string.IsNullOrEmpty(details.AnimalKiller))
            {
                if (!animalStats.TryGetValue(details.AnimalKiller, out var aStats))
                    aStats = animalStats[details.AnimalKiller] = new AnimalStats();

                aStats.kills++;
                aStats.lastEvent = DateTime.UtcNow;

                SaveAnimalStats();
            }

            // Global environmental death counters (used by /envstats).
            // When PlayersOnlyEnv is true, only real-player deaths contribute to these totals.
            // NPC / animal deaths to environment are still tracked in their own stats, but are excluded here.
            bool isEnvironmentalKiller = details.KillerNameRaw == "Environment" || details.KillerNameRaw == "Fire" ||
                                         details.KillerNameRaw == "Heat" || details.KillerNameRaw == "Burning" ||
                                         details.KillerNameRaw == "Freezing" || details.KillerNameRaw == "Drowning" ||
                                         details.KillerNameRaw == "Starvation" || details.KillerNameRaw == "Dehydration" ||
                                         details.KillerNameRaw == "Poison" || details.KillerNameRaw == "FallDamage" ||
                                         details.KillerNameRaw == "Fall Damage";

            if (isEnvironmentalKiller && !details.IsSuicide)
            {
                if (!config.PlayersOnlyEnv || classification.IsRealPlayer)
                {
                    if (!environmentalDeaths.ContainsKey(details.Cause)) environmentalDeaths[details.Cause] = 0;
                    environmentalDeaths[details.Cause]++;
                    SaveEnvironmentalStats();
                }
            }

            // Suicides are only possible for real players, so always count them
            if (details.IsSuicide)
            {
                if (!environmentalDeaths.ContainsKey("Suicide")) environmentalDeaths["Suicide"] = 0;
                environmentalDeaths["Suicide"]++;
                SaveEnvironmentalStats();
            }
        }

        private void RecordRecentDeathAndAnnounce(string victimName, DeathDetails details, string grid, string shortName,
            DeathClassification classification)
        {
            string displayKiller = details.KillerNameRaw;
            if (details.IsSuicide)
                displayKiller = "Suicide";
            else if (classification.IsRealPlayer && !string.IsNullOrEmpty(victimName)
                     && string.Equals(victimName, details.KillerNameRaw, StringComparison.OrdinalIgnoreCase))
                displayKiller = string.IsNullOrEmpty(details.Cause) || details.Cause == "Death"
                    ? "Suicide"
                    : details.Cause;
            string currentTime = DateTime.Now.ToString("HH:mm");

            recentDeaths.Insert(0, new DeathEntry
            {
                victim = victimName,
                killer = displayKiller,
                time = currentTime,
                headshot = details.IsHeadshot && !details.IsSuicide,
                grid = grid
            });

            if (recentDeaths.Count > config.MaxDeaths)
                recentDeaths.RemoveAt(recentDeaths.Count - 1);
        }

        private string LocalizeTypeName(string englishName, string userId)
        {
            if (string.IsNullOrEmpty(englishName)) return englishName;
            string key = TypeNameToLangKey(englishName);
            if (key == null) return englishName;
            string translated = lang.GetMessage(key, this, userId);
            return string.IsNullOrEmpty(translated) || translated == key ? englishName : translated;
        }

        private static string TypeNameToLangKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            switch (name.Trim().ToLowerInvariant())
            {
                case "wolf": return "KillerNameWolf";
                case "bear": return "KillerNameBear";
                case "polar bear": return "KillerNamePolarBear";
                case "boar": return "KillerNameBoar";
                case "chicken": return "KillerNameChicken";
                case "deer": return "KillerNameDeer";
                case "horse": return "KillerNameHorse";
                case "cow": return "KillerNameCow";
                case "bull": return "KillerNameBull";
                case "calf": return "KillerNameCalf";
                case "sheep": return "KillerNameSheep";
                case "lamb": return "KillerNameLamb";
                case "goat": return "KillerNameGoat";
                case "rabbit":
                case "bunny": return "KillerNameRabbit";
                case "squirrel": return "KillerNameSquirrel";
                case "frog":
                case "toad": return "KillerNameFrog";
                case "sea turtle":
                case "turtle": return "KillerNameSeaTurtle";
                case "jellyfish":
                case "jelly": return "KillerNameJellyfish";
                case "seagull":
                case "gull": return "KillerNameSeagull";
                case "snake": return "KillerNameSnake";
                case "shark": return "KillerNameShark";
                case "crocodile":
                case "croc":
                case "alligator": return "KillerNameCrocodile";
                case "panther": return "KillerNamePanther";
                case "tiger": return "KillerNameTiger";
                case "bees": return "KillerNameBees";
                case "bee hive": return "KillerNameBeeHive";
                case "scientist": return "KillerNameScientist";
                case "heavy scientist": return "KillerNameHeavyScientist";
                case "patrol scientist": return "KillerNamePatrolScientist";
                case "roaming scientist": return "KillerNameRoamingScientist";
                case "junkpile scientist": return "KillerNameJunkpileScientist";
                case "oil rig scientist": return "KillerNameOilRigScientist";
                case "bandit scientist": return "KillerNameBanditScientist";
                case "tunnel dweller": return "KillerNameTunnelDweller";
                case "ch47 scientist": return "KillerNameCH47Scientist";
                case "bradley scientist": return "KillerNameBradleyScientist";
                case "cargo scientist": return "KillerNameCargoScientist";
                case "patrol helicopter": return "KillerNamePatrolHelicopter";
                case "bradley apc": return "KillerNameBradleyAPC";
                case "ch47": return "KillerNameCH47";
                case "minicopter": return "KillerNameMinicopter";
                case "scrap heli": return "KillerNameScrapHeli";
                case "modular car": return "KillerNameModularCar";
                case "rowboat": return "KillerNameRowboat";
                case "rhib": return "KillerNameRHIB";
                case "tugboat": return "KillerNameTugboat";
                case "submarine": return "KillerNameSubmarine";
                case "snowmobile": return "KillerNameSnowmobile";
                case "train": return "KillerNameTrain";
                case "environment": return "KillerNameEnvironment";
                case "suicide": return "CauseSuicide";
                case "death": return "CauseDeath";
                case "fall damage": return "CauseFallDamage";
                case "drowning": return "CauseDrowning";
                case "radiation poisoning": return "CauseRadiationPoisoning";
                case "freezing": return "CauseFreezing";
                case "heat": return "CauseHeat";
                case "explosion": return "CauseExplosion";
                case "poison": return "CausePoison";
                case "electric shock": return "CauseElectricShock";
                case "decay": return "CauseDecay";
                case "starvation": return "CauseStarvation";
                case "dehydration": return "CauseDehydration";
                case "bleeding out": return "CauseBleedingOut";
                default: return null;
            }
        }

        private struct DeathClassification
        {
            public bool IsRealPlayer;
            public bool IsAnimal;
            public bool IsNpcEntity;
            public BasePlayer VictimPlayer;
            public BasePlayer KillerPlayer;
            public bool KillerIsRealPlayer;
        }

        private struct DeathDetails
        {
            public string KillerNameRaw;
            public string Cause;
            public bool IsHeadshot;
            public bool IsSuicide;
            public string NpcTypeKey;
            public string NpcKillerTypeKey;
            public string AnimalVictim;
            public string AnimalKiller;
            /// <summary>Steam UserIDString of the killing player when known (from InitiatorPlayer or lastPlayerAttackerId).</summary>
            public string KillerPlayerId;
        }


        /// <summary>
        /// Resolves the configured wipe timezone (default Eastern). Tries Windows ID first, then IANA.
        /// </summary>
        private TimeZoneInfo GetWipeTimeZone()
        {
            string preferred = string.IsNullOrWhiteSpace(config?.WipeTimezone)
                ? "Eastern Standard Time"
                : config.WipeTimezone.Trim();

            try { return TimeZoneInfo.FindSystemTimeZoneById(preferred); }
            catch { /* try alternate IDs */ }

            // Cross-platform fallbacks
            string[] fallbacks =
            {
                "Eastern Standard Time", // Windows
                "America/New_York",      // Linux / modern .NET
                "US/Eastern"
            };
            foreach (var id in fallbacks)
            {
                if (string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { }
            }
            return TimeZoneInfo.Local;
        }

        private DateTime GetNowInWipeTz()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetWipeTimeZone());
        }

        private DateTime GetWipeStartInWipeTz()
        {
            var utc = _wipeStartTime.Kind == DateTimeKind.Utc
                ? _wipeStartTime
                : _wipeStartTime.ToUniversalTime();
            return TimeZoneInfo.ConvertTimeFromUtc(utc, GetWipeTimeZone());
        }

        /// <summary>
        /// Day of the current wipe cycle (Day 0 = wipe day).
        /// Counted in the configured wipe timezone (default Eastern Standard Time / America/New_York).
        /// Day boundary = midnight in that timezone (not UTC).
        /// Source: _wipeStartTime from dual wipe detection (OnNewSave / map identity).
        /// </summary>
        private int GetWipeDay()
        {
            try
            {
                if (_wipeStartTime.Year >= 2020)
                {
                    var nowLocal = GetNowInWipeTz();
                    var startLocal = GetWipeStartInWipeTz();
                    int day = (int)(nowLocal.Date - startLocal.Date).TotalDays;
                    return Math.Max(0, day);
                }
            }
            catch { /* fall through */ }

            // Fallback: native WipeTimer if available
            try
            {
                if (WipeTimer.serverinstance != null)
                {
                    TimeSpan untilNext = WipeTimer.serverinstance.GetTimeSpanUntilWipe();
                    if (untilNext.TotalDays > 0 && untilNext.TotalDays < 40)
                        return 0;
                }
            }
            catch { }

            return 0;
        }

        /// <summary>
        /// Time string for PvP feed entries: "D{day} HH:mm" using wipe-timezone clock.
        /// </summary>
        private string FormatPvpKillTime()
        {
            try
            {
                var local = GetNowInWipeTz();
                return $"D{GetWipeDay()} {local:HH:mm}";
            }
            catch
            {
                return $"D{GetWipeDay()} {DateTime.Now:HH:mm}";
            }
        }

        /// <summary>
        /// Insert a personal PvP history entry at the front and trim to MaxPvpFeedEntries.
        /// Time includes the day of the wipe cycle (e.g. "D14 14:32").
        /// </summary>
        private void RecordPvpEntry(List<PvpEntry> list, string opponent, bool headshot, string grid)
        {
            if (list == null) return;
            int max = config != null ? Math.Max(1, config.MaxPvpFeedEntries) : 12;
            list.Insert(0, new PvpEntry
            {
                opponent = opponent ?? "Unknown",
                time = FormatPvpKillTime(),
                headshot = headshot,
                grid = grid ?? ""
            });
            while (list.Count > max)
                list.RemoveAt(list.Count - 1);
        }

private void LoadDefaultMessages()
{
    lang.RegisterMessages(new Dictionary<string, string>
    {
        ["NoPermission"] = "You do not have permission to use this command.",
        ["StatsCleared"] = "All stats have been cleared.",
        ["CleanupDone"] = "Forced cleanup done. Players pruned: {0}",
        ["AdminTip"] = "Admin tip: /livestats cleanup -> force prune old player stats now",
        ["LangUsage"] = "Usage: /lslang <code>   e.g. /lslang de  /lslang nl  /lslang ru  /lslang zh-CN  /lslang en-pt",
        ["LangSet"] = "Language set to {0}. Sample: {1}",
        ["LangTest"] = "Oxide={0}  client={1}  pref={2}  file={3}\nSample YourStatsHeader: {4}",
        ["KillfeedDisabled"] = "Killfeed command is disabled.",
        ["NoRecentDeaths"] = "No recent deaths yet.",
        ["RecentDeathsHeader"] = "<size=20><color=#ff0000>Recent Deaths</color></size>",
        ["MyStatsDisabled"] = "MyStats command is disabled.",
        ["NoStatsRecorded"] = "No stats recorded yet.",
        ["TopDisabled"] = "Top command is disabled.",
        ["TopKillersHeader"] = "<size=20><color=#ff0000>Top 10 Killers</color></size>",
        ["NpcStatsDisabled"] = "NpcStats command is disabled.",
        ["NoNpcStats"] = "No NPC stats yet.",
        ["NpcStatsHeader"] = "<size=20><color=#ff0000>NPC Stats</color></size> <size=10><color=#c0c0c0>(Top 10 by Kills)</color></size>",
        ["AnimalStatsDisabled"] = "AnimalStats command is disabled.",
        ["NoAnimalStats"] = "No animal stats yet.",
        ["AnimalStatsHeader"] = "<size=20><color=#ff0000>Animal Stats</color></size> <size=10><color=#c0c0c0>(Top 10 by deaths)</color></size>",
        ["EnvStatsDisabled"] = "EnvStats command is disabled.",
        ["NoEnvStats"] = "No environmental deaths recorded yet.",
        ["EnvStatsHeader"] = "<size=20><color=#ff0000>Environmental Deaths</color></size>",
	["LiveStatsStatusHeader"] = "=== LiveStats Internal Status (Memory) ===",
        ["LiveStatsDisabled"] = "LiveStats command is disabled.",
        ["TrackedPlayers"] = "Tracked Players: {0}   (inactive >{1}d get auto-pruned)",
        ["TrackedNpcs"] = "Tracked NPC Types: {0}",
        ["TrackedAnimals"] = "Tracked Animal Types: {0}",
        ["RecentDeathsCached"] = "Recent Deaths cached: {0} / {1}",
	["SuicideEnvDeaths"] = "Suicides: {0}  |  Environmental Deaths: {1}",
        ["ActiveLogins"] = "Active login sessions: {0}",
        ["ProcessedDeaths"] = "Temp processed deaths: {0}",
        ["WorldExtensionLoaded"] = "LiveStatsWorld extension: loaded",
        ["WorldExtensionMissing"] = "LiveStatsWorld extension: not installed",
        ["TopEntry"] = "{0}. {1} - Kills: {2} | Deaths: {3} | K/D: {4:F2}",
        ["TypeStatsEntry"] = "{0}. {1} - Kills: {2} | Deaths: {3}",
        ["EnvStatsLine"] = "{0}: {1}",
        ["AnnounceSuicide"] = "{0} committed suicide",
        ["AnnounceDiedFrom"] = "{0} died from {1}{2}",
        ["AnnounceDiedEnv"] = "{0} died from environmental causes{1}",
        ["AnnounceWasKilledBy"] = "{0} was killed by {1}{2}{3}",
        ["AnnounceKilledBy"] = "{0} killed by {1}{2}{3}",
        ["AnnounceKillerKilledVictim"] = "{0} killed {1}{2}{3}",
        ["YourStatsHeader"] = "<size=20><color=#ff0000>Your Stats</color></size>",
        ["TotalKills"] = "<color=#44ff44>Total Kills:</color>",
        ["TotalDeaths"] = "<color=#ff4444>Total Deaths:</color>",
        ["KD"] = "<color=#ffff44>K/D:</color>",
        ["HeadshotsBreakdown"] = "Headshots: {0} (PvP: {1} | NPC: {2})",
        ["AnimalKD"] = "<color=#44ff44>Animal K/D:</color> {0} (Kills: {1} | Deaths: {2})",
        ["AnimalsKilledList"] = "Animals killed: {0}",
        ["KilledByAnimalsList"] = "Killed by animals: {0}",
        ["NpcKD"] = "<color=#44ff44>NPC K/D:</color> {0} (Kills: {1} | Deaths: {2})",
        ["NpcsKilledList"] = "NPCs killed: {0}",
        ["KilledByNpcsList"] = "Killed by NPCs: {0}",
        ["KillsBreakdown"] = "Kills -> Players: {0} | NPCs: {1} | Animals: {2}",
        ["DeathsBreakdown"] = "Deaths <- By Players: {0} | By NPCs: {1} | By Animals: {2}",
        ["PvpFeedDisabled"] = "PvP feed command is disabled.",
        ["PvpFeedHeader"] = "<size=20><color=#ff0000>Your PvP Kill Feed</color></size>",
        ["PvpFeedKillsHeader"] = "<color=#44ff44>Players you killed:</color>",
        ["PvpFeedDeathsHeader"] = "<color=#ff4444>Players who killed you:</color>",
        ["PvpFeedEmpty"] = "No PvP kills or deaths recorded yet.",
        ["PvpFeedEmptyKills"] = "No player kills recorded yet.",
        ["PvpFeedEmptyDeaths"] = "No deaths to players recorded yet.",
        // {0}=timePart  {1}=opponent  {2}=gridPart  {3}=headshotTag
        ["PvpFeedKillEntry"] = "{0} You killed {1}{2}{3}",
        ["PvpFeedDeathEntry"] = "{0} {1} killed you{2}{3}",
        // Shared feed formatting helpers
        ["HeadshotTag"] = " [HEADSHOT]",
        ["GridPrefix"] = " @ {0}",
        ["TimePrefix"] = " [{0}]",
        // Server killfeed line: {0}=timePart  {1}=victim  {2}=killer  {3}=gridPart  {4}=headshotTag
        ["KillFeedEntry"] = "{0} {1} <- {2}{3}{4}",
        ["RadiationDeaths"] = "<color=#ffaa00>Radiation Poisoning Deaths:</color> {0}",
        ["MinutesPlayed"] = "Minutes Played: {0} (Current session: {1}m)",
        ["KillStreak"] = "<color=#ffaa00>Killstreak:</color> {0} (Best: {1}){2}",
        // {0}=player name  {1}=streak count
        ["KillStreakAnnounce"] = "{0} is on a {1} kill streak!",
        ["KillStreakActiveTag"] = " [ACTIVE]",


        ["CauseDeath"] = "Death",
        ["CauseFallDamage"] = "Fall Damage",
        ["CauseDrowning"] = "Drowning",
        ["CauseRadiationPoisoning"] = "Radiation Poisoning",
        ["CauseFreezing"] = "Freezing",
        ["CauseHeat"] = "Heat",
        ["CauseExplosion"] = "Explosion",
        ["CausePoison"] = "Poison",
        ["CauseElectricShock"] = "Electric Shock",
        ["CauseDecay"] = "Decay",
        ["CauseStarvation"] = "Starvation",
        ["CauseDehydration"] = "Dehydration",
        ["CauseBleedingOut"] = "Bleeding Out",
        ["CauseSuicide"] = "Suicide",

        ["KillerNameEnvironment"] = "Environment",
        ["KillerNameSuicide"] = "Suicide",

        ["KillerNameWolf"] = "Wolf",
        ["KillerNameBear"] = "Bear",
        ["KillerNamePolarBear"] = "Polar Bear",
        ["KillerNameBoar"] = "Boar",
        ["KillerNameChicken"] = "Chicken",
        ["KillerNameDeer"] = "Deer",
        ["KillerNameHorse"] = "Horse",
        ["KillerNameCow"] = "Cow",
        ["KillerNameBull"] = "Bull",
        ["KillerNameCalf"] = "Calf",
        ["KillerNameSheep"] = "Sheep",
        ["KillerNameLamb"] = "Lamb",
        ["KillerNameGoat"] = "Goat",
        ["KillerNameRabbit"] = "Rabbit",
        ["KillerNameSquirrel"] = "Squirrel",
        ["KillerNameFrog"] = "Frog",
        ["KillerNameSeaTurtle"] = "Sea Turtle",
        ["KillerNameJellyfish"] = "Jellyfish",
        ["KillerNameSeagull"] = "Seagull",
        ["KillerNameSnake"] = "Snake",
        ["KillerNameShark"] = "Shark",
        ["KillerNameCrocodile"] = "Crocodile",
        ["KillerNamePanther"] = "Panther",
        ["KillerNameTiger"] = "Tiger",
        ["KillerNameBees"] = "Bees",
        ["KillerNameBeeHive"] = "Bee Hive",

        ["KillerNameScientist"] = "Scientist",
        ["KillerNameHeavyScientist"] = "Heavy Scientist",
        ["KillerNamePatrolScientist"] = "Patrol Scientist",
        ["KillerNameRoamingScientist"] = "Roaming Scientist",
        ["KillerNameJunkpileScientist"] = "Junkpile Scientist",
        ["KillerNameOilRigScientist"] = "Oil Rig Scientist",
        ["KillerNameBanditScientist"] = "Bandit Scientist",
        ["KillerNameTunnelDweller"] = "Tunnel Dweller",
        ["KillerNameCH47Scientist"] = "CH47 Scientist",
        ["KillerNameBradleyScientist"] = "Bradley Scientist",
        ["KillerNameCargoScientist"] = "Cargo Scientist",

        ["KillerNamePatrolHelicopter"] = "Patrol Helicopter",
        ["KillerNameBradleyAPC"] = "Bradley APC",
        ["KillerNameCH47"] = "CH47",
        ["KillerNameMinicopter"] = "Minicopter",
        ["KillerNameScrapHeli"] = "Scrap Heli",
        ["KillerNameModularCar"] = "Modular Car",
        ["KillerNameRowboat"] = "Rowboat",
        ["KillerNameRHIB"] = "RHIB",
        ["KillerNameTugboat"] = "Tugboat",
        ["KillerNameSubmarine"] = "Submarine",
        ["KillerNameSnowmobile"] = "Snowmobile",
        ["KillerNameTrain"] = "Train",

        ["IdleWarningMessage"] = "You have been idle. Move within {0} minute(s) or you will be kicked.",
        ["IdleKickMessage"] = "Kicked for being idle too long."
    }, this);
}

        private string PositionToGrid(Vector3 pos)
        {
            if (pos == Vector3.zero) return "???";

            float worldSize = ConVar.Server.worldsize;
            float halfSize = worldSize / 2f;

            // Outside the playable map
            if (Mathf.Abs(pos.x) > halfSize || Mathf.Abs(pos.z) > halfSize)
                return "Deep Sea";

            // Use Facepunch's official MapHelper so the grid always matches the in-game map overlay
            // (same approach used by RaidableBases and other maintained plugins)
            try
            {
                string grid = MapHelper.PositionToString(pos);
                if (!string.IsNullOrEmpty(grid) && grid != "Unknown")
                    return grid;
            }
            catch
            {
                // Fallback if MapHelper is unavailable for any reason
            }

            // Manual fallback (matches MapHelper logic as closely as possible)
            const float cellSize = 146.3f;
            int gridX = Mathf.FloorToInt((pos.x + halfSize) / cellSize);
            int gridZ = Mathf.FloorToInt((halfSize - pos.z) / cellSize);

            string letter;
            if (gridX < 26)
                letter = ((char)('A' + gridX)).ToString();
            else
            {
                int first  = (gridX / 26) - 1;
                int second =  gridX % 26;
                letter = ((char)('A' + first)).ToString() + ((char)('A' + second)).ToString();
            }

            return letter + gridZ;
        }

        private string GetRefinedCause(HitInfo info)
        {
            if (info == null || info.damageTypes == null)
                return lang.GetMessage("CauseDeath", this);

            ulong netId = info.HitEntity?.net?.ID.Value ?? 0;

            if (netId != 0 && lastEnvironmentalCause.TryGetValue(netId, out string storedCause))
            {
                // Accept both the canonical display names and any legacy raw keys
                switch (storedCause)
                {
                    case "Starvation":
                        return lang.GetMessage("CauseStarvation", this);
                    case "Dehydration":
                        return lang.GetMessage("CauseDehydration", this);
                    case "Freezing":
                        return lang.GetMessage("CauseFreezing", this);
                    case "Radiation":
                    case "Radiation Poisoning":
                        return lang.GetMessage("CauseRadiationPoisoning", this);
                    case "Heat":
                        return lang.GetMessage("CauseHeat", this);
                    case "Drowning":
                        return lang.GetMessage("CauseDrowning", this);
                    case "Poison":
                        return lang.GetMessage("CausePoison", this);
                    case "FallDamage":
                    case "Fall Damage":
                        return lang.GetMessage("CauseFallDamage", this);
                    default:
                        return lang.GetMessage("CauseDeath", this);
                }
            }

            if (info.damageTypes.Has(Rust.DamageType.Fall))
                return lang.GetMessage("CauseFallDamage", this);
            if (info.damageTypes.Has(Rust.DamageType.Drowned))
                return lang.GetMessage("CauseDrowning", this);
            if (info.damageTypes.Has(Rust.DamageType.Radiation))
                return lang.GetMessage("CauseRadiationPoisoning", this);
            if (info.damageTypes.Has(Rust.DamageType.Cold))
                return lang.GetMessage("CauseFreezing", this);
            if (info.damageTypes.Has(Rust.DamageType.Heat))
                return lang.GetMessage("CauseHeat", this);
            if (info.damageTypes.Has(Rust.DamageType.Explosion))
                return lang.GetMessage("CauseExplosion", this);
            if (info.damageTypes.Has(Rust.DamageType.Poison))
                return lang.GetMessage("CausePoison", this);
            if (info.damageTypes.Has(Rust.DamageType.ElectricShock))
                return lang.GetMessage("CauseElectricShock", this);
            if (info.damageTypes.Has(Rust.DamageType.Decay))
                return lang.GetMessage("CauseDecay", this);
            if (info.damageTypes.Has(Rust.DamageType.Hunger))
                return lang.GetMessage("CauseStarvation", this);
            if (info.damageTypes.Has(Rust.DamageType.Thirst))
                return lang.GetMessage("CauseDehydration", this);
            if (info.damageTypes.Has(Rust.DamageType.Bleeding))
                return lang.GetMessage("CauseBleedingOut", this);

            var major = info.damageTypes.GetMajorityDamageType();
            if (major == Rust.DamageType.Heat)
                return lang.GetMessage("CauseHeat", this);
            if (major == Rust.DamageType.Radiation)
                return lang.GetMessage("CauseRadiationPoisoning", this);

            return major != Rust.DamageType.Generic 
                ? major.ToString() 
                : lang.GetMessage("CauseDeath", this);
        }

        private bool IsVehicle(BaseEntity entity)
        {
            if (entity == null) return false;
            if (IsValidAnimal(entity)) return false;
            string name = entity.ShortPrefabName.ToLowerInvariant();
            if (name.Contains("scientist") || name.Contains("npc")) return false;
            return name.Contains("modularcar") || name.Contains("module_car") || name.Contains("car_chassis")
                || name.Contains("sedan") || name.Contains("modular")
                || name.Contains("balloon") || name.Contains("rowboat") || name.Contains("rhib")
                || name.Contains("tugboat") || name.Contains("kayak")
                || name.Contains("submarine") || name.Contains("ch47") || name.Contains("minicopter")
                || name.Contains("snowmobile") || name.Contains("motorbike") || name.Contains("bicycle")
                || name.Contains("pedaltrike") || name.Contains("vehicle");
        }

        private bool IsDeployedItem(BaseEntity entity)
        {
            if (entity == null) return false;
            string name = entity.ShortPrefabName.ToLowerInvariant();
            return name.Contains("sleepingbag") || name.Contains("bed") || name.Contains("deployed") || name.Contains("furnace") || name.Contains("campfire") || name.Contains("box") || name.Contains("workbench") || name.Contains("chair") || name.Contains("generator") || name.Contains("splitter") || name.Contains("combiner") || name.Contains("electrical") || name.Contains("sign") || name.Contains("light") || name.Contains("shelf") || name.Contains("locker") || name.Contains("vending") || name.Contains("fridge") || name.Contains("storage") || name.Contains("cupboard") || name.Contains("turret") || name.Contains("sam") || name.Contains("trap") || name.Contains("icethrone") || name.Contains("sofa") || name.Contains("table") || name.Contains("shelves") || name.Contains("rug");
        }

        private bool IsPlantOrCrop(BaseEntity entity)
        {
            if (entity == null) return false;
            string name = entity.ShortPrefabName.ToLowerInvariant();
            return name.Contains("plant") || name.Contains("corn") || name.Contains("berry") || name.Contains("hemp") || name.Contains("pumpkin");
        }

        private bool IsValidAnimal(BaseEntity entity)
        {
            if (entity == null) return false;
            // Covers jungle crocs / livestock / wildlife even if the prefab spelling changes
            if (entity is BaseAnimalNPC) return true;
            return IsAnimalPrefabName(entity.ShortPrefabName);
        }

        /// <summary>
        /// Cheap victim filter for the damage hook. BasePlayer covers real players and scientist NPCs.
        /// Animals are matched by prefab fragment without allocating a lowercased copy.
        /// </summary>
        private static bool IsStatsRelevantVictim(BaseCombatEntity entity)
        {
            if (entity is BasePlayer) return true;
            return IsAnimalPrefabName(entity.ShortPrefabName);
        }

        private static bool IsAnimalPrefabName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("bear", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("wolf", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("boar", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("chicken", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("stag", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("horse", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("cow", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("bull", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("calf", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sheep", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("lamb", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("goat", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("rabbit", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("bunny", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("squirrel", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("frog", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("toad", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("turtle", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("jellyfish", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("seagull", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("snake", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("shark", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("crocodile", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("croc", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("alligator", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("panther", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("tiger", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("bee", StringComparison.OrdinalIgnoreCase) >= 0;
        }

private string GetKillerName(BaseEntity entity)
{
    if (entity == null) 
        return lang.GetMessage("KillerNameEnvironment", this);

    // Real players
    if (entity is BasePlayer bp && bp.userID.IsSteamId())
    {
        string name = bp.displayName;
        if (string.IsNullOrWhiteSpace(name) || name.ToLowerInvariant() == "player")
        {
            if (playerStats.TryGetValue(bp.UserIDString, out var stats) && !string.IsNullOrWhiteSpace(stats.lastName))
                name = stats.lastName;
            else
                name = bp.UserIDString;
        }
        return name;
    }

    if (!config.UseCleanKillerNames) 
        return entity.ShortPrefabName;

    string shortName = entity.ShortPrefabName;
    string lowerName = shortName.ToLowerInvariant();

    // 1. Check config first (server owner override)
    if (config.CustomKillerNames != null && config.CustomKillerNames.TryGetValue(lowerName, out string configName))
        return configName;

    // 2. Try partial match from config
    if (config.CustomKillerNames != null)
    {
        string bestMatch = null;
        int bestScore = -1;

        foreach (var kvp in config.CustomKillerNames)
        {
            if (lowerName.Contains(kvp.Key.ToLowerInvariant()))
            {
                int score = (kvp.Key.Length * 10) + (kvp.Key.Count(c => c == '_') * 5);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = kvp.Value;
                }
            }
        }
        if (bestMatch != null) return bestMatch;
    }

    // 3. Fallback to language file (for translation support)
    if (IsValidAnimal(entity))
    {
        if (lowerName.Contains("wolf")) return lang.GetMessage("KillerNameWolf", this);
        if (lowerName.Contains("polar")) return lang.GetMessage("KillerNamePolarBear", this);
        if (lowerName.Contains("bear")) return lang.GetMessage("KillerNameBear", this);
        if (lowerName.Contains("boar")) return lang.GetMessage("KillerNameBoar", this);
        if (lowerName.Contains("chicken")) return lang.GetMessage("KillerNameChicken", this);
        if (lowerName.Contains("stag")) return lang.GetMessage("KillerNameDeer", this);
        if (lowerName.Contains("horse")) return lang.GetMessage("KillerNameHorse", this);
        // Livestock — check more-specific names before generic family names
        if (lowerName.Contains("calf")) return lang.GetMessage("KillerNameCalf", this);
        if (lowerName.Contains("bull")) return lang.GetMessage("KillerNameBull", this);
        if (lowerName.Contains("cow") || lowerName.Contains("cattle")) return lang.GetMessage("KillerNameCow", this);
        if (lowerName.Contains("lamb")) return lang.GetMessage("KillerNameLamb", this);
        if (lowerName.Contains("sheep") || lowerName.Contains("ewe") || lowerName.Contains("ram")) return lang.GetMessage("KillerNameSheep", this);
        if (lowerName.Contains("goat") || lowerName.Contains("kid")) return lang.GetMessage("KillerNameGoat", this);
        if (lowerName.Contains("rabbit") || lowerName.Contains("bunny")) return lang.GetMessage("KillerNameRabbit", this);
        if (lowerName.Contains("squirrel")) return lang.GetMessage("KillerNameSquirrel", this);
        if (lowerName.Contains("frog") || lowerName.Contains("toad")) return lang.GetMessage("KillerNameFrog", this);
        if (lowerName.Contains("turtle")) return lang.GetMessage("KillerNameSeaTurtle", this);
        if (lowerName.Contains("jellyfish") || lowerName.Contains("jelly")) return lang.GetMessage("KillerNameJellyfish", this);
        if (lowerName.Contains("seagull") || lowerName.Contains("gull")) return lang.GetMessage("KillerNameSeagull", this);
        if (lowerName.Contains("snake")) return lang.GetMessage("KillerNameSnake", this);
        if (lowerName.Contains("shark")) return lang.GetMessage("KillerNameShark", this);
        if (lowerName.Contains("crocodile") || lowerName.Contains("croc") || lowerName.Contains("alligator"))
            return lang.GetMessage("KillerNameCrocodile", this);
        if (lowerName.Contains("panther")) return lang.GetMessage("KillerNamePanther", this);
        if (lowerName.Contains("tiger")) return lang.GetMessage("KillerNameTiger", this);
        if (lowerName.Contains("bee")) return lang.GetMessage("KillerNameBees", this);
        if (lowerName.Contains("beehive")) return lang.GetMessage("KillerNameBeeHive", this);
    }

    // Scientists / NPCs
    if (lowerName.Contains("scientist"))
    {
        if (lowerName.Contains("heavy")) return lang.GetMessage("KillerNameHeavyScientist", this);
        if (lowerName.Contains("patrol")) return lang.GetMessage("KillerNamePatrolScientist", this);
        if (lowerName.Contains("roam")) return lang.GetMessage("KillerNameRoamingScientist", this);
        if (lowerName.Contains("junkpile")) return lang.GetMessage("KillerNameJunkpileScientist", this);
        if (lowerName.Contains("oilrig")) return lang.GetMessage("KillerNameOilRigScientist", this);
        if (lowerName.Contains("bandit")) return lang.GetMessage("KillerNameBanditScientist", this);
        if (lowerName.Contains("ch47")) return lang.GetMessage("KillerNameCH47Scientist", this);
        if (lowerName.Contains("bradley")) return lang.GetMessage("KillerNameBradleyScientist", this);
        if (lowerName.Contains("cargo")) return lang.GetMessage("KillerNameCargoScientist", this);
        return lang.GetMessage("KillerNameScientist", this);
    }

    if (lowerName.Contains("tunneldweller")) return lang.GetMessage("KillerNameTunnelDweller", this);

    // Vehicles
    if (lowerName.Contains("patrolhelicopter")) return lang.GetMessage("KillerNamePatrolHelicopter", this);
    if (lowerName.Contains("bradley")) return lang.GetMessage("KillerNameBradleyAPC", this);
    if (lowerName.Contains("ch47")) return lang.GetMessage("KillerNameCH47", this);
    if (lowerName.Contains("minicopter")) return lang.GetMessage("KillerNameMinicopter", this);
    if (lowerName.Contains("scraptransporthelicopter")) return lang.GetMessage("KillerNameScrapHeli", this);
    if (lowerName.Contains("modularcar")) return lang.GetMessage("KillerNameModularCar", this);
    if (lowerName.Contains("rowboat")) return lang.GetMessage("KillerNameRowboat", this);
    if (lowerName.Contains("rhib")) return lang.GetMessage("KillerNameRHIB", this);
    if (lowerName.Contains("tugboat")) return lang.GetMessage("KillerNameTugboat", this);
    if (lowerName.Contains("submarine")) return lang.GetMessage("KillerNameSubmarine", this);
    if (lowerName.Contains("snowmobile")) return lang.GetMessage("KillerNameSnowmobile", this);
    if (lowerName.Contains("train")) return lang.GetMessage("KillerNameTrain", this);

    // Final fallback
    string nice = shortName.Replace(".prefab", "").Replace("_", " ").Replace(".", " ");
    return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(nice.ToLower());
}

        void SaveLiveStats()
        {
            var players = new List<object>();
            DateTime now = DateTime.UtcNow;

            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.userID.IsSteamId()) continue;
                string id = p.UserIDString;
                if (!playerStats.TryGetValue(id, out var stats))
                    stats = playerStats[id] = new PlayerStats();

                int currentSession = sessionStarts.TryGetValue(id, out var startTime)
                    ? (int)(now - startTime).TotalMinutes : 0;

                int unbanked = loginTimes.TryGetValue(id, out var bankTime)
                    ? (int)(now - bankTime).TotalMinutes : 0;

                // Keep stored isPlayerDead in sync with the live entity for the dashboard
                bool dead = p.IsDead();
                stats.isPlayerDead = dead;

                players.Add(new
                {
                    name = p.displayName,
                    steamID = id,
                    minutesOnline = currentSession,
                    totalMinutesPlayed = stats.totalMinutesPlayed + unbanked,
                    kills = stats.kills,
                    deaths = stats.deaths,
                    headshots = stats.headshots,
                    animalsKilled = stats.animalsKilled,
                    npcsKilled = stats.npcsKilled,
                    currentKillStreak = stats.currentKillStreak,
                    bestKillStreak = stats.bestKillStreak,
                    isOnKillStreak = stats.isOnKillStreak,
                    isPlayerDead = dead,
                    isSleeping = p.IsSleeping()
                });
            }

            int wipeDay = GetWipeDay();
            string wipeStartUtc = _wipeStartTime.Year >= 2020
                ? _wipeStartTime.ToUniversalTime().ToString("o")
                : "";

            int wipeHour = config?.WipeHour ?? 14;
            int wipeMinute = config?.WipeMinute ?? 15;
            if (wipeHour < 0) wipeHour = 0;
            if (wipeHour > 23) wipeHour = 23;
            if (wipeMinute < 0) wipeMinute = 0;
            if (wipeMinute > 59) wipeMinute = 59;

            var data = new
            {
                serverRunning = true,
                serverName = ConVar.Server.hostname,
                map = ConVar.Server.level,
                mapSeed = ConVar.Server.seed.ToString(),
                mapSize = ConVar.Server.worldsize.ToString(),
                playersOnline = players.Count,
                maxPlayers = ConVar.Server.maxplayers,
                players = players,
                recentDeaths = recentDeaths,
                // Wipe day counted in configured timezone (default Eastern); day rolls at local midnight
                wipeDay = wipeDay,
                wipeStartUtc = wipeStartUtc,
                wipeTimezone = config?.WipeTimezone ?? "Eastern Standard Time",
                wipeHour = wipeHour,
                wipeMinute = wipeMinute,
                timestamp = DateTime.UtcNow.ToString("o"),
                lastUpdateUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("live_stats", data);
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"SaveLiveStats error: {ex.Message}");
            }
        }


        private void SendMessage(BasePlayer player, string message, bool addPrefix = false)
        {
            string final = addPrefix ? $"{message}" : message;
            if (player != null)
                player.ChatMessage(final);
            else
                Puts(final);
        }


        [ChatCommand("killfeed")]
        void CmdKillFeed(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.KillFeedCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("KillfeedDisabled", this, userId));
                return;
            }
            if (recentDeaths.Count == 0)
            {
                SendMessage(player, lang.GetMessage("NoRecentDeaths", this, userId));
                return;
            }
            SendMessage(player, lang.GetMessage("RecentDeathsHeader", this, userId));
            foreach (var death in recentDeaths)
            {
                string timePart = !string.IsNullOrEmpty(death.time)
                    ? string.Format(lang.GetMessage("TimePrefix", this, userId), death.time) : "";
                string headshot = death.headshot && config.KillFeedShowHeadshots
                    ? lang.GetMessage("HeadshotTag", this, userId) : "";
                string gridPart = (config.KillFeedShowGrid && !string.IsNullOrEmpty(death.grid) && death.grid != "???")
                    ? string.Format(lang.GetMessage("GridPrefix", this, userId), death.grid) : "";
                string msg = string.Format(
                    lang.GetMessage("KillFeedEntry", this, userId),
                    timePart, death.victim, death.killer, gridPart, headshot);
                SendMessage(player, msg);
            }
        }

        /// <summary>
        /// Personal PvP kill feed: players you killed and players who killed you.
        /// </summary>
        [ChatCommand("pvplog")]
        void CmdPvpFeed(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            string userId = player.UserIDString;
            if (!config.PvpFeedCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("PvpFeedDisabled", this, userId));
                return;
            }
            if (!player.userID.IsSteamId()) return;

            if (!playerStats.TryGetValue(userId, out var stats))
            {
                player.ChatMessage(lang.GetMessage("PvpFeedEmpty", this, userId));
                return;
            }

            var kills = stats.recentPvpKills ?? new List<PvpEntry>();
            var deaths = stats.recentPvpDeaths ?? new List<PvpEntry>();

            if (kills.Count == 0 && deaths.Count == 0)
            {
                player.ChatMessage(lang.GetMessage("PvpFeedEmpty", this, userId));
                return;
            }

            SendMessage(player, lang.GetMessage("PvpFeedHeader", this, userId));

            SendMessage(player, lang.GetMessage("PvpFeedKillsHeader", this, userId));
            if (kills.Count == 0)
            {
                SendMessage(player, lang.GetMessage("PvpFeedEmptyKills", this, userId));
            }
            else
            {
                foreach (var entry in kills)
                {
                    string timePart = !string.IsNullOrEmpty(entry.time)
                        ? string.Format(lang.GetMessage("TimePrefix", this, userId), entry.time) : "";
                    string hs = entry.headshot && config.KillFeedShowHeadshots
                        ? lang.GetMessage("HeadshotTag", this, userId) : "";
                    string gridPart = (config.KillFeedShowGrid && !string.IsNullOrEmpty(entry.grid) && entry.grid != "???")
                        ? string.Format(lang.GetMessage("GridPrefix", this, userId), entry.grid) : "";
                    SendMessage(player, string.Format(
                        lang.GetMessage("PvpFeedKillEntry", this, userId),
                        timePart, entry.opponent, gridPart, hs));
                }
            }

            SendMessage(player, lang.GetMessage("PvpFeedDeathsHeader", this, userId));
            if (deaths.Count == 0)
            {
                SendMessage(player, lang.GetMessage("PvpFeedEmptyDeaths", this, userId));
            }
            else
            {
                foreach (var entry in deaths)
                {
                    string timePart = !string.IsNullOrEmpty(entry.time)
                        ? string.Format(lang.GetMessage("TimePrefix", this, userId), entry.time) : "";
                    string hs = entry.headshot && config.KillFeedShowHeadshots
                        ? lang.GetMessage("HeadshotTag", this, userId) : "";
                    string gridPart = (config.KillFeedShowGrid && !string.IsNullOrEmpty(entry.grid) && entry.grid != "???")
                        ? string.Format(lang.GetMessage("GridPrefix", this, userId), entry.grid) : "";
                    SendMessage(player, string.Format(
                        lang.GetMessage("PvpFeedDeathEntry", this, userId),
                        timePart, entry.opponent, gridPart, hs));
                }
            }
        }

[ChatCommand("mystats")]
void CmdMyStats(BasePlayer player, string command, string[] args)
{
    if (player == null) return;
    if (!config.MyStatsCommandEnabled)
    {
        player.ChatMessage(lang.GetMessage("MyStatsDisabled", this, player.UserIDString));
        return;
    }
    if (!player.userID.IsSteamId()) return;

    string id = player.UserIDString;
    if (!playerStats.TryGetValue(id, out var stats))
    {
        player.ChatMessage(lang.GetMessage("NoStatsRecorded", this, player.UserIDString));
        return;
    }

    DateTime now = DateTime.UtcNow;

    // currentSession = continuous online time this login (never resets)
    int currentSession = sessionStarts.TryGetValue(id, out var startTime)
        ? (int)(now - startTime).TotalMinutes : 0;

    // unbanked = minutes since last bank point (avoids double-counting)
    int unbanked = loginTimes.TryGetValue(id, out var bankTime)
        ? (int)(now - bankTime).TotalMinutes : 0;

    int displayedTotalMinutes = stats.totalMinutesPlayed + unbanked;
    float kd = stats.deaths > 0 ? (float)stats.kills / stats.deaths : stats.kills;

    player.ChatMessage(lang.GetMessage("YourStatsHeader", this, player.UserIDString));
    player.ChatMessage($"{lang.GetMessage("TotalKills", this, player.UserIDString)} {stats.kills}  |  {lang.GetMessage("TotalDeaths", this, player.UserIDString)} {stats.deaths}  |  {lang.GetMessage("KD", this, player.UserIDString)} {kd:F2}");
    player.ChatMessage(string.Format(lang.GetMessage("HeadshotsBreakdown", this, player.UserIDString), stats.headshots, stats.playerHeadshots, stats.npcHeadshots));
    player.ChatMessage(string.Format(lang.GetMessage("KillsBreakdown", this, player.UserIDString), stats.playersKilled, stats.npcsKilled, stats.animalsKilled));
    player.ChatMessage(string.Format(lang.GetMessage("DeathsBreakdown", this, player.UserIDString), stats.deathsByPlayer, stats.deathsByNPC, stats.deathsByAnimal));

    // Combined suicides + environmental deaths using language key
    player.ChatMessage(string.Format(lang.GetMessage("SuicideEnvDeaths", this, player.UserIDString), 
        stats.suicides, stats.deathsByEnvironment));

    if (config.EnableKillStreaks)
    {
        string activeTag = stats.isOnKillStreak
            ? lang.GetMessage("KillStreakActiveTag", this, player.UserIDString) : "";
        player.ChatMessage(string.Format(
            lang.GetMessage("KillStreak", this, player.UserIDString),
            stats.currentKillStreak, stats.bestKillStreak, activeTag));
    }

    // NPC stats (per-type K/D) — all strings via Oxide lang
    if (stats.npcsKilled > 0 || stats.deathsByNPC > 0)
    {
        float npcKd = stats.deathsByNPC > 0 ? (float)stats.npcsKilled / stats.deathsByNPC : stats.npcsKilled;
        player.ChatMessage(string.Format(
            lang.GetMessage("NpcKD", this, player.UserIDString),
            npcKd.ToString("F2"), stats.npcsKilled, stats.deathsByNPC));

        if (stats.npcsKilledByType != null && stats.npcsKilledByType.Count > 0)
        {
            var topKills = stats.npcsKilledByType.OrderByDescending(x => x.Value).Take(5)
                .Select(x => $"{x.Key}: {x.Value}");
            player.ChatMessage(string.Format(
                lang.GetMessage("NpcsKilledList", this, player.UserIDString),
                string.Join(", ", topKills)));
        }
        if (stats.deathsByNpcType != null && stats.deathsByNpcType.Count > 0)
        {
            var topDeaths = stats.deathsByNpcType.OrderByDescending(x => x.Value).Take(5)
                .Select(x => $"{x.Key}: {x.Value}");
            player.ChatMessage(string.Format(
                lang.GetMessage("KilledByNpcsList", this, player.UserIDString),
                string.Join(", ", topDeaths)));
        }
    }

    // Animal hunter stats (per-type K/D) — all strings via Oxide lang
    if (stats.animalsKilled > 0 || stats.deathsByAnimal > 0)
    {
        float animalKd = stats.deathsByAnimal > 0 ? (float)stats.animalsKilled / stats.deathsByAnimal : stats.animalsKilled;
        player.ChatMessage(string.Format(
            lang.GetMessage("AnimalKD", this, player.UserIDString),
            animalKd.ToString("F2"), stats.animalsKilled, stats.deathsByAnimal));

        if (stats.animalsKilledByType != null && stats.animalsKilledByType.Count > 0)
        {
            var topKills = stats.animalsKilledByType.OrderByDescending(x => x.Value).Take(5)
                .Select(x => $"{x.Key}: {x.Value}");
            player.ChatMessage(string.Format(
                lang.GetMessage("AnimalsKilledList", this, player.UserIDString),
                string.Join(", ", topKills)));
        }
        if (stats.deathsByAnimalType != null && stats.deathsByAnimalType.Count > 0)
        {
            var topDeaths = stats.deathsByAnimalType.OrderByDescending(x => x.Value).Take(5)
                .Select(x => $"{x.Key}: {x.Value}");
            player.ChatMessage(string.Format(
                lang.GetMessage("KilledByAnimalsList", this, player.UserIDString),
                string.Join(", ", topDeaths)));
        }
    }

    player.ChatMessage(string.Format(lang.GetMessage("MinutesPlayed", this, player.UserIDString), displayedTotalMinutes, currentSession));
}
        [ChatCommand("top")]
        void CmdTop(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.TopCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("TopDisabled", this, userId));
                return;
            }
            var sorted = playerStats.OrderByDescending(x => x.Value.kills).Take(10).ToList();
            SendMessage(player, lang.GetMessage("TopKillersHeader", this, userId));
            int rank = 1;
            foreach (var entry in sorted)
            {
                float kd = entry.Value.deaths > 0 ? (float)entry.Value.kills / entry.Value.deaths : entry.Value.kills;
                SendMessage(player, string.Format(lang.GetMessage("TopEntry", this, userId), rank, entry.Value.lastName, entry.Value.kills, entry.Value.deaths, kd));
                rank++;
            }
        }

        [ChatCommand("npcstats")]
        void CmdNpcStats(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.NpcStatsCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("NpcStatsDisabled", this, userId));
                return;
            }
            if (npcStats.Count == 0)
            {
                SendMessage(player, lang.GetMessage("NoNpcStats", this, userId));
                return;
            }
            SendMessage(player, lang.GetMessage("NpcStatsHeader", this, userId));
            var sorted = npcStats.OrderByDescending(x => x.Value.kills).Take(10);
            int rank = 1;
            foreach (var entry in sorted)
            {
                SendMessage(player, string.Format(lang.GetMessage("TypeStatsEntry", this, userId), rank, LocalizeTypeName(entry.Key, userId), entry.Value.kills, entry.Value.deaths));
                rank++;
            }
        }

        [ChatCommand("animalstats")]
        void CmdAnimalStats(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.AnimalStatsCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("AnimalStatsDisabled", this, userId));
                return;
            }
            if (animalStats.Count == 0)
            {
                SendMessage(player, lang.GetMessage("NoAnimalStats", this, userId));
                return;
            }
            SendMessage(player, lang.GetMessage("AnimalStatsHeader", this, userId));
            var sorted = animalStats.OrderByDescending(x => x.Value.deaths).Take(10);
            int rank = 1;
            foreach (var entry in sorted)
            {
                SendMessage(player, string.Format(lang.GetMessage("TypeStatsEntry", this, userId), rank, LocalizeTypeName(entry.Key, userId), entry.Value.kills, entry.Value.deaths));
                rank++;
            }
        }

        [ChatCommand("envstats")]
        void CmdEnvStats(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.EnvStatsCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("EnvStatsDisabled", this, userId));
                return;
            }
            if (environmentalDeaths.Count == 0)
            {
                SendMessage(player, lang.GetMessage("NoEnvStats", this, userId));
                return;
            }
            SendMessage(player, lang.GetMessage("EnvStatsHeader", this, userId));
            var sorted = environmentalDeaths.OrderByDescending(x => x.Value);
            foreach (var entry in sorted)
                SendMessage(player, string.Format(lang.GetMessage("EnvStatsLine", this, userId), LocalizeTypeName(entry.Key, userId), entry.Value));
        }

        [ChatCommand("clearallstats")]
        void CmdClearAllStats(BasePlayer player, string command, string[] args)
        {
            if (player != null && !permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }
            playerStats.Clear();
            npcStats.Clear();
            animalStats.Clear();
            environmentalDeaths.Clear();
            recentDeaths.Clear();
            lastEnvironmentalCause.Clear();
            lastNpcAttacker.Clear();
            lastPlayerAttacker.Clear();
            lastPlayerAttackerId.Clear();
            lastAnimalAttacker.Clear();
            processedDeaths.Clear();
            processedNpcKills.Clear();
            SeedNpcTypes();
            SeedAnimalTypes();
            SeedEnvironmentalStats();
            FlushDirtyStats(force: true);
            SendMessage(player, lang.GetMessage("StatsCleared", this, player?.UserIDString), true);
        }

        [ChatCommand("livestats")]
        void CmdLiveStats(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (player != null && !config.LiveStatsCommandEnabled)
            {
                player.ChatMessage(lang.GetMessage("LiveStatsDisabled", this, userId));
                return;
            }

            SendMessage(player, lang.GetMessage("LiveStatsStatusHeader", this, userId));
            SendMessage(player, lang.GetMessage(worldExtensionReady ? "WorldExtensionLoaded" : "WorldExtensionMissing", this, userId));
            SendMessage(player, string.Format(lang.GetMessage("TrackedPlayers", this, userId), playerStats.Count, config.PlayerStatsCleanupDays));
            SendMessage(player, string.Format(lang.GetMessage("TrackedNpcs", this, userId), npcStats.Count));
            SendMessage(player, string.Format(lang.GetMessage("TrackedAnimals", this, userId), animalStats.Count));
            SendMessage(player, string.Format(lang.GetMessage("RecentDeathsCached", this, userId), recentDeaths.Count, config.MaxDeaths));
            SendMessage(player, string.Format(lang.GetMessage("ActiveLogins", this, userId), loginTimes.Count));
            SendMessage(player, string.Format(lang.GetMessage("ProcessedDeaths", this, userId), processedDeaths.Count));

            if (args.Length > 0 && args[0].ToLowerInvariant() == "cleanup")
            {
                if (player != null && !permission.UserHasPermission(player.UserIDString, AdminPermission))
                {
                    SendMessage(player, lang.GetMessage("NoPermission", this, userId));
                    return;
                }
                int before = playerStats.Count;
                CleanupOldPlayerStats();
                int after = playerStats.Count;
                SendMessage(player, string.Format(lang.GetMessage("CleanupDone", this, userId), before - after));
            }
            else if (player == null || permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                SendMessage(player, lang.GetMessage("AdminTip", this, userId));
            }
        }

        [ChatCommand("lslang")]
        private void CmdLsLang(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length == 0)
            {
                player.ChatMessage(lang.GetMessage("LangUsage", this, player.UserIDString));
                return;
            }
            string folder = ResolveLangFolder(args[0]);
            lang.SetLanguage(folder, player.UserIDString);
            string sample = lang.GetMessage("YourStatsHeader", this, player.UserIDString);
            player.ChatMessage(string.Format(lang.GetMessage("LangSet", this, player.UserIDString), folder, sample));
            Puts($"[Lang] {player.displayName} set language via /lslang -> {folder} pack={(LangFileExists(Name, folder) ? "yes" : "MISSING")}");
        }

        [ChatCommand("lstestlang")]
        private void CmdLsTestLang(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            string oxide = lang.GetLanguage(player.UserIDString) ?? "en";
            string rawConn = null;
            string rawInfo = null;
            try { rawConn = player.net?.connection?.language; } catch { }
            try { rawInfo = player.net?.connection?.info?.GetString("global.language", null); } catch { }
            bool pack = LangFileExists(Name, oxide);
            string sample = lang.GetMessage("YourStatsHeader", this, player.UserIDString);
            player.ChatMessage(string.Format(
                lang.GetMessage("LangTest", this, player.UserIDString),
                oxide,
                rawConn ?? "-",
                rawInfo ?? "-",
                pack ? "oxide/lang/" + oxide + "/" + Name + ".json" : "MISSING",
                sample));
            Puts($"[Lang] test {player.displayName} oxide={oxide} conn={rawConn ?? "-"} info={rawInfo ?? "-"} pack={(pack ? "yes" : "MISSING")}");
        }
    }
}
