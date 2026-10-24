using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.Database.World.Model;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// Normal and veteran (Prime) creature rows of the same instance creature (data/research/veteran-variants.tsv, scripts/research/veteran_pairs.py).
    /// Veteran creatures are separate Creature2 rows ("[STL] e314 - (N) Stormtalon" level 20 tier 1 -> "[STL] e314 - Stormtalon - Veteran" level 50
    /// tier 5); a veteran instance spawns the veteran row, so level and tier stats come from the tables (docs/ASSUMPTIONS.md "Veteran mode", certain).
    /// </summary>
    public static class VeteranVariants
    {
        private static Dictionary<(ushort World, uint Creature, InstanceDifficulty Mode), uint> variants;
        private static readonly object gate = new();

        private static void Load()
        {
            variants = new();
            string file = Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "veteran-variants.tsv");
            if (!File.Exists(file))
                return;

            foreach (string line in File.ReadLines(file))
            {
                string[] f = line.Split('\t');
                if (line.StartsWith('#') || f.Length < 4 || !ushort.TryParse(f[0], out ushort world) || !uint.TryParse(f[1], out uint from)
                    || !uint.TryParse(f[2], out uint to))
                    continue;
                variants[(world, from, f[3] == "veteran" ? InstanceDifficulty.Veteran : InstanceDifficulty.Normal)] = to;
            }
        }

        /// <summary>The creature to spawn instead of <paramref name="creature"/> in an instance of this difficulty, 0 when it stays.</summary>
        public static uint Get(ushort world, uint creature, InstanceDifficulty difficulty)
        {
            lock (gate)
            {
                if (variants == null)
                    Load();
                return variants.TryGetValue((world, creature, difficulty), out uint to) ? to : 0u;
            }
        }

        /// <summary>True when the world has veteran rows for its creatures (the veteran boss spawns are real, not scaled).</summary>
        public static bool HasVeteran(ushort world)
        {
            lock (gate)
            {
                if (variants == null)
                    Load();
                return variants.Keys.Any(k => k.World == world && k.Mode == InstanceDifficulty.Veteran);
            }
        }

        /// <summary>
        /// A copy of the spawn with the other creature row; the level and health stats of the spawn are dropped so the new row's level range
        /// (Creature2 minLevel/maxLevel) and table health apply.
        /// </summary>
        public static EntityModel Swap(EntityModel model, uint creature)
        {
            return new EntityModel
            {
                Id = model.Id, Type = model.Type, Creature = creature, World = model.World, Area = model.Area,
                X = model.X, Y = model.Y, Z = model.Z, Rx = model.Rx, Ry = model.Ry, Rz = model.Rz,
                DisplayInfo = model.DisplayInfo, OutfitInfo = model.OutfitInfo, Faction1 = model.Faction1, Faction2 = model.Faction2,
                QuestChecklistIdx = model.QuestChecklistIdx, ActivePropId = model.ActivePropId, WorldSocketId = model.WorldSocketId,
                EntitySpline = model.EntitySpline, EntityVendor = model.EntityVendor,
                EntityStat = model.EntityStat.Where(s => s.Stat != (byte)Entity.Static.Stat.Level && s.Stat != (byte)Entity.Static.Stat.Health).ToList(),
                EntityVendorCategory = model.EntityVendorCategory, EntityVendorItem = model.EntityVendorItem
            };
        }
    }
}
