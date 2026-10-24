# Client message formats: what is known without a client (2026-10-05)

Question: can the blocked systems (player trading, chat channels, group loot roll / master loot / loot rules, veteran/difficulty choice,
auction and commodity exchange) get real client message handlers? Rule: implement a layout only with two independent confirmations
or the client code itself.

## Sources checked
| source | what it gives | independent? |
|---|---|---|
| Client binary (WildStar64.exe, local copy, 13,927,576 bytes, PE timestamp 1537224357 = 2018-09-17) | the client's own message registry, serializers, deserializers, Lua API bindings and server-message dispatch. Static analysis only (pefile + capstone, local scripts not shipped), see "Client binary static analysis" below. | yes (Carbine code) |
| Client UI Lua (ClientData `UI/*/*.lua`, 203 files, extracted read-only by `scripts/research/wsarchive.py`) | Carbine's own addons: names and argument lists of the client API calls (`P2PTrading.*`, `GameLib.RollOnLoot`, `GameLib.AssignMasterLoot`, `GameLib.SetInstanceSettings`, `GroupLib.SetLootRules`, `MarketplaceLib.*`, `ChatSystemLib.JoinChannel`). Gives field sets and meaning, not wire order or bit widths. | yes (Carbine) |
| NexusForever `develop` (this repo's base) | chat channel messages with handlers (join, leave, kick, list, password, owner, moderator, mute, chat), used by real clients since 2020 | yes |
| NexusForever `game_rework` branch (943 opcodes vs 361 here) | opcode names and message models for trade, loot, instance settings, marketplace. Nearly all added by one contributor (krakal) in 2025 PRs #492, #505, #506, #508, #519, #524, as models only, with no handler. Loot rules: MaxtorCoder 2021 "Initial Party Implementation", with a handler. | one source per message |
| MaxtorCoder/WildstarPacketParser (parser for retail sniffs) | group messages only. Reads the ServerGroupJoin loot rule fields as 3/3/4/2 bits with the same LootRule/LootThreshold/HarvestLootRule values as this repo. Auction: only a counter in ServerOwnedItemAuctions. | yes (retail sniffs) |
| Bezgelor (research/bezgelor, Elixir) | loot/instance packets with made-up opcodes (0x0A25/0x0A26, byte-sized fields) that conflict with the enum. Not a source. | rejected |
| NexusForever PR #364/#280 (kirmmin, 2021) | ClientLootItem/Vacuum/ItemUseLootBag handlers. No roll, master or settings messages. | - |

Raw copies: `research/protocol/` (gitignored), fetched by `scripts/research/protocol_fetch.sh`; one line per upstream message
in `research/protocol/layouts.tsv` (`scripts/research/protocol_layouts.py`).

## Result per blocked message
Confidence: certain = client code or 2+ independent sources agree on opcode and layout; likely = one bit-level source plus independent agreement on fields;
single = one source only (not implemented); none = no source.

| system | message (opcode) | layout (upstream game_rework) | confidence | status |
|---|---|---|---|---|
| chat channels | ClientChatJoin 0x01BA, ClientChatLeave 0x01BE, ClientChatKick 0x01BD, ClientChatList 0x01D2, ClientChatPassword 0x01D0, ClientChatOwner 0x01CF, ClientChatModerator 0x01CD, ClientChatMute 0x01CE, ClientChat 0x01C3 | Type:14 bits, Name/Password wide strings, Order u32 / Channel(Type:14, ChatId u64) | certain (develop handlers used by real clients, game_rework the same, UI `ChatSystemLib.JoinChannel`) | **already handled here** (Network/Message/Handler/SocialHandler.cs), now exercised by the `chatchannel` probe |
| group loot rules | ClientGroupLootRulesChange 0x042E | GroupId u64, NormalRule:3, ThresholdRule:3, ThresholdQuality:4, HarvestRule:2 | likely: MaxtorCoder model + handler; field order = UI `GroupLib.SetLootRules(eNormalRule, eThresholdRule, eThresholdQuality, eHarvestRule)`; widths and enum values = ServerGroupJoin in the retail sniff parser. The opcode comes only from upstream. | **implemented** (GroupHandler, GroupManager.SetLootRules, probe `lootrules`) |
| group loot rules (echo) | ServerGroupLootRulesChange 0x042F | GroupId u64, Unknown u32 (0), the same 4 fields | likely (fields as above, one unknown u32 sent as 0 as upstream does) | **implemented** |
| loot roll | ClientLootRollAction 0x015D | OwnerUnitId u32, LootUnitId u32, Action:2 (Need 0, Greed 1, Pass 2) | single. UI `GameLib.RollOnLoot(nLootId, bNeed)` / `PassOnLoot(nLootId)` passes one loot id, not two unit ids. | blocked |
| master loot | ClientLootAssignMaster 0x00B5 | OwnerUnitId u32, LootUnitId u32, Assignee Identity | single. UI `GameLib.AssignMasterLoot(nLootId, unitLooter)` has the same mismatch. | blocked |
| loot roll results | ServerLootRoll 0x08A2, ServerLootWinner 0x08A3, ServerLootGrant 0x08A5, ServerLootNotify 0x07F2 | see layouts.tsv | single | blocked |
| instance difficulty | ClientSetInstanceSettings 0x0163 | InstancePortalUnitId u32, Difficulty:2, PrimeLevel u8, Rally:11 | single at bit level. UI `GameLib.SetInstanceSettings(eDifficulty, nRally, nPrimeLevel)` agrees on fields, not on order or widths. The server side (ServerShowInstanceGameModeDialog 0x0164) has unknowns. | blocked |
| instance utility | ClientClosedInstanceSettings 0x00D2, ClientResetSingleInstance 0x0153, ClientResetInstances 0x07B4, ClientLeavePendingRemovalInstance 0x057C | portal unit id u32 / empty | single (UI calls `GameLib.ResetSingleInstance`, `OnClosedInstanceSettings`, `LeavePendingRemovalInstance` exist) | blocked |
| player trading | ClientP2PTradingInitiateTrade 0x0192, AcceptInvite 0x018C, DeclineInvite 0x0191, AddItem 0x018D, RemoveItem 0x0194, SetMoney 0x0197, Commit 0x0190, CancelTrade 0x018F; ServerP2PTradeInvite 0x0193, UpdateItem 0x018E, ItemRemoved 0x0195, UpdateMoney 0x0198, Result 0x0196 | TargetUnitId u32; ItemGuid u64; ... | single and inconsistent: RemoveItem reads the item guid as u32 while AddItem reads u64, SetMoney reads u32 into a u64 property, ServerP2PTradeUpdateItem has 5 unknown fields (PR #492: "still some unknown fields"). The UI also calls `P2PTrading.UnCommit`, which has no message in upstream. | blocked |
| auction house | ClientAuctionSellOrderSubmit 0x06DC, BuyOrderSubmit 0x055E, Cancel 0x0094, AuctionsByFilterRequest 0x07DC, RequestOwnedItemAuctions 0x03ED + 9 server messages | see layouts.tsv | single (the sniff parser only reads a count from ServerOwnedItemAuctions) | blocked |
| commodity exchange | ClientCommoditySellOrderSubmit 0x06DA, OrderCancel 0x0093, RequestCommodityInfo 0x03E6, RequestOwnedCommodityOrders 0x03EC + 5 server messages | see layouts.tsv | single | blocked |
| daily login claim, resource conversion | none found | - | none | blocked |

## Client binary static analysis (2026-10-05)
Method: `pip install pefile capstone` (no Ghidra/radare2 needed). A local disassembly script (not shipped) disassembles every .pdata function
(2.3 M instructions, research/clientre/dis.txt, gitignored). The exe has no RTTI and no message names; structure comes from code:
- Message registry: two registration functions (1097 entries) call `register(opcode, structSize, bitSizeFn, writeFn | readFn)` through a
  vtable. Client messages get a bit-size fn and a writer, server messages a reader (written to research/clientre/registry.tsv).
- Bit writer helpers: WriteBits(value, n), u32 with n bits, u64, byte with n bits, float, wide string (1 bit long flag, 7/15 bit length,
  UTF-16), raw bytes. Readers: u32 with n bits, u64, u8 with n bits, u16 with n bits, float, wide string, raw bytes.
  A layout script prints each serializer as a field list (one line per field with bit width and struct offset).
- Send: one send function (net, opcode, struct*). The Lua API tables ({name, cfunc} pairs, 3211 entries) are mapped to the opcodes they send.
- Server dispatch: one dispatch function, `switch (opcode - 0x61)` through a jump table, 645 handled opcodes (research/clientre/handlers.tsv).
  Handler code gives field meaning (which struct offset feeds which Lua event field or state).

Bit packing is the same as GamePacketWriter here (LSB first, no alignment), so a client "u32 count + count*4 raw bytes" equals count u32 writes.

| message (opcode) | client code (serializer / user) | layout from the client | upstream game_rework | confidence | status |
|---|---|---|---|---|---|
| ClientLootRollAction 0x015D | client writer; `GameLib.RollOnLoot`, `PassOnLoot` | u32 OwnerUnitId (looked up by the client from the loot id), u32 LootUnitId (= Lua nLootId), Action:2 (RollOnLoot: need 0 / greed 1, PassOnLoot: 2) | same | certain | handler: counter only (no loot-window pipeline, see below) |
| ClientLootAssignMaster 0x00B5 | client writer; `GameLib.AssignMasterLoot` | u32 OwnerUnitId, u32 LootUnitId, Identity (RealmId:14, CharacterId u64) | same | certain | handler: counter only |
| ServerLootRoll 0x08A2 | client reader | u32 LootUnitId, Identity, ItemId:18, Action u32 | same | certain | model only |
| ServerLootWinner 0x08A3 | client reader | u32 LootUnitId, {Identity, u32 Value}, ItemId:18, u32 count, count x {Identity, u32 Value} | same | certain | model only |
| ServerLootGrant 0x08A5 / ServerLootNotify 0x07F2 | client readers, LootItem | Grant: u32 owner, u32 looter, LootItem. Notify: u32 owner, u32 parent, Explosion:1, u32 count, LootItems. LootItem: u32 LootUnitId, u32 Type, u32 ItemId, u32 Amount, 4 x 1 bit (CanLoot, RequiresRoll, OnlyMasterLootable, Explosion), u32 RollTime, u64 circuit, u32 glyph, u32 quality, u32 count, count x Identity | same | certain | model only |
| ClientSetInstanceSettings 0x0163 | client writer; `GameLib.SetInstanceSettings` | u32 InstancePortalUnitId, Difficulty:2, PrimeLevel:8, Rally:11 | same | certain | **implemented** (difficulty for the next instance of that world) |
| ClientClosedInstanceSettings 0x00D2, ClientResetSingleInstance 0x0153 | client writer | u32 (portal unit id) | same | certain | **implemented** |
| ClientResetInstances 0x07B4, ClientLeavePendingRemovalInstance 0x057C | client writer | empty | same | certain | ResetInstances **implemented** |
| ServerShowInstanceGameModeDialog 0x0164 | client reader | u32 portal, Difficulty:2, Parameters:11, ExistingDifficulty:2, u8 MaxPrimeWorld, u8 MaxPrimeGroup, u8 ScalingLevel, ExistingScaling:1, u8 ExistingPrime, IsRaid:1, PrimeAllowed:1 | same | certain | model only |
| ServerHideInstanceGameModeDialog 0x00D3 | client reader | **u32** (upstream: empty) | differs | certain | model |
| ServerInstanceSettings 0x00F1 | client reader | Difficulty:2, u32 PrimeLevel, Flags:8, u32 interval | same | certain | already sent here |
| ClientP2PTradingInitiateTrade 0x0192 | client writer; `P2PTrading.InitiateTrade` | u32 target unit id | same | certain | **implemented** |
| ClientP2PTradingAcceptInvite 0x018C, DeclineInvite 0x0191, CancelTrade 0x018F | client writer | empty | same | certain | **implemented** |
| ClientP2PTradingCommit 0x0190 | Commit and `P2PTrading.UnCommit` both send it | empty: one message toggles the commit state | same layout; upstream has no UnCommit | certain | **implemented** as a toggle |
| ClientP2PTradingAddItem 0x018D, RemoveItem 0x0194, SetMoney 0x0197 | client writer (all three) | **u64** (item guid / credits) | RemoveItem and SetMoney read u32 upstream (wrong) | certain | **implemented** |
| ServerP2PTradeInvite 0x0193 | client reader, client handler | u32 inviter unit id | same | certain | **implemented** |
| ServerP2PTradeUpdateItem 0x018E | client reader, list entry 0x50 bytes, Lua `GetTradeItems` | u32 TradeIndex, u32 OwnerUnitId (bIsMine), Item2Id:18 (nItemId), u64 ItemGuid, u32 Quantity (nQuantity), u64, u32, u64, Item2Id-like:18, **20 raw bytes** | upstream lacks the 20 bytes | certain (layout); the 4 unknown fields build the item instance (sent as 0) | **implemented** |
| ServerP2PTradeItemRemoved 0x0195 | client reader, client handler | u64 ItemGuid | same | certain | **implemented** |
| ServerP2PTradeResult 0x0196 | client reader, client handler | Result:5, Cancelled:1. Handler: 3 = invite accepted, 6/7 initiator (un)committed, 8/9 target (un)committed, 11 finished, 18 event only, rest = error text | same enum | certain | **implemented** |
| ServerP2PTradeUpdateMoney 0x0198 | client reader, handler client dispatch case | **u32 UnitId, then u64 Credits** (unit id picks partner or own amount) | reversed upstream (Credits first) | certain | **implemented** |
| ClientAuctionSellOrderSubmit 0x06DC | client writer | u64 ItemGuid, u64 MinimumBid, u64 Buyout | same | certain | **implemented** |
| ClientAuctionBuyOrderSubmit 0x055E | client writer | Item2Id:18, u64 AuctionId, u64 Amount | same | certain | **implemented** |
| ClientAuctionCancel 0x0094 | client writer | Item2Id:18, u64 AuctionId (upstream: AuctionId then Item2Id) | order differs | certain | **implemented** |
| ClientAuctionsByFilterRequest 0x07DC | client writer, filter + client table | Family:14, Category:14, Type:14, u32 n + n u32 item ids, u32 n + n filters {Type:3, body}, u32 ReverseSort, Sort:3, Property:8, u32 Page. Bodies: 0/1 Property:8 + float, 2 u32 + u32, 3 Quality:4 + 4, 4 Microchip:5 + **u32**, 5 u64, 6 Race:14 Class:14 Faction:14 u32 | same except RuneSlot minimum (u8 upstream) | certain | **implemented** (item id / family / category / type / page) |
| ClientRequestOwnedItemAuctions 0x03ED, RequestOwnedCommodityOrders 0x03EC | empty | empty | same | certain | **implemented** |
| ClientRequestCommodityInfo 0x03E6 | client writer | Item2Id:18 | same | certain | **implemented** |
| ClientCommoditySellOrderSubmit 0x06DA (buy and sell) / ServerCommodity order struct | client writer / client reader | CommodityOrder: u64 Id, Item2Id:18, u32 Quantity, u64 PricePerUnit, u64 Price, IsBuyOrder:1, ForceImmediate:1, u64 ListTime, u64 Expiration | same | certain | **implemented** |
| ClientCommodityOrderCancel 0x0093 | client writer | u64 OrderId, Item2Id:18, IsBuyOrder:1 | same | certain | **implemented** |
| ItemAuction struct (server 0x055F/0x0561/0x0562/0x0564 = struct only) | client reader | u64 x7 (Id, Owner, MinBid, Buyout, CurrentBid, TopBidder, Expiration), Item2Id:18, u32 Quantity, Item2Id:18, count:3 + count u32, u64, u32, u64, u32 | same (AuctionInfo) | certain | **implemented** |
| ServerAuctionBidResult 0x0560, PostResult 0x06DD | client reader | Result:8, ItemAuction | same | certain | **implemented** |
| ServerAuctionCancelResult 0x0565 | client reader | ItemAuction, then Result:8 | same | certain | **implemented** |
| ServerAuctionSearchResults 0x0563 / ServerOwnedItemAuctions 0x064D | client readers | u32 total, u32 page, u32 n, n auctions / u32 n, n auctions | same | certain | **implemented** |
| ServerCommodityOrderResult 0x06DB | client reader | Result:8, CommodityOrder, u64 cost | same | certain | **implemented** |
| ServerCommodityAuctionFilledPartial 0x024A / Removed 0x024B | client reader | CommodityOrder, u32 type | same | certain | model |
| ServerCommodityInfoResults 0x024C | client reader | Item2Id:18, u32 buy count, 3 x u64 buy prices (1/10/50 buckets), u32 sell count, 3 x u64, u32 n, n orders | same | certain | **implemented** |
| ServerOwnedCommodityOrders 0x064C | client reader | u32 n, n orders | same | certain | **implemented** |
| ServerMarketplaceStatus 0x0640 | client reader | u32 | same | certain | model |
| ClientGroupLootRulesChange 0x042E / ServerGroupLootRulesChange 0x042F | client writer / client reader | u64 group, 3, 3, 4, 2 / u64 group, u32, 3, 3, 4, 2 | same | certain (was likely) | implemented |
| ClientResourceConversion 0x00D7 (new) | client writer; `unit:ConvertResource(idConversion, nCount)` | ConversionId:14, u32 Count (source units), u64 (0 from the vendor UI; another caller passes an object id) | not upstream | certain (layout); u64 meaning unknown | **implemented** (vendor path) |
| ClientDailyLoginRewardsRequest 0x078F | `AccountItemLib.RequestDailyLoginRewards` (sent on the realm connection object) | empty | same opcode | certain | **implemented** |
| ServerDailyLoginUpdate 0x096E | client reader, client handler | u32 x4 (stored at state +0x180..+0x18c; +0x180 = tier, +0x188 = login day used by the claim check), u32 ms timer, float, State:3 (4 = claim allowed) | same opcode, no model | certain (layout), field names partly unknown (UI: nLoginDaysTotal, nRewardsAvailable, ePremiumKeyStatus) | not sent (field meaning not certain) |

Loot roll and master loot (now resolved by Game/Groups/LootWindow, see below). Before: the layouts are certain, but this server had no loot window at all (no ServerLootNotify/LootItem pipeline,
items go straight into bags through GroupLoot.Choose). A roll handler needs pending loot units first; until then the handlers count
the message in the ledger (`loot-roll-action`, `loot-master-assign`) and do nothing else.

## Periodic quests, Contract Board, mail take-all, loot window (2026-10-08)
| message / function | client code | layout / behaviour | confidence | status |
|---|---|---|---|---|
| ServerQuestInit 0x035F tail | client reader, client handler | u64 DailyRandomSeed stored by the client, triggers the redraw | certain | **sent** (seed per daily reset) |
| ServerQuestPeriodicReset 0x0362 | client reader, client handler | u64 seed, Flags:4 (bits 1/2/4/8 to the Lua event QuestPeriodicReset); redraw only when the seed changes | certain | **sent** at the daily reset, flags 0 |
| periodic draw | tree, quest insert, redraw, node draws, MT19937 | see docs/ASSUMPTIONS.md "Contract Board, periodic draw" | certain | Game/Quest/PeriodicQuests.cs |
| ServerQuestContractGoodQualityChanged 0x0252 | client reader (12 raw bytes), client handler | u32 Good contract quest per contract type 0/1/2 | certain | **sent** at login and reset |
| Contract board Lua | ContractsLib (GetPeriodicContracts, IsAtContractBoard = window 85, GetMaxActiveContracts = reward property 37), Contract methods | Accept sends ClientQuestAccept (location 300, bag 0xFFFFFFFF, QuestId:15) or ClientQuestRetry when Botched; Complete sends ClientQuestComplete | certain | handled (existing quest handlers), probe `contract` |
| window type 85 "ContractBoardOpen" | open client switch (index 77 = type 85) | client side window; ClientEntityInteract event 85 recorded | certain | **handled** |
| ClientMailTakeAllFromSelection 0x0125 | MailSystemLib.TakeAllAttachmentsFromMultipleMessages, client writer | u32 count, count x u64 MailId, u32 mailbox UnitId | certain | **implemented**, probe `mailtakeall` |
| ServerLootNotify 0x07F2 | client handler | owner must be a known unit (the corpse); items attach to it | certain | **sent** by LootWindow |
| ServerLootGrant 0x08A5, ServerLootWinner 0x08A3, ServerLootRoll 0x08A2 | readers above | as in the table above | certain | **sent** by LootWindow |
| ClientLootRollAction 0x015D, ClientLootAssignMaster 0x00B5 | as above | record the decision; the world loop resolves | certain | **implemented**, probe `lootwindow`, bots answer windows (BotSystems.AnswerLoot) |
| ServerEntityFaction 0x0934 | client reader | u32 unit, Faction:14 | certain | sent by FactionSet |
| prerequisite evaluator | client switch (type-1, 0x127 cases, table RVA 0x4a4848), evaluator object vtable | types 22/23/33/34/35/36 -> +0x408 with objective index 0..5; 68 -> +0x400; 94 -> +0x410; 6 -> +0x418 | certain | types 22-36 evaluated |

## Tradeskills, crafting, harvesting (2026-10-08)
Recovered from WildStar64.exe (read only) by following the CraftingLib Lua functions to their message writers and the server opcodes to their
readers. Code: Network/Message/Model/TradeskillMessages.cs, Handler/TradeskillHandler.cs. None of it was seen in live traffic (no client).
Confidence: certain = Lua function -> send call with the opcode in research/clientre/lua-sends.tsv; likely = writer/reader disassembly only.

| opcode | message | layout | confidence |
|---|---|---|---|
| 0x0857 | ClientTradeskillLearn (CraftingLib.LearnTradeskill) | u32 skill, u32 skill to drop (0 = none) | certain (opcode), likely (fields) |
| 0x084E | ClientTradeskillPickTalent (PickTradeskillTalent) | u32 skill, u32 talent tier index 0-9 (the window sends level - 1), u32 TradeskillBonus | certain (2026-10-18; was read as a TradeskillTalentTier row) |
| 0x0858 | ClientTradeskillResetTalents (ResetTradeskillTalents) | u32 skill | certain |
| 0x0852 | ClientCraftingCraftItemSimple (CraftItem, skill without Flags bit 8) | u32 spell cast id, u32 station unit, u32 schematic, u32 count | likely |
| 0x0851 | ClientCraftingCraftItem (CraftItem, coordinate skill) | same four u32, 18 bit catalyst item | likely |
| 0x084A | ClientCraftingAdditive (AddAdditive) | u32 station, 18 bit additive item, 18 bit catalyst | likely |
| 0x0850 | ClientCraftingSimpleCraft (CompleteCraft, no power core) | u32 spell cast, u32 station, u32 schematic | likely |
| 0x084F | ClientCraftingComplexCraft (CompleteCraft with power core) | three u32, u64 craft stats, 18 bit power core, u32 AP/SP split, 3 bit count + u32 charges | likely |
| 0x084D | ClientCraftingAbandon | 1 byte, unread | likely |
| 0x0853 | ServerCraftingFinish (client reader) | pass 1 bit, schematic 15, item 18, u32, u32 hot/cold, direction 4, u32 xp, u32 count + u32 materials | likely |
| 0x0856 | ServerProfessionsLoad (client reader) | u32 n + TradeskillInfo, u32 n + schematics, u32 n + (u32, f32, f32) discoveries, u32 n + flag words, u32 relearn cooldown ms | likely |
| 0x0860 | ServerProfessionUpdate (client reader) | TradeskillInfo: u32 skill, u32 xp, u32 active, u32 proficiency flags, u32 talent points, 10 x u32 talent tiers | likely |
| 0x085F / 0x085E | ServerSchematicAddLearned / RemoveLearned (client reader) | u32 skill, u32 schematic, f32 x, f32 y | likely |

Harvesting has no message of its own: the client's Unit:CanBeHarvestedBy checks unit type 5 (HarvestUnit) -> Creature2 ->
TradeskillHarvestingInfo and evaluates its PrerequisiteId (type 19, tier) for the player (certain). The tool is an item (Item2 family 28, slot 7)
whose ItemSpecial.Spell4IdOnActivate is the "Harvesting - <skill> - Tier N" spell; using it is ClientItemUse (existing handler) with the node
as target unit (likely: the item-use path is the only one that casts ItemSpecial spells).

## Lua-reachable client messages, runes, death (2026-10-09)
Recovered from WildStar64.exe (read only): writers/readers with constants from the Lua API functions'
disassembly, behaviour from the client UI scripts (extracted read only: `docker compose run --rm -e NF_EXTRACT_PATTERN="UI/Vendor/*.lua" uiextract`).
Code: Network/Message/Model/LuaApiMessages.cs, Handler/LuaApiHandler.cs, RuneMessages.cs/RuneHandler.cs, VendorHandler (sell junk), GroupManager (join requests).
Each one has a bot probe through the handler (system-ok in the ledger). Unhandled Lua-reachable client messages: 44 -> 35; the reason for each remaining one is in
docs/ASSUMPTIONS.md "Client messages without a handler".

| opcode | message | layout | confidence |
|---|---|---|---|
| 0x00C5 | ClientChallengeChoice | challenge id 14, choice 5 (1 activate, 2 abandon, 14 accept shared, 15 reject shared), value 32 | certain (constants) |
| 0x0150 | ClientResetAttributePoints | empty | certain |
| 0x0167 | ClientSellJunkToVendor | empty | certain |
| 0x015A | ClientResurrectRequest | unit 32, RezType 7, value 32 (upstream read 32 bit type) | likely |
| 0x03B4 | ClientFriendshipAddByName | name, realm (wide), type 4 (Friend 0, Ignore 1, Rival 3, Account 8 from the enum registration), note | likely |
| 0x03C2 | ClientFriendshipRemoveByName | name, realm, type 4 | likely |
| 0x0422 | ClientGroupRequestJoin | name, realm (wide) | likely |
| 0x0424 | ServerGroupRequestJoinResponse (to the leader) | group id 64, GroupMemberInfo | likely |
| 0x0453 | ClientGroupRequestJoinResponse | group id 64, accept 1, name, flag 1 | likely |
| 0x0456 | ServerGroupRequestJoinResult | group id 64, name, GroupLib.Result 5, flag 1 | likely |
| 0x05B4/0x05B5/0x05B6 | ClientMatchingQueueLeaveAll / Leave / LeaveAsGroup | empty / match type 5 / match type 5 | likely |
| 0x0720 | ClientCinematicState | state 8, flag 1 | likely |
| 0x0859-0x085C | rune slot add / clear / install / reroll | docs/ASSUMPTIONS.md "Runes" | likely (0x085B certain) |

## What would unblock the rest
A retail sniff or the client binary (WildStar64.exe, 13.9 MB, sha1 a5974e9628897fc1965b7a740bce68df5b621175), or a second emulator
or packet tool that implements trade/loot roll/instance settings/marketplace on its own. Until then the bot drivers call the server managers directly
(MarketManager, GroupLoot, DungeonMapInstance.Difficulty), which is the code a handler would call.

## Server friendship messages (readers from research/clientre/registry.tsv, Lua events from the handlers)
| opcode | Lua event | layout |
|---|---|---|
| 0x03BE ServerFriendshipList | FriendshipLoaded | u16 count, entries: u64 friendship id, realm 14, u64 character id, wstring name, type 4 |
| 0x03B5 ServerFriendshipAdd | FriendshipAdd | entry, realm 14, u64 character id, float (unidentified, 0) |
| 0x03C3 ServerFriendshipRemove | FriendshipRemove, FriendshipPostRemove | u64 friendship id |
| 0x03C4 ServerFriendshipResult | FriendshipResult | wstring name, result 6 (client enum FriendshipResult) |
| 0x03C9 ServerFriendshipSetNote | (none, list refresh) | u64 friendship id, wstring note |
| 0x03CB (not sent) | FriendshipUpdate | u64 friendship id, type 4 |
| 0x03CA (not sent) | (none) | realm 14, u64 character id, float |
| 0x03BA (not sent) | FriendshipInvitesRecieved | u16 count, invites: u64, realm 14, u64, type 3, float, wstring, wstring, 14, 3, u8 |
| 0x0507 ServerHousingNeighbors | HousingNeighborsLoaded | u32 count, entries: u64, u64, realm 14 + u64 character id, u32 (the two u64 undecoded, sent empty) |
| 0x0519 (not sent) | HousingNeighborUpdate | neighbour entry, 3-bit change type |

## Crafting depth and salvage (2026-10-18)
Recovered from WildStar64.exe (read only). Code: Network/Message/Model/ClientItemSalvage.cs, Handler/ItemHandler.cs, Handler/TradeskillHandler.cs.

| opcode | message | layout | confidence |
|---|---|---|---|
| 0x015E | ClientItemSalvage (confirm button type SalvageItem = 4; upstream name ClientItemUseLootBag) | ItemLocation (9 bit location, u32 bag index), u64 item guid | certain |
| 0x0852 | ClientCraftingCraftItemSimple on a circuit skill (Flags 4) | starts the craft; ClientCraftingComplexCraft 0x084F completes it with the power core | certain (control flow) |
| 0x084A | ClientCraftingAdditive | the 18 bit catalyst is a per-additive catalyst (effects 1-9 or 14) | certain |

Client errors used by salvage: 0x48 ItemCannotBeSalvaged (Item2.Flags 0x100, no Salvage/SalvageException row), 0x2A ItemLevelToLow (character below
GameFormula 1034 dataint0 = 8), 0x06 ItemBadId.
