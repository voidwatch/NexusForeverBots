# Quest coverage: why doable quests are not completed

## Coverage run (2026-10-07, 1 h 60-bot run on 4f33f24, 05:24-06:24)
Numbers (scripts/sample.sh, ledger quest-coverage): **3181 -> 3249 of 4274 doable quests completed at least once** in the hour (+68; the previous 1 h run on d9af472 added 46), **2702 -> 2756 without QA heuristics**, 493 rely on one (tier-grant 384, reward-pane 40, unknown10 37, indicator 34). 498 distinct quests and 1257 completions in the hour. 8 more quests need an objective in a dungeon/raid world (not counted as doable for quest bots).
Run health: 0 map-update/spell-update exceptions, world 51 172k ticks in ~61 min (~31k per 11 min), CPU after the 30 min warm-up 149 / 155 / 154 / 135 % per 9 min interval (flat, 60 bots, ~2.4 % per bot), RSS 7.28 -> 7.35 GB.
The doable count fell from 4383 (d9af472) to 4274 for data reasons, not coverage: 52 retired quests have Level < 1 (comparison decode), ~60 quests whose data-0 activate objectives were synthetic now resolve to creatures without spawns, 8 need an instance. History note: /cache is the world-cache named volume (compose), so quest-outcomes.txt survives container recreation; before that volume existed "completed at least once" came from the character DB only (a lower bound: forced tests re-take completed quests and flip their DB row back to Accepted).

gt pickwhy at the end (60 bots, 30 Exile / 30 Dominion): 1025 open, 858 prereq-quests (chain-blocked), 79 prereq-id, 41 level, 37 candidate, 10 already-in-log-or-done. The faction verdicts (26 faction-dominion) and window verdicts (32) of d9af472 are gone.

What was wrong and is fixed (live forced runs with `bot quest <id> any` and trace lines in scripts/bot-feed.sh):
- Prerequisite comparisons 3-6 were mis-mapped (seed: 3 >=, 4 >, 5 <=, 6 <). Failure texts give 3 >, 4 >=, 5 <, 6 <= (certain, docs/ASSUMPTIONS.md). Every level window, vital, resource and count check changed meaning. t1(o4065,v1,c5) is Level < 1: retired content, not doable.
- Objective text macros $m(..), $p(..), $i(..) were not read (114 data-0 activate objectives); the reward pane target group names the creatures of data-0 activate objectives (likely). objective-synthetic per 10 min gate 300 -> 16.
- Entity types AiTurret (13) and Destructible (2) spawned as Simple (not attackable); they are creature units now.
- 9 quests push an Item2 id that does not exist; QuestAdd threw. 3144 and 3224 complete now.
- Quests with flag 0x4000 (cannot abandon) that stall (9428, 10518, 6994 ...) were kept forever and steered travel; parked now. Quests whose required objective only spawns in an instance (9428: CSI 58487 in the Datascape) are not picked.
- CompleteQuest objectives: the named quests are taken as ties (6603 completes). Event-gated quests (t118 Equal 1): the bot goes into the event area once (9717, 9737 seen). "Q<id> Complete" flag achievements (4719, 4727) are granted (t7 gates of 7009 ...).
- Mondo's lab spawns sat in map-less world 3091; moved to 1634 by objective-indicator evidence; 6993 completes.
- Forced stalled roots that complete now: 7868, 5056, 5857, 7740, 6149, 9282, 6993, 7768, 7720, 5372, 6912, 4785, 6603, 3144, 3224. Their stalls were picker/hold problems (bots held raid-only or unabandonable quests), not objective logic.

Still open, with the exact reason:
- 10518 "The Face of the Enemy": objective 21288 "Interrupt the $(creature=73494), then destroy it"; 73494 is faction 219 "Friendly To All" in Creature2 and in both spawn rows, so it cannot be attacked. The real game switches it by a script that is not in the data. Parked.
- 9428: CSI 58487 only spawns in the Datascape raid (1333); needs a raid team run.
- 9707/9723 (12 chain quests): achievement 5598 (type 12, no title, no criteria); nothing says what sets it.
- 8993 objective 16257: type 10 with data 1 (the generic group 1); no creatures to resolve.
- Prerequisite types 22 and 23 (quest objective counts, client text "Quest objective requirements not met"): undecoded, strict for quests.
- 412 + 171 "picker never chose it": chain roots and quests no bot took within the hour; 858 pickwhy verdicts are chain-blocked (prerequisite quest nobody online has done). More bot-hours, not code, is the next lever there; 175 chains start at a quest that is not doable (no spawned target).

First failing step per open quest at the end of the run (questwhy, top rows):

