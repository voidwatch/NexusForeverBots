using System;
using System.Linq;
using System.Numerics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Movement.Generator;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Crowd control, immunity, aura removal and forced movement. Field meanings come from the game table dump of real spells
    /// (see scripts/spell-data.sh), each handler says which ones are certain.
    /// </summary>
    public partial class Spell
    {
        // DataBits00 = CCState, DurationTime = milliseconds (Stagger: no DataBits00 and 2500 ms, that is Stun; Spatial Shift: 19, PullToLocation)
        [SpellEffectHandler(SpellEffectType.CCStateSet)]
        private void HandleEffectCCStateSet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            var state = (CCState)info.Entry.DataBits00;
            if (target.IsCrowdControlImmune)
            {
                Diagnostics.FailureLedger.Record("cc-state", $"{state} blocked by immunity");
                return;
            }

            target.SetCCState(info.Entry.DataBits00, info.Entry.DurationTime / 1000d * System.Math.Max(0f, target.GetModifiedProperty(NexusForever.WorldServer.Game.Entity.Static.Property.CCDurationModifier, 1f)));
            Diagnostics.FailureLedger.Record("cc-state", $"{state} applied to {(target is Player ? "player" : "creature")}", $"spell {parameters.SpellInfo.Entry.Id}");

            // proc trigger 41 = being crowd controlled (certain: 12 tooltips "Whenever you are CC'd ...", "When hit by a CC ...")
            target.FireProcs(41u, caster);
        }

        // SpellDispel: DataBits03 = the Spell4Base.spellClass to remove (36 BuffDispellable, 38 DebuffDispellable, 14 BuffNonDispelRightClickOk;
        // certain: Purify "cleanse N debuffs" 38, Cleanse "removes all beneficial magic effects" 36), DataBits00/01 = how many (tooltips cite
        // both as the count; -1 = all, Void Slip "removing all ... debuffs"), DataBits05 1-3 = dispel power ("Hostile Low/Mid/High Power
        // Cancel"), DataBits02/04 unknown. Removes the newest matching lasting effects up to DataBits01 (likely); power is not compared (no
        // per-spell resistance field found). The server does not track the casting id of other casters' buffs, so client icons stay until they expire.
        [SpellEffectHandler(SpellEffectType.SpellDispel)]
        private void HandleEffectSpellDispel(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint spellClass = info.Entry.DataBits03;
            uint max = info.Entry.DataBits01;
            uint removed = 0u;
            foreach (uint spell4Id in Enumerable.Reverse(target.ActiveEffectSpellIds()))
            {
                if (removed >= max)
                    break;
                Shared.GameTable.Model.Spell4Entry spell = Shared.GameTable.GameTableManager.Instance.Spell4.GetEntry(spell4Id);
                if (spell == null || Shared.GameTable.GameTableManager.Instance.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell)?.SpellClass != spellClass)
                    continue;
                target.RemoveSpellEffects(spell4Id);
                removed++;
            }
            Diagnostics.FailureLedger.Record("spell-dispel", $"class {spellClass}: removed {(removed == 0u ? "none" : removed.ToString())}", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = bit mask of CCState values to clear (Unstoppable Force: 0x1B0EFFF, every breakable state)
        [SpellEffectHandler(SpellEffectType.CCStateBreak)]
        private void HandleEffectCCStateBreak(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            int cleared = target.BreakCCStates(info.Entry.DataBits00);
            if (cleared > 0)
                Diagnostics.FailureLedger.Record("cc-state", "states broken", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DurationTime = milliseconds (0 = until removed, capped at 30 s so a scripted phase cannot make something unkillable forever),
        // DataBits00 = kind. From the spell names: 4 crowd control, 8 damage, 3 force move, 2 interrupt (spell counter), 29 healing,
        // 32 invulnerable, 31 and 60 "CC, force move, disguise and charm". Other kinds are stored and reported.
        [SpellEffectHandler(SpellEffectType.SpellEffectImmunity)]
        private void HandleEffectSpellEffectImmunity(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint kind = info.Entry.DataBits00;
            double seconds = info.Entry.DurationTime > 0u ? info.Entry.DurationTime / 1000d : 30d;
            target.AddImmunity(kind, seconds);
            Diagnostics.FailureLedger.Record("cc-state", $"immunity kind {kind} applied", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = 2 (by spell), DataBits01 = spell id of the effect to remove
        [SpellEffectHandler(SpellEffectType.SpellForceRemove)]
        private void HandleEffectSpellForceRemove(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            int removed = target.RemoveAuras(info.Entry.DataBits01);
            Diagnostics.FailureLedger.Record("aura-removal", removed > 0 ? "removed an active effect" : "nothing to remove", $"spell {info.Entry.DataBits01} by {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = direction (3 forward, 12 backward, inferred from Leap and Urgent Withdrawal), DataBits01 = distance as float,
        // DataBits03 = duration in milliseconds. Only creatures and bots are moved, a human's client moves itself.
        [SpellEffectHandler(SpellEffectType.ForcedMove)]
        private void HandleEffectForcedMove(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is Player { Session: not Bots.BotSession })
            {
                Diagnostics.FailureLedger.Record("forced-move", "skipped for a human player");
                return;
            }

            if (target.IsForceMoveImmune)
            {
                Diagnostics.FailureLedger.Record("forced-move", "blocked by immunity");
                return;
            }

            uint direction = info.Entry.DataBits00;
            if (direction != 3u && direction != 12u)
            {
                Diagnostics.FailureLedger.Record("forced-move", $"direction {direction} not understood", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            WorldEntity reference = caster.TargetGuid != 0u ? caster.GetVisible<WorldEntity>(caster.TargetGuid) : null;
            if (reference == null || target.Map == null || target.MovementManager == null)
            {
                Diagnostics.FailureLedger.Record("forced-move", "no target to move relative to");
                return;
            }

            float distance = BitConverter.Int32BitsToSingle((int)info.Entry.DataBits01);
            double seconds = Math.Max(info.Entry.DataBits03, 100u) / 1000d;
            if (distance <= 0f || distance > 60f)
            {
                Diagnostics.FailureLedger.Record("forced-move", $"distance {distance} out of range", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            Vector3 toward = Vector3.Normalize(new Vector3(reference.Position.X - target.Position.X, 0f, reference.Position.Z - target.Position.Z));
            if (float.IsNaN(toward.X))
                return;

            Vector3 offset = toward * distance * (direction == 12u ? -1f : 1f);
            var final = new Vector3(target.Position.X + offset.X, target.Position.Y, target.Position.Z + offset.Z);
            final.Y = target.Map.GetTerrainHeight(final.X, final.Z, target.Position.Y);

            target.MovementManager.LaunchGenerator(new DirectMovementGenerator
            {
                Begin = target.Position,
                Final = final,
                Map   = target.Map
            }, (float)(distance / seconds));

            Diagnostics.FailureLedger.Record("forced-move", direction == 12u ? "moved backward" : "moved forward", $"spell {parameters.SpellInfo.Entry.Id}");
        }
    }
}
