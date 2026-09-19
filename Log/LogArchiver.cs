using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MC.Log
{
    internal class LogArchiver
    {
        public void Archive(string directoryPath, string activeFileName)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException(nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
                return;

            ArchiveCurrentDirectory(directoryPath, activeFileName);
        }
        
        private static void ReplaceZip(
            string tempZipPath,
            string zipPath)
        {
            if (File.Exists(zipPath))
            {
                File.Replace(
                    tempZipPath,
                    zipPath,
                    null);
            }
            else
            {
                File.Move(
                    tempZipPath,
                    zipPath);
            }
        }

        /// <summary>
        /// Archiwizuje stare pliki w aktualnym katalogu
        /// </summary>
        private void ArchiveCurrentDirectory(
            string directoryPath,
            string activeFileName)
        {
            var dirPath = directoryPath;

            if (!Directory.Exists(dirPath))
                return;

            var zipPath = dirPath + ".zip";
            var tempZipPath = zipPath + ".tmp";

            // Tylko te pliki będziemy mogli później usunąć.
            var archivedFiles = new List<string>();

            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                var files = Directory
                   .EnumerateFiles(dirPath)
                   .ToList();

                var fileNames = new HashSet<string>(
                    files.Select(Path.GetFileName),
                    StringComparer.OrdinalIgnoreCase);

                using (var zip = ZipFile.Open(
                    tempZipPath,
                    ZipArchiveMode.Create))
                {
                    // Zachowujemy wcześniejsze wpisy z ZIP-a.
                    if (File.Exists(zipPath))
                    {
               

                        using (var oldZip = ZipFile.OpenRead(zipPath))
                        {
                            foreach (var entry in oldZip.Entries)
                            {
                                if (fileNames.Contains(entry.FullName))
                                {
                                    continue;
                                }

                                var newEntry = zip.CreateEntry(
                                    entry.FullName,
                                    CompressionLevel.Optimal);

                                using (var source = entry.Open())
                                using (var destination = newEntry.Open())
                                {
                                    source.CopyTo(destination);
                                }
                            }
                        }
                    }

                    // Dodajemy tylko zamknięte pliki.
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        // Aktualnego loga nie archiwizujemy.
                        if (string.Equals(
                            fileName,
                            activeFileName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            zip.CreateEntryFromFile(
                                file,
                                fileName,
                                CompressionLevel.Optimal);

                            archivedFiles.Add(file);
                        }
                        catch (IOException ex)
                        {
                            Debug.WriteLine(
                                $"Failed to archive file '{file}': {ex}");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Debug.WriteLine(
                                $"Access denied for file '{file}': {ex}");
                        }
                    }
                }

                // ZIP.tmp został poprawnie zamknięty.
                // Dopiero teraz podmieniamy właściwy ZIP.
                ReplaceZip(
                    tempZipPath,
                    zipPath);

                // Dopiero po poprawnej podmianie ZIP-a
                // usuwamy pliki, które faktycznie zostały zarchiwizowane.
                foreach (var file in archivedFiles)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"Failed to delete archived file '{file}': {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"ArchiveCurrentDirectory failed for '{dirPath}': {ex}");

                try
                {
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine(
                        $"Failed to cleanup temporary ZIP '{tempZipPath}': {cleanupEx}");
                }
            }
        }
    }
}
