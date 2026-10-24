# Performance: CPU and memory regression (2026-10-09)

Question: 60 bots averaged 143% CPU and 7.3-8.0 GiB in soak/20261009-0303 (build 8bd5d30), 117% and 5.6-7.0 GiB in soak/20261007-2305 (before tradeskills).

Method: scripts/bench.sh (the 10-minute gate in foreground steps): world recreated with the given .env values, CPU = cgroup usage_usec over the
window from 4 min after start to the end (about 7 min, 100% = one core), memory = cgroup memory.current, active grids from `gt maps` at the end.
Old builds were built detached with only the quest-targets dump patch on top. Same world DB for all runs.

| build | bots | BOTS_DUNGEON | CPU avg | interval min-max | memory | active grids |
|---|---|---|---|---|---|---|
| 6b4c09d (before tradeskills) | 30 | 0 | 103% | 96-107% | 6.94 GiB | |
| 8bd5d30 (soak build) | 30 | 1263 | 110% | 101-115% | 6.50 GiB | |
| 3b447bf (HEAD then) | 30 | 0 | 104% | 99-107% | 2.45 GiB | |
| 3b447bf | 30 | 1263 | 107% | 100-115% | 2.43 GiB | |
| 6b4c09d | 60 | 0 | 114% | 109-120% | 6.72 GiB | 299 |
| 6b4c09d | 60 | 1263 | 110% | 101-123% | 6.69 GiB | 260 |
| 4167a92 (tradeskills) | 60 | 1263 | 118% | 113-129% | 6.88 GiB | 294 |
| ad4a948 (+ coordinate variants, skill swap probe) | 60 | 1263 | 128% | 113-143% | 6.66 GiB | 394 |
| 8bd5d30 (soak build) | 60 | 1263 | 129% | 123-136% | 6.90 GiB | 367 |
| 53333b5 (HEAD) | 60 | 1263 | 121% / 130% (two runs) | 110-137% | 2.57 GiB | 368 / 376 |

Findings
- Memory: solved by c7d6d1d (terrain cells allocate zone-bound/height arrays only when present and not uniform): 6.7-6.9 -> 2.5-2.6 GiB at 60 bots,
  flat over the window. The soak's 7.3-8.0 GiB was the eager terrain arrays plus a Skullcano instance loading its own copy.
- CPU: no single hot code path. Run-to-run noise of one build is about +-5% (HEAD 121% and 130%). CPU follows the number of active map grids:
  the step is between 4167a92 and ad4a948 (294 -> 394 grids, 118% -> 128%), where system bots started crafting coordinate variants and
  swapping crafting skills at trainers, so they travel to trainers, stations and nodes and keep more grids (with their NPCs) awake.
  Profile (gt profile, ms per second of wall time): map.grids 805 -> 945 in the soaks, entity.NonPlayer calls 79k -> 92k/s, new entity.HarvestUnit
  about 11-12 ms/s (7k calls/s, 1.6 us each). Per-grid cost did not change (1.3-1.7 ms per grid update).
- Smaller per-feature costs at HEAD (60 bots, ms/s): map.actions 26 -> 44, map.relocate 11 -> 19, player.base 8 -> 15 (AMPs, resurrection,
  world boss lifecycle); together about 3% of one core.
- The soaks also differed in env: 20261009-0303 ran BOTS_DUNGEON=1263, 20261007-2305 BOTS_DUNGEON=0; measured effect at 60 bots is within noise.
- Conclusion: the CPU rise is the cost of what the bots now do (more places visited, harvest nodes as live entities), not something that
  accumulates; what matters is the trend across the 10-minute gate windows, and they are flat. Cost per feature: crafting/harvest bot travel
  about +10% of one core at 60 bots (100 extra active grids), HarvestUnit updates about 1%, the system probes and handlers added on 2026-10-09 about 3%.

## Out of memory in world 1061 (2026-10-22)

The 36 h clean soak (soak/20261019-0021) and the world that kept running after it restarted four times. All four were the same event.

