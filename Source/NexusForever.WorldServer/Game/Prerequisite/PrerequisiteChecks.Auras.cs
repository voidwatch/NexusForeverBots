using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Prerequisite.Static;

namespace NexusForever.WorldServer.Game.Prerequisite
{
    public sealed partial class PrerequisiteManager
    {
        // Type 15: value is a spell4 id (the stealth cast rule reads "NotEqual 40838 and NotEqual 58421", one buff must not be active).
        // The server only tracks effects that ran through a handler with a duration, so "must have" rules cannot be proven false
        // yet: during a spell cast they pass, "must not have" rules are real.
        [PrerequisiteCheck(PrerequisiteType.AuraActive)]
        private static bool PrerequisiteCheckAuraActive(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool has = player.HasAura(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.AuraActive} {comparison}");
                    return true;
            }
        }

        // Type 59: object is a spell id, value a count, any comparison. The server does not stack effects, so a spell is 1 stack while its aura runs, else 0.
        [PrerequisiteCheck(PrerequisiteType.AuraStacks)]
        private static bool PrerequisiteCheckAuraStacks(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            Diagnostics.FailureLedger.Record("prerequisite-inferred", "type 59 read as stacks of an aura", $"spell {objectId} value {value}");
            return Compare(comparison, player.HasAuraOrBase(objectId) ? 1u : 0u, value);
        }

        // Type 116: 29 rows, all stealth abilities and all with value 28455. Read as "the caster is stealthed" (Equal) / "is not" (NotEqual).
        // Other values are treated as an aura check like type 15.
        [PrerequisiteCheck(PrerequisiteType.SpellState)]
        private static bool PrerequisiteCheckSpellState(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool has = value == 28455u ? player.IsStealthed : player.HasAura(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.SpellState} {comparison}");
                    return true;
            }
        }

        // Type 56: value is a CCState, NotEqual = the unit does not have it. "Must have" rules can refer to the target of the spell
        // instead of the caster, so they pass during spell casts.
        [PrerequisiteCheck(PrerequisiteType.CCState)]
        private static bool PrerequisiteCheckCCState(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool has = player.HasCCState(value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return has || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !has;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.CCState} {comparison}");
                    return true;
            }
        }

        // Type 19 (certain, the client's prerequisite evaluator): object is a TradeskillTier row; the character's tier of that row's skill
        // (0 when the skill is not learned or swapped out) is compared with the row's tier; the value column is not read. So Equal asks for
        // exactly that tier (65 rows, e.g. the tier quests "You are not an Armorer of at least Apprentice level") and GreaterThanOrEqual for
        // at least it (175 rows, "Not enough Mining skill").
        [PrerequisiteCheck(PrerequisiteType.Tradeskill)]
        private static bool PrerequisiteCheckTradeskill(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            var tier = NexusForever.Shared.GameTable.GameTableManager.Instance.TradeskillTier.GetEntry(objectId);
            if (tier == null)
            {
                Diagnostics.FailureLedger.Record("prerequisite-tradeskill-tier-unknown", $"object {objectId}");
                return false;
            }
            long level = player.TradeskillManager.GetTier(tier.TradeSkillId);
            value = tier.Tier;

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
                    return false;
            }
        }

        // Type 26: inferred to be the zone the character is in (value = zone id). Not proven, so "must be in" passes during spell casts
        // and only "must not be in" is enforced there; quests use the real comparison.
        [PrerequisiteCheck(PrerequisiteType.Zone)]
        private static bool PrerequisiteCheckZone(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            // the 32 quest rows of this type all name a sub zone of the quest's own zone (e.g. quest 5504 "The Lost Caverns" in Western Deradune);
            // a player in a sub zone of it is inside it too
            bool inZone = player.Zone != null && System.Linq.Enumerable.Contains(player.ZoneAndParents(), value);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return inZone || LenientScope;
                case PrerequisiteComparison.NotEqual:
                    return !inZone;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.Zone} {comparison}");
                    return true;
            }
        }

        // Type 27 ("You are out of bounds"): value is a WorldZone id (124 of 386 terms are ids of build 16042, the names fit the spells, e.g. 5986
        // "The Captain's Coffers" for Holostation Protection, 2339 Phagelab Level 1 for Fresh Thermal Water). The client's evaluator returns false
        // for it, so only the server evaluated it; it is read like type 26 (zone or a parent zone). Ids missing from WorldZone never match.
        [PrerequisiteCheck(PrerequisiteType.ZoneServer)]
        private static bool PrerequisiteCheckZoneServer(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            return PrerequisiteCheckZone(player, comparison, value, objectId);
        }

        // Type 16 (client code, "Distance requirements not met"): object is a WorldLocation2 id; the location must be in the unit's world,
        // then the 3D distance from the unit to it is compared with value (rows 28094/28095: "Shouting" within 10 m of 20957/20960).
        // A missing location or another world fails, as in the client.
        [PrerequisiteCheck(PrerequisiteType.LocationDistance)]
        private static bool PrerequisiteCheckLocationDistance(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            NexusForever.Shared.GameTable.Model.WorldLocation2Entry location = NexusForever.Shared.GameTable.GameTableManager.Instance.WorldLocation2.GetEntry(objectId);
            if (location == null || player.Map?.Entry == null || player.Map.Entry.Id != location.WorldId)
                return false;
            float distance = System.Numerics.Vector3.Distance(player.Position, new System.Numerics.Vector3(location.Position0, location.Position1, location.Position2));
            return Compare(comparison, distance, value);
        }

        // Type 39 (client code, "Incorrect player"): Equal = the evaluated unit is a character (unit type 20), NotEqual = it is not.
        // The server evaluates prerequisites for players only, so Equal passes and NotEqual fails.
        [PrerequisiteCheck(PrerequisiteType.IsPlayer)]
        private static bool PrerequisiteCheckIsPlayer(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return true;
                case PrerequisiteComparison.NotEqual:
                    return false;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.IsPlayer} {comparison}");
                    return true;
            }
        }

        // Type 118: object is a PublicEventObjective id (1337 of the 1357 objects are), value is its status in the client's PublicEventStatus enum
        // (Inactive 0, Active 1, Succeeded 2, Failed 3: the Lua enum registration in the client pushes those numbers). The status comes from the
        // event run on the player's map; no run means Inactive.
        [PrerequisiteCheck(PrerequisiteType.PublicEventObjective)]
        private static bool PrerequisiteCheckPublicEventObjective(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            uint status = (uint)Events.PublicEventEngine.Instance.StatusOf(objectId, player.Map);
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return status == value;
                case PrerequisiteComparison.NotEqual:
                    return status != value;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.PublicEventObjective} {comparison}");
                    return false;
            }
        }

        // Settler Campfire is refused in adventures/raids/dungeons (6/9/11), Mailbox and Mini Vendbot in PvP and warplots (3/7/8),
        // Summon Group in housing (2/5): the object is the World.type of the current map, value unused (0 in 30 of 31 rows).
        [PrerequisiteCheck(PrerequisiteType.WorldType)]
        private static bool PrerequisiteCheckWorldType(Player player, PrerequisiteComparison comparison, uint value, uint objectId)
        {
            bool inType = player.Map?.Entry != null && player.Map.Entry.Type == objectId;
            switch (comparison)
            {
                case PrerequisiteComparison.Equal:
                    return inType;
                case PrerequisiteComparison.NotEqual:
                    return !inType;
                default:
                    Diagnostics.FailureLedger.Record("unhandled-prerequisite-comparison", $"{PrerequisiteType.WorldType} {comparison}");
                    return true;
            }
        }
    }
}
