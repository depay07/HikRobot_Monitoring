using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GlobalSmartCameraModuleCs;
using ImageSourceModuleCs;
using VM.Core;

namespace SC6000DualMonitor
{
    internal sealed class RemoteExposureSession : IExposureSession
    {
        private readonly RemoteInspectionSession _owner;
        private readonly VmSolution _solution;
        private readonly VmProcedure _procedure;
        private readonly GlobalSmartCameraModuleTool _camera;
        private readonly IntPtr _handle;
        private readonly string _recipe;
        private readonly uint[] _sources;
        public string Description { get { return _owner.CameraProcedureDescription + "\r\n" + _recipe + "\r\n" + _camera.Name; } }
        public override string ToString() { return _camera.Name + " (ID " + _camera.ID + ")"; }
        private RemoteExposureSession(RemoteInspectionSession owner, VmProcedure procedure, GlobalSmartCameraModuleTool camera, uint[] sources)
        {
            _owner = owner; _procedure = procedure; _camera = camera; _sources = sources;
            _solution = VmSolution.Instance; _handle = _solution.BaseHandle;
            _recipe = new RemoteSolutionSession(null, null).VerifiedCurrentPath;
        }
        public static IList<IExposureSession> Discover(RemoteInspectionSession owner, VmProcedure procedure)
        {
            var solution = VmSolution.Instance;
            var inventory = solution.ServerSDKManagerInstance.mGlobalModuleManager.GetAllGlobalModule();
            if (!inventory.HasValue || inventory.Value.nNum > 2064 || (inventory.Value.nNum > 0 &&
                (inventory.Value.astModuleInfo == null || inventory.Value.nNum > inventory.Value.astModuleInfo.Length)))
                throw new InvalidOperationException("카메라 모듈 목록을 읽지 못했습니다. 다시 연결하세요.");
            var sources = new Dictionary<uint, List<uint>>();
            var local = procedure.GetAllModuleList();
            for (int i = 0; i < local.nNum; i++)
            {
                var source = procedure.GetModuleByID(local.astModuleInfo[i].nModuleID) as ImageSourceModuleTool;
                if (source == null || source.ModuParams.ImageSourceType != ImageSourceParam.ImageSourceTypeEnum.Camera) continue;
                uint id;
                if (!uint.TryParse(ReadParameter(solution, source.ID, "CameraID"), out id))
                    throw new InvalidOperationException(source.Name + ": 연결된 카메라 ID를 읽지 못했습니다.");
                if (!sources.ContainsKey(id)) sources[id] = new List<uint>();
                sources[id].Add(source.ID);
            }
            var result = new List<IExposureSession>();
            for (int i = 0; i < inventory.Value.nNum; i++)
            {
                var info = inventory.Value.astModuleInfo[i];
                if (CalibrationDeviceData.Text(info.strModuleName) != "GlobalSmartCameraModule" || !sources.ContainsKey(info.nNodeID)) continue;
                var camera = solution.GetModuleByID(info.nNodeID) as GlobalSmartCameraModuleTool;
                if (camera == null) throw new InvalidOperationException("스마트 카메라 SDK 객체를 불러오지 못했습니다. 뷰어를 다시 연결하세요.");
                result.Add(new RemoteExposureSession(owner, procedure, camera, sources[info.nNodeID].ToArray()));
            }
            if (result.Count == 0) throw new InvalidOperationException("현재 검사 흐름의 Image Source에 연결된 스마트 카메라를 찾지 못했습니다. VisionMaster에서 이미지 소스가 Camera인지 확인하세요.");
            return result;
        }
        private static string ReadParameter(VmSolution solution, uint id, string name)
        {
            string value = "";
            solution.ServerSDKManagerInstance.mModuleManager.GetParamValue(id, name, ref value, true);
            return value;
        }
        private void Guard()
        {
            if (_owner.IsClosed || !ReferenceEquals(_solution, VmSolution.Instance) || _solution.IsDispose || _handle != _solution.BaseHandle ||
                new RemoteSolutionSession(null, null).VerifiedCurrentPath != _recipe ||
                !ReferenceEquals(_solution.GetModuleByID(_camera.ID), _camera) ||
                !ReferenceEquals(_solution.GetModuleByID(_procedure.ID), _procedure))
                throw new InvalidOperationException("카메라 연결 또는 레시피가 변경되었습니다. Set Up을 다시 여세요.");
            foreach (var id in _sources)
            {
                var source = _procedure.GetModuleByID(id) as ImageSourceModuleTool;
                if (source == null || source.ModuParams.ImageSourceType != ImageSourceParam.ImageSourceTypeEnum.Camera ||
                    ReadParameter(_solution, id, "CameraID") != _camera.ID.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("Image Source의 카메라 연결이 변경되었습니다. Set Up을 다시 여세요.");
                if (CalibrationDeviceData.Subscriptions(source).Any(s => {
                    var input = CalibrationDeviceData.Text(s.strSubInput);
                    return input == "ExposureTimeInput" || input == "GainInput";
                })) throw new InvalidOperationException("Image Source에 노출/게인 입력이 연결되어 있어 실행 시 값을 덮어씁니다. VisionMaster에서 해당 입력 연결을 해제한 후 사용하세요.");
            }
            var local = _procedure.GetAllModuleList();
            for (int i = 0; i < local.nNum; i++)
                if (local.astModuleInfo[i].strModuleName == "CameraImageParamsModule")
                    throw new InvalidOperationException("검사 흐름에 Camera Image Params 모듈이 있어 노출/게인을 덮어쓸 수 있습니다. 해당 모듈 설정을 VisionMaster에서 확인하세요.");
        }
        public ExposureSettings Read()
        {
            Guard();
            var result = new ExposureSettings {
                Exposure = ReadNumeric("ExposureTime"),
                Gain = ReadNumeric("Gain"),
                AutoExposure = Mode("AutoExposureMode"), AutoGain = Mode("AutoGainMode") };
            return result;
        }
        private CameraParameterValue ReadNumeric(string name)
        {
            string raw = ReadParameter(_solution, _camera.ID, name);
            try { return CameraParameterValue.Parse(raw); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(name + ": " + ex.Message +
                    " (응답 필드 수: " + (raw ?? "").Split('$').Length + ")", ex);
            }
        }
        private int Mode(string name)
        {
            int mode;
            if (!int.TryParse(ReadParameter(_solution, _camera.ID, name).Split('$')[0], out mode) || mode < 0 || mode > 2)
                throw new InvalidOperationException("노출/게인 자동 모드를 확인하지 못했습니다: " + name);
            return mode;
        }
        public void Write(double exposure, double gain)
        {
            var current = Read(); current.Validate(exposure, gain);
            // Same checked IMVS_SetParamValue path as GlobalSmartCameraParam; explicitly
            // serialize invariant decimals (the SDK's typed setter uses CurrentCulture).
            var manager = _solution.ServerSDKManagerInstance.mModuleManager;
            manager.SetParamValue(_camera.ID, "ExposureTime", exposure.ToString("R", CultureInfo.InvariantCulture), true);
            manager.SetParamValue(_camera.ID, "Gain", gain.ToString("R", CultureInfo.InvariantCulture), true);
        }
        public void Pause() { Guard(); _owner.Pause(); }
        public void Save() { Guard(); _owner.Save(); }
        public void Resume() { Guard(); _owner.Resume(); }
    }
}
