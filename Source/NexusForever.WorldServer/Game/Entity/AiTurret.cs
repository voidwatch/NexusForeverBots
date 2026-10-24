using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Entity type 13 (AiTurret, 244 world database rows) and 2 (Destructible, 13 rows) had no entity class and spawned as Simple, which is not a
    /// unit: kill objectives on them could never be met (quest 10518 objective 21288 "Interrupt the $(creature=73494), then destroy it", the two
    /// 73494 spawns in world 3460 are type 13). They are creature units like NonPlayer, sent with their own entity model.
    /// </summary>
    [DatabaseEntity(EntityType.AiTurret)]
    public class AiTurret : NonPlayer
    {
        public AiTurret()
            : base(EntityType.AiTurret)
        {
        }
    }

    [DatabaseEntity(EntityType.Destructible)]
    public class Destructible : NonPlayer
    {
        public Destructible()
            : base(EntityType.Destructible)
        {
        }
    }
}
