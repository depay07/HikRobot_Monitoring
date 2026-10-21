using System;
using System.Collections.Generic;
using System.Linq;

namespace SC6000DualMonitor
{
    // The SDK exposes a named boolean output for each numeric condition, including int hole/blob checks.
    internal static class NgReasonReader
    {
        public static string Read(IList<ConditionDefinition> definitions, Func<string, int?> read)
        {
            var failed = new List<string>();
            var unknown = new List<string>();
            foreach (var definition in definitions)
            {
                int? value;
                try { value = read(definition.Name + "Out"); }
                catch { value = null; }
                if (value == 0) failed.Add(definition.Caption + " 에러");
                else if (value != 1) unknown.Add(definition.Caption);
            }
            if (unknown.Count > 0) failed.Add("판정 확인 불가: " + string.Join(", ", unknown));
            if (failed.Count == 0) return "NG 사유 확인 불가 (개별 판정 결과를 확인하세요)";
            return string.Join("\r\n", failed);
        }
    }
}
