using System;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Health regeneration and class resource regeneration/decay over time. The client changes no vital over time except Focus and the shield,
    /// and neither the client nor the game tables hold base rates for these (docs/ASSUMPTIONS.md "Regeneration and decay"). The wiki gives the
    /// direction only: Volatility "when out of combat gradually declines to zero" (Engineer page), Kinetic Energy "gradually decays over time
    /// while both in and out of combat" (Warrior page), Suit Power "regenerates quickly" (Suit Power page). The base rates are therefore
    /// DESIGN VALUES, set with NF_ environment variables (docs/CONFIGURATION.md), and scaled by the spell-row multipliers that exist:
    /// HealthRegenMultiplier (8), ResourceRegenMultiplier0-6 (16-22, per resource slot) and SpellMechanicEnergyRegenOrDecayMultiplier (117).
    /// </summary>
    public static class Regeneration
    {
        private static float Env(string name, float fallback)
            => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0f ? value : fallback;

        /// <summary>Percent of maximum health per second out of combat (design value, default 5).</summary>
        public static readonly float HealthOutOfCombat = Env("NF_REGEN_HEALTH_OOC", 5f);
        /// <summary>Percent of maximum health per second in combat (design value, default 0).</summary>
        public static readonly float HealthInCombat = Env("NF_REGEN_HEALTH_COMBAT", 0f);
        /// <summary>Stalker Suit Power per second, in and out of combat (design value, default 5).</summary>
        public static readonly float SuitPower = Env("NF_REGEN_SUIT_POWER", 5f);
        /// <summary>Warrior Kinetic Energy lost per second in combat (design value, default 10 of 1000).</summary>
        public static readonly float KineticDecayInCombat = Env("NF_DECAY_KINETIC_COMBAT", 10f);
        /// <summary>Warrior Kinetic Energy lost per second out of combat (design value, default 100 of 1000).</summary>
        public static readonly float KineticDecayOutOfCombat = Env("NF_DECAY_KINETIC_OOC", 100f);
        /// <summary>Engineer Volatility lost per second out of combat (design value, default 10 of 100).</summary>
        public static readonly float VolatilityDecayOutOfCombat = Env("NF_DECAY_VOLATILITY_OOC", 10f);

        /// <summary>Called about once per second with the seconds since the last call.</summary>
        public static void Apply(Player player, double seconds)
        {
            if (player.IsDead)
                return;

            float healthRate = player.InCombat ? HealthInCombat : HealthOutOfCombat;
            if (healthRate > 0f && player.Health < player.MaxHealth)
            {
                float amount = player.MaxHealth * healthRate / 100f * (float)seconds * player.GetModifiedProperty(Property.HealthRegenMultiplier, 1f);
                if (amount >= 1f)
                    player.Heal((uint)amount);
            }

            switch (player.Class)
            {
                case Class.Stalker:
                    if (SuitPower > 0f)
                        Change(player, 3u, SuitPower * (float)seconds * player.GetModifiedProperty(Property.ResourceRegenMultiplier3, 1f));
                    break;
                case Class.Warrior:
                {
                    float rate = player.InCombat ? KineticDecayInCombat : KineticDecayOutOfCombat;
                    if (rate > 0f)
                        Change(player, 1u, -rate * (float)seconds * player.GetModifiedProperty(Property.SpellMechanicEnergyRegenOrDecayMultiplier, 1f));
                    break;
                }
                case Class.Engineer:
                    if (!player.InCombat && VolatilityDecayOutOfCombat > 0f)
                        Change(player, 1u, -VolatilityDecayOutOfCombat * (float)seconds);
                    break;
            }
        }

        private static void Change(Player player, uint slot, float amount)
        {
            float current = player.GetResource(slot);
            if (amount == 0f || (amount < 0f && current <= 0f) || (amount > 0f && current >= player.GetVitalMax(5u + slot)))
                return;
            player.AddVital(5u + slot, amount);
        }
    }
}
