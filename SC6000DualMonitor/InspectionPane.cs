using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class InspectionPane : UserControl
    {
        private readonly CameraSettings _settings;
        private readonly InspectionCounter _counter = new InspectionCounter();
        private readonly object _bindingGate = new object();
        private readonly Timer _displayTimer = new Timer { Interval = 200 };
        private readonly TextBox _part = new TextBox { Dock = DockStyle.Fill, MaxLength = 120 };
        private readonly Button _savePart = new Button { Text = "모델명 저장", AutoSize = true };
        private readonly Button _setup = new Button { Text = "Set Up", AutoSize = true };
        private readonly Button _reset = new Button { Text = "Reset", AutoSize = true };
        private readonly Label _state = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label[] _counts = new Label[4];
        private readonly PartNumberStore _store;
        private RemoteInspectionSession _session;
        private string _solutionPath;
        private bool _setupOpen;
        public InspectionPane(CameraSettings settings)
        {
            _settings = settings;
            _store = new PartNumberStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WILLB", "SC6000Monitor", "Models"));
            Dock = DockStyle.Fill; Margin = Padding.Empty;
            BackColor = Color.FromArgb(238, 242, 246); ForeColor = Color.FromArgb(20, 40, 60);
            Font = new Font("Segoe UI", 9);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(6, 2, 6, 2) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var partRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            partRow.Controls.Add(new Label { Text = "PART NO", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            partRow.Controls.Add(_part, 1, 0); partRow.Controls.Add(_savePart, 2, 0);
            layout.Controls.Add(partRow, 0, 0);
            var countRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = Padding.Empty };
            for (int i = 0; i < 4; i++) countRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            countRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            countRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            countRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var colors = new[] { Color.DarkGreen, Color.Firebrick, Color.DarkOrange, Color.FromArgb(20, 40, 60) };
            string[] headings = { "OK", "NG", "Errors", "Total" };
            for (int i = 0; i < 4; i++)
            {
                countRow.Controls.Add(new Label { Text = headings[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(255, 229, 90) }, i, 0);
                _counts[i] = new Label { Text = "0", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = colors[i], Font = new Font(Font, FontStyle.Bold) };
                countRow.Controls.Add(_counts[i], i, 1);
            }
            _reset.Dock = DockStyle.Fill; countRow.Controls.Add(_reset, 4, 0); countRow.SetRowSpan(_reset, 2);
            layout.Controls.Add(countRow, 0, 1);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.Controls.Add(_setup, 0, 0); bottom.Controls.Add(_state, 1, 0); layout.Controls.Add(bottom, 0, 2);
            Controls.Add(layout);
            _part.Text = settings == null ? "" : settings.PartNumber;
            _state.Text = "검사 결과 연결 대기";
            _savePart.Enabled = _setup.Enabled = false;
            _savePart.Click += delegate { SavePart(); };
            _setup.Click += delegate { OpenSetup(); };
            _reset.Click += delegate
            {
                if (MessageBox.Show(this, "이 카메라의 OK / NG / Errors / Total을 초기화할까요?", "Count Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _counter.Reset(); RenderCounts();
            };
            _displayTimer.Tick += delegate { RenderCounts(); };
            _displayTimer.Start();
        }
        public void Bind(RemoteInspectionSession session, string solutionPath)
        {
            Unbind();
            SetModelPath(solutionPath);
            lock (_bindingGate) { _session = session; _session.ResultArrived += OnResult; }
            _setup.Enabled = true;
        }
        public void SetModelPath(string solutionPath)
        {
            _solutionPath = solutionPath;
            _part.Text = _settings.PartNumber ?? "";
            string storageError = null;
            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                try { _part.Text = _store.Read(_settings.Ip, solutionPath, _settings.PartNumber); }
                catch (Exception ex) { storageError = "모델명 읽기 실패: " + ex.Message; }
            }
            _savePart.Enabled = !string.IsNullOrWhiteSpace(solutionPath);
            if (storageError != null) _state.Text = storageError;
            else if (_setup.Enabled || _state.Text == "검사 결과 연결 대기")
                _state.Text = string.IsNullOrWhiteSpace(solutionPath) ? "모델명 저장: 레시피 경로 확인 필요" : "검사 대기";
        }
        private void OnResult(object sender, InspectionResultEventArgs result)
        {
            lock (_bindingGate)
            {
                if (!ReferenceEquals(sender, _session)) return;
                _counter.Record(result);
            }
            // Do not queue one UI message per frame: the display timer paints snapshots.
        }
        public void Unbind()
        {
            RemoteInspectionSession previous;
            lock (_bindingGate) { previous = _session; _session = null; _counter.Reset(); }
            // Outside the binding lock: Dispose can wait for an SDK callback holding its own lock.
            if (previous != null) { previous.ResultArrived -= OnResult; previous.Dispose(); }
            _setup.Enabled = _savePart.Enabled = false;
            _solutionPath = null;
            _state.Text = "검사 결과 연결 대기";
            RenderCounts();
        }
        public void Failed(Exception error)
        { Unbind(); _state.Text = "Count / Set Up 연결 실패: " + error.Message; }
        private void RenderCounts()
        {
            var count = _counter.Read();
            long[] values = { count.Ok, count.Ng, count.Errors, count.Total };
            for (int i = 0; i < 4; i++) _counts[i].Text = values[i].ToString("N0");
            if (_session != null && count.Total > 0 && !_setupOpen) _state.Text = count.LastResult;
        }
        private void SavePart()
        {
            try { _store.Write(_settings.Ip, _solutionPath, _part.Text.Trim()); _part.Text = _part.Text.Trim(); _state.Text = "모델명 저장 완료"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "PART NO", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void OpenSetup()
        {
            if (_session == null || _setupOpen) return;
            _setupOpen = true;
            try { using (var form = new InspectionSetupForm(_session, _settings.Title)) form.ShowDialog(this); }
            finally { _setupOpen = false; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _displayTimer.Stop(); _displayTimer.Dispose(); Unbind(); }
            base.Dispose(disposing);
        }
    }
}
