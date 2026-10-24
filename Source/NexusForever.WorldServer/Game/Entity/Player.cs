using System.Threading.Tasks;
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NexusForever.Database.Auth;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.Shared.Configuration;
using NexusForever.Shared.Database;
using NexusForever.Shared.Game;
using NexusForever.Shared.Game.Events;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.GameTable.Static;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Game.Achievement;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Entity.Network;
using NexusForever.WorldServer.Game.Entity.Network.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Guild;
using NexusForever.WorldServer.Game.Guild.Static;
using NexusForever.WorldServer.Game.Housing;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.RBAC.Static;
using NexusForever.WorldServer.Game.Reputation;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Game.Setting;
using NexusForever.WorldServer.Game.Setting.Static;
using NexusForever.WorldServer.Game.Social;
using NexusForever.WorldServer.Game.Social.Static;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;

namespace NexusForever.WorldServer.Game.Entity
{
    public class Player : UnitEntity, ISaveAuth, ISaveCharacter, ICharacter
    {
        private readonly static ILogger log = LogManager.GetCurrentClassLogger();

        // TODO: move this to the config file
        private const double SaveDuration = 60d;

        public ulong CharacterId { get; }
        public string Name { get; }
        public Sex Sex { get; }
        public Race Race { get; }
        public Class Class { get; }
        public Faction Faction { get; }
        public List<float> Bones { get; } = new();

        public CharacterFlag Flags
        {
            get => flags;
            set
            {
                flags = value;
                saveMask |= PlayerSaveMask.Flags;
            }
        }
        private CharacterFlag flags;

        public Path Path
        {
            get => path;
            set
            {
                path = value;
                PathActivatedTime = DateTime.UtcNow;
                saveMask |= PlayerSaveMask.Path;
            }
        }
        private Path path;

        public DateTime PathActivatedTime { get; private set; }

        public sbyte CostumeIndex
        {
            get => costumeIndex;
            set
            {
                costumeIndex = value;
                saveMask |= PlayerSaveMask.Costume;
            }
        }
        private sbyte costumeIndex;

        public InputSets InputKeySet
        {
            get => inputKeySet;
            set
            {
                inputKeySet = value;
                saveMask |= PlayerSaveMask.InputKeySet;
            }
        }
        private InputSets inputKeySet;

        public byte InnateIndex
        {
            get => innateIndex;
            set
            {
                innateIndex = value;
                saveMask |= PlayerSaveMask.Innate;
            }
        }
        private byte innateIndex;

        public DateTime CreateTime { get; }
        public double TimePlayedTotal { get; private set; }
        public double TimePlayedLevel { get; private set; }
        public double TimePlayedSession { get; private set; }

        /// <summary>
        /// Guid of the <see cref="WorldEntity"/> that currently being controlled by the <see cref="Player"/>.
        /// </summary>
        public uint ControlGuid { get; private set; }

        /// <summary>
        /// Guid of the <see cref="Vehicle"/> the <see cref="Player"/> is a passenger on.
        /// </summary>
        public uint VehicleGuid
        {
            get => MovementManager.GetPlatform() ?? 0u;
            set => MovementManager.SetPlatform(value);
        }

        /// <summary>
        /// Guid of the <see cref="VanityPet"/> currently summoned by the <see cref="Player"/>.
        /// </summary>
        public uint? VanityPetGuid { get; set; }

        /// <summary>Unit id of the Scientist scanbot following this player, if one was deployed.</summary>
        public uint? ScanbotGuid { get; set; }

        public bool IsSitting => currentChairGuid != null;

        /// <summary>Seconds since the class resource regeneration last ran (once per second, stat updates are sent to the client).</summary>
        public double ResourceRegenClock;

        /// <summary>Class resource pool: ResourceMax of the slot and its current value (Game/Spell/InnateCost.cs).</summary>
        public void SetClassResourcePool(uint slot, float max, float start)
        {
            if (slot > 6u)
                return;
            SetProperty((Property)((uint)Property.ResourceMax0 + slot), max, max);
            SetStat((Stat)((uint)Stat.Resource0 + slot), start);
        }
        private uint? currentChairGuid;

        /// <summary>
        /// Returns if <see cref="Player"/> has premium signature subscription.
        /// </summary>
        public bool SignatureEnabled => Session.AccountRbacManager.HasPermission(Permission.Signature);

        public WorldSession Session { get; }

        /// <summary>
        /// Returns if <see cref="Player"/>'s client is currently in a loading screen.
        /// </summary>
        public bool IsLoading { get; set; } = true;

        /// <summary>
        /// Returns a <see cref="float"/> representing decimal value, in days, since the character was last online. Used by <see cref="ICharacter"/>.
        /// </summary>
        /// <remarks>
        /// 0 is always returned for online players.
        /// </remarks>
        public float? GetOnlineStatus() => 0f;

        public Inventory Inventory { get; }
        public CurrencyManager CurrencyManager { get; }
        public PathManager PathManager { get; }
        public PathMissionManager PathMissionManager { get; }
        public Rewards.DailyLoginManager DailyLoginManager { get; }
        public RuneManager RuneManager { get; }
        public TitleManager TitleManager { get; }
        public SpellManager SpellManager { get; }
        public CostumeManager CostumeManager { get; }
        public PetCustomisationManager PetCustomisationManager { get; }
        public KeybindingManager KeybindingManager { get; }
        public DatacubeManager DatacubeManager { get; }
        public MailManager MailManager { get; }
        public ZoneMapManager ZoneMapManager { get; }
        public QuestManager QuestManager { get; }
        public CharacterAchievementManager AchievementManager { get; }
        public SupplySatchelManager SupplySatchelManager { get; }
        public Tradeskill.TradeskillManager TradeskillManager { get; }
        public Challenge.ChallengeManager ChallengeManager { get; }
        public Travel.TaxiManager TaxiManager { get; }
        public XpManager XpManager { get; }
        public ReputationManager ReputationManager { get; }
        public GuildManager GuildManager { get; }
        public ChatManager ChatManager { get; }
        public ResidenceManager ResidenceManager { get; }

        public VendorInfo SelectedVendorInfo { get; set; } // TODO unset this when too far away from vendor

        private UpdateTimer saveTimer = new(SaveDuration);
        private PlayerSaveMask saveMask;

        private LogoutManager logoutManager;

        /// <summary>
        /// Returns if <see cref="Player"/> can teleport.
        /// </summary>
        public bool CanTeleport() => pendingTeleport == null;
        private PendingTeleport pendingTeleport;

        /// <summary>
        /// Create a new <see cref="Player"/> from supplied <see cref="WorldSession"/> and <see cref="CharacterModel"/>.
        /// </summary>
        public Player(WorldSession session, CharacterModel model)
            : base(EntityType.Player)
        {
            ActivationRange = BaseMap.DefaultVisionRange;

            Session         = session;

            CharacterId     = model.Id;
            Name            = model.Name;
            Sex             = (Sex)model.Sex;
            Race            = (Race)model.Race;
            Class           = (Class)model.Class;
            path            = (Path)model.ActivePath;
            PathActivatedTime = model.PathActivatedTimestamp;
            CostumeIndex    = model.ActiveCostumeIndex;
            InputKeySet     = (InputSets)model.InputKeySet;
            Faction         = (Faction)model.FactionId;
            Faction1        = (Faction)model.FactionId;
            Faction2        = (Faction)model.FactionId;
            innateIndex     = model.InnateIndex;
            flags           = (CharacterFlag)model.Flags;

            CreateTime      = model.CreateTime;
            TimePlayedTotal = model.TimePlayedTotal;
            TimePlayedLevel = model.TimePlayedLevel;

            Session.EntitlementManager.Initialise(model);

            foreach (CharacterStatModel statModel in model.Stat)
                stats.Add((Stat)statModel.Stat, new StatValue(statModel));

            // managers
            CostumeManager          = new CostumeManager(this, session.Account, model);
            Inventory               = new Inventory(this, model);
            CurrencyManager         = new CurrencyManager(this, model);
            PathManager             = new PathManager(this, model);
            PathMissionManager      = new PathMissionManager(this);
            DailyLoginManager       = new Rewards.DailyLoginManager(this);
            RuneManager             = new RuneManager(this);
            TitleManager            = new TitleManager(this, model);
            SpellManager            = new SpellManager(this, model);
            PetCustomisationManager = new PetCustomisationManager(this, model);
            KeybindingManager       = new KeybindingManager(this, session.Account, model);
            DatacubeManager         = new DatacubeManager(this, model);
            MailManager             = new MailManager(this, model);
            ZoneMapManager          = new ZoneMapManager(this, model);
            QuestManager            = new QuestManager(this, model);
            AchievementManager      = new CharacterAchievementManager(this, model);
            SupplySatchelManager    = new SupplySatchelManager(this, model);
            TradeskillManager       = new Tradeskill.TradeskillManager(this, model);
            ChallengeManager        = new Challenge.ChallengeManager(this);
            TaxiManager             = new Travel.TaxiManager(this);
            XpManager               = new XpManager(this, model);
            ReputationManager       = new ReputationManager(this, model);
            GuildManager            = new GuildManager(this, model);
            ChatManager             = new ChatManager(this);
            ResidenceManager        = new ResidenceManager(this);

            // temp
            Properties.Add(Property.BaseHealth, new PropertyValue(Property.BaseHealth, 200f, 800f));
            Properties.Add(Property.ShieldCapacityMax, new PropertyValue(Property.ShieldCapacityMax, 0f, 450f));
            Properties.Add(Property.MoveSpeedMultiplier, new PropertyValue(Property.MoveSpeedMultiplier, 1f, 1f));
            Properties.Add(Property.JumpHeight, new PropertyValue(Property.JumpHeight, 2.5f, 2.5f));
            Properties.Add(Property.GravityMultiplier, new PropertyValue(Property.GravityMultiplier, 1f, 1f));
            // sprint
            Properties.Add(Property.ResourceMax0, new PropertyValue(Property.ResourceMax0, 500f, 500f));
            // dash
            Properties.Add(Property.ResourceMax7, new PropertyValue(Property.ResourceMax7, 200f, 200f));

            Costume costume = null;
            if (CostumeIndex >= 0)
                costume = CostumeManager.GetCostume((byte)CostumeIndex);

            SetAppearance(Inventory.GetItemVisuals(costume));
            SetAppearance(model.Appearance
                .Select(a => new ItemVisual
                {
                    Slot      = (ItemSlot)a.Slot,
                    DisplayId = a.DisplayId
                }));

            foreach (CharacterBoneModel bone in model.Bone.OrderBy(bone => bone.BoneIndex))
                Bones.Add(bone.Bone);

            SetStat(Stat.Sheathed, 1u);

            // temp
            SetStat(Stat.Dash, 200F);
            // sprint
            SetStat(Stat.Resource0, 500f);
            SetStat(Stat.Shield, 450u);
            ApplyLevelStats(false);
            Spell.InnateCost.InitialisePool(this);

            CharacterManager.Instance.RegisterPlayer(this);
        }

