# WildStar Bots: Status and Roadmap

As of 2026-09-30 (snapshot).

## Goal

WildStar should work as it was intended, and a small friend group should be able to play it with bots that behave like real players. Over time those bots should also talk with you.

The bots stack is this repository. A separate stable server is untouched on purpose; bots are ported there only when they are ready and off by default.

## Where it stands (2026-10-02)

Bots log in, quest, fight, run public events, and (new) clear dungeons as groups; everything they cannot handle is counted and reported from the terminal. The game itself is still incomplete (see the lists below), but the missing data problem has a first answer: spawns, loot, creature abilities and boss lists come from archived community data, and every guess is written down in `docs/ASSUMPTIONS.md`.

| Area | What works today | Notes |
| --- | --- | --- |
| Spawn data | Community world database plus about 37,000 researched rows (archived Jabbithole NPC pages, clustered sightings, `scripts/research`, `scripts/import-research-spawns.sh`); quest creatures without any page are placed at the quest indicator locations. | `gt questcheck`: giver-not-spawned 1076 to about 540, receiver-not-spawned 745 to about 380, objective-not-spawned 2079 to about 440. Still missing: zones newer than the archive (Halon Ring, Murkmire, Arcterra tracts). |
| Loot | About 40,000 researched drop chances (rolled per kill for the killer), generic level based drops without data, credits (placeholder). Bots with a full bag discard junk. | `data/research/creature-drops.tsv`. |
| Creature spells | Creatures cast their researched NPC abilities (non auto-attack) at their target, a dead creature does not cast. | `gt abilities <creature>`. |
| Public events | Data driven engine (`Game/Events`): starts near the event, objectives in parent order, spawned units, timers, quest credit (`CompleteEvent` data is an objective id or an event id). Bot event test mode (`NF_BOTS_EVENTS=1`) ran 335 startable events; about a third completed within 6 minutes, the rest need real scripts. | No client packets (tracker) yet. |
| Instances | Dungeons, raids, expeditions and adventures are instanced per group and reset when empty (`DungeonInstancedMap`), no respawns inside, heights from the nearest world location. `gt instances` lists them. | |
| Encounters | Data driven boss fights (`Game/Encounters`, `data/encounters`): phases by health, ability timers, adds, invulnerable add phases, enrage, wipe reset. Hand written files for Stormtalon's Lair, Kel Voreth, Skullcano, Sanctuary, Genetic Archives and Datascape bosses, drafts from the RaidCore sources, generated defaults for every elite creature. `gt encounters` counts them. | Timings are assumptions. |
| Bot dungeon runs | `NF_BOTS_DUNGEON=world NF_BOTS_TEAM=n`: server side groups of n bots enter, clear, leave, wait for the reset and repeat (`scripts/dungeon-check.sh`, `scripts/dungeon-sweep.sh`). Stormtalon's Lair: first clear and reset in 10 minutes. | |
| Stability | World 1061 out of memory (self-replicating Grimwort proxy summons) fixed 2026-10-22: summons bounded, runtime units no longer respawn, memory line and heap-growth detector in the failure report, tick parts guarded. | docs/PERFORMANCE.md "Out of memory in world 1061", `scripts/summon-check.sh`. |
| Quests, combat, spells, prerequisites | As described in the earlier sections of this file. | |

## Roadmap

Each phase ends with a gate. Bots are the test harness for phases 1 to 4 and the feature in phases 5 and 6.

