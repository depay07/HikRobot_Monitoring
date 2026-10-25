using System;
using System.Drawing;
using System.Windows.Forms;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal sealed class SolutionPane : UserControl
    {
        public Panel Content { get; private set; }
        public InspectionPane Inspection { get; private set; }
        public event Action SolutionChanging;
        public event Action SolutionChanged;
        private readonly SolutionController _controller;
        private readonly Action _reloadFrontend;
        private readonly Label _current = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Text = "Solution: 연결 대기" };
        private readonly Label _state = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
        private readonly ComboBox _choices = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, MinimumSize = Size.Empty };
        private readonly Button _refresh = new Button { Dock = DockStyle.Fill, Text = "새로고침", BackColor = SystemColors.Control, UseVisualStyleBackColor = true };
        private readonly Button _change = new Button { Dock = DockStyle.Fill, Text = "변경", BackColor = SystemColors.Control, UseVisualStyleBackColor = true };
        private readonly Button _reload = new Button { Text = "화면 다시 읽기", AutoSize = true, ForeColor = Color.Black, BackColor = SystemColors.Control, UseVisualStyleBackColor = true };
        private readonly ToolTip _tip = new ToolTip();

        private bool _connected, _busy;
        public bool IsBusy { get { return _busy; } }
        public void RefreshCurrent() { ReadCurrent(); }
        private string _currentPath;
        public SolutionPane(ISolutionSession session, Action reloadFrontend, CameraSettings settings = null)
        {
            _controller = new SolutionController(session); _reloadFrontend = reloadFrontend;
            Dock = DockStyle.Fill; AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(45, 52, 56); ForeColor = Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.Controls.Add(_state, 0, 0); footer.SetColumnSpan(_state, 4);
            footer.Controls.Add(_reload, 0, 1); _reload.Anchor = AnchorStyles.Left;
            footer.Controls.Add(_choices, 1, 1); footer.Controls.Add(_refresh, 2, 1); footer.Controls.Add(_change, 3, 1);
            _refresh.ForeColor = _change.ForeColor = Color.Black;
            _refresh.AutoSize = _change.AutoSize = true;
            _refresh.AutoSizeMode = _change.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _refresh.Padding = _change.Padding = new Padding(12, 3, 12, 3);
            // Preferred size includes the actual font metrics at the current DPI.
            _refresh.MinimumSize = _refresh.GetPreferredSize(Size.Empty);
            _change.MinimumSize = _change.GetPreferredSize(Size.Empty);
            Inspection = new InspectionPane(settings);
            layout.Controls.Add(_current, 0, 0); layout.Controls.Add(Inspection, 0, 1); layout.Controls.Add(Content, 0, 2); layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
            _choices.SelectedIndexChanged += delegate { UpdateEnabled(); }; // Selection never calls Load.
            _refresh.Click += delegate { RefreshSolutions(false); };
            _change.Click += delegate { ChangeSolution(); };
            _reload.Click += delegate { ReloadDisplay(); };
            _tip.SetToolTip(_reload, "현재 Operation Interface를 다시 읽습니다. Solution과 카운터는 유지됩니다.");

            UpdateEnabled();
        }
        public void Connected()
        {
            _connected = true; ReadCurrent(); RefreshSolutions(true);
        }
        private void DisplayCurrent(string path)
        {
            _currentPath = path;
            _current.Text = "Solution: " + (string.IsNullOrWhiteSpace(path) ? "실행 경로 미제공" : SolutionPaths.FileName(path));
            _tip.SetToolTip(_current, path ?? string.Empty);
            UpdateEnabled();
        }
        private string ReadCurrent()
        {
            try
            {
                DisplayCurrent(_controller.CurrentPath);
                return string.IsNullOrWhiteSpace(_currentPath) ? "현재 실행 Solution 경로를 SDK가 제공하지 않았습니다." : string.Empty;
            }
            catch (Exception ex)
            {
                DisplayCurrent(null);
                _current.Text = "Solution: 경로 조회 실패";
                string error = "현재 실행 Solution 조회 실패: " + Describe(ex);
                _tip.SetToolTip(_current, error);
                SetState(error);
                return error;
            }
        }
        private void SetState(string value) { _state.Text = value; _tip.SetToolTip(_state, value); }
        private void SetBusy(bool busy, string text)
        {
            _busy = busy; Content.Enabled = !busy; SetState(text); UpdateEnabled(); _state.Refresh();
        }
        private void UpdateEnabled()
        {
            var selected = _choices.SelectedItem as SolutionEntry;
            _reload.Enabled = _refresh.Enabled = _connected && !_busy;
            _choices.Enabled = _connected && !_busy && _choices.Items.Count > 0;
            _change.Enabled = _connected && !_busy && selected != null && !SolutionPaths.Same(_currentPath, selected.Path);
        }
        private void ReloadDisplay()
        {
            if (!_connected || _busy) return;
            SetBusy(true, "화면 다시 읽는 중...");
            string state;
            try
            {
                // Frontend only: never load the startup recipe, reset counts or restart acquisition.
                _reloadFrontend();
                state = "화면 다시 읽기 완료. 다음 검사 영상과 Errors를 확인하세요.";
            }
            catch (Exception ex)
            {
                state = "화면 다시 읽기 실패: " + Describe(ex);
                MessageBox.Show(this, state, "화면 다시 읽기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            SetBusy(false, state);
        }
        private void RefreshSolutions(bool initial)
        {
            if (!_connected || _busy || !IsHandleCreated) return;
            string selected = (_choices.SelectedItem as SolutionEntry)?.Path;
            SetBusy(true, "목록 조회 중...");
            BeginInvoke(new Action(delegate
            {
                string state = "";
                try
                {
                    var entries = _controller.Refresh();
                    string currentWarning = ReadCurrent();
                    _choices.Items.Clear();
                    foreach (var entry in entries) _choices.Items.Add(entry);
                    foreach (SolutionEntry entry in _choices.Items)
                        if (SolutionPaths.Same(entry.Path, selected ?? _currentPath)) { _choices.SelectedItem = entry; break; }
                    state = entries.Count == 0 ? ".sol / .solx 파일이 없습니다." : entries.Count + "개 Solution";
                    if (!string.IsNullOrEmpty(currentWarning)) state += " / " + currentWarning;
                }
                catch (Exception ex)
                {
                    _choices.Items.Clear();
                    state = "목록 조회 실패: " + Describe(ex);
                    if (!initial) MessageBox.Show(this, state, "WILLB | Solution 목록", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { SetBusy(false, state); }
            }));
        }
        private void ChangeSolution()
        {
            if (!_connected || _busy || !_change.Enabled) return;
            var selected = _choices.SelectedItem as SolutionEntry;
            SetBusy(true, "Switching...");
            BeginInvoke(new Action(delegate
            {
                string state = "";
                try
                {
                    if (SolutionChanging != null) SolutionChanging();
                    var result = _controller.Switch(selected);
                    DisplayCurrent(result.CurrentPath);
                    if (result.VerificationPending)
                    {
                        _reloadFrontend();
                        state = "전환 호출 완료: " + selected + " / 실제 경로 확인 불가 (자동 복원 불가)";
                        if (result.Error != null) state += " / " + Describe(result.Error);
                    }
                    else if (result.Success)
                    {
                        if (!result.Unchanged) _reloadFrontend();
                        state = result.Unchanged ? "현재 Solution과 같습니다." : "변경 완료: " + SolutionPaths.FileName(result.CurrentPath);
                    }
                    else
                    {
                        state = (result.ChangeAttempted ? "변경 실패: " : "변경 전 확인 실패 (전환 명령을 보내지 않았습니다): ") + Describe(result.Error) + "\r\n" +
                            (!result.ChangeAttempted ? "현재 경로 정보가 서로 다르거나 선택한 파일이 유효하지 않아 전환하지 않았습니다." :
                            (result.OriginalPreserved ? "기존 Solution 유지/복원을 확인했습니다." : "기존 경로를 확인할 수 없거나 복원하지 못했습니다. 장비 상태를 확인하세요."));
                        if (result.RecoveryError != null) state += "\r\n복원 오류: " + Describe(result.RecoveryError);
                        if (result.RecoveryAttempted || result.LoadCompleted)
                            try { _reloadFrontend(); } catch (Exception ex) { state += "\r\n화면 재로딩 오류: " + Describe(ex); }
                        MessageBox.Show(this, state, "WILLB | Solution 변경 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (Exception ex)
                {
                    ReadCurrent();
                    state = "화면 갱신 실패: " + Describe(ex) + " (현재 Solution 표시를 확인하세요.)";
                    MessageBox.Show(this, state, "WILLB | Operation Interface", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    try { if (SolutionChanged != null) SolutionChanged(); }
                    catch (Exception ex) { Inspection.Failed(ex); }
                    SetBusy(false, state);
                }
            }));
        }
        private static string Describe(Exception error)
        {
            if (error == null) return "알 수 없는 오류";
            VmException vm = error as VmException;
            try { if (vm == null) vm = VmSolution.GetVmException(error); } catch { }
            return (vm == null ? "" : "0x" + vm.errorCode.ToString("X8") + " / ") + error.Message;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
