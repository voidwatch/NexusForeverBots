namespace NexusForever.Database.Character.Model
{
    /// <summary>One learned tradeskill of a character: xp, active flag, talent points and the picked talent tiers ("id,id,...").</summary>
    public class CharacterTradeskillModel
    {
        public ulong Id { get; set; }
        public uint TradeskillId { get; set; }
        public uint Xp { get; set; }
        public byte Active { get; set; }
        public uint TalentPoints { get; set; }
        public string Talents { get; set; }

        public virtual CharacterModel Character { get; set; }
    }
}
