# Data provenance

Retail client material is never committed. Everything derived from it is rebuilt by a script from your own client files.

| file or pattern | derived from | rebuilt by | committed or generated |
|---|---|---|---|
| data/tbl/*.tbl | client Patch folder (game tables) | `install.sh --client-dir` (map generator `-e`), or copied by the user (QUICKSTART.md) | generated at setup, git-ignored |
| data/map/*.nfmap | client Patch folder (terrain) | `docker compose run --rm mapgen` then scripts/gen-maps.sh | generated at setup, git-ignored |
| data/map/*.nffloor | client instance props (.dgn, .i3 sectors) | scripts/gen-floors.sh (scripts/research/floorgen.py) | generated at setup, git-ignored |
| data/map-gen/ | MapGenerator working output | scripts/gen-maps.sh | generated, git-ignored |
| data/spawns/research-spawns.tsv | archived Jabbithole sightings matched to Creature2 ids by name, zone, level | scripts/research/jabbithole_match.py | committed (facts from a community source, see SOURCES.md) |
| data/spawns/questloc.tsv, objind.tsv | wiki and quest indicator coordinates | scripts/research/questloc_spawns.py, indicator_spawns.py | committed |
| data/research/*.tsv | archived Jabbithole pages (drops, abilities, quest links, events, zones) | scripts/research/jabbithole_parse.py, jabbithole_events.py, jabbithole_zones_parse.py, jh_quest_links.py | committed |
| data/encounters/** | RaidCore and boss mod sources, guides, creature abilities | scripts/research/make_raidcore_encounters.py, bossmod_timers.py, encounter_audit.py | committed |
| research/ (raw crawl caches, wiki-raw, decode/, clientre/, the local client copy) | web caches and client analysis | the scripts in scripts/research | evidence only, git-ignored, not needed at runtime |

Runtime and setup inputs are only data/tbl, data/map, data/spawns, data/research and data/encounters. Spawns are loaded into the
database by scripts/import-research-spawns.sh from data/spawns.

## Three kinds of data

### 1. The client's own tables (needs your own copy)

All game rules come from the build 16042 client you install from: about 385 game tables (`.tbl`), the text table (`en-US.bin`), terrain (`.nfmap`) and instance prop geometry (`.nffloor`). Getting the client is your responsibility. These files and anything extracted from them are never committed or distributed, and `.gitignore` keeps `data/tbl`, `data/map` and `research/` out of the repository. Where a client binary or UI script was read to decode a message layout or a formula, the finding is written down in [ASSUMPTIONS.md](ASSUMPTIONS.md) or [PROTOCOL.md](PROTOCOL.md). The client code itself is not copied.

### 2. Community research (committed, with the source named)

The game tables hold no spawns, loot tables, event scripts or boss scripts, so these came from community material. The committed files are in the table above, and each source is listed in [SOURCES.md](SOURCES.md):

- creature spawns: the NexusForever world database (loaded from upstream at install, not copied) and archived Jabbithole NPC pages matched to Creature2 rows by name, zone and level ([ASSUMPTIONS.md](ASSUMPTIONS.md) "Researched spawn data", "Spawn clustering");
- creature drops and abilities: the same archived Jabbithole pages ("Researched creature drops and abilities");
- quest locations and coordinates: the WildStar archive wiki ([wiki-yield.md](wiki-yield.md)) and the client's quest indicator positions;
- boss ability timers and phases: RaidCore and other boss mod addon sources, plus dungeon guides ([ENCOUNTER-SOURCES.md](ENCOUNTER-SOURCES.md));
- formulas: the WildStar wikis and greenyneko.com posts on damage and stats, checked against the tables ([COMBAT-MATH.md](COMBAT-MATH.md), ASSUMPTIONS.md "Combat formulas").

Only facts were taken from these sources (ids, coordinates, chances, timers), not their text.

### 3. Decoded fact versus documented guess

Every reading in [ASSUMPTIONS.md](ASSUMPTIONS.md) carries a confidence:

- **certain**: read directly from a table field, a client message layout or a client function, or measured on the server;
- **likely**: a consistent pattern across many table rows, or a documented source that agrees with the tables;
- **guess** / **default**: nothing in the tables, the client or a source says it. A value was chosen so the system works and is recorded in ASSUMPTIONS.md; many of these are also counted in the failure ledger when used.

The guesses that affect play most:

| what | value used | why it is a guess | where |
|---|---|---|---|
| health regeneration, suit power, kinetic energy and volatility rates | design values (health 5% of max per second out of combat, suit power +5/s, ...) | the client has no base rates and no table holds one; only the direction comes from the wiki class pages | ASSUMPTIONS.md "Shutdown saves, regeneration defaults, hazards"; `NF_REGEN_*` / `NF_DECAY_*` in [CONFIGURATION.md](CONFIGURATION.md) |
| parameterless damage and heal rows | DataBits00 x assault power | the client tooltip and damage preview give 0 for these rows, so nothing shows the real amount | ASSUMPTIONS.md "Client spell modification evaluator, ..." |
| spell modification range, channel timing, threat, magnitudes and flags | counted, not applied | the server has no use for them (the client spell's own threat reads 0); cast time, cooldown and costs are applied | ASSUMPTIONS.md "Client spell modification evaluator, ..." |
| pet stance flags | stored per pet, AI unchanged | the message layout is certain, the effect on pet AI is not decoded | ASSUMPTIONS.md "Shutdown saves, regeneration defaults, hazards" |
| player base crit chance | 5% | the base from class or rating data was not found | ASSUMPTIONS.md "Combat formulas" |
| boss phases, enrage, special telegraphs | approximations from addon timers and guides | no retail encounter scripts exist | ASSUMPTIONS.md "Instances, groups and encounters" |
| holocrypt release point | nearest bind point | the tables have no holocrypt positions | ASSUMPTIONS.md "Death and resurrection" |
| worlds without terrain | flat plane at height 0 | the client has no terrain for them | ASSUMPTIONS.md "Worlds without a map file" |
| prerequisite types with no decoded meaning | assumed met in spell casts, counted in the ledger | no table or client code explains them | ASSUMPTIONS.md "Prerequisite types ... (assumed met)" |

Content that was never released (Halon Ring, Murkmire) is marked cut, not guessed at (ASSUMPTIONS.md "Cut content").
