# Changelog

## First release (2026-10)

The first state worth publishing. Everything below was measured by bots on this server; no game client was used to check the server-side work. Sources for each number are linked.

### What works

- **Install.** `install.sh` validates a build 16042 client, generates the game tables and terrain from it, builds and starts the Docker stack, checks the firewall, loads spawns and creates an account ([docs/QUICKSTART.md](docs/QUICKSTART.md)). `uninstall.sh` removes it.
- **Questing.** Quest bots take quests from the givers, complete the real objectives (kill, talk to, activate, collect, enter zone or area, cast a spell, complete an event, craft) and turn them in. Clean-bot verification ([docs/TESTING.md](docs/TESTING.md#clean-bot-quest-verification)) uses fresh characters with no harness level changes and synthetic credit off, and records how every completion was credited. 36-hour run, 60 clean bots (soak/20261019-0021):
  - 1748 completions of 1235 distinct quests. 849 quests were completed on real objectives only, 245 with a QA heuristic (mostly turn-in from a distance), 141 with a harness grant (crafting materials, power cores, tools), and 0 with synthetic credit.
  - Quest coverage: 1707 of 3053 doable quests completed by clean bots (56%), 1503 of them without any QA heuristic. Across all earlier runs with regular bots, 4410 distinct quests have been completed. Why the rest is open, group by group: [docs/QUEST-COVERAGE.md](docs/QUEST-COVERAGE.md).
- **Combat.** Damage, healing, mitigation, shields, crit, armor, class resources, cooldowns, auras, CC, procs and the stat pipeline from item budgets are computed from the game tables and documented formulas ([docs/COMBAT-MATH.md](docs/COMBAT-MATH.md)). `scripts/combat-check.sh` compares 51 server numbers with the tables and tooltips; 51 match. Every spell effect type seen in play has a handler, and every prerequisite type is evaluated or recorded as assumed. Creatures cast their researched abilities.
- **Crafting and tradeskills.** Learning, swapping, talents, gathering, coordinate crafting with additives and catalysts, circuit crafting with power cores, work orders and salvage are all driven by bots through the client message handlers ([docs/SYSTEMS.md](docs/SYSTEMS.md)).
- **Dungeons and raids.** Instancing per group, entry by portal, group finder or teleport, encounter scripts (hand-written, from RaidCore sources, or generated from the tables), veteran mode, lockouts and resets. Bot teams cleared 6 of 8 dungeons, Datascape, Genetic Archives and the Augmentors raid, and 11 worlds on veteran ([docs/INSTANCES.md](docs/INSTANCES.md)). In a 45-minute mortal run, 6 dungeon teams and 1 raid team killed 46 encounter bosses, with wipes and re-entry. Bots completed 44 of 55 instance quests.
- **Other systems.** 36 systems tracked: 22 done, 12 partial, 2 blocked ([docs/SYSTEMS.md](docs/SYSTEMS.md)). Done includes mail, guilds and circles, auction house and commodity exchange, vendors, bank, mounts, costumes, AMPs and runes, reputation and currencies, group finder, group loot, trading, death and resurrection, titles, achievements and daily resets.
- **Persistence.** Characters save every 60 s and on shutdown. Quest rows are saved in their own batch with a resync after a failure. The clean-bot quest record matched the database after the 36-hour run.
- **QA harness.** A failure ledger counts every unhandled effect, prerequisite, message and failed cast. Also included: a 10-minute gate, soak scripts, a live bot feed, and memory diagnostics with heap-growth detection ([docs/OPERATIONS.md](docs/OPERATIONS.md), [docs/TESTING.md](docs/TESTING.md)).
- **Load.** In the 36-hour run, 60 bots averaged 108% of one core (85-244%) with memory flat at about 4.2-4.3 GiB in the final hours. The world restarted four times during that run. The cause was a self-replicating creature summon chain that exhausted memory. It was fixed and covered by a regression check (`scripts/summon-check.sh`), then verified with a 24-hour clean-mode run at 60 bots: 0 restarts, 0 out-of-memory events, heap flat at about 1.9-2.0 GiB throughout ([docs/PERFORMANCE.md](docs/PERFORMANCE.md)).

### Known limitations

Biggest gaps first.

1. **No real client has verified this server.** Only bots have exercised it. Groups, raids, companions, the group finder popup, loot windows, path and challenge UI and every other client-facing flow added here follow decoded message layouts but have not been seen on a real client. Some server messages a real client needs are not decoded and not sent (for example the group finder ready popup, `matching-pending-popup-unsent`).
2. **Missing mechanics.** There is no fall, drowning or other environmental hazard damage, because no table holds the amounts. Mentoring and account-wide friends are not implemented. Account friends need account data on the auth server, and nothing documents the stat scaling for mentoring.
3. **Best-guess values.** Health and resource regeneration and decay rates are design values chosen from the wiki's descriptions, because the client has no base rates. Other guesses: the amounts of parameterless damage rows, player base crit (5%), some Spell4Modification parameters (range, channel, threat) and pet stance AI flags. Each is marked in [docs/ASSUMPTIONS.md](docs/ASSUMPTIONS.md) with its confidence; the regeneration values are configurable ([docs/CONFIGURATION.md](docs/CONFIGURATION.md)).
4. **QA shortcuts in places of real data.** 16 transport units (for example to Arcterra, Palaver Point and Star-Comm Basin) have only a bare activate spell, so the server picks their destination by name instead of from client data. 536 of 579 crafting materials have no gather or drop source in any data, so bots get them from a QA grant; 118 craft-materials quests counted as covered rely on that. Tools, catalysts and power cores are granted the same way.
5. **Quests that cannot complete on real objectives yet.** 764 quests still keep at least one objective with no real path: scripted ActivateEntity steps (297), 28 unresolved work-order pools, and targets that are not spawned. Clean bots skip them. Communicator breadcrumb quests have no bot driver. 24 CompleteEvent objectives sit in instances quest bots do not run, and five point at missing table rows ([docs/QUEST-COVERAGE.md](docs/QUEST-COVERAGE.md)).
6. **Data gaps in instances.** Five raid worlds have no spawn data. Shiphand missions have no boss encounters defined. Boss phases, enrage and special telegraphs are approximations from addon timers and guides, not retail scripts.
7. **Combat values with no source.** Weapon damage (build 16042 tables carry none), level-difference scaling, threat amounts, creature shields, item stat ratings above item level 50 and per-item random stat rolls.
8. **Partial systems.** Housing, paths, challenges, travel, friends and chat channels have the gaps listed per row in [docs/SYSTEMS.md](docs/SYSTEMS.md).
9. **Out of scope.** PvP (battlegrounds, arenas, warplots, rated PvP). Content that was never released (Halon Ring, Murkmire) is marked cut and excluded.
10. **Runtime.** .NET 5 is end-of-life, Linux hosts only, Podman untested.

### Where this is headed

The long-term goal is bots you can play with as companions, including bots you can talk to. None of that is in this release. The bots here are a test harness, and companion bots only follow and assist. There is no timeline for conversational bots. The work now is to make the game itself behave as it did.
