using System;

namespace NexusForever.WorldServer.Game.Combat
{
    [Flags]
    public enum ResurrectionType
    {
        None                 = 0x00,
        WakeHere             = 0x01,
        Holocrypt            = 0x02,
        SpellCasterLocation  = 0x04,
        ExitInstance         = 0x20,
        WakeHereServiceToken = 0x40
    }
}
