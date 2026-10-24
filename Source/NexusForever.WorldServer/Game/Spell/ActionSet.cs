using System;
using System.Linq;
using System.Collections.Generic;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NexusForever.WorldServer.Network.Message.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Spell
{
    public class ActionSet : ISaveCharacter
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public const byte MaxTierPoints  = 42;
        public const byte MaxActionSets  = 4;
        public const byte MaxActionCount = 48;
        public const byte MaxAmpPoints   = 45 + 10; // 10 are bonus unlocked
        public const byte MaxTier        = 9;

        public ulong Owner { get; }
        public byte Index { get; }
        public byte TierPoints { get; private set; }
        public byte AmpPoints { get; private set; }

        /// <summary>AMP power of the set: 45 base plus the bonus unlocked by "AMP Power Unlock" (GiveAugmentPowerToPlayer, at most 10).</summary>
        public byte AmpPowerTotal => (byte)(MaxAmpPoints - 10 + bonusAmpPower);
        public byte TierPointsTotal => (byte)(MaxTierPoints + bonusTierPoints);
        private byte bonusAmpPower;
        private byte bonusTierPoints;

        /// <summary>Bonus AMP power and ability tier points from unlock items (SpellManager keeps the count per character).</summary>
        public void GrantBonus(byte ampPower, byte tierPoints)
        {
            bonusAmpPower   += ampPower;
            AmpPoints       += ampPower;
            bonusTierPoints += tierPoints;
            TierPoints      += tierPoints;
        }

        /// <summary>
        /// Collection of <see cref="ActionSetShortcut"/> contained in the <see cref="ActionSet"/>.
        /// </summary>
        public IEnumerable<ActionSetShortcut> Actions => actions.Values.Where(a => !a.PendingDelete);

        /// <summary>
        /// Collection of <see cref="ActionSetAmp"/> contained in the <see cref="ActionSet"/>.
        /// </summary>
        public IEnumerable<ActionSetAmp> Amps => amps.Values.Where(a => !a.PendingDelete);

        private readonly Dictionary<UILocation, ActionSetShortcut> actions = new();
        private readonly Dictionary<ushort, ActionSetAmp> amps = new();
        private readonly List<ushort> invalidAmpRows = new();

        private ActionSetSaveMask saveMask;
        private readonly Player player;

        /// <summary>LevelUpUnlock row 26 "AMP System": AMPs unlock at level 10.</summary>
        public const uint AmpUnlockLevel = 10u;

        /// <summary>
        /// Create a new <see cref="ActionSet"/> with supplied index.
        /// </summary>
        public ActionSet(byte index, Player player)
        {
            this.player = player;
            Owner      = player.CharacterId;
            Index      = index;
            TierPoints = MaxTierPoints;
            AmpPoints  = MaxAmpPoints - 10;
        }

        public void Save(CharacterContext context)
        {
            if (saveMask == ActionSetSaveMask.None)
                return;

            if ((saveMask & ActionSetSaveMask.ActionSetAmps) != 0)
            {
                foreach (ushort invalid in invalidAmpRows)
                    context.Entry(new CharacterActionSetAmpModel { Id = Owner, SpecIndex = Index, AmpId = invalid }).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
                invalidAmpRows.Clear();

                foreach ((ushort id, ActionSetAmp amp) in amps.OrderBy(i => i.Value.PendingDelete == true).ToList())
                {
                    if (amp.PendingDelete)
                        amps.Remove(id);

                    amp.Save(context);
                }
            }

            if ((saveMask & ActionSetSaveMask.ActionSetActions) != 0)
            {
                foreach ((UILocation location, ActionSetShortcut shortcut) in actions.OrderBy(i => i.Value.PendingDelete == true).ToList())
                {
                    if (shortcut.PendingDelete)
                        actions.Remove(location);

                    shortcut.Save(context);
                }
            }

            saveMask = ActionSetSaveMask.None;
        }

        /// <summary>
        /// Return <see cref="ActionSetShortcut"/> at supplied <see cref="UILocation"/>.
        /// </summary>
        public ActionSetShortcut GetShortcut(UILocation location)
        {
            if (!actions.TryGetValue(location, out ActionSetShortcut shortcut))
                return null;

            return shortcut.PendingDelete ? null : shortcut;
        }

        /// <summary>
        /// Return <see cref="ActionSetShortcut"/> with supplied <see cref="ShortcutType"/> and object id.
        /// </summary>
        public ActionSetShortcut GetShortcut(ShortcutType type, uint objectId)
        {
            ActionSetShortcut shortcut = actions.Values
                .Where(a => !a.PendingDelete)
                .SingleOrDefault(a => a.ShortcutType == type && a.ObjectId == objectId);

            return shortcut;
        }

        /// <summary>
        /// Return <see cref="ActionSetAmp"/> with supplied id.
        /// </summary>
        public ActionSetAmp GetAmp(ushort id)
        {
            if (!amps.TryGetValue(id, out ActionSetAmp amp))
                return null;

            return amp.PendingDelete ? null : amp;
        }

        /// <summary>
        /// Add shortcut to <see cref="ActionSet"/> to supplied <see cref="UILocation"/>.
        /// </summary>
        public void AddShortcut(UILocation location, ShortcutType type, uint objectId, byte tier)
        {
            if (actions.TryGetValue(location, out ActionSetShortcut shortcut) && !shortcut.PendingDelete)
                throw new InvalidOperationException($"Failed to add shortcut {type} {objectId} to {location}, location is occupied!");

            if (type == ShortcutType.Spell)
            {
                checked
                {
                    TierPoints -= CalculateTierCost(tier);
                }
            }

            if (shortcut != null)
            {
                shortcut.EnqueueDelete(false);
                shortcut.ShortcutType = type;
                shortcut.ObjectId     = objectId;
                shortcut.Tier         = tier;
            }
            else
                actions.Add(location, new ActionSetShortcut(this, location, type, objectId, tier));

            saveMask |= ActionSetSaveMask.ActionSetActions;

            log.Trace($"Added shortcut {type} {objectId} at {location} to action set {Index}.");
        }

        /// <summary>
        /// Add shortcut to <see cref="ActionSet"/> from an existing database model.
        /// </summary>
        public void AddShortcut(CharacterActionSetShortcutModel model)
        {
            var shortcut = new ActionSetShortcut(this, model);

            if (shortcut.ShortcutType == ShortcutType.Spell)
            {
                checked
                {
                    TierPoints -= CalculateTierCost(model.Tier);
                }
            }

            actions.Add(shortcut.Location, shortcut);
        }

        /// <summary>
        /// Update a <see cref="ShortcutType.Spell"/> shortcut with supplied tier.
        /// </summary>
        public void UpdateSpellShortcut(uint spell4BaseId, byte tier)
        {
            ActionSetShortcut shortcut = GetShortcut(ShortcutType.Spell, spell4BaseId);
            if (shortcut == null)
                throw new ArgumentException();

            checked
            {
                TierPoints += CalculateTierCost(shortcut.Tier);
                TierPoints -= CalculateTierCost(tier);
            }

            shortcut.Tier = tier;
            saveMask |= ActionSetSaveMask.ActionSetActions;
        }

        /// <summary>
        /// Remove shortcut from <see cref="ActionSet"/> at supplied <see cref="UILocation"/>.
        /// </summary>
        public void RemoveShortcut(UILocation location)
        {
            ActionSetShortcut shortcut = GetShortcut(location);
            if (shortcut == null)
                throw new ArgumentException($"Failed to remove shortcut from {location}, location isn't occupied!");

            if (shortcut.ShortcutType == ShortcutType.Spell)
            {
                checked
                {
                    TierPoints += CalculateTierCost(shortcut.Tier);
                }
            }

            if (shortcut.PendingCreate)
                actions.Remove(location);
            else
            {
                shortcut.EnqueueDelete(true);
                saveMask |= ActionSetSaveMask.ActionSetActions;
            }

            log.Trace($"Removed shortcut {shortcut.ShortcutType} {shortcut.ObjectId} at {location} from action set {Index}.");
        }

        private byte CalculateTierCost(byte tier)
        {
            if (tier > MaxTier)
                throw new ArgumentOutOfRangeException();
                
            byte cost = 0;
            for (byte i = 2; i <= tier; i++)
                cost += (byte)(i == 5 || i == 9 ? 5 : 1);

            return cost;
        }

        /// <summary>
        /// Points spent in the categories that unlock the tiers of the category (EldanAugmentationCategory Tier2Category00/01; every row
        /// names only itself).
        /// </summary>
        private uint CategoryPoints(EldanAugmentationCategoryEntry category)
        {
            return (uint)Amps
                .Where(a => a.Entry.EldanAugmentationCategoryId != 0u
                    && (a.Entry.EldanAugmentationCategoryId == category.EldanAugmentationCategoryIdTier2Category00
                        || a.Entry.EldanAugmentationCategoryId == category.EldanAugmentationCategoryIdTier2Category01))
                .Sum(a => a.Entry.PowerCost);
        }

        /// <summary>
        /// Why the AMP cannot be added, or null. Rules from the tables (docs/ASSUMPTIONS.md "AMPs"): the class (ClassId), level 10
        /// (LevelUpUnlock 26), the AMP named by EldanAugmentationIdRequired, CategoryTier 1 needs Tier2CostAmount (3) and CategoryTier 2
        /// Tier3CostAmount (11) points in the category, and enough AMP power.
        /// </summary>
        public string CanAddAmp(EldanAugmentationEntry entry)
        {
            if (entry == null)
                return "unknown AMP";
            if (amps.TryGetValue((ushort)entry.Id, out ActionSetAmp existing) && !existing.PendingDelete)
                return "already added";
            if (player != null && entry.ClassId != (uint)player.Class)
                return $"class {entry.ClassId}";
            if (player != null && player.Level < AmpUnlockLevel)
                return "below level 10";
            if (player?.SpellManager != null && !player.SpellManager.IsAmpUnlocked(entry.Id))
                return "not unlocked (AMP item not used)";
            if (entry.EldanAugmentationIdRequired != 0u && GetAmp((ushort)entry.EldanAugmentationIdRequired) == null)
                return $"requires AMP {entry.EldanAugmentationIdRequired}";
            if (entry.CategoryTier > 0u)
            {
                EldanAugmentationCategoryEntry category = GameTableManager.Instance.EldanAugmentationCategory.GetEntry(entry.EldanAugmentationCategoryId);
                if (category != null)
                {
                    uint needed = entry.CategoryTier == 1u ? category.Tier2CostAmount00 : category.Tier3CostAmount00;
                    if (CategoryPoints(category) < needed)
                        return $"tier {entry.CategoryTier + 1} needs {needed} points in category {category.Id}";
                }
            }
            if (entry.PowerCost > AmpPoints)
                return "not enough AMP power";
            return null;
        }

        /// <summary>
        /// After a respec: AMPs whose requirement (required AMP or category tier points) is no longer met are removed too.
        /// </summary>
        private void RemoveUnsupportedAmps()
        {
            bool removed;
            do
            {
                removed = false;
                foreach (ActionSetAmp amp in Amps.ToList())
                {
                    bool requiredMissing = amp.Entry.EldanAugmentationIdRequired != 0u && GetAmp((ushort)amp.Entry.EldanAugmentationIdRequired) == null;
                    bool tierMissing = false;
                    if (amp.Entry.CategoryTier > 0u)
                    {
                        EldanAugmentationCategoryEntry category = GameTableManager.Instance.EldanAugmentationCategory.GetEntry(amp.Entry.EldanAugmentationCategoryId);
                        if (category != null)
                        {
                            uint needed = amp.Entry.CategoryTier == 1u ? category.Tier2CostAmount00 : category.Tier3CostAmount00;
                            // the AMP's own points do not count towards its own tier
                            tierMissing = CategoryPoints(category) - amp.Entry.PowerCost < needed;
                        }
                    }
                    if (requiredMissing || tierMissing)
                    {
                        RemoveAmp(amp);
                        removed = true;
                    }
                }
            }
            while (removed);
        }

        /// <summary>
        /// Add AMP to <see cref="ActionSet"/> with supplied id.
        /// </summary>
        public void AddAmp(ushort id)
        {
            EldanAugmentationEntry entry = GameTableManager.Instance.EldanAugmentation.GetEntry(id);
            if (entry == null)
                throw new ArgumentException($"Invalid eldan augmentation id {id}!");

            if (amps.TryGetValue(id, out ActionSetAmp amp) && !amp.PendingDelete)
                throw new InvalidOperationException($"Failed to add AMP {id}, location is already occupied!");

            checked
            {
                AmpPoints -= (byte)entry.PowerCost;
            }

            if (amp != null)
                amp.EnqueueDelete(false);
            else
                // a row skipped at load is still in the database: keep it instead of deleting and adding the same key
                amps.Add(id, new ActionSetAmp(this, entry, !invalidAmpRows.Remove(id)));

            saveMask |= ActionSetSaveMask.ActionSetAmps;
            player?.SpellManager?.MarkAmpSpellsDirty();

            log.Trace($"Added AMP {id} to action set {Index}.");
        }

        /// <summary>
        /// Add AMP to <see cref="ActionSet"/> from an existing database model.
        /// </summary>
        public void AddAmp(CharacterActionSetAmpModel model)
        {
            // rows saved before the AMP rules were enforced may name AMPs that do not exist, belong to another class or cost more than the set
            // has: they are left out (and stay in the database untouched)
            EldanAugmentationEntry entry = GameTableManager.Instance.EldanAugmentation.GetEntry(model.AmpId);
            string refusal = entry == null ? "unknown AMP" : player != null && entry.ClassId != (uint)player.Class ? $"class {entry.ClassId}"
                : entry.PowerCost > AmpPoints ? "over the AMP power" : amps.ContainsKey(model.AmpId) ? "duplicate" : null;
            if (refusal != null)
            {
                // the row goes on the next save (rows written before the AMP id was saved as a byte, 2026-10-09, are mostly of this kind)
                Diagnostics.FailureLedger.Record("amp-load-skipped", refusal, $"amp {model.AmpId} action set {Index}");
                if (refusal != "duplicate")
                {
                    invalidAmpRows.Add(model.AmpId);
                    saveMask |= ActionSetSaveMask.ActionSetAmps;
                }
                return;
            }

            AmpPoints -= (byte)entry.PowerCost;
            amps.Add(model.AmpId, new ActionSetAmp(this, entry, false));
        }

        /// <summary>
        /// Remove one or more AMP's from <see cref="ActionSet"/> depending on supplied <see cref="AmpRespecType"/>.
        /// </summary>
        public void RemoveAmp(AmpRespecType type, uint value)
        {
            switch (type)
            {
                case AmpRespecType.Full:
                {
                    foreach (ActionSetAmp amp in Amps.ToList())
                        RemoveAmp(amp);
                    break;
                }
                case AmpRespecType.Section:
                {
                    foreach (ActionSetAmp amp in Amps
                        .ToList()
                        .Where(a => a.Entry.EldanAugmentationCategoryId == value))
                        RemoveAmp(amp);
                    break;
                }
                case AmpRespecType.Single:
                {
                    ActionSetAmp amp = GetAmp((ushort)value);
                    RemoveAmp(amp);
                    break;
                }
                default:
                    throw new ArgumentException();
            }

            RemoveUnsupportedAmps();
            player?.SpellManager?.MarkAmpSpellsDirty();
        }

        private void RemoveAmp(ActionSetAmp amp)
        {
            if (amp == null)
                throw new ArgumentNullException();

            checked
            {
                AmpPoints += (byte)amp.Entry.PowerCost;
            }

            if (amp.PendingCreate)
                amps.Remove((ushort)amp.Entry.Id);
            else
            {
                amp.EnqueueDelete(true);
                saveMask |= ActionSetSaveMask.ActionSetAmps;
            }

            log.Trace($"Removed AMP {amp.Entry.Id} from action set {Index}.");
        }

        /// <summary>
        /// Build a network representation of the <see cref="ActionSetShortcut"/>'s in the <see cref="ActionSet"/>.
        /// </summary>
        public ServerActionSet BuildServerActionSet()
        {
            var serverActionSet = new ServerActionSet
            {
                Index    = Index,
                Unknown3 = 1,
                Result   = LimitedActionSetResult.Ok
            };

            for (UILocation i = 0; i < (UILocation)MaxActionCount; i++)
            {
                ActionSetShortcut action = GetShortcut(i);
                serverActionSet.Actions.Add(new ServerActionSet.Action
                {
                    ShortcutType = action?.ShortcutType ?? ShortcutType.None,
                    ObjectId     = action?.ObjectId ?? 0,
                    Location     = new ItemLocation
                    {
                        // TODO: this might not be correct, what about shortcuts that aren't spells?
                        Location = action != null ? InventoryLocation.Ability : (InventoryLocation)300, // no idea why 300, this is what retail did
                        BagIndex = (uint)(action?.Location ?? i)
                    }
                });
            }

            return serverActionSet;
        }

        /// <summary>
        /// Build a network representation of the <see cref="ActionSetAmp"/>'s in the <see cref="ActionSet"/>.
        /// </summary>
        public ServerAmpList BuildServerAmpList()
        {
            var serverAmpList = new ServerAmpList
            {
                SpecIndex = Index
            };

            foreach (ActionSetAmp amp in Amps)
                serverAmpList.Amps.Add((ushort)amp.Entry.Id);

            return serverAmpList;
        }
    }
}
