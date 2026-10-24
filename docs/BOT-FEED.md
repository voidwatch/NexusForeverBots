# Bot feed

`scripts/bot-feed.sh [-f] [bot-name] [last N lines]` shows what bots do, one line per event: time, bot, level, zone id, event.
Events: quest accepted / completed, stuck (quest stalled and abandoned, with objective state), level up, changed world (dungeon, event or zone travel), died, resurrected.
The server queues the lines and a background thread writes /cache/bot-feed.log (rotated at 20 MB, `.1` is the previous file). Nothing is written on the tick path and there are no movement lines.
Not yet in the feed: boss engaged/killed, loot, objective progress (planned: hook encounter and loot code).

## 2026-10-02 soak finding (the reason it exists)
soak 20261002-1709: completed-quest rows in the database did not change in 3 h with 35 quest bots (2925 rows, 562 quests). Memory rose in steps (6.6 -> 9.6 GB) when instance worlds were first loaded (worlds 1421, 990, 870, 3519, 1537, 1634 start ticking late) and then stayed flat.
