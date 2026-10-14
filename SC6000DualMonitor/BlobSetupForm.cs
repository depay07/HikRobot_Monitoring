using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class BlobEditorTarget
    {
        public string Name, DisplayName;
        public IBlobSettingsSession Session;
        public override string ToString() { return DisplayName; }
    }
    internal sealed class BlobSetupForm : Form
    {
        private readonly Func<IList<BlobEditorTarget>> _discover;
        private readonly string _preferred;
        private readonly ComboBox _modules = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _target = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly Label _mode = new Label { Dock = DockStyle.Fill, AutoSize = true };
        private readonly Label _state = new Label { Dock = DockStyle.Fill };
        private readonly NumericUpDown _low = Number(0, 255), _high = Number(0, 255);
        private readonly NumericUpDown _minArea = Number(1, 999999999), _maxArea = Number(1, 999999999);
        private readonly CheckBox _area = new CheckBox { Text = "면적 필터 사용", AutoSize = true };
        private readonly Button _apply = new Button { Text = "적용 및 카메라 저장", AutoSize = true, Enabled = false };
        private readonly TableLayoutPanel _editor;
        private IBlobSettingsSession _session;
        private BlobSettings _baseline;
        public BlobSetupForm(string title, Func<IList<BlobEditorTarget>> discover, string preferred)
        {
            _discover = discover; _preferred = preferred;
            Text = title + " | BLOB 조건 설정";
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10); Size = new Size(750, 520); MinimumSize = new Size(660, 490);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.Controls.Add(_modules, 0, 0); layout.Controls.Add(_target, 0, 1);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "생산 트리거를 멈춘 상태에서 적용하세요. 저장 시 검사 흐름을 일시 정지합니다.\r\nLow / High는 Double Threshold 모드에서 조정할 수 있습니다." }, 0, 2);
            _editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Enabled = false };
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            string[] labels = { "현재 Threshold Mode", "Low Threshold (0~255)", "High Threshold (0~255)", "Area Enable", "최소 면적", "최대 면적" };
            Control[] inputs = { _mode, _low, _high, _area, _minArea, _maxArea };
            for (int i = 0; i < labels.Length; i++)
            {
                _editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / labels.Length));
                _editor.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
                _editor.Controls.Add(inputs[i], 1, i);
            }
            layout.Controls.Add(_editor, 0, 3); layout.Controls.Add(_state, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
            var reload = new Button { Text = "카메라 값 다시 읽기", AutoSize = true };
            buttons.Controls.Add(close); buttons.Controls.Add(_apply); buttons.Controls.Add(reload); layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout); CancelButton = close;
            _modules.SelectedIndexChanged += delegate { ReadCamera(); };
            _area.CheckedChanged += delegate { UpdateInputs(); };
            reload.Click += delegate { if (_modules.SelectedItem == null) Discover(); else ReadCamera(); };
            _apply.Click += delegate { Apply(); };
            Shown += delegate { Discover(); };
        }
        private static NumericUpDown Number(decimal min, decimal max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 0, Dock = DockStyle.Fill, ThousandsSeparator = false };
        }
        private void Discover()
        {
            try
            {
                _modules.Items.Clear(); _session = null; _baseline = null; _apply.Enabled = _editor.Enabled = false;
                var targets = _discover();
                foreach (var item in targets) _modules.Items.Add(item);
                var selected = string.IsNullOrWhiteSpace(_preferred) ? (targets.Count == 1 ? targets[0] : null) : targets.FirstOrDefault(t => t.Name == _preferred);
                if (selected != null) _modules.SelectedItem = selected;
                else if (targets.Count == 0) _state.Text = "현재 검사 흐름에 Blob Analysis 모듈이 없습니다.";
                else _state.Text = string.IsNullOrWhiteSpace(_preferred) ? "설정할 BLOB 모듈을 위 목록에서 선택하세요." : "BLOB_MODULE 이름과 일치하는 모듈이 없습니다. 위 목록에서 실제 모듈을 선택하세요.";
            }
            catch (Exception ex) { _state.Text = "BLOB 목록 조회 실패: " + ex.Message; }
        }
        private bool ReadCamera()
        {
            _baseline = null; _apply.Enabled = _editor.Enabled = false;
            var selected = _modules.SelectedItem as BlobEditorTarget;
            _session = selected == null ? null : selected.Session;
            _target.Text = _session == null ? "" : _session.TargetDescription;
            _low.Value = _high.Value = 0; _minArea.Value = _maxArea.Value = 1; _area.Checked = false; _mode.Text = "";
            if (_session == null) return false;
            try
            {
                var values = _session.Read(); values.Validate();
                _low.Value = values.Low; _high.Value = values.High; _minArea.Value = values.MinArea; _maxArea.Value = values.MaxArea; _area.Checked = values.AreaEnabled;
                _baseline = values.Copy();
                _mode.Text = values.DoubleThreshold ? "Double Threshold" : "모드 " + values.ThresholdMode + " (Low/High 편집 비활성)";
                _editor.Enabled = _apply.Enabled = true; UpdateInputs();
                _state.Text = DateTime.Now.ToString("HH:mm:ss") + " BLOB 값 읽기 완료";
                return true;
            }
            catch (Exception ex) { _state.Text = "BLOB 재조회 실패: " + ex.Message; return false; }
        }
        private void UpdateInputs()
        {
            _low.Enabled = _high.Enabled = _baseline != null && _baseline.DoubleThreshold;
            _minArea.Enabled = _maxArea.Enabled = _baseline != null && _area.Checked;
        }
        private void Apply()
        {
            if (_session == null || _baseline == null) return;
            try
            {
                ValidateChildren();
                var requested = _baseline.Copy();
                requested.Low = (int)_low.Value; requested.High = (int)_high.Value;
                requested.MinArea = (int)_minArea.Value; requested.MaxArea = (int)_maxArea.Value; requested.AreaEnabled = _area.Checked;
                requested.Validate();
                Enabled = false; _state.Text = "BLOB 적용·확인·저장 중..."; _state.Refresh();
                BlobSettingsController.Apply(_session, _baseline, requested);
                if (ReadCamera()) _state.Text = "BLOB 적용 및 카메라 저장 완료";
            }
            catch (Exception ex) { _apply.Enabled = false; _baseline = null; _editor.Enabled = false; _state.Text = "실패: " + ex.Message; }
            finally { Enabled = true; }
        }
    }
}
