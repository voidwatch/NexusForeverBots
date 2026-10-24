using System;
using System.IO;

namespace NexusForever.Shared.IO.Map
{
    /// <summary>
    /// Floor heights of worlds whose geometry is props (dungeon .dgn and building .i3 sectors), written by scripts/research/floorgen.py
    /// as a sidecar (.nffloor) next to the .nfmap. Every cell holds the heights of all upward-facing surfaces, ascending, so several floors
    /// can be stacked. See docs/ASSUMPTIONS.md (Instance floor heights).
    /// </summary>
    public class FloorFile
    {
        private const uint Magic   = 0x4C46464Eu; // NFFL
        private const uint Version = 2u;
        private const uint Empty   = 0xFFFFFFFFu;

        private float cell;
        private int tile;
        private int tileX0;
        private int tileZ0;
        private int tilesW;
        private int tilesD;
        private uint[] index;
        private uint[] starts;
        private float[] heights;

        public static FloorFile Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
                throw new InvalidDataException($"{path} is not a NFFL v{Version} floor file, rerun scripts/research/floorgen.py.");

            var file = new FloorFile
            {
                cell   = reader.ReadSingle(),
                tile   = (int)reader.ReadUInt32(),
                tileX0 = reader.ReadInt32(),
                tileZ0 = reader.ReadInt32(),
                tilesW = (int)reader.ReadUInt32(),
                tilesD = (int)reader.ReadUInt32()
            };
            uint heightCount = reader.ReadUInt32();

            file.index = new uint[file.tilesW * file.tilesD];
            int tileCount = 0;
            for (int i = 0; i < file.index.Length; i++)
            {
                file.index[i] = reader.ReadUInt32();
                if (file.index[i] != Empty)
                    tileCount++;
            }

            file.starts = new uint[tileCount * (file.tile * file.tile + 1)];
            for (int i = 0; i < file.starts.Length; i++)
                file.starts[i] = reader.ReadUInt32();

            file.heights = new float[heightCount];
            for (int i = 0; i < file.heights.Length; i++)
                file.heights[i] = reader.ReadSingle();

            if (stream.Position != stream.Length)
                throw new InvalidDataException($"{path} has trailing data.");

            return file;
        }

        /// <summary>
        /// Floor heights (ascending) at the position, empty when the props have no floor there.
        /// </summary>
        public ReadOnlySpan<float> GetHeights(float x, float z)
        {
            int ix = (int)MathF.Floor(x / cell);
            int iz = (int)MathF.Floor(z / cell);
            int tx = FloorDiv(ix, tile) - tileX0;
            int tz = FloorDiv(iz, tile) - tileZ0;
            if (tx < 0 || tz < 0 || tx >= tilesW || tz >= tilesD)
                return ReadOnlySpan<float>.Empty;

            uint t = index[tz * tilesW + tx];
            if (t == Empty)
                return ReadOnlySpan<float>.Empty;

            long c = t * (long)(tile * tile + 1) + (iz - FloorDiv(iz, tile) * tile) * tile + (ix - FloorDiv(ix, tile) * tile);
            uint start = starts[c];
            return new ReadOnlySpan<float>(heights, (int)start, (int)(starts[c + 1] - start));
        }

        private static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : -((-a + b - 1) / b);
        }
    }
}
