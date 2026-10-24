# Operations runbook

How to run, update, back up and observe the stack. Settings are in [CONFIGURATION.md](CONFIGURATION.md). All commands run from the repository root. The scripts use `docker compose` only; they do not call plain `docker` (exceptions are noted in the script table).

## Start, stop, status

```bash
docker compose up -d              # start (builds missing images; first build takes minutes)
docker compose ps                 # world should become (healthy); start_period is 180 s because tables and maps load slowly
docker compose stop               # stop everything, data is kept
docker compose start              # start again
docker compose restart world      # restart only the game server
docker compose down               # remove containers, keep the volumes
docker compose down -v            # ALSO deletes the database and the world cache volume (all accounts and characters)
```

Services: `db` (MariaDB 10.11, not published), `world` (24000), `auth` (23115), `sts` (6600). `world` creates the database schema (EF migrations) and the realm row; `auth` and `sts` wait until `world` is healthy. The world container is limited to `WORLD_MEM_LIMIT` (default 14g); if it exceeds that, only the world container is killed and restarted.

First-time setup is `./install.sh --client-dir DIR` (`--check` only checks a running stack; `./uninstall.sh` removes it, asking before the volumes; `setup.sh` is the old name). It generates or requires `data/tbl` and `data/map` to exist. Step by step: [QUICKSTART.md](QUICKSTART.md).

### Accounts and the server console

```bash
scripts/create-account.sh you@example.com      # prompts for the password (or set NF_ACCOUNT_PASSWORD)
docker attach --sig-proxy=false --detach-keys=ctrl-x nexusforeverbots-world-1    # server console; detach with Ctrl-X
```

Never press Ctrl-C while attached: it reaches the server process and stops it. Emails are stored lower-case.

Any console command also runs from the host without attaching: `scripts/gt.sh <command>` (examples: `scripts/gt.sh gt maps`, `scripts/gt.sh bot list`, `scripts/gt.sh gt show Creature2 26016`). It writes the command to `/cache/commands.txt` in the container, the server picks it up within a second, and the script prints the answer. It gives up after `GT_WAIT` seconds (default 20).

### World data

An empty world has no creatures. After the first start:

```bash
scripts/load-world-data.sh && docker compose restart world        # open-world zones (Alizar, Isigrol, Olyssia)
scripts/load-instance-data.sh && docker compose restart world      # instance spawn files, run after the line above
```

Both use the upstream world database checked out into `world-data/` (revision from `WORLD_DATA_REF`) and replace the rows of the zones they load, so re-running is safe. `scripts/import-research-spawns.sh` loads the repository's own spawn files (`data/spawns/`) into the world database as entity rows from id 9000000; it takes about a minute for 60 000 rows, and the world needs a restart afterwards.

## Logs

```bash
docker compose logs -f world              # follow (also: auth, sts, db)
docker compose logs --tail=80 world       # last lines; the last exception explains a crash on start
```

The world container uses the json-file log driver, 50 MB per file, 6 files, so long runs do not fill the disk. Set `LOG_LEVEL=Info` in `.env` to drop Trace and Debug lines.

Other runtime files live in the container's `/cache` (volume `world-cache`): `failure-report.txt`, `quest-coverage.txt`, `bot-feed.log` (20 MB, rotated, `.1` is the previous file), `bots.json`, quest outcomes and sweep results. Read them through the scripts below or `docker compose exec -T world cat /cache/<file>`.

## Update

```bash
git pull
docker compose up -d --build
```

Database migrations run when the world starts. Table caches in `/cache` are rebuilt by the server as needed. Take a backup first if the update changes the schema (new migrations appear in `Source/NexusForever.Database.*`).

## Database backup and restore

```bash
set -a; . ./.env; set +a
docker compose exec -T db mariadb-dump -uroot -p"$DB_ROOT_PASSWORD" --all-databases > backup-$(date +%F).sql
# restore into a running, empty stack:
docker compose exec -T db mariadb -uroot -p"$DB_ROOT_PASSWORD" < backup-2026-01-01.sql
```

What matters, in order:

