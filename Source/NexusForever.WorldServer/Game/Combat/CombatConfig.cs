using System;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Combat and death handling is off unless NF_COMBAT=1 is set, so the stock behaviour stays untouched by default.
    /// The numbers here are placeholders that give playable fights, not WildStar's real formulas.
    /// </summary>
    public static class CombatConfig
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("NF_COMBAT") == "1";

        /// <summary>
        /// NF_COMBAT_AGGRO=1 makes hostile creatures attack players and bots that come close, not only when hurt.
        /// </summary>
        public static readonly bool Aggro = Enabled && Environment.GetEnvironmentVariable("NF_COMBAT_AGGRO") == "1";

        /// <summary>Distance at which a hostile creature notices a player.</summary>
        public const float AggroRange = 12f;

        public static uint KillXp(uint creatureLevel)
        {
            return 40u + creatureLevel * 25u;
        }

        /// <summary>Seconds a creature corpse stays before it is removed.</summary>
        public const double CorpseTime = 10d;

        /// <summary>Seconds after removal until a creature respawns.</summary>
        public const double RespawnTime = 45d;

        /// <summary>Seconds after which a dead human player is resurrected automatically.</summary>
        public const double PlayerAutoResurrectTime = 60d;

        /// <summary>Seconds a dead bot waits before it resurrects itself.</summary>
        public const double BotResurrectTime = 10d;

        /// <summary>
        /// Seconds between two "Wake Here" resurrections (1 h for free players, https://wildstar.fandom.com/wiki/Patch_09_29_2015).
        /// There is no Signature account state here, so everybody gets the free value.
        /// </summary>
        public const double WakeHereCooldown = 3600d;

        /// <summary>Fraction of maximum health a resurrected unit comes back with.</summary>
        public const double ResurrectHealth = 0.5d;

        private static readonly Random rng = new();

        public static uint SpellDamage(uint casterLevel)
        {
            return Roll(20d + casterLevel * 12d);
        }

        public static uint MeleeDamage(uint creatureLevel)
        {
            return Roll(6d + creatureLevel * 5d);
        }

        public static uint SpellHeal(uint casterLevel)
        {
            return Roll(30d + casterLevel * 14d);
        }

        public static uint CreatureHealth(uint level)
        {
            return 120u + level * 90u;
        }

        /// <summary>Random spread of a hit, +-15%.</summary>
        public static double DamageRoll()
        {
            return 0.85d + rng.NextDouble() * 0.30d;
        }

        // +-15%, never zero
        private static uint Roll(double value)
        {
            double result = value * (0.85d + rng.NextDouble() * 0.30d);
            return (uint)Math.Max(1d, Math.Round(result));
        }
    }
}
