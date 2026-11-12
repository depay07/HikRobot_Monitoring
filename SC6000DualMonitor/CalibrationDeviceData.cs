using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationOutput
    {
        public string Name, Type, Extra;
        public int DataType;
        public CalibrationOutput[] Children = new CalibrationOutput[0];
        public string Signature { get { return new JavaScriptSerializer().Serialize(new object[] { Name, Type, Extra, DataType, CalibrationDeviceData.InventorySignature(Children) }); } }
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
            if (list == null) throw new InvalidOperationException("카메라 측정 항목 목록을 읽지 못했습니다.");
            try
            {
                int remaining = 10000;
                return DecodeLevel(list.pstModuIoInfo, list.nNum, 0, new HashSet<IntPtr>(), ref remaining);
            }
            finally { GC.KeepAlive(list); } // SDK finalizer owns ALL nested buffers; copy before returning.
        }
        private static CalibrationOutput[] DecodeLevel(IntPtr pointer, int count, int depth, HashSet<IntPtr> active, ref int remaining)
        {
            if (depth > 32 || count < 0 || count > remaining || (count > 0 && pointer == IntPtr.Zero))
                throw new InvalidOperationException("카메라 측정 항목 구조가 불완전합니다.");
            if (count == 0) return new CalibrationOutput[0];
            if (!active.Add(pointer)) throw new InvalidOperationException("카메라 측정 항목 구조가 순환합니다.");
            remaining -= count;
            try
            {
                var result = new CalibrationOutput[count];
                int stride = Marshal.SizeOf(typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
                for (int i = 0; i < count; i++)
                {
                    var item = (ImvsSdkDefine.IMVS_MODULE_IO_INFO)Marshal.PtrToStructure(IntPtr.Add(pointer, checked(i * stride)), typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
                    result[i] = new CalibrationOutput { Name = Text(item.szParamName), Type = Text(item.szParamType), Extra = Text(item.szExtraData), DataType = item.nDataType };
                    if (item.nDataType == 2)
                        result[i].Children = DecodeLevel(item.pstCombInfo, item.nNum, depth + 1, active, ref remaining);
                }
                // Names are scoped to a group: repeated names in DIFFERENT groups are legal.
                if (result.Any(x => string.IsNullOrWhiteSpace(x.Name)) || result.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != result.Length)
                    throw new InvalidOperationException("카메라 측정 항목 그룹에 빈 이름 또는 중복 이름이 있습니다.");
                return result;
            }
            finally { active.Remove(pointer); }
        }
        internal static IEnumerable<CalibrationOutput> Leaves(IEnumerable<CalibrationOutput> items)
        {
            foreach (var item in items)
            {
                if (item.DataType == 2) { foreach (var child in Leaves(item.Children)) yield return child; }
                else yield return item;
            }
        }
        internal static CalibrationOutput[] Measurements(IEnumerable<CalibrationOutput> items)
        {
            var selected = Leaves(items).Where(x => x.DataType == 1 && x.Type == "float" &&
                (x.Name == "L2LAbsDist" || x.Name == "CircleRadius")).ToArray();
            if (selected.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != selected.Length)
                throw new InvalidOperationException("같은 이름의 원본 픽셀 측정값이 여러 그룹에 있어 구분할 수 없습니다.");
            return selected;
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
