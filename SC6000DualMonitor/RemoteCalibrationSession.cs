using System;
using System.Collections.Generic;
using System.Linq;
using GlobalVariableModuleCs;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class RemoteCalibrationSession : ICalibrationEditorSession
    {
        private readonly RemoteInspectionSession _owner;
        private readonly CalibrationCatalog _catalog;
        private readonly CalibrationMapping _mapping;
        private readonly GlobalVariableModuleTool _global;
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
        public string Description { get { return _owner.CameraProcedureDescription + " / " + System.IO.Path.GetFileName(_recipePath) + " / " + _global.Name; } }
        public CalibrationModel Baseline { get; private set; }
        public bool[] RadiusInputs { get { return _fields.Select(f => f == "CircleRadius").ToArray(); } }
        public RemoteCalibrationSession(RemoteInspectionSession owner, CalibrationCatalog catalog, CalibrationMapping mapping)
        {
            _owner = owner; _catalog = catalog; _mapping = mapping;
            _recipePath = catalog.Recipe;
            _global = catalog.Resolve(mapping);
            _binding = catalog.BindingSignature();
            _sources = mapping.Rows.Select(r => catalog.Sources.Single(s => s.Module.ID == r.SourceId && s.Field == r.Field).Module).ToArray();
            _fields = mapping.Rows.Select(r => r.Field).ToArray();
            _frames = new CalibrationFrameCollector(4);
            Baseline = ReadModel();
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
        private void VerifyRecipe()
        {
            if (_disposed || _owner.IsClosed || !string.Equals(_recipePath, _solution.CurrentPath, StringComparison.Ordinal))
                throw new InvalidOperationException("카메라 연결 또는 레시피가 변경되었습니다. 보정 창을 다시 여세요.");
            if (_catalog.BindingSignature() != _binding) throw new InvalidOperationException("측정값/전역 변수 연결이 변경되었습니다. 보정 창을 다시 여세요.");
            _catalog.Resolve(_mapping);
        }
        public CalibrationModel ReadModel()
        {
            VerifyRecipe();
            var values = new double[4];
            for (int i = 0; i < 4; i++)
            {
                string name = _mapping.Rows[i].Variable;
                var value = _global.GetVarFloat(name);
                if (value.nValueNum != 1 || value.pFloatVal == null || value.pFloatVal.Length != 1 || !CalibrationModel.Positive(value.pFloatVal[0]))
                    throw new InvalidOperationException(name + ": 보정계수는 양수 float 단일 값이어야 합니다. VisionMaster에서 기존 계산계수를 초기값으로 넣으세요.");
                values[i] = value.pFloatVal[0];
            }
            VerifyRecipe();
            return new CalibrationModel(_mapping.Rows.Select(r => r.Label).ToArray(), _mapping.Rows.Select(r => r.Variable).ToArray(), values);
        }
        public void Arm()
        {
            lock (_gate) { _armed = false; _ready = null; _frames.Clear(); }
            var current = ReadModel();
            if (!Baseline.Same(current)) throw new InvalidOperationException("보정계수가 변경되었습니다. 보정 창을 닫고 다시 여세요.");
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
                    var complete = _frames.Add(data.nExecuteCount, i, (double)results[0].pFloatValue[0] * (_fields[i] == "CircleRadius" ? 2.0 : 1.0));
                    if (complete != null) { _ready = complete; _armed = false; break; }
                }
            }
        }
        public void WriteModel(CalibrationModel model)
        {
            VerifyRecipe();
            if (!Baseline.SameTargets(model)) throw new InvalidOperationException("보정 대상 변수가 변경되었습니다.");
            for (int i = 0; i < model.Count; i++)
            {
                VerifyRecipe();
                float value = (float)model.Factors[i];
                CameraLog.Write(_catalog.Camera, "CalibrationWrite", model.Variables[i] + "=" +
                    value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                _global.SetVarFloat(model.Variables[i], new[] { value });
            }
        }
        public void Pause() { CancelRead(); VerifyRecipe(); _owner.Pause(); }
        private void LogVariableList(string stage)
        {
            // ValueList is variable-list XML, NOT a read of the saved .solx file.
            // Its text representation is diagnostic only. Verify actual values with GetVarFloat.
            try
            {
                var list = _global.GetAllGlobalVar();
                foreach (var row in _mapping.Rows)
                    CameraLog.Write(_catalog.Camera, "CalibrationList", stage + " " + row.Variable + " list=[" +
                        string.Join(" | ", list.Where(v => v.strValueName == row.Variable).Select(v => v.strValueType + ":" + v.strValue)) + "]");
            }
            catch (Exception ex) { CameraLog.Write(_catalog.Camera, "CalibrationListUnavailable", stage + " " + ex.Message); }
        }
        public void Save()
        {
            var before = ReadModel(); // Also checks camera, recipe and bindings before saving.
            LogVariableList("before-save");
            try
            {
                CameraLog.Write(_catalog.Camera, "CalibrationSaveBegin", _recipePath + " " + string.Join("; ",
                    before.Variables.Select((name, i) => name + "=" + ((float)before.Factors[i]).ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                _owner.Save(); // Existing checked SaveAs to the current CAMERA path, never a local dialog.
                if (!before.Same(ReadModel()))
                    throw new InvalidOperationException("레시피 저장 후 카메라의 실행 보정계수가 변경되었습니다.");
                CameraLog.Write(_catalog.Camera, "CalibrationSaveComplete", _recipePath);
                LogVariableList("after-save");
            }
            catch (Exception ex)
            {
                CameraLog.Write(_catalog.Camera, "CalibrationSaveFailed", ex.ToString());
                throw new InvalidOperationException(ex.Message + "\r\n진단 로그: " + CameraLog.PathFor(_catalog.Camera), ex);
            }
        }
        public void Resume() { VerifyRecipe(); _owner.Resume(); }
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
