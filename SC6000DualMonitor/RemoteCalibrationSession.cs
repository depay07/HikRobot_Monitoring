using System;
using System.Collections.Generic;
using System.Linq;
using CalculatorModuleCs;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.BaseInterface;

namespace SC6000DualMonitor
{
    internal sealed class RemoteCalibrationSession : ICalibrationEditorSession
    {
        private readonly RemoteInspectionSession _owner;
        private readonly CalculatorModuleTool _calculator;
        private readonly VmProcedure _procedure;
        private readonly ImvsSdkDefine.IMVS_SUBSCRIBE_INFO[] _links;
        private readonly VmModule[] _sources;
        private readonly string[] _fields;
        private readonly object _gate = new object();
        private readonly CalibrationFrameCollector _frames;
        private readonly List<VmModule> _subscriptions = new List<VmModule>();
        private CalibrationFrame _ready;
        private bool _armed, _disposed;
        private readonly string _binding;
        private readonly RemoteSolutionSession _solution = new RemoteSolutionSession(null, null);
        private readonly string _recipePath;
        public string Description { get { return _owner.CameraProcedureDescription + " / " + _calculator.Name; } }
        public CalibrationModel Baseline { get; private set; }
        public bool[] RadiusInputs { get { return _fields.Select(f => f == "CircleRadius").ToArray(); } }
        public static List<CalculatorModuleTool> Modules(VmProcedure procedure)
        {
            var list = procedure.GetAllModuleList();
            var result = new List<CalculatorModuleTool>();
            for (int i = 0; list.astModuleInfo != null && i < list.nNum && i < list.astModuleInfo.Length; i++)
            {
                var module = procedure.GetModuleByID(list.astModuleInfo[i].nModuleID) as CalculatorModuleTool;
                if (module != null) result.Add(module);
            }
            return result;
        }
        public RemoteCalibrationSession(RemoteInspectionSession owner, VmProcedure procedure, string moduleName = null)
        {
            _owner = owner; _procedure = procedure;
            _recipePath = _solution.CurrentPath;
            if (string.IsNullOrWhiteSpace(_recipePath)) throw new InvalidOperationException("현재 카메라 레시피 경로를 확인할 수 없습니다. 보정값은 변경하지 않았습니다.");
            var modules = Modules(procedure);
            if (moduleName != null) modules = modules.Where(m => m.Name == moduleName).ToList();
            if (modules.Count != 1) throw new InvalidOperationException("보정할 Variable Calculation 모듈을 선택하세요. 조회된 모듈: " + string.Join(", ", modules.Select(m => m.Name)));
            _calculator = modules[0];
            _binding = BindingSignature();
            _links = CalibrationDeviceData.Subscriptions(_calculator);
            var all = ReadModel();
            var supported = Enumerable.Range(0, all.Count).Where(i => Source(all.Inputs[i]) != null).Select(i => all.Names[i]).ToArray();
            if (supported.Length == 0) throw new InvalidOperationException("Calculator에서 선 간 거리 또는 원 반지름에 연결된 보정 항목을 찾지 못했습니다. 조회된 항목: " + string.Join(", ", all.Names));
            Baseline = all.Select(supported);
            _sources = new VmModule[Baseline.Count]; _fields = new string[Baseline.Count];
            _frames = new CalibrationFrameCollector(Baseline.Count);
            for (int i = 0; i < Baseline.Count; i++)
            {
                var source = Source(Baseline.Inputs[i]);
                _sources[i] = _procedure.GetModuleByID(source.Value.nPubModuleID);
                if (_sources[i] == null) throw new InvalidOperationException("측정 모듈을 찾지 못했습니다: " + source.Value.nPubModuleID);
                _fields[i] = CalibrationDeviceData.Text(source.Value.strPubOutput);
            }
            try
            {
                foreach (var module in _sources.Distinct())
                {
                    module.ModuleResultCallBackArrived += OnSource;
                    _subscriptions.Add(module);
                    module.EnableResultCallback();
                }
            }
            catch { Dispose(); throw; }
        }
        private string BindingSignature()
        {
            return CalibrationDeviceData.BindingSignature(_calculator);
        }
        private void VerifyRecipe()
        {
            if (_owner.IsClosed || !string.Equals(_recipePath, _solution.CurrentPath, StringComparison.Ordinal))
                throw new InvalidOperationException("카메라 연결 또는 레시피가 변경되었습니다. 보정 창을 다시 여세요.");
        }
        private ImvsSdkDefine.IMVS_SUBSCRIBE_INFO? Source(string inputName)
        {
            return CalibrationDeviceData.MeasurementSource(_calculator.ID, inputName, _links);
        }
        public CalibrationModel ReadModel()
        {
            VerifyRecipe();
            if (_binding != null && BindingSignature() != _binding) throw new InvalidOperationException("Calculator의 측정값 연결이 변경되었습니다. 보정 창을 다시 여세요.");
            var model = CalibrationDeviceData.ReadItems(() => CalibrationDeviceData.Outputs(_calculator), name =>
            {
                try { return ((IUserStringData)_calculator.Params)[name]; }
                catch (Exception ex)
                {
                    var sdk = ex as VmException;
                    string detail = sdk == null ? ex.Message : "SDK 0x" + unchecked((uint)sdk.errorCode).ToString("X8") + " / " + ex.Message;
                    throw new InvalidOperationException("Calculator 항목 읽기 실패: " + name +
                        "\r\n대상: " + Description + " (ID " + _calculator.ID + ")" +
                        "\r\n레시피: " + _recipePath + "\r\n원인: " + detail +
                        "\r\n전체 항목을 읽지 못해 보정값을 변경하지 않았습니다.", ex);
                }
            }, Baseline == null ? null : Baseline.Names);
            VerifyRecipe();
            if (_binding != null && BindingSignature() != _binding) throw new InvalidOperationException("조회 중 측정값 연결이 변경되었습니다. 보정 창을 다시 여세요.");
            return model;
        }

