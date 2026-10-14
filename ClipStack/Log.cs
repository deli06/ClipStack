using System;
using System.Diagnostics;
using System.IO;

namespace ClipStack
{
    /// <summary>
    /// Hataları %LOCALAPPDATA%\ClipStack\log.txt dosyasına yazar. Release sürümünde Debug.WriteLine
    /// görünmediği için sorunların izini sürmenin tek yolu budur. Dosya 1 MB'ı geçince bir öncekine taşınır.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new();

        public static string FilePath => PathOverride ?? Path.Combine(Settings.DataFolder, "log.txt");

        /// <summary>Testler gerçek log dosyasına yazmasın diye.</summary>
        internal static string? PathOverride { get; set; }

        public static void Error(string message, Exception? ex = null) => Write("HATA", message, ex);

        public static void Info(string message) => Write("BİLGİ", message, null);

        private static void Write(string level, string message, Exception? ex)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex != null) line += Environment.NewLine + ex;
            Debug.WriteLine(line);

            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                        File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Log yazılamıyorsa uygulamayı düşürmeye değmez.
            }
        }
    }
}
