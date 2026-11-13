using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Forms;
using SC6000DualMonitor;

class GraphicsToggleTests
{
    public class QueuedDisplay : DispatcherObject
    {
        public string ShapeObjectPath { get; private set; }
        public readonly System.Collections.Generic.List<string> Applied = new System.Collections.Generic.List<string>();
        public QueuedDisplay(string paths) { ShapeObjectPath = paths; }
        public object ShapeObjectPathSource
        {
            get { return ShapeObjectPath; }
            set
            {
                VerifyAccess();
                ShapeObjectPath = (string)value;
                string snapshot = ShapeObjectPath;
                Dispatcher.BeginInvoke(new Action(() => Applied.Add(snapshot)), DispatcherPriority.Background);
            }
        }
    }
    public class Display
    {
        public string ShapeObjectPath { get; private set; }
        public string Removed;
        public int Writes;
        public Display(string paths) { ShapeObjectPath = paths; }
        public object ShapeObjectPathSource
        {
            get { return ShapeObjectPath; }
            set { Removed = ShapeObjectPath; ShapeObjectPath = (string)value; Writes++; }
        }
    }
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    [STAThread] static void Main(string[] args)
    {
        const string other = "Proc1.Other.[Display].Line";
        const string a = "Proc1.Graphics Set1.[Display].Circle";
        const string b = "Proc1.Graphics Set1.[Display].Show Text";
        var display = new Display(other);
        var adapter = new FrontendGraphics(display, new[] { a, b });
        Check(adapter.State == CheckState.Unchecked, "initial unchecked");
        adapter.Set(true);
        Check(adapter.State == CheckState.Checked, "check all target outputs");
        Check(display.Removed == other, "unsubscribe sees old selection");
        Check(display.ShapeObjectPath == other + ";" + a + ";" + b, "preserve unrelated outputs");
        adapter.Set(true); Check(display.Writes == 1, "idempotent check");
        adapter.Set(false);
        Check(display.Removed.Contains(a) && display.ShapeObjectPath == other, "uncheck removes old targets only");
        Check(adapter.State == CheckState.Unchecked, "unchecked readback");
        adapter.Set(false); Check(display.Writes == 2, "idempotent uncheck");
        for (int i = 0; i < 100; i++) { adapter.Set(true); adapter.Set(false); }
        Check(display.ShapeObjectPath == other, "100 check/uncheck cycles preserve other selection");
        var partial = new FrontendGraphics(new Display(a), new[] { a, b });
        Check(partial.State == CheckState.Indeterminate, "partial graphics selection");
        partial.Set(true); Check(partial.State == CheckState.Checked, "partial to all");
        partial.Set(false); Check(partial.State == CheckState.Unchecked, "remove last selection");
        var camera2 = new Display(a);
        new FrontendGraphics(camera2, new[] { a }).Set(false);
        Check(display.ShapeObjectPath == other, "camera independence");

        // A real asynchronous boundary must separate unsubscription and resubscription.
        foreach (string original in new[] { other + ";" + a + ";" + b, other + ";" + a, other })
        {
            var target = new Display(original);
            var reconnect = new FrontendGraphics(target, new[] { a, b });
            var first = new TaskCompletionSource<bool>();
            var second = new TaskCompletionSource<bool>();
            int drains = 0;
            var task = reconnect.ReconnectAsync(() => ++drains == 1 ? first.Task : second.Task);
            Check(!task.IsCompleted && drains == 1 && target.ShapeObjectPath != original, "wait for first transition");
            Check(target.ShapeObjectPath.Contains(other), "unrelated graphic retained during reconnect");
            first.SetResult(true);
            Check(!task.IsCompleted && drains == 2 && target.ShapeObjectPath == original, "restore exact full/partial/off selection before second drain");
            second.SetResult(true); task.GetAwaiter().GetResult();
            Check(target.ShapeObjectPath == original && target.Writes == 2, "single reconnect cycle completes");
        }
        var failed = new Display(other + ";" + a);
        int failedDrains = 0;
        var failure = new TaskCompletionSource<bool>(); failure.SetException(new InvalidOperationException("dispatcher failure"));
        try
        {
            new FrontendGraphics(failed, new[] { a, b }).ReconnectAsync(() => ++failedDrains == 1 ? failure.Task : Task.FromResult(true)).GetAwaiter().GetResult();
            throw new Exception("expected reconnect error");
        }
        catch (InvalidOperationException) { }
        Check(failed.ShapeObjectPath == other + ";" + a && failedDrains == 2, "restore selection even when first drain fails");

        // Exercise the production Dispatcher overload, not just the injected barriers.
        var queued = new QueuedDisplay(other + ";" + a);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(queued.Dispatcher));
        try
        {
            var reconnectTask = new FrontendGraphics(queued, new[] { a }).ReconnectAsync();
            var pump = new DispatcherFrame();
            reconnectTask.ContinueWith(t => queued.Dispatcher.BeginInvoke(new Action(() => pump.Continue = false)));
            Dispatcher.PushFrame(pump);
            reconnectTask.GetAwaiter().GetResult();
            Check(queued.Applied.SequenceEqual(new[] { other, other + ";" + a }), "WPF processes off then on before completion on UI thread");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        string sdk = args[0];
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (s, e) => {
            string p = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.ReflectionOnlyLoadFrom(p) : Assembly.ReflectionOnlyLoad(e.Name);
        };
        var asm = Assembly.ReflectionOnlyLoadFrom(Path.Combine(sdk, "FrontendUI.Design.dll"));
        var view = asm.GetType("FrontendUI.Design.Controls.ImageControl", true);
        Check(view.GetProperty("ShapeObjectPath").PropertyType == typeof(string), "SDK selection paths");
        Check(view.GetProperty("ShapeObjectPathSource").CanWrite, "SDK public source setter");
        Check(view.GetProperty("ModuleSource") != null && view.GetProperty("ShapeSourceList") != null, "SDK discovery");
        var holder = view.GetMethod("GetLeafNode", BindingFlags.NonPublic | BindingFlags.Instance).ReturnType.GetGenericArguments()[0];
        Check(holder.GetProperty("HolderTarget") != null && holder.GetProperty("EnumEntrysA") != null, "SDK tree contract");
        var convert = asm.GetType("FrontendUI.Design.Controls.BaseControl").GetMethod("CheckBindingPathWithOutSolName", BindingFlags.Static | BindingFlags.NonPublic);
        Check(convert != null && convert.GetParameters().Length == 2, "SDK relative path conversion");
        var xml = Assembly.ReflectionOnlyLoadFrom(Path.Combine(sdk, "Apps.XmlParser.dll")).GetType("Apps.XmlParser.Object");
        Check(xml.GetProperty("Owner") != null, "SDK output owner");
        Check(xml.GetInterfaces().Any(i => i.Name == "IVmFullPath" && i.GetProperty("FullPath") != null), "SDK explicit full path");

        var app = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        var paneType = app.GetType("SC6000DualMonitor.SolutionPane");
        using (var pane = (Control)Activator.CreateInstance(paneType, new object[] { null, new Action(() => {}), null }))
        {
            var fieldFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var check = (CheckBox)paneType.GetField("_graphics", fieldFlags).GetValue(pane);
            bool? requested = null;
            paneType.GetEvent("GraphicsChanged").AddEventHandler(pane, new Action<bool>(value => requested = value));
            var set = paneType.GetMethod("SetGraphicsState");
            set.Invoke(pane, new object[] { CheckState.Checked, true, "test" });
            Check(!check.Enabled, "disconnected checkbox disabled");
            paneType.GetField("_connected", fieldFlags).SetValue(pane, true);
            set.Invoke(pane, new object[] { CheckState.Checked, true, "test" });
            Check(check.Enabled && check.Checked && !check.AutoCheck, "connected checkbox shows verified state");
            Check(requested == null, "state refresh does not write SDK");
            typeof(Control).GetMethod("OnClick", fieldFlags).Invoke(check, new object[] { EventArgs.Empty });
            Check(requested == false, "checked click requests uncheck");
            set.Invoke(pane, new object[] { CheckState.Unchecked, true, "test" });
            typeof(Control).GetMethod("OnClick", fieldFlags).Invoke(check, new object[] { EventArgs.Empty });
            Check(requested == true, "unchecked click requests check");
            paneType.GetField("_busy", fieldFlags).SetValue(pane, true);
            set.Invoke(pane, new object[] { CheckState.Checked, true, "test" });
            Check(!check.Enabled, "recipe operation disables checkbox");
            paneType.GetField("_busy", fieldFlags).SetValue(pane, false);
            set.Invoke(pane, new object[] { CheckState.Unchecked, false, "not found" });
            Check(!check.Enabled && !check.Checked, "missing graphic disables checkbox");
        }
        Console.WriteLine("PASS: " + checks + " graphics selection, SDK and main UI checks (no camera connected)");
    }
}