        public void Arm()
        {
            lock (_gate) { _armed = false; _ready = null; _frames.Clear(); }
            var current = ReadModel();
            if (!Baseline.Same(current)) throw new InvalidOperationException("Calculator 설정이 변경되었습니다. 보정 창을 닫고 다시 여세요.");
            lock (_gate) { if (_disposed) throw new ObjectDisposedException("Calibration"); _armed = true; }
        }
        public CalibrationFrame TakeFrame()
        {
            lock (_gate) { var frame = _ready; _ready = null; return frame; }
        }
        public void CancelRead() { lock (_gate) { _armed = false; _ready = null; _frames.Clear(); } }
        private void OnSource(object sender, EventArgs args)
        {
            var callback = args as VMControls.Interface.ValueEventArgs;
            if (callback == null || !(callback.Value is ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)) return;
            var data = (ImvsSdkDefine.IMVS_MODULE_RESULT_INFO_LIST_EX_Data)callback.Value;
            if (data.bIsSelfExecute != 0 || data.nErrorCode != 0 || data.pInfo == null) return;
            lock (_gate)
            {
                if (!_armed || _disposed) return;
                for (int i = 0; i < _sources.Length; i++)
                {
                    if (!ReferenceEquals(sender, _sources[i])) continue;
                    var results = data.pInfo.Where(f => f.strParamName == _fields[i]).ToArray();
                    if (results.Length != 1 || results[0].nValueStatus != 1 || results[0].nValueNum != 1 || results[0].pFloatValue == null || results[0].pFloatValue.Length != 1) continue;
                    var complete = _frames.Add(data.nExecuteCount, i, results[0].pFloatValue[0]);
                    if (complete != null) { _ready = complete; _armed = false; break; }
                }
            }
        }
        public void WriteModel(CalibrationModel model)
        {
            VerifyRecipe();
            if (BindingSignature() != _binding) throw new InvalidOperationException("측정 연결이 변경되어 보정을 중단합니다.");
            // Match the installed Calculator UI protocol, preserving ALL names/types/indices and inputs, including non-calibration items.
            var parameters = (IUserBytesData)_calculator.Params;
            parameters["command#start"] = new byte[1];
            foreach (string json in model.JsonInOrder()) parameters["command#data"] = System.Text.Encoding.UTF8.GetBytes(json);
            ((IUserStringData)_calculator.Params)["command#io"] = "";
            if (BindingSignature() != _binding) throw new InvalidOperationException("적용 후 측정 연결 확인 실패");
        }
        public void Pause() { CancelRead(); VerifyRecipe(); _owner.Pause(); }
        public void Save() { _owner.Save(); }
        public void Resume() { _owner.Resume(); }
        public void Dispose()
        {
            lock (_gate) { _disposed = true; _armed = false; if (_frames != null) _frames.Clear(); }
            foreach (var source in _subscriptions)
                try { source.ModuleResultCallBackArrived -= OnSource; }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Calibration unsubscribe: " + ex); }
            _subscriptions.Clear(); // Other viewer features also depend on callbacks; never disable them globally.
        }
    }
}