| # | stall started (UTC) | process ended (UTC) | how it ended |
|---|---|---|---|
| 1 | 2026-10-19 20:12 | 21:25:56 | runtime "Out of memory." (after `gt objective 17794`, which only happened to be the next allocation) |
| 2 | 2026-10-19 23:44 | 2026-10-20 00:25:48 | unhandled AggregateException: OutOfMemoryException in `DbConnectionOptions.ParseInternal` <- `UseMySql` <- `CharacterContext.OnConfiguring` <- `DbContext.SaveChangesAsync` <- `CharacterDatabase.Save` (CharacterDatabase.cs:33), waited by `Task.WaitAll` in `GlobalGuildManager.Update` (GlobalGuildManager.cs:168) on the world thread. The guild save only made the allocation that failed; it is not the cause. |
| 3 | 2026-10-20 22:30 | 23:17:24 | runtime "Out of memory." |
| 4 | 2026-10-22 01:30 | 02:30:40 | no message, exit code 0, no shutdown-save line (the stop handler did not run, so it was no SIGTERM and no `Environment.Exit`; nothing in the repo or its scripts restarted it). World 1061 had stopped ticking at 01:31 and the server sat at the heap limit for an hour. The exit code 0 comes from the runtime's own fatal path (likely, not proven). |

Server activity in the jump windows (log copy, script commands from the file inbox): 20:00-20:12 35 commands (gt maps 23, gt memory 9,
gt profile 1, gt effects 1), 23:40-23:53 39 (gt maps 23, gt memory 10, gt profile 2, gt effects 2), 22:30-23:17 on 10-20 59 (gt maps 40,
gt memory 19). That is the routine soak and watcher polling; no dump, export or sweep ran in any window. Before restart 4 the last lines
are the same polling (`gt maps` at 02:29:49, `gt memory` at 02:29:33, 274 commands between 00:00 and 02:00, from a watcher outside the
soak, which had ended), then the new process starts at 02:30:40 without any line from the old one. The exit code 0 is not explained by
any code path in the repo (no `Environment.Exit`, shutdown command or timer restart ran; ShutdownSave would have logged).

What the evidence shows (log copy, soak snapshots `memory.txt`, `failures.txt`):
- Every time one map, world 1061 (NewCentral / Isigrol: Grimvault, Blighthaven), was involved: its tick time went 25 ms -> 1.5 s -> 3.6 s -> 10 s within a
  minute, then single ticks of 37 to 49 minutes (`gt maps` max 2956002 ms and 2227007 ms).
- Its queued grid actions (adds, removes, relocations waiting for the map update) went from 0 at 20:02 to 406045 at 20:12 and 1209555 at 21:05.
  The managed heap went from 1.8 GB to 8.7 GB in the same 10 minutes and then to 10006 MB "after collection", i.e. live objects, not garbage.
  The effective heap limit is 10752 MB (75% of the 14 GiB container limit, the .NET default); docker showed 10-12 GiB, the heap was at its cap.
- In the same windows the ledger counted 258k (crash 1) and 1.24M (crash 2) "Root applied to creature" and 0.57M / 2.97M periodic damage
  ticks per 10 minutes (normal: a few thousand). The spell ids in those ledger lines are only the first example; the counts are any spell.
  The telegraphs involved are 3-5 m circles, so thousands of units stood on one spot.
- Before crash 2 the creature spell "Living Grounds" (55076 and 57840) climbed from 0 to 1800 casts in the 80 minutes after the restart.

Cause (mechanism shown from the code and tables; the runtime repro is below):
1. The researched spawns contain 34 rows of creature 52647 "Grimwort - Proxy - Grimvault - Q8994" in world 1061 (Harrowing Heights, the kill
   target of quest 8994 "Components for Life"). Creature abilities come from the Jabbithole page with the same name, so 52647 gets the
   Grimwort's Living Grounds: root 2-3 s, damage every 0.5-1 s, and SummonCreature 52647 (2-3 s) on **every unit** in its 3 m telegraph.
2. Telegraphs hit every unit in the shape (no friend/foe filter), so the summons hit each other. A summon placed on a unit stands exactly
   where that unit stands: every cast adds one proxy per unit already in the circle, i.e. the stack doubles.
3. Removals go through the same queue as adds, 100 actions per map update. Once a burst is larger than that, expiring summons wait for
   their removal and keep fighting and casting meanwhile, which feeds the next doubling. Each queued NonPlayer is about 7 KB: 1.2M of them
   are the 8 GB.
4. Every runtime-made unit (spell summons, event and holdout units, encounter adds) kept its made-up spawn model, and the corpse handler
   scheduled a respawn from it: summons killed by bots or by each other came back as permanent world creatures. Killed proxies returned
   as permanent Living Grounds casters, which is the slow build-up over hours before the jump.

