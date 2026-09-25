// Requires: LiveStats
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("LiveStatsWorld", "FiREST0N3D", "1.1.51")]
    [Description("World time, real date, moon phases, polar handling, real-solar atmosphere, Open-Meteo weather extension for LiveStats. Requires LiveStats. CONFLICTS when enabled: other time/weather plugins (TimeOfDay, RealTime, Weather), event managers that spawn on time jumps. TimeSystem + UseLocalWeather default OFF. Catch-up event suppress is opt-in. v1.1.51: Clear vs Few Clouds vs Partly Cloudy.")]
    class LiveStatsWorld : RustPlugin
    {
        private enum CloudSwapPhase
        {
            Idle = 0,
            FadeOut = 1,
            Hold = 2,
            FadeIn = 3
        }

        private enum LogArea
        {
            Time,
            Weather,
            Lightning,
            Stats
        }
        /// <summary>
        /// Live weather channels. Blend, dissolve, and lightning flash all read/write
        /// this object instead of a pile of sibling fields on the plugin.
        /// Cloud-swap phase / hop queue stay on the plugin until CloudSwapController.
        /// </summary>
        private sealed class WeatherRuntimeState
        {
            public string currentWeatherProfile = "clear";
            public string pendingWeatherProfile = "clear";
            public string currentCloudConfig = "";
            public string hysteresisCandidate = null;
            public int hysteresisVotes = 0;

            public float targetClouds = 0f, currentClouds = 0f;
            public float targetMie = 0f, currentMie = 0f;
            public float targetBrightness = 1f, currentBrightness = 1f;

            public float targetRain = 0f, currentRain = 0f;
            public float targetWind = 0f, currentWind = 0f;
            public float targetFog = 0f, currentFog = 0f;
            public float targetFogMultiplier = 1.0f, currentFogMultiplier = 1.0f;
            public float targetFogRampStart = 50f, currentFogRampStart = 50f;
            public float targetFogRampEnd = 800f, currentFogRampEnd = 800f;
            public float targetFogHeightFalloff = 0.5f, currentFogHeightFalloff = 0.5f;
            public float targetThunder = 0f, currentThunder = 0f;
            public float targetRainbow = 0f, currentRainbow = 0f;
            public float targetWetness = 0.15f, currentWetness = 0.15f;
            public float targetWetnessSnow = 0.1f, currentWetnessSnow = 0.1f;
            public float targetDust = 0f, currentDust = 0f;
            public float targetRayleigh = 1.0f, currentRayleigh = 1.0f;
            public float targetContrast = 1.0f, currentContrast = 1.0f;
            public float targetDirectionality = 0.7f, currentDirectionality = 0.7f;
            public float targetAttenuation = 0.6f, currentAttenuation = 0.6f;

            public float targetCloudBrightness = 1.0f, currentCloudBrightness = 1.0f;
            public float targetCloudSharpness = 0.5f, currentCloudSharpness = 0.5f;
            public float targetCloudScattering = 1.0f, currentCloudScattering = 1.0f;
            public float targetCloudColoring = 1.0f, currentCloudColoring = 1.0f;
            public float targetCloudSize = 1.0f, currentCloudSize = 1.0f;
            public float targetCloudSaturation = 1.0f, currentCloudSaturation = 1.0f;
            public float targetCloudOpacity = 0.9f, currentCloudOpacity = 0.9f;

            public int lastWeatherCode = 0;
            public float lastCloudLow = 0f;
            public float lastCloudMid = 0f;
            public float lastCloudHigh = 0f;

            public float appliedClouds = float.NaN, appliedRain = float.NaN, appliedWind = float.NaN;
            public float appliedFog = float.NaN, appliedFogMultiplier = float.NaN;
            public float appliedFogRampStart = float.NaN, appliedFogRampEnd = float.NaN;
            public float appliedFogHeightFalloff = float.NaN;
            public float appliedThunder = float.NaN, appliedRainbow = float.NaN;
            public float appliedWetness = float.NaN, appliedWetnessSnow = float.NaN;
            public float appliedDust = float.NaN;
            public float appliedMie = float.NaN, appliedRayleigh = float.NaN;
            public float appliedBrightness = float.NaN, appliedContrast = float.NaN;
            public float appliedDirectionality = float.NaN, appliedAttenuation = float.NaN;
            public float appliedCloudBrightness = float.NaN, appliedCloudSharpness = float.NaN;
            public float appliedCloudScattering = float.NaN, appliedCloudColoring = float.NaN;
            public float appliedCloudSize = float.NaN, appliedCloudSaturation = float.NaN;
            public float appliedCloudOpacity = float.NaN;
            public float appliedDirLight = float.NaN, appliedAmbLight = float.NaN;
            public float appliedVCloudSun = float.NaN, appliedVCloudMoon = float.NaN;
            public float appliedSunMesh = float.NaN, appliedMoonMesh = float.NaN;
            public float appliedReflection = float.NaN;
            public float appliedNightlightBri = float.NaN, appliedNightlightDist = float.NaN, appliedNightlightFade = float.NaN;
            public float targetDirLight = 1f, currentDirLight = 1f;
            public float targetAmbLight = 1f, currentAmbLight = 1f;
            public float targetVCloudSun = 1f, currentVCloudSun = 1f;
            public float targetVCloudMoon = 1f, currentVCloudMoon = 1f;
            public float targetSunMesh = 1f, currentSunMesh = 1f;
            public float targetMoonMesh = 1f, currentMoonMesh = 1f;
            public float targetReflection = 1f, currentReflection = 1f;

            public float lastOmCape = 0f, lastOmShortwave = 0f, lastOmHumidity = 50f, lastOmVisibility = 10000f;
            public bool lastOmIsDay = true;

            public float WeatherBlendFactor = 0.045f;
            public float WeatherBlendInterval = 1.5f;
            public float _dynamicBlendSeconds = 90f;
            public bool _weatherInitialized = false;

            public float _lastLightningTime = 0f;
            public float _lightningFlashUntil = 0f;
            public float _lightningFlashBri = float.NaN;
            public float _lightningFlashCon = float.NaN;
            public float _lightningFlashCloudBri = float.NaN;
            public float _lightningFlashRayleigh = float.NaN;
            public float _nextLightningEarliest = 0f;
            public float _lightningWobbleSeed = 0f;

            public float currentCcn = 0.35f;
            public float targetCcn = 0.35f;
            public float currentInstability = 0f;
            public float targetInstability = 0f;
            public float _seedBias = 0f;
            public float _seedBiasEnd = 0f;
            public float _lastCcnUpdate = 0f;

            public float _lastWeatherSuccessTime = 0f;
            public int _consecutiveWeatherFails = 0;
            public float _pressureTendency = 0f;
            public float _rainLatch = 0f;
            public float _lastGustValue = 0f;
            public float _lookAheadRain = 0f;
            public float _lookAheadHours = 0f;
            public string _lookAheadLabel = "none";
            public string _lastInterpLogProfile = null;

            public void InvalidateAppliedSky()
            {
                appliedClouds = appliedCloudOpacity = appliedAttenuation = float.NaN;
                appliedBrightness = appliedContrast = appliedRayleigh = float.NaN;
                appliedMie = appliedDirectionality = float.NaN;
                appliedCloudBrightness = appliedCloudSharpness = appliedCloudScattering = float.NaN;
                appliedCloudColoring = appliedCloudSize = appliedCloudSaturation = float.NaN;
                appliedDirLight = appliedAmbLight = float.NaN;
                appliedVCloudSun = appliedVCloudMoon = float.NaN;
                appliedSunMesh = appliedMoonMesh = appliedReflection = float.NaN;
            }

            public void InvalidateAppliedAll()
            {
                InvalidateAppliedSky();
                appliedRain = appliedWind = appliedFog = float.NaN;
                appliedFogMultiplier = appliedFogRampStart = appliedFogRampEnd = float.NaN;
                appliedFogHeightFalloff = appliedThunder = appliedRainbow = float.NaN;
                appliedWetness = appliedWetnessSnow = appliedDust = float.NaN;
            }
        }


        [PluginReference]
        private Plugin LiveStats;

        private ConfigData config;

        private const string AdminPermission = "livestatsworld.admin";
        private const string HostAdminPermission = "livestats.admin";

        private Timer _worldTimer;
        private Timer _weatherPollTimer;
        private Timer _timeSyncTimer;

        private bool _ready = false;

        // ==================== WORLD STATS ====================
        private float lastWorldHour = 12f;
        private int lastWorldDay = 0;
        private float lastMoonPhase = 0f;
        private string lastMoonPhaseName = "Unknown";
        private float lastMoonIllumination = 0f;
        private float lastSunAltitude = 0f;
        private float lastSunAzimuth = 0f;
        private string lastSunPosition = "Unknown";
        private string lastWeather = "Clear";
        private float lastWeatherIntensity = 0f;
        private float lastRainIntensity = 0f;
        private float lastThunderIntensity = 0f;
        private float lastWindIntensity = 0f;
        private float lastFogIntensity = 0f;

        // ==================== REAL TIME / DATE / POLAR ====================
        private float _cachedUtcOffsetSeconds = 0f;
        private string _cachedTimezone = "UTC";
        private DateTime _lastLocalDate = DateTime.MinValue;
        private bool _isPolarDay = false;
        private bool _isPolarNight = false;
        private float _daylightDurationSeconds = 43200f;
        private bool _timeProgressDisabled = false;
        private DateTime? _lastSunrise = null;
        private DateTime? _lastSunset = null;
        private bool _tournamentActive = false;
        private bool _initialDateSynced = false;
        private int lastSplitDayIndex = 0;
        private float lastSplitClockRate = 1f;
        private float _vanillaDayLengthMinutes = float.NaN;
        private float _appliedDayLengthMinutes = float.NaN;
        private bool _splitSpeedScalingOn = false;

        // Temporary suppress window after forced time/date changes so vanilla
        // catch-up events (cargo plane, CH47, cargo ship, patrol heli) are killed.
        private float _eventSuppressUntil = 0f;
        private const float EventSuppressSeconds = 15f;

        // ==================== LOCAL WEATHER ====================
        private Timer _weatherBlendTimer;
        private readonly WeatherRuntimeState wx = new WeatherRuntimeState();

        private CloudSwapPhase _cloudSwapPhase = CloudSwapPhase.Idle;
        private string _cloudSwapDesired = null;
        private float _cloudSwapPhaseStart = 0f;
        private float _cloudSwapSavedOpacity = 0.5f;
        private float _cloudSwapSavedCoverage = 0.4f;
        private float _cloudSwapOutDuration = 2.0f;
        private float _cloudSwapHoldDuration = 0.35f;
        private float _cloudSwapInDuration = 2.5f;
        private float _cloudSwapFadeFromOpacity = 0.08f;
        private float _cloudSwapFadeFromCoverage = 0.10f;
        private float _cloudSwapSavedBri = 1f;
        private float _cloudSwapSavedCon = 1f;
        private float _cloudSwapSavedCloudBri = 1f;
        private float _cloudSwapSavedRay = 1f;
        private float _cloudSwapSavedAtten = 0.6f;
        private float _cloudSwapSavedMie = 0f;
        private float _cloudSwapSavedScatter = 1f;
        private float _cloudSwapSavedColor = 1f;
        private float _cloudSwapSavedSharp = 0.5f;
        private float _cloudSwapSavedSize = 1f;
        private float _cloudSwapSavedSat = 1f;
        private float _cloudSwapSavedDir = 0.7f;
        private Timer _cloudSwapTimer;
        private readonly List<string> _cloudSwapQueue = new List<string>(4);
        private int _cloudSwapHopsTotal = 1;
        private static readonly string[] SoftSkyChain =
        {
            "Clear_VClouds",
            "RainMild_VClouds",
            "Overcast_VClouds"
        };
        private const float CloudSwapTickInterval = 0.1f;
        // Keep a thin veil so the sky cannot blow out to full-sun during the mesh swap.
        private const float CloudSwapFloor = 0.08f;
        private const float CloudSwapFloorCoverage = 0.10f;

        private float _cloudSwapSavedVSun = 1f, _cloudSwapSavedVMoon = 1f;
        private const float WeatherApplyEpsilon = 0.02f;
        private Timer _lightningFlashTimer;
        private readonly List<LightningStroke> _lightningStrokes = new List<LightningStroke>(8);

        private struct LightningStroke
        {
            public float Start;
            public float End;
            public float Peak;
            public float Softness; // 0 = hard white spike, 1 = milky cloud flash
        }

        private Timer _weatherRetryTimer;

        private struct WeatherSample
        {
            public DateTime TimeUtc;
            public int WeatherCode;
            public float CloudPercent;
            public float CloudLow;
            public float CloudMid;
            public float CloudHigh;
            public float Temperature;
            public float DewPoint;
            public float Humidity;
            public float Pressure;
            public float RainMm;
            public float SnowMm;
            public float ShowersMm;
            public float PrecipProb;
            public float Cape;
            public float Shortwave;
            public float SoilMoisture;
            public float WindKmh;
            public float GustKmh;
            public float WindDir;
            public float VisibilityM;
            public bool IsDay;
        }

        private readonly List<WeatherSample> _weatherAnchors = new List<WeatherSample>(128);
        // Reused scratch list for Open-Meteo parse to avoid allocating a new List every poll.
        private readonly List<WeatherSample> _anchorScratch = new List<WeatherSample>(128);
        // Pre-parsed hourly timestamps (filled once per poll, not per minutely sample).
        private readonly List<DateTime> _hourlyTimeScratch = new List<DateTime>(128);
        private double lastServerUptimeMinutes = 0;

        // ==================== INIT / LIFECYCLE ====================

        void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
            LoadDefaultMessages();
            Puts("LiveStatsWorld v1.1.51 loaded (Clear / Few Clouds / Partly Cloudy)");
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            string rustLang = null;
            try { rustLang = player.net?.connection?.language; } catch { }
            if (string.IsNullOrWhiteSpace(rustLang)) return;
            rustLang = rustLang.Trim();
            if (rustLang.Equals("pirate", StringComparison.OrdinalIgnoreCase))
                rustLang = "en-PT";
            string current = null;
            try { current = lang.GetLanguage(player.UserIDString); } catch { }
            if (!string.Equals(current, rustLang, StringComparison.OrdinalIgnoreCase))
                lang.SetLanguage(rustLang, player.UserIDString);
        }

        void OnServerInitialized(bool initial)
        {
            if (_ready) return;

            if (LiveStats == null)
                LiveStats = plugins.Find("LiveStats");

            if (LiveStats == null)
            {
                PrintError("LiveStats host plugin is required. LiveStatsWorld will not start until LiveStats is loaded.");
                return;
            }

            _ready = true;
            Puts("LiveStats host detected -- world stats, time system & weather systems starting.");

            // Start real-time / date system FIRST so vanilla progression is disabled
            // immediately and the first accurate date-set (after Open-Meteo TZ) is
            // the earliest meaningful [Time] action on load.
            if (config.TimeSystem != null && config.TimeSystem.Enabled)
            {
                StartTimeSystem();
            }

            // Restore tournament state if configured
            if (config.Tournament != null && config.Tournament.Enabled)
            {
                _tournamentActive = true;
                Puts("[Time] Tournament mode is enabled in config.");
            }

            if (config.UseLocalWeather)
            {
                wx.WeatherBlendInterval = Mathf.Clamp(config.WeatherBlendInterval, 0.5f, 5f);
                float blendSec = Mathf.Clamp(config.WeatherBlendSeconds, 25f, 180f);
                wx._dynamicBlendSeconds = blendSec;
                wx.WeatherBlendFactor = Mathf.Clamp(wx.WeatherBlendInterval / blendSec, 0.02f, 0.28f);

                if (config.PersistWeatherState)
                    LoadWeatherState();

                Puts($"LocalWeather enabled (poll every {Mathf.Max(5, config.LocalWeatherUpdateIntervalMinutes)}m, " +
                     $"blend tick {wx.WeatherBlendInterval:F1}s, blend base {blendSec:F0}s)");

                // First poll after bootstrap so navmesh / asset warmup does not
                // starve Unity's HTTP stream (timeoutReached on a 200 body).
                timer.Once(25f, SyncLocalWeather);
                float pollInterval = Mathf.Max(5, config.LocalWeatherUpdateIntervalMinutes) * 60f;
                RestartEvery(ref _weatherPollTimer, pollInterval, SyncLocalWeather);
                RestartEvery(ref _weatherBlendTimer, wx.WeatherBlendInterval, BlendLocalWeather);
            }

            if (config.CollectWorldStats)
            {
                float interval = Mathf.Max(5f, config.WorldStatsUpdateInterval);
                RestartEvery(ref _worldTimer, interval, CollectWorldStats);
                timer.Once(5f, CollectWorldStats);
                Puts($"World stats collection enabled (every {interval:F0}s)");
            }

            ApplyCatchUpSpawnSubscription();
        }

        private void ApplyCatchUpSpawnSubscription()
        {
            bool want = config?.TimeSystem != null && config.TimeSystem.SuppressCatchUpEvents;
            if (want)
                Subscribe(nameof(OnEntitySpawned));
            else
                Unsubscribe(nameof(OnEntitySpawned));
        }

        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "LiveStats" && !_ready)
            {
                Puts("LiveStats loaded after LiveStatsWorld -- initializing.");
                OnServerInitialized(false);
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "LiveStats")
            {
                Puts("LiveStats host unloaded -- pausing all timers.");
                _ready = false;
                StopManagedTimers();
                RestoreVanillaTimeProgression();
            }
        }

        void Unload()
        {
            StopManagedTimers();
            RestoreVanillaTimeProgression();

            if (config != null && config.UseLocalWeather && config.PersistWeatherState)
                SaveWeatherState();
        }

        void OnServerShutdown()
        {
            StopManagedTimers();
            RestoreVanillaTimeProgression();
            if (config != null && config.UseLocalWeather && config.PersistWeatherState)
                SaveWeatherState();
        }

        /// <summary>
        /// Kill vanilla catch-up events that fire right after a forced time/date change.
        /// Only active for a short window; normal later events are left alone.
        /// </summary>
        void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null) return;
            // Opt-in only — default off so other event plugins are not disrupted
            if (config?.TimeSystem == null || !config.TimeSystem.SuppressCatchUpEvents) return;
            if (Time.realtimeSinceStartup > _eventSuppressUntil) return;

            bool isEvent =
                entity is CargoPlane ||
                entity is PatrolHelicopter ||
                entity is CargoShip ||
                entity is CH47Helicopter;

            if (!isEvent)
            {
                string prefab = entity.ShortPrefabName ?? string.Empty;
                isEvent =
                    prefab.IndexOf("cargo_plane", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prefab.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prefab.IndexOf("ch47", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prefab.IndexOf("cargoshi", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (isEvent)
            {
                if (config != null && config.DebugMode)
                    Puts($"[Time] Suppressed catch-up event: {entity.ShortPrefabName}");
                NextFrame(() =>
                {
                    if (entity != null && !entity.IsDestroyed)
                        entity.Kill();
                });
            }
        }

        private void BeginEventSuppress()
        {
            // No-op unless explicitly enabled (avoids fighting other event plugins)
            if (config?.TimeSystem == null || !config.TimeSystem.SuppressCatchUpEvents)
                return;

            float secs = config.TimeSystem.SuppressCatchUpSeconds;
            if (secs < 1f) secs = 1f;
            if (secs > 60f) secs = 60f;
            _eventSuppressUntil = Time.realtimeSinceStartup + secs;
            if (config.DebugMode)
                Puts($"[Time] Event suppress active for {secs:F0}s");
        }

        // ==================== TIMER HUB / LOGGING ====================

        private void StopTimer(ref Timer slot)
        {
            slot?.Destroy();
            slot = null;
        }

        private void RestartEvery(ref Timer slot, float interval, Action action)
        {
            StopTimer(ref slot);
            slot = timer.Every(interval, action);
        }

        private void StopManagedTimers()
        {
            StopTimer(ref _worldTimer);
            StopTimer(ref _weatherBlendTimer);
            StopTimer(ref _weatherPollTimer);
            StopTimer(ref _timeSyncTimer);
            StopTimer(ref _lightningFlashTimer);
            StopTimer(ref _weatherRetryTimer);
            StopCloudSwapTicker();
        }

        private void Log(LogArea area, string message)
        {
            Puts($"[{area}] {message}");
        }

        private void LogDebug(LogArea area, string message)
        {
            if (config != null && config.DebugMode)
                Puts($"[{area}] {message}");
        }

        // ==================== REAL TIME / DATE SYSTEM ====================

        private void StartTimeSystem()
        {
            if (config.TimeSystem == null || !config.TimeSystem.Enabled) return;
            StopTimer(ref _timeSyncTimer);

            // RTC (SyncHourToRealTime): own the clock — freeze vanilla progression.
            // RTC off: leave ProgressTime on so the in-game hour still cycles at game
            // speed; SyncDateToRealDate will hold Year/Month/Day until the real
            // calendar date changes (see SyncRealTimeAndDate / HoldInGameDate).
            try
            {
                var todTime = TOD_Sky.Instance?.Components?.Time;
                if (todTime != null)
                {
                    if (SplitDayActive() && config.TimeSystem.SplitDay.DynamicSpeedScaling)
                    {
                        if (float.IsNaN(_vanillaDayLengthMinutes))
                            _vanillaDayLengthMinutes = todTime.DayLengthInMinutes;
                        todTime.ProgressTime = true;
                        todTime.UseTimeCurve = false;
                        _timeProgressDisabled = false;
                        _splitSpeedScalingOn = true;
                        Puts("[Time] SplitDay dynamic speed scaling on (ProgressTime runs; day length follows day/night rate).");
                    }
                    else if (config.TimeSystem.SyncHourToRealTime || SplitDayActive())
                    {
                        todTime.ProgressTime = false;
                        todTime.UseTimeCurve = false;
                        _timeProgressDisabled = true;
                        _splitSpeedScalingOn = false;
                        Puts(SplitDayActive()
                            ? "[Time] Vanilla time progression disabled (SplitDay snaps hour; DynamicSpeedScaling off)."
                            : "[Time] Vanilla time progression disabled (RTC hour sync on).");
                    }
                    else
                    {
                        todTime.ProgressTime = true;
                        todTime.UseTimeCurve = true;
                        _timeProgressDisabled = false;
                        _splitSpeedScalingOn = false;
                        Puts("[Time] Vanilla time progression left on (RTC hour sync off — date hold only).");
                    }
                }
            }
            catch (Exception ex)
            {
                Puts($"[Time] Could not configure ProgressTime: {ex.Message}");
            }

            // Cover the initial force so catch-up events are killed.
            BeginEventSuppress();

            float interval = SplitDayActive()
                ? Mathf.Clamp(config.TimeSystem.SplitDay.UpdateIntervalSeconds, 1f, 10f)
                : Mathf.Max(10f, config.TimeSystem.UpdateIntervalSeconds);
            RestartEvery(ref _timeSyncTimer, interval, SyncRealTimeAndDate);

            // When local weather is enabled the first successful Open-Meteo response
            // already has the real TZ offset + sunrise/sunset and will call
            // SyncRealTimeAndDate immediately (so the date-set log is the first
            // accurate time action). Only bootstrap with a delayed call when
            // weather is off (offset stays 0 / UTC).
            if (!config.UseLocalWeather)
                timer.Once(3f, SyncRealTimeAndDate);

            if (SplitDayActive())
            {
                var s = config.TimeSystem.SplitDay;
                Puts($"[Time] Real-time system started (update every {interval:F0}s). SplitDay ON days/real={s.InGameDaysPerRealDay} day%={s.DaytimePercent} night%={s.NighttimePercent} window={s.DayStartHour:F1}-{s.DayEndHour:F1}");
            }
            else
                Puts($"[Time] Real-time system started (update every {interval:F0}s). SyncHour={config.TimeSystem.SyncHourToRealTime}, SyncDate={config.TimeSystem.SyncDateToRealDate}");
        }

        private void RestoreVanillaTimeProgression()
        {
            try
            {
                var todTime = TOD_Sky.Instance?.Components?.Time;
                if (todTime != null)
                {
                    if (_splitSpeedScalingOn && !float.IsNaN(_vanillaDayLengthMinutes))
                    {
                        todTime.DayLengthInMinutes = _vanillaDayLengthMinutes;
                        Puts($"[Time] Restored vanilla DayLengthInMinutes={_vanillaDayLengthMinutes:F1}");
                    }
                    if (_timeProgressDisabled || _splitSpeedScalingOn)
                    {
                        todTime.ProgressTime = true;
                        todTime.UseTimeCurve = true;
                        Puts("[Time] Vanilla time progression restored.");
                    }
                }
            }
            catch { }
            _timeProgressDisabled = false;
            _splitSpeedScalingOn = false;
            _appliedDayLengthMinutes = float.NaN;
        }

        private void SyncRealTimeAndDate()
        {
            if (config.TimeSystem == null || !config.TimeSystem.Enabled) return;
            if (TOD_Sky.Instance == null || TOD_Sky.Instance.Cycle == null) return;

            try
            {
                // Tournament overrides take highest priority
                if (_tournamentActive || (config.Tournament != null && config.Tournament.Enabled))
                {
                    ApplyTournamentOverrides();
                    return;
                }

                DateTime utcNow = DateTime.UtcNow;
                DateTime localNow = utcNow.AddSeconds(_cachedUtcOffsetSeconds);

                // --- Hour sync ---
                // SplitDay owns the clock when enabled (compressed days + variable day/night rate).
                // Otherwise RTC writes the real local hour.
                if (SplitDayActive())
                {
                    ApplySplitDayClock(localNow);
                }
                else if (config.TimeSystem.SyncHourToRealTime)
                {
                    float realHour = (float)localNow.TimeOfDay.TotalHours;
                    TOD_Sky.Instance.Cycle.Hour = realHour;
                    lastWorldHour = realHour;
                }

                // --- Date sync (drives correct moon phase) ---
                // Always run on the very first successful sync (even if calendar day
                // already matches) so the load-time log appears with the real TZ
                // offset as soon as Open-Meteo data arrives.
                //
                // When RTC hour sync is OFF, or SplitDay is cycling the hour past
                // midnight X times per real day, vanilla ProgressTime / day-roll
                // must not advance the calendar. Hold Year/Month/Day to the last
                // real local date until the real calendar date changes.
                if (config.TimeSystem.SyncDateToRealDate)
                {
                    if (localNow.Date != _lastLocalDate.Date || !_initialDateSynced)
                    {
                        SetInGameDate(localNow);
                        _lastLocalDate = localNow;
                        _initialDateSynced = true;
                        Puts($"[Time] In-game date set to real local date: {localNow:yyyy-MM-dd} (TZ offset {_cachedUtcOffsetSeconds / 3600f:F1}h)");
                    }
                    else if (_initialDateSynced && (!config.TimeSystem.SyncHourToRealTime || SplitDayActive()))
                    {
                        HoldInGameDate(_lastLocalDate);
                    }
                }

                // --- Polar handling (hour freeze skipped while SplitDay owns the clock) ---
                if (!SplitDayActive() && config.TimeSystem.Polar != null && config.TimeSystem.Polar.Enabled)
                    ApplyPolarRules(localNow);

                // --- Moon mode ---
                ApplyMoonMode();

                // When local weather is off, still apply a lightweight solar brightness
                // so Option A works without the full weather system.
                if (config.TimeSystem.UseRealSolarAtmosphere && !config.UseLocalWeather)
                    ApplyStandaloneSolarBrightness(localNow);

                // Update stats — hour from cycle; day ordinal owned by SetInGameDate when date sync is on
                lastWorldHour = TOD_Sky.Instance.Cycle.Hour;
                if (config.TimeSystem == null || !config.TimeSystem.SyncDateToRealDate)
                    lastWorldDay = (int)TOD_Sky.Instance.Cycle.Day;
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"[Time] SyncRealTimeAndDate error: {ex.Message}");
            }
        }

        private bool SplitDayActive()
        {
            return config?.TimeSystem != null
                && config.TimeSystem.Enabled
                && config.TimeSystem.SplitDay != null
                && config.TimeSystem.SplitDay.Enabled;
        }

        /// <summary>
        /// Map real local time-of-day onto X in-game days packed into one real 24h,
        /// with daytime/nighttime using different clock rates. Real AlignRealHour
        /// (default 00:00) always equals in-game 00:00 of cycle 0.
        /// </summary>
        private void ApplySplitDayClock(DateTime localNow)
        {
            var s = config.TimeSystem.SplitDay;
            int days = Mathf.Clamp(s.InGameDaysPerRealDay, 1, 24);
            float align = Mathf.Repeat(s.AlignRealHour, 24f);

            double realSec = localNow.TimeOfDay.TotalSeconds;
            double alignSec = align * 3600.0;
            double shifted = realSec - alignSec;
            if (shifted < 0) shifted += 86400.0;

            double slice = 86400.0 / days;
            int idx = (int)Math.Floor(shifted / slice);
            if (idx < 0) idx = 0;
            if (idx >= days) idx = days - 1;
            double u = (shifted - idx * slice) / slice;
            u = Math.Max(0.0, Math.Min(0.999999, u));

            float hour;
            float rate;
            MapSplitDayProgress((float)u, s, out hour, out rate);
            rate = BlendSplitDayRate(hour, s, rate);

            var cycle = TOD_Sky.Instance.Cycle;
            float prev = cycle.Hour;
            bool wrapped = hour + 18f < prev;
            if (wrapped)
                BeginEventSuppress();

            if (s.DynamicSpeedScaling)
                ApplySplitDaySpeed(rate);

            float drift = HourDelta(prev, hour);
            float maxDrift = s.HourDriftCorrection > 0f ? s.HourDriftCorrection : 0.04f;
            // Always snap on wrap, when scaling is off, or when engine time has drifted
            // off the real-midnight lock.
            if (!s.DynamicSpeedScaling || wrapped || drift >= maxDrift)
                cycle.Hour = hour;

            lastWorldHour = cycle.Hour;
            lastSplitDayIndex = idx;
            lastSplitClockRate = rate;
        }

        private static float HourDelta(float a, float b)
        {
            float d = Mathf.Abs(Mathf.Repeat(a, 24f) - Mathf.Repeat(b, 24f));
            return d > 12f ? 24f - d : d;
        }

        /// <summary>
        /// Ease the day/night clock rate across dawn and dusk so speed does not slam.
        /// </summary>
        private static float BlendSplitDayRate(float gameHour, SplitDayConfig s, float segmentRate)
        {
            float band = Mathf.Max(0f, s.RateBlendHours);
            if (band < 0.01f) return segmentRate;

            float dayStart = Mathf.Clamp(s.DayStartHour, 0f, 23.9f);
            float dayEnd = Mathf.Clamp(s.DayEndHour, dayStart + 0.1f, 24f);

            float dayPct = Mathf.Max(0f, s.DaytimePercent);
            float nightPct = Mathf.Max(0f, s.NighttimePercent);
            float sum = dayPct + nightPct;
            if (sum < 0.01f) { dayPct = 70f; nightPct = 30f; sum = 100f; }
            dayPct /= sum;
            nightPct /= sum;

            float hDay = dayEnd - dayStart;
            float hNight = (24f - dayEnd) + dayStart;
            float pack = Mathf.Max(1, s.InGameDaysPerRealDay) / 24f;
            float dayRate = dayPct > 1e-5f ? (hDay / dayPct) * pack : segmentRate;
            float nightRate = nightPct > 1e-5f && hNight > 0.01f ? (hNight / nightPct) * pack : segmentRate;

            float h = Mathf.Repeat(gameHour, 24f);
            if (h >= dayStart - band && h <= dayStart + band)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(dayStart - band, dayStart + band, h));
                return Mathf.Lerp(nightRate, dayRate, t);
            }
            if (h >= dayEnd - band && h <= dayEnd + band)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(dayEnd - band, dayEnd + band, h));
                return Mathf.Lerp(dayRate, nightRate, t);
            }
            if (h > dayStart && h < dayEnd) return dayRate;
            return nightRate;
        }

        /// <summary>
        /// TOD DayLengthInMinutes is real minutes for a full 24h game cycle.
        /// rate = game hours per real hour → minutes = (24 / rate) * 60.
        /// </summary>
        private void ApplySplitDaySpeed(float gameHoursPerRealHour)
        {
            var todTime = TOD_Sky.Instance?.Components?.Time;
            if (todTime == null) return;
            float rate = Mathf.Max(0.05f, gameHoursPerRealHour);
            float minutes = Mathf.Clamp((24f / rate) * 60f, 8f, 2400f);
            if (!float.IsNaN(_appliedDayLengthMinutes) && Mathf.Abs(minutes - _appliedDayLengthMinutes) < 0.75f)
                return;
            if (float.IsNaN(_vanillaDayLengthMinutes))
                _vanillaDayLengthMinutes = todTime.DayLengthInMinutes;
            todTime.DayLengthInMinutes = minutes;
            todTime.ProgressTime = true;
            todTime.UseTimeCurve = false;
            _appliedDayLengthMinutes = minutes;
            _splitSpeedScalingOn = true;
        }

        private static void MapSplitDayProgress(float u, SplitDayConfig s, out float gameHour, out float gameHoursPerRealHour)
        {
            float dayStart = Mathf.Clamp(s.DayStartHour, 0f, 23.9f);
            float dayEnd = Mathf.Clamp(s.DayEndHour, dayStart + 0.1f, 24f);

            float hDay = dayEnd - dayStart;
            float hNightMorn = dayStart;          // 00:00 → day start
            float hNightEve = 24f - dayEnd;       // day end → 24:00
            float hNight = hNightMorn + hNightEve;

            float dayPct = Mathf.Max(0f, s.DaytimePercent);
            float nightPct = Mathf.Max(0f, s.NighttimePercent);
            float sum = dayPct + nightPct;
            if (sum < 0.01f) { dayPct = 70f; nightPct = 30f; sum = 100f; }
            dayPct /= sum;
            nightPct /= sum;

            // Night real-time is split across evening + morning by game-hour length.
            float fMorn = hNight > 0.01f ? nightPct * (hNightMorn / hNight) : 0f;
            float fDay = dayPct;
            float fEve = hNight > 0.01f ? nightPct * (hNightEve / hNight) : 0f;

            // Sequence inside each compressed day, starting at game 00:00:
            //   morning night → day → evening night
            float u0 = 0f;
            float u1 = u0 + fMorn;
            float u2 = u1 + fDay;
            float u3 = 1f;

            if (u < u1 && fMorn > 1e-5f)
            {
                float t = (u - u0) / fMorn;
                gameHour = Mathf.Lerp(0f, dayStart, t);
                gameHoursPerRealHour = (hNightMorn / fMorn) * (Mathf.Max(1, s.InGameDaysPerRealDay) / 24f);
            }
            else if (u < u2 && fDay > 1e-5f)
            {
                float t = (u - u1) / fDay;
                gameHour = Mathf.Lerp(dayStart, dayEnd, t);
                gameHoursPerRealHour = (hDay / fDay) * (Mathf.Max(1, s.InGameDaysPerRealDay) / 24f);
            }
            else if (fEve > 1e-5f)
            {
                float t = Mathf.Clamp01((u - u2) / Math.Max(1e-5f, u3 - u2));
                gameHour = Mathf.Lerp(dayEnd, 24f, t);
                if (gameHour >= 24f) gameHour = 23.999f;
                gameHoursPerRealHour = (hNightEve / fEve) * (Mathf.Max(1, s.InGameDaysPerRealDay) / 24f);
            }
            else
            {
                gameHour = dayEnd;
                gameHoursPerRealHour = Mathf.Max(1, s.InGameDaysPerRealDay);
            }
        }

        /// <summary>
        /// Lightweight brightness push used when UseLocalWeather is false.
        /// Full weather path already modulates wx.targetBrightness via GetSolarDayFactor.
        /// </summary>
        private void ApplyStandaloneSolarBrightness(DateTime localNow)
        {
            float factor = GetSolarDayFactor(localNow);
            if (factor < 0f) return;

            float nightScale = Mathf.Clamp(config.TimeSystem.NightBrightnessScale, 0.55f, 0.95f);
            if (_isPolarNight && config.TimeSystem.Polar != null)
                nightScale = Mathf.Max(nightScale, Mathf.Clamp01(config.TimeSystem.Polar.MinimumNightBrightness));

            float bri = Mathf.Lerp(nightScale, 1.05f, factor);
            // Only write when it actually changed enough to matter
            if (float.IsNaN(wx.appliedBrightness) || Mathf.Abs(bri - wx.appliedBrightness) >= 0.02f)
            {
                SetWeatherConvar("weather.atmosphere_brightness", bri, ref wx.appliedBrightness, 0.02f);
                wx.currentBrightness = bri;
                wx.targetBrightness = bri;
            }
        }

        private void SetInGameDate(DateTime localDate)
        {
            try
            {
                // TOD_Sky uses Year / Month / Day together for celestial positions
                // (sun path, moon phase, stars). Setting only a synthetic Day counter
                // produced a visual moon that did not match the real phase we report.

                BeginEventSuppress();

                var cycle = TOD_Sky.Instance.Cycle;
                cycle.Year  = localDate.Year;
                cycle.Month = localDate.Month;
                cycle.Day   = localDate.Day;

                // Stable ordinal for stats (does not drive the visual moon).
                lastWorldDay = localDate.DayOfYear + (localDate.Year % 10) * 365;

                float realPhase = CalculateRealMoonPhase(localDate);
                lastMoonPhase = realPhase;
                lastMoonPhaseName = GetMoonPhaseName(realPhase);
                lastMoonIllumination = Mathf.Clamp01(Mathf.Sin(realPhase * Mathf.PI));
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"[Time] SetInGameDate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Quietly re-assert Year/Month/Day to the held real date without event suppress.
        /// Used when RTC hour sync is off so vanilla ProgressTime can cycle the hour
        /// (and would otherwise roll Cycle.Day at in-game midnight) while the calendar
        /// date stays fixed until the real local date changes.
        /// </summary>
        private void HoldInGameDate(DateTime heldDate)
        {
            try
            {
                var cycle = TOD_Sky.Instance?.Cycle;
                if (cycle == null) return;

                if (cycle.Year == heldDate.Year && cycle.Month == heldDate.Month && cycle.Day == heldDate.Day)
                    return;

                cycle.Year  = heldDate.Year;
                cycle.Month = heldDate.Month;
                cycle.Day   = heldDate.Day;

                lastWorldDay = heldDate.DayOfYear + (heldDate.Year % 10) * 365;

                float realPhase = CalculateRealMoonPhase(heldDate);
                lastMoonPhase = realPhase;
                lastMoonPhaseName = GetMoonPhaseName(realPhase);
                lastMoonIllumination = Mathf.Clamp01(Mathf.Sin(realPhase * Mathf.PI));

                if (config.DebugMode)
                    Puts($"[Time] Held in-game date at {heldDate:yyyy-MM-dd} (vanilla day-roll reversed)");
            }
            catch (Exception ex)
            {
                if (config.DebugMode) Puts($"[Time] HoldInGameDate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Approximate real moon phase 0..1 (0 = new, 0.5 = full) from date.
        /// Simple lunar cycle approximation (synodic month ~29.53059 days).
        /// </summary>
        private float CalculateRealMoonPhase(DateTime date)
        {
            // Known new moon reference: 2000-01-06 18:14 UTC
            DateTime knownNewMoon = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
            double daysSince = (date.ToUniversalTime() - knownNewMoon).TotalDays;
            double cycle = 29.530588853;
            double phase = (daysSince % cycle) / cycle;
            if (phase < 0) phase += 1.0;
            return (float)phase;
        }

        private void ApplyPolarRules(DateTime localNow)
        {
            if (!_isPolarDay && !_isPolarNight) return;

            var polar = config.TimeSystem.Polar;
            var cycle = TOD_Sky.Instance.Cycle;

            if (_isPolarDay)
            {
                switch (polar.PolarDayMode)
                {
                    case "PermanentDay":
                    case "FreezeAtNoon":
                        cycle.Hour = 12f;
                        lastWorldHour = 12f;
                        break;
                    // ContinueClock = do nothing extra
                }
            }
            else if (_isPolarNight)
            {
                switch (polar.PolarNightMode)
                {
                    case "PermanentNight":
                    case "FreezeAtMidnight":
                        cycle.Hour = 0f;
                        lastWorldHour = 0f;
                        break;
                    case "BrightNight":
                        // Keep real hour but we can boost brightness via atmosphere convars if desired
                        // (already handled in weather brightness logic)
                        break;
                    // ContinueClock = do nothing
                }
            }
        }

        /// <summary>
        /// Returns 0 (full night) .. 1 (full day) based on real Open-Meteo sunrise/sunset.
        /// Used by Option A to drive atmosphere while keeping Cycle.Hour = real wall-clock time.
        /// Returns -1 when solar data is unavailable (caller should fall back to TOD-hour logic).
        /// </summary>
        private float GetSolarDayFactor(DateTime localNow)
        {
            // SplitDay owns the visual day/night cycle via Cycle.Hour — do not
            // also drive atmosphere from real sunrise/sunset or polar daylight.
            if (SplitDayActive()) return -1f;
            if (_isPolarDay) return 1f;
            if (_isPolarNight)
            {
                // BrightNight still wants some residual light; Permanent* is handled by hour freeze.
                var polar = config.TimeSystem?.Polar;
                if (polar != null && polar.PolarNightMode == "BrightNight")
                    return Mathf.Clamp01(polar.MinimumNightBrightness);
                return 0f;
            }

            if (!_lastSunrise.HasValue || !_lastSunset.HasValue)
                return -1f;

            TimeSpan nowT = localNow.TimeOfDay;
            TimeSpan riseT = _lastSunrise.Value.TimeOfDay;
            TimeSpan setT = _lastSunset.Value.TimeOfDay;

            double nowSec = nowT.TotalSeconds;
            double riseSec = riseT.TotalSeconds;
            double setSec = setT.TotalSeconds;

            // Guard against pathological data (e.g. polar edge cases already filtered above)
            if (setSec <= riseSec)
                setSec += 86400.0;

            float halfTransitionMin = Mathf.Clamp(config.TimeSystem?.DawnDuskMinutes ?? 40f, 10f, 90f);
            double halfSec = halfTransitionMin * 60.0;
            double fullSec = halfSec * 2.0;

            // Deep night before dawn window
            if (nowSec < riseSec - halfSec)
                return 0f;

            // Dawn ramp: rise - half → rise + half
            if (nowSec < riseSec + halfSec)
            {
                float t = (float)((nowSec - (riseSec - halfSec)) / fullSec);
                return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            }

            // Full day
            if (nowSec < setSec - halfSec)
                return 1f;

            // Dusk ramp: set - half → set + half
            if (nowSec < setSec + halfSec)
            {
                float t = (float)((nowSec - (setSec - halfSec)) / fullSec);
                return Mathf.SmoothStep(1f, 0f, Mathf.Clamp01(t));
            }

            // Deep night after dusk
            return 0f;
        }

        private void ApplyMoonMode()
        {
            if (config.TimeSystem == null) return;
            string mode = config.TimeSystem.MoonMode ?? "Real";

            if (mode == "Real")
            {
                // Visual moon driven by Year/Month/Day in SetInGameDate.
                return;
            }

            var cycle = TOD_Sky.Instance?.Cycle;
            if (cycle == null) return;

            if (mode == "ForceFull")
            {
                BeginEventSuppress();
                cycle.Year  = 2020;
                cycle.Month = 6;
                cycle.Day   = 5;
                lastMoonPhase = 0.5f;
                lastMoonPhaseName = "Full Moon";
                lastMoonIllumination = 1f;
            }
            else if (mode == "ForceNew")
            {
                BeginEventSuppress();
                cycle.Year  = 2020;
                cycle.Month = 6;
                cycle.Day   = 21;
                lastMoonPhase = 0f;
                lastMoonPhaseName = "New Moon";
                lastMoonIllumination = 0f;
            }
            else if (mode == "Custom")
            {
                float p = Mathf.Clamp01(config.TimeSystem.CustomMoonPhase);
                lastMoonPhase = p;
                lastMoonPhaseName = GetMoonPhaseName(p);
                lastMoonIllumination = Mathf.Clamp01(Mathf.Sin(p * Mathf.PI));
                BeginEventSuppress();
                cycle.Year  = 2020;
                cycle.Month = 6;
                cycle.Day   = Mathf.Clamp(1 + (int)(p * 27f), 1, 28);
            }
        }

        private void ApplyTournamentOverrides()
        {
            if (config.Tournament == null) return;
            var t = config.Tournament;
            var cycle = TOD_Sky.Instance?.Cycle;
            if (cycle == null) return;

            BeginEventSuppress();

            if (t.ForcePermanentDay)
            {
                cycle.Hour = 12f;
                lastWorldHour = 12f;
            }

            if (t.FreezeTimeAtHour >= 0f && t.FreezeTimeAtHour <= 24f)
            {
                cycle.Hour = t.FreezeTimeAtHour;
                lastWorldHour = t.FreezeTimeAtHour;
            }

            if (t.ForceFullMoon)
            {
                cycle.Year  = 2020;
                cycle.Month = 6;
                cycle.Day   = 5;
                lastMoonPhase = 0.5f;
                lastMoonPhaseName = "Full Moon";
                lastMoonIllumination = 1f;
            }

            if (t.DisableDateProgression)
            {
                // Date is already locked by not calling SetInGameDate
            }

            // Weather disable is handled by simply not updating targets if desired
            // (can be extended later)
        }

        // ==================== OPEN-METEO (extended with daily + offset) ====================

        private void SyncLocalWeather()
        {
            if (!config.UseLocalWeather) return;

            double lat = config.LocalWeatherLatitude;
            double lon = config.LocalWeatherLongitude;

            string urlFull = BuildOpenMeteoUrl(lat, lon, rich: true);
            string urlMin = BuildOpenMeteoUrl(lat, lon, rich: false);

            void HandleResponse(int httpCode, string response, bool wasRich)
            {
                bool looksJson = !string.IsNullOrEmpty(response) && (response[0] == '{' || response[0] == '[');
                if (httpCode != 200 || !looksJson)
                {
                    string body = string.IsNullOrEmpty(response) ? "empty body" : response.Substring(0, Math.Min(180, response.Length));
                    Puts($"[Weather] Open-Meteo HTTP {httpCode} ({(wasRich ? "rich" : "minimal")}) -- {body}");
                    if (wasRich)
                    {
                        Puts("[Weather] Retrying with minimal variable set...");
                        EnqueueOpenMeteo(urlMin, (c2, r2) => HandleResponse(c2, r2, false));
                        return;
                    }
                    wx._consecutiveWeatherFails++;
                    float age = wx._lastWeatherSuccessTime > 0f ? Time.realtimeSinceStartup - wx._lastWeatherSuccessTime : -1f;
                    Puts($"[Weather] Poll failed (x{wx._consecutiveWeatherFails}); continuing with {_weatherAnchors.Count} stale anchors" +
                         (age >= 0 ? $" (last success {age:F0}s ago)" : ""));
                    ScheduleWeatherRetry();
                    return;
                }

                try
                {
                    var data = JsonConvert.DeserializeObject<OpenMeteoResponse>(response);
                    if (data == null)
                    {
                        Puts("[Weather] Open-Meteo JSON deserialized to null -- keeping existing anchors");
                        wx._consecutiveWeatherFails++;
                        return;
                    }

                    // Cache timezone / offset for real-time system
                    if (data.utc_offset_seconds.HasValue)
                        _cachedUtcOffsetSeconds = data.utc_offset_seconds.Value;
                    if (!string.IsNullOrEmpty(data.timezone))
                        _cachedTimezone = data.timezone;

                    // Open-Meteo with timezone=auto returns LOCAL wall-clock strings (no Z/offset).
                    // utc_offset_seconds = seconds to ADD to UTC to get local → UTC = local - offset.
                    double apiOffsetSec = data.utc_offset_seconds ?? _cachedUtcOffsetSeconds;

                    // Daily astronomical data (sunrise/sunset are also local wall-clock)
                    if (data.daily != null)
                    {
                        if (data.daily.daylight_duration != null && data.daily.daylight_duration.Count > 0)
                        {
                            float dur = data.daily.daylight_duration[0];
                            _daylightDurationSeconds = dur;
                            _isPolarDay = dur > 86000f;
                            _isPolarNight = dur < 400f;
                            if (_isPolarDay || _isPolarNight)
                                Puts($"[Time] Polar detection: daylight={dur:F0}s -> {(_isPolarDay ? "POLAR DAY" : "POLAR NIGHT")}");
                        }
                        // Keep sunrise/sunset as local wall-clock (TimeOfDay only is compared to localNow).
                        if (data.daily.sunrise != null && data.daily.sunrise.Count > 0 && DateTime.TryParse(data.daily.sunrise[0], null, System.Globalization.DateTimeStyles.None, out DateTime srLocal))
                            _lastSunrise = DateTime.SpecifyKind(srLocal, DateTimeKind.Unspecified);
                        if (data.daily.sunset != null && data.daily.sunset.Count > 0 && DateTime.TryParse(data.daily.sunset[0], null, System.Globalization.DateTimeStyles.None, out DateTime ssLocal))
                            _lastSunset = DateTime.SpecifyKind(ssLocal, DateTimeKind.Unspecified);
                    }

                    var anchors = _anchorScratch;
                    anchors.Clear();

                    // ---- CPU-friendly ingest ----
                    // 1) Pre-parse hourly timestamps ONCE (old code re-parsed every hourly
                    //    stamp for every minutely sample → thousands of DateTime.TryParse).
                    // 2) Monotonic index walk (both series are sorted).
                    // 3) Subsample minutely series so we never build 96 dense anchors.
                    // 4) Defer SyncRealTimeAndDate + UpdateTargetsFromClock off this frame.
                    //
                    // IMPORTANT: API times are local wall-clock. Convert to UTC with the
                    // response offset so interpolation against DateTime.UtcNow is correct.
                    // (Previously AssumeUniversal treated local strings as UTC → ~4h shift on EDT.)

                    var hourlyTimes = _hourlyTimeScratch;
                    hourlyTimes.Clear();
                    int hourlyCount = 0;
                    if (data.hourly?.time != null)
                    {
                        hourlyCount = data.hourly.time.Count;
                        for (int h = 0; h < hourlyCount; h++)
                        {
                            if (DateTime.TryParse(data.hourly.time[h], null, System.Globalization.DateTimeStyles.None, out DateTime htLocal))
                                hourlyTimes.Add(DateTime.SpecifyKind(htLocal, DateTimeKind.Unspecified).AddSeconds(-apiOffsetSec));
                            else
                                hourlyTimes.Add(DateTime.MinValue);
                        }
                    }

                    // Prefer 15-minute series when present (subsampled only if above MaxMinutelyAnchors)
                    if (data.minutely_15?.time != null && data.minutely_15.time.Count > 0)
                    {
                        int n = data.minutely_15.time.Count;
                        int maxMinutely = Mathf.Clamp(config.MaxMinutelyAnchors > 0 ? config.MaxMinutelyAnchors : 96, 24, 192);
                        // Cap density: keep all samples unless over max; then stride down.
                        int step = n > maxMinutely ? Mathf.Max(1, (n + maxMinutely - 1) / maxMinutely) : 1;
                        int hPtr = 0; // monotonic pointer into pre-parsed hourly times

                        for (int i = 0; i < n; i += step)
                        {
                            if (!DateTime.TryParse(data.minutely_15.time[i], null, System.Globalization.DateTimeStyles.None, out DateTime tLocal))
                                continue;
                            DateTime t = DateTime.SpecifyKind(tLocal, DateTimeKind.Unspecified).AddSeconds(-apiOffsetSec);

                            float pressure = data.current?.surface_pressure ?? 1013f;
                            float precipProb = 0f, cape = 0f, shortwave = 0f, soil = 0.25f;
                            float cLow = 0f, cMid = 0f, cHigh = 0f;

                            if (hourlyCount > 0)
                            {
                                // Advance pointer while next hourly is still <= t
                                while (hPtr + 1 < hourlyCount && hourlyTimes[hPtr + 1] != DateTime.MinValue && hourlyTimes[hPtr + 1] <= t)
                                    hPtr++;

                                int hi0 = hPtr;
                                int hi1 = (hPtr + 1 < hourlyCount) ? hPtr + 1 : hPtr;
                                DateTime ht0 = hourlyTimes[hi0];
                                DateTime ht1 = hourlyTimes[hi1];
                                if (ht0 == DateTime.MinValue) { ht0 = t; hi0 = 0; }
                                if (ht1 == DateTime.MinValue) { ht1 = ht0; hi1 = hi0; }

                                double pSpan = (ht1 - ht0).TotalSeconds;
                                float pu = pSpan > 1.0 ? Mathf.Clamp01((float)((t - ht0).TotalSeconds / pSpan)) : 0f;

                                pressure = Mathf.Lerp(
                                    SafeAt(data.hourly.surface_pressure, hi0, pressure),
                                    SafeAt(data.hourly.surface_pressure, hi1, pressure), pu);
                                precipProb = Mathf.Lerp(SafeAt(data.hourly.precipitation_probability, hi0, 0f), SafeAt(data.hourly.precipitation_probability, hi1, 0f), pu);
                                cape = Mathf.Lerp(SafeAt(data.hourly.cape, hi0, 0f), SafeAt(data.hourly.cape, hi1, 0f), pu);
                                shortwave = Mathf.Lerp(SafeAt(data.hourly.shortwave_radiation, hi0, 0f), SafeAt(data.hourly.shortwave_radiation, hi1, 0f), pu);
                                soil = Mathf.Lerp(SafeAt(data.hourly.soil_moisture_0_to_7cm, hi0, 0.25f), SafeAt(data.hourly.soil_moisture_0_to_7cm, hi1, 0.25f), pu);
                                cLow = Mathf.Lerp(SafeAt(data.hourly.cloud_cover_low, hi0, 0) / 100f, SafeAt(data.hourly.cloud_cover_low, hi1, 0) / 100f, pu);
                                cMid = Mathf.Lerp(SafeAt(data.hourly.cloud_cover_mid, hi0, 0) / 100f, SafeAt(data.hourly.cloud_cover_mid, hi1, 0) / 100f, pu);
                                cHigh = Mathf.Lerp(SafeAt(data.hourly.cloud_cover_high, hi0, 0) / 100f, SafeAt(data.hourly.cloud_cover_high, hi1, 0) / 100f, pu);
                            }

                            float rain = SafeAt(data.minutely_15.rain, i, 0f);
                            float precip = SafeAt(data.minutely_15.precipitation, i, 0f);
                            float snow = SafeAt(data.minutely_15.snowfall, i, 0f);
                            float showers = SafeAt(data.minutely_15.showers, i, 0f);

                            anchors.Add(new WeatherSample
                            {
                                TimeUtc = t,
                                WeatherCode = SafeAt(data.minutely_15.weather_code, i, 0),
                                CloudPercent = SafeAt(data.minutely_15.cloud_cover, i, 0) / 100f,
                                CloudLow = cLow, CloudMid = cMid, CloudHigh = cHigh,
                                Temperature = SafeAt(data.minutely_15.temperature_2m, i, 15f),
                                DewPoint = SafeAt(data.minutely_15.dew_point_2m, i, 10f),
                                Humidity = SafeAt(data.minutely_15.relative_humidity_2m, i, 60f),
                                Pressure = pressure,
                                RainMm = rain > 0 ? rain : precip,
                                SnowMm = snow,
                                ShowersMm = showers,
                                PrecipProb = precipProb,
                                Cape = cape,
                                Shortwave = shortwave,
                                SoilMoisture = soil,
                                WindKmh = SafeAt(data.minutely_15.wind_speed_10m, i, 0f),
                                GustKmh = SafeAt(data.minutely_15.wind_gusts_10m, i, 0f),
                                WindDir = SafeAt(data.minutely_15.wind_direction_10m, i, 0f),
                                VisibilityM = SafeAt(data.minutely_15.visibility, i, 20000f),
                                IsDay = true
                            });
                        }
                    }

                    // Hourly samples fill gaps / extend horizon. Dup check uses sorted
                    // minutely anchors + two-pointer style (anchors already time-ordered).
                    if (data.hourly?.time != null && hourlyCount > 0)
                    {
                        int aPtr = 0;
                        for (int i = 0; i < hourlyCount; i++)
                        {
                            DateTime t = hourlyTimes[i];
                            if (t == DateTime.MinValue) continue;

                            // Advance past anchors clearly before this hourly
                            while (aPtr < anchors.Count && anchors[aPtr].TimeUtc < t.AddMinutes(-1.0))
                                aPtr++;

                            bool dup = false;
                            for (int di = aPtr; di < anchors.Count; di++)
                            {
                                double mins = (anchors[di].TimeUtc - t).TotalMinutes;
                                if (mins > 1.0) break;
                                if (mins > -1.0 && mins < 1.0) { dup = true; break; }
                            }
                            if (dup) continue;

                            float rain = SafeAt(data.hourly.rain, i, 0f);
                            float precip = SafeAt(data.hourly.precipitation, i, 0f);
                            float snow = SafeAt(data.hourly.snowfall, i, 0f);
                            anchors.Add(new WeatherSample
                            {
                                TimeUtc = t,
                                WeatherCode = SafeAt(data.hourly.weather_code, i, 0),
                                CloudPercent = SafeAt(data.hourly.cloud_cover, i, 0) / 100f,
                                CloudLow = SafeAt(data.hourly.cloud_cover_low, i, 0) / 100f,
                                CloudMid = SafeAt(data.hourly.cloud_cover_mid, i, 0) / 100f,
                                CloudHigh = SafeAt(data.hourly.cloud_cover_high, i, 0) / 100f,
                                Temperature = SafeAt(data.hourly.temperature_2m, i, 15f),
                                DewPoint = SafeAt(data.hourly.dew_point_2m, i, 10f),
                                Humidity = SafeAt(data.hourly.relative_humidity_2m, i, 60f),
                                Pressure = SafeAt(data.hourly.surface_pressure, i, 1013f),
                                RainMm = rain > 0 ? rain : precip,
                                SnowMm = snow,
                                ShowersMm = SafeAt(data.hourly.showers, i, 0f),
                                PrecipProb = SafeAt(data.hourly.precipitation_probability, i, 0f),
                                Cape = SafeAt(data.hourly.cape, i, 0f),
                                Shortwave = SafeAt(data.hourly.shortwave_radiation, i, 0f),
                                SoilMoisture = SafeAt(data.hourly.soil_moisture_0_to_7cm, i, 0.25f),
                                WindKmh = SafeAt(data.hourly.wind_speed_10m, i, 0f),
                                GustKmh = SafeAt(data.hourly.wind_gusts_10m, i, 0f),
                                WindDir = SafeAt(data.hourly.wind_direction_10m, i, 0f),
                                VisibilityM = SafeAt(data.hourly.visibility, i, 20000f),
                                IsDay = SafeAt(data.hourly.is_day, i, 1) > 0
                            });
                        }
                    }

                    if (anchors.Count == 0 && data.current != null)
                    {
                        var cur = data.current;
                        float cRain = cur.rain ?? 0f;
                        float cPrecip = cur.precipitation ?? 0f;
                        anchors.Add(new WeatherSample
                        {
                            TimeUtc = DateTime.UtcNow,
                            WeatherCode = cur.weather_code ?? 0,
                            CloudPercent = (cur.cloud_cover ?? 0) / 100f,
                            CloudLow = (cur.cloud_cover_low ?? 0) / 100f,
                            CloudMid = (cur.cloud_cover_mid ?? 0) / 100f,
                            CloudHigh = (cur.cloud_cover_high ?? 0) / 100f,
                            Temperature = cur.temperature_2m ?? 15f,
                            DewPoint = cur.dew_point_2m ?? 10f,
                            Humidity = cur.relative_humidity_2m ?? 60f,
                            Pressure = cur.surface_pressure ?? 1013f,
                            RainMm = cRain > 0 ? cRain : cPrecip,
                            SnowMm = cur.snowfall ?? 0f,
                            ShowersMm = cur.showers ?? 0f,
                            PrecipProb = cur.precipitation_probability ?? 0f,
                            Cape = cur.cape ?? 0f,
                            Shortwave = cur.shortwave_radiation ?? 0f,
                            SoilMoisture = cur.soil_moisture_0_to_7cm ?? 0.25f,
                            WindKmh = cur.wind_speed_10m ?? 0f,
                            GustKmh = cur.wind_gusts_10m ?? 0f,
                            WindDir = cur.wind_direction_10m ?? 0f,
                            VisibilityM = cur.visibility ?? 20000f,
                            IsDay = (cur.is_day ?? 1) > 0
                        });
                    }

                    anchors.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
                    if (anchors.Count == 0)
                    {
                        Puts("[Weather] Parsed 0 anchors -- keeping previous forecast");
                        wx._consecutiveWeatherFails++;
                        return;
                    }

                    // Swap into live list (still on this frame — cheap Clear/AddRange)
                    _weatherAnchors.Clear();
                    _weatherAnchors.AddRange(anchors);
                    wx._lastWeatherSuccessTime = Time.realtimeSinceStartup;
                    wx._consecutiveWeatherFails = 0;
                    StopTimer(ref _weatherRetryTimer);

                    int anchorCount = _weatherAnchors.Count;
                    string tzName = _cachedTimezone;
                    float tzOff = _cachedUtcOffsetSeconds / 3600f;
                    bool richFlag = wasRich;

                    // Spread follow-up work across the next few frames so the HTTP
                    // callback frame does not also run date sync + full target rebuild.
                    timer.Once(0.05f, () =>
                    {
                        if (config.TimeSystem != null && config.TimeSystem.Enabled)
                            SyncRealTimeAndDate();
                    });
                    timer.Once(0.20f, () =>
                    {
                        UpdateTargetsFromClock(forceLog: true);
                        Puts($"[Weather] Anchors loaded: {anchorCount} ({(richFlag ? "rich" : "minimal")}) | TZ={tzName} offset={tzOff:F1}h");
                    });
                }
                catch (Exception ex)
                {
                    Puts($"[Weather] LocalWeather parse error: {ex.Message}");
                }
            }

            EnqueueOpenMeteo(urlFull, (httpCode, response) => HandleResponse(httpCode, response, true));
        }

        private const float OpenMeteoTimeoutSeconds = 45f;

        private static readonly Dictionary<string, string> OpenMeteoHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = "LiveStatsWorld/1.1.39 (Rust dedicated; +https://open-meteo.com)",
            ["Accept"] = "application/json"
        };

        private void EnqueueOpenMeteo(string url, Action<int, string> callback)
        {
            webrequest.Enqueue(url, null, callback, this, Oxide.Core.Libraries.RequestMethod.GET, OpenMeteoHeaders, OpenMeteoTimeoutSeconds);
        }

        private void ScheduleWeatherRetry()
        {
            if (wx._consecutiveWeatherFails > 3) return;
            if (_weatherRetryTimer != null && !_weatherRetryTimer.Destroyed) return;
            float delay = wx._consecutiveWeatherFails <= 1 ? 20f : 45f;
            Log(LogArea.Weather, $"Retrying poll in {delay:F0}s");
            StopTimer(ref _weatherRetryTimer);
            _weatherRetryTimer = timer.Once(delay, () =>
            {
                _weatherRetryTimer = null;
                SyncLocalWeather();
            });
        }

        private string BuildOpenMeteoUrl(double lat, double lon, bool rich)
        {
            int days = Mathf.Clamp(config.ForecastHorizonDays, 1, 5);
            string current =
                "temperature_2m,dew_point_2m,relative_humidity_2m,precipitation,rain,snowfall,weather_code," +
                "cloud_cover,surface_pressure,wind_speed_10m,wind_gusts_10m,wind_direction_10m,visibility,is_day";
            string hourly =
                "temperature_2m,dew_point_2m,relative_humidity_2m,precipitation,rain,snowfall,weather_code," +
                "cloud_cover,cloud_cover_low,cloud_cover_mid,cloud_cover_high,surface_pressure," +
                "wind_speed_10m,wind_gusts_10m,wind_direction_10m,visibility,is_day,precipitation_probability";
            string minutely =
                "temperature_2m,dew_point_2m,relative_humidity_2m,precipitation,rain,snowfall,weather_code," +
                "cloud_cover,wind_speed_10m,wind_gusts_10m,wind_direction_10m,visibility";
            string daily = "sunrise,sunset,daylight_duration";

            if (rich)
            {
                current += ",showers,cloud_cover_low,cloud_cover_mid,cloud_cover_high";
                hourly += ",showers,cape,shortwave_radiation,soil_moisture_0_to_7cm";
                minutely += ",showers";
            }

            // Rich: hourly + a short minutely window. Minimal: hourly only.
            // Oxide HttpWebRequest NREs on a null SSL stream when the body is huge
            // (96 × 15-min slices + 3-day hourly used to trip WaitForResponse).
            if (!rich)
            {
                return $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                       $"&current={current}&hourly={hourly}&daily={daily}" +
                       $"&forecast_days={Mathf.Min(days, 2)}&timezone=auto";
            }

            int minutelyCount = Mathf.Clamp(config.ForecastMinutely15Count > 0 ? config.ForecastMinutely15Count : 48, 24, 96);
            return $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                   $"&current={current}&hourly={hourly}&minutely_15={minutely}&daily={daily}" +
                   $"&forecast_days={days}&forecast_minutely_15={minutelyCount}&timezone=auto";
        }

        private static float SafeAt(List<float> list, int i, float fallback) => list != null && i >= 0 && i < list.Count ? list[i] : fallback;
        private static float SafeAt(List<float?> list, int i, float fallback) { if (list == null || i < 0 || i >= list.Count) return fallback; return list[i] ?? fallback; }
        private static int SafeAt(List<int> list, int i, int fallback) => list != null && i >= 0 && i < list.Count ? list[i] : fallback;
        private static int SafeAt(List<int?> list, int i, int fallback) { if (list == null || i < 0 || i >= list.Count) return fallback; return list[i] ?? fallback; }

        // ==================== WEATHER TARGET / BLEND LOGIC (preserved from original) ====================

        private void UpdateTargetsFromClock(bool forceLog = false)
        {
            if (_weatherAnchors.Count == 0) return;

            DateTime now = DateTime.UtcNow;
            WeatherSample a = _weatherAnchors[0];
            WeatherSample b = _weatherAnchors[_weatherAnchors.Count - 1];

            if (now <= a.TimeUtc) b = a;
            else if (now >= b.TimeUtc) a = b;
            else
            {
                for (int i = 0; i < _weatherAnchors.Count - 1; i++)
                {
                    if (now >= _weatherAnchors[i].TimeUtc && now <= _weatherAnchors[i + 1].TimeUtc)
                    {
                        a = _weatherAnchors[i];
                        b = _weatherAnchors[i + 1];
                        break;
                    }
                }
            }

            double span = (b.TimeUtc - a.TimeUtc).TotalSeconds;
            float u = span > 1.0 ? Mathf.Clamp01((float)((now - a.TimeUtc).TotalSeconds / span)) : 0f;

            if (config.EnableAnticipatory && span > 30.0)
            {
                float segChange =
                    Mathf.Abs(b.RainMm - a.RainMm) / 8f +
                    Mathf.Abs(b.CloudPercent - a.CloudPercent) +
                    Mathf.Abs(b.Pressure - a.Pressure) / 25f +
                    Mathf.Abs(b.SnowMm - a.SnowMm) / 5f;
                if (segChange > 0.06f)
                {
                    float bias = Mathf.Clamp(config.AnticipatoryBias, 0f, 0.4f);
                    u = Mathf.Clamp01(u + bias * (1f - u));
                }
            }

            if (span > 10.0)
            {
                float aWindEff = Mathf.Max(a.WindKmh, a.GustKmh * 0.7f);
                float bWindEff = Mathf.Max(b.WindKmh, b.GustKmh * 0.7f);
                float dRain = Mathf.Abs(b.RainMm - a.RainMm) / 12f;
                float dCloud = Mathf.Abs(b.CloudPercent - a.CloudPercent);
                float dWind = Mathf.Abs(bWindEff - aWindEff) / 65f;
                float dVis = (a.VisibilityM > 0 && b.VisibilityM > 0) ? Mathf.Abs(b.VisibilityM - a.VisibilityM) / 15000f : 0f;
                float dirDelta = Mathf.Abs(b.WindDir - a.WindDir) % 360f;
                if (dirDelta > 180f) dirDelta = 360f - dirDelta;
                float dDir = Mathf.InverseLerp(25f, 120f, dirDelta);
                float changeMagnitude = dRain * 1.4f + dCloud * 1.1f + dWind * 1.35f + dVis * 0.9f + dDir * 0.85f;
                float idealSeconds = (float)span * 0.55f;
                if (changeMagnitude > 0.08f)
                    idealSeconds = idealSeconds / Mathf.Clamp(changeMagnitude * 2.2f, 0.5f, 3.5f);
                float windFactor = Mathf.InverseLerp(8f, 45f, Mathf.Max(aWindEff, bWindEff));
                idealSeconds *= Mathf.Lerp(1.0f, 0.62f, windFactor);
                if (dDir > 0.5f) idealSeconds *= 0.85f;
                float configBase = Mathf.Clamp(config.WeatherBlendSeconds, 25f, 180f);
                wx._dynamicBlendSeconds = Mathf.Clamp(idealSeconds, 20f, Mathf.Min(configBase * 1.6f, 180f));
                wx.WeatherBlendFactor = Mathf.Clamp(wx.WeatherBlendInterval / wx._dynamicBlendSeconds, 0.02f, 0.30f);
            }
            else
            {
                float blendSec = Mathf.Clamp(config.WeatherBlendSeconds, 25f, 180f);
                wx._dynamicBlendSeconds = blendSec;
                wx.WeatherBlendFactor = Mathf.Clamp(wx.WeatherBlendInterval / blendSec, 0.02f, 0.28f);
            }

            int weatherCode = u < 0.5f ? a.WeatherCode : b.WeatherCode;
            float cloudPercent = Mathf.Lerp(a.CloudPercent, b.CloudPercent, u);
            float cloudLow = Mathf.Lerp(a.CloudLow, b.CloudLow, u);
            float cloudMid = Mathf.Lerp(a.CloudMid, b.CloudMid, u);
            float cloudHigh = Mathf.Lerp(a.CloudHigh, b.CloudHigh, u);
            float temperature = Mathf.Lerp(a.Temperature, b.Temperature, u);
            float dewPoint = Mathf.Lerp(a.DewPoint, b.DewPoint, u);
            float humidity = Mathf.Lerp(a.Humidity, b.Humidity, u);
            float pressure = Mathf.Lerp(a.Pressure, b.Pressure, u);
            float rainMm = Mathf.Lerp(a.RainMm, b.RainMm, u);
            float snowMm = Mathf.Lerp(a.SnowMm, b.SnowMm, u);
            float showersMm = Mathf.Lerp(a.ShowersMm, b.ShowersMm, u);
            float precipProb = Mathf.Lerp(a.PrecipProb, b.PrecipProb, u);
            float cape = Mathf.Lerp(a.Cape, b.Cape, u);
            float shortwave = Mathf.Lerp(a.Shortwave, b.Shortwave, u);
            float soilMoisture = Mathf.Lerp(a.SoilMoisture, b.SoilMoisture, u);
            float windSustained = Mathf.Lerp(a.WindKmh, b.WindKmh, u);
            float windGust = Mathf.Lerp(a.GustKmh, b.GustKmh, u);
            float windDir = Mathf.Lerp(a.WindDir, b.WindDir, u);
            float visibilityM = Mathf.Lerp(a.VisibilityM, b.VisibilityM, u);
            bool isDay = u < 0.5f ? a.IsDay : b.IsDay;

            wx._pressureTendency = b.Pressure - a.Pressure;

            ComputeTargetsFromRaw(weatherCode, cloudPercent, cloudLow, cloudMid, cloudHigh,
                temperature, dewPoint, humidity, pressure, rainMm, snowMm, showersMm,
                precipProb, cape, shortwave, soilMoisture,
                windSustained, windGust, windDir, visibilityM, isDay, forceLog);

            ApplyMultiDayLookAhead(forceLog);
        }

        private void ComputeTargetsFromRaw(int weatherCode, float cloudPercent, float cloudLow, float cloudMid, float cloudHigh,
            float temperature, float dewPoint, float humidity, float pressure, float rainMm, float snowMm, float showersMm,
            float precipProb, float cape, float shortwave, float soilMoisture,
            float windSustained, float windGust, float windDir, float visibilityM, bool isDay, bool forceLog)
        {
            float windKmh = Mathf.Max(windSustained, windGust * 0.7f);
            float gustValue = Mathf.Clamp01(Mathf.InverseLerp(0f, 80f, windGust));
            float sustainedValue = Mathf.Clamp01(Mathf.InverseLerp(0f, 55f, windSustained));
            wx._lastGustValue = gustValue;
            float tempDewSpread = Mathf.Abs(temperature - dewPoint);

            float calculatedFog = 0f;
            if (tempDewSpread < 3.0f || humidity > 85f)
                calculatedFog = Mathf.InverseLerp(3.0f, 0.0f, tempDewSpread) * (humidity / 100f);
            if (visibilityM > 0 && visibilityM < 2000f)
                calculatedFog = Mathf.Max(calculatedFog, Mathf.InverseLerp(2000f, 200f, visibilityM));
            float windSuppress = Mathf.Clamp01(Mathf.InverseLerp(8f, 28f, windKmh));
            calculatedFog *= (1f - windSuppress * 0.85f);
            float fogValue = Mathf.Clamp01(calculatedFog);

            float fogMultiplier = 1.0f + fogValue * 0.85f;
            if (visibilityM > 0 && visibilityM < 5000f)
                fogMultiplier = Mathf.Max(fogMultiplier, Mathf.Lerp(1.9f, 1.0f, Mathf.InverseLerp(200f, 5000f, visibilityM)));
            fogMultiplier = Mathf.Clamp(fogMultiplier, 0.85f, 2.2f);

            float fogRampStart = Mathf.Lerp(15f, 120f, 1f - fogValue);
            float fogRampEnd = Mathf.Lerp(180f, 1200f, 1f - fogValue);
            if (visibilityM > 0)
            {
                fogRampStart = Mathf.Min(fogRampStart, Mathf.Clamp(visibilityM * 0.08f, 10f, 150f));
                fogRampEnd = Mathf.Min(fogRampEnd, Mathf.Clamp(visibilityM * 0.55f, 150f, 1500f));
            }
            float fogHeightFalloff = Mathf.Lerp(0.35f, 0.85f, fogValue);

            float pressureFactor = 0f;
            if (pressure > 0)
                pressureFactor = Mathf.Clamp01(Mathf.InverseLerp(1020f, 990f, pressure));
            float tendFactor = 0f;
            if (Mathf.Abs(wx._pressureTendency) > 0.3f)
                tendFactor = Mathf.Clamp(wx._pressureTendency / 8f, -1f, 1f);

            float cloudDensityModifier = Mathf.Lerp(0.95f, 1.35f, pressureFactor);
            if (tendFactor < 0f) cloudDensityModifier *= Mathf.Lerp(1f, 1.12f, -tendFactor);
            else if (tendFactor > 0f) cloudDensityModifier *= Mathf.Lerp(1f, 0.92f, tendFactor);

            float structuralCover = cloudPercent;
            bool cirrusVeil = false;
            if (cloudLow + cloudMid + cloudHigh > 0.05f)
            {
                float lowMid = cloudLow * 1.15f + cloudMid * 1.0f;
                float highOnly = cloudHigh * 0.55f;
                structuralCover = Mathf.Clamp01(lowMid + highOnly);
                structuralCover = Mathf.Max(structuralCover, cloudPercent * 0.85f);
                cirrusVeil = cloudHigh > 0.45f && (cloudLow + cloudMid) < 0.35f && cloudPercent < 0.75f;
            }
            float cloudsValue = Mathf.Clamp01(structuralCover * cloudDensityModifier);

            float cloudAttenuation = 0.55f;
            if (pressure > 0 && cloudPercent > 0.30f)
            {
                float coverFactor = Mathf.InverseLerp(0.30f, 0.95f, cloudPercent);
                cloudAttenuation = Mathf.Lerp(0.55f, 1.55f, pressureFactor * coverFactor);
            }

            float rainRaw = Mathf.Max(rainMm, showersMm * 0.85f);
            float rainValue = Mathf.Clamp01(1f - Mathf.Exp(-rainRaw * 0.35f));
            if (rainRaw > 0.01f)
                rainValue = Mathf.Max(rainValue, Mathf.InverseLerp(0f, 12f, rainRaw) * 0.5f);

            // Open-Meteo often reports weather_code 0-3 and rain=0 during real light
            // rain, while hourly precipitation_probability stays high. WMO 51+ is
            // drizzle-or-worse even when the mm field is still 0.
            if (weatherCode >= 51 && weatherCode < 70)
                rainValue = Mathf.Max(rainValue, weatherCode >= 61 ? 0.22f : 0.12f);
            else if (weatherCode >= 80 && weatherCode < 90)
                rainValue = Mathf.Max(rainValue, 0.28f);
            else if (rainRaw < 0.05f && precipProb >= 55f && cloudPercent >= 0.40f)
            {
                float popFloor = Mathf.Lerp(0.06f, 0.16f, Mathf.Clamp01((precipProb - 55f) / 35f));
                rainValue = Mathf.Max(rainValue, popFloor);
            }

            // 0.08 used to eat 0.1 mm/h drizzle (rainValue ≈ 0.03).
            const float rainOn = 0.03f;
            const float rainOff = 0.015f;
            if (wx._rainLatch < 0.5f)
            {
                if (rainValue >= rainOn) wx._rainLatch = 1f;
                else rainValue = 0f;
            }
            else
            {
                if (rainValue <= rainOff) { wx._rainLatch = 0f; rainValue = 0f; }
                else rainValue = Mathf.Max(rainValue, rainOff + 0.01f);
            }

            bool isShowers = showersMm > rainMm * 0.6f && showersMm > 0.15f || (weatherCode >= 80 && weatherCode <= 82);
            float showerFactor = isShowers ? Mathf.Clamp01(showersMm / Mathf.Max(0.2f, rainRaw)) : 0f;
            float windValue = sustainedValue;
            fogMultiplier = Mathf.Clamp(fogMultiplier + rainValue * 0.45f + windValue * 0.15f, 0.85f, 2.4f);
            float prob01 = Mathf.Clamp01(precipProb / 100f);

            UpdateCcnAndInstability(humidity, temperature, dewPoint, pressure, rainValue, windKmh, Mathf.Max(windValue, gustValue), cape);

            if (config.EnableCcnPhysics && rainValue > 0.02f)
            {
                float eff = RainEfficiencyFromCcn(wx.currentCcn);
                rainValue = Mathf.Clamp01(rainValue * eff);
            }

            string earlyProfile = GetWeatherProfile(weatherCode);
            bool isSnow = earlyProfile == "snow";
            bool isFreezing = (weatherCode >= 56 && weatherCode <= 57) || (weatherCode >= 66 && weatherCode <= 67);

            float snowIntensity = 0f;
            if (isSnow)
            {
                float snowSrc = snowMm > 0.01f ? snowMm : rainMm;
                snowIntensity = Mathf.Clamp01(Mathf.InverseLerp(0.0f, 6.0f, snowSrc));
                rainValue = Mathf.Min(rainValue, snowIntensity * 0.45f);
            }
            else if (temperature < 0f && rainValue > 0.05f)
            {
                snowIntensity = Mathf.Clamp01(rainValue * 0.6f);
                rainValue *= 0.5f;
            }

            // --- Aggressive storm detection (Open-Meteo frequently under-reports WMO 95+) ---
            // Signals: CAPE, pressure level/tendency, instability, gusts, look-ahead.
            // Dry lightning is allowed when those signals are present — we do not invent
            // rain unless ForceRainWithSyntheticStorm is on.
            bool highCape = cape >= (config.StormCapeThreshold > 0f ? config.StormCapeThreshold : 900f);
            bool veryHighCape = cape >= (config.StormVeryHighCape > 0f ? config.StormVeryHighCape : 1400f);
            bool heavyPrecip = rainValue >= 0.45f || (rainRaw >= 4.0f);
            bool moderatePrecip = rainValue >= 0.22f || (rainRaw >= 1.5f);
            // Instability is computed moisture/shear/CAPE mix — do NOT also OR cape>=700 here.
            // That made every veryHighCape hour count as unstable and lock Storm_VClouds with Rain:0.
            bool unstable = wx.currentInstability >= 0.55f;
            bool pressureSupport = pressure > 0f && pressure < 1008f;
            bool fallingPressure = wx._pressureTendency < -1.2f;
            bool lookAheadStorm = wx._lookAheadLabel == "storm" || (wx._lookAheadRain > 0.40f && wx._lookAheadHours > 0f && wx._lookAheadHours < 6f);
            bool gusty = windGust >= 45f || gustValue >= 0.55f;
            bool wetSignal = moderatePrecip || heavyPrecip || weatherCode >= 51;
            bool convectiveSupport = fallingPressure || lookAheadStorm || gusty || wetSignal;
            // Storm *sky* (Storm_VClouds + 0.90 cover) needs rain on the ground or
            // showers-or-worse WMO. Dry CAPE + a cell 5h out is thunder on RainMild.
            bool stormSkyOk = weatherCode >= 80 || rainValue >= 0.12f || rainRaw >= 0.8f;

            string profile = earlyProfile; // start from WMO mapping; may be promoted below
            profile = RefineFairSkyProfile(profile, weatherCode, cloudLow + cloudMid + cloudHigh, cloudPercent, rainValue);
            bool aggressivePromo = false; // true when we forced storm without WMO 95+

            if (config.EnableAggressiveStormDetection && profile != "storm" && profile != "snow")
            {
                if (weatherCode >= 95)
                    profile = "storm";
                else if (stormSkyOk && veryHighCape && convectiveSupport)
                {
                    profile = "storm";
                    aggressivePromo = true;
                }
                else if (stormSkyOk && highCape && unstable && convectiveSupport)
                {
                    profile = "storm";
                    aggressivePromo = true;
                }
                else if (highCape && heavyPrecip && (pressureSupport || unstable || fallingPressure))
                {
                    profile = "storm";
                    aggressivePromo = true;
                }
                else if (heavyPrecip && weatherCode >= 80 && (unstable || fallingPressure || gusty))
                {
                    profile = "storm";
                    aggressivePromo = true;
                }
            }

            // Optional: invent rain when we promote a dry CAPE storm.
            if ((config.ForceRainWithSyntheticStorm || !config.AllowDryLightning) && aggressivePromo && weatherCode < 95)
            {
                float rainFloor = lookAheadStorm
                    ? Mathf.Lerp(0.35f, 0.55f, Mathf.Clamp01(wx._lookAheadRain))
                    : (veryHighCape ? 0.45f : 0.35f);
                if (rainValue < rainFloor)
                    rainValue = rainFloor;
            }

            float thunderValue = 0f;
            if (profile == "storm" || weatherCode >= 95)
            {
                if (weatherCode >= 95)
                {
                    thunderValue = weatherCode >= 96 ? 0.85f : 0.70f;
                    thunderValue = Mathf.Clamp01(thunderValue + rainValue * 0.25f + windValue * 0.12f + wx.currentInstability * 0.20f);
                }
                else if (config.AllowDryLightning)
                {
                    // Thermal storms can flash with little/no rain. Rain still adds punch.
                    float thermal = veryHighCape ? 0.52f : highCape ? 0.32f : 0.18f;
                    float inst = wx.currentInstability * 0.38f;
                    float rainPart = rainValue * 0.32f;
                    thunderValue = Mathf.Clamp01(thermal + inst + rainPart + windValue * 0.10f);
                }
                else
                {
                    float rainDriven = Mathf.Lerp(0.12f, 0.72f, Mathf.Clamp01(rainValue / 0.55f));
                    float capeBoost = highCape ? Mathf.Lerp(0f, 0.18f, Mathf.Clamp01(rainValue / 0.40f)) : 0f;
                    thunderValue = Mathf.Clamp01(rainDriven + capeBoost + windValue * 0.10f + wx.currentInstability * 0.12f);
                }
            }
            else if (!isSnow && (highCape || veryHighCape) && (unstable || gusty || fallingPressure || rainValue > 0.20f))
            {
                float thermal = config.AllowDryLightning
                    ? (veryHighCape ? 0.42f : 0.24f)
                    : (rainValue * 0.35f);
                float rainGate = config.AllowDryLightning ? 1f : Mathf.Clamp01(rainValue / 0.30f);
                thunderValue = Mathf.Clamp01((thermal + wx.currentInstability * 0.32f + rainValue * 0.28f + windValue * 0.10f) * Mathf.Lerp(0.35f, 1f, rainGate));
            }
            else if (config.EnableInstability && wx.currentInstability > 0.60f && (highCape || rainValue > 0.12f || gusty))
            {
                float rainGate = config.AllowDryLightning ? 1f : Mathf.Clamp01(rainValue / 0.25f);
                thunderValue = Mathf.Clamp01(((wx.currentInstability - 0.40f) * 0.90f + rainValue * 0.20f + (highCape ? 0.12f : 0f)) * Mathf.Lerp(0.40f, 1f, rainGate));
            }

            float rainbowValue = 0f;
            if (isDay && !isSnow && rainValue > 0.12f && rainValue < 0.7f && temperature > 4f)
                rainbowValue = Mathf.Clamp01(rainValue * 1.45f);

            float soilWet = Mathf.Clamp01(Mathf.InverseLerp(0.15f, 0.45f, soilMoisture)) * 0.12f;
            float wetnessRain = 0.04f + soilWet;
            float wetnessSnow = 0.04f;
            if (isSnow)
            {
                wetnessSnow = Mathf.Clamp01(snowIntensity * 0.90f + 0.08f);
                wetnessRain = Mathf.Clamp01(snowIntensity * 0.20f + soilWet);
            }
            else if (rainValue > 0.04f)
            {
                wetnessRain = Mathf.Clamp01(rainValue * 0.95f + 0.08f + soilWet);
                if (isFreezing) wetnessRain = Mathf.Clamp01(wetnessRain + 0.15f);
            }

            if (rainValue < 0.08f && !isSnow)
            {
                float dryRate = 1f + Mathf.Clamp01(Mathf.InverseLerp(5f, 40f, windKmh)) * 1.8f;
                wetnessRain = Mathf.Max(0.05f, wetnessRain / dryRate);
            }
            if (isSnow && snowIntensity < 0.12f)
            {
                float dryRate = 1f + Mathf.Clamp01(Mathf.InverseLerp(5f, 40f, windKmh)) * 1.2f;
                wetnessSnow = Mathf.Max(0.05f, wetnessSnow / dryRate);
            }

            float humidityFactor = Mathf.InverseLerp(50f, 95f, humidity);
            float visibilityFactor = visibilityM > 0 ? Mathf.InverseLerp(14000f, 600f, visibilityM) : 0f;
            float dewFactor = Mathf.Clamp01(Mathf.InverseLerp(4.0f, 0.5f, tempDewSpread));

            float atmosphereMie = Mathf.Clamp01(humidityFactor * 0.55f + visibilityFactor * 0.70f + dewFactor * 0.45f);
            if (fogValue > 0.35f) atmosphereMie = Mathf.Max(atmosphereMie, fogValue * 1.15f);
            if (config.EnableCcnPhysics)
                atmosphereMie = Mathf.Clamp(atmosphereMie + wx.currentCcn * 0.28f, 0f, 1.7f);
            atmosphereMie = Mathf.Clamp(atmosphereMie * 1.10f, 0f, 1.70f);

            float atmosphereRayleigh = Mathf.Lerp(1.20f, 0.60f, atmosphereMie * 0.85f + pressureFactor * 0.15f);
            float cloudDarken = Mathf.InverseLerp(0.30f, 0.95f, cloudPercent);
            float atmosphereBrightness = Mathf.Lerp(1.20f, 0.72f, Mathf.Max(cloudDarken * 0.9f, atmosphereMie * 0.65f, pressureFactor * 0.5f));
            float atmosphereContrast = Mathf.Lerp(1.10f, 0.80f, Mathf.Max(cloudDarken * 0.85f, atmosphereMie * 0.55f));
            float atmosphereDirectionality = Mathf.Lerp(0.88f, 0.48f, atmosphereMie * 0.7f + pressureFactor * 0.25f);

            // --- Day/night atmosphere ---
            // Option A: when TimeSystem + UseRealSolarAtmosphere are on and we have
            // Open-Meteo sunrise/sunset, drive brightness from real solar times while
            // keeping Cycle.Hour = real wall-clock. Otherwise fall back to TOD hour.
            float solarFactor = -1f;
            if (config.TimeSystem != null && config.TimeSystem.Enabled && config.TimeSystem.UseRealSolarAtmosphere && !SplitDayActive())
            {
                DateTime localNowForSolar = DateTime.UtcNow.AddSeconds(_cachedUtcOffsetSeconds);
                solarFactor = GetSolarDayFactor(localNowForSolar);
            }

            if (solarFactor >= 0f)
            {
                float nightScale = Mathf.Clamp(config.TimeSystem.NightBrightnessScale, 0.55f, 0.95f);
                // Polar BrightNight can raise the floor further via MinimumNightBrightness
                if (_isPolarNight && config.TimeSystem.Polar != null)
                    nightScale = Mathf.Max(nightScale, Mathf.Clamp01(config.TimeSystem.Polar.MinimumNightBrightness));

                atmosphereBrightness *= Mathf.Lerp(nightScale, 1f, solarFactor);
                atmosphereContrast = Mathf.Lerp(atmosphereContrast * 0.92f, atmosphereContrast, solarFactor);
                atmosphereMie = Mathf.Lerp(Mathf.Min(1.7f, atmosphereMie * 1.12f), atmosphereMie, solarFactor);
                atmosphereDirectionality = Mathf.Lerp(
                    Mathf.Min(atmosphereDirectionality, 0.55f),
                    atmosphereDirectionality,
                    solarFactor);

                // Shortwave still adds a little midday punch when the sun is up
                if (solarFactor > 0.6f && shortwave > 50f)
                {
                    float sun = Mathf.Clamp01(Mathf.InverseLerp(50f, 800f, shortwave));
                    atmosphereBrightness = Mathf.Lerp(atmosphereBrightness,
                        Mathf.Min(1.25f, atmosphereBrightness + 0.12f), sun * 0.5f * solarFactor);
                }
            }
            else
            {
                // Legacy TOD-hour thresholds (used when TimeSystem is off or no solar data yet)
                float todHour = 12f;
                try { var tod = TOD_Sky.Instance; if (tod != null) todHour = tod.Cycle.Hour; } catch { }
                bool isNight = !isDay || todHour < 5.5f || todHour > 20.5f;
                bool isDawnDusk = isDay && ((todHour >= 5.5f && todHour < 7.5f) || (todHour > 18.5f && todHour <= 20.5f));
                if (isNight)
                {
                    atmosphereBrightness *= 0.82f;
                    atmosphereMie = Mathf.Min(1.7f, atmosphereMie * 1.12f);
                    atmosphereDirectionality = Mathf.Min(atmosphereDirectionality, 0.55f);
                }
                else if (isDawnDusk)
                {
                    atmosphereBrightness *= 0.92f;
                    atmosphereDirectionality = Mathf.Lerp(atmosphereDirectionality, 0.55f, 0.35f);
                }
                else if (isDay && shortwave > 50f)
                {
                    float sun = Mathf.Clamp01(Mathf.InverseLerp(50f, 800f, shortwave));
                    atmosphereBrightness = Mathf.Lerp(atmosphereBrightness, Mathf.Min(1.25f, atmosphereBrightness + 0.12f), sun * 0.5f);
                }
            }

            // profile already decided above (WMO + aggressive storm promotion)

            float dustIntensity = 0f;
            if (config.EnableDust && rainValue < 0.08f && !isSnow)
            {
                float dry = Mathf.Clamp01(Mathf.InverseLerp(config.DustHumidityMax, config.DustHumidityMax * 0.4f, humidity));
                float windy = Mathf.Clamp01(Mathf.InverseLerp(config.DustWindMinKmh, config.DustWindMinKmh * 1.8f, windKmh));
                float visCue = 0f;
                if (visibilityM > 0 && visibilityM < config.DustVisibilityMaxM)
                    visCue = Mathf.InverseLerp(config.DustVisibilityMaxM, 500f, visibilityM) * 0.35f;
                dustIntensity = Mathf.Clamp01(dry * windy + visCue * dry);
                if (config.EnableCcnPhysics)
                    dustIntensity = Mathf.Clamp01(dustIntensity + wx.currentCcn * 0.15f * dry);
            }

            float cloudDensity = cloudsValue;
            float cloudOpacity = Mathf.Clamp01(cloudsValue * 0.85f + 0.08f + humidityFactor * 0.12f);
            float cloudBrightness = 1.0f;
            float cloudSharpness = 0.45f;
            float cloudScattering = 1.0f;
            float cloudColoring = 1.0f;
            float cloudSize = 1.0f;
            float cloudSaturation = 1.0f;

            if (config != null && config.UseDeckMixer)
            {
                MixDeckSliders(cloudLow, cloudMid, cloudHigh, cloudsValue, rainValue, fogValue,
                    humidityFactor, pressureFactor, cape, dustIntensity, profile,
                    ref cloudDensity, ref cloudOpacity, ref cloudBrightness, ref cloudSharpness,
                    ref cloudScattering, ref cloudColoring, ref cloudSize, ref cloudSaturation,
                    ref cloudAttenuation);
            }
            else
            {
                ApplyLegacyProfileSliders(profile, cloudsValue, rainValue, fogValue, humidityFactor,
                    pressureFactor, dustIntensity,
                    ref cloudDensity, ref cloudOpacity, ref cloudBrightness, ref cloudSharpness,
                    ref cloudScattering, ref cloudColoring, ref cloudSize, ref cloudSaturation,
                    ref cloudAttenuation);
            }

            float finalAttenuation = Mathf.Clamp(cloudAttenuation * (1.0f + (cloudDensity - 0.5f) * 0.45f), 0.50f, 1.95f);

            wx.targetClouds = cloudDensity;
            wx.targetCloudOpacity = cloudOpacity;
            wx.targetMie = atmosphereMie;
            wx.targetBrightness = atmosphereBrightness;
            wx.targetRain = rainValue;
            wx.targetWind = windValue;
            wx.targetFog = fogValue;
            wx.targetFogMultiplier = fogMultiplier;
            wx.targetFogRampStart = fogRampStart;
            wx.targetFogRampEnd = fogRampEnd;
            wx.targetFogHeightFalloff = fogHeightFalloff;
            wx.targetThunder = thunderValue;
            wx.targetRainbow = rainbowValue;
            wx.targetWetness = wetnessRain;
            wx.targetWetnessSnow = wetnessSnow;
            wx.targetDust = dustIntensity;
            wx.targetRayleigh = atmosphereRayleigh;
            wx.targetContrast = atmosphereContrast;
            wx.targetDirectionality = atmosphereDirectionality;
            wx.targetAttenuation = finalAttenuation;
            wx.targetCloudBrightness = cloudBrightness;
            wx.targetCloudSharpness = cloudSharpness;
            wx.targetCloudScattering = cloudScattering;
            wx.targetCloudColoring = cloudColoring;
            wx.targetCloudSize = cloudSize;
            wx.targetCloudSaturation = cloudSaturation;
            wx.lastWeatherCode = weatherCode;
            wx.lastCloudLow = cloudLow;
            wx.lastCloudMid = cloudMid;
            wx.lastCloudHigh = cloudHigh;
            wx.pendingWeatherProfile = profile;
            wx.lastOmCape = cape;
            wx.lastOmShortwave = shortwave;
            wx.lastOmHumidity = humidity;
            wx.lastOmVisibility = visibilityM;
            wx.lastOmIsDay = isDay;
            ComputeOptionalLightingFromMeteo(rainValue, cloudsValue, fogValue, thunderValue,
                wetnessRain, cape, shortwave, humidity, visibilityM, isDay);
            ClampWeatherTargets();

            if (!wx._weatherInitialized)
            {
                wx.currentClouds = wx.targetClouds; wx.currentCloudOpacity = wx.targetCloudOpacity;
                wx.currentMie = wx.targetMie; wx.currentBrightness = wx.targetBrightness;
                wx.currentRain = wx.targetRain; wx.currentWind = wx.targetWind; wx.currentFog = wx.targetFog;
                wx.currentFogMultiplier = wx.targetFogMultiplier;
                wx.currentFogRampStart = wx.targetFogRampStart; wx.currentFogRampEnd = wx.targetFogRampEnd;
                wx.currentFogHeightFalloff = wx.targetFogHeightFalloff;
                wx.currentThunder = wx.targetThunder; wx.currentRainbow = wx.targetRainbow; wx.currentWetness = wx.targetWetness;
                wx.currentWetnessSnow = wx.targetWetnessSnow; wx.currentDust = wx.targetDust;
                wx.currentRayleigh = wx.targetRayleigh; wx.currentContrast = wx.targetContrast;
                wx.currentDirectionality = wx.targetDirectionality; wx.currentAttenuation = wx.targetAttenuation;
                wx.currentCloudBrightness = wx.targetCloudBrightness; wx.currentCloudSharpness = wx.targetCloudSharpness;
                wx.currentCloudScattering = wx.targetCloudScattering; wx.currentCloudColoring = wx.targetCloudColoring;
                wx.currentCloudSize = wx.targetCloudSize; wx.currentCloudSaturation = wx.targetCloudSaturation;
                wx._weatherInitialized = true;
                SetWeatherProfile(profile, rainValue);
            }
            else if (forceLog)
            {
                TryCommitWeatherProfile(profile, rainValue);
            }

            string forecastName = profile switch
            {
                "storm" => "Storm", "rain" => "Rain", "snow" => "Snow", "dust" => "Dust", "fog" => "Fog",
                "overcast" => "Overcast", "partly_cloudy" => "Partly Cloudy", "few" => "Few Clouds", _ => "Clear"
            };

            if (forceLog || forecastName != wx._lastInterpLogProfile)
            {
                wx._lastInterpLogProfile = forecastName;
                string wantAsset = DesiredCloudConfig(profile, rainValue);
                string haveAsset = string.IsNullOrEmpty(wx.currentCloudConfig) ? "?" : wx.currentCloudConfig;
                string deck = (config != null && config.UseDeckMixer) ? "deck" : "wmo";
                Puts($"[Weather] Targets -> {forecastName} | {deck} loaded:{haveAsset} want:{wantAsset} | code={weatherCode} cape={cape:F0} Rain:{wx.targetRain:F2} Clouds:{wx.targetClouds:F2} L/M/H:{cloudLow:F2}/{cloudMid:F2}/{cloudHigh:F2} | CCN:{wx.currentCcn:F2} instab:{wx.currentInstability:F2} | blend~{wx._dynamicBlendSeconds:F0}s");
            }
        }

        private void TryCommitWeatherProfile(string profile, float rainIntensity)
        {
            wx.currentWeatherProfile = profile;
            if (!config.AllowCloudConfigSwap || _cloudSwapPhase != CloudSwapPhase.Idle) return;
            string desiredConfig = DesiredCloudConfig(profile, rainIntensity);
            if (string.IsNullOrEmpty(desiredConfig) || desiredConfig == wx.currentCloudConfig)
            {
                wx.hysteresisCandidate = null;
                wx.hysteresisVotes = 0;
                return;
            }
            if (desiredConfig == wx.hysteresisCandidate) wx.hysteresisVotes++;
            else { wx.hysteresisCandidate = desiredConfig; wx.hysteresisVotes = 1; }
            // Soft-sky swaps (Clear / RainMild / Overcast): need 2+ agreeing polls so we don't thrash
            // Exception: no asset loaded yet this session → apply immediately
            bool leavingHeavy = wx.currentCloudConfig == "Storm_VClouds" || wx.currentCloudConfig == "RainHeavy_VClouds";
            bool softSkySwap = !leavingHeavy
                && IsSoftSkyCloudConfig(desiredConfig)
                && IsSoftSkyCloudConfig(wx.currentCloudConfig);
            int required = softSkySwap ? Mathf.Max(2, config.HysteresisRequiredVotes) : Mathf.Max(1, config.HysteresisRequiredVotes);
            if (string.IsNullOrEmpty(wx.currentCloudConfig) || leavingHeavy)
                required = 1;
            if (wx.hysteresisVotes >= required)
            {
                BeginCloudAssetDissolve(desiredConfig);
                wx.hysteresisCandidate = null;
                wx.hysteresisVotes = 0;
            }
        }

        /// <summary>
        /// Soft sky assets that look similar enough to warrant extra hysteresis (avoids thrash)
        /// and a longer dissolve. RainMild is included so Clear↔PartlyCloudy and
        /// PartlyCloudy↔Overcast do not snap on a single poll.
        /// </summary>
        private static bool IsSoftSkyCloudConfig(string cfg) =>
            string.IsNullOrEmpty(cfg)
            || cfg == "Clear_VClouds"
            || cfg == "Overcast_VClouds"
            || cfg == "RainMild_VClouds"
            || cfg == "Fog_VClouds";

        /// <summary>
        /// Low-altitude deck: Open-Meteo low cloud (&lt;~2 km: stratus / fog / low sc)
        /// dominates mid and high. That sky is a ceiling, not a mid-level pile —
        /// Fog_VClouds reads correctly; Overcast/RainMild sit too high.
        /// </summary>
        private bool IsLowAltitudeCloudDeck()
        {
            float minLow = config != null && config.LowDeckCloudMin > 0f ? config.LowDeckCloudMin : 0.50f;
            if (wx.lastCloudLow < minLow) return false;
            // Low must be the main layer, not just present under a thick mid/high stack.
            if (wx.lastCloudLow + 0.08f < wx.lastCloudMid) return false;
            if (wx.lastCloudLow + 0.05f < wx.lastCloudHigh) return false;
            return wx.lastCloudMid < 0.62f;
        }

        private bool ThunderBlocksClearSky()
        {
            float floor = 0.20f;
            if (config != null && config.LightningThunderThreshold > 0f)
                floor = Mathf.Min(floor, config.LightningThunderThreshold);
            return Mathf.Max(wx.currentThunder, wx.targetThunder) >= floor;
        }

        private string DesiredCloudConfig(string profile, float rainIntensity)
        {
            string pick = (config != null && config.UseDeckMixer)
                ? PickDeckCloudConfig(profile, rainIntensity)
                : PickLegacyCloudConfig(profile, rainIntensity);

            if (pick == "Clear_VClouds" && ThunderBlocksClearSky())
                pick = "RainMild_VClouds";
            return pick;
        }

        /// <summary>
        /// Stock-asset pick from Open-Meteo layer mix. Storm/heavy rain still win
        /// when justified; otherwise low→Fog, mid thick→Overcast, broken→Clear,
        /// everything else→RainMild (the mid-deck workhorse).
        /// </summary>
        private string PickDeckCloudConfig(string profile, float rainIntensity)
        {
            float low = wx.lastCloudLow;
            float mid = wx.lastCloudMid;
            float high = wx.lastCloudHigh;
            float stack = low + mid + high;
            bool lowDeck = (config == null || config.UseFogCloudsForLowDeck) && IsLowAltitudeCloudDeck();
            bool highOnly = high > 0.40f && (low + mid) < 0.22f;
            bool cirrus = highOnly || (high > 0.45f && (low + mid) < 0.35f);
            bool midThick = mid >= 0.72f && mid >= low - 0.02f;
            bool broken = stack < 0.28f && rainIntensity < 0.05f && wx.lastWeatherCode <= 1;

            if (profile == "storm" && (rainIntensity >= 0.12f || wx.lastWeatherCode >= 80))
                return "Storm_VClouds";
            if (profile == "rain" && rainIntensity > 0.55f)
                return "RainHeavy_VClouds";
            if (profile == "fog" || lowDeck)
                return "Fog_VClouds";
            if (profile == "dust")
                return "Overcast_VClouds";
            if (profile == "few" || broken || (profile == "clear" && stack < 0.22f))
                return "Clear_VClouds";
            // WMO 3 + 99% high is still a cirrus deck — do not keep Overcast_VClouds.
            if (cirrus && rainIntensity < 0.08f)
                return highOnly && high > 0.55f ? "RainMild_VClouds" : "Clear_VClouds";
            if (midThick && rainIntensity < 0.12f)
                return "Overcast_VClouds";
            if (profile == "snow")
                return rainIntensity > 0.4f ? "RainMild_VClouds" : "Overcast_VClouds";
            return "RainMild_VClouds";
        }

        private string PickLegacyCloudConfig(string profile, float rainIntensity)
        {
            bool lowDeck = config == null || config.UseFogCloudsForLowDeck;
            if (lowDeck) lowDeck = IsLowAltitudeCloudDeck();

            if (config != null && config.PreferOvercastOnly)
            {
                if (profile == "storm")
                    return (rainIntensity >= 0.12f || wx.lastWeatherCode >= 80) ? "Storm_VClouds" : "RainMild_VClouds";
                if (profile == "rain") return rainIntensity > 0.55f ? "RainHeavy_VClouds" : "RainMild_VClouds";
                if (profile == "fog" || lowDeck) return "Fog_VClouds";
                return "Overcast_VClouds";
            }

            switch (profile)
            {
                case "storm":
                    return (rainIntensity >= 0.12f || wx.lastWeatherCode >= 80) ? "Storm_VClouds" : "RainMild_VClouds";
                case "rain":
                    if (rainIntensity > 0.55f) return "RainHeavy_VClouds";
                    return lowDeck ? "Fog_VClouds" : "RainMild_VClouds";
                case "partly_cloudy": return lowDeck ? "Fog_VClouds" : "RainMild_VClouds";
                case "few": return "Clear_VClouds";
                case "snow":
                    if (lowDeck) return "Fog_VClouds";
                    return rainIntensity > 0.4f ? "RainMild_VClouds" : "Overcast_VClouds";
                case "fog": return "Fog_VClouds";
                case "overcast": return lowDeck ? "Fog_VClouds" : "Overcast_VClouds";
                case "dust": return "Overcast_VClouds";
                default: return lowDeck ? "Fog_VClouds" : "Clear_VClouds";
            }
        }

        private void MixDeckSliders(float low, float mid, float high, float cloudsValue, float rainValue, float fogValue,
            float humidityFactor, float pressureFactor, float cape, float dustIntensity, string profile,
            ref float cloudDensity, ref float cloudOpacity, ref float cloudBrightness, ref float cloudSharpness,
            ref float cloudScattering, ref float cloudColoring, ref float cloudSize, ref float cloudSaturation,
            ref float cloudAttenuation)
        {
            float cape01 = Mathf.Clamp01(cape / 2500f);
            float low01 = Mathf.Clamp01(low);
            float mid01 = Mathf.Clamp01(mid);
            float high01 = Mathf.Clamp01(high);
            float rain01 = Mathf.Clamp01(rainValue);
            float fog01 = Mathf.Clamp01(fogValue);
            float thick = Mathf.Clamp01(low01 * 1.05f + mid01 * 0.95f + high01 * 0.40f);
            thick = Mathf.Max(thick, cloudsValue * 0.85f);

            cloudDensity = Mathf.Clamp01(Mathf.Lerp(0.04f, 0.98f, thick));
            cloudOpacity = Mathf.Clamp01(Mathf.Lerp(0.10f, 0.98f, thick) + humidityFactor * 0.08f + fog01 * 0.12f);
            cloudSize = Mathf.Clamp(Mathf.Lerp(0.70f, 1.50f, mid01 * 0.55f + low01 * 0.35f + rain01 * 0.25f), 0.40f, 2.50f);
            cloudSharpness = Mathf.Clamp01(Mathf.Lerp(0.62f, 0.06f, thick * 0.75f + rain01 * 0.25f));
            cloudScattering = Mathf.Clamp(Mathf.Lerp(0.70f, 2.10f, low01 * 0.55f + fog01 * 0.35f + rain01 * 0.35f), 0.30f, 2.50f);
            cloudColoring = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.28f, thick * 0.65f + rain01 * 0.40f));
            cloudSaturation = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.55f, thick * 0.55f + rain01 * 0.30f));
            cloudBrightness = Mathf.Clamp(Mathf.Lerp(1.18f, 0.60f, thick * 0.50f + rain01 * 0.35f + cape01 * 0.15f + pressureFactor * 0.15f), 0.30f, 1.50f);
            cloudAttenuation = Mathf.Clamp(cloudAttenuation + low01 * 0.35f + rain01 * 0.40f + cape01 * 0.15f, 0.20f, 2.00f);

            if (profile == "storm")
            {
                cloudDensity = Mathf.Max(cloudDensity, 0.90f);
                cloudOpacity = 1f;
                cloudBrightness = Mathf.Min(cloudBrightness, 0.58f);
                cloudSharpness = Mathf.Min(cloudSharpness, 0.08f);
                cloudScattering = Mathf.Max(cloudScattering, 1.70f);
                cloudSize = Mathf.Max(cloudSize, 1.45f);
                cloudAttenuation = Mathf.Max(cloudAttenuation, 1.45f);
            }
            else if (profile == "snow")
            {
                cloudBrightness = Mathf.Max(cloudBrightness, 1.00f);
                cloudSaturation = Mathf.Max(cloudSaturation, 0.82f);
            }
            else if (profile == "dust")
            {
                cloudDensity = Mathf.Clamp01(Mathf.Max(cloudDensity * 0.75f, 0.25f) + dustIntensity * 0.35f);
                cloudOpacity = Mathf.Clamp01(0.45f + dustIntensity * 0.40f);
                cloudColoring = Mathf.Min(cloudColoring, 0.55f);
            }
            else if (profile == "clear")
            {
                // Empty dome — same mesh as "few", sliders off so the wisps disappear.
                cloudDensity = 0f;
                cloudOpacity = 0f;
                cloudAttenuation = Mathf.Min(cloudAttenuation, 0.40f);
                cloudSize = 0.70f;
                cloudSharpness = 0.65f;
                cloudScattering = 0.70f;
                cloudBrightness = Mathf.Max(cloudBrightness, 1.12f);
            }
            else if (profile == "few")
            {
                // Clear_VClouds at visible-wisp strength.
                cloudDensity = Mathf.Clamp01(Mathf.Lerp(0.06f, 0.20f, thick));
                cloudOpacity = Mathf.Clamp01(Mathf.Lerp(0.14f, 0.30f, thick) + humidityFactor * 0.08f);
                cloudAttenuation = Mathf.Min(cloudAttenuation, 0.50f);
                cloudSize = Mathf.Clamp(Mathf.Lerp(0.70f, 0.95f, thick), 0.40f, 1.10f);
                cloudSharpness = 0.60f;
                cloudScattering = 0.75f;
                cloudBrightness = Mathf.Max(cloudBrightness, 1.14f);
            }

            // High-only deck: thin the sliders so RainMild does not read as a mid overcast slab.
            if (high01 > 0.40f && low01 + mid01 < 0.22f && rain01 < 0.08f)
            {
                cloudDensity = Mathf.Min(cloudDensity, Mathf.Lerp(0.16f, 0.46f, high01));
                cloudOpacity = Mathf.Min(cloudOpacity, Mathf.Lerp(0.20f, 0.52f, high01));
                cloudSize = Mathf.Min(cloudSize, 1.05f);
                cloudSharpness = Mathf.Max(cloudSharpness, 0.38f);
                cloudAttenuation = Mathf.Min(cloudAttenuation, 0.70f);
            }
        }

        private void ApplyLegacyProfileSliders(string profile, float cloudsValue, float rainValue, float fogValue,
            float humidityFactor, float pressureFactor, float dustIntensity,
            ref float cloudDensity, ref float cloudOpacity, ref float cloudBrightness, ref float cloudSharpness,
            ref float cloudScattering, ref float cloudColoring, ref float cloudSize, ref float cloudSaturation,
            ref float cloudAttenuation)
        {
            switch (profile)
            {
                case "clear":
                    cloudDensity = 0f;
                    cloudOpacity = 0f;
                    cloudBrightness = 1.20f; cloudSharpness = 0.65f; cloudScattering = 0.70f;
                    cloudColoring = 1.00f; cloudSize = 0.70f; cloudSaturation = 1.00f;
                    cloudAttenuation = Mathf.Min(cloudAttenuation, 0.40f);
                    break;
                case "few":
                    cloudDensity = Mathf.Clamp01(Mathf.Lerp(0.06f, 0.20f, cloudsValue));
                    cloudOpacity = Mathf.Clamp01(0.14f + humidityFactor * 0.10f);
                    cloudBrightness = 1.16f; cloudSharpness = 0.60f; cloudScattering = 0.75f;
                    cloudColoring = 1.00f; cloudSize = 0.82f; cloudSaturation = 1.00f;
                    cloudAttenuation = Mathf.Min(cloudAttenuation, 0.50f);
                    break;
                case "partly_cloudy":
                    cloudDensity = Mathf.Clamp01(Mathf.Lerp(0.32f, 0.55f, cloudsValue));
                    cloudOpacity = Mathf.Clamp01(0.35f + humidityFactor * 0.16f);
                    cloudBrightness = 1.10f; cloudSharpness = 0.48f; cloudScattering = 1.12f;
                    cloudColoring = 0.92f; cloudSize = 1.18f; cloudSaturation = 1.00f;
                    break;
                case "overcast":
                    cloudDensity = Mathf.Clamp01(Mathf.Lerp(0.82f, 0.98f, Mathf.Max(cloudsValue, 0.55f)));
                    cloudOpacity = Mathf.Clamp01(0.92f + humidityFactor * 0.06f);
                    cloudBrightness = Mathf.Lerp(0.85f, 0.68f, pressureFactor);
                    cloudSharpness = 0.10f; cloudScattering = 1.45f; cloudColoring = 0.42f;
                    cloudSize = 1.40f; cloudSaturation = 0.68f;
                    break;
                case "rain":
                    cloudDensity = Mathf.Clamp01(Mathf.Max(cloudsValue * 1.22f, 0.78f));
                    cloudOpacity = Mathf.Clamp01(0.92f + rainValue * 0.08f);
                    cloudBrightness = Mathf.Lerp(0.82f, 0.68f, rainValue);
                    cloudSharpness = 0.12f; cloudScattering = Mathf.Lerp(1.40f, 1.70f, rainValue);
                    cloudColoring = 0.38f; cloudSize = 1.35f; cloudSaturation = 0.68f;
                    break;
                case "snow":
                    cloudDensity = Mathf.Clamp01(Mathf.Max(cloudsValue * 1.10f, 0.68f));
                    cloudOpacity = 0.88f; cloudBrightness = 1.08f; cloudSharpness = 0.20f;
                    cloudScattering = 1.50f; cloudColoring = 0.70f; cloudSize = 1.28f; cloudSaturation = 0.88f;
                    break;
                case "storm":
                    cloudDensity = Mathf.Clamp01(Mathf.Max(cloudsValue * 1.32f, 0.90f));
                    cloudOpacity = 1.0f; cloudBrightness = 0.58f; cloudSharpness = 0.06f;
                    cloudScattering = 1.90f; cloudColoring = 0.25f; cloudSize = 1.55f; cloudSaturation = 0.55f;
                    cloudAttenuation = Mathf.Max(cloudAttenuation, 1.55f);
                    break;
                case "dust":
                    cloudDensity = Mathf.Clamp01(Mathf.Max(cloudsValue * 0.7f, 0.25f) + dustIntensity * 0.35f);
                    cloudOpacity = Mathf.Clamp01(0.45f + dustIntensity * 0.40f);
                    cloudBrightness = Mathf.Lerp(0.95f, 0.75f, dustIntensity);
                    cloudSharpness = 0.08f; cloudScattering = Mathf.Lerp(1.4f, 2.0f, dustIntensity);
                    cloudColoring = 0.55f; cloudSize = 1.20f; cloudSaturation = 0.65f;
                    break;
                case "fog":
                    cloudDensity = Mathf.Clamp01(cloudsValue * 0.75f + 0.20f);
                    cloudOpacity = Mathf.Clamp01(0.50f + fogValue * 0.40f);
                    cloudBrightness = 0.90f; cloudSharpness = 0.04f; cloudScattering = 2.30f;
                    cloudColoring = 0.32f; cloudSize = 1.45f; cloudSaturation = 0.75f;
                    break;
            }
        }

        private static int SoftSkyChainIndex(string cfg)
        {
            if (string.IsNullOrEmpty(cfg)) return -1;
            for (int i = 0; i < SoftSkyChain.Length; i++)
                if (SoftSkyChain[i] == cfg) return i;
            return -1;
        }

        /// <summary>
        /// Clear ↔ Overcast walks through RainMild. Neighbor steps and anything
        /// off this chain (Fog / RainHeavy / Storm) stay a single hop.
        /// </summary>
        private static void BuildSoftSkySwapPath(string from, string to, List<string> path)
        {
            path.Clear();
            if (string.IsNullOrEmpty(to) || to == from) return;
            int a = SoftSkyChainIndex(from);
            int b = SoftSkyChainIndex(to);
            if (a < 0 || b < 0 || Mathf.Abs(a - b) <= 1)
            {
                path.Add(to);
                return;
            }
            int step = b > a ? 1 : -1;
            for (int i = a + step; i != b + step; i += step)
                path.Add(SoftSkyChain[i]);
        }

        private void BeginCloudAssetDissolve(string desiredConfig)
        {
            if (string.IsNullOrEmpty(desiredConfig) || desiredConfig == wx.currentCloudConfig || _cloudSwapPhase != CloudSwapPhase.Idle) return;
            BuildSoftSkySwapPath(wx.currentCloudConfig, desiredConfig, _cloudSwapQueue);
            if (_cloudSwapQueue.Count == 0) return;
            _cloudSwapHopsTotal = _cloudSwapQueue.Count;
            string first = _cloudSwapQueue[0];
            _cloudSwapQueue.RemoveAt(0);
            string pathLog = first;
            for (int i = 0; i < _cloudSwapQueue.Count; i++)
                pathLog += " -> " + _cloudSwapQueue[i];
            Puts($"[Weather] Cloud dissolve path {wx.currentCloudConfig} -> {pathLog}");
            StartCloudSwapHop(first);
        }

        private void StartCloudSwapHop(string hopConfig)
        {
            if (string.IsNullOrEmpty(hopConfig)) return;
            _cloudSwapDesired = hopConfig;
            _cloudSwapSavedOpacity = Mathf.Max(CloudSwapFloor, wx.currentCloudOpacity);
            _cloudSwapSavedCoverage = Mathf.Max(CloudSwapFloorCoverage, wx.currentClouds);
            _cloudSwapSavedBri = wx.currentBrightness;
            _cloudSwapSavedCon = wx.currentContrast;
            _cloudSwapSavedCloudBri = wx.currentCloudBrightness;
            _cloudSwapSavedRay = wx.currentRayleigh;
            _cloudSwapSavedAtten = wx.currentAttenuation;
            _cloudSwapSavedMie = wx.currentMie;
            _cloudSwapSavedScatter = wx.currentCloudScattering;
            _cloudSwapSavedColor = wx.currentCloudColoring;
            _cloudSwapSavedSharp = wx.currentCloudSharpness;
            _cloudSwapSavedSize = wx.currentCloudSize;
            _cloudSwapSavedSat = wx.currentCloudSaturation;
            _cloudSwapSavedDir = wx.currentDirectionality;
            _cloudSwapSavedVSun = wx.currentVCloudSun;
            _cloudSwapSavedVMoon = wx.currentVCloudMoon;

            int hopsThisChain = Mathf.Max(1, _cloudSwapHopsTotal);
            float chain = Mathf.Clamp(config.CloudSwapDissolveSeconds, 2.5f, 12f);
            float hop = chain / hopsThisChain;
            bool multi = hopsThisChain > 1;
            _cloudSwapOutDuration = Mathf.Max(multi ? 0.70f : 1.2f, hop * 0.38f);
            _cloudSwapHoldDuration = Mathf.Clamp(hop * 0.12f, multi ? 0.18f : 0.25f, multi ? 0.40f : 0.60f);
            _cloudSwapInDuration = Mathf.Max(multi ? 0.90f : 1.6f, hop * 0.50f);

            _cloudSwapPhase = CloudSwapPhase.FadeOut;
            _cloudSwapPhaseStart = Time.realtimeSinceStartup;
            StartCloudSwapTicker();
            TickCloudAssetDissolve();
            ApplyCloudSwapConvars();
            int remaining = _cloudSwapQueue.Count;
            Puts($"[Weather] Cloud dissolve hop -> {hopConfig} ({_cloudSwapHopsTotal - remaining}/{_cloudSwapHopsTotal}) out {_cloudSwapOutDuration:F1}s / hold {_cloudSwapHoldDuration:F1}s / in {_cloudSwapInDuration:F1}s");
        }

        private void StartCloudSwapTicker()
        {
            if (_cloudSwapTimer != null) return;
            _cloudSwapTimer = timer.Every(CloudSwapTickInterval, () =>
            {
                if (_cloudSwapPhase == CloudSwapPhase.Idle)
                {
                    StopCloudSwapTicker();
                    return;
                }
                TickCloudAssetDissolve();
                ApplyCloudSwapConvars();
            });
        }

        private void StopCloudSwapTicker()
        {
            StopTimer(ref _cloudSwapTimer);
        }

        private void InvalidateAppliedSkyConvars()
        {
            // load_cloud_config silently resets engine lighting to the asset defaults.
            wx.InvalidateAppliedSky();
        }

        private void ApplyCloudSwapConvars()
        {
            var climate = SingletonComponent<global::Climate>.Instance;
            if (climate != null)
                climate.Overrides.Clouds = wx.currentClouds;
            // Quiet: 10 Hz writes would flood the dedicated log. Values still reach clients.
            SetWeatherConvar("weather.cloud_coverage", wx.currentClouds, ref wx.appliedClouds, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_opacity", wx.currentCloudOpacity, ref wx.appliedCloudOpacity, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_attenuation", wx.currentAttenuation, ref wx.appliedAttenuation, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_brightness", wx.currentCloudBrightness, ref wx.appliedCloudBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_scattering", wx.currentCloudScattering, ref wx.appliedCloudScattering, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_coloring", wx.currentCloudColoring, ref wx.appliedCloudColoring, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_sharpness", wx.currentCloudSharpness, ref wx.appliedCloudSharpness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_size", wx.currentCloudSize, ref wx.appliedCloudSize, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_saturation", wx.currentCloudSaturation, ref wx.appliedCloudSaturation, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_brightness", wx.currentBrightness, ref wx.appliedBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_contrast", wx.currentContrast, ref wx.appliedContrast, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_rayleigh", wx.currentRayleigh, ref wx.appliedRayleigh, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_mie", wx.currentMie, ref wx.appliedMie, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_directionality", wx.currentDirectionality, ref wx.appliedDirectionality, WeatherApplyEpsilon, "F2", true);
            ApplyOptionalLightingConvars(true);
        }

        /// <summary>
        /// Hold pre-swap lighting during fade-out/hold so thinning clouds do not
        /// read as a sunny flash. Fade-in eases toward the live weather targets.
        /// </summary>
        private void ApplyDissolveLighting(float fadeIn01)
        {
            bool flashLive = wx._lightningFlashUntil > Time.realtimeSinceStartup;
            if (fadeIn01 <= 0f)
            {
                wx.currentAttenuation = _cloudSwapSavedAtten;
                wx.currentMie = _cloudSwapSavedMie;
                wx.currentCloudScattering = _cloudSwapSavedScatter;
                wx.currentCloudColoring = _cloudSwapSavedColor;
                wx.currentCloudSharpness = _cloudSwapSavedSharp;
                wx.currentCloudSize = _cloudSwapSavedSize;
                wx.currentCloudSaturation = _cloudSwapSavedSat;
                wx.currentDirectionality = _cloudSwapSavedDir;
                if (!flashLive)
                {
                    wx.currentBrightness = _cloudSwapSavedBri;
                    wx.currentContrast = _cloudSwapSavedCon;
                    wx.currentCloudBrightness = _cloudSwapSavedCloudBri;
                    wx.currentRayleigh = _cloudSwapSavedRay;
                }
                wx.currentVCloudSun = _cloudSwapSavedVSun;
                wx.currentVCloudMoon = _cloudSwapSavedVMoon;
                return;
            }

            float s = SmootherStep(fadeIn01);
            wx.currentAttenuation = Mathf.Lerp(_cloudSwapSavedAtten, wx.targetAttenuation, s);
            wx.currentMie = Mathf.Lerp(_cloudSwapSavedMie, wx.targetMie, s);
            wx.currentCloudScattering = Mathf.Lerp(_cloudSwapSavedScatter, wx.targetCloudScattering, s);
            wx.currentCloudColoring = Mathf.Lerp(_cloudSwapSavedColor, wx.targetCloudColoring, s);
            wx.currentCloudSharpness = Mathf.Lerp(_cloudSwapSavedSharp, wx.targetCloudSharpness, s);
            wx.currentCloudSize = Mathf.Lerp(_cloudSwapSavedSize, wx.targetCloudSize, s);
            wx.currentCloudSaturation = Mathf.Lerp(_cloudSwapSavedSat, wx.targetCloudSaturation, s);
            wx.currentDirectionality = Mathf.Lerp(_cloudSwapSavedDir, wx.targetDirectionality, s);
            if (!flashLive)
            {
                wx.currentBrightness = Mathf.Lerp(_cloudSwapSavedBri, wx.targetBrightness, s);
                wx.currentContrast = Mathf.Lerp(_cloudSwapSavedCon, wx.targetContrast, s);
                wx.currentCloudBrightness = Mathf.Lerp(_cloudSwapSavedCloudBri, wx.targetCloudBrightness, s);
                wx.currentRayleigh = Mathf.Lerp(_cloudSwapSavedRay, wx.targetRayleigh, s);
            }
            wx.currentVCloudSun = LerpOptional(_cloudSwapSavedVSun, wx.targetVCloudSun, s);
            wx.currentVCloudMoon = LerpOptional(_cloudSwapSavedVMoon, wx.targetVCloudMoon, s);
        }

        /// <summary>
        /// Ken Perlin smootherstep (zero 1st and 2nd derivative at 0 and 1) so the
        /// two endpoints ease instead of the old single-sample jump.
        /// </summary>
        private static float SmootherStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        /// <summary>
        /// Time-based smootherstep opacity/coverage dissolve. Owns cloud opacity/coverage
        /// while active so the normal wx.WeatherBlendFactor lerp cannot fight the fade.
        /// Mesh swap only fires when both channels are at the floor; a short hold then
        /// eases back to the live weather targets.
        /// </summary>
        private void TickCloudAssetDissolve()
        {
            if (_cloudSwapPhase == CloudSwapPhase.Idle) return;
            float elapsed = Time.realtimeSinceStartup - _cloudSwapPhaseStart;

            if (_cloudSwapPhase == CloudSwapPhase.FadeOut)
            {
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.4f, _cloudSwapOutDuration));
                float s = SmootherStep(t);
                wx.currentCloudOpacity = Mathf.Lerp(_cloudSwapSavedOpacity, CloudSwapFloor, s);
                wx.currentClouds = Mathf.Lerp(_cloudSwapSavedCoverage, CloudSwapFloorCoverage, s);
                ApplyDissolveLighting(0f);

                // Swap mesh at the veil floor (not a punched-out clear sky)
                if (t >= 0.98f)
                {
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, $"weather.load_cloud_config {_cloudSwapDesired}");
                    wx.currentCloudConfig = _cloudSwapDesired;
                    wx.currentCloudOpacity = CloudSwapFloor;
                    wx.currentClouds = CloudSwapFloorCoverage;
                    _cloudSwapFadeFromOpacity = CloudSwapFloor;
                    _cloudSwapFadeFromCoverage = CloudSwapFloorCoverage;
                    ApplyDissolveLighting(0f);
                    InvalidateAppliedSkyConvars();
                    _cloudSwapPhase = CloudSwapPhase.Hold;
                    _cloudSwapPhaseStart = Time.realtimeSinceStartup;
                    ApplyCloudSwapConvars();
                    Puts($"[Weather] Cloud asset loaded -> {_cloudSwapDesired} (holding lighting, then fade in)");
                }
            }
            else if (_cloudSwapPhase == CloudSwapPhase.Hold)
            {
                wx.currentCloudOpacity = CloudSwapFloor;
                wx.currentClouds = CloudSwapFloorCoverage;
                ApplyDissolveLighting(0f);
                if (elapsed >= _cloudSwapHoldDuration)
                {
                    _cloudSwapFadeFromOpacity = wx.currentCloudOpacity;
                    _cloudSwapFadeFromCoverage = wx.currentClouds;
                    _cloudSwapPhase = CloudSwapPhase.FadeIn;
                    _cloudSwapPhaseStart = Time.realtimeSinceStartup;
                    Puts($"[Weather] Cloud asset fading in -> {_cloudSwapDesired}");
                }
            }
            else if (_cloudSwapPhase == CloudSwapPhase.FadeIn)
            {
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.5f, _cloudSwapInDuration));
                float s = SmootherStep(t);
                // Mid-hops (Clear→RainMild on the way to Overcast) only walk partway
                // toward the live target so RainMild is actually visible as a step.
                bool moreHops = _cloudSwapQueue.Count > 0;
                bool emptySky = (_cloudSwapDesired == "Clear_VClouds" || wx.currentCloudConfig == "Clear_VClouds")
                    && wx.targetCloudOpacity < 0.04f && wx.targetClouds < 0.04f;
                float liveOp = emptySky
                    ? wx.targetCloudOpacity
                    : (wx.targetCloudOpacity > 0.05f ? wx.targetCloudOpacity : Mathf.Max(CloudSwapFloor, _cloudSwapSavedOpacity));
                float liveCov = emptySky
                    ? wx.targetClouds
                    : (wx.targetClouds > 0.05f ? wx.targetClouds : Mathf.Max(CloudSwapFloorCoverage, _cloudSwapSavedCoverage));
                float mid = moreHops ? 0.55f : 1f;
                float goalOp = Mathf.Lerp(_cloudSwapSavedOpacity, liveOp, mid);
                float goalCov = Mathf.Lerp(_cloudSwapSavedCoverage, liveCov, mid);
                wx.currentCloudOpacity = Mathf.Lerp(_cloudSwapFadeFromOpacity, goalOp, s);
                wx.currentClouds = Mathf.Lerp(_cloudSwapFadeFromCoverage, goalCov, s);
                ApplyDissolveLighting(moreHops ? t * mid : t);

                if (t >= 0.98f)
                {
                    wx.currentCloudOpacity = goalOp;
                    wx.currentClouds = goalCov;
                    ApplyDissolveLighting(moreHops ? mid : 1f);
                    if (moreHops)
                    {
                        string next = _cloudSwapQueue[0];
                        _cloudSwapQueue.RemoveAt(0);
                        Puts($"[Weather] Cloud dissolve next hop -> {next}");
                        StartCloudSwapHop(next);
                        return;
                    }
                    _cloudSwapPhase = CloudSwapPhase.Idle;
                    _cloudSwapDesired = null;
                    _cloudSwapHopsTotal = 1;
                    StopCloudSwapTicker();
                    Puts("[Weather] Cloud dissolve complete");
                }
            }
        }

        private void BlendLocalWeather()
        {
            if (!config.UseLocalWeather) return;
            if (config.Tournament != null && config.Tournament.Enabled && config.Tournament.DisableWeatherChanges) return;

            UpdateTargetsFromClock(forceLog: false);

            float distance = Mathf.Abs(wx.currentRain - wx.targetRain) + Mathf.Abs(wx.currentClouds - wx.targetClouds) +
                             Mathf.Abs(wx.currentFog - wx.targetFog) + Mathf.Abs(wx.currentThunder - wx.targetThunder) * 0.5f;
            float baseT = wx.WeatherBlendFactor;
            float t = Mathf.Clamp(baseT * (0.70f + distance * 1.5f), baseT * 0.55f, baseT * 2.8f);
            bool clearing = (wx.targetRain < wx.currentRain - 0.02f) || (wx.targetClouds < wx.currentClouds - 0.04f);
            float clearMul = clearing ? 0.82f : 1.0f;
            float tFast = Mathf.Clamp01(t * 1.45f * clearMul);
            float tMid = Mathf.Clamp01(t * 1.05f * clearMul);
            float tRain = Mathf.Clamp01(t * 0.95f * clearMul);
            float tFog = Mathf.Clamp01(t * 0.85f * clearMul);
            float tWetUp = Mathf.Clamp01(t * 0.70f);
            float tWetDown = Mathf.Clamp01(t * (0.85f + wx.currentWind * 1.1f));

            // During cloud-asset dissolve, TickCloudAssetDissolve owns opacity + coverage
            // so the normal blend cannot fight the fade (that fight caused visible snaps).
            if (_cloudSwapPhase == CloudSwapPhase.Idle)
            {
                wx.currentCloudOpacity = Mathf.Lerp(wx.currentCloudOpacity, wx.targetCloudOpacity, tFast);
                wx.currentClouds = Mathf.Lerp(wx.currentClouds, wx.targetClouds, tMid);
                wx.currentAttenuation = Mathf.Lerp(wx.currentAttenuation, wx.targetAttenuation, tFast);
                wx.currentMie = Mathf.Lerp(wx.currentMie, wx.targetMie, tFast);
                bool flashLive = wx._lightningFlashUntil > Time.realtimeSinceStartup;
                if (!flashLive)
                {
                    wx.currentBrightness = Mathf.Lerp(wx.currentBrightness, wx.targetBrightness, tMid);
                    wx.currentContrast = Mathf.Lerp(wx.currentContrast, wx.targetContrast, tMid);
                    wx.currentCloudBrightness = Mathf.Lerp(wx.currentCloudBrightness, wx.targetCloudBrightness, tMid);
                    wx.currentRayleigh = Mathf.Lerp(wx.currentRayleigh, wx.targetRayleigh, tMid);
                }
                wx.currentCloudSharpness = Mathf.Lerp(wx.currentCloudSharpness, wx.targetCloudSharpness, tMid);
                wx.currentCloudScattering = Mathf.Lerp(wx.currentCloudScattering, wx.targetCloudScattering, tMid);
                wx.currentCloudColoring = Mathf.Lerp(wx.currentCloudColoring, wx.targetCloudColoring, tMid);
                wx.currentCloudSize = Mathf.Lerp(wx.currentCloudSize, wx.targetCloudSize, tMid);
                wx.currentCloudSaturation = Mathf.Lerp(wx.currentCloudSaturation, wx.targetCloudSaturation, tMid);
                wx.currentDirectionality = Mathf.Lerp(wx.currentDirectionality, wx.targetDirectionality, tMid);
                wx.currentDirLight = Mathf.Lerp(wx.currentDirLight, wx.targetDirLight, tMid);
                wx.currentAmbLight = Mathf.Lerp(wx.currentAmbLight, wx.targetAmbLight, tMid);
                wx.currentVCloudSun = Mathf.Lerp(wx.currentVCloudSun, wx.targetVCloudSun, tMid);
                wx.currentVCloudMoon = Mathf.Lerp(wx.currentVCloudMoon, wx.targetVCloudMoon, tMid);
                wx.currentSunMesh = Mathf.Lerp(wx.currentSunMesh, wx.targetSunMesh, tMid);
                wx.currentMoonMesh = Mathf.Lerp(wx.currentMoonMesh, wx.targetMoonMesh, tMid);
                wx.currentReflection = Mathf.Lerp(wx.currentReflection, wx.targetReflection, tMid);
            }
            wx.currentWind = Mathf.Lerp(wx.currentWind, wx.targetWind, tMid);
            wx.currentRain = Mathf.Lerp(wx.currentRain, wx.targetRain, tRain);
            wx.currentThunder = Mathf.Lerp(wx.currentThunder, wx.targetThunder, tRain);
            wx.currentRainbow = Mathf.Lerp(wx.currentRainbow, wx.targetRainbow, tRain);
            wx.currentFog = Mathf.Lerp(wx.currentFog, wx.targetFog, tFog);
            wx.currentFogMultiplier = Mathf.Lerp(wx.currentFogMultiplier, wx.targetFogMultiplier, tFog);
            wx.currentFogRampStart = Mathf.Lerp(wx.currentFogRampStart, wx.targetFogRampStart, tFog);
            wx.currentFogRampEnd = Mathf.Lerp(wx.currentFogRampEnd, wx.targetFogRampEnd, tFog);
            wx.currentFogHeightFalloff = Mathf.Lerp(wx.currentFogHeightFalloff, wx.targetFogHeightFalloff, tFog);
            float wetT = wx.targetWetness >= wx.currentWetness ? tWetUp : tWetDown;
            wx.currentWetness = Mathf.Lerp(wx.currentWetness, wx.targetWetness, wetT);
            wx.currentWetnessSnow = Mathf.Lerp(wx.currentWetnessSnow, wx.targetWetnessSnow, wetT);
            wx.currentDust = Mathf.Lerp(wx.currentDust, wx.targetDust, tMid);

            // Snap settled channels so lerp + solar/CCN jitter cannot hunt under F2
            // rounding and re-issue the same printed convar forever.
            // Dissolve ticker owns these two channels; snapping toward the live
            // weather target here is what turned a 1.7s in-ramp into a single jump.
            if (_cloudSwapPhase == CloudSwapPhase.Idle)
            {
                SnapSettled(ref wx.currentCloudOpacity, wx.targetCloudOpacity);
                SnapSettled(ref wx.currentClouds, wx.targetClouds);
                SnapSettled(ref wx.currentAttenuation, wx.targetAttenuation);
                SnapSettled(ref wx.currentMie, wx.targetMie);
                if (wx._lightningFlashUntil <= Time.realtimeSinceStartup)
                {
                    SnapSettled(ref wx.currentBrightness, wx.targetBrightness);
                    SnapSettled(ref wx.currentContrast, wx.targetContrast);
                }
                SnapSettled(ref wx.currentCloudBrightness, wx.targetCloudBrightness);
                SnapSettled(ref wx.currentCloudSharpness, wx.targetCloudSharpness);
                SnapSettled(ref wx.currentCloudScattering, wx.targetCloudScattering);
                SnapSettled(ref wx.currentCloudColoring, wx.targetCloudColoring);
                SnapSettled(ref wx.currentCloudSize, wx.targetCloudSize);
                SnapSettled(ref wx.currentCloudSaturation, wx.targetCloudSaturation);
                SnapSettled(ref wx.currentRayleigh, wx.targetRayleigh);
                SnapSettled(ref wx.currentDirectionality, wx.targetDirectionality);
            }
            SnapSettled(ref wx.currentWind, wx.targetWind);
            SnapSettled(ref wx.currentRain, wx.targetRain);
            SnapSettled(ref wx.currentThunder, wx.targetThunder);
            SnapSettled(ref wx.currentRainbow, wx.targetRainbow);
            SnapSettled(ref wx.currentFog, wx.targetFog);
            SnapSettled(ref wx.currentFogMultiplier, wx.targetFogMultiplier);
            SnapSettled(ref wx.currentFogRampStart, wx.targetFogRampStart, 2f);
            SnapSettled(ref wx.currentFogRampEnd, wx.targetFogRampEnd, 5f);
            SnapSettled(ref wx.currentFogHeightFalloff, wx.targetFogHeightFalloff);
            SnapSettled(ref wx.currentWetness, wx.targetWetness);
            SnapSettled(ref wx.currentWetnessSnow, wx.targetWetnessSnow);
            SnapSettled(ref wx.currentDust, wx.targetDust);

            TickCloudAssetDissolve();
            UpdateLightningFlash();
            ApplyWeatherToClimate();

            bool lightningOn = config.EnableLightningStrikes || config.EnableRealLightning;
            if (lightningOn && wx.currentThunder >= config.LightningThunderThreshold && SkyAllowsLightning())
                TrySpawnLightning();

            lastWeather = PublicWeatherLabel();
            lastWeatherIntensity = Mathf.Max(wx.currentClouds, wx.currentDust, wx.currentRain);
            lastRainIntensity = wx.currentRain;
            lastThunderIntensity = wx.currentThunder;
            lastWindIntensity = wx.currentWind;
            lastFogIntensity = wx.currentFog;
        }

        /// <summary>
        /// Public label for /worldstats and the LiveStats website.
        /// Follows the mesh on screen (and rain/dust), not the WMO target — so
        /// "Clear" is not published while RainMild/Overcast is still loaded.
        /// </summary>
        private string PublicWeatherLabel()
        {
            if (wx.currentDust > 0.35f && wx.currentRain < 0.1f) return "Dust";

            string asset = wx.currentCloudConfig;
            if (string.IsNullOrEmpty(asset))
            {
                return wx.pendingWeatherProfile switch
                {
                    "storm" => "Storm",
                    "rain" => "Rain",
                    "snow" => "Snow",
                    "fog" => "Fog",
                    "overcast" => "Overcast",
                    "partly_cloudy" => "Partly Cloudy",
                    "few" => "Few Clouds",
                    _ => "Clear"
                };
            }

            switch (asset)
            {
                case "Storm_VClouds":
                    return "Storm";
                case "RainHeavy_VClouds":
                    return wx.currentRain > 0.12f ? "Rain" : "Storm";
                case "RainMild_VClouds":
                    if (wx.currentRain > 0.12f) return "Rain";
                    return "Partly Cloudy";
                case "Overcast_VClouds":
                    return "Overcast";
                case "Fog_VClouds":
                    return wx.currentFog > 0.08f || wx.pendingWeatherProfile == "fog" ? "Fog" : "Overcast";
                case "Clear_VClouds":
                    if (wx.pendingWeatherProfile == "few" || wx.currentClouds >= 0.05f || wx.currentCloudOpacity >= 0.08f)
                        return "Few Clouds";
                    return "Clear";
                default:
                    return wx.pendingWeatherProfile == "clear" ? "Clear" : "Partly Cloudy";
            }
        }

        private static bool Changed(float current, float applied, float eps) => float.IsNaN(applied) || Mathf.Abs(current - applied) >= eps;

        private static void SnapSettled(ref float current, float target, float eps = WeatherApplyEpsilon)
        {
            if (Mathf.Abs(current - target) < eps)
                current = target;
        }

        private static float Quantize(float value, string format)
        {
            if (format == "F0") return Mathf.Round(value);
            if (format == "F1") return Mathf.Round(value * 10f) / 10f;
            if (format == "F3") return Mathf.Round(value * 1000f) / 1000f;
            return Mathf.Round(value * 100f) / 100f;
        }

        /// <summary>
        /// Write a weather convar only when the printed value would change.
        /// QuietWeatherConvars hides the engine echo; the convar still applies to clients.
        /// </summary>
        private void SetWeatherConvar(string name, float value, ref float applied, float eps, string format = "F2", bool forceQuiet = false)
        {
            float q = Quantize(value, format);
            if (!float.IsNaN(applied) && Quantize(applied, format) == q)
            {
                applied = q;
                return;
            }
            if (!Changed(q, applied, eps))
            {
                applied = q;
                return;
            }

            string next = q.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            var opt = ConsoleSystem.Option.Server;
            if (forceQuiet || (config != null && config.QuietWeatherConvars))
                opt = opt.Quiet();
            ConsoleSystem.Run(opt, $"{name} {next}");
            applied = q;
        }

        /// <summary>
        /// Facepunch does NOT use 0-1 for every weather.* key.
        /// Unit channels (rain/fog/coverage/…) stay 0-1.
        /// Atmosphere / cloud scale channels follow vanilla weather.report
        /// (Rayleigh 2, Mie 4, Size 2, Contrast 1.25). Never write -1.
        /// </summary>
        private void ClampWeatherTargets()
        {
            wx.targetRain = Mathf.Clamp01(wx.targetRain);
            wx.targetWind = Mathf.Clamp01(wx.targetWind);
            wx.targetFog = Mathf.Clamp01(wx.targetFog);
            wx.targetThunder = Mathf.Clamp01(wx.targetThunder);
            wx.targetRainbow = Mathf.Clamp01(wx.targetRainbow);
            wx.targetDust = Mathf.Clamp01(wx.targetDust);
            wx.targetWetness = Mathf.Clamp01(wx.targetWetness);
            wx.targetWetnessSnow = Mathf.Clamp01(wx.targetWetnessSnow);
            wx.targetClouds = Mathf.Clamp01(wx.targetClouds);
            wx.targetCloudOpacity = Mathf.Clamp01(wx.targetCloudOpacity);
            wx.targetCloudSharpness = Mathf.Clamp01(wx.targetCloudSharpness);
            wx.targetCloudColoring = Mathf.Clamp01(wx.targetCloudColoring);
            wx.targetCloudSaturation = Mathf.Clamp01(wx.targetCloudSaturation);
            wx.targetFogHeightFalloff = Mathf.Clamp01(wx.targetFogHeightFalloff);
            wx.targetDirectionality = Mathf.Clamp01(wx.targetDirectionality);

            wx.targetFogMultiplier = Mathf.Clamp(wx.targetFogMultiplier, 0.50f, 2.00f);
            wx.targetFogRampStart = Mathf.Clamp(wx.targetFogRampStart, 5f, 400f);
            wx.targetFogRampEnd = Mathf.Clamp(wx.targetFogRampEnd, 80f, 2500f);
            if (wx.targetFogRampEnd < wx.targetFogRampStart + 40f)
                wx.targetFogRampEnd = wx.targetFogRampStart + 40f;

            wx.targetBrightness = Mathf.Clamp(wx.targetBrightness, 0.30f, 1.50f);
            wx.targetContrast = Mathf.Clamp(wx.targetContrast, 0.50f, 1.50f);
            wx.targetRayleigh = Mathf.Clamp(wx.targetRayleigh, 0.20f, 2.50f);
            wx.targetMie = Mathf.Clamp(wx.targetMie, 0.00f, 2.00f);
            wx.targetAttenuation = Mathf.Clamp(wx.targetAttenuation, 0.20f, 2.00f);
            wx.targetCloudBrightness = Mathf.Clamp(wx.targetCloudBrightness, 0.30f, 1.50f);
            wx.targetCloudScattering = Mathf.Clamp(wx.targetCloudScattering, 0.30f, 2.50f);
            wx.targetCloudSize = Mathf.Clamp(wx.targetCloudSize, 0.40f, 2.50f);
            wx.targetDirLight = Mathf.Clamp01(wx.targetDirLight);
            wx.targetAmbLight = Mathf.Clamp01(wx.targetAmbLight);
            wx.targetVCloudSun = Mathf.Clamp01(wx.targetVCloudSun);
            wx.targetVCloudMoon = Mathf.Clamp01(wx.targetVCloudMoon);
            wx.targetSunMesh = Mathf.Clamp01(wx.targetSunMesh);
            wx.targetMoonMesh = Mathf.Clamp01(wx.targetMoonMesh);
            wx.targetReflection = Mathf.Clamp01(wx.targetReflection);
        }

        private void ClampWeatherCurrent()
        {
            wx.currentRain = Mathf.Clamp01(wx.currentRain);
            wx.currentWind = Mathf.Clamp01(wx.currentWind);
            wx.currentFog = Mathf.Clamp01(wx.currentFog);
            wx.currentThunder = Mathf.Clamp01(wx.currentThunder);
            wx.currentRainbow = Mathf.Clamp01(wx.currentRainbow);
            wx.currentDust = Mathf.Clamp01(wx.currentDust);
            wx.currentWetness = Mathf.Clamp01(wx.currentWetness);
            wx.currentWetnessSnow = Mathf.Clamp01(wx.currentWetnessSnow);
            wx.currentClouds = Mathf.Clamp01(wx.currentClouds);
            wx.currentCloudOpacity = Mathf.Clamp01(wx.currentCloudOpacity);
            wx.currentCloudSharpness = Mathf.Clamp01(wx.currentCloudSharpness);
            wx.currentCloudColoring = Mathf.Clamp01(wx.currentCloudColoring);
            wx.currentCloudSaturation = Mathf.Clamp01(wx.currentCloudSaturation);
            wx.currentFogHeightFalloff = Mathf.Clamp01(wx.currentFogHeightFalloff);
            wx.currentDirectionality = Mathf.Clamp01(wx.currentDirectionality);

            wx.currentFogMultiplier = Mathf.Clamp(wx.currentFogMultiplier, 0.50f, 2.00f);
            wx.currentFogRampStart = Mathf.Clamp(wx.currentFogRampStart, 5f, 400f);
            wx.currentFogRampEnd = Mathf.Clamp(wx.currentFogRampEnd, 80f, 2500f);
            if (wx.currentFogRampEnd < wx.currentFogRampStart + 40f)
                wx.currentFogRampEnd = wx.currentFogRampStart + 40f;

            wx.currentBrightness = Mathf.Clamp(wx.currentBrightness, 0.30f, 1.50f);
            wx.currentContrast = Mathf.Clamp(wx.currentContrast, 0.50f, 1.50f);
            wx.currentRayleigh = Mathf.Clamp(wx.currentRayleigh, 0.20f, 2.50f);
            wx.currentMie = Mathf.Clamp(wx.currentMie, 0.00f, 2.00f);
            wx.currentAttenuation = Mathf.Clamp(wx.currentAttenuation, 0.20f, 2.00f);
            wx.currentCloudBrightness = Mathf.Clamp(wx.currentCloudBrightness, 0.30f, 1.50f);
            wx.currentCloudScattering = Mathf.Clamp(wx.currentCloudScattering, 0.30f, 2.50f);
            wx.currentCloudSize = Mathf.Clamp(wx.currentCloudSize, 0.40f, 2.50f);

            wx.currentDirLight = Mathf.Clamp01(wx.currentDirLight);
            wx.currentAmbLight = Mathf.Clamp01(wx.currentAmbLight);
            wx.currentVCloudSun = Mathf.Clamp01(wx.currentVCloudSun);
            wx.currentVCloudMoon = Mathf.Clamp01(wx.currentVCloudMoon);
            wx.currentSunMesh = Mathf.Clamp01(wx.currentSunMesh);
            wx.currentMoonMesh = Mathf.Clamp01(wx.currentMoonMesh);
            wx.currentReflection = Mathf.Clamp01(wx.currentReflection);
        }

        private void ApplyWeatherToClimate()
        {
            ClampWeatherCurrent();
            var climate = SingletonComponent<global::Climate>.Instance;
            if (climate == null) return;
            climate.Overrides.Rain = wx.currentRain;
            climate.Overrides.Wind = wx.currentWind;
            climate.Overrides.Fog = wx.currentFog;
            climate.Overrides.Clouds = wx.currentClouds;

            float eps = WeatherApplyEpsilon;
            SetWeatherConvar("weather.rain", wx.currentRain, ref wx.appliedRain, eps);
            SetWeatherConvar("weather.fog", wx.currentFog, ref wx.appliedFog, eps);
            SetWeatherConvar("weather.wind", wx.currentWind, ref wx.appliedWind, eps);
            SetWeatherConvar("weather.dust_chance", wx.currentDust, ref wx.appliedDust, eps);
            SetWeatherConvar("weather.fog_multiplier", wx.currentFogMultiplier, ref wx.appliedFogMultiplier, eps);
            SetWeatherConvar("weather.atmosphere_fog_ramp_start_distance", wx.currentFogRampStart, ref wx.appliedFogRampStart, 2f, "F0");
            SetWeatherConvar("weather.atmosphere_fog_ramp_end_distance", wx.currentFogRampEnd, ref wx.appliedFogRampEnd, 5f, "F0");
            SetWeatherConvar("weather.atmosphere_fog_height_falloff", wx.currentFogHeightFalloff, ref wx.appliedFogHeightFalloff, eps);
            SetWeatherConvar("weather.cloud_coverage", wx.currentClouds, ref wx.appliedClouds, eps);
            SetWeatherConvar("weather.cloud_opacity", wx.currentCloudOpacity, ref wx.appliedCloudOpacity, eps);
            SetWeatherConvar("weather.cloud_attenuation", wx.currentAttenuation, ref wx.appliedAttenuation, eps);
            SetWeatherConvar("weather.cloud_brightness", wx.currentCloudBrightness, ref wx.appliedCloudBrightness, eps);
            SetWeatherConvar("weather.cloud_sharpness", wx.currentCloudSharpness, ref wx.appliedCloudSharpness, eps);
            SetWeatherConvar("weather.cloud_scattering", wx.currentCloudScattering, ref wx.appliedCloudScattering, eps);
            SetWeatherConvar("weather.cloud_coloring", wx.currentCloudColoring, ref wx.appliedCloudColoring, eps);
            SetWeatherConvar("weather.cloud_size", wx.currentCloudSize, ref wx.appliedCloudSize, eps);
            SetWeatherConvar("weather.cloud_saturation", wx.currentCloudSaturation, ref wx.appliedCloudSaturation, eps);
            SetWeatherConvar("weather.thunder", wx.currentThunder, ref wx.appliedThunder, eps);
            SetWeatherConvar("weather.rainbow", wx.currentRainbow, ref wx.appliedRainbow, eps);
            SetWeatherConvar("weather.wetness_rain", wx.currentWetness, ref wx.appliedWetness, eps);
            SetWeatherConvar("weather.wetness_snow", wx.currentWetnessSnow, ref wx.appliedWetnessSnow, eps);
            SetWeatherConvar("weather.atmosphere_mie", wx.currentMie, ref wx.appliedMie, eps);
            SetWeatherConvar("weather.atmosphere_rayleigh", wx.currentRayleigh, ref wx.appliedRayleigh, eps);
            SetWeatherConvar("weather.atmosphere_brightness", wx.currentBrightness, ref wx.appliedBrightness, eps);
            SetWeatherConvar("weather.atmosphere_contrast", wx.currentContrast, ref wx.appliedContrast, eps);
            SetWeatherConvar("weather.atmosphere_directionality", wx.currentDirectionality, ref wx.appliedDirectionality, eps);
            ApplyOptionalLightingConvars(false);
            ApplyNightlightConvars();
        }

        private bool IsGameNightHour()
        {
            float hour = 12f;
            try
            {
                var tod = TOD_Sky.Instance;
                if (tod != null) hour = tod.Cycle.Hour;
            }
            catch { }
            float start = 6f, end = 20f;
            var split = config?.TimeSystem?.SplitDay;
            if (split != null)
            {
                start = split.DayStartHour;
                end = split.DayEndHour;
            }
            return hour < start || hour >= end;
        }

        private static float LerpOptional(float from, float to, float s)
        {
            return Mathf.Lerp(from, to, Mathf.Clamp01(s));
        }

        /// <summary>0 new moon, 1 full moon. Floor so a sliver still reads as a disc.</summary>
        private float MoonIlluminationFactor()
        {
            return Mathf.Clamp(lastMoonIllumination, 0.12f, 1f);
        }

        /// <summary>
        /// Map Open-Meteo scalars onto the extra 0-1 light multipliers.
        /// 1.0 = identity. Never write -1 (vanilla would own the channel).
        /// </summary>
        private void ComputeOptionalLightingFromMeteo(float rain, float cover, float fog, float thunder,
            float wetness, float cape, float shortwave, float humidity, float visibilityM, bool isDay)
        {
            wx.targetDirLight = wx.targetAmbLight = 1f;
            wx.targetVCloudSun = wx.targetVCloudMoon = 1f;
            wx.targetSunMesh = wx.targetMoonMesh = 1f;
            wx.targetReflection = 1f;
            var lit = config?.Lighting;
            if (lit == null || !lit.Enabled) return;

            float rain01 = Mathf.Clamp01(rain);
            float cover01 = Mathf.Clamp01(cover);
            float fog01 = Mathf.Clamp01(fog);
            float thunder01 = Mathf.Clamp01(thunder);
            float wet01 = Mathf.Clamp01(wetness);
            float sun01 = Mathf.Clamp01(shortwave / 800f);
            float humid01 = Mathf.Clamp01((humidity - 40f) / 55f);
            float visDim = visibilityM > 0f ? Mathf.Clamp01(Mathf.InverseLerp(14000f, 800f, visibilityM)) : 0f;
            float cape01 = Mathf.Clamp01(cape / 2500f);
            float thick = Mathf.Clamp01(cover01 * 0.70f + rain01 * 0.55f + fog01 * 0.35f + thunder01 * 0.25f + visDim * 0.20f);

            if (lit.DriveLightMultipliers)
            {
                // Direct sun follows shortwave, punched down by thick sky.
                wx.targetDirLight = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.52f, thick) * Mathf.Lerp(0.82f, 1.00f, isDay ? sun01 : 0.15f));
                // Humid / foggy air lifts fill light a little; storm still dims.
                wx.targetAmbLight = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.72f, thick) * Mathf.Lerp(1.00f, 1.00f, humid01));
            }

            if (lit.DriveVCloudColorScale)
            {
                wx.targetVCloudSun = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.62f, thick) * (isDay ? Mathf.Lerp(0.75f, 1.00f, sun01) : 0.55f));
                float moonCloudClear = Mathf.Clamp01(lit.VCloudMoonNightClear);
                float moonCloudThick = Mathf.Clamp01(lit.VCloudMoonNightOvercast);
                if (moonCloudThick > moonCloudClear) moonCloudThick = moonCloudClear;
                float nightCloud = Mathf.Lerp(moonCloudClear, moonCloudThick, thick) * MoonIlluminationFactor();
                wx.targetVCloudMoon = Mathf.Clamp01(isDay ? Mathf.Min(0.35f, nightCloud) : nightCloud);
            }

            if (lit.DriveMeshBrightness)
            {
                wx.targetSunMesh = Mathf.Clamp01(Mathf.Lerp(1.00f, 0.78f, thick) * (isDay ? Mathf.Lerp(0.80f, 1.00f, sun01) : 0.35f));
                float moonClear = Mathf.Clamp01(lit.MoonMeshNightClear);
                float moonThick = Mathf.Clamp01(lit.MoonMeshNightOvercast);
                if (moonThick > moonClear) moonThick = moonClear;
                float nightMesh = Mathf.Lerp(moonClear, moonThick, thick) * MoonIlluminationFactor();
                // Day: keep the disc quiet. Night: cap so moonlight does not bleach vclouds.
                wx.targetMoonMesh = Mathf.Clamp01(isDay ? Mathf.Min(0.28f, nightMesh) : nightMesh);
            }

            if (lit.DriveReflection)
                wx.targetReflection = Mathf.Clamp01(Mathf.Lerp(0.70f, 1.00f, Mathf.Max(wet01, rain01)));

            // CAPE only tightens vcloud/sun a hair when a cell is primed — no mesh swap.
            if (cape01 > 0.35f && rain01 < 0.12f)
            {
                float dryCell = (cape01 - 0.35f) * 0.15f;
                wx.targetVCloudSun = Mathf.Clamp01(wx.targetVCloudSun - dryCell);
                wx.targetDirLight = Mathf.Clamp01(wx.targetDirLight - dryCell * 0.5f);
            }
        }

        private void ApplyOptionalLightingConvars(bool quiet)
        {
            var lit = config?.Lighting;
            if (lit == null || !lit.Enabled) return;
            float eps = WeatherApplyEpsilon;
            if (lit.DriveLightMultipliers)
            {
                SetWeatherConvar("weather.directional_light_multiplier", wx.currentDirLight, ref wx.appliedDirLight, eps, "F2", quiet);
                SetWeatherConvar("weather.ambient_light_multiplier", wx.currentAmbLight, ref wx.appliedAmbLight, eps, "F2", quiet);
            }
            if (lit.DriveVCloudColorScale)
            {
                SetWeatherConvar("weather.vclouds_sun_color_scale", wx.currentVCloudSun, ref wx.appliedVCloudSun, eps, "F2", quiet);
                SetWeatherConvar("weather.vclouds_moon_color_scale", wx.currentVCloudMoon, ref wx.appliedVCloudMoon, eps, "F2", quiet);
            }
            if (lit.DriveMeshBrightness)
            {
                SetWeatherConvar("weather.sun_mesh_brightness_multiplier", wx.currentSunMesh, ref wx.appliedSunMesh, eps, "F2", quiet);
                SetWeatherConvar("weather.moon_mesh_brightness_multiplier", wx.currentMoonMesh, ref wx.appliedMoonMesh, eps, "F2", quiet);
            }
            if (lit.DriveReflection)
                SetWeatherConvar("weather.reflection_multiplier", wx.currentReflection, ref wx.appliedReflection, eps, "F2", quiet);
        }

        private void ApplyNightlightConvars()
        {
            var lit = config?.Lighting;
            if (lit == null || !lit.NightlightEnabled) return;
            float bri = Mathf.Clamp(lit.NightlightBrightness, 0f, 0.05f);
            bool night = !wx.lastOmIsDay || IsGameNightHour();
            if (night && wx.currentClouds > 0.45f)
                bri = Mathf.Clamp(bri + Mathf.Max(0f, lit.NightlightOvercastBoost), 0f, 0.05f);
            float dist = Mathf.Clamp(lit.NightlightDistance, 1f, 20f);
            float fade = Mathf.Clamp01(lit.NightlightFadeFraction);
            SetWeatherConvar("env.nightlight_brightness", bri, ref wx.appliedNightlightBri, 0.0005f, "F3");
            SetWeatherConvar("env.nightlight_distance", dist, ref wx.appliedNightlightDist, 0.05f, "F2");
            SetWeatherConvar("env.nightlight_fadefraction", fade, ref wx.appliedNightlightFade, 0.01f, "F2");
        }

        /// <summary>
        /// Split fair weather into three looks that stock assets can actually show:
        /// clear = empty dome on Clear_VClouds, few = wisps on Clear_VClouds,
        /// partly_cloudy = RainMild_VClouds.
        /// </summary>
        private static string RefineFairSkyProfile(string profile, int weatherCode, float stack, float cloudPercent, float rainValue)
        {
            if (profile == "storm" || profile == "rain" || profile == "snow"
                || profile == "fog" || profile == "dust" || profile == "overcast")
                return profile;

            if (rainValue >= 0.05f)
                return "partly_cloudy";

            float cover = Mathf.Max(stack, cloudPercent);
            // Layer mix wins over WMO. Code 2 with 5% cover is still an empty dome.
            if (cover <= 0.10f)
                return "clear";
            if (cover <= 0.28f && weatherCode <= 1)
                return "few";
            if (weatherCode <= 2 || profile == "clear" || profile == "few" || profile == "partly_cloudy")
                return "partly_cloudy";
            return profile;
        }

        private string GetWeatherProfile(int weatherCode)
        {
            if (weatherCode >= 95) return "storm";
            if (config.EnableSnow && weatherCode >= 71 && weatherCode <= 77) return "snow";
            if (config.EnableSnow && weatherCode >= 85 && weatherCode <= 86) return "snow";
            if (weatherCode >= 80 && weatherCode <= 82) return "rain";
            if (weatherCode >= 61 && weatherCode <= 67) return "rain";
            if (weatherCode >= 51 && weatherCode <= 57) return "rain";
            if (weatherCode == 45 || weatherCode == 48) return "fog";
            if (config.EnableDust && weatherCode >= 7 && weatherCode <= 9) return "dust";
            if (weatherCode >= 3) return "overcast";
            if (weatherCode == 2) return "partly_cloudy";
            if (weatherCode == 1) return "few";
            return "clear";
        }

        private void SetWeatherProfile(string profile, float rainIntensity = 0f)
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.clear_chance 0");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.dust_chance 0");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.fog_chance 0");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.overcast_chance 0");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.rain_chance 0");
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.storm_chance 0");
            wx.currentWeatherProfile = profile ?? "clear";
            if (config.AllowCloudConfigSwap)
            {
                string cloudConfig = DesiredCloudConfig(wx.currentWeatherProfile, rainIntensity);
                if (!string.IsNullOrEmpty(cloudConfig) && cloudConfig != wx.currentCloudConfig)
                {
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, $"weather.load_cloud_config {cloudConfig}");
                    wx.currentCloudConfig = cloudConfig;
                }
            }
            Puts($"[Weather] Continuous mode -- profile '{wx.currentWeatherProfile}', cloud '{wx.currentCloudConfig}'");
        }

        private void ApplyMultiDayLookAhead(bool forceLog)
        {
            wx._lookAheadRain = 0f; wx._lookAheadHours = 0f; wx._lookAheadLabel = "none";
            if (!config.EnableLookAhead || _weatherAnchors.Count < 2) return;
            DateTime now = DateTime.UtcNow;
            float bestScore = 0f; float bestHours = 0f; float bestRain = 0f; int bestCode = 0; float bestCloud = 0f; float bestCape = 0f;
            foreach (var s in _weatherAnchors)
            {
                double hours = (s.TimeUtc - now).TotalHours;
                if (hours < 1.0 || hours > 72.0) continue;
                float rainI = Mathf.Clamp01(1f - Mathf.Exp(-Mathf.Max(s.RainMm, s.ShowersMm) * 0.35f));
                float cloudI = Mathf.Clamp01(s.CloudPercent);
                float stormI = s.WeatherCode >= 95 ? 1f : (s.WeatherCode >= 80 ? 0.55f : 0f);
                float capeI = Mathf.Clamp01(Mathf.InverseLerp(200f, 2000f, s.Cape));
                float tWeight = Mathf.Clamp01(1f - (float)((hours - 1.0) / 71.0));
                tWeight *= tWeight;
                float score = (rainI * 0.45f + cloudI * 0.20f + stormI * 0.25f + capeI * 0.20f) * tWeight;
                if (score > bestScore) { bestScore = score; bestHours = (float)hours; bestRain = rainI; bestCode = s.WeatherCode; bestCloud = cloudI; bestCape = s.Cape; }
            }
            if (bestScore < 0.08f) return;
            wx._lookAheadRain = bestRain; wx._lookAheadHours = bestHours;
            wx._lookAheadLabel = bestCode >= 95 ? "storm" : bestRain > 0.35f ? "rain" : bestCloud > 0.6f ? "cloud" : "mild";
            float maxBias = Mathf.Clamp(config.LookAheadMaxBias, 0f, 0.35f);
            float proximity = Mathf.Clamp01(1f - (bestHours - 1f) / 36f);
            float bias = maxBias * bestScore * Mathf.Lerp(0.35f, 1f, proximity);
            if (bias > 0.02f && _cloudSwapPhase == CloudSwapPhase.Idle && wx.pendingWeatherProfile != "clear")
            {
                wx.targetCloudOpacity = Mathf.Clamp01(wx.targetCloudOpacity + bias * 0.55f);
                wx.targetClouds = Mathf.Clamp01(wx.targetClouds + bias * 0.40f);
                wx.targetAttenuation = Mathf.Min(1.9f, wx.targetAttenuation + bias * 0.25f);
            }
            if (forceLog) Puts($"[Weather] Look-ahead: {wx._lookAheadLabel} in ~{wx._lookAheadHours:F0}h");
        }

        private void UpdateCcnAndInstability(float humidity, float temperature, float dewPoint, float pressure, float rainValue, float windKmh, float windValue, float cape = 0f)
        {
            if (wx._seedBias > 0f && Time.realtimeSinceStartup >= wx._seedBiasEnd) wx._seedBias = 0f;
            if (config.EnableCcnPhysics)
            {
                float baseCcn = Mathf.Clamp01(config.CcnBaseLevel);
                float dust = 0f;
                if (rainValue < 0.08f && windKmh > 12f)
                {
                    float dryFactor = Mathf.Clamp01(Mathf.InverseLerp(55f, 20f, humidity));
                    dust = Mathf.InverseLerp(12f, 45f, windKmh) * dryFactor * Mathf.Clamp01(config.CcnDustRate);
                }
                float humidBoost = Mathf.InverseLerp(40f, 90f, humidity) * 0.08f;
                float washout = rainValue * Mathf.Clamp01(config.CcnWashoutRate);
                wx.targetCcn = Mathf.Clamp01(baseCcn + dust + humidBoost + wx._seedBias - washout);
                float dt = Mathf.Clamp(Time.realtimeSinceStartup - wx._lastCcnUpdate, 0.5f, 10f);
                wx._lastCcnUpdate = Time.realtimeSinceStartup;
                wx.currentCcn = Mathf.Lerp(wx.currentCcn, wx.targetCcn, Mathf.Clamp01(dt / 25f));
            }
            else wx.currentCcn = wx.targetCcn = Mathf.Clamp01(config.CcnBaseLevel);

            if (config.EnableInstability)
            {
                float tempDewSpread = Mathf.Abs(temperature - dewPoint);
                float moistureInst = Mathf.Clamp01(Mathf.InverseLerp(5f, 0.5f, tempDewSpread));
                float warmBoost = Mathf.Clamp01(Mathf.InverseLerp(5f, 28f, temperature)) * 0.30f;
                float pressureInst = pressure > 0 ? Mathf.Clamp01(Mathf.InverseLerp(1018f, 995f, pressure)) : 0f;
                float shear = windValue * 0.20f;
                float capeInst = Mathf.Clamp01(Mathf.InverseLerp(100f, 2500f, cape));
                wx.targetInstability = Mathf.Clamp01(moistureInst * 0.30f + warmBoost * 0.15f + pressureInst * 0.20f + shear + capeInst * 0.45f + wx._seedBias * 0.3f);
                wx.currentInstability = Mathf.Lerp(wx.currentInstability, wx.targetInstability, 0.12f);
            }
            else wx.currentInstability = wx.targetInstability = 0f;
        }

        private static float RainEfficiencyFromCcn(float ccn)
        {
            if (ccn < 0.25f) return Mathf.Lerp(0.70f, 1.05f, Mathf.InverseLerp(0f, 0.25f, ccn));
            if (ccn < 0.55f) return Mathf.Lerp(1.05f, 1.15f, Mathf.InverseLerp(0.25f, 0.55f, ccn));
            return Mathf.Lerp(1.15f, 0.75f, Mathf.InverseLerp(0.55f, 1.0f, ccn));
        }

        /// <summary>
        /// No bolts on an empty mesh. Thunder may still tick; RainMild / Overcast /
        /// Storm / Fog can flash.
        /// </summary>
        private bool SkyAllowsLightning()
        {
            string asset = wx.currentCloudConfig ?? "";
            if (string.IsNullOrEmpty(asset) || asset == "Clear_VClouds")
                return false;
            return true;
        }

        private void TrySpawnLightning()
        {
            float now = Time.realtimeSinceStartup;
            if (now < wx._nextLightningEarliest) return;
            float t = Mathf.Clamp01(wx.currentThunder);
            float minI = Mathf.Max(2f, config.LightningMinInterval);
            float maxI = Mathf.Max(minI + 1f, config.LightningMaxInterval);
            float interval = Mathf.Lerp(maxI, minI, t * t);
            // Real cells are bursty: long quiet stretches, then a cluster. Never a fixed beat.
            interval *= UnityEngine.Random.Range(0.45f, 1.75f);
            if (UnityEngine.Random.value < 0.18f + t * 0.12f)
                interval *= UnityEngine.Random.Range(0.18f, 0.42f); // same-cell follow-up
            else if (UnityEngine.Random.value < 0.12f)
                interval *= UnityEngine.Random.Range(1.6f, 2.4f);
            float chance = t < 0.6f ? Mathf.Lerp(0.08f, 0.28f, Mathf.InverseLerp(0.45f, 0.6f, t)) : Mathf.Lerp(0.28f, 0.75f, Mathf.InverseLerp(0.6f, 1f, t));
            if (!config.AllowDryLightning && wx.currentRain < 0.12f && t < 0.7f)
                chance *= 0.45f;
            wx._nextLightningEarliest = now + interval * UnityEngine.Random.Range(0.22f, 0.55f);
            if (UnityEngine.Random.value > chance) return;
            wx._nextLightningEarliest = now + interval;
            wx._lastLightningTime = now;
            Vector3 pos = PickLightningPosition();
            if (pos == Vector3.zero) return;
            string prefab = config.LightningEffectPrefab != null ? config.LightningEffectPrefab.Trim() : "";
            if (!string.IsNullOrEmpty(prefab) && prefab.IndexOf("c4", StringComparison.OrdinalIgnoreCase) < 0 && prefab.IndexOf("explosion", StringComparison.OrdinalIgnoreCase) < 0)
            {
                try { Effect.server.Run(prefab, pos); } catch { }
            }
            if (config.LightningFlash) BeginLightningFlash(t);
        }

        private Vector3 PickLightningPosition()
        {
            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0) return Vector3.zero;
            BasePlayer target = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var p = players[UnityEngine.Random.Range(0, players.Count)];
                if (p == null || p.IsDead()) continue;
                if (!p.IsSleeping()) { target = p; break; }
                target = p;
            }
            if (target == null) return Vector3.zero;
            float dist = UnityEngine.Random.Range(60f, 180f);
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            return target.transform.position + new Vector3(Mathf.Cos(ang) * dist, UnityEngine.Random.Range(80f, 160f), Mathf.Sin(ang) * dist);
        }

        private void BeginLightningFlash(float thunder)
        {
            float now = Time.realtimeSinceStartup;
            if (wx._lightningFlashUntil <= now)
            {
                wx._lightningFlashBri = wx.currentBrightness;
                wx._lightningFlashCon = wx.currentContrast;
                wx._lightningFlashCloudBri = wx.currentCloudBrightness;
                wx._lightningFlashRayleigh = wx.currentRayleigh;
            }

            // Real CG flash: 1–5 return strokes, first usually brightest, gaps 30–180ms,
            // duration of each stroke is not a fixed pulse. Occasional soft in-cloud glow.
            float mean = Mathf.Clamp(config.LightningFlashDuration, 0.04f, 0.22f);
            int strokes = 1;
            float roll = UnityEngine.Random.value;
            if (thunder > 0.55f && roll < 0.72f) strokes = 2;
            if (thunder > 0.70f && roll < 0.42f) strokes = 3;
            if (thunder > 0.85f && roll < 0.18f) strokes = 4;
            if (thunder > 0.92f && UnityEngine.Random.value < 0.08f) strokes = 5;

            _lightningStrokes.Clear();
            float cursor = now;
            float firstPeak = Mathf.Lerp(0.22f, 0.78f, thunder) * UnityEngine.Random.Range(0.72f, 1.28f);
            for (int i = 0; i < strokes; i++)
            {
                float peak = i == 0
                    ? firstPeak
                    : firstPeak * UnityEngine.Random.Range(0.22f, 0.72f);
                // Mix of hard nearby spikes and softer distant/cloud flashes
                float softness = UnityEngine.Random.value < 0.35f
                    ? UnityEngine.Random.Range(0.45f, 0.90f)
                    : UnityEngine.Random.Range(0.05f, 0.35f);
                if (i == 0 && UnityEngine.Random.value < 0.20f)
                    softness = UnityEngine.Random.Range(0.55f, 0.95f); // first stroke can itself be milky

                float on = mean * UnityEngine.Random.Range(0.55f, 1.65f);
                if (i > 0) on *= UnityEngine.Random.Range(0.45f, 0.95f);
                on = Mathf.Clamp(on, 0.03f, 0.22f);

                _lightningStrokes.Add(new LightningStroke
                {
                    Start = cursor,
                    End = cursor + on,
                    Peak = peak,
                    Softness = softness
                });
                cursor += on;
                if (i < strokes - 1)
                    cursor += UnityEngine.Random.Range(0.035f, 0.20f);
            }

            // Continuing-current afterglow (dim wobble, not a second hard stroke)
            if (UnityEngine.Random.value < 0.40f + thunder * 0.25f)
            {
                float glow = UnityEngine.Random.Range(0.06f, 0.22f);
                _lightningStrokes.Add(new LightningStroke
                {
                    Start = cursor,
                    End = cursor + glow,
                    Peak = firstPeak * UnityEngine.Random.Range(0.08f, 0.22f),
                    Softness = UnityEngine.Random.Range(0.70f, 1.0f)
                });
                cursor += glow;
            }

            wx._lightningWobbleSeed = UnityEngine.Random.Range(0f, 64f);
            wx._lightningFlashUntil = cursor + 0.02f;
            StopTimer(ref _lightningFlashTimer);
            TickLightningFlash();
            RestartEvery(ref _lightningFlashTimer, 0.05f, TickLightningFlash);
        }

        private void TickLightningFlash()
        {
            float now = Time.realtimeSinceStartup;
            if (wx._lightningFlashUntil <= 0f || now >= wx._lightningFlashUntil || _lightningStrokes.Count == 0)
            {
                RestoreLightningFlash();
                return;
            }

            float overlay = 0f;
            float softness = 0.3f;
            for (int i = 0; i < _lightningStrokes.Count; i++)
            {
                var s = _lightningStrokes[i];
                if (now < s.Start || now > s.End) continue;
                float u = Mathf.Clamp01((now - s.Start) / Mathf.Max(0.02f, s.End - s.Start));
                // Fast rise, slower tail — not a square gate
                float env = u < 0.18f
                    ? Mathf.SmoothStep(0f, 1f, u / 0.18f)
                    : Mathf.SmoothStep(1f, 0f, (u - 0.18f) / 0.82f);
                overlay = Mathf.Max(overlay, s.Peak * env);
                softness = s.Softness;
            }

            if (overlay > 0.01f)
            {
                // Irregular flicker inside the envelope (ionization + camera persistence)
                float wobble =
                    0.72f
                    + 0.18f * Mathf.Sin((now + wx._lightningWobbleSeed) * 37.1f)
                    + 0.10f * Mathf.Sin((now + wx._lightningWobbleSeed) * 71.6f)
                    + UnityEngine.Random.Range(-0.08f, 0.08f);
                overlay *= Mathf.Clamp(wobble, 0.55f, 1.25f);
            }

            float baseBri = float.IsNaN(wx._lightningFlashBri) ? wx.currentBrightness : wx._lightningFlashBri;
            float baseCon = float.IsNaN(wx._lightningFlashCon) ? wx.currentContrast : wx._lightningFlashCon;
            float baseCloud = float.IsNaN(wx._lightningFlashCloudBri) ? wx.currentCloudBrightness : wx._lightningFlashCloudBri;
            float baseRay = float.IsNaN(wx._lightningFlashRayleigh) ? wx.currentRayleigh : wx._lightningFlashRayleigh;

            // Hard stroke: more brightness, less contrast crush. Soft: milkier, lifts clouds.
            float briBoost = Mathf.Lerp(overlay, overlay * 0.62f, softness);
            float conBoost = Mathf.Lerp(overlay * 0.55f, overlay * 0.22f, softness);
            float cloudBoost = Mathf.Lerp(overlay * 0.15f, overlay * 0.45f, softness);
            float rayDip = Mathf.Lerp(0f, overlay * 0.18f, softness);

            wx.currentBrightness = Mathf.Min(1.85f, baseBri + briBoost);
            wx.currentContrast = Mathf.Min(1.55f, baseCon + conBoost);
            wx.currentCloudBrightness = Mathf.Min(1.6f, baseCloud + cloudBoost);
            wx.currentRayleigh = Mathf.Max(0.45f, baseRay - rayDip);

            SetWeatherConvar("weather.atmosphere_brightness", wx.currentBrightness, ref wx.appliedBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_contrast", wx.currentContrast, ref wx.appliedContrast, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_brightness", wx.currentCloudBrightness, ref wx.appliedCloudBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_rayleigh", wx.currentRayleigh, ref wx.appliedRayleigh, WeatherApplyEpsilon, "F2", true);
        }

        private void RestoreLightningFlash()
        {
            StopTimer(ref _lightningFlashTimer);
            _lightningStrokes.Clear();
            wx._lightningFlashUntil = 0f;
            if (!float.IsNaN(wx._lightningFlashBri)) wx.currentBrightness = wx._lightningFlashBri;
            if (!float.IsNaN(wx._lightningFlashCon)) wx.currentContrast = wx._lightningFlashCon;
            if (!float.IsNaN(wx._lightningFlashCloudBri)) wx.currentCloudBrightness = wx._lightningFlashCloudBri;
            if (!float.IsNaN(wx._lightningFlashRayleigh)) wx.currentRayleigh = wx._lightningFlashRayleigh;
            wx._lightningFlashBri = float.NaN;
            wx._lightningFlashCon = float.NaN;
            wx._lightningFlashCloudBri = float.NaN;
            wx._lightningFlashRayleigh = float.NaN;
            SetWeatherConvar("weather.atmosphere_brightness", wx.currentBrightness, ref wx.appliedBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_contrast", wx.currentContrast, ref wx.appliedContrast, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.cloud_brightness", wx.currentCloudBrightness, ref wx.appliedCloudBrightness, WeatherApplyEpsilon, "F2", true);
            SetWeatherConvar("weather.atmosphere_rayleigh", wx.currentRayleigh, ref wx.appliedRayleigh, WeatherApplyEpsilon, "F2", true);
        }

        private void UpdateLightningFlash()
        {
            if (wx._lightningFlashUntil <= 0f) return;
            if (Time.realtimeSinceStartup >= wx._lightningFlashUntil)
                RestoreLightningFlash();
        }

        private class WeatherStateData
        {
            public string Profile = "clear";
            public string CloudConfig = "";
            public int WeatherCode = 0;
            public float Rain, Wind, Fog, FogMultiplier, FogRampStart, FogRampEnd, FogHeightFalloff;
            public float Thunder, Rainbow, Wetness, WetnessSnow;
            public float Clouds, CloudOpacity, Mie, Brightness, Rayleigh, Contrast, Directionality, Attenuation;
            public float CloudBrightness, CloudSharpness, CloudScattering, CloudColoring, CloudSize, CloudSaturation;
            public string SavedAt = "";
            public float UtcOffsetSeconds;
            public string Timezone = "";
        }

        private void SaveWeatherState()
        {
            try
            {
                var data = new WeatherStateData
                {
                    Profile = wx.currentWeatherProfile ?? "clear",
                    CloudConfig = wx.currentCloudConfig ?? "",
                    WeatherCode = wx.lastWeatherCode,
                    Rain = wx.currentRain, Wind = wx.currentWind, Fog = wx.currentFog,
                    FogMultiplier = wx.currentFogMultiplier, FogRampStart = wx.currentFogRampStart, FogRampEnd = wx.currentFogRampEnd,
                    FogHeightFalloff = wx.currentFogHeightFalloff, Thunder = wx.currentThunder, Rainbow = wx.currentRainbow,
                    Wetness = wx.currentWetness, WetnessSnow = wx.currentWetnessSnow,
                    Clouds = wx.currentClouds, CloudOpacity = wx.currentCloudOpacity, Mie = wx.currentMie, Brightness = wx.currentBrightness,
                    Rayleigh = wx.currentRayleigh, Contrast = wx.currentContrast, Directionality = wx.currentDirectionality, Attenuation = wx.currentAttenuation,
                    CloudBrightness = wx.currentCloudBrightness, CloudSharpness = wx.currentCloudSharpness, CloudScattering = wx.currentCloudScattering,
                    CloudColoring = wx.currentCloudColoring, CloudSize = wx.currentCloudSize, CloudSaturation = wx.currentCloudSaturation,
                    SavedAt = DateTime.UtcNow.ToString("o"),
                    UtcOffsetSeconds = _cachedUtcOffsetSeconds,
                    Timezone = _cachedTimezone ?? ""
                };
                Interface.Oxide.DataFileSystem.WriteObject("livestats_weather_state", data);
            }
            catch (Exception ex) { Puts($"[Weather] Failed to save weather state: {ex.Message}"); }
        }

        private void LoadWeatherState()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<WeatherStateData>("livestats_weather_state");
                if (data == null || string.IsNullOrEmpty(data.Profile)) return;
                if (!string.IsNullOrEmpty(data.SavedAt) && DateTime.TryParse(data.SavedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime saved) && (DateTime.UtcNow - saved).TotalHours > 2.0) return;
                wx.currentWeatherProfile = data.Profile;
                wx.currentCloudConfig = data.CloudConfig ?? "";
                wx.lastWeatherCode = data.WeatherCode;
                wx.currentRain = data.Rain; wx.currentWind = data.Wind; wx.currentFog = data.Fog;
                wx.currentFogMultiplier = data.FogMultiplier; wx.currentFogRampStart = data.FogRampStart; wx.currentFogRampEnd = data.FogRampEnd;
                wx.currentFogHeightFalloff = data.FogHeightFalloff; wx.currentThunder = data.Thunder; wx.currentRainbow = data.Rainbow;
                wx.currentWetness = data.Wetness; wx.currentWetnessSnow = data.WetnessSnow;
                wx.currentClouds = data.Clouds; wx.currentCloudOpacity = data.CloudOpacity; wx.currentMie = data.Mie; wx.currentBrightness = data.Brightness;
                wx.currentRayleigh = data.Rayleigh; wx.currentContrast = data.Contrast; wx.currentDirectionality = data.Directionality; wx.currentAttenuation = data.Attenuation;
                wx.currentCloudBrightness = data.CloudBrightness; wx.currentCloudSharpness = data.CloudSharpness; wx.currentCloudScattering = data.CloudScattering;
                wx.currentCloudColoring = data.CloudColoring; wx.currentCloudSize = data.CloudSize; wx.currentCloudSaturation = data.CloudSaturation;
                wx.targetRain = wx.currentRain; wx.targetWind = wx.currentWind; wx.targetFog = wx.currentFog; wx.targetFogMultiplier = wx.currentFogMultiplier;
                wx.targetFogRampStart = wx.currentFogRampStart; wx.targetFogRampEnd = wx.currentFogRampEnd; wx.targetFogHeightFalloff = wx.currentFogHeightFalloff;
                wx.targetThunder = wx.currentThunder; wx.targetRainbow = wx.currentRainbow; wx.targetWetness = wx.currentWetness; wx.targetWetnessSnow = wx.currentWetnessSnow;
                wx.targetClouds = wx.currentClouds; wx.targetCloudOpacity = wx.currentCloudOpacity; wx.targetMie = wx.currentMie; wx.targetBrightness = wx.currentBrightness;
                wx.targetRayleigh = wx.currentRayleigh; wx.targetContrast = wx.currentContrast; wx.targetDirectionality = wx.currentDirectionality; wx.targetAttenuation = wx.currentAttenuation;
                wx.targetCloudBrightness = wx.currentCloudBrightness; wx.targetCloudSharpness = wx.currentCloudSharpness; wx.targetCloudScattering = wx.currentCloudScattering;
                wx.targetCloudColoring = wx.currentCloudColoring; wx.targetCloudSize = wx.currentCloudSize; wx.targetCloudSaturation = wx.currentCloudSaturation;

                wx.pendingWeatherProfile = wx.currentWeatherProfile ?? "clear";
                if (Mathf.Abs(data.UtcOffsetSeconds) > 1f)
                    _cachedUtcOffsetSeconds = data.UtcOffsetSeconds;
                if (!string.IsNullOrEmpty(data.Timezone))
                    _cachedTimezone = data.Timezone;

                // Persistence only restored numbers + the asset *name*. The engine still has
                // whatever cloud config was last loaded this boot (often Clear_VClouds).
                // Re-apply the volumetric asset and force convars so the sky matches.
                if (config.AllowCloudConfigSwap)
                {
                    string asset = wx.currentCloudConfig;
                    bool dryClear = wx.currentRain < 0.12f
                        && wx.lastWeatherCode < 80
                        && wx.currentWeatherProfile != "storm"
                        && wx.currentWeatherProfile != "rain";
                    if (string.IsNullOrEmpty(asset)
                        || (dryClear && (asset == "Storm_VClouds" || asset == "RainHeavy_VClouds")))
                    {
                        string reconciled = DesiredCloudConfig(wx.currentWeatherProfile, wx.currentRain);
                        if (!string.IsNullOrEmpty(reconciled) && reconciled != asset)
                            Puts($"[Weather] Persist asset {asset} ignored for {wx.currentWeatherProfile} — using {reconciled}");
                        asset = reconciled;
                    }
                    if (!string.IsNullOrEmpty(asset))
                    {
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, $"weather.load_cloud_config {asset}");
                        wx.currentCloudConfig = asset;
                        Puts($"[Weather] Restored cloud asset -> {asset}");
                    }
                }

                // Force next ApplyWeatherToClimate pass to push every convar
                wx.InvalidateAppliedAll();

                wx._weatherInitialized = true;
                Puts($"[Weather] Restored previous state -> {data.Profile} asset={wx.currentCloudConfig}");
            }
            catch { }
        }

        // ==================== WORLD STATS ====================

        private void CollectWorldStats()
        {
            try
            {
                var sky = TOD_Sky.Instance;
                if (sky == null || sky.Cycle == null) return;

                lastWorldHour = sky.Cycle.Hour;

                if (config.TimeSystem != null && config.TimeSystem.Enabled && config.TimeSystem.SyncDateToRealDate)
                {
                    // lastMoonPhase / lastWorldDay already set in SetInGameDate
                }
                else
                {
                    lastWorldDay = (int)sky.Cycle.Day;
                    float effectiveDay = sky.Cycle.Day;
                    if (sky.Cycle.Hour >= 20f) effectiveDay += 1f;
                    float rawPhase = (effectiveDay % 8f) / 8f;
                    lastMoonPhase = rawPhase;
                    lastMoonPhaseName = GetMoonPhaseName(lastMoonPhase);
                    lastMoonIllumination = Mathf.Clamp01(Mathf.Sin(lastMoonPhase * Mathf.PI));
                }

                float hour = lastWorldHour % 24f;
                if (hour < 0f) hour += 24f;
                lastSunAltitude = Mathf.Sin((hour - 6f) / 12f * Mathf.PI) * 70f;
                lastSunAzimuth = (hour / 24f) * 360f;
                lastSunPosition = GetSunPositionDescription(lastWorldHour);
                lastServerUptimeMinutes = Time.realtimeSinceStartup / 60.0;
                SaveWorldStats();
            }
            catch (Exception ex) { Puts($"[World Stats] CollectWorldStats crashed: {ex.Message}"); }
        }

        private string GetMoonPhaseName(float phase)
        {
            if (phase < 0.06f || phase > 0.94f) return "New Moon";
            if (phase < 0.19f) return "Waxing Crescent";
            if (phase < 0.31f) return "First Quarter";
            if (phase < 0.44f) return "Waxing Gibbous";
            if (phase < 0.56f) return "Full Moon";
            if (phase < 0.69f) return "Waning Gibbous";
            if (phase < 0.81f) return "Last Quarter";
            return "Waning Crescent";
        }

        private string GetSunPositionDescription(float hour)
        {
            // Prefer real Open-Meteo solar times when available (Option A)
            if (config.TimeSystem != null && config.TimeSystem.Enabled &&
                config.TimeSystem.UseRealSolarAtmosphere && !SplitDayActive() &&
                _lastSunrise.HasValue && _lastSunset.HasValue && !_isPolarDay && !_isPolarNight)
            {
                DateTime localNow = DateTime.UtcNow.AddSeconds(_cachedUtcOffsetSeconds);
                float factor = GetSolarDayFactor(localNow);
                if (factor <= 0.02f) return "Night";
                if (factor < 0.35f)
                {
                    // Rising or falling — decide by proximity
                    double nowSec = localNow.TimeOfDay.TotalSeconds;
                    double riseSec = _lastSunrise.Value.TimeOfDay.TotalSeconds;
                    double setSec = _lastSunset.Value.TimeOfDay.TotalSeconds;
                    if (Math.Abs(nowSec - riseSec) < Math.Abs(nowSec - setSec))
                        return factor < 0.15f ? "Dawn" : "Sunrise";
                    return factor < 0.15f ? "Dusk" : "Sunset";
                }
                if (factor < 0.55f) return "Morning";
                if (factor > 0.85f)
                {
                    double nowSec = localNow.TimeOfDay.TotalSeconds;
                    double setSec = _lastSunset.Value.TimeOfDay.TotalSeconds;
                    if (nowSec > setSec - 7200) return "Evening"; // within ~2h of sunset
                    return "Afternoon";
                }
                return "Noon";
            }

            // Fallback: classic fixed-hour buckets
            hour = hour % 24f;
            if (hour < 0) hour += 24f;
            if (hour >= 23f || hour < 4f) return "Night";
            if (hour < 5f) return "Dawn";
            if (hour < 7f) return "Sunrise";
            if (hour < 11f) return "Morning";
            if (hour < 13f) return "Noon";
            if (hour < 17f) return "Afternoon";
            if (hour < 19f) return "Evening";
            if (hour < 21f) return "Sunset";
            return "Dusk";
        }


        private class WorldStatsData
        {
            public string note;
            public double inGameHour;
            public string inGameTime;
            public int inGameDay;
            public string realTimezone;
            public double realUtcOffsetHours;
            public bool isPolarDay;
            public bool isPolarNight;
            public double daylightDurationSeconds;
            public string realSunrise;
            public string realSunset;
            public double? solarDayFactor;
            public double moonPhase;
            public string moonPhaseName;
            public double moonIllumination;
            public double sunAltitude;
            public double sunAzimuth;
            public string sunPosition;
            public string weather;
            public double weatherIntensity;
            public double rainIntensity;
            public double thunderIntensity;
            public double windIntensity;
            public double fogIntensity;
            public double serverUptimeMinutes;
            public string serverUptimeFormatted;
            public bool tournamentActive;
            public bool splitDayActive;
            public int splitDayIndex;
            public int inGameDaysPerRealDay;
            public double daytimePercent;
            public double nighttimePercent;
            public double splitClockRate;
            public string lastUpdatedRealWorld;
        }

        // Reused instance so SaveWorldStats does not allocate a new object graph every tick.
        private readonly WorldStatsData _worldStatsScratch = new WorldStatsData();

        private void SaveWorldStats()
        {
            if (!config.CollectWorldStats) return;
            try
            {
                string formattedTime = $"{(int)lastWorldHour:D2}:{(int)((lastWorldHour % 1) * 60):D2}";
                string uptimeFormatted = $"{(int)(lastServerUptimeMinutes / 60)}h {(int)(lastServerUptimeMinutes % 60)}m";
                float solarFactorNow = -1f;
                if (config.TimeSystem != null && config.TimeSystem.Enabled && config.TimeSystem.UseRealSolarAtmosphere)
                {
                    DateTime localNow = DateTime.UtcNow.AddSeconds(_cachedUtcOffsetSeconds);
                    solarFactorNow = GetSolarDayFactor(localNow);
                }

                var worldData = _worldStatsScratch;
                worldData.note = SplitDayActive()
                    ? "SplitDay: X in-game days packed into one real 24h. Date held to the real calendar. Atmosphere follows in-game hour."
                    : "Time values reflect real local time when TimeSystem is enabled. Atmosphere brightness follows real sunrise/sunset when UseRealSolarAtmosphere is true.";
                worldData.inGameHour = Math.Round(lastWorldHour, 2);
                worldData.inGameTime = formattedTime;
                worldData.inGameDay = lastWorldDay;
                worldData.realTimezone = _cachedTimezone;
                worldData.realUtcOffsetHours = Math.Round(_cachedUtcOffsetSeconds / 3600.0, 2);
                worldData.isPolarDay = _isPolarDay;
                worldData.isPolarNight = _isPolarNight;
                worldData.daylightDurationSeconds = Math.Round(_daylightDurationSeconds, 0);
                worldData.realSunrise = _lastSunrise?.ToString("HH:mm") ?? null;
                worldData.realSunset = _lastSunset?.ToString("HH:mm") ?? null;
                worldData.solarDayFactor = solarFactorNow >= 0f ? Math.Round(solarFactorNow, 3) : (double?)null;
                worldData.moonPhase = Math.Round(lastMoonPhase, 3);
                worldData.moonPhaseName = lastMoonPhaseName;
                worldData.moonIllumination = Math.Round(lastMoonIllumination, 3);
                worldData.sunAltitude = Math.Round(lastSunAltitude, 1);
                worldData.sunAzimuth = Math.Round(lastSunAzimuth, 1);
                worldData.sunPosition = lastSunPosition;
                worldData.weather = lastWeather;
                worldData.weatherIntensity = Math.Round(lastWeatherIntensity, 2);
                worldData.rainIntensity = Math.Round(lastRainIntensity, 2);
                worldData.thunderIntensity = Math.Round(lastThunderIntensity, 2);
                worldData.windIntensity = Math.Round(lastWindIntensity, 2);
                worldData.fogIntensity = Math.Round(lastFogIntensity, 2);
                worldData.serverUptimeMinutes = Math.Round(lastServerUptimeMinutes, 1);
                worldData.serverUptimeFormatted = uptimeFormatted;
                worldData.tournamentActive = _tournamentActive;
                worldData.splitDayActive = SplitDayActive();
                worldData.splitDayIndex = lastSplitDayIndex;
                worldData.inGameDaysPerRealDay = SplitDayActive() ? config.TimeSystem.SplitDay.InGameDaysPerRealDay : 1;
                worldData.daytimePercent = SplitDayActive() ? config.TimeSystem.SplitDay.DaytimePercent : 0;
                worldData.nighttimePercent = SplitDayActive() ? config.TimeSystem.SplitDay.NighttimePercent : 0;
                worldData.splitClockRate = SplitDayActive() ? Math.Round(lastSplitClockRate, 2) : 1;
                worldData.lastUpdatedRealWorld = DateTime.UtcNow.ToString("o");
                Interface.Oxide.DataFileSystem.WriteObject("world_stats", worldData);
            }
            catch (Exception ex) { if (config.DebugMode) Puts($"[Debug] Failed to save world_stats: {ex.Message}"); }
        }

        // ==================== COMMANDS ====================

        [ChatCommand("worldstats")]
        void CmdWorldStats(BasePlayer player, string command, string[] args)
        {
            string userId = player?.UserIDString ?? "";
            if (!config.WorldStatsCommandEnabled || !config.CollectWorldStats)
            {
                SendMessage(player, lang.GetMessage("WorldStatsDisabled", this, userId));
                return;
            }
            string formattedTime = $"{(int)lastWorldHour:D2}:{(int)((lastWorldHour % 1) * 60):D2}";
            string uptime = $"{(int)(lastServerUptimeMinutes / 60)}h {(int)(lastServerUptimeMinutes % 60)}m";
            SendMessage(player, lang.GetMessage("WorldStatsHeader", this, userId));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsInGameTime", this, userId), formattedTime, lastWorldDay));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsTimezone", this, userId), _cachedTimezone, _cachedUtcOffsetSeconds / 3600f));
            if (_isPolarDay) SendMessage(player, lang.GetMessage("WorldStatsPolarDay", this, userId));
            if (_isPolarNight) SendMessage(player, lang.GetMessage("WorldStatsPolarNight", this, userId));
            if (_lastSunrise.HasValue && _lastSunset.HasValue)
                SendMessage(player, string.Format(lang.GetMessage("WorldStatsSunriseSunset", this, userId), _lastSunrise.Value, _lastSunset.Value));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsMoonPhase", this, userId), lastMoonPhaseName, lastMoonPhase));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsSunPosition", this, userId), lastSunPosition));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsWeather", this, userId), lastWeather, lastWeatherIntensity));
            SendMessage(player, string.Format(lang.GetMessage("WorldStatsServerUptime", this, userId), uptime));
            if (_tournamentActive) SendMessage(player, lang.GetMessage("WorldStatsTournament", this, userId));
        }

        [ChatCommand("worldtime")]
        private void CmdWorldTime(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player))
            {
                SendMessage(player, lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            string userId = player?.UserIDString;
            if (args == null || args.Length == 0)
            {
                SendMessage(player, lang.GetMessage("WorldTimeStatusHeader", this, userId));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeEnabled", this, userId), config.TimeSystem?.Enabled));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeSync", this, userId), config.TimeSystem?.SyncHourToRealTime, config.TimeSystem?.SyncDateToRealDate));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeSolar", this, userId), config.TimeSystem?.UseRealSolarAtmosphere));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeHourDay", this, userId), lastWorldHour, lastWorldDay));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeMoon", this, userId), lastMoonPhaseName, lastMoonPhase));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeTzOffset", this, userId), _cachedTimezone, _cachedUtcOffsetSeconds / 3600f));
                if (_lastSunrise.HasValue && _lastSunset.HasValue)
                    SendMessage(player, string.Format(lang.GetMessage("WorldTimeSunTimes", this, userId), _lastSunrise.Value, _lastSunset.Value));
                float sf = GetSolarDayFactor(DateTime.UtcNow.AddSeconds(_cachedUtcOffsetSeconds));
                if (sf >= 0f) SendMessage(player, string.Format(lang.GetMessage("WorldTimeSolarFactor", this, userId), sf));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimePolar", this, userId), _isPolarDay, _isPolarNight));
                SendMessage(player, string.Format(lang.GetMessage("WorldTimeTournament", this, userId), _tournamentActive));
                if (config.TimeSystem?.SplitDay != null)
                {
                    var sd = config.TimeSystem.SplitDay;
                    SendMessage(player, string.Format(lang.GetMessage("WorldTimeSplit", this, userId),
                        sd.Enabled, sd.InGameDaysPerRealDay, sd.DaytimePercent, sd.NighttimePercent,
                        sd.DayStartHour, sd.DayEndHour, lastSplitDayIndex, lastSplitClockRate));
                }
                return;
            }

            switch (args[0].ToLower())
            {
                case "tournament":
                    if (args.Length < 2) { SendMessage(player, lang.GetMessage("WorldTimeUsageTournament", this, userId)); return; }
                    bool on = args[1].ToLower() == "on" || args[1] == "1" || args[1].ToLower() == "true";
                    _tournamentActive = on;
                    if (config.Tournament != null) config.Tournament.Enabled = on;
                    SendMessage(player, lang.GetMessage(on ? "WorldTimeTournamentOn" : "WorldTimeTournamentOff", this, userId));
                    if (!on) SyncRealTimeAndDate();
                    break;
                case "freeze":
                    if (args.Length < 2 || !float.TryParse(args[1], out float h)) { SendMessage(player, lang.GetMessage("WorldTimeUsageFreeze", this, userId)); return; }
                    if (config.Tournament == null) config.Tournament = new TournamentConfig();
                    config.Tournament.Enabled = true;
                    config.Tournament.FreezeTimeAtHour = Mathf.Clamp(h, 0f, 24f);
                    _tournamentActive = true;
                    TOD_Sky.Instance.Cycle.Hour = config.Tournament.FreezeTimeAtHour;
                    SendMessage(player, string.Format(lang.GetMessage("WorldTimeFrozen", this, userId), config.Tournament.FreezeTimeAtHour));
                    break;
                case "fullmoon":
                    if (config.TimeSystem == null) config.TimeSystem = new TimeSystemConfig();
                    config.TimeSystem.MoonMode = "ForceFull";
                    ApplyMoonMode();
                    SendMessage(player, lang.GetMessage("WorldTimeFullMoon", this, userId));
                    break;
                case "split":
                    if (config.TimeSystem == null) config.TimeSystem = new TimeSystemConfig();
                    if (config.TimeSystem.SplitDay == null) config.TimeSystem.SplitDay = new SplitDayConfig();
                    if (args.Length < 2)
                    {
                        SendMessage(player, lang.GetMessage("WorldTimeUsageSplit", this, userId));
                        return;
                    }
                    switch (args[1].ToLower())
                    {
                        case "on":
                        case "1":
                        case "true":
                            config.TimeSystem.SplitDay.Enabled = true;
                            config.TimeSystem.Enabled = true;
                            config.TimeSystem.SyncDateToRealDate = true;
                            SaveConfig();
                            StartTimeSystem();
                            SyncRealTimeAndDate();
                            SendMessage(player, lang.GetMessage("WorldTimeSplitOn", this, userId));
                            break;
                        case "off":
                        case "0":
                        case "false":
                            config.TimeSystem.SplitDay.Enabled = false;
                            SaveConfig();
                            StartTimeSystem();
                            SyncRealTimeAndDate();
                            SendMessage(player, lang.GetMessage("WorldTimeSplitOff", this, userId));
                            break;
                        case "days":
                            if (args.Length < 3 || !int.TryParse(args[2], out int d))
                            {
                                SendMessage(player, lang.GetMessage("WorldTimeUsageSplit", this, userId));
                                return;
                            }
                            config.TimeSystem.SplitDay.InGameDaysPerRealDay = Mathf.Clamp(d, 1, 24);
                            SaveConfig();
                            StartTimeSystem();
                            SyncRealTimeAndDate();
                            SendMessage(player, string.Format(lang.GetMessage("WorldTimeSplitDays", this, userId), config.TimeSystem.SplitDay.InGameDaysPerRealDay));
                            break;
                        case "daypct":
                            if (args.Length < 3 || !float.TryParse(args[2], out float dp))
                            {
                                SendMessage(player, lang.GetMessage("WorldTimeUsageSplit", this, userId));
                                return;
                            }
                            dp = Mathf.Clamp(dp, 1f, 99f);
                            config.TimeSystem.SplitDay.DaytimePercent = dp;
                            config.TimeSystem.SplitDay.NighttimePercent = 100f - dp;
                            SaveConfig();
                            SyncRealTimeAndDate();
                            SendMessage(player, string.Format(lang.GetMessage("WorldTimeSplitPct", this, userId), dp, 100f - dp));
                            break;
                        default:
                            SendMessage(player, lang.GetMessage("WorldTimeUsageSplit", this, userId));
                            break;
                    }
                    break;
                case "real":
                    _tournamentActive = false;
                    if (config.Tournament != null) config.Tournament.Enabled = false;
                    if (config.TimeSystem != null)
                    {
                        config.TimeSystem.MoonMode = "Real";
                        config.TimeSystem.SyncHourToRealTime = true;
                        config.TimeSystem.SyncDateToRealDate = true;
                        if (config.TimeSystem.SplitDay != null)
                            config.TimeSystem.SplitDay.Enabled = false;
                    }
                    SaveConfig();
                    StartTimeSystem();
                    SyncRealTimeAndDate();
                    SendMessage(player, lang.GetMessage("WorldTimeReal", this, userId));
                    break;
                case "status":
                    CmdWorldTime(player, command, new string[0]);
                    break;
                default:
                    SendMessage(player, lang.GetMessage("WorldTimeCommands", this, userId));
                    break;
            }
        }

        [ChatCommand("weatherreload")]
        private void CmdWeatherReload(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player)) { SendMessage(player, lang.GetMessage("WeatherNeedAdmin", this, player.UserIDString)); return; }
            ForceWeatherPoll(player);
        }

        [ConsoleCommand("livestats.weatherreload")]
        private void ConWeatherReload(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player() != null && !HasAdmin(arg.Player()))
            {
                arg.ReplyWith("livestatsworld.admin required");
                return;
            }
            ForceWeatherPoll(arg.Player());
            arg.ReplyWith("Weather poll requested.");
        }

        private void ForceWeatherPoll(BasePlayer player)
        {
            if (!config.UseLocalWeather)
            {
                string off = "UseLocalWeather is off — nothing to poll.";
                Puts($"[Weather] {off}");
                if (player != null) SendMessage(player, off);
                return;
            }
            Puts("[Weather] Manual poll requested.");
            if (player != null) SendMessage(player, "Polling Open-Meteo…");
            SyncLocalWeather();
        }

        [ChatCommand("weatherstatus")]
        private void CmdWeatherStatus(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player)) { SendMessage(player, lang.GetMessage("WeatherNeedAdmin", this, player.UserIDString)); return; }
            string report = string.Format(lang.GetMessage("WeatherStatusLine", this, player?.UserIDString),
                wx.pendingWeatherProfile, wx.currentCloudConfig, wx.currentRain.ToString("F2"), wx.currentClouds.ToString("F2"),
                wx.currentCcn.ToString("F2"), wx._dynamicBlendSeconds.ToString("F0"), _weatherAnchors.Count);
            Puts(report);
            if (player != null) SendMessage(player, report);
        }

        [ChatCommand("weatherseed")]
        private void CmdWeatherSeed(BasePlayer player, string command, string[] args)
        {
            if (player != null && !HasAdmin(player)) { SendMessage(player, lang.GetMessage("WeatherNeedAdmin", this, player.UserIDString)); return; }
            if (!config.SeedCommandEnabled) { if (player != null) SendMessage(player, lang.GetMessage("WeatherSeedDisabled", this, player.UserIDString)); return; }
            float intensity = 0.45f; float duration = 600f;
            if (args != null && args.Length >= 1) float.TryParse(args[0], out intensity);
            if (args != null && args.Length >= 2) float.TryParse(args[1], out duration);
            intensity = Mathf.Clamp01(intensity); duration = Mathf.Clamp(duration, 30f, 3600f);
            wx._seedBias = intensity; wx._seedBiasEnd = Time.realtimeSinceStartup + duration;
            string msg = string.Format(lang.GetMessage("WeatherSeedLine", this, player?.UserIDString), intensity.ToString("F2"), duration.ToString("F0"));
            Puts(msg); if (player != null) SendMessage(player, msg);
        }

        [ConsoleCommand("livestats.weatherstatus")]
        private void ConWeatherStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"label={wx.pendingWeatherProfile} asset={wx.currentCloudConfig} rain={wx.currentRain:F2} clouds={wx.currentClouds:F2} opacity={wx.currentCloudOpacity:F2} atten={wx.currentAttenuation:F2} CCN={wx.currentCcn:F2} anchors={_weatherAnchors.Count}");
        }

        [ConsoleCommand("livestats.weatherseed")]
        private void ConWeatherSeed(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player() != null && !HasAdmin(arg.Player())) { arg.ReplyWith(lang.GetMessage("PermissionDenied", this, arg.Player().UserIDString)); return; }
            float intensity = arg.Args != null && arg.Args.Length >= 1 && float.TryParse(arg.Args[0], out float i) ? i : 0.45f;
            float duration = arg.Args != null && arg.Args.Length >= 2 && float.TryParse(arg.Args[1], out float d) ? d : 600f;
            intensity = Mathf.Clamp01(intensity); duration = Mathf.Clamp(duration, 30f, 3600f);
            wx._seedBias = intensity; wx._seedBiasEnd = Time.realtimeSinceStartup + duration;
            string uid = arg.Player() != null ? arg.Player().UserIDString : null;
            arg.ReplyWith(string.Format(lang.GetMessage("WeatherSeedConsole", this, uid), intensity.ToString("F2"), duration.ToString("F0")));
        }

        // ==================== CONFIG ====================

        class ConfigData
        {
            [JsonProperty("CollectWorldStats")] public bool CollectWorldStats { get; set; } = true;
            [JsonProperty("WorldStatsUpdateInterval")] public float WorldStatsUpdateInterval { get; set; } = 10f;
            [JsonProperty("WorldStatsCommandEnabled")] public bool WorldStatsCommandEnabled { get; set; } = true;

            /// <summary>OFF by default. When true, drives in-game weather from Open-Meteo. Conflicts with other weather plugins.</summary>
            [JsonProperty("UseLocalWeather")] public bool UseLocalWeather { get; set; } = false;
            [JsonProperty("LocalWeatherUpdateIntervalMinutes")] public int LocalWeatherUpdateIntervalMinutes { get; set; } = 15;
            [JsonProperty("LocalWeatherLatitude")] public double LocalWeatherLatitude { get; set; } = 39.95;
            [JsonProperty("LocalWeatherLongitude")] public double LocalWeatherLongitude { get; set; } = -75.16;

            [JsonProperty("WeatherBlendSeconds")] public float WeatherBlendSeconds { get; set; } = 50f;
            [JsonProperty("WeatherBlendInterval")] public float WeatherBlendInterval { get; set; } = 1.5f;
            [JsonProperty("HysteresisRequiredVotes")] public int HysteresisRequiredVotes { get; set; } = 2;
            [JsonProperty("EnableSnow")] public bool EnableSnow { get; set; } = true;
            [JsonProperty("EnableAnticipatory")] public bool EnableAnticipatory { get; set; } = true;
            [JsonProperty("AnticipatoryBias")] public float AnticipatoryBias { get; set; } = 0.18f;
            [JsonProperty("ForecastHorizonDays")] public int ForecastHorizonDays { get; set; } = 3;
            /// <summary>How many 15-minute samples to request from Open-Meteo (24–192). 96 ≈ 24h dense coverage.</summary>
            [JsonProperty("ForecastMinutely15Count")] public int ForecastMinutely15Count { get; set; } = 96;
            /// <summary>Hard cap on minutely anchors kept in memory after subsample (24–192). Default 96.</summary>
            [JsonProperty("MaxMinutelyAnchors")] public int MaxMinutelyAnchors { get; set; } = 96;
            [JsonProperty("EnableLookAhead")] public bool EnableLookAhead { get; set; } = true;
            [JsonProperty("LookAheadMaxBias")] public float LookAheadMaxBias { get; set; } = 0.22f;
            [JsonProperty("PersistWeatherState")] public bool PersistWeatherState { get; set; } = true;

            [JsonProperty("EnableLightningStrikes")] public bool EnableLightningStrikes { get; set; } = true;
            [JsonProperty("LightningSoundOnly")] public bool LightningSoundOnly { get; set; } = true;
            [JsonProperty("LightningFlash")] public bool LightningFlash { get; set; } = true;
            /// <summary>Mean first-stroke length in seconds. Actual strokes are randomized around this (not a fixed loop).</summary>
            [JsonProperty("LightningFlashDuration")] public float LightningFlashDuration { get; set; } = 0.08f;
            [JsonProperty("LightningMinInterval")] public float LightningMinInterval { get; set; } = 4f;
            [JsonProperty("LightningMaxInterval")] public float LightningMaxInterval { get; set; } = 18f;
            [JsonProperty("LightningThunderThreshold")] public float LightningThunderThreshold { get; set; } = 0.45f;
            [JsonProperty("LightningEffectPrefab")] public string LightningEffectPrefab { get; set; } = "";
            [JsonProperty("EnableRealLightning")] public bool EnableRealLightning { get; set; } = false;

            [JsonProperty("EnableCcnPhysics")] public bool EnableCcnPhysics { get; set; } = true;
            [JsonProperty("EnableInstability")] public bool EnableInstability { get; set; } = true;
            [JsonProperty("CcnBaseLevel")] public float CcnBaseLevel { get; set; } = 0.35f;
            [JsonProperty("CcnWashoutRate")] public float CcnWashoutRate { get; set; } = 0.35f;
            [JsonProperty("CcnDustRate")] public float CcnDustRate { get; set; } = 0.20f;
            [JsonProperty("SeedCommandEnabled")] public bool SeedCommandEnabled { get; set; } = true;

            [JsonProperty("AllowCloudConfigSwap")] public bool AllowCloudConfigSwap { get; set; } = true;
            [JsonProperty("PreferOvercastOnly")] public bool PreferOvercastOnly { get; set; } = false;
            /// <summary>When true, load Fog_VClouds when Open-Meteo low cloud dominates mid/high (stratus / low ceiling), not only on WMO fog codes.</summary>
            [JsonProperty("UseFogCloudsForLowDeck")] public bool UseFogCloudsForLowDeck { get; set; } = true;
            /// <summary>Minimum low-cloud fraction (0–1) before a sky counts as a low-altitude deck.</summary>
            [JsonProperty("LowDeckCloudMin")] public float LowDeckCloudMin { get; set; } = 0.50f;
            /// <summary>When true, pick the stock VCloud asset and slider vector from Open-Meteo low/mid/high + CAPE instead of WMO profile alone.</summary>
            [JsonProperty("UseDeckMixer")] public bool UseDeckMixer { get; set; } = true;
            /// <summary>Total seconds for cloud-asset crossfade (fade-out + short hold + fade-in). 5–8 recommended. Driven at 10 Hz so coverage/opacity ease instead of snapping on the weather-blend timer.</summary>
            [JsonProperty("CloudSwapDissolveSeconds")] public float CloudSwapDissolveSeconds { get; set; } = 6.0f;

            [JsonProperty("EnableDust")] public bool EnableDust { get; set; } = true;
            [JsonProperty("DustHumidityMax")] public float DustHumidityMax { get; set; } = 35f;
            [JsonProperty("DustWindMinKmh")] public float DustWindMinKmh { get; set; } = 28f;
            [JsonProperty("DustVisibilityMaxM")] public float DustVisibilityMaxM { get; set; } = 8000f;

            /// <summary>
            /// When true, promote to "storm" profile (and Storm_VClouds) using CAPE, pressure tendency,
            /// instability, gusts and look-ahead even when Open-Meteo weather_code is still &lt; 95.
            /// Helps when the model under-reports real thunderstorms.
            /// </summary>
            [JsonProperty("EnableAggressiveStormDetection")] public bool EnableAggressiveStormDetection { get; set; } = true;
            /// <summary>CAPE (J/kg) above which storm promotion becomes likely (with other support signals).</summary>
            [JsonProperty("StormCapeThreshold")] public float StormCapeThreshold { get; set; } = 900f;
            /// <summary>Very high CAPE threshold — can promote with only moderate precip / instability.</summary>
            [JsonProperty("StormVeryHighCape")] public float StormVeryHighCape { get; set; } = 1400f;
            /// <summary>
            /// When true, lightning may fire with little/no rain if CAPE, instability, gusts
            /// or a storm profile already produced enough thunder. Default true.
            /// </summary>
            [JsonProperty("AllowDryLightning")] public bool AllowDryLightning { get; set; } = true;
            /// <summary>
            /// When true, invent a rain floor if we promote a storm without WMO 95+
            /// (old v1.1.15 behaviour). Default false — keep API precip and allow dry lightning.
            /// </summary>
            [JsonProperty("ForceRainWithSyntheticStorm")] public bool ForceRainWithSyntheticStorm { get; set; } = false;
            /// <summary>
            /// When true, weather.* convar writes still apply in-game but do not echo
            /// "weather.atmosphere_brightness: 0.80" into the dedicated server log.
            /// Unchanged values are never written either way. Default false.
            /// </summary>
            [JsonProperty("QuietWeatherConvars")] public bool QuietWeatherConvars { get; set; } = false;

            [JsonProperty("Lighting")] public WeatherLightingConfig Lighting { get; set; } = new WeatherLightingConfig();

            [JsonProperty("TimeSystem")] public TimeSystemConfig TimeSystem { get; set; } = new TimeSystemConfig();
            [JsonProperty("Tournament")] public TournamentConfig Tournament { get; set; } = new TournamentConfig();

            [JsonProperty("DebugMode")] public bool DebugMode { get; set; } = false;
        }

        public class TimeSystemConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = false;
            [JsonProperty("SyncHourToRealTime")] public bool SyncHourToRealTime { get; set; } = true;
            [JsonProperty("SyncDateToRealDate")] public bool SyncDateToRealDate { get; set; } = true;
            [JsonProperty("UpdateIntervalSeconds")] public float UpdateIntervalSeconds { get; set; } = 10f;
            [JsonProperty("MoonMode")] public string MoonMode { get; set; } = "Real"; // Real | ForceFull | ForceNew | Custom
            [JsonProperty("CustomMoonPhase")] public float CustomMoonPhase { get; set; } = 0.5f;

            /// <summary>
            /// When true, briefly kill vanilla cargo plane / patrol heli / cargo ship / CH47
            /// that spawn right after a forced time/date change (catch-up events).
            /// Default false for coexistence with other event plugins. Only applies while TimeSystem is enabled.
            /// </summary>
            [JsonProperty("SuppressCatchUpEvents")] public bool SuppressCatchUpEvents { get; set; } = false;
            /// <summary>Seconds to suppress catch-up events after a forced time/date write.</summary>
            [JsonProperty("SuppressCatchUpSeconds")] public float SuppressCatchUpSeconds { get; set; } = 15f;

            // Option A: keep real 24h clock, drive atmosphere brightness/contrast from
            // real Open-Meteo sunrise/sunset instead of fixed TOD hour thresholds.
            [JsonProperty("UseRealSolarAtmosphere")] public bool UseRealSolarAtmosphere { get; set; } = true;
            // Half-width of the dawn/dusk brightness ramp in minutes (full transition ≈ 2× this).
            [JsonProperty("DawnDuskMinutes")] public float DawnDuskMinutes { get; set; } = 40f;
            // Multiplier applied to atmosphere brightness at full night (0.6–0.9 typical).
            [JsonProperty("NightBrightnessScale")] public float NightBrightnessScale { get; set; } = 0.78f;

            [JsonProperty("Polar")] public PolarConfig Polar { get; set; } = new PolarConfig();
            [JsonProperty("SplitDay")] public SplitDayConfig SplitDay { get; set; } = new SplitDayConfig();
        }

        public class SplitDayConfig
        {
            /// <summary>
            /// Pack this many in-game 00:00–24:00 cycles into one real local 24h day.
            /// Real AlignRealHour (default midnight) always maps to in-game 00:00 of cycle 0.
            /// </summary>
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = false;
            [JsonProperty("InGameDaysPerRealDay")] public int InGameDaysPerRealDay { get; set; } = 3;
            /// <summary>Share of each compressed day spent in the daytime hour window. Normalized with NighttimePercent.</summary>
            [JsonProperty("DaytimePercent")] public float DaytimePercent { get; set; } = 70f;
            /// <summary>Share of each compressed day spent in the night hour window (before DayStart and after DayEnd).</summary>
            [JsonProperty("NighttimePercent")] public float NighttimePercent { get; set; } = 30f;
            /// <summary>In-game hour where daytime begins (clock runs at the day rate from here to DayEndHour).</summary>
            [JsonProperty("DayStartHour")] public float DayStartHour { get; set; } = 6f;
            /// <summary>In-game hour where nighttime begins.</summary>
            [JsonProperty("DayEndHour")] public float DayEndHour { get; set; } = 20f;
            /// <summary>Real local hour that lines up with in-game 00:00. 0 = real midnight.</summary>
            [JsonProperty("AlignRealHour")] public float AlignRealHour { get; set; } = 0f;
            /// <summary>How often the owned clock is aligned to real time. With DynamicSpeedScaling the engine runs between ticks.</summary>
            [JsonProperty("UpdateIntervalSeconds")] public float UpdateIntervalSeconds { get; set; } = 2f;
            /// <summary>
            /// When true, TOD ProgressTime runs and DayLengthInMinutes is scaled to the
            /// current day/night rate so the sun eases instead of stepping. Hour is only
            /// rewritten when it drifts off the real-midnight lock.
            /// </summary>
            [JsonProperty("DynamicSpeedScaling")] public bool DynamicSpeedScaling { get; set; } = true;
            /// <summary>Game-hour half-width of the dawn/dusk speed blend. 0 = hard rate switch.</summary>
            [JsonProperty("RateBlendHours")] public float RateBlendHours { get; set; } = 0.75f;
            /// <summary>Snap Cycle.Hour back when it drifts this many game hours from the mapped lock.</summary>
            [JsonProperty("HourDriftCorrection")] public float HourDriftCorrection { get; set; } = 0.04f;
        }

        public class WeatherLightingConfig
        {
            /// <summary>Master switch for the extra weather.* light/vcloud multipliers. 1.0 is identity; never write -1 (that hands the channel back to vanilla).</summary>
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("DriveVCloudColorScale")] public bool DriveVCloudColorScale { get; set; } = true;
            [JsonProperty("DriveLightMultipliers")] public bool DriveLightMultipliers { get; set; } = true;
            [JsonProperty("DriveMeshBrightness")] public bool DriveMeshBrightness { get; set; } = true;
            [JsonProperty("DriveReflection")] public bool DriveReflection { get; set; } = true;
            [JsonProperty("NightlightEnabled")] public bool NightlightEnabled { get; set; } = true;
            [JsonProperty("NightlightBrightness")] public float NightlightBrightness { get; set; } = 0.008f;
            [JsonProperty("NightlightDistance")] public float NightlightDistance { get; set; } = 5f;
            [JsonProperty("NightlightFadeFraction")] public float NightlightFadeFraction { get; set; } = 0.65f;
            /// <summary>Added to nightlight brightness on cloudy nights only.</summary>
            [JsonProperty("NightlightOvercastBoost")] public float NightlightOvercastBoost { get; set; } = 0.004f;
            /// <summary>Night moon-disc multiplier on a clear sky. 0-1. 0.95 bleaches volumetric clouds.</summary>
            [JsonProperty("MoonMeshNightClear")] public float MoonMeshNightClear { get; set; } = 0.42f;
            /// <summary>Night moon-disc multiplier under thick cover. 0-1, should stay below Clear.</summary>
            [JsonProperty("MoonMeshNightOvercast")] public float MoonMeshNightOvercast { get; set; } = 0.22f;
            /// <summary>How hard moonlight paints the vcloud mesh on a clear night. This is the cloud-wash channel.</summary>
            [JsonProperty("VCloudMoonNightClear")] public float VCloudMoonNightClear { get; set; } = 0.38f;
            /// <summary>Moonlight on vclouds under thick cover.</summary>
            [JsonProperty("VCloudMoonNightOvercast")] public float VCloudMoonNightOvercast { get; set; } = 0.16f;
        }

        public class PolarConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = true;
            [JsonProperty("PolarDayMode")] public string PolarDayMode { get; set; } = "PermanentDay";
            [JsonProperty("PolarNightMode")] public string PolarNightMode { get; set; } = "BrightNight";
            [JsonProperty("MinimumNightBrightness")] public float MinimumNightBrightness { get; set; } = 0.35f;
        }

        public class TournamentConfig
        {
            [JsonProperty("Enabled")] public bool Enabled { get; set; } = false;
            [JsonProperty("ForcePermanentDay")] public bool ForcePermanentDay { get; set; } = false;
            [JsonProperty("ForceFullMoon")] public bool ForceFullMoon { get; set; } = false;
            [JsonProperty("FreezeTimeAtHour")] public float FreezeTimeAtHour { get; set; } = -1f;
            [JsonProperty("DisableWeatherChanges")] public bool DisableWeatherChanges { get; set; } = false;
            [JsonProperty("DisableDateProgression")] public bool DisableDateProgression { get; set; } = false;
        }

        private class OpenMeteoResponse
        {
            public float? utc_offset_seconds { get; set; }
            public string timezone { get; set; }
            public CurrentWeather current { get; set; }
            public HourlyWeather hourly { get; set; }
            public MinutelyWeather minutely_15 { get; set; }
            public DailyWeather daily { get; set; }
        }

        private class DailyWeather
        {
            public List<string> time { get; set; }
            public List<string> sunrise { get; set; }
            public List<string> sunset { get; set; }
            public List<float> daylight_duration { get; set; }
        }

        private class CurrentWeather
        {
            public float? temperature_2m { get; set; }
            public float? dew_point_2m { get; set; }
            public float? relative_humidity_2m { get; set; }
            public float? precipitation { get; set; }
            public float? rain { get; set; }
            public float? showers { get; set; }
            public float? snowfall { get; set; }
            public float? precipitation_probability { get; set; }
            public int? weather_code { get; set; }
            public int? cloud_cover { get; set; }
            public int? cloud_cover_low { get; set; }
            public int? cloud_cover_mid { get; set; }
            public int? cloud_cover_high { get; set; }
            public float? surface_pressure { get; set; }
            public float? wind_speed_10m { get; set; }
            public float? wind_gusts_10m { get; set; }
            public float? wind_direction_10m { get; set; }
            public float? visibility { get; set; }
            public int? is_day { get; set; }
            public float? cape { get; set; }
            public float? shortwave_radiation { get; set; }
            public float? soil_moisture_0_to_7cm { get; set; }
        }

        private class HourlyWeather
        {
            public List<string> time { get; set; }
            public List<float?> temperature_2m { get; set; }
            public List<float?> dew_point_2m { get; set; }
            public List<float?> relative_humidity_2m { get; set; }
            public List<float?> precipitation { get; set; }
            public List<float?> rain { get; set; }
            public List<float?> showers { get; set; }
            public List<float?> snowfall { get; set; }
            public List<float?> precipitation_probability { get; set; }
            public List<int?> weather_code { get; set; }
            public List<int?> cloud_cover { get; set; }
            public List<int?> cloud_cover_low { get; set; }
            public List<int?> cloud_cover_mid { get; set; }
            public List<int?> cloud_cover_high { get; set; }
            public List<float?> surface_pressure { get; set; }
            public List<float?> wind_speed_10m { get; set; }
            public List<float?> wind_gusts_10m { get; set; }
            public List<float?> wind_direction_10m { get; set; }
            public List<float?> visibility { get; set; }
            public List<int?> is_day { get; set; }
            public List<float?> cape { get; set; }
            public List<float?> shortwave_radiation { get; set; }
            public List<float?> soil_moisture_0_to_7cm { get; set; }
        }

        private class MinutelyWeather
        {
            public List<string> time { get; set; }
            public List<float?> temperature_2m { get; set; }
            public List<float?> dew_point_2m { get; set; }
            public List<float?> relative_humidity_2m { get; set; }
            public List<float?> precipitation { get; set; }
            public List<float?> rain { get; set; }
            public List<float?> showers { get; set; }
            public List<float?> snowfall { get; set; }
            public List<int?> weather_code { get; set; }
            public List<int?> cloud_cover { get; set; }
            public List<float?> wind_speed_10m { get; set; }
            public List<float?> wind_gusts_10m { get; set; }
            public List<float?> wind_direction_10m { get; set; }
            public List<float?> visibility { get; set; }
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
                if (config == null)
                    throw new Exception("Config object is null");
            }
            catch (Exception ex)
            {
                Puts($"Config file is missing or corrupt ({ex.Message}) - generating a new default config.");
                LoadDefaultConfig();
                return;
            }

            // Soft migration – only fill missing nested objects.
            // Existing user values are NEVER overwritten.
            bool changed = false;

            if (config.TimeSystem == null)
            {
                config.TimeSystem = new TimeSystemConfig();
                changed = true;
            }

            if (config.TimeSystem.Polar == null)
            {
                config.TimeSystem.Polar = new PolarConfig();
                changed = true;
            }

            if (config.TimeSystem.SplitDay == null)
            {
                config.TimeSystem.SplitDay = new SplitDayConfig();
                changed = true;
            }

            if (config.Tournament == null)
            {
                config.Tournament = new TournamentConfig();
                changed = true;
            }

            if (config.Lighting == null)
            {
                config.Lighting = new WeatherLightingConfig();
                changed = true;
            }

            // Soft-migrate new v1.1.12 storm-detection keys when they are still at type defaults
            // (missing from older config JSON). Do not overwrite an intentional false / custom value
            // once the user has saved the config at least once with the new keys present.
            if (config.StormCapeThreshold <= 0f)
            {
                config.StormCapeThreshold = 900f;
                config.StormVeryHighCape = 1400f;
                config.EnableAggressiveStormDetection = true;
                changed = true;
            }

            // Soft-migrate v1.1.14 denser-anchor keys
            if (config.ForecastMinutely15Count <= 0)
            {
                config.ForecastMinutely15Count = 96;
                changed = true;
            }
            if (config.MaxMinutelyAnchors <= 0)
            {
                config.MaxMinutelyAnchors = 96;
                changed = true;
            }

            NormalizeConfig();

            if (changed)
            {
                Puts("Config was missing some sections – filled missing parts with defaults (user values preserved).");
                SaveConfig();
            }
        }

        private void NormalizeConfig()
        {
            if (config == null) return;

            config.WorldStatsUpdateInterval = Mathf.Clamp(config.WorldStatsUpdateInterval, 5f, 300f);
            config.LocalWeatherUpdateIntervalMinutes = Mathf.Clamp(config.LocalWeatherUpdateIntervalMinutes, 5, 180);
            config.WeatherBlendSeconds = Mathf.Clamp(config.WeatherBlendSeconds, 25f, 180f);
            config.WeatherBlendInterval = Mathf.Clamp(config.WeatherBlendInterval, 0.5f, 5f);
            config.HysteresisRequiredVotes = Mathf.Max(1, config.HysteresisRequiredVotes);
            config.ForecastHorizonDays = Mathf.Clamp(config.ForecastHorizonDays, 1, 5);
            config.ForecastMinutely15Count = Mathf.Clamp(config.ForecastMinutely15Count, 24, 96);
            config.MaxMinutelyAnchors = Mathf.Clamp(config.MaxMinutelyAnchors, 24, 192);
            config.LookAheadMaxBias = Mathf.Clamp(config.LookAheadMaxBias, 0f, 0.35f);
            config.AnticipatoryBias = Mathf.Clamp(config.AnticipatoryBias, 0f, 0.4f);
            config.CloudSwapDissolveSeconds = Mathf.Clamp(config.CloudSwapDissolveSeconds, 2f, 20f);
            config.LowDeckCloudMin = Mathf.Clamp01(config.LowDeckCloudMin);
            config.LightningMinInterval = Mathf.Max(1f, config.LightningMinInterval);
            config.LightningMaxInterval = Mathf.Max(config.LightningMinInterval + 1f, config.LightningMaxInterval);
            config.LightningThunderThreshold = Mathf.Clamp01(config.LightningThunderThreshold);
            config.LightningFlashDuration = Mathf.Clamp(config.LightningFlashDuration, 0.04f, 0.22f);
            config.CcnBaseLevel = Mathf.Clamp01(config.CcnBaseLevel);
            config.StormCapeThreshold = Mathf.Max(100f, config.StormCapeThreshold);
            config.StormVeryHighCape = Mathf.Max(config.StormCapeThreshold, config.StormVeryHighCape);

            if (double.IsNaN(config.LocalWeatherLatitude) || config.LocalWeatherLatitude < -90 || config.LocalWeatherLatitude > 90)
                config.LocalWeatherLatitude = 39.95;
            if (double.IsNaN(config.LocalWeatherLongitude) || config.LocalWeatherLongitude < -180 || config.LocalWeatherLongitude > 180)
                config.LocalWeatherLongitude = -75.16;

            if (config.TimeSystem == null)
                config.TimeSystem = new TimeSystemConfig();
            config.TimeSystem.UpdateIntervalSeconds = Mathf.Max(10f, config.TimeSystem.UpdateIntervalSeconds);
            config.TimeSystem.DawnDuskMinutes = Mathf.Clamp(config.TimeSystem.DawnDuskMinutes, 10f, 90f);
            config.TimeSystem.NightBrightnessScale = Mathf.Clamp(config.TimeSystem.NightBrightnessScale, 0.55f, 0.95f);
            config.TimeSystem.SuppressCatchUpSeconds = Mathf.Clamp(config.TimeSystem.SuppressCatchUpSeconds, 1f, 60f);
            config.TimeSystem.CustomMoonPhase = Mathf.Clamp01(config.TimeSystem.CustomMoonPhase);
            string moon = config.TimeSystem.MoonMode ?? "Real";
            if (moon != "Real" && moon != "ForceFull" && moon != "ForceNew" && moon != "Custom")
                config.TimeSystem.MoonMode = "Real";

            if (config.TimeSystem.SplitDay == null)
                config.TimeSystem.SplitDay = new SplitDayConfig();
            var sd = config.TimeSystem.SplitDay;
            sd.InGameDaysPerRealDay = Mathf.Clamp(sd.InGameDaysPerRealDay, 1, 24);
            sd.DaytimePercent = Mathf.Clamp(sd.DaytimePercent, 1f, 99f);
            sd.NighttimePercent = Mathf.Clamp(sd.NighttimePercent, 1f, 99f);
            sd.DayStartHour = Mathf.Clamp(sd.DayStartHour, 0f, 23.9f);
            sd.DayEndHour = Mathf.Clamp(sd.DayEndHour, sd.DayStartHour + 0.1f, 24f);
            sd.AlignRealHour = Mathf.Repeat(sd.AlignRealHour, 24f);
            sd.UpdateIntervalSeconds = Mathf.Clamp(sd.UpdateIntervalSeconds, 1f, 10f);
            sd.RateBlendHours = Mathf.Max(0f, sd.RateBlendHours);
            sd.HourDriftCorrection = Mathf.Max(0.01f, sd.HourDriftCorrection);

            if (config.TimeSystem.Polar == null)
                config.TimeSystem.Polar = new PolarConfig();
            config.TimeSystem.Polar.MinimumNightBrightness = Mathf.Clamp01(config.TimeSystem.Polar.MinimumNightBrightness);

            if (config.Lighting == null)
                config.Lighting = new WeatherLightingConfig();
            var lit = config.Lighting;
            lit.NightlightBrightness = Mathf.Clamp(lit.NightlightBrightness, 0f, 0.05f);
            lit.NightlightDistance = Mathf.Clamp(lit.NightlightDistance, 1f, 20f);
            lit.NightlightFadeFraction = Mathf.Clamp01(lit.NightlightFadeFraction);
            lit.MoonMeshNightClear = Mathf.Clamp01(lit.MoonMeshNightClear);
            lit.MoonMeshNightOvercast = Mathf.Clamp01(lit.MoonMeshNightOvercast);
            lit.VCloudMoonNightClear = Mathf.Clamp01(lit.VCloudMoonNightClear);
            lit.VCloudMoonNightOvercast = Mathf.Clamp01(lit.VCloudMoonNightOvercast);

            if (config.Tournament == null)
                config.Tournament = new TournamentConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return true;
            string id = player.UserIDString;
            return permission.UserHasPermission(id, AdminPermission) || permission.UserHasPermission(id, HostAdminPermission);
        }

        private void SendMessage(BasePlayer player, string message)
        {
            if (player != null) player.ChatMessage(message);
            else Puts(message);
        }

        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command.",
                ["WorldStatsDisabled"] = "World stats collection is disabled.",
                ["WorldStatsHeader"] = "<size=20><color=#ff0000>World Stats</color></size>",
                ["WorldStatsInGameTime"] = "In-Game Time: {0} (Day {1})",
                ["WorldStatsMoonPhase"] = "Moon Phase: {0} ({1:F2})",
                ["WorldStatsSunPosition"] = "Sun Position: {0}",
                ["WorldStatsWeather"] = "Weather: {0} (Intensity: {1:F2})",
                ["WorldStatsServerUptime"] = "Server Uptime: {0}",
                ["WorldStatsNote"] = "Note: When TimeSystem is enabled this reflects real local time of the configured coordinates.",
                ["WeatherSeedDisabled"] = "Cloud seeding command is disabled in config.",
                ["WeatherNeedAdmin"] = "You need livestatsworld.admin (or livestats.admin).",
                ["WeatherReload"] = "Polling Open-Meteo…",
                ["WorldStatsTimezone"] = "Timezone: {0} (UTC{1:+0.0;-0.0})",
                ["WorldStatsPolarDay"] = "<color=#ffaa00>POLAR DAY active</color>",
                ["WorldStatsPolarNight"] = "<color=#8888ff>POLAR NIGHT active</color>",
                ["WorldStatsSunriseSunset"] = "Real sunrise: {0:HH:mm}  |  sunset: {1:HH:mm}",
                ["WorldStatsTournament"] = "<color=#ff5555>TOURNAMENT MODE ACTIVE</color>",
                ["WorldTimeStatusHeader"] = "<color=#00ffaa>WorldTime Status</color>",
                ["WorldTimeEnabled"] = "TimeSystem Enabled: {0}",
                ["WorldTimeSync"] = "Sync Hour: {0} | Sync Date: {1}",
                ["WorldTimeSolar"] = "Real Solar Atmosphere: {0}",
                ["WorldTimeHourDay"] = "Current Hour: {0:F2} | Day: {1}",
                ["WorldTimeMoon"] = "Moon: {0} ({1:F2})",
                ["WorldTimeTzOffset"] = "Timezone: {0} | Offset: {1:F1}h",
                ["WorldTimeSunTimes"] = "Sunrise: {0:HH:mm}  Sunset: {1:HH:mm}",
                ["WorldTimeSolarFactor"] = "Solar day factor: {0:F2} (0=night, 1=day)",
                ["WorldTimePolar"] = "Polar Day: {0} | Polar Night: {1}",
                ["WorldTimeTournament"] = "Tournament: {0}",
                ["WorldTimeUsageTournament"] = "Usage: /worldtime tournament <on|off>",
                ["WorldTimeTournamentOn"] = "<color=#ff5555>Tournament mode ENABLED</color>",
                ["WorldTimeTournamentOff"] = "<color=#55ff55>Tournament mode DISABLED -- returning to real time</color>",
                ["WorldTimeUsageFreeze"] = "Usage: /worldtime freeze <hour 0-24>",
                ["WorldTimeFrozen"] = "Time frozen at {0:F1}",
                ["WorldTimeFullMoon"] = "Forced Full Moon.",
                ["WorldTimeReal"] = "Returned to full real-time + real-date sync.",
                ["WorldTimeCommands"] = "Commands: status | tournament <on|off> | freeze <hour> | fullmoon | split <on|off|days N|daypct N> | real",
                ["WorldTimeSplit"] = "SplitDay: {0} | {1} in-game days / real day | day {2:F0}% night {3:F0}% | window {4:F1}-{5:F1} | cycle {6} | rate {7:F2}x",
                ["WorldTimeUsageSplit"] = "Usage: /worldtime split <on|off|days 1-24|daypct 1-99>",
                ["WorldTimeSplitOn"] = "<color=#55ff55>SplitDay ENABLED</color> — X in-game days packed into one real 24h. Date held to the real calendar.",
                ["WorldTimeSplitOff"] = "<color=#ffaa55>SplitDay DISABLED</color>",
                ["WorldTimeSplitDays"] = "SplitDay in-game days per real day: {0}",
                ["WorldTimeSplitPct"] = "SplitDay daytime {0:F0}% / nighttime {1:F0}%",
                ["WeatherStatusLine"] = "[Weather] label={0} asset={1} rain={2} clouds={3} CCN={4} blend~{5}s anchors={6}",
                ["WeatherSeedLine"] = "[Weather] Cloud seed intensity {0} for {1}s",
                ["PermissionDenied"] = "Permission denied.",
                ["WeatherSeedConsole"] = "Seed intensity={0} duration={1}s",
            }, this);
        }
    }
}
