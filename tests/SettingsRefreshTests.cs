using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using SC6000DualMonitor;

class SettingsRefreshTests
{
    class Tracked
    {
        public readonly List<string> Calls = new List<string>();
        public int Writes;
        public void Pause() { Calls.Add("pause"); }
        public void Save() { Calls.Add("save"); }
        public void Resume() { Calls.Add("resume"); }
    }
    class Exposure : Tracked, IExposureSession
    {
        double exposure = 5, gain = 2;
        public string Description { get { return "Exposure"; } }
        public ExposureSettings Read() { return new ExposureSettings {
            Exposure = new CameraParameterValue { Value = exposure, Min = 1, Max = 100 },
            Gain = new CameraParameterValue { Value = gain, Min = 0, Max = 24 } }; }
        public void Write(double e, double g) { exposure = e; gain = g; Writes++; }
    }
    class Blob : Tracked, IBlobSettingsSession
    {
        BlobSettings values = new BlobSettings { Low = 0, High = 80, MinArea = 100, MaxArea = 150000, AreaEnabled = true, ThresholdMode = 3 };
        public string TargetDescription { get { return "Blob"; } }
        public BlobSettings Read() { return values.Copy(); }
        public void Write(BlobSettings requested) { values = requested.Copy(); Writes++; }
    }
    class Conditions : Tracked, IInspectionSettingsSession
    {
        IList<ConditionRange> values = new List<ConditionRange> { new ConditionRange(new ConditionDefinition("float0", "CB"), 13.8, 14.3) };
        public IList<ConditionRange> ReadRanges() { return new List<ConditionRange>(values); }
        public void WriteRange(ConditionRange requested) { values[0] = requested; Writes++; }
    }
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Call(object target, string name) { target.GetType().GetMethod(name, Flags).Invoke(target, null); }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    static void Verify(Form form, Tracked session, string read)
    {
        using (form)
        {
            // Force WinForms synchronization context; pump continuations as the live dialog does.
            form.CreateControl();
            Call(form, read);
            int refreshes = 0;
            var pending = new TaskCompletionSource<bool>();
            var refresh = form.GetType().GetProperty("RefreshGraphicsAfterSave");
            refresh.SetValue(form, new Func<Task>(() => {
                refreshes++;
                Check(session.Calls.Last() == "resume" && session.Calls.Contains("save"), "refresh follows save and resume: " + form.Text);
                return pending.Task;
            }), null);
            Call(form, "Apply");
            Check(refreshes == 1 && !form.Enabled && Field<bool>(form, "_applying"), "wait while reconnect pending: " + form.Text);
            int writes = session.Writes;
            Call(form, "Apply");
            Check(session.Writes == writes && refreshes == 1, "ignore duplicate apply");
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            form.GetType().GetMethod("OnFormClosing", Flags).Invoke(form, new object[] { closing });
            Check(closing.Cancel, "cannot dispose dialog during reconnect");
            pending.SetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Field<bool>(form, "_applying") && DateTime.UtcNow < deadline) Application.DoEvents();
            Check(form.Enabled && !Field<bool>(form, "_applying"), "reenable after reconnect");
            Check(Field<Label>(form, "_state").Text.Contains("저장 완료"), "success status after reconnect");
            refresh.SetValue(form, new Func<Task>(() => { refreshes++; throw new InvalidOperationException("display-only failure"); }), null);
            Call(form, "Apply");
            Check(session.Writes == writes + 1 && refreshes == 2, "display failure does not roll back or repeat write");
            Check(Field<Label>(form, "_state").Text.Contains("저장과 검사 재개는 완료"), "report saved values separately from display failure");
            int previousRefreshes = refreshes;
            Call(form, read);
            Check(refreshes == previousRefreshes, "read-only action does not reconnect");
        }
    }
    [STAThread] static void Main()
    {
        var e = new Exposure();
        Verify(new ExposureSetupForm("1", () => new List<IExposureSession> { e }, null), e, "Discover");
        var b = new Blob();
        Verify(new BlobSetupForm("1", () => new List<BlobEditorTarget> { new BlobEditorTarget { Name = "Blob", DisplayName = "Blob", Session = b } }, null), b, "Discover");
        var c = new Conditions();
        Verify(new InspectionSetupForm(c, "1"), c, "ReadCamera");
        int forbidden = 0;
        try
        {
            SettingsSaveCompletion.ApplyAsync(() => { throw new InvalidOperationException("save failure"); },
                () => { forbidden++; return Task.FromResult(true); }).GetAwaiter().GetResult();
            throw new Exception("expected failed transaction");
        }
        catch (InvalidOperationException) { }
        Check(forbidden == 0, "failed transaction must not run success reconnect");
        Console.WriteLine("PASS: " + checks + " exposure, BLOB, OK/NG post-save UI and failure checks (mock cameras)");
    }
}
