# Testing

How a change is checked, which scripts do it, and what the numbers mean. There is no unit test project: the compiler (the Docker build) and runs with bots are the tests. Running the stack is in [OPERATIONS.md](OPERATIONS.md), settings in [CONFIGURATION.md](CONFIGURATION.md), what bots do in [BOTS.md](BOTS.md), code layout in [ARCHITECTURE.md](ARCHITECTURE.md).

Only thresholds that are written down in this repository (scripts, docs) are stated here. Where the repository states none, this document says so.

## The loop

1. Change code and build: `docker compose build`. The image is the only place the code is compiled; a compile error fails here.
2. Run the 10-minute gate (below). A clean gate is the minimum for pushing.
3. For data and trends, run a soak. For a specific area, run the focused check for it (combat, dungeons, events, paths, systems).
4. Read the failure report, fix the most frequent items first, commit small.

## The 10-minute gate

```bash
setsid nohup scripts/gate.sh 30 </dev/null >/tmp/gate.log 2>&1 &     # about 11 minutes
scripts/status.sh                                                      # then read the digest
```

`scripts/gate.sh [bots]` (default 30) clears the shell variables that would override `.env`, runs `docker compose up -d`, waits 90 s, sets the bot count with `scripts/bots.sh count`, waits 600 s, and then stores:

| File | Content |
|---|---|
| `/tmp/gate-fail.txt` | the failure report (`/cache/failure-report.txt`) |
| `/tmp/gate-maps.txt` | `gt maps`: per world, average and maximum update time in ms, active grids and the tick count |
| `/tmp/gate-stats.txt` | `docker stats` of the world container (CPU, memory) |
| `/tmp/gate-done.txt` | marker written last; its absence means the gate is still running or was not run |

The gate uses whatever `.env` holds (the settings for the run you want to test, for example `COMBAT=1`, `BOTS_IMMORTAL=1`; a 30-bot QA run is the first recipe in CONFIGURATION.md). It changes the bot count only.

`scripts/status.sh` prints a one-screen digest: whether the gate finished, the quest-coverage line, `system-fail` entries, **exceptions PASS or FAIL**, the top of the failure report, the first lines of the map timings, the stats line and the git state. Do not read the raw files first.

### The thresholds

| Check | Pass | Where documented |
|---|---|---|
| Update exceptions | No `map-update-exception` and no `spell-update-exception` in the failure report. `status.sh` counts matching lines and prints FAIL if there is any. | OPERATIONS.md, `scripts/status.sh` |
| Maps keep ticking | Every loaded map has a tick count of about 27 000 per 11 minutes at the default `TICK_MS=20` (the ceiling at 50 updates per second is 33 000). A map with far fewer ticks than that is stalled; a map that was only loaded part of the time (instance and idle-unloaded worlds) has fewer by design. A mortal run recorded about 28 000 ticks per 10 minutes. | OPERATIONS.md, COMBAT-MATH.md |
| CPU | High absolute CPU with many bots is normal. Judge the trend, not the level: after a 30-minute warm-up CPU and memory must stay flat. Measured levels for orientation: about 100-130 % (one core = 100 %) for 30 bots, 140-170 % for 60. | CONFIGURATION.md, the project's working notes ("CPU rule") |
| Character saves | No `character-save-failed` entry (any cause). `scripts/gate.sh` writes `/tmp/gate-verdict.txt`, `status.sh` prints `character saves: PASS` or FAIL with the causes. The reasons per row are in `character-save-cause` and `/cache/character-save.log`. | ASSUMPTIONS.md "Character saves" |
| Memory | Flat after warm-up. Steps when instance worlds are first loaded are expected (BOT-FEED.md); a sustained climb is a failure. | OPERATIONS.md, BOT-FEED.md |
| Instances released | The failure report's third line (`instances: N live (...), C created, R released`) returns to the value it had before the instance teams started once every team has left and 120 s have passed; every instance created by a run shows up in `instance-released`. An instance that stays live with nobody inside is a leak. To check it at the end of a run without a restart, use `scripts/gt.sh bot target 0` (logs every bot out) and read the line after 3 minutes; `scripts/bots.sh count 0` recreates the container and resets the counters. | ASSUMPTIONS.md "Instances, groups and encounters" |

The documents state no numeric threshold for any other category in the report; for those, compare with the previous run of the same configuration (see "What counts as a regression").

## Soaks

