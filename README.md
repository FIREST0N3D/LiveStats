# LiveStats

Oxide plugins for Rust dedicated servers.

**LiveStats** tracks players, NPCs, and animals, writes a live JSON snapshot for a dashboard, and handles wipe detection.

**LiveStatsWorld** is an optional extension: real-world date/time, moon, solar lighting, Open-Meteo weather, and lunar tides. Weather and the time system stay **off** until you turn them on. Tides default **on** and write `env.oceanlevel`.

**LiveStatsEvents** is the custom event engine. It owns the schedule and the classic world events (cargo, supply plane, patrol heli, Chinook, Bradley, F-15, hackable crates, airfield Chinook). NPC and vehicle types are spawned by companion plugins when those files are present.

Author in `[Info]`: **FiREST0N3D**  
Server this was built for: [rustjawn.com](https://www.rustjawn.com)

| Plugin | Version | Requires |
|--------|---------|----------|
| LiveStats | 1.9.818 | Oxide / Rust dedicated |
| LiveStatsWorld | 1.1.52 | LiveStats |
| LiveStatsEvents | 1.13.167 | LiveStatsWorld |


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

**1.9.818 (current, unchanged this drop):** livestock and critter aliases (heifer, steer, wether, hare, foal), crab / crab-swarm killer names, and the 1.9.817 last-hit attribution, death dedupe, and unified playtime bank.

---

## What LiveStatsWorld does

World only starts after it sees LiveStats. If LiveStats is missing it logs an error and sits idle.

**Off by default (safe next to other plugins):**

- `UseLocalWeather` — drive `weather.*` from [Open-Meteo](https://open-meteo.com)
- `TimeSystem.Enabled` — lock in-game date (and optionally hour) to real local time at your coordinates

**On by default (v1.1.52):**

- Lunar tides. `env.oceanlevel` follows the real moon: two highs per lunar day, full range at new/full (spring), scaled down at quarters (neap). Restores the base level on unload. Conflicts with any other plugin that writes `oceanlevel`.

**v1.1.51:** three extra sky decks — Clear, Few Clouds, Partly Cloudy — so light cover is not forced onto RainMild/Overcast. High-only cirrus still leaves the thick Overcast mesh. Cloud dissolve hops between decks instead of snapping.

**When you enable weather / time:**

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

**Conflicts (tides, on unless you set `Tide.Enabled` false):**  
Any plugin or admin command that owns `env.oceanlevel`. Amplitude above ~2 floods a lot of shoreline and fights water-base plugins. Default spring half-range is `0.65`.

---

## What LiveStatsEvents does

Events is the scheduler. It does **not** replace LiveStats or World.

**Core-owned (no companion required):**

- Supply drop / cargo plane
- Cargo ship (lifecycle monitor: egress when the path ends or the ship sits idle, then force-kill after the egress window)
- Patrol helicopter
- Chinook (vanilla AI inbound, crate chat only after a real drop, egress to deep water / Deep Sea grid)
- Bradley APC (road intersection hops; last ends banned so it does not loop the same path; radtown lots are a gate jump, not a drive around the painted ring; blocked highway segments are dropped)
- F-15 flyby
- Hackable crates that the core is allowed to track
- Airfield Chinook — stealable guarded CH47 at the airfield monument (skipped if the map has no airfield)

Hot air balloons are despawn-only. The scheduler forces `HotAirBalloon` **Disabled** even if an old config still has it enabled. Population is zeroed when vanilla suppression is on.

If `Modules.RequireCompanionPlugins` is true (default) and the companion is missing, those types are skipped. Core will not spawn them itself.

**Cadence**

- Global gap between *any* two events: random in `[MinMinutesBetweenAnyEvent, MaxMinutesBetweenAnyEvent]` (default 5–20 minutes; floor is clamped to 8)
- Optional `MinPlayersOnline` gate (default 2)
- `UseScheduledEvents false` = weighted random from the `Events[]` list (default)
- Cold boot reserves the first event as Cargo Ship after 8–18 minutes
- Soft reload keeps the schedule clock instead of resetting that reservation (a cargo already on the map, or a recent persisted time, opens the random pool)
- Boot cleanup of leftover vanilla cargo planes / queued event ents when `DisableVanillaEvents` is on
- Shared live-entity index (spawn/kill hooks, sanity rebuild about every 20 minutes) so counts are not a full world scan every tick

**Map data**

- `oxide/data/LiveStatsMonuments.json` — monument skeleton (`monuments-v3.3`): through-roads, spurs, gates, lanes
- `oxide/data/LiveStatsBradleyPaths.json` — Bradley prefab path templates (`paths-v3`)
- `/event monbake` rebuilds both after a map change

**Chat** announces inbound (ETA) then spawn. Cancelled events (example: base raid with no TC) tell the server why.

---

## Install

1. Rust dedicated server with the newest version Oxide.
2. Copy `LiveStats.cs` into `oxide/plugins`. Wait for `LiveStats v1.9.818 loaded`.
3. Copy `LiveStatsWorld.cs`. Console should print `LiveStatsWorld v1.1.52 loaded` and that the LiveStats host was detected. Tide line follows if `Tide.Enabled` is still true.
4. Copy `LiveStatsEvents.cs`. Console should print `LiveStatsEvents v1.13.167` and an `Enabled events (N): …` line.
5. Edit configs under `oxide/config/` **before** turning weather, time, or `DisableVanillaEvents` on. Set `Tide.Enabled` false if another plugin owns ocean level.

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
| `/worldstats` | everyone | In-game clock, moon, tide (`oceanlevel` + range), sun, weather, uptime |
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
| `/event monbake` | admin | Rebuild monument + Bradley path data files |
| `/event <type>` | admin | Force that type (respects companion gates) |
| `/supplydrop` `/airdrop` | admin | Alias for supply plane |

**Type aliases:** `supply` / `drop` / `plane` → supplydrop · `ship` → cargo · `heli` / `patrol` → patrol heli · `ch47` → chinook · `tank` → bradley · `flyby` → f15 · `airfield` / `airfieldchinook` → stealable airfield CH47 · `subway` / `metro` → subway patrol · `mine` → mine guard · `rail` / `trainpatrol` → rail patrol · `raid` → base raid · `heavies` → heavy scientists · `peacekeepers` → peacekeeper patrol · `ambush` → road ambush · `tunnel` → tunnel squad

NPC / vehicle names match the config `Type` field (`peacekeeperpatrol`, `heavyscientists`, `tugboat`, `submarine`, …).

Console:

```text
livestats.event status
livestats.event cargo
livestats.event monbake
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

Leave weather and time **false** until lat/lon are yours. Tides are separate and default on.

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
| `Tide.Enabled` | `true` | Lunar `oceanlevel`. Set false to leave the ocean alone |
| `Tide.BaseOceanLevel` | `0` | Level restored on unload |
| `Tide.SpringAmplitude` | `0.65` | Half-range at new/full moon (≈ metres). Keep under ~2 |
| `Tide.NeapScale` | `0.42` | Quarter-moon range as a fraction of spring |
| `Tide.UseRealMoon` | `true` | Phase from the calendar even if moon mode is forced |
| `Tide.IncludeSolarComponent` | `true` | Small solar semi-diurnal so the two daily highs differ |
| `Tide.UpdateIntervalSeconds` | `20` | |
| `Tide.RestoreOnUnload` | `true` | |
| `DebugMode` | `false` | |

Open-Meteo is fetched with Oxide `webrequest` (no API key). The server needs outbound HTTPS.

---

## Config — LiveStatsEvents (`oxide/config/LiveStatsEvents.json`)

| Key | Default | Notes |
|-----|---------|--------|
| `Enabled` | `true` | Master switch |
| `DisableVanillaEvents` | `false` | When true, plugin kills leftover vanilla cargo/heli/Bradley schedules and zeros hot-air-balloon population |
| `Modules.RequireCompanionPlugins` | `true` | NPC/vehicle types need their module |
| `Modules.Vehicles` | `true` | Allow vehicle types when Vehicles is loaded |
| `Modules.Npc` | `true` | Allow NPC types when NPC is loaded |
| `MinPlayersOnline` | `2` | 0 = run on an empty server |
| `CheckIntervalSeconds` | `75` | Scheduler tick (clamped 45–180) |
| `MinMinutesBetweenAnyEvent` | `5` | Global cooldown floor (runtime clamp 8) |
| `MaxMinutesBetweenAnyEvent` | `20` | Global cooldown ceiling |
| `UseScheduledEvents` | `false` | `false` = weighted random from `Events[]` |
| `Debug` | `false` | Cooldown / spawn `Puts` — leave off on live boxes |
| `Events` | list | Each row: `Type`, `Enabled`, `Weight`, interval / announce fields |

Soft-added if an older config is missing them: `BaseRaid`, `SubwayPatrol`, `MineGuard` (rebalanced off the old rare schedule), `RailPatrol`, `AirfieldChinook`. `HotAirBalloon` is forced off.

Do **not** pre-seed `Events[]` in a way that duplicates rows. Newtonsoft appends onto a pre-filled list and you will see every type twice. A corrupt config is replaced with defaults — the old file is not kept.

NPC lifetimes, shore/road weights, and vehicle coastal rules live in `LiveStatsEventsNPC.json` and `LiveStatsEventsVehicles.json`, not in the core file.

Chinook tour defaults (if an old config is still on the 1.13.17 short values): patrol 18 minutes, 8 monument hops, 75s between waypoints, at least 8 minutes before the crate drop.

---

## Data files

| File | Writer | Safe to delete? |
|------|--------|-----------------|
| `oxide/data/live_stats.json` | LiveStats | Yes — rebuilt on the next timer |
| `oxide/data/livestats_wipe.json` (name may vary) | LiveStats | Only if you want wipe identity reset |
| Player / NPC / animal / env JSON under `oxide/data/` | LiveStats | Wipes tracked stats |
| Weather persist file (when `PersistWeatherState`) | LiveStatsWorld | Next poll rebuilds weather |
| `oxide/data/LiveStatsMonuments.json` | LiveStatsEvents | Yes — rebuilt on boot or `/event monbake` |
| `oxide/data/LiveStatsBradleyPaths.json` | LiveStatsEvents | Yes — rebuilt with the monument bake |
| `oxide/data/live_events.json` | LiveStatsEventFeed | Yes — rebuilt on the next pin tick |

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

World and Events have their own `lang` keys for `/worldstats`, `/worldtime`, and `/event`. World’s `/worldstats` tide line is `WorldStatsTide`.

---

## Load / unload notes

- Reloading LiveStats while World is loaded: World pauses, then `OnPluginLoaded` starts it again.
- Unloading LiveStats: World stops timers and restores vanilla `DayLengthInMinutes` if it changed them.
- Unloading LiveStats: `live_stats.json` is marked `serverRunning: false` so a dashboard can show offline.
- Unloading World: lunar tide restores `Tide.BaseOceanLevel` when `RestoreOnUnload` is true.
- Reloading Events: schedule clock is kept (soft reload). NPC module kills leftover event scientists on load.
- Idle input hook is **unsubscribed** when `EnableIdleKick` is false.
- World’s `OnEntitySpawned` catch-up hook is **unsubscribed** unless `SuppressCatchUpEvents` is true.

---

## What else exists on the production box

Documented above when you ship the file:

- LiveStatsEventsNPC — scientist patrols, tunnels, mine guard, ambush, base raid
- LiveStatsEventsVehicles — tug / sub / mini / scrap + player-owned hulls
- LiveStatsEventFeed — map pins JSON
- LiveStatsSystem — help, board inbox, Steam build check, restart schedule
- MapImageSaver — map image export

LiveStats + World still run without any of those.

---

## Changelog

Older entries are kept so a server moving up from the last README can see every documented step. Companion plugins were not in this source drop; their last notes are unchanged.

### LiveStatsEvents 1.13.167
- Bradley radtown lots are a gate jump across the yard instead of a drive around the painted ring (`radtown via=tpl` no longer loops the lot)
- Blocked highway segments are dropped from Bradley routes
- Monument skeleton is `monuments-v3.3` (`oxide/data/LiveStatsMonuments.json`); Bradley prefab paths are `paths-v3` (`oxide/data/LiveStatsBradleyPaths.json`)
- `/event monbake` (alias `bake`) rebuilds both files after a map change

### LiveStatsEvents 1.13.59
- Bradley random intersection hops; last four route ends banned
- Chinook crate chat only after a real drop; void egress is Deep Sea
- Companion handshake: NPC / Vehicles register once with core

### LiveStatsEvents since 1.13.17 (still in 1.13.167, not called out on the load line)
- Chinook tour lengthened: 18 minute patrol, 8 monument hops, 75s waypoint interval, at least 8 minutes before the crate drop (old defaults were 12m / 5 hops / 50s)
- Cargo ship lifecycle monitor: egress after the path ends or the ship sits idle, force-kill after the egress window
- Cold boot reserves Cargo Ship for 8–18 minutes; soft reload keeps the schedule and does not re-reserve that gap
- Shared live-entity index (spawn/kill hooks, ~20 minute sanity rebuild) plus a combined maintenance tick
- Soft-added event rows when missing: BaseRaid, SubwayPatrol, MineGuard (schedule rebalanced off the old rare window), RailPatrol, AirfieldChinook (stealable guarded CH47)
- Hot air balloon forced Disabled (despawn-only). Vanilla suppression also zeros balloon population and leftover Launch Site Bradleys
- Duplicate `Events[]` rows are collapsed on load; a corrupt config is replaced and the old file is not kept

### LiveStatsWorld 1.1.52
- Lunar spring/neap tides via `env.oceanlevel` (two highs per lunar day; spring at new/full, neap scale at quarters)
- Optional solar semi-diurnal so the two daily highs are not identical
- `/worldstats` prints tide state, ocean level, and range
- Restores `Tide.BaseOceanLevel` on unload / LiveStats host unload
- Load line still reports the 1.1.51 Clear / Few Clouds / Partly Cloudy decks

### LiveStatsWorld 1.1.51
- Added Clear / Few Clouds / Partly Cloudy cloud decks so light cover is not forced onto RainMild or Overcast
- High-only cirrus does not hold the thick Overcast mesh
- WeatherRuntimeState, timer hub, and CloudSwapPhase dissolve

### LiveStatsWorld earlier (still in 1.1.52)
- v1.1.12 storm detection from CAPE (soft-migrated thresholds; aggressive storm detection on)
- v1.1.14 denser Open-Meteo anchors (15-minute samples, capped)
- v1.1.15 precip latch left off by default so API precip is kept and dry lightning is allowed
- Split-day clock, real-solar atmosphere, polar day/night, and opt-in catch-up event suppress (default off)

### LiveStats 1.9.818
- Livestock + critter aliases (heifer, steer, wether, hare, foal)
- Crab / crab swarm killer names
- Keeps 1.9.817 last-hit attribution, death dedupe, unified playtime bank


---

## License

MIT (see `LICENSE`).  
Rust and the Oxide/uMod framework are owned by their respective authors. This project is not affiliated with Facepunch or uMod.