1. **The database** (the dump above): accounts (auth), characters with items, quests, mail, guilds and residences (character), and spawn rows (world). The world database can be rebuilt with the load scripts, the other two cannot.
2. **Per-character state in `/cache`**: AMPs, runes, friends, neighbours, daily login streaks, path missions and tradeskills are kept as JSON files per character id in `/cache/<amps|runes|friends|neighbours|dailylogin|pathmissions|tradeskills>/` (docs/ASSUMPTIONS.md "Character JSON state"), not in the database. Back them up together with the dump, or characters lose that progress:
   ```bash
   docker compose exec -T world tar -C /cache -czf - amps runes friends neighbours dailylogin pathmissions tradeskills truth bots.json 2>/dev/null > cache-$(date +%F).tgz
   # restore (nobody online), then restart so nothing in memory overwrites it:
   docker compose exec -T world tar -C /cache -xzf - < cache-2026-01-01.tgz && docker compose restart world
   ```
   Missing folders are skipped (tar warns, the archive is still written). `truth/` is the clean-bot quest record and `bots.json` the companion list; keep them if you use those.
3. **`.env`**: the database passwords. Keep it out of backups you share.

What does not matter: `data/tbl`, `data/map` (regenerate from the client), `world-data/` (upstream checkout, re-cloned by the load scripts), the parsed table cache (`*.bin.cache` in `/cache`, rebuilt at start), reports and logs (`failure-report.txt`, `quest-coverage.txt`, `quest-check.txt`, `bot-feed.log`, `character-save.log`, `out/`), `soak/` and the images.

`scripts/sql.sh <world|character|auth> "<sql>"` runs one statement (output capped at 40 lines).

Changing `REALM_HOST`: edit `.env`, then `docker compose up -d`; the world rewrites the realm row at start.

## Bots

```bash
scripts/bots.sh status
scripts/bots.sh enable 50          # 50 bots, combat on
scripts/bots.sh count 30           # resize
scripts/bots.sh immortal on        # no damage to bots
scripts/bots.sh aggro on           # hostile creatures also attack anyone close
scripts/bots.sh combat off         # also disables bots
scripts/bots.sh disable            # log bots out, characters stay in the database
scripts/bots.sh focus 12,32        # take quests with these objective type ids first (deletes unfinished bot quests); focus off
scripts/bot-feed.sh -f             # live feed of what bots do (optional bot name, last N lines, at most 40 when not following)
```

Each of these edits `.env` and recreates the world container, so connected players are disconnected for a moment. Bot roles for dungeon, event, path and system runs are set in `.env` (see CONFIGURATION.md); `scripts/bots.sh` does not edit those.

## Scripts

