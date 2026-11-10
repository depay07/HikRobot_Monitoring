using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationModel
    {
        private readonly Dictionary<string, object>[] _items;
        private readonly int[] _selected;
        public readonly string[] Names, Inputs, Prefixes;
        public readonly double[] Factors;
        public int Count { get { return Names.Length; } }
        public int PreservedCount { get { return _items.Length - Count; } }
        private static readonly Regex Formula = new Regex(@"^(?<prefix><float:<base64 (?<input>[A-Za-z0-9+/=]+) />>\[0\]\s*\*\s*)(?<factor>[0-9.eE+\-]+)$");
        public CalibrationModel(string[] json, string[] selectedNames = null)
        {
            if (json == null || json.Length == 0) throw new InvalidOperationException("Calculator 계산 항목을 읽지 못했습니다.");
            var serializer = new JavaScriptSerializer();
            _items = json.Select(j => serializer.Deserialize<Dictionary<string, object>>(j)).OrderBy(x => Convert.ToInt32(x["Index"])).ToArray();
            if (_items.Any(x => !x.ContainsKey("Name") || !x.ContainsKey("Expression") || !x.ContainsKey("Type")) ||
                _items.Select(x => Convert.ToString(x["Name"])).Distinct(StringComparer.Ordinal).Count() != _items.Length ||
                _items.Select(x => Convert.ToInt32(x["Index"])).Distinct().Count() != _items.Length)
                throw new InvalidOperationException("Calculator 이름 또는 순서가 중복되어 있습니다.");
            var selected = new List<int>();
            for (int i = 0; i < _items.Length; i++)
            {
                string name = Convert.ToString(_items[i]["Name"]);
                if (selectedNames != null && !selectedNames.Contains(name, StringComparer.Ordinal)) continue;
                double factor;
                var match = Formula.Match(Convert.ToString(_items[i]["Expression"]));
                bool valid = Convert.ToString(_items[i]["Type"]) == "float" && match.Success &&
                    double.TryParse(match.Groups["factor"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out factor) && Positive(factor);
                if (valid) selected.Add(i);
                else if (selectedNames != null) throw new InvalidOperationException(name + ": 보정 대상 계산식이 변경되었습니다. 다시 읽으세요.");
            }
            _selected = selected.ToArray();
            Names = _selected.Select(i => Convert.ToString(_items[i]["Name"])).ToArray();
            if (Names.Length == 0 || (selectedNames != null && !Names.OrderBy(n => n).SequenceEqual(selectedNames.OrderBy(n => n))))
                throw new InvalidOperationException("보정 가능한 '픽셀값 × 양수 계수' 항목을 찾지 못했습니다.");
            var matches = _selected.Select(i => Formula.Match(Convert.ToString(_items[i]["Expression"]))).ToArray();
            Inputs = matches.Select(m => m.Groups["input"].Value).ToArray();
            Prefixes = matches.Select(m => m.Groups["prefix"].Value).ToArray();
            Factors = matches.Select(m => double.Parse(m.Groups["factor"].Value, CultureInfo.InvariantCulture)).ToArray();
        }
        public CalibrationModel Select(string[] names) { return new CalibrationModel(JsonInOrder(), names); }
        public static bool Positive(double value) { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }
        public CalibrationModel Calculate(double[] pixels, double[] actual)
        {
            if (pixels == null || actual == null || pixels.Length != Count || actual.Length != Count) throw new InvalidOperationException("표시된 보정 항목의 측정값과 실제 치수를 입력하세요.");
            var serializer = new JavaScriptSerializer();
            var copies = _items.Select(x => new Dictionary<string, object>(x)).ToArray();
            for (int i = 0; i < Count; i++)
            {
                if (!Positive(pixels[i]) || !Positive(actual[i])) throw new InvalidOperationException(Names[i] + ": 픽셀값과 실제 치수는 0보다 큰 유효한 값이어야 합니다.");
                double factor = actual[i] / pixels[i];
                if (!Positive(factor) || factor > float.MaxValue || factor < float.Epsilon) throw new InvalidOperationException("보정계수가 카메라의 계산 범위를 벗어납니다.");
                copies[_selected[i]]["Expression"] = Prefixes[i] + factor.ToString("R", CultureInfo.InvariantCulture);
            }
            return new CalibrationModel(copies.Select(serializer.Serialize).ToArray(), Names);
        }
        public string[] JsonInOrder() { return _items.Select(new JavaScriptSerializer().Serialize).ToArray(); }
        public bool Same(CalibrationModel other)
        {
            if (other == null || _items.Length != other._items.Length || !Names.SequenceEqual(other.Names)) return false;
            var serializer = new JavaScriptSerializer();
            for (int i = 0; i < _items.Length; i++)
            {
                if (!_items[i].Keys.OrderBy(k => k).SequenceEqual(other._items[i].Keys.OrderBy(k => k))) return false;
                foreach (string key in _items[i].Keys)
                {
                    object a = _items[i][key], b = other._items[i][key];
                    if (a is ValueType && !(a is bool) && b is ValueType && !(b is bool))
                    { if (Convert.ToDouble(a) != Convert.ToDouble(b)) return false; }
                    else if (serializer.Serialize(a) != serializer.Serialize(b)) return false;
                }
            }
            return true;
        }
    }
    internal interface ICalibrationSettingsSession
    {
        CalibrationModel ReadModel();
        void WriteModel(CalibrationModel model);
        void Pause();
        void Save();
        void Resume();
    }
    internal interface ICalibrationEditorSession : ICalibrationSettingsSession, IDisposable
    {
        string Description { get; }
        CalibrationModel Baseline { get; }
        bool[] RadiusInputs { get; }
        void Arm();
        CalibrationFrame TakeFrame();
        void CancelRead();
    }
    internal static class CalibrationController
    {
        public static void Apply(ICalibrationSettingsSession session, CalibrationModel baseline, CalibrationModel requested)
        {
            if (baseline == null || requested == null) throw new InvalidOperationException("기준 측정값을 먼저 읽으세요.");
            if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("Calculator 설정이 변경되었습니다. 기준 측정값을 다시 읽으세요.");
            session.Pause();
            bool attempted = false, saveAttempted = false;
            try
            {
                if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("편집 중 Calculator 설정이 변경되었습니다.");
                attempted = true;
                session.WriteModel(requested);
                if (!requested.Same(session.ReadModel())) throw new InvalidOperationException("카메라 보정계수 쓰기 확인 실패");
                saveAttempted = true;
                session.Save();
                if (!requested.Same(session.ReadModel())) throw new InvalidOperationException("저장 후 보정계수 확인 실패");
            }
            catch (Exception failure)
            {
                try
                {
                    if (attempted)
                    {
                        session.WriteModel(baseline);
                        if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("기존 보정계수 복원 확인 실패");
                        if (saveAttempted) session.Save();
                    }
                }
                catch (Exception recovery) { throw new InvalidOperationException("보정 적용 및 복원 실패. 검사 흐름을 정지 상태로 유지합니다.\r\n적용: " + failure.Message + "\r\n복원: " + recovery.Message, failure); }
                try { session.Resume(); }
                catch (Exception ex) { throw new InvalidOperationException("기존 계수는 유지/복원했으나 검사 재개 실패: " + ex.Message, failure); }
                throw new InvalidOperationException("보정 적용 실패. 기존 계수를 유지하거나 복원했습니다.\r\n" + failure.Message, failure);
            }
            try { session.Resume(); }
            catch (Exception ex) { throw new InvalidOperationException("보정 저장은 완료했으나 검사 재개 실패: " + ex.Message, ex); }
        }
    }
    internal sealed class CalibrationFrame
    {
        public uint Frame;
        public double[] Pixels;
        public DateTime Time;
    }
    // Bounded join of the four source measurements; never mix execution numbers.
    internal sealed class CalibrationFrameCollector
    {
        private readonly int _count;
        public CalibrationFrameCollector() : this(4) { }
        public CalibrationFrameCollector(int count) { if (count < 1) throw new ArgumentOutOfRangeException("count"); _count = count; }
        private readonly Dictionary<uint, double[]> _pending = new Dictionary<uint, double[]>();
        public CalibrationFrame Add(uint frame, int slot, double value)
        {
            if (slot < 0 || slot >= _count || !CalibrationModel.Positive(value)) return null;
            double[] values;
            if (!_pending.TryGetValue(frame, out values))
            {
                if (_pending.Count >= 64) _pending.Clear();
                values = new double[_count]; _pending.Add(frame, values);
            }
            values[slot] = value;
            if (values.Any(v => v <= 0)) return null;
            _pending.Clear();
            return new CalibrationFrame { Frame = frame, Pixels = (double[])values.Clone(), Time = DateTime.Now };
        }
        public void Clear() { _pending.Clear(); }
    }
}
