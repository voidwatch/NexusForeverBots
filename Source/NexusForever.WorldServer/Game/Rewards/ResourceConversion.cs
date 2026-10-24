using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Rewards
{
    public enum ConversionResult { Done, UnknownRow, MissingSource, CannotAffordSurcharge, BagsFull, UnknownTarget }

    /// <summary>
    /// Resource conversion (ResourceConversion, 90 rows; ResourceConversionGroup lists the rows an NPC offers): ResourceConversionTypeEnum 0 trades SourceCount of item SourceId
    /// for TargetCount of item TargetId, type 1 trades items for TargetCount of currency TargetId (ids 260, 817, 480 are currency ids). A surcharge, when set, is paid in the currency
    /// SurchargeId. ASSUMPTIONS (docs/ASSUMPTIONS.md "Resource conversion"): types inferred from the id ranges, surcharge is a currency, no NPC link exists in the tables loaded
    /// here (ResourceConversionGroup is not referenced by Creature2), there is no client message in the protocol, bots call Convert.
    /// </summary>
    public static class ResourceConversion
    {
        /// <summary>Reach to the conversion NPC (guess; the client checks the unit, the server reach is not known).</summary>
        public const float NpcReach = 30f;

        /// <summary>
        /// Conversion rows offered by a creature: Creature2.resourceConversionGroupId -> ResourceConversionGroup (49 creatures, 29 spawned).
        /// The client's ConvertResource needs such a unit but does not send it (the message's u64 is 0), so the server looks for one in reach.
        /// </summary>
        public static IEnumerable<uint> RowsOf(uint creatureId)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            ResourceConversionGroupEntry group = creature != null && creature.ResourceConversionGroupId != 0u
                ? GameTableManager.Instance.ResourceConversionGroup.GetEntry(creature.ResourceConversionGroupId) : null;
            if (group == null)
                return Enumerable.Empty<uint>();
            return new[] { group.ResourceConversionId00, group.ResourceConversionId01, group.ResourceConversionId02, group.ResourceConversionId03,
                group.ResourceConversionId04, group.ResourceConversionId05 }.Where(id => id != 0u);
        }

        /// <summary>A creature within <see cref="NpcReach"/> offers the conversion row.</summary>
        public static bool NpcInReach(Player player, uint conversionId)
        {
            if (player.Map == null)
                return false;
            player.Map.Search(player.Position, NpcReach, new Map.Search.SearchCheckRange(player.Position, NpcReach, player), out List<GridEntity> around);
            return around.OfType<WorldEntity>().Any(e => e.CreatureId != 0u && RowsOf(e.CreatureId).Contains(conversionId));
        }

        public static ConversionResult Convert(Player player, uint conversionId)
        {
            ResourceConversionEntry row = GameTableManager.Instance.ResourceConversion.GetEntry(conversionId);
            if (row == null)
                return Record(ConversionResult.UnknownRow, conversionId);

            if (player.Inventory.GetItemCount(row.SourceId) < Math.Max(1u, row.SourceCount))
                return Record(ConversionResult.MissingSource, conversionId);

            if (row.SurchargeId != 0u && row.SurchargeCount > 0u && !player.CurrencyManager.CanAfford((CurrencyType)row.SurchargeId, row.SurchargeCount))
                return Record(ConversionResult.CannotAffordSurcharge, conversionId);

            bool toCurrency = row.ResourceConversionTypeEnum == 1u;
            if (toCurrency ? !Enum.IsDefined(typeof(CurrencyType), (int)row.TargetId) : GameTableManager.Instance.Item.GetEntry(row.TargetId) == null)
                return Record(ConversionResult.UnknownTarget, conversionId);

            // rows with a -1 (uint.MaxValue) count are placeholder data, not a real payout
            if (row.TargetCount > 100000u)
                return Record(ConversionResult.UnknownTarget, conversionId);

            if (!toCurrency && player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) == 0u && player.Inventory.GetItemCount(row.SourceId) <= Math.Max(1u, row.SourceCount))
                return Record(ConversionResult.BagsFull, conversionId);

            player.Inventory.ItemDelete(row.SourceId, Math.Max(1u, row.SourceCount));
            if (row.SurchargeId != 0u && row.SurchargeCount > 0u)
                player.CurrencyManager.CurrencySubtractAmount((CurrencyType)row.SurchargeId, row.SurchargeCount);

            if (toCurrency)
                player.CurrencyManager.CurrencyAddAmount((CurrencyType)row.TargetId, row.TargetCount);
            else
                player.Inventory.ItemCreate(InventoryLocation.Inventory, row.TargetId, Math.Max(1u, row.TargetCount), ItemUpdateReason.Script);

            return Record(ConversionResult.Done, conversionId);
        }

        private static ConversionResult Record(ConversionResult result, uint id)
        {
            FailureLedger.Record("resource-conversion", result.ToString(), $"row {id}");
            return result;
        }
    }
}
