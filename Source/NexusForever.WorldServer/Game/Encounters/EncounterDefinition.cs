using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexusForever.WorldServer.Game.Encounters
{
    /// <summary>
    /// A boss encounter as data (data/encounters/*.json): phases entered by boss health, ability timers per phase, adds, an enrage timer.
    /// Hand written files and files generated from the researched creature abilities (scripts/research/make_encounters.py) share the format.
    /// </summary>
    public sealed class EncounterDefinition
    {
        /// <summary>Creature name of the boss (matched on the client text, case and punctuation ignored).</summary>
        [JsonPropertyName("boss")] public string Boss { get; set; }
        /// <summary>Optional world the encounter belongs to (0 = any), for bosses that share a name between dungeons.</summary>
        [JsonPropertyName("world")] public uint World { get; set; }
        /// <summary>Seconds in combat until the boss enrages (0 = never).</summary>
        [JsonPropertyName("enrage")] public double Enrage { get; set; }
        /// <summary>Distance from its spawn at which the boss gives up and resets (default 120 m).</summary>
        [JsonPropertyName("leash")] public float Leash { get; set; }
        /// <summary>Where the definition came from: hand written, raidcore, jabbithole, generated.</summary>
        [JsonPropertyName("source")] public string Source { get; set; }
        /// <summary>
        /// Timer confidence (scripts/research/bossmod_timers.py): likely = two independent boss mods agree on the repeat timers of half or
        /// more of the abilities, partial = some, guess = assumed timers. Hand written "certain" is allowed for timers settled by the tables.
        /// </summary>
        [JsonPropertyName("confidence")] public string Confidence { get; set; }
        /// <summary>Ability name confidence (scripts/research/encounter_audit.py): certain = every name confirmed by the Spell4 designer
        /// descriptions or by observed casts, partial, guess, tables (generated from researched spell ids).</summary>
        [JsonPropertyName("names")] public string Names { get; set; }
        [JsonPropertyName("phases")] public List<PhaseDefinition> Phases { get; set; } = new();
    }

    public static class EncounterClass
    {
        public const string Sourced   = "sourced";
        public const string Partial   = "partial";
        public const string Guide     = "guide";
        public const string Generated = "generated";

        /// <summary>
        /// sourced: timers confirmed by two sources (or the tables); partial: some timers confirmed; guide: phases and names from guides or
        /// boss mods, timers assumed; generated: no definition, researched abilities on assumed timers.
        /// </summary>
        public static string Of(EncounterDefinition definition)
        {
            if (definition == null || definition.Source == "generated")
                return Generated;
            return definition.Confidence switch
            {
                "certain" or "likely" => Sourced,
                "partial"             => Partial,
                _                     => Guide
            };
        }
    }

    public sealed class PhaseDefinition
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        /// <summary>The phase starts when the boss health percentage drops to this value or below (the first phase has 100).</summary>
        [JsonPropertyName("health")] public double Health { get; set; } = 100d;
        /// <summary>Spell names (resolved against the boss abilities) or Spell4 ids cast on timers while the phase lasts.</summary>
        [JsonPropertyName("abilities")] public List<AbilityDefinition> Abilities { get; set; } = new();
        /// <summary>Creatures summoned when the phase starts, then every Repeat seconds.</summary>
        [JsonPropertyName("adds")] public List<AddDefinition> Adds { get; set; } = new();
        /// <summary>Chat line for the phase change (no client text exists for it, shown as a system message to nearby players).</summary>
        [JsonPropertyName("say")] public string Say { get; set; }
        /// <summary>The boss takes no damage during the phase (adds phases).</summary>
        [JsonPropertyName("invulnerable")] public bool Invulnerable { get; set; }
        /// <summary>The next phase starts when every add of this phase is dead (instead of by health).</summary>
        [JsonPropertyName("nextWhenAddsDead")] public bool NextWhenAddsDead { get; set; }
    }

    public sealed class AbilityDefinition
    {
        /// <summary>Spell name or Spell4 id as text.</summary>
        [JsonPropertyName("spell")] public string Spell { get; set; }
        /// <summary>Seconds after the phase starts until the first cast.</summary>
        [JsonPropertyName("first")] public double First { get; set; } = 10d;
        /// <summary>Seconds between casts (0 = once).</summary>
        [JsonPropertyName("every")] public double Every { get; set; } = 20d;
        /// <summary>tank (the current target), random (a random attacker nearby), self (no target, ground or self cast).</summary>
        [JsonPropertyName("target")] public string Target { get; set; } = "tank";
    }

    public sealed class AddDefinition
    {
        [JsonPropertyName("creature")] public string Creature { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; } = 2;
        /// <summary>Seconds after the phase start for the first wave.</summary>
        [JsonPropertyName("first")] public double First { get; set; }
        /// <summary>Seconds between waves (0 = one wave).</summary>
        [JsonPropertyName("every")] public double Every { get; set; }
    }
}
