using NexusForever.WorldServer.Game.Entity;

namespace NexusForever.WorldServer.Game.Spell
{
    public class SpellParameters
    {
        public CharacterSpell CharacterSpell { get; set; }
        public SpellInfo SpellInfo { get; set; }
        public SpellInfo ParentSpellInfo { get; set; }
        public SpellInfo RootSpellInfo { get; set; }
        public bool UserInitiatedSpellCast { get; set; }
        public uint PrimaryTargetId { get; set; }
        /// <summary>The spell is a creature's activation spell (Creature2.Spell4IdActivate) cast on PrimaryTargetId (EntityHandler.ActivateCast).</summary>
        public bool IsActivation { get; set; }
        public Position Position { get; set; }
        public ushort TaxiNode { get; set; }
    }
}
