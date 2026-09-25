# LiveStats

Oxide plugins for Rust dedicated servers.

**LiveStats** tracks players, NPCs, and animals, writes a live JSON snapshot for a dashboard, and handles wipe detection.

**LiveStatsWorld** is an optional extension: real-world date/time, moon, solar lighting, and Open-Meteo weather. Weather and the time system stay **off** until you turn them on.

**LiveStatsEvents** is the custom event engine. It owns the schedule and the classic world events (cargo, supply plane, patrol heli, Chinook, Bradley, F-15, hackable crates). NPC and vehicle types are spawned by companion plugins when those files are present.

Author in `[Info]`: **FiREST0N3D**  
Server this was built for: [rustjawn.com](https://www.rustjawn.com)

| Plugin | Version | Requires |
|--------|---------|----------|
| LiveStats | 1.9.818 | Oxide / Rust dedicated |
| LiveStatsWorld | 1.1.51 | LiveStats |
| LiveStatsEvents | 1.13.59 | LiveStatsWorld |
| LiveStatsEventsNPC | 1.4.81 | LiveStatsEvents (optional companion) |
| LiveStatsEventsVehicles | 1.0.39 | LiveStatsEvents (optional companion) |
| LiveStatsEventFeed | 1.3.75 | LiveStatsEvents (optional dashboard pins) |

Drop the `.cs` files in `oxide/plugins`. Oxide compiles them on load. **Load LiveStats first, then World, then Events.** Companions can load in any order after Events.

Vanilla `server.events` and `cargoship.event_enabled` should stay **false** on the command line if Events is going to own cargo / heli / Bradley. Set `DisableVanillaEvents` in the Events config only when you want the plugin to force that at runtime.

---

## What LiveStats does

- Per-player kills, deaths, headshots, PvP / NPC / animal breakdowns
- Kill streaks (players + NPCs + animals, no death in between)
- Personal PvP log (who you killed / who killed you)
- Server kill feed with optional grid + headshot tag
- NPC and animal type stats (Heavy Scientist, bear, crocodile, livestock, crab, …)
- Environmental deaths (drown, fall, starve, freeze, heat, rad) — drown is **not** counted as suicide
- Dual wipe detection: Oxide `OnNewSave` plus a stored map identity (`level|seed|worldsize`)
- Optional AFK warning + kick (off by default)
- Writes `oxide/data/live_stats.json` on a timer for an external dashboard (online players, streaks, wipe day, recent deaths)
- Language packs under `oxide/lang/<code>/LiveStats.json`, with `/lslang` per player

LiveStats does **not** spawn events, change weather, or move NPCs.

---

## What LiveStatsWorld does

World only starts after it sees LiveStats. If LiveStats is missing it logs an error and sits idle.

**Off by default (safe next to other plugins):**

- `UseLocalWeather` — drive `weather.*` from [Open-Meteo](https://open-meteo.com)
- `TimeSystem.Enabled` — lock in-game date (and optionally hour) to real local time at your coordinates

**v1.1.51:** three extra sky decks — Clear, Few Clouds, Partly Cloudy — so light cover is not forced onto RainMild/Overcast. High-only cirrus still leaves the thick Overcast mesh. Cloud dissolve hops between decks instead of snapping.

**When you enable them:**

- Open-Meteo poll (default every 15 minutes) + smooth blend into Rust weather convars
- Cloud mesh dissolve (Clear → RainMild → Overcast) instead of snapping the sky
- Look-ahead rain / storm promotion from CAPE + WMO codes
- Optional lightning (sound + brightness flash; real bolt prefab off by default)
- Real sunrise/sunset atmosphere if `UseRealSolarAtmosphere` is on
- Polar day/night handling for high latitudes
- Optional split-day: pack N in-game days into one real day
- Optional short suppress of vanilla cargo/heli/CH47 right after a forced time jump (`SuppressCatchUpEvents`, default **false**)

**Conflicts (only after you enable TimeSystem or UseLocalWeather):**  
TimeOfDay, RealTime, other weather controllers, and event plugins that spawn on sudden time jumps.

---

## What LiveStatsEvents does

Events is the scheduler. It does **not** replace LiveStats or World.

**Core-owned (no companion required):**

- Supply drop / cargo plane
- Cargo ship
- Patrol helicopter
- Chinook (vanilla AI inbound, crate chat after a real drop, egress to deep water)
- Bradley APC (road intersection hops; last ends banned so it does not loop the same path)
- F-15 flyby
- Hackable crates that the core is allowed to track

**Companion-owned (core only decides *when*):**

| Type | Companion |
|------|-----------|
| Heavy scientists, peacekeepers, rail / subway / tunnel squads, mine guard, road ambush, base raid | `LiveStatsEventsNPC` |
| Tugboat, submarine, minicopter, scrap heli | `LiveStatsEventsVehicles` |

If `Modules.RequireCompanionPlugins` is true (default) and the companion is missing, those types are skipped. Core will not spawn them itself.

**Cadence**

- Global gap between *any* two events: random in `[MinMinutesBetweenAnyEvent, MaxMinutesBetweenAnyEvent]` (default 5–20 minutes)
- Optional `MinPlayersOnline` gate (default 2)
- `UseScheduledEvents false` = weighted random from the `Events[]` list (default)
- Soft reload keeps the schedule clock instead of resetting it
- Boot cleanup of leftover vanilla cargo planes / queued event ents when `DisableVanillaEvents` is on

**Chat** announces inbound (ETA) then spawn. Cancelled events (example: base raid with no TC) tell the server why.

**LiveStatsEventFeed** (optional) writes `oxide/data/live_events.json` for map pins and the site event list. It is not required for in-game events to run.

---

## Install

1. Rust dedicated server with Oxide 2.
2. Copy `LiveStats.cs` into `oxide/plugins`. Wait for `LiveStats v1.9.818 loaded`.
3. Copy `LiveStatsWorld.cs`. Console should print that the LiveStats host was detected.
4. Copy `LiveStatsEvents.cs`. Console should print `LiveStatsEvents v1.13.59` and an `Enabled events (N): …` line.
5. Optional: `LiveStatsEventsNPC.cs`, `LiveStatsEventsVehicles.cs`, `LiveStatsEventFeed.cs`.
6. Edit configs under `oxide/config/` **before** turning weather, time, or `DisableVanillaEvents` on.

Command-line (recommended when Events owns the map):

```text
+server.events false
+cargoship.event_enabled false
```

First World boot writes defaults in the `.cs`. Current source defaults latitude/longitude to **39.95, -75.16** (Philadelphia). Change those to your server before `UseLocalWeather true`.

---

## Permissions

| Permission | Plugin | Who |
|------------|--------|-----|
| `livestats.admin` | LiveStats | `/clearallstats`, `/livestats cleanup`, World admin fallback |
| `livestats.idle.bypass` | LiveStats | Skip AFK kick (only if idle kick is enabled) |
| `livestatsworld.admin` | LiveStatsWorld | `/worldtime`, `/weatherreload`, `/weatherseed`, `/weatherstatus` |
| `livestatsevents.admin` | LiveStatsEvents | `/event`, `/supplydrop`, console `livestats.event` |
| `livestats.event` | LiveStatsEvents | Same as Events admin (also accepted) |

Grant:

```text
oxide.grant group admin livestats.admin
oxide.grant group admin livestatsworld.admin
oxide.grant group admin livestatsevents.admin
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

Each command can be disabled in `oxide/config/LiveStats.json`.

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

## Chat / console commands — LiveStatsEvents

| Command | Who | What |
|---------|-----|------|
| `/event status` | admin | What’s live, next gap, module handshake |
| `/event reload` | admin | Re-read config without unloading |
| `/event <type>` | admin | Force that type (respects companion gates) |
| `/supplydrop` `/airdrop` | admin | Alias for supply plane |

**Type aliases:** `supply` / `drop` / `plane` → supplydrop · `ship` → cargo · `heli` / `patrol` → patrol heli · `ch47` → chinook · `tank` → bradley · `flyby` → f15

NPC / vehicle names match the config `Type` field (`peacekeeperpatrol`, `heavyscientists`, `tugboat`, `submarine`, …).

Console:

```text
livestats.event status
livestats.event cargo
livestats.spawnf15e
```

`/event` and `/event status` work even when the engine is not fully ready so you can see why (World missing, `Enabled false`, …).

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
| `PlayersOnlyEnv` | `true` | Only real players count toward `/envstats` |
| `ClearStatsOnWipe` | `true` | |
| `WipeTimezone` | `Eastern Standard Time` | Windows ID. Linux often `America/New_York` |
| `WipeHour` / `WipeMinute` | `14` / `15` | Local wipe clock used for “day of wipe” |
| `EnableIdleKick` | `false` | |
| `CustomKillerNames` | map of prefab → label | Override chat names (includes livestock + crab in 1.9.818) |

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
| `TimeSystem.Enabled` | `false` | Master time switch |
| `TimeSystem.SuppressCatchUpEvents` | `false` | Kill vanilla cargo/heli/CH47 for a few seconds after a time jump |
| `DebugMode` | `false` | |

Open-Meteo is fetched with Oxide `webrequest` (no API key). The server needs outbound HTTPS.

---

## Config — LiveStatsEvents (`oxide/config/LiveStatsEvents.json`)

| Key | Default | Notes |
|-----|---------|--------|
| `Enabled` | `true` | Master switch |
| `DisableVanillaEvents` | `false` | When true, plugin kills leftover vanilla cargo/heli/Bradley schedules |
| `Modules.RequireCompanionPlugins` | `true` | NPC/vehicle types need their module |
| `Modules.Vehicles` | `true` | Allow vehicle types when Vehicles is loaded |
| `Modules.Npc` | `true` | Allow NPC types when NPC is loaded |
| `MinPlayersOnline` | `2` | 0 = run on an empty server |
| `CheckIntervalSeconds` | `75` | Scheduler tick |
| `MinMinutesBetweenAnyEvent` | `5` | Global cooldown floor |
| `MaxMinutesBetweenAnyEvent` | `20` | Global cooldown ceiling |
| `UseScheduledEvents` | `false` | `false` = weighted random from `Events[]` |
| `Debug` | `false` | Cooldown / spawn `Puts` — leave off on live boxes |
| `Events` | list | Each row: `Type`, `Enabled`, `Weight`, duration / announce fields |

Do **not** pre-seed `Events[]` in a way that duplicates rows. Newtonsoft appends onto a pre-filled list and you will see every type twice.

NPC lifetimes, shore/road weights, and vehicle coastal rules live in `LiveStatsEventsNPC.json` and `LiveStatsEventsVehicles.json`, not in the core file.

---

## Data files

| File | Writer | Safe to delete? |
|------|--------|-----------------|
| `oxide/data/live_stats.json` | LiveStats | Yes — rebuilt on the next timer |
| `oxide/data/livestats_wipe.json` (name may vary) | LiveStats | Only if you want wipe identity reset |
| Player / NPC / animal / env JSON under `oxide/data/` | LiveStats | Wipes tracked stats |
| Weather persist file (when `PersistWeatherState`) | LiveStatsWorld | Next poll rebuilds weather |
| `oxide/data/live_events.json` | LiveStatsEventFeed | Yes — rebuilt on the next pin tick |

Do not commit `oxide/data/` or live `oxide/config/*.json` to git.

---

## Dashboard JSON

`live_stats.json` includes:

- `serverRunning`, `serverName`, map name / seed / size
- Online player list: name, SteamID, session minutes, totals, streaks, `isPlayerDead`, `isSleeping`
- `recentDeaths`
- `wipeDay`, `wipeStartUtc`, timezone + wipe clock
- `timestamp` / `lastUpdateUnix`

`live_events.json` (Feed) includes `active`, `recent` (max 10), `inbound`, pin percents, map size / seed. Air and cargo pins update about twice a second; NPC pins about once a second.

Point any site you want at those files. This repo does not include the rustjawn.com frontend.

---

## Localization

LiveStats ships English via `LoadDefaultMessages` and extra packs on disk as `oxide/lang/<code>/LiveStats.json`.

- `/lslang es` — that player only
- `LanguageForce` in config — whole server
- `LanguageSyncFromClient` — uses the Rust client language on connect

World and Events have their own `lang` keys for `/worldstats`, `/worldtime`, and `/event`.

---

## Load / unload notes

- Reloading LiveStats while World is loaded: World pauses, then `OnPluginLoaded` starts it again.
- Unloading LiveStats: World stops timers and restores vanilla `DayLengthInMinutes` if it changed them.
- Unloading LiveStats: `live_stats.json` is marked `serverRunning: false` so a dashboard can show offline.
- Reloading Events: schedule clock is kept (soft reload). NPC module kills leftover event scientists on load.
- Idle input hook is **unsubscribed** when `EnableIdleKick` is false.
- World’s `OnEntitySpawned` catch-up hook is **unsubscribed** unless `SuppressCatchUpEvents` is true.

---

## Changelog

### LiveStatsEventFeed 1.3.75
- Reused write / dedup / NPC scratch buffers so Oxide memory no longer climbs hundreds of MB over a long boot
- Live-file read throttled; core index sync every 20s
- Pin rates unchanged (air 0.5s, NPC ~1.2s)

### LiveStatsEventsNPC 1.4.81
- Reject origin / M12 teleports on drive
- No `PositionToGrid` or `ResolveRustNav` on the walk tick
- Debug strings only built when `Debug` is true

### LiveStatsEvents 1.13.59
- Bradley random intersection hops; last four route ends banned
- Chinook crate chat only after a real drop; void egress is Deep Sea
- Companion handshake: NPC / Vehicles register once with core

### LiveStatsWorld 1.1.51
- Added Clear / Few Clouds / Partly Cloudy cloud decks so light cover is not forced onto RainMild or Overcast
- High-only cirrus does not hold the thick Overcast mesh
- WeatherRuntimeState, timer hub, and CloudSwapPhase dissolve

### LiveStats 1.9.818
- Livestock + critter aliases (heifer, steer, wether, hare, foal)
- Crab / crab swarm killer names
- Keeps 1.9.817 last-hit attribution, death dedupe, unified playtime bank

---

## License

MIT (see `LICENSE`).  
Rust and the Oxide/uMod framework are owned by their respective authors. This project is not affiliated with Facepunch or uMod.
