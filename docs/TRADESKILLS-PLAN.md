# Tradeskills plan (research only, nothing implemented by this document)

## What exists
Game/Tradeskill/TradeskillManager.cs (222 lines): learn, tier xp, material check, coordinate success/crit/fail, quest credit for CraftSchematic/ObtainSchematic. Progress is stored as JSON per character in /cache/tradeskills/<id>.json (`{"Xp":{skill:xp},"Crafted":[schematic ids]}`), not in the character database. No tradeskill client messages exist in the protocol enum, so a client cannot craft; bots call the manager directly (probe `tradeskill`).

## What the tables hold (row counts from data/tbl, header record counts)
| table | rows | state |
|---|---|---|
| Tradeskill | 13 | used (learn, crafting vs gathering split) |
| TradeskillTier | 56 | used (tier xp, prerequisite 19) |
| TradeskillSchematic2 | 2867 | used (materials, output, coordinate grid) |
| TradeskillMaterial / MaterialCategory | 300 / 17 | used for material checks |
| TradeskillHarvestingInfo | 200 | unused: harvest node to loot/xp mapping |
| TradeskillAdditive | 129 | unused: crafting additives (grid shift) |
| TradeskillCatalyst / CatalystOrdering | 164 / 2 | unused: catalysts (bonus stats/crit) |
| TradeskillBonus | 248 | unused: stat bonuses applied to crafted gear |
| TradeskillTalentTier | 92 | unused: tech tree / talent points |
| TradeskillProficiency | 13 | unused |
| TradeskillAchievementLayout / Reward | 1609 / 761 | unused: tradeskill achievements |

## What is missing
1. Harvesting: nodes (resource creatures/ gatherables) have no spawns in the data; HarvestingInfo maps node to loot but placement is unknown. Needs spawn research (Jabbithole/wiki coordinates).
2. Additives and catalysts: tables loaded, effect on the grid and on output quality not implemented. Source needed for the formula (wiki Crafting pages).
3. Talents / tech tree: TalentTier rows exist, no spend mechanic, no stat effect.
4. Persistence: JSON in the cache volume survives container recreation but not a character-DB reset; move to a character DB table.
5. Client messages: none in the enum, so no real client could use any of it. Blocked for client use, not for bots.
6. Work orders and tradeskill achievements: not implemented.

## Quests that depend on it
- 384 quests currently complete through the QA tier-grant shortcut (QuestCoverage kind "tier-grant"; ledger tradeskill-qa-tier-grant): the bot is given RequiredXp of tier 2-5 instead of crafting up to it. 142 quests were originally blocked by tradeskill prerequisites (SYSTEMS.md).
- Replacing the shortcut needs a bot craft loop per skill: gather or buy materials, craft schematics of the current tier until the xp threshold, then take the quest. Needs harvest nodes (1) for gathering skills and a vendor/loot source for materials of the crafting skills.

## Order of work (when it is picked up)
1. Craft loop in bots with real xp gain from schematic crafts (the xp-per-craft value must come from Schematic2 fields; verify against the table before using).
2. Harvest nodes from scraped spawns, then HarvestingInfo loot.
3. Persistence to the database.
4. Additives, catalysts, bonus, talents after a documented formula source is found.

## Status (2026-10-08)
Done: persistence in the character DB, client messages (learn, talents, craft simple/coordinate, additives, complete, abandon; server load/update/finish/learned),
harvesting (HarvestUnit, tools, node loot from research data, tier xp, respawn), talent points from tradeskill achievements (TradeskillAchievementReward),
tradeskill achievement types, quest rewards 4/5, bots craft to quest tiers at stations (QA tier-xp grant removed; heuristic kind is now "craft-materials"),
coordinate-craft variants via additives.
Open: material sources (research/tradeskill-materials.tsv: 350 of 579 materials have no source; salvage not implemented), tool and material vendor stock,
Farmer gathering (no tool), Survivalist skinning, catalysts, TradeskillBonus effects of talents, power cores, work orders.
