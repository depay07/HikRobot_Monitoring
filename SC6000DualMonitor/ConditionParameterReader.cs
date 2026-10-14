using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SC6000DualMonitor
{
    // Read the device's dynamic parameter list on every request, including newly added rows.
    internal static class ConditionParameterReader
    {
        public static IList<ConditionRange> Read(Func<string, string> read, IList<ConditionDefinition> fallback)
        {
            var names = new List<string>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in read("DynamicParams").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = entry.Split(',');
                if (fields.Length < 2) throw new InvalidOperationException("카메라 조건 목록 형식이 잘못되었습니다.");
                string key = fields[0].Trim();
                keys.Add(key);
                if (key.EndsWith("_Min", StringComparison.Ordinal))
                {
                    string name = key.Substring(0, key.Length - 4);
                    if (name.StartsWith("float", StringComparison.Ordinal) || name.StartsWith("int", StringComparison.Ordinal))
                        names.Add(name);
                }
            }
            if (names.Count == 0) throw new InvalidOperationException("지정한 Condition Detection에 숫자 조건 항목이 없습니다.");
            var result = new List<ConditionRange>();
            foreach (string name in names.Distinct(StringComparer.Ordinal))
            {
                if (!keys.Contains(name + "_Max") || !keys.Contains(name + "_ReMark"))
                    throw new InvalidOperationException("카메라 조건 항목 구성이 불완전합니다: " + name);
                string caption = read(name + "_ReMark").Trim();
                var configured = fallback.FirstOrDefault(d => d.Name == name);
                if (caption.Length == 0) caption = configured == null ? name : configured.Caption;
                var definition = ConditionDefinition.Parse(name + ":" + name)[0];
                definition = new ConditionDefinition(definition.Name, caption);
                var range = new ConditionRange(definition, Number(read(name + "_Min"), name), Number(read(name + "_Max"), name));
                range.Validate();
                result.Add(range);
            }
            return result;
        }
        internal static double Number(string text, string name)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException("카메라 조건 값이 숫자가 아닙니다: " + name + " = " + text);
            return value;
        }
    }
    internal interface IInspectionTargetInfo
    {
        string TargetDescription { get; }
    }
}
