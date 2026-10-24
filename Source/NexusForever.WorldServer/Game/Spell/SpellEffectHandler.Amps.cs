using System;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// AMP and ability point unlock items (docs/ASSUMPTIONS.md "AMPs").
    /// </summary>
    public partial class Spell
    {
        // UnlockInlaidAugment: DataBits00 = EldanAugmentation id (281 rows, every row names an AMP of the item's class; spell names are the
        // AMP names, e.g. 51515 "Power Hitter" -> AMP 30). The AMP can be put into action sets from now on.
        [SpellEffectHandler(SpellEffectType.UnlockInlaidAugment)]
        private void HandleEffectUnlockInlaidAugment(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            if (NexusForever.Shared.GameTable.GameTableManager.Instance.EldanAugmentation.GetEntry(info.Entry.DataBits00) == null)
            {
                Diagnostics.FailureLedger.Record("amp-unlock", "unknown AMP", $"spell {parameters.SpellInfo.Entry.Id} amp {info.Entry.DataBits00}");
                return;
            }

            Diagnostics.FailureLedger.Record("amp-unlock", player.SpellManager.UnlockAmp(info.Entry.DataBits00) ? "unlocked" : "already open", $"amp {info.Entry.DataBits00}");
        }

        // GiveAugmentPowerToPlayer (only spell 67476 "AMP Power Unlock"): DataBits00 = points (1); at most 10 bonus (prerequisite 28557).
        [SpellEffectHandler(SpellEffectType.GiveAugmentPowerToPlayer)]
        private void HandleEffectGiveAugmentPowerToPlayer(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            if (!player.SpellManager.GrantBonusPoints((int)info.Entry.DataBits00, 0))
                Diagnostics.FailureLedger.Record("amp-unlock", "bonus AMP power at the maximum", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // GiveAbilityPointsToPlayer (only spell 67478 "Ability Tier Point Unlock"): DataBits00 = points (1); at most 7 bonus (prerequisite 28559).
        [SpellEffectHandler(SpellEffectType.GiveAbilityPointsToPlayer)]
        private void HandleEffectGiveAbilityPointsToPlayer(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            if (!player.SpellManager.GrantBonusPoints(0, (int)info.Entry.DataBits00))
                Diagnostics.FailureLedger.Record("amp-unlock", "bonus ability tier points at the maximum", $"spell {parameters.SpellInfo.Entry.Id}");
        }
    
        // Resurrect (20 rows, all target flag 2 or 4 = the dead target, except Time Lapse / Guardian Angel on self): DataBits00 = health,
        // DataBits01 = focus (floats; 0.25 in the out-of-combat "Resurrect"/"Resuscitate", 20-2000 in combat and station versions). The dead
        // player gets the offer on the death screen and takes it with RezType SpellCasterLocation. Dead creatures are not resurrected.
        [SpellEffectHandler(SpellEffectType.Resurrect)]
        private void HandleEffectResurrect(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
            {
                Diagnostics.FailureLedger.Record("rez-rule", "Resurrect effect on a creature ignored", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            if (!player.IsDead)
                return;

            player.OfferCasterResurrection(caster, BitConverter.Int32BitsToSingle((int)info.Entry.DataBits00), BitConverter.Int32BitsToSingle((int)info.Entry.DataBits01));
            Diagnostics.FailureLedger.Record("rez-rule", "caster resurrection offered", $"spell {parameters.SpellInfo.Entry.Id}");
        }
    }
}
