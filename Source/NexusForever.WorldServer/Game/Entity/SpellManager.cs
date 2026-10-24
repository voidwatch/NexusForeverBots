using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NexusForever.WorldServer.Network.Message.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Entity
{
    public class SpellManager : IUpdate, ISaveCharacter
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Index of the active <see cref="ActionSet"/>.
        /// </summary>
        public byte ActiveActionSet
        {
            get => activeActionSet;
            private set
            {
                saveMask |= SpellManagerSaveMask.ActiveActionSet;
                activeActionSet = value;
            }
        }

        private byte activeActionSet;

        private readonly Player player;

        private readonly Dictionary<uint /*spell4BaseId*/, CharacterSpell> spells = new();
        private readonly Dictionary<uint /*spell4Id*/, double /*cooldown*/> spellCooldowns = new();
        private double globalSpellCooldown;

        private readonly ActionSet[] actionSets = new ActionSet[ActionSet.MaxActionSets];

        private SpellManagerSaveMask saveMask;

        /// <summary>AMP unlocks and bonus points, kept in the character JSON store ("amps").</summary>
        public sealed class AmpSaved
        {
            public List<uint> Unlocked { get; set; } = new();
            public int BonusAmpPower { get; set; }
            public int BonusTierPoints { get; set; }
        }

        private readonly CharacterJson<AmpSaved> ampStore;

        /// <summary>Prerequisite 28557 of "AMP Power Unlock" (type 237 LessThan 10, failure text "Maximum AMP power reached").</summary>
        public const int MaxBonusAmpPower = 10;
        /// <summary>Prerequisite 28559 of "Ability Tier Point Unlock" (type 235 LessThan 7, failure text "Maximum Ability Tier points reached").</summary>
        public const int MaxBonusTierPoints = 7;

        public int BonusAmpPower => ampStore.Value.BonusAmpPower;
        public int BonusTierPoints => ampStore.Value.BonusTierPoints;

        private static HashSet<uint> lockedAmps;

        /// <summary>
        /// AMPs named by an UnlockInlaidAugment effect (281 rows, DataBits00 = EldanAugmentation id, the spells of the AMP items) are locked
        /// until that item is used; every other AMP is open from level 10 (docs/ASSUMPTIONS.md "AMPs", likely).
        /// </summary>
        public static bool AmpNeedsUnlock(uint ampId)
        {
            lockedAmps ??= new HashSet<uint>(GameTableManager.Instance.Spell4Effects.Entries
                .Where(e => e != null && e.EffectType == (uint)SpellEffectType.UnlockInlaidAugment)
                .Select(e => e.DataBits00));
            return lockedAmps.Contains(ampId);
        }

        public bool IsAmpUnlocked(uint ampId) => !AmpNeedsUnlock(ampId) || ampStore.Value.Unlocked.Contains(ampId);

        /// <summary>UnlockInlaidAugment: the AMP can be put into action sets from now on. False when it was open already.</summary>
        public bool UnlockAmp(uint ampId)
        {
            if (IsAmpUnlocked(ampId))
                return false;
            ampStore.Value.Unlocked.Add(ampId);
            ampStore.Save();
            return true;
        }

        /// <summary>GiveAugmentPowerToPlayer / GiveAbilityPointsToPlayer: bonus points for every action set, capped like the item prerequisites.</summary>
        public bool GrantBonusPoints(int ampPower, int tierPoints)
        {
            AmpSaved saved = ampStore.Value;
            int amp = Math.Clamp(ampPower, 0, MaxBonusAmpPower - saved.BonusAmpPower);
            int tier = Math.Clamp(tierPoints, 0, MaxBonusTierPoints - saved.BonusTierPoints);
            if (amp == 0 && tier == 0)
                return false;

            saved.BonusAmpPower += amp;
            saved.BonusTierPoints += tier;
            ampStore.Save();
            foreach (ActionSet set in actionSets)
                set.GrantBonus((byte)amp, (byte)tier);
            if (!player.IsLoading)
            {
                SendServerAbilityPoints();
                player.Session.EnqueueMessageEncrypted(actionSets[ActiveActionSet].BuildServerAmpList());
            }
            return true;
        }

        // AMP passives (EldanAugmentation.Spell4IdAugment) of the active action set that are applied to the player
        private readonly HashSet<uint> appliedAmpSpells = new();
        private bool ampSpellsDirty = true;

        /// <summary>
        /// Create a new <see cref="SpellManager"/> from existing <see cref="CharacterModel"/> database model.
        /// </summary>
        public SpellManager(Player owner, CharacterModel model)
        {
            player = owner;

            foreach (CharacterSpellModel spellModel in model.Spell)
            {
                SpellBaseInfo spellBaseInfo = GlobalSpellManager.Instance.GetSpellBaseInfo(spellModel.Spell4BaseId);
                Item item = player.Inventory.SpellCreate(spellBaseInfo.Entry, ItemUpdateReason.NoReason);
                spells.Add(spellModel.Spell4BaseId, new CharacterSpell(owner, spellModel, spellBaseInfo, item));
            }

            GrantSpells();

            ampStore = new CharacterJson<AmpSaved>("amps", owner.CharacterId);
            for (byte i = 0; i < ActionSet.MaxActionSets; i++)
            {
                actionSets[i] = new ActionSet(i, player);
                // the bonus comes first: saved AMPs may use the bonus power
                actionSets[i].GrantBonus((byte)Math.Clamp(ampStore.Value.BonusAmpPower, 0, MaxBonusAmpPower), (byte)Math.Clamp(ampStore.Value.BonusTierPoints, 0, MaxBonusTierPoints));

                foreach (CharacterActionSetShortcutModel shortcutModel in model.ActionSetShortcut
                    .Where(c => c.SpecIndex == i))
                    actionSets[i].AddShortcut(shortcutModel);

                foreach (CharacterActionSetAmpModel ampModel in model.ActionSetAmp
                    .Where(c => c.SpecIndex == i))
                    actionSets[i].AddAmp(ampModel);
            }

            activeActionSet = model.ActiveSpec;
        }

        public void GrantSpells()
        {
            // TODO: TEMPORARY, this should eventually be used on level up
            foreach (SpellLevelEntry spellLevel in GameTableManager.Instance.SpellLevel.Entries
                .Where(s => s.ClassId == (byte)player.Class && s.CharacterLevel <= player.Level)
                .OrderBy(s => s.CharacterLevel))
            {
                //FIXME
                if (spellLevel.PrerequisiteId > 0)
                    continue;

                Spell4Entry spell4Entry = GameTableManager.Instance.Spell4.GetEntry(spellLevel.Spell4Id);
                if (spell4Entry == null)
                    continue;

                if (GetSpell(spell4Entry.Spell4BaseIdBaseSpell) == null)
                    AddSpell(spell4Entry.Spell4BaseIdBaseSpell);
            }

            ClassEntry classEntry = GameTableManager.Instance.Class.GetEntry((byte)player.Class);
            foreach (uint classSpell in classEntry.Spell4IdInnateAbilityActive
                .Concat(classEntry.Spell4IdInnateAbilityPassive)
                .Concat(classEntry.Spell4IdAttackPrimary)
                .Concat(classEntry.Spell4IdAttackUnarmed))
            {
                Spell4Entry spell4Entry = GameTableManager.Instance.Spell4.GetEntry(classSpell);
                if (spell4Entry == null)
                    continue;

                if (GetSpell(spell4Entry.Spell4BaseIdBaseSpell) == null)
                    AddSpell(spell4Entry.Spell4BaseIdBaseSpell);
            }
        }

        public void Update(double lastTick)
        {
            if (ampSpellsDirty && player.Map != null && !player.IsDead)
                RefreshAmpSpells();

            // update global cooldown
            if (globalSpellCooldown > 0d)
            {
                if (globalSpellCooldown - lastTick <= 0d)
                {
                    globalSpellCooldown = 0d;
                    log.Trace("Global spell cooldown has reset.");
                }
                else
                    globalSpellCooldown -= lastTick;
            }

            // update spell cooldowns
            foreach ((uint spellId, double cooldown) in spellCooldowns.ToArray())
            {
                if (cooldown - lastTick <= 0d)
                {
                    spellCooldowns.Remove(spellId);
                    log.Trace($"Spell {spellId} cooldown has reset.");
                }
                else
                    spellCooldowns[spellId] = cooldown - lastTick;
            }

            foreach (CharacterSpell unlockedSpell in spells.Values)
                unlockedSpell.Update(lastTick);
        }

        public void Save(CharacterContext context)
        {
            if (saveMask != SpellManagerSaveMask.None)
            {
                // character is attached in Player::Save, this will only be local lookup
                CharacterModel character = context.Character.Find(player.CharacterId);
                EntityEntry<CharacterModel> entity = context.Entry(character);

                if ((saveMask & SpellManagerSaveMask.ActiveActionSet) != 0)
                {
                    character.ActiveSpec = ActiveActionSet;
                    entity.Property(p => p.ActiveSpec).IsModified = true;
                }

                saveMask = SpellManagerSaveMask.None;
            }

            foreach (CharacterSpell spell in spells.Values)
                spell.Save(context);

            foreach (ActionSet actionSet in actionSets)
                actionSet.Save(context);
        }

        /// <summary>
        /// Returns <see cref="CharacterSpell"/> for an existing spell.
        /// </summary>
        public CharacterSpell GetSpell(uint spell4BaseId)
        {
            return spells.TryGetValue(spell4BaseId, out CharacterSpell spell) ? spell : null;
        }

        /// <summary>
        /// Add a new <see cref="CharacterSpell"/> created from supplied spell base id and tier.
        /// </summary>
        public void AddSpell(uint spell4BaseId, byte tier = 1)
        {
            SpellBaseInfo spellBaseInfo = GlobalSpellManager.Instance.GetSpellBaseInfo(spell4BaseId);
            if (spellBaseInfo == null)
                throw new ArgumentOutOfRangeException();

            SpellInfo spellInfo = spellBaseInfo.GetSpellInfo(tier);
            if (spellInfo == null)
                throw new ArgumentOutOfRangeException();

            if (spells.ContainsKey(spell4BaseId))
                throw new InvalidOperationException();

            Item item = player.Inventory.SpellCreate(spellBaseInfo.Entry, ItemUpdateReason.NoReason);

            var unlockedSpell = new CharacterSpell(player, spellBaseInfo, tier, item);
            if (!player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerSpellUpdate
                {
                    Spell4BaseId = spell4BaseId,
                    TierIndex    = tier,
                    Activated    = tier > 0
                });
            }

            spells.Add(spellBaseInfo.Entry.Id, unlockedSpell);
        }

        /// <summary>
        /// Update existing <see cref="CharacterSpell"/> with supplied tier. The base tier will be updated if no action set index is supplied.
        /// </summary>
        public void UpdateSpell(uint spell4BaseId, byte tier, byte? actionSetIndex)
        {
            SpellBaseInfo spellBaseInfo = GlobalSpellManager.Instance.GetSpellBaseInfo(spell4BaseId);
            if (spellBaseInfo == null)
                throw new ArgumentOutOfRangeException();

            SpellInfo spellInfo = spellBaseInfo.GetSpellInfo(tier);
            if (spellInfo == null)
                throw new ArgumentOutOfRangeException();

            CharacterSpell spell = GetSpell(spell4BaseId);
            if (spell == null)
                throw new ArgumentOutOfRangeException();

            if (actionSetIndex == null)
                spell.Tier = tier;
            else
            {
                ActionSet actionSet = GetActionSet(actionSetIndex.Value);
                actionSet.UpdateSpellShortcut(spell4BaseId, tier);
            }

            if (!player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerSpellUpdate
                {
                    Spell4BaseId = spell4BaseId,
                    TierIndex = tier,
                    SpecIndex = actionSetIndex ?? 0,
                    Activated = tier > 0
                });
            }
        }

        /// <summary>
        /// Return the tier for supplied spell.
        /// This will either be the <see cref="ActionSetShortcut"/> tier if placed in the active <see cref="ActionSet"/> or base tier if not.
        /// </summary>
        public byte GetSpellTier(uint spell4BaseId)
        {
            CharacterSpell spell = GetSpell(spell4BaseId);
            if (spell == null)
                throw new ArgumentException();

            ActionSet actionSet = GetActionSet(ActiveActionSet);
            ActionSetShortcut shortcut = actionSet.GetShortcut(ShortcutType.Spell, spell4BaseId);
            return shortcut?.Tier ?? spell.Tier;
        }

        public List<CharacterSpell> GetPets()
        {
            return spells.Values
                .Where(s => s.BaseInfo.SpellType.Id == 27 ||
                            s.BaseInfo.SpellType.Id == 30 ||
                            s.BaseInfo.SpellType.Id == 104)
                .ToList();
        }

        /// <summary>
        /// The spell4 rows of the spells the character knows (the tier they have learned).
        /// </summary>
        public IEnumerable<NexusForever.Shared.GameTable.Model.Spell4Entry> GetKnownSpell4Entries()
        {
            return spells.Values.Where(s => s.SpellInfo != null).Select(s => s.SpellInfo.Entry).ToList();
        }

        /// <summary>
        /// Return spell cooldown for supplied spell id in seconds.
        /// </summary>
        public double GetSpellCooldown(uint spellId)
        {
            return spellCooldowns.TryGetValue(spellId, out double cooldown) ? cooldown : 0d;
        }

        /// <summary>
        /// Set spell cooldown in seconds for supplied spell id.
        /// </summary>
        public void SetSpellCooldown(uint spell4Id, double cooldown)
        {
            if (cooldown < 0d)
                throw new ArgumentOutOfRangeException();

            if (spellCooldowns.ContainsKey(spell4Id))
                spellCooldowns[spell4Id] = cooldown;
            else
                spellCooldowns.Add(spell4Id, cooldown);

            log.Trace($"Spell {spell4Id} cooldown set to {cooldown} seconds.");

            if (!player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerCooldown
                {
                    Cooldown = new Cooldown
                    {
                        Type          = 1,
                        TypeId        = spell4Id,
                        SpellId       = spell4Id,
                        TimeRemaining = (uint)(cooldown * 1000u)
                    }
                });
            }
        }

        public void ResetAllSpellCooldowns()
        {
            foreach (uint spell4Id in spellCooldowns.Keys.ToList())
                SetSpellCooldown(spell4Id, 0d);
        }

        public double GetGlobalSpellCooldown()
        {
            return globalSpellCooldown;
        }

        public void SetGlobalSpellCooldown(double cooldown)
        {
            globalSpellCooldown = cooldown;
            log.Trace($"Global spell cooldown set to {cooldown} seconds.");
        }

        /// <summary>
        /// Return <see cref="ActionSet"/> at supplied index.
        /// </summary>
        public ActionSet GetActionSet(byte actionSetIndex)
        {
            if (actionSetIndex >= ActionSet.MaxActionSets)
                throw new ArgumentOutOfRangeException();

            return actionSets[actionSetIndex];
        }

        /// <summary>
        /// Update active <see cref="ActionSet"/> with supplied index, returned <see cref="SpecError"/> is sent to the client.
        /// </summary>
        public SpecError SetActiveActionSet(byte value)
        {
            if (value >= ActionSet.MaxActionSets)
                return SpecError.InvalidIndex;

            if (value == ActiveActionSet)
                return SpecError.NoChange;

            // TODO: handle other errors

            ActiveActionSet = value;
            ampSpellsDirty = true;
            return SpecError.Ok;
        }

        /// <summary>The AMPs of an action set changed: the passives are re-applied on the next update.</summary>
        public void MarkAmpSpellsDirty() => ampSpellsDirty = true;

        public bool IsAmpSpellApplied(uint spell4Id) => appliedAmpSpells.Contains(spell4Id);

        /// <summary>
        /// An AMP is a passive: its Spell4IdAugment (774 of 910 rows have a spell, duration 0, effects Proc 285, UnitPropertyModifier 221,
        /// Fluff 83, Proxy 33...) is cast on the player while the AMP is in the active action set and its lasting effects are removed when the
        /// AMP is respecced or the action set switched (docs/ASSUMPTIONS.md "AMPs", likely).
        /// </summary>
        private void RefreshAmpSpells()
        {
            ampSpellsDirty = false;
            var wanted = new HashSet<uint>(actionSets[ActiveActionSet].Amps
                .Select(a => a.Entry.Spell4IdAugment)
                .Where(id => id != 0u && GameTableManager.Instance.Spell4.GetEntry(id) != null));

            // the selected class innate's stance passive (Class.Spell4IdInnateAbilityPassive[InnateIndex], e.g. Warrior 44926 "Onslaught - Stance -
            // Passive", 44932 "Juggernaut - Stance - Passive": health x1.05, threat, support x1.04) is a lasting passive like an AMP (certain since
            // The client tooltip of the stance's Activate button, "A Defensive Stance that grants: ...", cites the passive's effect ids)
            ClassEntry classEntry = GameTableManager.Instance.Class.GetEntry((byte)player.Class);
            if (classEntry != null && player.InnateIndex < classEntry.Spell4IdInnateAbilityPassive.Length)
            {
                uint innate = classEntry.Spell4IdInnateAbilityPassive[player.InnateIndex];
                uint prerequisite = player.InnateIndex < classEntry.PrerequisiteIdInnateAbility.Length ? classEntry.PrerequisiteIdInnateAbility[player.InnateIndex] : 0u;
                if (innate != 0u && GameTableManager.Instance.Spell4.GetEntry(innate) != null
                    && (prerequisite == 0u || NexusForever.WorldServer.Game.Prerequisite.PrerequisiteManager.Instance.Meets(player, prerequisite)))
                {
                    wanted.Add(innate);
                    if (!appliedAmpSpells.Contains(innate))
                        Diagnostics.FailureLedger.Record("innate-stance-applied", $"class {player.Class} index {player.InnateIndex}", $"spell {innate}");
                }
            }

            foreach (uint spell4Id in appliedAmpSpells.Where(id => !wanted.Contains(id)).ToList())
            {
                player.RemoveSpellEffects(spell4Id);
                appliedAmpSpells.Remove(spell4Id);
            }

            foreach (uint spell4Id in wanted.Where(id => !appliedAmpSpells.Contains(id)).ToList())
            {
                try
                {
                    player.CastSpell(spell4Id, new SpellParameters { UserInitiatedSpellCast = false, PrimaryTargetId = player.Guid });
                    appliedAmpSpells.Add(spell4Id);
                }
                catch (Exception exception)
                {
                    Diagnostics.FailureLedger.Record("amp-spell-failed", $"{exception.GetType().Name}: {exception.Message}", $"spell {spell4Id}");
                }
            }
        }

        public void SendInitialPackets()
        {
            SendServerAbilities();
            SendServerSpellList();
            SendServerAbilityPoints();
            SendServerActionSets();
            SendServerAmpLists();

            player.Session.EnqueueMessageEncrypted(new ServerCooldownList
            {
                Cooldowns = spellCooldowns.Select(c => new Cooldown
                {
                    Type          = 1,
                    SpellId       = c.Key,
                    TypeId        = c.Key,
                    TimeRemaining = (uint)(c.Value * 1000u)
                }).ToList()
            });
        }

        private void SendServerAbilities()
        {
            foreach (Item spell in player.Inventory
                .Where(b => b.Location == InventoryLocation.Ability)
                .SelectMany(s => s))
            {
                player.Session.EnqueueMessageEncrypted(new ServerItemAdd
                {
                    InventoryItem = new InventoryItem
                    {
                        Item   = spell.BuildNetworkItem(),
                        Reason = ItemUpdateReason.NoReason
                    }
                });
            }
        }

        private void SendServerSpellList()
        {
            var serverSpellList = new ServerSpellList();
            foreach ((uint spell4BaseId, CharacterSpell spell) in spells)
            {
                SpellBaseInfo spellBaseInfo = GlobalSpellManager.Instance.GetSpellBaseInfo(spell4BaseId);
                if (spellBaseInfo == null)
                    continue;
                
                for (byte i = 0; i < ActionSet.MaxActionSets; i++)
                {
                    ActionSetShortcut shortcut = actionSets[i].GetShortcut(ShortcutType.Spell, spell4BaseId);
                    serverSpellList.Spells.Add(new ServerSpellList.Spell
                    {
                        Spell4BaseId      = spell4BaseId,
                        TierIndexAchieved = shortcut?.Tier ?? spell.Tier,
                        SpecIndex         = i
                    });

                    // class ability
                    if (spellBaseInfo.SpellType.Id != 5)
                        break;
                }
            }

            player.Session.EnqueueMessageEncrypted(serverSpellList);
        }

        public void SendServerAbilityPoints()
        {
            player.Session.EnqueueMessageEncrypted(new ServerAbilityPoints
            {
                AbilityPoints      = actionSets[ActiveActionSet].TierPoints,
                TotalAbilityPoints = actionSets[ActiveActionSet].TierPointsTotal
            });
        }

        private void SendServerActionSets()
        {
            for (byte i = 0; i < ActionSet.MaxActionSets; i++)
            {
                ActionSet actionSet = GetActionSet(i);
                player.Session.EnqueueMessageEncrypted(actionSet.BuildServerActionSet());
            }
        }

        private void SendServerAmpLists()
        {
            for (byte i = 0; i < ActionSet.MaxActionSets; i++)
            {
                ActionSet actionSet = GetActionSet(i);
                player.Session.EnqueueMessageEncrypted(actionSet.BuildServerAmpList());
            }
        }
    }
}
