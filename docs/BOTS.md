# Bots

What the bots are, what they do, and what their numbers mean. Settings that switch the roles on are in [CONFIGURATION.md](CONFIGURATION.md) ("Combat and bots", "Bot roles"); code layout is in [ARCHITECTURE.md](ARCHITECTURE.md); how the runs are judged is in [TESTING.md](TESTING.md).

The bots exist to test the server. They are a harness: every quest, dungeon, system and combat path they can drive produces counters in the failure ledger, and the counters show what is broken or missing. They are not a model of a good player (see "Harness limits").

## What a bot is

A bot is a normal character on a normal account (every bot has its own hidden account, created on demand) whose connection has no socket. `Bots/BotSession.cs` is a `WorldSession` subclass that hands everything the server would send to a client to the bot's code instead of a network connection, and keeps the heartbeat alive. The bot then acts by calling the same handlers a client message would reach (`CharacterHandler` for character list, create and select; `InstanceHandler` for instance settings; `ResurrectionHandler`; and so on) or, where no client message exists in this repository, the managers those handlers call. Characters persist in the database; lowering the bot count only logs bots out.

Two kinds exist (`Bots/BotManager.cs`):

- **Companion bots** (`bot add <name>` in the console, up to `BOTS_MAX`) belong to a player. They accept group invites from a group their owner is in, follow and assist the owner. `bot mode` switches follow / stay / passive; `bot summon` teleports them to the owner. They are restored after a restart.
- **Ambient bots** (`BOTS_TARGET`) are a population kept at a target size. Everything below is about them. A new ambient bot is created with random race, class and appearance, then set to a random level from 5 to 50 on its first think (`Bot.ThinkAmbient`).

Bots log in at about four per second after the world is up (`BotManager.SpawnInterval`).

### The think loop

`BotManager.Update` runs from the world update and updates every bot. A bot is a small state machine (`Starting` > `ListingCharacters` > `CreatePending` or `SelectPending` > `WaitingForWorld` > `InWorld`, or `Failed`). Once in the world it thinks every 0.5 s (`ThinkInterval`). An exception in a bot is caught, counted as `bot-failed` and the bot stops; it does not affect the world.

One think (`Bot.Think`):

