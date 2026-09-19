using System;
using System.Diagnostics;
using System.IO;

namespace MC.Log
{
    public sealed class LogFileManager
    {
        private readonly string _baseFolder;
        private readonly LogArchiver _archiver;
        private readonly string _baseDirectory;

        public LogFileManager(string baseFolder = "Log", string baseDirectory = null)
        {
            _archiver = new LogArchiver();
            _baseFolder = baseFolder;
            _baseDirectory = string.IsNullOrEmpty(baseDirectory)
                            ? AppDomain.CurrentDomain.BaseDirectory
                            : baseDirectory;
        }

        private string BasePath =>
            Path.Combine(_baseDirectory, _baseFolder);

        /// <summary>
        /// Zwraca pełną ścieżkę do aktualnego pliku logu
        /// </summary>
        public string GetLogFilePath(string fileName, DateTime date)
        {
            var dir = EnsureLogDirectories(date);
            return Path.Combine(dir, $"{fileName}_{date.Day}.txt");
        }
        /// <summary>
        /// Tworzy strukturę: Log/year/month
        /// </summary>
        public string EnsureLogDirectories(DateTime date)
        {
            var yearPath = Path.Combine(BasePath, date.Year.ToString());
            var monthPath = Path.Combine(yearPath, date.Month.ToString());

            try
            {
                if (!Directory.Exists(yearPath))
                    Directory.CreateDirectory(yearPath);

                if (!Directory.Exists(monthPath))
                    Directory.CreateDirectory(monthPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnsureLogDirectories error: {ex.Message}");
            }

            return monthPath;
        }
        /// <summary>
        /// Zwraca folder miesiąca (np. Log/2026/6)
        /// </summary>
        public string GetMonthFolder(DateTime date)
            => Path.Combine(BasePath, date.Year.ToString(), date.Month.ToString());
        /// <summary>
        /// Zwraca folder roku
        /// </summary>
        public string GetYearFolder(DateTime date) 
            => Path.Combine(BasePath, date.Year.ToString());
        /// <summary>
        /// Sprawdza czy folder logów istnieje
        /// </summary>
        public bool Exists(DateTime date)
        {
            return Directory.Exists(GetMonthFolder(date));
        }
        /// <summary>
        /// Czyści stare foldery (prosta wersja Twojej logiki retention)
        /// </summary>
        public void CleanupOldLogs()
        {
            var basePath = BasePath;

            if (!Directory.Exists(basePath))
                return;
            
            //var pY = GetYearFolder(DateTime.Now);
            var currentLogFileName = $"{_baseFolder}_{DateTime.Now.Day}.txt";
            var monthPath = GetMonthFolder(DateTime.Now);

            _archiver.Archive(monthPath, currentLogFileName);
        }
    }
}