```bash
setsid nohup scripts/soak.sh 3 60 10 </dev/null >soak.log 2>&1 &     # hours, bots, minutes between snapshots
scripts/soak-summary.sh [soak/<time>]                                 # digest, newest run by default
scripts/remaining.sh soak/<time>                                      # how much is left (counts from the last snapshot)
scripts/spell-data.sh [file]                                          # table data behind unhandled effects and prerequisites (world must be running with bots)
```

A soak collects data; it is not a place to wait. What it writes and what it changes in `.env` is in OPERATIONS.md ("Soak"). Points that matter for testing:

- Snapshots go to `soak/<start time>/snap-NNN/` (failure report, map timings, profile, memory, effects, bot list). `stats.csv` has CPU and memory per sample; `events.log` has restarts and throttling.
- **Throttling**: if CPU stays above 70 % of the machine (cores x 70 %) in two snapshots in a row, `soak.sh` lowers the bot count by 10 (never below 20) and logs it. A throttled run changes the load; note it when comparing runs.
- Spell data is dumped at snapshot 3, every 12th snapshot, and at the end.
- `summary.txt` has the numbers: events, CPU min/avg/max, first and last bot quest counts in the database, quest coverage, top of each failure group, map timings, unhandled spell effects, most frequent warnings. `stats.csv` has columns `time,cpu_percent,memory,restarts,bots_target`, so CPU per bot is `cpu_percent / bots_target` (the summary does not print it).
- Soak length: a long run is for the final check of a milestone. For verifying a single change, 1 hour with 60 bots is used in the repository's notes; `scripts/cov-test.sh [bots]` is a 1-hour variant that only logs quest coverage every 10 minutes to `/tmp/cov.out`; `scripts/quest-run.sh minutes bots` runs only quest bots.
- Read CPU and memory as a trend in `stats.csv` after the first 30 minutes. `scripts/memwatch.sh` prints container memory and the managed heap with and without a forced collection to tell a leak from a lazy collector. `scripts/sample.sh` appends one line (time, CPU seconds, RSS, bots online, ticks, quest coverage) per call; the difference of two lines gives CPU percent. `scripts/bench.sh start|sample|finish <label>` is the gate split into steps with many CPU samples, for comparing two builds (`docs/PERFORMANCE.md` is such a comparison; it found about ±5 % run-to-run noise for one build, so smaller differences are not evidence).

## Clean-bot quest verification

The regular bots carry XP, quests, skills and items from many earlier runs, harness level changes and grants, and the quest-coverage line counts a quest as done when any character ever completed it. A completion by them cannot be traced to how it happened. Clean-bot mode can: `BOTS_CLEAN=1` (`NF_BOTS_CLEAN`) gives every ambient bot an account of its own made for clean runs, and every completion of such a bot is written down with the paths that credited it.

### What a clean bot is

- Accounts `bot.clean-N@bots.invalid` (N = 0, 1, 2, ... in creation order), created on demand with a new character: even N start in the tutorial (character creation start PreTutorial, world 3460; the server has no start location for the Arkship start), odd N on Nexus (the skip-tutorial start). Both start at level 1 to 3 with an empty quest log.
- The harness never changes their level: no random start level, no level rotation; `bot quest <id>` (forced test path) still sets the level and is tagged `level-set`. They level only by XP from kills, quests, events and path missions.
- All of them are quest bots (path missions first for the first `BOTS_PATH_BOTS`); the dungeon, event and system-probe roles, which grant items, credits and levels, are off. Companion bots (`bot add`) are refused in clean mode.
- They never pick the 128 quests of zone 12, a hidden zone of developer and beta quests ("Level 15", "[Beta] Weekly Costume Piece!", "Ready Your Character for the Malgrave Trail" with 55 million XP). Characters that completed one before this rule (the first clean runs) carry `dev-zone-xp` on every later completion.
- **Retired** characters: a clean character with `dev-zone-xp` or `level-set` is no longer a fresh start. Clean runs do not log its account in again and make a new clean account instead (the log line "Bots: target population ..." gives the number retired). The account and character stay in the database.
- Clean runs log in only clean accounts; other runs never log them in (`BotManager.Initialise`). The characters stay in the database; the next clean run reconnects to them, after a restart too. There is no deletion tooling.
- List them: `scripts/clean-bots.sh [lines]` (account, character id and name, world, completed quest rows, quests in the log, account creation time, status active / retired / no character yet; read only). `gt questtruth` prints how many are online.

### How a completion is classified

