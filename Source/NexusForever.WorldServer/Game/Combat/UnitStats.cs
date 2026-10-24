using System;
using System.Collections.Concurrent;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Stat values from the game tables. A creature's value for each unit property (indexed by <see cref="Property"/>) is the CreatureLevel row of its
    /// level times the multipliers of its archetype, difficulty and tier. Sniffed spawn health tracks this to within about 15% (spawn values are
    /// current rather than maximum health, so the match cannot be exact).
    /// </summary>
    public static class UnitStats
    {
        private static readonly ConcurrentDictionary<(uint Creature, uint Level), float[]> creatureCache = new();

        /// <summary>
        /// The CreatureLevel row for a level (clamped to the table), the baseline stat values of a unit of that level.
        /// </summary>
        public static float[] LevelRow(uint level)
        {
            CreatureLevelEntry entry = GameTableManager.Instance.CreatureLevel.GetEntry(Math.Clamp(level, 1u, 50u));
            return entry?.UnitPropertyValue ?? Array.Empty<float>();
        }

        /// <summary>
        /// Every unit property of a creature at a level: level row times archetype, difficulty and tier multipliers.
        /// </summary>
        public static float[] Creature(uint creatureId, uint level)
        {
            return creatureCache.GetOrAdd((creatureId, level), key =>
            {
                float[] values = (float[])LevelRow(key.Level).Clone();
                Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(key.Creature);
                if (creature == null)
                    return values;

                Multiply(values, GameTableManager.Instance.Creature2ArcheType.GetEntry(creature.Creature2ArcheTypeId)?.UnitPropertyMultiplier);
                Multiply(values, GameTableManager.Instance.Creature2Difficulty.GetEntry(creature.Creature2DifficultyId)?.UnitPropertyMultiplier);
                Multiply(values, GameTableManager.Instance.Creature2Tier.GetEntry(creature.Creature2TierId)?.UnitPropertyMultiplier);
                return values;
            });
        }

        public static float Get(float[] values, Property property)
        {
            return (uint)property < values.Length ? values[(uint)property] : 0f;
        }

        private static void Multiply(float[] values, float[] multipliers)
        {
            if (multipliers == null)
                return;

            for (int i = 0; i < values.Length && i < multipliers.Length; i++)
                values[i] *= multipliers[i];
        }
    }
}
