using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
class DisplayRecoveryTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [STAThread] static void Main(string[] args)
    {
        var asm = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = asm.GetType("SC6000DualMonitor.SolutionPane");
        int reloads = 0, changes = 0;
        using (var pane = (Control)Activator.CreateInstance(type, new object[] { null, new Action(delegate { reloads++; }), null }))
        {
            type.GetEvent("SolutionChanging").AddEventHandler(pane, new Action(delegate { changes++; }));
            type.GetEvent("SolutionChanged").AddEventHandler(pane, new Action(delegate { changes++; }));
            var reload = type.GetMethod("ReloadDisplay", Flags);
            reload.Invoke(pane, null);
            Check(reloads == 0, "Disconnected reload must be ignored");
            type.GetField("_connected", Flags).SetValue(pane, true);
            var inspection = type.GetProperty("Inspection").GetValue(pane, null);
            var counter = Field(inspection, "_counter");
            var outcome = asm.GetType("SC6000DualMonitor.InspectionOutcome");
            var eventType = asm.GetType("SC6000DualMonitor.InspectionResultEventArgs");
            var error = Activator.CreateInstance(eventType, new object[] { Enum.Parse(outcome, "Error"), "Module ID 18 / 0xE0000001" });
            inspection.GetType().GetMethod("OnResult", Flags).Invoke(inspection, new object[] { null, error });
            reload.Invoke(pane, null);
            var snapshot = counter.GetType().GetMethod("Read").Invoke(counter, null);
            Check((long)snapshot.GetType().GetProperty("Total").GetValue(snapshot, null) == 1, "Reload reset counts");
            Check(reloads == 1 && changes == 0, "Reload must not switch or rebind solution");
            Check(Field(inspection, "_lastError").ToString().Contains("ID 18"), "Lost error details");
            var ok = Activator.CreateInstance(eventType, new object[] { Enum.Parse(outcome, "Ok"), "OK" });
            inspection.GetType().GetMethod("OnResult", Flags).Invoke(inspection, new object[] { null, ok });
            Check(Field(inspection, "_lastError").ToString().Contains("ID 18"), "Success erased last error");
            type.GetField("_busy", Flags).SetValue(pane, true);
            reload.Invoke(pane, null);
            Check(reloads == 1, "Busy reload must be ignored");
        }
        Console.WriteLine("PASS: frontend-only recovery preserves counts and error details; disconnected/busy guards");
    }
}
