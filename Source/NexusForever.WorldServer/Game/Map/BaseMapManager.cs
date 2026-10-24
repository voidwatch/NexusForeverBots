using System.Collections.Generic;
using System.IO;
using NexusForever.Shared;
using NexusForever.Shared.Configuration;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.IO.Map;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    public sealed class BaseMapManager : Singleton<BaseMapManager>
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, MapFile> mapFiles = new();

        private BaseMapManager()
        {
        }

        public void Initialise()
        {
            ValidateMapFiles();
            PreCacheMapFiles();
        }

        private void ValidateMapFiles()
        {
            log.Info("Validating map files...");

            string mapPath = ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.MapPath;
            if (mapPath == null || !Directory.Exists(mapPath))
                throw new DirectoryNotFoundException("Invalid path to base maps! Make sure you have set it in the configuration file.");

            foreach (string fileName in Directory.EnumerateFiles(mapPath, "*.nfmap"))
            {
                using FileStream stream = File.OpenRead(fileName);
                using var reader = new BinaryReader(stream);
                var mapFile = new MapFile();
                mapFile.ReadHeader(reader);
            }
        }

        private void PreCacheMapFiles()
        {
            log.Info("Caching map files...");

            List<ushort> precachedBaseMaps = ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.PrecacheBaseMaps;
            if (precachedBaseMaps == null)
                return;

            foreach (ushort worldId in precachedBaseMaps)
            {
                WorldEntry entry = GameTableManager.Instance.World.GetEntry(worldId);
                if (entry == null)
                    throw new ConfigurationException($"Invalid world id {worldId} supplied for precached base maps!");

                LoadBaseMap(entry.AssetPath);
            }
        }

        /// <summary>
        /// Returns an existing <see cref="MapFile"/> for the supplied asset, if it doesn't exist a new one will be created from disk.
        /// </summary>
        public MapFile GetBaseMap(string assetPath)
        {
            // maps update in parallel, the dictionary and the load of a file must be one critical section
            lock (mapFiles)
            {
                if (mapFiles.TryGetValue(assetPath, out MapFile mapFile))
                    return mapFile;

                return LoadBaseMap(assetPath);
            }
        }

        /// <summary>
        /// Forget a loaded map file so its memory can be freed (open world maps are 200 to 500 MB each when loaded and stayed for the life of the process).
        /// </summary>
        public void UnloadBaseMap(string assetPath)
        {
            lock (mapFiles)
                mapFiles.Remove(assetPath);
        }

        /// <summary>
        /// Returns true when the map file of the asset is loaded or exists on disk (some worlds in the tables have no map data).
        /// </summary>
        public bool HasBaseMap(string assetPath)
        {
            lock (mapFiles)
            {
                if (mapFiles.ContainsKey(assetPath))
                    return true;
            }

            string mapPath = ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.MapPath;
            string asset   = Path.Combine(mapPath, Path.GetFileName(assetPath.Replace('\\', '/')));
            return File.Exists(Path.ChangeExtension(asset, ".nfmap"));
        }

        private MapFile LoadBaseMap(string assetPath)
        {
            string mapPath  = ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.MapPath;
            // asset paths in the game tables use Windows separators (Map\Western), normalise so this also works on Linux
            string asset    = Path.Combine(mapPath, Path.GetFileName(assetPath.Replace('\\', '/')));
            string filePath = Path.ChangeExtension(asset, ".nfmap");

            if (!File.Exists(filePath))
            {
                // no terrain exists for this world in the client files (some instances): prop floors when the sidecar exists, else a flat
                // plane at height 0, see docs/ASSUMPTIONS.md
                var empty = new MapFile();
                mapFiles.Add(assetPath, empty);
                LoadFloor(empty, asset);
                if (empty.Floor == null)
                    Diagnostics.FailureLedger.Record("map-file-missing-flat-fallback", assetPath);
                return empty;
            }

            using FileStream stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);
            var mapFile = new MapFile();
            mapFiles.Add(assetPath, mapFile);
            mapFile.Read(reader);
            LoadFloor(mapFile, asset);

            log.Trace($"Initialised base map file for asset {assetPath}.");
            return mapFile;
        }

        /// <summary>
        /// Attach the prop floor sidecar (.nffloor, scripts/research/floorgen.py) of instance worlds whose geometry is props.
        /// </summary>
        private static void LoadFloor(MapFile mapFile, string asset)
        {
            string floorPath = Path.ChangeExtension(asset, ".nffloor");
            if (!File.Exists(floorPath))
                return;

            try
            {
                mapFile.Floor = FloorFile.Load(floorPath);
                log.Info($"Loaded prop floor heights {floorPath}.");
            }
            catch (System.Exception e)
            {
                Diagnostics.FailureLedger.Record("map-floor-file-invalid", floorPath, e.Message);
            }
        }
    }
}
