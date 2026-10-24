using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.Game;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Network;
using NexusForever.WorldServer.Game.Entity.Network.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// The Scientist's scanbot: a pet of the scanbot profile's creature that follows its owner. The owner's client is told its unit id
    /// (ServerPathScientistScanbotState), which makes the path tracker show it as deployed.
    /// </summary>
    public class Scanbot : WorldEntity
    {
        private const float FollowDistance = 2f;
        private const float FollowMinRecalculateDistance = 4f;

        public uint OwnerGuid { get; private set; }
        public Creature2Entry Creature { get; }

        private readonly UpdateTimer followTimer = new(1d);

        public Scanbot(Player owner, uint creature)
            : base(EntityType.Pet)
        {
            OwnerGuid   = owner.Guid;
            Creature    = GameTableManager.Instance.Creature2.GetEntry(creature);
            var display = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(x => x.Creature2DisplayGroupId == Creature.Creature2DisplayGroupId);
            DisplayInfo = display?.Creature2DisplayInfoId ?? 0u;

            SetProperty(Property.BaseHealth, 800.0f, 800.0f);
            SetStat(Stat.Health, 800u);
            SetStat(Stat.Level, owner.Level);
            SetStat(Stat.Sheathed, 0u);
        }

        protected override IEntityModel BuildEntityModel()
        {
            return new PetEntityModel
            {
                CreatureId = Creature.Id,
                OwnerId    = OwnerGuid,
                Name       = ""
            };
        }

        public override void OnAddToMap(BaseMap map, uint guid, Vector3 vector)
        {
            base.OnAddToMap(map, guid, vector);

            Player owner = GetVisible<Player>(OwnerGuid);
            if (owner == null)
            {
                RemoveFromMap();
                return;
            }

            owner.ScanbotGuid = Guid;
            owner.Session.EnqueueMessageEncrypted(new ServerPathScientistScanbotState
            {
                ScanbotUnitId = Guid
            });
        }

        public override void OnEnqueueRemoveFromMap()
        {
            followTimer.Reset(false);
            OwnerGuid = 0u;
        }

        public override void Update(double lastTick)
        {
            base.Update(lastTick);

            followTimer.Update(lastTick);
            if (!followTimer.HasElapsed)
                return;

            Player owner = GetVisible<Player>(OwnerGuid);
            if (owner == null)
            {
                RemoveFromMap();
                return;
            }

            if (owner.Position.GetDistance(Position) >= FollowMinRecalculateDistance)
                MovementManager.Follow(owner, FollowDistance);

            followTimer.Reset();
        }
    }
}
