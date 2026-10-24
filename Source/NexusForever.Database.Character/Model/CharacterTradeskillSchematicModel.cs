namespace NexusForever.Database.Character.Model
{
    /// <summary>A schematic a character learned (from an item, a quest or a coordinate discovery) or crafted once (first craft xp).</summary>
    public class CharacterTradeskillSchematicModel
    {
        public ulong Id { get; set; }
        public uint SchematicId { get; set; }
        public byte Discovered { get; set; }
        public byte Crafted { get; set; }

        public virtual CharacterModel Character { get; set; }
    }
}
