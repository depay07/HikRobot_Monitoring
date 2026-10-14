using System;
using System.Collections.Generic;
using IMVSBlobFindModuCs;
using VM.Core;

namespace SC6000DualMonitor
{
    internal sealed class BlobModuleChoice
    {
        public IMVSBlobFindModuTool Module { get; private set; }
        public BlobModuleChoice(IMVSBlobFindModuTool module) { Module = module; }
        public override string ToString() { return Module.Name + " (ID " + Module.ID + ")"; }
    }
    internal sealed class RemoteBlobSession : IBlobSettingsSession
    {
        private readonly RemoteInspectionSession _owner;
        private readonly IMVSBlobFindModuTool _module;
        private readonly string _target;
        public string TargetDescription { get { return _target; } }
        public RemoteBlobSession(RemoteInspectionSession owner, BlobModuleChoice choice)
        {
            _owner = owner; _module = choice.Module;
            _target = owner.CameraProcedureDescription + " / " + choice;
        }
        public BlobSettings Read()
        {
            var p = _module.ModuParams;
            return new BlobSettings { Low = p.LowThreshold, High = p.HightThreshold,
                MinArea = p.MinArea, MaxArea = p.MaxArea, AreaEnabled = p.SelectByArea, ThresholdMode = (int)p.ThresholdType };
        }
        public void Write(BlobSettings values)
        {
            values.Validate();
            var p = _module.ModuParams;
            var current = Read();
            if (current.ThresholdMode != values.ThresholdMode)
                throw new InvalidOperationException("BLOB 임계값 모드가 변경되었습니다. 설정을 다시 읽으세요.");
            if (!values.DoubleThreshold && (values.Low != current.Low || values.High != current.High))
                throw new InvalidOperationException("Double Threshold 모드가 아니므로 임계값을 변경하지 않았습니다.");
            if (!values.AreaEnabled && current.AreaEnabled) p.SelectByArea = false;
            if (values.DoubleThreshold)
            {
                if (values.Low > current.High) { p.HightThreshold = values.High; p.LowThreshold = values.Low; }
                else { p.LowThreshold = values.Low; p.HightThreshold = values.High; }
            }
            if (values.MinArea > current.MaxArea) { p.MaxArea = values.MaxArea; p.MinArea = values.MinArea; }
            else { p.MinArea = values.MinArea; p.MaxArea = values.MaxArea; }
            p.SelectByArea = values.AreaEnabled;
        }
        public void Pause() { _owner.Pause(); }
        public void Save() { _owner.Save(); }
        public void Resume() { _owner.Resume(); }
    }
}
