using System;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Secondary stats and the parts of the damage formula built on them.
    /// Rating to percent: the retail client's curve (client code, called by every Unit:Get*Chance/Get*RatingPercent Lua API,
    /// certain). Each stat has a GameFormula row: slope s = Datafloat0 / level x Datafloat01 (percent per rating), linear up to the
    /// knee Datafloat02 (value Dataint0 at rating 0), above it knee + s x over x cos(pi/2 x over / (over + Datafloat03)) (diminishing returns),
    /// then percent x 0.01 + the unit's base property, capped at Dataint01 x 0.01.
    /// What each stat does: client character sheet tooltips (en-US.bin), e.g. "Your chance to Deflect an attack, avoiding all damage from that attack".
    /// Order and composition (greenyneko.com "Damage Calculation In WildStar"): crit as base x severity, armor pierce as resist x (1 - pierce),
    /// deflect as deflect - strikethrough. See docs/ASSUMPTIONS.md "Rating curves".
    /// </summary>
    public static class DamageMath
    {
        private static readonly Random rng = new();

        /// <summary>Player base crit chance when no effect sets BaseCritChance (guess; UnitProperty2 default is 0, see docs/ASSUMPTIONS.md).</summary>
        public const float DefaultCritChance   = 0.05f;
        public const float DefaultCritSeverity = 1.5f;

        // GameFormula rows read by the client per stat (certain, research/decode/ratings/curves.tsv)
        public const uint FormulaStrikethrough   = 1230;
        public const uint FormulaCritChance      = 1231;
        public const uint FormulaCritSeverity    = 1232;
        public const uint FormulaLifesteal       = 1233;
        public const uint FormulaMitigation      = 1234;
        public const uint FormulaDeflect         = 1235;
        public const uint FormulaDeflectCrit     = 1236;
        public const uint FormulaMultiHitChance  = 1240;
        public const uint FormulaReflectChance   = 1241;
        public const uint FormulaCritMitigation  = 1242;
        public const uint FormulaIntensity       = 1243;
        public const uint FormulaVigor           = 1244;
        public const uint FormulaGlanceChance    = 1245;
        public const uint FormulaArmorPierce     = 1269;
        public const uint FormulaMultiHitAmount  = 1270;
        public const uint FormulaGlanceAmount    = 1271;
        public const uint FormulaReflectAmount   = 1272;
        public const uint FormulaTenacity        = 2378;
        public const uint FormulaHope            = 2379;
        public const uint FormulaToughness       = 2380;

        /// <summary>
        /// The client's rating curve: the unit's stat (base fraction + rating converted) for a GameFormula row, capped by the row.
        /// </summary>
        public static float Curve(uint formulaId, float rating, uint level, float baseValue)
        {
            GameFormulaEntry f = GameTableManager.Instance.GameFormula.GetEntry(formulaId);
            if (f == null || level == 0u)
                return baseValue;

            float slope = f.Datafloat0 / level * f.Datafloat01;
            float value;
            if (slope <= 0f)
                value = f.Dataint0;
            else
            {
                float knee = (f.Datafloat02 - f.Dataint0) / slope;
                if (rating < knee)
                    value = slope * rating + f.Dataint0;
                else
                {
                    float over = rating - knee;
                    float scale = over + f.Datafloat03 > 0f ? over / (over + f.Datafloat03) : 0f;
                    value = MathF.Cos(MathF.PI * 0.5f * scale) * (over * slope) + f.Datafloat02;
                }
            }

            return MathF.Min(value * 0.01f + baseValue, f.Dataint01 * 0.01f);
        }

        /// <summary>A unit's value of a property: its table/gear value with the active property modifiers.</summary>
        public static float Value(UnitEntity unit, Property property, float fallback = 0f)
        {
            float[] stats = unit switch
            {
                NonPlayer npc => npc.CombatStats,
                Player player => player.CombatStats,
                _ => null
            };
            float baseValue = stats != null && (uint)property < stats.Length ? stats[(uint)property] : fallback;
            float value = unit.GetModifiedProperty(property, baseValue);
            return converting ? value : value + Converted(unit, property, stats);
        }

        [ThreadStatic]
        private static bool converting;

        /// <summary>
        /// UnitPropertyConversion (effect 145): DataBits00 = source property, DataBits01 = target property, DataBits02 = share (float). The target
        /// gains share x the source's value (tooltips: Power Converter 58994 "Increase your Multi-Hit Severity rating by 50% of your Critical Hit
        /// Severity rating", rune Mountain 82056 "Increase your Armor by 1% of your Max Health" with source 7 BaseHealth = max health). The source is
        /// read without other conversions, so rows that convert both ways (test spell 86841) do not feed each other.
        /// </summary>
        private static float Converted(UnitEntity unit, Property property, float[] stats)
        {
            float sum = 0f;
            foreach (UnitEntity.PropertyModifierEntry m in unit.PropertyModifiers)
            {
                if (m.ConvertFrom is not Property source || m.Property != property)
                    continue;
                converting = true;
                try
                {
                    float sourceValue = source == Property.BaseHealth ? unit.MaxHealth : Value(unit, source);
                    sum += m.ValueA * sourceValue;
                }
                finally
                {
                    converting = false;
                }
            }
            return sum;
        }

        /// <summary>A secondary stat of a unit as a fraction: base property plus the converted rating property.</summary>
        public static float Stat(UnitEntity unit, uint formulaId, Property baseProperty, Property ratingProperty, float baseDefault = 0f)
        {
            return Curve(formulaId, MathF.Max(0f, Value(unit, ratingProperty)), unit.Level, Value(unit, baseProperty, baseDefault));
        }

        public static float CritChance(UnitEntity unit) => Stat(unit, FormulaCritChance, Property.BaseCritChance, Property.RatingCritChanceIncrease, unit is Player ? DefaultCritChance : 0f);
        public static float CritSeverity(UnitEntity unit) => Stat(unit, FormulaCritSeverity, Property.CriticalHitSeverityMultiplier, Property.RatingCritSeverityIncrease, DefaultCritSeverity);
        public static float DeflectCritChance(UnitEntity unit) => Stat(unit, FormulaDeflectCrit, Property.BaseAvoidCritChance, Property.RatingCritChanceDecrease);
        public static float CritMitigation(UnitEntity unit) => Stat(unit, FormulaCritMitigation, Property.BaseCriticalMitigation, Property.RatingCriticalMitigation);
        public static float DeflectChance(UnitEntity unit) => Stat(unit, FormulaDeflect, Property.BaseAvoidChance, Property.RatingAvoidIncrease);
        public static float Strikethrough(UnitEntity unit) => Stat(unit, FormulaStrikethrough, Property.BaseAvoidReduceChance, Property.RatingAvoidReduce);
        public static float ArmorPierce(UnitEntity unit) => Stat(unit, FormulaArmorPierce, Property.IgnoreArmorBase, Property.RatingArmorPierce);
        public static float GlanceChance(UnitEntity unit) => Stat(unit, FormulaGlanceChance, Property.BaseGlanceChance, Property.RatingGlanceChance);
        public static float GlanceAmount(UnitEntity unit) => Stat(unit, FormulaGlanceAmount, Property.BaseGlanceAmount, Property.RatingGlanceAmount);
        public static float MultiHitChance(UnitEntity unit) => Stat(unit, FormulaMultiHitChance, Property.BaseMultiHitChance, Property.RatingMultiHitChance);
        public static float MultiHitAmount(UnitEntity unit) => Stat(unit, FormulaMultiHitAmount, Property.BaseMultiHitAmount, Property.RatingMultiHitAmount);
        public static float Lifesteal(UnitEntity unit) => Stat(unit, FormulaLifesteal, Property.BaseLifesteal, Property.RatingLifesteal);
        public static float ReflectChance(UnitEntity unit) => Stat(unit, FormulaReflectChance, Property.BaseDamageReflectChance, Property.RatingDamageReflectChance);
        public static float ReflectAmount(UnitEntity unit) => Stat(unit, FormulaReflectAmount, Property.BaseDamageReflectAmount, Property.RatingDamageReflectAmount);
        public static float Intensity(UnitEntity unit) => Stat(unit, FormulaIntensity, Property.BaseIntensity, Property.RatingIntensity);
        public static float Vigor(UnitEntity unit) => Stat(unit, FormulaVigor, Property.BaseVigor, Property.RatingVigor);
        public static float Tenacity(UnitEntity unit) => Stat(unit, FormulaTenacity, Property.BaseTenacity, Property.RatingTenacity);
        public static float Hope(UnitEntity unit) => Stat(unit, FormulaHope, Property.BaseHope, Property.RatingHope);
        public static float Toughness(UnitEntity unit) => Stat(unit, FormulaToughness, Property.BaseToughness, Property.RatingToughness);

        /// <summary>
        /// Mitigation (0 to 0.75) of a damage type (1 physical, 2 tech, 3 magic) as the client computes it for the character sheet
        /// (certain): DamageMitigationPctOffset + the type's offset + curve 1234 of (armor + the type's resist rating).
        /// </summary>
        public static float Mitigation(UnitEntity unit, uint damageType)
        {
            int offset = (int)Math.Clamp(damageType, 1u, 3u) - 1;
            float rating = Value(unit, Property.Armor) + Value(unit, Property.ResistPhysical + offset);
            float baseValue = Value(unit, Property.DamageMitigationPctOffset) + Value(unit, Property.DamageMitigationPctOffsetPhysical + offset);
            return Math.Clamp(Curve(FormulaMitigation, MathF.Max(0f, rating), unit.Level, baseValue), 0f, 1f);
        }

        /// <summary>Physical mitigation of a unit (used by creature auto attacks and the gt combat checks).</summary>
        public static float ArmorResist(UnitEntity unit) => Mitigation(unit, 1u);

        /// <summary>Mitigation of an armor value alone for a unit of a level (gt combat checks).</summary>
        public static float ArmorResist(float armor, uint level) => Math.Clamp(Curve(FormulaMitigation, MathF.Max(0f, armor), level, 0f), 0f, 1f);

        /// <summary>Outcome of one hit after the secondary stats of both sides.</summary>
        public struct HitResult
        {
            public bool Deflected;
            public bool Crit;
            public bool Glanced;
            public bool MultiHit;
            /// <summary>Multiplier on the base amount (crit, glance, mitigation after pierce, vigor/tenacity/toughness, multi-hit).</summary>
            public float Multiplier;
            public float Mitigation;
        }

        /// <summary>
        /// Rolls one damaging hit of a type from attacker to target with the client's stat curves. Creatures carry no ratings in the tables,
        /// so for them only the base properties and spell modifiers count.
        /// </summary>
        public static HitResult RollHit(UnitEntity attacker, UnitEntity target, uint damageType)
        {
            var result = new HitResult { Multiplier = 1f };

            // "Your chance to Deflect an attack, avoiding all damage"; "Strikethrough that exceeds a foe's Deflect rate is converted into Armor Pierce"
            float deflect = DeflectChance(target);
            float strike = Strikethrough(attacker);
            if (deflect - strike > 0f && rng.NextDouble() < deflect - strike)
            {
                result.Deflected = true;
                result.Multiplier = 0f;
                return result;
            }
            float pierce = Math.Clamp(ArmorPierce(attacker) + MathF.Max(0f, strike - deflect), 0f, 1f);

            // "Your attacks ignore this percent of the foe's mitigation"
            if (damageType >= 1u && damageType <= 3u)
            {
                result.Mitigation = Mitigation(target, damageType) * (1f - pierce);
                result.Multiplier *= 1f - result.Mitigation;
            }

            // "Reduces the chance foes have to Critically Hit you"; "Reduces bonus damage caused by a foe's Critical Hit"
            if (rng.NextDouble() < CritChance(attacker) - DeflectCritChance(target))
            {
                result.Crit = true;
                float bonus = MathF.Max(0f, CritSeverity(attacker) - 1f) * (1f - Math.Clamp(CritMitigation(target), 0f, 1f));
                result.Multiplier *= 1f + bonus;
            }

            // "Your chance to reduce each incoming damaging attack by your Glance Mitigation"
            float glance = GlanceChance(target);
            if (glance > 0f && rng.NextDouble() < glance)
            {
                result.Glanced = true;
                result.Multiplier *= 1f - Math.Clamp(GlanceAmount(target), 0f, 1f);
            }

            // "damage is increased up to this percent, based on your current health" / "target's current missing health";
            // "damage reduction is increased up to this percent, based on your current missing health". Vigor's row starts at 100 (a factor).
            float vigor = Vigor(attacker) - 1f;
            if (vigor > 0f)
                result.Multiplier *= 1f + vigor * HealthFraction(attacker);
            float tenacity = Tenacity(attacker);
            if (tenacity > 0f)
                result.Multiplier *= 1f + tenacity * (1f - HealthFraction(target));
            float toughness = Toughness(target);
            if (toughness > 0f)
                result.Multiplier *= 1f - Math.Clamp(toughness * (1f - HealthFraction(target)), 0f, 1f);

            // "chance for each application of damage or healing to be applied an additional time at a reduced value"
            float multiChance = MultiHitChance(attacker);
            if (multiChance > 0f && rng.NextDouble() < multiChance)
            {
                result.MultiHit = true;
                result.Multiplier *= 1f + MultiHitAmount(attacker);
            }
            return result;
        }

        /// <summary>
        /// Rolls one heal: crit as for damage, Intensity ("outgoing healing increased by this percent") and Hope
        /// ("healing is increased up to this percent, based on your target's current missing health"). Returns the multiplier.
        /// </summary>
        public static float RollHeal(UnitEntity healer, UnitEntity target, out bool crit)
        {
            float multiplier = 1f;
            crit = rng.NextDouble() < CritChance(healer);
            if (crit)
                multiplier *= MathF.Max(1f, CritSeverity(healer));
            multiplier *= 1f + Intensity(healer);
            float hope = Hope(healer);
            if (hope > 0f)
                multiplier *= 1f + hope * (1f - HealthFraction(target));
            if (rng.NextDouble() < MultiHitChance(healer))
                multiplier *= 1f + MultiHitAmount(healer);
            return multiplier;
        }

        /// <summary>
        /// After a hit landed: the attacker heals for its Lifesteal share, and the target may reflect a share back ("Your chance to return
        /// damage dealt to you back to the foe at a reduced value"). Reflected damage is not reflected again.
        /// </summary>
        public static void AfterHit(UnitEntity attacker, UnitEntity target, uint damage)
        {
            if (damage == 0u || attacker == null || target == null || attacker == target)
                return;

            float lifesteal = Lifesteal(attacker);
            if (lifesteal > 0f && !attacker.IsDead)
                attacker.Heal((uint)MathF.Max(1f, MathF.Round(damage * lifesteal)));

            float reflect = ReflectChance(target);
            if (reflect > 0f && !attacker.IsDead && rng.NextDouble() < reflect)
            {
                uint back = (uint)MathF.Round(damage * Math.Clamp(ReflectAmount(target), 0f, 5f));
                if (back > 0u)
                    attacker.TakeDamage(target, back, out _);
            }
        }

        private static float HealthFraction(UnitEntity unit)
        {
            uint max = unit.MaxHealth;
            return max == 0u ? 1f : Math.Clamp((float)unit.Health / max, 0f, 1f);
        }

        /// <summary>
        /// Rolls a crit for a hit by this unit with its crit chance and severity. Returns the damage multiplier (1 for no crit).
        /// </summary>
        public static float CritMultiplier(UnitEntity attacker)
        {
            float chance = CritChance(attacker);
            if (rng.NextDouble() >= chance)
                return 1f;
            return MathF.Max(1f, CritSeverity(attacker));
        }

        /// <summary>
        /// One crit roll with a chance and severity: the multiplier of the hit (1 when it does not crit). Expected value 1 + chance * (severity - 1).
        /// </summary>
        public static float RollCrit(float chance, float severity)
        {
            return rng.NextDouble() < Math.Clamp(chance, 0f, 1f) ? Math.Max(1f, severity) : 1f;
        }
    }
}