`Diagnostics/QuestTruth.cs` follows each quest of a clean character from acceptance (`QuestManager.QuestAdd`) to completion (`QuestManager.QuestComplete`) and appends one row per completion to `/cache/truth/quest-truth.tsv`. State of quests still in the log is kept in `/cache/truth/quest-truth-state.json` (written every 30 s and by the shutdown save), so a restart does not lose it. The files live in a sub folder because the game-table cache (`FileCache`) deletes every file directly in `/cache` when the shared assembly changes, which a rebuild touching the database or shared projects does (`quest-outcomes.txt` and `bot-feed.log` are lost that way too). Without a state file the completion counts are rebuilt from the rows; retirement is read from the database (completed zone 12 quests), not from the state.

| Column | Content |
|---|---|
| `time_utc`, `quest`, `character`, `character_id` | when, which quest, which clean character |
| `level`, `accept_level` | character level at completion and when the quest was taken |
| `how` | `synthetic` (some objective credited directly, or a public event given up), else `heuristic` (some objective or the quest relies on a QA heuristic), else `grant` (a harness grant was used), else `real` |
| `heuristic` | kinds: `unknown10`, `indicator`, `reward-pane` (from the objective data, see BOTS.md "Synthetic credit and QA heuristics"), `zone-inside` (EnterZone credited by the bot because it already stood in the zone), `remote-turn-in` (a quest without objectives turned in away from its receiver: nothing of it was played), `no-turn-in-path` (`turn_in` is `no-receiver`), `dev-zone`, and `synthetic` / `event-gave-up` |
| `grants` | kinds received while the quest was in the log: `item-grant`, `material-grant`, `schematic-grant`, `tool-grant`, `catalyst-grant`, `powercore-grant`, `relearn-credit`, `talent-point-grant`; tradeskill grants also count for every later quest that needs a tier of that skill; `level-set` and `dev-zone-xp` count for every later quest of the character; `untracked-accept` when the acceptance was not seen |
| `objectives` | per objective `index:type:path` (`real`, or the heuristic / synthetic kinds above) |
| `turn_in` | the turn-in a player would have had, by the server's own rules in `QuestComplete`: `receiver-near` (a receiver creature in view), `communicator` (the quest may be turned in by communicator), `exempt` (imbuement and contract quests), `auto` (auto-complete), `receiver-not-near` (receivers exist, none in view), `no-receiver` (none of these: a player could not turn it in) |
| `synthetic_on`, `immortal`, `minutes` | run settings, minutes from acceptance to completion |

Grants are attached to every quest in the log at the time (conservative: a grant can be counted against a quest it did not help, never the other way round). Bots skip the giver and receiver checks (BOTS.md "Quest picking"); `turn_in` shows how far that matters per completion.

A quest is **verified** when at least one clean completion has `how = real`. Heuristic and grant completions are counted separately per kind; synthetic ones are not verified. Turning in at a distance is shown, not counted against a quest with objectives.

`gt questtruth` writes `/cache/truth/quest-truth-open.tsv`: every doable quest (BOTS.md "Reading the quest-coverage line") no clean bot completed, with the reason: `in-progress`, `stalled` (clean bots took it and gave up; detail = the objective states of the last stall), `taken`, else what the online clean bots say to it: `candidate` (one could take it, not picked yet), the quest manager's refusal (prerequisite, level, ...), `faction`, or `window` (outside every clean bot's pick window, detail = their level range). The last column is the last stall any bot ever reported (`quest-outcomes.txt`).

In clean mode the quest-coverage line and the bots' "nobody completed it yet" preference count clean completions only. The doable list depends on `BOTS_SYNTHETIC`: with `0`, quests with an objective that has no in-world path are not indexed at all (3053 doable instead of 4229 on 2026-10-18), so compare open lists only between runs with the same setting.

### Running it

Short check (30 minutes, phases, so no step has to wait in one command):

```bash
scripts/clean-run.sh start 30 0     # BOTS_CLEAN=1, 30 bots, BOTS_SYNTHETIC=0; stores the database counts of all other accounts and characters
scripts/clean-run.sh restart        # optional, mid-run: the clean bots must come back with their quests
scripts/clean-run.sh collect        # after 30 minutes: quest truth, failure report, maps, CPU, database counts after + verdict
```

The run folder is `soak/clean-<time>/` (`verdict.txt`, `quest-truth*.tsv`, `quest-truth-summary.txt`, `db-before.txt` / `db-after.txt`, `failures.txt`, `maps.txt`, `clean-bots.txt`). `verdict.txt` requires 0 map/spell-update exceptions, 0 character-save failures and identical row counts for every other account and character (`scripts/db-counts.sh`: accounts, characters, every `character_*` table, items, mail, residences, guild members, plus checksums of the account and character rows).

