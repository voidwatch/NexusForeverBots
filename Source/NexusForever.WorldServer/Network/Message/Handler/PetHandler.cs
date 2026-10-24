using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class PetHandler
    {
        [MessageHandler(GameMessageOpcode.ClientSummonVanityPet)]
        public static void HandleClientSummonVanityPet(WorldSession session, ClientSummonVanityPet summonVanityPet)
        {
            CharacterSpell spell = session.Player.SpellManager.GetSpell(summonVanityPet.Spell4BaseId);
            if (spell == null)
                throw new InvalidPacketValueException();

            byte tier = session.Player.SpellManager.GetSpellTier(spell.BaseInfo.Entry.Id);
            session.Player.CastSpell(new SpellParameters
            {
                SpellInfo = spell.BaseInfo.GetSpellInfo(tier)
            });
        }

        [MessageHandler(GameMessageOpcode.ClientPetCustomisation)]
        public static void HandleClientPetCustomisation(WorldSession session, ClientPetCustomisation petcustomisation)
        {
            session.Player.PetCustomisationManager.AddCustomisation(petcustomisation.PetType,
                petcustomisation.PetObjectId,
                petcustomisation.FlairSlotIndex,
                petcustomisation.FlairId);
        }

        [MessageHandler(GameMessageOpcode.ClientPetStanceChange)]
        public static void HandleClientPetStanceChange(WorldSession session, ClientPetStanceChange request)
        {
            Player player = session.Player;
            NonPlayer pet = player?.GetVisible<NonPlayer>(request.PetUnitId);
            if (pet == null || pet.PetOwnerGuid != player.Guid)
            {
                Diagnostics.FailureLedger.Record("pet-stance-refused", "not a pet of the player", $"unit {request.PetUnitId}");
                return;
            }

            if (request.Stance is not (1 or 2 or 4 or 8 or 16))
            {
                Diagnostics.FailureLedger.Record("pet-stance-refused", "stance is not one of the flags 1, 2, 4, 8, 16", $"stance {request.Stance}");
                return;
            }

            pet.PetStance = request.Stance;
            session.EnqueueMessageEncrypted(new ServerChangePetStance
            {
                PetUnitId = pet.Guid,
                Stance    = request.Stance
            });
            Diagnostics.FailureLedger.Record("pet-stance", $"stance {request.Stance} set (stored, behaviour not decoded)", $"creature {pet.CreatureId}");
        }
    }
}
