# Research pipeline (spawns, loot, abilities, events, bosses)

The client tables hold no spawns, drops, creature ability lists, event scripts or boss scripts. This is how the data was built and how to rebuild it. Every assumption is in `docs/ASSUMPTIONS.md`.

## Sources
- **Jabbithole** (jabbithole.com, WildStar database, offline since 2020): copies in the Internet Archive (web.archive.org, snapshots 2014-2016). NPC pages have world x,z coordinates of sightings (identical to database positions, checked against a vendor), drop chances with sample sizes, NPC ability lists (Spell4 ids), the public events an NPC takes part in. Zone pages list the NPCs of a zone and the boss encounters of dungeons. Public event pages list objectives and involved NPCs.
- **RaidCore** (github.com/Immemor/RaidCore, WildStar boss mod): unit names, cast names, timer bar lengths and health thresholds of the Genetic Archives, Datascape, Redmoon Terror and Y-83 encounters. Cloned into research/raidcore.
- **Guides** (thecomfywizard.com, elijaa.org, search summaries of wildstar.fandom.com): phase structure and ability names of the dungeon bosses.
- **NexusForever** upstream (AGPL, same license as this repo): `PublicEventObjectiveType` and `PublicEventType` enums.
- Checked and not used: Bezgelor (Elixir port): its spawn, loot and event data files contain placeholder ids and invented numbers, its boss scripts are AI-generated from wiki text.

## Scripts (all in `scripts/research`, raw pages cached in the gitignored `research/`)
1. `gt exportneeded` (from the host: `scripts/gt.sh gt exportneeded`, then copy `/cache/*.tsv` to `research/` with `docker compose exec -T world cat`): writes needed-creatures.tsv (quest creatures without a database spawn, with indicator locations), creatures.tsv, zones.tsv, zone-worlds.tsv, worlds.tsv, locations.tsv.
2. `run-all.sh`: wayback CDX listings are in research/jh-*-cdx.txt (`curl 'https://web.archive.org/cdx/search/cdx?url=jabbithole.com/npcs/*&output=txt&collapse=urlkey&filter=statuscode:200&fl=original,timestamp'`, same for zones and public-events); `jabbithole_pages.py zones|public-events` and `jabbithole_fetch.py --all` download politely (one persistent connection, one request per second, resumable). About 24,000 NPC pages take 7 hours.
3. `jabbithole_parse.py`, `jabbithole_zones_parse.py`, `jabbithole_events.py`: write the committed tables in `data/research/` (drops, abilities, creature info, zone and event lists), mounted read-only into the world container.
4. `jabbithole_match.py`: matches pages to Creature2 ids by name, zone, level, clusters sightings into spawns, places instance zones and quest creatures, writes `research/research-spawns.tsv` and `research/match-report.txt`.
5. `scripts/import-research-spawns.sh`: `gt importspawns` turns the tsv into entity rows 9000000+ (heights, display, faction), the script applies the SQL; restart the world afterwards.
6. Quest gaps (numbers in docs/ASSUMPTIONS.md "Quest spawn gaps"): `questloc_spawns.py` places receivers at Quest2.WorldLocation2IdReceiver
   (research/extra-spawns/questloc.tsv, imported together with research-spawns.tsv; `gt importspawns` prints rows per source); `jh_quests.py fetch|parse`
   (archived Jabbithole quest pages: started/completed by), `jh_quest_links.py` (data/research/quest-links.tsv, giver/receiver links the tables lack);
   `questorigin.py`, `gapcreatures.py`, `gapsim.py`, `jh_quest_cross.py` explain what is left; `questloc_check.py`, `indicator_check.py` measure the evidence.
7. `make_raidcore_encounters.py`: drafts in `data/encounters/raidcore`. Hand written files are in `data/encounters/dungeons` and `data/encounters/raids`.

## Measuring
- `gt questcheck` (giver, receiver and objective spawns per quest), `gt eventplan`, `gt encounters`, `gt abilities <creature>`, `gt instances`, `gt profile`.
- `scripts/gate.sh` (10 minute check with CPU), `scripts/dungeon-check.sh` and `scripts/dungeon-sweep.sh` (bot teams in dungeons and raids), `NF_BOTS_EVENTS=1` (event test mode).

## Combat formula audit
- `scripts/combat-check.sh` (world running): compares `gt combat <level> [spell4] [creature] [power]` with expected values from
  `scripts/research/combat_expect.py` (tables + sources); prints ok/DIFF per check and a total. Evidence in docs/ASSUMPTIONS.md "Combat formulas".
- Offline decoders (write research/decode/combat/): `dmgcoef.py` (damage/heal field distributions), `paramtypes.py` (ParameterType use per
  effect type), `wikistats.py` (archive wiki NPC infobox health vs tables), `jhstats.py` (Jabbithole NPC health vs tables).
