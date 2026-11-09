using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC6000DualMonitor
{
    // Per-camera rolling diagnostics log. Fixed location, no config changes required.
    // %LOCALAPPDATA%\WILLB\SC6000Monitor\Logs\camera_<ip>.log (one file per camera process).
    internal static class CameraLog
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object _gate = new object();
        public static string PathFor(string cameraIp)
        {
            string safe = string.IsNullOrEmpty(cameraIp) ? "unknown" : cameraIp;
            foreach (char invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WILLB", "SC6000Monitor", "Logs", "camera_" + safe + ".log");
        }
        public static void Write(string cameraIp, string tag, string message)
        {
            try
            {
                string path = PathFor(cameraIp);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + tag + "] " +
                    (message ?? "").Replace("\r", " ").Replace("\n", " ");
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    RotateLocked(path);
                    File.AppendAllText(path, line + "\r\n", Encoding.UTF8);
                }
            }
            catch { /* Diagnostics must never break inspection. */ }
        }
        private static void RotateLocked(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxBytes) return;
                string backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
            catch { }
        }
    }
}
