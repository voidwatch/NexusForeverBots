# Opcode coverage (scripts/research/opcode_audit.py)

Client messages = registry entries with a writer in the retail client (research/clientre/registry.tsv); server messages = entries with a reader. Handled = a [MessageHandler] exists here. Lua = client API functions that send the opcode (static analysis, depth 5). Live counts of client messages without a handler: ledger category `system-unhandled-message`.

Client messages: 408: handled 189, upstream name 138, unnamed 66, enum only 15

Server messages: 788: none 518, model 237, enum only 33

## Client messages without a handler
| opcode | name | status | sent by (Lua) |
|---|---|---|---|
| 0x0001 | State2 | enum only |  |
| 0x0002 |  | unnamed |  |
| 0x0003 | ServerHello | enum only |  |
| 0x0004 |  | unnamed |  |
| 0x0005 |  | unnamed |  |
| 0x0006 |  | unnamed |  |
| 0x0010 |  | unnamed |  |
| 0x0072 |  | unnamed |  |
| 0x0073 |  | unnamed |  |
| 0x0096 | ClientGameCommand (upstream) | upstream name |  |
| 0x0098 | ClientCastBindPointSpell (upstream) | upstream name |  |
| 0x0099 | ClientCastPathExplorerSearching (upstream) | upstream name |  |
| 0x009B | ClientMoveToCast (upstream) | upstream name |  |
| 0x009D | ClientCastObjectiveAbility (upstream) | upstream name |  |
| 0x00B6 |  | unnamed |  |
| 0x00B8 | ClientFriendshipBlock (upstream) | upstream name | SetLock |
| 0x00B9 |  | unnamed |  |
| 0x00C2 | ClientCastServiceToken (upstream) | upstream name |  |
| 0x00C3 | ClientCCStateStunUpdate (upstream) | upstream name |  |
| 0x00C7 | ServerChallengeUpdate (upstream) | upstream name |  |
| 0x00C8 |  | unnamed |  |
| 0x00D5 | ClientCombatOptions (upstream) | upstream name |  |
| 0x00DA |  | unnamed |  |
| 0x00DE | ClientDash (upstream) | upstream name |  |
| 0x00E3 | ClientCastDefaultAttack (upstream) | upstream name |  |
| 0x00E8 | ClientDuelAccept (upstream) | upstream name | AcceptDuel |
| 0x00E9 | ClientDuelDecline (upstream) | upstream name | DeclineDuel |
| 0x00EA | ClientDuelForfeit (upstream) | upstream name | ForfeitDuel |
| 0x00EC | ClientDuelInitate (upstream) | upstream name | InitiateDuel |
| 0x00ED |  | unnamed |  |
| 0x00F0 | ClientPathScientistDismissScanbot (upstream) | upstream name | PathAction, PathAction2, ScientistToggleScanBot |
| 0x00F3 | ClientPathExplorerProgressReport (upstream) | upstream name |  |
| 0x00F9 | ClientPathExplorerPowerMapProgress (upstream) | upstream name |  |
| 0x00FB |  | unnamed |  |
| 0x00FC |  | unnamed |  |
| 0x00FD |  | unnamed |  |
| 0x0103 | ClientGalacticArchiveUnlock (upstream) | upstream name |  |
| 0x0105 | ClientGalacticArchiveViewed (upstream) | upstream name | SetViewed |
| 0x010A | ServerHazardList (upstream) | upstream name |  |
| 0x010B | ServerHazardModifiers (upstream) | upstream name |  |
| 0x011B |  | unnamed |  |
| 0x011D |  | unnamed |  |
| 0x012B | ClientOptions (upstream) | upstream name | SetSharedChallengePreference |
| 0x012D |  | unnamed |  |
| 0x0142 |  | unnamed |  |
| 0x0144 | ClientCharacterAppearanceChange (upstream) | upstream name |  |
| 0x014F | ClientLootItem (upstream) | upstream name |  |
| 0x015C | ClientCCStateKnockdownBreak (upstream) | upstream name |  |
| 0x015E | ClientItemUseLootBag | enum only |  |
| 0x015F | ClientPathScientistDismissScanbotPathAction (upstream) | upstream name |  |
| 0x0170 | ClientSetIgnoreDuelRequests (upstream) | upstream name | SetIgnoreDuelRequests |
| 0x0173 | ClientPvpToggleFlags (upstream) | upstream name | TogglePvpFlags |
| 0x017A | ClientAbilityBookActivateSpell (upstream) | upstream name |  |
| 0x017C | ClientSpendAttributePoints (upstream) | upstream name |  |
| 0x017E | ClientSetSprintState (upstream) | upstream name |  |
| 0x0180 | ClientPathScientistRequestScanbot (upstream) | upstream name | PathAction, PathAction2, ScientistToggleScanBot |
| 0x018E | ServerP2PTradeUpdateItem | enum only |  |
| 0x0198 | ServerP2PTradeUpdateMoney | enum only |  |
| 0x01A2 | ClientCommitAmpSpec (upstream) | upstream name |  |
| 0x01AD | ClientLootVacuum (upstream) | upstream name |  |
| 0x01B0 |  | unnamed |  |
| 0x01B6 |  | unnamed |  |
| 0x01B8 |  | unnamed |  |
| 0x01C1 |  | unnamed |  |
| 0x01C4 |  | unnamed |  |
| 0x0233 | ClientPendingAccountItemClaim (upstream) | upstream name |  |
| 0x023C | ClientStatisticsWatchdog (upstream) | upstream name |  |
| 0x023D | ClientStatisticsWindowOpen (upstream) | upstream name |  |
| 0x023E | ClientStatisticsGfx (upstream) | upstream name |  |
| 0x023F | ClientStatisticsConnection (upstream) | upstream name |  |
| 0x0240 | ClientStatisticsFramerate (upstream) | upstream name |  |
| 0x0248 | ClientCombatLogDisableOthers (upstream) | upstream name |  |
| 0x0249 | ClientCombatLogDisables (upstream) | upstream name |  |
| 0x0253 |  | unnamed |  |
| 0x0265 | ClientCreddExchangeBuyOrderSubmit (upstream) | upstream name |  |
| 0x0267 | ClientCreddExchangeOrderCancel (upstream) | upstream name | CancelOrder |
| 0x0269 | ClientCreddExchangeInfoRequest (upstream) | upstream name | RequestExchangeInfo |
| 0x026B | ClientCreddExchangeSellOrderSubmit (upstream) | upstream name |  |
| 0x033F | ClientCustomerSurveySubmit (upstream) | upstream name | SendResult |
| 0x0356 | ClientDialogOpened (upstream) | upstream name |  |
| 0x0360 | ClientCommunicatorAction (upstream) | upstream name |  |
| 0x038F |  | unnamed |  |
| 0x0397 | ClientFriendshipAccountAddByEmail (upstream) | upstream name |  |
| 0x0398 | ClientFriendshipAccountUpgradeFromFriend (upstream) | upstream name |  |
| 0x039B | ClientFriendshipAccountSetPublicDisplayName (upstream) | upstream name |  |
| 0x039D | ClientFriendshipAccountSetPrivateData (upstream) | upstream name |  |
| 0x039E | ClientFriendshipIgnoreStrangersState (upstream) | upstream name |  |
| 0x03A1 | ClientFriendshipAccountInviteResponse (upstream) | upstream name |  |
| 0x03A2 | ClientFriendshipAccountInviteMarkSeen (upstream) | upstream name |  |
| 0x03A7 | ClientFriendshipAccountPersonalPresenceChange (upstream) | upstream name |  |
| 0x03AC | ClientFriendshipAccountSetPublicNote (upstream) | upstream name |  |
| 0x03AF | ClientFriendshipAccountRemove (upstream) | upstream name |  |
| 0x03B7 | ClientFriendshipSetAutoResponseMessage (upstream) | upstream name |  |
| 0x03BC | ClientFriendshipInviteResponse (upstream) | upstream name |  |
| 0x03BD | ClientFriendshipInviteMarkSeen (upstream) | upstream name |  |
| 0x03BF | ClientFriendshipGetLocations (upstream) | upstream name |  |
| 0x03C8 | ClientFriendshipSetNoteByName (upstream) | upstream name |  |
| 0x03CD | ClientFortuneFlipCard (upstream) | upstream name |  |
| 0x03D3 | ClientFortuneNotifyStorefront (upstream) | upstream name |  |
| 0x03D4 | ClientFortuneStart (upstream) | upstream name |  |
| 0x03E7 | ClientCreddHistoryRequest (upstream) | upstream name |  |
| 0x03E8 |  | unnamed |  |
| 0x03EE | ClientGetRealmTransferDestinations (upstream) | upstream name |  |
| 0x03F0 |  | unnamed |  |
| 0x03F1 | ClientAccountItemGiftToAccountFriend (upstream) | upstream name |  |
| 0x03F2 | ClientAccountItemGiftByIdentity (upstream) | upstream name |  |
| 0x0411 |  | unnamed |  |
| 0x0412 |  | unnamed |  |
| 0x0432 | ClientGroupMarkUnit (upstream) | upstream name | ClearTargetMarker |
| 0x0439 |  | unnamed | CloseMentoringAOIDialog |
| 0x043B |  | unnamed | CancelMentoring |
| 0x043E |  | unnamed | AcceptMentoring |
| 0x0459 |  | unnamed |  |
| 0x045C |  | unnamed |  |
| 0x0460 |  | unnamed |  |
| 0x0463 |  | unnamed |  |
| 0x0471 | ClientGuildBankTransaction (upstream) | upstream name |  |
| 0x0473 | ClientGuildBankTransaction2 (upstream) | upstream name |  |
| 0x0477 | ClientGuildBankTabOpen (upstream) | upstream name | RefreshUI |
| 0x04A6 | ClientGuildStandardModify (upstream) | upstream name |  |
| 0x04A8 | ClientGuildBankMoneyTransaction (upstream) | upstream name |  |
| 0x04B2 | ClientGuildPerkActivate (upstream) | upstream name |  |
| 0x04B3 | ClientGuildBankTabRename (upstream) | upstream name |  |
| 0x04CF | ClientGuildSetStandard (upstream) | upstream name |  |
| 0x04E6 | ClientHousingPlaceDecor (upstream) | upstream name |  |
| 0x04ED |  | unnamed |  |
| 0x0504 |  | unnamed |  |
| 0x050D | ClientHousingChangeInteriorWallpaper (upstream) | upstream name |  |
| 0x0522 |  | unnamed |  |
| 0x0523 |  | unnamed |  |
| 0x052F | ClientHousingDoorInteraction (upstream) | upstream name |  |
| 0x0546 | ClientICCommChannelJoin (upstream) | upstream name |  |
| 0x0549 | ClientICCommChannelNotJoined (upstream) | upstream name |  |
| 0x054B | ClientICCommMessage (upstream) | upstream name |  |
| 0x0550 |  | unnamed |  |
| 0x056E |  | unnamed |  |
| 0x057B |  | unnamed |  |
| 0x059C | ClientRealmInfoRequest (upstream) | upstream name |  |
| 0x059F |  | unnamed |  |
| 0x05A3 | ServerMailAvailable | enum only |  |
| 0x05C8 | ClientMatchingMatchReadyResponse (upstream) | upstream name |  |
| 0x05D1 | ClientMatchingMatchInitiateVoteToKick (upstream) | upstream name | InitiateVoteToKick |
| 0x05D3 | ClientMatchingMatchInitiateVoteToSurrender (upstream) | upstream name | InitiateVoteToSurrender |
| 0x05D5 | ClientMatchingMatchInitiateLookingForReplacements (upstream) | upstream name | LookForReplacements |
| 0x05DA | ClientMatchingMatchLeave (upstream) | upstream name | LeaveGame |
| 0x05F9 | ClientMatchingQueueRandomParty (upstream) | upstream name |  |
| 0x0602 | ClientMatchingStopLookingForReplacements (upstream) | upstream name | StopLookingForReplacements |
| 0x0606 | ClientMatchingTransferIntoMatch (upstream) | upstream name | TransferIntoMatch |
| 0x0617 | ClientMatchingMatchCastVoteKick (upstream) | upstream name | CastVoteKick |
| 0x0624 | ClientMatchingMatchCastVoteSurrender (upstream) | upstream name | CastVoteSurrender |
| 0x062A |  | unnamed |  |
| 0x0634 |  | unnamed |  |
| 0x0635 | Server0635 | enum only |  |
| 0x0636 | ServerMovementControl | enum only |  |
| 0x0638 | ServerEntityCommand | enum only |  |
| 0x0639 | Server0639 | enum only |  |
| 0x063B | ClientPlayerMovementSpeedUpdate | enum only |  |
| 0x063E |  | unnamed |  |
| 0x066A | ClientPathMissionAttemptScientistExperimentation (upstream) | upstream name |  |
| 0x0670 | ClientPathSettlerImprovementBuildTier (upstream) | upstream name | BuildTier |
| 0x067D | ClientPathSoldierImprovementBuild (upstream) | upstream name | Build |
| 0x068A |  | unnamed |  |
| 0x068C | ClientPathScientistSetScannerName (upstream) | upstream name |  |
| 0x068E | ClientPetSetStance (upstream) | upstream name | Pet_SetStance |
| 0x06C5 | ClientIncidentReport (upstream) | upstream name |  |
| 0x06D1 | ClientSupportTicket (upstream) | upstream name |  |
| 0x06E7 |  | unnamed |  |
| 0x06E8 | ClientPtrCopy (upstream) | upstream name | InitiatePTRCharacterCopy |
| 0x06EE | ClientPublicEventVote (upstream) | upstream name | CastVote |
| 0x06FA | ClientPublicEventRequestScoreboard (upstream) | upstream name |  |
| 0x0701 |  | unnamed |  |
| 0x0719 | ClientRaidInfoRequest (upstream) | upstream name |  |
| 0x071C | ServerRandomRollResponse | enum only |  |
| 0x071D | ClientCinematicCameraSubjectPosVel (upstream) | upstream name |  |
| 0x071E | ClientCinematicCameraSubjectSpline (upstream) | upstream name |  |
| 0x071F | ClientCinematicCameraSubjectUnit (upstream) | upstream name |  |
| 0x0721 | ClientCinematicWhiteOutFinished (upstream) | upstream name |  |
| 0x0754 | ClientGenericMapNodeChosen (upstream) | upstream name |  |
| 0x0760 |  | unnamed |  |
| 0x0761 | ServerRealmList | enum only |  |
| 0x0762 |  | unnamed |  |
| 0x076E |  | unnamed |  |
| 0x076F | ClientRecruitmentGuildSubscribe (upstream) | upstream name |  |
| 0x0770 | ClientCreddRedeem (upstream) | upstream name |  |
| 0x0786 | ClientCharacterRename (upstream) | upstream name |  |
| 0x0793 | ClientFortuneNotifyGame (upstream) | upstream name |  |
| 0x0795 |  | unnamed |  |
| 0x0799 | ClientLeaderboardPvpRequest (upstream) | upstream name |  |
| 0x079A | ClientLeaderboardPveRequest (upstream) | upstream name |  |
| 0x079E | ClientPremiumLockboxKeyRequest (upstream) | upstream name |  |
| 0x07B6 |  | unnamed |  |
| 0x07C6 | ClientAccountItemGiftReturn (upstream) | upstream name |  |
| 0x07CC | ClientRewardRotationsRequestUpdate (upstream) | upstream name |  |
| 0x07CE | ClientRewardTrackClaimRewardPoint (upstream) | upstream name | ClaimRewardPoint |
| 0x07CF | ClientRewardTrackChooseTrack (upstream) | upstream name |  |
| 0x07E3 |  | unnamed |  |
| 0x0800 |  | unnamed |  |
| 0x0805 | ClientSpellClientSideInteraction (upstream) | upstream name |  |
| 0x081A | Server081A (upstream) | upstream name |  |
| 0x081B | Server081B (upstream) | upstream name |  |
| 0x081C | Server081C (upstream) | upstream name |  |
| 0x081D |  | unnamed |  |
| 0x0823 | ClientSteamAchievements (upstream) | upstream name |  |
| 0x0825 |  | unnamed |  |
| 0x0828 | ClientStorefrontPurchaseAccount | enum only |  |
| 0x082A | ClientStorefrontPurchaseCharacter | enum only |  |
| 0x082C |  | unnamed |  |
| 0x082E |  | unnamed |  |
| 0x0830 | ClientReportBug (upstream) | upstream name | ReportBug |
| 0x0831 | ClientStuck (upstream) | upstream name |  |
| 0x0833 | ClientSuggest (upstream) | upstream name |  |
| 0x0839 | ClientAccountItemTake (upstream) | upstream name |  |
| 0x0862 | ServerTransferDestinationRealmList (upstream) | upstream name |  |
| 0x0865 |  | unnamed |  |
| 0x0928 |  | unnamed |  |
| 0x0942 |  | unnamed |  |
| 0x094F | ClientCastGuildBossToken (upstream) | upstream name |  |
| 0x0956 | ClientWarPartyBossTokensRequest (upstream) | upstream name |  |
| 0x1388 |  | unnamed |  |

