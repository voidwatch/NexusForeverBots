using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NexusForever.Shared.Game.Map;

namespace NexusForever.Shared.IO.Map
{
    public class MapFile : IReadable, IEnumerable<MapFileGrid>
    {
        protected const uint Magic   = 0x504D464Eu; // NFMP
        protected const uint Version = 3u;
        protected const uint Build   = 16042u;

        public string Asset { get; protected set; }

        protected readonly Dictionary<uint /*hash*/, MapFileGrid> grids = new();

        private static (uint gridX, uint gridY) GetGridCoord(Vector3 vector)
        {
            int gridX = MapDefines.WorldGridOrigin + (int)Math.Floor(vector.X / MapDefines.GridSize);
            if (gridX < 0 || gridX > MapDefines.WorldGridCount)
                throw new ArgumentOutOfRangeException($"Position X: {vector.X} is invalid!");

            int gridZ = MapDefines.WorldGridOrigin + (int)Math.Floor(vector.Z / MapDefines.GridSize);
            if (gridZ < 0 || gridZ > MapDefines.WorldGridCount)
                throw new ArgumentOutOfRangeException($"Position Z: {vector.Z} is invalid!");

            return ((uint)gridX, (uint)gridZ);
        }

        public void Read(BinaryReader reader)
        {
            ReadHeader(reader);
            Asset = reader.ReadString();

            uint gridCount = reader.ReadUInt32();
            for (int i = 0; i < gridCount; i++)
            {
                var grid = new MapFileGrid();
                grid.Read(reader);
                grids.Add((grid.X << 16) | grid.Y, grid);
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException();
        }

        public void ReadHeader(BinaryReader reader)
        {
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException();

            if (reader.ReadUInt32() != Version)
                throw new InvalidDataException("Invalid map version, use the MapGenerator to create the latest map files.");

            if (reader.ReadUInt32() != Build)
                throw new InvalidDataException("Invalid client build, use the MapGenerator to create map files from the correct client build.");
        }

        /// <summary>
        /// Return world area id at supplied position.
        /// </summary>
        public uint? GetWorldAreaId(Vector3 vector)
        {
            MapFileGrid grid = GetGrid(vector);
            return grid?.GetWorldZoneId(vector);
        }

        /// <summary>
        /// Return terrain height at supplied position.
        /// </summary>
        public float GetTerrainHeight(Vector3 vector)
        {
            MapFileGrid grid = GetGrid(vector);
            if (grid == null)
                return 0f;

            return grid.GetTerrainHeight(vector);
        }

        /// <summary>
        /// Terrain height when the map has a height grid at the position (instance maps have none, their geometry comes from props).
        /// </summary>
        public bool TryGetTerrainHeight(Vector3 vector, out float height)
        {
            MapFileGrid grid = GetGrid(vector);
            height = grid == null ? 0f : grid.GetTerrainHeight(vector);
            return grid != null;
        }

        /// <summary>
        /// Prop floor heights (.nffloor sidecar), null when the world has none.
        /// </summary>
        public FloorFile Floor { get; set; }

        // an entity walks onto a floor up to this far above its feet (stairs, ramps sampled every 2 m)
        public const float FloorStepUp = 2.5f;
        // larger height changes in one lookup keep the old height: a gap in the floor data (m3 props such as bridges are not in the sidecar)
        // must not drop entities to a lower level
        public const float FloorMaxChange = 8f;

        /// <summary>
        /// Height of the floor an entity at <paramref name="referenceY"/> stands on at x,z, using the prop floors and the terrain grid as layers.
        /// Picks the highest layer at most <see cref="FloorStepUp"/> above the reference; returns false when there is no layer within
        /// <see cref="FloorMaxChange"/>. Without a floor sidecar this is the terrain height (unchanged behaviour).
        /// </summary>
        public bool TryGetFloorHeight(float x, float z, float referenceY, out float height)
        {
            bool hasTerrain = TryGetTerrainHeight(new Vector3(x, 0f, z), out float terrain);
            if (Floor == null)
            {
                height = terrain;
                return hasTerrain;
            }

            float below = float.NegativeInfinity;
            float above = float.PositiveInfinity;
            foreach (float h in Floor.GetHeights(x, z))
                Consider(h, referenceY, ref below, ref above);
            if (hasTerrain)
                Consider(terrain, referenceY, ref below, ref above);

            if (referenceY - below <= FloorMaxChange)
                height = below;
            else if (above - referenceY <= FloorMaxChange)
                height = above;
            else
            {
                height = referenceY;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer (prop floor or terrain) closest to <paramref name="y"/> at x,z, for spawns whose height is only an estimate.
        /// </summary>
        public bool TryGetNearestFloorHeight(float x, float z, float y, out float height)
        {
            height = y;
            float best = float.PositiveInfinity;
            if (Floor != null)
            {
                foreach (float h in Floor.GetHeights(x, z))
                {
                    if (MathF.Abs(h - y) < best)
                    {
                        best   = MathF.Abs(h - y);
                        height = h;
                    }
                }
            }

            if (TryGetTerrainHeight(new Vector3(x, 0f, z), out float terrain) && MathF.Abs(terrain - y) < best)
            {
                best   = MathF.Abs(terrain - y);
                height = terrain;
            }

            return !float.IsPositiveInfinity(best);
        }

        private static void Consider(float h, float referenceY, ref float below, ref float above)
        {
            if (h <= referenceY + FloorStepUp)
            {
                if (h > below)
                    below = h;
            }
            else if (h < above)
                above = h;
        }

        private MapFileGrid GetGrid(Vector3 vector)
        {
            (uint gridX, uint gridY) = GetGridCoord(vector);
            uint hash = (gridY << 16) | gridX;
            return grids.TryGetValue(hash, out MapFileGrid grid) ? grid : null;
        }

        public IEnumerator<MapFileGrid> GetEnumerator()
        {
            return grids.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