        private float[] combatStats = System.Array.Empty<float>();
        private float[] gearStats;
        private bool gearDirty = true;

        /// <summary>
        /// Stat values the spell formula and armor use: the CreatureLevel row of the level (assumption: players use the same level rows as
        /// creatures) plus the properties of the equipped items (<see cref="Combat.ItemStats"/>).
        /// </summary>
        public float[] CombatStats => combatStats.Length > 0 ? combatStats : Combat.UnitStats.LevelRow(Level);

        /// <summary>
        /// Sum of the properties of the equipped items, indexed by <see cref="Property"/>.
        /// </summary>
        public float[] GearStats => gearStats;

        /// <summary>
        /// Equipped items changed; stats are recalculated on the next update (never inside the inventory move itself).
        /// </summary>
        public void MarkGearDirty()
        {
            gearDirty = true;
            imbuementsDirty = true;
        }

        private bool imbuementsDirty = true;

        /// <summary>
        /// Base health, shield and combat stats from the CreatureLevel row of the character's level plus equipped items. Level up refills health
        /// and shield; equipping only clamps them to the new maximum.
        /// </summary>
        public void ApplyLevelStats(bool refill)
        {
            float[] row = Combat.UnitStats.LevelRow(Level);
            if (row.Length == 0)
                return;

            gearDirty = false;
            gearStats = SumGear();
            ApplySetBonuses();
            var stats = (float[])row.Clone();
            for (int i = 0; i < stats.Length && i < gearStats.Length; i++)
                stats[i] += gearStats[i];
            combatStats = stats;

            float health = Combat.UnitStats.Get(row, Property.BaseHealth) + gearStats[(int)Property.BaseHealth];
            float shield = Combat.UnitStats.Get(row, Property.ShieldCapacityMax) + gearStats[(int)Property.ShieldCapacityMax];
            if (health <= 0f)
                return;

            SetProperty(Property.BaseHealth, health, health);
            if (shield > 0f)
                SetProperty(Property.ShieldCapacityMax, shield, shield);

            // energy shield items set mitigation, regeneration and reboot time; without one the defaults apply (0 = default in the shield code)
            float mitigation = gearStats[(int)Property.ShieldMitigationMax];
            SetProperty(Property.ShieldMitigationMax, mitigation, mitigation);
            shieldRegen = gearStats[(int)Property.ShieldRegenPct] > 0f ? gearStats[(int)Property.ShieldRegenPct] : 0.25f;
            shieldReboot = gearStats[(int)Property.ShieldRebootTime] > 0f ? gearStats[(int)Property.ShieldRebootTime] : 2500f;

            if (refill || Health > health)
                SetStat(Stat.Health, (uint)health);

            if (shield > 0f && (refill || Shield > shield))
                SetStat(Stat.Shield, (uint)shield);
        }

        private float shieldRegen = 0.25f;
        private float shieldReboot = 2500f;

        private readonly List<uint> setBonusModifiers = new();
        private readonly HashSet<uint> setBonusSpells = new();
        private const uint SetBonusModifierBase = 0xF0000000u;

        /// <summary>
        /// Item set bonuses (docs/ASSUMPTIONS.md "Item sets", likely): every equipped item adds its ItemRuneInstance.ItemSetPower to its
        /// ItemSetId; each ItemSetBonus of the set whose RequiredPower is reached applies its UnitProperty2Id as a lasting property modifier
        /// value x Scalar (when set) + Offset, or its Spell4Id as a passive.
        /// </summary>
        private void ApplySetBonuses()
        {
            foreach (uint id in setBonusModifiers)
                RemoveSpellEffects(id);
            setBonusModifiers.Clear();

            var power = new Dictionary<uint, uint>();
            if (Inventory != null)
                foreach (Item item in Inventory.GetItems(InventoryLocation.Equipped))
                {
                    // the set power is on the rune items (982 runes carry ItemSetId/ItemSetPower, gear carries none)
                    foreach (var entry in RuneManager?.Installed(item) ?? Enumerable.Empty<NexusForever.Shared.GameTable.Model.Item2Entry>())
                    {
                        if (entry.ItemRuneInstanceId == 0u)
                            continue;
                        var rune = GameTableManager.Instance.ItemRuneInstance.GetEntry(entry.ItemRuneInstanceId);
                        if (rune == null || rune.ItemSetId == 0u || rune.ItemSetPower == 0u)
                            continue;
                        power[rune.ItemSetId] = power.GetValueOrDefault(rune.ItemSetId) + rune.ItemSetPower;
                    }
                }

            var spells = new HashSet<uint>();
            foreach ((uint setId, uint setPower) in power)
            {
                var set = GameTableManager.Instance.ItemSet.GetEntry(setId);
                if (set == null)
                    continue;
                foreach (uint bonusId in new[] { set.ItemSetBonusId00, set.ItemSetBonusId01, set.ItemSetBonusId02, set.ItemSetBonusId03, set.ItemSetBonusId04, set.ItemSetBonusId05,
                    set.ItemSetBonusId06, set.ItemSetBonusId07, set.ItemSetBonusId08, set.ItemSetBonusId09, set.ItemSetBonusId10, set.ItemSetBonusId11 })
                {
                    var bonus = bonusId != 0u ? GameTableManager.Instance.ItemSetBonus.GetEntry(bonusId) : null;
                    if (bonus == null || bonus.RequiredPower == 0u || bonus.RequiredPower > setPower)
                        continue;
                    if (bonus.UnitProperty2Id != 0u)
                    {
                        uint id = SetBonusModifierBase | bonus.Id;
                        AddPropertyModifier(new PropertyModifierEntry
                        {
                            Spell4Id = id,
                            Property = (Property)bonus.UnitProperty2Id,
                            Type     = 1u,
                            ValueA   = bonus.Scalar != 0f ? bonus.Scalar : 1f,
                            ValueB   = bonus.Offset,
                            Remaining = double.MaxValue
                        });
                        setBonusModifiers.Add(id);
                    }
                    if (bonus.Spell4Id != 0u && GameTableManager.Instance.Spell4.GetEntry(bonus.Spell4Id) != null)
                        spells.Add(bonus.Spell4Id);
                }
            }

            int specials = 0;
            foreach (uint spell in EquipSpecialSpells())
                if (spells.Add(spell))
                    specials++;
            if (specials > 0)
                Diagnostics.FailureLedger.Record("item-special-equip", "applied", $"{specials} on-equip spells");

            foreach (uint spell in setBonusSpells.Where(s => !spells.Contains(s)).ToList())
            {
                RemoveSpellEffects(spell);
                setBonusSpells.Remove(spell);
            }
            if (Map != null && !IsDead)
                foreach (uint spell in spells.Where(s => !setBonusSpells.Contains(s)).ToList())
                {
                    try
                    {
                        CastSpell(spell, new NexusForever.WorldServer.Game.Spell.SpellParameters { UserInitiatedSpellCast = false, PrimaryTargetId = Guid });
                        setBonusSpells.Add(spell);
                    }
                    catch (System.Exception exception)
                    {
                        Diagnostics.FailureLedger.Record("item-set-bonus", $"spell {spell} failed: {exception.GetType().Name}");
                    }
                }

            if (setBonusModifiers.Count > 0 || setBonusSpells.Count > 0)
                Diagnostics.FailureLedger.Record("item-set-bonus", "applied", $"{setBonusModifiers.Count} property bonuses, {setBonusSpells.Count} spells from {power.Count} sets");
        }

