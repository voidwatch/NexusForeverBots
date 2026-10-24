using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Prerequisite.Static;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.Reputation.Static;

namespace NexusForever.WorldServer.Game.Prerequisite
{
    public sealed partial class PrerequisiteManager
    {
        [PrerequisiteCheck(PrerequisiteType.InstanceVeteran)]
        private static bool PrerequisiteCheckInstanceVeteran(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return CompareInstanceVeteran(player.Map, comparison, value);
        }

        /// <summary>Type 143 against a map: 1 in a veteran instance, 0 everywhere else (docs/ASSUMPTIONS.md "Prerequisite 143").</summary>
        public static bool CompareInstanceVeteran(Map.BaseMap map, PrerequisiteComparison comparison, uint value)
        {
            uint veteran = map is Map.DungeonMapInstance dungeon && dungeon.Difficulty == Map.InstanceDifficulty.Veteran ? 1u : 0u;
            return comparison switch
            {
                PrerequisiteComparison.Equal    => veteran == value,
                PrerequisiteComparison.NotEqual => veteran != value,
                _                               => true
            };
        }

        [PrerequisiteCheck(PrerequisiteType.Level)]
        private static bool PrerequisiteCheckLevel(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            uint level = player.Level;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return level == value;
                case PrerequisiteComparison.NotEqual:
                    return level != value;
                case PrerequisiteComparison.GreaterThanOrEqual:
                    return level >= value;
                case PrerequisiteComparison.GreaterThan:
                    return level > value;
                case PrerequisiteComparison.LessThanOrEqual:
                    return level <= value;
                case PrerequisiteComparison.LessThan:
                    return level < value;
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Level}!");
                    return true;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Race)]
        private static bool PrerequisiteCheckRace(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.Race == (Race)value;
                case PrerequisiteComparison.NotEqual:
                    return player.Race != (Race)value;
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Race}!");
                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Class)]
        private static bool PrerequisiteCheckClass(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.Class == (Class)value;
                case PrerequisiteComparison.NotEqual:
                    return player.Class != (Class)value;
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Class}!");
                    return true;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Quest)]
        private static bool PrerequisiteCheckQuest(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.QuestManager.GetQuestState((ushort)objectId) == (QuestState)value;
                case PrerequisiteComparison.NotEqual:
                    return player.QuestManager.GetQuestState((ushort)objectId) != (QuestState)value;
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Quest}!");
                    return false;
            }
        }

        // Type 68 (client code, certain): the quest is in the player's log, value is an index below its objective count, and
        // that objective is complete (a completed quest counts every objective as complete). Equal = complete, NotEqual = not, others false.
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveIndex)]
        private static bool PrerequisiteCheckQuestObjectiveIndex(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool complete = ObjectiveComplete(player, (ushort)objectId, value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return complete;
                case PrerequisiteComparison.NotEqual:
                    return !complete;
                default:
                    return false;
            }
        }

        private static bool ObjectiveComplete(Player player, ushort questId, uint index)
        {
            Game.Quest.QuestInfo info = Game.Quest.GlobalQuestManager.Instance.GetQuestInfo(questId);
            QuestState? state = player.QuestManager.GetQuestState(questId);
            if (info == null || state == null || index >= info.Objectives.Count)
                return false;
            return state == QuestState.Completed || player.QuestManager.IsObjectiveComplete(questId, index);
        }

        /// <summary>
        /// The client's "objective is active" rule: an objective of type 0 always is; with flag 0x2 (sequential) every earlier
        /// objective without flag 0x20 (optional) must be complete; with flag 0x1 every earlier objective must be complete or active itself.
        /// </summary>
        private static bool ObjectiveActive(Player player, ushort questId, Game.Quest.QuestInfo info, int index)
        {
            Shared.GameTable.Model.QuestObjectiveEntry entry = info.Objectives[index].Entry;
            if (entry.Type == 0u)
                return true;
            if ((entry.Flags & 0x2u) != 0u)
            {
                for (int i = 0; i < index; i++)
                    if ((info.Objectives[i].Entry.Flags & 0x20u) == 0u && !ObjectiveComplete(player, questId, (uint)i))
                        return false;
                return true;
            }
            if ((entry.Flags & 0x1u) != 0u)
            {
                for (int i = index - 1; i >= 0; i--)
                    if (!ObjectiveComplete(player, questId, (uint)i) && !ObjectiveActive(player, questId, info, i))
                        return false;
            }
            return true;
        }

        // Type 5: object is a faction id, value the reputation amount to compare with the character's standing (0 when they have none).
        // Type 94, likely: the objective with index "value" of quest "object" is complete (Equal) or not (NotEqual), see PrerequisiteType.
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveDone)]
        private static bool PrerequisiteCheckQuestObjectiveDone(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool complete = player.QuestManager.GetQuestState((ushort)objectId) == QuestState.Completed
                || player.QuestManager.IsObjectiveComplete((ushort)objectId, value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return complete;
                case PrerequisiteComparison.NotEqual:
                    return !complete;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.QuestObjectiveDone} {comparison}");
                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Reputation)]
        private static bool PrerequisiteCheckReputation(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            if (FactionManagerHas(objectId) == false)
            {
                Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"Reputation faction {objectId} not in the tables");
                return false;
            }

            float amount = player.ReputationManager.GetReputation((Faction)objectId)?.Amount ?? 0f;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return amount == value;
                case PrerequisiteComparison.NotEqual:
                    return amount != value;
                case PrerequisiteComparison.GreaterThanOrEqual:
                    return amount >= value;
                case PrerequisiteComparison.GreaterThan:
                    return amount > value;
                case PrerequisiteComparison.LessThanOrEqual:
                    return amount <= value;
                case PrerequisiteComparison.LessThan:
                    return amount < value;
                default:
                    return false;
            }
        }

        private static bool FactionManagerHas(uint factionId) => NexusForever.WorldServer.Game.Reputation.FactionManager.Instance.GetFaction((Faction)factionId) != null;

        [PrerequisiteCheck(PrerequisiteType.Path)]
        private static bool PrerequisiteCheckPath(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.PathManager.IsPathActive((Path)value);
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Path}!");

                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Achievement)]
        private static bool PrerequisiteCheckAchievement(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.NotEqual:
                    return !player.AchievementManager.HasCompletedAchievement((ushort)objectId);
                case PrerequisiteComparison.Equal:
                    return player.AchievementManager.HasCompletedAchievement((ushort)objectId);
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Achievement}!");
                    return false;
            }
        }

        // Type 109: inferred to be the same shape as the achievement check (object = achievement id), "must have" passes in spell casts (lenient scope).
        [PrerequisiteCheck(PrerequisiteType.Unknown109)]
        private static bool PrerequisiteCheckUnknown109(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Diagnostics.FailureLedger.Record("prerequisite-inferred", "type 109 read as achievement completed", $"achievement {objectId}");
            bool completed = player.AchievementManager.HasCompletedAchievement((ushort)objectId);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return completed || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !completed;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"109 {comparison}");
                    return true;
            }
        }

        // Type 67: value is a PathMission id, Equal = the player completed it. Missions the server cannot play yet never complete, so "must have completed" rules stay closed for quests.
        [PrerequisiteCheck(PrerequisiteType.PathMission)]
        private static bool PrerequisiteCheckPathMission(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Diagnostics.FailureLedger.Record("prerequisite-inferred", "type 67 read as path mission completed", $"mission {value}");
            bool completed = player.PathMissionManager.IsCompleted(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return completed || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !completed;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"67 {comparison}");
                    return true;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.CharacterTitle)]
        private static bool PrerequisiteCheckCharacterTitle(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool owns = player.TitleManager.Any(t => t.Entry.Id == objectId);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return owns || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !owns;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"288 {comparison}");
                    return true;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.SpellBaseId)]
        private static bool PrerequisiteCheckSpellBaseId(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.NotEqual:
                    return player.SpellManager.GetSpell(objectId) == null;
                case PrerequisiteComparison.Equal:
                    return player.SpellManager.GetSpell(objectId) != null;
                default:
                    log.Warn($"Unhandled PrerequisiteComparison {comparison} for {PrerequisiteType.Achievement}!");
                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.BaseFaction)]
        private static bool PrerequisiteCheckBaseFaction(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.Faction1 == (Faction)value;
                case PrerequisiteComparison.NotEqual:
                    return player.Faction1 != (Faction)value;
                default:
                    return false;
            }
        }

        [System.ThreadStatic]
        private static int nestedPrerequisiteDepth;

        /// <summary>
        /// Inferred from data rather than documented: objectId is another prerequisite id and value is 1 (must be met) or 0 (must not be met).
        /// </summary>
        [PrerequisiteCheck(PrerequisiteType.Prerequisite)]
        private static bool PrerequisiteCheckPrerequisite(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            if (nestedPrerequisiteDepth >= 4 || GameTableManager.Instance.Prerequisite.GetEntry(objectId) == null)
                return false;

            nestedPrerequisiteDepth++;
            try
            {
                bool met = PrerequisiteManager.Instance.Meets(player, objectId);
                switch (comparison)
                {
                    case PrerequisiteComparison.Equal:
                        return met == (value != 0u);
                    case PrerequisiteComparison.NotEqual:
                        return met != (value != 0u);
                    default:
                        log.Warn($"Unhandled {comparison} for {PrerequisiteType.Prerequisite}!");
                        return false;
                }
            }
            finally
            {
                nestedPrerequisiteDepth--;
            }
        }

        /// <summary>
        /// Type 73: the current amount of the vital ObjectId (Vital table id) against Value, not a percentage (type 71 is the percent check).
        /// Likely (docs/ASSUMPTIONS.md "Vital prerequisites"): 252 terms, almost all class resources (6-9 Resource1-4, 15 Focus) in
        /// bands such as 4 >= 100 with 5 < 199 (prerequisite 7171, cast levels by resource), "You're out of Vitalus Treatments" is 3 > 0.
        /// A vital id the server cannot read (0, 2, 20, 21 ...) fails the check and is counted once per id in the ledger, it is not logged.
        /// </summary>
        [PrerequisiteCheck(PrerequisiteType.Vital)]
        private static bool PrerequisiteCheckVital(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            float? current = player.GetVitalValue(objectId);
            if (current == null)
            {
                Diagnostics.FailureLedger.Record("prerequisite-vital-unknown", $"vital {objectId} not readable", $"comparison {comparison} value {value}");
                return false;
            }

            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return current.Value == value;
                case PrerequisiteComparison.NotEqual:
                    return current.Value != value;
                case PrerequisiteComparison.GreaterThan:
                    return current.Value > value;
                case PrerequisiteComparison.GreaterThanOrEqual:
                    return current.Value >= value;
                case PrerequisiteComparison.LessThan:
                    return current.Value < value;
                case PrerequisiteComparison.LessThanOrEqual:
                    return current.Value <= value;
                default:
                    Diagnostics.FailureLedger.Record("prerequisite-vital-unknown", $"comparison {comparison}", $"vital {objectId} value {value}");
                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.BonusAmpPower)]
        private static bool PrerequisiteCheckBonusAmpPower(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return Compare(comparison, player.SpellManager.BonusAmpPower, value);
        }

        [PrerequisiteCheck(PrerequisiteType.BonusTierPoints)]
        private static bool PrerequisiteCheckBonusTierPoints(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return Compare(comparison, player.SpellManager.BonusTierPoints, value);
        }

        static bool Compare(PrerequisiteComparison comparison, float left, float right)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal: return left == right;
                case PrerequisiteComparison.NotEqual: return left != right;
                case PrerequisiteComparison.GreaterThanOrEqual: return left >= right;
                case PrerequisiteComparison.GreaterThan: return left > right;
                case PrerequisiteComparison.LessThanOrEqual: return left <= right;
                case PrerequisiteComparison.LessThan: return left < right;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", comparison.ToString());
                    return true;
            }
        }

        // Type 71: vital (object, VitalModifier ids) as a percentage of its maximum.
        [PrerequisiteCheck(PrerequisiteType.VitalPercent)]
        private static bool PrerequisiteCheckVitalPercent(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            float? percent = player.GetVitalPercent(objectId);
            if (percent == null)
            {
                Diagnostics.FailureLedger.Record("prerequisite-inferred", "type 71 on a vital without a maximum", $"vital {objectId}");
                return LenientScope;
            }

            return Compare(comparison, (float)System.Math.Round(percent.Value), value);
        }

        // Type 129: object is a spell group id (the groups of Spell4GroupList rows, as in ModifySpellCooldown mode 2), value an example spell of it
        // or 0. Read as "has an active effect from a spell of the group" (likely, docs/ASSUMPTIONS.md): Sprint and Stealth NotEqual 313 (the Moodie
        // Mask carrier auras), mounts NotEqual 343, Fireball/Death Spike NotEqual 424 ("Generic Spell Lockout"), Tremor NotEqual 426 (Tremor itself),
        // Purify Equal 600 (Affinity procs). The server tracks only part of the auras, so "must have" passes outside a proven aura in spell casts
        // and is counted when it passes unproven elsewhere; "must not have" is enforced.
        [PrerequisiteCheck(PrerequisiteType.SettlerImprovement)]
        private static bool PrerequisiteCheckSettlerImprovement(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool has = player.HasAuraInGroup(objectId);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    if (!has && !LenientScope)
                        Diagnostics.FailureLedger.Record("prerequisite-assumed-met", "type 129 must-have spell group aura not tracked", $"group {objectId} value {value}");
                    return true;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.SettlerImprovement} {comparison}");
                    return true;
            }
        }

        // Type 117 "Public event requirement not met" (client code): object is a PublicEvent id; Equal = the event is running for the
        // player (the client asks its public event list), NotEqual = it is not. Value unused. Here: an unfinished run of the event on the player's map.
        [PrerequisiteCheck(PrerequisiteType.Unknown117)]
        private static bool PrerequisiteCheckUnknown117(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool running = Events.PublicEventEngine.Instance.IsRunning(objectId, player.Map);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return running;
                case PrerequisiteComparison.NotEqual:
                    return !running;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"117 {comparison}");
                    return true;
            }
        }

        // Type 69 (client evaluator reads the challenge's IsActivated flag; certain): object = Challenge id, the challenge
        // is running. Equal = running, NotEqual = not running, other comparisons false (as in the client). Value is not read.
        [PrerequisiteCheck(PrerequisiteType.Unknown69)]
        private static bool PrerequisiteCheckUnknown69(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return EqualOrNot(comparison, player.ChallengeManager.IsActive(objectId), 69u);
        }

        // Type 160: the count of the active challenge (object = Challenge id) compared with value; 0 when the challenge is not running.
        // Failure text of row 13017 "Ellevar - Ch515 isActive and >4"; challenge 1078 "Diagnostic Tests" gates its step creatures on values 0..7.
        [PrerequisiteCheck(PrerequisiteType.ChallengeProgress)]
        private static bool PrerequisiteCheckChallengeProgress(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            uint count = player.ChallengeManager.ActiveChallenges.FirstOrDefault(c => c.Entry.Id == objectId)?.Count ?? 0u;
            return Compare(comparison, count, value);
        }

        // Type 246: value is an Item2 id (791 of 793 rows), PrerequisiteType error text "Inventory requirement not met"; every row uses
        // NotEqual (e.g. 40390: spell "Unsealing Tomb" needs no "Genesis Key" 83765 in the bags). Equal = has the item, NotEqual = has not.
        [PrerequisiteCheck(PrerequisiteType.InventoryItem)]
        private static bool PrerequisiteCheckInventoryItem(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool has = player.Inventory.GetItemCount(value) > 0u;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.InventoryItem} {comparison}");
                    return false;
            }
        }

        // Type 64: object is a class resource index, value the amount compared with the current value of Stat Resource[object]. Row 17120 (failure
        // text "Not enough Psi Points") gates Esper "Telekinetic Storm" on object 1 > 1, and Psi Points are Resource1; rows 20111/29484
        // ("Insufficient Spell Power", "Requires more Spell Power") use object 4 with values 0-100, and Spell Power is Resource4.
        // Type 28: the player is in combat (UnitEntity.InCombat). 155 rows NotEqual, 71 Equal; failure texts "You must be in combat" (903),
        // "Combo points are only generated in combat", "Cannot use in combat or in PvP instances" (certain).
        [PrerequisiteCheck(PrerequisiteType.InCombat)]
        private static bool PrerequisiteCheckInCombat(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return player.InCombat;
                case PrerequisiteComparison.NotEqual:
                    return !player.InCombat;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.InCombat} {comparison}");
                    return true;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.ClassResource)]
        private static bool PrerequisiteCheckClassResource(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            if (objectId > 6u)
            {
                Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.ClassResource} resource index {objectId}");
                return false;
            }

            float amount = player.GetResource(objectId);
            return Compare(comparison, amount, value);
        }

        // Type 13: value is an Item2 id, PrerequisiteType error text "Item equipment requirement not met" (rows 40390/40391 pair it with 246 on the
        // same "Genesis Key": neither in the bags nor equipped). Equal = equipped, NotEqual = not equipped.
        [PrerequisiteCheck(PrerequisiteType.ItemEquipped)]
        private static bool PrerequisiteCheckItemEquipped(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool equipped = player.Inventory.IsEquipped(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return equipped;
                case PrerequisiteComparison.NotEqual:
                    return !equipped;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.ItemEquipped} {comparison}");
                    return false;
            }
        }

        // Type 54: value is a PathMission id, error text "You do not meet the player path mission requirement". Row 13120 (with type 52 Path)
        // gates the activation of creature 13095 "Chua Ship Anchor" and the visibility of 34667 "Explosive Charges" of mission 679
        // "DEMOLITION: Anchors Away", so Equal = the mission is open for the player and not done (done is type 67).
        [PrerequisiteCheck(PrerequisiteType.PathMissionActive)]
        private static bool PrerequisiteCheckPathMissionActive(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool active = player.PathMissionManager.IsActive(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return active;
                case PrerequisiteComparison.NotEqual:
                    return !active;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.PathMissionActive} {comparison}");
                    return false;
            }
        }

        // Types 22, 23, 33, 34, 35, 36: progress of objective 0-5 of quest <object> compared with value. The client evaluator (client switch)
        // calls one function with the index: a completed quest counts its objectives as full (the objective count, 100 for
        // percent objectives), a quest in the log its progress, anything else 0. Certain (client code), docs/ASSUMPTIONS.md.
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress0)]
        private static bool PrerequisiteCheckQuestObjectiveProgress0(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 0u);
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress1)]
        private static bool PrerequisiteCheckQuestObjectiveProgress1(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 1u);
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress2)]
        private static bool PrerequisiteCheckQuestObjectiveProgress2(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 2u);
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress3)]
        private static bool PrerequisiteCheckQuestObjectiveProgress3(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 3u);
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress4)]
        private static bool PrerequisiteCheckQuestObjectiveProgress4(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 4u);
        [PrerequisiteCheck(PrerequisiteType.QuestObjectiveProgress5)]
        private static bool PrerequisiteCheckQuestObjectiveProgress5(Player player, PrerequisiteComparison comparison, uint value, uint objectId) => QuestObjectiveProgress(player, comparison, value, objectId, 5u);

        private static bool QuestObjectiveProgress(Player player, PrerequisiteComparison comparison, uint value, uint objectId, uint index)
        {
            uint progress = player.QuestManager.ObjectiveProgressForPrerequisite((ushort)objectId, index);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return progress == value;
                case PrerequisiteComparison.NotEqual:
                    return progress != value;
                case PrerequisiteComparison.GreaterThan:
                    return progress > value;
                case PrerequisiteComparison.GreaterThanOrEqual:
                    return progress >= value;
                case PrerequisiteComparison.LessThan:
                    return progress < value;
                case PrerequisiteComparison.LessThanOrEqual:
                    return progress <= value;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"quest objective progress {comparison}");
                    return false;
            }
        }

        // Type 61 "Path mission count is incorrect" (client code): object is a PathEpisode id; the number of completed
        // missions of the episode, leaving out missions with PathMissionFlags 0x40, is compared with value (rows 3532/3535: episode 16/13 at least 6/10).
        [PrerequisiteCheck(PrerequisiteType.Unknown61)]
        private static bool PrerequisiteCheckUnknown61(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            int count = 0;
            foreach (PathMissionEntry mission in GameTableManager.Instance.PathMission.Entries)
                if (mission.PathEpisodeId == objectId && (mission.PathMissionFlags & 0x40u) == 0u && player.PathMissionManager.IsCompleted(mission.Id))
                    count++;
            return Compare(comparison, count, value);
        }

        // Type 102 "Spell effect requirements not met" (client code): object is a Spell4Effects id (248 of 317 objects are); Equal = the
        // unit has an active effect from that row, NotEqual = it has none. The server tracks spells with a duration, not rows, so the row's spell is
        // checked; like type 15, "must have" passes in spell casts (the server may not have tracked the effect), "must not have" is real.
        [PrerequisiteCheck(PrerequisiteType.Unknown102)]
        private static bool PrerequisiteCheckUnknown102(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Spell4EffectsEntry effect = GameTableManager.Instance.Spell4Effects.GetEntry(objectId);
            bool has = effect != null && player.HasAura(effect.SpellId);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"102 {comparison}");
                    return true;
            }
        }

        // Type 74 (client evaluator reads the challenge's unlocked flag; certain for the field): object = Challenge id, the challenge is unlocked
        // (the flag gates the challenge's map regions and progress display; failure text "The specified challenge is locked"). The client
        // gets the flag from the server's challenge unlock event; this server unlocks a challenge when it first starts (likely).
        [PrerequisiteCheck(PrerequisiteType.Unknown74)]
        private static bool PrerequisiteCheckUnknown74(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return EqualOrNot(comparison, player.ChallengeManager.IsUnlocked(objectId), 74u);
        }

        // Type 125 (client evaluator reads the challenge's IsInCooldown flag; certain): object = Challenge id, the
        // challenge is in its restart cooldown. This server has none, so Equal is false and NotEqual true.
        [PrerequisiteCheck(PrerequisiteType.Unknown125)]
        private static bool PrerequisiteCheckUnknown125(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return EqualOrNot(comparison, player.ChallengeManager.IsInCooldown(objectId), 125u);
        }

        // Type 107 (client evaluator; certain): object = PathMission id. For a creature the bit of that creature's object index in the
        // mission's state bits; for a player (unit type 20/23) the client returns false for Equal and true for NotEqual, which is the
        // only case the server evaluates here.
        [PrerequisiteCheck(PrerequisiteType.Unknown107)]
        private static bool PrerequisiteCheckUnknown107(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return EqualOrNot(comparison, false, 107u);
        }

        // Type 155 (client evaluator; certain): object = PathMission id, value compared with the client's mission state number
        // (0 no record, 1 scan not started, 2 available, 3 started, 4 complete; PathMissionManager.ClientState), every comparison.
        [PrerequisiteCheck(PrerequisiteType.Unknown155)]
        private static bool PrerequisiteCheckUnknown155(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return Compare(comparison, player.PathMissionManager.ClientState(objectId), value);
        }

        private static bool EqualOrNot(PrerequisiteComparison comparison, bool state, uint type)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return state;
                case PrerequisiteComparison.NotEqual:
                    return !state;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{type} {comparison}");
                    return false;
            }
        }

        // Type 141 "You are out of bounds" (client code): object is a WorldLocation2 id; Equal = the unit is in the location's world and
        // closer to it than its radius, NotEqual = it is not (row 11999: Q6985 Detonate the Bombs, location 25351).
        [PrerequisiteCheck(PrerequisiteType.Unknown141)]
        private static bool PrerequisiteCheckUnknown141(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(objectId);
            bool inside = location != null && player.Map?.Entry != null && player.Map.Entry.Id == location.WorldId
                && System.Numerics.Vector3.Distance(player.Position, new System.Numerics.Vector3(location.Position0, location.Position1, location.Position2)) < location.Radius;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return inside;
                case PrerequisiteComparison.NotEqual:
                    return !inside;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"141 {comparison}");
                    return true;
            }
        }

        // Type 232 "Plane requirement not met" (client code): Equal = (plane flags & value) != 0, NotEqual = no bit of value set. The client
        // keeps the flags at its state +0x6758, written only by server message 0x0172 (client dispatch case, two u32). This server never sends it, so the
        // flags are 0: NotEqual passes, Equal fails, exactly what a client of this server evaluates.
        [PrerequisiteCheck(PrerequisiteType.Unknown232)]
        private static bool PrerequisiteCheckUnknown232(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            const uint planeFlags = 0u;
            bool set = (planeFlags & value) != 0u;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return set;
                case PrerequisiteComparison.NotEqual:
                    return !set;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"232 {comparison}");
                    return true;
            }
        }

        // Types 194 "Ground mounts cannot be used in this area" and 195 "Hoverboard mounts cannot be used in this area" (one client function):
        // mounting is allowed unless the current zone or a parent has WorldZone flag 0x10 (281 zones: dungeons, Datascape, houses
        // of Thayd...), or the client finds itself indoors and no zone of the chain has flag 0x100 (Traverse Tunnels, SuperMall). The indoor test
        // reads the client's own terrain/props (not known here, read as outdoors). Equal = allowed, NotEqual = not allowed; value unused.
        [PrerequisiteCheck(PrerequisiteType.Unknown194)]
        private static bool PrerequisiteCheckUnknown194(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return MountAllowed(player, comparison);
        }

        private static bool MountAllowed(Player player, PrerequisiteComparison comparison)
        {
            bool allowed = true;
            foreach (uint zoneId in player.ZoneAndParents())
                if (((GameTableManager.Instance.WorldZone.GetEntry(zoneId)?.Flags ?? 0u) & 0x10u) != 0u)
                    allowed = false;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return allowed;
                case PrerequisiteComparison.NotEqual:
                    return !allowed;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"mount area {comparison}");
                    return true;
            }
        }

        // Types 194 "Ground mounts cannot be used in this area" and 195 "Hoverboard mounts cannot be used in this area" (one client function):
        // mounting is allowed unless the current zone or a parent has WorldZone flag 0x10 (281 zones: dungeons, Datascape, houses
        // of Thayd...), or the client finds itself indoors and no zone of the chain has flag 0x100 (Traverse Tunnels, SuperMall). The indoor test
        // reads the client's own terrain/props (not known here, read as outdoors). Equal = allowed, NotEqual = not allowed; value unused.
        [PrerequisiteCheck(PrerequisiteType.Unknown195)]
        private static bool PrerequisiteCheckUnknown195(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return MountAllowed(player, comparison);
        }

        // Type 226 "Entitlement requirement not met" (client code): object is an Entitlement id (row 35365: 9 "2-Step
        // Verification Dividends", which gates the Retroblade mount); Equal = the account or character holds it, NotEqual = it does not. Value unused.
        [PrerequisiteCheck(PrerequisiteType.Unknown226)]
        private static bool PrerequisiteCheckUnknown226(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            var type = (EntitlementType)objectId;
            bool has = (player.Session.EntitlementManager.GetAccountEntitlement(type)?.Amount ?? 0u) > 0u
                || (player.Session.EntitlementManager.GetCharacterEntitlement(type)?.Amount ?? 0u) > 0u;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"226 {comparison}");
                    return true;
            }
        }

        // Type 270 "Loyalty requirement not met" (client code): for a character, the account's loyalty points are compared with value
        // (800-270000, gating loyalty rewards such as Velocirex (Badlands), Keen Eye, Quantum Capacitor). This server has no loyalty programme and
        // stores no points, so the account has 0 (the comparison form is certain, 0 points is this server's state, not a retail number).
        [PrerequisiteCheck(PrerequisiteType.Unknown270)]
        private static bool PrerequisiteCheckUnknown270(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            const uint loyaltyPoints = 0u;
            return Compare(comparison, loyaltyPoints, value);
        }

        // Type 43 (client code, certain): the quest is held (in the player's active quest list), objective <value> exists, is not complete
        // and is active (ObjectiveActive). Equal = all of that, NotEqual = not, other comparisons false. An index past the objectives is never
        // active (rows 20840 and the quest-less rows read false for Equal). Fits the data: 30 of the 63 creature activations it gates name
        // exactly that objective's creature, which must be usable before the objective is credited.
        [PrerequisiteCheck(PrerequisiteType.QuestStateOf)]
        private static bool PrerequisiteCheckQuestStateOf(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Game.Quest.QuestInfo info = Game.Quest.GlobalQuestManager.Instance.GetQuestInfo((ushort)objectId);
            bool inProgress = info != null
                && value < info.Objectives.Count
                && player.QuestManager.GetQuestState((ushort)objectId) is QuestState.Accepted or QuestState.Achieved
                && !player.QuestManager.IsObjectiveComplete((ushort)objectId, value)
                && ObjectiveActive(player, (ushort)objectId, info, (int)value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return inProgress;
                case PrerequisiteComparison.NotEqual:
                    return !inProgress;
                default:
                    return false;
            }
        }

        [PrerequisiteCheck(PrerequisiteType.Currency293)]
        private static bool PrerequisiteCheckCurrency293(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Diagnostics.FailureLedger.Record("prerequisite-inferred", "type 293 read as currency amount", $"currency {objectId} value {value}");
            return Compare(comparison, player.CurrencyManager.GetCurrency((CurrencyType)objectId) ?? 0ul, value);
        }
    }
}
