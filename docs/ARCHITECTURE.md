# Architecture

How the code is laid out, where game data comes from, how a spell cast and a quest objective travel through the server, and how to add the three most common extension points (spell effect handler, prerequisite check, ledger category). Settings are in [CONFIGURATION.md](CONFIGURATION.md), running the stack in [OPERATIONS.md](OPERATIONS.md), the bots in [BOTS.md](BOTS.md), testing in [TESTING.md](TESTING.md).

The server is a fork of NexusForever (.NET 5, C#). Most of the upstream structure is unchanged; this document concentrates on what to expect when you open a file.

## Top-level layout

| Path | Content |
|---|---|
| `Source/NexusForever.WorldServer` | The game server and everything the bots add (below). |
| `Source/NexusForever.Shared` | Code shared by the servers: game table reader (`GameTable/`), network and message dispatch (`Network/`), the world update loop (`WorldManager.cs`), the database manager. |
| `Source/NexusForever.Database`, `.Database.Auth`, `.Database.Character`, `.Database.World` | Entity Framework contexts, entity classes and migrations for the three databases (`nexus_forever_auth`, `nexus_forever_character`, `nexus_forever_world`). |
| `Source/NexusForever.AuthServer`, `.StsServer` | Login and realm list servers. |
| `Source/NexusForever.MapGenerator` | Tool that reads the client archive and writes the `.nfmap` map files (compose service `mapgen`, profile `tools`). |
| `Source/NexusForever.ClientConnector` | Client side connection helper from upstream. |
| `data/` | Client tables (`tbl/`) and maps (`map/`, both yours, not in git), plus files kept in git: `research/` (research tables), `encounters/` (boss definitions), `spawns/` (spawn files for the import script). |
| `docker/`, `Dockerfile`, `docker-compose.yml` | The stack. The image is the only place the code is compiled. |
| `scripts/` | Operating and test scripts (table in OPERATIONS.md); `scripts/research/` holds Python data helpers. |
| `docs/` | Documentation. `ASSUMPTIONS.md` records every behaviour that is a best-supported guess, with its source. |

## The world server

All of the following is under `Source/NexusForever.WorldServer`.

| Folder | What it owns |
|---|---|
| `WorldServer.cs` | `Main`: initialises the managers in a fixed order (tables, maps, entities, factions, guilds, residences, items, prerequisites, spells, quests, ...) and registers the per-tick update calls. The order matters where a comment says so. |
| `Bots/` | The bot population and everything that drives it: `Bot` (login, think loop, hunting), `BotManager`, `BotQuests` and `QuestCoverage` (what bots may take, what counts as doable), `DungeonTeams`, `BotPaths`, `BotSystems` and the other probe files, `ForceSweep`. See [BOTS.md](BOTS.md). |
| `Game/Spell` | `Spell` (one cast: checks, targets, effects, events), `SpellEffectHandler*.cs` (one partial class, one method per effect type), `GlobalSpellManager` (spell tables and handler registry), `SpellInfo`/`SpellBaseInfo`, `Telegraph`, `ActionSet*` and `CharacterSpell` (the player's abilities). |
| `Game/Prerequisite` | `PrerequisiteManager` (evaluates a prerequisite row: AND/OR rows, nested rows, the lenient scope for casts) and `PrerequisiteChecks*.cs` (one static method per prerequisite type). |
| `Game/Entity` | Entities and their state: `WorldEntity` > `UnitEntity` (auras, crowd control states, immunities, procs, property modifiers, pending spells, damage taking) > `Player` and `NonPlayer`; plus the per-player managers (`QuestManager`, `SpellManager`, `PathMissionManager`, inventory, currency, mail, ...). |
| `Game/Quest` | `Quest`, `QuestObjective`, `QuestInfo` (table view of a quest), `TargetGroupHelper` (resolves target groups to creatures), `GlobalQuestManager`, periodic quests, cut content list. |
| `Game/Combat` | Combat switches and constants (`CombatConfig`, read from `NF_COMBAT*`), `DamageMath` (rating curves, crit, mitigation), `SpellFormula` (effect amounts from the table parameters), `UnitStats`, `ItemStats`, `RespawnManager`, resurrection and bind points. Formulas and sources: [COMBAT-MATH.md](COMBAT-MATH.md). |
| `Game/Encounters` | Data-driven boss encounters (`EncounterDefinition` read from `data/encounters`, `EncounterController` runs phases, ability timers, adds and enrage). |
| `Game/Events` | `PublicEventEngine`. |
| `Game/Map` | Maps, grids, instances (`DungeonInstancedMap`, `DungeonMapInstance`), spawning (`BaseMap.SpawnGrid`), veteran variants. |
| `Game/Groups` | Groups, group finder, instance lockouts, group loot. |
| `Game/Research` | Readers for the research tables in `data/research` (creature abilities, drops, event info). |
| Other `Game/*` | One folder per system: Achievement, Challenge, Guild, Housing, Mail, Market, Reputation, Rewards, Social, Tradeskill, Travel, ... |
| `Diagnostics/` | `FailureLedger` (counters, see below), `BotFeed`, `QuestOutcomes`, `Profiler`. |
| `Command/` | Console command framework. `Command/Handler/*CommandCategory.cs` hold the commands; `GameTableCommandCategory` is the `gt` command family, `BotCommandCategory` the `bot` family. `FileCommandInbox` reads `/cache/commands.txt`, which is how `scripts/gt.sh` reaches the running server. |
| `Network/Message/Handler/` | One static class per area (`SpellHandler`, `QuestHandler`, `VendorHandler`, ...). A method marked `[MessageHandler(GameMessageOpcode.X)]` takes `(WorldSession session, ClientX message)`. `Network/Message/Model/` holds the message classes. |
| `Web/`, `wwwroot/` | The embedded web server. |

### The update loop

`WorldManager` (in `Shared`) runs one loop. Each pass takes at least `NF_TICK_MS` milliseconds (default 20) and calls the update delegate registered in `WorldServer.cs`: network, maps, bots, groups, market, respawns, public events, buyback, quest manager. `MapManager` updates the maps; each map updates its grids, entities and their pending spells.

## Where game data comes from

1. **Client tables** (`*.tbl`) in the folder named by `GameTablePath` (`/data/tbl` in the stack, mounted from `./data/tbl`). `GameTableManager` has one `GameTable<TEntry>` property per table; a property with `[GameData]` is loaded at start, the others when first used. `en-US.bin` supplies the text. Entries are plain classes (`Shared/GameTable/Model`). Console: `gt show <Table> <id>`, `gt find`, `gt findall` read these at runtime.
2. **Map files** (`.nfmap`) in `Map__MapPath` (`/data/map`), generated from the client archive by the map generator. Instance worlds without a map file fall back to a flat plane (`map-file-missing-flat-fallback` in the ledger; the reasoning is in ASSUMPTIONS.md).
3. **World database** (`nexus_forever_world`): the `entity` rows that place creatures and objects. They are loaded from the upstream community world database (`scripts/load-world-data.sh`, `scripts/load-instance-data.sh`) and from this repository's own spawn files (`scripts/import-research-spawns.sh`, ids from 9000000). `EntityCacheManager` indexes them per world; `BaseMap.SpawnGrid` creates the creatures when a grid becomes active.
4. **Character and auth databases**: accounts, characters, inventory, quests, mail, guilds, and so on. Bots are ordinary characters with hidden accounts.
5. **`data/research/*.tsv`** (path `NF_RESEARCH_PATH`): tables built from outside sources for things the client tables do not hold: creature abilities, drops, public event NPCs, quest links, veteran creature variants, path mission targets.
6. **`data/encounters/`** (path `NF_ENCOUNTER_PATH`): boss encounter definitions, one JSON file per dungeon or raid, read by `EncounterManager`.
7. **`/cache`**: files the server writes at run time (failure report, quest coverage, bot feed, `bots.json`, quest outcomes). The cache volume survives restarts.

Sources and assumptions for the research data are in [DATA-PROVENANCE.md](DATA-PROVENANCE.md), [SOURCES.md](SOURCES.md) and [ASSUMPTIONS.md](ASSUMPTIONS.md).

## The failure ledger

`Diagnostics/FailureLedger.cs` is a static set of deduplicated counters keyed by `(category, key)`, each with a count, one example string and first/last seen time. `FailureLedger.Record(category, key, example)` is cheap and thread safe. A timer rewrites `/cache/failure-report.txt` every 30 seconds once something was recorded; the counters start from zero at each restart (`gt reset` clears them).

The design rule, which also governs new code:

- **Record, never throw in update loops.** Everything that runs every tick (map update, spell update, grid actions, periodic effects, encounter casts, bot think) catches exceptions at the loop, records them under a `*-exception` category with the first stack frame, drops the offending item and carries on. Examples: `MapManager` (`map-update-exception`), `UnitEntity` around `spell.Update` (`spell-update-exception`), `Spell.SchedulePeriodic` (`spell-tick-exception`), `Bot.Update` (`bot-failed`).
- **Never silently ignore.** A thing the server cannot handle is counted: an effect type without a handler (`unhandled-spell-effect`), a prerequisite type without a check (`unhandled-prerequisite-type`), an effect that is parsed but not applied (`effect-stored`), a refused cast, a creature casting while dead (`creature-cast-while-dead`), a spell queue that overflows (`spell-pending-overflow`).
- **Do not guess semantics into player-visible numbers.** If a meaning is not decoded, store it, count it, and write the assumption in `docs/ASSUMPTIONS.md`.

Categories are free-form strings; the common ones are listed in OPERATIONS.md and TESTING.md. Nothing enforces a registry of categories, so a new one appears in the report as soon as code records it.

## Flow: a spell cast

1. **Entry.** A client message (`ClientCastSpell`, `ClientCastSpellContinuous` in `SpellHandler`) looks up the ability in the player's `SpellManager` and calls `CharacterSpell.Cast`, which calls `Owner.CastSpell(new SpellParameters { CharacterSpell, SpellInfo, UserInitiatedSpellCast = true })`. Bots call `CastSpell` directly with the same parameters. Creatures (`NonPlayer`, `EncounterController`), procs (`UnitEntity`), proxy effects (`SpellEffectHandler`) and path code call it too, without `UserInitiatedSpellCast`.
2. **`UnitEntity.CastSpell(SpellParameters)`.** Refuses disabled spells (`DisableManager`), refuses a dead creature (ledger `creature-cast-while-dead`), refuses when too many spells are pending on the unit (`spell-pending-overflow`), then creates a `Spell`, calls `Cast()` and adds it to `pendingSpells`. Each tick `UnitEntity` calls `spell.Update` for every pending spell (in a try/catch) and removes finished ones.
3. **`Spell.Cast`** runs `CheckCast`: the caster cast prerequisite (`PrerequisiteManager.Meets(..., lenient: true)`, players only; prerequisites of creature casts are not evaluated yet, there is a TODO in `CheckPrerequisites`), crowd control conditions, caster death, and for user initiated player casts the spell cooldown, global cooldown and ability charges. A failed check sends a cast result to the client. Otherwise it sets the global cooldown, builds telegraphs for non-player casters, sends `SpellStart` and enqueues `Execute` after the cast time.
4. **`Spell.Execute`** sets the spell cooldown, runs `SelectTargets` (the caster; the selected target when combat is on; units inside the spell's telegraphs), `ExecuteEffects` and `CostSpell`, credits activation, fires the "on cast" procs, reports the cast to the quest, path and challenge managers (`OnSpellSuccess`) and sends `SpellGo`.
5. **`ExecuteEffects`** loops over the spell's effect rows. For each row it selects the targets whose flags match, finds the handler with `GlobalSpellManager.GetEffectHandler(effectType)` and invokes it per target. A missing handler is counted and logged once. Effects with a duration register an aura on the target; damage and heal rows with a tick time are scheduled again for every tick by `SchedulePeriodic`.
6. **Effect handlers** (`SpellEffectHandler*.cs`) do the work: `HandleEffectDamage` goes through `Combat.SpellFormula.Amount` and `DamageMath` and ends in `UnitEntity.TakeDamage`, which applies immunities, shields and death; deaths of creatures reach `NonPlayer` kill handling (loot, experience, quest credit; next section).

Prerequisites on a spell cast run in a **lenient scope**: a check type the server has no handler for counts as met, so one unknown rule does not disable an ability. Quests and everything else stay strict. "Must have" rules therefore pass in casts, "must not have" rules are enforced.

## Flow: a quest objective

Objective progress arrives through events on the player's `QuestManager` (`Game/Entity/QuestManager.cs`):

- A creature dies: `NonPlayer` kill handling rolls loot, grants experience, informs the public event engine and the dungeon team counter, then calls `QuestManager.OnCreatureKilled(creatureId, guid)` for every credited player. That method (also feeding `ChallengeManager`) walks the active quests and, per objective type, decides whether the creature matches: `KillCreature` by id, `KillTargetGroup(s)` through `TargetGroupHelper.Contains`, `VirtualCollect` through the drop rules, `Unknown10`/`Unknown15` through the scripted-target tables in `QuestCoverage`. A match calls `quest.ObjectiveUpdate(objectiveId, 1)`.
- Other events have their own entry points: `OnCreatureActivated`, `OnCreatureTalked`, `OnSpellSuccess` (called from `Spell.Execute`), `CheckAreas` (zone and area objectives), and generic `ObjectiveUpdate(type, data, progress)` calls from systems such as tradeskills (`CraftSchematic`).
- `Quest.ObjectiveUpdate` skips completed objectives, honours sequential objectives (`CanUpdateObjective`), applies the progress, sends the objective update to the client, completes optional objectives once the required ones are done, and sets the quest to `Achieved` when every objective is complete.
- Turn-in is `QuestManager.QuestComplete(questId, reward, communicator, skipReceiverCheck)`: it checks the state, gives rewards, advances follow-up quests and marks the quest completed.

Quest acceptance is `QuestManager.QuestAdd`, subject to the quest's prerequisite row (strict mode). `CanAcceptWithoutGiver` is the variant the bots use when no giver is nearby.

## How to extend

### Add a spell effect handler

Add a method to a `SpellEffectHandler*.cs` file (the class is `partial`), marked with the attribute. `GlobalSpellManager.InitialiseSpellEffectHandlers` finds it by reflection at start; there is no registration list. Handler signature: `(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)`; the caster, parameters and spell info are fields of the `Spell` instance. The effect row is `info.Entry` (`DataBits00..` hold the effect specific values).

Example from `SpellEffectHandler.Misc.cs`:

```csharp
// DataBits00 = unit state id ... Meaning not settled, stored for the report.
[SpellEffectHandler(SpellEffectType.UnitStateSet)]
private void HandleEffectUnitStateSet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
{
    Diagnostics.FailureLedger.Record("effect-stored", $"UnitStateSet state {info.Entry.DataBits00} not applied (state meaning undecoded)", $"spell {parameters.SpellInfo.Entry.Id}");
}
```

A handler that cannot apply what the data says should still exist and record `effect-stored` (or `effect-applied` when it did something worth counting), so the effect leaves the `unhandled-spell-effect` list but stays visible. Do not throw; if the effect type is new to the enum, add it to `Game/Spell/Static/SpellEffectType.cs` first. Use `gt effects` and `scripts/spell-data.sh` to see which effect types still lack handlers and which spells use them.

### Add a prerequisite check

Add a static method to a `PrerequisiteChecks*.cs` file (the class is `partial`), marked with the attribute. `PrerequisiteManager.Initialise` collects them by reflection. Signature: `(Player player, PrerequisiteComparison comparison, uint value, uint objectId)` returning whether the rule is met.

Example (`PrerequisiteChecks.cs`):

```csharp
[PrerequisiteCheck(PrerequisiteType.Level)]
private static bool PrerequisiteCheckLevel(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
{
    uint level = player.Level;
    switch (comparison)
    {
        case PrerequisiteComparison.Equal:              return level == value;
        case PrerequisiteComparison.GreaterThanOrEqual: return level >= value;
        // ...
        default:
            log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Level}!");
            return true;
    }
}
```

Add the type to `Game/Prerequisite/Static/PrerequisiteType.cs` if it is missing. Where the meaning of a type, comparison or value is inferred rather than known, say so in the code comment and record it in ASSUMPTIONS.md; `prerequisite-assumed-met` and `prerequisite-inferred` are the existing ledger categories for that. `gt prereqrow <id>` prints a prerequisite row.

### Add a ledger category

There is nothing to declare. Call `Diagnostics.FailureLedger.Record(category, key, example)` where the event happens:

```csharp
Diagnostics.FailureLedger.Record("creature-cast-while-dead", $"spell {parameters.SpellInfo.Entry.Id}",
    $"parent {parameters.ParentSpellInfo?.Entry.Id} root {parameters.RootSpellInfo?.Entry.Id} creature {CreatureId}");
```

Rules of thumb used throughout the code: the key is the thing you want to count separately (a spell id, a type, a world), so keep its cardinality bounded; the example is one representative detail and is only stored for the first occurrence of the key. Report layout prints at most 25 keys per category (80 for categories starting with `system`, 400 for `dungeon-*` and `encounter-boss-killed`; the limits are in `FailureLedger.Format`), most frequent first. Do not record inside code that runs thousands of times per tick unless the key set is small: the call takes a dictionary lookup.

### Add a console command

Add a method marked `[Command(Permission, "description", "name")]` to a `*CommandCategory` class in `Command/Handler`; nested category classes make sub-commands. The command is then reachable from the server console and through `scripts/gt.sh`.

### Add a client message handler

Add a method marked `[MessageHandler(GameMessageOpcode.X)]` with the message class from `Network/Message/Model` to a class in `Network/Message/Handler`. An `InvalidPacketValueException` thrown by a handler disconnects the client; any other exception is logged and the connection stays up (`Shared/Network/GameSession.cs`). Client messages that reach the server without a handler are counted as `system-unhandled-message`.

## Unknown

- Whether prerequisites of creature casts (the TODO in `Spell.CheckPrerequisites`) matter for any ability: not measured.
- A complete list of ledger categories: the categories come from `Record(...)` call sites in the code (357 distinct literal names in `Record("..."` calls when this was written; a few categories are built from variables); there is no registry to read them from.
