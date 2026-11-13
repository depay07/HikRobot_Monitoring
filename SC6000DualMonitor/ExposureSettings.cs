using System;
using System.Globalization;

namespace SC6000DualMonitor
{
    internal sealed class CameraParameterValue
    {
        public double Value;
        public double? Min, Max;
        public static CameraParameterValue Parse(string raw)
        {
            var parts = (raw ?? "").Split('$');
            // VM 4.4.50 GlobalSmartCameraParamItem accepts value$min$max with
            // additional fields. Only the first three fields are numeric bounds;
            // requiring exactly three rejects valid camera replies.
            if (parts.Length == 2) throw new InvalidOperationException("카메라 값/범위 응답에 최대값이 없습니다.");
            var result = new CameraParameterValue { Value = Number(parts[0]) };
            if (parts.Length >= 3)
            {
                result.Min = Number(parts[1]); result.Max = Number(parts[2]);
                if (result.Min > result.Max || result.Value < result.Min || result.Value > result.Max)
                    throw new InvalidOperationException("카메라가 반환한 값과 허용 범위가 일치하지 않습니다.");
            }
            return result;
        }
        internal static double Number(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException("카메라가 유효한 숫자를 반환하지 않았습니다: " + text);
            return value;
        }
        public void Validate(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 999999999 ||
                (Min.HasValue && value < Min.Value) || (Max.HasValue && value > Max.Value))
                throw new InvalidOperationException(label + ": 카메라 허용 범위 안의 값을 입력하세요.");
        }
        public string RangeText { get { return Min.HasValue ? Min.Value.ToString("G9", CultureInfo.InvariantCulture) + " ~ " + Max.Value.ToString("G9", CultureInfo.InvariantCulture) : "카메라에서 범위를 제공하지 않음"; } }
    }

    internal sealed class ExposureSettings
    {
        public CameraParameterValue Exposure, Gain;
        public int AutoExposure, AutoGain;
        public bool Manual { get { return AutoExposure == 0 && AutoGain == 0; } }
        public void Validate(double exposure, double gain)
        {
            if (!Manual) throw new InvalidOperationException("VisionMaster에서 노출/게인 자동 모드를 OFF로 설정한 뒤 다시 읽으세요.");
            Exposure.Validate(exposure, "Exposure"); Gain.Validate(gain, "Gain");
            if (exposure <= 0) throw new InvalidOperationException("노출 시간은 0보다 커야 합니다.");
        }
        internal static bool Near(double a, double b) { return Math.Abs(a - b) <= Math.Max(0.000001, Math.Max(Math.Abs(a), Math.Abs(b)) * 0.0000001); }
        public bool Same(ExposureSettings other)
        { return other != null && AutoExposure == other.AutoExposure && AutoGain == other.AutoGain && Near(Exposure.Value, other.Exposure.Value) && Near(Gain.Value, other.Gain.Value); }
    }
    internal interface IExposureSession
    {
        string Description { get; }
        ExposureSettings Read();
        void Write(double exposure, double gain);
        void Pause();
        void Save();
        void Resume();
    }
    internal static class ExposureController
    {
        public static ExposureSettings Apply(IExposureSession session, ExposureSettings baseline, double exposure, double gain)
        {
            if (baseline == null) throw new InvalidOperationException("카메라 값을 먼저 읽으세요.");
            baseline.Validate(exposure, gain);
            Check(baseline, session.Read(), "편집 중 카메라 값이 변경되었습니다. 다시 읽으세요.");
            session.Pause();
            bool attempted = false, saving = false;
            ExposureSettings original = null, accepted = null;
            try
            {
                original = session.Read();
                Check(baseline, original, "편집 중 카메라 값이 변경되었습니다. 다시 읽으세요.");
                original.Validate(exposure, gain);
                attempted = true;
                session.Write(exposure, gain);
                accepted = session.Read();
                accepted.Validate(accepted.Exposure.Value, accepted.Gain.Value);
                // Smart cameras may quantize/clamp values. Persist and display the read-back values.
                saving = true; session.Save();
                Check(accepted, session.Read(), "저장 후 카메라 값이 달라졌습니다.");
            }
            catch (Exception failure)
            {
                try
                {
                    if (attempted)
                    {
                        session.Write(original.Exposure.Value, original.Gain.Value);
                        Check(original, session.Read(), "기존 노출/게인 값을 복원하지 못했습니다.");
                        if (saving) { session.Save(); Check(original, session.Read(), "복원 저장 후 값이 다릅니다."); }
                    }
                }
                catch (Exception recovery)
                { throw new InvalidOperationException("노출/게인 적용 및 복원 실패. 검사 흐름을 정지 상태로 유지합니다.\r\n적용: " + failure.Message + "\r\n복원: " + recovery.Message, failure); }
                try { session.Resume(); }
                catch (Exception resume) { throw new InvalidOperationException("기존 값은 유지/복원했으나 검사 재개에 실패했습니다: " + resume.Message, failure); }
                throw new InvalidOperationException("적용하지 못했습니다. 기존 값을 유지/복원했습니다.\r\n" + failure.Message, failure);
            }
            try { session.Resume(); }
            catch (Exception ex) { throw new InvalidOperationException("저장은 완료했으나 검사 재개에 실패했습니다: " + ex.Message, ex); }
            return accepted;
        }
        private static void Check(ExposureSettings expected, ExposureSettings actual, string message)
        { if (!expected.Same(actual)) throw new InvalidOperationException(message); }
    }
}
