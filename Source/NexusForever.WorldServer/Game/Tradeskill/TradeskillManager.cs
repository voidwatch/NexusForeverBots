using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Game.Tradeskill
{
    public enum CraftResult
    {
        Success,
        Critical,
        Failed,
        UnknownSchematic,
        SkillNotLearned,
        TierTooLow,
        MissingMaterials,
        InventoryFull
    }

    /// <summary>
    /// Learned tradeskills, tradeskill xp and crafting for one player. Rules are derived from the game tables
    /// (Tradeskill, TradeskillTier, TradeskillSchematic2); the assumptions are in docs/ASSUMPTIONS.md "Tradeskills".
    /// State is saved in the character database (character_tradeskill, character_tradeskill_schematic); the older JSON files in
    /// /cache/tradeskills are imported once on load.
    /// </summary>
    public partial class TradeskillManager : ISaveCharacter
    {
        /// <summary>
        /// Active skills a character may hold among the ones a trainer offers (no hobby flag 1, no auto-learn flag 16): crafting and gathering
        /// alike. Certain: the client's trainer window lists every Tradeskill row without flags 1/16 and counts the active ones against 2.
        /// </summary>
        public const int MaxTrainerSkills = 2;

        private sealed class State
        {
            public Dictionary<uint, uint> Xp { get; set; } = new();
            public List<uint> Crafted { get; set; } = new();
        }

        private readonly Player player;
        public Player Owner => player;
        private readonly State state = new();
        private readonly HashSet<uint> discovered = new();
        private readonly Dictionary<uint, uint[]> talents = new();
        // talent points earned per skill from tradeskill achievements (TradeskillAchievementReward.TalentPoints), saved in talentPoints
        private readonly Dictionary<uint, uint> earnedPoints = new();
        // known skills that were swapped out at a trainer: xp, talents and points stay (the client keeps them with their tier and prices
        // the relearn from that tier, certain)
        private readonly HashSet<uint> inactive = new();
        // QA bookkeeping, not saved: skills whose xp came partly from crafts with QA-granted materials (bots). Skills that already had xp at
        // load count as granted too, their history is unknown.
        private readonly HashSet<uint> qaMaterialSkills = new();
        // rows that exist in the database, and what changed since the last save
        private readonly HashSet<uint> savedSkills = new();
        private readonly HashSet<uint> savedSchematics = new();
        private readonly HashSet<uint> dirtySkills = new();
        private readonly HashSet<uint> dirtySchematics = new();
        private readonly HashSet<uint> removedSkills = new();

        private static string Folder => System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "tradeskills");
        private string FilePath => System.IO.Path.Combine(Folder, $"{player.CharacterId}.json");

        public TradeskillManager(Player player, CharacterModel model = null)
        {
            this.player = player;
            if (model != null)
            {
                foreach (CharacterTradeskillModel row in model.Tradeskills)
                {
                    state.Xp[row.TradeskillId] = row.Xp;
                    earnedPoints[row.TradeskillId] = row.TalentPoints;
                    savedSkills.Add(row.TradeskillId);
                    if (row.Active == 0)
                        inactive.Add(row.TradeskillId);
                    if (row.Xp > 0u)
                        qaMaterialSkills.Add(row.TradeskillId);
                    if (!string.IsNullOrEmpty(row.Talents))
                        talents[row.TradeskillId] = row.Talents.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => uint.TryParse(t, out uint v) ? v : 0u).ToArray();
                }
                foreach (CharacterTradeskillSchematicModel row in model.TradeskillSchematics)
                {
                    savedSchematics.Add(row.SchematicId);
                    if (row.Crafted != 0 && !state.Crafted.Contains(row.SchematicId))
                        state.Crafted.Add(row.SchematicId);
                    if (row.Discovered != 0)
                        discovered.Add(row.SchematicId);
                }
            }

            if (savedSkills.Count == 0)
                ImportJson();
            EnforceLimit();
        }

        /// <summary>
        /// Characters saved under the older rule (two crafting skills plus any gathering skills) can hold more than <see cref="MaxTrainerSkills"/>
        /// active trainer skills: the extra ones are swapped out (kept with their xp), gathering skills first, then the lowest xp.
        /// </summary>
        private void EnforceLimit()
        {
            List<uint> limited = LearnedSkills.Where(id => CountsAgainstLimit(id)).ToList();
            foreach (uint skill in limited
                .OrderBy(id => (GameTableManager.Instance.Tradeskill.GetEntry(id).Flags & 2u) != 0u ? 0 : 1)
                .ThenBy(id => GetXp(id))
                .Take(Math.Max(0, limited.Count - MaxTrainerSkills)))
            {
                inactive.Add(skill);
                MarkSkill(skill);
                FailureLedger.Record("tradeskill-over-limit-swapped-out", $"skill {skill}", $"xp {GetXp(skill)} kept");
            }
        }

        /// <summary>One-time import of the JSON state written before the database tables existed.</summary>
        private void ImportJson()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                State old = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
                if (old == null)
                    return;

                foreach ((uint skill, uint xp) in old.Xp)
                {
                    state.Xp[skill] = xp;
                    dirtySkills.Add(skill);
                }
                foreach (uint schematic in old.Crafted.Distinct())
                {
                    state.Crafted.Add(schematic);
                    dirtySchematics.Add(schematic);
                }
                FailureLedger.Record("tradeskill-json-imported", $"{old.Xp.Count} skills, {old.Crafted.Count} schematics");
            }
            catch (Exception ex)
            {
                FailureLedger.Record("tradeskill-load-failed", ex.GetType().Name, ex.Message);
            }
        }

        /// <summary>Marks the state changed; written by the next character save.</summary>
        private void MarkSkill(uint tradeskillId) => dirtySkills.Add(tradeskillId);

        private void MarkSchematic(uint schematicId) => dirtySchematics.Add(schematicId);

        public void Save(CharacterContext context)
        {
            foreach (uint skill in removedSkills)
            {
                if (!savedSkills.Remove(skill))
                    continue;
                var row = new CharacterTradeskillModel { Id = player.CharacterId, TradeskillId = skill };
                context.Attach(row);
                context.Remove(row);
            }
            removedSkills.Clear();

            foreach (uint skill in dirtySkills)
            {
                if (!state.Xp.TryGetValue(skill, out uint xp))
                    continue;

                var row = new CharacterTradeskillModel
                {
                    Id           = player.CharacterId,
                    TradeskillId = skill,
                    Xp           = xp,
                    Active       = (byte)(inactive.Contains(skill) ? 0 : 1),
                    TalentPoints = earnedPoints.TryGetValue(skill, out uint earned) ? earned : 0u,
                    Talents      = talents.TryGetValue(skill, out uint[] picked) ? string.Join(",", picked) : ""
                };
                if (savedSkills.Contains(skill))
                    context.Update(row);
                else
                {
                    context.Add(row);
                    savedSkills.Add(skill);
                }
            }
            dirtySkills.Clear();

            foreach (uint schematic in dirtySchematics)
            {
                var row = new CharacterTradeskillSchematicModel
                {
                    Id          = player.CharacterId,
                    SchematicId = schematic,
                    Crafted     = (byte)(state.Crafted.Contains(schematic) ? 1 : 0),
                    Discovered  = (byte)(discovered.Contains(schematic) ? 1 : 0)
                };
                if (savedSchematics.Contains(schematic))
                    context.Update(row);
                else
                {
                    context.Add(row);
                    savedSchematics.Add(schematic);
                }
            }
            dirtySchematics.Clear();
        }

        private TradeskillInfo Info(uint tradeskillId)
        {
            var info = new TradeskillInfo
            {
                TradeskillId = tradeskillId,
                Xp           = GetXp(tradeskillId),
                IsActive     = HasLearned(tradeskillId) ? 1u : 0u,
                TalentPoints = TalentPoints(tradeskillId)
            };
            if (talents.TryGetValue(tradeskillId, out uint[] picked))
                Array.Copy(picked, info.TalentTierIds, Math.Min(picked.Length, info.TalentTierIds.Length));
            return info;
        }

        /// <summary>ServerProfessionUpdate 0x0860: one skill's xp, active flag and talents (client reader).</summary>
        public void SendProfessionUpdate(uint tradeskillId)
        {
            player.Session?.EnqueueMessageEncrypted(new ServerProfessionUpdate { Tradeskill = Info(tradeskillId) });
        }

        /// <summary>ServerProfessionsLoad 0x0856 at login: every skill, learned and discovered schematics (client reader).</summary>
        public void SendProfessionsLoad()
        {
            var message = new ServerProfessionsLoad();
            EnsureAutoLearned();
            foreach (uint skill in state.Xp.Keys)
                message.Tradeskills.Add(Info(skill));
            foreach (uint schematic in discovered.Union(state.Crafted).Union(TierSchematics()).Distinct())
            {
                message.LearnedSchematics.Add(schematic);
                message.LearnedSchematicDiscovered.Add(discovered.Contains(schematic));
            }
            player.Session?.EnqueueMessageEncrypted(message);
        }

        /// <summary>
        /// Coordinate craft in progress (Tradeskill.Flags bit 8): schematic, grid position moved by additives, additives used, catalysts.
        /// The start catalyst (ClientCraftingCraftItem) stays on the craft: its additive effects change every additive and its global effects
        /// (11-16) apply at completion, like the client's GetAdditiveInfo / GetGlobalCatalystInfo.
        /// </summary>
        public sealed class CraftSession
        {
            public uint SchematicId;
            public uint CatalystItemId;
            public float X, Y;
            public int Additives;
            public int Catalysts;
            public bool Circuit;
            public readonly List<TradeskillCatalystEntry> Applied = new();
        }

        public CraftSession Current { get; private set; }

        /// <summary>
        /// A crafting station within 20 m that serves the skill (Creature2.TradeSkillIdStation = the skill, or 0xFFFFFFFF for every skill).
        /// The client sends the station's unit id; 0 or an unknown id falls back to any station near the player.
        /// </summary>
        public bool AtStation(uint tradeskillId, uint stationUnitId)
        {
            if (player.Map == null)
                return false;

            player.Map.Search(player.Position, 20f, new Map.Search.SearchCheckRange(player.Position, 20f), out List<GridEntity> near);
            // stations are spawned as Simple (type 10) or NonPlayer, so any creature entity counts
            return near.OfType<WorldEntity>().Where(n => n is not Player).Any(n => (stationUnitId == 0u || n.Guid == stationUnitId || near.All(x => x.Guid != stationUnitId))
                && GameTableManager.Instance.Creature2.GetEntry(n.CreatureId) is { } c && (c.TradeSkillIdStation == tradeskillId || c.TradeSkillIdStation == uint.MaxValue));
        }

        /// <summary>
        /// A catalyst item fits a craft when its TradeskillCatalyst row has the schematic's skill and exactly its tier (certain: the client's
        /// GetAvailableCatalysts and AddAdditive compare both) and the item is in the bags. Returns the row or null with the reason.
        /// </summary>
        private TradeskillCatalystEntry CatalystFor(TradeskillSchematic2Entry schematic, uint catalystItemId, out string refusal)
        {
            Item2Entry item = GameTableManager.Instance.Item.GetEntry(catalystItemId);
            TradeskillCatalystEntry row = item == null ? null : GameTableManager.Instance.TradeskillCatalyst.GetEntry(item.TradeskillCatalystId);
            refusal = row == null ? "not a catalyst" : row.TradeSkillId != schematic.TradeSkillId ? $"catalyst of skill {row.TradeSkillId}"
                : row.Tier != schematic.Tier ? $"catalyst tier {row.Tier} on a tier {schematic.Tier} schematic"
                : player.Inventory.GetItemCount(catalystItemId) == 0u ? "not in the bag" : null;
            return refusal == null ? row : null;
        }

        /// <summary>
        /// ClientCraftingCraftItem 0x0851: starts a coordinate craft at the grid origin with an optional catalyst. The catalyst is used up when
        /// the craft starts (likely: the client lists only carried catalysts and counts them per craft).
        /// </summary>
        public CraftResult StartCraft(uint schematicId, uint catalystItemId)
        {
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematicId);
            if (schematic == null || !KnowsSchematic(schematic))
                return CraftResult.UnknownSchematic;
            if (schematic.Tier > GetTier(schematic.TradeSkillId))
                return CraftResult.TierTooLow;
            if (!HasMaterials(schematic))
                return CraftResult.MissingMaterials;

            var session = new CraftSession { SchematicId = schematicId, CatalystItemId = catalystItemId };
            if (catalystItemId != 0u)
            {
                TradeskillCatalystEntry row = CatalystFor(schematic, catalystItemId, out string refusal);
                if (row == null)
                {
                    FailureLedger.Record("tradeskill-catalyst-refused", refusal, $"item {catalystItemId} schematic {schematicId}");
                    return CraftResult.MissingMaterials;
                }
                player.Inventory.ItemDelete(catalystItemId, 1u);
                session.Applied.Add(row);
                session.Catalysts++;
                FailureLedger.Record("tradeskill-catalyst-used", "craft start", $"catalyst {row.Id} ({string.Join(",", TradeskillCatalysts.Effects(row).Select(e => $"{e.Kind}:{e.Value:0.##}"))}) schematic {schematicId}");
            }

            Current = session;
            return CraftResult.Success;
        }

        /// <summary>Additive slots of a schematic: its own MaxAdditives, else its tier's, else the skill's (client AddAdditive), plus bonus 26.</summary>
        public int MaxAdditives(TradeskillSchematic2Entry schematic)
        {
            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(schematic.TradeSkillId);
            TradeskillTierEntry tier = GameTableManager.Instance.TradeskillTier.Entries.FirstOrDefault(t => t != null && t.TradeSkillId == schematic.TradeSkillId && t.Tier == schematic.Tier);
            uint slots = schematic.MaxAdditives != 0u ? schematic.MaxAdditives : tier?.MaxAdditives is > 0u ? tier.MaxAdditives : skill?.MaxAdditives ?? 0u;
            return (int)slots + (int)MathF.Round(BonusSum(TradeskillBonusType.ExtraAdditiveSlot, schematic.TradeSkillId));
        }

        /// <summary>
        /// Highest additive tier a schematic accepts: its own tier; hobby skills (Tradeskill.Flags 1, Cooking) 1 + bonus 27 ("access to additional
        /// ingredients") instead (certain: client AddAdditive / GetAvailableAdditives).
        /// </summary>
        public uint MaxAdditiveTier(TradeskillSchematic2Entry schematic)
        {
            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(schematic.TradeSkillId);
            if (skill != null && (skill.Flags & 1u) != 0u)
                return 1u + (uint)MathF.Round(BonusSum(TradeskillBonusType.ExtraIngredients, schematic.TradeSkillId));
            return schematic.Tier;
        }

        /// <summary>
        /// Where an additive lands relative to the current point, before the random spread: TradeskillAdditive vector x bonus 15, landing radius
        /// x bonus 16 (both with the client's direction filter), then the craft's catalysts and the additive's own catalyst (client GetAdditiveInfo).
        /// </summary>
        public TradeskillCatalysts.AdditiveShape AdditiveShapeFor(TradeskillAdditiveEntry additive, IEnumerable<TradeskillCatalystEntry> catalysts)
        {
            float power  = BonusFactor(TradeskillBonusType.AdditivePower, additive.TradeSkillId, 0u, additive.VectorX, additive.VectorY);
            float spread = BonusFactor(TradeskillBonusType.AdditiveSpread, additive.TradeSkillId, 0u, additive.VectorX, additive.VectorY);
            var shape = new TradeskillCatalysts.AdditiveShape { X = additive.VectorX * power, Y = additive.VectorY * power, RadiusMax = additive.Radius * spread };
            foreach (TradeskillCatalystEntry row in catalysts)
                TradeskillCatalysts.Apply(row, shape);
            return shape;
        }

        /// <summary>
        /// ClientCraftingAdditive 0x084A (additive item, optional catalyst). Rules from the client's AddAdditive and crafting window (certain):
        /// the additive belongs to the schematic's skill, its tier is at most <see cref="MaxAdditiveTier"/>, at most <see cref="MaxAdditives"/> per craft;
        /// additives are bought from the crafting window's additive store for the item's buy price x bonus 14, they are not taken from the bags
        /// (every one of the 127 additive items has no buy price in Item2, so they cost nothing here: ledger tradeskill-additive-bought). A catalyst
        /// sent with the additive must change additives (effects 1-9, 14), fit the schematic and is used up (likely). The additive lands at its
        /// vector plus a random point in the ring sector RadiusMin..RadiusMax, StartAngle..StartAngle+MaxRotation (the shape is the client's
        /// GetAdditiveInfo; uniform over the area and the angle measured from the grid's +X axis: guess).
        /// </summary>
        public bool AddAdditive(uint additiveItemId, uint catalystItemId = 0u)
        {
            if (Current == null)
                return false;

            Item2Entry item = GameTableManager.Instance.Item.GetEntry(additiveItemId);
            TradeskillAdditiveEntry additive = item == null ? null : GameTableManager.Instance.TradeskillAdditive.GetEntry(item.TradeskillAdditiveId);
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(Current.SchematicId);
            ulong price = 0ul;
            CurrencyType currency = CurrencyType.None;
            if (item != null && item.CurrencyTypeId.Length > 0 && item.CurrencyTypeId[0] != 0u)
            {
                currency = (CurrencyType)item.CurrencyTypeId[0];
                price = (ulong)(item.CurrencyAmount[0] * (additive == null ? 1f : BonusFactor(TradeskillBonusType.AdditiveCost, additive.TradeSkillId, 0u, additive.VectorX, additive.VectorY)));
            }
            string refusal = additive == null ? "not an additive" : schematic == null ? "no craft schematic"
                : additive.TradeSkillId != schematic.TradeSkillId ? $"additive of skill {additive.TradeSkillId}"
                : additive.Tier > MaxAdditiveTier(schematic) ? $"additive tier {additive.Tier} above {MaxAdditiveTier(schematic)}"
                : Current.Additives >= MaxAdditives(schematic) ? $"limit {MaxAdditives(schematic)} reached"
                : price > 0ul && !player.CurrencyManager.CanAfford(currency, price) ? $"costs {price} of currency {(uint)currency}" : null;

            TradeskillCatalystEntry catalyst = null;
            if (refusal == null && catalystItemId != 0u)
            {
                catalyst = CatalystFor(schematic, catalystItemId, out refusal);
                if (catalyst != null && !TradeskillCatalysts.ChangesAdditive(catalyst))
                    refusal = "catalyst does not change additives";
                else if (catalyst != null && Current.Additives + 1 > 4)
                    refusal = "catalyst on an additive after the fourth";
                if (refusal != null)
                    catalyst = null;
            }
            if (refusal != null)
            {
                FailureLedger.Record("tradeskill-additive-refused", refusal, $"item {additiveItemId} catalyst {catalystItemId} schematic {Current.SchematicId}");
                return false;
            }

            if (price > 0ul)
                player.CurrencyManager.CurrencySubtractAmount(currency, price);
            FailureLedger.Record("tradeskill-additive-bought", price > 0ul ? "priced" : "no buy price in Item2", $"item {additiveItemId} price {price}");

            var catalysts = new List<TradeskillCatalystEntry>(Current.Applied.Where(TradeskillCatalysts.ChangesAdditive));
            if (catalyst != null)
            {
                player.Inventory.ItemDelete(catalystItemId, 1u);
                catalysts.Add(catalyst);
                Current.Catalysts++;
                FailureLedger.Record("tradeskill-catalyst-used", "additive", $"catalyst {catalyst.Id} ({string.Join(",", TradeskillCatalysts.Effects(catalyst).Select(e => $"{e.Kind}:{e.Value:0.##}"))}) schematic {Current.SchematicId}");
            }

            TradeskillCatalysts.AdditiveShape shape = AdditiveShapeFor(additive, catalysts);
            float rMin = MathF.Max(0f, MathF.Min(shape.RadiusMin, shape.RadiusMax)), rMax = MathF.Max(shape.RadiusMin, shape.RadiusMax);
            float r = MathF.Sqrt((float)random.NextDouble() * (rMax * rMax - rMin * rMin) + rMin * rMin);
            float a = shape.StartAngle + (float)random.NextDouble() * shape.MaxRotation;
            Current.X += shape.X + r * MathF.Cos(a);
            Current.Y += shape.Y + r * MathF.Sin(a);
            Current.Additives++;
            return true;
        }

        /// <summary>
        /// ClientCraftingSimpleCraft 0x0850 / ClientCraftingComplexCraft 0x084F: finishes the coordinate craft. Global catalysts act first: 15/16
        /// move the point to the nearest crafting / discovery target (variant centre; likely), 12/13 scale the craft / discovery target radii and
        /// 11 the critical radius (certain: the client names them so). Talent bonuses 17 / 20 scale the same radii. The variant (child schematic,
        /// TradeskillSchematic2IdParent) whose circle holds the point is made, else the base schematic.
        /// </summary>
        public CraftResult CompleteCraft(uint schematicId)
        {
            CraftSession session = Current;
            if (session?.Circuit == true)
                return CraftResult.UnknownSchematic;
            Current = null;
            if (session == null || (schematicId != 0u && schematicId != session.SchematicId))
                return CraftResult.UnknownSchematic;

            TradeskillSchematic2Entry parent = GameTableManager.Instance.TradeskillSchematic2.GetEntry(session.SchematicId);
            if (parent == null)
                return CraftResult.UnknownSchematic;

            List<TradeskillSchematic2Entry> variants = GameTableManager.Instance.TradeskillSchematic2.Entries
                .Where(e => e != null && e.TradeskillSchematic2IdParent == session.SchematicId && e.Id != session.SchematicId && e.Radius > 0f).ToList();
            bool IsDiscovery(TradeskillSchematic2Entry e) => e.DiscoverableQuadrant != 0u && !discovered.Contains(e.Id) && !state.Crafted.Contains(e.Id);
            // a variant the character does not know and cannot discover is no target (its circle is not on the client's grid)
            variants.RemoveAll(e => !IsDiscovery(e) && !KnowsSchematic(e));
            uint OutputType(TradeskillSchematic2Entry e) => GameTableManager.Instance.Item.GetEntry(e.Item2IdOutput)?.Item2TypeId ?? 0u;

            float craftCatalyst     = TradeskillCatalysts.Multiplier(session.Applied, TradeskillCatalysts.CraftRadius);
            float discoveryCatalyst = TradeskillCatalysts.Multiplier(session.Applied, TradeskillCatalysts.DiscoveryRadius);
            float critCatalyst      = TradeskillCatalysts.Multiplier(session.Applied, TradeskillCatalysts.CritRadius);
            float discoveryBonus    = BonusFactor(TradeskillBonusType.DiscoveryRadius, parent.TradeSkillId);
            float RadiusOf(TradeskillSchematic2Entry e) => e.Radius * (IsDiscovery(e) ? discoveryCatalyst * discoveryBonus
                : craftCatalyst * BonusFactor(TradeskillBonusType.CraftTargetRadius, e.TradeSkillId, OutputType(e)));

            foreach ((uint kind, bool discovery) in new[] { (TradeskillCatalysts.AttractCraftTarget, false), (TradeskillCatalysts.AttractDiscovery, true) })
            {
                if (!TradeskillCatalysts.Has(session.Applied, kind))
                    continue;
                TradeskillSchematic2Entry nearest = variants.Where(e => IsDiscovery(e) == discovery)
                    .OrderBy(e => (e.VectorX - session.X) * (e.VectorX - session.X) + (e.VectorY - session.Y) * (e.VectorY - session.Y)).FirstOrDefault();
                if (nearest != null)
                {
                    FailureLedger.Record("tradeskill-catalyst-applied", discovery ? "attract discovery target" : "attract craft target", $"schematic {nearest.Id} from ({session.X:0.##},{session.Y:0.##})");
                    session.X = nearest.VectorX;
                    session.Y = nearest.VectorY;
                }
            }

            if (craftCatalyst != 1f || discoveryCatalyst != 1f || critCatalyst != 1f)
                FailureLedger.Record("tradeskill-catalyst-applied", "radius multipliers", $"craft x{craftCatalyst:0.##} discovery x{discoveryCatalyst:0.##} crit x{critCatalyst:0.##}");

            TradeskillSchematic2Entry variant = variants
                .Select(e => (Entry: e, Distance: MathF.Sqrt((e.VectorX - session.X) * (e.VectorX - session.X) + (e.VectorY - session.Y) * (e.VectorY - session.Y))))
                .Where(e => e.Distance <= RadiusOf(e.Entry))
                .OrderBy(e => e.Distance) // overlapping circles: the nearest centre (assumption)
                .Select(e => e.Entry)
                .FirstOrDefault();
            if (variant != null)
            {
                if (variant.DiscoverableQuadrant != 0u)
                    LearnSchematic(variant.Id);
                return Craft(variant.Id, session.X, session.Y, session.SchematicId, RadiusOf(variant) / variant.Radius, critCatalyst);
            }
            return Craft(session.SchematicId, session.X, session.Y, 0u, craftCatalyst * BonusFactor(TradeskillBonusType.CraftTargetRadius, parent.TradeSkillId, OutputType(parent)), critCatalyst);
        }

        public void AbandonCraft() => Current = null;

        /// <summary>Circuit board crafting (Tradeskill.Flags 4: Weaponsmith, Armorer, Outfitter, Tailor).</summary>
        public static bool IsCircuit(TradeskillSchematic2Entry schematic) =>
            schematic != null && (GameTableManager.Instance.Tradeskill.GetEntry(schematic.TradeSkillId)?.Flags & 4u) is > 0u;

        /// <summary>A universal schematic needs no power core: tier 0 with schematic flag 4 (certain: the client's GetSchematicInfo bIsUniversal).</summary>
        public static bool IsUniversal(TradeskillSchematic2Entry schematic) => schematic.Tier == 0u && (schematic.Flags & 4u) != 0u;

        /// <summary>
        /// The client's GetAvailablePowerCores test: Item2Category 143 (Power Core), the core's power level at most the output's, and the core's
        /// slot flags 0 or sharing a bit with the output's (certain for the category and the slot test, likely that the compared fields are
        /// PowerLevel: the 35 cores use exactly the 10/12/14 ... 85 power levels of the circuit outputs, every non-universal schematic has a core).
        /// </summary>
        public static bool FitsPowerCore(Item2Entry core, Item2Entry output) =>
            core != null && output != null && core.Item2CategoryId == 143u && core.PowerLevel <= output.PowerLevel
            && (core.EquippedSlotFlags == 0u || (core.EquippedSlotFlags & output.EquippedSlotFlags) != 0u);

        /// <summary>
        /// ClientCraftingCraftItemSimple 0x0852 on a circuit schematic starts the craft (certain: the client's CraftItem sends 0x0852 for every
        /// skill without flag 8, and its CompleteCraft sends 0x084F for circuit skills). The materials are used up at the start: "Once a craft
        /// begins, the players materials are consumed. However they can still abandon their craft at any time. They still lose the materials but
        /// wont have to use up a Power Core." (wildstaronline-archive.fandom.com/wiki/Circuit_Board_Crafting).
        /// </summary>
        public CraftResult StartCircuitCraft(uint schematicId)
        {
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematicId);
            if (schematic == null || !KnowsSchematic(schematic))
                return CraftResult.UnknownSchematic;
            if (schematic.Tier > GetTier(schematic.TradeSkillId))
                return CraftResult.TierTooLow;
            if (!HasMaterials(schematic))
                return CraftResult.MissingMaterials;

            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            uint outputType  = GameTableManager.Instance.Item.GetEntry(schematic.Item2IdOutput)?.Item2TypeId ?? 0u;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != 0u)
                    player.Inventory.ItemDelete(materials[i], MaterialCost(schematic.TradeSkillId, outputType, materials[i], costs[i]));

            Current = new CraftSession { SchematicId = schematicId, Circuit = true };
            return CraftResult.Success;
        }

        /// <summary>
        /// ClientCraftingComplexCraft 0x084F: finishes a circuit craft with the chosen power core (used up; none for universal schematics). The
        /// result's item level is the core's power level when it is below the output's ("If you use a level 50 schematic but only a level 15
        /// Power Core, the completed item will be level 15", same wiki page): items carry no per-instance level in the character database here,
        /// so that case is counted (tradeskill-powercore-level-not-stored) and the output keeps its own level. Socket charges, the AP/SP split
        /// and the overcharge failure chance are not modelled (the craft succeeds; ledger tradeskill-circuit-charges-ignored).
        /// </summary>
        public CraftResult CompleteCircuitCraft(uint schematicId, uint powerCoreItemId, string charges)
        {
            CraftSession session = Current;
            if (session == null || !session.Circuit || (schematicId != 0u && schematicId != session.SchematicId))
                return CraftResult.UnknownSchematic;

            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(session.SchematicId);
            Item2Entry output = GameTableManager.Instance.Item.GetEntry(schematic?.Item2IdOutput ?? 0u);
            if (schematic == null)
                return CraftResult.UnknownSchematic;

            if (!IsUniversal(schematic))
            {
                Item2Entry core = GameTableManager.Instance.Item.GetEntry(powerCoreItemId);
                string refusal = core == null ? "no power core" : !FitsPowerCore(core, output) ? $"core {powerCoreItemId} does not fit output {output?.Id}"
                    : player.Inventory.GetItemCount(powerCoreItemId) == 0u ? "core not in the bag" : null;
                if (refusal != null)
                {
                    // the session stays: the client can pick another core
                    FailureLedger.Record("tradeskill-powercore-refused", refusal, $"schematic {schematic.Id}");
                    return CraftResult.MissingMaterials;
                }
                player.Inventory.ItemDelete(powerCoreItemId, 1u);
                FailureLedger.Record("tradeskill-powercore-used", $"quality {core.ItemQualityId}", $"core {core.Id} level {core.PowerLevel} output {output?.Id} level {output?.PowerLevel}");
                if (output != null && core.PowerLevel < output.PowerLevel)
                    FailureLedger.Record("tradeskill-powercore-level-not-stored", $"core level {core.PowerLevel} < output {output.PowerLevel}", $"schematic {schematic.Id}");
            }
            if (!string.IsNullOrEmpty(charges))
                FailureLedger.Record("tradeskill-circuit-charges-ignored", $"schematic {schematic.Id}", charges);

            Current = null;
            return Craft(schematic.Id, materialsTaken: true);
        }

        private bool HasMaterials(TradeskillSchematic2Entry schematic, uint times = 1u)
        {
            uint[] materials = { schematic.Item2IdMaterial00, schematic.Item2IdMaterial01, schematic.Item2IdMaterial02, schematic.Item2IdMaterial03, schematic.Item2IdMaterial04 };
            uint[] costs     = { schematic.MaterialCost00, schematic.MaterialCost01, schematic.MaterialCost02, schematic.MaterialCost03, schematic.MaterialCost04 };
            uint outputType  = GameTableManager.Instance.Item.GetEntry(schematic.Item2IdOutput)?.Item2TypeId ?? 0u;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != 0u && player.Inventory.GetItemCount(materials[i]) < MaterialCost(schematic.TradeSkillId, outputType, materials[i], costs[i]) * times)
                    return false;
            return true;
        }

        /// <summary>
        /// Material count of one craft: the schematic's cost (at least 1) minus bonus 6 for materials of the bonus's Item2Type ("reduces ...
        /// requirements by 1/2/4"), never below 1 (likely: "by up to 2").
        /// </summary>
        public uint MaterialCost(uint tradeskillId, uint outputType, uint material, uint cost)
        {
            cost = Math.Max(cost, 1u);
            if (material == 0u)
                return cost;
            uint materialType = GameTableManager.Instance.Item.GetEntry(material)?.Item2TypeId ?? 0u;
            uint less = (uint)MathF.Round(BonusSum(TradeskillBonusType.MaterialReduction, tradeskillId, outputType, materialType));
            if (less == 0u)
                return cost;
            uint reduced = cost > less ? cost - less : 1u;
            if (reduced != cost)
                RecordBonus(TradeskillBonusType.MaterialReduction, $"item {material}: {cost} -> {reduced}");
            return reduced;
        }

        /// <summary>Kept for callers of the JSON era: the state is saved with the character now.</summary>
        public void Save()
        {
        }

        /// <summary>Active skills.</summary>
        public IEnumerable<uint> LearnedSkills => state.Xp.Keys.Where(k => !inactive.Contains(k));

        /// <summary>Active and swapped-out skills.</summary>
        public IEnumerable<uint> KnownSkills => state.Xp.Keys;

        /// <summary>The skill is learned and active (a swapped-out skill keeps its xp but cannot be used).</summary>
        public bool HasLearned(uint tradeskillId) => state.Xp.ContainsKey(tradeskillId) && !inactive.Contains(tradeskillId);

        public bool IsInactive(uint tradeskillId) => state.Xp.ContainsKey(tradeskillId) && inactive.Contains(tradeskillId);

        public uint GetXp(uint tradeskillId) => state.Xp.TryGetValue(tradeskillId, out uint xp) ? xp : 0u;

        /// <summary>Weaponsmith, Armorer, Outfitter, Tailor (flag 4), Technologist, Architect (flag 8, not the Cooking hobby).</summary>
        public static bool IsCraftingSkill(TradeskillEntry skill) => skill != null && (skill.Flags & 0xCu) != 0u && (skill.Flags & 1u) == 0u;

        /// <summary>Skills a trainer offers and that count against <see cref="MaxTrainerSkills"/> (no hobby flag 1, no auto-learn flag 16).</summary>
        public static bool CountsAgainstLimit(TradeskillEntry skill) => skill != null && (skill.Flags & 0x11u) == 0u;

        public static bool CountsAgainstLimit(uint tradeskillId) => CountsAgainstLimit(GameTableManager.Instance.Tradeskill.GetEntry(tradeskillId));

        /// <summary>Active skills that count against the trainer limit.</summary>
        public int LimitedSkillCount => LearnedSkills.Count(id => CountsAgainstLimit(id));

        /// <summary>Tier of the active skill, 0 when it is not learned or swapped out (the client's prerequisite 19 reads 0 for both).</summary>
        public uint GetTier(uint tradeskillId) => HasLearned(tradeskillId) ? TierOfXp(tradeskillId, GetXp(tradeskillId)) : 0u;

        /// <summary>
        /// Number of the skill's TradeskillTier rows whose RequiredXp is reached (certain: the client's profession update counts the rows of
        /// the skill's tier list in order until one needs more xp); at least 1.
        /// </summary>
        private static uint TierOfXp(uint tradeskillId, uint xp)
        {
            uint tier = 1u;
            foreach (TradeskillTierEntry entry in GameTableManager.Instance.TradeskillTier.Entries.Where(r => r != null && r.TradeSkillId == tradeskillId))
                if (xp >= entry.RequiredXp && entry.Tier > tier)
                    tier = entry.Tier;
            return tier;
        }

        /// <summary>Credits a trainer asks to make a swapped-out skill active again: TradeskillTier.RelearnCost of its tier (0 for tier 1).</summary>
        public ulong RelearnCost(uint tradeskillId)
        {
            if (!IsInactive(tradeskillId))
                return 0ul;
            uint tier = TierOfXp(tradeskillId, GetXp(tradeskillId));
            return GameTableManager.Instance.TradeskillTier.Entries.FirstOrDefault(t => t != null && t.TradeSkillId == tradeskillId && t.Tier == tier)?.RelearnCost ?? 0ul;
        }

        /// <summary>QA harness: the skill's xp now depends on materials the harness granted.</summary>
        public void MarkQaMaterials(uint tradeskillId) => qaMaterialSkills.Add(tradeskillId);

        /// <summary>QA harness: the skill's xp (and so its tier) depends on granted materials or on history from before the server start.</summary>
        public bool UsedQaMaterials(uint tradeskillId) => qaMaterialSkills.Contains(tradeskillId);

        /// <summary>Why <see cref="Learn"/> refused, for callers that report it.</summary>
        public string LastRefusal { get; private set; }

        /// <summary>
        /// Learns a tradeskill at a trainer (ClientTradeskillLearn 0x0857: skill, skill to swap out; the client's trainer window sends the
        /// second id from its "swap with" buttons). Rules (client trainer and CraftingLib, certain unless noted):
        /// - a skill that is already active is refused (the client sends nothing then);
        /// - at most <see cref="MaxTrainerSkills"/> active skills without flags 1/16, gathering included; the third needs a swap;
        /// - the swapped-out skill stays known, inactive, with xp, talents and points;
        /// - a known inactive skill comes back with its xp for TradeskillTier.RelearnCost credits of its tier (the client shows that price as
        ///   monRelearnCost; that the server takes it is likely). The relearn cooldown (client GetRelearnCooldown, a server timestamp) has no
        ///   duration in any table: none is applied (assumption, docs/ASSUMPTIONS.md).
        /// Hobby and auto-learn skills are not limited.
        /// </summary>
        public bool Learn(uint tradeskillId, uint dropTradeskillId = 0u)
        {
            LastRefusal = null;
            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(tradeskillId);
            if (skill == null)
                return Refuse("unknown skill");
            if (HasLearned(tradeskillId))
                return Refuse("already active");

            bool swap = dropTradeskillId != 0u && dropTradeskillId != tradeskillId && HasLearned(dropTradeskillId);
            if (CountsAgainstLimit(skill) && LimitedSkillCount - (swap && CountsAgainstLimit(dropTradeskillId) ? 1 : 0) >= MaxTrainerSkills)
                return Refuse("limit of two active trainer skills");

            ulong cost = RelearnCost(tradeskillId);
            if (cost > 0ul && !player.CurrencyManager.CanAfford(CurrencyType.Credits, cost))
                return Refuse($"relearn costs {cost} credits");

            if (swap)
            {
                inactive.Add(dropTradeskillId);
                MarkSkill(dropTradeskillId);
                SendProfessionUpdate(dropTradeskillId);
                FailureLedger.Record("tradeskill-swapped-out", $"skill {dropTradeskillId}", $"for {tradeskillId}, xp {GetXp(dropTradeskillId)} kept");
            }

            if (cost > 0ul)
            {
                player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, cost);
                FailureLedger.Record("tradeskill-relearned", $"skill {tradeskillId}", $"{cost} credits, xp {GetXp(tradeskillId)}");
            }

            bool relearn = inactive.Remove(tradeskillId);
            if (!relearn)
                state.Xp[tradeskillId] = 0u;
            removedSkills.Remove(tradeskillId);
            MarkSkill(tradeskillId);
            player.QuestManager.ObjectiveUpdate(QuestObjectiveType.LearnTradeskill, 0u, 1u);
            SendProfessionUpdate(tradeskillId);
            return true;
        }

        private bool Refuse(string reason)
        {
            LastRefusal = reason;
            return false;
        }

        /// <summary>Auto-learn skills (Tradeskill.Flags 16: Farmer, Runecrafting; the client's bIsAutoLearn) are known without a trainer (likely).</summary>
        private void EnsureAutoLearned()
        {
            foreach (TradeskillEntry skill in GameTableManager.Instance.Tradeskill.Entries.Where(t => t != null && (t.Flags & 0x10u) != 0u))
            {
                if (state.Xp.ContainsKey(skill.Id))
                    continue;
                state.Xp[skill.Id] = 0u;
                MarkSkill(skill.Id);
                FailureLedger.Record("tradeskill-auto-learned", $"skill {skill.Id}");
            }
        }

        /// <summary>Schematics with flag 1 up to the active tier of each skill: the client adds them to its known list on a tier change (certain).</summary>
        private IEnumerable<uint> TierSchematics()
        {
            foreach (uint skill in LearnedSkills.ToList())
            {
                uint tier = GetTier(skill);
                foreach (TradeskillSchematic2Entry schematic in GameTableManager.Instance.TradeskillSchematic2.Entries)
                    if (schematic != null && schematic.TradeSkillId == skill && (schematic.Flags & 1u) != 0u && schematic.Tier <= tier)
                        yield return schematic.Id;
            }
        }

        /// <summary>Adds tradeskill xp; tier changes are sent to the client and counted.</summary>
        public void AddXp(uint tradeskillId, uint xp)
        {
            if (!state.Xp.ContainsKey(tradeskillId) || xp == 0u)
                return;

            uint before = GetTier(tradeskillId);
            state.Xp[tradeskillId] += xp;
            MarkSkill(tradeskillId);
            uint after = GetTier(tradeskillId);
            if (after > before)
            {
                FailureLedger.Record("tradeskill-tier-up", $"skill {tradeskillId} tier {after}", $"xp {state.Xp[tradeskillId]}");
                foreach (TradeskillTierEntry reached in GameTableManager.Instance.TradeskillTier.Entries.Where(t => t != null && t.TradeSkillId == tradeskillId && t.Tier > before && t.Tier <= after))
                    player.AchievementManager.CheckAchievements(player, Achievement.Static.AchievementType.TradeskillTierReached, reached.Id);
            }
            SendProfessionUpdate(tradeskillId);
        }

        /// <summary>
        /// Talent points of a skill: the points its completed tradeskill achievements gave (TradeskillAchievementReward.TalentPoints, 206 points
        /// over 761 rows, certain). Picks do not spend them (certain, client talent window and PickTradeskillTalent): a talent tier opens when
        /// the points reach its PointsToUnlock (3, 6, 9 ... 30 for Weaponsmith; 1-5 for Architect) and holds one pick.
        /// </summary>
        public uint TalentPoints(uint tradeskillId) => earnedPoints.TryGetValue(tradeskillId, out uint e) ? e : 0u;

        /// <summary>The tradeskill an achievement belongs to: its category or a parent category is the skill's AchievementCategoryId.</summary>
        public static uint SkillOfAchievement(AchievementEntry achievement)
        {
            uint category = achievement?.AchievementCategoryId ?? 0u;
            for (int depth = 0; category != 0u && depth < 6; depth++)
            {
                TradeskillEntry skill = GameTableManager.Instance.Tradeskill.Entries.FirstOrDefault(t => t != null && t.AchievementCategoryId == category);
                if (skill != null)
                    return skill.Id;
                category = GameTableManager.Instance.AchievementCategory.GetEntry(category)?.AchievementCategoryIdParent ?? 0u;
            }
            return 0u;
        }

        /// <summary>
        /// A completed achievement with a TradeskillAchievementReward row: talent points for its skill, schematics learned, reputation (certain:
        /// the row's fields; the skill comes from the achievement category).
        /// </summary>
        public void OnAchievementCompleted(ushort achievementId)
        {
            foreach (TradeskillAchievementRewardEntry reward in GameTableManager.Instance.TradeskillAchievementReward.Entries.Where(r => r != null && r.AchievementId == achievementId))
            {
                uint skill = SkillOfAchievement(GameTableManager.Instance.Achievement.GetEntry(achievementId));
                if (reward.TalentPoints > 0u)
                {
                    if (skill != 0u && HasLearned(skill))
                    {
                        earnedPoints[skill] = (earnedPoints.TryGetValue(skill, out uint e) ? e : 0u) + reward.TalentPoints;
                        MarkSkill(skill);
                        SendProfessionUpdate(skill);
                        FailureLedger.Record("tradeskill-talent-point-earned", $"skill {skill}", $"achievement {achievementId}");
                    }
                    else
                        FailureLedger.Record("tradeskill-talent-point-no-skill", $"achievement {achievementId}", $"skill {skill} not learned or not found");
                }

                foreach (uint schematic in new[] { reward.TradeSkillSchematicId00, reward.TradeSkillSchematicId01, reward.TradeSkillSchematicId02, reward.TradeSkillSchematicId03,
                    reward.TradeSkillSchematicId04, reward.TradeSkillSchematicId05, reward.TradeSkillSchematicId06, reward.TradeSkillSchematicId07 })
                    if (schematic != 0u)
                        LearnSchematic(schematic);

                if (reward.Faction2Id != 0u && reward.FactionIdAmount != 0u)
                    player.ReputationManager.UpdateReputation((Reputation.Static.Faction)reward.Faction2Id, reward.FactionIdAmount);
            }
        }

        /// <summary>The skill's talent tiers in the order of the client's talent window (TradeskillTalentTier rows by PointsToUnlock, then id; likely).</summary>
        public static List<TradeskillTalentTierEntry> TalentTiers(uint tradeskillId) => GameTableManager.Instance.TradeskillTalentTier.Entries
            .Where(r => r != null && r.TradeSkillId == tradeskillId).OrderBy(r => r.PointsToUnlock).ThenBy(r => r.Id).Take(10).ToList();

        /// <summary>
        /// ClientTradeskillPickTalent 0x084E (skill, tier index 0-9, bonus id). Certain from the client's PickTradeskillTalent: the second field is
        /// the 0-based tier index of the talent window (it sends level - 1), the skill must be active, the slot empty, the tier's PointsToUnlock at
        /// most the skill's talent points, and the bonus one of the tier's five. Until 2026-10-18 the server read a TradeskillTalentTier row id and
        /// counted picks as spent points, so no tier above PointsToUnlock 0 (none exists) could ever be picked.
        /// </summary>
        public bool PickTalent(uint tradeskillId, uint tierIndex, uint bonusId)
        {
            List<TradeskillTalentTierEntry> rows = TalentTiers(tradeskillId);
            if (!HasLearned(tradeskillId) || bonusId == 0u || tierIndex >= rows.Count)
                return false;
            TradeskillTalentTierEntry tier = rows[(int)tierIndex];
            if (!new[] { tier.TradeSkillBonusId00, tier.TradeSkillBonusId01, tier.TradeSkillBonusId02, tier.TradeSkillBonusId03, tier.TradeSkillBonusId04 }.Contains(bonusId))
                return false;

            uint[] picked = talents.TryGetValue(tradeskillId, out uint[] current) ? current : new uint[10];
            if (picked.Length < 10)
                Array.Resize(ref picked, 10);
            if (picked[tierIndex] != 0u || TalentPoints(tradeskillId) < tier.PointsToUnlock)
                return false;

            picked[tierIndex] = bonusId;
            talents[tradeskillId] = picked;
            MarkSkill(tradeskillId);
            SendProfessionUpdate(tradeskillId);
            return true;
        }

        /// <summary>ClientTradeskillResetTalents 0x0858 (skill): every picked talent is cleared; the respec cost (TalentTier.RespecCost) is charged in credits.</summary>
        public bool ResetTalents(uint tradeskillId)
        {
            if (!talents.TryGetValue(tradeskillId, out uint[] picked) || picked.All(t => t == 0u))
                return false;

            ulong cost = GameTableManager.Instance.TradeskillTalentTier.Entries.Where(r => r != null && r.TradeSkillId == tradeskillId).Select(r => (ulong)r.RespecCost).DefaultIfEmpty(0ul).Max();
            if (cost > 0ul && !player.CurrencyManager.CanAfford(CurrencyType.Credits, cost))
                return false;
            if (cost > 0ul)
                player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, cost);

            talents.Remove(tradeskillId);
            MarkSkill(tradeskillId);
            SendProfessionUpdate(tradeskillId);
            return true;
        }

        /// <summary>
        /// A schematic of an active skill is known when it was learned (schematic item, achievement reward, discovery, crafted before), or it
        /// has flag 1 and its tier is reached (certain: the client adds those itself on a tier change). A coordinate variant (child row) that
        /// is not discoverable comes with its parent (likely: the grid shows the parent's variants). Every other schematic (flag 0 roots:
        /// 217 taught by GiveSchematic items, 371 by TradeskillAchievementReward) must be learned first.
        /// </summary>
        public bool KnowsSchematic(TradeskillSchematic2Entry schematic)
        {
            if (schematic == null || !HasLearned(schematic.TradeSkillId))
                return false;
            if (discovered.Contains(schematic.Id) || state.Crafted.Contains(schematic.Id))
                return true;
            if ((schematic.Flags & 1u) != 0u && schematic.Tier <= GetTier(schematic.TradeSkillId))
                return true;
            if (schematic.TradeskillSchematic2IdParent != 0u && schematic.DiscoverableQuadrant == 0u && schematic.TradeskillSchematic2IdParent != schematic.Id)
                return KnowsSchematic(GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematic.TradeskillSchematic2IdParent));
            return false;
        }

        /// <summary>Learns a discoverable schematic (from a schematic item, a quest reward or a discovery).</summary>
        public void LearnSchematic(uint schematicId)
        {
            if (!discovered.Contains(schematicId))
            {
                discovered.Add(schematicId);
                MarkSchematic(schematicId);
                TradeskillSchematic2Entry entry = GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematicId);
                player.Session?.EnqueueMessageEncrypted(new ServerSchematicAddLearned { TradeskillId = entry?.TradeSkillId ?? 0u, SchematicId = schematicId });
                player.AchievementManager.CheckAchievements(player, Achievement.Static.AchievementType.TradeskillLearnSchematics, schematicId);
            }
        }

        /// <summary>QA harness only: raises the skill to the xp needed for a tier so bots can follow quest chains without grinding.</summary>
        public void GrantXpForTier(uint tradeskillId, uint tier)
        {
            uint needed = GameTableManager.Instance.TradeskillTier.Entries.Where(r => r != null)
                .Where(t => t.TradeSkillId == tradeskillId && t.Tier <= tier).Select(t => t.RequiredXp).DefaultIfEmpty(0u).Max();
            if (HasLearned(tradeskillId) && state.Xp[tradeskillId] < needed)
                AddXp(tradeskillId, needed - state.Xp[tradeskillId]);
        }

        /// <summary>
        /// Crafts a schematic. Coordinate (flag 8) professions succeed when the click position (x, y) is inside the schematic's Radius
        /// (x radiusFactor: catalysts and talent bonuses) and are critical inside CritRadius (x critFactor); every other profession always succeeds.
        /// Talent bonuses: 6 lowers the cost of materials of an Item2Type, 11 is a chance that such materials are not used up, 10 multiplies the
        /// output of an Item2Type, 5 / 65 multiply the success / failure xp (semantics from the bonus tooltips, likely; docs/ASSUMPTIONS.md).
        /// </summary>
        public CraftResult Craft(uint schematicId, float x = float.NaN, float y = float.NaN, uint materialsFrom = 0u, float radiusFactor = 1f, float critFactor = 1f, bool materialsTaken = false)
        {
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematicId);
            if (schematic == null)
                return CraftResult.UnknownSchematic;

            if (!HasLearned(schematic.TradeSkillId))
                return CraftResult.SkillNotLearned;

            if (!KnowsSchematic(schematic))
                return CraftResult.UnknownSchematic;

            if (schematic.Tier > GetTier(schematic.TradeSkillId))
                return CraftResult.TierTooLow;

            // a variant of a coordinate craft without its own materials uses the base schematic's
            TradeskillSchematic2Entry cost = materialsFrom != 0u && schematic.Item2IdMaterial00 == 0u ? GameTableManager.Instance.TradeskillSchematic2.GetEntry(materialsFrom) ?? schematic : schematic;
            uint[] materials = { cost.Item2IdMaterial00, cost.Item2IdMaterial01, cost.Item2IdMaterial02, cost.Item2IdMaterial03, cost.Item2IdMaterial04 };
            uint[] costs     = { cost.MaterialCost00, cost.MaterialCost01, cost.MaterialCost02, cost.MaterialCost03, cost.MaterialCost04 };
            uint outputType  = GameTableManager.Instance.Item.GetEntry(schematic.Item2IdOutput)?.Item2TypeId ?? 0u;
            for (int i = 0; i < materials.Length; i++)
                costs[i] = MaterialCost(schematic.TradeSkillId, outputType, materials[i], costs[i]);
            if (materialsTaken)
                Array.Clear(materials, 0, materials.Length);
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != 0u && player.Inventory.GetItemCount(materials[i]) < costs[i])
                    return CraftResult.MissingMaterials;

            TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(schematic.TradeSkillId);
            bool coordinate = (skill.Flags & 8u) != 0u && schematic.Radius > 0f;

            CraftResult result = CraftResult.Success;
            if (coordinate)
            {
                float dx = (float.IsNaN(x) ? schematic.VectorX : x) - schematic.VectorX;
                float dy = (float.IsNaN(y) ? schematic.VectorY : y) - schematic.VectorY;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > schematic.Radius * radiusFactor)
                    result = CraftResult.Failed;
                else if (schematic.CritRadius > 0f && distance <= schematic.CritRadius * critFactor && schematic.Item2IdOutputCrit != 0u)
                    result = CraftResult.Critical;
            }

            uint output = result == CraftResult.Failed ? schematic.Item2IdOutputFail : schematic.Item2IdOutput;
            uint count  = Math.Max(schematic.OutputCount, 1u);
            if (result == CraftResult.Critical)
                count += schematic.OutputCountCritBonus;
            if (result != CraftResult.Failed)
            {
                float outputFactor = BonusFactor(TradeskillBonusType.OutputMultiplier, schematic.TradeSkillId, outputType);
                if (outputFactor != 1f)
                {
                    // a fractional part is a chance of one more (guess: "outputs increased by 50%" on single outputs)
                    float scaled = count * outputFactor;
                    uint more = (uint)MathF.Floor(scaled) + (random.NextDouble() < scaled - MathF.Floor(scaled) ? 1u : 0u);
                    RecordBonus(TradeskillBonusType.OutputMultiplier, $"schematic {schematic.Id} x{outputFactor:0.##}: {count} -> {more}");
                    count = Math.Max(more, 1u);
                }
            }

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == 0u)
                    continue;
                uint materialType = GameTableManager.Instance.Item.GetEntry(materials[i])?.Item2TypeId ?? 0u;
                float keep = BonusSum(TradeskillBonusType.MaterialReturn, schematic.TradeSkillId, outputType, materialType);
                if (keep > 0f && random.NextDouble() < keep)
                {
                    RecordBonus(TradeskillBonusType.MaterialReturn, $"schematic {schematic.Id} item {materials[i]} x{costs[i]} kept (chance {keep:0.###})");
                    continue;
                }
                player.Inventory.ItemDelete(materials[i], costs[i]);
            }

            if (output != 0u)
                player.Inventory.ItemCreate(InventoryLocation.Inventory, output, count);

            uint earnedXp = 0u;
            TradeskillTierEntry tier = GameTableManager.Instance.TradeskillTier.Entries.Where(r => r != null)
                .FirstOrDefault(t => t.TradeSkillId == schematic.TradeSkillId && t.Tier == Math.Max(schematic.Tier, 1u));
            if (tier != null)
            {
                bool first = !state.Crafted.Contains(schematic.Id);
                earnedXp = result == CraftResult.Failed ? tier.FailXp : first && tier.FirstCraftXp > 0u ? tier.FirstCraftXp : tier.CraftXp;
                TradeskillBonusType xpBonus = result == CraftResult.Failed ? TradeskillBonusType.FailXp : TradeskillBonusType.SuccessXp;
                float xpFactor = BonusFactor(xpBonus, schematic.TradeSkillId, outputType);
                if (xpFactor != 1f && earnedXp > 0u)
                {
                    uint scaled = (uint)MathF.Round(earnedXp * xpFactor);
                    RecordBonus(xpBonus, $"schematic {schematic.Id} x{xpFactor:0.##}: {earnedXp} -> {scaled}");
                    earnedXp = scaled;
                }
                AddXp(schematic.TradeSkillId, earnedXp);
            }

            if (!state.Crafted.Contains(schematic.Id))
            {
                state.Crafted.Add(schematic.Id);
                MarkSchematic(schematic.Id);
            }

            if (result != CraftResult.Failed)
            {
                player.QuestManager.ObjectiveUpdate(QuestObjectiveType.CraftSchematic, schematic.Id, 1u);
                player.QuestManager.ObjectiveUpdate(QuestObjectiveType.ObtainSchematic, schematic.Id, count);
                if (output != 0u)
                {
                    player.AchievementManager.CheckAchievements(player, Achievement.Static.AchievementType.TradeskillCraftItem, output, 0u, count);
                    player.AchievementManager.CheckAchievements(player, Achievement.Static.AchievementType.TradeskillCraftChecklist, output);
                    player.AchievementManager.CheckAchievements(player, Achievement.Static.AchievementType.TradeskillCraftAny, output);
                }
            }

            player.Session?.EnqueueMessageEncrypted(new ServerCraftingFinish
            {
                Pass        = result != CraftResult.Failed,
                SchematicId = schematic.Id,
                ItemId      = output,
                EarnedXp    = earnedXp
            });
            return result;
        }
    }
}