The long verification run (60 clean bots, 36 hours, snapshots every 10 minutes; `soak.sh` sets `BOTS_IMMORTAL=1`, run the mortal variant with `scripts/clean-run.sh` instead):

```bash
sed -i 's/^BOTS_CLEAN=.*/BOTS_CLEAN=1/; s/^BOTS_SYNTHETIC=.*/BOTS_SYNTHETIC=0/' .env   # both keys must exist in .env (add them otherwise)
docker compose stop world                                   # count only after the old world saved its online bots on shutdown
scripts/db-counts.sh > /tmp/db-before.txt
setsid nohup scripts/soak.sh 36 60 10 </dev/null >soak.log 2>&1 &
# afterwards:
scripts/soak-summary.sh                                     # includes the quest-truth table
scripts/db-counts.sh > /tmp/db-after.txt; diff <(grep -v '^#' /tmp/db-before.txt) <(grep -v '^#' /tmp/db-after.txt) && echo unchanged
```

`soak.sh` writes the quest truth every 6th snapshot and at the end into the run folder (rows since the start of the run only).

### Reading quest-truth

`scripts/quest-truth.sh <dir> [since]` (or the files a run wrote) prints the summary from `scripts/quest-truth.py`:

```
| class | quests | kinds |
| verified (real objectives only, no grant) | ... |
| heuristic | ... | remote-turn-in n, indicator n, ... |
| grant | ... | item-grant n, material-grant n, ... |
| synthetic | ... | synthetic n, event-gave-up n |
| not completed | ... | stalled n, window n, prerequisite quest n, ... |
```

Each quest counts once, with its best completion (real before grant before heuristic before synthetic). For a single quest: `awk -F'\t' '$2==<id>' quest-truth.tsv` shows every completion with its objective paths; `grep -P '^<id>\t' quest-truth-open.tsv` the reason it is still open.

## The mortal-bot run

Immortal bots (`BOTS_IMMORTAL=1`, the soak and gate default) test the server without deaths. A **mortal** run tests combat, death and resurrection with real damage:

```
BOTS_TARGET=30
COMBAT=1
BOTS_IMMORTAL=0
BOTS_DAMAGE=1
```

(`COMBAT_AGGRO=1` additionally makes hostile creatures start fights.) Run it for the length of a gate or a half hour, then read the `combat-stats` and `player-death` categories (see BOTS.md, "Reading the combat-stats lines"). The recorded run in COMBAT-MATH.md ("Stage E") is the model: 30 minutes, 30 bots, 0 map/spell exceptions, every map ticking, flat memory, then the counts of deaths and resurrections, kills, damage effects by bots and creatures, and the creatures that kill the most bots. Expect deaths and dungeon wipes: bots do not heal each other or dodge telegraphs. Judge by exceptions, ticks and trend, by `bot deaths ≈ resurrections`, and by killers that look wrong (a creature that kills many bots in seconds is a balance or data question; level-mismatch deaths are a harness effect).

## Focused checks

### `scripts/combat-check.sh`

Compares the combat numbers the running server computes with numbers computed independently from the game tables and the documented sources.

```bash
scripts/combat-check.sh
```

