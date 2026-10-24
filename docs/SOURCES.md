# External sources

Every kind of source the data and decodes in this repository rest on. Only sources already cited in [ASSUMPTIONS.md](ASSUMPTIONS.md), [ENCOUNTER-SOURCES.md](ENCOUNTER-SOURCES.md), [PROTOCOL.md](PROTOCOL.md) or the research scripts are listed. How each one was used and what was derived from it: [DATA-PROVENANCE.md](DATA-PROVENANCE.md). Add a line when a new source is used.

## Official client data (your own copy, never distributed)

| source | used for |
|---|---|
| WildStar client build 16042: game tables (`.tbl`), text (`en-US.bin`), world and prop files in the `Patch` archive | all game rules, terrain, instance floors; extracted on your machine by `install.sh` / the map generator |
| Client UI scripts (Lua in the `Patch` archive) and the client executable | message layouts, opcode senders, formulas the client evaluates (findings in ASSUMPTIONS.md and PROTOCOL.md; nothing copied) |

## Emulator and tool projects

| source | URL | used for | license |
|---|---|---|---|
| NexusForever | https://github.com/NexusForever | the emulator this project extends; develop and game_rework branches read for protocol and structure | AGPL-3.0 |
| NexusForever.WorldDatabase | https://github.com/NexusForever/NexusForever.WorldDatabase | community creature spawns by zone, including instances (`scripts/load-world-data.sh`, `load-instance-data.sh`, loaded from upstream at install) | per repository |
| The-Ham-Sandwich (WildStar file format tools, GitHub) | - | model, prop and world file format reference for instance floor heights (`scripts/research/floorgen.py`) | per repository |
| WildstarPacketParser (GitHub) | - | packet layout reference ([PROTOCOL.md](PROTOCOL.md)) | per repository |
| Bezgelor (another WildStar emulator) | - | read and rejected as a numeric source because its values are invented ([PROTOCOL.md](PROTOCOL.md)) | per repository |

## Encounter sources

| source | used for |
|---|---|
| RaidCore (WildStar raid addon, source on GitHub) | raid boss timers and phases (`data/encounters/raidcore`, `scripts/research/make_raidcore_encounters.py`, `bossmod_timers.py`) |
| LUI BossMods, DruseraBossMods and other WildStar boss mod addons | second source for boss ability names and timers ([ENCOUNTER-SOURCES.md](ENCOUNTER-SOURCES.md)) |
| Dungeon and raid guides: gameskinny, elijaa.org, wildstar.mmorpg-life.com, thecomfywizard.com | ability names and order ([ENCOUNTER-SOURCES.md](ENCOUNTER-SOURCES.md)) |

## Community databases and wikis

| source | URL | used for | license |
|---|---|---|---|
| Jabbithole (archived copies) | https://web.archive.org (jabbithole.com pages, 2014-2019 snapshots) | creature spawn sightings, drops, abilities, quest links, event NPCs, item tooltips (`data/research`, `data/spawns`, `scripts/research/jabbithole_*.py`, `jh_*.py`) | site content, archived; only ids, coordinates and chances taken |
| WildStar Online archive wiki | https://wildstaronline-archive.fandom.com/wiki | zones, NPCs, quests, coordinates, mechanics (Farside, Arcterra, Tales From Beyond The Fringe) ([wiki-yield.md](wiki-yield.md), `scripts/research/wiki_*.py`) | CC-BY-SA (Fandom) |
| WildStar wiki, including archived patch notes | https://wildstar.fandom.com/wiki | mechanics, stat pages, public events, death and resurrection rules | CC-BY-SA (Fandom) |
| greenyneko.com | https://greenyneko.com | damage calculation, assault power rating, item stat posts (ASSUMPTIONS.md "Combat formulas") | blog posts, formulas cited |
| A pastebin of stat definitions | - | definitions only, no numbers used (ASSUMPTIONS.md "Leftover decodes") | n/a |
| A video and wiki summaries of the unreleased Dreadmoor zone | - | evidence that the zone was never released (ASSUMPTIONS.md "Quest spawn gaps") | n/a |
| RageZone thread "WildStar unfinished Emu + Tools" and related archived tool pages | https://web.archive.org | leads only; nothing copied | n/a |

What each research source contains and how it was crawled: [RESEARCH.md](RESEARCH.md).
