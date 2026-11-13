using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class ExposureSetupForm : Form
    {
        private readonly Func<IList<IExposureSession>> _discover;
        private readonly Action<string> _log;
        private readonly ComboBox _cameras = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _target = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly Label _state = new Label { Dock = DockStyle.Fill };
        private readonly Label _exposureRange = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _gainRange = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly NumericUpDown _exposure = Number(), _gain = Number();
        private readonly Button _apply = new Button { Text = "적용 및 카메라 저장", AutoSize = true, Enabled = false };
        private readonly TableLayoutPanel _editor;
        private ExposureSettings _baseline;
        private IExposureSession _session;
        private bool _applying;
        public Func<Task> RefreshGraphicsAfterSave { get; set; }
        public ExposureSetupForm(string title, Func<IList<IExposureSession>> discover, Action<string> log)
        {
            _discover = discover; _log = log;
            Text = title + " | 노출 / 게인 설정";
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("맑은 고딕", 10); ClientSize = new Size(760, 430); MinimumSize = new Size(760, 460);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            foreach (var height in new[] { 34, 76, 58 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(_cameras, 0, 0); layout.Controls.Add(_target, 0, 1);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "생산 트리거를 멈춘 뒤 적용하세요. 적용 중 검사 흐름을 일시 정지합니다.\r\n현재 카메라 레시피에 저장합니다. 저장 후 다음 촬영부터 확인하세요." }, 0, 2);
            _editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Enabled = false };
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            _editor.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); _editor.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _editor.Controls.Add(new Label { Text = "노출 시간 Exposure (ms)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _editor.Controls.Add(new Label { Text = "게인 Gain (dB)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            _editor.Controls.Add(_exposure, 1, 0); _editor.Controls.Add(_gain, 1, 1);
            _editor.Controls.Add(_exposureRange, 2, 0); _editor.Controls.Add(_gainRange, 2, 1);
            layout.Controls.Add(_editor, 0, 3); layout.Controls.Add(_state, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
            var reload = new Button { Text = "카메라 값 다시 읽기", AutoSize = true };
            buttons.Controls.Add(close); buttons.Controls.Add(_apply); buttons.Controls.Add(reload);
            layout.Controls.Add(buttons, 0, 5); Controls.Add(layout); CancelButton = close;
            Shown += delegate { Discover(); };
            _cameras.SelectedIndexChanged += delegate { ReadCamera(); };
            reload.Click += delegate { if (_cameras.SelectedItem == null) Discover(); else ReadCamera(); };
            _apply.Click += delegate { Apply(); };
        }
        private static NumericUpDown Number()
        { return new NumericUpDown { Minimum = -999999999, Maximum = 999999999, DecimalPlaces = 6, Increment = 0.1m, Anchor = AnchorStyles.Left | AnchorStyles.Right }; }
        private void InvalidateValues()
        { _baseline = null; _editor.Enabled = _apply.Enabled = false; }
        private void Discover()
        {
            InvalidateValues(); _cameras.Items.Clear();
            try
            {
                var targets = _discover();
                foreach (var target in targets) _cameras.Items.Add(target);
                if (targets.Count == 1) _cameras.SelectedIndex = 0;
                else _state.Text = targets.Count == 0 ? "설정 가능한 카메라가 없습니다." : "위 목록에서 설정할 카메라를 선택하세요.";
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void Display(ExposureSettings values)
        {
            values.Exposure.Validate(values.Exposure.Value, "Exposure"); values.Gain.Validate(values.Gain.Value, "Gain");
            _exposure.Value = (decimal)values.Exposure.Value; _gain.Value = (decimal)values.Gain.Value;
            _exposureRange.Text = "범위: " + values.Exposure.RangeText;
            _gainRange.Text = "범위: " + values.Gain.RangeText;
            _baseline = values;
            _editor.Enabled = _apply.Enabled = values.Manual;
        }
        private void ReadCamera()
        {
            InvalidateValues(); _session = _cameras.SelectedItem as IExposureSession;
            _target.Text = _session == null ? "" : _session.Description;
            _exposure.Value = _gain.Value = 0; _exposureRange.Text = _gainRange.Text = "";
            if (_session == null) return;
            try
            {
                Display(_session.Read());
                _state.ForeColor = _baseline.Manual ? Color.DarkBlue : Color.Firebrick;
                _state.Text = _baseline.Manual ? "카메라 값 읽기 완료. 1 ms = 1,000 μs입니다." :
                    "자동 노출 또는 자동 게인이 켜져 있습니다.\r\nVisionMaster에서 두 자동 모드를 OFF로 설정한 뒤 다시 읽으세요.";
            }
            catch (Exception ex) { Fail(ex); }
        }
        private async void Apply()
        {
            if (_session == null || _baseline == null || _applying) return;
            _applying = true;
            try
            {
                ValidateChildren();
                double exposure = (double)_exposure.Value, gain = (double)_gain.Value;
                _baseline.Validate(exposure, gain);
                Enabled = false; _state.Text = "노출/게인 적용 및 현재 레시피 저장 중..."; _state.Refresh();
                ExposureSettings accepted = null;
                string warning = await SettingsSaveCompletion.ApplyAsync(
                    () => { accepted = ExposureController.Apply(_session, _baseline, exposure, gain); }, RefreshGraphicsAfterSave);
                Display(accepted);
                bool adjusted = !ExposureSettings.Near(exposure, accepted.Exposure.Value) || !ExposureSettings.Near(gain, accepted.Gain.Value);
                _state.ForeColor = Color.DarkBlue;
                _state.Text = "카메라 저장 완료: Exposure " + accepted.Exposure.Value.ToString("G9", CultureInfo.InvariantCulture) +
                    " ms / Gain " + accepted.Gain.Value.ToString("G9", CultureInfo.InvariantCulture) + " dB" +
                    (adjusted ? "\r\n입력값과 실제 적용값이 다릅니다. 카메라에서 읽은 값을 표시했습니다." : "");
                if (_log != null) _log(_state.Text);
                if (warning != null)
                {
                    _state.ForeColor = Color.Firebrick; _state.Text = warning;
                    if (_log != null) _log(warning);
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { _applying = false; Enabled = true; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { if (_applying) e.Cancel = true; base.OnFormClosing(e); }
        private void Fail(Exception ex)
        {
            InvalidateValues(); _state.ForeColor = Color.Firebrick; _state.Text = ex.Message;
            if (_log != null) _log(ex.ToString());
            if (Visible) MessageBox.Show(this, ex.Message, "노출 / 게인 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