Every file directly in `scripts/` (the one-line descriptions come from each script's header). The `scripts/research/` folder holds Python helpers for data research and reports; a few are named in the sections below.

| Script | What it does |
|---|---|
| `bench.sh` | CPU and memory bench for comparing builds or settings: `start <label> [bots] [KEY=VALUE...]` writes the pairs to `.env` and recreates the world, `sample <label> [n] [sec]` takes cgroup samples into `/tmp/bench-<label>.csv`, `finish <label>` writes exceptions, ticks and CPU/memory min/avg/max to `/tmp/bench-<label>.txt`. |
| `bot-feed.sh` | Live stream of bot events (time, bot, level, zone, event), read from `/cache/bot-feed.log`; `-f` follows. |
| `bots.sh` | Turn bots, combat, aggro, immortality and objective focus on or off in `.env` and recreate the world. |
| `clean-bots.sh` | `[lines]`: lists the clean-bot accounts and characters (`BOTS_CLEAN=1`), their quest counts and whether they are retired. Read only. |
| `clean-run.sh` | Clean-bot verification run in phases: `start [bots] [synthetic 0\|1]`, `restart`, `collect` (quest truth, database counts before/after, verdict) in `soak/clean-<time>/`. |
| `combat-check.sh` | Compares the combat numbers the server computes (`gt combat`) with expected values from the tables and sources; one line per check. Needs the world running. |
| `cov-test.sh` | One-hour coverage test: restarts the world with N bots and logs distinct completed quests every 10 minutes to `/tmp/cov.out`. |
| `cpu.sh` | CPU percent (of one core) over 5 s and memory of the world container, through cgroup counters. |
| `craft-summary.sh` | `[report file]`: crafting counters (crafts, catalysts, power cores, salvage, gathers, QA grants) from a failure report. |
| `create-account.sh` | Creates a game account with a throw-away world container that writes it to the auth database and exits. |
| `db-counts.sh` | Row counts of every non-clean account and character table; run before and after a clean run and diff. |
| `dungeon-check.sh` | `WORLD TEAM_SIZE BOT_COUNT SECONDS`: sets the dungeon values in `.env`, waits, prints the dungeon and encounter ledger and loaded instances. |
| `dungeon-collect.sh` | `OUTDIR WORLD TEAM`: collects a run started by `dungeon-start.sh` into `OUTDIR/WORLD.txt` and `OUTDIR/summary.txt`. |
| `dungeon-report.py` | Turns the newest `soak/dungeon-sweep/*/summary.txt` into `docs/DUNGEONS.md`. |
| `dungeon-start.sh` | `WORLD TEAM`: starts one bot-team dungeon run and returns at once. |
| `dungeon-sweep.sh` | `[dungeon_minutes] [raid_minutes]`: runs a bot team through every dungeon, expedition, adventure and raid world that has spawn data; writes `soak/dungeon-sweep/<time>/summary.txt`. Bots are immortal unless `IMMORTAL=0`. |
| `enable-tables.py` | `Name1 Name2 ...`: checks that a table class has the same field count as the `.tbl` file and marks OK tables as loaded in `GameTableManager.cs`. Developer tool. |
| `force-sweep.sh` | Forced quest sweep: `start [bots] [min per quest] [queue file]`, `wait`, `status`, `stop` (restores `.env`), `collect` (writes `docs/SWEEP-RESULTS.tsv` and prints a grouped summary). |
| `gate.sh` | 10-minute check with 30 bots (or the number given); writes `/tmp/gate-*.txt` and `/tmp/gate-done.txt`. Run in the background with `setsid nohup`. |
| `gen-floors.sh` | `[world id ...]`: builds prop floor-height sidecars (`data/map/<Asset>.nffloor`) from the client archive for instance worlds, one process per world. |
| `gen-maps.sh` | Runs the map generator over the client `Patch` folder and writes `.nfmap` files to `data/map-gen`; copy new files into `data/map` afterwards. It calls plain `docker run`; where that is unavailable, use `docker compose run --rm mapgen` instead. |
| `gt.sh` | Runs any world console command from the host and prints the answer. |
| `import-research-spawns.sh` | Loads `data/spawns/*.tsv` into the world database as entity rows 9000000 and up; restart the world afterwards. |
| `instance-run.sh` | `start "<worlds>" <team>` runs one bot team per world at once, `collect OUTDIR` writes the report and per-world table, `restore` restores `.env`. |
| `load-instance-data.sh` | Loads the upstream Instance SQL files that fit this fork's schema (only entity, entity_stats and entity_spline rows). |
| `load-world-data.sh` | Loads the upstream open-world spawn data (Alizar, Isigrol, Olyssia). |
| `memwatch.sh` | `[seconds between] [rounds]` (defaults 300 and 12): prints container memory and the managed heap, with and without a forced collection, to tell a leak from a lazy GC. |
| `outcomes.sh` | What bots did with each quest, kept across restarts: totals and the quests taken but never completed (`raw` prints the whole file). |
| `quest-run.sh` | `minutes bots`: quest throughput run with only quest bots, digest in `quest-run.out`. |
| `quest-truth.sh`, `quest-truth.py` | Clean-bot quest record: `quest-truth.sh <out-dir> [since]` writes it for one run, `quest-truth.py <dir>` summarises `quest-truth.tsv` and `quest-truth-open.tsv` per class (real, grant, heuristic, synthetic). |
| `raid-batches.sh` | `OUTDIR [minutes per batch]`: raid worlds in batches of three, because all eight at once hit the 14g memory limit. |
| `remaining.sh` | `[soak dir]`: estimates how much is left to fix (handled and stored-only spell effect types, unhandled counts, quest coverage) from a soak snapshot or the live server. |
| `report.sh` | `failures` (default) or `quests`: prints the failure report or the quest coverage file from the running world. |
| `sample.sh` | One line per call (time, process CPU seconds, RSS, bots online, ticks, quest coverage); append to a file and diff two lines for CPU percent. |
| `save-restart-test.sh` | `restart [item id]` or `fault`: character save safety (shutdown save, row-by-row retry). Needs bots with quests. |
| `save-stress.sh` | Random quest transitions with saves and relogs, then compares every bot's quests with its database rows. |
| `soak.sh` | `[hours] [bots] [minutes between snapshots]`: unattended bot run that collects data; see below. |
| `soak-summary.sh` | `[soak dir]`: summarises a soak run (default: the newest in `soak/`). |
| `spell-data.sh` | `[output file]`: dumps the table data behind every unhandled spell effect, prerequisite type and frequently failing spell. Needs the world running with bots active. |
| `sql.sh` | `<world\|character\|auth> "<sql>"`: one SQL statement against a database of the stack, output capped at 40 lines. |
| `status.sh` | One-screen digest of the last `gate.sh` run (exceptions pass/fail, top failures, maps, stats) and git state. |
| `summon-check.sh` | Regression check for the runaway summon chain: places self-summoning creatures next to a bot and fails if summons, the action queue or the heap pass their caps. |
| `system-counts.sh` | `[report file]`: ok and fail counts per system probe kind from a failure report (default: the running world). |
| `tbl-dump.py` | `Name [maxrows]`: dumps rows of a `.tbl` straight from `data/tbl` without the server. Strings print as offsets, numbers raw. |

Related files outside `scripts/`: `install.sh` (guided install, `--client-dir`, `--project`, `--check`, `--yes`, `--bots N`, `--combat`, `--realm-host`, `--account`; see `./install.sh --help`), `docker-compose.yml` tool services (profile `tools`): `mapgen`, `uiextract`, `dotnettools`, `nugetaudit`, run with `docker compose run --rm <service>`.

Research helpers worth knowing: `scripts/research/questwhy.py <dump> out.tsv [out.md]` (first failing step per open quest, input from `gt questdump`), `scripts/research/openwhy.py` (reason per open quest from sweep results and the bot feed), `scripts/research/instance_table.py` (used by `instance-run.sh collect`).

## The failure ledger

The server counts everything it could not handle or wants to measure in a ledger of deduplicated counters (`Diagnostics/FailureLedger.cs`). The file `/cache/failure-report.txt` is rewritten every 30 seconds once anything has been recorded and starts from zero at each restart. `scripts/report.sh` prints it; `gt report` prints the same in the console; `gt reset` clears the counters.

Layout:

```
Failure report at <UTC time>
quest-coverage: <completed> / <doable> doable quests completed at least once, ...
== <category> (<n> kinds, <total> total)
  <count>x <key> [<example>]
```

Entries are grouped by category and sorted by count within a group. Categories are free-form strings chosen by the code; a few you will meet most often:

| Category | Meaning |
|---|---|
| `map-update-exception`, `spell-update-exception`, `grid-action-exception`, `spell-tick-exception` | An exception caught in an update loop. Any of the first two means a bug; the gate in this repository treats them as failure. |
| `unhandled-spell-effect`, `unhandled-prerequisite-type`, `effect-stored` | Spell effects and prerequisite types the server has no handler for, or stores without applying. |
| `cast-failed` | A spell cast failed; the key says why. |
| `quest-stalled`, `bot-failed` | Quests a bot could not finish in time; bots that failed. |
| `objective-synthetic` | Objectives credited directly by a bot (`BOTS_SYNTHETIC=1`), counted per type. |
| `system-ok`, `system-fail`, `system-skip`, `system-info` | Results of the system probes (see `BOTS_SYSTEM_BOTS`); `scripts/system-counts.sh` totals them. |
| `system-unhandled-message` | Client message opcodes the server has no handler for. |
| `dungeon-*`, `encounter-*` | Bot-team dungeon runs: entered, cleared, wipes, timeouts, boss kills, ability casts. |
| `player-death` | Deaths, with the killer. |

`scripts/report.sh quests` prints the quest coverage file, written the first time a bot looks at quests after a start. If the report says "Not written yet", wait a minute after bots start.

## Memory and heap diagnostics

The failure report starts with a `== memory` line, also printed by `scripts/gt.sh gt memory show`. It is written by the report timer thread, so it is still updated when the world thread hangs. Its fields:

| field | meaning |
|---|---|
| heap, GC heap, fragmented, committed | managed memory: live objects, what the GC holds, free gaps inside it, what it took from the OS |
| limit | the .NET heap limit: 75% of the container limit (10752 MB with `WORLD_MEM_LIMIT=14g`). An "Out of memory." while docker shows 10-12 GiB is this limit |
| gen0/1/2 | collection counts; gen2 rising quickly means the GC is under pressure |
| working set | process memory as the OS sees it (heap plus runtime, maps, native) |
| maps, entities | loaded maps and entities in them |
| queued grid actions (largest map, peak) | entity adds/removes waiting for the map update; normally a few hundred to about 1100 |
| spell summons (per-map peak), pending spells, queued respawns | units created by spells, casts in flight, respawns waiting |
| ledger keys, feed | failure ledger size and the bot feed's write queue |

Normal with 30-60 bots: heap about 2-4 GB, flat after the first instance worlds have loaded (memory rises in steps as instances load, then stays flat). `soak.sh` appends heap, committed, limit, gen2 and queued actions to every snapshot line of `events.log`.

A growing heap. A managed heap that grows by 1 GiB within 2 minutes writes a `heap-growth` ledger entry with the memory line at that moment (at most every 10 minutes). The last out-of-memory case looked like this: the grid action queue climbed from 0 to over a million and spell summons doubled, because creatures summoned copies of themselves ([PERFORMANCE.md](PERFORMANCE.md), "Out of memory in world 1061"). So if it happens again, check:

1. the queue and summon fields: a queue in the tens of thousands or summons near the 400-per-map cap means a runaway spawn or cast chain (`summon-refused` and `grid-action-backlog` ledger entries name the map and the action types);
2. entities: a steadily rising count with a flat queue means something is created and never removed;
3. heap with a flat entity count: run `scripts/memwatch.sh` (container memory and heap with and without a forced collection). If the heap drops after the forced collection, the GC is lazy, not leaking;
4. `docker compose ps world` for a restart (Up time reset) and `docker compose logs world | grep -c "Out of memory"`.

`scripts/summon-check.sh` is the regression check for the summon chain. The gate fails on any `heap-growth` entry, a summon peak over 400 or a queue peak over 20000.

## Gate and soak

### 10-minute gate

```bash
setsid nohup scripts/gate.sh 30 </dev/null >/tmp/gate.log 2>&1 &
# about 11 minutes later:
scripts/status.sh
```

Pass criteria used in this repository: no `map-update-exception` or `spell-update-exception` lines in the failure report, every map ticking (about 27 000 ticks per 11 minutes at the default `TICK_MS=20`), CPU in the normal range for the bot count (see CONFIGURATION.md for measured values).

### Soak

```bash
setsid nohup scripts/soak.sh 3 60 10 </dev/null >soak.log 2>&1 &     # 3 hours, 60 bots, snapshot every 10 minutes
scripts/soak-summary.sh                                                # newest run
scripts/remaining.sh soak/<time>                                       # how much is left
```

What `soak.sh` does:

1. Writes these values into `.env` (and leaves them there): `BOTS_TARGET=<bots>`, `COMBAT=1`, `COMBAT_AGGRO=0`, `PROFILE=1`, `BOTS_IMMORTAL=1`, `LOG_LEVEL=Info`, `TICK_MS=20`. Restore your own values afterwards.
2. Starts the stack, waits 240 s, then every N minutes takes a snapshot in `soak/<start time>/snap-NNN/`: `failures.txt`, `quests.txt` (first two snapshots), `maps.txt`, `profile.txt`, `memory.txt`, `effects.txt`, `bots.txt`, `quests-db.txt` (bot quest rows from the database), `time.txt`.
3. Appends CPU percent and memory to `stats.csv` and events to `events.log`. If the world container is not running it starts it. If CPU exceeds 70% of the machine (cores x 70%) in two snapshots in a row, it lowers the bot count by 10, never below 20.
4. Dumps spell data (`spell-data-*.txt`) at snapshot 3 and every 12th snapshot, and at the end.
5. At the end writes `remaining.txt`, `quest-coverage-final.txt`, `errors.log` (deduplicated warnings and errors), `world-log-tail.txt.gz`, `summary.txt`, `env.txt` (passwords masked) and `bundle.tar.gz`. The bots keep running; stop them with `scripts/bots.sh disable`.

Reading `summary.txt`: events (restarts, throttling), CPU min/avg/max, the first and last bot quest counts in the database, quest coverage, the top of each failure group, map update timings, unhandled spell effects and the most frequent warnings. Judge CPU and memory by the trend in `stats.csv` (flat after warm-up is good; a sustained climb means something accumulates), not by one number. Memory rises in steps when instance worlds are first loaded (see `docs/BOT-FEED.md`, "2026-10-02 soak finding") and then stays flat.

## Disk and file growth

Bounded by design: the world container's docker log (json-file, 6 x 50 MB), `/cache/bot-feed.log` (20 MB, one rotated copy), `/cache/character-save.log` (restarted at 5 MB), the failure report and coverage files (rewritten, not appended).

Grows without a cap, clean up by hand:

| what | where | growth | clean up |
|---|---|---|---|
| soak runs | `soak/<time>/` (bundle, snapshots, spell-data dumps) | about 1-2 MB per hour of soak (the 36-hour run: 48 MB); 53 runs took 272 MB | delete old run folders; keep the ones you cite |
| unanswered console commands | `/cache/out/` in the container | `scripts/gt.sh` deletes its answer after reading it; answers to commands that timed out stay (hundreds of small files after months) | `docker compose exec -T world sh -c 'rm -f /cache/out/*'` while no `gt.sh` call runs |
| script console output | repository root: `soak.log`, `gate.log`, `quest-run.log`, `quest-run.out`, `soak-summary.txt`, `spell-data*.txt` | one file per run type, overwritten or appended by the command line you used | git-ignored; delete when done. Prefer writing them to `/tmp` or the run folder |
| gate and bench output | `/tmp/gate-*.txt`, `/tmp/bench-*` | small, overwritten per run | none needed |
| per-character JSON | `/cache/<state folder>/<character id>.json` | one file per character, never deleted when a character is | harmless; part of the backup above |
| images | `docker image ls` | each `docker compose build` leaves the old image untagged | `docker image prune` |
| database | `db-data` volume | clean-bot accounts are never deleted (each clean run adds characters) | deliberate; see [TESTING.md](TESTING.md#clean-bot-quest-verification) |

## Common failures and fixes

These are the ones documented in the scripts and the docs in this repository. Install-time errors are in [QUICKSTART.md](QUICKSTART.md#troubleshooting).

| Symptom | Cause and fix |
|---|---|
| `install.sh` says client data is missing | `data/tbl` or `data/map` is empty or nested one level too deep (`data/tbl/tbl`). |
| `dependency failed to start: container ...-world-1 is unhealthy` | The world crashed on start. `docker compose logs --tail=80 world`; the last exception says why. |
| `Could not find file '/data/map/....nfmap'` | A map is missing; regenerate the maps or copy all of `map/`. |
| Client: "No realms are available at this time" | The auth server cannot open `REALM_HOST:24000` from inside its container. Usually a host firewall (ufw) blocks container-to-host traffic; allow the Docker subnet to port 24000/tcp. `./install.sh --check` tests this. Right after a restart it can take about 15 seconds. |
| Client: "Cannot connect to NCSoft login services" | The client is not redirected; use the hosts-file method in [QUICKSTART.md](QUICKSTART.md#2-connect-the-game-client) and add every host the message lists. |
| `Realm id 1 doesn't exist in the database` | `REALM_HOST` is empty so the realm row was not created. Set it in `.env` and run `docker compose up -d`. |
| `Access denied` for the database user after editing `.env` | A DB password was changed after the first start. MariaDB reads it only when the volume is created; see `docker compose down -v` above. |
| Login fails with wrong credentials | No such account or wrong password; create it with `scripts/create-account.sh`. |
| Empty world, no creatures | World data not loaded: `scripts/load-world-data.sh` then `docker compose restart world`. |
| A setting in `.env` has no effect | A variable with the same name is exported in your shell and overrides `.env`: `unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1)`. Also recreate the container: `docker compose up -d world`. |
| `scripts/gt.sh`: "No answer after 20 seconds" | The world is not up or still loading (`docker compose ps`); raise `GT_WAIT`. |
| `scripts/report.sh`: "Not written yet" | The report is written 30 s after the first recorded event, the coverage file when a bot first looks at quests. Wait a minute after bots start. |
| World container restarts during a run with many instance worlds | It reached `WORLD_MEM_LIMIT` (14g). Run raids in batches of three (`scripts/raid-batches.sh`), lower the number of loaded worlds, or raise the limit. |
| High CPU with many bots | Expected: CPU follows the number of active map grids (`docs/PERFORMANCE.md`). Check that it is not rising over time. `LOG_LEVEL=Info` reduces it. |
| Windows error on closing the client | Seen on exit with the 64-bit client; the cause is in the client executable. Characters and settings were intact afterwards. |
| French/German "not loaded" warnings in the world log | Harmless; only `en-US.bin` is required. |

## Unknown

- The exact output sections of `scripts/remaining.sh` and `scripts/status.sh` beyond what their headers and code state; they were read, not run.
- Whether `docker compose down` followed by `up` keeps the bot list in `bots.json`: it is stored in the `world-cache` volume, which `down` keeps and `down -v` deletes (from the compose file); not tested here.
- Recommended minimum host size: the measured bot runs used 2.5-4.5 GiB for the world container alone; no tested minimum is documented.