1. **Trustworthy data (now).** Rebuild, run 10 minutes, then the second overnight run. Gate: no map or spell update exceptions, hundreds of distinct quests completed, kills counted per hour.
2. **Spells and prerequisites from data.** Read `spell-data.txt` and `gt effectdata` output, then implement VitalModifier (which resource each vital number means), the aura-type prerequisites (129, 116, 15), UnitPropertyModifier, ModifySpellCooldown and Proc (needs an event system). Replace the lenient prerequisite mode once the types are understood. Gate: the top ten unhandled effects and prerequisites drop to zero and class resource bars move correctly.
3. **Real objectives instead of synthetic ones.** Bots walk into zones and areas, pick up items, use client side interactions and cast the required spells. Gate: synthetic credits per type fall to near zero and a human can finish a quest chain end to end.
4. **Dungeons.** Instanced dungeon maps, group entry, creatures that cast spells, and encounter scripts for a first dungeon. Gate: a group of five bots clears one dungeon.
5. **Playing with bots.** Groups, companion roles (tank, healer, damage), follow and assist, loot rules, revive, level sync. Gate: you and two friends clear a dungeon with bots filling the group.
6. **Talking with bots.** Gate: a bot answers in chat in character, using retrieved WildStar knowledge, without slowing the world.

After phase 2, the bot code and combat layer can be ported into `nexusforevercontainer` with bots off by default, once you decide to.

## Talking with bots

Bots should answer when spoken to, in character, running on your own hardware. The design keeps most traffic away from the model and gives it real WildStar knowledge to draw on.

1. **Rules first.** Greetings, invitations, follow, stay, assist and similar requests are matched by simple rules and answered from templates. No model call.
2. **A local model for real conversation.** Gemma or Qwen, quantized, served by llama.cpp or Ollama in its own container with a CPU limit so the world tick does not suffer. Which one and which size is decided by a benchmark on the server host (reply speed and how well it stays in character), not by name. A bot calls it only when a player addresses it directly (whisper, its name in say, party chat).
3. **RAG with three sources.**
    - Game knowledge from the client tables: quest text, creature, zone and item names, lore. The server already has these loaded, so it exports them itself to text chunks (a `gt` command writing JSONL).
    - Server knowledge: hand-written markdown with your rules, commands, an FAQ, and what bots can and cannot do.
    - Bot memory: short notes per bot about players and earlier conversations, in SQLite.
4. **Retrieval starts simple.** SQLite full-text search (FTS5) first, which needs no embeddings. Local embeddings with sqlite-vec are added only if keyword search misses too often. Three or four chunks go into each prompt, together with the bot's persona (name, class, level, zone, current quest, group role) and the last six chat lines.
5. **Limits instead of a bill.** Replies capped at about 80 output tokens, a per-bot cooldown, a per-player hourly limit, one generation at a time, and a timeout that falls back to rules or silence.
6. **Safe by construction.** Player chat is untrusted text. The model gets no tools and its output is only ever chat text: length limited, links removed, never executed as a command. Retrieved game text comes from the client tables, chat never enters the index.
7. **Off by default.** `BOTS_TALK=1` turns it on.

Cost and speed: no API cost, only CPU and electricity. A quantized 4B model on CPU may need 10 to 20 seconds for an 80 token reply (an estimate, to be measured), and the world server already uses about one core with 60 bots. Bots will show a short thinking delay, and a GPU would change this, which is worth checking on firefly first.

Build order: (1) export the game knowledge corpus from the client tables, which needs no model and can start now; (2) read chat aimed at a bot and answer with rules; (3) build the retrieval index; (4) add the model container and benchmark Gemma against Qwen; (5) add bot memory.

## Open decisions and how to run it

Decisions that are yours:

- When to port bots and combat into the stable `nexusforevercontainer` (bots off by default).
- Gemma or Qwen for the local model, decided by a benchmark on the server host.
- Which dungeon to script first for phase 4.

Commands, all from `~/bots/NexusForeverBots`:

```
git pull && docker compose build world && docker compose up -d world
unset $(grep -E '^[A-Z_]+=' .env | cut -d= -f1)
scripts/bots.sh count 30                    # start bots
scripts/report.sh failures                  # what the server could not handle
scripts/gt.sh gt maps                       # map update timings
scripts/spell-data.sh                       # data behind unhandled spells
setsid nohup scripts/soak.sh 8 60 10 </dev/null >soak.log 2>&1 &   # overnight run
scripts/soak-summary.sh                     # morning summary
```
