using System;

namespace SC6000DualMonitor
{
    // Keep the transaction independent of the SDK so restoration failures can be tested.
    internal interface IReplayDevice
    {
        bool Continuous { get; set; }
        bool Enabled { get; set; }
        int Source { get; set; }
        int SdkSource { get; }
        void Stop();
        uint Run(byte[] image);
    }
    internal sealed class ReplayControl
    {
        private readonly IReplayDevice _device;
        private bool _continuous, _enabled;
        private int? _restoreSource;
        public bool Active { get; private set; }
        public bool Faulted { get; private set; }
        public ReplayControl(IReplayDevice device) { _device = device; }
        public void Enter()
        {
            if (Active) throw new InvalidOperationException("이미 재검사 모드입니다.");
            _continuous = _device.Continuous; _enabled = _device.Enabled;
            Active = true;
            try { _device.Continuous = false; _device.Enabled = false; _device.Stop(); }
            catch { Faulted = true; throw; }
        }
        public uint Execute(byte[] image)
        {
            if (!Active || Faulted) throw new InvalidOperationException("재검사를 시작할 수 없습니다. 실행 상태를 먼저 복원하세요.");
            _restoreSource = _device.Source;
            try
            {
                _device.Source = _device.SdkSource;
                if (_device.Source != _device.SdkSource) throw new InvalidOperationException("SDK 이미지 입력 전환을 확인할 수 없습니다.");
                _device.Enabled = true;
                return _device.Run(image);
            }
            finally
            {
                try { _device.Enabled = false; _device.Stop(); RestoreSource(); }
                catch { Faulted = true; throw; }
            }
        }
        private void RestoreSource()
        {
            if (!_restoreSource.HasValue) return;
            _device.Source = _restoreSource.Value;
            if (_device.Source != _restoreSource.Value) throw new InvalidOperationException("원래 이미지 소스 복원 실패. 생산을 재개하지 마세요.");
            _restoreSource = null;
        }
        public void Exit(Action beforeResume = null)
        {
            if (!Active) return;
            try
            {
                _device.Enabled = false; _device.Stop(); RestoreSource();
                if (beforeResume != null) beforeResume();
                _device.Enabled = _enabled; _device.Continuous = _continuous;
                Active = false; Faulted = false;
            }
            catch
            {
                Faulted = true;
                try { _device.Continuous = false; _device.Enabled = false; _device.Stop(); } catch { }
                throw;
            }
        }
    }
}
