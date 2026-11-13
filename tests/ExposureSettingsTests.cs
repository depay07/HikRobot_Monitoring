using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using SC6000DualMonitor;

class ExposureSettingsTests
{
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static string Fails(Action action) { try { action(); } catch (Exception ex) { checks++; return ex.Message; } throw new Exception("Expected failure"); }
    static ExposureSettings Values(double exposure = 5, double gain = 2)
    { return new ExposureSettings { Exposure = CameraParameterValue.Parse(exposure.ToString(CultureInfo.InvariantCulture) + "$0.01$100$0.001"), Gain = CameraParameterValue.Parse(gain.ToString(CultureInfo.InvariantCulture) + "$0$24$0.1") }; }
    class Session : IExposureSession
    {
        public ExposureSettings ValuesNow = Values();
        public string Description { get { return "192.168.10.51 | Proc1\r\n/root/vmtempfiles/ftp/solution/검사.solx\r\nSmart Camera1"; } }
        public override string ToString() { return "Smart Camera1"; }
        public List<string> Calls = new List<string>();
        public int Writes, Saves, Reads;
        public bool PartialFailure, RestoreFailure, SaveFailure, Adjust, ResumeFailure, Offline, ChangeOnPause;
        public ExposureSettings Read() { Calls.Add("read"); Reads++; if (Offline) throw new Exception("offline"); return Copy(); }
        ExposureSettings Copy() { var value = Values(ValuesNow.Exposure.Value, ValuesNow.Gain.Value); value.AutoExposure = ValuesNow.AutoExposure; value.AutoGain = ValuesNow.AutoGain; return value; }
        public void Write(double exposure, double gain)
        {
            Calls.Add("write"); Writes++;
            if (Writes > 1 && RestoreFailure) throw new Exception("restore failed");
            ValuesNow.Exposure.Value = Adjust && Writes == 1 ? Math.Round(exposure, 1) : exposure;
            if (PartialFailure && Writes == 1) throw new Exception("gain write failed");
            ValuesNow.Gain.Value = gain;
        }
        public void Pause() { Calls.Add("pause"); if (ChangeOnPause) ValuesNow.Gain.Value = 3; }
        public void Save() { Calls.Add("save"); Saves++; if (SaveFailure && Saves == 1) throw new Exception("save failed"); }
        public void Resume() { Calls.Add("resume"); if (ResumeFailure) throw new Exception("resume failed"); }
    }
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static T Field<T>(object o, string name) { return (T)o.GetType().GetField(name, Private).GetValue(o); }
    static void Invoke(object o, string name) { o.GetType().GetMethod(name, Private).Invoke(o, null); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var parsed = CameraParameterValue.Parse("5.125$0.01$100");
            Check(parsed.Value == 5.125 && parsed.Min == 0.01 && parsed.Max == 100, "device numbers invariant across locales");
            // Shipped VM4.4.50 GlobalSmartCameraParamItem reads the first three
            // fields whenever Length > 2; the remaining fields are not bounds.
            // Synthetic contract fixtures, not recorded camera network traffic.
            foreach (var raw in new[] { "5.125$0.01$100$0.001", "5.125$0.01$100$", "5.125$0.01$100$extra$metadata" })
            {
                var extended = CameraParameterValue.Parse(raw);
                Check(extended.Value == 5.125 && extended.Min == 0.01 && extended.Max == 100, "SDK extended numeric reply accepted: " + raw);
                Check(extended.RangeText == "0.01 ~ 100", "extended reply preserves device limits");
                Fails(() => extended.Validate(101, "Exposure"));
            }
            CultureInfo.CurrentCulture = culture;
            Check(!CameraParameterValue.Parse("1.25").Min.HasValue, "scalar camera reply supported");
            foreach (var raw in new[] { "NULL$0$10", "NaN", "Infinity", "1$2", "1$10$2", "20$0$10", "",
                "NULL$0$10$0", "5$NaN$10$0", "5$0$Infinity$0", "5$10$2$0", "20$0$10$0", "5$$10$0", "5$0$$0" })
                Fails(() => CameraParameterValue.Parse(raw));
            var s = new Session(); var baseline = s.Read();
            var actual = ExposureController.Apply(s, baseline, 10, 4);
            Check(actual.Exposure.Value == 10 && actual.Gain.Value == 4, "both settings applied");
            Check(s.Saves == 1 && s.Calls.IndexOf("pause") < s.Calls.IndexOf("write") && s.Calls[s.Calls.Count - 1] == "resume", "pause/write/save/read/resume transaction");
            var other = new Session(); Check(other.ValuesNow.Exposure.Value == 5 && other.Saves == 0, "other camera unchanged");
            s = new Session(); baseline = s.Read();
            Fails(() => ExposureController.Apply(s, baseline, 1000, 4));
            Check(s.Writes == 0 && !s.Calls.Contains("pause"), "invalid range rejected before pause");
            Fails(() => ExposureController.Apply(s, baseline, 0, 4));
            s.ValuesNow.AutoExposure = 2; baseline = s.Read();
            Fails(() => ExposureController.Apply(s, baseline, 10, 4)); Check(s.Writes == 0, "automatic exposure not overwritten");
            s = new Session(); baseline = s.Read(); s.ValuesNow.Gain.Value = 3;
            Fails(() => ExposureController.Apply(s, baseline, 10, 4)); Check(s.Writes == 0, "stale editor rejected");
            s = new Session { ChangeOnPause = true }; baseline = s.Read();
            Fails(() => ExposureController.Apply(s, baseline, 10, 4)); Check(s.Writes == 0 && s.Calls.Contains("resume"), "race after pause preserved");
            s = new Session { PartialFailure = true }; baseline = s.Read();
            Fails(() => ExposureController.Apply(s, baseline, 10, 4));
            Check(s.ValuesNow.Same(baseline) && s.Writes == 2 && s.Saves == 0 && s.Calls.Contains("resume"), "partial write rolled back");
            s = new Session { SaveFailure = true }; baseline = s.Read();
            Fails(() => ExposureController.Apply(s, baseline, 10, 4));
            Check(s.ValuesNow.Same(baseline) && s.Saves == 2 && s.Calls.Contains("resume"), "failed save restores and saves original");
            s = new Session { PartialFailure = true, RestoreFailure = true }; baseline = s.Read();
            Check(Fails(() => ExposureController.Apply(s, baseline, 10, 4)).Contains("정지 상태"), "restore failure explained");
            Check(!s.Calls.Contains("resume"), "failed recovery does not resume inspection");
            s = new Session { Adjust = true }; baseline = s.Read(); actual = ExposureController.Apply(s, baseline, 10.123, 4);
            Check(actual.Exposure.Value == 10.1 && s.Saves == 1, "device quantization read back and saved");
            s = new Session { ResumeFailure = true }; baseline = s.Read();
            Check(Fails(() => ExposureController.Apply(s, baseline, 10, 4)).Contains("저장은 완료"), "resume failure not mislabeled save failure");
            s = new Session();
            using (var form = new ExposureSetupForm("1번 카메라", () => new List<IExposureSession> { s }, null))
            {
                Invoke(form, "Discover"); Check(Field<Button>(form, "_apply").Enabled, "single connected camera selected automatically");
                Check(Field<NumericUpDown>(form, "_exposure").Value == 5, "current value loaded");
                s.ValuesNow.Exposure.Value = 8; Invoke(form, "ReadCamera");
                Check(Field<NumericUpDown>(form, "_exposure").Value == 8, "reload reads fresh camera value");
                s.Offline = true; Invoke(form, "ReadCamera");
                Check(!Field<Button>(form, "_apply").Enabled && Field<NumericUpDown>(form, "_exposure").Value == 0, "failed reload clears stale value and disables save");
                s.Offline = false; s.ValuesNow.AutoGain = 2; Invoke(form, "ReadCamera");
                Check(!Field<Button>(form, "_apply").Enabled && Field<Label>(form, "_state").Text.Contains("OFF"), "auto mode explained in UI");
                s.ValuesNow.AutoGain = 0;
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2000, -2000); form.Show(); Application.DoEvents();
                using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(args[0]); }
                form.Hide();
            }
            using (var form = new ExposureSetupForm("선택", () => new List<IExposureSession> { new Session(), new Session() }, null))
            { Invoke(form, "Discover"); Check(!Field<Button>(form, "_apply").Enabled && Field<ComboBox>(form, "_cameras").SelectedIndex == -1, "multiple cameras require explicit selection"); }
            Console.WriteLine("PASS: " + checks + " exposure/gain transaction, parsing and UI checks (mock camera)"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
