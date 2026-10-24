using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Game.Entity;

namespace NexusForever.WorldServer.Game.Map
{
    public enum InstanceDifficulty
    {
        Normal,
        Veteran
    }

    public class DungeonMapInstance : MapInstance
    {
        /// <summary>
        /// Veteran instances: NF_VETERAN_WORLDS (world ids, or "all") forces veteran per world, otherwise the owner's choice from
        /// ClientSetInstanceSettings (<see cref="InstanceSettings"/>). Veteran rows replace the normal creatures (<see cref="VeteranVariants"/>);
        /// creatures without a veteran row are scaled in NonPlayer.OnAddToMap (docs/ASSUMPTIONS.md "Veteran mode").
        /// </summary>
        public InstanceDifficulty Difficulty { get; private set; }

        /// <summary>Character the instance was created for: the group leader at creation, or the lone character (raid progress is saved under it).</summary>
        public ulong OwnerCharacterId { get; set; }

        /// <summary>Spawn ids of the encounter bosses killed in this instance.</summary>
        private readonly System.Collections.Generic.HashSet<uint> killedBosses = new();

        public int KilledBossCount => killedBosses.Count;

        private bool IsRaid => Entry.Type == (uint)Static.MapType.Raid;

        /// <summary>An encounter boss of this instance died: progress is counted, a raid saves it for the owner's lockout period.</summary>
        public void OnBossKilled(NonPlayer boss)
        {
            if (!killedBosses.Add(boss.EntityId))
                return;
            Diagnostics.FailureLedger.Record("instance-boss-progress", $"world {Entry.Id} {Difficulty}", $"{killedBosses.Count} bosses killed, last creature {boss.CreatureId}");
            if (IsRaid)
                Groups.InstanceLockouts.SaveRaidBoss(OwnerCharacterId, (ushort)Entry.Id, boss.EntityId);
        }

        public const float VeteranHealthScale = 1.5f;
        public const float VeteranDamageScale = 1.25f;

        public override void Initialise(NexusForever.Shared.GameTable.Model.WorldEntry entry)
        {
            base.Initialise(entry);
            SetUnloadDelay(120d);

            string worlds = System.Environment.GetEnvironmentVariable("NF_VETERAN_WORLDS") ?? "";
            if (worlds == "all" || worlds.Split(',', System.StringSplitOptions.RemoveEmptyEntries).Contains(entry.Id.ToString()))
                Difficulty = InstanceDifficulty.Veteran;
            else if (InstanceSettings.For(InstanceId) is InstanceSettings.Choice choice)
                Difficulty = choice.Difficulty; // ClientSetInstanceSettings of the owner (leader or lone player)
        }

        protected override NexusForever.Database.World.Model.EntityModel SpawnModel(NexusForever.Database.World.Model.EntityModel model)
        {
            uint creature = VeteranVariants.Get((ushort)Entry.Id, model.Creature, Difficulty);
            if (creature == 0u)
                return model;
            Diagnostics.FailureLedger.Record(Difficulty == InstanceDifficulty.Veteran ? "veteran-variant" : "normal-variant", $"world {Entry.Id}");
            return VeteranVariants.Swap(model, creature);
        }

        /// <summary>
        /// A creature without a normal/veteran pair whose designer description marks the other difficulty ("[KV] e444 - Grond - Bloodspawn Flea -
        /// Veteran", level 50, from a researched spawn in Kel Voreth) is not spawned: same rule as encounter spells (<see cref="Encounters.DesignerTag.FitsMode"/>).
        /// </summary>
        protected override bool SkipSpawn(NexusForever.Database.World.Model.EntityModel model)
        {
            // a raid boss this owner already killed in the lockout period stays dead in the new instance
            if (IsRaid && OwnerCharacterId != 0ul && Groups.InstanceLockouts.IsRaidBossSaved(OwnerCharacterId, (ushort)Entry.Id, model.Id))
            {
                killedBosses.Add(model.Id);
                Diagnostics.FailureLedger.Record("raid-lockout-boss-skipped", $"world {Entry.Id}", $"spawn {model.Id} creature {model.Creature}");
                return true;
            }
            if (model.Creature == 0u || VeteranVariants.Get((ushort)Entry.Id, model.Creature, Difficulty) != 0u)
                return false;
            var tag = Encounters.DesignerTag.Parse(GameTableManager.Instance.Creature2.GetEntry(model.Creature)?.Description);
            if (tag == null || tag.FitsMode(Difficulty == InstanceDifficulty.Veteran))
                return false;
            Diagnostics.FailureLedger.Record("spawn-other-difficulty", $"world {Entry.Id} {Difficulty}", $"creature {model.Creature}");
            return true;
        }

        protected override MapPosition GetPlayerReturnLocation(Player player)
        {
            MapPosition position = DungeonInstancedMap.GetReturn(player.CharacterId);
            if (position != null)
                return position;

            // no recorded position (the player logged in inside the instance, so the entry was not seen): the open world entrance portal of
            // this world. Without one the player stays in the instance at its position (the unload then recreates it, counted)
            if (InstancePortals.OpenWorldPortalTo((ushort)Entry.Id) is var portal && portal != null)
            {
                var entry = NexusForever.Shared.GameTable.GameTableManager.Instance.World.GetEntry(portal.Value.World);
                if (entry != null)
                {
                    Diagnostics.FailureLedger.Record("instance-return", $"world {Entry.Id}", "no remembered entry: sent to the entrance portal");
                    return new MapPosition { Info = new MapInfo { Entry = entry, InstanceId = null }, Position = portal.Value.Position };
                }
            }
            Diagnostics.FailureLedger.Record("instance-return-unknown", $"world {Entry.Id}", "no remembered entry and no entrance portal");
            return new MapPosition { Info = new MapInfo { Entry = Entry, InstanceId = InstanceId }, Position = player.Position };
        }
    }
}