        /// <summary>Set bonus property modifiers and passives currently on the player (probe use).</summary>
        public int SetBonusCount => setBonusModifiers.Count + setBonusSpells.Count;

        internal bool ImbuementQuestDone(uint questId) => QuestManager?.GetQuestState((ushort)questId) == Quest.Static.QuestState.Completed;

        /// <summary>
        /// On-equip spells of equipped items: the item's own ItemSpecial (551 items carry one with Spell4IdOnEquip, the tooltip's "Special:" line)
        /// and type 1 imbuement rewards (an ItemSpecial granted by a finished imbuement step). ItemSpecial prerequisites are checked.
        /// </summary>
        private IEnumerable<uint> EquipSpecialSpells()
        {
            if (Inventory == null)
                yield break;
            foreach (Item item in Inventory.GetItems(InventoryLocation.Equipped))
            {
                var entry = item?.Info?.Entry;
                if (entry == null)
                    continue;
                var specials = new List<uint>();
                if (entry.ItemSpecialId00 != 0u)
                    specials.Add(entry.ItemSpecialId00);
                foreach (var reward in Quest.ItemImbuements.Earned(entry, ImbuementQuestDone))
                    if (reward.ItemImbuementRewardTypeEnum == Quest.ItemImbuements.RewardItemSpecial)
                        specials.Add(reward.RewardObjectId);
                foreach (uint id in specials)
                {
                    var special = GameTableManager.Instance.ItemSpecial.GetEntry(id);
                    if (special == null || special.Spell4IdOnEquip == 0u || GameTableManager.Instance.Spell4.GetEntry(special.Spell4IdOnEquip) == null)
                        continue;
                    if (special.PrerequisiteIdGeneric00 != 0u && !Prerequisite.PrerequisiteManager.Instance.Meets(this, special.PrerequisiteIdGeneric00))
                        continue;
                    yield return special.Spell4IdOnEquip;
                }
            }
        }

        private float[] SumGear()
        {
            var sum = new float[Combat.ItemStats.PropertyCount];
            if (Inventory == null)
                return sum;

            int equipped = 0;
            foreach (Item item in Inventory.GetItems(InventoryLocation.Equipped))
            {
                // an item above the character's level gives nothing: items cannot be equipped below Item2.RequiredLevel, so a worn one only
                // exists after the level went down (bot level rotation, GM level set); counting it gave level 1 bots ilvl 135 stats
                if (item?.Info?.Entry != null && item.Info.Entry.RequiredLevel > Level)
                {
                    Diagnostics.FailureLedger.Record("gear-above-level", "worn item above the character level gives no stats", $"item {item.Info.Entry.Id} requires {item.Info.Entry.RequiredLevel}, level {Level}");
                    continue;
                }

                // installed runes add their own item stats
                foreach (var rune in RuneManager?.Installed(item) ?? Enumerable.Empty<NexusForever.Shared.GameTable.Model.Item2Entry>())
                {
                    float[] runeValues = Combat.ItemStats.For(rune);
                    if (runeValues != null)
                        for (int i = 0; i < sum.Length; i++)
                            if (i != (int)Property.ShieldMitigationMax && i != (int)Property.ShieldRegenPct && i != (int)Property.ShieldRebootTime)
                                sum[i] += runeValues[i];
                }

                // imbuement steps of type 5 raise the level the item's stats are computed from
                float[] values = Combat.ItemStats.For(item?.Info?.Entry, Quest.ItemImbuements.Sum(item?.Info?.Entry, Quest.ItemImbuements.RewardItemLevel, ImbuementQuestDone));
                if (values == null)
                    continue;
                equipped++;
                // imbuement steps whose quest is done: type 0 adds float x the item's unrounded secondary rating sum to one property
                foreach (var reward in Quest.ItemImbuements.Earned(item.Info.Entry, ImbuementQuestDone))
                    if (reward.ItemImbuementRewardTypeEnum == Quest.ItemImbuements.RewardStat && reward.RewardObjectId < sum.Length)
                        sum[reward.RewardObjectId] += reward.RewardValueFloat * Combat.ItemStats.SecondaryRaw(item.Info.Entry);
                for (int i = 0; i < sum.Length; i++)
                {
                    // shield mitigation, regeneration and reboot are properties of the one shield, not sums
                    if (i == (int)Property.ShieldMitigationMax || i == (int)Property.ShieldRegenPct || i == (int)Property.ShieldRebootTime)
                    {
                        if (values[i] > 0f)
                            sum[i] = values[i];
                    }
                    else
                        sum[i] += values[i];
                }
            }

            if (equipped > 0)
                Diagnostics.FailureLedger.Record("gear-stats-applied", Session is Bots.BotSession ? "bot" : "human", $"{equipped} items, AR {sum[(int)Property.AssaultRating]} health {sum[(int)Property.BaseHealth]}");
            return sum;
        }

        private double shieldIdle;

        /// <summary>
        /// A standing shield takes its share of every hit (half by default, the shield mitigation property can change that) until it is empty; what
        /// the shield cannot hold goes to health. Returns the damage that reaches health. Hits also restart the regeneration delay.
        /// </summary>
        public uint AbsorbWithShield(uint amount, WorldEntity source = null)
        {
            if (!CombatConfig.Enabled)
                return amount;

            shieldIdle = 0d;
            float share = GetModifiedProperty(Property.ShieldMitigationMax, GetPropertyValue(Property.ShieldMitigationMax) ?? 0f);
            if (share <= 0f)
                share = 0.5f;

            uint shield = (uint)Shield;
            if (shield == 0u)
                return amount;

            uint absorbed = System.Math.Min(shield, (uint)System.Math.Round(amount * System.Math.Min(share, 1f)));
            SetStat(Stat.Shield, shield - absorbed);
            // proc triggers 114 "whenever your shields are damaged" and 115 "when your shields are penetrated"
            if (absorbed > 0u)
                FireProcs(114u, source);
            if (absorbed == shield)
                FireProcs(115u, source);
            return amount - absorbed;
        }

        /// <summary>
        /// Removes shield capacity directly (DamageShields), not restarting the regeneration delay.
        /// </summary>
        public void DrainShield(uint amount)
        {
            uint shield = (uint)Shield;
            if (amount == 0u || shield == 0u)
                return;

            SetStat(Stat.Shield, shield - System.Math.Min(shield, amount));
        }

        private double shieldTick;

        // out of the fight for the reboot time (default 2.5 s) the shield regains a quarter of its capacity per tick (default 500 ms)
        private void UpdateShield(double lastTick)
        {
            float max = GetPropertyValue(Property.ShieldCapacityMax) ?? 0f;
            if (!CombatConfig.Enabled || IsDead || max <= 0f || (uint)Shield >= (uint)max)
                return;

            shieldIdle += lastTick;
            if (shieldIdle < GetModifiedProperty(Property.ShieldRebootTime, shieldReboot) / 1000d)
                return;

            shieldTick += lastTick;
            double tick = GetModifiedProperty(Property.ShieldTickTime, 500f) / 1000d;
            if (shieldTick < tick)
                return;

            shieldTick = 0d;
            SetStat(Stat.Shield, (uint)System.Math.Min(max, (uint)Shield + max * GetModifiedProperty(Property.ShieldRegenPct, shieldRegen)));
        }

