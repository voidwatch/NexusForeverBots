using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
﻿using System;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Vital, property, proc, stealth and cooldown effects. What the game tables settle is applied; what they do not settle
    /// is stored with its raw data and reported under "effect-stored" so it is visible in the failure report instead of silently ignored.
    /// Field meanings come from the table dump (scripts/spell-data.sh); each handler says how sure they are.
    /// </summary>
    public partial class Spell
    {
        private const double Forever = 1e9;

        private static float Float(uint bits) => BitConverter.Int32BitsToSingle((int)bits);

        private static double Lasts(SpellTargetInfo.SpellTargetEffectInfo info) => info.Entry.DurationTime > 0u ? info.Entry.DurationTime / 1000d : Forever;

        // DataBits00 = vital, DataBits01/DataBits02 = signed amount (equal in nearly every sample: min and max).
        // Sure: 1 Health (Health Dispenser), 5-11 Resource0-6 (the "Resource Buff" test spell touches exactly these, Warrior "Add Cell" is 6 = Kinetic).
        // Likely: 15 Focus. 3 shield capacity (inferred from the shield spells). 16 Dash; the class named vitals (4, 12, 18, 19, 26) go to the
        // class resource slot (UnitEntity.ResolveVital). Not applied: the rest.
        [SpellEffectHandler(SpellEffectType.VitalModifier)]
        private void HandleEffectVitalModifier(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // amounts are signed: a cost is stored as a negative whole number (4294966096 = -1200)
            uint vital = info.Entry.DataBits00;
            int min    = (int)info.Entry.DataBits01;
            int max    = Math.Max(min, (int)info.Entry.DataBits02);
            float amount = min == max ? min : new Random(Guid.NewGuid().GetHashCode()).Next(min, max + 1);

            // DataBits05 is a share of the vital's maximum (certain: tooltips cite it as "data05pct of your maximum Focus", Medic Recharge
            // 42596 0.05-0.2 per Actuator, Esper Meditate 32818; Stalker stance passives hold -1.0 = empty the Suit Power on a stance change)
            float share = Float(info.Entry.DataBits05);
            if (share != 0f)
            {
                if (share >= -1f && share <= 1f)
                {
                    float vitalMax = target.GetVitalMax(vital);
                    amount += share * vitalMax;
                    if (vitalMax <= 0f)
                        Diagnostics.FailureLedger.Record("vital-share", $"vital {vital} has no maximum, share not applied", $"spell {parameters.SpellInfo.Entry.Id} share {share}");
                }
                else
                    Diagnostics.FailureLedger.Record("vital-share", $"DataBits05 {share} outside -1..1 not applied (meaning unknown)", $"spell {parameters.SpellInfo.Entry.Id} vital {vital}");
            }

            bool applied = amount != 0f && target.AddVital(vital, amount);
            Diagnostics.FailureLedger.Record("effect-stored", $"VitalModifier vital {vital} {(applied ? "applied" : "not applied")} on {(target is Player ? "player" : "creature")}", $"spell {parameters.SpellInfo.Entry.Id}, amount {amount}");
            if (target is Player resourcePlayer && vital != 1u && vital != 3u)
                Diagnostics.FailureLedger.Record("resource-flow", $"{resourcePlayer.Class} vital {vital} {(amount > 0f ? "gain" : amount < 0f ? "loss" : "zero")} spell {parameters.SpellInfo.Entry.Id}{(applied ? "" : " not applied")}");
        }

        // DataBits00 = Property id (matches the Property enum: 100 MoveSpeedMultiplier, 42 Armor, 138 DamageDealtMultiplierMelee),
        // DataBits01 = modifier type 1-4 (meaning not settled, see gt propmod), DataBits02/03 = floats.
        [SpellEffectHandler(SpellEffectType.UnitPropertyModifier)]
        private void HandleEffectUnitPropertyModifier(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            var property = (Property)info.Entry.DataBits00;
            target.AddPropertyModifier(new UnitEntity.PropertyModifierEntry
            {
                Spell4Id  = parameters.SpellInfo.Entry.Id,
                Property  = property,
                Type      = info.Entry.DataBits01,
                ValueA    = Float(info.Entry.DataBits02),
                ValueB    = Float(info.Entry.DataBits03),
                Remaining = Lasts(info)
            });

            Diagnostics.FailureLedger.Record("effect-stored", $"UnitPropertyModifier {property} type {info.Entry.DataBits01} applied to server side stats only (damage, resist, CC duration), the client is not told", $"spell {parameters.SpellInfo.Entry.Id}, {Float(info.Entry.DataBits02)}/{Float(info.Entry.DataBits03)}");
        }

        // DataBits00 = source property, DataBits01 = target property, DataBits02 = share (float): the target gains share x the source (tooltips of
        // 58994, 82056, 87367, certain). Read by every stat that goes through DamageMath.Value (ratings, armor, resists).
        [SpellEffectHandler(SpellEffectType.UnitPropertyConversion)]
        private void HandleEffectUnitPropertyConversion(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            target.AddPropertyModifier(new UnitEntity.PropertyModifierEntry
            {
                Spell4Id    = parameters.SpellInfo.Entry.Id,
                Property    = (Property)info.Entry.DataBits01,
                ConvertFrom = (Property)info.Entry.DataBits00,
                ValueA      = Float(info.Entry.DataBits02),
                Remaining   = Lasts(info)
            });
            Diagnostics.FailureLedger.Record("effect-applied", $"UnitPropertyConversion {(Property)info.Entry.DataBits00} -> {(Property)info.Entry.DataBits01}", $"spell {parameters.SpellInfo.Entry.Id}, share {Float(info.Entry.DataBits02)}");
        }

        // ModifySpell: DataBits00 = Spell4Base id or 0, DataBits01 = spell group id; the Spell4Modification rows of this effect row say what
        // changes (SpellModifications, the client's evaluator). Cast time, cooldown, innate costs, innate requirement amounts and the
        // Spell4Conditions rows are applied to the target's own casts; range, channel timing, threat and the rest have no server use and are counted.
        [SpellEffectHandler(SpellEffectType.ModifySpell)]
        private void HandleEffectModifySpell(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            IReadOnlyList<Spell4ModificationEntry> rows = SpellModifications.RowsOf(info.Entry.Id);
            if (rows.Count == 0)
            {
                Diagnostics.FailureLedger.Record("spell-modification", "ModifySpell row without Spell4Modification rows", $"spell {parameters.SpellInfo.Entry.Id} effect {info.Entry.Id}");
                return;
            }
            target.AddSpellMod(new UnitEntity.SpellModEntry
            {
                Spell4Id  = parameters.SpellInfo.Entry.Id,
                EffectId  = info.Entry.Id,
                BaseId    = info.Entry.DataBits00,
                GroupId   = info.Entry.DataBits00 == 0u ? info.Entry.DataBits01 : 0u,
                Rows      = rows,
                Remaining = Lasts(info)
            });
            foreach (Spell4ModificationEntry row in rows)
            {
                bool applied = SpellModifications.Applied(row.ModificationParameterEnum);
                Diagnostics.FailureLedger.Record(applied ? "spell-modification" : "effect-stored", $"ModifySpell parameter {row.ModificationParameterEnum} type {row.ModificationTypeEnum} {(applied ? "applied" : "stored, no server value")}", $"spell {parameters.SpellInfo.Entry.Id}");
            }
        }

        // SharedHealthPool: 13 rows, all Medic Barrier tiers 41369/60231-60237, every DataBits 0, duration 5 s, target flags 5 (caster and
        // allies in the field). The name says damage is pooled across the units, but no row gives a share, a cap or a pool size, and the
        // spell's tooltips only cite its shield mitigation; nothing is guessed into damage. Counted.
        [SpellEffectHandler(SpellEffectType.SharedHealthPool)]
        private void HandleEffectSharedHealthPool(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", "SharedHealthPool: no data in the rows, damage is not pooled", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // Script (effect 13): 86 of 104 rows carry no data and no parameter at all, the other 18 belong to test, prototype or deleted spells
        // ("[TEST] C_robogolf", "[PROTOTYPE] Discovery - Primal Buff", "[DELETE] Q447"). The behaviour lived in server scripts keyed by spell
        // id that are not in any shipped file; the spell's other effects still apply. Counted, nothing changes.
        [SpellEffectHandler(SpellEffectType.Script)]
        private void HandleEffectScript(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-script-no-data", "Script effect: server script hook, no data in the tables", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = trigger, DataBits01 = spell to cast, DataBits02 = chance (float, 1.0 = always), DataBits03 = target mode,
        // DataBits04 = internal cooldown in ms (1000 in the stealth sample). Triggers and mode bits: docs/ASSUMPTIONS.md, fired by UnitEntity.FireProcs.
        [SpellEffectHandler(SpellEffectType.Proc)]
        private void HandleEffectProc(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            target.AddProc(new UnitEntity.ProcEntry
            {
                Spell4Id     = parameters.SpellInfo.Entry.Id,
                Trigger      = info.Entry.DataBits00,
                ProcSpell4Id = info.Entry.DataBits01,
                Chance       = Float(info.Entry.DataBits02),
                Mode         = info.Entry.DataBits03,
                CooldownMs   = info.Entry.DataBits04,
                ApplierGuid  = caster.Guid,
                Remaining    = Lasts(info)
            });

            Diagnostics.FailureLedger.Record("effect-stored", $"Proc trigger {info.Entry.DataBits00} mode {info.Entry.DataBits03} registered", $"spell {parameters.SpellInfo.Entry.Id} casts {info.Entry.DataBits01}");
        }

        // Stealth toggles: the "On" spell carries two Stealth effects gated by prerequisites, DataBits02 0 = enter, 1 = leave (inferred).
        // The stealthed state is real (prerequisites 116 read it), creatures do not react to it yet.
        [SpellEffectHandler(SpellEffectType.Stealth)]
        private void HandleEffectStealth(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (info.Entry.DataBits02 == 1u)
            {
                target.RemoveAurasOfEffect((uint)SpellEffectType.Stealth);
                Diagnostics.FailureLedger.Record("stealth", "left stealth", $"spell {parameters.SpellInfo.Entry.Id}");
            }
            else
            {
                target.AddAura(parameters.SpellInfo.Entry.Id, (uint)SpellEffectType.Stealth, Lasts(info));
                Diagnostics.FailureLedger.Record("stealth", "entered stealth", $"spell {parameters.SpellInfo.Entry.Id}");
            }
        }

        [SpellEffectHandler(SpellEffectType.RemoveStealth)]
        private void HandleEffectRemoveStealth(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            target.RemoveAurasOfEffect((uint)SpellEffectType.Stealth);
            Diagnostics.FailureLedger.Record("stealth", "stealth removed", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = 1 and DataBits01 = a signal id: a message to the client-side script (ravel) of the spell. Nothing for the server to do.
        [SpellEffectHandler(SpellEffectType.RavelSignal)]
        private void HandleEffectRavelSignal(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", "RavelSignal (client script message, nothing to do on the server)", $"spell {parameters.SpellInfo.Entry.Id}, signal {info.Entry.DataBits01}");
        }

        // Seen on quest/tract visual spells (e.g. 37403, 20 s, with an Activate effect): changes which phase layer the target is shown in. Entities
        // here have no phase layers (everything is visible to everyone), so only the data is recorded. ASSUMPTION (docs/ASSUMPTIONS.md).
        [SpellEffectHandler(SpellEffectType.ChangePhase)]
        private void HandleEffectChangePhase(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", "ChangePhase (no phase layers, everything visible to everyone)", $"spell {parameters.SpellInfo.Entry.Id}, bits {info.Entry.DataBits00}/{info.Entry.DataBits01}");
        }

        // Seen next to SummonVehicle (speeder quests, DataBits00 = 1): changes a reward property of the caster while the vehicle exists. Reward
        // properties are only the premium/entitlement tiers here, so only the data is recorded. ASSUMPTION (docs/ASSUMPTIONS.md).
        [SpellEffectHandler(SpellEffectType.RewardPropertyModifier)]
        private void HandleEffectRewardPropertyModifier(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", "RewardPropertyModifier (reward properties are tier based, not changed by spells)", $"spell {parameters.SpellInfo.Entry.Id}, bits {info.Entry.DataBits00}");
        }

        // Decoded from 522 effects: DataBits00 = what DataBits01 names (0 spell base id, 1 spell4 id - both confirmed by the id ranges;
        // 2 a spell group id, 3 a SpellCoolDown id, see SpellGroups), DataBits02 = how DataBits03 (a float, milliseconds) is used
        // (2 add to the remaining cooldown, negative values shorten it; 3 set the cooldown to the value; 0 and 1 multiply the remaining
        // cooldown by DataBits03: value 0 resets it - Quick Shot Left/Right reset each other, stance 46867 "Reset Assault and Support ability
        // cooldowns" is group 66 x 0 - and 0.8 is "Reduce all cooldowns by 20%" (Fixation 33272, pctsub); likely). A lasting
        // multiplier for the effect duration (rows with a duration) is not modelled: only the instant change is applied.
        [SpellEffectHandler(SpellEffectType.ModifySpellCooldown)]
        private void HandleEffectModifySpellCooldown(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint mode = info.Entry.DataBits00;
            uint how  = info.Entry.DataBits02;
            float ms  = Float(info.Entry.DataBits03);

            // multiplier forms: remaining cooldown x factor (0 = reset)
            if (how < 2u && ms >= 0f && ms <= 1f)
            {
                int scaled = 0;
                if (target is Player multPlayer)
                {
                    foreach (Spell4Entry known in multPlayer.SpellManager.GetKnownSpell4Entries().Where(k => mode switch
                    {
                        0u => k.Spell4BaseIdBaseSpell == info.Entry.DataBits01,
                        1u => k.Id == info.Entry.DataBits01,
                        _  => SpellGroups.Matches(k, mode, info.Entry.DataBits01)
                    }))
                    {
                        double current = multPlayer.SpellManager.GetSpellCooldown(known.Id);
                        if (current <= 0d)
                            continue;
                        multPlayer.SpellManager.SetSpellCooldown(known.Id, current * ms);
                        scaled++;
                    }
                }
                Diagnostics.FailureLedger.Record("effect-stored", $"ModifySpellCooldown mode {mode}/{how} remaining x {ms:0.##} on {scaled} spell(s){(info.Entry.DurationTime > 0u ? ", lasting multiplier not modelled" : "")}", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            // mode 2 names a spell group (listed in the spell's Spell4GroupList row), mode 3 a shared SpellCoolDown id (docs/ASSUMPTIONS.md)
            if (target is Player groupPlayer && (mode == 2u || mode == 3u) && (how == 2u || how == 3u))
            {
                int changed = 0;
                foreach (Spell4Entry known in groupPlayer.SpellManager.GetKnownSpell4Entries().Where(k => SpellGroups.Matches(k, mode, info.Entry.DataBits01)))
                {
                    double current = groupPlayer.SpellManager.GetSpellCooldown(known.Id);
                    double next    = how == 3u ? ms / 1000d : current + ms / 1000d;
                    if (how == 3u || current > 0d)
                    {
                        groupPlayer.SpellManager.SetSpellCooldown(known.Id, Math.Max(0d, next));
                        changed++;
                    }
                }

                Diagnostics.FailureLedger.Record("effect-stored", $"ModifySpellCooldown group mode {mode}/{how} applied to {changed} known spell(s)", $"spell {parameters.SpellInfo.Entry.Id}, group {info.Entry.DataBits01} {ms} ms");
                return;
            }

            if (target is Player player && (mode == 0u || mode == 1u) && (how == 2u || how == 3u))
            {
                uint spell4Id = mode == 1u ? info.Entry.DataBits01 : player.SpellManager.GetSpell(info.Entry.DataBits01)?.SpellInfo.Entry.Id ?? 0u;
                if (spell4Id != 0u)
                {
                    double current = player.SpellManager.GetSpellCooldown(spell4Id);
                    double next    = how == 3u ? ms / 1000d : current + ms / 1000d;
                    if (how == 3u || current > 0d)
                        player.SpellManager.SetSpellCooldown(spell4Id, Math.Max(0d, next));

                    Diagnostics.FailureLedger.Record("effect-stored", $"ModifySpellCooldown mode {mode}/{how} applied", $"spell {parameters.SpellInfo.Entry.Id}, {info.Entry.DataBits01} {ms} ms");
                    return;
                }
            }

            // creatures: the abilities they cast (CreatureResearch) carry the same ids
            if (target is NonPlayer npc && (how == 2u || how == 3u))
            {
                uint wanted = info.Entry.DataBits01;
                int changed = mode switch
                {
                    0u => npc.ModifyAbilityCooldown(a => a.Spell4BaseIdBaseSpell == wanted, ms / 1000d, how == 3u),
                    1u => npc.ModifyAbilityCooldown(a => a.Id == wanted, ms / 1000d, how == 3u),
                    _ => npc.ModifyAbilityCooldown(a => SpellGroups.Matches(a, mode, wanted), ms / 1000d, how == 3u)
                };
                Diagnostics.FailureLedger.Record("effect-stored", $"ModifySpellCooldown mode {mode}/{how} on a creature applied to {changed} ability(ies)", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            string why = target is not Player ? "target is not a player" : how < 2u ? "multiplier form" : "spell not known to the target";
            Diagnostics.FailureLedger.Record("effect-stored", $"ModifySpellCooldown mode {mode}/{how} skipped: {why}", $"spell {parameters.SpellInfo.Entry.Id}, {info.Entry.DataBits01}/{ms}");
        }

        // DataBits00 = quest id, DataBits01 = index of the objective in the quest, DataBits02 = amount. Confirmed by spell names
        // ("Q9110 - Sear Roan Steak"): the spell moves the quest along without the normal objective type.
        [SpellEffectHandler(SpellEffectType.QuestAdvanceObjective)]
        private void HandleEffectQuestAdvanceObjective(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            bool advanced = player.QuestManager.AdvanceObjective((ushort)info.Entry.DataBits00, (byte)info.Entry.DataBits01, Math.Max(1u, info.Entry.DataBits02));
            Diagnostics.FailureLedger.Record("quest-advance", advanced ? "objective advanced" : "quest or objective not active", $"quest {info.Entry.DataBits00} objective {info.Entry.DataBits01} by spell {parameters.SpellInfo.Entry.Id}");
        }

        // Rows with decoded parameters (support power x a + level x b, 236 of 262 rows, e.g. Shield Surge, Bolstering Strike) restore that amount,
        // like heals (Combat.SpellFormula). Otherwise ParameterType 22 with 0.3 in "Medic - Energize" is read as 30% of maximum shield (inferred).
        [SpellEffectHandler(SpellEffectType.HealShields)]
        private void HandleEffectHealShields(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            float max      = target.GetShieldMax();
            float? formula = Combat.SpellFormula.Amount(info.Entry, caster, target);
            float fraction = info.Entry.ParameterValue00 > 0f ? info.Entry.ParameterValue00 : 0.3f;
            float amount   = formula ?? fraction * max;
            bool applied   = max > 0f && amount > 0f && target.AddVital(3u, amount);
            Diagnostics.FailureLedger.Record("effect-stored", $"HealShields {(applied ? "applied" : "not applied")}{(formula != null ? " (formula)" : "")}", $"spell {parameters.SpellInfo.Entry.Id}, amount {amount:0}");
        }

        // Effects whose whole meaning is on the client or a state the server does not model: recorded so they show up in the report,
        // nothing else to do. ShieldOverload (shields off for DurationTime), SetBusy (entity is in use for DurationTime), Scale
        // (visual size), ModifyInterruptArmor (DataBits00 2, DataBits01 1), DisguiseOutfit and ChangeDisplayName (looks and name).
        // AchievementAdvance: DataBits00 achievement id, DataBits01 amount (inferred from spell 55197 "Achievement Advance - Spines of Doom").
        // Grants when the amount completes a plain counter achievement, anything else is recorded, not guessed (docs/ASSUMPTIONS.md).
        [SpellEffectHandler(SpellEffectType.AchievementAdvance)]
        private void HandleEffectAchievementAdvance(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            Game.Achievement.AchievementInfo achievement = Game.Achievement.GlobalAchievementManager.Instance.GetAchievement((ushort)info.Entry.DataBits00);
            if (achievement == null || achievement.ChecklistEntries.Count != 0 || achievement.Entry.Value > info.Entry.DataBits01)
            {
                NoteClientOnly("AchievementAdvance", info);
                return;
            }

            if (!player.AchievementManager.HasCompletedAchievement(achievement.Id))
                player.AchievementManager.GrantAchievement(achievement.Id);
        }

        [SpellEffectHandler(SpellEffectType.ShieldOverload)]
        private void HandleEffectShieldOverload(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ShieldOverload", info);

        [SpellEffectHandler(SpellEffectType.SetBusy)]
        private void HandleEffectSetBusy(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // creatures that are in use do nothing else for the duration (inferred from the name); players have no server side busy state
            if (target is NonPlayer npc && info.Entry.DurationTime > 0u)
                npc.DelayActions(info.Entry.DurationTime / 1000d);
            else
                NoteClientOnly("SetBusy", info);
        }

        [SpellEffectHandler(SpellEffectType.Scale)]
        private void HandleEffectScale(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("Scale", info);

        [SpellEffectHandler(SpellEffectType.ModifyInterruptArmor)]
        private void HandleEffectModifyInterruptArmor(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ModifyInterruptArmor", info);

        [SpellEffectHandler(SpellEffectType.DisguiseOutfit)]
        private void HandleEffectDisguiseOutfit(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("DisguiseOutfit", info);

        [SpellEffectHandler(SpellEffectType.ItemVisualSwap)]
        private void HandleEffectItemVisualSwap(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ItemVisualSwap", info);

        [SpellEffectHandler(SpellEffectType.ChangeDisplayName)]
        private void HandleEffectChangeDisplayName(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ChangeDisplayName", info);

        [SpellEffectHandler(SpellEffectType.ModifyAbilityCharges)]
        private void HandleEffectModifyAbilityCharges(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ModifyAbilityCharges", info);

        // The target creature leaves the map; spawn point creatures are rescheduled so a quest NPC is not gone until the next restart. Players are never despawned.
        [SpellEffectHandler(SpellEffectType.DespawnUnit)]
        private void HandleEffectDespawnUnit(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not NonPlayer npc)
                return;

            npc.Despawn();
            Diagnostics.FailureLedger.Record("effect-applied", "DespawnUnit removed a creature", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // Facility (guild/housing plot) changes: no facilities exist on the server, recorded.
        [SpellEffectHandler(SpellEffectType.FacilityModification)]
        private void HandleEffectFacilityModification(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("FacilityModification", info);

        // Leaving a vehicle: DataBits00 is a mode (3 in the one spell that uses it, the bug bomb). The player is taken off the vehicle.
        [SpellEffectHandler(SpellEffectType.Disembark)]
        private void HandleEffectDisembark(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is Player player && player.VehicleGuid != 0u)
                player.Dismount();
            else
                NoteClientOnly("Disembark", info);
        }

        // DataBits00 = vital (1 health, 5-11 resources), DataBits02 = percentage of the maximum (100f = full), DataBits03 = 1 give to the target, 0 take from it.
        // Read from "Full restore granted at level up" (health and resource 0, 100f, 1), the medkit (25f, 1) and "Steamglider EnergyDrain (Malaise)" (resource 0, 20f, 0): inferred.
        [SpellEffectHandler(SpellEffectType.SapVital)]
        private void HandleEffectSapVital(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint vital     = info.Entry.DataBits00;
            float percent  = Float(info.Entry.DataBits02);
            bool restore   = info.Entry.DataBits03 == 1u;
            bool applied;
            if (vital == 1u)
            {
                uint amount = (uint)Math.Max(0d, Math.Round(target.MaxHealth * percent / 100d));
                if (restore)
                    target.Heal(amount, caster);
                else
                    target.TakeDamage(caster, amount, out _);
                applied = true;
            }
            else
                applied = target.AddVital(vital, restore ? percent : -percent);

            Diagnostics.FailureLedger.Record("effect-stored", $"SapVital vital {vital} {(restore ? "restore" : "drain")} {(applied ? "applied" : "not applied")}", $"spell {parameters.SpellInfo.Entry.Id}, {percent}%");
        }

        // A marker with a duration (2067 ms in "Shadow Master Summon Minions on Aware"): the creature does nothing else while the effect runs (inferred).
        [SpellEffectHandler(SpellEffectType.NpcExecutionDelay)]
        private void HandleEffectNpcExecutionDelay(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is NonPlayer npc && info.Entry.DurationTime > 0u)
                npc.DelayActions(info.Entry.DurationTime / 1000d);
            else
                NoteClientOnly("NpcExecutionDelay", info);
        }

        // Client side creature flags (16416 and 16384 in telegraph and snare aura spells): nothing to do on the server.
        // DataBits00 = Faction2 id (certain: "Q932 - Make player neutral to pumera" 290, "Tranquilizer Gun" / "In Stasis" 219 Friendly To All,
        // "Q7658 - Reading Vitals" 218). The creature takes the faction for the effect duration (0 = until it respawns). Players keep their faction:
        // Faction1 is also their Exile/Dominion identity, a disguise would need a disposition override (recorded, docs/ASSUMPTIONS.md "FactionSet").
        [SpellEffectHandler(SpellEffectType.FactionSet)]
        private void HandleEffectFactionSet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint faction = info.Entry.DataBits00;
            if (GameTableManager.Instance.Faction2.GetEntry(faction) == null)
            {
                Diagnostics.FailureLedger.Record("faction-set-unknown", $"faction {faction} not in Faction2", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            if (target is Player)
            {
                Diagnostics.FailureLedger.Record("effect-stored", "FactionSet on a player recorded, not applied", $"spell {parameters.SpellInfo.Entry.Id} faction {faction}");
                return;
            }

            target.SetFactionFor((Reputation.Static.Faction)faction, Lasts(info));
            Diagnostics.FailureLedger.Record("faction-set", $"creature faction set to {faction}", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        [SpellEffectHandler(SpellEffectType.ModifyCreatureFlags)]
        private void HandleEffectModifyCreatureFlags(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("ModifyCreatureFlags", info);

        // DataBits00 is a TradeskillSchematic2 id (the spells of schematic items, e.g. "Schematic: ..."; 385 of the 1171 rows name an existing
        // schematic, the other 786 name ids no longer in the table). The schematic joins the known list (ServerSchematicAddLearned).
        [SpellEffectHandler(SpellEffectType.GiveSchematic)]
        private void HandleEffectGiveSchematic(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;
            uint schematicId = info.Entry.DataBits00;
            if (GameTableManager.Instance.TradeskillSchematic2.GetEntry(schematicId) == null)
            {
                Diagnostics.FailureLedger.Record("give-schematic-unknown", $"schematic {schematicId} not in TradeskillSchematic2", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }
            player.TradeskillManager.LearnSchematic(schematicId);
            Diagnostics.FailureLedger.Record("give-schematic", $"schematic {schematicId}", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        [SpellEffectHandler(SpellEffectType.GiveLootTableToPlayer)]
        private void HandleEffectGiveLootTableToPlayer(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("GiveLootTableToPlayer", info);

        [SpellEffectHandler(SpellEffectType.SpellCounter)]
        private void HandleEffectSpellCounter(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("SpellCounter", info);

        [SpellEffectHandler(SpellEffectType.DistributedDamage)]
        private void HandleEffectDistributedDamage(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("DistributedDamage", info);

        [SpellEffectHandler(SpellEffectType.Absorption)]
        private void HandleEffectAbsorption(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("Absorption", info);

        [SpellEffectHandler(SpellEffectType.SpellForceRemoveChanneled)]
        private void HandleEffectSpellForceRemoveChanneled(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteClientOnly("SpellForceRemoveChanneled", info);

        private void NoteClientOnly(string name, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", $"{name} recorded, not applied", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = 6, 7 or 8 (damage type or school, not settled), DataBits02 = float, 1.3 in Analyze Weakness (+30% damage taken from the caster).
        [SpellEffectHandler(SpellEffectType.PersonalDmgHealMod)]
        private void HandleEffectPersonalDmgHealMod(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            target.AddPersonalMod(new UnitEntity.PersonalModEntry
            {
                Spell4Id   = parameters.SpellInfo.Entry.Id,
                CasterGuid = caster.Guid,
                Kind       = info.Entry.DataBits00,
                Direction  = info.Entry.DataBits01,
                Factor     = Float(info.Entry.DataBits02),
                Remaining  = Lasts(info)
            });

            Diagnostics.FailureLedger.Record("effect-stored", $"PersonalDmgHealMod kind {info.Entry.DataBits00} direction {info.Entry.DataBits01} applied", $"spell {parameters.SpellInfo.Entry.Id}, factor {Float(info.Entry.DataBits02)}");
        }
    }
}
