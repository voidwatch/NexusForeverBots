using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;

namespace NexusForever.WorldServer.Game.Tradeskill
{
    /// <summary>
    /// TradeskillBonus.tradeskillBonusEnum values the crafting code applies. Names from the tooltips of the 248 rows (every row of a type says the
    /// same thing); the additive ones (14, 15, 16, 26, 27) and the direction filter are read the same way by the client's crafting window.
    /// docs/ASSUMPTIONS.md "Crafting: catalysts, talents, additives, salvage".
    /// </summary>
    public enum TradeskillBonusType
    {
        SuccessXp          = 5,   // x value: "experience from crafting success increased by 5%"
        MaterialReduction  = 6,   // - value per material of Item2Type tertiary: "reduces ... requirements by 1"
        OutputMultiplier   = 10,  // x value for outputs of Item2Type secondary: "outputs ... increased by 50%"
        MaterialReturn     = 11,  // + value chance that materials of Item2Type tertiary are not used up
        AdditiveCost       = 14,  // x value on the additive price (client GetBuyPrice of an additive item)
        AdditivePower      = 15,  // x value on the additive vector (client GetAdditiveInfo)
        AdditiveSpread     = 16,  // x value on the additive landing radius (client GetAdditiveInfo)
        CraftTargetRadius  = 17,  // x value on craft target radii: "craft targets are easier to hit"
        DiscoveryRadius    = 20,  // x value on discovery target radii: "discoveries are 20% easier to hit"
        ExtraAdditiveSlot  = 26,  // + value additive slots (client AddAdditive limit)
        ExtraIngredients   = 27,  // + value additive tiers for hobby skills (client AddAdditive / GetAvailableAdditives)
        ExtraCatalyst      = 28,  // + value catalysts per craft (stored, see AddAdditive)
        FailXp             = 65,  // x value: "experience gained from crafting failures increased by 8%"
    }

    public partial class TradeskillManager
    {
        private readonly Random random = new();

        private readonly struct BonusPart
        {
            public readonly uint Type, Skill, OutputType, Filter;
            public readonly float Value;
            public BonusPart(uint type, uint skill, uint outputType, uint filter, float value)
            {
                Type = type; Skill = skill; OutputType = outputType; Filter = filter; Value = value;
            }
        }

        /// <summary>
        /// Bonus rows the character has: picked talents (TradeskillTalentTier bonuses), rows granted by a reached tier (TradeSkillTierId) and rows
        /// granted by a completed achievement (AchievementId). The tier and achievement rows: likely (14 + 14 rows, nothing else refers to them).
        /// </summary>
        private IEnumerable<TradeskillBonusEntry> BonusRows()
        {
            foreach ((uint skill, uint[] picked) in talents)
            {
                if (!HasLearned(skill))
                    continue;
                foreach (uint bonusId in picked)
                    if (bonusId != 0u && GameTableManager.Instance.TradeskillBonus.GetEntry(bonusId) is { } row)
                        yield return row;
            }

            foreach (TradeskillBonusEntry row in GameTableManager.Instance.TradeskillBonus.Entries)
            {
                if (row == null)
                    continue;
                if (row.TradeSkillTierId != 0u && GameTableManager.Instance.TradeskillTier.GetEntry(row.TradeSkillTierId) is { } tier && GetTier(tier.TradeSkillId) >= tier.Tier)
                    yield return row;
                else if (row.AchievementId != 0u && row.AchievementId <= ushort.MaxValue && player.AchievementManager.HasCompletedAchievement((ushort)row.AchievementId))
                    yield return row;
            }
        }

        private IEnumerable<BonusPart> BonusParts(TradeskillBonusType type)
        {
            foreach (TradeskillBonusEntry row in BonusRows())
            {
                if (row.TradeskillBonusEnum00 == (uint)type)
                    yield return new BonusPart(row.TradeskillBonusEnum00, row.ObjectIdPrimary00, row.ObjectIdSecondary00, row.ObjectIdTertiary00, row.Value00);
                if (row.TradeskillBonusEnum01 == (uint)type)
                    yield return new BonusPart(row.TradeskillBonusEnum01, row.ObjectIdPrimary01, row.ObjectIdSecondary01, row.ObjectIdTertiary01, row.Value01);
                if (row.TradeskillBonusEnum02 == (uint)type)
                    yield return new BonusPart(row.TradeskillBonusEnum02, row.ObjectIdPrimary02, row.ObjectIdSecondary02, row.ObjectIdTertiary02, row.Value02);
            }
        }

