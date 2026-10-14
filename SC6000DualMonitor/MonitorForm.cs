using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class MonitorForm : Form
    {
        private readonly List<bool> _configured = new List<bool>();
        private readonly List<Process> _viewers = new List<Process>();
        private readonly List<Panel> _panels = new List<Panel>();
        private readonly List<Label> _statuses = new List<Label>();
        private readonly Timer _watch = new Timer { Interval = 200 };
        private readonly string _exePath;
        private readonly string _baseDir;
        private Image _logo;

        public MonitorForm(IniConfig config, string exePath, string baseDir)
        {
            _exePath = exePath;
            _baseDir = baseDir;
            AutoScaleMode = AutoScaleMode.None;
            Text = "WILLB | SC6000 Operation Interface Monitor";
            Icon = Icon.ExtractAssociatedIcon(exePath);
            StartPosition = FormStartPosition.Manual;
            var screens = Screen.AllScreens;
            Bounds = screens[Math.Min(config.MonitorIndex, screens.Length - 1)].WorkingArea;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(640, 400);
            BackColor = Color.White;

            var policy = new CameraLayout(config.CameraCount);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 90));
            LoadLogo();
            var header = new MonitorHeader(_logo, config.InspectionText);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = policy.Columns, RowCount = policy.Rows,
                Margin = Padding.Empty, Padding = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                BackColor = Color.FromArgb(45, 52, 56) };
            for (int row = 0; row < policy.Rows; row++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / policy.Rows));
            for (int col = 0; col < policy.Columns; col++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / policy.Columns));
            for (int i = 0; i < config.CameraCount; i++)
            {
                CameraSettings camera = config.GetCamera(i + 1);
                bool configured = !string.IsNullOrWhiteSpace(camera.Ip);
                _configured.Add(configured);
                var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(1), BackColor = Color.FromArgb(45, 52, 56) };
                var status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White,
                    Text = configured ? camera.Title + "\r\n" + camera.Ip + "\r\n연결 준비 중..." :
                        camera.Title + "\r\n카메라 IP 미설정\r\n실행 파일 옆 config.ini의 [CAMERA" + (i + 1) + "]에 IP를 입력한 후 다시 실행하세요.", Font = new Font("Segoe UI", 12) };
                if (configured)
                    panel.Controls.Add(status);
                else
                {
                    // Render the same controls without starting a viewer or contacting the SDK.
                    var solutions = new SolutionPane(
                        new RemoteSolutionSession(camera.SolutionDirectory, camera.SolutionPassword), delegate { }, camera);
                    solutions.Content.Controls.Add(status);
                    panel.Controls.Add(solutions);
                }
                Point cell = policy.Cell(i);
                grid.Controls.Add(panel, cell.X, cell.Y);
                panel.SizeChanged += delegate { FitViewers(); };
                _panels.Add(panel);
                _statuses.Add(status);
            }
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            Controls.Add(layout);
            Shown += delegate { StartViewers(); };
            _watch.Tick += delegate
            {
                FitViewers();
                for (int i = 0; i < _viewers.Count; i++)
                    if (_viewers[i] != null && _viewers[i].HasExited)
                        _statuses[i].Text = "카메라 " + (i + 1) + " 화면이 종료되었습니다.\r\n프로그램을 다시 실행해 주세요.";
            };
        }

        private void FitViewers()
        {
            for (int i = 0; i < _viewers.Count; i++)
            {
                Process process = _viewers[i];
                if (process != null && !process.HasExited) EmbeddedWindow.FitChildren(_panels[i], process.Id);
            }
        }

        private void LoadLogo()
        {
            using (var stream = typeof(MonitorForm).Assembly.GetManifestResourceStream("SC6000DualMonitor.WillBLogo.png"))
            using (var source = Image.FromStream(stream))
                _logo = new Bitmap(source);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr window);
        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private void StartViewers()
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (!_configured[i]) { _viewers.Add(null); continue; }
                try
                {
                    _viewers.Add(Process.Start(new ProcessStartInfo
                    {
                        FileName = _exePath,
                        WorkingDirectory = _baseDir,
                        UseShellExecute = false,
                        Arguments = "--viewer --slot=" + (i + 1) + " --parent=" + _panels[i].Handle.ToInt64()
                    }));
                }
                catch (Exception ex)
                {
                    _viewers.Add(null);
                    _statuses[i].Text = "카메라 " + (i + 1) + " 화면을 시작하지 못했습니다.\r\n" + ex.Message;
                    Trace.WriteLine(ex);
                }
            }
            _watch.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            _watch.Stop();
            foreach (Panel panel in _panels)
                EnumChildWindows(panel.Handle, delegate(IntPtr window, IntPtr unused)
                {
                    uint pid; GetWindowThreadProcessId(window, out pid);
                    if (GetParent(window) == panel.Handle && _viewers.Any(p => p != null && p.Id == pid)) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            foreach (Process process in _viewers)
            {
                if (process == null) continue;
                try
                {
                    if (!process.HasExited)
                    {
                        // Only our own viewer processes; allow SDK disposal before termination.
                        process.CloseMainWindow();
                        if (!process.WaitForExit(1500)) process.Kill();
                    }
                }
                catch (Exception ex) { Trace.WriteLine(ex); }
                finally { process.Dispose(); }
            }
            _watch.Dispose();
            if (_logo != null) _logo.Dispose();

        }
    }
}
