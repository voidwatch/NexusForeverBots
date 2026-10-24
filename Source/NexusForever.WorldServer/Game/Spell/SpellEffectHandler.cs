using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Game.Spell
{
    public delegate void SpellEffectDelegate(Spell spell, UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info);

    public partial class Spell
    {
        private static bool QuestKillTarget(Player caster, UnitEntity target)
        {
            if (target is not NonPlayer quarry || !caster.QuestManager.NeedsKill(quarry.CreatureId))
                return false;
            Diagnostics.FailureLedger.Record("quest-kill-friendly-target", $"creature {quarry.CreatureId} faction {quarry.Faction1}", "damaged: open kill objective");
            return true;
        }

        [SpellEffectHandler(SpellEffectType.Damage)]
        private void HandleEffectDamage(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // gathering nodes take no combat damage, a harvesting tool's spell gathers them (HarvestUnit); a tool spell has two damage effects,
            // the first one that reaches the node gathers it
            if (target is HarvestUnit node)
            {
                node.OnToolHit(caster, parameters.SpellInfo.Entry.Id, parameters.RootSpellInfo?.Entry.Id ?? parameters.ParentSpellInfo?.Entry.Id ?? 0u);
                return;
            }

            if (!Combat.CombatConfig.Enabled)
            {
                // TODO: calculate damage
                info.AddDamage((DamageType)info.Entry.DamageType, 1337);
                return;
            }

            // no self damage, no player versus player and no creature versus creature
            if (target == caster || target.IsDead)
                return;
            if (caster is Player && target is Player)
                return;
            if (caster is not Player && target is not Player)
                return;
            // wildlife and other neutral creatures can be attacked too, friendly ones and vendors cannot
            // (except a creature the player has an open kill objective for: the client data spawns 37 such quest targets friendly, faction 219
            // "Friendly To All" and others, also in sniffed spawns; the game turned them hostile by quest scripts, e.g. FactionSet spells
            // "Q7658 - Reading Vitals", "aggressive cubig - q3901". Assumption, likely, docs/ASSUMPTIONS.md; forced sweep)
            if (caster is Player && (target is NonPlayer { VendorInfo: not null }
                || (caster.GetDispositionTo(target.Faction1) == Disposition.Friendly && !QuestKillTarget((Player)caster, target))))
                return;

            // damage = the effect's parameters (assault power x a + level x b, see Combat.SpellFormula); rows without decoded parameters keep
            // the older form: coefficient (DataBits00, a float) times the caster's assault power (guess). Players use the CreatureLevel row of their
            // level until gear ratings exist (assumption).
            float? formula = Combat.SpellFormula.Amount(info.Entry, caster, target);
            float coefficient = System.BitConverter.Int32BitsToSingle((int)info.Entry.DataBits00);
            if (!(coefficient > 0f) || float.IsInfinity(coefficient))
                coefficient = 1f;

            float rating = 0f;
            if (formula == null)
            {
                rating = caster.GetModifiedProperty(Property.AssaultRating, Combat.UnitStats.Get(Combat.SpellFormula.Stats(caster), Property.AssaultRating)) * Combat.SpellFormula.AssaultPowerPerRating;
                if (rating <= 0f)
                    rating = Combat.CombatConfig.SpellDamage(caster.Level);
            }
            float baseAmount = formula ?? coefficient * rating;

            // damage type 1 physical, 2 tech, 3 magic: the dealt and taken multipliers of the type come from the spell modifiers on either side;
            // deflect, mitigation (armor + the type's resist rating), armor pierce, crit, glance and multi-hit use the client's rating curves (Combat.DamageMath)
            float dealt = 1f, taken = 1f;
            if (info.Entry.DamageType >= 1u && info.Entry.DamageType <= 3u)
            {
                int offset = (int)info.Entry.DamageType - 1;
                dealt  = caster.GetModifiedProperty(Property.DamageDealtMultiplierPhysical + offset, 1f);
                taken  = target.GetModifiedProperty(Property.DamageTakenMultiplierPhysical + offset, 1f);
            }

            if (caster is Player { Session: Bots.BotSession })
                baseAmount *= Bots.BotManager.DamageBoost;
            Combat.DamageMath.HitResult hit = Combat.DamageMath.RollHit(caster, target, info.Entry.DamageType);
            if (hit.Deflected)
            {
                Diagnostics.FailureLedger.Record("combat-stats", "hit deflected");
                target.FireProcs(51u, caster);
                return;
            }
            float resist = hit.Mitigation;
            float crit = hit.Crit ? 2f : 1f;
            if (hit.Glanced)
                Diagnostics.FailureLedger.Record("combat-stats", "hit glanced");
            if (hit.MultiHit)
                Diagnostics.FailureLedger.Record("combat-stats", "multi-hit");
            uint damage = (uint)System.Math.Max(0d, System.Math.Round(baseAmount * dealt * taken * hit.Multiplier * Combat.CombatConfig.DamageRoll()));
            if (damage == 0u && resist < 1f)
                damage = 1u;

            if (target is NonPlayer)
            {
                uint max = target.MaxHealth;
                string bucket = max < 500u ? "<500" : max < 2000u ? "<2k" : max < 10000u ? "<10k" : max < 100000u ? "<100k" : ">=100k";
                Diagnostics.FailureLedger.Record("creature-max-health", bucket, $"creature {target.CreatureId} level {target.Level} health {max}");
            }
            if (damage == 0u)
            {
                Diagnostics.FailureLedger.Record("combat-stats", "damage fully resisted");
                return;
            }

            info.AddDamage((DamageType)info.Entry.DamageType, damage);

            Diagnostics.FailureLedger.Record("combat-stats", $"damage effect applied by {CasterKind(caster)}");
            info.Damage.KilledTarget   = target.TakeDamage(caster, damage, out uint overkill);
            info.Damage.OverkillAmount = overkill;
            Combat.DamageMath.AfterHit(caster, target, damage - overkill);
            caster.FireProcs(12u, target);
            caster.FireProcs(30u, target);
            if (hit.MultiHit)
                caster.FireProcs(251u, target);
            if (crit > 1f)
            {
                caster.FireProcs(145u, target);
                target.FireProcs(146u, caster);
            }
        }

        /// <summary>Who cast an effect, for the combat-stats counters: a bot, a human player or a creature.</summary>
        private static string CasterKind(UnitEntity unit) => unit is Player { Session: Bots.BotSession } ? "bot" : unit is Player ? "human" : "creature";

        [SpellEffectHandler(SpellEffectType.Heal)]
        private void HandleEffectHeal(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!Combat.CombatConfig.Enabled || target.IsDead)
                return;

            // healing something hostile makes no sense
            if (target != caster && caster is Player && caster.GetDispositionTo(target.Faction1) == Disposition.Hostile)
                return;

            Diagnostics.FailureLedger.Record("combat-stats", $"heal effect applied by {CasterKind(caster)}");
            // heal = the effect's parameters (support power x a + level x b, see Combat.SpellFormula); rows without decoded parameters keep
            // the older form: coefficient (DataBits00, a float) times the caster's support rating
            float? formula = Combat.SpellFormula.Amount(info.Entry, caster, target);
            float coefficient = System.BitConverter.Int32BitsToSingle((int)info.Entry.DataBits00);
            if (!(coefficient > 0f) || float.IsInfinity(coefficient))
                coefficient = 1f;

            float rating = 0f;
            if (formula == null)
            {
                rating = caster.GetModifiedProperty(Property.SupportRating, Combat.UnitStats.Get(Combat.SpellFormula.Stats(caster), Property.SupportRating)) * Combat.SpellFormula.SupportPowerPerRating;
                if (rating <= 0f)
                    rating = Combat.CombatConfig.SpellHeal(caster.Level);
            }
            float baseAmount = formula ?? coefficient * rating;

            // crit, Intensity, Hope and multi-hit with the client's rating curves (Combat.DamageMath.RollHeal)
            float rolled = Combat.DamageMath.RollHeal(caster, target, out bool critHeal);
            float crit = critHeal ? 2f : 1f;
            float amount = baseAmount * caster.GetModifiedProperty(Property.HealingMultiplierOutgoing, 1f) * target.GetModifiedProperty(Property.HealingMultiplierIncoming, 1f) * rolled * (float)Combat.CombatConfig.DamageRoll();
            target.Heal((uint)System.Math.Max(1d, System.Math.Round(amount)), caster);
            if (crit > 1f)
                caster.FireProcs(147u, target);
            if (target != caster)
            {
                caster.FireProcs(20u, target);
                target.FireProcs(21u, caster);
            }
        }

        [SpellEffectHandler(SpellEffectType.Activate)]
        private void HandleEffectActivate(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // one activation per cast, whichever of the effect's targets is reached first
            if (caster is Player player)
                CreditActivation(player);
        }

        private void CreditActivation(Player player)
        {
            if (activationHandled || parameters.PrimaryTargetId == 0u)
                return;

            WorldEntity activated = player.GetVisible<WorldEntity>(parameters.PrimaryTargetId);
            if (activated == null)
                return;

            activationHandled = true;
            player.QuestManager.OnCreatureActivated(activated.CreatureId, activated.Guid);
            player.PathMissionManager.OnCreatureActivated(activated.CreatureId, activated.Guid, activated);
            Events.PublicEventEngine.Instance.OnCreatureActivated(player, activated);
        }

        // DataBits00 = item id, DataBits01 = count (0 means one)
        [SpellEffectHandler(SpellEffectType.GiveItemToPlayer)]
        private void HandleEffectGiveItemToPlayer(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
                return;

            if (ItemManager.Instance.GetItemInfo(info.Entry.DataBits00) == null)
            {
                Diagnostics.FailureLedger.Record("give-item-missing", $"item {info.Entry.DataBits00} not in the tables", $"spell {info.Entry.SpellId}");
                return;
            }

            player.Inventory.ItemCreate(InventoryLocation.Inventory, info.Entry.DataBits00, System.Math.Max(1u, info.Entry.DataBits01));
            Diagnostics.FailureLedger.Record("spell-give-item", "item given to a player", $"spell {info.Entry.SpellId} item {info.Entry.DataBits00}");
        }

        [SpellEffectHandler(SpellEffectType.Proxy)]
        private void HandleEffectProxy(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // a dead creature neither ticks nor proxies effects
            if (target.IsDead && target is not Player)
                return;

            // the spell to cast is in DataBits00, rows with 0 there keep it in DataBits01 (Engineer volatility ticks)
            uint proxiedSpell = info.Entry.DataBits00 != 0u ? info.Entry.DataBits00 : info.Entry.DataBits01;
            Spell4Entry proxied = GameTableManager.Instance.Spell4.GetEntry(proxiedSpell);
            if (proxied == null)
            {
                Diagnostics.FailureLedger.Record("proxy-spell-missing", $"spell4 {proxiedSpell} not in the tables", $"spell {info.Entry.SpellId}");
                return;
            }

            // spells that proxy each other (Datascape "Purge" 46811 <-> 46812) would bounce forever: a raid fight held 62k live spells and
            // ran the world out of memory. A proxy back to the spell that started the chain is not cast (guess for the termination,
            // docs/ASSUMPTIONS.md "Proxy cycles"; the real stop condition is not decoded).
            if (proxiedSpell == parameters.SpellInfo.Entry.Id || proxiedSpell == parameters.RootSpellInfo?.Entry.Id || proxiedSpell == parameters.ParentSpellInfo?.Entry.Id)
            {
                Diagnostics.FailureLedger.Record("proxy-cycle", $"spell {info.Entry.SpellId} -> {proxiedSpell} not cast (already in the chain)", $"root {parameters.RootSpellInfo?.Entry.Id}");
                return;
            }

            // A player class spell proxied onto another unit is cast by the original caster at that unit (likely, docs/ASSUMPTIONS.md
            // "Proxy caster"): in class spells such proxies carry caster-side effects on target flag 1 (Warrior "Add Cell - ... - Target"
            // energy per enemy hit, cooldown cuts, procs, self buffs) and damage, CC and heals on flag 2, which only fits that reading.
            // Other proxied spells keep the upstream reading: the target casts them (debuffs and hazards that act on the unit they sit on).
            if (target != caster && !caster.IsDead
                && (GameTableManager.Instance.Spell4Base.GetEntry(proxied.Spell4BaseIdBaseSpell)?.ClassIdPlayer ?? 0u) != 0u)
            {
                caster.CastSpell(proxiedSpell, new SpellParameters
                {
                    ParentSpellInfo        = parameters.SpellInfo,
                    RootSpellInfo          = parameters.RootSpellInfo,
                    UserInitiatedSpellCast = false,
                    PrimaryTargetId        = target.Guid
                });
                Diagnostics.FailureLedger.Record("proxy-caster", "class spell cast by the original caster at the proxy target", $"spell {info.Entry.SpellId} -> {proxiedSpell}");
                return;
            }

            target.CastSpell(proxiedSpell, new SpellParameters
            {
                ParentSpellInfo        = parameters.SpellInfo,
                RootSpellInfo          = parameters.RootSpellInfo,
                UserInitiatedSpellCast = false
            });
        }

        [SpellEffectHandler(SpellEffectType.Disguise)]
        private void HandleEffectDisguise(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            Creature2Entry creature2 = GameTableManager.Instance.Creature2.GetEntry(info.Entry.DataBits02);
            if (creature2 == null)
                return;

            Creature2DisplayGroupEntryEntry displayGroupEntry = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(d => d.Creature2DisplayGroupId == creature2.Creature2DisplayGroupId);
            if (displayGroupEntry == null)
                return;

            player.SetDisplayInfo(displayGroupEntry.Creature2DisplayInfoId);
        }

        [SpellEffectHandler(SpellEffectType.SummonVehicle)]
        private void HandleEffectSummonVehicle(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (target is not Player player)
            {
                Diagnostics.FailureLedger.Record("vehicle-summon", "refused: target is not a player", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            if (GameTableManager.Instance.Creature2.GetEntry(info.Entry.DataBits00) == null
                || GameTableManager.Instance.UnitVehicle.GetEntry(info.Entry.DataBits01) == null)
            {
                Diagnostics.FailureLedger.Record("vehicle-summon", "refused: unknown creature or vehicle", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            if (!player.CanMount())
            {
                Diagnostics.FailureLedger.Record("vehicle-summon", "refused: player cannot enter a vehicle now", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            var vehicle = new SummonedVehicle(player, parameters.SpellInfo.Entry.Id, info.Entry.DataBits00, info.Entry.DataBits01);
            vehicle.EnqueuePassengerAdd(player, VehicleSeatType.Pilot, 0);
            var position = new MapPosition { Position = player.Position };
            if (player.Map.CanEnter(vehicle, position))
            {
                player.Map.EnqueueAdd(vehicle, position);
                Diagnostics.FailureLedger.Record("vehicle-summon", "vehicle queued", $"spell {parameters.SpellInfo.Entry.Id}");
            }
            else
                Diagnostics.FailureLedger.Record("vehicle-summon", "refused: map cannot take the vehicle", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        [SpellEffectHandler(SpellEffectType.SummonMount)]
        private void HandleEffectSummonMount(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // TODO: handle NPC mounting?
            if (!(target is Player player))
                return;

            if (!player.CanMount())
            {
                Diagnostics.FailureLedger.Record("mount-summon", "refused: player cannot mount now", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            var mount = new Mount(player, parameters.SpellInfo.Entry.Id, info.Entry.DataBits00, info.Entry.DataBits01, info.Entry.DataBits04);
            mount.EnqueuePassengerAdd(player, VehicleSeatType.Pilot, 0);

            // usually for hover boards
            /*if (info.Entry.DataBits04 > 0u)
            {
                mount.SetAppearance(new ItemVisual
                {
                    Slot      = ItemSlot.Mount,
                    DisplayId = (ushort)info.Entry.DataBits04
                });
            }*/

            var position = new MapPosition
            {
                Position = player.Position
            };

            if (player.Map.CanEnter(mount, position))
            {
                player.Map.EnqueueAdd(mount, position);
                Diagnostics.FailureLedger.Record("mount-summon", "mount queued", $"spell {parameters.SpellInfo.Entry.Id}");
            }
            else
                Diagnostics.FailureLedger.Record("mount-summon", "refused: map cannot take the mount", $"spell {parameters.SpellInfo.Entry.Id}");

            // FIXME: also cast 52539,Riding License - Riding Skill 1 - SWC - Tier 1,34464
            // FIXME: also cast 80530,Mount Sprint  - Tier 2,36122
        }

        [SpellEffectHandler(SpellEffectType.Teleport)]
        private void HandleEffectTeleport(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            WorldLocation2Entry locationEntry = GameTableManager.Instance.WorldLocation2.GetEntry(info.Entry.DataBits00);
            if (locationEntry == null)
                return;

            if (target is Player player)
                if (player.CanTeleport())
                {
                    // a quest bot outside a team run has no instance to enter, the bot code would send it straight back out
                    WorldEntry destination = GameTableManager.Instance.World.GetEntry(locationEntry.WorldId);
                    if (player.Session is Bots.BotSession && destination != null && (Map.Static.MapType)destination.Type is Map.Static.MapType.Dungeon or Map.Static.MapType.Raid or Map.Static.MapType.Expedition or Map.Static.MapType.Adventure)
                    {
                        Diagnostics.FailureLedger.Record("bot-spell-teleport-instance", $"spell {Spell4Id} to world {locationEntry.WorldId}", "skipped for a bot");
                        return;
                    }

                    if (player.Session is Bots.BotSession)
                        Diagnostics.FailureLedger.Record("bot-spell-teleport", $"spell {Spell4Id} world {player.Map?.Entry.Id} to {locationEntry.WorldId}",
                            caster is Player ? "cast by the bot" : $"cast by creature {caster.CreatureId}");
                    player.TeleportTo((ushort)locationEntry.WorldId, locationEntry.Position0, locationEntry.Position1, locationEntry.Position2);
                }
        }

        [SpellEffectHandler(SpellEffectType.FullScreenEffect)]
        private void HandleFullScreenEffect(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            // TODO/FIXME: Add duration into the queue so that the spell will automatically finish at the correct time. This is a workaround for Full Screen Effects.
            events.EnqueueEvent(new Event.SpellEvent(info.Entry.DurationTime / 1000d, () => { status = SpellStatus.Finished; SendSpellFinish(); }));
        }

        [SpellEffectHandler(SpellEffectType.RapidTransport)]
        private void HandleEffectRapidTransport(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            TaxiNodeEntry taxiNode = GameTableManager.Instance.TaxiNode.GetEntry(parameters.TaxiNode);
            if (taxiNode == null)
                return;

            WorldLocation2Entry worldLocation = GameTableManager.Instance.WorldLocation2.GetEntry(taxiNode.WorldLocation2Id);
            if (worldLocation == null)
                return;

            if (!(target is Player player))
                return;

            if (!player.CanTeleport())
                return;

            var rotation = new Quaternion(worldLocation.Facing0, worldLocation.Facing0, worldLocation.Facing2, worldLocation.Facing3);
            player.Rotation = rotation.ToEulerDegrees();
            player.TeleportTo((ushort)worldLocation.WorldId, worldLocation.Position0, worldLocation.Position1, worldLocation.Position2);
        }

        [SpellEffectHandler(SpellEffectType.LearnDyeColor)]
        private void HandleEffectLearnDyeColor(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            player.Session.GenericUnlockManager.Unlock((ushort)info.Entry.DataBits00);
        }

        [SpellEffectHandler(SpellEffectType.UnlockMount)]
        private void HandleEffectUnlockMount(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            Spell4Entry spell4Entry = GameTableManager.Instance.Spell4.GetEntry(info.Entry.DataBits00);
            player.SpellManager.AddSpell(spell4Entry.Spell4BaseIdBaseSpell);

            player.Session.EnqueueMessageEncrypted(new ServerUnlockMount
            {
                Spell4Id = info.Entry.DataBits00
            });
        }

        [SpellEffectHandler(SpellEffectType.UnlockPetFlair)]
        private void HandleEffectUnlockPetFlair(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            player.PetCustomisationManager.UnlockFlair((ushort)info.Entry.DataBits00);
        }

        [SpellEffectHandler(SpellEffectType.UnlockVanityPet)]
        private void HandleEffectUnlockVanityPet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            Spell4Entry spell4Entry = GameTableManager.Instance.Spell4.GetEntry(info.Entry.DataBits00);
            player.SpellManager.AddSpell(spell4Entry.Spell4BaseIdBaseSpell);

            player.Session.EnqueueMessageEncrypted(new ServerUnlockMount
            {
                Spell4Id = info.Entry.DataBits00
            });
        }

        [SpellEffectHandler(SpellEffectType.SummonVanityPet)]
        private void HandleEffectSummonVanityPet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            // enqueue removal of existing vanity pet if summoned
            if (player.VanityPetGuid != null)
            {
                VanityPet oldVanityPet = player.GetVisible<VanityPet>(player.VanityPetGuid.Value);
                oldVanityPet?.RemoveFromMap();
                player.VanityPetGuid = 0u;
            }

            var vanityPet = new VanityPet(player, info.Entry.DataBits00);

            var position = new MapPosition
            {
                Position = player.Position
            };

            if (player.Map.CanEnter(vanityPet, position))
                player.Map.EnqueueAdd(vanityPet, position);
        }

        // DataBits00 = creature id, DataBits02 = count (1 in every use), DurationTime = lifetime in ms (0 = until killed).
        // Display comes from the creature's display group, the faction of the summoner is inherited. Summons are neutral to quests (no spawn model, no respawn).
        [SpellEffectHandler(SpellEffectType.SummonCreature)]
        private void HandleEffectSummonCreature(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(info.Entry.DataBits00);
            if (creature == null || target.Map == null)
            {
                Diagnostics.FailureLedger.Record("summon-creature-failed", "creature or map missing", $"spell {parameters.SpellInfo.Entry.Id} creature {info.Entry.DataBits00}");
                return;
            }

            uint count = System.Math.Clamp(info.Entry.DataBits02, 1u, 5u);
            if (!SummonAllowed(target.Map, creature.Id))
                return;
            Creature2DisplayGroupEntryEntry displayGroup = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(d => d.Creature2DisplayGroupId == creature.Creature2DisplayGroupId);
            for (uint i = 0u; i < count; i++)
            {
                var summon = new NonPlayer();
                summon.Initialise(new NexusForever.Database.World.Model.EntityModel
                {
                    Type        = (byte)EntityType.NonPlayer,
                    Creature    = creature.Id,
                    World       = (ushort)target.Map.Entry.Id,
                    X           = target.Position.X + i,
                    Y           = target.Position.Y,
                    Z           = target.Position.Z,
                    DisplayInfo = displayGroup?.Creature2DisplayInfoId ?? 0u,
                    OutfitInfo  = 0,
                    Faction1    = (ushort)target.Faction1,
                    Faction2    = (ushort)target.Faction2
                });

                if (info.Entry.DurationTime > 0u)
                    summon.SummonLifetime = info.Entry.DurationTime / 1000d;
                summon.IsSpellSummon = true;
                summonsThisCast++;

                var position = new MapPosition { Position = new Vector3(target.Position.X + i, target.Position.Y, target.Position.Z) };
                if (target.Map.CanEnter(summon, position))
                    target.Map.EnqueueAdd(summon, position);
            }
        }

        // Bounds on spell summons (docs/PERFORMANCE.md "Out of memory in world 1061"). A summon effect applies once per hit unit, so a
        // spell that summons on every target can multiply without end; the caps are far above anything a real fight needs (the largest summon
        // count per effect is 5, the busiest boss fights hold a few dozen adds) and every refusal is counted.
        private const int MaxSummonsPerCast = 10;
        private const int MaxSpellSummonsPerMap = 400;
        private const int MaxPendingActionsForSummon = 5000;
        private int summonsThisCast;

        private bool SummonAllowed(NexusForever.WorldServer.Game.Map.BaseMap map, uint creatureId)
        {
            string reason = summonsThisCast >= MaxSummonsPerCast ? $"more than {MaxSummonsPerCast} summons from one cast"
                : map.SpellSummonCount >= MaxSpellSummonsPerMap ? $"map holds {MaxSpellSummonsPerMap} spell summons"
                : map.PendingActionCount >= MaxPendingActionsForSummon ? $"map has {MaxPendingActionsForSummon}+ queued grid actions"
                : null;
            if (reason == null)
                return true;

            Diagnostics.FailureLedger.Record("summon-refused", reason, $"spell {parameters.SpellInfo.Entry.Id} creature {creatureId} map {map.Entry.Id}");
            return false;
        }

        /// <summary>Place a summoned creature next to the target (the code of SummonCreature), null when it cannot enter the map.</summary>
        private NonPlayer SpawnSummon(UnitEntity target, uint creatureId, double lifetime)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            if (creature == null || target.Map == null)
                return null;
            if (!SummonAllowed(target.Map, creatureId))
                return null;

            Creature2DisplayGroupEntryEntry displayGroup = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(d => d.Creature2DisplayGroupId == creature.Creature2DisplayGroupId);
            var summon = new NonPlayer();
            summon.Initialise(new NexusForever.Database.World.Model.EntityModel
            {
                Type        = (byte)EntityType.NonPlayer,
                Creature    = creature.Id,
                World       = (ushort)target.Map.Entry.Id,
                X           = target.Position.X,
                Y           = target.Position.Y,
                Z           = target.Position.Z,
                DisplayInfo = displayGroup?.Creature2DisplayInfoId ?? 0u,
                OutfitInfo  = 0,
                Faction1    = (ushort)caster.Faction1,
                Faction2    = (ushort)caster.Faction2
            });
            summon.SummonLifetime = lifetime;
            summon.IsSpellSummon = true;
            summonsThisCast++;

            var position = new MapPosition { Position = target.Position };
            if (!target.Map.CanEnter(summon, position))
                return null;
            target.Map.EnqueueAdd(summon, position);
            return summon;
        }

        // SummonTrap: DataBits00 = trap creature, DataBits01 = spell the trap's owner casts on the first hostile unit that comes within
        // DataBits04 (float metres) of it, DataBits02 = arming delay in ms (certain for the spell and the creature: Tether Mine 38860 places
        // creature 26850 and casts 38861 "Tether"; radius and delay likely: 3-5.3 m, 0.5-2 s). The trap lasts the effect duration.
        // DataBits05/06 (a spell or visual id, a flag) are not used.
        [SpellEffectHandler(SpellEffectType.SummonTrap)]
        private void HandleEffectSummonTrap(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            NonPlayer trap = SpawnSummon(target, info.Entry.DataBits00, info.Entry.DurationTime > 0u ? info.Entry.DurationTime / 1000d : 60d);
            if (trap == null)
            {
                Diagnostics.FailureLedger.Record("summon-trap", "creature or map missing", $"spell {parameters.SpellInfo.Entry.Id} creature {info.Entry.DataBits00}");
                return;
            }
            trap.TrapOwnerGuid = caster.Guid;
            trap.TrapSpell4Id  = info.Entry.DataBits01;
            trap.TrapRadius    = System.Math.Max(1f, System.BitConverter.Int32BitsToSingle((int)info.Entry.DataBits04));
            trap.TrapArmTime   = info.Entry.DataBits02 / 1000d;
            Diagnostics.FailureLedger.Record("summon-trap", "placed", $"spell {parameters.SpellInfo.Entry.Id} creature {info.Entry.DataBits00}");
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Owner, uint Creature), NonPlayer> pets = new();

        // SummonPet: DataBits00 = the pet creature (certain: Geist 32944, Artillerybot 42683, Bruiserbot 42685), DataBits01 = the pet kind
        // (Engineer bots 2/3/7, Esper 4/5, Settler turret 9, Stalker clone 11). One pet per owner and creature: a new summon replaces the old.
        // The pet follows its owner and fights the owner's hostile target with its own auto attack (creature fight loop); its abilities
        // ("deals X damage every 2 s") are not cast. Lifetime = effect duration, persistent pets (duration 0) 1 h or until the owner is gone.
        [SpellEffectHandler(SpellEffectType.SummonPet)]
        private void HandleEffectSummonPet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            var key = (caster.Guid, info.Entry.DataBits00);
            if (pets.TryRemove(key, out NonPlayer old) && old.Map != null && old.Map == caster.Map)
                old.Despawn();

            NonPlayer pet = SpawnSummon(caster, info.Entry.DataBits00, info.Entry.DurationTime > 0u ? info.Entry.DurationTime / 1000d : 3600d);
            if (pet == null)
            {
                Diagnostics.FailureLedger.Record("summon-pet", "creature or map missing", $"spell {parameters.SpellInfo.Entry.Id} creature {info.Entry.DataBits00}");
                return;
            }
            pet.PetOwnerGuid = caster.Guid;
            pets[key] = pet;
            Diagnostics.FailureLedger.Record("summon-pet", $"kind {info.Entry.DataBits01} summoned, auto attack only", $"spell {parameters.SpellInfo.Entry.Id} creature {info.Entry.DataBits00}");
        }

        /// <summary>The living pet that <paramref name="summonSpell4Id"/> (a spell with a SummonPet effect) gave this owner, null when none.</summary>
        public static NonPlayer GetPet(uint ownerGuid, uint summonSpell4Id)
        {
            if (pets.IsEmpty)
                return null;
            summonCreatures ??= GameTableManager.Instance.Spell4Effects.Entries
                .Where(e => e.EffectType == (uint)SpellEffectType.SummonPet)
                .ToLookup(e => e.SpellId, e => e.DataBits00);
            foreach (uint creature in summonCreatures[summonSpell4Id])
                if (pets.TryGetValue((ownerGuid, creature), out NonPlayer pet) && pet.Map != null && !pet.IsDead)
                    return pet;
            return null;
        }

        private static ILookup<uint, uint> summonCreatures;

        /// <summary>The living pets of this owner.</summary>
        public static System.Collections.Generic.IEnumerable<NonPlayer> GetPets(uint ownerGuid)
        {
            if (pets.IsEmpty)
                return Enumerable.Empty<NonPlayer>();
            return pets.Where(p => p.Key.Owner == ownerGuid && p.Value.Map != null && !p.Value.IsDead).Select(p => p.Value).ToList();
        }

        // PetCastSpell (111, 191 rows): DataBits00 = the summon spell whose pet acts, DataBits01 = the spell that pet casts (certain from the
        // rows: Repair Bot "Shield Boost" 35657 -> summon 42810, pet spell 70640; Artillery Bot "Barrage" 35123 -> summon 42814, pet spell 49502
        // "Target finder"). The owner's ability button turns into this spell while the pet lives (Spell4.spell4IdPetSwitch of the summon).
        // The pet casts at the owner's target, or at itself when the owner has none. DataBits03-05 (0/0/0, 1/1/1, 1/-1/900) are not decoded.
        [SpellEffectHandler(SpellEffectType.PetCastSpell)]
        private void HandleEffectPetCastSpell(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            NonPlayer pet = GetPet(caster.Guid, info.Entry.DataBits00);
            if (pet == null)
            {
                Diagnostics.FailureLedger.Record("pet-cast-spell", "no living pet from the summon spell", $"spell {parameters.SpellInfo.Entry.Id} summon {info.Entry.DataBits00}");
                return;
            }
            Spell4Entry petSpell = GameTableManager.Instance.Spell4.GetEntry(info.Entry.DataBits01);
            if (petSpell == null || GlobalSpellManager.Instance.GetSpellBaseInfo(petSpell.Spell4BaseIdBaseSpell)?.GetSpellInfo((byte)petSpell.TierIndex) == null)
            {
                Diagnostics.FailureLedger.Record("pet-cast-spell", "pet spell missing", $"spell {info.Entry.DataBits01}");
                return;
            }
            uint targetGuid = parameters.PrimaryTargetId != 0u ? parameters.PrimaryTargetId : caster.TargetGuid;
            pet.CastSpell(petSpell.Id, new SpellParameters { PrimaryTargetId = targetGuid != 0u && targetGuid != caster.Guid ? targetGuid : pet.Guid });
            Diagnostics.FailureLedger.Record("pet-cast-spell", "pet casts", $"spell {parameters.SpellInfo.Entry.Id} pet spell {petSpell.Id}");
        }

        [SpellEffectHandler(SpellEffectType.TitleGrant)]
        private void HandleEffectTitleGrant(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!(target is Player player))
                return;

            player.TitleManager.AddTitle((ushort)info.Entry.DataBits00);
        }

        [SpellEffectHandler(SpellEffectType.Fluff)]
        private void HandleEffectFluff(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
        }

        // ForcedAction (only King Honeygrave "Blinding Swarm"): a client side forced action/animation on the target, carries no server state.
        // ASSUMPTION (docs/ASSUMPTIONS.md): nothing to apply on the server, the damage and forced move effects of the same spell do the work.
        [SpellEffectHandler(SpellEffectType.ForcedAction)]
        private void HandleEffectForcedAction(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-client-only", "ForcedAction (client side action, not sent)", $"spell {parameters.SpellInfo.Entry.Id}");
        }
    }
}