        /// <summary>
        /// The client's direction filter of the additive bonuses (14, 15, 16): tertiary 0 = any additive, 1 = vector X > 0, 2 = Y > 0, 3 = X < 0,
        /// 4 = Y < 0 (certain, client bonus accumulator). Cooking: Spicy/Savory/Sour/Sweet, Technologist and Architect: their four axes.
        /// </summary>
        private static bool DirectionMatches(uint filter, float x, float y) => filter switch
        {
            0u => true,
            1u => x > 0f,
            2u => y > 0f,
            3u => x < 0f,
            4u => y < 0f,
            _  => false
        };

        private static bool Matches(BonusPart part, uint skill, uint outputType) =>
            (part.Skill == 0u || part.Skill == skill) && (part.OutputType == 0u || part.OutputType == outputType);

        /// <summary>Product of the matching multiplier bonuses (1 when none); the client multiplies them (accumulator mode 3).</summary>
        public float BonusFactor(TradeskillBonusType type, uint skill, uint outputType = 0u, float? x = null, float? y = null)
        {
            float factor = 1f;
            foreach (BonusPart part in BonusParts(type))
                if (Matches(part, skill, outputType) && (x == null || DirectionMatches(part.Filter, x.Value, y ?? 0f)))
                    factor *= part.Value;
            return factor;
        }

        /// <summary>Sum of the matching additive bonuses (0 when none); tertiary filters on the material's Item2Type when given.</summary>
        public float BonusSum(TradeskillBonusType type, uint skill, uint outputType = 0u, uint materialType = 0u)
        {
            float sum = 0f;
            foreach (BonusPart part in BonusParts(type))
                if (Matches(part, skill, outputType) && (part.Filter == 0u || part.Filter == materialType))
                    sum += part.Value;
            return sum;
        }

        /// <summary>Bonus ids of the picked talents of a skill (for the QA harness and the probes).</summary>
        public IEnumerable<uint> PickedTalents(uint tradeskillId) =>
            talents.TryGetValue(tradeskillId, out uint[] picked) ? picked.Where(t => t != 0u) : Enumerable.Empty<uint>();

        /// <summary>Bonus id picked in a talent tier slot (0-9) of a skill, 0 when empty; the same bonus id can sit in several tiers.</summary>
        public uint PickedTalentAt(uint tradeskillId, int tierIndex) =>
            talents.TryGetValue(tradeskillId, out uint[] picked) && tierIndex >= 0 && tierIndex < picked.Length ? picked[tierIndex] : 0u;

        /// <summary>QA harness only: one talent point for a skill (the real source is TradeskillAchievementReward.TalentPoints; counted).</summary>
        public void GrantQaTalentPoint(uint tradeskillId)
        {
            if (!HasLearned(tradeskillId))
                return;
            earnedPoints[tradeskillId] = (earnedPoints.TryGetValue(tradeskillId, out uint e) ? e : 0u) + 1u;
            MarkSkill(tradeskillId);
            SendProfessionUpdate(tradeskillId);
            FailureLedger.Record("tradeskill-qa-talent-point-grant", $"skill {tradeskillId}");
            QuestTruth.Grant(player, "talent-point-grant", tradeskillId);
        }

        private static void RecordBonus(TradeskillBonusType type, string detail)
        {
            FailureLedger.Record("tradeskill-bonus-applied", type.ToString(), detail);
        }
    }

