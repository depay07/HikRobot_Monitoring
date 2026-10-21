using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Winform.Release;

namespace SC6000DualMonitor
{
    internal sealed class ViewerForm : Form
    {
        private const ushort VmRemotePort = 5556;
        private readonly CameraSettings _settings;
        private readonly IntPtr _parent;
        private readonly Timer _layoutTimer = new Timer { Interval = 200 };
        private VmFrontendControl _frontend;
        private readonly SolutionPane _solutions;
        private readonly RemoteSolutionSession _remoteSolutions;
        private readonly Label _status;
        private bool _frontendLoaded;
        private bool _connected;
        private bool _resizeQueued;

        public ViewerForm(CameraSettings settings, IntPtr parent)
        {
            _settings = settings;
            _parent = parent;
            AutoScaleMode = AutoScaleMode.None;
            Text = settings.Title;
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(45, 52, 56);
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White, Font = new Font("Segoe UI", 12),
                Text = settings.Title + "\r\n" + settings.Ip + "\r\n연결 중..." };
            _remoteSolutions = new RemoteSolutionSession(settings.SolutionDirectory, settings.SolutionPassword);
            _solutions = new SolutionPane(_remoteSolutions, ReloadFrontend, settings);
            _solutions.SolutionChanging += _solutions.Inspection.Unbind;
            _solutions.SolutionChanged += BindInspection;
            _solutions.Content.Controls.Add(_status);
            _solutions.Content.SizeChanged += delegate { QueueFrontendResize(); };
            Controls.Add(_solutions);
            _layoutTimer.Tick += delegate { FitParent(); };
            Shown += delegate { FitParent(); _layoutTimer.Start(); BeginInvoke(new Action(Connect)); };
            ClientSizeChanged += delegate { QueueFrontendResize(); };
        }

        private void ReloadFrontend()
        {
            _frontendLoaded = false;
            _frontend.LoadFrontendSource();
            _frontendLoaded = true;
            QueueFrontendResize();
        }

        private void BindInspection()
        {
            RemoteInspectionSession inspection = null;
            string path = null;
            try { path = _remoteSolutions.VerifiedCurrentPath; }
            catch (Exception ex) { Trace.WriteLine(ex); }
            try
            {
                inspection = new RemoteInspectionSession(_settings);
                _solutions.Inspection.Bind(inspection, path);
            }
            catch (Exception ex)
            {
                if (inspection != null) inspection.Dispose();
                _solutions.Inspection.Failed(ex);
                _solutions.Inspection.SetModelPath(path);
                Trace.WriteLine(ex);
            }
        }

        private void QueueFrontendResize()
        {
            if (!_frontendLoaded || _resizeQueued || !IsHandleCreated || IsDisposed || Disposing) return;
            _resizeQueued = true;
            BeginInvoke(new Action(delegate
            {
                _resizeQueued = false;
                if (!_frontendLoaded || _frontend == null || _frontend.IsDisposed || IsDisposed) return;
                try
                {
                    // Dock/layout must finish before the SDK measures its WPF frontend.
                    PerformLayout();
                    _frontend.PerformLayout();
                    _frontend.AutoChangeSize();
                }
                catch (Exception ex) { Trace.WriteLine(ex); }
            }));
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (_parent != IntPtr.Zero)
                {
                    cp.Parent = _parent;
                    cp.Style = (cp.Style & ~unchecked((int)0x80000000)) | 0x40000000; // WS_CHILD, no WS_POPUP
                }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetParent(Handle, _parent);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        private void FitParent()
        {
            if (!IsWindow(_parent)) { Close(); return; }
            EmbeddedWindow.Fit(Handle, _parent, false);
        }
        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(_settings.Ip))
            {
                _status.Text = _settings.Title + "\r\n카메라 IP 미설정: config.ini를 확인하세요.";
                return;
            }
            string stage = "Remote 연결";
            try
            {
                // Set REMOTE before constructing any VM controls, as in the SDK sample.
                VmSolution.SetControlMode(ControlModeType.REMOTE);
                _frontend = new VmFrontendControl { Dock = DockStyle.Fill, Visible = false };
                _solutions.Content.Controls.Add(_frontend);
                VmSolution.GetSolutionInstanceToDevice(new DeviceModeInfo
                {
                    emDeviceMode = DeviceModeType.NET,
                    strDevIP = _settings.Ip,
                    nPort = VmRemotePort,
                    strPassword = _settings.Password
                });
                _connected = true;
                if (!string.IsNullOrWhiteSpace(_settings.SolutionPath))
                {
                    stage = "시작 Solution 로딩: " + _settings.SolutionPath;
                    // The path belongs to the camera, not to the monitoring PC.
                    VmSolution.Load(_settings.SolutionPath, _settings.SolutionPassword);
                }
                stage = "Operation Interface 로딩";
                FitParent();
                _frontend.LoadFrontendSource();
                _frontendLoaded = true;
                _frontend.Visible = true;
                _frontend.BringToFront();
                FitParent();
                QueueFrontendResize();
                _status.Visible = false;
                _solutions.Connected();
                BindInspection();
            }
            catch (Exception ex)
            {
                VmException vm = ex as VmException;
                try { if (vm == null) vm = VmSolution.GetVmException(ex); } catch { }
                string code = vm == null ? null : vm.errorCode.ToString("X8");
                string message = ConnectionErrors.Describe(code, ex.Message);
                string details = _settings.Title + "\r\nIP: " + _settings.Ip + ":" + VmRemotePort +
                    "\r\n단계: " + stage + "\r\n\r\n" + message +
                    (code == null ? "" : "\r\n\r\n오류 코드: 0x" + code);
                _frontendLoaded = false;
                if (_frontend != null) _frontend.Visible = false;
                _status.Text = details;
                _status.Visible = true;
                _status.BringToFront();
                Trace.WriteLine(ex);
                // Keep the failure inside this camera pane; other cameras remain usable.
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _layoutTimer.Stop();
            _layoutTimer.Dispose();
            _solutions.Inspection.Unbind();
            if (_frontend != null) { _solutions.Content.Controls.Remove(_frontend); _frontend.Dispose(); }
            if (_connected)
                try { VmSolution.Instance?.Dispose(); }
                catch (Exception ex) { Trace.WriteLine(ex); }
            base.OnFormClosed(e);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
    }

    internal static class ConnectionErrors
    {
        public static string Describe(string code, string fallback)
        {
            switch (code)
            {
                case "E000070C":
                    return "VisionMaster가 카메라에 접속 중이거나 원격 측 실행 충돌이 발생했습니다.\r\n" +
                        "카메라에 접속한 VisionMaster를 종료한 후 이 프로그램을 다시 실행해 주세요.";
                case "E0000111":
                    return "카메라에 연결할 수 없습니다. 카메라가 감지되지 않거나 통신에 응답하지 않습니다.\r\n" +
                        "카메라 전원, LAN 케이블, IP 주소 및 네트워크 연결 상태를 확인한 후 다시 실행해 주세요.";
                default:
                    return "연결 또는 화면 로딩 중 오류가 발생했습니다.\r\n" + fallback +
                        "\r\n카메라 연결 상태와 설정을 확인해 주세요.";
            }
        }
    }
}