        public override void Update(double lastTick)
        {
            if (logoutManager != null)
            {
                // don't process world updates while logout is finalising
                if (logoutManager.ReadyToLogout)
                    return;

                logoutManager.Update(lastTick);
            }

            long t0 = Diagnostics.Profiler.Start();
            base.Update(lastTick);
            Spell.InnateCost.Regenerate(this, lastTick);
            Diagnostics.Profiler.Add("player.base", t0);
            t0 = Diagnostics.Profiler.Start();
            TitleManager.Update(lastTick);
            SpellManager.Update(lastTick);
            CostumeManager.Update(lastTick);
            Diagnostics.Profiler.Add("player.spells", t0);
            t0 = Diagnostics.Profiler.Start();
            QuestManager.Update(lastTick);
            PathMissionManager.Update(lastTick);
            ChallengeManager.Update(lastTick);
            Diagnostics.Profiler.Add("player.quests", t0);

            if (gearDirty)
            {
                try
                {
                    ApplyLevelStats(false);
                }
                catch (Exception ex)
                {
                    gearDirty = false;
                    Diagnostics.FailureLedger.Record("gear-stats-exception", ex.GetType().Name, ex.Message);
                }
            }

            if (imbuementsDirty && QuestManager != null)
            {
                imbuementsDirty = false;
                try
                {
                    QuestManager.CheckImbuements();
                }
                catch (Exception ex)
                {
                    Diagnostics.FailureLedger.Record("imbuement-exception", ex.GetType().Name, ex.Message);
                }
            }

            eventTimer -= lastTick;
            if (eventTimer <= 0d)
            {
                eventTimer = 2d;
                long te = Diagnostics.Profiler.Start();
                Events.PublicEventEngine.Instance.PlayerTick(this);
                Diagnostics.Profiler.Add("player.events", te);
            }

            if (CombatConfig.Enabled)
            {
                UpdateDeath(lastTick);
                UpdateShield(lastTick);
            }

            saveTimer.Update(lastTick);
            if (saveTimer.HasElapsed)
            {
                double timeSinceLastSave = GetTimeSinceLastSave();
                TimePlayedSession += timeSinceLastSave;
                TimePlayedLevel += timeSinceLastSave;
                TimePlayedTotal += timeSinceLastSave;

                Save();
            }
        }

        private double eventTimer;
        private double deathTimer;

        /// <summary>Set by a bot probe right before it kills the player on purpose (resurrection tests), so the death is not counted as a combat or environment death.</summary>
        public string ProbeDeathCause { get; set; }

        protected override void OnDeath(WorldEntity killer)
        {
            deathTimer = CombatConfig.PlayerAutoResurrectTime;
            casterRez = null;
            MovementManager.StopSpline();

            if (ProbeDeathCause == null)
                Diagnostics.FailureLedger.Record("combat-stats", $"player died ({(Session is Bots.BotSession ? "bot" : "human")})");
            // who killed whom (mortal bots): creature id, rank and level against the player's level and map
            string killerKey = ProbeDeathCause != null ? $"probe kill ({ProbeDeathCause})" : killer switch
            {
                NonPlayer npc => KillerName(npc),
                Player => "player",
                // the server has no fall, drowning or hazard damage: a null killer is a scripted Kill(null)
                null => "no killer (scripted kill)",
                _ => $"entity type {killer.Type}"
            };
            Diagnostics.FailureLedger.Record(ProbeDeathCause != null ? "player-death-probe" : "player-death", killerKey, $"{(Session is Bots.BotSession ? "bot" : "human")} L{Level} world {Map?.Entry?.Id ?? 0u}");
            ProbeDeathCause = null;
            ItemDurability.OnDeath(this);
            SendResurrectionShow();
        }

        private static string KillerName(NonPlayer npc)
        {
            Creature2Entry entry = GameTableManager.Instance.Creature2.GetEntry(npc.CreatureId);
            string name = entry == null || entry.LocalizedTextIdName == 0u ? "?" : GameTableManager.Instance.TextEnglish.GetEntry(entry.LocalizedTextIdName);
            return $"creature {npc.CreatureId} {name} rank {entry?.Creature2DifficultyId ?? 0u} L{npc.Level}";
        }

        /// <summary>A resurrection offered by a caster's Resurrect effect, waiting for the dead player to accept it.</summary>
        private sealed class CasterRez
        {
            public uint CasterGuid;
            public float Health;
            public float Focus;
        }

        private CasterRez casterRez;

        public bool HasCasterRezOffer => casterRez != null;

        /// <summary>
        /// Resurrection options the death screen offers (client death.lua reads them from ServerResurrectionShow flags): Wake Here always
        /// (cooldown in the message), Holocrypt when the world has a bind point spawn, Exit Instance inside a dungeon, the caster option
        /// while a Resurrect effect is waiting.
        /// </summary>
        public ResurrectionType ResurrectionOptions
        {
            get
            {
                ResurrectionType options = ResurrectionType.WakeHere;
                if (Map is DungeonMapInstance)
                    options |= ResurrectionType.ExitInstance;
                else if (Map != null && BindPoints.Nearest((ushort)Map.Entry.Id, Position) != null)
                    options |= ResurrectionType.Holocrypt;
                if (casterRez != null)
                    options |= ResurrectionType.SpellCasterLocation;
                return options;
            }
        }

        private void SendResurrectionShow()
        {
            Session.EnqueueMessageEncrypted(new ServerResurrectionShow
            {
                GhostId             = Guid,
                RezCost             = 0u,
                TimeUntilRezMs      = 0u,
                Dead                = true,
                ShowRezFlags        = ResurrectionOptions,
                HasCasterRezRequest = casterRez != null,
                TimeUntilWakeHereMs = (uint)System.Math.Max(0d, (WakeHereReadyAt - System.DateTime.UtcNow).TotalMilliseconds),
                TimeUntilForceRezMs = (uint)System.Math.Max(0d, deathTimer * 1000d)
            });
        }

        /// <summary>
        /// Resurrect effect on this dead player: the offer is shown on the death screen and taken with ClientResurrectAccept
        /// (RezType SpellCasterLocation). Health and focus are the effect's DataBits00/01 (docs/ASSUMPTIONS.md "Death and resurrection").
        /// </summary>
        public void OfferCasterResurrection(UnitEntity caster, float health, float focus)
        {
            if (!IsDead)
                return;

            casterRez = new CasterRez { CasterGuid = caster?.Guid ?? 0u, Health = health, Focus = focus };
            SendResurrectionShow();
        }

        /// <summary>
        /// The death screen choice (ClientResurrectAccept). Returns false when the option is not available, the player stays dead and the
        /// death timer still resurrects them in place.
        /// </summary>
        public bool ChooseResurrection(ResurrectionType type)
        {
            if (!IsDead)
                return false;

            switch (type)
            {
                case ResurrectionType.WakeHere:
                case ResurrectionType.WakeHereServiceToken:
                    return TryWakeHere();
                case ResurrectionType.SpellCasterLocation:
                {
                    if (casterRez == null)
                    {
                        Diagnostics.FailureLedger.Record("rez-rule", "caster resurrection refused: no offer");
                        return false;
                    }
                    CasterRez offer = casterRez;
                    casterRez = null;
                    Revive(RezHealth(offer.Health));
                    Diagnostics.FailureLedger.Record("rez-rule", "caster resurrection taken", $"health {offer.Health:0.##}");
                    return true;
                }
                case ResurrectionType.Holocrypt:
                {
                    System.Numerics.Vector3? spot = Map == null || Map is DungeonMapInstance ? null : BindPoints.Nearest((ushort)Map.Entry.Id, Position);
                    if (spot == null)
                    {
                        Diagnostics.FailureLedger.Record("rez-rule", BindPoints.Ready ? "holocrypt refused: no bind point in this world" : "holocrypt refused: bind point index still loading", $"world {Map?.Entry.Id}");
                        return false;
                    }
                    Resurrect();
                    TeleportTo((ushort)Map.Entry.Id, spot.Value.X, spot.Value.Y, spot.Value.Z);
                    Diagnostics.FailureLedger.Record("rez-rule", "holocrypt taken", $"world {Map.Entry.Id}");
                    return true;
                }
                case ResurrectionType.ExitInstance:
                {
                    MapPosition back = Map is DungeonMapInstance ? DungeonInstancedMap.GetReturn(CharacterId) : null;
                    if (back == null)
                    {
                        Diagnostics.FailureLedger.Record("rez-rule", "exit instance refused: not in an instance or no way back known", $"world {Map?.Entry.Id}");
                        return false;
                    }
                    Resurrect();
                    TeleportTo(back);
                    Diagnostics.FailureLedger.Record("rez-rule", "exit instance taken", $"world {Map.Entry.Id}");
                    return true;
                }
                default:
                    Diagnostics.FailureLedger.Record("rez-rule", "unknown resurrection type", $"{(uint)type}");
                    return false;
            }
        }

        /// <summary>A Resurrect effect health value: up to 1 a fraction of maximum health, above that a percentage capped at 100 (guess).</summary>
        private uint RezHealth(float value)
        {
            double fraction = value <= 0f ? CombatConfig.ResurrectHealth : value <= 1f ? value : System.Math.Min(100f, value) / 100d;
            return System.Math.Max(1u, (uint)(MaxHealth * fraction));
        }

        /// <summary>When the next "Wake Here" resurrection is allowed (not persisted).</summary>
        public System.DateTime WakeHereReadyAt { get; private set; }

