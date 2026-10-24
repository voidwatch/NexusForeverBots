# Assumptions (what, why, source)

- Prerequisite 129 (comparison [2,1], objects [313,796], used by Spatial Shift, Sprint, Stealth, PvP time bombs): meaning not decoded. 313 and 796 are not Spell4 ids.
  Spell casts treat it as met (lenient scope), quests never use it. Evidence: `gt prereqrow 12939`. Seen 30x per 10-minute run.
- Creature spells cast by a dead caster (CasterCannotBeDead, ~320 per run) come from procs and death-triggered casts of creatures; they are refused and counted, not fixed.
- Quest-kill-not-a-target in dungeon mode (NF_BOTS_DUNGEON) is expected: the bots kill everything hostile, not only quest targets.
- Prerequisite 129 update: other rows using it (6738, 8763, 9110, 10242) gate Settler path quests (e.g. 6749/6751, Spell4 27372 "Settler Template Improvement - Harvesting Buff") with object = small category id and value = Spell4 ids.
  Read as "Settler improvement state"; no such state exists on the server, so it is now met everywhere (ledger: prerequisite-assumed-met). Wrong if Settler improvements must be built first.

- Crit and armor (Game/Combat/DamageMath.cs): players crit 5% of hits for 1.5x (severity default from greenyneko.com damage calculation; the 5% base is assumed, gear rating curves are undocumented so ratings are not converted). Players take physical resist = armor / (armor + 1.5 * level armor) with the CreatureLevel armor of their level (no gear); the 1.5 constant is assumed. Creatures never crit.
- EarnCurrency objectives are credited from real currency gains; bots still credit them synthetically (data 5 is Prestige, a PvP currency).
- Instance worlds (dungeons, raids): the MapGenerator skips them (props as terrain, no .area grids), so they have no height grid. Creature spawns for them come from the researched Jabbithole data (sections below); heights from the nearest world location.

## Researched spawn data (Jabbithole archive)
- What: creature spawn positions for quest creatures that have no spawn in the community world database. Rows are entity ids 9000000+ (`scripts/import-research-spawns.sh`).
- Source: archived copies of jabbithole.com NPC pages on web.archive.org (2014-2016 snapshots). Each page lists world x,z coordinates ("title" of the map markers; checked against a known vendor, identical to the database position). Scraper `scripts/research/jabbithole_fetch.py`, at most 1 request per second, raw pages cached in research/ (gitignored).
- Matching (`scripts/research/jabbithole_match.py`): by creature name; the page zone must map to a world (zone name, else the world of the nearest existing spawn); level and zone narrow candidates when several creatures share a name. Giver/receiver NPCs shared by up to 30 creature ids are placed for every id (same NPC, several phases/factions), monsters only when at most 3 candidates. Everything else is listed in research/match-report.txt as ambiguous or skipped.
- Y is the map terrain height at x,z (the page has no height); worlds without a map (instances) get y 0. Rotation 0, area filled by the server at load.
- Zone to world: the name must exist in WorldZone/WorldLocation2; Jabbithole zones not in the client tables are skipped.

## Researched creature drops and abilities (Jabbithole archive)
- Source: the same archived Jabbithole NPC pages. "Drops" tab: item id, chance and kills sampled; "Abilities" tab: Spell4 ids of the creature's NPC abilities. Parsed into data/research/*.tsv by `scripts/research/jabbithole_parse.py`, mounted read-only into the world container (`NF_RESEARCH_PATH`, default /data/research).
- Match to a creature: by name, closest page level (a page with data wins over one without). Several Creature2 rows with one name share the page.
- Loot: each researched drop is rolled once per kill with its sampled chance for every player who gets the kill credit (killer and grouped players within 80 m, personal loot, no loot window). Without a page: 8% (25% superior/prime, 50% group/raid elite) for one level-appropriate armor/weapon of quality 2-3 from the item table. Credits: level x 2-5 (x4 for elites), no data (placeholder). Bots with a full bag lose their lowest-quality gear item (they have no vendor trips), humans get the normal inventory-full error.
- Abilities: auto-attacks use the creature's researched auto-attack spell row (see "Combat math"); every other ability is cast at the threat when in range off its table cooldown (10 s when 0), at most one cast per 3 s plus cast time. One spell per base id (first listed tier). A dead creature does not cast; skipped casts are counted as `creature-cast-while-dead`.
- Not done: veteran (tier 2) variants of dungeon pages, drop tables shared per loot group, drop chances scaling with group size.

## Public event engine (Game/Events/PublicEventEngine.cs)
- Source: PublicEvent and PublicEventObjective tables (objective types decoded from the NexusForever upstream `PublicEventObjectiveType` enum: 0 kill target group, 1/8 kill event (objective) unit, 3/4 activate target group, 5/9/25 script, 6 participants in trigger volume, 12 defend objective unit, 13 timed win, 14-16 cluster kills, 20 exterminate, ...). The tables hold no script, so the engine plays the generic part (everything below is an assumption).
- A quest's CompleteEvent data is a PublicEventObjective id in most quests and a PublicEvent id in the rest (checked: "Defend yourself against intruders" = objective 344 of event 100); an objective id wins when both exist. A finished objective credits the objective id, a succeeded event credits the event id, for every participant.
- Start: a player within the event's circle (event location, else first objective location, else the indicator of the quest objective that asks for it; radius 40-90 m, 2D distance), level at least the event minimum, 5 minutes after a success and 2 minutes after a failure. Pvp event types (warplot, battleground, arena) are not started.
- Objectives: those flagged initial (else those without parent) start active, children start when their parent succeeds, the remaining parentless ones after 45 s or the first success. Count 0 counts as 6. Kill and exterminate objectives count kills of target group members in the circle (+30 m) and spawn missing units (3-40) around the center; event-unit objectives take creatures from the objective id, else the creatures researched for the event (Jabbithole event pages, data/research/event-npcs.tsv), else the creatures of the event's kill target groups. Timed win succeeds when its timer ran out, defend succeeds when the timer runs out with the unit alive. Objectives the tables cannot drive (script, depots, resource pools, talk-to, turnstile, ...) are credited once everything else is done or after 60 s (counted as `event-objective-synthetic`).
- Failure: an objective timer running out, the event timer running out, 120 s without a participant nearby, or a 30 minute cap. Rewards: quest credit and 5x kill XP, nothing else yet (PublicEventRewardModifier not used).
- No client packets yet: players do not see the event tracker (the fork has no public event messages). Needs a client test.

