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
        public static readonly string[] Names = { "top_size", "middle_size", "bottom_size", "circle_size" };
        public static readonly string[] Captions = { "상단 치수", "중간 치수", "하단 치수", "원 지름" };
        private readonly Dictionary<string, object>[] _items;
        public readonly string[] Inputs = new string[4];
        public readonly string[] Prefixes = new string[4];
        public readonly double[] Factors = new double[4];
        private static readonly Regex Formula = new Regex(@"^(?<prefix><float:<base64 (?<input>[A-Za-z0-9+/=]+) />>\[0\]\s*\*\s*)(?<factor>[0-9.eE+\-]+)$");
        public CalibrationModel(string[] json)
        {
            if (json == null || json.Length != 4) throw new InvalidOperationException("치수 보정은 지정된 4개 계산 항목이 있는 레시피만 지원합니다.");
            var serializer = new JavaScriptSerializer();
            _items = new Dictionary<string, object>[4];
            for (int i = 0; i < 4; i++)
            {
                var item = serializer.Deserialize<Dictionary<string, object>>(json[i]);
                if (Convert.ToString(item["Name"]) != Names[i] || Convert.ToString(item["Type"]) != "float")
                    throw new InvalidOperationException("Calculator 항목 이름/형식 불일치: " + Names[i]);
                var match = Formula.Match(Convert.ToString(item["Expression"]));
                double factor;
                if (!match.Success || !double.TryParse(match.Groups["factor"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out factor) || !Positive(factor))
                    throw new InvalidOperationException(Captions[i] + ": '픽셀값 × 보정계수' 형태가 아닙니다. 기존 계산식은 변경하지 않았습니다.");
                _items[i] = item; Factors[i] = factor; Inputs[i] = match.Groups["input"].Value; Prefixes[i] = match.Groups["prefix"].Value;
            }
            if (!_items.Select(x => Convert.ToInt32(x["Index"])).OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2, 3 }))
                throw new InvalidOperationException("Calculator 항목 순서가 중복되어 있습니다.");
        }
        public static bool Positive(double value) { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }
        public CalibrationModel Calculate(double[] pixels, double[] actual)
        {
            if (pixels == null || actual == null || pixels.Length != 4 || actual.Length != 4) throw new InvalidOperationException("4개 항목의 측정값과 실제 치수가 필요합니다.");
            var serializer = new JavaScriptSerializer();
            var json = new string[4];
            for (int i = 0; i < 4; i++)
            {
                if (!Positive(pixels[i]) || !Positive(actual[i])) throw new InvalidOperationException(Captions[i] + ": 픽셀값과 실제 치수는 0보다 큰 유효한 값이어야 합니다.");
                double factor = actual[i] / pixels[i]; // Circle input is radius; actual entry is diameter. No extra factor of two.
                if (!Positive(factor) || factor > float.MaxValue || factor < float.Epsilon) throw new InvalidOperationException("보정계수가 카메라의 계산 범위를 벗어납니다.");
                var item = new Dictionary<string, object>(_items[i]);
                item["Expression"] = Prefixes[i] + factor.ToString("R", CultureInfo.InvariantCulture);
                json[i] = serializer.Serialize(item);
            }
            return new CalibrationModel(json);
        }
        public string[] JsonInOrder()
        {
            var serializer = new JavaScriptSerializer();
            return _items.OrderBy(x => Convert.ToInt32(x["Index"])).Select(serializer.Serialize).ToArray();
        }
        public bool Same(CalibrationModel other)
        {
            if (other == null) return false;
            var serializer = new JavaScriptSerializer();
            for (int i = 0; i < 4; i++)
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
        private readonly Dictionary<uint, double[]> _pending = new Dictionary<uint, double[]>();
        public CalibrationFrame Add(uint frame, int slot, double value)
        {
            if (slot < 0 || slot >= 4 || !CalibrationModel.Positive(value)) return null;
            double[] values;
            if (!_pending.TryGetValue(frame, out values))
            {
                if (_pending.Count >= 64) _pending.Clear();
                values = new double[4]; _pending.Add(frame, values);
            }
            values[slot] = value;
            if (values.Any(v => v <= 0)) return null;
            _pending.Clear();
            return new CalibrationFrame { Frame = frame, Pixels = (double[])values.Clone(), Time = DateTime.Now };
        }
        public void Clear() { _pending.Clear(); }
    }
}
