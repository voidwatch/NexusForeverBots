using System;
using System.Collections.Generic;

namespace NexusForever.WorldServer.Bots
{
    public static class BotNames
    {
        private static readonly string[] first =
        {
            "Aer", "Bal", "Cor", "Dra", "Eld", "Fen", "Gal", "Hor", "Ith", "Jar", "Kor", "Lyr", "Mor", "Nyx", "Orl", "Pel",
            "Quin", "Ral", "Syl", "Tor", "Ulm", "Vex", "Wyn", "Xan", "Yor", "Zar", "Bran", "Cael", "Dov", "Esk", "Fael", "Grim"
        };

        private static readonly string[] second =
        {
            "an", "ara", "eth", "iel", "ion", "ix", "lyn", "mir", "on", "or", "rin", "sha", "thor", "us", "wen", "zen",
            "dar", "gorn", "kai", "las", "mar", "nis", "ric", "tan", "vek", "wyn", "yth", "zul", "bel", "cor", "dal", "fen"
        };

        /// <summary>
        /// Generate a fantasy style name, for example "Corwen". About a thousand distinct names before a numeric suffix is needed.
        /// </summary>
        private static readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Remember a name that is in use so no other bot picks it (two bots creating the same name at once would collide in the world).
        /// </summary>
        public static void Reserve(string name)
        {
            if (name != null)
                reserved.Add(name);
        }

        /// <summary>
        /// Generate a name no other bot in this process has used.
        /// </summary>
        public static string GenerateUnique(Random random, bool suffix = false)
        {
            for (int i = 0; i < 30; i++)
            {
                string name = Generate(random, suffix || i >= 15);
                if (reserved.Add(name))
                    return name;
            }

            string fallback = Generate(random, true) + random.Next(0, 10);
            reserved.Add(fallback);
            return fallback;
        }

        public static string Generate(Random random, bool suffix = false)
        {
            string name = first[random.Next(first.Length)] + second[random.Next(second.Length)];
            return suffix ? name + random.Next(10, 99) : name;
        }
    }
}