## Spawn clustering (Jabbithole sightings)
- A Jabbithole NPC page lists where the creature was *seen*, up to about a hundred sightings (a boss seen walking around has dozens). One row per sighting put dozens of copies of one boss into the world, so sightings are clustered (12 m, greedy) and each cluster centre is one spawn. Boss rank (Prime) or group/raid eliteness: one spawn only, at the largest cluster. Several pages of the same creature in the same world (zone variants, normal/veteran listings) are merged before clustering.
- Instance zones (dungeons, raids, expeditions, adventures): every NPC page is placed, not only quest creatures. The creature row is the name match with the best score: description contains a zone word or abbreviation, level close to the page level (or the median level of the zone's pages), veteran and test rows ranked down (normal difficulty is placed). Pages with no candidate scoring 2 are listed as `instance-unmatched` in research/match-report.txt.
- Height: instance maps have no height grid, the height is the one of the nearest world location of the same world within 600 m, else 0. Entities in such maps keep their height when they move (BaseMap.GetTerrainHeight fallback), so nothing is snapped to 0. Superseded where a .nffloor sidecar exists: see "Instance floor heights from prop geometry".
- Creatures no page placed go to the indicator locations of the quest objectives that name them (4 around each location, one for givers and receivers).
- Not placed (about 960 of 3500 quest creatures): zones added after the 2014-2016 archive (Arcterra tracts) and test or placeholder rows. They remain open data gaps.

## Dungeons, raids and encounters
- World types 1 (expeditions), 6 (adventures), 9 (raids) and 11 (dungeons) are instanced (DungeonInstancedMap): one instance per group (or per character without a group), unloaded 120 s after the last player left, which resets creatures and encounters. Dungeon creatures do not respawn. Players return to where they came from.
- Encounters (data/encounters/*.json, Game/Encounters): phases by boss health percentage, ability timers per phase, add waves, an enrage timer (damage +50% every 20 s, up to 10 stacks). Without a file, every creature with prime rank or group/raid eliteness in the research data gets a generated encounter: three phases at 100/60/30 percent, its researched non-auto-attack abilities staggered every `20 + 2 x abilities` seconds, enrage after 10 minutes (`source: generated`). Files in data/encounters/raidcore are drafts built from the RaidCore addon sources (unit names, cast names, bar lengths, health thresholds); abilities that cannot be matched take the researched abilities of the creature. Timings are guesses, there is no capture to check them against.
- Boss fights: a creature keeps a list of everyone who hurt it and forgets someone only after 10 s away from the map (a teleport inside the map must not reset the boss); it resets (full health, encounter state, adds removed) when nobody is left, or when it is dragged more than 120 m (boss) or 45 m (others) from its spawn.
- Bot teams (NF_BOTS_DUNGEON=world, NF_BOTS_TEAM=n): server side groups of n bots (groups of up to 40, normal groups stay at 5), set to the median creature level of the instance, enter, clear (nothing hostile for 6 s), leave, wait 150 s for the reset and go again. Wipes (all members dead) send the team back in.

## PersonalDmgHealMod
- DataBits00 is a damage type number (3 to 8) or 12, DataBits01 a direction (1 or 2), DataBits02 the factor. Analyze Weakness (Stalker) puts 1.2 on types 6, 7 and 8 with direction 1 on its target; Unsteady Miasma and Nerve Induction use 0.8 with directions 2 and 1 on types 3 to 5; Fusion Probe (Medic) uses 1.2 on type 12.
- Applied as: direction 1 multiplies the damage the holder takes from the spell's caster, direction 2 the damage the holder deals to the caster, kind 12 the healing the holder receives from the caster; the damage types are not told apart (a factor applies to all damage from that caster). Lasts for the duration of the effect (permanent when it has none, until the aura is removed by its expiry).

## Quest prerequisites
- A quest whose prerequisite quest id is not in the quest table (the quest can never be completed) ignores that prerequisite, counted as `quest-prerequisite-missing`. Prerequisite type 43 (quest state) reads a quest the character does not have as state 0 (unknown).

## Memory
- The memory climb of long runs (4.3 to 6 GB over 5 h, 6.8 to 9.9 GB in 70 minutes of the mixed 60 bot run) was explained with `gt memory collect` (forced GC: the managed heap after collection equals the heap before, so it is live data, not lazy collection): the baseline is 4.1 GB (tables and the preloaded maps), each open world map file costs 200 to 580 MB once a bot visits the world (Halon Ring and Farside +0.6 GB each, ledger `map-created`), and every bot costs about 25 MB. Visited maps stayed loaded for the life of the process.
- Idle open world maps (no active grid, entity or pending action for 420 s, `NF_MAP_IDLE_UNLOAD`) are now unloaded with their map file (`map-unloaded` in the ledger), reloaded on the next visit (a stall of 1 to 3 s on the world thread while the file loads). The compose file caps the world container at 14 GB (`WORLD_MEM_LIMIT`) so a leak kills only that container.
- Target group results used by bots and events (`TargetGroupHelper.ResolveSpawned`) only keep creatures that have a spawn; filter based groups (race, faction) that match thousands of creatures are no longer cached unfiltered.

## Prerequisite types 117 and 61 (assumed met)
What: 117 (object 822, Equal 0; gates Quest2 10115-10117) and 61 (nested under type 11 in spell rows 3528-3537, object 12-16 GreaterThan 2-10) are treated as met and counted in `prerequisite-assumed-met`.
Why: no decodable source (names missing from the tables, nothing found online). A missing prerequisite row now fails strict (quests) and passes in spell casts instead of throwing.
- ObtainSchematic (38) and Unknown31 (31) quest objectives stay synthetic: no tradeskills exist, and type 31 has empty data and text. Recorded as open data gaps.

## Cut content: Halon Ring and Murkmire
- What: Halon Ring (zone 866) and Murkmire (zone 207) and every zone below them in WorldZone.ParentZoneId were never released. Quests whose zone is in those trees are "cut".
- Why/source: maintainer decision from the release history (both zones were scrapped before launch). Game/Quest/CutContent.cs implements the test.
- Effect: `gt questcheck` reports them only as `cut` (178 quests) and skips the giver/receiver/objective checks; `gt exportneeded` skips their creatures. They are excluded from the definition of done and from the open data gaps.

## Wiki-sourced mechanics (crawled 2026-10-03, scripts/research/wiki_crawl.py mech, raw pages in research/wiki-raw/mech)
The wildstar.fandom.com pages for death, groups and public events are mostly stubs; what they do state:
- Dungeons: 5-player groups, normal and veteran mode (veteran is level 50 with extra boss mechanics). Source: https://wildstar.fandom.com/wiki/Dungeon
- Raids: 20-player (some 40) groups; weekly lockout, reset Wednesday 2am PST; entering generates an instance, killing any boss or miniboss locks every member; non-boss population respawns within 4 hours if the raid is not cleared; some raid layouts are randomised weekly. Source: https://wildstar.fandom.com/wiki/Raid. Our DungeonInstancedMap has no weekly lockout (bots re-enter freely for testing), assumption kept.
- Reputation ranks: Liked 2000, Accepted 4000, Popular 8000, Esteemed 16000, Beloved 32000. Source: https://wildstar.fandom.com/wiki/Reputation
- Resurrection (patch notes 09/29/2015, https://wildstar.fandom.com/wiki/Patch_09_29_2015): Wake Here cooldown 1 h for free players, 30 min for Signature; players resurrect with at least 50% Focus (100% in arenas, battlegrounds, warplots). Patch 03/03/2015: players respawn inside the starter ring after a wipe. Our auto-resurrect after a timer with ResurrectHealth is a QA simplification; Focus restore is not applied (Focus vital id is inferred only).
- Public events: open to any player, bigger rewards with more participants (https://wildstar.fandom.com/wiki/Public_event). No wave orders are listed on the wiki; event wave definitions stay from the PublicEvent tables.
- Companion pages only describe the Esper AMP and vanity pets, there is no NPC companion system to implement.
- Not on the wiki at all (stays assumption): death penalty rules, group loot rules, quest UI behaviour.

## Instance floor heights from prop geometry (2026-10-03, scripts/research/floorgen.py)
- What: dungeons, raids, expeditions and adventures are built from props, not the terrain grid. A sidecar `data/map/<Asset>.nffloor`
  (NFFL v2, sparse 32x32 tiles of 1 m cells, every upward-facing surface height per cell, so stacked floors are kept) is generated from
  the client archive and loaded next to the .nfmap (BaseMapManager.LoadFloor). Lookup (MapFile.TryGetFloorHeight): prop floors and the
  terrain grid are layers; the highest layer at most 2.5 m above the entity's current height wins; a change of more than 8 m keeps the old
  height (gaps in the data, e.g. m3 bridges, must not drop entities a level). Movement generators chain the reference height point to point
  (stairs). Researched spawns (id >= 9000000) in such worlds are put on the layer nearest their estimated height. Without a sidecar the
  behaviour is unchanged (terrain grid, else keep height).
- Formats (certain where tested, reverse engineered unless noted): archive .index/.archive PACK/AIDX/AARC and the area PROp chunk
  (104-byte entries: model type 0 m3, 1 i3, 3 dgn; UTF-16 path) ported from The-Ham-Sandwich (github.com/adarec1994/The-Ham-Sandwich,
  src/Archive.cpp, src/Area/Props.cpp). Prop transform: position + quaternion (x,y,z,w) + uniform scale, positions in world coordinates.
  `.dgn` (magic NGND) lists the building `.i3` and dressing `.N.dgn` layers as UTF-16 strings. Building `.i3` (FEDI = IDEF) names its
  sectors; sector geometry is `<building>.<SectorName>.i3` (FEDS = SDEF, version 0x1e): 0x1b0-byte header of (count u64, offset u64)
  pairs with offsets relative to 0x1b0: 0x20 name, 0x38 vertex positions (vec3 f32), 0x48 20-byte vertex attributes, 0x58 u32 indices
  (triangle list), 0x68 u16 per triangle; bounding box at 0x120. Upward-facing = counter-clockwise in x/z seen from above (normal y >= 0.5).
- Evidence (likely): 3404 (the only test world with sniffed NexusForever.WorldDatabase spawns): 237 spawns, 135 within 0.25 m of the
  chosen floor, the rest are hovering creatures (all type 10, 5-35 m above a floor) or on m3 props. WorldLocation2 points (designer-placed)
  within 1 m of the chosen floor: 3404 58/63, 2188 90/102, 2149 28/29, 1232 50/66, RedMoonTerror 117/169, GeneticArchives 43/77,
  Stormtalon 92/160 (scripts/research/floorcheck.py; misses are on terrain or m3 props). Live (gt floorcheck, 5 bots): Stormtalon's Lair
  players 5/5 and creatures 132/133 within 1 m of the floor; Skullcano 94/96 creatures, 58 of them where the terrain grid alone is >3 m off.
- Static m3 props (likely): `wsgeom.m3_mesh` reads the version-100 .m3 geometry (Ham Sandwich src/models/M3Loader.cpp ReadGeometry:
  1584-byte header, geometry block at 0x250, 200-byte geometry entry, 112-byte submeshes; positions float3 or int16/1024) and takes the
  submeshes of the lowest variant group plus group 255, without bones/animation. `floorgen.py --m3` adds them; `scripts/gen-floors.sh`
  uses it and falls back to buildings only when the sidecar would exceed NF_FLOOR_MAX_MB (64 MB; foliage-heavy worlds: AdventureHycrest
  255 MB, ColdbloodCitadel, UltimateProtogamesJuniors, ShiphandLevel6, AdventureGaleras, Datascape). Effect on the 26 test worlds
  (floorcheck.py, 3738 WorldLocation2 points): floor in cell 1709 -> 2218, chosen floor within 1 m 1126 -> 1330, within 2.5 m 1310 -> 1576,
  no world got worse (HalloftheHundred 78 -> 117, Skullcano 52 -> 70, OsunDungeon 0 -> 17, AdventureWhitevale 87 -> 149).
  Live after the m3 rebuild (gt floorcheck, 5 bots in Stormtalon's Lair, 2 bosses killed): players 5/5 on the floor, creatures 88 on, 1 near, 0 off.
- Not covered (guess/unknown): the dressing `.N.dgn` layers; the 20-byte vertex attributes, the per-triangle u16 (material/sector part,
  likely) and header fields 0x08/0x18. No collision flags were found, so render geometry is used as floor (m3 foliage and small clutter
  count as floor too). Worlds without area files (3010 AdventureFarside, 3102 RedMoonTerror40man) have nothing to generate from and keep
  the flat fallback.
- Regenerate: `scripts/gen-floors.sh [world ids]` or `python3 scripts/research/floorgen.py [--m3] -o data/map <world ids>` (one world per
  process for big worlds, ~1 GB RAM for open adventures), check with `scripts/research/floorcheck.py <world ids>` (FLOORDIR=dir to check
  another set) and `scripts/gt.sh gt floorcheck` while bots are in instances. Dungeon runs need BOTS_DUNGEON_BOTS unset or >0 in .env.

## Worlds without a map file (flat fallback)
- What: worlds whose .nfmap does not exist (client files have no terrain for them, e.g. 3010 and the 40 man Red Moon Terror world 3102) load an empty MapFile: height 0, no area ids. Creatures keep their researched/location-derived heights.
- Why: before, map creation threw FileNotFoundException inside the map update (40 map-update-exception in the 2026-10-02 sweep) and teams could not enter. Counted in the ledger as map-file-missing-flat-fallback.
- Source: none, engineering fallback.

## Full archive wiki crawl (2026-10-03, scripts/research/wiki_full.py + wiki_parse.py)
- What: all 4284 non-redirect pages of wildstaronline-archive.fandom.com (ns 0/4/10/14/500) via the MediaWiki API; 55 pages contain coordinate pairs (209 rows, research/wiki-coords.tsv).
- Why nothing imported: coordinates are in achievement/collectible lists, names map to several Creature2 ids and carry no world; placing would be a guess. Yield table: research/wiki-yield.md. Spawn counts in questcheck are unchanged.

## Decoded table semantics (2026-10-03, scripts/research/tbl.py offline reader; dumps in research/decode/)
Tools: `scripts/research/objdump.py <type>`, `keyscan.py <ids>`, `propmod.py`, `procs.py`, `proctips.py` read data/tbl directly.
- Quest objective type 10 (Unknown10, 52 rows) — likely. A "do X to N creatures" checklist: free captives, overload cannons, scan crystals,
  jumpstart snowblowers, secure flank points. Data is a TargetGroup id (8 of 8 rows where a group exists match the creatures in the text and
  reward pane), else a Creature2 id (18876 Potential Recruit, 30840 Firestorm Flingatron, 56902 Certification Panel); data 0 uses the reward
  pane group. No QuestAdvanceObjective effect points at any of them, so credit comes from acting on the creature: activation, a non-class
  spell (quest item, vehicle) landing on it, or killing it if hostile; each unit counts once. 27 rows have data 0 and no pane (dungeon
  medals, challenges, "invisible script checklist", "signal yourself"): script-only, stay synthetic.
- Quest objective type 15 (Unknown15, 26 rows) — likely. "Kill X and Y" named kills. Data is a TargetGroup in the older rows (ids up to
  ~14000); in later rows data is not a group (values do not join to any table consistently: Spell4, Prerequisite, Creature2 hits are
  unrelated by name) and the reward pane group plus the $(creature=N) names in the text are the targets. Count 1 with several named
  creatures completes when each named creature has been killed (guess on the "and"; no counter-example in the data).
- Quest objective type 31 (Unknown31, 260 rows) — certain that it is tradeskill content: 257 of 260 are in quest category Tradeskills
  (work orders: "Work Order: Novice Training Armor", "Titanium on My Mind"), the other 3 are dailies with random sub-tasks. Data (38..621)
  does not join to any client table (scanned all 385 tables; no tradeskill table has these ids) — it points at a server-only work-order
  table. Undecodable without that table and without tradeskills; left synthetic, like CraftSchematic/ObtainSchematic.
- Dynamic (per-mille) kill objectives: progress now counts kills exactly; before, count 3 gave 3 x 333 = 999 and never completed.
- UnitPropertyModifier types 1-4 — data cannot separate them further. All four have the same A/B form (value*A+B: type 2 0.75/0 snares,
  type 3 1.0/0.01 crit adds, type 4 1.0/80 max shield), so all are applied in ascending type order (already the case in
  UnitEntity.GetModifiedProperty). Distribution: type 2 is 65% debuffs (A<1), type 3 talents/AMPs/stances, type 4 encounter buffs and item
  specials. Upstream NexusForever (master, develop, spells-v2-gr) and SpellWorks do not implement or name them. Order = guess, form = certain.
- Proc triggers (DataBits00), from 3498 rows, spell names and tooltips (research/decode/proc-tooltips.txt):
  certain: 1 on kill, 12 on damage dealt ("chance on damage dealt"), 16 on hit received, 20 on heal other, 146 suffering a critical hit,
  17/18/19 on damaged melee/ranged/magic (names), 124 on dash; likely: 0 on death, 10 on cast, 21 on being healed, 30 on hit,
  114 shield damaged, 115 shields depleted, 145 landing a critical hit, 147 landing a critical heal, 251 multi-hit, 51 deflect, 53 block,
  41 on being CC'd, 152 focus threshold, 40 health threshold. Fired now: 0 1 10 12 16 20 21 30 114 115 145 146 147. 17/18/19 (no
  melee/ranged/magic split of hits), 51/53/124/251/41 (no deflect, block, dash, multi-hit or CC events on the server) stay registered.
  Heals can now crit (147 tooltips: "direct Critical Heal").
- Proc mode (DataBits03) — likely: bit field. Bit 4 = proc spell aimed at the other party (its effects use target flag 2 in 554 of 700
  rows); bits 1/2 = aimed at the proc holder (target flag 1 in ~85%). 8/16/32 undecoded; names give 10 "Caster to Target",
  18 "Target to Caster" (Spellslinger Affinity). Implemented: primary target = other when bit 4, else self when bit 1 or 2.

## Combat formulas (audit 2026-10-03, scripts/combat-check.sh, research/decode/combat/)
Evidence per formula, with confidence. Sources: game tables (build 16042), greenyneko.com "Damage Calculation In WildStar" (2017-12),
archive wiki Attributes/Shield/Quick Draw pages (research/wiki-all.jsonl), 5458 archived Jabbithole NPC pages with "Health: x - y"
(research/decode/combat/jhstats.tsv), bezgelor (research/bezgelor: invented numbers, health 50/level, crit 5%, x1.5; not used as evidence).
- Spell amount = sum of ParameterValue x stat(ParameterType) over the four pairs of the Spell4Effects row (Combat/SpellFormula.cs).
  certain: 12 = assault power, 13 = support power, 10 = caster level. 12 624 Damage rows and 1 057 Heal rows use only decoded types; player
  abilities carry exactly (12, a) + (10, b), heals (13, a) + (10, b). Matches greenyneko baseDamage = AP x mod + levelStatRate x level;
  Quick Draw wiki "+9.26% + 4.94" vs table 0.1029/5.5; Mortar Strike blog 94.02%/29.77 vs table 0.7953/25.18 (both x1.182: a later patch).
  likely: 1-4 = Strength/Dexterity/Technology/Magic (creature attacks pair them with 12 at the same value in 7374/7391 rows; the school follows
  the attribute: Swipe/Snap physical type 1, Zap/Mortar tech type 3, Lash/Fire Storm magic type 4). likely: 7 = % of the target's max health
  (tooltips "of the target's max health" x5, self heal "of your Max Health"). Undecoded, left out and counted (`spell-param-unknown`):
  6 weapon damage ("[600% Weapon Damage]", no weapon damage on the server), 8 (% of health, current or max unclear), 9, 18, 5, 11, 15, 14, 22.
  Update 2026-10-04: 5 = Wisdom (likely; heals 27592/27972 "700%/400% of your Wisdom" carry (5, 7.0)/(5, 4.0); property 4 of the row);
  8 = max health of the unit hit (likely; Blistering Heat 27935/27936/27946 "8%/16%/24% of your max health" carry (8, 0.08/0.16/0.24), self heal
  46267 "15% of your maximum health" (8, 0.15)); both applied. 6 and 9 stay unknown (certain that they mean weapon damage: 27264 "75% Strength,
  100% Weapon" = (1, 0.75)+(6, 1.0), 33868 "[Weapon + Dex damage]" = (9, x)+(2, x)), but build 16042 weapons carry no damage value: Item2 has no
  damage field, Item2Category weaponPowerBase/Modifier are 0/1 for every row, and 2016-19 weapon tooltips (Jabbithole) list only ratings. They
  appear on beta-era abilities and four auto-attacks; the last soak (20261004-1538) logged 0 type 6/9 casts. Remaining unknown at runtime: 15, 11.
  Rows with no parameters (14 758 Damage rows, mostly creature spells) keep the older DataBits00 x assault rating form (guess; since 2026-10-11 x assault power = rating x 0.25). With parameters
  DataBits00 is only 0 or 1 and is not used as a coefficient.
- Fixed: before this audit every Damage/Heal effect did DataBits00 (mostly 1.0) x the full assault rating: a level 50 Quick Draw shot did
  ~3523 instead of 637. HealShields read ParameterValue00 (0.74 support power coefficient) as a fraction of max shield; now the formula.
- Player AP/SP: since 2026-10-04 the level row plus equipped item ratings (see "Equipment stats"). Earlier: CreatureLevel row of the level (3522.8 at 50, 61.6 at 1; the row is 4 x the attribute value at every level). guess: players
  without gear match the level row. greenyneko "AP rating / 4" converts item ratings (17765 -> 4441.25); applied since 2026-10-11 (see "Combat math").
- Crit: certain severity default 1.5 (UnitProperty2 127 default; greenyneko 150%); expected multiplier 1 + chance x (severity - 1) (greenyneko).
  guess: base chance 5% (UnitProperty2 102 BaseCritChance default is 0; the real base comes from class/rating data not found). Creatures never crit (guess).
- Armor: likely it mitigates every damage type (wiki Attributes: "the blanket reduction stat"; resistances "based on your Armor Value") - now
  applied to tech and magic too, and to creatures from their table armor (wiki: "for both friends and foes"; greenyneko example boss resist 47.12%).
  guess: curve resist = armor / (armor + 1.5 x level-row armor), 40% at level armor, cap 90%; no source gives the curve. Armor pierce,
  deflect/strikethrough, multi-hit, vigor, tenacity (greenyneko forms) are not applied: the server has no ratings to feed them.
- Shield: certain 50% of each hit goes to the shield (wiki Shield; ShieldMitigationMax default 0, 0.5 used). Regeneration uses the table
  defaults ShieldRegenPct 0.25 per ShieldTickTime 500 ms after ShieldRebootTime 2500 ms (UnitProperty2 176-178). The wiki says 8 s reboot and
  5%/s (launch-era energy shield items); the table is the build the server serves, kept. Creature shields: table index 41 (archetype x difficulty).
- Fixed (certain): the server's table reader shifted Creature2ArcheType.UnitPropertyMultiplier by one slot (the pad after the Icon string was
  read as element 0, the check for "string is the last field" compared a table field index with the model field count). Every creature got
  the multiplier of the next property: Strength 0, health x1 instead of e.g. 0.96, armor x0 for archetype 10, assault/support from the wrong
  slot. Adimar (59617, level 50): server 15 366 health, table 21 590, Jabbithole 24 300; armor 0 -> resist 0. Raw bytes: row 10 has five 1.06
  floats at record offset 16 (Python reader and the bytes agree). Also fixed: 64-bit fields are now 8-byte aligned in the record, as the
  Python reader does; TradeskillTier.RelearnCost/AchievementCategoryId and CharacterCustomizationSelection.Cost were read 4 bytes early.
  A full byte-level comparison of both readers over data/tbl (scripts/research/tblcmp.py) finds no other differing table.
- Creature stats: certain form level row x archetype x difficulty x tier: Jabbithole difficulty ratios equal the table ratios exactly
  (diff2/diff1 1.3437, diff4/diff1 1.994 at level 50). The live health of 2014-16 is table x 1.126 at level 50 (549 pages, mode) and up to
  x1.37 at level 1-6, the same for every difficulty/archetype: the CreatureLevel health curve changed between those patches and build 16042.
  Kept the table (likely right for this build); if real-client health bars disagree, multiply BaseHealth by the per-level ratio in jhstats.tsv.
- Player base health/shield: CreatureLevel BaseHealth (7) and ShieldCapacityMax (41), equal values (39706 at 50). guess: no class
  table holds player health (Class, ClassSecondaryStatBonus hold innate spells and attribute->secondary 0.5 ratios only).
- CC break on damage: certain CCStates.defaultBreakProbability is 1.0 for Sleep and 0 for every other state; a hit now removes Sleep
  (UnitEntity.TakeDamage, before shields). Immortal bots return before that point, so their sleeps still run out by time.
- CC duration and property modifiers: unchanged by this audit (see the UnitPropertyModifier and CC sections); CCStateDiminishingReturns has
  names only, no numbers, so diminishing returns are not modelled.

## Equipment stats (Game/Combat/ItemStats.cs, 2026-10-04)
Sources: 610 archived Jabbithole item pages captured 2016-2019 (after the Drop 6 / F2P stat revamp; scripts/research/jh_items.py, sample
seeded and stratified by slot, raw pages in research/items-raw/, parsed research/decode/items/jh-items.tsv, 484 matched to Item2), the fits in
scripts/research/item_fit.py and item_expect.py, greenyneko.com (2017-12 "Calculating Assault Power Rating on Items" and "How To Calculate More
Stats On Items", 2018-02 "Calculation of the Maximum Shield Stat"), archive wiki infoboxes Consort's Vesture and Picaroon's Terrawarp Garment.
- certain: item level = Item2.PowerLevel (Jabbithole "Effective Level" equals it on 475 of 484 items; it runs requiredLevel +0/+2/+4 by quality).
- certain: ItemStat type 1 = a UnitProperty2 id, its budget is the ItemBudget fraction in the same column; type 2 = an ItemRandomStatGroup id
  (group 14 holds exactly the four shield properties). guess: random groups take the entries in table order, the server stores no per-item roll.
  Type 4: ItemStat rows 440-451 hold 1-4 copies of type 4 with data 63, 127 or 64; 542 items use 440-446, 531 of
  them are crafting outputs (TradeskillSchematic2) and the other 11 are Strain/Grimvault rewards. likely: a "Random Attribute" slot (archived
  Jabbithole tooltip of Breach-Finder 37682 lists "+ Random Attribute" lines after its ratings; crafted items show none, the crafter fills
  them with additives). guess: the data is a mask of the allowed attributes (63 = six bits = the six pre-Drop-6 primary attributes, 127
  adds a seventh). No budget exists for these slots, so they add nothing (ledger `item-stat-random-slot`).
- certain: max health = 60 x ilvl x ItemSlot.ArmorModifier on the six armor slots (187/187 tooltips; greenyneko head 60/chest 75/gloves 45 per level).
- certain: assault + support rating = 3.6 x ilvl x slot ArmorModifier on armor, weapon attachment, support system and implant slots (247 exact,
  49 within 1-3%); 4.5 x ilvl x slot when the item has no ItemStat row (every such item in the sample, ratio 4.39-4.5); weapons 111.375 x ilvl (Adaptive Infoblade ilvl 116:
  3229 + 9689). greenyneko gives the same 3.6/4.5/2.7 per slot. Support share = (Item2.SupportPowerPercentage + 1) / 2 (certain: -1 -> 0% 116 items,
  -0.5 -> 25% 89, 0 -> 50% 43, 0.5 -> 75% 47, 1 -> 100% 92).
- likely: armor = Item2Category.ArmorBase + 20 x L x slot ArmorModifier x category ArmorModifier, L = ilvl up to 50, above that 50 + 0.34 x (ilvl - 50)
  (186/189 tooltips exact; the knee is a fit of 57 items of ilvl 52-120).
- likely: secondary ratings = budget fraction x 5 x ilvl x slot ArmorModifier (weapons 3.75 x ilvl) x UnitProperty2.ValuePerPoint / 10 (ratings 10,
  focus 1.5, health 18 per point all fit). Exact up to ilvl 50; above it the tooltips are 4-11% lower without a clean curve, kept linear.
- likely: energy shields (ItemStat 139/439 = capacity, mitigation, regen, reboot) with budget fraction f: capacity 75 x (1.5 + f) x ilvl
  (greenyneko 225 x ilvl at f = 0.5, 30/45 tooltips exact, the rest are random-group rolls), mitigation 25% + 75% f, regen 5% + 20% f, reboot
  10 s x (1 - f) (tooltips 25/62/81%, 15/20%, 2/5/10 s). Applied to ShieldMitigationMax, the regen fraction per shield tick and the reboot time.
  guess: the regen percent uses the same per-tick unit as the UnitProperty2 default 0.25.
- Player stats = CreatureLevel row of the level + sum of equipped items (health, shield, armor, ratings). guess: the row stays as the base (no
  source says what a naked level 50 has). Assault/support power = rating x 0.25 since 2026-10-11 (certain, client binary; see "Combat math"); the level row has
  Strength..Wisdom = rating / 4 at every level, i.e. the attributes already are power-sized.
- Secondary ratings (crit, multi-hit, strikethrough, vigor ...) are stored on the player's combat stats; since 2026-10-08 they are converted by
  the client's rating curves ("Rating curves", Combat/DamageMath.cs). Gadgets (slot 46) and augments get no power (no tooltip sample).
- Equip/unequip marks the player's gear dirty (Inventory AddItem/RemoveItem on the equipped bag); the next player update recalculates, never
  inside the inventory call. Bots (Bots/BotGear.cs, every 25-35 s) equip carried items with a higher rating score that their level, class and
  class armor/weapon proficiency allow (Class.StartingItemProficiencies & ItemProficiency bit of the item category). Ledger: bot-gear-equipped,
  bot-gear-refused, gear-stats-applied. Checked by scripts/combat-check.sh against tooltips (gt itemstats).

## Tradeskills (Game/Tradeskill/TradeskillManager.cs)
- Tradeskill.Flags (certain, the client's CraftingLib.GetTradeskillInfo): 1 bIsHobby, 2 bIsHarvesting, 4 bIsCircuitBoardCrafting, 8 bIsCoordinateCrafting, 16 bIsAutoLearn. Cooking = 1+8, Fishing = 1+2, Farmer = 1+2+16, Runecrafting = 16.
- Limit (certain, client trainer window + GetTrainerTradeskills): a trainer offers every skill without flags 1 and 16 (Weaponsmith, Armorer, Outfitter, Tailor, Technologist, Architect, Mining, Survivalist, Relic Hunter) and the window counts the active ones against 2 (knMaxTradeskills); gathering counts. A third needs a swap ("swap with" sends the second id of ClientTradeskillLearn). The server refuses a third (TradeskillManager.MaxTrainerSkills). Before 2026-10-17 the server counted flag 4 + Technologist only and left Architect and gathering out.
- Known schematics: learned ones (schematic item, achievement reward, discovery, crafted before) plus flag-1 schematics up to the active tier (certain: on a tier change the client adds every schematic with flag 1 and tier <= the new tier to its known list itself; 1253 rows). Flag-0 roots are taught by GiveSchematic items (217) or TradeskillAchievementReward (371); a non-discoverable coordinate variant (child row) is known with its parent (likely). Until 2026-10-17 every non-discoverable schematic counted as known. Tier = number of the skill's TradeskillTier rows whose RequiredXp is reached (certain, client profession update).
- Coordinate crafting: success when the click is within Radius of (VectorX, VectorY), critical within CritRadius (if a crit output exists), failure gives Item2IdOutputFail. Additives, catalysts and talent bonuses: see "Crafting: catalysts, talents, additives, power cores, salvage, farming".
- XP per craft = TradeskillTier.CraftXp (FirstCraftXp the first time, FailXp on failure) of the schematic's tier.
- Prerequisite 19 (certain, client prerequisite evaluator): object = TradeskillTier row; the character's tier of that skill (0 if not learned or swapped out) is compared with the row's tier by the comparison; the value column is not read. Equal therefore means exactly that tier (65 rows of tier quests). Until 2026-10-17 the server used "0 when the tier is reached, -1 else" against the value (likely, from failure texts).
- GiveSchematic (effect 0x26, certain from the data): DataBits00 = TradeskillSchematic2 id; the schematic joins the known list. 385 of 1171 rows name an existing schematic, the rest name removed ids (ledger give-schematic-unknown).
- State is saved in the character database since 2026-10-08 (character_tradeskill: xp, earned talent points, picked talents; character_tradeskill_schematic: discovered/crafted). The older JSON files are imported once.
- QA harness: bots learn, craft and gather through the client message handlers (BotTradeskills). A quest that needs tier N makes the bot craft schematics of its current tier at a crafting station until TradeskillTier.RequiredXp is reached (real xp; until 2026-10-08 the xp was granted, ledger tradeskill-qa-tier-grant, now gone). Materials are still granted when the bot lacks them (tradeskill-qa-material-grant): research/tradeskill-materials.tsv (scripts/research/tradeskill_materials.py) shows 37 of 579 materials come from harvest nodes, 55 from creature drops, 137 from other schematics and 350 (5093 of 5752 schematic uses, mostly runes, signs and chunks from salvage) from no known source; no vendor in the world database sells any of them. Quests behind a tier above 1 are counted as heuristic kind "craft-materials" (was "tier-grant").
- Crafting needs a station within 20 m (Creature2.TradeSkillIdStation = skill or 0xFFFFFFFF, certain from the table) for the client messages; 59 station spawns in 4 open worlds.
- Talent points come from tradeskill achievements: TradeskillAchievementReward.TalentPoints (206 points over 761 rows; the same rows also teach schematics and give reputation) — certain from the table fields; the skill is found by walking the achievement's category up to Tradeskill.AchievementCategoryId (likely). This replaced the earlier 2026-10-08 guess of one point per tier.
- Tradeskill achievement types (certain, from the 761 reward rows' titles and descriptions): 35 craft N of an item (ObjectId item), 37 reach a tier (ObjectId TradeskillTier row), 40 craft each item of a checklist, 44 learn each schematic of a checklist, 76 craft N of any checklist item (counted in Data1). Types 6 (quest checklist) and 7 (child achievements) are not driven here.
- Talent picks: ClientTradeskillPickTalent names a TradeskillTalentTier row and one of its five bonuses; rows are ordered by PointsToUnlock, a row needs that many points already spent (likely). Bonus effects (TradeskillBonus): see "Crafting: catalysts, talents, additives, power cores, salvage, farming".
- Quest reward type 4 (TradeSkillXp): ObjectId is a TradeskillTier row (all 34 distinct ids are tier rows, several are not Tradeskill ids), the amount goes to that row's skill if learned (likely). Type 5 (TradeSkill): ObjectId is the skill to learn (one row, quest 6380 -> 21 Tailor; likely).
- Swapping out (certain, client): a swapped-out skill stays in the known list inactive (TradeskillInfo.IsActive 0) with its tier; CraftingLib.GetRelearnCost reads TradeskillTier.RelearnCost of that tier (tier 2: 10000, 3: 50000, 4: 250000, 5: 500000, 6: 2500000 copper). The server keeps xp, talents and points (character_tradeskill.active) and charges the cost on relearn (likely: the client shows it as the price). The relearn cooldown (client GetRelearnCooldown, a server timestamp in ServerProfessionsLoad) has no duration in any table: none applied (assumption). Learning an already active skill is refused (the client sends nothing then). Auto-learn skills (flag 16) are given at login (likely, from the flag name).

## Harvesting (Game/Entity/HarvestUnit.cs)
- Nodes are entity type 5 (HarvestUnit, 5015 world database rows of 83 creature kinds; Creature2.CreationTypeEnum 5 + TradeskillHarvestingInfoId on 296 creatures). Who can gather = the client's Unit:CanBeHarvestedBy (client code): the node's TradeskillHarvestingInfo.PrerequisiteId (type 19, tradeskill tier at least the node's) — certain.
- Tools: Item2 family 28 (Laser Pickaxe = category 106 Mining, Laser Chainsaw = 116 Survivalist, Relic Blaster = 117 Relic Hunter; Item2Category.TradeSkillId), item slot 7; ItemSpecial.Spell4IdOnActivate is "<tool> - Harvesting - <skill> - Tier N" with two Damage effects at the target (certain from the tables). Source for "Survivalists harvest wood by felling trees with a laser chainsaw", "The Miner uses a Laser Pickaxe": wildstaronline-archive.fandom.com/wiki/Survivalist, /wiki/Mining.
- One tool use gathers the node (guess: node health versus tool damage is not in the data; WildStar showed several tool hits). Loot = the node's researched drop table (Jabbithole, data/research/creature-drops.tsv by node name; 62 of 168 node names have rows, e.g. Celestion Tree -> Knotted Heartwood 100%), each row rolled by its chance, the likeliest row given if nothing rolled (assumption). Xp = TradeskillTier.CraftXp of the node's tier (gathering rows have learn = craft = quest xp; likely). The node then respawns after the normal respawn time.
- Node drops are pre-revamp item ids: 12 of them (Item2 flag 0x4000, or used by no schematic) have a same-name current material the schematics use (Iron Chunk 12542 -> 81821, Platinum Chunk 14188 -> 81839, Titanium/Xenocite/Galactium Chunk, Quartz, Diamond, Sapphire, Chrysalus, Starshard, Sign of Earth/Fire). A gather gives the current id (likely: same name, exactly one current candidate each; ledger harvest-material-current-id). The wiki pages of these materials say they come from nodes (e.g. Iron Chunk: "The nodes for this resource can most commonly be found in Algoroc, Celestion, Deradune"). With it 43 of the 579 schematic materials have a node source.
- QA harness: a bot with a craft goal gathers the materials of its next training craft from nodes that drop them (travel to the node kind, swap its gathering skill in at the trainer, tool use) for up to 3 minutes per material; what is still missing is granted (tradeskill-qa-material-grant, gathered ones tradeskill-material-gathered). A skill whose xp used granted materials is marked in memory; craft-tier quests completed with unmarked skills count as real (quest-craft-tier-real), the others keep the heuristic kind craft-materials.
- No vendor stock or drop source for tools exists in the data (the tools have 99-credit buy prices, so a tradeskill vendor sold them in the game); bots get the novice tool as a counted QA grant (tradeskill-qa-tool-grant). Survivalist skinning of creature corpses is not implemented.

- Objective data not found in TradeskillSchematic2: 141 of 215 CraftSchematic and 972 of 2520 ObtainSchematic objectives use ids that join to no table (counted as tradeskill-schematic-unknown, stay synthetic).

## Crafting: catalysts, talents, additives, power cores, salvage, farming (Game/Tradeskill/TradeskillBonuses.cs, Salvage.cs)
Sources: the retail client (catalyst function, Item:GetGlobalCatalystInfo / GetAdditiveInfo / GetBuyPrice / CanSalvage, CraftingLib AddAdditive /
GetAvailableCatalysts / GetAvailablePowerCores / GetSchematicInfo / CraftItem / CompleteCraft, the interact function), the client crafting window
(CraftingGrid.lua, Crafting.lua), the tables, wildstaronline-archive.fandom.com (Circuit_Board_Crafting, Hobby, Tradeskill) and archived Jabbithole item pages.
- Catalyst effects (TradeskillCatalyst.tradeskillCatalystEnum, certain: the client's switch, named by the TEST items 2-15 and the item tooltips): 1 X x value,
  2 Y x value, 3 swap X/Y, 4 rotate the vector by value degrees (counter-clockwise), 5 invert the vector, 6 landing radius max x value, 7 landing radius
  min = min + (max - min) x value, 8 landing start angle = value degrees, 9 landing angle range = value degrees, 14 vector x value; 11 / 12 / 13 multiply
  the crit / craft / discovery target radius; 15 / 16 "attract the crafting point to the nearest crafting / discovery target at craft completion".
  Enum 10 has one row (value 0, no item): unknown, ignored. A catalyst fits when its row has the schematic's skill and exactly its tier (certain).
  "Global" = first effect 11-16 but not 14 (certain); the crafting window offers only global catalysts at the craft start, the additive message carries
  one that changes additives (effects 1-9 or 14, at most up to the fourth additive: certain). The start catalyst's additive effects also apply to every
  additive (the client's GetAdditiveInfo applies the craft's catalyst, then the additive's). Catalysts are used up when sent (likely).
  Attraction moves the point to the variant's centre (likely). No schematic has CritRadius > 0, so effect 11 changes nothing today.
- Additives (certain, client crafting window "Additives Store"): additives are bought per use for the item's buy price x bonus 14, not taken from the
  bags; all 127 additive items have no buy price in Item2, so they cost nothing here (ledger tradeskill-additive-bought; the client may compute a
  default price from a formula that was not decoded). Limit per craft: schematic MaxAdditives, else its TradeskillTier's, else the skill's, plus bonus 26.
  Additive tier at most the schematic's tier; hobby skills (Cooking) 1 + bonus 27. Landing: vector x bonus 15, plus a random point in the ring sector
  radius RadiusMin..RadiusMax (RadiusMax = TradeskillAdditive.Radius x bonus 16), angle StartAngle..StartAngle+MaxRotation (default 0..2 pi): the shape is
  the client's (certain); uniform over the area and the angle measured from the grid +X axis is a guess. Before this the server moved the point by
  the vector alone and took the additive item from the bag.
- TradeskillBonus objects (likely: every row's tooltip agrees): primary = tradeskill (0 = any), secondary = Item2Type of the craft output (0 = any),
  tertiary = Item2Type of the material for 6 / 11 ("Ore", "Wood", "Meat", "Omni-Plasm" ...) and the additive direction for 14 / 15 / 16 (1 X > 0,
  2 Y > 0, 3 X < 0, 4 Y < 0: certain, the client's accumulator). Multipliers multiply, counts add (client accumulator modes; per-type mode inferred
  from the values). Applied: 5 success xp x, 65 failure xp x, 6 material count - value (never below 1), 10 output count x (fraction = chance of one
  more: guess), 11 chance that the matching material is not used up, 14 additive price x, 15 additive vector x, 16 additive landing radius x, 17 craft
  target radius x, 20 discovery target radius x, 26 additive slots +, 27 hobby additive tiers +. Stored only: circuit-board bonuses 1, 2, 3, 8, 9, 12,
  21, 55-63 (sockets, charge, overcharge, failure: the server does not model circuit charges), 4 summon station, 13 maker's mark, 28 extra catalyst,
  64. Active rows: picked talents plus rows with TradeSkillTierId (tier reached) or AchievementId (achievement completed) (likely). Ledger
  tradeskill-bonus-applied per type. Bonus ids repeat across talent tiers (Outfitter bonus 332 in tiers 2, 5 and 8, the other trainer skills alike): a pick fills a tier slot, the same id picked in two tiers counts twice (likely: the client checks only the slot and the tier's five ids).
- Discovery radius: a discoverable variant not yet learned uses its Radius x discovery multipliers, every other variant Radius x craft multipliers (likely).
- Circuit crafting (certain from the client control flow): CraftItem sends 0x0852 for every skill without flag 8; for circuit skills (flag 4) it starts the
  craft and CompleteCraft sends 0x084F with the power core. Materials are used at the start, abandoning loses them but not the core (wiki
  Circuit_Board_Crafting). Before this the server crafted on 0x0852 and refused the 0x084F. Socket charges, AP/SP split and the overcharge failure
  chance are not modelled: the craft succeeds (ledger tradeskill-circuit-charges-ignored).
- Power cores: universal schematics (tier 0 and schematic flag 4) need none (certain, GetSchematicInfo bIsUniversal). Otherwise one bag item of
  Item2Category 143 with PowerLevel at most the output's and slot flags 0 or overlapping (category and slot test certain, the compared field likely:
  the 35 cores use exactly the circuit outputs' power levels), used up at completion. "If you use a level 50 schematic but only a level 15 Power Core,
  the completed item will be level 15" (wiki): items have no per-instance level in the character database, so a lower core is counted
  (tradeskill-powercore-level-not-stored) and the output keeps its own level; bots use the core of the output's level. Core quality (3-6) is
  recorded with the use, its effect on the item is not decoded.
- Salvage (ClientItemSalvage 0x015E {ItemLocation, u64 guid}, certain: the SalvageItem confirm button (type 4) sends it; upstream named it
  ClientItemUseLootBag). Allowed (certain, client): Item2.Flags 0x100 never; a Salvage row of the item's Item2Type with Level <= the item's level
  (PowerLevel: likely), or a SalvageException row (flags 4 = needs a salvage key: no keys exist here, refused and counted); character level >=
  GameFormula 1034 dataint0 = 8 (the wiki's "level 6 {{fact}}" is wrong). Outputs: not in the client; archived Jabbithole "Salvaged to" tabs of 476
  items (scripts/research/jh_salvage.py -> data/research/salvage-outputs.tsv, 371 of 2335 type/level keys, 521 output items). The item's own rows, else
  its type and band, else the nearest band of its type; each output rolled by its observed share, one each, the likeliest if nothing rolled; pre-revamp
  material ids map to the current ones like harvest drops. Salvage critical: ItemQuality.SalvageCritChance rolls the table twice (guess from the
  wiki's "Salvage Critical, which can drop more rare materials"). Outputs of types without data: refused, ledger salvage-no-output-data.
- Farmer (certain, client interact function): for nodes of skill 20 the client skips the harvesting tool and casts the class auto attack
  (Class.Spell4IdAttackPrimary / Spell4IdAttackUnarmed) at the node; "The ability to harvest plants is inherent to all players and does not require
  a trainer or special tools" (wiki Hobby). The server gathers a node of a gathering skill without tool items (Farmer) when the player's auto attack
  hits it. Without a tool in the tool slot the same interact casts the auto attack at Mining, Survivalist and Relic Hunter nodes too (certain, same
  function); the wiki's Mining page: "the player's weapon will do just fine, the proper tools will make the process much faster". The auto attack
  gathers any node the character may harvest (tier prerequisite); one hit gathers like a tool use (guess: no speed data). Farmer has one tier with
  0 xp, 2054 node spawns of 58 kinds, 40 of 49 node names have researched drops. Survivalist skinning ("skin animals and butcher them for meat",
  /wiki/Survivalist): no table marks skinnable creatures (the 26 non-node creatures with a TradeskillHarvestingInfo are disguised nodes), no client
  message for it was found; the leather and meat come from the researched creature drops for every player: not implemented.
- QA harness: bots pay missing relearn credits (tradeskill-qa-relearn-credit-grant), get catalysts and power cores as counted grants
  (tradeskill-qa-catalyst-grant, tradeskill-qa-powercore-grant: no source for the current items in the research data), a talent point when the
  probe needs one (tradeskill-qa-talent-point-grant), and salvage researched items (salvage-qa-item-grant).

## Challenges (Game/Challenge/ChallengeManager.cs)
- Source: wildstaronline-archive.fandom.com/wiki/Challenges (four player-facing kinds; timed; bronze/silver/gold). Type numbers are now from the client (see "Challenges" below). Rewards: "Challenge rewards" below.

## World bosses (data/encounters/worldbosses/world-bosses.json)
- Which creatures are world bosses: Creature2 internal names tagged "[WB]" (9 with spawns: Metal Maw, Metal Maw Prime, Kraggar the Earth-Render, King Honeygrave, Dreamspore, Grendelus the Guardian, Zoetic, Scorchwing, Gargantua). The wiki has no mechanics for them.
- Ability names are real spells from Spell4 whose descriptions name the boss; phase thresholds, timers, targets, add counts and enrage (600 s) are assumptions.

## Group loot, lockouts, veteran mode, resets
- Group loot: rules are per group. The leader sets them with ClientGroupLootRulesChange (0x042E, layout in docs/PROTOCOL.md, confidence likely); defaults are NeedBeforeGreed for both rules with threshold Good, the values ServerGroupJoin sends to the client. Items below the threshold quality use the normal rule, items at or above it use the threshold rule (certain: field names eNormalRule/eThresholdRule/eThresholdQuality in the client UI GroupFrame/RaidFrameMasterLoot Lua and in upstream). Before this change items below the threshold went to the killer. Only the leader may change the rules (upstream GroupServer). Harvest rule is stored and sent, not applied (no harvest loot split here). In dungeons/raids one drop roll is made for the group and the rule picks the winner (class-usable items win over greed rolls; Master gives the item to the leader because the master loot choice message is single-sourced); open world loot stays personal.
- ServerGroupLootRulesChange (0x042F) carries an unknown u32 after the group id; it is sent as 0 like upstream (guess).
- Client protocol research (2026-10-05): see docs/PROTOCOL.md. The client binary research read the layouts of trade, loot roll/master, instance settings, marketplace, resource conversion and daily login from the retail client binary itself (certain); they are implemented except loot roll/master (no loot window here, counted only).
- Instance settings (Game/Map/InstanceSettings.cs): the choice from ClientSetInstanceSettings belongs to the instance owner (group or lone character), only the group leader may set it, and it applies to instances created afterwards (an instance keeps its difficulty until it resets). The portal unit in the message is not mapped to a world, so the choice covers all dungeon worlds (guess). NF_VETERAN_WORLDS still forces veteran. Prime level and rally are stored, not applied (no prime/rally tables are used). ClientResetInstances unloads the owner's empty instances (leader only); ClientResetSingleInstance is counted only (portal -> world unknown).
- Raid lockout: source wildstar.fandom.com/wiki/Raid (weekly, cleared Wednesday 02:00 PST = 10:00 UTC). A locked player gets no loot from a boss they already killed this period. Dungeons have no lockout. Daily dungeon reward (5000 credits, once per day, reset 10:00 UTC) is an assumption. State is in memory (lost at restart).
- Veteran mode: veteran (Prime) creatures are separate Creature2 rows, not a scaling of the normal ones (certain: "[STL] e314 - (N) Stormtalon" 17163 level 20 tier 1 and "[STL] e314 - Stormtalon - Veteran" 33406 level 50 tier 5; the same pattern for the [SK], [KV], [TS], [UPJ] dungeons and the W2149/PE390 shiphand units). scripts/research/veteran_pairs.py pairs every spawned instance creature with the veteran row of the same localized name, preferring the same description tag ([STL]), encounter code (e314) and display group; without a tag match the veteran row must carry the dungeon's own tag or the same world token (W2149). Result data/research/veteran-variants.tsv: 180 normal -> veteran pairs in 11 worlds, 25 Swordmaiden (1271) spawns that are veteran rows get their "- Normal" row in normal mode, 40 ambiguous pairs left out (listed by the script). A veteran instance spawns the veteran row with its own level range and tier (Creature2Tier 5: x1.394 attributes, x1.673 health), so the old flat x1.5 health / x1.25 damage now only applies in instance worlds without any veteran rows (guess, kept from before). Raids (Datascape, Genetic Archives, Red Moon Terror) have no veteran rows: one difficulty (certain from the table, no Veteran/Prime description in [DS]/[GA]/[RMT] rows). Loot: no separate veteran loot table exists in the client data; the veteran rows drop what CreatureLoot gives their own level (likely).
- Veteran choice for bot teams: the team leader sends ClientSetInstanceSettings (0x0163) through the handler before entering; NF_BOTS_VETERAN = alternate (default: every second run of a world with veteran rows), all, off. Team members take the quests whose kill objectives target a creature of the instance (QuestManager.CanAcceptWithoutGiver + QuestAdd, the same rules a player meets); the Prime dailies (9843-9848 ...) are refused while they are not in the day's periodic offer (real rule).
- Dungeon clear (QA harness): a run is clear only when no hostile spawn is left in a grid the bots have not visited (spawns in unloaded grids do not exist yet; Skullcano veteran was counted clear with Redmoon, Thunderfoot and Tugga never spawned).
- Dungeon team target choice (QA harness): bots prefer encounter bosses in reach over adds and hazard units; a bot on an add looks for a boss every 6 s. Kel Voreth veteran stalled for 20 minutes while the team fought Trogun's Fissure units (1.4 M health each) with the boss at 70 %. Achieved quests are turned in by dungeon bots every 10 s without visiting the receiver (same harness rule as quest bots).

## Taxis, market, trade
- Taxi: no flight simulation; pays TaxiRoute.Price and teleports to the destination node's WorldLocation2. Nodes unlock at AutoUnlockLevel or when visited. Faction column not enforced.
- Auction house: bids are escrowed, outbid bidders are refunded, the house keeps 5% (assumed), sold/unsold goods are delivered at once to online players, otherwise held as claims. Commodity exchange is a limit order book (price then age priority, trades at the resting price). Trade is all-or-nothing. State is in memory.
- Player trade over the client messages (Game/Market/TradeManager.cs): one ClientP2PTradingCommit message toggles the sender's commit (certain: the client's Commit and P2PTrading.UnCommit both send it); any offer change clears both commits (certain: the client's own UpdateItem/UpdateMoney handlers clear both flags). Trade needs both players on the same map instance (target unit id is resolved on the initiator's map; likely, WildStar trade was range-limited) and the same faction (likely). Only bag items (not equipped) can be offered (likely). ServerP2PTradeUpdateItem's four item-instance fields and 20 trailing bytes are sent as zero (meaning unknown). TradeIndex is the item's index in the owner's offer (guess).
- Auction/commodity messages (Network/Message/Handler/MarketHandler.cs): auction listing time 24 h (guess, the message has no duration), 10 auctions per search page (guess), owned auctions = the player's listings plus the ones they lead the bidding on (guess), commodity price buckets 1/10/50 units (upstream comment) filled with the marginal unit price from the best orders (guess). Property, rune slot and equippable-by filters are ignored and counted (`auction-filter-ignored`). ForceImmediate commodity orders are treated as normal limit orders (counted). Seller cancel returns the item and refunds a standing bid (guess).
- Resource conversion message: the count is in source units (certain: the UI sends nBatches x nSourceCount); each batch is one conversion. The u64 field is 0 from the vendor UI and ignored.

## Vendor stock
- What: the world database has no `entity_vendor` rows (community data and researched spawns carry none). Creatures whose Creature2 description contains "vendor" (not DEPRECATE, "For Display" or test) get a generated stock: 24 credit-priced items (quality <= Good, no quest activation) with RequiredLevel within 2 of the creature's minimum level, chosen from a deterministic offset by creature id. Buy/sell multipliers 1.
- Why: without stock, vendors, buyback and currency sinks cannot be exercised by bots. Source: Creature2 descriptions (design-time names such as "Weapon Vendor"), Item2 price columns. Real stock per vendor is server-side data that does not exist in the client tables; categories are not generated.
- Code: Game/Entity/VendorFallback.cs, hooked in NonPlayer.Initialise.

## ProxyChannelVariableTime (effect 94)
- What: treated like ProxyRandomExclusive: DataBits00-09 are (spell4 id, weight) pairs, one spell is picked per execution of the effect and cast on the target.
- Why: the row for [Event Boss] Metal Maw - Shed Symbiotes lists spell ids with small second values (567, 167, 166) that look like weights or delays; picking one per tick is the least harmful reading. 26 occurrences in a 3 h soak. Source: game tables only (Spell4Effects 56741, 56744).

## Quest resets and Wake Here
- Quest daily/weekly resets (upstream: weekly on Tuesday 10:00 UTC) now use the same clock as the instance lockouts: daily 10:00 UTC, weekly Wednesday 10:00 UTC (= Wednesday 02:00 PST), source https://wildstar.fandom.com/wiki/Raid. Whether quest weeklies use the same weekday is an assumption.
- Wake Here (ClientResurrectRequest) has a 1 h cooldown per player (not persisted), source https://wildstar.fandom.com/wiki/Patch_09_29_2015 (free players; no Signature state exists here). While it is on cooldown the request is refused and the 60 s death timer resurrects the player. Graveyard/holocrypt/caster rez and the 50% Focus restore are not implemented (Focus vital is inferred only).

## Public event objectives without data
- What: objectives whose type has no creature or group behind it (Script 5, type 7, ScriptWithoutCount 9, 21, 25, 28, DefendObjectiveUnit or kill types with no resolvable creature) used to be credited after the rest of the event was done or 60 s (883 credits in a 3 h soak, 11 kinds). Now: with Count >= 2 every kill by a participant in the event area counts toward Count (`event-objective-played`, "counts kills"); with Count < 2 a participant must stay in the area for 15 s ("needs a participant"). A counted objective that is still open after 300 s (and has no shorter timer) is credited and recorded as `event-objective-synthetic`.
- Why: the tables hold no script text or counters for these (the localized strings are missing), kill counts are what the Count values (8-100) most likely measure in wave events. Not proven. Source: PublicEventObjective rows (e.g. 3294 Count 8, 5005 Count 75, 663 Count 20).

## Path missions (Game/Entity/PathMissionManager.cs)
- Mission type numbers (certain): the client registers PathMission.PathMissionType in code (client analysis): Soldier 0 Holdout, 4 Assassinate, 5 Demolition, 6 Rescue, 7 SWAT, 8 Script; Settler 9 Script, 19 Hub, 21 Infrastructure, 25 Mayor, 26 Sheriff, 27 Scout; Scientist 2 Scan, 10 Script, 14 ScanChecklist, 20 FieldStudy, 22 Experimentation, 23 SpecimenSurvey, 24 DatacubeDiscovery; Explorer 3 Area, 11 Script, 12 Door, 13 ScavengerHunt, 15 Vista, 16 ExploreZone, 17 ActivateChecklist, 18 PowerMap. The Script types have no rows. Other client enums read the same way: ExplorerScavengerClueType 0 CreatureDrop, 1 ActivateCreature, 2 Dig; SettlerAvenue 0 Economy, 1 Security, 2 Quality of Life; DatacubeType 0 Datacube, 1 Chronicle, 2 Journal (all certain).
- Which table a mission's ObjectId points at (certain: ObjectIds of each type fall into exactly one table): 0 PathSoldierEvent, 4 PathSoldierAssassinate, 5/6 PathSoldierActivate, 7 PathSoldierSWAT, 19 PathSettlerHub, 21 PathSettlerInfrastructure, 25 PathSettlerMayor, 26 PathSettlerSheriff, 27 PathExplorerArea, 2/14 PathScientistCreatureInfo, 20 PathScientistFieldStudy, 22 PathScientistExperimentation, 23 PathScientistSpecimenSurvey, 24 PathScientistDatacubeDiscovery, 3/15 PathExplorerArea, 12 PathExplorerDoor, 13 PathExplorerScavengerHunt, 16 MapZone (names match: "CARTOGRAPHY: Auroria" -> MapZone 6 Auroria), 17 PathExplorerActivate, 18 PathExplorerPowerMap.
- Targets the tables do not hold: Jabbithole NPC pages have a "path missions" tab with the PathMission id in the link (/path-missions/biology-corrupted-coterie-2391); scripts/research/jh_path_missions.py turns 1748 such pages into data/research/path-mission-targets.tsv (2085 rows; targets for Holdout 86/98, Scan 138/163, ScanChecklist 49/57, SpecimenSurvey 13/16, ScavengerHunt 53/56 missions). Creature ids are all Creature2 rows with the page's name (likely).
- Holdout (likely): started by using the creature whose Creature2.PathMissionIdSoldier is the mission (certain link: 106 such creatures for 98 holdouts, all with activate spell 1817 "Soldier Control Point"). Waves = PathSoldierEventWave rows of the event (certain count), failure after PathSoldierEvent.MaxEventTime (certain field). Enemies are the mission's Jabbithole targets, 3 per wave at 12 m around the start (guess: the wave contents are server scripts). The event types (Holdout, Defend, Timed, TimedDefend, TowerDefense, WhackAMole...) are not distinguished: all are "kill every wave in time" (guess).
- Scientist scan (likely): a scan is a successful cast of "Scientist Scanbot Scan" (Spell4 45403, 2 s, 15 m; 31469 the 8 s variant; effect only Fluff, so the server does the work) on a target. Scan: each Jabbithole target name scanned once (count = number of names, guess); ScanChecklist: PathScientistCreatureInfo.ChecklistCount different units (likely); SpecimenSurvey: one unit per objective text (likely). FieldStudy and SWAT count any successful spell on their target (as before).
- Experimentation (likely): use a creature whose Creature2.PathScientistExperimentationId is the mission's row; the pattern minigame is client side and counted as solved (guess). Datacube discovery (likely): use every type 0 datacube of the zone (Datacube.WorldZoneId under the mission zone, creatures with Creature2.DatacubeId), each once.
- Explorer: Area/Vista/Scout = visit every PathExplorerNode of the area (as before; Scout uses the same table, certain). Door (likely): the micro zone (PathExplorerDoor.WorldZoneIdInsideMicro) entered, plus one use of TargetGroupIdActivate and one kill of TargetGroupIdKill when set. ScavengerHunt (likely): every clue: CreatureDrop = kill the clue creature/group, ActivateCreature = use it, Dig = the explorer dig effect (PathActionExplorerDig, spells 27712-27714 "Searching") within max(ActiveRadius, 20 m) of the clue location.
- Settler: Hub (likely) = improvements built at the hub's depots (PathSettlerImprovementGroup.Creature2IdDepot): each use of a depot builds the next tier of one of its groups (ContributionValue + tier x PerTierBonusContributionValue to its avenue, capped by the hub's avenue maximum), complete at PathSettlerHub.MissionCount (= the sum of the three caps on 74 of 96 hubs). Mayor (likely) = one improvement at a depot within 80 m of each listed location ("Create an improvement in Hub 1!"). Infrastructure (guess for the unit) = use resource creatures (or cast the resource spells) to hold up to MaxStackCountResource00, use the depot to deposit, progress += resources held, complete at Count. Resource costs of improvements (PathSettlerImprovement.CountResource) are not charged: the settler resource nodes (PathSettlerHub map resource locations) are not spawned.
- ExploreZone (16, 20 missions): ObjectId is the MapZone (certain: "CARTOGRAPHY: Auroria" -> MapZone 6 Auroria, all 20 names match). Progress = MapZoneHexGroups of that map zone the player discovered, complete when all are (likely: "Explore all of Auroria"; the client's zone map completion is the same set, ZoneMap.IsComplete). Discovery uses the upstream position -> hex conversion (ZoneMapManager, certain from upstream: the client % matched); it reduces to hexX = floor((B + x) / 48 + 1/3), hexY = floor(((B + z) / 27.712812 + 1) / 2), B = world origin offset, which the bot driver inverts to visit each group (Game/Map/ZoneMapIndex.cs). Mission 2414 (MapZone 48) has no hex groups and stays unsupported. Bots now discover hexes too (the old check skipped players still flagged as loading, which bots always are).
- Zone map discovery across overlapping maps (guess): 9773 of 27812 hexes belong to several map zones (Auroria's group 412 lies inside Illium, MapZone 78); with only the current zone's map discovered, "Explore all of Auroria" could not be completed. A step now discovers the hex groups of every map zone joined to the player's world (MapZoneWorldJoin) that has a group within one hex. The 3 hex groups without any hex (Whitevale 286 and two more) are not counted (they could never be discovered). The arrival point of a teleport is explored like a step (players still loading are skipped).
- Zone on arrival (certain bug): GridEntity only set its zone on relocation, so an entity that changed world by teleport kept the old world's zone (or none) until it moved; bots that only teleport had no zone, which hid every path mission (GetAvailable needs a zone), zone map discovery and EnterZone. The zone of the arrival point is now set on add to map (Player.OnZoneUpdate runs: EnterZone objectives count the zone entered by teleport).
- Holdouts without listed enemies (guess): 66 of 86 holdouts have only their control point on Jabbithole and in the tables (PathSoldierEventWave rows hold no creatures). Their waves are the hostile creatures spawned within 120 m of the control point (most frequent first, up to 4 kinds), 3 units a wave as for the others.
- PowerMap (18, 25 missions): "TRACKING: follow the ..." (client UI: a ready state at the start, a leader unit, nCompleted/nNeeded drops, a waiting state with a despawn countdown when out of range; UI_PathExplorerContent_PathExplorerMissions.lua, certain). The trail: every one of the 25 missions has a Spline2 whose first node lies within 4 m of the mission's WorldLocation2Id00 or PathExplorerPowerMap.WorldLocation2IdVisual (certain match, likely meaning). The leader is virtual (the leading creature is not linked in the tables): it moves along the spline at 4 m/s (guess) while the player is within DistanceThreshold and waits otherwise; 30 s of waiting fails the map (guess). Start = the player within 15 m of the start location (guess: the client's start button message is not identified). CollectQuantity drops lie evenly along the trail and count when the leader passes them with the player in range (guess); maps with CollectQuantity 0 complete at the end of the trail.
- Missions count anywhere in the episode's world (PathEpisode.WorldId), not only inside the episode's zone: area nodes and targets of missions 1129 and 1320 are outside their episode zone (certain from the node locations).
- No mission packets are sent: ServerPathMissionActivate (0x06BA) and ServerPathMissionUpdate (0x06BB) layouts are confirmed from the client reader (count u32, then id 15 bits, completed 1, two u32, reason 3 bits, giver u32), but what the two u32 values hold (progress, checklist bits) is not read from the client yet.
- XP: no table gives mission XP. ASSUMPTION: path XP per mission = (XP needed for path level 30) / (missions of that path), plus 2 kill-XP units of account XP.
- Bot driver (Bots/BotPaths.cs, NF_BOTS_PATH_BOTS = n quest bots): plays missions of the bot's own path through client entry points: units through ClientActivateUnitCast (EntityHandler), scans and digs as spell casts, kills as fights, visits as moves. One mission at a time, given up after 4 minutes (path-bot-gave-up with the step). The old probe `pathmission` still feeds the hooks directly.

## Group finder (Game/Groups/GroupFinder.cs)
- What: a server-side queue per dungeon world that forms a group of one tank, one healer, three damage dealers of the same faction (entries expire after 2 minutes). Roles by class: Warrior, Engineer, Stalker can tank; Esper, Medic, Spellslinger can heal; everyone can deal damage.
- Why: the original WildStar group finder (Matching) uses the same 1/1/3 dungeon composition (https://wildstar.fandom.com/wiki/Dungeon lists 5 player groups; class roles are common WildStar knowledge); the exact class lists, level range rules and the 15 minute dungeon cooldown for deserters are not verified. This protocol enum has no matching messages, so no client can queue yet; bots call it (probe `groupfinder`, `group`).

## Taxi node faction
- What: TaxiNodeFactionEnum 1 = Exile nodes, 2 = Dominion nodes, 0 = both; travel from or to a node of the other faction is refused (`taxi-travel-refused WrongFaction`). The same 1/2/0 scheme is used by PathMissionFactionEnum.
- Source: node texts (node 5 Gallow, 7 Thayd = 1; node 18 Camp Virtue, 20 Primal Drill Site Alpha = 2), counts 107/108/46.

## Challenges
- ChallengeTypeEnum (certain, client enum ChallengeType read from its registration code): 0 Combat, 1 Ability, 2 General, 3 UNUSED01, 4 Item, 7 ChecklistActivate. Type 5 (75 rows) is not in the client enum; its Target is a VirtualItem (certain: 70 of 73 targets are VirtualItem ids and the names match, "Energized Stones" -> VirtualItem "Energized Stones", "Data Recovery" -> "Eldan Data Core"). The old reading of type 5 as "activate a target group" was wrong.
- Targets (certain by table match): Combat and ChecklistActivate = TargetGroup (284/284, 16/16), Ability = Spell4 (45/45, "Fire Bomb", "Augmented Shot", "Extinguishing Fire"), Item = Item2 (4/5, "Pure Loftite Fragment", "Meteorite").
- Played (likely): Combat = kills of group members; Ability = successful casts of the Spell4 (or another Spell4 of its base spell); Item = items of that Item2 coming into the bags; ChecklistActivate = each group unit used once; type 5 = kills or uses of the creatures its progress text names ("Collect energized stones by killing Slateface Gronyx"; scripts/research/challenge_sources.py matches Creature2 names and plural forms, data/research/challenge-sources.tsv, 53 of 75), one virtual item each (guess for the amount); General with a goal location (WorldLocation2IdIndicator != start) = a race that ends at the goal.
- Tier counts: medals by count. Tiers above 1000 are milliseconds left on the clock when the goal is reached (likely: race rows [0, 30000, 40000], [1, 11000, 23000]; larger = better like the count tiers), 0/1 = reached at all. Time limit 180 s (guess, no table holds it; tiers up to 120000 ms left fit). Timed ChecklistActivate rows (millisecond tiers: 276, 546, 957, 1105, 1106) are played: done when the needed units are used, medal by the time left; the number needed is the number in the progress text ("Disable the 4 Eldan Power Generators"), else the distinct creatures of the target group (guess). The clock of a timed row runs max(180 s, best tier + 30 s) so that tiers like 180000 and 265000 ms left are reachable (guess). General rows without a goal: 21 of 60 count-tier rows name their creatures in the progress text ("Smash $+ Gloomthorn Eggs", "Pull Up Wiggleroot Radishes"); scripts/research/challenge_sources.py matches them like type 5 (kills of hostile, uses of the others; likely), skipping dodge/survive, follow and adventure-medal rows and generic words ("still", "clear"). The other General rows (dodging, rings, vehicles, adventure medals, millisecond tiers without goal) need their scripts and stay challenge-type-unsupported.
- Bot driver: path bots (Bots/BotPaths.cs) pick a playable challenge of their zone (else anywhere), move to its start, activate it with ClientChallengeChoice 0x00C5 through the handler and do what it counts (`challenge-bot` medal / no medal in the ledger).

## Quest log limit (GameFormula 655 = 40)
- What: MeetsPrerequisites refused every non-contract quest once a character had more than 40 COMPLETED quests (upstream code compared `completedQuests.Count`). It now compares the quests in the log (`activeQuests.Count`). Contracts are not limited (their slot count, RewardProperty.ActiveContractSlots, is not implemented).
- Evidence: soak 20261004-0201 (3 h, 60 bots): 3308 completed quest rows at the first snapshot and 3323 at the last, i.e. 15 completions in 3 hours, bots at about 55 completed quests each. A player can finish far more than 40 quests in the real game, so the limit cannot be on completions.

## Challenge rewards
- What: RewardTrackRewards rows (446, now loaded with RewardTrack) of a challenge's RewardTrackId. RewardPointFlags is a bit per medal (1, 2, 4, 8, 16, 32 = counts 151/150/79/53/9/4); rows with the same flag are the alternatives of that medal, class specific rows carry a PrerequisiteId. The three RewardChoiceId columns are Item table ids (checked: 84758, 85149 and 72011 exist in `Item`) with RewardTrackRewardTypeEnum 0; type 1 entries (e.g. 2485) are not items and are skipped. A finished challenge gives every medal it reached one randomly picked alternative (bots choose at random), and the row's currency if any.
- Why: the point costs in RewardTrack (RewardPointCost00-09) suggest a points system for tracks used elsewhere (e.g. daily login); challenges are assumed to map medals to flags. Not proven; no client capture exists.

## Periodic quests (rotating dailies, Game/Quest/PeriodicQuests.cs)
- What: 231 quests carry a PeriodicQuestGroupId (64 groups, groups belong to PeriodicQuestSet rows, sets to PeriodicQuestSetCategory rows; tables now loaded). Each character gets a daily draw: per category SetsOffered sets (by Weight), per set GroupsOffered groups (by Weight), per group PeriodicQuestsOffered quests (by the quest's PeriodicQuestWeight); a count of 0 offers one, weight 0 counts as 1. The draw is seeded by character id and the daily reset day (10:00 UTC). Only offered quests can be accepted; MaxPeriodicQuestsAllowed of a group limits its quests in the log.
- Why: the field names say exactly this (offered / max allowed / weight); how the real server seeds, re-rolls or persists the offer is unknown. Quests without a group (about 950 daily quests) keep being always available after their reset.

## Character JSON state
- What: path missions and tradeskills keep per character state in /cache/<name>/<characterId>.json (CharacterJson<T>), saved on completion and every 30 s when dirty.
- Why: adding database tables needs EF migrations that cannot be generated or tested in this setup; JSON survives restarts and is enough for bots. Not safe for several world servers.

## Daily login (Game/Rewards/DailyLoginManager.cs)
- What: DailyLoginReward has 365 rows (LoginDay 1-365, RewardObjectValue, all type enum 0). One claim per daily reset day gives the item of the streak day; a claim the day after continues the streak, a longer gap restarts it. Item ids missing from the Item table (e.g. 184) are recorded in the ledger (`daily-login`), not given. State is JSON per character.
- Why: that is what a per-day table implies; the streak rules, wrap-around and tier enum (DailyLoginRewardTierEnum, set on every 10th day) are guesses and no claim message exists in the protocol (bots call it, probe `dailylogin`).

## Resource conversion (Game/Rewards/ResourceConversion.cs)
- What: ResourceConversion (90 rows) type 0 = SourceCount of item SourceId for TargetCount of item TargetId, type 1 = items for currency TargetId (ids 260, 817, 480). Surcharge (SurchargeId, SurchargeCount) is assumed to be a currency. ResourceConversionGroup (64 rows) lists rows per NPC; nothing in Creature2 links to it, so no NPC offers conversions yet.
- Why: id ranges (type 0 targets are item ids around 6000-13000, type 1 targets are small currency-like ids). Probe `conversion` runs every row without a surcharge.

## Prerequisite types 232, 194, 195, 226, 270 (assumed met)
What: seen only in spell-cast rows (mount/hoverboard summons: 232 NotEqual 4, 194 and 195 Equal 0; 226 Equal 0 object 9; 270 GreaterThan 8000-270000, a duration-like value). Treated as met and counted in `prerequisite-assumed-met`.
Why: no names in the tables, no source found online. They previously passed through the lenient spell scope unevaluated; now the assumption is explicit. Wrong if they gate mounting indoors/in combat or timers.

## Prerequisite types 102 and 22, effect ForcedAction (assumed; 22 decoded 2026-10-08)
What: 102 (creature 27993 activation row 19974 object 110233 value 47059; BOGO board spell row 40352) and 22 (quest row 10365, object = quest 6277, value 1) are treated as met, also for quests, and counted in `prerequisite-assumed-met`. ForcedAction (one spell, King Honeygrave Blinding Swarm) is a client side forced action, recorded as `effect-client-only`, nothing applied on the server.
Why: no names in the tables and no source online. Effect: at most 3 quests (type 22) and one creature activation (type 102) are not gated; the rest of the spell (damage, forced move) works.
- SetBusy on a creature delays its actions for DurationTime (inferred from the name, no source); NpcExecutionDelay likewise. Players have no busy state; recorded in effect-stored.
- AchievementAdvance (DataBits00 achievement id, DataBits01 amount): inferred from spell 55197; grants a plain counter achievement when the amount completes it, otherwise recorded. Source: spell data only.

## Quest spawn gaps and quests without giver/receiver (2026-10-05)
Scripts (scripts/research): questorigin.py, gapcreatures.py, gapsim.py, questloc_check.py, questloc_spawns.py, indicator_check.py, indicator_spawns.py,
jh_quests.py (archived Jabbithole quest pages, 583 gap quests had one, all fetched via Wayback, 1.5 s apart), jh_quest_cross.py, jh_quest_links.py.
- Receiver position = Quest2.WorldLocation2IdReceiver (likely). Check against community spawns: the location is within 2 m of a spawned receiver in
  1660 of 2023 quests, within 10 m in 91 %. questloc_spawns.py places receivers without any spawn there (one row per 12 m cluster; same-name versions:
  only the one receiving most quests; several different unspawned receivers: not placed, 7 quests). 101 rows (source `questloc`), 90 creatures.
- Objective indicators (QuestObjective.WorldLocationsIdIndicator) lie within 5 m of the target in 58 %, 20 m in 72 % (likely), but every remaining
  single-target objective with an indicator points at a Creature2 id that does not exist, so nothing new was placed (`objective-creature-missing`).
- Cut also when the turn-in location is in Halon Ring/Murkmire, or the quest category is 44 Murkmire / 47, 55 Halon Ring (many such quests carry zone
  1480, which is not in WorldZone). Certain for the location rule (the zone tree is the Halon Ring / Murkmire cut list), likely for the categories.
- `test-map`: turn-in on an unnamed world whose asset path contains test/sandbox (all 287 such worlds are unnamed; 825 DeathwoodRanchDanaTestMap,
  1163 CombatSandbox). `dev-content`: Quest Test Island (262), PublicEventIsland (1663), titles tagged [DNT]/[TEST]/[DEPRECATED]/[DEPR], or only
  NPCs described as [PH]/[PROTOTYPE]/Prototype/Cheat/Quest Test. Likely. None has an archived Jabbithole page.
- `unreleased-zone`: Dreadmoor (zone 2197, category 41) and Coralus (2186/4679, category 43). Dreadmoor is described as an unreleased zone
  (https://www.youtube.com/watch?v=Y_wJsCTdZdU and wiki summaries); the archived Jabbithole Coralus zone page (coralus-89, 2014) lists no NPCs, quests,
  challenges or path missions (Thayd: 241 NPCs), the same as the cut Halon Ring pages, and no NPC page among 24,000 is found in either zone. Likely.
  Reported separately from the Halon Ring / Murkmire cut list.
- `untitled-internal`: 965 quests have no title text. None of the 677 that lacked a giver has an archived page; the 38 untitled quests that have one show
  level 0, 0 XP and an object or nothing as starter. Hidden helper quests driven by scripts/objects, never offered by an NPC (likely).
- Giver/receiver links from the archive (data/research/quest-links.tsv, loaded by GlobalQuestManager): Jabbithole "Started by"/"Completed by" NPC links
  that Creature2.QuestIdGiven/Receive lack, mostly the tradeskill work orders (Work Order Board in Thayd/Illium and zone boards). NPC name resolved to
  Creature2 by name, then the Jabbithole zone in the description, then the quest faction (Exile/Dominion) in the description, then a spawn in the
  zone's world; anything still ambiguous is left out (research/decode/quests/quest-links-report.tsv). 248 links, likely.
- Item imbuement quests (Game/Quest/ItemImbuements.cs): Item2.ItemImbuementId -> ItemImbuement (up to 15 chained quests + ItemImbuementReward per step).
  Archive: "Started by" the items, "Completed by" nothing, category "Item Imbuements"; Quest2.Flags 0x40000 on 563 of 597 chain entries. Equipping such
  an item offers the first unfinished quest of the chain (prerequisites and log limit apply); the quest completes without a receiver. Likely.
  ItemImbuementReward types 0 and 1 are decoded and applied (see "Imbuement rewards"); 4/5/6 stay `imbuement-reward-not-applied`.
- Contracts (Quest2.Flags 0x80000, 55 quests, type 13): taken and handed in at the Contract Board (EntityInteraction event 85 ContractBoardOpen), which
  the server does not implement; questcheck reports `contract` and skips their giver/receiver checks.
  Implemented: contract quests are accepted and handed in without a giver or receiver creature (ledger `contract-accepted`), the Contract Board is a client
  UI over ClientQuestAccept/ClientQuestComplete, no extra message is known (ServerQuestContractGoodQualityChanged 0x0252 is the only contract message in the registry,
  its layout and meaning are unknown, not sent). Slot limit (RewardProperty.ActiveContractSlots) and the board's daily offer rotation are not modelled: assumption, best-supported default is that all 55 are always offered.
- Seasonal NPCs placed by questloc (Shade's Eve, Winterfest quest givers in Thayd/Illium) stand there all year; no event calendar exists.
- gt questcheck (5194 quests) before -> after: giver-not-spawned 434 -> 52, receiver-not-spawned 301 -> 20, objective-not-spawned 384 -> 41,
  no-giver 1530 -> 141, no-receiver 1100 -> 99, cut 178 -> 375; new classes: unreleased-zone 185, dev-content 220, test-map 35,
  untitled-internal 707, contract 55, objective-creature-missing 59 (objective points at a Creature2 id that does not exist: table gap).
  Imported research spawn rows by source (gt importspawns): page 19215, instance 9688, indicator 6680, questloc 101.

## Leftover decodes (2026-10-05; scripts/research/spellinfo.py, proxyscan.py, cdgroups.py)
Ranked by the last soak's effect-stored counts (soak/20261004-1538, snap-019): VitalModifier vital 6 on creatures 142k, ModifySpellCooldown
on creatures 83k, UnitPropertyModifier (stored, server-side) 13.9k x4, SetBusy 6k, RavelSignal 3.2k, cooldown group 182 2.8k, proc modes 1.4k.
- ModifySpellCooldown modes 2/3 (certain): mode 2 DataBits01 = a spell group id; a spell is in it when the id is one of the 32
  SpellGroupId fields of its Spell4GroupList row. Mode 3 DataBits01 = a SpellCoolDown id (shared cooldown) carried in Spell4.SpellCoolDownId00-02.
  Evidence (cdgroups.py over all 73-effects): "False Retreat - T4 Cooldown" -> group 507 = False Retreat only, "Flame Burst - Cooldown" 473 =
  Flame Burst only, "Feedback - T4 Spell modify" 582 = Feedback only, "Void Pact - CDR" 559 = Quick Draw/Charged Shot/Rapid Fire, Affinity
  ("reduce their active cooldowns", tooltip) 8 = 668 abilities; mode 3: Stealth cancel 182 = the three Nano Skin stances, "Stance Master" 135 =
  Stance Juggernaut/Onslaught, "Rapid Fire - Tap" 212 = Rapid Fire. The earlier reading (Spell4GroupListId == id) matched only TEST spells.
  Code: Game/Spell/SpellGroups.cs. Multiplier forms (DataBits02 0/1) stay stored.
- Proxy caster (likely, class spells only): a player class spell (Spell4Base.ClassIdPlayer != 0) proxied onto another unit is cast by the
  original caster with that unit as primary target. Evidence: in non-deprecated class spells, proxies aimed at targets (flags 2/4/6) carry on
  target flag 1 only caster-side effects (VitalModifier resource gain 296: Warrior "Add Cell - Relentless Strikes - Target" +180 kinetic energy
  per enemy hit; ModifySpellCooldown 69; Proc 12; force move back "False Retreat - Return Proxy" 48; self buffs 24; shield restore 14) and
  on flag 2 damage 546, CC 156, heals 124. Before, the target cast them: 142k "VitalModifier vital 6 not applied on creature" and 83k creature
  cooldown edits per soak, and a creature's own target got the CC. Creature/hazard proxies keep the upstream reading (target casts), because
  some of them put debuffs on flag 1 (Food Poison, Necropotion Goo knockdown); no field separates the two cases (DataBits04/05, flags,
  Spell4Base targeting all checked). Ledger: proxy-caster.
- Spell parameter types (likely): names from upstream NexusForever game_rework SpellEffectParameterType (Brutality, Finesse, Tech, Moxie,
  Insight, Weapon, TargetMaxHealth, CasterMaxHealth, WeaponDPS, PerLevel, Grit, AssaultPower, SupportPower, Caster/TargetShieldCapacity,
  Caster/TargetMaxShieldCapacity, ItemBudget, TargetCurrentHealth, TargetShieldCapacity2, TargetMissingHealth, TargetMissingShields,
  CasterCurrentHealth, CasterShieldCapacity2, CasterMissingHealth, CasterMissingShields). It agrees with all 9 types decoded here before
  (1-5 = UnitProperty2 0-4 localized Brutality/Finesse/Tech/Moxie/Insight, 7, 8, 10, 12, 13, 6/9 weapon) and with tooltips: Medic Energize
  (22, 0.3) "restore 30% of your missing shield", Curse of Shared Fate (19) "of your current health". Applied: 8 = caster max health (was the
  target's; same unit in every tooltip sample), 11 = Grit = UnitProperty2 40 "Stamina" (localized "Grit"; CreatureLevel 5 at L1, 500 at L50;
  771 unknown-param hits per soak, creature attacks such as Spider Trap Webbing (11, 1.969) + (2, 1.969)), 14-17, 19, 21-23, 25, 26.
  HealShields with type 22 now restores the missing shield fraction (Energize/Barrier) instead of 30% of max.
  Unknown: 18 ItemBudget (no per-effect budget value on the server: potions, gadgets, runes), 20/24 (duplicate names), 6/9 weapon damage.
- Weapon damage (6/9), decision: no value exists in build 16042 (Item2 has no damage field, Item2Category weaponPowerBase 0 everywhere,
  2016-19 tooltips list only ratings). The rows using them are beta-era/[DEPRECATED]/[TEST] class spells and four auto-attacks; 0 casts in
  the last soak. A spell with only weapon terms keeps the old DataBits00 form; mixed rows use the decoded terms. Not guessed.
- Proc mode bits (likely; procs.py rows + Spellslinger Affinity tooltip "When one affected ally receives a direct heal, restore health to the
  OTHER ally, and reduce their active cooldowns"): bits 1/2/4 choose the primary target: 1 = the unit that applied the proc, 2 = the holder,
  4 = the other party of the event. Affinity "Caster to Target" puts a mode 9 proc on the ally whose proc heal must reach the spellslinger
  (applier), "Target to Caster" puts mode 17 on the spellslinger whose heal must reach the ally (applier); the removal procs (10/18) act on the
  holder. Bit 8 = the applier casts: Stalker Analyze Weakness CDR (mode 9, cooldown cut on flag 1 when the debuffed enemy dies), Nano Virus
  regen (mode 9, vital gain on flag 1 when the debuffed enemy is hit), Frost Snap CDR, Power Link counter, Medic Emergency/Protective Surge
  heals (mode 10, medic heals the holder), Sentinel (mode 12, warrior hits the attacker). Bit 32 = the chosen target casts: Open Wounds
  (mode 36, the victim casts Bleeding with damage on flag 1), Plague of Uncontrollable Stench (mode 36, the aura re-registers itself on the
  other party = spreads). guess: bit 16 = holder casts (same as no bit; mode 18/20 rows: Death Rattle, Incendiary Ammo, Acidic Hide, removal
  procs all work that way).
- Proc trigger 41 = being crowd controlled (certain, 12 tooltips "Whenever you are CC'd ..."): now fired from CCStateSet. Still without a
  server event: 17/18/19 (melee/ranged/magic hit), 51 deflect, 53 block, 124 dash, 251 multi-hit, 205 interrupted (likely by names).
- Rating to percent (crit, multi-hit, deflect, strikethrough, vigor, tenacity): no source. Searched: wildstar.fandom.com Stat page (API),
  archive wiki Attributes (cached crawl: "calculated from your base Critical Hit and your Critical Hit Rating", no numbers), greenyneko
  (2017-18 posts give only the crit expectation form), pastebin YgZJJVA5 (definitions only), patch notes 08/15/2016 (glance conversion changed,
  no numbers), GameFormula (1217 unnamed rows), client UI Lua (the client calls unitPlayer:GetCritChance(), computed in the engine).
  Ratings stay stored on the player, not converted. SUPERSEDED by "Rating curves": the client binary has the curve.
- Prerequisite type 240 = world type (certain): object is World.type of the current map (3 PvP, 6 adventure, 7/8 warplot, 9 raid,
  11 dungeon, 2/5 housing; the server's MapType enum agrees for 2/5/6/9/11), comparison Equal = in such a world, NotEqual = not.
  All 15 rows that spells use fit: Settler's Campfire not in 6/9/11, Mailbox and Mini Vendbot not in 3/7/8, Air Brakes not in 9/11,
  Summon Group not in 2/5, Endosmose Grenade split by PvP (3/8) and PvE instance (6/9/11). It was unevaluated 7971 times in one gate and made
  the Settler Campfire quest cast fail 2657 times (row 17449 nests row 30463).
- Prerequisite type 129 (likely; was "Settler improvement, assumed met", 118k checks per soak): object = a spell group id (the same groups
  ModifySpellCooldown mode 2 names, membership through Spell4GroupList), value = an example spell of the group or 0. Read as "has an active
  effect from a spell of the group". Fits: Sprint/Stealth NotEqual 313 (group = the Moodie Mask carrier auras), mounts NotEqual 343 (mask
  carriers + knockback), Fireball/Death Spike NotEqual 424 ("Generic Spell Lockout"), Tremor NotEqual 426 (Tremor's own auras), Purify
  effects Equal 600 with value 39063 (group = the Affinity procs, 39063 = Affinity), Whirlwind/Plasma Whip Equal 385-387 (milestone buffs),
  Settler's Campfire NotEqual 483 ("Remove All Path Buffs"). NotEqual is enforced from auras, property modifiers and procs the server
  tracks; Equal passes when unproven (the server does not track every aura) and is counted in prerequisite-assumed-met outside spell casts.
- Still assumed met (no source, no separating data): 232 (mount rows NotEqual value 4), 194/195 (mount rows Equal 0, always together),
  270 (GreaterThan 800-300000, item/reward rows), 226 (object 5/9/10/22/46), 102, 117, 61.

## Boss encounter and loot sources (2026-10-05; docs/ENCOUNTER-SOURCES.md)
- Timers, raids (confidence likely where marked): scripts/research/bossmod_timers.py compares literal timer lengths in three boss mods: RaidCore
  (github.com/Immemor/RaidCore), DruseraBossMods (github.com/Immemor/DruseraBossMods, separate code, timers measured from cast start/end) and
  LUI-BossMods (github.com/laserloui/LUI-BossMods; copies RaidCore numbers in most modules, so it only counts where its value differs).
  Agreement = within max(5 s, 15 %). Agreeing repeat timers: Kuralak Vanish into Darkness 47/50 s, Outbreak 45/40 s, DNA Siphon 88/90 s;
  Gloomclaw Rupture 39-43/43 s. Encounter "confidence": likely when half or more of its abilities agree (Kuralak x2, Gloomclaw), else guess.
  Disagreeing or single-source numbers are listed per ability in "timerSources" and research/bossmod-timers.tsv and are not counted as sourced
  (e.g. Ohmna Genetic Torrent: Drusera 84 s, RaidCore has a "big spew" bar of 60/45/40 s that may or may not be the same cast).
- Timers, dungeons and world bosses (guess): no dungeon boss mod source is reachable (CurseForge "Boss Mods" addon, which covered veteran
  dungeons, is not on GitHub and the Wayback Machine was offline 2026-10-05), the archive wiki dungeon pages give no ability intervals,
  and every boss spell in Spell4 has SpellCoolDown 0 (the server AI drove the timing). Timers stay assumptions.
- What the tables do settle (certain): the Spell4 designer descriptions "[TAG] e### - (N|V|N/V) Boss - Spell - Base|Proxy - Tier n" group every
  boss spell by encounter id (e314 Stormtalon, e412 Kuralak = "Genetic Architect", e410 Experiment X-89 = "Strain Mauler", e446 Drokk, e486 Rayna)
  and mark normal/veteran-only spells and proxies. Generated encounters drop proxies, messaging, visual/intro/flavour, auto-attack, test and
  deprecated rows and spells of the other difficulty (DesignerTag.cs); named abilities resolve to a base spell of the current difficulty.
  A repeat never comes sooner than the spell's CastTime/ChannelMaxTime plus 2 s.
- Ability names: scripts/research/encounter_audit.py marks a guide/boss-mod name "certain" when a Spell4 row with that name carries the boss's
  designer name or encounter id, or when Jabbithole saw the boss cast it. Fixed by that check: Rayna "Lava Pools" is the spell Smoldering
  ("Smoldering Pool - Base", likely), Kuralak "Siphon" is "DNA Siphon" (certain, e412 and both boss mods).
- RaidCore draft files (data/encounters/raidcore): phase thresholds come from "nPourcent ==" checks, which are mostly "phase soon" warnings
  (Kuralak warns at 74 %), so thresholds there are likely a few percent high (guess). Abilities without a matched timer keep a 25 s default.
- Boss loot: the Jabbithole parser missed the 2014 page layout (tab "tab-drop", item urls with a hash suffix), which is where most dungeon and
  raid boss pages are. Fixed: 224k drop rows (was 153k), e.g. Stormtalon 244 items from 629 kills, Kuralak 60, Rayna 26, Bosun Octog 10
  (16 kills). Rows for item ids missing from Item2 are dropped at parse time (632). Chances are the observed per-kill frequency; one roll per kill
  for the group in instances (GroupLoot). Small samples (under ~30 kills: Bosun Octog, Experiment X-89, Rayna) are noisy (likely). Bosses
  without a page (Dreadphage Ohmna, Avatus) keep the generic fallback. Counters: loot-boss-sourced / loot-boss-generic in the ledger.
- Event loot and wave orders: no source found. Jabbithole event pages list only NPCs and sub-events, no Jabbithole item page names a public
  event as its source, the wiki has no wave orders. Events keep XP rewards and the PublicEvent/PublicEventObjective table order (unchanged).

## Prerequisite types 69 and 36 (assumed met; 36 decoded 2026-10-08)
What: type 69 (object 515, value 4, row 13017, paired with 160) gates the activation of creature 33548 (Eldan Security Mine); type 36 (object 9110, value 4, row 29877) gates Grimvault Exanite Chef creatures 53682 and 60197.
Why: no names in the tables and no source online. Before: counted as unhandled and strict, so 1542 activations per 3 h soak were refused. Now: met and counted in prerequisite-assumed-met, like 117 and 61.

## Quest coverage blockers and decodes (2026-10-06; scripts/research/questwhy.py, `gt questdump`)
- Picker regression (certain): commit 6215e70 put EnterArea/EnterZone/CollectItem/CompleteEvent/SpellSuccess* in QuestCoverage.BotReady, which made them non-synthetic, but BotQuests.ResolveTargets had no case for them (911 quests with objectives were never indexed, so never picked) and CreditSynthetic only ran their drivers for synthetic objectives. Now: the drivers (TryVisitArea/TryVisitZone/TrySpellObjective/event visit) run whenever such an objective is open; an objective is synthetic only when the tables give the driver nowhere to go (no visitable indicator or spawned group creature, no zone location, no spawned cast group or indicator, event not runnable, no item source).
- EnterZone (certain): 119 of the 199 EnterZone objectives name a parent zone (WorldZone.parentZoneId). Entering a sub zone credits the zone and its parents; a player already inside when taking the quest is credited by the bot driver the same way.
- CollectItem sources (likely): the objective's reward pane group (6 of 327 objectives) and creatures whose researched drop table (data/research/creature-drops.tsv, rolled by CreatureLoot) has the item (16 of 274 items). The rest has no known source and stays synthetic (counted).
- Quests without objectives (likely): taking the quest achieves it (Quest sets Achieved when every objective is complete; with none that is vacuously true), it is handed in at the receiver. Bots take the titled ones (78 such quests open the chains of 211 quests); untitled ones are hidden flag quests granted by scripts and are left alone.
- Prerequisite type 118 (certain): object = PublicEventObjective id (1337 of 1357 objects), value = objective status in the client's PublicEventStatus Lua enum, read from the client's enum registration (Inactive 0, Active 1, Succeeded 2, Failed 3). Evaluated against the event run on the player's map; no run = Inactive.
- Prerequisite type 19 (certain for the object, likely for the value): object = TradeskillTier id (object 5 = Weaponsmith tier 5, failure text "You must be an Expert Weaponsmith"; object 8 = Mining tier 1, "Not enough Mining skill"). Compared value is 1 when the tradeskill is learned and the tier reached, else 0 (240 of 268 rows compare with 0). Bots learn a tier-1 tradeskill when a quest is refused only for lacking it (ledger bot-tradeskill-learned-for-quest), crafting skills stay limited to two.
- Prerequisite 26663 (certain): type 15 aura 63524 "Carrying Explosives", given by activating creature 57003 "Air-Dropped Explosives" (its activate spell), gates the Grimvault bomb placement sites 56970/57014/57015 (public event PE528). Already evaluated by type 15; no table change needed.
- Quest 9800 "Hold the Beach!" (certain): group 12468 creatures (68487...68866) are referenced by no table except TargetGroup 12468, and the quest is in zone 4680 (Dreadmoor/Coralus, never released). Cut content, no source.
- Prerequisite type 1 with a non-zero object (58 rows, e.g. row 4106 object 4065 value 1): treated as Level, the object is ignored (likely; the objects repeat a sibling clause). Row 4106 is Level LessThan 1, see the comparison decode below.

## Telegraph shapes 1, 3, 6 (from TelegraphDamage rows and the spells using them)
- 1 Ring (certain): param00 inner radius, param01 outer radius (00 <= 01 in all 469 rows; bezgelor reads them the same way). param02 (77 rows) is ignored like on circles (guess).
- 3 Cross (likely): spells "Thunder Cross", "X Marks the Spot", "Tentacular Retaliation" (45 degree rotated copies make an X). Params: 00 full width of an arm (guess), 01 height, 02 tip to tip length (likely: the copies of "Tentacular Retaliation" 9/8/37.5 sit 18.8 = 37.5/2 apart so the tips touch); 03 (17 rows) taken as the length of the second arm (guess).
- 6 Ellipsoid (likely): spell "Test Ellipsoid Telegraphs" (telegraph 5487, 65/6/65). Params 00/01/02 = X (across), Y (vertical) and Z (along) radii: "Body Slam" 5/30/5, "Blaze" 75/2.5/75.
- Grid search size for Square/Rectangle used param01 (the height); now the corner distance of width 00 and length 02. Pie used 0 (no search); now param01.
- `gt telegraph <id> [scale]` draws the hit area with a bot as caster.

## Out-of-combat health regeneration
- Not in the data: UnitProperty2 has only HealthRegenMultiplier (8, default 1.0) and focus recovery rates (107/108, 0.011); no base health regen rate exists in UnitProperty2, the spells ("Health Regen Flatten" 25712 sets the multiplier to ~0) or the scraped wiki pages. The bot rest heal in Bot.cs (d1278a3, d412299) stays until a rate is found.
- ActivateEntity/ActivateEntity2/SucceedCSI objectives with data 0 and no creature in the text (likely): the target is the one activatable creature kind (Creature2.spell4IdActivate00 set) spawned within 30 m of the objective's indicator location 00. Evidence: of the 142 activate objectives that name a creature spawned in the indicator's world, 118 have it within 30 m of the indicator. Of the 646 data-0 objectives with an indicator, 145 have exactly one such kind (used, ledger objective-indicator-target), 224 have two or more (ambiguous, stay synthetic, ledger objective-indicator-ambiguous), 277 have none.
- Prerequisite 36224 (certain): type 118, PublicEventObjective 4510 status Active (1), gates activation of creature 68244 (PE699 Grimvault Watcher Ward portal). Evaluated by the type 118 check.
- Prerequisites 4106, 6232, 14643 (certain after the comparison decode below): Level LessThan 1, which no character meets. They gate 52 quests whose givers still exist: Everstar Grove, The Ravenous Grove, Base Drop, Ability Training 101, the old path faction intros (Explorers Union, Exile Academy of Science ...), Graylight L50 quests. Read as retired content (the old start zones were replaced in the 2015 free-to-play update); they are not doable and leave the coverage count (4383 -> 4333). Earlier this line read them as "level <= 1, granted to new characters" from the wrong enum.
- Picker (certain from the quest dump): 182 of 190 never-accepted root quests were follow-ups whose prerequisite quest some bot had completed; only that bot can take them, and a random pick over everything open rarely chose them. Bots now take their own chain follow-ups first (listed prerequisite quests or a type 6 Equal prerequisite), also below their level window.
- Crafting tiers 2-5 (QA harness): 162 quests need a TradeskillTier above 1 (prerequisite type 19 GreaterThan 0). Bots with the skill get the tier's RequiredXp (ledger tradeskill-qa-tier-grant), like the schematic material grant; a player earns it by crafting. Superseded on 2026-10-08: bots now craft up to the tier at a station for real xp (BotTradeskills); only materials are granted.
- Prerequisite type 43 (certain for the shape, likely for Equal = in progress): object = quest, value = 0-based objective index. Of the 63 creature activations gated by type 43, 30 name exactly the creature of that objective (10056 = quest 6439 index 5 SucceedCSI creature 28979; 10760 = quest 6612 index 4 creature 30260), most others its target group or event. Equal = quest held and that objective not complete (it must pass before the activation credits the objective; "complete" is type 68). 289 rows whose index is past the quest's objectives keep the quest state reading (ledger prerequisite-inferred).
- Creature activate slots (likely): Creature2 has four Spell4IdActivate/PrerequisiteIdActivateSpell slots; 156 creatures use several, gated variants first and usually an ungated one last. The first slot whose prerequisite is met is cast (before: slot 00 only, so creature 33548 "Eldan Security Mine" needed challenge 515 for the plain quest disarm in slot 01).
- Prerequisite type 160 (likely): object = Challenge id, value compared with the running challenge's count (0 when not running). Row 13017 failure text "Ch515 isActive and >4"; challenge 1078 "Diagnostic Tests" gates its step creatures on values 0..7. Type 69 is likely "challenge active" (texts "Ch515 isActive", "CH276 - Active") but stays met and counted.
- Prerequisite types 13, 246, 54 (certain from the client error texts in PrerequisiteType.tbl, values joined to tables): 13 "Item equipment requirement not met" (value Item2 id, item equipped), 246 "Inventory requirement not met" (value Item2 id in 791 of 793 rows, item in the bags), 54 "You do not meet the player path mission requirement" (value PathMission id; row 13120 gates the anchors and charges of mission 679 "DEMOLITION: Anchors Away", so Equal = mission open for the player and not done; done is type 67).
- Prerequisite type 64 (certain): object = class resource index (Stat Resource0-6), value compared with its current amount. Row 17120 "Not enough Psi Points" gates Esper "Telekinetic Storm" on object 1 (Psi Points are Resource1); rows 20111/29484 "Insufficient/Requires more Spell Power" use object 4 with values 0-100 (Spell Power is Resource4).
- Bot travel (harness): a quest without progress for 2 minutes whose targets are not around gets the trip (this map first, else any world) even when the other held quest keeps the bot busy. Evidence: 79 of 103 sampled stall events in a 60-bot run happened in a world where none of the quest's open targets spawn.
- Chain restart (harness, certain from gt pickwhy): with 30 bots online, 1065 of 1391 open doable quests were refused by every bot of the right faction in the level window because none of them had done the prerequisite quest (only bots now offline had). A completed quest that leads through its follow-ups to a doable quest nobody completed now counts as open for picking, so any bot restarts the chain. `gt pickwhy` writes the per-quest verdict to /cache/pick-why.tsv.

## Quest stalls and pick spread (2026-10-07; live runs with `bot quest <id> any` and trace lines in scripts/bot-feed.sh)
- Sequential objectives (certain for the bot side, likely for optional): flag 0x2 makes an objective wait for the objectives before it (Quest.CanUpdateObjective). Bots now only target objectives the server would update; before, the nearest creature was often one of a later objective, the credit was refused and the bot repeated it for the 6 minute stall timeout (quest 6246: six chained TalkTo/SucceedCSI). Optional objectives (0x20) no longer hold a later required one back: quests 3901 (required kill 13714 behind optional SucceedCSI 13717) and 4944 (TalkTo 18651 behind optional type 10 objective 18876) could never finish otherwise; an optional objective that blocks would be mandatory. Likely.
- CSI activation spells (likely): activation spells with Spell4Base.CastMethod 5 (ClientSideInteraction, e.g. 41000 "Drill for Water - HCon", only a SetBusy effect) have no Activate effect, so activating their creature never credited anything. The client plays the CSI minigame and reports success; there is no client here, so a finished cast of a creature's activation spell (EntityHandler.ActivateCast, SpellParameters.IsActivation) credits the activation when the spell has no Activate effect. Ledger activation-credit-cast-finish counts them by cast method (first run: method 5 spell 41000, method 0 spell 85452).
- Researched spawn heights (certain): 24k researched spawn rows (ids from 9000000) have Y 0; BaseMap.SpawnGrid puts their creatures on the terrain, but bots teleported to the raw point stayed at Y 0 and the 3D 60 m search never found the creatures (forced quest 3623, terrain near -880 in world 22). Bots at exactly Y 0 now take the same terrain height (ledger bot-ground-snap).
- Activation reach (harness): horizontal 2.5 m and 12 m vertical, and a second try at the same creature within 15 m activates from there (creature 13717 of quest 3901 stayed 8 m away after every teleport). The server does not check activation range (EntityHandler TODO).
- Activation prerequisites (harness, same rule as spell casts): when no activate slot passes strictly, the slots are checked again in the lenient scope (undecoded types pass, decoded ones are still checked); ledger activation-prerequisite-lenient. Creature 70846 (Holo-Scroll, quests 10029/10421) is gated by type 23 twice (OR), which is not decoded.
- Prerequisite type 94 (likely): client text "Quest objective requirements not met" (shared with 22 and 23); object quest id, value objective index 0-4, only Equal/NotEqual (871 uses). Row 40514 gates the Starfall firework holders of quest 10496 on "objective 0 (activate 5 holders) NotEqual"; row 6019 = quest 4944 accepted and objective 4 not done. Read like 68: the objective is complete (a completed quest counts as complete).
- Prerequisite types 22 and 23 (unknown): same client text; value goes up to 20 and 23 is compared with LessThanOrEqual (row 855), so it is a count, not an objective index. Not evaluated; they stay in the ledger (strict for quests, lenient in casts and activations).
- Abandoned mentioned quests (certain, code bug): QuestAbandon queued a mentioned quest for delete and moved it to the inactive list as Mentioned while it stayed in activeQuests, so it kept a log slot and taking it again threw "An item with the same key has already been added" (bot quest 10518, 6735, 6992, 9428). Mentioned quests now leave the active list; GetActiveQuests skips quests waiting for their delete.
- Map-less world spawns (likely): Jabbithole zone names were matched to world 3091 (ShiphandExileArkship, no map) for Mondo's Laboratory, which is in 1634 (DominionArkShipTutorial): quest 6993/6994 objective 52246 has indicator 41283 in 1634 at the same coordinates as creatures 33220/52245/54243. scripts/research/remap_mapless.py moves a spawn of an open map-less world to the mapped world whose WorldLocation2 rows are within 60 m (3 or more, 80 % one world) when at least 30 % of the world's spawns point there: 20 spawns 3091 -> 1634, 8 spawns 825 -> 22. Dungeon/adventure/raid worlds are never moved.
- PickLevel (certain from the table): the pick level is max(ConLevel, PrerequisiteLevel); quests 3001/3011 have ConLevel 1 and PrerequisiteLevel 34, so only level 1-9 bots looked at them and QuestManager refused every one ("level"). PrerequisiteLevel above the level cap 50 (quest 3303: 99, 3503: 90) can never be met: those quests are not doable.
- Level rotation (harness): SuggestLevel counts only open quests the bot's faction, race and class may take (and whose prerequisite row passes, rows with a level check are placed by PickLevel), and divides by the bots of the same faction already within 3 levels, so bots spread over the open level ranges of their own faction (gt pickwhy before: 26 faction-dominion and 36 level verdicts).
- Heuristic credit (counted apart): quest-coverage in the ledger and docs/QUEST-COVERAGE.md now splits completed doable quests into "without QA heuristics" and "rely on one": indicator (activate objective whose creature is the one activatable kind near its indicator), unknown10 (type 10/15 objective credited through ScriptedTargets), tier-grant (crafting tier above 1 granted as xp).

## Prerequisite comparison ids (2026-10-07, certain)
PrerequisiteComparison 3-6 were mapped 3 GreaterThanOrEqual, 4 GreaterThan, 5 LessThanOrEqual, 6 LessThan (seeded from NexusForeverContainer). The failure texts in Prerequisite.tbl say otherwise:
- 4 = GreaterThanOrEqual: "You must be at least level 17 to enter Stormtalon's Lair" (t1 c4 v17), same for Skullcano 32, Kel Voreth 22, Swordmaiden 37; "Requires an Arena Rating of 1400 or higher" (t251-253 c4 v1400) and 40 more rating rows; 107 rows "level c4 50" would be unreachable as GreaterThan 50.
- 5 = LessThan: "Maximum AMP power reached" (t237 c5 v10), "Maximum Ability Tier points reached" (t235 c5 v7), "Maximum number of Bots already summoned" (t59 c5 v2, the Engineer bot limit is 2); "Item Special - Enrage" health c5 34; "Bolster Shields" shield c5 100.
- 3 = GreaterThan: "You're out of Vitalus Treatments / Adrenaline Injections" (t51 c3 v0 = count > 0); "Shield Burst"/"Healing Hands" shield c3 0.
- 6 = LessThanOrEqual: quest level windows pair c4 and c6 (quest 7980 con level 15: c4 14 & c6 15; 8066 con level 11: c4 9 & c6 11), empty or excluding the con level with the old enum. Heat gauge bands c4 75 & c5 90, c4 50 & c5 75, c5 50.
- One text disagrees: row 13017 "Ch515 isActive and >4" uses c4 (designer shorthand).
Effects: every prerequisite comparing with 3-6 changes meaning (level windows, vital percent, class resources, reputation, counts). Type 19 (tradeskill tier) now compares 0 = tier reached, -1 = not (likely): 175 rows c4 0 "Not enough Mining skill" and 65 rows c1 0 "You are not an Armorer of at least Apprentice level" both ask for the tier. Type 23 row 855 is LessThan, not LessThanOrEqual.
- Objective creature macros (certain): texts name creatures as $(creature=N), $m(creature=N) (plural), $p(creature=N) and $i(creature=N); only the first form was read. 114 data-0 activate/CSI objectives name their creature only in a prefixed form (e.g. 7224 "Use the Specimen Net to capture $i(creature=19949)").
- Reward pane target group (likely): QuestObjective.TargetGroupIdRewardPane (type 1 groups) lists the objective's creatures: it contains the data creature in 242 of 278 activate/CSI objectives that have one, and the text-named creature in 184 of 252 data-0 ones. Data-0 activate objectives without a named creature now use it (ledger objective-reward-pane-target, counted as heuristic "reward-pane"); the indicator heuristic stays the fallback when none of these creatures spawns. Effect in a 30-bot gate: objective-synthetic 300 -> 16 per run; quests whose resolved creatures have no spawn are no longer doable (4333 -> 4274).
- Quests whose required objective only has targets in dungeon/raid/expedition/adventure worlds are not picked by quest bots and not counted as doable (8 quests, shown apart in the coverage line); before, one open-world target anywhere in the quest was enough (9428: TalkTo in the open world, CSI 58487 only in the Datascape 1333).
- Quests with Quest2 flag 0x4000 (cannot abandon) that stall are parked by the bot (ledger bot-quest-parked) instead of the swallowed abandon exception: 20 bot characters held 9428 forever and every stall timeout sent them travelling for it.
- Bot faction spread (harness): ambient bot accounts are queued alternating by their character's faction (DB holds 40 Exile and 40 Dominion bot characters, the first 30 online were 20/10); open doable quests were 437 Exile-only, 478 Dominion-only, 188 both. Now 15/15 at 30 bots.
- Event trips (harness, likely): a quest refused on prerequisite type 118 Equal 1 (public event objective active, 16 quests e.g. 6919 "Before They Escape!") sends the bot once to the event's area, which starts the event (PublicEventEngine.PlayerTick), like a player walking in; ledger bot-quest-event-trip.
- Entity types 13 (AiTurret, 244 world database rows) and 2 (Destructible, 13 rows) had no entity class and spawned as Simple (not a unit), so kill objectives on them could never be met (quest 10518 "Interrupt the $(creature=73494), then destroy it": both 73494 spawns are type 13). They spawn as creature units now (classes AiTurret/Destructible derived from NonPlayer) and are sent with AiTurretEntityModel/DestructibleEntityModel (certain for the server side; the client view of these models is untested, no client).
- 9 quests push an item that is not in Item2 (3144: 6036, 3224: 6035, 3225, 3226, 3555, 3579, 3580, 3707, 3708): QuestAdd threw "Value cannot be null" and the quest could not be taken. The missing item is skipped (ledger quest-pushed-item-missing); forced 3144 and 3224 complete.
- CompleteQuest objectives (type 24): a bot holding the quest takes the named quests (ties); quest 6603 "Lightreach Mission" completes with 6607/6608/6609.
- Quest flag achievements (likely): type 12 achievements titled "Q<quest id> Complete" (4719 "Q6715 Complete", 4727 "Q7008 Complete", value 1, no checklist) are granted when that quest is completed; prerequisite type 7 rows gate follow-ups on them (7009 needs 4727, 14 chain quests behind it). Ledger quest-flag-achievement. Achievement 5598 (type 12, no title, gates 9707/9723) has nothing that says what sets it; unknown, stays.

## Contract Board messages, loot window (first pass): superseded by "Contract Board, periodic draw" below (layouts recovered from the client binary)
- What: no layout for a Contract Board open/offer message and no ServerLootNotify (0x07F2) layout is documented in any verified source (ServerQuestContractGoodQualityChanged 0x0252 is the only contract message in the registry, layout unknown; loot notify exists only as an enum name). Sending a guessed layout to a real client could disconnect it, and bots cannot read it, so there is nothing to measure.
- Standing default: contracts stay UI over ClientQuestAccept/ClientQuestComplete (see the contracts entry above); loot is granted server side. Revisit only with a source for the packet layouts (client capture or a fork that has them).

## Forced quest sweep
- Mentioned quests can be accepted (certain): QuestManager.CanAcceptWithoutGiver refused every quest in the list; QuestAdd (the accept path from the client) only refuses Accepted/Achieved and Completed-not-reset. Mentioned/Botched/Ignored/Abandoned now pass the same way. Evidence: 3772 character_quest rows in state 5 (Mentioned); 301 of 583 quests completed by the forced sweep had the normal picker verdict already-in-log-or-done.
- Friendly quest kill targets can be damaged by a player who holds an open objective their death advances (kill, kill target group, virtual or quest item the creature gives, named kill) (likely). 37 open quests stalled in the forced sweep on a KillCreature/KillTargetGroup target the bot could not damage: faction 219 "Friendly To All" (32), 170 "Dominion NPC" (5), 171, 342, 363, 502, 757, 1086. The sniffed community spawns carry the same factions (e.g. 22775, 26042, 32981 faction 219, spawn ids below 9000000), so the game changed them at run time; the FactionSet spells named after such quests ("Q7658 - Reading Vitals", "aggressive cubig - q3901", "Q4986 - Canimid Altercation", "Dominion Arkship Tutorial - Q7009") show the mechanism is a quest script, which the data does not contain. Default: the damage handler lets such a player hit the creature (only while the objective is open and its sequence reached, vendors never). Ledger: quest-kill-friendly-target. Code: QuestManager.NeedsKill, Spell.QuestKillTarget.
- FactionSet (effect type 9, 186 rows) has no handler; no FactionSet spell was cast in the sweep or the 10 h soak (their casters are scripts). Left unhandled, Spell.cs records unhandled-spell-effect if one is ever cast.
- Bot bags: bots destroy items no quest uses (not pushed by a quest, not a CollectItem objective, not a quest starter), lowest quality first, when fewer than 4 bag slots are free or a forced quest pushes more items than fit (QA behaviour standing in for selling loot). Evidence: 21 forced quests "QuestAdd left it out of the log (inventory slots 0)". Ledger: bot-bag-cleared.

## Contract Board, periodic draw, mail take-all, loot window, FactionSet, prerequisite 22/23/33-36 (2026-10-08)
Sources: retail client static analysis (local copy of the client, not shipped; details in docs/PROTOCOL.md "Periodic quests, Contract Board").
- Periodic quests and contracts (certain, client code): the client draws rotating dailies itself. Tree: groups in PeriodicQuestGroup row order; a set joins its category when its first group is read; key = running sum of the child weights (set.Weight, group.Weight, Quest2.PeriodicQuestWeight); a quest joins only with QuestRepeatPeriodEnum != 0 and weight != 0 (Quest2 row order, group 1 when its group is missing). Draw per node: Offered >= children -> all; else Offered picks without replacement, each pick MT19937(node id + picks left + (u32)seed), roll = floor(u32/2^32 * total) + 1, first child whose sum >= roll, its weight taken out. A count of 0 offers nothing (the old assumption "0 offers one" was wrong). Categories by id. Replaces the per-character draw: the offer is realm wide per seed.
- Seed (guess, harmless): the value is server-chosen; the server sends days since 0001-01-01 of the daily reset day in ServerQuestInit.DailyRandomSeed and a new one with ServerQuestPeriodicReset (Flags 0: the 4 flag bits only feed the Lua event QuestPeriodicReset, meaning unknown).
- Quests with a group but outside the client tree (no repeat period or weight 0) stay always offered (the client never shows them as periodic; recorded as an assumption).
- Contract Board (certain): accept = ClientQuestAccept at location 300 / bag -1 (Contract:Accept; ClientQuestRetry 0x365 when the contract is Botched), hand in = ClientQuestComplete (Contract:Complete), abandon = the quest abandon. The board shows per type the Good contract the server names in ServerQuestContractGoodQualityChanged (u32 per type 0/1/2) and the client draws of the quality 2 and 3 groups (groups 50/51 PvE, 53/54 PvP). Slots: reward property 37 ActiveContractSlots, data 0, Discrete (RewardPropertyPremiumModifier 225/226: 3 basic, 4 signature) - enforced. Opening (interaction event 85) is client side; the server records it.
- Good contract choice (guess): one pick from the type's quality 1 group (49 PvE, 52 PvP) with the client draw and the day's seed; the retail rule is unknown, any choice is shown by the client as sent.
- Mail take-all (certain layout): u32 count, count x u64 mail id, u32 mailbox unit (same field the single take sends). Answers (likely): the single-take replies per item, cash first, attachments highest index first; unpaid COD mails skipped; a full bag stops the run.
- Loot window (layouts certain, rules partly guessed): need/greed and master loot open a window on the corpse (ServerLootNotify, owner = creature, one loot id per item). Roll time 60 s (guess: NeedVsGreed.lua only shows nTimeLeft in ms). Missing rolls at the deadline = pass (guess), need beats greed then highest 1-100 (as before), all passed drops the item (guess; the client has a LootRollAllPassed event), master loot not assigned in time goes to the leader (guess). Need is refused for an item the class cannot use (GameLib.IsNeedRollAllowed, rule as before).
- FactionSet (effect 9, certain for DataBits00 = Faction2 id): "Q932 - Make player neutral to pumera" 290, "Tranquilizer Gun"/"In Stasis" 219 Friendly To All, "Q7658 - Reading Vitals" 218. Applied to creatures for the effect duration (0 = until respawn) and shown with ServerEntityFaction. Players: recorded only, Faction1 is also their Exile/Dominion identity (a disguise needs a disposition override). Bots cast the quest's FactionSet spell (spell description names the quest, effect on the target, not 219) on a friendly kill target before attacking: 16 quests have such a spell. The older fallback (a quest holder may damage a friendly kill target) stays for the others (ledger quest-kill-friendly-target vs quest-faction-spell-cast).
- Prerequisite types 22, 23, 33, 34, 35, 36 (certain, client evaluator): progress of objective 0..5 of quest <object> compared with value. Completed quest = the objective's count (100 for percent objectives), quest in the log = its progress, else 0. Replaces "22 assumed met" and "36 assumed met".
- Out-of-combat health regeneration: still not recoverable. The client has no health regen code or Lua API (GetShieldRegenPct and focus regen exist, nothing for health; HealthRegenMultiplier 8 is only a multiplier), the wiki Stat page and the scraped archive pages give no rate. The bot rest heal stays (harness only).
- Objective type 10 data 1 (quest 8993 "Follow the creature", count 5): TargetGroup 1 holds creatures 1/1972/1974/1955, unrelated to the quest's creature 52465, so data is not a target group here; meaning unknown, stays synthetic. Achievement 5598 (type 12, zone 12, flags 2): no spell effect, quest or table sets it (gt findall 5598: only the achievement row and unrelated ids); script-only, unknown.

## Rating curves and secondary stats (2026-10-08; research/decode/ratings/curves.tsv)
- certain (client code): every Unit:Get<Stat>Chance / Get<Stat>RatingPercent Lua API calls one curve, client code, with a
  GameFormula row per stat, the unit's level (effective level at +0x3c, else level), its base property and its rating property (unit
  property float = unit + 0x5c4 + id x 8, checked on 22 callers). s = Datafloat0 / level x Datafloat01; knee = (Datafloat02 - Dataint0) / s;
  below the knee value = Dataint0 + s x rating, above it Datafloat02 + s x over x cos(pi/2 x over / (over + Datafloat03)) (the called function is cos:
  its small-argument branch is 1 - x^2/2 ...); result = min(value x 0.01 + base, Dataint01 x 0.01).
  Rows: 1230 strikethrough, 1231 crit chance, 1232 crit severity, 1233 lifesteal, 1234 mitigation, 1235 deflect, 1236 deflect crit,
  1240/1270 multi-hit chance/amount, 1241/1272 reflect chance/amount, 1242 critical mitigation, 1243 intensity, 1244 vigor (starts at 100),
  1245/1271 glance chance/amount, 1269 armor pierce (base IgnoreArmorBase), 2378 tenacity, 2379 hope, 2380 toughness; 1238/1239 PvP.
  Example level 50 crit: 0.0025% per rating up to 30% at 12 000 rating, then diminishing towards 57.5%, cap 100%.
- certain (client code and its six callers): mitigation of a damage type = DamageMitigationPctOffset + DamageMitigationPctOffset<type>
  + curve 1234 of (Armor + Resist<type>), cap 75%. Level-row armor gives 27.8% at level 10 and 30.3% at level 50 (same at every level, which
  supports the 1/level slope). Replaces the guessed armor / (armor + 1.5 x level armor) curve (40% at level armor, cap 90%).
  Resist properties are ratings added to armor (not percentages): "Doomcall (70% Invuln)" multiplies ResistPhysical/Tech/Magic by 100,
  which lands at the 75% cap.
- certain (client tooltip strings, en-US.bin): what each stat does. Deflect "avoiding all damage from that attack"; strikethrough "reduces
  the chance foes have to Deflect your attacks. Strikethrough that exceeds a foe's Deflect rate is converted into Armor Pierce"; armor
  pierce "Your attacks ignore this percent of the foe's mitigation"; critical mitigation "Reduces bonus damage caused by a foe's Critical
  Hit"; deflect crit "Reduces the chance foes have to Critically Hit you"; glance "chance to reduce each incoming damaging attack by your
  Glance Mitigation"; multi-hit "chance for each application of damage or healing to be applied an additional time at a reduced value"
  (amount = multi-hit severity); lifesteal "Your damage heals you for this percent of damage done"; vigor "damage is increased up to this
  percent, based on your current health"; tenacity "... based on your target's current missing health"; toughness "damage reduction is
  increased up to this percent, based on your current missing health"; hope "healing is increased up to this percent, based on your
  target's current missing health"; intensity "outgoing healing amount and focus costs are increased by this percent"; reflect "chance to
  return damage dealt to you back to the foe at a reduced value".
- likely (composition, Combat/DamageMath.RollHit): deflect - strikethrough rolled first (deflected = no damage, proc trigger 51), then
  mitigation x (1 - pierce) (greenyneko "enemyResist x (1 - ARP)"), crit chance - deflect crit, bonus (severity - 1) x (1 - critical
  mitigation), glance, vigor/tenacity/toughness scaled linearly by the health fraction ("up to"), multi-hit as an extra (1 + amount) on the
  same hit (the client shows a second number; the server folds it into one, proc trigger 251), then lifesteal and reflect on the dealt
  damage (reflected damage is not reflected again). Heals: crit, (1 + intensity), hope, multi-hit. Focus cost x intensity: not applied.
- guess (kept): players' base crit 5% when no effect sets BaseCritChance (UnitProperty2 default 0); creatures base 0. Ratings come only from
  gear (ItemStats) and effects, the level row has none, so creatures deflect/glance/crit only through spell modifiers.
- Weapon damage parameters 6 and 9: still unknown. The client has no weapon damage Lua API and no string for it ("weaponPowerBase/Modifier"
  exist only as Item2Category field names, 0/1 in every row); 0 casts of such rows in the last soak.
- ModifySpellCooldown DataBits02 0/1 (113 rows) — likely: multiply the remaining cooldown by DataBits03. Value 0 = reset (Quick
  Shot Left/Right 28913/28914 reset each other; stance 46867 "Reset Assault and Support ability cooldowns" = group 66 x 0); 0.8 = "Reduce all
  cooldowns by 20%" (Fixation 33272, tooltip token data03pctsub = 1 - 0.8); 29213 "Reduces Spell Group by 50%" = 0.5. 0 and 1 are not
  separated by the data (both instant on rows without a duration). Rows with a duration (26, e.g. 29216 "Reduce GCD by 50%" for 10 s) would
  also need a lasting multiplier on new cooldowns: not modelled, counted in the ledger. Mode 0 with spell base 0 (Wrecking Ball) matches nothing.

## Instance runs (2026-10-08; scripts/instance-run.sh, docs/INSTANCES.md)
- certain: Creature2Difficulty.groupValue is the intended group size (5 for difficulty 12 dungeon bosses, 20 for 17/18, 40 for 23).
  Laveka the Dark-Hearted 65997 (difficulty 18, 26.4 M health, description "[RMT] Laveka - Boss") was placed in Skullcano 1263 by a
  name-only Jabbithole match; a 5-bot team hit it for 20 minutes and took 1 %. Dropped (scripts/research/fix_instance_creatures.py).
  The Skullcano Laveka is 24488 "[SK] e329 ... Flavor - Normal" or 25698 "[SK] e376 ... Vignette": two candidates, none placed.
- likely: the leading tag of a Creature2 description names its instance ([SK] Skullcano 1263, [STL] Stormtalon's Lair 382, [KV] Kel
  Voreth 1336, [TS] Sanctuary 1271, [GA] 1462, [DS] 1333, [IC] 3040, [UP] 2980, [UPJ] 3173, [RMT] 3102 set by hand; each other tag has 80 %+ of its instance-pass and
  community spawns in that world). jabbithole_match.py mapped "Skullcano Island" to open world 22 in its main pass (ALIAS was only used
  in the instance pass; fixed), so Bosun Octog 24486 and Mordechai Redmoon 24489 stood in world 22 and Skullcano had neither. Moved to
  1263 at the page coordinates (inside the instance's spawn frame), Ohmna 49395 likewise to 1462. Boss rows whose page zone is a
  capital or open zone (Thayd, Illium, Farside, a warplot: Aethros, Stormtalon, Trogun, Octog, Mordechai, Ohmna) were dropped: 20 rows.
  Bounty boards (24285, tagged [SK], difficulty group 0) are real open-world creatures and stay.
- certain (bug): BaseMap.Search/GridSearch with a large radius near the world edge stepped outside the 128 x 512 m grid and threw;
  bots in Infinite Labs (x -29000) and 3173 failed on every think (the dungeon teams then waited forever for the failed member).
  Searches are clamped to the world now; a team goes on without a member that is away for 60 s (ledger dungeon-member-missing).

## AMPs (2026-10-09; Game/Spell/ActionSet.cs, Game/Entity/SpellManager.cs, SpellEffectHandler.Amps.cs)
- AMP passive: EldanAugmentation.Spell4IdAugment (774 of 910 rows have a spell; duration 0; effects Proc 285, UnitPropertyModifier 221, Fluff 83, Proxy 33) is cast on the player while the AMP sits in the active action set and its lasting effects are removed on respec or action set switch. Likely (an AMP is described as a passive, every spell targets the caster, no other place in the tables applies it).
- Rules enforced in ClientRequestActionSetChanges: class (ClassId), level 10 (LevelUpUnlock row 26 "AMP System"; certain), the AMP named by EldanAugmentationIdRequired (certain from the column), CategoryTier 1 needs Tier2CostAmount (3) and CategoryTier 2 Tier3CostAmount (11) points spent in the category (EldanAugmentationCategory names only itself in Tier2Category00; likely), power cost. A respec also removes AMPs whose requirement is no longer met (likely).
- AMP unlock items: SpellEffectType UnlockInlaidAugment (127, 281 rows): DataBits00 = EldanAugmentation id (spell names equal the AMP names; certain). AMPs named by such an effect are locked until the item is used (likely; the other AMPs are open from level 10).
- Bonus points: GiveAugmentPowerToPlayer (128, spell 67476 "AMP Power Unlock") and GiveAbilityPointsToPlayer (78, spell 67478 "Ability Tier Point Unlock"), DataBits00 = 1 point. Caps from their cast prerequisites: 28557 type 237 LessThan 10 ("Maximum AMP power reached") and 28559 type 235 LessThan 7 ("Maximum Ability Tier points reached"): certain. Prerequisite types 235 (bonus tier points) and 237 (bonus AMP power) are evaluated. Unlocks and bonus points live in the character JSON store "amps".
- Not done: AMP respec cost (client GetEldanAugmentationRespecCost, no table found), base AMP power per level (kept at 45 from level 10; the client reads it from data not identified), 0x01A2 "ClientCommitAmpSpec" is a string message (7 bit length + UTF-16), not an AMP list: the upstream name is wrong (likely).
- Runes (Game/Entity/RuneManager.cs, Network/Message/Handler/RuneHandler.cs): rune elements are the client's Item.CodeEnumRuneType registration (Air 7, Water 8, Earth 9, Fire 10, Logic 11, Life 12, Fusion 13; certain). Gear carries the defined sockets (431 items, ItemRuneInstance DefinedSocketType = 11/12/13 = Logic/Life/Fusion), rune items carry the set (982 runes with ItemRuneInstance ItemSetId/ItemSetPower; certain from the columns). Slot count: defined sockets, then ItemQuality.DefaultRunes (Good 1 ... Artifact 5), up to ItemQuality.MaxRunes (likely). Random slot elements are uniform Air..Life (guess: ItemRuneSlotRandomization is empty in this client). A rune fits a slot of its element, a Fusion rune any slot (guess). A filled slot must be cleared before another rune goes in (certain, client check in InstallRuneIntoSlot). Clearing destroys the rune (guess, two flag bits undecoded). Adding and rerolling slots is free (cost unknown: ItemQuality.RuneCostMultiplier 0.55-1.25 exists, the base is not in the tables). Installed runes add their item stats (ItemStats.For on the rune) and their set power; ItemSetBonus applies when the set power reaches RequiredPower: UnitProperty2Id as value x Scalar (when not 0) + Offset (133 additive rows such as +0.18 % crit chance, 25 multiplier rows such as x1.01 focus pool; likely), Spell4Id as a passive (135 rows). The item network model is not changed: what the client expects in Glyphs/RandomGlyphData is not decoded, so a real client does not see slots yet.
- Rune messages: client messages recovered (writers, likely): 0x0859 RuneSlotAdd {u64 item guid, 1 bit, 5 bit rune type}, 0x085A RuneSlotClear {u64 guid, u32 slot, 1 bit, 1 bit}, 0x085B RuneInstall (CraftingLib.InstallRuneIntoSlot; certain opcode) {u64 guid, u32 count, count x u32 rune Item2 id per slot, up to 8}, 0x085C RuneSlotReroll {u64 guid, u32 slot, 5 bit type}. Item model writes Glyphs (4 bit count) then a second 6 bit count that upstream fills with Glyphs.Count (fixed to the Unknown88 count).

## Death and resurrection (2026-10-09; Game/Entity/Player.cs, ResurrectionHandler, client UI/Death/death.lua)
- The client death screen (death.lua, read from the Patch archive) offers Wake Here (bWakeHere, cost monRezCost, cooldown fWakeHereCooldown), Holocrypt, Exit Instance and the caster's offer (bAcceptCasterRez + bHasCasterRezRequest); each button calls Unit:Resurrect(RezType, 0) which sends 0x015A. Layout (client writer): u32 unit, RezType:7, u32 value; upstream read the type as 32 bits (fixed, certain). ServerResurrectionShow layout confirmed by the client reader (u32, u32, u32, 1 bit, 8 bit flags, 1 bit, u32, u32).
- Wake Here: in place, 1 h cooldown (wiki Signature page: free players 1 hour, signature 50 % less), no credit cost (no cost table found; RezCost 0).
- Holocrypt: the tables have no holocrypt positions (no MapZonePOI sprite, 2 unspawned "Holocrypt" creatures). Guess: the release goes to the nearest bind point creature spawn of the world (Creature2.BindPointId, 55 "Transmat Terminal" creatures, 44 spawns in worlds 22, 51, 1061); worlds without one offer no Holocrypt button.
- Exit Instance: inside a dungeon instance, back to where the player came from (DungeonInstancedMap return position).
- Resurrect effect (16, 20 rows): target flag 2/4 = the dead target; DataBits00 health, DataBits01 focus as floats. Values up to 1 are a fraction of maximum health (0.25 in the out-of-combat Resurrect/Resuscitate: likely); values above 1 (20, 30, 250, 500-2000) are read as percent capped at 100 (guess). The dead player gets the offer (death screen flag SpellCasterLocation) and takes it with ClientResurrectAccept; spells with a Resurrect effect may target dead units. Dead creatures are not resurrected.
- Death penalty: none applied. Items have Durability but no source says how much a death costs (the client has a CombatLogDurabilityLoss event; no table or wiki page gives the amount). Recorded as unknown.

## World bosses (2026-10-09; PublicEventEngine)
- Lifecycle, certain from PublicEventObjective (all 12 "[GROUP 20+]" boss events 150, 169, 177, 372, 491, 586, 590, 593, 641, 847, 852, 855 have the same rows; 372 "Dethrone the King", 491 "Forgotten Trial of Tresayne", 586 Mechathorn, 641 Sulak, 847 "His Own Kingdom" are the ones without an encounter file): an initial TimedWin "The Progenitor Access Console is resetting" of 3600 s, a type 7 objective "Place 75 Progenitor Access Particles (item 81325) into the console or wait" (objectId 36 = PublicEventVirtualItemDepot), a 120 s TimedWin "<boss> will break out / is coming", and KillTargetGroup of the boss (groups 14189, 14190, 9129, 9092, 14192, 8600, 8527 = one creature each).
- Implemented: a world boss kill (in or outside an event run) starts the reset; the boss creature respawns after reset + countdown (3720 s) instead of the 45 s creature respawn, and the event cannot start before then. An event started after the reset has the boss up and only the kill objective active.
- Not implemented: the item deposit shortcut (no source for item 81325 in the drop data; the depot table has no amounts), the reward rotation (PublicEvent.RewardRotationContentId 26/36 -> RewardRotationContent type 4; RewardRotationEssence names account currencies without amounts). Group size: Creature2Difficulty 18 groupValue 20 and the "[GROUP 20+]" title are a recommendation; nothing in the tables restricts who may fight or loot, so nothing is enforced. Loot: archived Jabbithole drops by name (7 of 9 bosses; Dreamspore none). King Honeygrave and Zoetic have spawns but no event: ordinary respawn.

## Client messages without a handler (2026-10-09; Network/Message/Handler/LuaApiHandler.cs, docs/PROTOCOL.md "Lua-reachable client messages")
Layouts from the client writers/readers (client analysis), constants from the Lua API functions (disassembly), behaviour from the
client UI scripts (extracted read only with `docker compose run --rm -e NF_EXTRACT_PATTERN=... uiextract`).
- 0x00C5 ChallengeChoice {challenge id 14, choice 5, value 32}: choice 1 Activate, 2 Abandon, 14 AcceptShared, 15 RejectShared (certain, the
  constants the four ChallengesLib functions store). Activate starts the challenge, Abandon ends it without a medal. Accepting a shared challenge
  starts it for the player; nothing on the server shares challenges yet (ServerChallengeShared 0x00A9 is never sent; guess for the value field = sharer).
- 0x0167 SellJunkToVendor (no body): sells every backpack item of Item2Category 94 "Junk" with its whole stack to the open vendor (certain: the
  vendor UI counts junk as GetItemCategory() == Item.CodeEnumItem2Category.Junk over the bag; Vendor.lua line 529).
- 0x0422 GroupLib.Join {name, realm} / 0x0453 AcceptRequest/DenyRequest {group id 64, accept 1, requester name, flag 1}: the leader receives
  0x0424 {group id, GroupMemberInfo of the requester} (client reader, the same member struct as the group join message; likely) and the
  requester 0x0456 {group id, name, GroupLib.Result 5 bits, flag 1} (client reader): SentToLeader, GroupNotFound, Full, Pending, LeaderOffline,
  WrongFaction, Accepted, Declined, ExpiredInvitee after 30 s (result names from GroupDisplay.lua's GroupLib.Result table; likely). The two flag bits are unknown (sent 0).
- 0x05B4 LeaveAllQueues, 0x05B5 LeaveQueue {match type 5}, 0x05B6 LeaveQueueAsGroup {match type 5}: the server's group finder has one queue, all
  three take the player (or the whole group) out of it (likely; the match type is not used).
- 0x0150 ResetAttributePoints: Drop 6 removed attribute points, build 16042 has no attribute point table: recorded (`attribute-reset`), no effect (likely).
- 0x0720 Cinematics_Play {state 8, flag 1}: the cinematic runs on the client; no quest objective or event in the tables waits on one: recorded (`cinematic-state`).
- Not implemented, with the reason: PvP (duels 0x00E8-0x00EC, 0x0170, pvp flags 0x0173, vote surrender 0x05D3/0x0624) out of scope; match votes and
  replacements (0x05D1, 0x05D5, 0x05DA, 0x0602, 0x0606, 0x0617) need a matchmaker with server-made matches (the group finder forms bot groups server
  side); public event vote 0x06EE {event 14, vote 14, choice 14, 32} needs the vote start message (PublicEvent.GetActiveVote) and the event data that
  says which objective a vote decides (64 PublicEventVote rows have texts only); reward track claim 0x07CE {track 14, 32} conflicts with challenge
  rewards granted on completion (ChallengeManager); mentoring 0x0439/0x043B/0x043E needs level scaling of stats; pet stance 0x068E {unit 32, stance 5}
  needs combat pets; scientist scanbot 0x0180/0x00F0 and settler/soldier builds 0x0670/0x067D need the path improvement data (PathSettlerImprovement*)
  wired to spawns; CREDD 0x0267/0x0269, survey 0x033F, bug report 0x0830, PTR copy 0x06E8, galactic archive viewed 0x0105, options 0x012B, friendship
  lock 0x00B8, guild bank tab 0x0477, target marker 0x0432: no gameplay effect a bot can observe or no server state behind them.

## Item stat type 4: see "Equipment stats" above. Still unknown in equipment stats: weapon damage (no field in build 16042),
  per-item random stat rolls (not stored), ratings above item level 50 (tooltips 4-11 % below the linear formula, no clean curve).

## Proxy cycles and runaway casts (2026-10-09; SpellEffectHandler.HandleEffectProxy, UnitEntity.CastSpell)
- Datascape "Purge" 46811 and 46812 each carry a Proxy (26) of the other (DataBits03 1.0f, DataBits04 0xFFFFFFFF, DataBits05 60). Cast as
  written they bounce forever: a 20-bot Datascape run held 62k live spells and the world ran out of memory (GC heap cap) right after the
  System Daemons. guess: a proxy back to a spell already in its chain (itself, its parent or the root) is not cast (ledger `proxy-cycle`);
  the real stop condition (DataBits04/05 may be a jump count or interval) is not decoded.
- Safety net: a unit with 400 pending spells refuses further casts and records which spells pile up (`spell-pending-overflow`).

## Terrain memory: MapFileCell keeps zone-bound and height arrays only when the cell has them and they are not uniform
  (certain: same lookups; a heap dump showed 782k cells with eager 64x64 + 17x17 arrays, ~4.2 GB). World heap 7.7 -> 2.3 GB with 30 bots.

## Remaining partial systems (client binary WildStar64.exe, 2026-10-10)
Lua -> opcode map redone (research/clientre/lua-sends2.tsv): linear disassembly and the second send function
(small fixed messages) give 346 sending Lua functions (143 before). Several "no client message" claims in docs/SYSTEMS.md were wrong.
- Durability and repair (certain for the formulas, likely for the value): only Item2Family 1/2 have durability; max points = GameFormula 559
  dataint01 (20) + Item2.powerLevel (client code); current = max * fraction + 0.5; repair cost = GameFormula 559 datafloat03 (0.25) *
  missing/max * item value + 0.5, value taken as the credit sell price. Wiki check (scripts/research/wiki_durability.py): 2 tooltips
  match 20 + power level, 1 differs (Fibertronic Canvas Jacket 39 vs 33 for the matched row; several rows share the name). 0x014C RepairItemVendor
  {guid,0,0} / RepairAllItemsVendor {0, vendor, x}; 0x01A7 ServerItemDurabilityUpdate {guid, float}. Durability loss: no source for the amount, off
  unless NF_DURABILITY_DEATH_LOSS (fraction per death) is set.
- Taxis (certain layout, likely semantics): 0x00FF PurchaseFlightPath/TakeShuttle = count + node ids (TaxiMap.lua tPath.tRoute). Price = sum of hop
  prices, the player arrives at the last node; the flight itself is client-side movement, not simulated.
- Matching (certain): MatchMakingLib.MatchType enum from a client table (Battleground 0, Dungeon 1, Adventure 2, Arena 3, Warplot 4,
  RatedBattleground 5, OpenArena 6, Event 7, Shiphand 8, WorldStory 9, PrimeLevelDungeon 10, PrimeLevelExpedition 11, PrimeLevelAdventure 12,
  ScaledPrimeLevelAdventure 13, ScaledPrimeLevelDungeon 14, ScaledPrimeLevelExpedition 15); Roles Tank 0, Healer 1, DPS 2 (doubles pushed into the
  Lua enum). Queue 0x05EF/0x05F3 (match type 5, map ids, 14 bit, flags, roles bit field 1 << role, prime level), random 0x05F8, role check 0x05B2.
  Only dungeon types queue (the server has no other queue). Premade groups: role check then "premade match" when 5 with a tank and a healer;
  smaller premades are not filled from the solo queue (guess at retail behaviour, recorded in the ledger).
- Housing plugs (certain layout, likely semantics): 0x0510 = owner identity, plot (u32, property index), plug item (u32), facing (u32, PlacePlug sends 4
  = Default, SetPlugRotation the Lua value), unused u32, operation (3 bits: 1 place/rotate, 2 remove, 4 repair), 5 x 20 contribution bytes (one per
  HousingPlugItem.HousingContributionInfoId0x). The plug must fit the plot type and its PrerequisiteId00/01; FABkit item cost and contributions are not
  modelled (guess: placing is free); repair sets build state 4 (plug decay is not modelled).
- Dye unlocks (certain): Item2.genericUnlockSetId is a GenericUnlockSet id; all 418 such items are dye unlocks and 39 of their sets list an entry id that
  differs from the set id. The upstream handler read the set id as an entry id (wrong dye for 39 items) and returned early when the dye was NOT unlocked
  (the item could never be used). Fixed: entries resolved through the set, the item is used and every entry unlocked.
- Dye cost (certain formula from the client's GetDyeCost, likely level source): per item and dyed channel ceil(GameFormula 602 f0 (5) ^
  ((level + f1 (-10)) / f2 (10)) * slot factor * DyeColorRamp.CostMultiplier * channel base), at least 1. Slot factor (GameFormula 1326, by
  Item2Type.ItemSlotId): chest/legs 1.0, head/shoulder 0.7, feet/hands 0.8, other slots no cost; channel base GameFormula 602 dataint0/01/02 = 384/256/128.
  The level is read from the player object (likely the player's level). Charged on costume save for channels that change; the Token bit (service
  token payment) is recorded and not charged (token currency not identified).
- Instance portals (likely): InstancePortal has no destination (name, level range, completion time, type); Creature2.instancePortalId marks 264 portal
  units (80 spawned as entity type 14, of which 15 resolve by name). Destination = the instanced World whose name equals the portal name without
  "Enter/Portal to/Entrance to/Travel to" or the first part of the Creature2 description; types 2/3 (leave/exit) return to the remembered entry
  position. Most spawned portals are transports between continents ("Ship to Crimson Isle"), adventure hubs and shiphand shuttles whose destination
  is neither in the tables nor resolvable by name: recorded as instance-portal-unmapped.
  Second pass (2026-10-10): most instanced worlds have no name, only an asset path. Housing expeditions and shiphand missions match on the asset
  name without its Housing1x1Dungeon/Shiphand prefix (Melting Pot 1896, Spider Tomb 1899, Mayday 1901, Rage Logic 1627; likely), the six
  adventures by title (Siege of Tempest Refuge -> AdventureGaleras 1233, Crimelords of Whitevale 1323, War of the Wilds 1393, Malgrave Trail
  1181, Hycrest Insurrection 1149, Riot in the Void -> AdventureAstrovoidPrison 1437; certain from the adventure list), Truthbound Ascendancy
  1318 by name. Spawned portal units resolved: 14 by name before, +~12 now. Still unmapped (no destination in any table): continent ships and
  zone portals into open worlds ("Ship to Crimson Isle", "Portal to Algoroc", "Transport to Levian Bay": the open-world arrival point is in no
  table), Combat Training Room, Alpha Sanctum, Steady Traveler/Blue Sparrow (no matching world), Greylight Grapplefest, Kel Voreth Underforge and
  Abandoned Test Lab expeditions (their asset names do not contain the title: Housing1x1GenericEldanTestLabDungeon is likely the test lab, not mapped
  without evidence).
  Open-world transports (likely): "Ship/Portal/Transport/Journey/Fly/Shuttle to X" go to a portal unit spawned in the WorldZone named X
  (spawn zone = nearest WorldLocation2 with a zone within 400 m, plus parents), preferring open worlds. scripts/research/portal_travel.py: 57 of 75
  such spawns resolve and the result is consistent with the map (Ship to Thayd -> Olyssia 51 dock, Ship to Illium -> Alizar 22 dock, Portal to
  Deradune -> 22, Portal to Celestion -> 51, Ship to Crimson Isle -> 870). Not resolved: Halon Ring (cut content), Arcterra, Palaver Point,
  Walker's/Virtue's Landing, Galactic Observer, Star-Comm Basin, Skullcano (no portal unit spawned in the target zone).
- Group flags (certain bits, likely rules): 0x0408 = group id, member identity, new flags, changed bits; the Lua setters use the server's
  GroupMemberInfoFlags bits (SetRoleTank 0x20 ... SetCanMark 0x2000, SetReady sets 0x800 and 0x4000). A member may change their own roles (unless role
  locked) and ready state, the leader everything. 0x0406 group flags: Raid bit = convert to raid (max 20, guess at the size), only the leader.
  0x044A ready check clears every member's ready state (guess: the retail ready check message to members is not identified).
- Friends (certain layouts): 0x03B3 add by identity + type + note, 0x03C1 remove by identity + type, 0x03C7 note. Friend, ignore and rival lists and
  notes are now kept per character in the cache volume (they were in memory only); notes capped at 64 characters (guess, no client limit found).
  Account friends (type 8, 0x03BD) need the auth server and are not handled.
- Resource conversion NPCs (certain link, guess reach): Creature2.resourceConversionGroupId (49 creatures, 29 spawned) -> ResourceConversionGroup rows. The
  client's ConvertResource(unit, row, count) checks the unit's group but the 0x00D7 message carries no unit (its u64 is 0), so the server requires a
  creature offering the row within 30 m (reach guessed).
- SummonVehicle (certain): effect type 6, DataBits00 = Creature2 id, DataBits01 = UnitVehicle id (all 12 rows: one pilot seat); the caster pilots, the
  vehicle leaves the map with its last passenger. The client Lua has no seat-change or vehicle-ability message (only Disembark 0x01AF).
- Neighbours (certain layouts, likely rules): 0x0512 invite (identity or name), 0x0513 accept bit, 0x0515 evict, 0x0518 permission (bit 0 = roommate,
  guess). Lists are mutual and persist per character; invites expire after 1 minute (guess); NeighborsOnly/RoommatesOnly privacy enforced on visits;
  roommates may decorate. ServerHousingNeighbors 0x0507 stays empty: its reader has two u64 per entry (one probably the residence) not decoded.
- Group target markers (certain layout): 0x0432 {marker, unit id} needs the CanMark flag (client checks 0x2000); server 0x0431 {group id, count,
  {marker, unit id}}; one marker per unit, unit 0 clears (likely).
- SetInstanceDifficulty 0x0412 {group id, 2-bit difficulty} uses the same store as ClientSetInstanceSettings (leader only); GotoGroupInstance 0x0411
  {group id} teleports into the world of a member who is in a dungeon instance (instances are keyed by group), at the instance entrance (likely).
- GameFormula 559 also holds dataint0 5000 and floats 0.015 / 0.01 / 0.0362 that the client never reads (only dataint01 and datafloat03 are read):
  probably server-side durability loss rates; not applied without a source for their meaning.

## Imbuement rewards and item specials (2026-10-10; Game/Quest/ItemImbuements.cs, Player.SumGear/EquipSpecialSpells)
- Source: 31 archived Jabbithole item pages in research/items-raw with an "Imbuement:" block, compared with ItemImbuementReward rows.
- Type 0 (65 rows), certain: RewardObjectId = Property id, the item gains RewardValueFloat x (sum of the item's secondary ratings before rounding).
  Razorblight 16316: 26.25 x 0.15 = +3.9375 Critical Hit Severity; Stardust Blade 40268: (72 + 48) x 0.15 = +18.0 Multi-Hit; Gleamrose 40230
  (34 + 51) x 0.15 = 12.75 Glance; 41154 (66 + 99) x 0.15 = 24.75; four more within 1 % (tooltip ratings are rounded). Applied while equipped and
  the step's quest is completed by the character (the quest is per character, the item model stores no per-item imbuement state: likely).
- Type 1 (37 rows), certain: RewardObjectId = ItemSpecial id; tooltip 40301 lists "Special: Absorbent Shields" from the imbuement while its own
  ItemSpecialId00 is 0. The ItemSpecial's Spell4IdOnEquip is cast as a passive while equipped.
- Item specials in general: 551 items carry ItemSpecialId00 with Spell4IdOnEquip ("Special: Laceration" etc.) and were never applied; they are
  now cast on equip and removed on unequip like set bonus spells (ItemSpecial prerequisite checked). certain (table + tooltips).
- Types 4 (1 row value 1, Gladiator PvP gear), 5 (20 rows, values 1-20: PvP, Shade's Eve, Laveka and dungeon items) and 6 (1 row, "Patent
  Pending") are not decoded: no archived tooltip shows a matching line (PvP coat 73804 shows "+25.0 Insight" for type 5 value 5, one sample).
  guess for 5: item power or a PvP stat; left stored and ledgered.

## Friendship list messages (2026-10-10; Network/Message/Model/FriendshipMessages.cs, Game/Social/FriendManager.cs)
- Server opcodes and layouts from the client's message registry readers (client analysis) and the Lua events their handlers
  fire (client analysis): 0x03BE FriendshipLoaded (u16 count, entries), 0x03B5 FriendshipAdd (entry + realm/character/
  float tail), 0x03C3 FriendshipRemove (u64 id), 0x03C4 FriendshipResult (name, 6-bit result), 0x03C9 note (u64 id, string). certain for layouts.
- Entry (client reader): friendship id u64, realm 14 bits, character id u64, name, type 4 bits. Friendship ids are the server's choice
  (type << 60 | character id). The float of 0x03B5 (also 0x03CA alone, no Lua event) is not identified; 0 is sent. guess.
- FriendshipResult values from the client's Lua enum table (FriendshipResult): PlayerNotFound read as 8 next to
  RequestTimedOut 8, the first entry is the 0.0 slot, so PlayerNotFound = 0 (likely).
- Account friends (type 8/0x03BD) and 0x03AA-0x03B2 (FriendshipAccount* events) need account data on the auth server; not built.

## Path mission packets (2026-10-10; Game/Entity/PathMissionManager.cs)
- ServerPathMissionActivate 0x06BA / Update 0x06BB entry (client reader): id 15, completed 1, user data u32, state data u32, reason 3,
  giver u32. The handler (through the mission object constructor) stores completed at +0x24, user data at +0x1c, state data at
  +0x20; vtable slot 0x38 = completed, 0x40 = user data, 0x48 = popcount(user data). certain.
- PathMission:GetNumCompleted (client code) switches on PathMissionType: plain user data for 2, 4, 7, 19, 21, 24; popcount (checklist
  bits) for 5, 6, 17 and slot 0x48 (default popcount; a subclass may override it: likely) for 3, 13, 15, 20, 23, 25, 26, 27; 14 = slot 0x48 x 100
  / target; 16 from the explored map hexes; 18 from a power-map component; 8-12 and 22 give 0. certain for the switch.
- Sent: Activate when a mission first gets a state (it is in reach) and the full list at login; Update on progress and completion. Bitmask
  types get the low `progress` bits set (which checklist entry each bit stands for is not tracked: likely the client only counts). State data,
  reason and giver are sent as 0 (meaning not read: guess).

## Transference (spell effect 5)
What: DataBits00 1 = drain health from the target and give a share to the caster; DataBits02 = damage-like coefficient, DataBits05 = share (capped at 1). Other DataBits00 values (3 "Drain Energy") are recorded, not applied.
Why: 402 of 559 rows are vital 1 and are named Drain Life; the float fields look like the Damage coefficient. Source: `gt effectdump Transference`, no external documentation found.

## DamageShields (effect 138)
Removes the effect's formula amount (Damage-style parameters, SpellFormula) from the target's shield stat (players only, no shield = nothing). Source: rune "Supercharged" specials (spell 82315) pair it with a Damage effect and target the caster, so it reads as shield spent for damage. Likely, not confirmed by a client.

## ChangePhase and RewardPropertyModifier (spell effects)
- Both are recorded under `effect-stored` and do nothing else. ChangePhase (16 uses, e.g. spell 37403 with DurationTime 20000 and an Activate effect) would move the target to another phase layer; the server has no per-player visibility layers, so everything is visible to everyone. RewardPropertyModifier (6 uses, next to SummonVehicle, DataBits00 = 1) would change a reward property; reward properties here come only from the account tier. Source: spell dumps in soak/20261010-1119/spell-data-snap-003.txt. Needs a client to verify what players see.

## Prerequisite types 74, 125, 107, 155, 141 (assumed met)
What: 74 (NotEqual 0, object 443) and 125 (NotEqual 0, object 328) only appear next to type 69/11 on the same Challenge id (rows 10681, 9358, creatures of challenge objects); 107 (NotEqual 0) carries a PathMission id and sits beside 54 or 155 (Equal 3) in rows 30147, 22972, 12194 (path mission demo creatures); 141 appears once (row 11999, spell Q6985). Why: no text or second source decodes them; 774+89+84+27+20+1 checks per 10 h soak. Treated as met and counted in `prerequisite-assumed-met`, like 69. Next: a path or challenge bot run with the counter per row.

## Effects GiveSchematic, GiveLootTableToPlayer, SpellCounter, DistributedDamage, Absorption, SpellForceRemoveChanneled (recorded only)
What: 1-8 occurrences each per 10 h soak. Handlers record the spell id in `effect-stored` and apply nothing. Why: GiveSchematic needs tradeskill schematics from a learned-recipe flow not exercised by quests; the others have no second data source for their DataBits. Next: decode when a quest or boss script needs them.

## Combat math (2026-10-11; full pipeline in docs/COMBAT-MATH.md)
- certain: assault power = unit property 35 (AssaultRating) x GameFormula 1266 Datafloat0, support power = property 36 x Datafloat01 (both
  0.25). Source: retail client Unit:GetAssaultPower (client code: movss [unit+0x6dc] = property (0x6dc-0x5c4)/8 = 35, ecx 0x4f2 =
  GameFormula 1266, mulss [row+0x18]) and GetSupportPower ([unit+0x6e4] = 36, [row+0x1c]); greenyneko.com 2017-12 "Damage
  Calculation In WildStar": "17765 Assault Power Rating equals 17765 / 4 = 4441.25 Assault Power", Mortar Strike baseDamage 94.02% AP +
  29.77 x level = the table row 41457 (0.7953, 25.18) x 1.1822 on both terms. Spell parameter type 12/13 now multiply power, not rating.
  Every assault term was 4x too high before (players and creatures).
- certain: Damage/Heal rows with TickTime > 0 and DurationTime > TickTime deal their amount every tick (tooltip of 33287: "Deals
  $(eff=64449.input0pct) damage every $(eff=64449.tickTimeMS) seconds for $(eff=64449.durationMS) seconds"). guess: the first tick lands at
  the cast and DurationTime / TickTime ticks in total (capped at 60); ticks stop when the target dies or leaves the map.
- likely: a creature's auto-attack is the first Damage row of its researched "[AUTO-ATTACK]" / "Basic Attack" spell (7261 of 8890 creature
  pages list one). guess: without one, 0.8 x (Strength + assault power), the most common auto-attack row (187 of 647); one swing per 2.5 s
  (auto-attack spells have no cast time or cooldown in the tables).
- likely: prerequisite type 43 with a value past the quest's objectives reads as a bit mask of objective indices when every set bit names
  an objective (one row: 20840 = quest 7009 value 4 = objective 16099, the datacube creature 33022 it gates). 35 activation rows fit the
  plain index reading, 6 the mask, 3 both; rows whose value is a valid index keep the index reading.
- likely: an activate spell missing from Spell4 (10 creatures, e.g. 49836 -> 6754, five creatures -> 6755) was server-only; the activation
  is credited like a creature without an activate spell (ledger activation-spell-missing).
- missing, no source found (ledger/docs only, no guessed behaviour): weapon damage (parameter types 6/9), level-difference scaling, health
  regeneration rate (HealthRegenMultiplier exists, no base rate), threat amounts (first attacker stays the target), creature shields
  absorbing hits, the player crit base (5% kept, guess).
- certain (client): the effect amount formula, client code (called by the tooltip code for "actual" values).
  Stat part: round-half-away(sum of value x S) with S = property 0-4 for types 1-5, property 40 (Grit) for 11, assault/support
  power for 12/13; types 6-10 have no case there. Flat part (client jump table): 7 target max health, 8 caster max health,
  10 level, 14/15 caster/target shield, 16/17 max shield, 18 item budget x a caller value, 19 target health, 21 target missing
  health, 22 target missing shield, 23 caster health, 25/26 caster missing health/shield; 9, 11-13, 20, 24 no case. Effect types 8, 10, 12,
  33, 118, 138 then take (DataBits01 + flat) x DataBits00 (Transference 5: DataBits03/02; byte client table); the DataBits values on
  the rows fit (Damage DataBits00 1.0/3.0/3.5 floats, DataBits01 0 or small integers; Transference DataBits02 1.0-5.0 floats). For casters
  that are not characters ([unit+0x80] != 20, the field Unit:IsACharacter tests; 23 also skips) the stat part is capped:
  cap = (effective level x GF712.Datafloat0 + Datafloat01) x |sum of stat coefficients / their count|, above it cap x (1 + Datafloat02 x
  (1 - exp(-(stat - cap) / cap))), and both parts are divided by Datafloat03 (712 = 70, 100, 1, 1). The result is multiplied by the caster's
  DamageDealtMultiplier of the effect's damage type (property 140 + damage type), already applied by the server's damage handler.
- certain (client): the stat-part rounding is real: the stat part adds +-0.5 (a .rdata constant 0.5f) to the sum and truncates with
  cvttss2si into eax; the caller keeps it as an int (r10). So Quick Draw T1 (12, 0.1029) + (10, 5.5) at L1 is
  round(15.39 x 0.1029 = 1.58) + 5.5 = 7.5, not greenyneko's unrounded 7.08 (L10: 10 + 55 = 65, not 65.34). greenyneko's formula has no
  rounding step and only uses L50 values, where it is inside the tolerance either way. scripts/research/combat_expect.py now applies the
  client formula (rounded stat part, (DataBits01 + flat) x DataBits00).
  likely: the server computed the same (shared game code); applied in Combat/SpellFormula.cs. Not replicated: the client truncates the
  stat part to 16 bits (overflow above 32767 would be a tooltip bug).
- Client names of all parameter types (client text ids): 1 Brutality, 2 Finesse, 3 Tech, 4 Moxie, 5 Insight, 6 Weapon, 7 Target
  Max Health, 8 Caster Max Health, 9 Weapon DPS, 10 Per Level, 11 Grit, 12 Assault Power, 13 Support Power, 14/24 Caster Shield Capacity,
  15/20 Target Shield Capacity, 16 Caster Max Shield Capacity, 17 Target Max Shield Capacity, 18 Item Budget, 19 Target Current Health,
  21 Target Missing Health, 22 Target Missing Shields, 23 Caster Current Health, 25 Caster Missing Health, 26 Caster Missing Shields.
- certain since 2026-10-15 (client stance tooltips, see "Combat gaps"): the selected class innate's stance passive (Class.Spell4IdInnateAbilityPassive[InnateIndex], gated by PrerequisiteIdInnateAbility)
  is a lasting passive applied with the AMP passives and swapped on ClientInnateChange. Evidence: those spells are named "... Stance -
  Passive" (Warrior 44926/44932, Engineer 45000/41504, Stalker 45057/45058/46059) and carry permanent UnitPropertyModifier rows (Juggernaut:
  health x1.05, support x1.04, threat +2.75, deflect +5%); the Spell4IdInnateAbilityActive slot holds the "Activate Button" spell the client
  casts. Before 2026-10-11 the index was stored and never applied. Ledger innate-stance-applied.

## Time-to-kill check, mortal bots, gear above level (2026-10-11)
- likely: a worn item whose Item2.RequiredLevel is above the character level gives no stats (Player.SumGear, ledger gear-above-level).
  Evidence: the client and BotGear refuse to equip such items, so a worn one only exists after the level went down, which retail never
  did (mentoring scaled gear instead; no scaling source). Before: bot level rotation left level 1-11 bots with ilvl 70-135 gear (a level 1
  Spellslinger had 26775 health, 12807 assault rating, mitigation at the 75% cap). BotGear now replaces such items.
- certain (tables): Creature2 rows without a difficulty rank (Creature2DifficultyId 0) that share a boss name are flavor copies: the 3
  spawned ones are described "Comm Call NPC" (Avatus 58332), "Elemental Pair Rooms Intro" (Avatus 63151) and "Boss - Flavor Only" (Metal Maw
  Prime 62377). EncounterManager no longer gives them an encounter (ledger encounter-flavor-copy); the real bosses (Avatus 30505 Raid Prime,
  Metal Maw Prime 26190) are spawned as well.
- finding, not applied: the server never charges Spell4.InnateCostType/InnateCost (class resources for spenders: Esper Mind Burst type 6
  EMM 4 cost 1, Stalker Impale type 8 cost 35). Cost types seen: 15 (649 rows, Focus by the class mix, likely), 5, 6, 19, 18, 8, 23, 7, 4,
  13, 16 (Dash Left/Right, so the type is not the VitalModifier id: guess that it is the client's Vital enum). Without costs a player can
  cast spenders on every global cooldown: scripts/research/ttk.py puts a level-appropriate player's single-target DPS at 4420 (server) vs
  3443 (one spender per 4 s, guess) vs 2395 (builders only) at level 50, and solo Prime families die in 27-28 s instead of 39-52 s.
  Next step: decode the cost type enum (client Spell4 cost check) and the EMM (InnateCostEMMId) table before charging costs.
  Superseded on 2026-10-15: the enum is the Vital table, Resource1 costs are charged (section "Combat gaps").
- guess (re-checked, no new source): player base crit chance 5% (UnitProperty2 102 default 0; CreatureLevel rows stop at property 99;
  the Bezgelor emulator's 5% is its own constant, research/bezgelor character_stats.ex; the archived wiki "Stat" page gives no number).
- certain (tables): designer descriptions mark difficulty with a " - Veteran - " or " - Normal - " segment as well as "(V)"/"(N)" (10 and
  12 Spell4 rows, e.g. "[KV] e444 - Grond - Bloodburst - Veteran - Base - Tier 1" summons 56575 "[KV] e444 - Grond - Bloodspawn Flea -
  Veteran", level 50). DesignerTag.FitsMode uses both forms. 2 rows say " - Hard - " (RMT Engineers); not treated as a mode (no evidence
  which difficulty it is).
- certain (tables): the same rule applies to researched creature abilities and to spawns. The gate after the encounter fix still had
  "[KV] e444 - Grond - Bloodburst - Veteran" (63099) cast 6 times and 10 bot deaths to its level 50 "Bloodspawn Flea - Veteran" (56575) in
  normal Kel Voreth: the Jabbithole ability lists hold both difficulties' spells. NonPlayer.LoadAbilities now drops abilities for the other
  difficulty (ledger creature-ability-other-difficulty). DungeonMapInstance.SkipSpawn applies the rule to unpaired spawns (ledger
  spawn-other-difficulty; data/spawns/research-spawns.tsv has 56575 in world 1336, no hit in that gate).

## Combat gaps (2026-10-15)
- certain: "no killer (fall, environment)" deaths were the system bots' resurrection probes (BotSystems RezProbe/RezOptionsProbe call
  Kill(null)): on the running server 938 such deaths = system-ok rez 478 + rezoptions 460, all system bots (L28 in the 8 h run, L50 after the
  group-finder probe raised them), all in world 22. The server has no fall, drowning or hazard damage at all. Probe kills are now labelled
  (ledger player-death-probe, Player.ProbeDeathCause) and left out of player-death and combat-stats; a null killer reads "scripted kill".
  Fall damage: the client has a combat log type (CombatLogFallingDamage string in WildStar64.exe) and Property 134 FallingDamageMultiplier,
  so the server should send it, but no table holds the height/damage curve. Not implemented (no source), bots never take fall damage.
- likely: prerequisite type 143 is the instance's veteran mode (value 0 normal, 1 veteran). Evidence: its 113 rows gate only dungeon and
  adventure spells ([TS] [SK] [KV] [STL] [UPJ], Malgrave Adventure caster-cast row 14893); rows 14409 (Equal 0) and 14893 (Equal 1) split 322
  damage spells into a normal and a veteran copy of the same effect, and the veteran copies carry a median 1.54x the coefficients (321 pairs).
  Before 2026-10-15 effect prerequisites (PrerequisiteIdCasterApply/TargetApply) were never read, so both copies landed: normal casts did ~2.5x
  and veteran casts ~1.65x their damage. Now Spell.DifficultyAllows skips rows whose type 143 terms do not match the caster's map
  (ledger effect-difficulty-gate); quests and casts evaluate 143 too (PrerequisiteType.InstanceVeteran). The other terms of effect
  prerequisites are still not evaluated (unchanged).
- certain (decode): Spell4.InnateCostType is a Vital table id (Vital.tbl: 1 Health, 4 Kinetic Energy, 5 Endurance, 6-11 Resource1-6,
  12 Suit Power, 15 Focus, 16 Dash, 18 Actuator, 19 Spell Surge, 26 Volatility). Class.vitalEnumResource00-07 maps resource slots to vitals
  per class (Warrior 4, Engineer 26, Medic 18 in slot 1; Stalker 12 in slot 3; Spellslinger 19 in slot 4; slot 0 = 5, slot 7 = 16), which
  matches the server's Stat comments. Live class abilities (SpellLevel): Esper/Engineer pay 6 (Resource1), Medic 18 (= its Resource1) and 15,
  Stalker 8 (Resource3), Spellslinger and healers 15 (Focus); Warrior abilities have no innate cost.
  (Superseded on 2026-10-16: charging is on by default and the builders land.) Implemented but off (NF_INNATE_COSTS=1 / .env INNATE_COSTS=1 turns it on): Resource1 costs checked and charged for user casts
  (Game/Spell/InnateCost.cs, CastResult CasterVitalCostResource1, ledger innate-cost), bots skip what they cannot pay. The tables refill
  Resource1 through builder VitalModifier rows (Esper 10, Warrior 37, Engineer 4, Medic 5), but in the server they never land: the
  generators sit behind Proxy rows on telegraph targets (Esper Telekinetic Strike -> 30900 "+1 Psi Point", TargetFlags 4), and not one
  positive vital-6 change appeared in three 10-minute gates, while Engineer 46297 "Remove Volatility" (Spell4ConditionsIdCaster 4,
  conditions not evaluated) applied -100 Volatility 576 times. Charging on that state locked every Esper/Engineer/Medic spender
  (Resource1 0 on all 7 such bots, 455 waits). Off by default the costs are counted ("would charge"). Next: player telegraph targets
  for proxies and Spell4Conditions (in/out of combat) before turning it on. Not applied: Focus, Suit Power, Spell Surge, Dash. No table holds the pool size or the
  regeneration rate (CreatureLevel values 0 for properties 5, 9-22; only percentage modifiers such as Gather Focus exist), so charging them
  would empty the pool for good. Counted as "innate-cost not charged". Resource maxima (ResourceMax1) are not set either, so Resource1 has
  no cap. InnateCostEMMId and SpellLevel.costMultiplier are not applied.
- certain (was likely): class stance passives. The client tooltip of each stance's Activate button ("A Defensive Stance that
  grants: Max Health $(eff=174531...)") cites the Passive spell's own effect ids: Warrior Juggernaut 4/4, Engineer Provoke 4/4, Eradicate 1/2
  (the other in the active spell), Stalker Evasive 3/4, Agile 3/5, Lethal through the passive's proxy 60724. Warrior Onslaught's tooltip
  cites only the active-triggered spell 42908, so for that one passive it stays likely.
- certain (client formula), applied: Damage/Heal rows without parameters but with DataBits01 set (Transference DataBits03) are
  DataBits01 x DataBits00 (Ice Blizzard 1500, Firebloom 1200, mine explosions 450): 3521 of the 16874 bare rows (of 33502 damage/heal
  rows). Ledger spell-amount-flat-only. The other 13353 bare rows (12179 spells) have no amount (the client gives 0); they keep the server's
  DataBits00 x assault power guess (ledger spell-amount-fallback). 40 creature names cast such a spell (37 spawned, 364 spawn rows; mostly old unarmed/rifle auto-attack rows),
  16 abilities are fully on the fallback: scripts/research/noparam.py, research/decode/combat/noparam-creatures.tsv.
- Level rotation: bots stepped down (L49 to L13) stayed in the zone they stood in; when PickAnywhere then found nothing (it requires
  CanAcceptWithoutGiver and fresh quests, SuggestLevel does not), they swapped levels in place and died to L50 creatures (worlds 426 and
  1061). Now an idle open-world bot whose 60 m surroundings (3+ hostiles) have a median level more than 10 above its own goes to the spawn of
  an open quest in its level window (BotQuests.TryFindLevelSpot, ledger bot-level-zone-move). Engineering rule, not game behaviour.

## Combat resource economy (2026-10-16)
- certain (client decode, WildStar64.exe cast check, caster bits, target bits): Spell4Conditions rows are a
  conditionMask of unit states plus a conditionValue saying for each masked bit whether the state must be on (bit set) or off (clear); bits of the
  value outside the mask are ignored. Bits: 0x001 dead, 0x002 in combat, 0x004 falling, 0x008 moving, 0x010 stealth, 0x020 piloting,
  0x040 embarked, 0x080 taxi, 0x100 swimming, 0x200 jumping, 0x400 sprinting, each with its own CastResult (CasterCannotBeX / CasterMustBeX,
  TargetCannotBeX / TargetMustBeX). 48 rows; the common ones: 1 (0xe1, 50296 spells: caster not dead/piloting/embarked/taxi), 4 (mask 0 = no
  condition, 6507 caster / 5248 target), 15 (0x81, 57327 targets: target not dead, not on a taxi), 43 (0x4e1, 2537: also not sprinting),
  21 (0x2ef, 1115), 14/7 (0xe3: also not in combat, e.g. mounts, 556/319). "Must" rows: 13/16 must be dead (resurrections), 22 must be in
  combat (62), 33 target must be in combat, 24/23 must be swimming (118), 31/29 must be piloting (18), 26 moving, 27/47 sprinting, 42 jumping,
  45 target falling. Implemented (Game/Spell/SpellConditions.cs, Spell.CheckConditions): dead, in combat and stealth are evaluated for the
  caster and the primary target; the other states are not tracked by the server, "cannot" passes (a unit standing on the ground meets it),
  "must" passes and is counted (ledger spell-condition "state not tracked"). User casts and creature casts are refused with the client's
  CastResult; casts started by effects (proxies, procs, passives) only record the failure (likely: the client evaluates conditions in its cast
  check, the server triggers those spells itself). Spell4CCConditions (CC-state masks) are still not evaluated.
- guess: combat state. The server had none. UnitEntity.InCombat is set by hostile damage dealt or taken and while a creature fights the unit,
  and ends 5 s after the last such event (the timeout is a guess; the client keeps the flag at unit + 0x2ac and is told through
  ServerUnitEnteredCombat, which the server now sends). Used by Spell4Conditions bit 0x2, prerequisite type 28 and the Medic Actuator refill.
- certain: prerequisite type 28 = in combat (Equal) / not in combat (NotEqual). Failure texts: row 903 Equal "You must be in combat" (gates
  42148 "[Hidden] Engineer - Add 15 Volatility"), "Combo points are only generated in combat", NotEqual "Cannot use in combat or in PvP
  instances". 155 NotEqual and 71 Equal terms. Implemented (PrerequisiteChecks InCombat). Effect apply prerequisites other than type 143 are
  still not evaluated for spell effects (unchanged), so row 903 only matters where prerequisites are checked.
- certain (packet): the client's ClientCastSpell carries only ClientUniqueId, BagIndex, CasterId and ButtonPressed, no target list or aim
  point. Player telegraphs are therefore built at execute time from the caster's server-side position and rotation (from its movement
  commands). Proxy rows on telegraph targets (TargetFlags 4) of player casts now land on the telegraph's targets; when the telegraph matched
  nobody the proxy lands on the selected target (guess, same rule as the player Damage rows, COMBAT-MATH step 1). A Proxy row's DataBits04
  caps how many matched targets it is applied to (likely: Telekinetic Strike 32893 "Deal damage to $(aoeTc) foes and generate a Psi Point"
  proxies 30900 "+1 Psi Point" with DataBits04 1; 0xFFFFFFFF = all). Bots turn towards their target before a cast (they never rotate by
  moving). Result: Esper 30900 "+1 Psi Point" landed 17-20 times per gate (0 before).
- certain that the tooltip names the generator, likely that it is granted once per completed cast: 118 class abilities name a resource
  generator only in their action bar tooltip ("$(eff=95337.data02rawint) Volatility", effect 95337 = VitalModifier of 42148 "Add 15
  Volatility") with no proxy, proc or cast event reaching it (scripts/research/tooltip_generators.py, research/decode/resources/
  tooltip-generators.tsv). A completed user cast of such an ability casts the generator on the caster (Game/Spell/TooltipGenerators.cs,
  ledger tooltip-generator). How the retail server linked them is not in the data.
- certain: VitalModifier DataBits05 is a share of the vital's maximum (tooltips "data05pct of your maximum Focus"; Medic Recharge 42596,
  Esper Meditate 32818; Stalker stance passives hold -1.0 = empty Suit Power on a stance change). Applied when within -1..1 and the vital has a
  maximum; Focus has none yet (ledger vital-share). The class-named vitals (4, 12, 18, 19, 26) now resolve to the class's resource slot through
  Class.vitalEnumResourceNN, 16 to Dash (before, e.g. Medic Recharge's -4 Actuator was dropped).
- Pools (Game/Spell/InnateCost.Pool): Warrior 1000 Kinetic Energy, starts 0 (certain: wildstaronline-archive Warrior page "maximum value of
  1000KE"); Engineer 100 Volatility, rests at 0 (certain: Engineer page "maximum capacity of one hundred, and a resting state of zero");
  Medic 4 Actuators, start full (certain: Medic page "Actuators start at four charges"); Esper 5 Psi Points (likely: spender tooltips list
  1PP-5PP, Mind Burst 32809); Spellslinger 100 Spell Power, starts full (likely: four circles, Spell Surge "Requires 25 Spell Power");
  Stalker 100 Suit Power, starts full (guess for 100: costs 15-35, Punish "if below 35 Suit Power").
- Regeneration: Spellslinger Spell Power 25 per 12 s in and out of combat (likely: tentonhammer Spellslinger class guide "recharges at the
  same rate in or out of combat and takes around 12 seconds from empty to one full circle"); Medic Actuators refill to 4 out of combat
  (certain that they do, wiki Medic "When you leave combat, your Actuators will quickly restore to a full four charges"; 1 per second is a
  guess). Unknown, not modelled (no table: Class, CreatureLevel and Property only hold ResourceRegenMultiplier0-10 percentages; wiki and
  guides give no rate; patch notes only give ability buffs such as "Suit Power regen to 5 per second"): Kinetic Energy decay in and out of
  combat (wiki: it "gradually decays"), Volatility decay out of combat (wiki: "gradually declines to zero"), base Suit Power regeneration
  (wiki: "regenerates quickly"), Focus pool size and recovery (wiki: "naturally over time; faster out of combat").
- Charging (NF_INNATE_COSTS, now on by default): Resource1 costs (Esper Psi, Engineer Volatility, Medic Actuators) and Spellslinger Spell
  Power (only the Surge variants cost it, vital 19 x 25) are checked and charged; caster innate requirements with eval 2 (at least: Warrior
  Whirlwind 33802 250 KE, Medic Fusion Probe 88576 1 Actuator) are checked; eval 1 (Plasma Wall 38787) is not decoded and passes (ledger
  innate-requirement). Suit Power and Focus costs are counted, not charged: without a base regeneration they would empty for good.
- Engineering rule, not game behaviour: bot rotation. Bots cast from every learned ability (the Ability bag, before only its first 8
  slots, which held no Engineer/Esper builder), class spenders gated by a charged resource first, then other class abilities (Spell4Base
  spellClass 40), then the rest. Before, class auto attacks (26434 "Unarmed Auto Attack", 960 "Psyblade Strike", 40510 "Heavy Shot",
  spellClass 0, no cooldown) took every global cooldown and builders/spenders were almost never cast.

## Real quest objectives instead of synthetic credit (2026-10-16)
Static count: `/cache/quest-coverage.txt` "Synthetic objectives by type"; per objective `/cache/synthetic-objectives.tsv`; table paths per objective `scripts/research/synthwhy.py`.
- EnterZone (certain): the zone update credits the zone and its parents (Player.ZoneAndParents), so a point inside any sub zone that the server's map
  files resolve is a real path. `scripts/research/zonepoints.py` reads the zone ids of every cell of data/map/*.nfmap and writes one point per
  (zone, world) to data/research/zone-points.tsv. File layout: a grid's X is the world Z grid index (MapFile.GetGrid hashes (gridZ << 16) | gridX),
  cells are not swapped; checked against the server: `gt zonecheck 22` 5655 of 6053 WorldLocation2 agree, the script reproduces the same 5655/398.
  WorldLocation2 entries alone missed every zone that has only sub-zone locations (1405 Southern Wilderrun, 1949 Crimson Badlands, 4763 Arcterra).
- CollectItem text sources (certain from the text): "Collect $m(item=81821) from $m(creature=18563)" names the item's source. Killing, activating or
  gathering such a creature gives one item while the objective is open (QuestManager.OnQuestItemSource, `quest-loot`). Iron Chunk 81821 is in no
  researched drop table of Iron Node (they drop Iron Ore 12542), so without this the tutorial mining quests 9031/9058-9060 could not be done.
- Equip objectives (likely): 15 ActivateEntity objectives with no creature read "Equip the $(item=N)" (gathering tool and item tutorials). Equipping
  the item credits them (Inventory.ItemMove -> QuestManager.OnItemEquipped). No table states the trigger; the text is the only evidence.
- Item spells (certain): 24 SpellSuccess objectives name a spell that an item casts (ItemSpecial.Spell4IdOnActivate, e.g. 84594 Gladiator's Weapon
  Enhancer -> 83163 = objective data). Bots use the item through ClientItemUse. The items have no vendor stock or drop source in any data, so
  bots get them as a QA grant (`quest-qa-item-grant`, heuristic kind `item-grant`).
- Creature macros without parentheses (`$creature=33057`, 6 objectives) are read like `$(creature=N)` (certain: same id space, the creature fits the text).
- BOTS_SYNTHETIC=0 used to switch off the drivers too (the check sat at the top of CreditSynthetic); now only the direct credit is skipped.
- Unknown31 (work orders and random dailies), revised 2026-10-17: the client has no description case for type 31, but its quest state handler replaces
  the objective at the quest's random slot with the QuestObjective row named by RandomResultId (ServerQuestInit active entry, ServerQuestStateChange),
  certain. So type 31 is a slot the server fills from a pool; the pools (the objective's data, 38-621) are not in the tables. The members are the
  2593 CraftSchematic/ObtainSchematic objectives that no quest references: they come in blocks of six (one per item slot) with the same delivery
  location, skill, tier and item line, laid out in pool order. scripts/research/workorder_pools.py matches 229 of the 257 pools (195 by text:
  delivery location, profession, title words; 34 by position between matched neighbours; 0 order violations among the text matches) and writes
  data/research/workorder-pools.tsv (likely). The server picks one row per character and quest (fixed hash, so a relog keeps it; retail picked
  at random, assumption), sends it as RandomResultId and counts progress on it; the work order completes like any craft objective (ObtainSchematic:
  craft the item). 28 pools (31 quests, e.g. "Embrace Perfection", "Titanium on My Mind") stay unresolved and synthetic (quest-random-pool-unknown).
- CompleteEvent data of the 14 open live quests does not resolve to a public event that fits (250/251 are PvP path ability events, the text
  describes wave fights; 436/438 objectives belong to event 0): server-only mini events, synthetic.
- EnterArea target groups (type 1) of the 16 open live quests name creatures without spawns; the objectives have no indicator. Synthetic until spawned.

## Leftover decodes: prerequisites, CC conditions, spell modifications, summons, Focus (2026-10-16)
Client findings are from WildStar64.exe, read by static analysis (local scripts, not shipped).
- certain: CCStates.tbl names the crowd control ids, and the server enum was one too low from 19 up. Now 14 DeTaunt (table "Pacify"),
  17 Pushback, 18 Pull, 19 PositionSwitch, 20 Tether, 21 Snare, 22 Interrupt, 23 Daze, 24 Subdue, 25 Grounded, 26 DisableCinematic,
  27 AbilityRestriction. Effect data agrees (CCStateSet 20: Tether Mine/Tether Bolt; 21: Chill, Cripple, Pounce; 22: Kick, Arcane Shock;
  19: Spatial Shift), and so does the client's CC condition check, which returns Caster/Target(Must|Cannot)Be<name> for bit n in that order.
  Only ledger names change (the server stores the raw id); the old "PullToLocation 19, Snare 20 ..." list is superseded.
- certain: Spell4CCConditions (client code, called from the caster and the target checks). ccStateMask selects
  CC states (bit n = CCStates row n). ccStateFlagsRequired says "must be" (bit set) or "cannot be" (bit clear), and the unit's state n is
  its CC count at unit+0x2d8+16n. All 12 rows are "cannot" rows. Row 1 is the ability default (49910 spells: stun, sleep, polymorph, fear,
  knockdown, vulnerability, disable, knockback, pull, position switch, grounded, bits 26/27). Row 4 adds disarm, row 5 adds silence. The
  client skips the caster's "cannot" rules when a cast-request flag (+0x48) is set. The server reads that flag as "cast started by an
  effect" (likely), so such casts only record. Applied in Spell.CheckCCConditions (ledger spell-cc-condition).
- certain: prerequisite 68 (client code). The quest is in the log, value is an objective index below the count, and
  that objective is complete (a completed quest counts as complete). Equal = complete, NotEqual = not complete, other comparisons false.
- certain: prerequisite 43 (client code). The quest is held, objective <value> exists, is not complete and is active:
  type 0 is always active; flag 0x2 needs every earlier objective without flag 0x20 complete; flag 0x1 needs every earlier objective
  complete or active. An index past the objectives is false for Equal. This replaces the earlier bit-mask and quest-state readings.
- certain: prerequisites 16, 27, 39, 61, 102, 117, 141, 194/195, 226, 232, 270 (client evaluators named in PrerequisiteType.cs). 232 and
  270 read client state that this server never sets: plane flags (message 0x0172) and loyalty points. Both are 0, which is what a client of
  this server evaluates. 27 is a server-only check ("You are out of bounds", zone chain like 26: likely). 74, 107, 125 and 155 stay assumed met: no bot reaches them.
- superseded (see "Client spell modification evaluator, challenge and path mission prerequisites" below): the earlier tooltip reading of ModifySpell / Spell4Modification.
- certain for the fields, likely for the count: SpellDispel (53). DataBits03 = Spell4Base.spellClass removed (36 BuffDispellable, 38
  DebuffDispellable). DataBits00/01 = count (tooltips cite both, -1 = all). DataBits05 1-3 = power ("Low/Mid/High Power Cancel"), not
  compared. The newest matching lasting effects are removed. Client buff icons of other casters stay until they expire (casting ids are not tracked).
- certain for spell and creature, likely for radius and delay: SummonTrap (106). DataBits00 = trap creature, DataBits01 = spell the owner
  casts on the first hostile unit inside DataBits04 (float metres), DataBits02 = arming delay in ms (Tether Mine 38860 -> creature 26850 -> 38861 Tether).
- certain for the creature: SummonPet (98). DataBits00 = pet creature, DataBits01 = pet kind (Engineer bots 2/3/7, Esper 4/5, Settler
  turret 9, Stalker clone 11). Engineering choices: one pet per owner and creature; the pet follows its owner and uses only its auto attack on
  the owner's hostile target; lifetime is the effect duration (1 h for duration 0).
- certain (no data): SharedHealthPool (148) has 13 rows, all Medic Barrier tiers, every DataBits 0. Recorded as stored, no damage pooling.
  UnitPropertyConversion (145): the target property gains share x the source property (tooltips of 58994, 82056, 87367), applied.
  Script (13): 86 of 104 rows carry no data, the rest are test spells. Counted.
- certain: Focus recovery uses the client formula (InnateCost.RecoverFocus). The pool is property 5 BaseFocusPool (unit+0x5ec; client
  properties sit at unit+0x5c4+8n). Out of combat the rate is BaseFocusRecoveryOutofCombat (108, 0.011) x pool per second
  (GetFocusRegenNonCombat). In combat it is the rating curve of base 107 and RatingFocusRecovery (GameFormula 1237), applied
  every GameFormula 88 ms (500) as pool x rate x ms / 1000. The wiki Stat page says the same ("percent of your Focus Pool
  ... each second while in combat"). Focus costs are still counted, not charged.
- still unknown: health regeneration. The client has no health regen code or API: GetShieldRegenPct and the focus functions exist, nothing
  for health, and property 8 is only a multiplier. Suit Power, Kinetic Energy and Volatility decay rates have no table, client function or
  wiki number (the Suit Power page says only "regenerates quickly").
- certain (rows): Warrior Onslaught passive 44926. DamageDealtMultiplier 141-143 are x1.0 (no change). VitalModifier vital 6 is -1200
  when the stance applies, which empties the 1000 Kinetic Energy pool unless spell 59092 is active (prerequisite 24604). SpellForceRemove
  ends Overdrive 42908 (x1.2 for 8 s, cast by the stance activation). No hidden damage formula.
- unchanged guess: 13353 damage/heal rows (12179 spells) have neither parameters nor a designer amount. The client formula
  gives 0 for them, so no client formula exists. The server keeps DataBits00 x assault power. Creatures affected:
  40 names, 37 spawned, 364 spawn rows (mostly old unarmed and rifle auto-attack rows); 16 abilities are fully on the guess.
- superseded (see "Client spell modification evaluator, challenge and path mission prerequisites" below): the earlier reading that
  prerequisites 69, 74 and 125 (challenge record fields) and 107/155 (path mission state) could not be decoded from the client alone.

## Client spell modification evaluator, challenge and path mission prerequisites, regeneration, pets (2026-10-16)
Read from the client executable (local copy, read only). Confidence per line.
- certain: ModifySpell (55) is applied by the client to its own copy of each of the player's spells. When the effect starts, the rows of
  the effect (Spell4Modification.spell4EffectsId = effect row id) are added to every spell of the base (DataBits00) or spell group
  (DataBits01); when it ends they are removed. The client then rebuilds the modified spell copy that its cast bar, cooldown display and
  range checks read. Rows are kept per spell sorted by parameter and priority; within one parameter and priority the clamp rows (type 2)
  come after the others. The base value is the unmodified spell value.
- certain: parameter = which spell value: 0 castTime, 1 targetMinRange, 2 targetMaxRange, 3 spellCoolDown, 4 casterInnateRequirementEval0,
  5 casterInnateRequirementValue0, 6 casterInnateRequirementValue1, 7 targetBeginInnateRequirementEval, 8 innateCost0, 9 innateCost1,
  10 channelInitialDelay, 11 channelMaxTime, 12 channelPulseTime, 13 threat (the client spell has no such value and reads 0), 14 the spell
  flags, 15 Spell4Base mechanicAggressionMagnitude, 16 mechanicDominationMagnitude, 17 spell4ConditionsIdCaster, 18 spell4ConditionsIdTarget,
  19 casterInnateRequirementEval1, 20 targetBeginInnateRequirementValue, 21 the aoe target constraints value. Rows in the table use
  0, 2, 3, 5, 6, 8, 9, 11, 12, 13, 17.
- certain: type (data00..03 = d0..d3). 0: change = (d0 - 1) x base + d1; with d3 = 0 the change is a raise that stops at d2 (the value
  never goes above d2 through it), with d3 != 0 a cut that stops at d2. 1: change = (d0 - 1) x current value; d1 is the stop value, d2
  the direction flag. 2: clamp, d1 = 0 at most d0, d1 != 0 at least d0. 3: set to d0. 4: bit flags d0, cleared when d1 = 0, set otherwise.
  So data02/data03 are the stop value and direction (Neutralize +5 cost up to 1000; cooldown -10000 down to 1500 (row 17); Efficiency x0.98
  down to 1).
- certain: combination. Parameters 0-3, 5, 6, 8-13, 15, 16, 20, 21 add up: for each priority the changes of its rows are summed (each
  measured against the value before that priority), a clamp row of that priority clamps the summed change, and the sum is added.
  14, 17 and 18 apply row after row. 4, 7 and 19 use only the first row. Consequence: a lone clamp row clamps a change of 0 and does
  nothing (Requiem/Combustion "cost 0" rows 97-99, Impulse "cooldown 3000" rows 87/88, cast time row 353); an "at least" clamp adds
  (rows 496/497: cooldown at least +5 s). The tooltip text that prints these numbers is formatting only.
- likely: the server evaluates the same way (the client copy has to agree with the server for cast bars and cooldowns). Applied on the
  server: cast time, cooldown, innate costs 0 and 1 (before, cost 1 used parameter 8), caster innate requirement amounts (rows 536/537
  need no Kinetic Energy), caster/target Spell4Conditions rows (row 388). Range, channel timing, threat, magnitudes and flags have no
  server use and are counted.
- certain: prerequisite argument order. The client passes object and value to each evaluator; types 68, 69, 74, 107, 125 read the
  object as the record id, 155 reads the object as the mission and compares the value (rows: object 230/36/1462 = missions, value 0-4).
- certain: 69 = challenge <object> is running (Lua IsActivated), 125 = challenge <object> is in its restart cooldown (Lua IsInCooldown),
  74 = challenge <object> is unlocked (the flag the client needs before it shows the challenge's map regions and percent progress; the
  server's challenge event "unlocked" sets it). Equal = set, NotEqual = clear, other comparisons false. The rows are used by Creature2
  visibility, animation state and activate-spell prerequisites (start objects usable while a challenge is still locked).
  Server: running = the challenge manager has it active (certain); no cooldown exists on this server, so never in cooldown (certain for
  this server); unlocked = started at least once this session (likely: the client gets the unlock from the server when a challenge
  first triggers).
- certain: 107 = for a player unit Equal is false and NotEqual true; for creatures the bit of the creature's object index in the
  mission's state bits. 155 = the client's mission state number of PathMission <object> compared with value: 0 no mission record,
  4 complete, otherwise by type: holdout 3 while running else 2; scan and scan checklist 3 with bit 0 of the user data else 1;
  assassinate, demolition, rescue, SWAT 2 | bit 0; scavenger hunt 3 with bit 30 else 2; power map 3 while running else 2; others 3.
  Implemented in PathMissionManager.ClientState from the state and user data the server sends.
- certain (none found): regeneration and decay. The client changes no vital over time except Focus (decoded before) and has Lua getters
  for the shield values (ShieldRegenPct 176, ShieldTickTime 177, ShieldRebootTime 178), which the server already applies. The unit
  property names give HealthRegenMultiplier 8, ResourceRegenMultiplier_0-6 16-22 (default 0), _7 39, _8 51, _9 53, _10 55,
  SpellMechanicEnergyRegenOrDecayMultiplier 117 and SpellMechanicEnergyDecayOverdriveMultiplier 118 (default 1), BreathDecay 125
  (default 0.8). No client code reads 8, 16-22 or 117/118 for a vital; they are server multipliers on base rates that are not in the
  client or the tables. From the spell rows (likely): 16 is the Endurance/sprint regeneration ("Increase Energy Regen", "Sprint
  Regeneration"), 39 the Dash regeneration ("Dash Regen"), 117 the Warrior Kinetic Energy decay ("Energy Decay -5%", "Can't Stop, Won't
  Stop" -20%/-50%). Base rates stay unknown; nothing applied. GameFormula: of the client's formula reads, only Focus (88, 1237) and a dash
  spell id (508) touch vitals; 823 is housing decay.
- certain (none found): parameterless damage/heal rows. The client's effect tooltip value and the spell damage preview (asdmg/admg/sdmg)
  both come from the damage formula, which gives 0 without parameters; the effect token keys (data, input, tickTimeMS, durationMS,
  delayMS, threat, pct...) print raw fields only where a tooltip asks. Of 23047 effect tokens in the tooltip texts, one cites a
  parameterless damage row (key input0). The DataBits00 x assault power guess stays.
- certain (no client logic): SharedHealthPool (148) and SummonPet (98) have no client effect object (the client builds effect objects
  for 22 other types); the name appears only in the effect name list. SharedHealthPool stays stored. Pets on the client: a stance per pet
  set by the server (ServerChangePetStance), a stance change request (client message 0x068E: pet unit id and stance flag 1/2/4/8/16,
  no server handler yet), a pet command bar and a dismiss command.
- certain (rows): PetCastSpell (111, 191 rows, had no server handler): DataBits00 = the summon spell whose pet acts, DataBits01 = the
  spell that pet casts (Repair Bot Shield Boost 35657 -> summon 42810 -> pet spell 70640; Artillery Bot Barrage 35123 -> 42814 -> 49502).
  Spell4.spell4IdPetSwitch (43 summon spells) names the pet ability the summon's button turns into while the pet lives (Bruiser Bot ->
  Blitz 35501). Server: casting the summon ability with its pet alive casts the switch spell (the client still sends the button's bag
  index; likely), PetCastSpell makes the pet cast at the owner's target. DataBits03-05 of PetCastSpell are not decoded.


## Character saves: quest objective batches (2026-10-17; Game/Entity/CharacterSave.cs, Player.SaveCharacterSerialised, QuestManager.Save, CharacterContext.KeepKeyValues)
Symptom: soak failure reports since 2026-10-07 showed `db-save-failed` "character: DbUpdateException ... FK__character_quest_objective_id__character_id" (8896 in the 36 h soak, 60 bots) and "DbUpdateConcurrencyException ... CharacterQuestObjectiveModel:Modified" (3171), plus 165 "InvalidOperationException ... CharacterQuestObjectiveModel cannot be tracked". The ledger key was only the exception type, so the DbUpdateException count held every kind of DbUpdateException (foreign key, duplicate key), with the first message as the example.
Reproduction: `scripts/save-stress.sh` (gt queststress: random accept, re-accept, abandon, achieve, complete, repeat, mention and objective credit on a fixed mix of 42 repeatable, communicator, timed and plain quests, saves after every call, a third of the bots relogging every 40 calls; then gt questverify compares every bot's quests with its rows). With the old save path, three 5-6 min runs with 30 bots gave 90, 6 and 3 failed batches: 73/1/1 concurrency, 7/1/0 duplicate key, 5/2/1 foreign key, 5/2/1 duplicate tracking; gt questverify found 72 and 5 rows that differ from the game.
Causes, in the order they chain (certain, each seen in /cache/character-save.log with the quest's transitions):
1. Seed: key columns are mapped with `HasDefaultValue(0)` (objective `index`, also other composite keys), which EF treats as generated on insert. A new objective with index 0 gets a temporary key value from one counter shared by all contexts instead of 0. When that counter reaches a real index of the same quest, the add of that objective throws "another instance with the same key value is already being tracked" (logged: quest 9586, objective 3 added while index 0 was tracked under the temporary value 3). About (objectives - 1) / 256 of new multi-objective quests hit it.
2. The throw happens while the batch is being built: the quest row and the objectives before the failing one had their save flags cleared, the rest kept "create". The whole character batch (character, amps, currency, mail, quests: one transaction) is lost.
3. Next save: the remaining objectives are inserted without their quest row -> foreign key failure; that batch is lost too.
4. Every insert and delete lost in 2-3 is believed written: later updates of those rows affect 0 rows (concurrency), a later delete affects 0 rows, a later insert of a row that is still there fails (duplicate key). Each failure loses the batch again, so a character snowballs until it relogs.
5. Separate source of duplicate keys: rows with state Abandoned (6) were not loaded into any quest list, so the game did not know the quest and inserted it again on the next accept (verify: "quest row the game does not hold (state 6)").
Fixes:
- CharacterContext.KeepKeyValues: primary key properties that are "generated on add" only because of a column default are set to never generated, so EF sends the real value (0 included). The columns keep their default. After this change the same stress test with the old save path gave 0 failures (verify: 0 differences), so this was the seed.
- Quests are saved in their own batch (own context and transaction) after the rest of the character, so a bad quest row can no longer lose the character, amps, currency or mail rows. Both batches are built on the world thread before waiting for an earlier save of the same character (before, a waiting save built its batch on a pool thread while the world kept changing the quests).
- A failed quest batch requests a resync: the next save loads the character's quest rows and writes the game state (insert missing, update changed, delete rows the game no longer holds), instead of the change flags. A quest build that throws drops its half-built batch and resyncs too.
- QuestManager.Save saves each quest id once per pass (active list first) and records `quest-in-two-lists` if a quest id is held twice; an Abandoned/Unknown row at login is held as a pending delete and removed at the next save (`quest-row-stale-state`).
- Relog: the logout cleanup now releases the account only when its save has finished (it was released as soon as the save started), a select while a save of that character is in flight is refused (the bot retries), and a character list read before the last save of the character is read again before entering (`character-stale-load`).
Ledger: `character-save-failed` "<cause> <part>", cause fk, concurrency, duplicate-key, duplicate-tracking or other, part character, quests, quest-resync or build; `character-save-cause` gives the row-level reason with the quest's transitions; `character-save-resync` counts repairs; ordered details in /cache/character-save.log. scripts/gate.sh writes /tmp/gate-verdict.txt and scripts/status.sh shows "character saves: PASS/FAIL"; 0 is required.
Not changed: the main character batch (everything but quests) is still one transaction whose flags are cleared before the write; a failure there still loses its rows until they change again. Other key columns with HasDefaultValue in the auth and world contexts were not checked.
Verification (2026-10-17, 30 bots): save stress with the fixed code and overlapping saves plus relogs, 6 min: 0 character-save-failed, questverify 0 differences over 30 bots; the old save path with only the key fix: 0 as well. gt questresync after rows were deleted and added behind the game (one quest row, one quest's objectives, one unknown row): resync inserted 1, deleted 1, restored the objectives, questverify 0 differences. Three 10-minute gates: 0 map/spell update exceptions, 0 character-save-failed, maps about 32 000-33 000 ticks, CPU 131 %, 121 %, 120 %; combat-check 51 ok, 0 differ. Restart in the middle of quests (`docker compose restart world`): 30 of 30 bots back, 315 of 316 active quests kept, 26 279 quest rows equal to the game; the one missing had been completed 15 s before the restart, after the last 60 s save, and the bot completed it again (a restart loses what changed since the last periodic save; there is no save at shutdown).

## Shutdown saves, regeneration defaults, hazards and small systems (2026-10-17)
- Shutdown save (certain, measured): SIGTERM/Ctrl-C saves every online character on the world thread and waits up to NF_SHUTDOWN_SAVE_SECONDS
  (15 s); the world service has stop_grace_period 30 s. scripts/save-restart-test.sh restart: an item and an objective step gained 5 s before
  `docker compose stop` are in the database after the restart (3/3; 30 characters saved in about 0.1 s); with the save off they are lost.
- Character batch failures: a failed batch is written again one row per transaction (two passes); only rows that still fail are dropped and named
  in character-save-row-dropped. Tested with an injected foreign-key row (save-restart-test.sh fault): 4 rows written, 1 dropped.
- Key columns: the auth (6 models) and world (9 models) contexts map composite keys with HasDefaultValue like the character context did; no row
  with a 0 key component exists today, KeepKeyValues is applied to all three contexts anyway (no change for non-zero keys).
- Regeneration and decay (design values, not decoded): the client has no base rates and no table holds one. Defaults: health 5 % of max per
  second out of combat, 0 in combat; suit power +5/s; kinetic energy -10/s in combat, -100/s out of combat; volatility -10/s out of combat.
  Directions from the wiki class pages (Warrior, Engineer, Suit Power); scaled by HealthRegenMultiplier (property 8), ResourceRegenMultiplier3
  and SpellMechanicEnergyRegenOrDecayMultiplier (117) (likely). Env NF_REGEN_* / NF_DECAY_* (docs/CONFIGURATION.md).
- Pet stance (certain layout): ClientPetStanceChange 0x068E {unit 32, stance 5} -> ServerChangePetStance 0x068F, stance flags 1/2/4/8/16 stored
  per pet; what each flag does to the pet AI is not decoded (stored, not applied).

### Hazards
- Fall damage (no source for the amount): the client shows it when ServerUpdateHealth 0x092F carries mask 0x40 (DamageOrHealingDone, type 6)
  and has a CombatLogFallingDamage event {nDamageAmount, bTargetKilled}; Property 134 FallingDamageMultiplier and SlowFallMultiplier exist, and
  spell conditions Caster/Target_MustBe/CannotBe_Falling. No table or client code holds the height-to-damage curve, the client sends no fall
  report (only positions and the undecoded movement state), and the server sends no combat log at all. Not implemented (off), bots take none.
- Breath / drowning (no source for the rate): ServerUpdateHealth mask 0x200 (Suffocate, type 7 and Breath_FlashEvent with health/max), a
  BreathChanged event on a unit field, ShouldShowBreathBar, Property 125 BreathDecay. No breath rate, no water volumes on the server. Not
  implemented (off).
- Hazard meters (Hazard.tbl, 228 rows; effects 86 HazardEnable / 87 HazardModify / 88 HazardSuspend, 89 effect rows): hazardTypeEnum from the
  client's Lua enum (HazardType_Breath 0, Radiation 1, Temperature 2, Proximity 3, Timer 4: likely, listed in reverse in the client and the row
  names agree: "Hazmat Battery Charge" 0, "Radiation" 1, "Blistering Heat" 2, "Game Huntress ... Proximity" 3, "Timer" 4). Ids certain:
  HazardEnable DataBits00, HazardModify DataBits04 (49/49 rows, amount DataBits03 float: "Heat Dissipation" -3 per 500 ms tick, "Warming Up"
  +3 per s), HazardSuspend DataBits00. meterChangeRate is per second (likely): timer hazards 91 (max 105, rate -1) and 99 (max 150) are enabled
  by spells lasting 105 000 and 150 000 ms. Server message 0x0109 {hazard 14, action 3} (client handler: 0 HazardEnabled + minimap unit,
  1 HazardRemoved, 3 remove all; certain). The meter value message, when Spell4IdDamage and the threshold proc spells fire and the flags are
  not decoded, and no hazard spell was cast in any gate, so the effects are recorded (effect-stored "Hazard* recorded") and nothing is applied.

### Small systems
- Challenges with a time limit: Challenge.challengeFlags bit 8 (the client's Challenge:IsTimeTiered tests it; 80 of 643 rows) marks tiers that
  are milliseconds on the clock. Before, "a tier above 1000" took 3 untimed rows for timed and missed 11 timed ones.
- Holdout waves: PathSoldierEvent.initialSpawnTime delays the first wave (2-5 s on 203 of 356 rows, 0 on 56; certain field) and
  maxTimeBetweenWaves (30-60 s on most rows) brings the next wave while the current one still stands (field certain, meaning likely; ledger
  path-holdout-wave-overlap). Holdouts that found no enemy are not started again (path-holdout-no-spawn was 120 per gate for mission 1852).
- Imbuement reward types 4/5/6 (certain from the client's item builder, which sums ItemImbuementReward.RewardValue of the finished steps per
  type): 4 adds quality steps to the item (result clamped to 2-7, the ItemQuality row then gives DefaultRunes/MaxRunes); 5 adds item levels to
  the level the stat budget is computed from (the field is the item level: likely); 6 adds rune slots to DefaultRunes and MaxRunes (8 at most).
  Applied: stats from PowerLevel + type 5 sum (ItemStats.For(item, bonus)); rune slots from the shifted quality + type 6 sum (RuneManager).
  Quality is not used by the stat formula here, so type 4 only changes rune slots.
- Transports without a dock unit: the unit's own activate spell names the arrival where it teleports. Creature2.spell4IdActivate with a
  Teleport effect (DataBits00 = WorldLocation2) is used before the name rule: "Ship to Walker's Landing" 43681 -> Farside Walker's Landing,
  "Ship to Virtue's Landing" 43560 -> Virtue's Landing, "Ship to Thayd" 44078 -> Spaceport Horizon, "Ship to Illium" 44079 -> Spaceport Alpha,
  Sovereign's Landing, Touchdown Site Bravo (certain: table link). Still without a destination (their activate spell is a bare Activate
  effect run by a server script): Arcterra (2), Palaver Point (2), Galactic Observer (2), Star-Comm Basin (2), Genetic Archives access panes (2, the instance name rule may still take them to the raid), Travel to
  Skullcano, Test Raid, Halon Ring (4, cut content).
- Mentoring (not built): client sends 0x0439 CloseMentoringAOIDialog, 0x043B CancelMentoring, 0x043E AcceptMentoring; the server's 0x043A fires
  Group_MentorLeftAOI; Group_Mentor and Group_MentorRelationship come from the group member update, whose GroupCharacter already carries
  EffectiveLevel (7 bits) and a mentor target (realm 14 + character u64). Missing: how stats scale to the effective level (no table maps gear or
  base stats to a lower level), so a mentored character would keep its full stats. Left unbuilt rather than half-scaled.
- Account friends (not built): 0x03BD and the FriendshipAccount* events need account-level relations on the auth server; every bot has one
  character per account, so a bot test could not tell account friends from character friends.

## Instances, groups and encounters (2026-10-17)
Lifecycle of an instance and what each step rests on. Confidence: certain = client message layout or table field read directly; likely = a
documented source or a consistent table pattern; default = chosen where nothing says, counted in the ledger.
- Entry, solo and group (certain, existing): an instanced world (World.type 1, 6, 9, 11) holds one instance per group (instance id = group id with
  bit 62 set) or per character without a group (DungeonInstancedMap.InstanceIdFor). Entry paths a bot uses: the open world entrance portal unit
  (Creature2.instancePortalId; destination by name, likely, see "Instance portals"; 19 instanced worlds have a spawned entrance portal, ledger
  `dungeon-portals`) through ClientActivateUnit, the group finder, or a plain teleport.
- Group finder to instance (layouts certain, flow default): a match (solo queue, filled premade, full premade after its role check) becomes a
  pending game (Game/Groups/MatchingGames.cs). ClientMatchingMatchReadyResponse 0x05C8 is one bit, 1 = accept; the client sends it from its
  pending-game popup and raises MatchingCancelPendingGame on a decline (certain: client sender). TransferIntoMatch 0x0606 and LeaveGame 0x05DA
  have no body (certain: client writers). Defaults: 60 s to answer; a decline or a timeout cancels the game, the group stays and nobody is queued
  again; all accepted = every member is moved to the entrance of the group's instance; LeaveGame = back to the remembered entry position and out
  of the group. The server message that opens the popup on a real client is not decoded and not sent (`matching-pending-popup-unsent`).
- Leaving the group inside its instance (likely): the player gets ServerPendingWorldRemoval (reason GroupMembership) and is moved out after the
  GameFormula 1123 timer (dataint0 30000 ms; the upstream comment says the client defaults to 30 s too); LeavePendingRemovalInstance 0x057C leaves
  at once; joining the group again cancels it. Before this nothing started a removal (only an admin command did).
- Return position: the open world position before entering. A character that logs in inside an instance (no entry seen) used to be "returned"
  into the same instance, which created a new instance on every unload (8 such characters at one start). Now: the world's open world entrance
  portal (`instance-return`), else the old behaviour (`instance-return-unknown`).
- Boss kill progress: each instance keeps the spawn ids of its killed encounter bosses (`instance-boss-progress`). No client message for
  progress is known.
- Raid progress across instances (likely + default): "killing any boss locks every member" (wildstar.fandom.com/wiki/Raid). Default: bosses an
  owner's raid instance killed are not spawned again in that owner's raid instances of the same world until the weekly reset (Wednesday
  10:00 UTC); owner = the group leader when the instance is created, or the lone character (`raid-progress-saved`, `raid-lockout-boss-skipped`).
  NF_RAID_SAVED_INSTANCES=0 turns it off. The loot gate per character and boss stays as before. Kept in memory (lost at restart).
- Dungeon lockout: none (wiki: dungeons have no lockout); the daily dungeon reward amount stays an assumption (see "Group loot, lockouts").
- Instance release: an empty instance unloads 120 s after the last player left. The failure report's third line counts live instances per type,
  created and released, with the mean lifetime (Game/Map/InstanceStats.cs, `instance-released`); a run ends with the live count back at its
  start value.
- Bug fixed: one refused player in the instance add queue (instance full or limit) ended the queue pass with `return`, which dropped the players
  that were waiting for an unloading instance of the same world; it now continues with the next one.
- Encounter spell lists from the tables (certain link): a Creature2 row of Creature2Difficulty rank Prime (rankValue 2) for a group or raid
  (groupValue 5, 20, 40; the titles "Rank: Group Prime", "Rank: Raid Prime (20 Man)" say so) whose designer description is the named boss of its
  encounter ("[SK] e323 - Bosun Octog - Veteran": tag, encounter number, boss, at most a difficulty word) gets the Spell4 rows with the same tag,
  encounter number and boss name as its spell list when no researched casts exist (`encounter-spells-from-tags`). Units of an encounter that are
  not its boss ("[SK] e329 - Molten Chasm - Magma Wave- Veteran", the same Group Prime rank) are left out. Helper and other-difficulty rows are
  filtered as before (DesignerTag.IsHelper, FitsMode).
- Approximations (labelled, not from any table): (1) generated encounters: three phases at 100/60/30 % health, every listed spell on a timer
  (first after 6 s + 5 s per spell, then every 20 s + 2 s per spell, 20 % and 40 % faster in phases 2 and 3), targets tank / tank / random by
  turns; chosen so that every spell of the list is cast in a normal fight length, nothing in the tables orders them. (2) Enrage after the file's
  enrage time (generated: 600 s): damage +50 % every 20 s, up to 10 stacks; chosen as a soft wipe timer, not the original's. (3) Guide and boss mod
  files: ability names checked against Spell4 (docs/ENCOUNTER-SOURCES.md), timers "guess" unless two sources agree. (4) Leash 120 m (boss) / 45 m.
  (5) Add waves from guide text. None of these is in the client data; the original scripts ran on the server.
- Bot teams (harness): groups are formed by invites through the client handlers (same faction; invites refuse other factions with
  WrongFaction, so a team waits 90 s for a same-faction set, then takes server members for the rest), teams above 5 are converted to a raid by
  the leader (ClientGroupFlagsUpdate, raid of up to 20), above 20 the rest are server members. Entry by NF_BOTS_DUNGEON_ENTRY (portal / finder /
  teleport / auto by turns). Leaving: ClientGroupLeave inside the instance, then 0x057C; after a matched game LeaveGame. Getting to a portal is a
  teleport. Dungeon bots also take the instance's quests with activate, talk and interaction objectives and use those objects between fights.
- Unplayable instanced worlds (no spawn rows in the world database, the community WorldDatabase or any researched source): 3032 and 3524
  RedMoonTerror (79 Raid Prime RMT rows and RaidCore timers exist, but no positions), 3041 UltimateProtogamesRaid, 3042 Datascape (the second
  Datascape world), 3009 HalloftheHundred. No quest needs them (0 of the 55 instance quests).
- Instance quests for bot teams (harness, likely): the quests handed to a team are the ones with a kill, talk, activate or interaction
  objective on a creature the instance spawns; objectives without a creature in data use the creature their text names, else the quest
  bots' indicator / reward pane resolution (those completions are counted as heuristic, not real). Taken after entering, an EnterZone
  objective on a zone the player is already in is credited at once (same rule as the quest bots' TryVisitZone; nothing in the tables says
  whether the original server checked it on accept). A quest completed inside the instance offers its follow-ups there (Quest2
  prerequisite quests), as a player takes the next step. NF_BOTS_TEAM_LEVEL=quest takes the levels of the never completed instance quests by
  turns, so quests the boss level refuses are taken (the acceptance rules are not bypassed: faction, level, prerequisites, daily rotation).
- Instance quests that stay open, with the reason (data or rule, not a missing driver): 9849, 9928, 10602 and 9927 are rotating dailies
  (PeriodicQuestGroup) not drawn on the test days, or of the other faction; 4514/4516 (1138) and 9850 are Dominion only and need a Dominion
  team; 7030 needs PublicEvent 236 objective 916 active in world 1629 while its targets stand in 2980; 9813 needs 9767, which has no
  objective target anywhere; 3184 needs creatures 9801/9802 that are only spawned in world 790, which has no map; 9299 credits three
  scripted "complete these quests" steps (data 0, no creature or quest id in the tables); 10130's second step ("escape ... return to the
  Galactic Observer") is a location step without a creature or trigger in any table.
- Measured (instance runs 2026-10-17/18, immortal and mortal, 5-bot teams and a 20-bot raid team): of the 55 quests that need an objective
  in an instanced world, 44 were completed at least once (37 before), 14 of them seen completing with real objectives in these runs
  (6837, 8203, 9207, 9305, 9363, 9428, 9734, 9742, 9743, 9798, 10114, 10464, 10600, 10601), 10469 "Daemon Hunter" through the reward pane
  heuristic, 29 by earlier team runs (the same bots refuse them now as already done, so the mode was not measured again); 11 stay open for
  the reasons above.

## Clean-bot quest verification (2026-10-18; Diagnostics/QuestTruth.cs, BotManager.CleanMode, docs/TESTING.md)

- Zone 12 quests are developer and beta quests (likely). Evidence: WorldZone 12 has AllowAccess 0 and an empty name (text 7737); its 128 quests
  include "Level 15" ("Become Level 15"), "[Beta] Weekly Costume Piece!", "MC04 - Domina Hangar 1", "Ready Your Character for the Malgrave
  Trail" ("Receive items and be leveled up for the Malgrave Trail adventure"), "This Quest Has What Raiders Crave" ("Levels, Cash, and Zone to
  the Beta Raid Set-up Room!", RewardCashOverride 8675309), "Help is needed in Whitevale 4!"; all 12 quests of Quest2 with RewardXpOverride of
  1 million or more are in zone 12. A level 3 clean bot that took 7234 was level 50 after the turn-in, one with 6867 went 3 -> 28.
  Applied only to clean bots (they never pick them, completions are tagged dev-zone, the character keeps dev-zone-xp); regular bots still may.
  Some zone 12 quests could be real (80 have objectives, e.g. 9879 "Combat Training"); not checked one by one.
- Character creation start Arkship (0) has no start location in character_create (rows exist for 3 Nexus, 4 PreTutorial = world 3460,
  5 Level50), so creating an Arkship character threw "Value cannot be null (startingLocation)" (certain, 20 bot-failed in the first clean run).
  Clean bots use PreTutorial for the tutorial start. Regular bots try Nexus first and were not affected.
- Classification rules (harness definitions, not game semantics): a grant counts against every quest in the log at the time (over-counts,
  never under-counts); tradeskill grants follow the skill to later craft-tier quests; harness level changes and dev-zone XP follow the
  character. "Verified" = a clean completion with every objective credited by its own game event, no grant, and a turn-in path a player has.
- turn_in uses the server's own QuestComplete rules (receiver in view, IsCommunicatorReceived, imbuement/contract exemption, auto-complete).
  "receiver-not-near" on a quest with objectives is shown but not counted against it, because bots never walk to receivers (harness limit);
  on a quest without objectives it is the whole quest (remote-turn-in). "no-receiver": no player path to turn it in (no-turn-in-path).
- Clean characters that completed a zone 12 quest (16 of the first 39, before the skip existed) or had their level set by the harness are
  retired: clean runs no longer log them in (BotManager.Initialise, read from the database), new clean accounts replace them. Nothing is deleted.
- FileCache (game-table cache) deletes every file directly in /cache when the NexusForever.Shared module version changes (certain, FileCache.cs
  CheckAndCleanupCache; seen 2026-10-18 after a Database.Character change). Clean-bot files are in /cache/truth/; quest-outcomes.txt and
  bot-feed.log stay where they are and are lost on such rebuilds (pre-existing, not changed).
- Non-clean drift in the 36 h clean soak (soak/20261019-0021, started 2026-10-19 00:21 local = 22:21Z; certain): achievements +2,
  action_set_amp +15, character_quest +1, objectives -4, item -3, character_crc changed. Cause: the "before" db-counts was taken while the
  previous NORMAL world (30 regular bots) was still running; soak.sh's `docker compose up -d` recreated the world at 22:21:25Z and the logout
  save of those 30 bots wrote their unsaved state after the count. Evidence: exactly the 30 non-clean characters with lastOnline
  2026-10-18 22:21:25 (Player.SaveCharacter sets LastOnline on EVERY save, periodic and logout), no non-clean character with a later
  lastOnline (checked 2026-10-22 with the clean world still running), latest non-clean achievement dateCompleted 22:21:01Z, no non-clean
  character name in the world log from 22:21:27Z to the soak end (328k lines, 3 world starts: 22:21Z, 21:26Z, 00:26Z), and
  BotManager.Initialise skips every non-clean account when CleanMode is set (companions refused). Clean mode wrote nothing to other
  characters; the restarts saved none. Fix is in the measurement: clean-run.sh start and the TESTING.md soak recipe stop the world before
  counting. Nothing in the database was touched.

## Runaway summons, respawns, Vital prerequisites (2026-10-22; docs/PERFORMANCE.md "Out of memory in world 1061")

- Summoned units do not cast researched abilities that summon their own creature id (certain that the chain doubles without it: Grimwort
  proxy 52647 + Living Grounds 55076/57840 ran world 1061 out of memory four times). Whether the real proxy cast anything at all is unknown;
  permanent spawns of 52647 keep Living Grounds (the quest 8994 fight).
- Only creatures with a world database row respawn (certain for spell summons, which the client removes for good; likely for event units,
  holdout waves and encounter adds, which the event or encounter spawns again itself).
- Spell summon caps (10 per cast, 400 live per map, none while 5000+ grid actions are queued) are server safety limits, not game rules
  (guess for the values; the largest summon count in one effect is 5). Refusals are counted (`summon-refused`).
- Map action budget: 100 per update as configured, up to backlog/20 (max 2000) while a backlog waits. Server housekeeping, no game rule.
- Telegraph targets still include friends and the caster's own kind: TelegraphDamage.TargetTypeFlags (1 or 2 on the rows checked, 1 on
  Living Grounds and Warrior Grapple, 2 on Esper Restraint, all three hostile-only in play) is not decoded, so no filter was added.
- Vital prerequisites (type 73): the current amount of the vital ObjectId (Vital table id) compared with Value, not a percent (likely: 252
  terms, bands like >= 100 and < 199 on Resource1-4 and Focus for cast levels; "out of Vitalus Treatments" is 3 > 0; type 71 is the percent
  check). Unreadable vital ids (0, 2, 20, 21) fail the check and are counted per id (`prerequisite-vital-unknown`) instead of a log line per call.
- AoE target count (Spell4AoeTargetConstraints.TargetCount): telegraph targets of a spell are capped at the row's TargetCount
  (certain for the field: tooltips use the macro $(aoeTc), Telekinetic Strike "Deal damage to $(aoeTc) foes" has row 345 TargetCount 5;
  all 690 rows are 1-40: 1 x120, 5 x119, 10 x121, 3 x99, 2 x54, 4 x37, 20 x31, 40 x21). Which units are kept is TargetSelection (1 x457,
  3 x132, 5 x43, 2 x41, 4 x17), not decoded: the nearest to the caster are kept (guess). Before, every unit in the shape was hit; Esper
  Mending Banner 33345 (row 735, count 1, selection 5) proxied five heals onto ~100 units per cast and filled the caster's 400 pending spells.
