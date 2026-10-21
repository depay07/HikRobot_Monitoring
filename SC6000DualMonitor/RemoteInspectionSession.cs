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
        private readonly IfModuleTool _condition;
        private readonly string _procedureName;
        private readonly string _conditionName;
        private readonly string _cameraIp;
        public string TargetDescription { get { return _cameraIp + " | " + _procedure.Name + " / " + _condition.Name + " (ID " + _condition.ModuParams.ModuleID + ")"; } }
        private readonly IList<ConditionDefinition> _definitions;
        private readonly object _callbackGate = new object();
        private bool _disposed, _paused, _wasContinuous, _wasEnabled;
        public event EventHandler<InspectionResultEventArgs> ResultArrived;
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
            _procedureName = settings.ProcedureName;
            _conditionName = settings.ConditionModuleName;
            _cameraIp = settings.Ip;
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
            _condition.EnableResultCallback();
            _procedure.EnableResultCallback();
            _procedure.ModuleResultCallBackArrived += OnProcedureCompleted;
        }
        private void OnProcedureCompleted(object sender, EventArgs args)
        {
            lock (_callbackGate)
            {
                if (_disposed || _paused) return;
                InspectionResultEventArgs snapshot;
                try
                {
                    bool normal = _procedure.GetIsExecuteNormal() == 1;
                    string text = normal ? _condition.ModuResult.StrResult : ReadExecutionError();
                    InspectionOutcome outcome = InspectionCounter.Classify(normal, text);
                    string detail = outcome == InspectionOutcome.Error ? "Error: " + (string.IsNullOrWhiteSpace(text) ? "판정 결과 없음" : text) : text.Trim().ToUpperInvariant();
                    snapshot = new InspectionResultEventArgs(outcome, detail);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    snapshot = new InspectionResultEventArgs(InspectionOutcome.Error, "결과 읽기 오류: " + ex.Message);
                }
                var handler = ResultArrived;
                if (handler != null) handler(this, snapshot);
            }
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
            _wasContinuous = _procedure.ContinuousRunEnable;
            _wasEnabled = _procedure.IsEnabled;
            lock (_callbackGate) _paused = true;
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
            try { VmSolution.Save(); }
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
            lock (_callbackGate) { _disposed = true; ResultArrived = null; }
            _procedure.ModuleResultCallBackArrived -= OnProcedureCompleted;
            // Do not DisableModulesCallback: the embedded frontend also uses SDK callbacks.
        }
    }
}
