using System;
using System.IO;
using System.Linq;
using Nexus.Archive;
using NexusForever.Shared;
using NLog;

namespace NexusForever.MapGenerator
{
    public sealed class ExtractionManager : Singleton<ExtractionManager>
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        private static readonly string[] languageFiles =
        {
            "en-US.bin",
            "de-DE.bin",
            "fr-FR.bin",
            "en-GB.bin"
        };

        private ExtractionManager()
        {
        }

        public void Initialise()
        {
            // NF_EXTRACT_PATTERN="UI/Vendor/*.lua;UI/Challenges/*.lua": extract only those client files into files/<path> (research, read only)
            string patterns = Environment.GetEnvironmentVariable("NF_EXTRACT_PATTERN");
            if (!string.IsNullOrEmpty(patterns))
            {
                foreach (string pattern in patterns.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    foreach (IArchiveFileEntry fileEntry in ArchiveManager.Instance.MainArchive.IndexFile.GetFiles(pattern))
                        ExtractFile(ArchiveManager.Instance.MainArchive, fileEntry, Path.Combine("files", Path.GetDirectoryName(pattern) ?? ""));
                return;
            }

            log.Info("Extracting GameTables...");

            Directory.CreateDirectory("tbl");

            ExtractGameTables();
            ExtractLanguageFiles();
        }

        /// <summary>
        /// Extract all GameTables (*.tbl) from main client archive.
        /// </summary>
        private void ExtractGameTables()
        {
            string searchPattern = Path.Combine("DB", "*.tbl");
            foreach (IArchiveFileEntry fileEntry in ArchiveManager.Instance.MainArchive.IndexFile.GetFiles(searchPattern))
                ExtractFile(ArchiveManager.Instance.MainArchive, fileEntry);
        }

        /// <summary>
        /// Extract all language files (*.bin) from all present localisation client archives.
        /// </summary>
        private void ExtractLanguageFiles()
        {
            foreach (Archive archive in ArchiveManager.Instance.LocalisationArchives)
            {
                foreach (IArchiveFileEntry fileEntry in languageFiles
                    .Select(archive.IndexFile.FindEntry)
                    .OfType<IArchiveFileEntry>())
                {
                    ExtractFile(archive, fileEntry);
                }
            }
        }

        /// <summary>
        /// Extract supplied <see cref="IArchiveFileEntry"/> from <see cref="Archive"/>.
        /// </summary>
        private void ExtractFile(Archive archive, IArchiveFileEntry fileEntry, string directory = "tbl")
        {
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, fileEntry.FileName);

            using (Stream archiveStream = archive.OpenFileStream(fileEntry))
            using (FileStream fileStream = File.OpenWrite(filePath))
            {
                archiveStream.CopyTo(fileStream);
            }

            log.Info($"Extracted {fileEntry.FileName}...");
        }
    }
}
