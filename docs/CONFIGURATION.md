# Configuration guide

This guide lists every setting of the stack and ready-made recipes for the common runs. It assumes you cloned the repository and have your own copy of the client data for build 16042 in `data/tbl` and `data/map` (see [QUICKSTART.md](QUICKSTART.md#client-data)). Operating the stack (start, stop, logs, backups, scripts) is in [OPERATIONS.md](OPERATIONS.md).

## How settings reach the server

1. You edit `.env` in the repository root (create it with `./install.sh`, which fills in `.env.example`, or `cp .env.example .env`). `.env` is git-ignored.
2. `docker-compose.yml` reads `.env` and passes values to the `world` container as `NF_*` environment variables (and a few plain ones). The code reads only the `NF_*` names, never the `.env` names directly.
3. Changing `.env` takes effect when the container is recreated: `docker compose up -d world`. `scripts/bots.sh` does that for you for the settings it edits.

Two pitfalls:

- **Shell variables win over `.env`.** Compose prefers variables exported in your shell. The repository scripts run `unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1)` before calling compose for this reason. Do the same in your own shell if a setting seems to be ignored.
- Compose gives every optional variable a default (listed below). A variable that is missing from `.env` is not an error, except the four marked "required".

The servers also read their bundled JSON config (`Source/NexusForever.WorldServer/WorldServer.example.json` and the auth/sts equivalents) and let environment variables override any key, using `__` as the separator (for example `Map__GridUnloadTimer`). Add such variables to the `environment:` block of the service in `docker-compose.yml`. The compose file already sets the database connection strings, `Map__MapPath` and `Map__GridUnloadTimer` this way.

## `.env` settings

### Database and realm

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `DB_ROOT_PASSWORD` | none (required) | alphanumeric | MariaDB root password. Used only when the database volume is first created, and by the helper scripts that talk to the database. |
| `DB_USER` | none (required); `install.sh` writes `nexusforever` | alphanumeric | Application database user, granted on the three databases `nexus_forever_auth`, `nexus_forever_character`, `nexus_forever_world` by `docker/mariadb-init.sh`. |
| `DB_PASSWORD` | none (required) | alphanumeric | Application user password. It goes into a connection string unescaped, so keep it alphanumeric. |
| `REALM_HOST` | none (required) | IP or DNS name | Address clients are told to use for the world server. It must be reachable from the clients and from inside the containers. The world server writes it into the realm row at start. Passed as `NF_REALM_HOST`. |
| `REALM_NAME` | `NexusForever` | text | Realm name shown in the client (`NF_REALM_NAME`). |
| `REALM_ID` | `1` | number | Realm id in the auth database and `RealmId` of the world server. |
| `BIND_IP` | `0.0.0.0` | IP | Host address the published game ports are bound to (24000 world, 23115 auth, 6600 sts). The database port is never published. |
| `COMPOSE_PROJECT_NAME` | `nexusforeverbots` (the `name:` in `docker-compose.yml`) | lowercase, digits, `-`, `_` | Compose project: prefix of containers, volumes (the database) and the network. Only change it for a second, separate stack on the same machine (`install.sh --project NAME`). |
| `IMAGE_PREFIX` | `nexusforeverbots` | same as the project name | Prefix of the locally built images (`<prefix>-world:local`, `-auth`, `-sts`), so a second stack never retags the images of the first. |
| `WORLD_PORT` / `AUTH_PORT` / `STS_PORT` | `24000` / `23115` / `6600` | port numbers | Host ports the three servers are published on. The realm row always advertises the container port 24000 and the client uses fixed auth/STS ports, so a game client can only use a stack on the defaults; other values are for side-by-side test stacks. |

MariaDB reads the three `DB_*` passwords only when it creates a new data volume. Changing them later requires `docker compose down -v` (this deletes all accounts and characters) or changing them inside MariaDB yourself.

### World and performance

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `TICK_MS` | `20` | integer >= 1 (anything else falls back to 20) | Minimum milliseconds per world update (`NF_TICK_MS`). 20 means at most 50 updates per second. The upstream loop slept 1 ms and ran several hundred updates per second, which wastes CPU. |
| `LOG_LEVEL` | empty | `Trace`, `Debug`, `Info`, `Warn`, `Error` | Raises the minimum log level (`NF_LOG_LEVEL`). Empty keeps every level, which is chatty and costs CPU with many bots. `Info` is what the soak script uses. |
| `GRID_UNLOAD_TIMER` | `300` | seconds | Passed as `Map__GridUnloadTimer`: how long an idle grid stays loaded. The bundled JSON default is 3600. |
| `MAP_IDLE_UNLOAD` | `420` | seconds (number) | Seconds an open-world map with no active grid, entity or pending action stays loaded (`NF_MAP_IDLE_UNLOAD`). Each loaded world file takes 200 to 500 MB in memory according to the code comment. |
| `WORLD_MEM_LIMIT` | `14g` | docker memory size | `mem_limit` of the world container. If the world exceeds it, only this container is killed and restarted. |
| `PROFILE` | `0` | `1` turns on | `NF_PROFILE=1` turns on the internal timers shown by `gt profile` and `gt maps`. The timers cost a little CPU themselves. The soak script sets it to 1. |
| `PATCH_DIR` | `./client/Patch` | path | Only used by the one-shot tool services (`mapgen`, `uiextract`, profile `tools`): the client `Patch` folder, mounted read-only. Set it to your own client's `Patch` folder (install.sh does this from `--client-dir`); unset, the tools look in `./client/Patch` inside the repo (git-ignored). |
| `MAPGEN_ARGS` | `-g` | MapGenerator options | Not a `.env` key: container variable of the `mapgen` tool, set per run (`docker compose run --rm -e MAPGEN_ARGS="-e -g" mapgen`). `-e` extracts the tables to `data/map-gen/work/tbl`, `-g` generates maps to `data/map-gen/work/map`. `install.sh` uses it when `data/tbl` or `data/map` is missing. |

### Combat and bots

Bots are real characters that log in without a network connection. They need `COMBAT=1` to fight and quest. `scripts/bots.sh` sets `COMBAT=1` whenever you give it a bot count above 0.

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `COMBAT` | `0` | `1` on | `NF_COMBAT`: damage, healing, death, resurrection and creature respawn. Off keeps the stock stub behaviour. Any value other than `1` is off. |
| `COMBAT_AGGRO` | `0` | `1` on | `NF_COMBAT_AGGRO`: hostile creatures attack players and bots within 12 m. Only has an effect when `COMBAT=1`. |
| `BOTS_TARGET` | `0` | number | `NF_BOTS_TARGET`: how many ambient bots are kept logged in. 0 or anything unparsable means none. Bots log in at about four per second after the world is up. Characters persist when you lower the number. |
| `BOTS_MAX` | `8` | number >= 0 | `NF_BOTS_MAX`: limit for companion bots added with the console command `bot add`. |
| `INNATE_COSTS` | `1` | `0` off | `NF_INNATE_COSTS`: class resource costs of abilities are checked and charged (Esper Psi Points, Engineer Volatility, Medic Actuators, Spellslinger Spell Power for Surge casts) and caster resource requirements (Warrior Kinetic Energy thresholds) are checked. Players get their class resource pool at login. Suit Power and Focus costs are only counted. Only `0` turns it off. See docs/COMBAT-MATH.md "Resource economy". |
| `BOTS_IMMORTAL` | `0` | `1` on | `NF_BOTS_IMMORTAL`: bots take no damage, for unattended runs. |
| `BOTS_CLEAN` | `0` | `1` on | `NF_BOTS_CLEAN`: ambient bots use only clean accounts `bot.clean-N@bots.invalid` (new level 1-3 characters, reused by later clean runs, never logged in by other runs), all quest bots, no harness level changes, no companions; every completion is written to `/cache/truth/quest-truth.tsv` with how it was credited (TESTING.md "Clean-bot quest verification"). |
| `BOTS_SYNTHETIC` | `1` | `0` off | `NF_BOTS_SYNTHETIC`: when not `0`, bots credit objective types that have no in-world path yet directly (counted per type as `objective-synthetic` in the failure report). `0` leaves only real objectives: the drivers (zone, area, spell, item use, equip, crafting, events) still run, only the direct credit is skipped. |
| `BOTS_FOCUS` | empty | comma list of quest objective type ids, e.g. `12,32` | `NF_BOTS_FOCUS`: bots take quests containing one of those objective types first. `scripts/bots.sh focus TYPES` also deletes the bots' unfinished quests (it stops the world to do so). |
| `BOTS_DAMAGE` | `1` | float > 0 (invariant culture, `1.5`) | `NF_BOTS_DAMAGE`: multiplies the damage bots deal. Anything else falls back to 1. |

### Bot roles

Ambient bots are assigned a role by their index, in this order: dungeon bots first, then event bots, then system-probe bots, then path bots (which are quest bots), then plain quest bots (`Bots/BotManager.cs`). The counts below are therefore taken from the front of the bot list.

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `BOTS_DUNGEON` | `0` | world id or comma list (`382` or `382,1263,1336`) | `NF_BOTS_DUNGEON`: dungeon, raid or adventure worlds that bot teams run one after the other. Unparsable entries are dropped. 0 or empty leaves bots on quests. Needs spawn data for those worlds in the world database. |
| `BOTS_DUNGEON_BOTS` | all bots when `BOTS_DUNGEON` is set, else 0 | number | `NF_BOTS_DUNGEON_BOTS`: how many bots run dungeons. |
| `BOTS_TEAM` | `5` | number > 0 | `NF_BOTS_TEAM`: team size. Use 20 for raids (the recorded raid runs in `docs/INSTANCES.md` used 20). |
| `BOTS_RAID` | empty | world id or comma list (`1333,1462`) | `NF_BOTS_RAID`: raid worlds for raid teams. Raid teams are formed from the dungeon bots before the dungeon teams, so `BOTS_DUNGEON_BOTS` must cover both. |
| `BOTS_RAID_TEAM` | `20` | number > 0 | `NF_BOTS_RAID_TEAM`: raid team size. The leader converts the group to a raid (group flag, up to 20 by invite); members above 20 are added as server members. |
| `BOTS_RAID_TEAMS` | `1` | number >= 0 | `NF_BOTS_RAID_TEAMS`: how many raid teams. |
| `BOTS_DUNGEON_ENTRY` | `auto` | `auto`, `portal`, `finder`, `teleport` | `NF_BOTS_DUNGEON_ENTRY`: how teams get in. `portal`: to the world's entrance portal unit in the open world and ClientActivateUnit on it; `finder`: group finder queue as a group, role check, pending game accepted by every member (0x05C8), transfer into the match; `teleport`: straight to the entrance. `auto` takes portal and finder by turns where the world has both, one of them where it has one, else teleport. |
| `BOTS_DUNGEON_MINUTES` | `30` | minutes > 0 | `NF_BOTS_DUNGEON_MINUTES`: a team run that is not clear after this long is ended (`dungeon-timeout`) and the team leaves through the normal leave path. |
| `RAID_SAVED_INSTANCES` | `1` | `0` or `1` | `NF_RAID_SAVED_INSTANCES`: raid bosses killed in an instance stay dead in later instances of the same owner until the weekly reset. `0` turns it off (every raid instance spawns all bosses). |
| `BOTS_TEAM_LEVEL` | `boss` | `boss`, `median`, `quest`, or a fixed level 1-50 | `NF_BOTS_TEAM_LEVEL`: level of the team. `boss` = highest level among bosses that have an encounter definition (falls back to the median creature level when there is none); `median` = median creature level. `quest`: normal runs use the level most of the world's never completed instance quests ask for (`dungeon-quest-level`), so quests the boss level refuses can be taken; veteran runs keep level 50. |
| `BOTS_VETERAN` | `alternate` | `alternate`, `all`, `off` | `NF_BOTS_VETERAN`: whether team runs use the veteran variant of worlds that have one. `alternate` makes every second run (0, 2, 4, ...) veteran; `all` always; `off` never. Worlds without veteran data always run normal. |
| `VETERAN_WORLDS` | empty | `all` or comma list of world ids | `NF_VETERAN_WORLDS`: forces the veteran difficulty for those worlds (also for human players entering them). Otherwise the difficulty comes from the instance settings the group leader or lone player chose in the client. |
| `BOTS_EVENTS` | `0` | `1` on | `NF_BOTS_EVENTS`: bots travel from public event to public event and fight there; quests are ignored. When on and `BOTS_EVENT_BOTS` is unset, all bots (after the dungeon bots) are event bots. |
| `BOTS_EVENT_BOTS` | all when `BOTS_EVENTS=1`, else 0 | number | `NF_BOTS_EVENT_BOTS`: how many bots test events. |
| `BOTS_SYSTEM_BOTS` | `0` | number | `NF_BOTS_SYSTEM_BOTS`: bots after the dungeon and event bots that run system probes (the "system" probes whose ok/fail counts `scripts/system-counts.sh` prints). |
| `BOTS_PATH_BOTS` | `0` | number | `NF_BOTS_PATH_BOTS`: quest bots, after the system bots, that play path missions of their path first. |
| `BOTS_PATH_FOCUS` | empty | comma list of mission kinds: `Kill`, `Activate`, `Spell`, `Scan`, `Area`, `Holdout`, `Scavenger`, `Door`, `Hub`, `Infrastructure`, `Explore`, `PowerMap` | `NF_BOTS_PATH_FOCUS`: path bots only pick missions of these kinds. Names are case-insensitive; unknown names are dropped. |

### Persistence, regeneration and decay

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `SHUTDOWN_SAVE_SECONDS` | `15` | seconds, 0 turns the save off | `NF_SHUTDOWN_SAVE_SECONDS`: on SIGTERM (`docker compose stop/restart`) or Ctrl-C the world saves every online character and waits up to this long before it exits. Keep it below `stop_grace_period` of the world service (30 s). Outcome in `/cache/shutdown-save.log`. |
| `REGEN_HEALTH_OOC` | `5` | percent of max health per second | `NF_REGEN_HEALTH_OOC`: health regeneration out of combat, scaled by the unit's HealthRegenMultiplier. Design value: the client and the tables hold no base rate. |
| `REGEN_HEALTH_COMBAT` | `0` | percent per second | `NF_REGEN_HEALTH_COMBAT`: health regeneration in combat. Design value. |
| `REGEN_SUIT_POWER` | `5` | points per second | `NF_REGEN_SUIT_POWER`: Esper/Medic/Spellslinger/Stalker suit power (resource 3) regeneration, scaled by ResourceRegenMultiplier3. Design value. |
| `DECAY_KINETIC_COMBAT` | `10` | points per second | `NF_DECAY_KINETIC_COMBAT`: Warrior kinetic energy decay in combat, scaled by SpellMechanicEnergyRegenOrDecayMultiplier. Design value. |
| `DECAY_KINETIC_OOC` | `100` | points per second | `NF_DECAY_KINETIC_OOC`: kinetic energy decay out of combat. Design value. |
| `DECAY_VOLATILITY_OOC` | `10` | points per second | `NF_DECAY_VOLATILITY_OOC`: Engineer volatility decay out of combat. Design value. |

"Design value" means the number is a server default chosen to match the direction the wiki describes (suit power refills, kinetic energy and volatility drain between fights); no rate was decoded from the client or the tables. Fall damage, drowning and hazard meters have no setting: they are not simulated (docs/ASSUMPTIONS.md "Hazards").

### Items

| Name | Default | Valid values | What it does |
|---|---|---|---|
| `NF_DURABILITY_DEATH_LOSS` | `0` | fraction 0 to 1 | Fraction of durability lost per death. It is not mapped in `docker-compose.yml`; to use it add `NF_DURABILITY_DEATH_LOSS: ...` to the `world` environment block. The code comment says the real source of durability loss is not in any table or document, so loss on death is off by default. |

## Other variables the code reads

These are not in `.env`; most are fixed by `docker-compose.yml`.

| Name | Set by | What it does |
|---|---|---|
| `NF_RESEARCH_PATH` | not set; code default `/data/research` | Folder with the research tables (event NPCs, quest links, challenge sources, veteran variants, path missions, creature research). Compose mounts `./data/research` there read-only. |
| `NF_ENCOUNTER_PATH` | not set; code default `/data/encounters` | Folder with the boss encounter definitions. Compose mounts `./data/encounters` there read-only. |
| `CachePath` | compose: `/cache` | Folder for runtime files: failure report, quest coverage, bot store `bots.json`, bot feed, quest outcomes, sweep queue and results, the command inbox used by `scripts/gt.sh`, the parsed table cache. Backed by the named volume `world-cache`. |
| `GameTablePath` | compose: `/data/tbl` | Game tables, mounted read-only from `./data/tbl`. |
| `Map__MapPath` | compose: `/data/map` | Map files, mounted read-only from `./data/map`. |
| `NF_SKIP_MIGRATIONS` | unset | `1` stops the servers from applying EF migrations to the databases (for people who manage the schema themselves). |
| `NF_CREATE_ACCOUNT_EMAIL`, `NF_CREATE_ACCOUNT_PASSWORD` | `scripts/create-account.sh` | One-shot mode: the world process creates this account and exits (exit code 2 if it exists). Do not set the password in a file you keep. |
| `NF_ACCOUNT_PASSWORD` | your shell | Read by `install.sh` and `scripts/create-account.sh` so the password is not typed or put on a command line. |
| `NF_EXTRACT_PATTERN` | `uiextract` tool service | Glob of client files to extract (default `UI/Vendor/*.lua`). Research tool only. |
| `NF_FLOOR_MAX_MB` | `scripts/gen-floors.sh` | Size cap (default 64) for a prop floor-height sidecar; above it the world is rebuilt from buildings only. |
| `WORLD_DATA_REF` | your shell | Revision of the upstream world database that `scripts/load-world-data.sh` checks out (default `1e5ff92`). |

## Files and folders the stack uses

| Path | Content | In git |
|---|---|---|
| `.env` | your settings and database passwords | no |
| `data/tbl/`, `data/map/` | your client data (tables and maps) | no |
| `data/research/`, `data/encounters/`, `data/spawns/` | research tables, encounter definitions, spawn files used by the import scripts | yes |
| `data/map-gen/` | output of the map generator tool | no |
| `world-data/` | checkout of the upstream world database used by the load scripts | no |
| `soak/` | soak run output | no |
| volume `db-data` | the three databases | n/a |
| volume `world-cache` | `/cache` of the world container | n/a |

## Recipes

Each recipe is a set of `.env` lines. Apply with `docker compose up -d world` (or `docker compose up -d` for a first start). Lines not mentioned keep their value. Where a recipe says "scripts", it names a script described in [OPERATIONS.md](OPERATIONS.md).

Resource use below is measured on one machine; your numbers will differ. 100% CPU is one core.
- 30 bots: about 100-130% CPU and 2.5-3 GiB.
- 60 bots: about 140-170% CPU and 2.5-3.5 GiB.

Memory depends strongly on how many distinct worlds are loaded (instance worlds cost 1-2 GB each according to `scripts/raid-batches.sh`). High absolute CPU with many bots is expected; watch whether it climbs over time.

### 30-bot QA run

```
BOTS_TARGET=30
COMBAT=1
COMBAT_AGGRO=0
BOTS_IMMORTAL=1
BOTS_SYNTHETIC=1
LOG_LEVEL=Info
TICK_MS=20
```

Or `scripts/gate.sh 30`, which brings the stack up, waits 90 s, sets the count with `scripts/bots.sh count 30`, waits 10 minutes and stores the failure report, map timings and container stats in `/tmp/gate-*.txt`. Read them with `scripts/status.sh`. The pass criteria used in this repository ([TESTING.md](TESTING.md)): no `map-update-exception` or `spell-update-exception` in the failure report, and all maps tick (about 27 000 ticks per 11 minutes at `TICK_MS=20`).

### Mortal bots

Bots can die and are resurrected (bots after 10 s, humans automatically after 60 s: `CombatConfig.BotResurrectTime`, `PlayerAutoResurrectTime`).

```
BOTS_TARGET=30
COMBAT=1
BOTS_IMMORTAL=0
```

Add `COMBAT_AGGRO=1` to make hostile creatures start fights with nearby bots and players. The ledger counts deaths (`scripts/report.sh failures`).

### Dungeon team

One team of five through one dungeon (world ids come from `docs/INSTANCES.md` and `docs/DUNGEONS.md`; 382 is a dungeon there):

```
COMBAT=1
BOTS_IMMORTAL=1
BOTS_DUNGEON=382
BOTS_TEAM=5
BOTS_DUNGEON_BOTS=5
BOTS_TARGET=5
BOTS_EVENTS=0
```

Several worlds in parallel, one team per world, round robin: `BOTS_DUNGEON=382,1263,1336`, `BOTS_TEAM=5`, `BOTS_DUNGEON_BOTS=15`, `BOTS_TARGET=15`. `scripts/instance-run.sh start "382 1263 1336" 5` writes these lines for you. Raids: `BOTS_TEAM=20` and `BOTS_TARGET=20` per raid world; several raid worlds at once can exhaust the memory limit, so run them in batches of three (`scripts/raid-batches.sh`). The team runs the instance, leaves, waits for the reset and goes again; entry, clear time, wipes and boss kills are counted in the failure ledger under `dungeon-*` and `encounter-*` categories.

### Veteran mode

```
BOTS_DUNGEON=382
BOTS_TEAM=5
BOTS_DUNGEON_BOTS=5
BOTS_TARGET=5
BOTS_VETERAN=all
```

`all` runs the veteran variant every time (only for worlds that have one), `off` never, `alternate` every second run. To make veteran instances for everyone who enters a world, including human groups, set `VETERAN_WORLDS=382` (or `all`). Veteran instances scale creature health by 1.5 and damage by 1.25 (`VeteranHealthScale`, `VeteranDamageScale` in `Game/Map/DungeonMapInstance.cs`). The veteran team level is the highest level of the world's veteran creature rows, capped at 50.

### Path bots

```
BOTS_TARGET=30
COMBAT=1
BOTS_IMMORTAL=1
BOTS_PATH_BOTS=10
```

Optionally `BOTS_PATH_FOCUS=Explore,PowerMap` to test only some mission kinds. Path bots are counted after dungeon, event and system bots, so with the other roles at 0 they are the first 10 bots.

### Synthetic credit off

```
BOTS_TARGET=30
COMBAT=1
BOTS_IMMORTAL=1
BOTS_SYNTHETIC=0
```

Quests whose objectives have no real in-world path will then stall (they are reported as stalled quests) instead of being credited. The `objective-synthetic` category of the report stays empty.

### Quiet server with no bots

```
BOTS_TARGET=0
COMBAT=0
COMBAT_AGGRO=0
BOTS_IMMORTAL=0
LOG_LEVEL=Info
```

Or `scripts/bots.sh disable` (bots log out, their characters stay in the database; companions added with `bot add` still work whenever the world is up). With `COMBAT=0` the stock stub combat is used: nothing loses health or dies.

## Unknown

- Whether `BOTS_TARGET` above 0 works with `COMBAT=0`: `scripts/bots.sh` says bots need combat; the code path with combat off was not read for this guide.
- Valid upper limits for `BOTS_TARGET`, `TICK_MS`, `BOTS_TEAM`: the code only parses integers; no maximum is enforced where it was read. Measured runs used up to 60 bots ([PERFORMANCE.md](PERFORMANCE.md)); start lower and watch the CPU trend.