1. Write level, world and death changes to the bot feed.
2. If dead (with combat on): wait `BotResurrectTime` (10 s), then pick a death-screen option through the resurrection handler (the caster's offer first; inside a dungeon, in place; otherwise Wake Here, then holocrypt, then a forced resurrection).
3. Answer group invites; answer loot rolls.
4. Ambient bots run `ThinkAmbient`, in this order, returning as soon as one step takes the think:
   - get out of an instance the bot is not meant to be in; snap to the ground when the position has no height;
   - every 25-35 s equip better gear and spend ability points (`BotGear`, `BotAmps`);
   - system probes if the bot has that role (every 5-15 s, `BotSystems.Think`);
   - a crafting step if a quest needs a crafting tier (`BotTradeskills`: learn or swap the skill at the trainer message (the harness pays a missing relearn price,
     `tradeskill-qa-relearn-credit-grant`), pick open talent tiers (`bot-tradeskill-talent-picked`), gather materials from nodes (Farmer nodes with the class
     auto attack, `bot-farm-gather-cast`), craft at a station: coordinate crafts use a global catalyst every third craft (`tradeskill-catalyst-used`), variants
     are reached by adding additives one at a time towards the circle (additives land with a random spread), circuit crafts start with 0x0852 and finish with
     0x084F and a power core (`tradeskill-powercore-used`));
   - the dungeon, event or path step for bots with those roles;
   - quests (`ThinkQuests`), then hunting (`Hunt`);
   - otherwise wander within 40 m of home.

### Roles

Roles are assigned by bot index, dungeon bots first (`BotManager`, details in CONFIGURATION.md): **dungeon** (`BOTS_DUNGEON`), **event** (`BOTS_EVENTS`), **system probe** (`BOTS_SYSTEM_BOTS`), **path** (`BOTS_PATH_BOTS`, a quest bot that plays path missions first), **quest** (the rest). Only quest bots take quests.

### Mortal and immortal

With `BOTS_IMMORTAL=1` the damage code returns early for bots (`UnitEntity`, checked before any damage is applied), so bots never die and long unattended runs are not interrupted. With `0` bots take real damage, die, and resurrect as described above; `combat-stats` counts the deaths. Immortal bots cannot wipe in a dungeon, so wipe counts only mean something in mortal runs. `COMBAT_AGGRO=1` additionally makes hostile creatures start fights with bots within 12 m; without it creatures only fight back. `BOTS_DAMAGE` scales the damage bots deal (default 1 = real damage).

## Hunting and combat

`Bot.Hunt` picks a target (nearest living creature that is hostile to the bot, or one of its quest's kill targets, within 25 m or 60 m for quest targets; dungeon bots prefer encounter bosses), closes in with the movement manager, and casts the first usable ability from its action bar in slot order (`CastFirst`: off cooldown, with charges, in range, not recently refused). Heals are only cast on itself, when below 60 % in a dungeon run. A bot below 50 % health stops fighting and heals itself for 5 % of its maximum every 3 s; that is a harness shortcut, because players do not yet regenerate out of combat (code comment in `Hunt`).

## Quest picking

Bots do not walk to quest givers. `QuestManager.CanAcceptWithoutGiver` skips the giver and communicator checks but keeps every other rule (already in the log or completed and not yet reset, disabled quests, the prerequisite row in strict mode, so level, faction, race, class and prerequisite quests apply). A bot holds at most 2 quests (`MaxBotQuests`).

- **Local pick** (`BotQuests.Pick`): quests that need a creature standing near the bot, whose pick level is within -5 to +3 of the bot's level (`PickLevel` is the quest's con level or minimum level, lowered by an upper level cap in its prerequisite). Quests nobody has completed yet are preferred, the least tried first.
- **Far pick** (`PickAnywhere`): when a bot is idle (no quest, or no progress on any quest for 90 s) it picks any suitable quest that has an open-world target spawn anywhere and teleports to the spawn. Quests whose required objective only spawns in a dungeon or raid are not picked.
- **Level rotation**: when nothing is left at the bot's level, it is set to the level range with the most quests nobody completed (`bot-level-rotation`).
- **Ties**: quests that need another quest in the log or an objective of it, and `CompleteQuest` objectives, are taken while the bot holds the related quest.
- **Turn-in**: finished quests are completed immediately; with several item rewards the bot picks one at random.
- **Stalls**: a quest with no progress for 6 minutes is abandoned (`quest-stalled`, with the objective state as example). Quests that cannot be abandoned (Quest2 flag 0x4000) are parked and ignored. A quest whose targets are not in the bot's world for 2 minutes triggers a trip to the right world.
- **Forced path** (`bot quest <id>`, `bot sweep`, `scripts/force-sweep.sh`): a bot drops its quests and takes one chosen quest with prerequisites skipped (faction, race and class still apply), to test the quest itself. Results go to `docs/SWEEP-RESULTS.tsv`; `scripts/research/openwhy.py` turns them into the reason per open quest.

Kill, activate, talk, area, zone, spell, equip and event objectives have drivers that go and do them in the world. The bot-ready objective types are in `QuestCoverage.BotReady`.

Real paths per objective type, beyond kill/activate/talk:

- **EnterZone**: the bot goes to a point where the server's own map files resolve the zone or one of its sub zones (`data/research/zone-points.tsv` from `scripts/research/zonepoints.py`; the zone update credits the zone and its parents), else to a WorldLocation2 of the zone. Zones only inside instance worlds are not visited by lone bots.
- **CollectItem**: creatures with the item in their researched drop table, the reward pane group, and creatures the objective text names as the item's source ("Collect $m(item=81821) from $m(creature=18563)"): killing, activating or gathering one gives the item (`quest-loot`).
- **SpellSuccess** whose spell an item casts (ItemSpecial `Spell4IdOnActivate`, 24 objectives, e.g. "Obtain a Gladiator's Weapon Enhancer and use it"): the bot uses the item through `ClientItemUse` (`objective-item-use`).
- **ActivateEntity "Equip the $(item=N)"** (15 objectives): equipping the item credits the objective for any player (`Inventory.ItemMove` -> `QuestManager.OnItemEquipped`); the bot equips it (`objective-equip`).
- Creature macros without parentheses (`$creature=33057`) count as named creatures like `$(creature=N)`.

`gt zonecheck <world>` compares the zone the map file resolves at every WorldLocation2 of a world with the location's own zone (22: 5655 agree, 398 differ).

### Synthetic credit and QA heuristics

Some things a bot cannot do or the data does not say. Both kinds are counted apart from real completions so real coverage is not overstated.

**Synthetic credit** (`BotQuests.CreditSynthetic`, on unless `BOTS_SYNTHETIC=0`): 10 s after a quest is taken, each objective that is not complete is first offered to its real driver (public event visit, crafting, equip, zone visit, spell cast or item use, area visit). Only when the driver has nothing left to try, the objective is credited directly. Each direct credit is `objective-synthetic` with the objective type and an example quest; `objective-event-gave-up` is the same for public events after 8 minutes or 5 visits. With `BOTS_SYNTHETIC=0` the drivers still run, only the direct credit is skipped, so a completion in such a run is a real one. `QuestCoverage.IsSynthetic` decides ahead of time which objectives have no path; the quest-coverage file (`/cache/quest-coverage.txt`) lists them by type ("Synthetic objectives by type": objectives, quests, live quests = titled and not cut, quests where the type is the only synthetic one) and `/cache/synthetic-objectives.tsv` lists each one (`scripts/research/synthwhy.py` looks for a table path per objective).

What stays synthetic and why (live quests, counts of 2026-10-16):

| Type | Live quests | Reason |
|---|---|---|
| ActivateEntity with no creature (data 0) | 297 | Scripted steps ("Wait for the squads", "Order the Blue Squad to move", story panels): the text and tables name no creature, item or spell; 727 of 741 have no QuestAdvanceObjective spell either. |
| Unknown31 | 28 pools (31 quests) | Work orders and random dailies. Type 31 is a slot: the server picks a QuestObjective row from the quest's pool and sends it as RandomResultId, the client shows that row instead (certain, client quest state handler). Pools are not in the tables; `data/research/workorder-pools.tsv` (scripts/research/workorder_pools.py) maps 229 of 257 pools to their unreferenced CraftSchematic/ObtainSchematic rows, so those work orders are crafted for real. The 28 unresolved pools stay synthetic (`quest-random-pool-unknown`). |
| EnterZone | 54 | Sub zones the map files do not contain (caves, labs: 20 objectives with WorldLocation2 entries the map resolves to another zone) and zones only inside instance worlds. |
| Unknown10 / VirtualCollect | 42 / 40 | No creature in the data (27 / 37) or the named creatures have no spawn (14 / 15). |
| EnterArea | 16 | The group's creature has no spawn and the objective has no indicator. |
| CompleteEvent | 14 | Data does not resolve to a public event whose text matches (250 is a PvP path event, the text describes a wave fight). |
| CollectItem | 16 | Items missing from Item2, gathering materials without a researched node drop, or no source in any data. |

**QA heuristics** are guesses that let a bot do a real action against data that does not name its target (`QuestCoverage.HeuristicKind`). The quest-coverage line counts completed quests that rely on one:

| Kind | Meaning |
|---|---|
| `indicator` | An activate objective with no creature in the data or text: the bot uses the one activatable creature kind spawned within 30 m of the objective's map indicator. Two or more kinds there is ambiguous and stays synthetic (`objective-indicator-ambiguous`). |
| `reward-pane` | The creature comes from the quest's reward pane target group (likely, not certain). |
| `unknown10` | The quest has an objective of type 10 or 15, whose meaning is decoded from the tables only (kill / use targets via a target group or the creatures named in the text). |
| `item-grant` | The objective is done for real (equip, item use), but the item comes from a QA grant (`quest-qa-item-grant`): no vendor stock or drop source for it is known. |
| `craft-materials` | The quest needs a tradeskill tier above 1 and the completing bot's skill used QA-granted materials (or has xp from before the server start). Bots craft up to the tier for real xp and gather the materials from harvest nodes where the research drop data names a node (`tradeskill-material-gathered`, 3 minutes per material); the rest is granted (`tradeskill-qa-material-grant`). A completion with only gathered/quest materials is `quest-craft-tier-real` and drops the kind for that quest. |

Each heuristic is documented with its evidence in [ASSUMPTIONS.md](ASSUMPTIONS.md). A completion without any of these is a completion on data alone.

### Reading the quest-coverage line

The first line of the failure report, written by `BotQuests.CoverageLine`:

```
quest-coverage: <done> / <doable> doable quests completed at least once, <A> without QA heuristics, <B> rely on one (<kind> <n>, ...) (<C> distinct completed in all history; <D> more need an objective in a dungeon/raid world)
```

- *doable*: every objective of the quest resolves to something with an open-world spawn (or a driver), so a lone bot could do it. Quests the tables or spawns cannot support are not in this count; quests that need an instance are the *D* number.
- *done*: doable quests that at least one bot completed (counted from `QuestOutcomes`, which is kept across restarts in `/cache/quest-outcomes.txt`, so *done* does not reset with the ledger; the file is deleted with the game-table cache when a rebuild changes the shared assembly, the database history of completed quests is then what remains).
- *A* + *B* = *done*. *B* is broken down by heuristic kind.
- *C* counts all completions in history, including quests that are no longer doable.

A rising *done* with a stable *B* is real progress. `scripts/outcomes.sh` lists the quests bots took and never completed; `scripts/report.sh quests` prints the coverage file with objective types and "unlocked by adding one objective type" lists; `docs/QUEST-COVERAGE.md` has the first failing step per open quest.

## Clean bots (`BOTS_CLEAN`)

With `BOTS_CLEAN=1` the ambient population uses only clean accounts, `bot.clean-N@bots.invalid`, made on demand with a new character (even N in the tutorial start, odd N on Nexus) and reused by every later clean run; runs without the setting never log them in, and clean runs log in nothing else (companions are refused). What changes for these bots:

- No harness level changes: no random level on the first think, no level rotation (`bot-level-rotation` is skipped; an idle bot hunts or moves to a spot of its level instead). `bot quest <id>` still sets the level and is tagged `level-set`.
- Quest role only (path missions first for the first `BOTS_PATH_BOTS`); dungeon, event and system-probe roles are off, because they grant items, credits and levels.
- Zone 12 quests (developer and beta quests, see ASSUMPTIONS.md "Clean-bot quest verification") are never picked.
- "Nobody completed it yet" (pick preference) and the quest-coverage line mean "no clean bot completed it yet".
- Characters that stopped being a fresh start (`dev-zone-xp`, `level-set`) are retired: not logged in again, replaced by a new clean account.

Every completion is classified per objective (real, QA heuristic kind, synthetic) with the harness grants used while the quest was in the log and the turn-in a player would have had; the files, columns and the run commands are in [TESTING.md](TESTING.md) "Clean-bot quest verification". `scripts/clean-bots.sh` lists the clean accounts and characters. Nothing deletes them.

## Dungeon teams (`BOTS_DUNGEON`)

`Bots/DungeonTeams.cs`. Dungeon bots are grouped into teams of `BOTS_TEAM` (5, or 20 for raids). Each team is assigned a world from the `BOTS_DUNGEON` list in turn and moves through the states `Forming`, `Entering`, `Clearing`, `Leaving`, `Cooldown`:

1. **Forming/Entering**: the team levels to `BOTS_TEAM_LEVEL` (default: the highest level among the world's bosses that have an encounter definition); with `quest`, normal runs take the levels of the world's never completed instance quests by turns, the most common first (`dungeon-quest-level`), so quests the boss level refuses are taken too and leaves old groups. The leader invites every member through `ClientGroupInvite` and each accepts with `ClientGroupInviteResponse` (teams are picked from one faction because invites refuse other factions; after 90 s without such a set a mixed team is formed and the members the invites cannot add join as server members, `dungeon-group`). Raid teams are converted to a raid by the leader (`ClientGroupFlagsUpdate`). The leader sets the instance difficulty through `ClientSetInstanceSettings`. Entry (`BOTS_DUNGEON_ENTRY`, `dungeon-entry-mode`): **portal** = the bots go to the world's entrance portal unit in the open world and use it with `ClientActivateUnit`; **finder** = the leader queues the group (`ClientMatchingQueueParty`), every member answers the role check (one tank and one healer by class, `0x05B2`) and accepts the pending game (`0x05C8`), which moves the group into its instance; members outside later use `TransferIntoMatch` (`0x0606`); **teleport** = straight into the group's instance. `auto` takes portal and finder by turns where a world has both. If not all members are inside after 3 minutes: `dungeon-enter-failed`. Quests whose kill, activate, talk or interaction objectives target creatures the instance spawns are handed to the members first (`dungeon-quest-taken`, `-refused`, `-completed`; `instance-quest-completed` says real, heuristic or synthetic per quest).
2. **Clearing**: bots fight everything hostile. A bot attacks the nearest creature in reach (bosses first); when none is in reach it teleports next to the nearest living hostile creature, or to the nearest hostile spawn in a grid that is not loaded yet. Bots rest to 80 % health (self heal, as above) and wait while a teammate is below 70 %. The run counts as cleared (`dungeon-cleared`, with duration, kills, bosses and wipes) when either at least one boss fell and no boss is left alive, or nothing hostile is alive and at least one creature was killed, and in both cases no hostile spawn sits in a grid nobody has visited yet; the condition has to hold for a while (`ClearFor`). After 30 minutes the run ends as `dungeon-timeout`. When every member is dead it is a `dungeon-wipe`: encounters reset and the team enters again once the bots have resurrected. 60 s with nothing hostile alive and nothing killed is `dungeon-empty` (the world's spawn data has no hostile creature; not a clear).
3. **Leaving/Cooldown**: each member leaves the group inside the instance (`ClientGroupLeave`), which starts the 30 s pending removal, and leaves at once with `LeavePendingRemovalInstance` (`0x057C`); after a matched game the members use `LeaveGame` (`0x05DA`). A member still inside after 60 s is counted (`dungeon-leave-failed`) and sent back. The team waits for the instance to be released (`instance-released`, failure report line 3) and goes again. A run that is not clear after `BOTS_DUNGEON_MINUTES` (30) ends with `dungeon-timeout`.

Raid teams (`BOTS_RAID`, `BOTS_RAID_TEAM`, `BOTS_RAID_TEAMS`) are formed from the dungeon bots before the dungeon teams and run the raid worlds the same way. Between fights dungeon bots use the quest objects of the instance (consoles, items, people to talk to) through the quest bots' activation, each object at most once a minute (`dungeon-quest-object`); a quest object in a grid nobody has loaded yet is reached by a jump to its spawn point, as for hostile spawns.

Boss fights run through `Game/Encounters` (phases, ability timers, adds, enrage), recorded as `encounter-*`; `scripts/dungeon-check.sh` prints those categories.

**Veteran mode** (`BOTS_VETERAN`): for worlds that have veteran creature rows, `alternate` (default) makes every second run veteran (runs 2, 4, ...), `all` always, `off` never. A veteran run uses the highest level of the veteran creature rows (50 for the Prime dungeons). `dungeon-difficulty-mismatch` is recorded when the created instance does not have the difficulty the team chose. `VETERAN_WORLDS` forces veteran for chosen worlds, for human players too.

Reports from sweeps over all dungeons and raids: [DUNGEONS.md](DUNGEONS.md) and [INSTANCES.md](INSTANCES.md).

## Other roles

- **Event bots** (`BOTS_EVENTS=1`): go from one public event to the next (`PublicEventEngine.TestableEvents`), fight and activate what the event needs, move on when it ended or after 6 minutes. Each hand-over is `event-test` with the result; `event-never-started` records where the bot was when an event never started.
- **Path bots** (`BOTS_PATH_BOTS`, `Bots/BotPaths.cs`): play the missions of their own path in the open world, one at a time, through the same entry points a client uses (unit activation, spell casts for scans and digs, kills, area visits). A mission is given up after 4 minutes (`path-bot-gave-up` with the step). `BOTS_PATH_FOCUS` restricts the mission kinds. Result categories: `path-mission-completed`, `path-bot-*`, `path-holdout-*`, `path-powermap-*`.
- **System bots** (`BOTS_SYSTEM_BOTS`, `BotSystems.cs` and the other `Bot*Probes*.cs`): every 5-15 s run a probe of one system (mail, guild, auction, vendor, bank, costume, AMP, trade, housing, group finder, tradeskill, ...). A probe states expectations; each result is `system-ok`, `system-fail` or `system-skip`, with the probe name as the key. `scripts/system-counts.sh` totals them. `docs/SYSTEMS.md` lists which system has which driver. Crafting probes: `tradeskill-handlers` (learn, station craft incl. circuit start/finish), `craft-catalyst` (craft started with a catalyst, the catalyst must be used up), `tradeskill-talent` (pick the next talent tier through 0x084E, craft with it, reset after three picks), `salvage` (ClientItemSalvage 0x015E, the item must leave the bag), `farm` / `farm-check` (Farmer node gathered by the auto attack), `harvest` (tool on a node).

## Bot feed

`scripts/bot-feed.sh [-f] [bot-name] [last N lines]`: one line per event (time, bot, level, zone id, text): quests accepted, completed and stuck, level ups, world changes, deaths and resurrections, gear. Details in [BOT-FEED.md](BOT-FEED.md).

## Harness limits

- **No healer role.** Bots never heal each other: a heal ability is only cast on the bot itself, and only below 60 % health in a dungeon run. Teams have no tank or healer composition (the group finder probe forms such groups, but the dungeon teams are plain groups of the class mix that happened to log in).
- **No telegraph dodging.** Bots stand and cast; they do not move out of enemy telegraphs. The code under `Bots/` has no telegraph handling. A boss whose design relies on avoidance therefore kills mortal teams much faster than it would kill players. A mortal run of two normal-mode dungeons wiped repeatedly and timed out (recorded in COMBAT-MATH.md, "Stage E"); that is expected for an unhealed group that does not dodge, not by itself a server bug.
- **Teleport movement.** Hunting jumps to targets and spawns when they are out of reach, so pathing, line of sight and terrain collision are exercised much less than they would be by walking players.
- **Self-heal shortcut** while resting (above) and the random level assignment mean combat balance numbers from bot runs describe the harness, not a player.
- **Level rotation** can leave a bot in a zone far above its level after it is dropped to a low level; mortal bots then die to high-level creatures until they travel (COMBAT-MATH.md, "Stage E").
- **What bots cannot test**: real client behaviour (rendering, UI, the packets a real client sends in an order the bots do not), and human death and resurrection UI.
- Synthetic credit and QA heuristics (above) mean "completed" in the coverage line is not always "completed by doing the real thing".

## Reading the combat-stats lines

Combat counters are recorded under the category `combat-stats` (only with `COMBAT=1`). Keys, from the code:

| Key | Counts |
|---|---|
| `damage effect applied by bot / human / creature` | Damage effects that reached a target, by who cast them. `bot` vs `creature` shows whether bots out- or under-damage creatures. |
| `heal effect applied by ...` | The same for heals. A low `bot` number is expected (bots only self-heal). |
| `periodic Damage tick`, `periodic Heal tick` | Extra ticks of damage-over-time and heal-over-time rows after the first. |
| `creature killed by bot / human / other` | Creature deaths by killer kind. |
| `creature respawned` | Respawns scheduled by the respawn manager (corpse stays 10 s, respawn 45 s later; constants in `CombatConfig`). |
| `player died (bot / human)`, `player revived (bot / human)` | Deaths and resurrections. Zero bot deaths with immortal bots is correct; with mortal bots the two numbers should be close. |
| `hit deflected`, `hit glanced`, `multi-hit`, `damage fully resisted` | Outcomes of the hit roll (`DamageMath.RollHit`). They show that the rating curves fire; their rates follow the stats, not a target. |
| `auto-attack without a researched spell (default row)`, `auto-attack spell row without decoded parameters` | Creature auto-attacks that fall back to a generic damage row because the creature's ability data or the spell parameters are missing. A high number means placeholder damage is in use. |

Related categories: `player-death` (key = killer creature, so the creatures that kill bots most are visible; the top of this list in a mortal run is mostly level-mismatch deaths), `creature-cast-while-dead`, `creature-spell`, `creature-spell-exception`, `spell-pending-overflow`.

Read counters as ratios (for example bot damage effects : creature damage effects, deaths : resurrections, kills by bot : respawns), because absolute values depend on the number of bots and the run length. A change in the ratios between two builds is the signal.

## Unknown

- Why memory in some long runs grew in steps when instance worlds were first loaded: described in BOT-FEED.md as observed, cause not established in the repository.
- Whether the `reward-pane` heuristic is correct for every quest it is applied to: the code and ASSUMPTIONS.md call it likely, not certain.
- The meaning of quest objective types 10 and 15 and several prerequisite types is decoded from tables and text only; see ASSUMPTIONS.md.
