using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using IfModuleCs;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    // One session per Viewer process; all SDK configuration calls run on its UI thread.
    internal sealed class RemoteInspectionSession : IInspectionSettingsSession, IInspectionTargetInfo, IDisposable
    {
        private readonly VmProcedure _procedure;
        private readonly CurrentRecipeSave _recipeSave;
        private readonly IfModuleTool _condition;
        private readonly string _procedureName;
        private readonly string _conditionName;
        private readonly string _cameraIp;
        public string TargetDescription { get { return _cameraIp + " | " + _procedure.Name + " / " + _condition.Name + " (ID " + _condition.ModuParams.ModuleID + ")"; } }
        private readonly IList<ConditionDefinition> _definitions;
        private readonly object _callbackGate = new object();
        private CsvModuleCapture _csvCapture;
        public event EventHandler<CsvInspectionSample> CsvSampleReady;
        private bool _disposed, _paused, _wasContinuous, _wasEnabled;
        public event EventHandler<InspectionResultEventArgs> ResultArrived;
        public event EventHandler<NgCaptureEventArgs> NgCaptureRequested;
        private readonly NgFrameMatcher _ngFrames = new NgFrameMatcher();
        private long _conditionCallbacks, _procedureCallbacks, _captureRequests;
        public string CaptureDiagnostics
        {
            get { lock (_callbackGate) return "흐름: " + _procedureName + " / 조건: " + _conditionName +
                "\r\n조건 콜백: " + _conditionCallbacks + " / 흐름 콜백: " + _procedureCallbacks +
                " / NG 캡처 요청: " + _captureRequests + "\r\n결과 수신 제외 상태: " + _paused; }
        }
        public string CameraProcedureDescription { get { return _cameraIp + " | " + _procedure.Name; } }
        public IList<BlobModuleChoice> GetBlobModules()
        {
            var result = new List<BlobModuleChoice>();
            var list = _procedure.GetAllModuleList();
            for (int i = 0; i < list.nNum; i++)
            {
                var module = _procedure.GetModuleByID(list.astModuleInfo[i].nModuleID) as IMVSBlobFindModuCs.IMVSBlobFindModuTool;
                if (module != null) result.Add(new BlobModuleChoice(module));
            }
            return result;
        }


        public RemoteInspectionSession(CameraSettings settings)
        {
            _cameraIp = settings.Ip;
            var solutions = new RemoteSolutionSession(settings.SolutionDirectory, settings.SolutionPassword);
            _recipeSave = new CurrentRecipeSave(() => solutions.CurrentPath,
                path => VmSolution.SaveAs(path)); // Preserves the current camera recipe and its password
            _definitions = ConditionDefinition.Parse(settings.ConditionItems);
            var selection = AutoSelectInspectionTarget(settings.ProcedureName, settings.ConditionModuleName);
            _procedure = selection.Procedure;
            _condition = selection.Condition;
            _procedureName = _procedure.Name;
            _conditionName = _condition.Name;
            ReadRanges();
            _condition.ModuleResultCallBackArrived += OnConditionResult;
            _procedure.ModuleResultCallBackArrived += OnProcedureCompleted;
            try { _condition.EnableResultCallback(); _procedure.EnableResultCallback(); }
            catch
            {
                _condition.ModuleResultCallBackArrived -= OnConditionResult;
                _procedure.ModuleResultCallBackArrived -= OnProcedureCompleted;
                throw;
            }
        }

        private sealed class InspectionTarget
        {
            public VmProcedure Procedure;
            public IfModuleTool Condition;
            public string Description { get { return Procedure.Name + " / " + Condition.Name; } }
        }

        // Locate the actual modules from the recipe currently running on the camera.
        // Optional config names are preferences, never mandatory hardcoded defaults.
        // Do not guess if two candidate inspectors are equally plausible.
        private InspectionTarget AutoSelectInspectionTarget(string preferredProcedure, string preferredCondition)
        {
            var list = VmSolution.Instance.GetAllProcedureList();
            var available = new List<string>();
            var targets = new List<InspectionTarget>();
            var failures = new List<string>();
            if (list.astProcessInfo == null || list.nNum == 0)
                throw new InvalidOperationException("카메라의 실행 중인 레시피에서 검사 흐름을 찾지 못했습니다.");
            for (int p = 0; p < list.nNum && p < list.astProcessInfo.Length; p++)
            {
                string name = list.astProcessInfo[p].strProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                VmProcedure procedure = VmSolution.Instance[name] as VmProcedure;
                if (procedure == null) continue;
                available.Add(name);
                var modules = procedure.GetAllModuleList();
                if (modules.astModuleInfo == null) continue;
                for (int i = 0; i < modules.nNum && i < modules.astModuleInfo.Length; i++)
                {
                    var condition = procedure.GetModuleByID(modules.astModuleInfo[i].nModuleID) as IfModuleTool;
                    if (condition == null) continue;
                    try
                    {
                        // The numeric Min/Max/ReMark dynamic parameters identify a usable
                        // Condition Detection module without assuming its display name.
                        ConditionParameterReader.Read(key =>
                        {
                            string value = string.Empty;
                            int error = condition.ModuParams.GetParamValue(key, ref value);
                            if (error != 0) throw new VmException(error);
                            return value;
                        }, _definitions);
                        targets.Add(new InspectionTarget { Procedure = procedure, Condition = condition });
                    }
                    catch (Exception ex)
                    {
                        // Most other module types are not Condition Detection tools.
                        failures.Add(name + "/" + condition.Name + ": " + ex.Message);
                    }
                }
            }
            if (targets.Count == 0)
                throw new InvalidOperationException("Condition Detection 조건 모듈을 자동으로 찾지 못했습니다." +
                    "\r\n검색한 흐름: " + string.Join(", ", available) +
                    "\r\nVisionMaster에서 해당 검사 흐름의 Condition Detection에 숫자 Min/Max 조건이 있는지 확인하세요.");

            string pp = (preferredProcedure ?? "").Trim();
            string pc = (preferredCondition ?? "").Trim();
            var candidates = targets.AsEnumerable();

            // Fully specified and valid pair wins; otherwise use uniquely matching name(s).
            if (pp.Length > 0 && pc.Length > 0)
            {
                var both = candidates.Where(t => t.Procedure.Name == pp && t.Condition.Name == pc).ToList();
                if (both.Count == 1) return both[0];
            }
            if (pp.Length > 0)
            {
                var byProcedure = candidates.Where(t => t.Procedure.Name == pp).ToList();
                if (pc.Length > 0)
                {
                    var preferred = byProcedure.Where(t => t.Condition.Name == pc).ToList();
                    if (preferred.Count == 1) return preferred[0];
                }
                if (byProcedure.Count == 1) return byProcedure[0];
            }
            if (pc.Length > 0)
            {
                var byCondition = candidates.Where(t => t.Condition.Name == pc).ToList();
                if (byCondition.Count == 1) return byCondition[0];
            }
            if (targets.Count == 1) return targets[0];

            var enabled = targets.Where(t => t.Procedure.IsEnabled).ToList();
            if (enabled.Count == 1) return enabled[0];

            throw new InvalidOperationException("검사 모듈 후보가 여러 개라 자동 선택하지 않았습니다." +
                "\r\n후보: " + string.Join(" | ", targets.Select(t => t.Description)) +
                "\r\n선택하려는 흐름/모듈 이름을 config.ini의 PROCEDURE 및 CONDITION_MODULE에 지정하세요.");
        }

        private void OnConditionResult(object sender, EventArgs args)
        {
            lock (_callbackGate)
            {
                if (_disposed || _paused) return;
                try
                {
                    var callback = args as VMControls.Interface.ValueEventArgs;
                    if (callback == null || !(callback.Value is ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)) return;
                    var frame = (ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)callback.Value;
                    _conditionCallbacks++;
                    if (frame.bIsSelfExecute != 0) return;
                    string verdict = null;
                    if (frame.pInfo != null)
                    {
                        var field = frame.pInfo.FirstOrDefault(f => (f.strParamName ?? "").Trim('%') == "StrResult");
                        verdict = field.pStringValue != null && field.pStringValue.Length == 1 ? field.pStringValue[0].strValue : null;
                    }
                    bool ng = frame.nErrorCode == 0 && string.Equals((verdict ?? "").Trim(), "NG", StringComparison.OrdinalIgnoreCase);
                    bool error = frame.nErrorCode != 0 || (!ng && !string.Equals((verdict ?? "").Trim(), "OK", StringComparison.OrdinalIgnoreCase));
                    if (_ngFrames.Record(frame.nExecuteCount, ng, null, error)) RequestCapture(frame.nExecuteCount);
                }
                catch (Exception ex) { Trace.WriteLine("NG capture result: " + ex); }
            }
        }
        private void RequestCapture(uint frame, string detail = null)
        {
            _captureRequests++;
            NgCaptureRequested?.Invoke(this, new NgCaptureEventArgs { Frame = frame, TimeUtc = DateTime.UtcNow, ProcedureGuid = _procedure.GUID, Outcome = _ngFrames.Outcome(frame), Detail = detail ?? (_ngFrames.Outcome(frame) == "ERROR" ? "Condition Detection 판정 오류" : "NG 판정") });
        }
        private void OnProcedureCompleted(object sender, EventArgs args)
        {
            lock (_callbackGate)
            {
                if (_disposed || _paused) return;
                InspectionResultEventArgs snapshot;
                var callback = args as VMControls.Interface.ValueEventArgs;
                _procedureCallbacks++;
                try
                {
                    bool normal = _procedure.GetIsExecuteNormal() == 1;

                    string text = normal ? _condition.ModuResult.StrResult : ReadExecutionError();
                    InspectionOutcome outcome = InspectionCounter.Classify(normal, text);
                    string detail = outcome == InspectionOutcome.Error ? "Error: " + (string.IsNullOrWhiteSpace(text) ? "판정 결과 없음" : text) : text.Trim().ToUpperInvariant();
                    snapshot = new InspectionResultEventArgs(outcome, detail);
                    if (callback != null && _ngFrames.Record(callback.ExecuteCount, null, normal, outcome == InspectionOutcome.Error))
                        RequestCapture(callback.ExecuteCount, outcome == InspectionOutcome.Error ? detail : null);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    snapshot = new InspectionResultEventArgs(InspectionOutcome.Error, "결과 읽기 오류: " + ex.Message);
                    if (callback != null && _ngFrames.Record(callback.ExecuteCount, null, false)) RequestCapture(callback.ExecuteCount, snapshot.Detail);
                }
                if (callback != null && _csvCapture != null)
                    _csvCapture.ProcedureFinished(callback.ExecuteCount, snapshot.Outcome);
                var handler = ResultArrived;
                if (handler != null) handler(this, snapshot);
            }
        }
        // Called only when the operator opts in. No camera recipe/Save Text parameters are changed.
        public void SetCsvCaptureEnabled(bool enabled)
        {
            if (enabled)
            {
                lock (_callbackGate)
                {
                    if (_disposed) throw new ObjectDisposedException("RemoteInspectionSession");
                    if (_csvCapture != null) return;
                }
                var created = new CsvModuleCapture(_procedure);
                created.SampleReady += OnCsvSample;
                bool accepted = false;
                lock (_callbackGate)
                {
                    if (!_disposed && _csvCapture == null) { _csvCapture = created; accepted = true; }
                }
                if (!accepted) created.Dispose();
                if (!accepted) throw new InvalidOperationException("CSV 수집을 시작할 수 없습니다. 카메라 연결을 확인하세요.");
            }
            else
            {
                CsvModuleCapture previous;
                lock (_callbackGate) { previous = _csvCapture; _csvCapture = null; }
                if (previous != null) previous.Dispose();
            }
        }

        private void OnCsvSample(CsvInspectionSample sample)
        {
            var handler = CsvSampleReady;
            if (handler != null) handler(this, sample);
        }

        private string ReadExecutionError()
        {
            const string summary = "검사 흐름 실행 오류";
            try
            {
                var errors = _procedure.GetModuErrorInfoList();
                if (errors == null) return summary + " (SDK 상세 정보 없음)";
                var details = new List<string>();
                foreach (var error in errors)
                {
                    if (error.nErrorCode == 0) continue;
                    string name = string.IsNullOrWhiteSpace(error.strDisplayName) ? error.strModuleName : error.strDisplayName;
                    details.Add(name + " (ID " + error.nModuleID + ") / 0x" + error.nErrorCode.ToString("X8"));
                }
                return summary + (details.Count == 0 ? " (SDK 상세 정보 없음)" : "\r\n" + string.Join("\r\n", details));
            }
            catch (Exception ex) { return summary + " / 상세 조회 실패: " + ex.Message; }
        }
        public IList<ConditionRange> ReadRanges()
        {
            return ConditionParameterReader.Read(ReadParameter, _definitions);
        }
        private string ReadParameter(string name)
        {
            string value = string.Empty;
            int error = _condition.ModuParams.GetParamValue(name, ref value);
            if (error != 0) throw new VmException(error);
            return value;
        }
        private void WriteParameter(string name, double value, bool integer)
        {
            int error = _condition.ModuParams.SetParamValue(name, value.ToString(integer ? "0" : "R", System.Globalization.CultureInfo.InvariantCulture));
            if (error != 0) throw new VmException(error);
        }
        public void WriteRange(ConditionRange range)
        {
            range.Validate();
            // Re-discover before writing; never create a parameter from a stale grid row.
            var current = System.Linq.Enumerable.FirstOrDefault(ReadRanges(), r => r.Definition.Name == range.Definition.Name);
            if (current == null) throw new InvalidOperationException("조건 항목 없음: " + range.Definition.Name);
            string name = range.Definition.Name;
            if (range.Min > current.Max)
            {
                WriteParameter(name + "_Max", range.Max, range.Definition.IsInteger);
                WriteParameter(name + "_Min", range.Min, range.Definition.IsInteger);
            }
            else
            {
                WriteParameter(name + "_Min", range.Min, range.Definition.IsInteger);
                WriteParameter(name + "_Max", range.Max, range.Definition.IsInteger);
            }
        }

        public void Pause()
        {
            _recipeSave.Begin(); // Resolve before changing camera parameters or execution state.
            _wasContinuous = _procedure.ContinuousRunEnable;
            _wasEnabled = _procedure.IsEnabled;
            lock (_callbackGate) { _paused = true; _ngFrames.Clear(); }
            try
            {
                _procedure.ContinuousRunEnable = false;
                _procedure.IsEnabled = false;
            }
            catch
            {
                try { Resume(); } catch { }
                throw;
            }
        }
        public void Save()
        {
            // IsEnabled is also a recipe setting: save the original enabled state,
            // not our temporary editing state. All intervals have been verified by this point.
            if (_wasEnabled) _procedure.IsEnabled = true;
            try { _recipeSave.Save(); }
            finally { if (_wasEnabled) _procedure.IsEnabled = false; }
        }
        public void Resume()
        {
            if (_wasEnabled) _procedure.IsEnabled = true;
            else _procedure.IsEnabled = false;
            // Accept callbacks before restarting continuous acquisition.
            lock (_callbackGate) _paused = false;
            _procedure.ContinuousRunEnable = _wasContinuous;
        }
        public void Dispose()
        {
            CsvModuleCapture previous;
            lock (_callbackGate) { _disposed = true; previous = _csvCapture; _csvCapture = null; ResultArrived = null; CsvSampleReady = null; NgCaptureRequested = null; _ngFrames.Clear(); }
            if (previous != null) previous.Dispose();
            _procedure.ModuleResultCallBackArrived -= OnProcedureCompleted;
            _condition.ModuleResultCallBackArrived -= OnConditionResult;
            // Do not DisableModulesCallback: the embedded frontend also uses SDK callbacks.
        }
    }
}
