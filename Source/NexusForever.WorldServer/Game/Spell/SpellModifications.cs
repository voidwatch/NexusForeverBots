using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// ModifySpell (effect 55) and its Spell4Modification rows (keyed by the effect row id, spell4EffectsId).
    /// Effect data: DataBits00 = a Spell4Base id (the spells of that base are modified), or 0 and DataBits01 = a spell group id (every spell
    /// whose Spell4GroupList has the group: 460 = the Neutralize variants, 27 = healing spells, 452 = gadgets, 458 = path abilities).
    /// The client keeps a modified copy of each of the player's spells and evaluates the rows the same way (certain, docs/ASSUMPTIONS.md
    /// "Spell modifications"): every active row of a spell for one parameter, ordered by priority, clamp rows (type 2) last within a priority.
    /// Parameter = the spell value changed:
    ///   0 cast time (ms), 1 minimum range, 2 maximum range, 3 cooldown (ms), 4/19 caster innate requirement eval 0/1, 5/6 caster innate
    ///   requirement value 0/1, 7 target innate requirement eval, 8/9 innate cost 0/1, 10 channel initial delay, 11 channel max time,
    ///   12 channel pulse time, 13 threat (no value in the client spell), 14 spell flags, 15/16 Spell4Base aggression/domination magnitude,
    ///   17/18 caster/target Spell4Conditions id, 20 target innate requirement value, 21 aoe target constraints.
    /// Type (data00..03 = d0..d3), the result is a change (types 0, 1) or a value (types 2, 3, 4):
    ///   0: change (d0 - 1) x base + d1; d3 = 0 raises the value up to at most d2, d3 != 0 lowers it down to at least d2
    ///   1: change (d0 - 1) x current; d2 = 0 raises up to at most d1, d2 != 0 lowers down to at least d1
    ///   2: clamp: d1 = 0 at most d0, d1 != 0 at least d0     3: set to d0     4: bits d0, d1 = 0 cleared, d1 != 0 set
    /// Parameters 0-3, 5, 6, 8-13, 15, 16, 20, 21 add up: per priority the changes of the rows are summed (against the value before that
    /// priority), a clamp row of that priority clamps the sum. 14, 17, 18 chain row by row. 4, 7, 19 take only the first row.
    /// So a lone clamp row (Requiem/Combustion "cost 0" = 0, Impulse cooldown 3000) clamps a sum of 0 and changes nothing; "at least" clamps
    /// (row 496: cooldown at least +5000) do add.
    /// </summary>
    public static class SpellModifications
    {
        public const uint CastTime = 0u, MinRange = 1u, Range = 2u, Cooldown = 3u, RequirementEval0 = 4u, RequirementValue0 = 5u, RequirementValue1 = 6u,
            TargetRequirementEval = 7u, InnateCost = 8u, InnateCost1 = 9u, ChannelInitialDelay = 10u, ChannelMaxTime = 11u, ChannelPulseTime = 12u,
            Threat = 13u, SpellFlags = 14u, AggressionMagnitude = 15u, DominationMagnitude = 16u, CasterConditions = 17u, TargetConditions = 18u,
            RequirementEval1 = 19u, TargetRequirementValue = 20u, AoeTargetConstraints = 21u;

        /// <summary>Parameters the server reads after the modifications; the rest are stored and counted.</summary>
        public static bool Applied(uint parameter) => parameter is CastTime or Cooldown or RequirementValue0 or RequirementValue1 or InnateCost
            or InnateCost1 or CasterConditions or TargetConditions;

        private static ILookup<uint, Spell4ModificationEntry> rows;

        public static IReadOnlyList<Spell4ModificationEntry> RowsOf(uint spell4EffectsId)
        {
            rows ??= GameTableManager.Instance.Spell4Modification.Entries.ToLookup(r => r.Spell4EffectsId);
            return rows[spell4EffectsId].OrderBy(r => r.Priority).ToList();
        }

        public static bool Matches(Spell4Entry spell, uint baseId, uint groupId)
        {
            if (spell == null)
                return false;
            return baseId != 0u ? spell.Spell4BaseIdBaseSpell == baseId : SpellGroups.InGroup(spell, groupId);
        }

        private static bool Additive(uint parameter) => parameter < 22u && ((0x31BF6Fu >> (int)parameter) & 1u) != 0u;

        /// <summary>The value of one parameter after the rows (every active row of the spell for that parameter, in the order they became active).</summary>
        public static float Evaluate(IEnumerable<Spell4ModificationEntry> active, uint parameter, float baseValue)
        {
            // stable sort: priority, then the clamp rows of a priority after its other rows (client insertion order)
            List<Spell4ModificationEntry> sorted = active.OrderBy(r => r.Priority).ThenBy(r => r.ModificationTypeEnum == 2u ? 1 : 0).ToList();
            if (sorted.Count == 0)
                return baseValue;

            float value = baseValue;
            if (Additive(parameter))
            {
                foreach (IGrouping<uint, Spell4ModificationEntry> priority in sorted.GroupBy(r => r.Priority))
                {
                    float sum = 0f;
                    foreach (Spell4ModificationEntry row in priority)
                    {
                        if (row.ModificationTypeEnum == 2u)
                        {
                            sum = Step(row, baseValue, sum);
                            break;
                        }
                        sum += Step(row, baseValue, value);
                    }
                    value += sum;
                }
            }
            else if (parameter is SpellFlags or CasterConditions or TargetConditions)
            {
                foreach (Spell4ModificationEntry row in sorted)
                    value = Step(row, baseValue, value);
            }
            else
                value = Step(sorted[0], baseValue, baseValue);
            return value;
        }

        private static float Step(Spell4ModificationEntry row, float baseValue, float current)
        {
            switch (row.ModificationTypeEnum)
            {
                case 0u:
                    return Limited((row.Data00 - 1f) * baseValue + row.Data01, row.Data02 - current, (int)row.Data03 != 0);
                case 1u:
                    return Limited((row.Data00 - 1f) * current, row.Data01 - current, (int)row.Data02 != 0);
                case 2u:
                    return (int)row.Data01 == 0 ? System.MathF.Min(current, row.Data00) : System.MathF.Max(current, row.Data00);
                case 3u:
                    return row.Data00;
                case 4u:
                    uint bits = (uint)(long)row.Data00, now = (uint)(long)current;
                    return (int)row.Data01 == 0 ? now & ~bits : now | bits;
                default:
                    Diagnostics.FailureLedger.Record("spell-modification", $"type {row.ModificationTypeEnum} unknown, gives 0 like the client", $"row {row.Id}");
                    return 0f;
            }
        }

        // a raising row (lower = false) adds at most up to the limit, a lowering row takes away at most down to it
        private static float Limited(float change, float room, bool lower)
        {
            if (!lower)
                return System.MathF.Min(System.MathF.Max(change, 0f), System.MathF.Max(room, 0f));
            return System.MathF.Max(System.MathF.Min(change, 0f), System.MathF.Min(room, 0f));
        }
    }
}
