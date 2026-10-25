using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SC6000DualMonitor;

class ReplayTests
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Fails(Action action, string name) { try { action(); } catch { checks++; return; } throw new Exception(name); }
    sealed class Device : IReplayDevice
    {
        public bool Continuous { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public int SdkSource { get { return 2; } }
        int source = 1;
        public bool FailRun, FailRestore, FailStop;
        public int Runs;
        public int Source { get { return source; } set { if (FailRestore && value == 1) throw new IOException("restore"); source = value; } }
        public void Stop() { if (FailStop) throw new IOException("stop"); }
        public uint Run(byte[] data)
        {
            Check(Source == SdkSource && Enabled && !Continuous, "SDK input and manual execution only");
            Runs++; if (FailRun) throw new IOException("run"); return 42;
        }
    }
    static void Main()
    {
        var device = new Device(); var replay = new ReplayControl(device);
        Fails(() => replay.Execute(new byte[1]), "cannot run outside replay");
        replay.Enter(); Check(!device.Enabled && !device.Continuous, "stop before browsing");
        Check(replay.Execute(new byte[1]) == 42, "return exact execution frame");
        Check(device.Source == 1 && !device.Enabled, "restore camera source but keep stopped between selections");
        bool resumed = false; replay.Exit(() => resumed = true);
        Check(resumed && device.Enabled && device.Continuous && !replay.Active, "resume original states");
        device.Enabled = false; device.Continuous = false; replay.Enter(); replay.Exit();
        Check(!device.Enabled && !device.Continuous, "original disabled camera remains disabled");
        device.FailRun = true; replay.Enter(); Fails(() => replay.Execute(new byte[1]), "SDK failure reported");
        Check(device.Source == 1 && !device.Enabled, "restore after execution failure"); replay.Exit();
        device.FailRun = false; replay.Enter(); device.FailRestore = true;
        Fails(() => replay.Execute(new byte[1]), "restore failure reported");
        Check(replay.Faulted && !device.Enabled && !device.Continuous, "do not resume on restoration failure");
        Fails(() => replay.Execute(new byte[1]), "fault blocks further runs");
        device.FailRestore = false; replay.Exit(); Check(!replay.Active && device.Source == 1, "retry restoration");
        device.FailStop = true; Fails(() => replay.Enter(), "stop failure reported");
        Check(replay.Active && replay.Faulted, "failed entry retains recovery state"); device.FailStop = false; replay.Exit();
        string root = Path.Combine(Path.GetTempPath(), "sc6000-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, "camera1"), b = Path.Combine(root, "camera2");
            Func<string, string, DateTime, string> add = (dir, name, time) =>
            {
                Directory.CreateDirectory(dir); string path = Path.Combine(dir, name); File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); File.SetLastWriteTimeUtc(path, time); return path;
            };
            var time1 = DateTime.UtcNow.AddMinutes(-5); var time2 = time1.AddMinutes(1);
            string old = add(Path.Combine(a, "2026", "10", "24", "NG"), "zzz.bmp", time1);
            string latest = add(Path.Combine(a, "2026", "10", "23", "NG"), "aaa.png", time2);
            add(Path.Combine(a, "2026", "10", "23", "OK"), "not-ng.bmp", time2);
            add(Path.Combine(a, "2026", "99", "23", "NG"), "invalid-date.bmp", time2);
            add(Path.Combine(a, "2026", "10", "23", "NG"), "upload.tmp", time2);
            add(Path.Combine(a, "2026", "10", "23", "NG"), "unfinished.bmp", DateTime.UtcNow);
            add(Path.Combine(b, "2026", "10", "23", "NG"), "camera2.bmp", time2);
            var list = FtpOriginals.LoadRecent(a, "Camera1", 100);
            Check(list.Count == 2, "only complete NG image files in valid date folders");
            Check(list[0].ImagePath == latest && list[1].ImagePath == old, "sort by saved timestamp, not name or folder");
            Check(FtpOriginals.LoadRecent(a, "Camera1", 1).Single().ImagePath == latest, "limit newest");
            Check(FtpOriginals.LoadRecent(b, "Camera2", 100).Count == 1, "camera directories isolated");
            Check(FtpOriginals.ReadStable(list[0]).SequenceEqual(new byte[] { 1, 2, 3 }), "read exact chosen original");
            using (var locked = new FileStream(latest, FileMode.Open, FileAccess.Write, FileShare.Read))
                Fails(() => FtpOriginals.ReadStable(list[0]), "reject FTP file still open for writing");
            File.AppendAllText(latest, "more"); Fails(() => FtpOriginals.ReadStable(list[0]), "reject file changed since thumbnail");
            Fails(() => FtpOriginals.ReadStable(new NgRecord { ImagePath = old }), "reject screenshots as inspection inputs");
            Fails(() => FtpOriginals.LoadRecent("", "", 100), "missing root is explicit error");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: " + checks + " replay/FTP checks");
    }
}
