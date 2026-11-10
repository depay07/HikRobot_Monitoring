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
        public bool IsItem
        {
            get
            {
                if (!Name.StartsWith("%", StringComparison.Ordinal) || !Name.EndsWith("%", StringComparison.Ordinal)) return false;
                if (Name.Length < 3) throw new InvalidOperationException("카메라 계산 항목 이름이 비어 있습니다.");
                return !Regex.IsMatch(Extra, @"(?:^|[;\s])IsResultShow\s*=\s*true(?:$|[;\s])", RegexOptions.IgnoreCase);
            }
        }
        public string ItemName { get { return Name.Substring(1, Name.Length - 2); } }
    }

    // Query the device on EVERY read. VmModule.Outputs is an additive, lazy cache:
    // its template %var0% can survive after the camera's actual outputs have been loaded.
    internal static class CalibrationDeviceData
    {
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
        internal static CalibrationModel ReadItems(Func<CalibrationOutput[]> inventory, Func<string, string> read, string[] selection)
        {
            var before = inventory();
            var items = before.Where(x => x.IsItem).ToArray();
            if (items.Length == 0) throw new InvalidOperationException("카메라가 반환한 Calculator 계산 항목이 없습니다.");
            var json = new List<string>();
            foreach (var item in items)
            {
                string data = read(item.ItemName);
                var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(data);
                if (payload == null || !payload.ContainsKey("Name") || !payload.ContainsKey("Type") ||
                    Convert.ToString(payload["Name"]) != item.ItemName || Convert.ToString(payload["Type"]) != item.Type)
                    throw new InvalidOperationException("카메라 목록과 계산식의 이름/유형이 다릅니다: " + item.ItemName);
                json.Add(data);
            }
            if (InventorySignature(before) != InventorySignature(inventory()))
                throw new InvalidOperationException("조회 중 Calculator 항목이 변경되었습니다. 보정 창을 다시 여세요.");
            return new CalibrationModel(json.ToArray(), selection);
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
        internal static ImvsSdkDefine.IMVS_SUBSCRIBE_INFO? MeasurementSource(uint moduleId, string inputName, ImvsSdkDefine.IMVS_SUBSCRIBE_INFO[] links)
        {
            var matches = links.Where(x => x.nSubModuleID == moduleId && Text(x.strSubInput).Trim('%') == inputName).ToArray();
            if (matches.Length != 1 || matches[0].nIsPubConstant != 0) return null;
            var source = matches[0];
            string index = Text(source.strIndex), output = Text(source.strPubOutput);
            // Callback capture below is scalar; a variable or nonzero array index is not equivalent.
            if (source.nIsIndexConstant != 1 || source.nIndexModuleID != 0 || (index != "All" && index != "0")) return null;
            return output == "L2LAbsDist" || output == "CircleRadius" ? source : (ImvsSdkDefine.IMVS_SUBSCRIBE_INFO?)null;
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
