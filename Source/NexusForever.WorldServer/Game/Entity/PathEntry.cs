using System.Linq;
using Microsoft.EntityFrameworkCore;
﻿using System;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    public class PathEntry : ISaveCharacter
    {
        public Path Path { get; set;  }
        public ulong CharacterId { get; set; }

        public bool Unlocked
        {
            get => unlocked;
            set
            {
                if (value != unlocked)
                {
                    unlocked = value;
                    saveMask |= PathSaveMask.Unlocked;
                }
            }
        }
        private bool unlocked;

        public uint TotalXp
        {
            get => totalXp;
            set
            {
                if (value < totalXp)
                    throw new ArgumentException("New Level Rewarded Value must be higher, and not equal to current XP total.");

                totalXp = value;
                saveMask |= PathSaveMask.XPChange;
            }
        }
        private uint totalXp;

        public byte LevelRewarded
        {
            get => levelRewarded;
            set
            {
                if (value < levelRewarded)
                    throw new ArgumentException("New Level Rewarded Value must be higher, and not equal to current XP total.");

                levelRewarded = value;
                saveMask |= PathSaveMask.LevelChange;
            }
        }
        private byte levelRewarded;

        private PathSaveMask saveMask;

        /// <summary>
        /// Create a new <see cref="PathEntry"/> for a <see cref="Player"/> from <see cref="CharacterPathModel"/>
        /// </summary>
        public PathEntry(CharacterPathModel model)
        {
            CharacterId   = model.Id;
            Path          = (Path)model.Path;
            unlocked      = Convert.ToBoolean(model.Unlocked);
            totalXp       = model.TotalXp;
            levelRewarded = model.LevelRewarded;
            
            saveMask      = PathSaveMask.None;
        }

        /// <summary>
        /// Create a new <see cref="PathEntry"/>
        /// </summary>
        public PathEntry(ulong owner, Path path, bool isUnlocked)
        {
            CharacterId = owner;
            Path        = path;
            unlocked    = isUnlocked;

            saveMask    = PathSaveMask.Create;
        }

        /// <summary>
        /// Save the <see cref="CharacterPathModel"/> with it's current state
        /// </summary>
        /// <param name="context">The character context to save against</param>
        private readonly object saveLock = new();

        public void Save(CharacterContext context)
        {
            // two overlapping player saves must not both create the same row
            lock (saveLock)
                SaveLocked(context);
        }

        private void SaveLocked(CharacterContext context)
        {
            if (saveMask == PathSaveMask.None)
                return;

            // a Create that already reached the database (earlier save raced or was retried) becomes a full update
            if ((saveMask & PathSaveMask.Create) != 0
                && (context.CharacterPath.Local.Any(p => p.Id == CharacterId && p.Path == (byte)Path)
                    || context.CharacterPath.AsNoTracking().Any(p => p.Id == CharacterId && p.Path == (byte)Path)))
                saveMask = PathSaveMask.Unlocked | PathSaveMask.XPChange | PathSaveMask.LevelChange;

            // creates and updates are both a full upsert: an update of a row that never reached the database (0 rows affected) must not fail the whole save
            byte unlockedByte = Convert.ToByte(Unlocked);
            byte pathByte     = (byte)Path;
            context.Database.ExecuteSqlInterpolated($"INSERT INTO character_path (id, path, unlocked, totalXp, levelRewarded) VALUES ({CharacterId}, {pathByte}, {unlockedByte}, {TotalXp}, {LevelRewarded}) ON DUPLICATE KEY UPDATE unlocked = VALUES(unlocked), totalXp = VALUES(totalXp), levelRewarded = VALUES(levelRewarded)");

            saveMask = PathSaveMask.None;
        }
    }
}
