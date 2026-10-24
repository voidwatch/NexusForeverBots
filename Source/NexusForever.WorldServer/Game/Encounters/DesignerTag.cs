using System;
using System.Text.RegularExpressions;

namespace NexusForever.WorldServer.Game.Encounters
{
    /// <summary>
    /// The designer description of an encounter spell in Spell4: "[STL] e314 - (N/V) Stormtalon - Static Wave - Base - Tier 1".
    /// Tag = instance (STL, KV, SK, TS, GA, DS, RMT, ...), Encounter = the e### id shared by every spell of one boss fight,
    /// Mode = N (normal), V (veteran), N/V or empty (both), Boss = the designer name (not always the client name: Kuralak is "Genetic Architect").
    /// </summary>
    public sealed class DesignerTag
    {
        private static readonly Regex pattern = new(@"^(?:\[Dungeon\])?\[(?<tag>[A-Z0-9]{2,4})\]\s*e(?<enc>\d+)\s*-\s*(?:\((?<mode>[NV/]+)\)\s*)?(?<boss>[^-]+?)\s*-\s*(?<rest>.*)$",
            RegexOptions.Compiled);

        public string Tag { get; private init; }
        public uint Encounter { get; private init; }
        public string Mode { get; private init; }
        public string Boss { get; private init; }
        public string Rest { get; private init; }

        public static DesignerTag Parse(string description)
        {
            if (string.IsNullOrEmpty(description) || description[0] != '[')
                return null;

            Match m = pattern.Match(description);
            if (!m.Success)
                return null;

            return new DesignerTag
            {
                Tag       = m.Groups["tag"].Value,
                Encounter = uint.Parse(m.Groups["enc"].Value),
                Mode      = m.Groups["mode"].Success ? m.Groups["mode"].Value : "",
                Boss      = m.Groups["boss"].Value,
                Rest      = m.Groups["rest"].Value
            };
        }

        /// <summary>
        /// False for a spell marked for the other difficulty only: "(V)" or a " - Veteran - " segment in a normal run, "(N)" or a " - Normal - "
        /// segment in a veteran run (10 and 12 Spell4 rows use the segment form, e.g. "[KV] e444 - Grond - Bloodburst - Veteran - Base", which
        /// summons the level 50 "Bloodspawn Flea - Veteran"; mortal bots died 51 times to it in normal Kel Voreth).
        /// </summary>
        public bool FitsMode(bool veteran)
        {
            if (Mode == "V" || Rest.Contains("(V)", StringComparison.Ordinal) || HasSegment("Veteran"))
                return veteran;
            if (Mode == "N" || HasSegment("Normal"))
                return !veteran;
            return true;
        }

        private bool HasSegment(string word)
        {
            foreach (string segment in Rest.Split(" - "))
                if (segment.Trim().Equals(word, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// True for parts of a spell the boss never starts itself: proxies (cast by the base spell), messaging, visuals, intro flavour,
        /// auto-attacks (the melee formula does those) and test or deprecated rows.
        /// </summary>
        public bool IsHelper
        {
            get
            {
                foreach (string word in new[] { "Proxy", "Messaging", "Message", "Visual", "FX", "Flavor", "Fluff", "Intro", "Auto-Attack", "Auto Attack",
                    "Name Change", "Name Revert", "Interrupt Armor", "Cam Shake", "[TEST]", "DEPRECATE" })
                    if (Rest.Contains(word, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }
    }
}