        /// <summary>
        /// A resurrection the player asked for. Refused while "Wake Here" is on cooldown, the death timer then resurrects them as before.
        /// </summary>
        public bool TryWakeHere()
        {
            if (!IsDead)
                return false;

            if (System.DateTime.UtcNow < WakeHereReadyAt)
            {
                Diagnostics.FailureLedger.Record("rez-rule", "wake here refused: cooldown", $"{(WakeHereReadyAt - System.DateTime.UtcNow).TotalMinutes:0} min left");
                return false;
            }

            WakeHereReadyAt = System.DateTime.UtcNow.AddSeconds(CombatConfig.WakeHereCooldown);
            Resurrect();
            return true;
        }

        /// <summary>
        /// Resurrect the player where they died with half health.
        /// </summary>
        public void Resurrect()
        {
            if (!IsDead)
                return;

            casterRez = null;
            Revive(System.Math.Max(1u, (uint)(MaxHealth * CombatConfig.ResurrectHealth)));
        }

        protected override void OnRevive()
        {
            Diagnostics.FailureLedger.Record("combat-stats", $"player revived ({(Session is Bots.BotSession ? "bot" : "human")})");

            Session.EnqueueMessageEncrypted(new ServerResurrectionShow
            {
                GhostId = Guid,
                Dead    = false
            });
        }

        private void UpdateDeath(double lastTick)
        {
            if (IsLoading || Map == null)
                return;

            if (IsDead)
            {
                // nobody gets stuck on the death screen
                deathTimer -= lastTick;
                if (deathTimer <= 0d)
                    Resurrect();
                return;
            }

            // a character saved or created with no health (a death from before combat was enabled, say) comes back to life
            if (Health == 0u && MaxHealth > 0u)
                Revive(System.Math.Max(1u, (uint)(MaxHealth * CombatConfig.ResurrectHealth)));
        }