| reason | quests | examples (quest: detail) |
|---|---|---|
| chain root (doable, open): never accepted: no refusal reason in data (picker never chose it) | 412 | 2599: quest 5630 L14 ActivateEntity/CompleteEvent/EnterA; 3160: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr; 3162: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr; 3163: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr |
| chain root: prerequisite quest not doable (an objective has no spawned target or no bot path) | 175 | 2965: quest 2964; 3001: quest 3000; 3300: quest 3295; 3301: quest 3295 |
| never accepted: no refusal reason in data (picker never chose it) | 171 | 3167: L6 ActivateTargetGroup/EnterArea/KillCreature/Succ; 3670: L5 no-objectives; 3765: L8 KillCreature; 3891: L16 ActivateEntity/SucceedCSI/TalkTo |
| accepted but stalls (objective types left open) | 42 | 3166: KillCreature/SucceedCSI; 3487: KillCreature/KillTargetGroups/Unknown10; 3555: Unknown15; 3623: ActivateEntity/KillCreature |
| never accepted: prerequisite id t6 | 36 | 4368: t6(o4367,v1,c1)+t6(o4367,v3,c2); 4830: t6(o4834,v0,c1); 4834: t6(o4830,v0,c1); 4937: t6(o4683,v0,c1)+t6(o4938,v0,c1) |
| chain root: prerequisite quest has no objectives (bots never take it) | 31 | 2531: quest 2369 type 5; 2639: quest 2723 type 0; 2775: quest 3072 type 0; 2801: quest 2790 type 0 |
| chain root (doable, open): never accepted: prerequisite id t6 | 28 | 5861: quest 5860 t6(o5859,v0,c2)+t6(o5860,v0,c1); 5864: quest 5860 t6(o5859,v0,c2)+t6(o5860,v0,c1); 5865: quest 5860 t6(o5859,v0,c2)+t6(o5860,v0,c1); 5866: quest 5860 t6(o5859,v0,c2)+t6(o5860,v0,c1) |
| chain root (doable, open): accepted, stalls: type 32 ; type 32 ; type 32 ; type 12 ; type 12  | 18 | 6613: quest 6612 SucceedCSI/VirtualCollect; 6615: quest 6612 SucceedCSI/VirtualCollect; 6616: quest 6612 SucceedCSI/VirtualCollect; 6617: quest 6612 SucceedCSI/VirtualCollect |
| chain root: prerequisite quest has no open-world spawn of its targets | 16 | 3183: quest 3180 ActivateEntity/SucceedCSI; 3188: quest 3184 CollectItem/KillTargetGroups/SucceedCSI; 3269: quest 3184 CollectItem/KillTargetGroups/SucceedCSI; 4517: quest 4516 ActivateEntity/EnterZone/KillCreature/S |
| never accepted: prerequisite id t118 | 14 | 6919: t118(o893,v1,c1); 6921: t118(o895,v1,c1); 6922: t118(o896,v1,c1); 6925: t118(o884,v1,c1) |
| chain root (doable, open): never accepted: prerequisite id t7 | 12 | 9708: quest 9707 t7(o5598,v1,c1); 9709: quest 9707 t7(o5598,v1,c1); 9710: quest 9707 t7(o5598,v1,c1); 9711: quest 9707 t7(o5598,v1,c1) |
| never accepted: prerequisite id t52 | 7 | 7032: t52(o0,v1,c1)+t118(o917,v1,c1); 7039: t52(o0,v2,c1)+t118(o921,v1,c1); 7040: t52(o0,v2,c1)+t118(o920,v1,c1); 7046: t52(o0,v0,c1)+t118(o925,v1,c1) |
| never accepted: prerequisite id t68 | 6 | 4761: t68(o4759,v2,c1); 5920: t68(o5919,v0,c1); 5960: t68(o5959,v0,c1); 6718: t68(o6716,v0,c1) |
| never accepted: prerequisite id t26 | 6 | 4969: t26(o0,v1083,c1); 5504: t26(o0,v1321,c1); 5505: t26(o0,v1321,c1); 5506: t26(o0,v1321,c1) |
| chain root (doable, open): never accepted: prerequisite id t68 | 6 | 5908: quest 5920 t68(o5919,v0,c1); 5958: quest 5920 t68(o5919,v0,c1); 6033: quest 5920 t68(o5919,v0,c1); 6721: quest 6718 t68(o6716,v0,c1) |
| chain root (doable, open): accepted, stalls: type 5 ; type 2  | 5 | 3619: quest 3623 ActivateEntity/KillCreature; 3620: quest 3623 ActivateEntity/KillCreature; 3624: quest 3623 ActivateEntity/KillCreature; 3626: quest 3623 ActivateEntity/KillCreature |
| chain root (doable, open): accepted, stalls: type 3 ; type 12 ; type 2  | 5 | 3902: quest 3901 KillCreature/SucceedCSI/TalkTo; 3932: quest 3901 KillCreature/SucceedCSI/TalkTo; 3976: quest 3901 KillCreature/SucceedCSI/TalkTo; 5944: quest 3901 KillCreature/SucceedCSI/TalkTo |
| chain root (doable, open): accepted, stalls: type 5 ; type 8  | 5 | 7799: quest 7797 ActivateEntity/KillTargetGroups; 7800: quest 7797 ActivateEntity/KillTargetGroups; 7801: quest 7797 ActivateEntity/KillTargetGroups; 7805: quest 7797 ActivateEntity/KillTargetGroups |
| chain root (doable, open): never accepted: prerequisite id t11 | 4 | 6368: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 6369: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 6371: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 7977: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1) |
| never accepted: prerequisite id t1 | 4 | 8083: t1(o0,v16,c4)+t1(o0,v17,c6); 8084: t1(o0,v16,c4)+t1(o0,v17,c6); 8475: t1(o0,v1,c1); 9168: t1(o0,v1,c1) |
| chain root (doable, open): accepted, stalls: type 17 ; type 12 ; type 10 ; type 12 ; type 25 ; type 22  | 4 | 8995: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8996: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8997: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8998: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe |
| never accepted: prerequisite id t11 | 3 | 3914: t11(o4054,v0,c2); 6424: t11(o19920,v1,c1)+t6(o6424,v0,c1); 10510: t11(o43305,v0,c1)+t11(o41782,v0,c1) |

## Before the 2026-10-07 fixes (d9af472)

Measured 2026-10-07 01:10 at the end of a 1 h run with 60 bots on commit d9af472: 3040 of 4383 doable quests completed at least once (3108 distinct quests completed in all history); the hour added 46 (2994 -> 3040). The previous hour on e527d5d added 51 (2935 -> 2986) and was flat for its last 25 minutes. At the start of the 2026-10-06 work: 1509 of 2690 (the doable set grew when the 911 quests the picker had dropped were indexed again).

## What the online bots say to each open quest (gt pickwhy, 60 bots, end of the run)
Verdict per quest: candidate (some bot could take it now), else the refusal of the bots of the right faction in the level window.

- 1343 open quests, 60 bots, written to /cache/pick-why.tsv
- 1054 prereq-quests
- 128 prereq-id
- 52 candidate
- 36 level
- 32 window
- 26 faction-dominion
- 12 already-in-log-or-done

Of the 1054 prereq-quests verdicts, 201 have their prerequisite quests completed by some bot (the chain restart in d9af472 reaches them over time) and 853 wait for a prerequisite quest nobody has completed (chain-blocked; their roots are in the questwhy table below).

## First failing step per open quest (questwhy)


Generated by `scripts/gt.sh gt questdump` + `scripts/research/questwhy.py`. A quest is doable when bots can resolve every objective and a target spawns in a world a bot can enter (or it needs no creature). The reason is the first failing step: a chain-blocked quest is charged to the root of its prerequisite chain.

Open doable quests: 1343

