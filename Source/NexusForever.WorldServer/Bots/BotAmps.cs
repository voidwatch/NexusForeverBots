using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Static;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Bots spend AMP power through the client message (ClientRequestActionSetChanges, AMP list only), one AMP per gear pass: the cheapest
    /// AMP the action set accepts (lowest tier first), so tiers 2 and 3 open up as points go into a category.
    /// </summary>
    public static class BotAmps
    {
        public static void Think(Player me)
        {
            if (me.Level < ActionSet.AmpUnlockLevel)
                return;

            ActionSet set = me.SpellManager.GetActionSet(me.SpellManager.ActiveActionSet);
            if (set.AmpPoints == 0)
                return;

            EldanAugmentationEntry pick = GameTableManager.Instance.EldanAugmentation.Entries
                .Where(e => e != null && e.Id != 0u && e.Id <= ushort.MaxValue && e.Spell4IdAugment != 0u && set.CanAddAmp(e) == null)
                .OrderBy(e => e.CategoryTier)
                .ThenBy(e => e.PowerCost)
                .ThenBy(e => e.Id)
                .FirstOrDefault();
            if (pick == null)
                return;

            SpellHandler.HandleRequestActionSetChanges(me.Session, AddAmpMessage(set.Index, (ushort)pick.Id));
        }

        /// <summary>ClientRequestActionSetChanges with no shortcut or tier changes and one AMP.</summary>
        public static ClientRequestActionSetChanges AddAmpMessage(byte actionSetIndex, ushort ampId)
        {
            return BotPackets.Build<ClientRequestActionSetChanges>(w =>
            {
                w.Write(0u, 4u);
                w.Write(actionSetIndex, 3u);
                w.Write(0u, 5u);
                w.Write(1u, 7u);
                w.Write(ampId);
            });
        }

        public static ClientRequestAmpReset ResetMessage(byte actionSetIndex, AmpRespecType type, uint value)
        {
            return BotPackets.Build<ClientRequestAmpReset>(w =>
            {
                w.Write(actionSetIndex, 3u);
                w.Write((uint)type, 3u);
                w.Write(value);
            });
        }
    }
}
