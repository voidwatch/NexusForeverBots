using System;
using System.Text;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Base amount of a damage or heal effect from the ParameterType/ParameterValue pairs of its Spell4Effects row:
    /// amount = sum of value x stat. Decoded types (see docs/ASSUMPTIONS.md, "Combat formulas"):
    /// 12 assault power (= assault rating x 0.25, see AssaultPowerPerRating), 13 support power, 10 caster level (certain: player abilities carry exactly (12, x) + (10, y),
    /// the greenyneko.com formula baseDamage = assaultPower * mod + levelStatRate * level, Mortar Strike and Quick Draw tooltips);
    /// 1-4 the primary attributes Strength, Dexterity, Technology, Magic (likely: creature attacks pair them with 12 at the same value,
    /// 7374 of 7391 rows, and the attribute follows the damage school); 7 the target's maximum health (likely: tooltips of 5 damage spells say
    /// "of the target's max health", the self heal 42696 "of your Max Health"); 5 Wisdom (likely: heals 27592/27972 "700%/400% of your Wisdom"
    /// carry (5, 7.0)/(5, 4.0)); 8 the max health of the unit hit (likely: Blistering Heat 27935/27936/27946 "8%/16%/24% of your max health" on the
    /// debuffed unit carry (8, 0.08/0.16/0.24), the self heal 46267 "15% of your maximum health" (8, 0.15)).
    /// 6 "Weapon" and 9 "Weapon DPS" (client names, client text table) have no case in the client's amount formula and
    /// add nothing (certain for the client). Other undecoded types are recorded in the ledger and left out.
    /// Names of the rest from upstream NexusForever game_rework SpellEffectParameterType (it matches every type decoded here independently,
    /// and the tooltips of 19 and 22 confirm it): 8 caster max health, 11 Grit (UnitProperty2 40 "Stamina", localized "Grit"), 14/16 caster
    /// shield / max shield, 15/17 target shield / max shield, 19 target health, 21/22 target missing health / shields, 23/25/26 the caster's.
    /// The client's own names confirm all of these; 20/24 are second "Target/Caster Shield Capacity" entries the formula skips,
    /// 18 "Item Budget" multiplies a value the caller passes (unknown here).
    /// </summary>
    public static class SpellFormula
    {
        /// <summary>
        /// Assault/support power per point of assault/support rating. The retail client's Unit:GetAssaultPower and GetSupportPower
        /// (client code, certain) return unit property 35/36 times GameFormula 1266 Datafloat0/Datafloat01
        /// (0.25 both); greenyneko.com "Damage Calculation In WildStar" uses the same 17765 rating / 4 = 4441.25 power in baseDamage, and its
        /// Mortar Strike tooltip (94.02% AP + 29.77 x level) is the table row (0.7953, 25.18) x 1.182 on both terms.
        /// </summary>
        public static float AssaultPowerPerRating => powerFactors.Value.Assault;
        public static float SupportPowerPerRating => powerFactors.Value.Support;

        private static readonly Lazy<(float Assault, float Support)> powerFactors = new(() =>
        {
            GameFormulaEntry f = NexusForever.Shared.GameTable.GameTableManager.Instance.GameFormula.GetEntry(1266u);
            return (f != null && f.Datafloat0 > 0f ? f.Datafloat0 : 0.25f, f != null && f.Datafloat01 > 0f ? f.Datafloat01 : 0.25f);
        });

        /// <summary>
        /// Stat values of a unit: the creature's table stats, for players the CreatureLevel row of their level plus their equipped items.
        /// </summary>
        public static float[] Stats(UnitEntity unit)
        {
            if (unit is NonPlayer npc && npc.CombatStats.Length > 0)
                return npc.CombatStats;
            if (unit is Player player)
                return player.CombatStats;
            return UnitStats.LevelRow(unit.Level);
        }

        /// <summary>
        /// The value a parameter type scales with, null for an undecoded type. <paramref name="modify"/> applies the unit's property modifiers.
        /// </summary>
        public static float? StatFor(uint type, uint level, float[] stats, Func<Property, float, float> modify, UnitEntity target = null, UnitEntity caster = null)
        {
            switch (type)
            {
                case 1u: case 2u: case 3u: case 4u: case 5u:
                    return UnitStats.Get(stats, Property.Strength + (int)(type - 1u));
                case 7u:
                    return target?.MaxHealth;
                case 8u:
                    return (caster ?? target)?.MaxHealth;
                case 11u:
                    return modify(Property.Stamina, UnitStats.Get(stats, Property.Stamina));
                case 14u:
                    return caster?.Shield;
                case 15u:
                    return target?.Shield;
                case 16u:
                    return caster?.GetShieldMax();
                case 17u:
                    return target?.GetShieldMax();
                case 19u:
                    return target?.Health;
                case 21u:
                    return target == null ? null : (float)target.MaxHealth - target.Health;
                case 22u:
                    return target == null ? null : Math.Max(0f, target.GetShieldMax() - target.Shield);
                case 23u:
                    return caster?.Health;
                case 25u:
                    return caster == null ? null : (float)caster.MaxHealth - caster.Health;
                case 26u:
                    return caster == null ? null : Math.Max(0f, caster.GetShieldMax() - caster.Shield);
                case 10u:
                    return level;
                case 12u:
                    return modify(Property.AssaultRating, UnitStats.Get(stats, Property.AssaultRating)) * AssaultPowerPerRating;
                case 13u:
                    return modify(Property.SupportRating, UnitStats.Get(stats, Property.SupportRating)) * SupportPowerPerRating;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Base amount of an effect cast by this unit before crit, resist and random spread, or null when the row has no parameter at all
        /// (the caller keeps the older DataBits00 x power form for those rows).
        /// </summary>
        public static float? Amount(Spell4EffectsEntry entry, UnitEntity caster, UnitEntity target)
        {
            return Amount(entry, caster.Level, Stats(caster), caster.GetModifiedProperty, true, null, target, caster, caster is NonPlayer);
        }

        // GameFormula 712: power soft cap per point of coefficient = level x Datafloat0 + Datafloat01 (70 x level + 100), above it the
        // excess counts as Datafloat02 x (1 - e^-(excess / cap)) of the cap, everything divided by Datafloat03 (1)
        private static readonly Lazy<(float PerLevel, float Base, float Excess, float Divisor)> softCap = new(() =>
        {
            GameFormulaEntry f = NexusForever.Shared.GameTable.GameTableManager.Instance.GameFormula.GetEntry(712u);
            return f == null ? (70f, 100f, 1f, 1f) : (f.Datafloat0, f.Datafloat01, f.Datafloat02, f.Datafloat03);
        });

        /// <summary>
        /// The retail client's effect amount (client code; docs/COMBAT-MATH.md step 3, certain for the client):
        /// stat part = sum of value x stat over types 1-5 (attributes), 11 (Grit), 12/13 (assault/support power), rounded;
        /// flat part = sum of value x quantity over 7, 8, 10, 14-19, 21-23, 25, 26; types 6 and 9 (weapon), 20 and 24 add nothing.
        /// Damage, Heal, DistanceDependentDamage, DistributedDamage, HealShields and DamageShields: flat = (DataBits01 + flat) x DataBits00
        /// (Transference: DataBits03 / DataBits02). Creature casters (not characters, Unit:IsACharacter = type 20) soft cap the stat part at
        /// (70 x level + 100) x the mean stat coefficient; above the cap only cap x (1 - e^-(excess / cap)) more counts.
        /// <paramref name="explain"/> receives the terms (for 'gt combat').
        /// </summary>
        public static float? Amount(Spell4EffectsEntry entry, uint level, float[] stats, Func<Property, float, float> modify, bool record, StringBuilder explain = null, UnitEntity target = null, UnitEntity caster = null, bool creature = false)
        {
            float statSum = 0f, statCoefficients = 0f, flat = 0f;
            int statTerms = 0;
            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                (uint type, float value) = i switch
                {
                    0 => (entry.ParameterType00, entry.ParameterValue00),
                    1 => (entry.ParameterType01, entry.ParameterValue01),
                    2 => (entry.ParameterType02, entry.ParameterValue02),
                    _ => (entry.ParameterType03, entry.ParameterValue03)
                };
                if (type == 0u)
                    continue;

                any = true;
                bool statType = type is >= 1u and <= 5u or 11u or 12u or 13u;
                if (type is 6u or 9u or 20u or 24u)
                {
                    // the client's formula has no case for these (weapon, weapon DPS and the second shield names): they add nothing
                    explain?.Append($" type{type}x{value:0.####}=0(client)");
                    continue;
                }

                float? stat = StatFor(type, level, stats, modify, target, caster);
                if (stat == null)
                {
                    if (record)
                        Diagnostics.FailureLedger.Record("spell-param-unknown", $"type {type} effect {entry.EffectType}", $"spell {entry.SpellId} value {value}");
                    explain?.Append($" type{type}x{value:0.####}=unknown");
                    continue;
                }

                if (statType)
                {
                    statSum += value * stat.Value;
                    statCoefficients += value;
                    statTerms++;
                }
                else
                    flat += value * stat.Value;
                explain?.Append($" type{type}:{value:0.####}x{stat.Value:0.##}");
            }

            // a row without parameters but with a designer amount (DataBits01, Transference DataBits03: Ice Blizzard 1500, Firebloom 1200,
            // mine explosions 450) is that amount x DataBits00 in the client formula (3521 of 16874 bare damage/heal rows);
            // rows with neither stay on the caller's DataBits00 x power fallback (the client would give 0)
            if (!any && ((entry.EffectType == (uint)NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.Transference ? entry.DataBits03 : entry.DataBits01) != 0u))
            {
                any = true;
                if (record)
                    Diagnostics.FailureLedger.Record("spell-amount-flat-only", "row without parameters, designer amount x DataBits00");
            }

            if (!any)
            {
                if (record)
                    Diagnostics.FailureLedger.Record("spell-amount-fallback", "row without parameters or amount: DataBits00 x power", $"spell {entry.SpellId}");
                return null;
            }

            statSum = MathF.Round(statSum, MidpointRounding.AwayFromZero);
            switch ((NexusForever.WorldServer.Game.Spell.Static.SpellEffectType)entry.EffectType)
            {
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.Damage:
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.Heal:
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.DistanceDependentDamage:
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.DistributedDamage:
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.HealShields:
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.DamageShields:
                    flat = (entry.DataBits01 + flat) * BitConverter.Int32BitsToSingle((int)entry.DataBits00);
                    break;
                case NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.Transference:
                    flat = (entry.DataBits03 + flat) * BitConverter.Int32BitsToSingle((int)entry.DataBits02);
                    break;
            }
            if (float.IsNaN(flat) || float.IsInfinity(flat))
                flat = 0f;

            (float perLevel, float capBase, float excess, float divisor) = softCap.Value;
            if (creature && statTerms > 0 && divisor != 0f)
            {
                float cap = (level * perLevel + capBase) * MathF.Abs(statCoefficients / statTerms);
                if (statSum > cap && cap > 0f)
                {
                    explain?.Append($" softcap {cap:0.##}");
                    statSum = cap * (1f + excess * (1f - MathF.Exp(-(statSum - cap) / cap)));
                }
                statSum /= divisor;
                flat /= divisor;
            }

            return statSum + flat;
        }
    }
}
