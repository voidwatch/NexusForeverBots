namespace NexusForever.WorldServer.Game.Spell.Static
{
    /// <summary>
    /// Crowd control states set by the CCStateSet spell effect (DataBits00) and cleared by CCStateBreak (a bit mask of these values).
    /// Ids are the rows of CCStates.tbl (certain: the table names them, and the client's CC condition check returns
    /// Caster/Target(Must|Cannot)Be&lt;name&gt; for bit n in the same order). 19-25 were one lower before (PullToLocation 19, Snare 20...);
    /// the effect data agrees with the table: 20 Tether Mine/Tether Bolt, 21 Chill/Cripple, 22 Kick/Arcane Shock, 19 Spatial Shift.
    /// </summary>
    public enum CCState
    {
        Stun                = 0,
        Sleep               = 1,
        Root                = 2,
        Disarm              = 3,
        Silence             = 4,
        Polymorph           = 5,
        Fear                = 6,
        Hold                = 7,
        Knockdown           = 8,
        Vulnerability       = 9,
        VulnerabilityWithAct = 10,
        Disorient           = 11,
        Disable             = 12,
        Taunt               = 13,
        DeTaunt             = 14, // CCStates.tbl: "Pacify" (Calm, Fade Out)
        Blind               = 15,
        Knockback           = 16,
        Pushback            = 17,
        Pull                = 18,
        PositionSwitch      = 19,
        Tether              = 20,
        Snare               = 21,
        Interrupt           = 22,
        Daze                = 23,
        Subdue              = 24,
        Grounded            = 25,
        DisableCinematic    = 26,
        AbilityRestriction  = 27
    }
}
