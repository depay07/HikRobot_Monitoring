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
        // SDK callbacks include start, module data and repeated end notifications.
        // Keep a bounded history per session, including across settings Pause/Resume.
        private readonly HashSet<uint> _completedFrames = new HashSet<uint>();
        private readonly Queue<uint> _completedFrameOrder = new Queue<uint>();
        private long _conditionCallbacks, _procedureCallbacks, _captureRequests;
        public string CaptureDiagnostics
        {
            get { lock (_callbackGate) return "흐름: " + _procedureName + " / 조건: " + _conditionName +
                "\r\n조건 콜백: " + _conditionCallbacks + " / 흐름 콜백: " + _procedureCallbacks +
                " / NG 캡처 요청: " + _captureRequests + "\r\n결과 수신 제외 상태: " + _paused; }
        }
        public string CameraProcedureDescription { get { return _cameraIp + " | " + _procedure.Name; } }
        public bool IsClosed { get { lock (_callbackGate) return _disposed; } }
        public IList<IExposureSession> GetExposureSessions() { return RemoteExposureSession.Discover(this, _procedure); }
        public CalibrationCatalog CalibrationCatalog() { return new CalibrationCatalog(_cameraIp, _procedure); }
        public RemoteCalibrationSession OpenCalibration(CalibrationCatalog catalog, CalibrationMapping mapping) { return new RemoteCalibrationSession(this, catalog, mapping); }
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
            _procedureName = settings.ProcedureName;
            _conditionName = settings.ConditionModuleName;
            _cameraIp = settings.Ip;
            var solutions = new RemoteSolutionSession(settings.SolutionDirectory, settings.SolutionPassword);
            _recipeSave = new CurrentRecipeSave(() => solutions.CurrentPath,
                path => VmSolution.SaveAs(path)); // null password preserves the SDK recipe password
            _definitions = ConditionDefinition.Parse(settings.ConditionItems);
            _procedure = VmSolution.Instance[_procedureName] as VmProcedure;
            if (_procedure == null) throw new InvalidOperationException("검사 흐름을 찾을 수 없습니다: " + _procedureName + "\r\nconfig.ini의 PROCEDURE를 확인하세요.");
            // Use SDK instance names, not the generic title of the parameter dialog.
            var modules = _procedure.GetAllModuleList();
            var candidates = new List<IfModuleTool>();
            for (int i = 0; i < modules.nNum; i++)
            {
                var module = _procedure.GetModuleByID(modules.astModuleInfo[i].nModuleID) as IfModuleTool;
                if (module != null) candidates.Add(module);
            }
            var matches = candidates.Where(c => string.Equals(c.Name, _conditionName, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException("Condition Detection 모듈 이름을 정확히 지정하세요: " + _conditionName +
                    "\r\nSDK에서 확인한 이름: " + string.Join(", ", candidates.Select(c => c.Name + " (ID " + c.ID + ")")));
            _condition = matches[0];
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
                if (callback == null || !(callback.Value is ImvsSdkDefine.IMVS_MODULE_WORK_STAUS)) return;
                var status = (ImvsSdkDefine.IMVS_MODULE_WORK_STAUS)callback.Value;
                // Status 0 is the procedure end; execute count 0 is not an inspection.
                // Filter before reading mutable SDK results, capturing or exporting CSV.
                if (status.nWorkStatus != 0 || status.nExecuteCount == 0) return;
                uint frame = status.nExecuteCount;
                if (!_completedFrames.Add(frame)) return;
                _completedFrameOrder.Enqueue(frame);
                while (_completedFrameOrder.Count > 128) _completedFrames.Remove(_completedFrameOrder.Dequeue());
                try
                {
                    bool normal = _procedure.GetIsExecuteNormal() == 1;

                    string text = normal ? _condition.ModuResult.StrResult : ReadExecutionError();
                    InspectionOutcome outcome = InspectionCounter.Classify(normal, text);
                    string detail = outcome == InspectionOutcome.Error ? "Error: " + (string.IsNullOrWhiteSpace(text) ? "판정 결과 없음" : text) : text.Trim().ToUpperInvariant();
                    snapshot = new InspectionResultEventArgs(outcome, detail);
                    if (_ngFrames.Record(frame, null, normal, outcome == InspectionOutcome.Error))
                        RequestCapture(frame, outcome == InspectionOutcome.Error ? detail : null);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    snapshot = new InspectionResultEventArgs(InspectionOutcome.Error, "결과 읽기 오류: " + ex.Message);
                    if (_ngFrames.Record(frame, null, false)) RequestCapture(frame, snapshot.Detail);
                }
                // CSV must never interrupt counters or NG/ERROR capture.
                try { if (_csvCapture != null) _csvCapture.ProcedureFinished(frame, snapshot.Outcome); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("CSV completion: " + ex); }
                var handler = ResultArrived;
                if (handler != null) handler(this, snapshot);
            }
        }
        // Called only when the operator opts in. No camera recipe/Save Text parameters are changed.
        public string CsvProcedureName { get { return _procedure.Name; } }
        public string[] ListCsvModuleNames() { return CsvModuleCapture.ListModuleNames(_procedure); }
        public void SetCsvCaptureEnabled(bool enabled, CsvModuleMapping mapping = null)
        {
            if (enabled)
            {
                lock (_callbackGate)
                {
                    if (_disposed) throw new ObjectDisposedException("RemoteInspectionSession");
                    if (_csvCapture != null) return;
                }
                if (mapping == null) throw new InvalidOperationException("CSV 항목 설정을 먼저 완료하세요.");
                var created = new CsvModuleCapture(_procedure, mapping);
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
            CameraLog.Write(_cameraIp, "SettingsPause", "Procedure=" + _procedureName + "; Continuous=" + _wasContinuous + "; Enabled=" + _wasEnabled);
            lock (_callbackGate) { _paused = true; _ngFrames.Clear(); }
            try
            {
                // VM 4.4.50 sends StopExecute even for a redundant false assignment.
                // External-trigger mode is already non-continuous; IsEnabled gates edits.
                if (_wasContinuous) _procedure.ContinuousRunEnable = false;
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
            // Do not send StopExecute after re-enabling an external-trigger procedure.
            // Only a previously continuous procedure needs an explicit restart.
            if (_wasContinuous) _procedure.ContinuousRunEnable = true;
            CameraLog.Write(_cameraIp, "SettingsResume", "Procedure=" + _procedureName + "; Continuous=" + _wasContinuous + "; Enabled=" + _wasEnabled + "; no trailing stop command");
        }
        public void Dispose()
        {
            lock (_callbackGate) _disposed = true;
            SetCsvCaptureEnabled(false);
            lock (_callbackGate) { CsvSampleReady = null; ResultArrived = null; NgCaptureRequested = null; _ngFrames.Clear(); }
            _procedure.ModuleResultCallBackArrived -= OnProcedureCompleted;
            _condition.ModuleResultCallBackArrived -= OnConditionResult;
            // Do not DisableModulesCallback: the embedded frontend also uses SDK callbacks.
        }
    }
}
