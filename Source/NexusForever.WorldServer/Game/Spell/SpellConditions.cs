using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Spell4Conditions (Spell4.spell4ConditionsIdCaster/Target): conditionMask selects unit states, conditionValue says for each selected bit
    /// whether the state must be on (bit set) or off (bit clear). Bits of conditionValue outside the mask are ignored.
    /// Decoded from the retail client (certain for the client): caster check in client code (part of the cast check),
    /// target check in client code, same bit layout, each bit returns its own CastResult:
    ///   0x001 dead (0x39/0x3A, target 0x6A/0x6B)        0x002 in combat (0x3B/0x3C, target 0x6C/0x6D)
    ///   0x004 falling (0x136/0x135, target 0x137/0x138)  0x008 moving (0x3F/0x40, target 0x70/0x71)
    ///   0x010 stealth (0x41/0x42, target 0x72/0x73)      0x020 piloting (0x43/0x44, target 0x74/0x75)
    ///   0x040 embarked (0x45/0x46, target 0x76/0x77)     0x080 taxi (0x47/0x48, target 0x78/0x79)
    ///   0x100 swimming (0x117/0x118, target 0x8A/0x8B)    0x200 jumping (0xAB/0xAA)    0x400 sprinting (0x13A/0x139, target 0x13B/0x13C)
    /// The server tracks dead, in combat (UnitEntity.InCombat) and stealth. The other states are not tracked: such bits pass and are
    /// counted in the ledger (spell-condition "state not tracked").
    /// </summary>
    public static class SpellConditions
    {
        private readonly struct Bit
        {
            public readonly uint Mask;
            public readonly string Name;
            public readonly CastResult CasterCannot, CasterMust, TargetCannot, TargetMust;

            public Bit(uint mask, string name, CastResult casterCannot, CastResult casterMust, CastResult targetCannot, CastResult targetMust)
            {
                Mask = mask; Name = name; CasterCannot = casterCannot; CasterMust = casterMust; TargetCannot = targetCannot; TargetMust = targetMust;
            }
        }

        private static readonly Bit[] bits =
        {
            new(0x001u, "dead",      CastResult.CasterCannotBeDead,      CastResult.CasterMustBeDead,      CastResult.TargetCannotBeDead,      CastResult.TargetMustBeDead),
            new(0x002u, "in combat", CastResult.CasterCannotBeInCombat,  CastResult.CasterMustBeInCombat,  CastResult.TargetCannotBeInCombat,  CastResult.TargetMustBeInCombat),
            new(0x004u, "falling",   CastResult.CasterCannotBeFalling,   CastResult.CasterMustBeFalling,   CastResult.TargetCannotBeFalling,   CastResult.TargetMustBeFalling),
            new(0x008u, "moving",    CastResult.CasterCannotBeMoving,    CastResult.CasterMustBeMoving,    CastResult.TargetCannotBeMoving,    CastResult.TargetMustBeMoving),
            new(0x010u, "stealth",   CastResult.CasterCannotBeStealth,   CastResult.CasterMustBeStealth,   CastResult.TargetCannotBeStealth,   CastResult.TargetMustBeStealth),
            new(0x020u, "piloting",  CastResult.CasterCannotBePiloting,  CastResult.CasterMustBePiloting,  CastResult.TargetCannotBePiloting,  CastResult.TargetMustBePiloting),
            new(0x040u, "embarked",  CastResult.CasterCannotBeEmbarked,  CastResult.CasterMustBeEmbarked,  CastResult.TargetCannotBeEmbarked,  CastResult.TargetMustBeEmbarked),
            new(0x080u, "taxi",      CastResult.CasterCannotBeTaxi,      CastResult.CasterMustBeTaxi,      CastResult.TargetCannotBeTaxi,      CastResult.TargetMustBeTaxi),
            new(0x100u, "swimming",  CastResult.CasterCannotBeSwimming,  CastResult.CasterMustBeSwimming,  CastResult.TargetCannotBeSwimming,  CastResult.TargetMustBeSwimming),
            new(0x200u, "jumping",   CastResult.CasterCannotBeJumping,   CastResult.CasterMustBeJumping,   CastResult.CasterCannotBeJumping,   CastResult.CasterMustBeJumping),
            new(0x400u, "sprinting", CastResult.CasterCannotBeSprinting, CastResult.CasterMustBeSprinting, CastResult.TargetCannotBeSprinting, CastResult.TargetMustBeSprinting),
        };

        /// <summary>State of a unit for a condition bit, null when the server does not track it.</summary>
        private static bool? State(UnitEntity unit, uint mask)
        {
            switch (mask)
            {
                case 0x001u: return unit.IsDead;
                case 0x002u: return unit.InCombat;
                case 0x010u: return unit.IsStealthed;
                default:     return null;
            }
        }

        /// <summary>First condition the unit fails, CastResult.Ok when it meets all tracked ones.</summary>
        public static CastResult Check(UnitEntity unit, Spell4ConditionsEntry conditions, bool isCaster, uint spell4Id)
        {
            if (unit == null || conditions == null || conditions.ConditionMask == 0u)
                return CastResult.Ok;

            foreach (Bit bit in bits)
            {
                if ((conditions.ConditionMask & bit.Mask) == 0u)
                    continue;

                bool must = (conditions.ConditionValue & bit.Mask) != 0u;
                bool? state = State(unit, bit.Mask);
                if (state == null)
                {
                    // "must not" on an untracked state is what a unit standing still on the ground meets; "must" cannot be told
                    if (must)
                        Diagnostics.FailureLedger.Record("spell-condition", $"state not tracked: {(isCaster ? "caster" : "target")} must be {bit.Name}, passed", $"spell {spell4Id} condition {conditions.Id}");
                    continue;
                }

                if (state.Value == must)
                    continue;

                CastResult result = isCaster ? (must ? bit.CasterMust : bit.CasterCannot) : (must ? bit.TargetMust : bit.TargetCannot);
                Diagnostics.FailureLedger.Record("spell-condition", $"{(isCaster ? "caster" : "target")} {(must ? "must" : "cannot")} be {bit.Name}", $"spell {spell4Id} condition {conditions.Id}");
                return result;
            }
            return CastResult.Ok;
        }
    }
}
