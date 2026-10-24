using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Spell groups and shared cooldowns named by ModifySpellCooldown (effect 73), decoded with scripts/research/cdgroups.py:
    /// mode 2 names a spell group id, and a spell is in it when the group is listed in its Spell4GroupList row (SpellGroupId00-31);
    /// mode 3 names a SpellCoolDown id, shared by the spells that carry it in Spell4.SpellCoolDownId00-02.
    /// Evidence: "False Retreat - T4 Cooldown" names group 507 (only False Retreat), "Flame Burst - Cooldown" 473 (only Flame Burst),
    /// Affinity "reduce their active cooldowns" group 8 (668 abilities), "Stance Master" cooldown 135 (the warrior stances).
    /// </summary>
    public static class SpellGroups
    {
        private static readonly ConcurrentDictionary<uint, HashSet<uint>> groupsOfList = new();

        public static bool InGroup(Spell4Entry spell, uint groupId)
        {
            if (spell == null || spell.Spell4GroupListId == 0u || groupId == 0u)
                return false;

            HashSet<uint> groups = groupsOfList.GetOrAdd(spell.Spell4GroupListId, id =>
            {
                Spell4GroupListEntry entry = GameTableManager.Instance.Spell4GroupList.GetEntry(id);
                if (entry == null)
                    return new HashSet<uint>();
                return typeof(Spell4GroupListEntry).GetFields()
                    .Where(f => f.Name.StartsWith("SpellGroupId"))
                    .Select(f => (uint)f.GetValue(entry))
                    .Where(v => v != 0u)
                    .ToHashSet();
            });
            return groups.Contains(groupId);
        }

        public static bool SharesCooldown(Spell4Entry spell, uint coolDownId)
        {
            return spell != null && coolDownId != 0u
                && (spell.SpellCoolDownId00 == coolDownId || spell.SpellCoolDownId01 == coolDownId || spell.SpellCoolDownId02 == coolDownId);
        }

        /// <summary>Mode 2 or 3 of ModifySpellCooldown: does the spell belong to the group or shared cooldown the effect names.</summary>
        public static bool Matches(Spell4Entry spell, uint mode, uint id) => mode == 3u ? SharesCooldown(spell, id) : InGroup(spell, id);
    }
}
