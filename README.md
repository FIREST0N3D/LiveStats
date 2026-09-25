# LiveStats

Oxide plugins for Rust dedicated servers.

**LiveStats** tracks players, NPCs, and animals, writes a live JSON snapshot for a dashboard, and handles wipe detection.

**LiveStatsWorld** is an optional extension: real-world date/time, moon, solar lighting, and Open-Meteo weather. Weather and the time system stay **off** until you turn them on.

Author in `[Info]`: **FiREST0N3D**  
Server this was built for: [rustjawn.com](https://www.rustjawn.com)

| Plugin | Version | Requires |
|--------|---------|----------|
| LiveStats | 1.9.816 | Oxide / Rust dedicated |
| LiveStatsWorld | 1.1.48 | LiveStats |

Drop the `.cs` files in `oxide/plugins`. Oxide compiles them on load. **Load LiveStats first.**

---

## What LiveStats does

- Per-player kills, deaths, headshots, PvP / NPC / animal breakdowns
- Kill streaks (players + NPCs + animals, no death in between)
- Personal PvP log (who you killed / who killed you)
- Server kill feed with optional grid + headshot tag
- NPC and animal type stats (Heavy Scientist, bear, crocodile, livestock, …)
- Environmental deaths (drown, fall, starve, freeze, heat, rad) — drown is **not** counted as suicide
- Dual wipe detection: Oxide `OnNewSave` plus a stored map identity (`level|seed|worldsize`)
- Optional AFK warning + kick (off by default)
- Writes `oxide/data/live_stats.json` on a timer for an external dashboard (online players, streaks, wipe day, recent deaths)
- Language packs under `oxide/lang/<code>/LiveStats.json`, with `/lslang` per player

LiveStats does **not** spawn events, change weather, or move NPCs. That is the rest of the suite (not in this first drop).

---

## What LiveStatsWorld does

World only starts after it sees LiveStats. If LiveStats is missing it logs an error and sits idle.

**Off by default (safe next to other plugins):**

- `UseLocalWeather` — drive `weather.*` from [Open-Meteo](https://open-meteo.com)
- `TimeSystem.Enabled` — lock in-game date (and optionally hour) to real local time at your coordinates

**v1.1.48:** high-only cirrus (low/mid cloud ≈ 0, high cloud only) no longer holds the Overcast cloud mesh. Thin cirrus stays on a clearer deck while the blend still targets RainMild/Clear from the forecast.

**When you enable them:**

- Open-Meteo poll (default every 15 minutes) + smooth blend into Rust weather convars
- Cloud mesh dissolve (Clear → RainMild → Overcast) instead of snapping the sky
- Look-ahead rain / storm promotion from CAPE + WMO codes
- Optional lightning (sound + brightness flash; real bolt prefab off by default)
- Real sunrise/sunset atmosphere if `UseRealSolarAtmosphere` is on
- Polar day/night handling for high latitudes
- Optional split-day: pack N in-game days into one real day
- Optional short suppress of vanilla cargo/heli/CH47 right after a forced time jump (`SuppressCatchUpEvents`, default **false**)
- Writes world snapshot fields for the same dashboard LiveStats already feeds

**Conflicts (only after you enable TimeSystem or UseLocalWeather):**  
TimeOfDay, RealTime, other weather controllers, and event plugins that spawn on sudden time jumps.

---

## Install

1. Rust dedicated server with Oxide 2.
2. Copy `LiveStats.cs` into `oxide/plugins`.
3. Wait until the console prints `LiveStats v1.9.816 loaded`.
4. Copy `LiveStatsWorld.cs` into `oxide/plugins`.
5. Console should print that the LiveStats host was detected.
6. Edit the generated configs under `oxide/config/` **before** turning weather or time on.

First World boot writes whatever defaults are in the `.cs`. The current source defaults latitude/longitude to **39.95, -75.16** (Philadelphia). Change those to your server before `UseLocalWeather true`.

---

## Permissions

| Permission | Plugin | Who |
|------------|--------|-----|
| `livestats.admin` | LiveStats | `/clearallstats`, `/livestats cleanup`, World admin fallback |
| `livestats.idle.bypass` | LiveStats | Skip AFK kick (only if idle kick is enabled) |
| `livestatsworld.admin` | LiveStatsWorld | `/worldtime`, `/weatherreload`, `/weatherseed`, `/weatherstatus` |

Grant:

```text
oxide.grant group admin livestats.admin
oxide.grant group admin livestatsworld.admin
```

---

## Chat commands — LiveStats

| Command | Who | What |
|---------|-----|------|
| `/mystats` | everyone | Your K/D, streaks, NPC/animal breakdown, minutes played |
| `/top` | everyone | Top 10 killers |
| `/killfeed` | everyone | Recent server deaths |
| `/pvplog` | everyone | Your personal PvP kills and deaths |
| `/npcstats` | everyone | NPC types by kills |
| `/animalstats` | everyone | Animals by deaths (hunter board) |
| `/envstats` | everyone | Environmental death counts |
| `/livestats` | everyone | Plugin status; `cleanup` is admin-only |
| `/livestats cleanup` | admin | Prune stale player rows |
| `/clearallstats` | admin | Wipe all tracked stats |
| `/lslang <code>` | everyone | Set your Oxide lang folder (`en`, `es`, `pt-BR`, …) |
| `/lstestlang` | everyone | Debug which pack Oxide resolved |

Each command can be disabled in `oxide/config/LiveStats.json` (`MyStatsCommandEnabled`, `TopCommandEnabled`, …).

---

## Chat / console commands — LiveStatsWorld

| Command | Who | What |
|---------|-----|------|
| `/worldstats` | everyone | In-game clock, moon, sun, weather, uptime |
| `/worldtime` | admin | Status and time-system controls |
| `/worldtime status` | admin | Same as `/worldtime` with no args |
| `/worldtime tournament on\|off` | admin | Freeze a “event weekend” clock |
| `/worldtime freeze <0-24>` | admin | Hold the hour |
| `/worldtime fullmoon` | admin | Force full moon |
| `/worldtime split on\|off\|days N\|daypct N` | admin | Packed in-game days per real day |
| `/worldtime real` | admin | Back to real date/hour sync |
| `/weatherreload` | admin | Poll Open-Meteo now |
| `/weatherstatus` | admin | Current profile, rain, clouds, blend, anchors |
| `/weatherseed <0-1> [seconds]` | admin | Nudge cloud intensity (if `SeedCommandEnabled`) |

Console aliases (F1 / RCON):

```text
livestats.weatherreload
livestats.weatherstatus
livestats.weatherseed
```

---

## Config — LiveStats (`oxide/config/LiveStats.json`)

| Key | Default | Notes |
|-----|---------|--------|
| `UpdateInterval` | `10` | Seconds between `live_stats.json` writes |
| `MaxDeaths` | `8` | Kill-feed ring buffer |
| `DeathDedupeWindow` | `3` | Ignore double `OnEntityDeath` |
| `PlayerStatsCleanupDays` | `21` | Drop rows not seen this long |
| `EnableKillStreaks` | `true` | |
| `KillStreakStart` | `5` | Chat announce threshold |
| `AnnounceKillStreaks` | `true` | |
| `AnnouncePlayerDeaths` | `true` | Server chat |
| `AnnounceNpcDeaths` / `AnnounceAnimalKills` / `AnnounceAnimalDeaths` / `AnnounceEnvironmentalDeaths` | `true` | Toggle per category |
| `PlayersOnlyEnv` | `true` | Only real players count toward `/envstats` |
| `ClearStatsOnWipe` | `true` | |
| `WipeTimezone` | `Eastern Standard Time` | Windows ID. Linux often `America/New_York` |
| `WipeHour` / `WipeMinute` | `14` / `15` | Local wipe clock used for “day of wipe” |
| `EnableIdleKick` | `false` | |
| `IdleWarningMinutes` | `10` | |
| `IdleKickMinutes` | `15` | |
| `IdleBypassPermission` | `livestats.idle.bypass` | |
| `LanguageSyncFromClient` | `true` | Map Rust client language → Oxide pack |
| `LanguageForce` | `""` | Force one pack for everyone if set |
| `DebugMode` | `false` | Extra `Puts` |
| `CustomKillerNames` | map of prefab → label | Override chat names |

---

## Config — LiveStatsWorld (`oxide/config/LiveStatsWorld.json`)

Leave weather and time **false** until lat/lon are yours.

| Key | Default | Notes |
|-----|---------|--------|
| `UseLocalWeather` | `false` | Master weather switch |
| `LocalWeatherLatitude` | `39.95` | **Change this** |
| `LocalWeatherLongitude` | `-75.16` | **Change this** |
| `LocalWeatherUpdateIntervalMinutes` | `15` | Open-Meteo poll |
| `WeatherBlendSeconds` | `50` | How long a profile fade takes |
| `PersistWeatherState` | `true` | Survive `o.reload` |
| `QuietWeatherConvars` | `false` | Set `true` on public servers to stop `weather.*` log spam |
| `CollectWorldStats` | `true` | Moon/sun/weather snapshot |
| `EnableLightningStrikes` | `true` | |
| `LightningSoundOnly` | `true` | No bolt entity |
| `LightningFlash` | `true` | Brief brightness pop |
| `EnableRealLightning` | `false` | Prefab bolt |
| `TimeSystem.Enabled` | `false` | Master time switch |
| `TimeSystem.SyncHourToRealTime` | `true` | Used only if TimeSystem is on |
| `TimeSystem.SyncDateToRealDate` | `true` | |
| `TimeSystem.UseRealSolarAtmosphere` | `true` | Dawn/dusk from real sunrise |
| `TimeSystem.SuppressCatchUpEvents` | `false` | Kill vanilla cargo/heli/CH47 for a few seconds after a time jump |
| `TimeSystem.SplitDay.Enabled` | `false` | |
| `TimeSystem.SplitDay.InGameDaysPerRealDay` | `3` | |
| `Tournament.Enabled` | `false` | |
| `DebugMode` | `false` | |

Open-Meteo is fetched with Oxide `webrequest` (no API key). The server needs outbound HTTPS.

---

## Data files

| File | Writer | Safe to delete? |
|------|--------|-----------------|
| `oxide/data/live_stats.json` | LiveStats | Yes — rebuilt on the next timer |
| `oxide/data/livestats_wipe.json` (name may vary) | LiveStats | Only if you want wipe identity reset |
| Player / NPC / animal / env JSON under `oxide/data/` | LiveStats | Wipes tracked stats |
| Weather persist file (when `PersistWeatherState`) | LiveStatsWorld | Next poll rebuilds weather |


---

## Dashboard JSON (LiveStats)

`live_stats.json` includes:

- `serverRunning`, `serverName`, map name / seed / size
- Online player list: name, SteamID, session minutes, totals, streaks, `isPlayerDead`, `isSleeping`
- `recentDeaths`
- `wipeDay`, `wipeStartUtc`, timezone + wipe clock
- `timestamp` / `lastUpdateUnix`

Point any site you want at that file. This repo does not include the rustjawn.com frontend.

---

## Localization

LiveStats ships English via `LoadDefaultMessages` and extra packs on disk as `oxide/lang/<code>/LiveStats.json`.

- `/lslang es` — that player only
- `LanguageForce` in config — whole server
- `LanguageSyncFromClient` — uses the Rust client language on connect

World has its own `lang` keys for `/worldstats` and `/worldtime`.

---

## Load / unload notes

- Reloading LiveStats while World is loaded: World pauses, then `OnPluginLoaded` starts it again.
- Unloading LiveStats: World stops timers and restores vanilla `DayLengthInMinutes` if it changed them.
- Unloading LiveStats: `live_stats.json` is marked `serverRunning: false` so a dashboard can show offline.
- Idle input hook is **unsubscribed** when `EnableIdleKick` is false.
- World’s `OnEntitySpawned` catch-up hook is **unsubscribed** unless `SuppressCatchUpEvents` is true.

---

## What this first drop is not

These plugins exist on the production server but are **not** part of this README’s install:

- LiveStatsEvents / LiveStatsEventsNPC / LiveStatsEventsVehicles — custom event engine
- LiveStatsEventFeed — map pins JSON
- LiveStatsSystem — help, board inbox, restart schedule
- MapImageSaver — map image export

They will land in this repo later. They are not required for stats or weather.

---

## License

MIT (see `LICENSE`).  
Rust and the Oxide/uMod framework are owned by their respective authors. This project is not affiliated with Facepunch or uMod.
