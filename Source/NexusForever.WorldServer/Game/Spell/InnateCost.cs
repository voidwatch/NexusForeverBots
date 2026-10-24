using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Class resources: Spell4.InnateCostType0/1 and InnateCost0/1, the caster innate requirements, pool sizes and regeneration (tasks 21, 22).
    /// The cost type is a Vital table id (certain: 1 Health, 4 Kinetic Energy, 5 Endurance, 6-11 Resource1-6, 12 Suit Power, 15 Focus,
    /// 16 Dash, 18 Actuator, 19 Spell Surge, 26 Volatility). Class.vitalEnumResourceNN names the vital behind resource slot NN per class
    /// (Warrior 4, Engineer 26, Medic 18 in slot 1; Stalker 12 in slot 3; Spellslinger 19 in slot 4; every class 5 in slot 0 and 16 in slot 7).
    /// Only Resource1 costs are charged (Esper, Engineer, Medic): their builders refill it through VitalModifier rows. Stalker Suit Power
    /// (Resource3) and Focus are counted, not charged: no table, client or public source gives the base Suit Power regeneration or the
    /// Focus pool size (docs/COMBAT-MATH.md "Resource economy").
    /// </summary>
    public static class InnateCost
    {
        /// <summary>
        /// Charging is on with NF_INNATE_COSTS=1 (docker-compose INNATE_COSTS), see docs/COMBAT-MATH.md "Resource economy" for the per-class
        /// gate evidence.
        /// </summary>
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("NF_INNATE_COSTS") != "0";

        /// <summary>
        /// Resource slot (Stat Resource0-6, 7 = Dash) a vital refers to for this player's class, null when the vital is not a class resource.
        /// </summary>
        public static uint? ClassSlot(Player player, uint vital)
        {
            if (vital >= 5u && vital <= 11u)
                return vital - 5u;

            ClassEntry entry = GameTableManager.Instance.Class.GetEntry((byte)player.Class);
            if (entry == null || vital == 0u)
                return null;
            uint[] slots = { entry.VitalEnumResource00, entry.VitalEnumResource01, entry.VitalEnumResource02, entry.VitalEnumResource03,
                entry.VitalEnumResource04, entry.VitalEnumResource05, entry.VitalEnumResource06, entry.VitalEnumResource07 };
            for (uint i = 0u; i < slots.Length; i++)
                if (slots[i] == vital)
                    return i;
            return null;
        }

        /// <summary>Resource index charged for the cost type, null when the cost is not charged for this player.</summary>
        private static uint? ChargedResource(Player player, uint vital)
        {
            uint? slot = ClassSlot(player, vital);
            // Resource1 (Esper, Engineer, Medic, Warrior pools) and Spellslinger Spell Power (Resource4, sourced regeneration below; only the
            // Surge variants cost it, vital 19 x 25)
            if (slot == 1u || (slot == 4u && player.Class == Class.Spellslinger))
                return slot;
            return null;
        }

        /// <summary>
        /// Pool per class: (resource slot, maximum, value at login). Sources (docs/ASSUMPTIONS.md "Combat resource economy"):
        /// Warrior 1000 Kinetic Energy (certain, wiki Warrior "maximum value of 1000KE"), Engineer 100 Volatility resting at 0 (certain, wiki
        /// Engineer "maximum capacity of one hundred, and a resting state of zero"), Medic 4 Actuators starting full (certain, wiki Medic
        /// "Actuators start at four charges"), Esper 5 Psi Points (likely: spender tooltips list 1PP-5PP, Mind Burst 32809), Spellslinger
        /// 100 Spell Power starting full (likely: 4 circles, Spell Surge "Requires 25 Spell Power"), Stalker 100 Suit Power starting full
        /// (guess for 100: costs 30-35, Punish "if below 35 Suit Power"; full at start from guides).
        /// </summary>
        private static (uint Slot, float Max, float Start)? Pool(Class playerClass)
        {
            switch (playerClass)
            {
                case Class.Warrior:      return (1u, 1000f, 0f);
                case Class.Engineer:     return (1u, 100f, 0f);
                case Class.Esper:        return (1u, 5f, 0f);
                case Class.Medic:        return (1u, 4f, 4f);
                case Class.Stalker:      return (3u, 100f, 100f);
                case Class.Spellslinger: return (4u, 100f, 100f);
                default:                 return null;
            }
        }

        /// <summary>Set the class resource maximum and its value at login (Player constructor).</summary>
        public static void InitialisePool(Player player)
        {
            var pool = Pool(player.Class);
            if (pool == null)
                return;
            player.SetClassResourcePool(pool.Value.Slot, pool.Value.Max, pool.Value.Start);
        }

        /// <summary>
        /// Regeneration that has a source: Spellslinger Spell Power 25 per 12 s in and out of combat (likely: tentonhammer Spellslinger class
        /// guide "recharges at the same rate in or out of combat and takes around 12 seconds from empty to one full circle", 4 circles = 100);
        /// Medic Actuators refill to 4 out of combat (wiki Medic "When you leave combat, your Actuators will quickly restore to a full four
        /// charges": certain that they refill, the rate of 1 per second is a guess). Not modelled (no rate anywhere): Kinetic Energy decay
        /// (in and out of combat), Volatility decay out of combat, Suit Power regeneration. Focus recovers from the client formula (RecoverFocus).
        /// </summary>
        public static void Regenerate(Player player, double lastTick)
        {
            player.ResourceRegenClock += lastTick;
            if (player.ResourceRegenClock < 1d)
                return;
            lastTick = player.ResourceRegenClock;
            player.ResourceRegenClock = 0d;

            RecoverFocus(player, lastTick);
            Regeneration.Apply(player, lastTick);

            switch (player.Class)
            {
                case Class.Spellslinger:
                    Refill(player, 4u, (float)(lastTick * 25d / 12d));
                    break;
                case Class.Medic:
                    if (!player.InCombat)
                        Refill(player, 1u, (float)lastTick);
                    break;
            }
        }

        /// <summary>
        /// Focus (vital 15) recovery, the client's own numbers (certain): the pool is property 5 BaseFocusPool (unit+0x5ec, from gear, 1.5 per
        /// point); out of combat the unit regains BaseFocusRecoveryOutofCombat (108, default 0.011) x pool per second (Lua GetFocusRegenNonCombat);
        /// in combat the rate is the rating curve of BaseFocusRecoveryInCombat (107, 0.011) and RatingFocusRecovery (6) with
        /// GameFormula 1237, applied every GameFormula 88 ms (500) as pool x rate x ms / 1000 (GetFocusRegenInCombat).
        /// The wiki Stat page agrees: "Percent of your Focus Pool that you regenerate each second while in combat".
        /// </summary>
        private static void RecoverFocus(Player player, double seconds)
        {
            if (player.IsDead)
                return;
            float max = player.GetVitalMax(15u);
            if (max <= 0f || player.Focus >= max)
                return;
            float rate = player.InCombat
                ? Combat.DamageMath.Stat(player, 1237u, Property.BaseFocusRecoveryInCombat, Property.RatingFocusRecovery, 0.011f)
                : Combat.DamageMath.Value(player, Property.BaseFocusRecoveryOutofCombat, 0.011f);
            player.AddVital(15u, (float)(max * rate * seconds));
        }

        private static void Refill(Player player, uint slot, float amount)
        {
            if (player.IsDead)
                return;
            float max = player.GetVitalMax(5u + slot);
            if (max <= 0f || player.GetResource(slot) >= max)
                return;
            player.AddVital(5u + slot, amount);
        }

        /// <summary>Whether casting the spell is limited by a class resource this server tracks (a charged cost or a checked requirement).</summary>
        public static bool IsResourceGated(Player player, Spell4Entry spell)
        {
            if (!Enabled)
                return false;
            if ((spell.InnateCostType0 != 0u && ChargedResource(player, spell.InnateCostType0) != null)
                || (spell.InnateCostType1 != 0u && ChargedResource(player, spell.InnateCostType1) != null))
                return true;
            foreach (uint vital in new[] { spell.CasterInnateRequirement0, spell.CasterInnateRequirement1 })
            {
                uint? slot = vital == 0u ? null : ClassSlot(player, vital);
                if (slot != null && slot.Value <= 6u && player.GetVitalMax(5u + slot.Value) > 0f)
                    return true;
            }
            return false;
        }

        public static bool CanPay(Player player, Spell4Entry spell)
        {
            if (!Enabled)
                return true;
            return CanPay(player, spell.InnateCostType0, Cost(player, spell, spell.InnateCost0, SpellModifications.InnateCost)) && CanPay(player, spell.InnateCostType1, Cost(player, spell, spell.InnateCost1, SpellModifications.InnateCost1))
                && MeetsRequirement(player, spell, 0, false) && MeetsRequirement(player, spell, 1, false);
        }

        private static bool CanPay(Player player, uint vital, uint cost)
        {
            if (vital == 0u || cost == 0u)
                return true;
            uint? resource = ChargedResource(player, vital);
            return resource == null || player.GetResource(resource.Value) >= cost;
        }

        /// <summary>
        /// Spell4.casterInnateRequirementN (a vital), ...ValueN and ...EvalN: eval 2 is "at least" (likely: Warrior Whirlwind 33802 250 Kinetic
        /// Energy = the 250/500/750 thresholds of the wiki, Medic Fusion Probe 88576 1 Actuator and 40 Focus). Other evals (1 on Plasma Wall
        /// 38787) are not decoded and pass (ledger innate-requirement). Only class resources with a pool are checked.
        /// </summary>
        private static bool MeetsRequirement(Player player, Spell4Entry spell, int index, bool record)
        {
            uint vital = index == 0 ? spell.CasterInnateRequirement0 : spell.CasterInnateRequirement1;
            if (vital == 0u)
                return true;
            uint value = index == 0 ? spell.CasterInnateRequirementValue0 : spell.CasterInnateRequirementValue1;
            // ModifySpell parameters 5/6 change the required amount (rows 536/537: x0 = no amount needed)
            value = (uint)System.MathF.Round(player.ModifySpellValue(spell, index == 0 ? SpellModifications.RequirementValue0 : SpellModifications.RequirementValue1, value));
            uint eval  = index == 0 ? spell.CasterInnateRequirementEval0 : spell.CasterInnateRequirementEval1;

            uint? slot = ClassSlot(player, vital);
            if (slot == null || slot.Value > 6u || player.GetVitalMax(5u + slot.Value) <= 0f)
            {
                if (record)
                    Diagnostics.FailureLedger.Record("innate-requirement", $"not checked: vital {vital} has no pool", $"spell {spell.Id}");
                return true;
            }
            if (eval != 2u)
            {
                if (record)
                    Diagnostics.FailureLedger.Record("innate-requirement", $"not checked: eval {eval} not decoded", $"spell {spell.Id} vital {vital} value {value}");
                return true;
            }
            return player.GetResource(slot.Value) >= value;
        }

        /// <summary>Cast result for the first cost or requirement the player cannot meet, Ok otherwise.</summary>
        public static CastResult Check(Player player, Spell4Entry spell)
        {
            if (!Enabled)
                return CastResult.Ok;
            foreach ((uint vital, uint cost) in new[] { (spell.InnateCostType0, Cost(player, spell, spell.InnateCost0, SpellModifications.InnateCost)), (spell.InnateCostType1, Cost(player, spell, spell.InnateCost1, SpellModifications.InnateCost1)) })
            {
                if (CanPay(player, vital, cost))
                    continue;
                uint slot = ChargedResource(player, vital) ?? 1u;
                Diagnostics.FailureLedger.Record("innate-cost", $"refused, not enough Resource{slot}", $"spell {spell.Id}");
                return (CastResult)((uint)CastResult.CasterVitalCostResource0 + slot);
            }
            if (!MeetsRequirement(player, spell, 0, true) || !MeetsRequirement(player, spell, 1, true))
            {
                Diagnostics.FailureLedger.Record("innate-requirement", "refused, below the required amount", $"spell {spell.Id}");
                return CastResult.CasterVitalCostResource1;
            }
            return CastResult.Ok;
        }

        public static void Charge(Player player, Spell4Entry spell)
        {
            Charge(player, spell.InnateCostType0, Cost(player, spell, spell.InnateCost0, SpellModifications.InnateCost), spell.Id);
            Charge(player, spell.InnateCostType1, Cost(player, spell, spell.InnateCost1, SpellModifications.InnateCost1), spell.Id);
        }

        /// <summary>Innate cost after the caster's ModifySpell rows: parameter 8 for cost 0, 9 for cost 1 (Efficiency: -2%, Neutralize: +5 per use).</summary>
        private static uint Cost(Player player, Spell4Entry spell, uint cost, uint parameter)
        {
            return cost == 0u ? 0u : (uint)System.MathF.Round(player.ModifySpellValue(spell, parameter, cost));
        }

        private static void Charge(Player player, uint vital, uint cost, uint spellId)
        {
            if (vital == 0u || cost == 0u)
                return;

            uint? resource = ChargedResource(player, vital);
            if (resource == null)
            {
                Diagnostics.FailureLedger.Record("innate-cost", $"not charged: vital {vital} (class {player.Class}) has no sourced regeneration", $"spell {spellId}");
                return;
            }

            if (!Enabled)
            {
                Diagnostics.FailureLedger.Record("innate-cost", $"would charge Resource{resource.Value} (vital {vital}), off without NF_INNATE_COSTS=1", $"spell {spellId}");
                return;
            }
            player.AddVital(5u + resource.Value, -cost);
            Diagnostics.FailureLedger.Record("innate-cost", $"charged Resource{resource.Value} (vital {vital}, class {player.Class})");
        }
    }
}
