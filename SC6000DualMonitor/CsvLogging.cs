using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    // Captures per-execution FORMAT module outputs without changing camera parameters or Save Text.
    // Source modules are selected in UI and may be renamed by the operator.
    internal sealed class CsvInspectionSample : EventArgs
    {
        public uint Frame;
        public DateTime Time;
        public string Result;
        public string[] Values;
        public string Detail;
    }

    internal sealed class CsvModuleCapture : IDisposable
    {
        private sealed class Pending
        {
            public uint Frame;
            public DateTime CreatedUtc = DateTime.UtcNow;
            public DateTime Time;
            public string Result;
            public bool Completed;
            public readonly string[] Values = new string[4];
        }
        private sealed class Subscription
        {
            public object Module;
            public EventInfo Event;
            public Delegate Handler;
        }

        private readonly string[] _names;
        private readonly object _gate = new object();
        private readonly Dictionary<object, int> _modules = new Dictionary<object, int>();
        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly HashSet<uint> _emitted = new HashSet<uint>();
        private readonly Queue<uint> _emittedOrder = new Queue<uint>();
        private readonly List<Subscription> _subscriptions = new List<Subscription>();
        private Timer _timer;
        private bool _disposed;
        public event Action<CsvInspectionSample> SampleReady;

        private static Dictionary<string, object> Discover(VmProcedure procedure)
        {
            var found = new Dictionary<string, object>(StringComparer.Ordinal);
            var all = procedure.GetAllModuleList();
            if (all.astModuleInfo == null) return found;
            for (int i = 0; i < all.nNum && i < all.astModuleInfo.Length; i++)
            {
                object module = procedure.GetModuleByID(all.astModuleInfo[i].nModuleID);
                if (module == null) continue;
                Type type = module.GetType();
                if (type.FullName != "FormatModuleCs.FormatModuleTool") continue;
                if (type.GetEvent("ModuleResultCallBackArrived") == null ||
                    type.GetMethod("EnableResultCallback", Type.EmptyTypes) == null) continue;
                var property = type.GetProperty("Name");
                string name = property == null ? null : property.GetValue(module, null) as string;
                if (!string.IsNullOrWhiteSpace(name)) found[name] = module;
            }
            return found;
        }
        public static string[] ListModuleNames(VmProcedure procedure)
        {
            return Discover(procedure).Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public CsvModuleCapture(VmProcedure procedure, CsvModuleMapping mapping)
        {
            var discovered = Discover(procedure);
            mapping.Validate(discovered.Keys);
            _names = (string[])mapping.ModuleNames.Clone();

            try
            {
                for (int i = 0; i < _names.Length; i++)
                {
                    object module = discovered[_names[i]];
                    Type type = module.GetType();
                    EventInfo ev = type.GetEvent("ModuleResultCallBackArrived");
                    MethodInfo enable = type.GetMethod("EnableResultCallback", Type.EmptyTypes);
                    MethodInfo handler = GetType().GetMethod("OnModuleResult", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (ev == null || enable == null || handler == null)
                        throw new InvalidOperationException(_names[i] + ": SDK 결과 콜백 API를 사용할 수 없습니다.");
                    Delegate subscription = Delegate.CreateDelegate(ev.EventHandlerType, this, handler);
                    ev.AddEventHandler(module, subscription);
                    _subscriptions.Add(new Subscription { Module = module, Event = ev, Handler = subscription });
                    _modules.Add(module, i);
                    enable.Invoke(module, null);
                }
                _timer = new Timer(CheckExpired, null, 500, 500);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnModuleResult(object sender, EventArgs args)
        {
            var callback = args as VMControls.Interface.ValueEventArgs;
            if (callback == null || !(callback.Value is ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)) return;
            var result = (ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)callback.Value;
            if (result.bIsSelfExecute != 0 || result.nErrorCode != 0 || result.pInfo == null) return;

            // SDK FormatResult exposes format_out. Never substitute an arbitrary string/status.
            var outputs = result.pInfo.Where(f => string.Equals((f.strParamName ?? "").Replace("_", "").Replace(" ", "").Replace("[]", ""), "formatout", StringComparison.OrdinalIgnoreCase)).ToArray();
            string value = outputs.Length == 1 && outputs[0].pStringValue != null && outputs[0].pStringValue.Length == 1
                ? outputs[0].pStringValue[0].strValue : null;
            if (value == null) return;

            CsvInspectionSample ready = null;
            lock (_gate)
            {
                if (_disposed || _emitted.Contains(result.nExecuteCount)) return;
                int index;
                if (!_modules.TryGetValue(sender, out index)) return;
                Pending entry = GetPending(result.nExecuteCount);
                entry.Values[index] = value.Trim();
                if (entry.Completed && entry.Values.All(v => v != null))
                    ready = Complete(entry);
            }
            if (ready != null) Emit(ready);
        }

        public void ProcedureFinished(uint frame, InspectionOutcome outcome)
        {
            CsvInspectionSample ready = null;
            lock (_gate)
            {
                if (_disposed || _emitted.Contains(frame)) return;
                var entry = GetPending(frame);
                entry.Result = outcome.ToString().ToUpperInvariant();
                entry.Time = DateTime.Now;
                entry.Completed = true;
                if (entry.Values.All(v => v != null)) ready = Complete(entry);
            }
            if (ready != null) Emit(ready);
        }

        private Pending GetPending(uint frame)
        {
            Pending entry;
            if (!_pending.TryGetValue(frame, out entry))
            {
                entry = new Pending { Frame = frame };
                _pending.Add(frame, entry);
            }
            return entry;
        }

        private CsvInspectionSample Complete(Pending entry)
        {
            _pending.Remove(entry.Frame);
            _emitted.Add(entry.Frame);
            _emittedOrder.Enqueue(entry.Frame);
            while (_emittedOrder.Count > 2048) _emitted.Remove(_emittedOrder.Dequeue());
            var missing = _names.Where((n, i) => entry.Values[i] == null).ToArray();
            return new CsvInspectionSample
            {
                Frame = entry.Frame, Time = entry.Time == default(DateTime) ? DateTime.Now : entry.Time,
                Result = entry.Result ?? "ERROR", Values = (string[])entry.Values.Clone(),
                Detail = missing.Length == 0 ? "OK" : "MISSING: " + string.Join(";", missing)
            };
        }

        private void CheckExpired(object state)
        {
            var done = new List<CsvInspectionSample>();
            lock (_gate)
            {
                if (_disposed) return;
                var now = DateTime.UtcNow;
                // An unfinished frame (no procedure callback) cannot be exported as a valid inspection.
                foreach (var entry in _pending.Values.ToArray())
                {
                    if (!entry.Completed && now - entry.CreatedUtc > TimeSpan.FromSeconds(10))
                        _pending.Remove(entry.Frame);
                    else if (entry.Completed && now - entry.CreatedUtc > TimeSpan.FromSeconds(2))
                        done.Add(Complete(entry));
                }
                // Prevent a stalled device from growing the pending cache indefinitely.
                if (_pending.Count > 512)
                    foreach (var entry in _pending.Values.OrderBy(p => p.CreatedUtc).Take(_pending.Count - 512).ToArray())
                        if (entry.Completed) done.Add(Complete(entry)); else _pending.Remove(entry.Frame);
            }
            foreach (var row in done) Emit(row);
        }

        private void Emit(CsvInspectionSample row)
        {
            try { var handler = SampleReady; if (handler != null) handler(row); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("CSV result callback failed: " + ex); }
        }

        public void Dispose()
        {
            CsvInspectionSample[] remaining;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                remaining = _pending.Values.Where(p => p.Completed).ToArray().Select(Complete).ToArray();
                _pending.Clear();
            }
            foreach (var row in remaining) Emit(row);
            if (_timer != null) _timer.Dispose();
            foreach (var item in _subscriptions)
                try { item.Event.RemoveEventHandler(item.Module, item.Handler); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("CSV callback unsubscribe: " + ex); }
            _subscriptions.Clear();
        }
    }

    // Disk I/O is intentionally separated from the VisionMaster result callback.
    internal sealed class CsvInspectionWriter : IDisposable
    {
        private sealed class Entry
        {
            public CsvInspectionSample Sample;
            public string PartNumber;
            public string Recipe;
        }
        private readonly object _gate = new object();
        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private readonly string _folder;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _worker;
        private bool _stop;
        private long _saved, _missing, _dropped;
        private string _error;

        public CsvInspectionWriter(string camera)
        {
            string name = new string((camera ?? "camera").Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WILLB", "SC6000Monitor", "CsvHistory", name);
            _worker = new Thread(Work) { IsBackground = true, Name = "SC6000 CSV Writer" };
            _worker.Start();
        }
        public string Folder { get { return _folder; } }
        public string Status
        {
            get
            {
                lock (_gate)
                {
                    return _error != null ? "CSV 오류: " + _error :
                        "CSV " + _saved.ToString("N0") + "건 저장" +
                        (_missing == 0 ? "" : " / 측정값 누락 " + _missing.ToString("N0")) +
                        (_dropped == 0 ? "" : " / 큐 초과 " + _dropped.ToString("N0"));
                }
            }
        }
        public bool HasError { get { lock (_gate) return _error != null || _dropped > 0; } }

        public void Enqueue(CsvInspectionSample sample, string partNo, string recipe)
        {
            lock (_gate)
            {
                if (_stop) return;
                if (_queue.Count >= 2048)
                {
                    _dropped++;
                    _error = "처리 대기 2048건 초과 (드롭 " + _dropped + "건)";
                    return;
                }
                _queue.Enqueue(new Entry { Sample = sample, PartNumber = partNo, Recipe = recipe });
                _signal.Set();
            }
        }
        private static string Csv(string value)
        {
            string s = value ?? "";
            double number;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return s;
            // Prevent spreadsheet formula execution when a file is opened in Excel.
            if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' ||
                s[0] == '\t' || s[0] == '\r')) s = "'" + s;
            double numeric;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        private void Write(Entry[] entries)
        {
            var item = entries[0].Sample;
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "inspection_" + item.Time.ToString("yyyy-MM-dd") + ".csv");
            Append(path, entries);
        }
        private static void Append(string path, Entry[] entries)
        {
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                long length = stream.Length;
                stream.Position = length;
                try
                {
                    var content = new StringBuilder();
                    foreach (var entry in entries)
                    {
                    var item = entry.Sample;
                    var v = item.Values ?? new string[4];
                    string line = string.Join(",", new[] {
                        Csv(item.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)), item.Frame.ToString(CultureInfo.InvariantCulture),
                        Csv(entry.PartNumber), Csv(entry.Recipe), Csv(v.ElementAtOrDefault(0)), Csv(v.ElementAtOrDefault(1)),
                        Csv(v.ElementAtOrDefault(2)), Csv(v.ElementAtOrDefault(3)), Csv(item.Result), Csv(item.Detail) }) + "\r\n";
                    content.Append(line);
                    }
                    string batch = content.ToString();
                    if (length == 0) batch = "\uFEFFTime,Frame,PART NO,Recipe,Top,Middle,Bottom,Circle,Result,DataStatus\r\n" + batch;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(batch);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch { stream.SetLength(length); throw; }
            }
        }
        private void SaveRecovery()
        {
            try
            {
                string folder = Path.Combine(Path.GetTempPath(), "SC6000-CsvRecovery");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "recovery_" + Path.GetFileName(_folder) + "_" + Guid.NewGuid().ToString("N") + ".csv");
                lock (_gate)
                {
                    Append(file, _queue.ToArray());
                    _queue.Clear();
                    _error = "미저장 결과 복구 파일: " + file;
                }
            }
            catch (Exception ex) { lock (_gate) _error = "CSV 복구 저장 실패: " + ex.Message + " / 대기 " + _queue.Count; }
        }

        private void Work()
        {
            while (true)
            {
                Entry[] entries = null;
                lock (_gate)
                {
                    if (_queue.Count != 0)
                    {
                        DateTime day = _queue.Peek().Sample.Time.Date;
                        entries = _queue.TakeWhile(e => e.Sample.Time.Date == day).Take(128).ToArray();
                    }
                    else if (_stop) return;
                }
                if (entries == null) { _signal.WaitOne(500); continue; }
                try
                {
                    Write(entries);
                    lock (_gate)
                    {
                        foreach (var entry in entries)
                        {
                            _queue.Dequeue(); _saved++;
                            if (entry.Sample.Detail != "OK") _missing++;
                        }
                        _error = null;
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate) _error = ex.Message;
                    System.Diagnostics.Trace.WriteLine("SC6000 CSV write failed: " + ex);
                    bool stopping; lock (_gate) stopping = _stop;
                    if (stopping) { SaveRecovery(); return; }
                    _signal.WaitOne(1000);
                }
            }
        }
        public void Dispose()
        {
            lock (_gate) _stop = true;
            _signal.Set();
            if (Thread.CurrentThread != _worker && _worker.Join(5000))
            {
                _signal.Dispose();
                if (_error != null) System.Windows.Forms.MessageBox.Show(_error, "CSV 저장 상태");
            }
        }
    }
}
