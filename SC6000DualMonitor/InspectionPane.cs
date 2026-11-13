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
        private readonly CheckBox _csvEnabled = new CheckBox { Text = "CSV 기록 저장", AutoSize = true, Checked = false };
        private readonly Button _csvFolder = new Button { Text = "폴더", AutoSize = true };
        private readonly Button _csvMappingButton = new Button { Text = "항목 설정", AutoSize = true };
        private readonly CsvMappingStore _csvMappingStore;
        private readonly Label _csvState = new Label { AutoSize = false, Width = 162, Height = 26, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "CSV 기록 중지", ForeColor = Color.DimGray };
        private readonly CsvInspectionWriter _csvWriter;
        private readonly Button _csvButton = new Button { Text = "CSV", AutoSize = true };
        private volatile string _csvPartNumber = "";
        private volatile string _csvRecipePath = "";
        private string _csvActivationError;
        private bool _updatingCsvCheckbox;
        private volatile bool _csvActive;
        private readonly Label _state = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label[] _counts = new Label[4];
        private readonly PartNumberStore _store;
        private RemoteInspectionSession _session;
        private string _solutionPath;
        private string _lastError;
        private readonly Button _errorDetails = new Button { Text = "오류 상세", AutoSize = true, Enabled = false };
        private bool _setupOpen;
        public bool IsEditing { get { return _setupOpen; } }
        public InspectionPane(CameraSettings settings)
        {
            _settings = settings;
            _store = new PartNumberStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WILLB", "SC6000Monitor", "Models"));
            _csvWriter = new CsvInspectionWriter((settings == null ? "camera" : settings.Ip));
            _csvMappingStore = new CsvMappingStore((settings == null ? "camera" : settings.Ip));
            Dock = DockStyle.Fill; Margin = Padding.Empty;
            BackColor = Color.FromArgb(238, 242, 246); ForeColor = Color.FromArgb(20, 40, 60);
            Font = new Font("Segoe UI", 9);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(6, 2, 6, 2) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var partRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            partRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            partRow.Controls.Add(new Label { Text = "PART NO", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            partRow.Controls.Add(_part, 1, 0); partRow.Controls.Add(_savePart, 2, 0);
            _csvButton.Click += delegate { OpenCsvOptions(); };
            partRow.Controls.Add(_csvButton, 3, 0);
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
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(_errorDetails, 2, 0);
            _errorDetails.Click += delegate
            {
                string detail;
                lock (_bindingGate) detail = _lastError;
                if (detail != null) MessageBox.Show(this, detail, "최근 검사 오류 (" + (_settings == null ? "" : _settings.Ip) + ")", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            bottom.Controls.Add(_setup, 0, 0); bottom.Controls.Add(_state, 1, 0); layout.Controls.Add(bottom, 0, 2);
            Controls.Add(layout);
            _part.Text = settings == null ? "" : settings.PartNumber;
            _state.Text = "검사 결과 연결 대기";
            _csvPartNumber = _part.Text;
            _csvEnabled.Checked = false; // Explicit opt-in for every program launch.
            _csvEnabled.Enabled = false;
            _csvEnabled.CheckedChanged += delegate { ApplyCsvSetting(); };
            _csvState.Width = 520;
            _csvState.Height = 60;
            _csvMappingButton.Enabled = false;
            _csvMappingButton.Click += delegate { ConfigureCsvMapping(); };
            _csvFolder.Click += delegate
            {
                try { Directory.CreateDirectory(_csvWriter.Folder); System.Diagnostics.Process.Start("explorer.exe", "\"" + _csvWriter.Folder + "\""); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 저장 폴더"); }
            };
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
            _state.Text = "검사 연결: " + session.TargetDescription;
            _csvEnabled.Enabled = true;
            _csvMappingButton.Enabled = true;
            if (_csvEnabled.Checked) ApplyCsvSetting();
        }
        public void SetModelPath(string solutionPath)
        {
            if (_csvActive && !string.Equals(_csvRecipePath, solutionPath ?? "", StringComparison.Ordinal))
                _csvEnabled.Checked = false;
            _solutionPath = solutionPath;
            _part.Text = _settings.PartNumber ?? "";
            string storageError = null;
            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                try { _part.Text = _store.Read(_settings.Ip, solutionPath, _settings.PartNumber); }
                catch (Exception ex) { storageError = "모델명 읽기 실패: " + ex.Message; }
            }
            _csvRecipePath = solutionPath ?? "";
            _csvPartNumber = _part.Text;
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
                if (result.Outcome == InspectionOutcome.Error) _lastError = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + result.Detail;
            }
            // Do not queue one UI message per frame: the display timer paints snapshots.
        }
        private void OnCsvSample(object sender, CsvInspectionSample sample)
        {
            lock (_bindingGate)
            {
                if (!ReferenceEquals(sender, _session) || !_csvActive) return;
                _csvWriter.Enqueue(sample, _csvPartNumber, _csvRecipePath);
            }
        }
        private void OpenCsvOptions()
        {
            using (var dialog = new Form { Text = "CSV 검사 결과 기록", Size = new Size(600, 240), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            {
                var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var buttons = new FlowLayoutPanel { Width = 540, Height = 36 };
                buttons.Controls.Add(_csvEnabled); buttons.Controls.Add(_csvMappingButton); buttons.Controls.Add(_csvFolder);
                panel.Controls.Add(buttons); panel.Controls.Add(_csvState);
                panel.Controls.Add(new Label { Text = "항목 설정에서 Top / Middle / Bottom / Circle에 해당하는 Format 모듈을 선택하세요.\r\nCSV 기록은 창을 닫아도 계속됩니다. 카메라 레시피는 변경하지 않습니다.", Width = 540, Height = 50 });
                dialog.Controls.Add(panel);
                dialog.ShowDialog(this);
                buttons.Controls.Clear(); panel.Controls.Remove(_csvState); // Keep persistent controls alive.
            }
        }
        private void ConfigureCsvMapping()
        {
            var session = _session;
            if (session == null) return;
            if (_csvEnabled.Checked)
            {
                MessageBox.Show(this, "CSV 기록 저장 체크를 해제한 다음 항목을 변경하세요.", "CSV 설정");
                return;
            }
            try
            {
                string recipe = _csvRecipePath;
                using (var dialog = new CsvMappingForm(session.ListCsvModuleNames(), _csvMappingStore.Read(session.CsvProcedureName, _csvRecipePath)))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (!ReferenceEquals(session, _session) || recipe != _csvRecipePath) throw new InvalidOperationException("레시피가 변경되었습니다. 항목 설정을 다시 열어주세요.");
                    _csvMappingStore.Write(session.CsvProcedureName, recipe, dialog.Selection);
                    _csvActivationError = null;
                    UpdateCsvStatus();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 항목 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void ApplyCsvSetting()
        {
            if (_updatingCsvCheckbox) return;
            var session = _session;
            if (session == null) return;
            bool wanted = _csvEnabled.Checked;
            try
            {
                if (wanted)
                {
                    session.CsvSampleReady -= OnCsvSample;
                    session.CsvSampleReady += OnCsvSample;
                    _csvActive = true;
                    session.SetCsvCaptureEnabled(true, _csvMappingStore.Read(session.CsvProcedureName, _csvRecipePath));
                }
                else
                {
                    session.SetCsvCaptureEnabled(false);
                    session.CsvSampleReady -= OnCsvSample;
                }
                _csvActive = wanted;
                _csvActivationError = null;
            }
            catch (Exception ex)
            {
                _csvActive = false;
                _csvActivationError = ex.Message;
                try { session.SetCsvCaptureEnabled(false); } catch { }
                session.CsvSampleReady -= OnCsvSample;
                _updatingCsvCheckbox = true;
                _csvEnabled.Checked = false;
                _updatingCsvCheckbox = false;
                MessageBox.Show(this, "CSV 기록 활성화 실패:\r\n" + ex.Message + "\r\n기존 검사 기능은 유지됩니다.", "CSV 기록", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateCsvStatus();
        }
        private void UpdateCsvStatus()
        {
            _csvButton.Text = _csvWriter.HasError || _csvActivationError != null ? "CSV 오류" : (_csvActive ? "CSV 기록 중" : "CSV");
            _csvState.Text = _csvActivationError != null ? "CSV 오류: " + _csvActivationError :
                (_csvActive || _csvWriter.HasError ? _csvWriter.Status : "CSV 기록 중지");
            _csvState.ForeColor = _csvActivationError != null || _csvWriter.HasError ? Color.Firebrick : Color.DarkGreen;

        }
        public void Unbind()
        {
            RemoteInspectionSession previous;
            try { if (_session != null) _session.SetCsvCaptureEnabled(false); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("CSV stop: " + ex); } // Flush completed rows while still subscribed.
            lock (_bindingGate) { previous = _session; _session = null; _csvActive = false; _counter.Reset(); _lastError = null; }
            // Outside the binding lock: Dispose can wait for an SDK callback holding its own lock.
            if (previous != null) { previous.ResultArrived -= OnResult; previous.CsvSampleReady -= OnCsvSample; previous.Dispose(); }
            _csvEnabled.Enabled = false;
            _csvMappingButton.Enabled = false;
            _setup.Enabled = _savePart.Enabled = false;
            _solutionPath = null;
            _state.Text = "검사 결과 연결 대기";
            RenderCounts();
        }
        public void Failed(Exception error)
        { Unbind(); _state.Text = "Count / Set Up 연결 실패: " + error.Message; }
        private void RenderCounts()
        {
            lock (_bindingGate) _errorDetails.Enabled = _lastError != null;
            var count = _counter.Read();
            long[] values = { count.Ok, count.Ng, count.Errors, count.Total };
            for (int i = 0; i < 4; i++) _counts[i].Text = values[i].ToString("N0");
            if (_session != null && count.Total > 0 && !_setupOpen) _state.Text = count.LastResult;
            UpdateCsvStatus();
        }
        private void SavePart()
        {
            try { _store.Write(_settings.Ip, _solutionPath, _part.Text.Trim()); _part.Text = _part.Text.Trim(); _csvPartNumber = _part.Text; _state.Text = "모델명 저장 완료"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "PART NO", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void OpenSetup()
        {
            if (_session == null || _setupOpen) return;
            _setupOpen = true;
            bool filesChanged = false;
            try { using (var form = new SetupMenuForm(_session, _settings, RefreshGraphicsAfterSave)) { form.ShowDialog(this); filesChanged = form.RecipeFilesChanged; } }
            finally { _setupOpen = false; }
            if (filesChanged && RecipeFilesChanged != null) RecipeFilesChanged();
        }
        public event Action RecipeFilesChanged;
        public Func<System.Threading.Tasks.Task> RefreshGraphicsAfterSave { get; set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _displayTimer.Stop(); _displayTimer.Dispose(); Unbind(); _csvWriter.Dispose(); _csvEnabled.Dispose(); _csvFolder.Dispose(); _csvMappingButton.Dispose(); _csvState.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