How it works: `scripts/research/combat_expect.py` reads the tables (no C# code involved) and prints `key expected tolerance source`; for a fixed list of cases (levels 1, 10, 30 and 50, chosen spells and creatures, one case with a gear power override) the script asks the server with `gt combat <level> <spell4|0> <creature|0> <power|0>` and collects the `value` lines. `scripts/research/item_expect.py check` adds expected item stats (compared to archived item tooltips) which are checked against `gt itemstats`. Each key prints one line:

- `ok key value`: the server value is within the tolerance of the expected value (relative to the larger of the expected value and 1).
- `DIFF key server X expected Y (source)`: the numbers differ.
- `MISSING key expected Y`: the server did not report the key.

The last line is `combat-check: N ok, N differ, N missing`. The exit code is 0 only when there are no differences and nothing is missing. Run it after any change in `Game/Combat`, `SpellFormula`, `DamageMath`, `ItemStats` or the spell effect handlers that touch damage. The world must be running; bots are not needed. The formulas and their sources are in COMBAT-MATH.md.

### `scripts/outcomes.sh`

```bash
scripts/outcomes.sh        # totals and the quests that were taken but never completed, most stalled first
scripts/outcomes.sh raw    # quest, accepted, completed, stalled, last stall
```

Reads `/cache/quest-outcomes.txt`, which persists across restarts. First line: quests taken by bots, completed at least once (split into completed by the normal picker and by the forced path), and taken but never completed. The list below shows each such quest with its last stall text (objective ids, types, progress). This is the work list for quest fixes; `docs/QUEST-COVERAGE.md` and `docs/OPEN-QUESTS.tsv` hold the analysis of why each open quest fails, `scripts/force-sweep.sh` tests them one by one.

### Dungeons and raids

- `scripts/dungeon-check.sh WORLD TEAM BOTS SECONDS`: one team in one world, then the `dungeon-*` and `encounter-*` categories and the loaded instances.
- `scripts/dungeon-sweep.sh [dungeon_minutes] [raid_minutes]`: every dungeon, expedition, adventure and raid world with spawn data, one after the other; `scripts/dungeon-report.py` turns the result into `docs/DUNGEONS.md`.
- `scripts/instance-run.sh start|collect|restore`: several worlds at once, one team each. `scripts/raid-batches.sh`: raids in batches of three because all of them at once reach the memory limit.
- What to read: entered, cleared, wipes, timeouts, `dungeon-stalled` and `dungeon-remaining` (what was left), `encounter-boss-killed` per boss, `encounter-*-exception` (should be zero).
- Entry and exit paths: `dungeon-entry-mode` (portal, finder or teleport and why the others were not possible), `dungeon-group` (invites or server members), `matching-game` (pending, accepted, started, transferred), `instance-pending-removal` and `instance-leave-pending` (leaving through the group), `dungeon-leave-failed` (should be zero), `instance-released`.
- Instance quests: `instance-quest-completed` (real, heuristic or synthetic per quest); `scripts/research/instance_quests.py /cache/quest-targets.tsv <failure report> out.tsv` lists the quests that need an instanced world with their objectives and state. `scripts/research/instance_baseline.py` lists every instanced world with its spawns, bosses and boss spell lists.

### Systems, events, paths

Set `BOTS_SYSTEM_BOTS`, `BOTS_EVENTS` / `BOTS_EVENT_BOTS` or `BOTS_PATH_BOTS` (with `BOTS_PATH_FOCUS` for one mission kind) and read `scripts/system-counts.sh` (ok and fail per probe), the `event-*` and `path-*` categories. A probe with `system-fail` is a defect in the system or in its probe; read the key and example. `docs/SYSTEMS.md` maps systems to drivers and evidence.

### Character saves

`scripts/save-stress.sh [minutes] [transitions per call] [1|0]` (world running with bots): `gt queststress` runs random quest transitions on a fixed quest mix for every online bot (accept, re-accept, abandon, achieve, complete, repeat after a reset, mention, objective credit), with 1 a save after every call (overlapping saves), and a third of the bots relog every 40 calls; then `gt questverify` saves every bot and compares its quests with its database rows (`/cache/quest-verify.txt`, ledger `character-quest-mismatch`, one line per bot with its active quest ids). Pass: no `character-save-failed`, `verify: N bots, 0 with differences`. `gt questresync` makes the next save of every bot rewrite its quests from the game state (the repair that runs after a failed quest batch). The causes found with it are in ASSUMPTIONS.md "Character saves".

`scripts/save-restart-test.sh restart` (world running with bots): gives one bot an item and an objective step through `gt savetest`, stops the stack 5 s later (`docker compose stop`, so the shutdown save runs), starts it again and checks both in the database; pass = item and step present (`/cache/shutdown-save.log` shows how many characters were saved and how long it took). `scripts/save-restart-test.sh fault` injects a bad row into one character batch and checks that the other rows of the batch are written and only the bad one is dropped (ledger `character-save-row-dropped`).

### Quest coverage and sweeps

`scripts/report.sh quests` (coverage file), `scripts/force-sweep.sh start|wait|status|collect|stop` (forced run over every open doable quest; results in `docs/SWEEP-RESULTS.tsv`), `scripts/research/questwhy.py` and `openwhy.py` for the reason per open quest.

## The failure report

`scripts/report.sh failures` prints `/cache/failure-report.txt`: first the quest-coverage line, then categories (`== category (kinds, total)`) with the most frequent keys. It is rewritten every 30 s and reset at restart, so a run must be read from the file of that run (the gate copies it; the soak stores one per snapshot). Categories you will read most, grouped by what a rise or a nonzero value means:

| Group | Categories | Meaning |
|---|---|---|
| Stability (should be absent) | `map-update-exception`, `spell-update-exception`, `grid-action-exception`, `spell-tick-exception`, `dungeon-team-exception`, `encounter-cast-exception`, `creature-spell-exception`, `bot-gear-exception`, `db-save-failed`, `bot-failed` | A caught exception or a failed bot. The first two fail the gate. The others are defects to fix; read the example (first stack frame). |
| Data and decoding | `unhandled-spell-effect`, `unhandled-prerequisite-type`, `unhandled-prerequisite-comparison`, `effect-stored`, `prerequisite-assumed-met`, `prerequisite-inferred`, `prerequisite-row-missing`, `proxy-spell-missing`, `spell-param-unknown`, `loot-unknown-item`, `item-stat-type-unknown` | Something the tables say that the server cannot evaluate or only stores. The count shows how often play hits it; `scripts/remaining.sh` summarises them. |
| Casting and combat | `cast-failed` (key = reason), `spell-pending-overflow`, `creature-cast-while-dead`, `combat-stats`, `player-death`, `cc-state`, `proc-fired`, `proc-skipped` | Why casts fail and what combat did. The key of a `cast-failed` entry is the reason. |
| Quests | `quest-stalled`, `bot-quest-parked`, `bot-quest-progress`, `objective-synthetic`, `objective-inferred`, `quest-kill-friendly-target`, `quest-prerequisite-missing`, `pick-filter`, `pick-refused`, `bot-level-rotation` | Progress and blockers of quests. `objective-synthetic` per type is the amount of direct credit. |
| Systems | `system-ok`, `system-fail`, `system-skip`, `system-info`, `system-unhandled-message`, plus per-system categories (`mail-*`, `auction-*`, `tradeskill-*`, `vendor-*`, `loot-*`, `group-*`, `housing-*`, `challenge-*`, `path-*`, ...) | Probe results and refusals. `system-unhandled-message` lists client messages with no handler. |
| Dungeons and events | `dungeon-*`, `encounter-*`, `event-*`, `instance-*` | Runs, clears, wipes, boss kills, event outcomes. |
| Maps | `map-created`, `map-unloaded`, `map-file-missing-flat-fallback`, `map-floor-file-invalid`, `grid-unload-skipped` | Map lifecycle and data gaps. |

Counters with a bounded number of keys are safe to compare run to run; a key set that grows with the run (per quest, per creature) is expected to grow with run length.

## What counts as a regression

Compare a run with the previous run of the same configuration (bots, roles, `.env`), using the stored report and `stats.csv`:

1. **Any `map-update-exception` or `spell-update-exception`** where the previous run had none. This is the documented gate failure.
2. **A map that stops ticking** or ticks far below the others (`gt maps`), or a world container restart in `events.log` (for example the memory limit reached).
3. **A rising trend** in CPU or memory after the 30-minute warm-up. A single higher level is not a regression by itself; the build comparison in PERFORMANCE.md found ±5 % noise for one build.
4. **Quest coverage going down**: *done* in the quest-coverage line fell, or quests that were completed before are in `quest-stalled` now, or the *B* number (completions that rely on a QA heuristic) rose faster than *done*. `objective-synthetic` rising means real objectives were replaced by direct credit.
5. **A new key** in a stability category, or a large rise of an existing data category (`unhandled-*`, `cast-failed`, `effect-stored`) that was caused by the change. `scripts/remaining.sh` shows the totals side by side for two snapshots.
6. **`scripts/combat-check.sh` reports `DIFF` or `MISSING`** after a combat change.
7. **Dungeon results worsen** for a world that cleared before: more wipes or timeouts with immortal bots, a boss that no longer dies (`encounter-boss-killed` missing), `encounter-*-exception`.
8. **`system-fail` rises** for a probe that was clean.

A rise in a category that the change was meant to produce (for example more `effect-stored` because new handlers store data) is not a regression; say so in the commit message.

## Unknown

- A numeric acceptance threshold for CPU level, for the per-category counts, or for quest coverage: the repository states none (CPU is judged by trend; coverage is tracked as a growing count).
- The exact tolerances `combat-check.sh` applies per key: they are produced by `scripts/research/combat_expect.py` per case and not summarised anywhere.
- The expected tick count for settings other than `TICK_MS=20`: only the 20 ms figures are documented.