| reason | quests | examples (quest: detail) |
|---|---|---|
| chain root (doable, open): never accepted: no refusal reason in data (picker never chose it) | 365 | 2599: quest 5630 L14 ActivateEntity/CompleteEvent/EnterA; 3160: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr; 3162: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr; 3163: quest 3167 L6 ActivateTargetGroup/EnterArea/KillCr |
| never accepted: no refusal reason in data (picker never chose it) | 166 | 3011: L1 ActivateEntity; 3167: L6 ActivateTargetGroup/EnterArea/KillCreature/Succ; 3243: L10 ActivateEntity; 3303: L99 CollectItem |
| accepted but stalls (objective types left open) | 152 | 3144: ActivateEntity; 3166: KillCreature/SucceedCSI; 3184: CollectItem/KillTargetGroups/SucceedCSI; 3244: ActivateEntity |
| chain root: prerequisite quest not doable (an objective has no spawned target or no bot path) | 143 | 2965: quest 2964; 3001: quest 3000; 3300: quest 3295; 3301: quest 3295 |
| never accepted: prerequisite id t1 | 62 | 3479: t1(o4065,v1,c5); 3790: t1(o4065,v1,c5); 4208: t1(o4065,v1,c5); 4216: t1(o4065,v1,c5) |
| never accepted: prerequisite id t6 | 40 | 4368: t6(o4367,v1,c1)+t6(o4367,v3,c2); 4617: t6(o4508,v3,c1); 4834: t6(o4830,v0,c1); 4937: t6(o4683,v0,c1)+t6(o4938,v0,c1) |
| chain root: prerequisite quest has no objectives (bots never take it) | 32 | 2531: quest 2369 type 5; 2639: quest 2723 type 0; 2775: quest 3072 type 0; 2801: quest 2790 type 0 |
| chain root (doable, open): never accepted: prerequisite id t68 | 27 | 5078: quest 5075 t68(o5070,v3,c1)+t6(o5070,v3,c1)+t6(o50; 5079: quest 5075 t68(o5070,v3,c1)+t6(o5070,v3,c1)+t6(o50; 5080: quest 5075 t68(o5070,v3,c1)+t6(o5070,v3,c1)+t6(o50; 5305: quest 5075 t68(o5070,v3,c1)+t6(o5070,v3,c1)+t6(o50 |
| chain root (doable, open): never accepted: prerequisite id t6 | 22 | 5905: quest 5992 t6(o5958,v2,c1)+t6(o5958,v3,c1); 5907: quest 5992 t6(o5958,v2,c1)+t6(o5958,v3,c1); 6575: quest 6606 t6(o6605,v1,c1)+t6(o6605,v2,c1)+t6(o660; 6576: quest 6606 t6(o6605,v1,c1)+t6(o6605,v2,c1)+t6(o660 |
| chain root (doable, open): accepted, stalls: type 3 ; type 3 ; type 12 ; type 3 ; type 12 ; type 12  | 21 | 6244: quest 6246 SucceedCSI/TalkTo; 6247: quest 6246 SucceedCSI/TalkTo; 6249: quest 6246 SucceedCSI/TalkTo; 6251: quest 6246 SucceedCSI/TalkTo |
| chain root (doable, open): accepted, stalls: type 14  | 20 | 7874: quest 7868 ActivateTargetGroupChecklist; 7875: quest 7869 ActivateTargetGroupChecklist; 7876: quest 7868 ActivateTargetGroupChecklist; 7877: quest 7869 ActivateTargetGroupChecklist |
| chain root (doable, open): accepted, stalls: type 22 ; type 3 ; type 12 ; type 8 ; type 17  | 19 | 7007: quest 6994 EnterArea/EnterZone/KillTargetGroups/Su; 7008: quest 6994 EnterArea/EnterZone/KillTargetGroups/Su; 7009: quest 6994 EnterArea/EnterZone/KillTargetGroups/Su; 7010: quest 6994 EnterArea/EnterZone/KillTargetGroups/Su |
| chain root (doable, open): accepted, stalls: type 32 ; type 32 ; type 32 ; type 12 ; type 12  | 18 | 6613: quest 6612 SucceedCSI/VirtualCollect; 6615: quest 6612 SucceedCSI/VirtualCollect; 6616: quest 6612 SucceedCSI/VirtualCollect; 6617: quest 6612 SucceedCSI/VirtualCollect |
| never accepted: prerequisite id t118 | 16 | 6919: t118(o893,v1,c1); 6921: t118(o895,v1,c1); 6922: t118(o896,v1,c1); 6925: t118(o884,v1,c1) |
| chain root (doable, open): never accepted: prerequisite id t7 | 12 | 9708: quest 9707 t7(o5598,v1,c1); 9709: quest 9707 t7(o5598,v1,c1); 9710: quest 9707 t7(o5598,v1,c1); 9711: quest 9707 t7(o5598,v1,c1) |
| chain root (doable, open): accepted, stalls: type 2 ; type 3 ; type 8  | 10 | 3918: quest 4785 KillCreature/KillTargetGroups/TalkTo; 3930: quest 4785 KillCreature/KillTargetGroups/TalkTo; 3939: quest 4785 KillCreature/KillTargetGroups/TalkTo; 4023: quest 4785 KillCreature/KillTargetGroups/TalkTo |
| never accepted: prerequisite id t68 | 10 | 4761: t68(o4759,v2,c1); 5075: t68(o5070,v3,c1)+t6(o5070,v3,c1)+t6(o5070,v2,c1); 5671: t68(o5665,v1,c1); 5920: t68(o5919,v0,c1) |
| chain root (doable, open): accepted, stalls: type 17 ; type 22 ; type 17 ; type 14 ; type 5 ; type 3  | 10 | 6971: quest 6970 ActivateEntity/ActivateTargetGroupCheck; 6972: quest 6970 ActivateEntity/ActivateTargetGroupCheck; 6981: quest 6970 ActivateEntity/ActivateTargetGroupCheck; 6982: quest 6970 ActivateEntity/ActivateTargetGroupCheck |
| chain root (doable, open): accepted, stalls: type 14 ; type 22 ; type 10 ; type 5 ; type 3  | 10 | 7722: quest 7720 ActivateEntity/ActivateTargetGroupCheck; 7723: quest 7721 ActivateEntity/ActivateTargetGroupCheck; 7724: quest 7720 ActivateEntity/ActivateTargetGroupCheck; 7725: quest 7721 ActivateEntity/ActivateTargetGroupCheck |
| chain root (doable, open): accepted, stalls: type 18 ; type 10 ; type 3 ; type 10 ; type 3 ; type 12  | 9 | 4759: quest 4944 SucceedCSI/TalkTo/TalkToTargetGroup/Unk; 4765: quest 4944 SucceedCSI/TalkTo/TalkToTargetGroup/Unk; 4766: quest 4944 SucceedCSI/TalkTo/TalkToTargetGroup/Unk; 4767: quest 4944 SucceedCSI/TalkTo/TalkToTargetGroup/Unk |
| chain root (doable, open): accepted, stalls: type 5 ; type 5 ; type 5 ; type 5  | 9 | 5057: quest 5056 ActivateEntity; 5058: quest 5056 ActivateEntity; 5059: quest 5056 ActivateEntity; 5107: quest 5056 ActivateEntity |
| chain root: prerequisite quest has no open-world spawn of its targets | 8 | 3183: quest 3180 ActivateEntity/SucceedCSI; 4722: quest 4720 KillTargetGroup; 4782: quest 4720 KillTargetGroup; 8198: quest 8203 ActivateTargetGroupChecklist |
| chain root (doable, open): accepted, stalls: type 12 ; type 32  | 8 | 5859: quest 5857 SucceedCSI/VirtualCollect; 5860: quest 5857 SucceedCSI/VirtualCollect; 5861: quest 5857 SucceedCSI/VirtualCollect; 5864: quest 5857 SucceedCSI/VirtualCollect |
| chain root (doable, open): accepted, stalls: type 3 ; type 3 ; type 3  | 8 | 6150: quest 6149 TalkTo; 6151: quest 6149 TalkTo; 6152: quest 6149 TalkTo; 6153: quest 6149 TalkTo |
| chain root (doable, open): never accepted: prerequisite id t1 | 8 | 9071: quest 6759 t1(o4065,v1,c5); 9072: quest 6759 t1(o4065,v1,c5); 9115: quest 6761 t1(o4065,v1,c5); 9116: quest 6761 t1(o4065,v1,c5) |
| never accepted: prerequisite id t52 | 7 | 7032: t52(o0,v1,c1)+t118(o917,v1,c1); 7033: t52(o0,v1,c1)+t118(o918,v1,c1); 7039: t52(o0,v2,c1)+t118(o921,v1,c1); 7040: t52(o0,v2,c1)+t118(o920,v1,c1) |
| chain root (doable, open): accepted, stalls: type 22 ; type 22 ; type 22 ; type 5 ; type 8 ; type 5  | 6 | 4510: quest 4508 ActivateEntity/EnterArea/KillTargetGrou; 4511: quest 4508 ActivateEntity/EnterArea/KillTargetGrou; 4512: quest 4508 ActivateEntity/EnterArea/KillTargetGrou; 4615: quest 4508 ActivateEntity/EnterArea/KillTargetGrou |
| never accepted: prerequisite id t26 | 6 | 4969: t26(o0,v1083,c1); 5504: t26(o0,v1321,c1); 5505: t26(o0,v1321,c1); 5506: t26(o0,v1321,c1) |
| chain root (doable, open): accepted, stalls: type 12 ; type 12 ; type 5  | 6 | 5373: quest 5372 ActivateEntity/SucceedCSI; 5374: quest 5372 ActivateEntity/SucceedCSI; 5375: quest 5372 ActivateEntity/SucceedCSI; 5376: quest 5372 ActivateEntity/SucceedCSI |
| chain root (doable, open): accepted, stalls: type 32  | 6 | 7742: quest 7740 VirtualCollect; 7743: quest 7741 VirtualCollect; 7744: quest 7740 VirtualCollect; 7745: quest 7741 VirtualCollect |
| chain root (doable, open): accepted, stalls: type 5 ; type 8  | 6 | 7799: quest 7797 ActivateEntity/KillTargetGroups; 7800: quest 7797 ActivateEntity/KillTargetGroups; 7801: quest 7797 ActivateEntity/KillTargetGroups; 7804: quest 7797 ActivateEntity/KillTargetGroups |
| chain root (doable, open): accepted, stalls: type 2 ; type 5 ; type 5 ; type 5 ; type 2 ; type 8  | 6 | 9112: quest 10518 ActivateEntity/KillCreature/KillTarget; 9113: quest 10518 ActivateEntity/KillCreature/KillTarget; 10519: quest 10518 ActivateEntity/KillCreature/KillTarget; 10520: quest 10518 ActivateEntity/KillCreature/KillTarget |
| chain root (doable, open): accepted, stalls: type 3 ; type 5 ; type 5 ; type 5  | 6 | 9286: quest 9282 ActivateEntity/TalkTo; 9287: quest 9282 ActivateEntity/TalkTo; 9288: quest 9282 ActivateEntity/TalkTo; 9751: quest 9839 ActivateEntity/TalkTo |
| chain root (doable, open): accepted, stalls: type 5 ; type 2  | 5 | 3619: quest 3623 ActivateEntity/KillCreature; 3620: quest 3623 ActivateEntity/KillCreature; 3624: quest 3623 ActivateEntity/KillCreature; 3626: quest 3623 ActivateEntity/KillCreature |
| chain root (doable, open): accepted, stalls: type 3 ; type 12 ; type 2  | 5 | 3902: quest 3901 KillCreature/SucceedCSI/TalkTo; 3932: quest 3901 KillCreature/SucceedCSI/TalkTo; 3976: quest 3901 KillCreature/SucceedCSI/TalkTo; 5944: quest 3901 KillCreature/SucceedCSI/TalkTo |
| chain root (doable, open): accepted, stalls: type 12 ; type 12 ; type 32 ; type 12  | 5 | 6328: quest 6263 SucceedCSI/VirtualCollect; 6338: quest 6263 SucceedCSI/VirtualCollect; 6478: quest 6263 SucceedCSI/VirtualCollect; 9680: quest 6263 SucceedCSI/VirtualCollect |
| chain root (doable, open): accepted, stalls: type 23 ; type 23 ; type 23  | 5 | 8191: quest 8196 ActivateTargetGroup; 8192: quest 8196 ActivateTargetGroup; 8193: quest 8196 ActivateTargetGroup; 8194: quest 8196 ActivateTargetGroup |
| chain root (doable, open): accepted, stalls: type 12 ; type 12 ; type 12  | 4 | 4528: quest 4525 SucceedCSI; 4530: quest 4525 SucceedCSI; 4536: quest 4525 SucceedCSI; 4540: quest 4525 SucceedCSI |
| chain root (doable, open): never accepted: prerequisite id t11 | 4 | 6368: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 6369: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 6371: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1); 7977: quest 6424 t11(o19920,v1,c1)+t6(o6424,v0,c1) |
| chain root (doable, open): accepted, stalls: type 3 ; type 11 ; type 22 ; type 3  | 4 | 8096: quest 8823 EnterArea/SpellSuccess/TalkTo; 8097: quest 8823 EnterArea/SpellSuccess/TalkTo; 8098: quest 8823 EnterArea/SpellSuccess/TalkTo; 8099: quest 8823 EnterArea/SpellSuccess/TalkTo |
| chain root (doable, open): accepted, stalls: type 17 ; type 12 ; type 10 ; type 12 ; type 25 ; type 22  | 4 | 8995: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8996: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8997: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe; 8998: quest 8993 CompleteEvent/EnterArea/EnterZone/Succe |
| chain root (doable, open): accepted, stalls: type 5 ; type 5 ; type 5  | 4 | 9439: quest 9299 ActivateEntity; 9442: quest 9299 ActivateEntity; 9443: quest 9299 ActivateEntity; 9445: quest 9299 ActivateEntity |
| never accepted: prerequisite id t11 | 3 | 3914: t11(o4054,v0,c2); 6424: t11(o19920,v1,c1)+t6(o6424,v0,c1); 10510: t11(o43305,v0,c1)+t11(o41782,v0,c1) |
| never accepted: prerequisite id t43 | 3 | 4363: t43(o4362,v1,c1); 4364: t43(o4362,v2,c1); 6773: t43(o6778,v1,c1)+t43(o6778,v2,c1) |
| chain root (doable, open): accepted, stalls: type 22 ; type 5  | 3 | 5350: quest 5349 ActivateEntity/EnterArea; 5352: quest 5349 ActivateEntity/EnterArea; 5354: quest 5349 ActivateEntity/EnterArea |
| chain root (doable, open): accepted, stalls: type 14 ; type 14 ; type 8  | 3 | 6457: quest 6456 ActivateTargetGroupChecklist/KillTarget; 6463: quest 6456 ActivateTargetGroupChecklist/KillTarget; 6538: quest 6456 ActivateTargetGroupChecklist/KillTarget |
| chain root (doable, open): accepted, stalls: type 22 ; type 32 ; type 14 ; type 3 ; type 32 ; type 10  | 3 | 9959: quest 9958 ActivateTargetGroupChecklist/EnterArea/; 9960: quest 9958 ActivateTargetGroupChecklist/EnterArea/; 9961: quest 9958 ActivateTargetGroupChecklist/EnterArea/ |
| chain root (doable, open): accepted, stalls: type 17 ; type 14 ; type 12  | 3 | 10080: quest 10421 ActivateTargetGroupChecklist/EnterZone; 10423: quest 10421 ActivateTargetGroupChecklist/EnterZone; 10424: quest 10421 ActivateTargetGroupChecklist/EnterZone |
| chain root (doable, open): accepted, stalls: type 4 ; type 12 ; type 8  | 2 | 3188: quest 3184 CollectItem/KillTargetGroups/SucceedCSI; 3269: quest 3184 CollectItem/KillTargetGroups/SucceedCSI |
| chain root (doable, open): accepted, stalls: type 3 ; type 2  | 2 | 3900: quest 5843 KillCreature/TalkTo; 3970: quest 5843 KillCreature/TalkTo |
| chain root (doable, open): accepted, stalls: type 32 ; type 12 ; type 12  | 2 | 5695: quest 5694 SucceedCSI/VirtualCollect; 5696: quest 5694 SucceedCSI/VirtualCollect |
| chain root (doable, open): accepted, stalls: type 12 ; type 3 ; type 3 ; type 12  | 2 | 6047: quest 6045 SucceedCSI/TalkTo; 6072: quest 6045 SucceedCSI/TalkTo |
| never accepted: periodic (daily) group | 2 | 6488: group 22; 9659: group 20 |
| chain root (doable, open): accepted, stalls: type 24 ; type 24 ; type 24  | 2 | 6604: quest 6603 CompleteQuest; 6605: quest 6603 CompleteQuest |
| chain root (doable, open): accepted, stalls: type 8 ; type 22  | 2 | 6760: quest 6758 EnterArea/KillTargetGroups; 8913: quest 6758 EnterArea/KillTargetGroups |
| chain root (doable, open): accepted, stalls: type 17 ; type 12 ; type 5 ; type 12 ; type 23 ; type 12  | 2 | 9006: quest 9005 ActivateEntity/ActivateTargetGroup/Ente; 9007: quest 9005 ActivateEntity/ActivateTargetGroup/Ente |
| chain root (doable, open): accepted, stalls: type 17 ; type 3 ; type 17  | 2 | 9206: quest 9270 EnterZone/TalkTo; 9398: quest 9270 EnterZone/TalkTo |
| never accepted: prerequisite id t7 | 2 | 9707: t7(o5598,v1,c1); 9723: t7(o5598,v1,c1) |
| chain root (doable, open): accepted, stalls: type 10 ; type 8 ; type 2  | 1 | 3963: quest 3487 KillCreature/KillTargetGroups/Unknown10 |
| never accepted: prerequisite id t15 | 1 | 4998: t15(o0,v28350,c1) |
| never accepted: prerequisite id missing | 1 | 5052: missing |
| chain root (doable, open): accepted, stalls: type 12 ; type 14  | 1 | 7006: quest 7003 ActivateTargetGroupChecklist/SucceedCSI |
| chain root (doable, open): accepted, stalls: type 32 ; type 5 ; type 22 ; type 2  | 1 | 7350: quest 7348 ActivateEntity/EnterArea/KillCreature/V |
| chain root (doable, open): accepted, stalls: type 12 ; type 14 ; type 3  | 1 | 7853: quest 8792 ActivateTargetGroupChecklist/SucceedCSI |
| chain root (doable, open): accepted, stalls: none recorded | 1 | 8129: quest 8128 ActivateTargetGroupChecklist/SucceedCSI |
| chain root (doable, open): accepted, stalls: type 2 ; type 22 ; type 22  | 1 | 8181: quest 8179 EnterArea/KillCreature |
| chain root (doable, open): accepted, stalls: type 12  | 1 | 8851: quest 8850 SucceedCSI |
| chain root (doable, open): accepted, stalls: type 22 ; type 5 ; type 12 ; type 22 ; type 5 ; type 12  | 1 | 8914: quest 8910 ActivateEntity/EnterArea/SucceedCSI |
| chain root (doable, open): accepted, stalls: type 22 ; type 17 ; type 5  | 1 | 9088: quest 9086 ActivateEntity/EnterArea/EnterZone |

## Stalls by objective pattern

| last stall | quests | examples |
|---|---|---|
| type 32 | 10 | 4971, 6184, 6545, 6733, 7740, 7741, 8065, 8067, 8167, 9009 |
| none recorded | 9 | 3144, 3244, 3510, 3555, 3668, 3707, 3708, 7387, 8128 |
| type 14 | 9 | 4760, 7153, 7167, 7868, 7869, 8174, 8445, 10496, 10534 |
| type 8 | 8 | 6156, 6458, 9015, 9733, 9734, 9735, 9736, 9738 |
| type 2 | 6 | 3752, 3843, 5368, 6444, 6596, 7803 |
| type 3 ; type 5 ; type 4 | 6 | 9030, 9065, 9066, 9076, 9078, 9079 |
| type 12 | 3 | 4034, 7433, 8850 |
| type 32 ; type 32 ; type 32 | 3 | 5255, 5519, 7729 |
| type 3 ; type 2 | 3 | 5843, 9317, 9322 |
| type 12 ; type 14 | 3 | 7003, 7833, 7834 |
| type 5 ; type 2 | 2 | 3623, 7658 |
| type 12 ; type 12 ; type 12 ; type 12 | 2 | 6436, 6666 |
| type 12 ; type 5 | 2 | 6540, 8816 |
| type 14 ; type 22 ; type 10 ; type 5 ; type 3 | 2 | 7720, 7721 |
| type 3 | 2 | 7736, 7737 |
| type 3 ; type 3 ; type 14 ; type 12 ; type 8 | 2 | 8833, 8834 |
| type 4 | 2 | 9083, 9102 |
| type 3 ; type 5 ; type 5 ; type 5 | 2 | 9282, 9839 |
| type 3 ; type 24 | 2 | 9497, 9560 |
| type 12 ; type 12 ; type 12 ; type 12 ; type 12 ; type 18 | 2 | 9618, 9621 |
| type 32 ; type 32 ; type 14 ; type 32 ; type 22 ; type 5 | 2 | 9646, 9647 |
| type 12 ; type 2 | 1 | 3166 |
| type 4 ; type 12 ; type 8 | 1 | 3184 |
| type 10 ; type 8 ; type 2 | 1 | 3487 |
| type 3 ; type 5 | 1 | 3713 |

## What the reasons mean and what was done
- **picker never chose it**: `gt pickwhy` (d9af472) gives the real per-bot verdict; at the end of the final run see the table below.  the quest passes every check but no bot took it in the run. In the dump before 8254dc7, 182 of the 190 root quests of this kind were chain follow-ups whose prerequisite quest some bot had completed: only that bot can take them, and the picker chose at random among everything open. Bots now take their own chain follow-ups first (listed prerequisite quests or a type 6 Equal row), also below their level window. Earlier (458e650) the picker had dropped 911 quests whose objectives EnterArea/EnterZone/CollectItem/CompleteEvent/SpellSuccess had become bot-ready without a picker case.
- **prerequisite id t19**: TradeskillTier (decoded). Bots learn a tier-1 tradeskill for a quest (crafting skills spread over bots in fixed pairs) and, as a QA shortcut, get the RequiredXp of tiers 2-5 (ledger tradeskill-qa-tier-grant). Remaining t19 quests need a crafting skill the bot's fixed pair does not include. (the tier xp is now earned by crafting at a station; the heuristic kind is "craft-materials" because materials are still QA grants.)
- **prerequisite id t1** (object set, value 1, LessThanOrEqual; rows 4106/6232/14643): level <= 1, quests granted to new characters ("Welcome to Northern Wilds", "Ability Training 101", faction intros) with con levels 1-50. The picker now places them at level 1; a bot reaches them when level rotation sends it to level 1.
- **prerequisite id t6 / t68 / t43**: another quest accepted/achieved (6), one of its objectives done (68) or in progress (43, decoded in a9e78c0 from the creature gates). Bots take such quests while holding the other one (tied quests).
- **prerequisite id t26**: be in a sub zone of the quest zone. Bots travel into the zone once (ledger bot-quest-zone-trip).
- **prerequisite id t118**: a public event objective must be active; opens only while the event runs.
- **chain root: no objectives**: hidden flag quests (no title, giver or communicator message) granted by scripts; no data says when.
- **chain root: not doable**: the prerequisite quest has an objective whose creatures spawn nowhere or only in instances (data gap; quest 9800 group 12468 is Dreadmoor/Coralus cut content, referenced by no other table).
- **accepted, stalls**: the bot took the quest and made no progress for the stall timeout; the open objective types are listed. Fixed in this task: activations gated by type 43 (objective in progress), creatures whose first activate slot needs a challenge or a fail aura (first slot whose prerequisite is met is cast, 156 creatures), types 13/54/246/160. activation-prerequisite-failed went from 116 to 5 per 10-minute gate. Activate objectives with data 0 and no named creature use the one activatable creature kind within 30 m of the objective indicator (145 objectives; 224 with two or more kinds stay synthetic). Still synthetic: CollectItem without a known source, Unknown31 work orders (server-only table), ambiguous indicators.

## 10 h soak (soak/20261007-0731, 60 bots, commit a0955ff)
- 3256 -> 3266 of 4274 doable completed at least once (2762 -> 2772 without QA heuristics). Last 2 h (snapshots 48-59): +1 (3265 -> 3266). Saturated.
- CPU 116-163% (avg 134%) flat per hour, memory 6.1-6.99 GB flat (no growth), 0 map-update/spell-update exceptions.
- Every one of the 1008 open doable quests has its first failing step and the picker verdict in docs/OPEN-QUESTS.tsv (from questwhy + gt pickwhy at the end of the run). 493 wait for a prerequisite quest that nobody completed (chain roots listed in the same file), 79 are in a log or done, the rest are stalls, prerequisite ids (t6/t7/t26/t52/t68/t118), unspawned targets or no-objective roots as listed.

## Forced sweep (scripts/force-sweep.sh, server `bot sweep`)
Every open doable quest was handed to a free quest bot through the forced path (prerequisite checks skipped, level set to the quest's), bounded per quest (8 min, 15 min in the last run), 60 bots in parallel; after a completion the same bot took the quest's follow-ups. One line per quest in docs/SWEEP-RESULTS.tsv (run, outcome, via, normal picker verdict for that bot, objective progress, last trace, credit kind, heuristic kind).
- Before: 3266 / 4274 doable completed (10 h soak, saturated).
- Run 1 (roots, follow-ups by chain): 583 completed. 301 of them were refused by the normal picker as already-in-log-or-done: CanAcceptWithoutGiver refused quests in state Mentioned (communicator mention when the previous chain quest is handed in; 3772 rows in character_quest). Fixed (same rule as QuestAdd).
- Run 2 (all 384 still open, chain-blocked ones with prerequisite quests bypassed): 299 completed; 37 stalls on kill targets not hostile to the bot (fixed, likely: the quest holder may damage them), 21 not takeable because the bag was full (bots now clear items no quest uses).
- Runs 3/4 (what was left): 55 completed.
- Distinct quests completed by the sweep: 937 = 320 root + 307 chain (real prerequisites) + 310 with prerequisite quests bypassed. Credit: 666 with real objectives only, 237 used synthetic credit for an objective, 34 a public event given up.
- After: 4249 / 4274 doable (3712 without QA heuristics). This counts the 310 bypass completions; they show the quest itself works, not that its chain is reachable.
- Normal picker, 2 h with 60 bots after the fixes (be71fea): 535 completions, 319 distinct quests; 135 of the 1008 quests open before the forced sweep completed by the normal picker, 49 of which it had refused as already-in-log-or-done before the fix. 0 map/spell-update exceptions, CPU 108-155 % flat, memory 6.7-7.1 GB.
- The 25 open doable quests left each have an exact reason in docs/OPEN-QUESTS.tsv (scripts/research/openwhy.py): harvest nodes (HarvestUnit spawns as Simple, harvesting and node loot not implemented) 6, Unknown10 scripted objectives 4, CompleteQuest of a quest bots cannot do (Starfall seasonal, expedition counter) 3, objective creature not found near its spawn point 3, world bosses with 8-14 M health that need a group 2, still progressing at the time bound 2, activation gives no item (object loot not in the data) 2, friendly target of a VirtualCollect objective 1, activation refused by the caster prerequisite 1, progress then no credit 1.

## Why doable fell 4274 -> 4229 and completed 4254 -> 4205 (soak/20261009-0303)
Not a code change. Commit 08910c7 (scripts/research/fix_instance_creatures.py) moved or dropped the researched open-world spawns of 10 dungeon/raid
creatures (Creature2 description tags [STL] [SK] [KV] [GA] [RMT] [DS]: Jabbithole pages of capital/open zones had put them in worlds 22, 51, 1061, 1421, 1610).
The world DB had that import at the 03:03 soak and later held the older rows again; scripts/import-research-spawns.sh re-run 2026-10-09 17:30 makes the DB match
data/spawns again. Reproduced with the same build and env: 4250/4274 on the old rows, 4205/4229 on the committed rows (scripts/bench.sh fix-d1263).
The 49 completed quests that targeted those creatures were completed in the open world against bosses that do not stand there; 4 of them stay doable
through other target-group members (5454, 9296, 9947, 10102). The other 45 now count under "need an objective in a dungeon/raid world" (that counter
tested the wrong side, `!IsInstancedWorld`, and is fixed: 8 -> 55). 40 of the 45 need the veteran (Prime) boss. Veteran instances now spawn the
veteran rows (docs/INSTANCES.md "Veteran (Prime) runs"), all 11 worlds with veteran rows were cleared on veteran by bot teams, and the veteran kills
achieve these quests through their real KillCreature objectives (Achieved count for 9342/9363/9364/9371/9733/9734 rose with the runs); dungeon
bots turn them in since then. The Prime dailies 9843-9848 are taken only on days the periodic offer holds them. The other 5 need the normal boss
inside its instance (dungeon team bots).
Side effect: Skullcano 1263 now has Bosun Octog and Mordechai Redmoon and clears with 4 bosses (101 s, team of 5); before it was "no boss".

| quest | title | level | zone | target (moved creature) | objective | reason |
|---|---|---|---|---|---|---|
| 9305 | Live at the Archive | 50 | 4162 | Dreadphage Ohmna [GA] 49395 | - | only target is a dungeon/raid boss, spawn now in its instance |
| 9340 | Here Come the Osun | 1 | 4162 | Forgemaster Trogun veteran [KV] 32533 | Complete the Ruins of Kel Voreth on Prime difficulty | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9342 | Skydivers | 1 | 4162 | Stormtalon veteran [STL] 33406 | Complete Stormtalon's Lair on Prime difficulty | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9344 | The Golden Touch | 1 | 4162 | Mordechai Redmoon veteran [SK] 24895 | Complete Skullcano Island on Prime difficulty | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9361 | Howling Mad | 1 | 4162 | Aethros [STL] 17166 | Defeat $(creature=17166) | only target is a dungeon/raid boss, spawn now in its instance |
| 9363 | Fish Fry | 1 | 4162 | Bosun Octog [SK] 24486; Bosun Octog veteran [SK] 24894 | Defeat $(creature=24486) | only target is a dungeon/raid boss, spawn now in its instance |
| 9364 | Who's the Bosun? | 1 | 4162 | Bosun Octog veteran [SK] 24894 | Defeat $(creature=24894) on Prime difficulty | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9371 | Taking by Storm | 20 | 4162 | Stormtalon veteran [STL] 33406 | Kill $creature=17163 | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9489 | Ohmna-Potent | 50 | 4162 | Dreadphage Ohmna [GA] 49395 | Defeat the Dreadphage Ohmna | only target is a dungeon/raid boss, spawn now in its instance |
| 9559 | Ohmna-Potent | 50 | 4162 | Dreadphage Ohmna [GA] 49395 | Defeat the Dreadphage Ohmna | only target is a dungeon/raid boss, spawn now in its instance |
| 9583 | Clearing the Air | 20 | 4162 | Aethros [STL] 17166 | Kill $creature=17166 | only target is a dungeon/raid boss, spawn now in its instance |
| 9733 | [Dungeon] Stop the Ritual | 20 | 271 | Stormtalon veteran [STL] 33406 | Defeat $(creature=33406) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9734 | [Dungeon] Stop the Ritual | 20 | 271 | Stormtalon veteran [STL] 33406 | Defeat $(creature=33406) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9735 | [Dungeon] Fall of the Forgemaster | 25 | 1329 | Forgemaster Trogun veteran [KV] 32533 | Defeat $(creature=32533) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9736 | [Dungeon] Fall of the Forgemaster | 25 | 1329 | Forgemaster Trogun veteran [KV] 32533 | Defeat $(creature=32533) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9738 | [Dungeon] Remove the Redmoon | 35 | 1220 | [SK] 24489 24489; Mordechai Redmoon veteran [SK] 24895 | Defeat $(creature=24489) | only target is a dungeon/raid boss, spawn now in its instance |
| 9740 | [Dungeon] Remove the Redmoon | 35 | 1220 | [SK] 24489 24489; Mordechai Redmoon veteran [SK] 24895 | Defeat $(creature=24489) | only target is a dungeon/raid boss, spawn now in its instance |
| 9843 | Dungeon: Prime Stormtalon's Lair | 50 | 271 | Stormtalon veteran [STL] 33406 | Defeat $(creature=33406) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9844 | Dungeon: Prime Stormtalon's Lair | 50 | 271 | Stormtalon veteran [STL] 33406 | Defeat $(creature=33406) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9845 | Dungeon: Prime Ruins of Kel Voreth | 50 | 1329 | Forgemaster Trogun veteran [KV] 32533 | Defeat $(creature=32533) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9846 | Dungeon: Prime Ruins of Kel Voreth | 50 | 1329 | Forgemaster Trogun veteran [KV] 32533 | Defeat $(creature=32533) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9847 | Dungeon: Prime Skullcano | 50 | 1220 | Mordechai Redmoon veteran [SK] 24895 | Defeat $(creature=24895) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9848 | Dungeon: Prime Skullcano | 50 | 1220 | Mordechai Redmoon veteran [SK] 24895 | Defeat $(creature=24895) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9909 | Expedition: Prime They Came from Fragment Zero! | 50 | 4618 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9912 | Expedition: Prime They Came from Fragment Zero! | 50 | 4618 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9915 | Expedition: Prime Outpost M-13 | 50 | 1184 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9916 | Expedition: Prime Outpost M-13 | 50 | 1184 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9917 | Expedition: Prime Infestation | 50 | 1157 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9918 | Expedition: Prime Infestation | 50 | 1157 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9919 | Expedition: Prime Rage Logic | 50 | 1642 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9920 | Expedition: Prime Rage Logic | 50 | 1642 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9921 | Expedition: Prime Space Madness | 50 | 2411 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9922 | Expedition: Prime Space Madness | 50 | 2411 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9923 | Expedition: Prime Deep Space Exploration | 50 | 2624 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9924 | Expedition: Prime Deep Space Exploration | 50 | 2624 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9925 | Expedition: Prime The Gauntlet | 50 | 2608 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 9926 | Expedition: Prime The Gauntlet | 50 | 2608 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10110 | Expedition: Prime Evil from the Ether! | 50 | 4816 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10111 | Expedition: Prime Evil from the Ether! | 50 | 4816 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10144 | Forging Ahead | 1 | 4162 | Forgemaster Trogun veteran [KV] 32533 | Defeat $(creature=32531) | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10464 | Dungeon Crawler | 1 | 4162 | Mordechai Redmoon veteran [SK] 24895; Forgemaster Trogun veteran [KV] 32533; Stormtalon veteran [STL] 33406 | Complete a Prime Dungeon | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10576 | World Story: OMNICore-1 | 50 | 4385 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10577 | World Story: OMNICore-1 | 50 | 4385 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10578 | World Story: Vault of the Archon | 50 | 4487 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |
| 10579 | World Story: Vault of the Archon | 50 | 4487 | Stormtalon veteran [STL] 33406 | - | veteran (Prime) boss: open-world research spawn dropped, no instance spawn for the veteran version |

## Open groups of the 36 h clean soak (soak/20261019-0021/quest-truth-open.tsv)

Cause per group, measured from the file, `gt quest`, `gt event`, `gt worlds` and /cache/quest-check.txt. No synthetic credit was added.

| group | quests | cause | kind |
|---|---|---|---|
| stalled | 124 | 11 were cut content (zone 1480, Halon Ring): bots no longer pick cut quests (fixed). Of the rest, 109 have every target spawned (quest-check finds nothing), so the stall is the bot run, not the data. First open objective: CompleteEvent 24, KillTargetGroups 19, KillCreature 14, SucceedCSI 13, VirtualCollect 16, TalkTo 6, ActivateTargetGroupChecklist 5, others 27. | bot logic, 11 cut |
| stalled, CompleteEvent (24 of the 124) | 24 | The events are inside instances quest bots do not run: adventures (1233 Galeras 5, 1393 Northern Wilds 1, 1149 Hycrest 1, 1181 Malgrave 1; 1233 and 1393 have 0 spawns), dungeons and raids (1263, 1336, 382, 1333 Datascape 3, 1462 Genetic Archives 2). 5 name a PublicEventObjective id that is not in the table (e.g. 226); 2 are open-world events in world 22. | data gap (adventure spawns, 5 missing objective rows), bot logic (instance quests for quest bots) |
| candidate | 102 | Some online clean bot could take them but none had picked them yet: 42 are the type 11 "Help is needed in <zone> N" breadcrumbs of zone 12 at level 3 (no giver creature, offered by the communicator in the real game) and most others are level 45-49 (zones 2067, 2121, 2033) where only 1-2 clean bots were at the end. | bot logic (communicator breadcrumbs, level spread of the clean bots) |
| prereq-id row | 67 | The failing prerequisite rows are decoded and correct: quest state (type 6, incl. "none of" rows for mutually exclusive alternatives the bot already chose) 14, path (52, Explorer/Soldier only) 11, tradeskill tier (19, the bot lacks the tier) 9, objective of another quest (68) 6, zone (26, must stand in zone 1321) 6, others 3; 18 have no single row ("none"). | bot logic (path, tradeskill and zone of the bot), by design for the alternatives |
| faction-dominion | 44 | Level 47-49 Dominion quests; the only clean bot in their window at the end was Exile. | bot population (one faction at the cap) |

Known limitations left from this: adventure worlds 1233 and 1393 have no spawn data; quest bots do not run instance quests (team bots do,
docs/INSTANCES.md); communicator breadcrumb quests (type 11) have no bot driver; five CompleteEvent objectives point at missing rows.
