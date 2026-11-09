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
        private readonly VmModule[] _sources = new VmModule[4];
        private readonly string[] _fields = new string[4];
        private readonly object _gate = new object();
        private readonly CalibrationFrameCollector _frames = new CalibrationFrameCollector();
        private readonly List<VmModule> _subscriptions = new List<VmModule>();
        private CalibrationFrame _ready;
        private bool _armed, _disposed;
        private readonly string _binding;
        private readonly RemoteSolutionSession _solution = new RemoteSolutionSession(null, null);
        private readonly string _recipePath;
        public string Description { get { return _owner.CameraProcedureDescription + " / " + _calculator.Name; } }
        public CalibrationModel Baseline { get; private set; }
        public RemoteCalibrationSession(RemoteInspectionSession owner, VmProcedure procedure)
        {
            _owner = owner;
            _recipePath = _solution.CurrentPath;
            if (string.IsNullOrWhiteSpace(_recipePath)) throw new InvalidOperationException("현재 카메라 레시피 경로를 확인할 수 없습니다. 보정값은 변경하지 않았습니다.");
            var list = procedure.GetAllModuleList();
            var matches = new List<CalculatorModuleTool>();
            for (int i = 0; list.astModuleInfo != null && i < list.nNum && i < list.astModuleInfo.Length; i++)
            {
                var module = procedure.GetModuleByID(list.astModuleInfo[i].nModuleID) as CalculatorModuleTool;
                if (module != null && CalibrationModel.Names.All(n => module.Outputs.GetAllIO().Any(o => o.Name.Trim('%') == n))) matches.Add(module);
            }
            if (matches.Count != 1) throw new InvalidOperationException("top_size, middle_size, bottom_size, circle_size가 있는 Calculator가 정확히 1개여야 합니다.");
            _calculator = matches[0];
            Baseline = ReadModel();
            _binding = BindingSignature();
            for (int i = 0; i < 4; i++)
            {
                var input = _calculator.Inputs.GetAllIO().SingleOrDefault(x => x.Name.Trim('%') == Baseline.Inputs[i]);
                var source = input == null ? null : input.RelateIO;
                string expected = i == 3 ? "CircleRadius" : "L2LAbsDist";
                if (source == null || source.Name != expected || source.Root == null)
                    throw new InvalidOperationException(CalibrationModel.Captions[i] + ": 원본 " + expected + " 측정값 연결을 확인할 수 없습니다.");
                _sources[i] = source.Root; _fields[i] = source.Name;
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
            return string.Join(";", _calculator.Inputs.GetAllIO().OrderBy(x => x.Name).Select(x => x.Name + "=" +
                (x.RelateIO == null ? "" : x.RelateIO.Root.ID + ":" + x.RelateIO.Name)));
        }
        private void VerifyRecipe()
        {
            if (_owner.IsClosed || !string.Equals(_recipePath, _solution.CurrentPath, StringComparison.Ordinal))
                throw new InvalidOperationException("카메라 연결 또는 레시피가 변경되었습니다. 보정 창을 다시 여세요.");
        }
        public CalibrationModel ReadModel()
        {
            VerifyRecipe();
            var names = _calculator.Outputs.GetAllIO().Where(o => o is DynamicVmIO && !o.IsResultShow).Select(o => o.Name.Trim('%')).ToArray();
            if (names.Length != 4 || !names.OrderBy(n => n).SequenceEqual(CalibrationModel.Names.OrderBy(n => n)))
                throw new InvalidOperationException("이 보정 메뉴는 01.solx의 4개 치수 계산 구조를 지원합니다. 추가 계산 항목이 있는 레시피는 변경하지 않습니다.");
            if (_binding != null && BindingSignature() != _binding) throw new InvalidOperationException("Calculator의 측정값 연결이 변경되었습니다. 보정 창을 다시 여세요.");
            return new CalibrationModel(CalibrationModel.Names.Select(n => ((IUserStringData)_calculator.Params)[n]).ToArray());
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
                for (int i = 0; i < 4; i++)
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
            // Match the installed Calculator UI protocol, preserving all four names/types/indices and inputs.
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
            lock (_gate) { _disposed = true; _armed = false; _frames.Clear(); }
            foreach (var source in _subscriptions)
                try { source.ModuleResultCallBackArrived -= OnSource; }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Calibration unsubscribe: " + ex); }
            _subscriptions.Clear(); // Other viewer features also depend on callbacks; never disable them globally.
        }
    }
}
