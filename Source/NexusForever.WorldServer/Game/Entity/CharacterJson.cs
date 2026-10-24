using System;
using System.IO;
using System.Text.Json;
using NexusForever.WorldServer.Diagnostics;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Per character state kept as a JSON file in the cache volume, for systems that have no database table yet (path missions, tradeskills, daily login).
    /// ASSUMPTION (docs/ASSUMPTIONS.md "Character JSON state"): good enough for a QA server, replace by database tables with migrations before a public launch.
    /// </summary>
    public sealed class CharacterJson<T> where T : class, new()
    {
        private readonly string folder;
        private readonly ulong characterId;
        private T value;

        public CharacterJson(string name, ulong characterId)
        {
            this.characterId = characterId;
            folder = Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, name);
        }

        public bool Dirty { get; set; }

        public T Value => value ??= Load();

        private string FilePath => Path.Combine(folder, $"{characterId}.json");

        private T Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath)) ?? new T();
            }
            catch (Exception ex)
            {
                FailureLedger.Record("character-json-load-failed", $"{Path.GetFileName(folder)} {ex.GetType().Name}", ex.Message);
            }

            return new T();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Value));
                Dirty = false;
            }
            catch (Exception ex)
            {
                FailureLedger.Record("character-json-save-failed", $"{Path.GetFileName(folder)} {ex.GetType().Name}", ex.Message);
            }
        }
    }
}
