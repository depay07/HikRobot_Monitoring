using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ImageSourceModuleCs;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class RemoteReplayDevice : IReplayDevice
    {
        private readonly VmProcedure _procedure;
        private readonly ImageSourceModuleTool _source;
        private readonly IntPtr _handle;
        private readonly Func<bool> _valid;
        public RemoteReplayDevice(VmProcedure procedure, string moduleName, Func<bool> valid)
        {
            _procedure = procedure; _handle = VmSolution.Instance.BaseHandle; _valid = valid;
            var sources = new List<ImageSourceModuleTool>();
            var list = procedure.GetAllModuleList();
            for (int i = 0; i < list.nNum; i++)
            {
                var source = procedure.GetModuleByID(list.astModuleInfo[i].nModuleID) as ImageSourceModuleTool;
                if (source != null) sources.Add(source);
            }
            // Multiple inputs require supplying all their images. Do not silently mix a new input with stale ones.
            if (sources.Count != 1) throw new InvalidOperationException("재검사는 Image Source가 정확히 1개인 흐름을 지원합니다. 현재 " + sources.Count + "개입니다.");
            _source = sources.Single();
            if (!string.IsNullOrWhiteSpace(moduleName) && _source.Name != moduleName)
                throw new InvalidOperationException("REPLAY_IMAGE_MODULE 불일치. 실제 이름: " + _source.Name);
        }
        private void Guard() { if (!_valid()) throw new InvalidOperationException("레시피가 변경되어 재검사를 중단했습니다. 새 레시피를 다시 연결하세요."); }
        private static void Check(int error) { if (error != 0) throw new VmException(error); }
        public bool Continuous { get { Guard(); return _procedure.ContinuousRunEnable; } set { Guard(); _procedure.ContinuousRunEnable = value; } }
        public bool Enabled
        {
            get { Guard(); uint shield; Check(GetShield(_handle, _procedure.ID, out shield)); return shield == 0; }
            set { Guard(); Check(SetShield(_handle, _procedure.ID, value ? 0u : 1u)); if (Enabled != value) throw new InvalidOperationException("검사 흐름 실행 상태 전환 실패"); }
        }
        public int Source { get { Guard(); return (int)_source.ModuParams.ImageSourceType; } set { Guard(); _source.ModuParams.ImageSourceType = (ImageSourceParam.ImageSourceTypeEnum)value; } }
        public int SdkSource { get { return (int)ImageSourceParam.ImageSourceTypeEnum.SDK; } }
        public void Stop() { Guard(); Check(StopProcess(_handle, _procedure.ID, 5000)); }
        public uint Run(byte[] bytes)
        {
            Guard();
            using (var stream = new MemoryStream(bytes))
            using (var bitmap = new Bitmap(stream))
            {
                if ((long)bitmap.Width * bitmap.Height > 64000000) throw new InvalidOperationException("원본 이미지가 64MP 제한을 초과했습니다.");
                if (bitmap.PixelFormat != PixelFormat.Format8bppIndexed && bitmap.PixelFormat != PixelFormat.Format24bppRgb)
                    throw new InvalidOperationException("원본은 8비트 흑백 또는 24비트 컬러 이미지여야 합니다. 픽셀 값을 임의 변환하지 않습니다.");
                if (bitmap.PixelFormat == PixelFormat.Format8bppIndexed && bitmap.Palette.Entries.Where((c, i) => c.R != i || c.G != i || c.B != i).Any())
                    throw new InvalidOperationException("8비트 컬러 팔레트 이미지는 지원하지 않습니다.");
                var image = new ImageBaseData(bitmap);
                try
                {
                    // Send actual pixels over the connected SDK handle, never a PC path to the camera filesystem.
                    _source.SetImageData(image, true);
                    uint frame = 0; _procedure.Run(null, ref frame, true, true);
                    Guard();
                    if (_procedure.GetIsExecuteNormal() != 1) throw new InvalidOperationException("재검사 흐름 실행 오류. 카메라 모듈 상태를 확인하세요.");
                    return frame;
                }
                finally { GC.KeepAlive(image); }
            }
        }
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_PF_CtrlShieldProcess", CallingConvention = CallingConvention.StdCall)]
        private static extern int SetShield(IntPtr handle, uint procedure, uint shield);
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_PF_GetProcedureShieldStatus", CallingConvention = CallingConvention.StdCall)]
        private static extern int GetShield(IntPtr handle, uint procedure, out uint shield);
        [DllImport(".\\iMVS-6000PlatformSDK.dll", EntryPoint = "IMVS_PF_StopExecute_V30", CallingConvention = CallingConvention.StdCall)]
        private static extern int StopProcess(IntPtr handle, uint procedure, uint wait);
    }
}
