using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;

// Exercise the real built handler with installed SDK payloads, without a camera.
// A null procedure trips the result-read error path only for accepted completions.
class ProcedureCountingTests
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static Type sessionType, matcherType, statusType, dataType, eventType;
    static int passed;
    sealed class Session
    {
        public object Value, Matcher;
        public int Results;
        public Session()
        {
            Value = FormatterServices.GetUninitializedObject(sessionType);
            Set("_callbackGate", new object());
            Matcher = Activator.CreateInstance(matcherType);
            Set("_ngFrames", Matcher);
            Set("_completedFrames", new HashSet<uint>());
            Set("_completedFrameOrder", new Queue<uint>());
            var ev = sessionType.GetEvent("ResultArrived");
            ev.AddEventHandler(Value, Delegate.CreateDelegate(ev.EventHandlerType, this, GetType().GetMethod("Received")));
        }
        public void Received(object sender, EventArgs args) { Results++; }
        public void Set(string field, object value) { sessionType.GetField(field, F).SetValue(Value, value); }
        public void Send(uint phase, uint frame)
        {
            // Capture already delivered: no hardware/graphics dependencies needed.
            matcherType.GetMethod("Record").Invoke(Matcher, new object[] { frame, null, false, false });
            object status = Activator.CreateInstance(statusType);
            Put(status, "nWorkStatus", phase); Put(status, "nExecuteCount", frame);
            SendPayload(status, frame);
        }
        public void SendPayload(object payload, uint frame)
        {
            object ev = Activator.CreateInstance(eventType);
            Put(ev, "Value", payload); Put(ev, "ExecuteCount", frame);
            sessionType.GetMethod("OnProcedureCompleted", F).Invoke(Value, new object[] { null, ev });
        }
    }
    static void Put(object obj, string name, object value)
    {
        var field = obj.GetType().GetField(name);
        field.SetValue(obj, field.FieldType == typeof(object) ? value : Convert.ChangeType(value, field.FieldType));
    }
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        passed++; Console.WriteLine("PASS " + name);
    }
    static int Main(string[] args)
    {
        try
        {
            string sdk = Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                string path = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var platform = Assembly.LoadFrom(Path.Combine(sdk, "VM.PlatformSDKCS.dll"));
            sessionType = app.GetType("SC6000DualMonitor.RemoteInspectionSession", true);
            matcherType = app.GetType("SC6000DualMonitor.NgFrameMatcher", true);
            statusType = platform.GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_WORK_STAUS", true);
            dataType = platform.GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_RESULT_INFO_LIST_EX_Data", true);
            eventType = Assembly.LoadFrom(Path.Combine(sdk, "VMControls.Interface.dll")).GetType("VMControls.Interface.ValueEventArgs", true);
            var a = new Session();
            a.Send(1, 1); a.Send(1, 1);
            Check(a.Results == 0, "start notifications do not count or read SDK results");
            a.SendPayload(Activator.CreateInstance(dataType), 1);
            a.SendPayload(null, 1);
            a.Send(0, 0); a.Send(2, 1);
            Check(a.Results == 0, "data, empty, zero-frame and non-completion notifications ignored");
            a.Send(0, 1); a.Send(0, 1); a.Send(1, 1); a.Send(0, 1);
            Check(a.Results == 1, "one result for repeated completion, even after repeated start");
            a.Send(1, 2); a.Send(0, 2); a.Send(0, 1); a.Send(0, 2);
            Check(a.Results == 2, "next inspection counts once; delayed duplicate ignored");
            var b = new Session(); b.Send(0, 1);
            Check(b.Results == 1, "camera sessions have independent frame histories");
            a.Set("_paused", true); a.Send(0, 3);
            Check(a.Results == 2, "settings pause excludes results");
            a.Set("_paused", false); a.Send(0, 2); a.Send(0, 3);
            Check(a.Results == 3, "resume retains deduplication and accepts new completion");
            // Prepare capture state outside parallel calls; handler protects its own state.
            a.Send(0, 4);
            object repeat = Activator.CreateInstance(statusType);
            Put(repeat, "nWorkStatus", 0); Put(repeat, "nExecuteCount", 4);
            Parallel.For(0, 1000, i => a.SendPayload(repeat, 4));
            Check(a.Results == 4, "concurrent duplicate notifications do not increase count");
            for (uint frame = 5; frame <= 2000; frame++) { a.Send(1, frame); a.Send(0, frame); a.Send(0, frame); }
            Check(a.Results == 2000, "2000 inspections, including read errors, count exactly once each");
            Check(((HashSet<uint>)sessionType.GetField("_completedFrames", F).GetValue(a.Value)).Count == 128,
                "long-running frame history is bounded");
            a.Send(0, uint.MaxValue); a.Send(0, 1);
            Check(a.Results == 2002, "execute counter wraparound accepts new frames");
            a.Set("_disposed", true); a.Send(0, 2001);
            Check(a.Results == 2002, "disposed session ignores late callbacks");
            var reloaded = new Session(); reloaded.Send(0, 1);
            Check(reloaded.Results == 1, "new recipe session accepts reused frame numbers");
            Console.WriteLine(passed + " procedure counting checks passed; no camera accessed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
