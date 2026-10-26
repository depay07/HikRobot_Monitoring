using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly Panel _live = new Panel { Dock = DockStyle.Fill };
        private readonly Timer _captureTimer = new Timer { Interval = 50 };
        private readonly NgHistoryStore _history;
        private readonly NgFilmStrip _film;
        private FrontendCapture _capture;
        private RemoteInspectionSession _captureSession;
        private string _boundRecipe;
        private volatile bool _recipeLoading, _recipeRefreshPending;
        private DateTime _lastAttachAttempt;
        private readonly object _signalGate = new object();
        private readonly Queue<NgSignal> _signals = new Queue<NgSignal>();
        private int _signalDrops;
        private sealed class NgSignal { public object Session; public NgCaptureEventArgs Result; }
        private readonly List<PendingCapture> _pendingCaptures = new List<PendingCapture>();
        private readonly List<Task> _writes = new List<Task>();
        private int _saved, _missed, _pendingWrites;
        private bool _capturing, _recipeEventsSubscribed;
        private string _inspectionBindingError;
        private sealed class PendingCapture { public NgCaptureEventArgs Result; public string Recipe; public string LastError; }


        public ViewerForm(CameraSettings settings, IntPtr parent)
        {
            _settings = settings;
            _history = new NgHistoryStore(NgHistoryStore.DefaultDirectory(settings.Ip));
            _film = new NgFilmStrip(_history);
            _film.CaptureDiagnostics = () => "카메라: " + _settings.Title + " / IP: " + _settings.Ip + "\r\n레시피: " + (_boundRecipe ?? "확인 안 됨") +
                "\r\n검사 연결 오류: " + (_inspectionBindingError ?? "없음") + "\r\n" +
                (_captureSession == null ? "검사 결과 연결 없음" : _captureSession.CaptureDiagnostics) + "\r\n" +
                (_capture == null ? "영상 캡처 연결 없음" : _capture.Describe()) +
                "\r\n저장: " + _saved + " / 누락: " + _missed + " / 저장 대기: " + _pendingWrites;
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
            _solutions.SolutionChanging += delegate { ResetCaptureBinding(); _solutions.Inspection.Unbind(); };
            _solutions.SolutionChanged += delegate { BindInspection(); _recipeLoading = _recipeRefreshPending = false; };
            var images = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); images.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            images.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _live.Margin = _film.Margin = Padding.Empty;
            _live.Controls.Add(_status); images.Controls.Add(_live, 0, 0); images.Controls.Add(_film, 1, 0);
            _solutions.Content.Controls.Add(images);
            _live.SizeChanged += delegate { QueueFrontendResize(); };
            _captureTimer.Tick += delegate { CaptureTick(); };
            Controls.Add(_solutions);
            _layoutTimer.Tick += delegate { FitParent(); };
            Shown += delegate { FitParent(); _layoutTimer.Start(); BeginInvoke(new Action(Connect)); };
            ClientSizeChanged += delegate { QueueFrontendResize(); };
        }

        private void ReloadFrontend()
        {
            DetachCapture();
            _frontendLoaded = false;
            _frontend.LoadFrontendSource();
            _frontendLoaded = true;
            QueueFrontendResize();
            BeginInvoke(new Action(AttachCapture));
        }

        private void BindInspection()
        {
            _inspectionBindingError = null;
            ResetCaptureBinding();
            RemoteInspectionSession inspection = null;
            string path = null;
            try { path = _remoteSolutions.CurrentPath; }
            catch (Exception ex) { Trace.WriteLine(ex); }
            try
            {
                inspection = new RemoteInspectionSession(_settings);
                _solutions.Inspection.Bind(inspection, path);
                _boundRecipe = path; _captureSession = inspection;
                inspection.NgCaptureRequested += OnNgCapture;
            }
            catch (Exception ex)
            {
                if (inspection != null) inspection.Dispose();
                _inspectionBindingError = ex.Message;
                _film.SetStatus("NG 저장 연결 실패: " + ex.Message);
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
                _live.Controls.Add(_frontend);
                VmSolution.GetSolutionInstanceToDevice(new DeviceModeInfo
                {
                    emDeviceMode = DeviceModeType.NET,
                    strDevIP = _settings.Ip,
                    nPort = VmRemotePort,
                    strPassword = _settings.Password
                });
                _connected = true;
                VmSolution.OnSolutionLoadBeginCallBack += OnRecipeLoading;
                VmSolution.OnSolutionLoadEndCallBack += OnRecipeLoaded;
                _recipeEventsSubscribed = true;
                // Attach to the recipe already running on the camera. Never load config.ini's old startup path.
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
                _recipeLoading = _recipeRefreshPending = false;
                _captureTimer.Start();
                BeginInvoke(new Action(AttachCapture));
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

        private void OnRecipeLoading(object sender, EventArgs args) { _recipeLoading = true; }
        private void OnRecipeLoaded(object sender, EventArgs args) { _recipeLoading = false; _recipeRefreshPending = true; }
        private void CaptureTick()
        {
            if (_recipeLoading) return;
            if (_recipeRefreshPending)
            {
                if (_solutions.IsBusy || _solutions.Inspection.IsEditing) return;
                _recipeRefreshPending = false;
                try
                {
                    ResetCaptureBinding(); _solutions.Inspection.Unbind();
                    ReloadFrontend(); _solutions.RefreshCurrent(); BindInspection();
                }
                catch (Exception ex) { _film.SetStatus("레시피 화면 동기화 실패: " + ex.Message); }
            }
            if ((_capture == null || !_capture.HasViews) && DateTime.UtcNow - _lastAttachAttempt > TimeSpan.FromSeconds(2)) AttachCapture();
            TryPendingCaptures();
        }
        private void AttachCapture()
        {
            if (IsDisposed || !_frontendLoaded || _frontend == null) return;
            _lastAttachAttempt = DateTime.UtcNow;
            DetachCapture();
            try { _capture = new FrontendCapture(_frontend); _capture.Rendered += TryPendingCaptures;
                _film.SetStatus(_inspectionBindingError == null ? _capture.Availability : "NG 저장 연결 실패: " + _inspectionBindingError); }
            catch (Exception ex) { _film.SetStatus("NG 캡처 연결 실패: " + ex.Message); }
        }
        private void DetachCapture()
        {
            if (_capture != null) { try { _capture.Dispose(); } catch (Exception ex) { Trace.WriteLine(ex); } _capture = null; }
        }
        private void ResetCaptureBinding()
        {
            if (_captureSession != null) _captureSession.NgCaptureRequested -= OnNgCapture;
            _captureSession = null;
            if (_pendingCaptures.Count > 0) { _missed += _pendingCaptures.Count; _film.SetStatus("레시피 전환으로 미완료 NG 캡처 " + _pendingCaptures.Count + "건 취소 / 누락 " + _missed); }
            _pendingCaptures.Clear(); _boundRecipe = null;
            lock (_signalGate) { _signals.Clear(); _signalDrops = 0; }
        }
        private void PostUI(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (!IsDisposed && !Disposing) action(); })); }
            catch (InvalidOperationException) { }
        }
        private void OnNgCapture(object sender, NgCaptureEventArgs result)
        {
            // SDK callbacks must not queue unbounded UI work during a burst of NG results.
            lock (_signalGate)
            {
                if (_signals.Count >= 16) { _signalDrops++; return; }
                _signals.Enqueue(new NgSignal { Session = sender, Result = result });
            }
        }
        private void DrainNgSignals()
        {
            NgSignal[] signals; int dropped;
            lock (_signalGate) { signals = _signals.ToArray(); _signals.Clear(); dropped = _signalDrops; _signalDrops = 0; }
            if (dropped > 0) { _missed += dropped; _film.SetStatus("NG 수신 대기 초과 / 누락 " + _missed); }
            foreach (var signal in signals)
            {
                if (!ReferenceEquals(signal.Session, _captureSession)) continue;
                if (_pendingCaptures.Count >= 16) { _missed++; _film.SetStatus("NG 캡처 대기 초과 / 누락 " + _missed); continue; }
                _pendingCaptures.Add(new PendingCapture { Result = signal.Result, Recipe = _boundRecipe });
            }
        }
        private void TryPendingCaptures()
        {
            if (_capturing || _recipeLoading || _recipeRefreshPending || IsDisposed) return;
            _capturing = true;
            try
            {
                DrainNgSignals();
                foreach (var pending in _pendingCaptures.ToArray())
                {
                    byte[] png = null; string message = "NG 캡처 연결 대기"; bool success = false;
                    try { success = _capture != null && _capture.TryCapture(pending.Result.Frame, pending.Result.ProcedureGuid, out png, out message); }
                    catch (Exception ex) { message = "캡처 실패: " + ex.Message; }
                    pending.LastError = message;
                    if (!success && DateTime.UtcNow - pending.Result.TimeUtc < TimeSpan.FromSeconds(3)) continue;
                    _pendingCaptures.Remove(pending);
                    if (!success) { _missed++; _film.SetStatus("캡처 누락 " + _missed + " / " + message); continue; }
                    if (_pendingWrites >= 8) { _missed++; _film.SetStatus("NG 저장 대기 초과 / 누락 " + _missed); continue; }
                    var record = new NgRecord { Camera = _settings.Title + " (" + _settings.Ip + ")", Recipe = pending.Recipe,
                        TimeUtc = pending.Result.TimeUtc, Frame = pending.Result.Frame, ViewName = message };
                    _pendingWrites++;
                    _writes.RemoveAll(t => t.IsCompleted);
                    _writes.Add(Task.Run(delegate
                    {
                        try
                        {
                            _history.Save(png, record);
                            PostUI(delegate { _pendingWrites--; _saved++; _film.AddRecord(record); _film.SetStatus("저장 " + _saved + " / 누락 " + _missed + " (이번 실행)"); });
                        }
                        catch (Exception ex) { PostUI(delegate { _pendingWrites--; _missed++; _film.SetStatus("NG 저장 실패: " + ex.Message + " / 누락 " + _missed); }); }
                    }));
                }
            }
            finally { _capturing = false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_recipeEventsSubscribed)
            {
                VmSolution.OnSolutionLoadBeginCallBack -= OnRecipeLoading;
                VmSolution.OnSolutionLoadEndCallBack -= OnRecipeLoaded;
            }
            _captureTimer.Stop(); _captureTimer.Dispose();
            ResetCaptureBinding(); DetachCapture();
            // Disk tasks never wait for the UI thread. Finish already captured PNG writes before the viewer exits.
            try { Task.WaitAll(_writes.ToArray()); } catch (Exception ex) { Trace.WriteLine(ex); }
            _layoutTimer.Stop();
            _layoutTimer.Dispose();
            _solutions.Inspection.Unbind();
            if (_frontend != null) { _live.Controls.Remove(_frontend); _frontend.Dispose(); }
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