## Client messages with a handler
0x0093 ClientCommodityOrderCancel, 0x0094 ClientAuctionCancel, 0x0097 ClientActivateUnitCast, 0x009A ClientCastSpell, 0x00B1 ClientRequestActionSetChanges, 0x00B3 ClientActivateUnit, 0x00B5 ClientLootAssignMaster, 0x00BB ClientBuybackItemFromVendor, 0x00BE ClientVendorPurchase, 0x00BF ClientLogoutRequest, 0x00C0 ClientLogoutConfirm, 0x00C5 ClientChallengeChoice, 0x00C9 ClientHousingResidencePrivacyLevel, 0x00D2 ClientClosedInstanceSettings, 0x00D7 ClientResourceConversion, 0x00E4 ClientResurrectRequest, 0x00F2 ClientEnteredWorld, 0x00FF ClientTaxiPurchase, 0x010C ClientGuildHolomarkUpdate, 0x011E ClientMailDelete, 0x0122 ClientMailOpen, 0x0123 ClientMailPayCod, 0x0124 ClientMailReturn, 0x0125 ClientMailTakeAllFromSelection, 0x0126 ClientMailTakeAttachment, 0x0127 ClientMailTakeCash, 0x012A ClientItemMoveToSupplySatchel, 0x0141 ClientRapidTransport, 0x0149 ClientItemDelete, 0x014C ClientVendorRepair, 0x014E ClientEntityInteractChair, 0x0150 ClientResetAttributePoints, 0x0151 ClientRequestAmpReset, 0x0153 ClientResetSingleInstance, 0x015A ClientResurrectAccept, 0x015D ClientLootRollAction, 0x0163 ClientSetInstanceSettings, 0x0166 ClientSellItemToVendor, 0x0167 ClientSellJunkToVendor, 0x0168 ClientMailSend, 0x016A ClientNonSpellActionSetChanges, 0x016F ClientInnateChange, 0x0174 ClientChangeActiveActionSet, 0x0177 ClientToggleWeapons, 0x017D ClientItemSplit, 0x0182 ClientItemMove, 0x0184 ClientItemMoveFromSupplySatchel, 0x0185 ClientEntitySelect, 0x018C ClientP2PTradingAcceptInvite, 0x018D ClientP2PTradingAddItem, 0x018F ClientP2PTradingCancelTrade, 0x0190 ClientP2PTradingCommit, 0x0191 ClientP2PTradingDeclineInvite, 0x0192 ClientP2PTradingInitiateTrade, 0x0194 ClientP2PTradingRemoveItem, 0x0197 ClientP2PTradingSetMoney, 0x01AF ClientVehicleDisembark, 0x01BA ClientChatJoin, 0x01BD ClientChatKick, 0x01BE ClientChatLeave, 0x01C3 ClientChat, 0x01CD ClientChatModerator, 0x01CE ClientChatMute, 0x01CF ClientChatOwner, 0x01D0 ClientChatPassword, 0x01D2 ClientChatList, 0x01D4 ClientChatWhisper, 0x0241 ClientPing, 0x0244 ClientEncrypted, 0x0255 ClientCostumeSave, 0x0256 ClientCostumeSet, 0x025B ClientCharacterCreate, 0x025C ClientPacked, 0x0352 ClientCharacterDelete, 0x035A ClientQuestAbandon, 0x035B ClientQuestAccept, 0x035D ClientQuestComplete, 0x035E ClientQuestSetIgnore, 0x0364 ClientQuestSetTracked, 0x0365 ClientQuestRetry, 0x037E ClientEmote, 0x038B ClientCostumeItemForget, 0x038C ClientPackedWorld, 0x03B3 ClientFriendshipAdd, 0x03B4 ClientFriendshipAddByName, 0x03C1 ClientFriendshipRemove, 0x03C2 ClientFriendshipRemoveByName, 0x03C7 ClientFriendshipSetNote, 0x03E0 ClientCheat, 0x03E6 ClientRequestCommodityInfo, 0x03EC ClientRequestOwnedCommodityOrders, 0x03ED ClientRequestOwnedItemAuctions, 0x0400 ClientItemGenericUnlock, 0x0406 ClientGroupFlagsUpdate, 0x0408 ClientGroupMemberFlagsUpdate, 0x0416 ClientGroupInvite, 0x041A ClientGroupInviteResponse, 0x0422 ClientGroupRequestJoin, 0x0428 ClientGroupKick, 0x042B ClientGroupLeave, 0x042E ClientGroupLootRulesChange, 0x0446 ClientGroupPromote, 0x044A ClientGroupReadyCheck, 0x0453 ClientGroupRequestJoinResponse, 0x045E ClientQuestShareResult, 0x045F ClientQuestShare, 0x0481 ClientGuildRegister, 0x0490 ClientGuildInviteResponse, 0x04B1 ClientGuildOperation, 0x04DB ClientCastSpellContinuous, 0x04E7 ClientItemUseDecor, 0x04EA ClientHousingCommunityRemoval, 0x04EC ClientHousingCrateAllDecor, 0x04F5 ClientHousingCommunityDonate, 0x050A ClientHousingRemodel, 0x050B ClientHousingDecorUpdate, 0x050E ClientHousingFlagsUpdate, 0x0510 ClientHousingPlugUpdate, 0x0512 ClientHousingNeighborInvite, 0x0513 ClientHousingNeighborResponse, 0x0515 ClientHousingNeighborEvict, 0x0518 ClientHousingNeighborPermission, 0x0525 ClientHousingVendorList, 0x0529 ClientHousingRenameProperty, 0x052A ClientHousingPlacedResidencesList, 0x052B ClientHousingCommunityPlacement, 0x052C ClientHousingRandomCommunityList, 0x052D ClientHousingRandomResidenceList, 0x052E ClientHousingReturn, 0x0531 ClientHousingVisit, 0x0538 ClientHousingCommunityPrivacyLevel, 0x053C ClientHousingEditMode, 0x0552 ClientInspectPlayerRequest, 0x055E ClientAuctionBuyOrderSubmit, 0x056F BiInputKeySet, 0x0570 ClientRequestInputKeySet, 0x0571 ClientSetInputKeySet, 0x057C ClientLeavePendingRemoval, 0x058F ClientHelloRealm, 0x0592 ClientHelloAuth, 0x0597 ClientPlayerInfoRequest, 0x05B2 ClientMatchingRoleCheckResponse, 0x05B4 ClientMatchingQueueLeaveAll, 0x05B5 ClientMatchingQueueLeave, 0x05B6 ClientMatchingQueueLeaveAsGroup, 0x05EF ClientMatchingQueue, 0x05F3 ClientMatchingQueueParty, 0x05F8 ClientMatchingQueueRandom, 0x0637 ClientEntityCommand, 0x063A ClientZoneChange, 0x0693 ClientRequestPlayed, 0x06B2 ClientPathActivate, 0x06BD ClientPathUnlock, 0x06DA ClientCommodityOrderSubmit, 0x06DC ClientAuctionSellOrderSubmit, 0x071B ClientRandomRollRequest, 0x0720 ClientCinematicState, 0x0788 ClientHousingCommunityRename, 0x078E ClientTitleSet, 0x078F ClientDailyLoginRewardsRequest, 0x07A4 ClientRealmList, 0x07A5 ClientReplayLevelRequest, 0x07B4 ClientResetInstances, 0x07DC ClientAuctionsByFilterRequest, 0x07DD ClientCharacterSelect, 0x07DF ClientSelectRealm, 0x07E0 ClientCharacterList, 0x07EA ClientEntityInteract, 0x07ED ClientPetCustomisation, 0x0801 ClientSpellStopCast, 0x0802 ClientCancelEffect, 0x082D ClientStorefrontRequestCatalog, 0x082F ClientSummonVanityPet, 0x084A ClientCraftingAdditive, 0x084D ClientCraftingAbandon, 0x084E ClientTradeskillPickTalent, 0x084F ClientCraftingComplexCraft, 0x0850 ClientCraftingSimpleCraft, 0x0851 ClientCraftingCraftItem, 0x0852 ClientCraftingCraftItemSimple, 0x0857 ClientTradeskillLearn, 0x0858 ClientTradeskillResetTalents, 0x0859 ClientCraftingRuneSlotAdd, 0x085A ClientCraftingRuneSlotClear, 0x085B ClientCraftingRuneInstall, 0x085C ClientCraftingRuneSlotReroll, 0x090F ClientCostumeItemUnlock, 0x0943 ClientItemUse, 0x0959 ClientWhoRequest
