using System;

namespace SC6000DualMonitor
{
    internal sealed class BlobSettings
    {
        public int Low, High, MinArea, MaxArea, ThresholdMode;
        public bool AreaEnabled;
        public bool DoubleThreshold { get { return ThresholdMode == 3; } }
        public BlobSettings Copy() { return (BlobSettings)MemberwiseClone(); }
        public void Validate()
        {
            if (Low < 0 || High < 0 || Low > 255 || High > 255 || (DoubleThreshold && Low > High))
                throw new InvalidOperationException("임계값은 0~255이며 Low는 High 이하여야 합니다.");
            if (MinArea < 1 || MaxArea > 999999999 || MinArea > MaxArea)
                throw new InvalidOperationException("면적은 1~999999999이며 최소 면적은 최대 면적 이하여야 합니다.");
        }
        public bool Same(BlobSettings other)
        {
            return other != null && Low == other.Low && High == other.High && MinArea == other.MinArea &&
                MaxArea == other.MaxArea && AreaEnabled == other.AreaEnabled && ThresholdMode == other.ThresholdMode;
        }
    }
    internal interface IBlobSettingsSession
    {
        string TargetDescription { get; }
        BlobSettings Read();
        void Write(BlobSettings values);
        void Pause();
        void Save();
        void Resume();
    }
    internal static class BlobSettingsController
    {
        public static void Apply(IBlobSettingsSession session, BlobSettings baseline, BlobSettings requested)
        {
            if (baseline == null || requested == null) throw new InvalidOperationException("카메라 값을 먼저 읽으세요.");
            requested.Validate();
            if (baseline.ThresholdMode != requested.ThresholdMode ||
                (!baseline.DoubleThreshold && (baseline.Low != requested.Low || baseline.High != requested.High)))
                throw new InvalidOperationException("Low/High 편집은 Double Threshold 모드에서만 가능합니다. 모드는 VisionMaster에서 설정하세요.");
            VerifyUnchanged(baseline, session.Read());
            session.Pause();
            BlobSettings original = null;
            bool attempted = false, saveAttempted = false;
            try
            {
                original = session.Read();
                VerifyUnchanged(baseline, original);
                attempted = true;
                session.Write(requested);
                Verify(requested, session.Read());
                saveAttempted = true;
                session.Save();
                Verify(requested, session.Read());
            }
            catch (Exception failure)
            {
                try
                {
                    if (attempted)
                    {
                        session.Write(original);
                        Verify(original, session.Read());
                        if (saveAttempted) session.Save();
                    }
                }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException("BLOB 적용 및 복원 실패. 검사 흐름을 정지 상태로 유지합니다.\r\n적용: " + failure.Message + "\r\n복원: " + recovery.Message, failure);
                }
                try { session.Resume(); }
                catch (Exception resume) { throw new InvalidOperationException("BLOB 기존 값은 유지/복원했으나 검사 재개에 실패했습니다: " + resume.Message, failure); }
                throw new InvalidOperationException("BLOB 적용 실패. 기존 값을 유지하거나 복원했습니다.\r\n" + failure.Message, failure);
            }
            try { session.Resume(); }
            catch (Exception ex) { throw new InvalidOperationException("BLOB 저장은 완료했으나 검사 재개에 실패했습니다: " + ex.Message, ex); }
        }
        private static void VerifyUnchanged(BlobSettings expected, BlobSettings actual)
        {
            if (!expected.Same(actual)) throw new InvalidOperationException("편집 중 BLOB 값 또는 모드가 변경되었습니다. 다시 읽은 후 적용하세요.");
        }
        private static void Verify(BlobSettings expected, BlobSettings actual)
        {
            if (!expected.Same(actual)) throw new InvalidOperationException("카메라의 BLOB 값이 요청한 값과 다릅니다.");
        }
    }
}