    /// <summary>
    /// TradeskillCatalyst.tradeskillCatalystEnum (certain: the client's catalyst function and Item:GetGlobalCatalystInfo, named by the TEST catalyst
    /// items 2-15 "Increase Magnitude X +5", "Swap Vector", "Rotate Vector 45", "Invert Vector", "Ran Rad Max", "Ran Rad Min", "Ran Angle Center",
    /// "Ran Angle Range", "Crit/Craft/Discovery Radius Multiplier", "Increase Magnitude"; 15/16 from the item tooltips "Attracts the crafting point
    /// to the nearest ... crafting/discovery target at craft completion"). 1-9 and 14 change an additive, 11-16 (not 14) are "global".
    /// </summary>
    public static class TradeskillCatalysts
    {
        public const uint MultiplyX = 1, MultiplyY = 2, SwapXY = 3, Rotate = 4, Invert = 5, RadiusMax = 6, RadiusMin = 7, StartAngle = 8, AngleRange = 9,
            CritRadius = 11, CraftRadius = 12, DiscoveryRadius = 13, Magnitude = 14, AttractCraftTarget = 15, AttractDiscovery = 16;

        /// <summary>Where an additive can land: its vector from the current point and a ring sector around the vector's end.</summary>
        public sealed class AdditiveShape
        {
            public float X, Y, RadiusMin, RadiusMax, StartAngle, MaxRotation = MathF.PI * 2f;
        }

        public static IEnumerable<(uint Kind, float Value)> Effects(TradeskillCatalystEntry row)
        {
            if (row == null)
                yield break;
            uint[] kinds = { row.TradeskillCatalystEnum00, row.TradeskillCatalystEnum01, row.TradeskillCatalystEnum02, row.TradeskillCatalystEnum03, row.TradeskillCatalystEnum04 };
            float[] values = { row.Value00, row.Value01, row.Value02, row.Value03, row.Value04 };
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i] != 0u)
                    yield return (kinds[i], values[i]);
        }

        /// <summary>The client's GetGlobalCatalystInfo test: the first effect is 11-16 but not 14.</summary>
        public static bool IsGlobal(TradeskillCatalystEntry row) => row != null && row.TradeskillCatalystEnum00 > 10u && row.TradeskillCatalystEnum00 != 14u && row.TradeskillCatalystEnum00 < 17u;

        /// <summary>The client's AddAdditive test for a catalyst sent with an additive: one of its effects is 1-9 or 14.</summary>
        public static bool ChangesAdditive(TradeskillCatalystEntry row) => Effects(row).Any(e => e.Kind - 1u < 9u || e.Kind == Magnitude);

        /// <summary>Product of one global multiplier (11, 12 or 13) over the catalysts (1 when none).</summary>
        public static float Multiplier(IEnumerable<TradeskillCatalystEntry> rows, uint kind)
        {
            float factor = 1f;
            foreach (TradeskillCatalystEntry row in rows)
                foreach ((uint k, float v) in Effects(row))
                    if (k == kind)
                        factor *= v;
            return factor;
        }

        public static bool Has(IEnumerable<TradeskillCatalystEntry> rows, uint kind) => rows.Any(r => Effects(r).Any(e => e.Kind == kind));

        /// <summary>Applies a catalyst to an additive in the client's order (certain, same switch).</summary>
        public static void Apply(TradeskillCatalystEntry row, AdditiveShape shape)
        {
            const float degrees = MathF.PI / 180f;
            foreach ((uint kind, float value) in Effects(row))
            {
                switch (kind)
                {
                    case MultiplyX: shape.X *= value; break;
                    case MultiplyY: shape.Y *= value; break;
                    case SwapXY: (shape.X, shape.Y) = (shape.Y, shape.X); break;
                    case Rotate:
                    {
                        float a = value * degrees, sin = MathF.Sin(a), cos = MathF.Cos(a);
                        (shape.X, shape.Y) = (shape.X * cos - shape.Y * sin, shape.Y * cos + shape.X * sin);
                        break;
                    }
                    case Invert: shape.X = -shape.X; shape.Y = -shape.Y; break;
                    case RadiusMax: shape.RadiusMax *= value; break;
                    case RadiusMin: shape.RadiusMin = (shape.RadiusMax - shape.RadiusMin) * value + shape.RadiusMin; break;
                    case StartAngle: shape.StartAngle = value * degrees; break;
                    case AngleRange: shape.MaxRotation = value * degrees; break;
                    case Magnitude: shape.X *= value; shape.Y *= value; break;
                }
            }
        }
    }
}
