using System;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Spell4CCConditions (Spell4.spell4CCConditionsIdCaster/Target): ccStateMask selects CC states (bit n = CCStates.tbl row n),
    /// ccStateFlagsRequired says per selected bit whether the state must be on (bit set) or off (bit clear).
    /// Decoded from the retail client (certain): one client function, called from the cast check for the caster
    /// and from the target check. Bit n is tested against the unit's CC state n; a "cannot" failure returns
    /// Caster/TargetCannotBe&lt;state&gt;, a "must" failure Caster/TargetMustBe&lt;state&gt;. For the caster the "cannot" rules are skipped
    /// when a flag of the cast request (+0x48) is set; the server reads that as casts started by effects, which only record (likely).
    /// All 12 rows of the table are "cannot" rows (flags 0): row 1 (49910 spells: stun, sleep, polymorph, fear, knockdown, ...) is the
    /// default for abilities, row 4 adds disarm (weapon abilities), row 5 adds silence (spells).
    /// </summary>
    public static class SpellCCConditions
    {
        private static readonly CastResult?[,] results = Build();

        private static CastResult?[,] Build()
        {
            var table = new CastResult?[32, 4];
            for (int bit = 0; bit < 32; bit++)
            {
                string name = Enum.IsDefined(typeof(CCState), bit) ? ((CCState)bit).ToString() : null;
                if (name == null)
                    continue;
                string[] forms = { $"CasterCannotBe{name}", $"CasterMustBe{name}", $"TargetCannotBe{name}", $"TargetMustBe{name}" };
                for (int i = 0; i < 4; i++)
                {
                    if (Enum.TryParse(forms[i], out CastResult result))
                        table[bit, i] = result;
                    else if (Enum.TryParse($"CC{name}", out CastResult generic))
                        table[bit, i] = generic;
                }
            }
            return table;
        }

        /// <summary>Whether the unit meets every rule of the row, without recording (bots ask before they cast).</summary>
        public static bool Allows(UnitEntity unit, Spell4CCConditionsEntry conditions)
        {
            if (unit == null || conditions == null || conditions.CcStateMask == 0u)
                return true;
            for (int bit = 0; bit < 32; bit++)
            {
                uint mask = 1u << bit;
                if ((conditions.CcStateMask & mask) != 0u && unit.HasCCState((uint)bit) != ((conditions.CcStateFlagsRequired & mask) != 0u))
                    return false;
            }
            return true;
        }

        /// <summary>First CC rule the unit breaks, CastResult.Ok when it meets them all.</summary>
        public static CastResult Check(UnitEntity unit, Spell4CCConditionsEntry conditions, bool isCaster, uint spell4Id)
        {
            if (unit == null || conditions == null || conditions.CcStateMask == 0u)
                return CastResult.Ok;

            for (int bit = 0; bit < 32; bit++)
            {
                uint mask = 1u << bit;
                if ((conditions.CcStateMask & mask) == 0u)
                    continue;

                bool must = (conditions.CcStateFlagsRequired & mask) != 0u;
                if (unit.HasCCState((uint)bit) == must)
                    continue;

                CastResult result = results[bit, (isCaster ? 0 : 2) + (must ? 1 : 0)] ?? CastResult.CCStun;
                Diagnostics.FailureLedger.Record("spell-cc-condition", $"{(isCaster ? "caster" : "target")} {(must ? "must" : "cannot")} be {(CCState)bit}", $"spell {spell4Id} row {conditions.Id}");
                return result;
            }
            return CastResult.Ok;
        }
    }
}
