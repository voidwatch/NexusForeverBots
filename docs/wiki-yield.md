# Archive wiki yield

- pages fetched: 4284 (namespaces 0, 4, 10, 14, 500, redirects excluded)
- pages with coordinate pairs: 55 (209 coordinate rows)
- rows whose name equals a needed creature (quest giver/receiver/target without spawn): 13
- rows whose name equals any Creature2 name: 16
- spawns imported: 0. The 13 name matches are named NPCs from achievement "Locations" lists (Illium/Celestion), each name maps to 3+ Creature2 ids (same name, different versions/levels) and the achievement lists carry no world id or zone check, so placing them would be a guess; kept in research/wiki-coords.tsv only.
- Tales From Beyond The Fringe (10 KB): collectible issue locations per zone (item pickups, not NPCs); no creature, no quest data. Stored as coordinates only.

## Before / after (gt questcheck, 5194 quests)
giver-not-spawned 434 -> 434, objective-not-spawned 384 -> 384, no-giver 1530 -> 1530, no-receiver 1100 -> 1100 (unchanged).

## What the wiki did NOT fill
- Quest pages: ~200 quests across Category:*Quests, text only (no coordinates, no objective targets by id).
- NPC/mob pages: ~250, descriptive; coordinates absent.
- Boss abilities/timers: dungeon and raid pages are achievement lists and guild pages; no ability timers (RaidCore source remains the source).
- Loot tables, public event wave orders: not present beyond achievement names.
- Farside/Arcterra: stubs (see earlier crawl).
