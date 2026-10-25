using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SC6000DualMonitor
{
    internal sealed class NgRecord
    {
        public string ImagePath, Camera, Recipe, ViewName;
        public bool IsOriginal;
        public long FileLength;
        public DateTime TimeUtc;
        public uint Frame;
        public string Description { get { return Camera + " | " + TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") +
            "\r\n레시피: " + (string.IsNullOrEmpty(Recipe) ? "경로 확인 불가" : Recipe) + "\r\n검사 회차: " + Frame + " | " + ViewName; } }
    }

    internal sealed class NgHistoryStore
    {
        public string DirectoryPath { get; private set; }
        public NgHistoryStore(string directory) { DirectoryPath = Path.GetFullPath(directory); }
        public static string DefaultDirectory(string cameraIp)
        {
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(cameraIp ?? ""))).Replace("-", "").Substring(0, 16);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WILLB", "SC6000Monitor", "NgHistory", key);
        }
        public NgRecord Save(byte[] png, NgRecord record)
        {
            Directory.CreateDirectory(DirectoryPath);
            string id = record.TimeUtc.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(DirectoryPath, id + ".png");
            string manifest = Path.Combine(DirectoryPath, id + ".xml");
            try
            {
                File.WriteAllBytes(path + ".tmp", png);
                File.Move(path + ".tmp", path);
                new XDocument(new XElement("ng", new XAttribute("version", 1),
                    new XElement("time", record.TimeUtc.ToString("o", CultureInfo.InvariantCulture)),
                    new XElement("camera", record.Camera ?? ""), new XElement("recipe", record.Recipe ?? ""),
                    new XElement("view", record.ViewName ?? ""), new XElement("frame", record.Frame))).Save(manifest + ".tmp");
                File.Move(manifest + ".tmp", manifest); // Completed records become visible only after the PNG is durable.
                record.ImagePath = path;
                return record;
            }
            catch
            {
                // These unique files belong only to this failed write; never delete existing user captures.
                foreach (string file in new[] { path + ".tmp", manifest + ".tmp", path })
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                throw;
            }
        }
        public IList<NgRecord> LoadRecent(int count)
        {
            var records = new List<NgRecord>();
            if (!Directory.Exists(DirectoryPath)) return records;
            foreach (string manifest in Directory.EnumerateFiles(DirectoryPath, "*.xml").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                if (records.Count >= count) break;
                try
                {
                    if (new FileInfo(manifest).Length > 65536) continue;
                    XElement root;
                    using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        root = XDocument.Load(reader).Root;
                    string image = Path.ChangeExtension(manifest, ".png");
                    if (root == null || root.Name != "ng" || !File.Exists(image)) continue;
                    records.Add(new NgRecord { ImagePath = image, Camera = (string)root.Element("camera"), Recipe = (string)root.Element("recipe"),
                        ViewName = (string)root.Element("view"), Frame = (uint)root.Element("frame"),
                        TimeUtc = DateTime.Parse((string)root.Element("time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime() });
                }
                catch (Exception ex) when (ex is IOException || ex is XmlException || ex is FormatException || ex is OverflowException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            }
            return records.OrderByDescending(r => r.TimeUtc).ToList();
        }
    }
}
