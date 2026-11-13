using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SC6000DualMonitor
{
    // Only the four selected scalar global variables are editable. No Calculator expressions.
    internal sealed class CalibrationModel
    {
        public readonly string[] Names, Variables;
        public readonly double[] Factors;
        public int Count { get { return Names.Length; } }
        public CalibrationModel(string[] names, string[] variables, double[] factors)
        {
            if (names == null || variables == null || factors == null || names.Length != 4 || variables.Length != 4 || factors.Length != 4 ||
                names.Any(string.IsNullOrWhiteSpace) || variables.Any(string.IsNullOrWhiteSpace) || variables.Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidOperationException("서로 다른 보정계수 변수 4개와 표시 이름을 지정하세요.");
            Names = (string[])names.Clone(); Variables = (string[])variables.Clone();
            Factors = factors.Select(Canonical).ToArray();
        }
        private static double Canonical(double value)
        {
            float stored = (float)value;
            if (!Positive(stored)) throw new InvalidOperationException("보정계수는 카메라 float 범위의 양수여야 합니다.");
            return stored;
        }
        public static bool Positive(double value) { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }
        public CalibrationModel Calculate(double[] pixels, double[] actual)
        {
            if (pixels == null || actual == null || pixels.Length != Count || actual.Length != Count)
                throw new InvalidOperationException("네 항목의 측정값과 실제 치수를 입력하세요.");
            var factors = new double[Count];
            for (int i = 0; i < Count; i++)
            {
                if (!Positive(pixels[i]) || !Positive(actual[i])) throw new InvalidOperationException(Names[i] + ": 픽셀값과 실제 치수는 양수여야 합니다.");
                factors[i] = Canonical(actual[i] / pixels[i]);
            }
            return new CalibrationModel(Names, Variables, factors);
        }
        public bool SameTargets(CalibrationModel other)
        { return other != null && Names.SequenceEqual(other.Names) && Variables.SequenceEqual(other.Variables); }
        public bool Same(CalibrationModel other)
        { return SameTargets(other) && Factors.SequenceEqual(other.Factors); }
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
        public static async Task<string> ApplyAndRefreshAsync(ICalibrationSettingsSession session,
            CalibrationModel baseline, CalibrationModel requested, Func<Task> refreshGraphics)
        {
            Apply(session, baseline, requested);
            // Display failure must not roll back successfully saved calibration values.
            if (refreshGraphics != null)
                try { await refreshGraphics(); }
                catch (Exception ex) { return "보정계수 저장과 검사 재개는 완료했으나 화면 연결 갱신에 실패했습니다.\r\n" + ex.GetBaseException().Message; }
            return null;
        }
        public static void Apply(ICalibrationSettingsSession session, CalibrationModel baseline, CalibrationModel requested)
        {
            if (baseline == null || requested == null || !baseline.SameTargets(requested)) throw new InvalidOperationException("기준 측정값을 먼저 읽으세요.");
            if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("보정계수가 변경되었습니다. 기준 측정값을 다시 읽으세요.");
            session.Pause();
            bool attempted = false, saveAttempted = false;
            try
            {
                if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("편집 중 보정계수가 변경되었습니다.");
                attempted = true;
                session.WriteModel(requested);
                if (!requested.Same(session.ReadModel())) throw new InvalidOperationException("카메라 보정계수 쓰기 확인 실패");
                saveAttempted = true;
                session.Save();
                if (!requested.Same(session.ReadModel())) throw new InvalidOperationException("저장 후 보정계수 확인 실패");
            }
            catch (Exception failure)
            {
                bool runtimeRestored = false;
                try
                {
                    if (attempted)
                    {
                        session.WriteModel(baseline);
                        if (!baseline.Same(session.ReadModel())) throw new InvalidOperationException("기존 보정계수 복원 확인 실패");
                        runtimeRestored = true;
                        if (saveAttempted) session.Save();
                    }
                }
                catch (Exception recovery)
                {
                    string state = runtimeRestored
                        ? "이전 실행 보정계수는 복원했으나 레시피 저장 확인에 실패했습니다."
                        : "보정 적용 및 실행 계수 복원 확인에 실패했습니다.";
                    throw new InvalidOperationException(state + " 검사 흐름을 정지 상태로 유지합니다.\r\n적용: " + failure.Message + "\r\n복원: " + recovery.Message, failure);
                }
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
