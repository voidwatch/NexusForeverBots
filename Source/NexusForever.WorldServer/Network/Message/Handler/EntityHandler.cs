using System.Linq;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Network;
using NexusForever.WorldServer.Game.Entity.Network.Command;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game;
using NLog;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class EntityHandler
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        [MessageHandler(GameMessageOpcode.ClientEntityCommand)]
        public static void HandleEntityCommand(WorldSession session, ClientEntityCommand entityCommand)
        {
            WorldEntity mover = session.Player;
            if (session.Player.ControlGuid != session.Player.Guid)
                mover = session.Player.GetVisible<WorldEntity>(session.Player.ControlGuid);

            if (mover == null)
                return;

            foreach ((EntityCommand id, IEntityCommandModel command) in entityCommand.Commands)
            {
                switch (command)
                {
                    case SetPositionCommand setPosition:
                    {
                        // this is causing issues after moving to soon after mounting:
                        // session.Player.CancelSpellsOnMove();
                        mover.Relocate(setPosition.Position.Vector);
                        break;
                    }
                    case SetRotationCommand setRotation:
                        mover.Rotation = setRotation.Position.Vector;
                        break;
                }
            }

            mover.EnqueueToVisible(new ServerEntityCommand
            {
                Guid     = mover.Guid,
                Time     = entityCommand.Time,
                ServerControlled = false,
                Commands = entityCommand.Commands
            });
        }

        [MessageHandler(GameMessageOpcode.ClientActivateUnit)]
        public static void HandleActivateUnit(WorldSession session, ClientActivateUnit unit)
        {
            WorldEntity entity = session.Player.GetVisible<WorldEntity>(unit.UnitId);
            if (entity == null)
                throw new InvalidPacketValueException();

            // TODO: sanity check for range etc.

            NLog.LogManager.GetLogger("Interact").Info($"{session.Player.Name} activated unit {entity.Guid} (creature {entity.CreatureId}, {entity.GetType().Name}).");
            // portal units spawned as NonPlayer (Protogames Academy) have no Simple.OnActivate: route them to the portal code too
            if (entity is not Simple && GameTableManager.Instance.Creature2.GetEntry(entity.CreatureId) is { InstancePortalId: not 0u } portalCreature)
            {
                Game.Map.InstancePortals.Activate(session.Player, portalCreature);
                return;
            }
            entity.OnActivate(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientActivateUnitCast)]
        public static void HandleActivateUnitCast(WorldSession session, ClientActivateUnitCast unit)
        {
            WorldEntity entity = session.Player.GetVisible<WorldEntity>(unit.ActivateUnitId);
            if (entity == null)
                throw new InvalidPacketValueException();

            // TODO: sanity check for range etc.

            NLog.LogManager.GetLogger("Interact").Info($"{session.Player.Name} activate-cast unit {entity.Guid} (creature {entity.CreatureId}, {entity.GetType().Name}).");
            ActivateCast(session.Player, entity);
        }

        /// <summary>
        /// Activate a world object for a player: start the creature's activation spell (flares, levers, quest objects) on the player,
        /// quest credit is given when the spell's Activate effect runs. Creatures without an activation spell credit the quest directly.
        /// Used for clients and for bots.
        /// </summary>
        public static bool ActivateCast(Player player, WorldEntity entity)
        {
            entity.OnActivateCast(player);

            Creature2Entry creatureEntry = GameTableManager.Instance.Creature2.GetEntry(entity.CreatureId);
            uint activateSpellId = creatureEntry?.Spell4IdActivate00 ?? 0u;
            if (activateSpellId == 0u)
            {
                player.QuestManager.OnCreatureActivated(entity.CreatureId, entity.Guid);
                player.PathMissionManager.OnCreatureActivated(entity.CreatureId, entity.Guid, entity);
                Game.Events.PublicEventEngine.Instance.OnCreatureActivated(player, entity);
                return true;
            }

            // the first of the four activate slots whose prerequisite is met: 156 creatures have several, gated variants first and usually an
            // ungated one last (creature 33548 "Eldan Security Mine": slot 00 needs challenge 515 active, slot 01 is the plain quest disarm)
            uint[] slotSpells        = { creatureEntry.Spell4IdActivate00, creatureEntry.Spell4IdActivate01, creatureEntry.Spell4IdActivate02, creatureEntry.Spell4IdActivate03 };
            uint[] slotPrerequisites = { creatureEntry.PrerequisiteIdActivateSpell00, creatureEntry.PrerequisiteIdActivateSpell01, creatureEntry.PrerequisiteIdActivateSpell02, creatureEntry.PrerequisiteIdActivateSpell03 };
            activateSpellId = 0u;
            for (int slot = 0; slot < slotSpells.Length && activateSpellId == 0u; slot++)
                if (slotSpells[slot] != 0u && (slotPrerequisites[slot] == 0u || NexusForever.WorldServer.Game.Prerequisite.PrerequisiteManager.Instance.Meets(player, slotPrerequisites[slot])))
                    activateSpellId = slotSpells[slot];

            // the activation is a cast of the activate spell, so undecoded prerequisite types are treated like in spell casts (lenient scope:
            // unknown types pass, known ones are still checked) when no slot passes strictly (creature 70846 Holo-Scroll: types 23 + 23 OR)
            for (int slot = 0; slot < slotSpells.Length && activateSpellId == 0u; slot++)
                if (slotSpells[slot] != 0u && slotPrerequisites[slot] != 0u && NexusForever.WorldServer.Game.Prerequisite.PrerequisiteManager.Instance.Meets(player, slotPrerequisites[slot], true))
                {
                    activateSpellId = slotSpells[slot];
                    NexusForever.WorldServer.Diagnostics.FailureLedger.Record("activation-prerequisite-lenient", $"creature {entity.CreatureId} prerequisite {slotPrerequisites[slot]}");
                }

            if (activateSpellId == 0u)
            {
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("activation-prerequisite-failed", $"creature {entity.CreatureId} prerequisite {creatureEntry.PrerequisiteIdActivateSpell00}");
                return false;
            }

            // 10 creatures name an activate spell the client tables do not have (49836 "Dirt Mound" 6754, 10771 6755 ...; server-only spells,
            // likely): the activation itself is credited like a creature without an activate spell, instead of failing in CastSpell
            if (GameTableManager.Instance.Spell4.GetEntry(activateSpellId) == null)
            {
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("activation-spell-missing", $"creature {entity.CreatureId} spell {activateSpellId}");
                player.QuestManager.OnCreatureActivated(entity.CreatureId, entity.Guid);
                player.PathMissionManager.OnCreatureActivated(entity.CreatureId, entity.Guid, entity);
                Game.Events.PublicEventEngine.Instance.OnCreatureActivated(player, entity);
                return true;
            }

            // bots wait out the global/spell cooldown instead of failing the cast
            if (player.Session is NexusForever.WorldServer.Bots.BotSession
                && (player.SpellManager.GetGlobalSpellCooldown() > 0d || player.SpellManager.GetSpellCooldown(activateSpellId) > 0d))
                return false;

            try
            {
                player.CastSpell(activateSpellId, new NexusForever.WorldServer.Game.Spell.SpellParameters
                {
                    UserInitiatedSpellCast = true,
                    PrimaryTargetId        = entity.Guid,
                    IsActivation           = true
                });
                return true;
            }
            catch (System.Exception exception)
            {
                NLog.LogManager.GetLogger("Interact").Warn($"Could not cast activation spell {activateSpellId} of creature {entity.CreatureId}: {exception.Message}");
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("activation-cast-error", $"creature {entity.CreatureId} spell {activateSpellId}", exception.Message);
                return false;
            }
        }

        [MessageHandler(GameMessageOpcode.ClientEntityInteract)]
        public static void HandleClientEntityInteraction(WorldSession session, ClientEntityInteract entityInteraction)
        {
            WorldEntity entity = session.Player.GetVisible<WorldEntity>(entityInteraction.Guid);
            NLog.LogManager.GetLogger("Interact").Info($"{session.Player.Name} interacted with unit {entityInteraction.Guid} event {entityInteraction.Event} (creature {entity?.CreatureId}).");
            if (entity != null)
            {
                session.Player.QuestManager.ObjectiveUpdate(QuestObjectiveType.ActivateEntity, entity.CreatureId, 1u);
                session.Player.QuestManager.ObjectiveUpdate(QuestObjectiveType.TalkTo, entity.CreatureId, 1u);
                foreach (uint targetGroupId in AssetManager.Instance.GetTargetGroupsForCreatureId(entity.CreatureId) ?? Enumerable.Empty<uint>())
                    session.Player.QuestManager.ObjectiveUpdate(QuestObjectiveType.TalkToTargetGroup, targetGroupId, 1u);
            }

            switch (entityInteraction.Event)
            {
                case 37: // Quest NPC
                {
                    session.EnqueueMessageEncrypted(new Server0357
                    {
                        UnitId = entityInteraction.Guid
                    });
                    break;
                }
                case 49: // Handle Vendor
                    VendorHandler.HandleClientVendor(session, entityInteraction);
                    break;
                case 85: // "ContractBoardOpen": the client opens the board itself (window type 85); accepts arrive as ClientQuestAccept at location 300
                    session.Player.QuestManager.ContractBoardOpened(entityInteraction.Guid);
                    break;
                case 68: // "MailboxActivate"
                    var mailboxEntity = session.Player.Map.GetEntity<Mailbox>(entityInteraction.Guid);
                    break;
                case 8: // "HousingGuildNeighborhoodBrokerOpen"
                case 40:
                case 41: // "ResourceConversionOpen"
                case 42: // "ToggleAbilitiesWindow"
                case 43: // "InvokeTradeskillTrainerWindow"
                case 45: // "InvokeShuttlePrompt"
                case 46:
                case 47:
                case 48: // "InvokeTaxiWindow"
                case 65: // "MannequinWindowOpen"
                case 66: // "ShowBank"
                case 67: // "ShowRealmBank"
                case 69: // "ShowDye"
                case 70: // "GuildRegistrarOpen"
                case 71: // "WarPartyRegistrarOpen"
                case 72: // "GuildBankerOpen"
                case 73: // "WarPartyBankerOpen"
                case 75: // "ToggleMarketplaceWindow"
                case 76: // "ToggleAuctionWindow"
                case 79: // "TradeskillEngravingStationOpen"
                case 80: // "HousingMannequinOpen"
                case 81: // "CityDirectionsList"
                case 82: // "ToggleCREDDExchangeWindow"
                case 84: // "CommunityRegistrarOpen"
                case 86: // "BarberOpen"
                case 87: // "MasterCraftsmanOpen"
                default:
                    log.Warn($"Received unhandled interaction event {entityInteraction.Event} from Entity {entityInteraction.Guid}");
                    break;
            }
        }

        [MessageHandler(GameMessageOpcode.ClientEntityInteractChair)]
        public static void HandleClientEntityInteractEmote(WorldSession session, ClientEntityInteractChair interactChair)
        {
            WorldEntity chair = session.Player.GetVisible<WorldEntity>(interactChair.ChairUnitId);
            if (chair == null)
                throw new InvalidPacketValueException();

            Creature2Entry creatureEntry = GameTableManager.Instance.Creature2.GetEntry(chair.CreatureId);
            if ((creatureEntry.ActivationFlags & 0x200000) == 0)
                throw new InvalidPacketValueException();

            session.Player.Sit(chair);
        }
    }
}
