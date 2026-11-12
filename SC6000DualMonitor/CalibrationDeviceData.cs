using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationOutput
    {
        public string Name, Type, Extra;
        public int DataType;
        public string Signature { get { return new JavaScriptSerializer().Serialize(new object[] { Name, Type, Extra, DataType }); } }
    }

    // Query the device on EVERY read. VmModule.Outputs is an additive, lazy cache:
    // its template %var0% can survive after the camera's actual outputs have been loaded.
    internal static class CalibrationDeviceData
    {
        internal static ImvsSdkDefine.IMVS_MODULE_INFO[] GlobalVariables(ImvsSdkDefine.IMVS_MODULE_INFO_LIST? inventory)
        {
            if (!inventory.HasValue || inventory.Value.nNum > 2064 ||
                (inventory.Value.nNum > 0 && (inventory.Value.astModuleInfo == null || inventory.Value.nNum > inventory.Value.astModuleInfo.Length)))
                throw new InvalidOperationException("카메라의 전역 모듈 목록을 읽지 못했습니다. 변수가 없다는 뜻은 아닙니다. 다시 연결하세요.");
            var list = inventory.Value;
            var result = new List<ImvsSdkDefine.IMVS_MODULE_INFO>();
            for (int i = 0; i < list.nNum; i++)
                if (Text(list.astModuleInfo[i].strModuleName) == "GlobalVariableModule") result.Add(list.astModuleInfo[i]);
            if (result.Select(m => m.nNodeID).Distinct().Count() != result.Count)
                throw new InvalidOperationException("카메라 전역 모듈 ID가 중복되어 있습니다. 다시 연결하세요.");
            return result.ToArray();
        }
        internal static string Text(byte[] bytes)
        {
            if (bytes == null) throw new InvalidOperationException("카메라 항목 문자열이 없습니다.");
            int end = Array.IndexOf(bytes, (byte)0);
            return new UTF8Encoding(false, true).GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }
        internal static CalibrationOutput[] Decode(ImvsSdkDefine.IMVS_MODULE_IO_INFO_LIST list)
        {
            if (list == null || list.nNum < 0 || list.nNum > 10000 || (list.nNum > 0 && list.pstModuIoInfo == IntPtr.Zero))
                throw new InvalidOperationException("카메라 계산 항목 목록을 읽지 못했습니다.");
            var result = new List<CalibrationOutput>();
            try
            {
                int stride = Marshal.SizeOf(typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
                for (int i = 0; i < list.nNum; i++)
                {
                    var item = (ImvsSdkDefine.IMVS_MODULE_IO_INFO)Marshal.PtrToStructure(IntPtr.Add(list.pstModuIoInfo, checked(i * stride)), typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
                    result.Add(new CalibrationOutput { Name = Text(item.szParamName), Type = Text(item.szParamType), Extra = Text(item.szExtraData), DataType = item.nDataType });
                }
                if (result.Any(x => string.IsNullOrWhiteSpace(x.Name)) || result.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != result.Count)
                    throw new InvalidOperationException("카메라 계산 항목 목록에 빈 이름 또는 중복 이름이 있습니다.");
                return result.ToArray();
            }
            finally { GC.KeepAlive(list); } // The SDK list finalizer owns its nested native buffers.
        }
        public static CalibrationOutput[] Outputs(VmModule module)
        {
            return Decode(module.SolRoot.ServerSDKManagerInstance.mModuleManager.GetModuOutputIOInfo(module.ID));
        }
        public static string InventorySignature(IEnumerable<CalibrationOutput> items)
        {
            return new JavaScriptSerializer().Serialize(items.Select(x => x.Signature).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        internal static bool ScalarLink(ImvsSdkDefine.IMVS_SUBSCRIBE_INFO link, uint consumer, uint publisher, string output)
        {
            if (link.nSubModuleID != consumer || link.nPubModuleID != publisher || link.nIsPubConstant != 0 ||
                link.nIsIndexConstant != 1 || link.nIndexModuleID != 0) return false;
            string index = Text(link.strIndex);
            return (index == "All" || index == "0") && Text(link.strPubOutput).Trim('%') == output;
        }
        public static ImvsSdkDefine.IMVS_SUBSCRIBE_INFO[] Subscriptions(VmModule module)
        {
            var ids = new ImvsSdkDefine.IMVS_MODULE_ID_LIST { nNum = 1, anModuleId = new uint[2064], nReserved = new uint[4] };
            ids.anModuleId[0] = module.ID;
            var list = module.SolRoot.ServerSDKManagerInstance.mModuleManager.GetMultiModuleSubscribe(ids);
            if (list == null) throw new InvalidOperationException("카메라 계산 입력 연결을 읽지 못했습니다.");
            var result = list.pstSubscribeInfo();
            if (result == null || result.Length != list.nNum) throw new InvalidOperationException("카메라 계산 입력 연결이 불완전합니다.");
            return result;
        }
        public static string BindingSignature(VmModule module)
        {
            var inputs = Decode(module.SolRoot.ServerSDKManagerInstance.mModuleManager.GetModuInputIOInfo(module.ID));
            var links = Subscriptions(module).Select(x => new JavaScriptSerializer().Serialize(new object[] {
                x.nSubModuleID, Text(x.strSubInput), x.nPubModuleID, Text(x.strPubOutput), x.nIsPubConstant,
                x.nIndexModuleID, Text(x.strIndex), x.nIsIndexConstant })).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return InventorySignature(inputs) + "\n" + new JavaScriptSerializer().Serialize(links);
        }
    }
}