Fixed:
- Summoned units drop researched abilities that summon their own creature id (`NonPlayer.IsSpellSummon`, ledger `creature-ability-self-summon`).
- Only creatures with a world database row respawn (`RespawnsAtSpawnPoint`; ledger `respawn-skipped` counts the others).
- An expired summon stops acting at once (`despawning`), not when the queue gets to its removal.
- Spell summons are bounded: 10 per cast, 400 live per map, none while the map has 5000+ queued actions (ledger `summon-refused`).
- Map action budget grows with the backlog (100 per update, up to backlog/20, at most 2000), and a backlog over 20000 is written to the
  ledger once a minute with its make-up (`grid-action-backlog`: action types, creatures waiting to be added).
- A refused cast is finished instead of staying in the caster's pending list (one bot held 400 refused Blade Dance fluff casts and could not cast).

Diagnostics: the failure report has a `== memory` line (heap, GC heap, fragmented, committed, limit, gen0/1/2, working set, maps, entities,
queued grid actions with the largest map and the peak, spell summons and the per-map peak, pending spells, queued respawns, ledger keys,
feed queue). It is written by the report timer thread, so it is there while the world thread hangs. `gt memory show` prints the same line.
A managed heap that grows by 1 GiB within 2 minutes writes one `heap-growth` ledger entry (at most every 10 minutes) with that line.
soak.sh appends heap, committed, limit, gen2 and queued grid actions to every snapshot line of events.log.

What a crash costs: characters save every 60 s (`Player.SaveDuration`) from the map update, so a frozen map stops all saves. In the four
crashes the world was frozen from the stall start, nothing progressed after it, and the loss was at most the 60 s before the stall: the
clean-bot quest truth (1748 completions) and the database agree (all Completed, no duplicate completion), no completion fell into a stall
window. Six clean characters were last saved at a stall start (243-246 at 20:14-20:15, 241/242 at 23:48) and were not used again (the
clean-bot manager made new ones after the restart); no item slot clashes, no accepted quest without objective rows, no orphan objective rows.
Instance state and quest progress since the last save are lost. A runtime "Out of memory." ends the process without running managed
code, so nothing can be saved then. Exceptions in the world tick are now counted per part (`world-update-exception`) and the tick goes on;
an OutOfMemoryException and any unhandled exception are written to the ledger, the report and /cache/crash.log before the process ends
(`Diagnostics/CrashGuard.cs`); unobserved task exceptions are counted (`task-exception-unobserved`).

Reproduction (2026-10-22, on the gated build with 30 bots): `gt spawnnear 52647 6 any unguarded` places six proxies next to a bot with the
self-summon filter off (the fix for cause 1), so only the caps and the respawn rule hold the chain. Within 2.5 minutes one Living Grounds
cast tried to summon on more than 10 units (327 refusals `summon-refused`, the doubling of cause 2), spell summons on that map peaked at 46,
the grid action queue never went above 1108 (the gate's own peak) and the heap stayed at 2.2-2.3 GB. Five minutes later the chain had died
out: 0 spell summons, 387 refusals, no `heap-growth`. With the filter on the proxies cast nothing that summons. `scripts/summon-check.sh`
repeats this and fails when the summon peak passes 400, the queue peak 20000 or the heap grows 1 GiB (regression check; the gate also
fails on those peaks and on any `heap-growth` entry).

The heap limit in the memory line is 10752 MB with the 14 GiB container limit: the .NET default of 75% (confirmed, the runtime reports it
as TotalAvailableMemoryBytes). An "Out of memory." with docker showing 10-12 GiB is this limit, not the container's.

Fixed after the gates: every gate run showed a bot holding 400 pending Esper Mending Banner proxy casts (33346-33350, all cast in
the same tick, next event in 0 s) and 200-280 refused casts. One Mending Banner cast hit ~100 units of the open world with its telegraph and
the class-spell proxy rule cast five heals per unit from the caster. Spell4AoeTargetConstraints row 735 limits it to 1 target; the server did
not read TargetCount. Telegraph targets are now capped by it (docs/ASSUMPTIONS.md "AoE target count"); the next gate had 0 overflows.

Verified (2026-10-23, 24-hour clean-mode run, 60 bots, soak/20261022-2208): 0 restarts, 0 "Out of memory." lines in the container log,
heap flat at 1.8-2.0 GB for the full run (never approaching the 10752 MB limit), spell-summon peak and queued grid actions both near zero
throughout. The fix holds under the same conditions that produced all four crashes above.