        /// <summary>
        /// Save <see cref="Account"/> and <see cref="ServerCharacterList.Character"/> to the database.
        /// </summary>
        public void Save(Action callback = null)
        {
            Task authSave = DatabaseManager.Instance.AuthDatabase.Save(Save);
            Session.Events.EnqueueEvent(new TaskEvent(authSave,
            () =>
            {
                ReportSaveFault("auth", authSave);
                Task characterSave = SaveCharacterSerialised();
                Session.Events.EnqueueEvent(new TaskEvent(characterSave,
                () =>
                {
                    if (characterSave.IsFaulted)
                        CharacterSave.RecordFailure("build", characterSave.Exception, CharacterId);
                    callback?.Invoke();
                    Session.CanProcessPackets = true;
                    saveTimer.Resume();
                }));
            }));

            saveTimer.Reset(false);

            // prevent packets from being processed until asynchronous player save task is complete
            Session.CanProcessPackets = false;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Threading.SemaphoreSlim> saveGates = new();

        /// <summary>
        /// Saves of one character (also across a relog) must not overlap: two Create rows or two deletes of the same row fail the whole batch.
        /// </summary>
        private async Task SaveCharacterSerialised()
        {
            CharacterDatabase database = DatabaseManager.Instance.CharacterDatabase;

            // both batches are built now, on the world thread: built after waiting for the gate they would read the game state from a pool thread.
            // Quests go in their own batch so that a bad quest row cannot lose the character, amps, currency and mail rows (one batch is one transaction).
            CharacterContext context = database.CreateContext();
            CharacterContext questContext = database.CreateContext();
            List<CharacterSave.QuestRow> resync = null;
            var traces = new Dictionary<ushort, string>();
            CharacterSave.MarkSaveStarted(CharacterId);
            CharacterSave.MarkSaveInFlight(CharacterId, true);
            try
            {
                try
                {
                    SaveCharacter(context);
                }
                catch (Exception exception)
                {
                    // the managers built before the exception have cleared their change flags: write what was built instead of dropping it
                    CharacterSave.RecordFailure("build-character", exception, CharacterId);
                }
                CharacterSave.AddInjectedFault(CharacterId, context);
                try
                {
                    if (QuestManager.ResyncPending)
                        resync = QuestManager.TakeResyncSnapshot();
                    else
                    {
                        QuestManager.Save(questContext);
                        foreach (ushort questId in questContext.ChangeTracker.Entries<CharacterQuestModel>().Select(e => e.Entity.QuestId)
                            .Concat(questContext.ChangeTracker.Entries<CharacterQuestObjectiveModel>().Select(e => e.Entity.QuestId)))
                            traces.TryAdd(questId, $"{QuestManager.GetTrace(questId)} [{QuestManager.DescribeQuest(questId)}]");
                    }
                }
                catch (Exception exception)
                {
                    // building the quest batch failed part way: what was built is incomplete, the next save rewrites all quests
                    CharacterSave.RecordFailure("quests", exception, CharacterId);
                    QuestManager.RequestResync();
                    await questContext.DisposeAsync();
                    questContext = database.CreateContext();
                }
            }
            catch
            {
                await context.DisposeAsync();
                await questContext.DisposeAsync();
                CharacterSave.MarkSaveInFlight(CharacterId, false);
                throw;
            }

            System.Threading.SemaphoreSlim gate = saveGates.GetOrAdd(CharacterId, _ => new System.Threading.SemaphoreSlim(1, 1));
            if (gate.CurrentCount == 0)
                Diagnostics.FailureLedger.Record("character-save-overlap", "a save waits for the previous save of the character", resync != null ? "quest resync" : "delta");
            await gate.WaitAsync();
            try
            {
                try
                {
                    await context.SaveChangesAsync();
                }
                catch (Exception exception)
                {
                    CharacterSave.RecordFailure("character", exception, CharacterId);
                    try
                    {
                        await CharacterSave.Replay(database, CharacterId, context);
                    }
                    catch (Exception replayException)
                    {
                        CharacterSave.RecordFailure("character-replay", replayException, CharacterId);
                    }
                }

                try
                {
                    if (resync != null)
                        await CharacterSave.Resync(database, CharacterId, resync);
                    else
                        await questContext.SaveChangesAsync();
                }
                catch (Exception exception)
                {
                    CharacterSave.RecordFailure(resync != null ? "quest-resync" : "quests", exception, CharacterId);
                    QuestManager.RequestResync();
                    if (resync == null)
                    {
                        try
                        {
                            await CharacterSave.Diagnose(database, CharacterId, questContext, traces);
                        }
                        catch (Exception diagnoseException)
                        {
                            Diagnostics.FailureLedger.Record("character-save-cause", "diagnosis failed", diagnoseException.Message);
                        }
                    }
                }
            }
            finally
            {
                await context.DisposeAsync();
                await questContext.DisposeAsync();
                CharacterSave.MarkSaveInFlight(CharacterId, false);
                gate.Release();
            }
        }

        /// <summary>
        /// A faulted save task is otherwise invisible (the dirty flags are already cleared, so the changes are lost).
        /// </summary>
        private static void ReportSaveFault(string database, Task task)
        {
            if (task.IsFaulted)
                Diagnostics.FailureLedger.Record("db-save-failed", $"{database}: {task.Exception?.GetBaseException().GetType().Name}",
                    (task.Exception?.GetBaseException().Message ?? "") + " || " + (task.Exception?.InnerException?.Message ?? "") + " || "
                    + " || entries: " + (task.Exception?.GetBaseException() is Microsoft.EntityFrameworkCore.DbUpdateException due
                        ? string.Join(",", due.Entries.Take(6).Select(e => e.Metadata.ClrType.Name + ":" + e.State)) + " inner=" + due.InnerException?.GetType().Name + ":" + due.InnerException?.Message
                        : "")
                    + " || " + string.Join(" | ", (task.Exception?.GetBaseException().StackTrace ?? "").Split('\n').Take(2).Select(l => l.Trim())));
        }

        public void Save(AuthContext context)
        {
            Session.AccountRbacManager.Save(context);
            Session.GenericUnlockManager.Save(context);
            Session.AccountCurrencyManager.Save(context);
            Session.EntitlementManager.Save(context);

            CostumeManager.Save(context);
            KeybindingManager.Save(context);
        }

        public void Save(CharacterContext context)
        {
            SaveCharacter(context);
            QuestManager.Save(context);
        }

        /// <summary>
        /// Everything of the character except the quests, which <see cref="SaveCharacterSerialised"/> saves in a batch of their own.
        /// </summary>
        private void SaveCharacter(CharacterContext context)
        {
            var model = new CharacterModel
            {
                Id = CharacterId
            };

            EntityEntry<CharacterModel> entity = context.Attach(model);

            if (saveMask != PlayerSaveMask.None)
            {
                if ((saveMask & PlayerSaveMask.Location) != 0 && Map?.Entry != null && Zone != null)
                {
                    model.LocationX = Position.X;
                    entity.Property(p => p.LocationX).IsModified = true;

                    model.LocationY = Position.Y;
                    entity.Property(p => p.LocationY).IsModified = true;

                    model.LocationZ = Position.Z;
                    entity.Property(p => p.LocationZ).IsModified = true;

                    model.RotationX = Rotation.X;
                    entity.Property(p => p.RotationX).IsModified = true;

                    model.RotationY = Rotation.Y;
                    entity.Property(p => p.RotationY).IsModified = true;

                    model.RotationZ = Rotation.Z;
                    entity.Property(p => p.RotationZ).IsModified = true;

                    model.WorldId = (ushort)Map.Entry.Id;
                    entity.Property(p => p.WorldId).IsModified = true;

                    model.WorldZoneId = (ushort)Zone.Id;
                    entity.Property(p => p.WorldZoneId).IsModified = true;
                }

                if ((saveMask & PlayerSaveMask.Path) != 0)
                {
                    model.ActivePath = (uint)Path;
                    entity.Property(p => p.ActivePath).IsModified = true;
                    model.PathActivatedTimestamp = PathActivatedTime;
                    entity.Property(p => p.PathActivatedTimestamp).IsModified = true;
                }

                if ((saveMask & PlayerSaveMask.Costume) != 0)
                {
                    model.ActiveCostumeIndex = CostumeIndex;
                    entity.Property(p => p.ActiveCostumeIndex).IsModified = true;
                }

                if ((saveMask & PlayerSaveMask.InputKeySet) != 0)
                {
                    model.InputKeySet = (sbyte)InputKeySet;
                    entity.Property(p => p.InputKeySet).IsModified = true;
                }

                if ((saveMask & PlayerSaveMask.Flags) != 0)
                {
                    model.Flags = (uint)Flags;
                    entity.Property(p => p.Flags).IsModified = true;
                }

                if ((saveMask & PlayerSaveMask.Innate) != 0)
                {
                    model.InnateIndex = InnateIndex;
                    entity.Property(p => p.InnateIndex).IsModified = true;
                }

                saveMask = PlayerSaveMask.None;
            }

            model.TimePlayedLevel = (uint)TimePlayedLevel;
            entity.Property(p => p.TimePlayedLevel).IsModified = true;
            model.TimePlayedTotal = (uint)TimePlayedTotal;
            entity.Property(p => p.TimePlayedTotal).IsModified = true;
            model.LastOnline = DateTime.UtcNow;
            entity.Property(p => p.LastOnline).IsModified = true;

            foreach (StatValue stat in stats.Values)
                stat.SaveCharacter(CharacterId, context);

            Inventory.Save(context);
            CurrencyManager.Save(context);
            PathManager.Save(context);
            TitleManager.Save(context);
            CostumeManager.Save(context);
            PetCustomisationManager.Save(context);
            KeybindingManager.Save(context);
            SpellManager.Save(context);
            DatacubeManager.Save(context);
            MailManager.Save(context);
            ZoneMapManager.Save(context);
            AchievementManager.Save(context);
            SupplySatchelManager.Save(context);
            TradeskillManager.Save(context);
            XpManager.Save(context);
            ReputationManager.Save(context);
            GuildManager.Save(context);

            Session.EntitlementManager.Save(context);
        }

        protected override IEntityModel BuildEntityModel()
        {
            return new PlayerEntityModel
            {
                Id        = CharacterId,
                RealmId   = WorldServer.RealmId,
                Name      = Name,
                Race      = Race,
                Class     = Class,
                Sex       = Sex,
                Bones     = Bones,
                Title     = TitleManager.ActiveTitleId,
                GuildIds  = GuildManager
                    .Select(g => g.Id)
                    .ToList(),
                GuildName = GuildManager.GuildAffiliation?.Name,
                GuildType = GuildManager.GuildAffiliation?.Type ?? GuildType.None,
                PvPFlag   = PvPFlag.Disabled
            };
        }

        public override void OnAddToMap(BaseMap map, uint guid, Vector3 vector)
        {
            IsLoading = true;

            Session.EnqueueMessageEncrypted(new ServerChangeWorld
            {
                WorldId  = (ushort)map.Entry.Id,
                Position = new Position(vector),
                Yaw      = Rotation.X
            });

            // this must come before OnAddToMap
            // the client UI initialises the Holomark checkboxes during OnDocumentReady
            SendCharacterFlagsUpdated();

            base.OnAddToMap(map, guid, vector);

            // resummon vanity pet if it existed before teleport
            if (pendingTeleport?.VanityPetId != null)
            {
                var vanityPet = new VanityPet(this, pendingTeleport.VanityPetId.Value);

                var position = new MapPosition
                {
                    Position = Position
                };

                if (map.CanEnter(vanityPet, position))
                    map.EnqueueAdd(vanityPet, position);
            }

            pendingTeleport = null;

            // the arrival point is explored like a step there (a client still loading is skipped inside, its first move counts)
            ZoneMapManager.OnRelocate(vector);

            SendPacketsAfterAddToMap();
            if (PreviousMap == null)
                OnLogin();
        }

        public override void OnRelocate(Vector3 vector)
        {
            base.OnRelocate(vector);
            saveMask |= PlayerSaveMask.Location;

            ZoneMapManager.OnRelocate(vector);
        }

        protected override void OnZoneUpdate()
        {
            if (Zone != null)
            {
                TextTable tt = GameTableManager.Instance.GetTextTable(Language.English);
                if (tt != null)
                {
                    GlobalChatManager.Instance.SendMessage(Session, $"New Zone: ({Zone.Id}){tt.GetEntry(Zone.LocalizedTextIdName)}");
                }

                uint tutorialId = AssetManager.Instance.GetTutorialIdForZone(Zone.Id);
                if (tutorialId > 0)
                {
                    Session.EnqueueMessageEncrypted(new ServerTutorial
                    {
                        TutorialId = tutorialId
                    });
                }

                // a sub zone lies inside its parent zones: 119 of the 199 EnterZone objectives name a parent zone (WorldZone.parentZoneId)
                foreach (uint zoneId in ZoneAndParents())
                    QuestManager.ObjectiveUpdate(QuestObjectiveType.EnterZone, zoneId, 1);
            }

            ZoneMapManager.OnZoneUpdate();
        }

        /// <summary>
        /// The current zone and the zones that contain it (WorldZone.ParentZoneId chain), innermost first.
        /// </summary>
        public IEnumerable<uint> ZoneAndParents()
        {
            WorldZoneEntry zone = Zone;
            for (int depth = 0; zone != null && depth < 8; depth++)
            {
                yield return zone.Id;
                zone = zone.ParentZoneId != 0u ? GameTableManager.Instance.WorldZone.GetEntry(zone.ParentZoneId) : null;
            }
        }

        private void SendPacketsAfterAddToMap()
        {
            DateTime start = DateTime.UtcNow;

            SendInGameTime();
            PathManager.SendInitialPackets();
            BuybackManager.Instance.SendBuybackItems(this);

            ResidenceManager.SendHousingBasics();
            Session.EnqueueMessageEncrypted(new ServerHousingNeighbors());
            Session.EnqueueMessageEncrypted(new ServerInstanceSettings());

            SetControl(this);

            CostumeManager.SendInitialPackets();

            var playerCreate = new ServerPlayerCreate
            {
                ItemProficiencies = GetItemProficiencies(),
                FactionData       = new ServerPlayerCreate.Faction
                {
                    FactionId          = Faction1, // This does not do anything for the player's "main" faction. Exiles/Dominion
                    FactionReputations = ReputationManager
                        .Select(r => new ServerPlayerCreate.Faction.FactionReputation
                        {
                            FactionId = r.Id,
                            Value     = r.Amount
                        })
                        .ToList()
                },
                ActiveCostumeIndex    = CostumeIndex,
                InputKeySet           = (uint)InputKeySet,
                CharacterEntitlements = Session.EntitlementManager.GetCharacterEntitlements()
                    .Select(e => new ServerPlayerCreate.CharacterEntitlement
                    {
                        Entitlement = e.Type,
                        Count       = e.Amount
                    })
                    .ToList(),
                TradeskillMaterials   = SupplySatchelManager.BuildNetworkPacket(),
                Xp                    = XpManager.TotalXp,
                RestBonusXp           = XpManager.RestBonusXp
            };

            foreach (Currency currency in CurrencyManager)
                playerCreate.Money[(byte)currency.Id - 1] = currency.Amount;

            foreach (Item item in Inventory
                .Where(b => b.Location != InventoryLocation.Ability)
                .SelectMany(i => i))
            {
                playerCreate.Inventory.Add(new InventoryItem
                {
                    Item   = item.BuildNetworkItem(),
                    Reason = ItemUpdateReason.NoReason
                });
            }

            playerCreate.SpecIndex = SpellManager.ActiveActionSet;
            Session.EnqueueMessageEncrypted(playerCreate);

            TitleManager.SendTitles();
            SpellManager.SendInitialPackets();
            PetCustomisationManager.SendInitialPackets();
            KeybindingManager.SendInitialPackets();
            DatacubeManager.SendInitialPackets();
            MailManager.SendInitialPackets();
            ZoneMapManager.SendInitialPackets();
            Session.AccountCurrencyManager.SendInitialPackets();
            QuestManager.SendInitialPackets();
            AchievementManager.SendInitialPackets(null);
            TradeskillManager.SendProfessionsLoad();
            Session.EntitlementManager.SendInitialPackets();

            Session.EnqueueMessageEncrypted(new ServerPlayerInnate
            {
                InnateIndex = InnateIndex
            });

            log.Trace($"Player {Name} took {(DateTime.UtcNow - start).TotalMilliseconds}ms to send packets after add to map.");
        }

        public ItemProficiency GetItemProficiencies()
        {
            //TODO: Store proficiencies in DB table and load from there. Do they change ever after creation? Perhaps something for use on custom servers?
            ClassEntry classEntry = GameTableManager.Instance.Class.GetEntry((ulong)Class);
            return (ItemProficiency)classEntry.StartingItemProficiencies;
        }

        public override void OnRemoveFromMap()
        {
            DestroyDependents();
            base.OnRemoveFromMap();
        }

        public override void AddVisible(GridEntity entity)
        {
            base.AddVisible(entity);
            Session.EnqueueMessageEncrypted(((WorldEntity)entity).BuildCreatePacket());

            if (entity is Player player)
                player.PathManager.SendSetUnitPathTypePacket();

            if (entity == this)
            {
                Session.EnqueueMessageEncrypted(new ServerPlayerChanged
                {
                    Guid     = entity.Guid,
                    Unknown1 = 1
                });
            }
        }

        public override void RemoveVisible(GridEntity entity)
        {
            base.RemoveVisible(entity);

            if (entity != this)
            {
                Session.EnqueueMessageEncrypted(new ServerEntityDestroy
                {
                    Guid     = entity.Guid,
                    Unknown0 = true
                });
            }
        }

        /// <summary>
        /// Set the <see cref="WorldEntity"/> that currently being controlled by the <see cref="Player"/>.
        /// </summary>
        public void SetControl(WorldEntity entity)
        {
            ControlGuid = entity.Guid;
            entity.ControllerGuid = Guid;

            Session.EnqueueMessageEncrypted(new ServerMovementControl
            {
                Ticket    = 1,
                Immediate = true,
                UnitId    = entity.Guid
            });
        }

        /// <summary>
        /// Start delayed logout with optional supplied time and <see cref="LogoutReason"/>.
        /// </summary>
        public void LogoutStart(double timeToLogout = 30d, LogoutReason reason = LogoutReason.None, bool requested = true)
        {
            if (logoutManager != null)
                return;

            logoutManager = new LogoutManager(timeToLogout, reason, requested);

            Session.EnqueueMessageEncrypted(new ServerLogoutUpdate
            {
                TimeTillLogout     = (uint)timeToLogout * 1000,
                Unknown0           = false,
                SignatureBonusData = new ServerLogoutUpdate.SignatureBonuses
                {
                    // see FillSignatureBonuses in ExitWindow.lua for more information
                    Xp                = 0,
                    ElderPoints       = 0,
                    Currencies        = new ulong[15],
                    AccountCurrencies = new ulong[19]
                }
            });
        }

        /// <summary>
        /// Cancel the current logout, this will fail if the timer has already elapsed.
        /// </summary>
        public void LogoutCancel()
        {
            // can't cancel logout if timer has already elapsed
            if (logoutManager?.ReadyToLogout ?? false)
                return;

            logoutManager = null;
        }

        /// <summary>
        /// Finishes the current logout, saving and cleaning up the <see cref="Player"/> before redirect to the character screen.
        /// </summary>
        public void LogoutFinish()
        {
            if (logoutManager == null)
                throw new InvalidPacketValueException();

            Session.EnqueueMessageEncrypted(new ServerLogout
            {
                Requested = logoutManager.Requested,
                Reason    = logoutManager.Reason
            });

            CleanUp();
        }

        /// <summary>
        /// Save to the database, remove from the world and release from parent <see cref="WorldSession"/>.
        /// </summary>
        public void CleanUp()
        {
            GroupManager.Instance.OnLogout(this);
            Market.TradeManager.Instance.Cancel(this, P2PTradeResult.PlayerLogOut);
            CharacterManager.Instance.DeregisterPlayer(this);
            PlayerCleanupManager.Track(Session.Account);

            log.Trace($"Attempting to start cleanup for character {Name}({CharacterId})...");

            Session.Events.EnqueueEvent(new TimeoutPredicateEvent(TimeSpan.FromSeconds(15), CanCleanup,
                () =>
            {
                // the account stays tracked until the save has finished: a character selected before that would be loaded from the old rows
                bool saveStarted = false;
                try
                {
                    log.Trace($"Cleanup for character {Name}({CharacterId}) has started...");

                    OnLogout();

                    Save(() =>
                    {
                        if (Map != null)
                            RemoveFromMap();

                        Session.Player = null;
                        PlayerCleanupManager.Untrack(Session.Account);
                        log.Trace($"Cleanup for character {Name}({CharacterId}) has completed.");
                    });
                    saveStarted = true;
                }
                finally
                {
                    if (!saveStarted)
                        PlayerCleanupManager.Untrack(Session.Account);
                }
            }));
        }

        private bool CanCleanup()
        {
            return pendingTeleport == null;
        }

        private void OnLogin()
        {
            string motd = WorldServer.RealmMotd;
            if (motd?.Length > 0)
                GlobalChatManager.Instance.SendMessage(Session, motd, "MOTD", ChatChannelType.Realm);

            GuildManager.OnLogin();
            ChatManager.OnLogin();
            Game.Social.FriendManager.Instance.SendList(this);
            PathMissionManager?.SendMissions();
        }

        private void OnLogout()
        {
            GuildManager.OnLogout();
            ChatManager.OnLogout();
        }

        /// <summary>
        /// Teleport <see cref="Player"/> to supplied location.
        /// </summary>
        public void TeleportTo(ushort worldId, float x, float y, float z, ulong? instanceId = null, TeleportReason reason = TeleportReason.Relocate)
        {
            WorldEntry entry = GameTableManager.Instance.World.GetEntry(worldId);
            if (entry == null)
                throw new ArgumentException($"{worldId} is not a valid world id!");

            TeleportTo(entry, new Vector3(x, y, z), instanceId, reason);
        }

        /// <summary>
        /// Teleport <see cref="Player"/> to supplied location.
        /// </summary>
        public void TeleportTo(WorldEntry entry, Vector3 position, ulong? instanceId = null, TeleportReason reason = TeleportReason.Relocate)
        {
            TeleportTo(new MapPosition
            {
                Info     = new MapInfo
                {
                    Entry      = entry,
                    InstanceId = instanceId
                },
                Position = position
            }, reason);
        }

        /// <summary>
        /// Teleport <see cref="Player"/> to supplied location.
        /// </summary>
        public void TeleportTo(MapPosition mapPosition, TeleportReason reason = TeleportReason.Relocate)
        {
            if (!CanTeleport())
            {
                SendGenericError(GenericError.InstanceTransferPending);
                return;
            }

            if (DisableManager.Instance.IsDisabled(DisableType.World, mapPosition.Info.Entry.Id))
            {
                SendSystemMessage($"Unable to teleport to world {mapPosition.Info.Entry.Id} because it is disabled.");
                return;
            }

            // bots: who sends them to another world (a world change is rare, the stack capture is cheap there); first non-teleport frames
            if (Session is Bots.BotSession && Map != null && mapPosition.Info.Entry.Id != Map.Entry.Id)
            {
                string caller = string.Join(" < ", new System.Diagnostics.StackTrace(1, false).GetFrames()
                    .Select(f => f.GetMethod()).Where(m => m != null && m.Name != "TeleportTo").Take(3).Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
                Diagnostics.FailureLedger.Record("bot-world-change", caller, $"world {Map.Entry.Id} to {mapPosition.Info.Entry.Id}");
            }

            // store vanity pet summoned before teleport so it can be summoned again after being added to the new map
            uint? vanityPetId = null;
            if (VanityPetGuid != null)
            {
                VanityPet pet = GetVisible<VanityPet>(VanityPetGuid.Value);
                vanityPetId = pet?.Creature.Id;
            }

            pendingTeleport = new PendingTeleport
            {
                Reason      = reason,
                MapPosition = mapPosition,
                VanityPetId = vanityPetId
            };

            MapManager.Instance.AddToMap(this, mapPosition);
            log.Trace($"Teleporting {Name}({CharacterId}) to map: {mapPosition.Info.Entry.Id}, instance: {mapPosition.Info.InstanceId ?? 0ul}.");
        }

        /// <summary>
        /// Invoked when <see cref="Player"/> teleport fails.
        /// </summary>
        public void OnTeleportToFailed(GenericError error)
        {
            if (Map != null)
            {
                SendGenericError(error);
                pendingTeleport = null;

                log.Trace($"Error {error} occured during teleport for {Name}({CharacterId})!");
            }
            else
            {
                // player failed prerequisites to enter map on login
                // can not proceed, disconnect the client with a message
                Session.EnqueueMessageEncrypted(new ServerForceKick
                {
                    Reason = ForceKickReason.WorldDisconnect
                });

                log.Trace($"Error {error} occured during teleport for {Name}({CharacterId}), client will be disconnected!");
            }
        }

        /// <summary>
        /// Used to send the current in game time to this player
        /// </summary>
        private void SendInGameTime()
        {
            uint lengthOfInGameDayInSeconds = ConfigurationManager<WorldServerConfiguration>.Instance.Config.LengthOfInGameDay;
            if (lengthOfInGameDayInSeconds == 0u)
                lengthOfInGameDayInSeconds = (uint)TimeSpan.FromHours(3.5d).TotalSeconds; // Live servers were 3.5h per in game day

            double timeOfDay = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds / lengthOfInGameDayInSeconds % 1;

            Session.EnqueueMessageEncrypted(new ServerTimeOfDay
            {
                TimeOfDay = (uint)(timeOfDay * TimeSpan.FromDays(1).TotalSeconds),
                LengthOfDay = lengthOfInGameDayInSeconds
            });
        }

        /// <summary>
        /// Reset and restore default appearance for <see cref="Player"/>.
        /// </summary>
        public void ResetAppearance()
        {
            DisplayInfo = 0;

            EnqueueToVisible(new ServerEntityVisualUpdate
            {
                UnitId      = Guid,
                Race        = (byte)Race,
                Sex         = (byte)Sex,
                ItemVisuals = GetAppearance().ToList()
            }, true);
        }

        /// <summary>
        /// Make <see cref="Player"/> sit on provided <see cref="WorldEntity"/>.
        /// </summary>
        public void Sit(WorldEntity chair)
        {
            if (IsSitting)
                Unsit();

            currentChairGuid = chair.Guid;

            // TODO: Emit interactive state from the entity instance itself
            chair.EnqueueToVisible(new ServerEntityInteractiveUpdate
            {
                UnitId = chair.Guid,
                InUse  = true
            }, true);
            EnqueueToVisible(new ServerUnitSetChair
            {
                UnitId      = Guid,
                UnitIdChair = chair.Guid,
                WaitForUnit = false
            }, true);
        }

        /// <summary>
        /// Remove <see cref="Player"/> from the <see cref="WorldEntity"/> it is sitting on.
        /// </summary>
        public void Unsit()
        {
            if (!IsSitting)
                return;

            WorldEntity currentChair = GetVisible<WorldEntity>(currentChairGuid.Value);
            if (currentChair == null)
                throw new InvalidOperationException();

            // TODO: Emit interactive state from the entity instance itself
            currentChair.EnqueueToVisible(new ServerEntityInteractiveUpdate
            {
                UnitId = currentChair.Guid,
                InUse  = false
            }, true);
            EnqueueToVisible(new ServerUnitSetChair
            {
                UnitId      = Guid,
                UnitIdChair = 0,
                WaitForUnit = false
            }, true);

            currentChairGuid = null;
        }

        /// <summary>
        /// Shortcut method to grant XP to the player
        /// </summary>
        public void GrantXp(uint xp, ExpReason reason = ExpReason.Cheat)
        {
            XpManager.GrantXp(xp, reason);
        }

        /// <summary>
        /// Send <see cref="GenericError"/> to <see cref="Player"/>.
        /// </summary>
        public void SendGenericError(GenericError error)
        {
            Session.EnqueueMessageEncrypted(new ServerGenericError
            {
                Error = error
            });
        }

        /// <summary>
        /// Send message to <see cref="Player"/> using the <see cref="ChatChannel.System"/> channel.
        /// </summary>
        /// <param name="text"></param>
        public void SendSystemMessage(string text)
        {
            Session.EnqueueMessageEncrypted(new ServerChat
            {
                Channel = new Channel
                {
                    Type = ChatChannelType.System
                },
                Text    = text
            });
        }

        /// <summary>
        /// Returns whether this <see cref="Player"/> is allowed to summon or be added to a mount
        /// </summary>
        /// <summary>
        /// Time this player last became a vehicle passenger.
        /// </summary>
        public DateTime LastMountedAt { get; set; }

        public bool CanMount()
        {
            return VehicleGuid == 0u && pendingTeleport == null && logoutManager == null;
        }

        /// <summary>
        /// Dismounts this <see cref="Player"/> from a vehicle that it's attached to
        /// </summary>
        public void Dismount()
        {
            if (VehicleGuid != 0u)
            {
                Vehicle vehicle = GetVisible<Vehicle>(VehicleGuid);
                if (vehicle == null || vehicle.GetPassenger(Guid) == null)
                {
                    // the vehicle left the map (or this player's grid) without taking its passenger along
                    Diagnostics.FailureLedger.Record("dismount-vehicle-gone", "vehicle not visible or passenger unknown", $"vehicle {VehicleGuid}");
                    VehicleGuid = 0u;
                    return;
                }

                vehicle.PassengerRemove(this);
            }
        }

        /// <summary>
        /// Remove all entities associated with the <see cref="Player"/>
        /// </summary>
        private void DestroyDependents()
        {
            // vehicle will be removed if player is the last passenger
            if (VehicleGuid != 0u)
                Dismount();

            if (VanityPetGuid != null)
            {
                VanityPet pet = GetVisible<VanityPet>(VanityPetGuid.Value);
                pet?.RemoveFromMap();
                VanityPetGuid = null;
            }

            // TODO: Remove pets, scanbots
        }

        /// <summary>
        /// Returns the time in seconds that has past since the last <see cref="Player"/> save.
        /// </summary>
        public double GetTimeSinceLastSave()
        {
            return SaveDuration - saveTimer.Time;
        }

        /// <summary>
        /// Return <see cref="Disposition"/> between <see cref="Player"/> and <see cref="Faction"/>.
        /// </summary>
        public override Disposition GetDispositionTo(Faction factionId, bool primary = true)
        {
            // faction 0 means none: neutral without a ledger entry (356,000 per soak)
            if ((uint)factionId == 0u)
                return Disposition.Neutral;

            FactionNode targetFaction = FactionManager.Instance.GetFaction(factionId);
            if (targetFaction == null)
            {
                // creatures and spell targets without a faction (id 0) are neutral; throwing here dropped the spell that asked
                Diagnostics.FailureLedger.Record("faction-missing", $"faction {factionId} not in the tables, treated as neutral");
                return Disposition.Neutral;
            }

            // find disposition based on reputation level
            Disposition? dispositionFromReputation = GetDispositionFromReputation(targetFaction);
            if (dispositionFromReputation.HasValue)
                return dispositionFromReputation.Value;

            return base.GetDispositionTo(factionId, primary);
        }

        private Disposition? GetDispositionFromReputation(FactionNode node)
        {
            if (node == null)
                return null;

            // check if current node has required reputation
            Reputation.Reputation reputation = ReputationManager.GetReputation(node.FactionId);
            if (reputation != null)
                return FactionNode.GetDisposition(FactionNode.GetFactionLevel(reputation.Amount));

            // check if parent node has required reputation
            return GetDispositionFromReputation(node.Parent);
        }

        /// <summary>
        /// Add a new <see cref="CharacterFlag"/>.
        /// </summary>
        public void SetFlag(CharacterFlag flag)
        {
            Flags |= flag;
            SendCharacterFlagsUpdated();
        }

        /// <summary>
        /// Remove an existing <see cref="CharacterFlag"/>.
        /// </summary>
        public void RemoveFlag(CharacterFlag flag)
        {
            Flags &= ~flag;
            SendCharacterFlagsUpdated();
        }

        /// <summary>
        /// Returns if supplied <see cref="CharacterFlag"/> exists.
        /// </summary>
        public bool HasFlag(CharacterFlag flag)
        {
            return (Flags & flag) != 0;
        }

        /// <summary>
        /// Send <see cref="ServerCharacterFlagsUpdated"/> to client.
        /// </summary>
        public void SendCharacterFlagsUpdated()
        {
            Session.EnqueueMessageEncrypted(new ServerCharacterFlagsUpdated
            {
                Flags = flags
            });
        }
    }
}
