using System;
using System.Collections.Generic;
using System.IO;

namespace Faderly.Services
{
    /// <summary>
    /// Tiny diagnostic log at %AppData%\Faderly\faderly.log (capped at ~256 KB, one backup).
    /// Errors that the app recovers from silently still land here, so problems can be traced.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 256 * 1024;

        private static readonly object Gate = new();
        private static readonly HashSet<string> Seen = new();

        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Faderly");

        public static string FilePath { get; } = Path.Combine(Folder, "faderly.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Error(string where, Exception ex) => Write("ERROR", where, ex);

        /// <summary>Logs a recoverable problem once per run, so a fault that repeats every second can't flood the file.</summary>
        public static void WarnOnce(string where, Exception ex)
        {
            lock (Gate)
            {
                if (!Seen.Add(where + "|" + ex.GetType().FullName)) return;
            }
            Write("WARN", where, ex);
        }

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);

                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (ex != null) line += Environment.NewLine + ex;
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
