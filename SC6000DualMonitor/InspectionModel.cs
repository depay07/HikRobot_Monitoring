using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SC6000DualMonitor
{
    internal sealed class ConditionDefinition
    {
        public const string DefaultItems = "float0;float1;float2;int0;float3";
        public string Name { get; private set; }
        public string Caption { get; private set; }
        public bool IsInteger { get { return Name.StartsWith("int", StringComparison.Ordinal); } }
        public ConditionDefinition(string name, string caption) { Name = name; Caption = caption; }
        public static IList<ConditionDefinition> Parse(string text)
        {
            var result = new List<ConditionDefinition>();
            foreach (string item in (text ?? DefaultItems).Split(';'))
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                int split = item.IndexOf(':');
                string name = (split < 0 ? item : item.Substring(0, split)).Trim();
                string caption = split < 0 ? name : item.Substring(split + 1).Trim();
                bool valid = name.StartsWith("float", StringComparison.Ordinal) || name.StartsWith("int", StringComparison.Ordinal);
                int index;
                string suffix = name.StartsWith("float", StringComparison.Ordinal) ? name.Substring(5) : name.Length >= 3 ? name.Substring(3) : "";
                if (!valid || !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0)
                    throw new InvalidOperationException("CONDITION_ITEMS 항목 이름을 확인하세요: " + name);
                if (result.Any(d => d.Name == name)) throw new InvalidOperationException("중복 조건 항목: " + name);
                result.Add(new ConditionDefinition(name, string.IsNullOrEmpty(caption) ? name : caption));
            }
            if (result.Count == 0) throw new InvalidOperationException("CONDITION_ITEMS에 하나 이상의 항목을 지정하세요.");
            return result;
        }
    }

    internal sealed class ConditionRange
    {
        public ConditionDefinition Definition { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public ConditionRange(ConditionDefinition definition, double min, double max)
        { Definition = definition; Min = min; Max = max; }
        public void Validate()
        {
            if (double.IsNaN(Min) || double.IsInfinity(Min) || double.IsNaN(Max) || double.IsInfinity(Max) || Min > Max)
                throw new InvalidOperationException(Definition.Caption + ": 최소값은 최대값 이하여야 합니다.");
            if (Definition.IsInteger && (Min != Math.Truncate(Min) || Max != Math.Truncate(Max) || Min < int.MinValue || Max > int.MaxValue))
                throw new InvalidOperationException(Definition.Caption + ": Int32 범위의 정수를 입력하세요.");
        }
        public bool Matches(ConditionRange other)
        { return other != null && Definition.Name == other.Definition.Name && Close(Min, other.Min) && Close(Max, other.Max); }
        private static bool Close(double a, double b)
        { return Math.Abs(a - b) <= 0.0000001 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))); }
    }

    internal enum InspectionOutcome { Ok, Ng, Error }
    internal sealed class InspectionResultEventArgs : EventArgs
    {
        public InspectionOutcome Outcome { get; private set; }
        public string Detail { get; private set; }
        public InspectionResultEventArgs(InspectionOutcome outcome, string detail) { Outcome = outcome; Detail = detail; }
    }
    internal sealed class CountSnapshot
    {
        public long Ok, Ng, Errors;
        public string LastResult;
        public long Total { get { return Ok + Ng + Errors; } }
    }
    internal sealed class InspectionCounter
    {
        private readonly object _gate = new object();
        private long _ok, _ng, _errors;
        private string _last = "검사 대기";
        public void Record(InspectionResultEventArgs result)
        {
            lock (_gate)
            {
                switch (result.Outcome) { case InspectionOutcome.Ok: _ok++; break; case InspectionOutcome.Ng: _ng++; break; default: _errors++; break; }
                _last = result.Detail;
            }
        }
        public void Reset() { lock (_gate) { _ok = _ng = _errors = 0; _last = "검사 대기"; } }
        public CountSnapshot Read() { lock (_gate) return new CountSnapshot { Ok = _ok, Ng = _ng, Errors = _errors, LastResult = _last }; }
        public static InspectionOutcome Classify(bool executionNormal, string result)
        {
            if (!executionNormal) return InspectionOutcome.Error;
            string text = (result ?? "").Trim();
            if (string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase)) return InspectionOutcome.Ok;
            if (string.Equals(text, "NG", StringComparison.OrdinalIgnoreCase)) return InspectionOutcome.Ng;
            // The guide does not document the NIfResult numeric mapping. Never guess it.
            return InspectionOutcome.Error;
        }
    }

    internal interface IInspectionSettingsSession
    {
        IList<ConditionRange> ReadRanges();
        void WriteRange(ConditionRange range);
        void Pause();
        void Resume();
        void Save();
    }
    internal static class InspectionSettingsController
    {
        public static void Apply(IInspectionSettingsSession session, IList<ConditionRange> requested, IList<ConditionRange> baseline = null)
        {
            foreach (var range in requested) range.Validate();
            if (requested.Count == 0 || requested.Select(r => r.Definition.Name).Distinct().Count() != requested.Count)
                throw new InvalidOperationException("허용 범위 항목 구성이 잘못되었습니다.");
            var original = session.ReadRanges();
            if (!original.Select(r => r.Definition.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(requested.Select(r => r.Definition.Name).OrderBy(n => n, StringComparer.Ordinal)))
                throw new InvalidOperationException("설정 항목이 변경되었습니다. Set Up을 다시 여세요.");
            if (baseline != null) VerifyUnchanged(baseline, original);
            session.Pause();
            bool writeAttempted = false, saveAttempted = false;
            try
            {
                // Read again after pausing, so rollback restores the actual pre-edit camera values.
                original = session.ReadRanges();
                if (baseline != null) VerifyUnchanged(baseline, original);
                writeAttempted = true;
                foreach (var range in requested) session.WriteRange(range);
                Verify(requested, session.ReadRanges());
                saveAttempted = true;
                session.Save();
                Verify(requested, session.ReadRanges());
            }
            catch (Exception failure)
            {
                try
                {
                    if (writeAttempted)
                    {
                        foreach (var range in original) session.WriteRange(range);
                        Verify(original, session.ReadRanges());
                        if (saveAttempted) session.Save();
                    }
                }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException("적용 실패 후 기존 범위 복원도 실패했습니다. 해당 검사 흐름을 정지 상태로 유지했습니다. 카메라 설정을 확인하세요.\r\n적용: " + failure.Message + "\r\n복원: " + recovery.Message, failure);
                }
                try { session.Resume(); }
                catch (Exception resume) { throw new InvalidOperationException("기존 범위는 복원했으나 검사 재개에 실패했습니다: " + resume.Message, failure); }
                throw new InvalidOperationException("치수 적용/저장 실패. 기존 범위를 유지하거나 복원했습니다.\r\n" + failure.Message, failure);
            }
            try { session.Resume(); }
            catch (Exception ex) { throw new InvalidOperationException("치수 적용과 저장은 완료했으나 검사 재개에 실패했습니다: " + ex.Message, ex); }
        }
        private static bool SameRanges(IList<ConditionRange> expected, IList<ConditionRange> actual)
        {
            return expected.Count == actual.Count && actual.Select(r => r.Definition.Name).Distinct().Count() == actual.Count &&
                expected.All(r => r.Matches(actual.FirstOrDefault(a => a.Definition.Name == r.Definition.Name)));
        }
        private static void VerifyUnchanged(IList<ConditionRange> baseline, IList<ConditionRange> actual)
        {
            if (!SameRanges(baseline, actual))
                throw new InvalidOperationException("편집 중 카메라 조건이 변경되었습니다. 다시 읽은 후 적용하세요.");
        }
        private static void Verify(IList<ConditionRange> expected, IList<ConditionRange> actual)
        {
            if (!SameRanges(expected, actual))
                throw new InvalidOperationException("카메라에서 다시 읽은 허용 범위가 요청값과 다릅니다.");
        }
    }

    internal sealed class PartNumberStore
    {
        private readonly string _directory;
        public PartNumberStore(string directory) { _directory = directory; }
        private string PathFor(string camera, string solution)
        {
            if (string.IsNullOrWhiteSpace(solution)) throw new InvalidOperationException("현재 레시피 경로를 확인할 수 없어 모델명을 저장하지 않았습니다.");
            using (var sha = SHA256.Create())
            {
                string key = camera + "\n" + solution;
                string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
                return Path.Combine(_directory, hash + ".xml");
            }
        }
        public string Read(string camera, string solution, string fallback)
        {
            string path = PathFor(camera, solution);
            if (!File.Exists(path)) return fallback ?? "";
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            return doc.DocumentElement.GetAttribute("partNo");
        }
        public void Write(string camera, string solution, string partNumber)
        {
            string path = PathFor(camera, solution);
            Directory.CreateDirectory(_directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var doc = new XmlDocument();
                var root = doc.CreateElement("model");
                root.SetAttribute("partNo", partNumber ?? ""); doc.AppendChild(root); doc.Save(temp);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
