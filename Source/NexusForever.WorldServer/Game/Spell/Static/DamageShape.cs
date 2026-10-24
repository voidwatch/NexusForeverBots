namespace NexusForever.WorldServer.Game.Spell.Static
{
    public enum DamageShape
    {
        Circle        = 0,
        Ring          = 1,
        Square        = 2,
        /// <summary>
        /// Two crossed rectangles (spells "Thunder Cross", "X Marks the Spot", 45 degree rotated copies make an X). Params like Square:
        /// 00 width of an arm (guess), 01 height, 02 tip to tip length (copies of "Tentacular Retaliation" sit 02/2 apart);
        /// 03 when set the length of the second arm (17 of 104 rows, guess).
        /// </summary>
        Cross         = 3,
        Cone          = 4,
        /// <summary>
        /// This is a pie shape, where the radius value is the size of the missing slice.
        /// </summary>
        Pie           = 5,
        /// <summary>
        /// Ellipsoid (spell "Test Ellipsoid Telegraphs"), params 00/01/02 are the X (width), Y (height) and Z (length) radii: "Body Slam" 5/30/5, "Blaze" 75/2.5/75.
        /// </summary>
        Ellipsoid     = 6,
        Rectangle     = 7,
        LongCone      = 8
    }
}
