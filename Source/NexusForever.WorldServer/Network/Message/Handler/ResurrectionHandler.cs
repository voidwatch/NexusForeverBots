using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class ResurrectionHandler
    {
        // ClientResurrectRequest (0x00E4, no fields) is kept as a plain Wake Here; the stock death screen sends ClientResurrectAccept with the
        // chosen RezType (death.lua: WakeHere, Holocrypt, SpellCasterLocation, ExitInstance).
        [MessageHandler(GameMessageOpcode.ClientResurrectRequest)]
        public static void HandleResurrectRequest(WorldSession session, ClientResurrectRequest request)
        {
            if (CombatConfig.Enabled)
                session.Player?.TryWakeHere();
        }

        [MessageHandler(GameMessageOpcode.ClientResurrectAccept)]
        public static void HandleResurrectAccept(WorldSession session, ClientResurrectAccept accept)
        {
            if (CombatConfig.Enabled)
                session.Player?.ChooseResurrection(accept.Type);
        }
    }
}
