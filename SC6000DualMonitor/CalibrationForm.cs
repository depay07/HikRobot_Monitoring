using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationForm : Form
    {
        private readonly ICalibrationEditorSession _session;
        private readonly NumericUpDown[] _actual;
        private readonly Label[] _pixels, _factors;
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoSize = false };
        private readonly Button _read = new Button { Text = "기준 측정값 읽기", AutoSize = true };
        private readonly Button _apply = new Button { Text = "적용 및 카메라 저장", AutoSize = true, Enabled = false };
        private readonly Timer _timer = new Timer { Interval = 100 };
        private CalibrationFrame _frame;
        private CalibrationModel _requested;
        private DateTime _deadline;
        private bool _waiting;
        public CalibrationForm(ICalibrationEditorSession session, string title)
        {
            _session = session;
            int count = session.Baseline.Count;
            _actual = new NumericUpDown[count]; _pixels = new Label[count]; _factors = new Label[count];
            Text = title + " | 치수 보정";
            ClientSize = new Size(960, Math.Min(720, 270 + count * 42)); MinimumSize = new Size(900, 440);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
            MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = session.Description + "\r\n기준 제품을 놓고 [기준 측정값 읽기]를 누른 뒤 검사 트리거를 1회 보내세요.\r\n표시된 각 항목의 실제 치수를 mm로 입력하세요. 원은 지름을 입력합니다.\r\n적용 중에는 생산 트리거를 멈추세요. 현재 카메라 레시피에 저장합니다." }, 0, 0);
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = count + 1, AutoScroll = true, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            foreach (float width in new float[] { 16, 20, 20, 22, 22 }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            for (int i = 0; i <= count; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            string[] headings = { "현재 레시피 항목", "계산에 사용할 픽셀값", "실제 치수 (mm)", "현재 계수", "새 계수" };
            for (int i = 0; i < 5; i++) table.Controls.Add(Cell(headings[i]), i, 0);
            for (int i = 0; i < _actual.Length; i++)
            {
                table.Controls.Add(Cell(session.Baseline.Names[i] + (session.RadiusInputs[i] ? " (지름)" : "")), 0, i + 1);
                _pixels[i] = Cell("읽기 전" + PixelBasis(i)); table.Controls.Add(_pixels[i], 1, i + 1);
                _actual[i] = new NumericUpDown { DecimalPlaces = 6, Minimum = 0, Maximum = 1000000, Increment = 0.01m, Dock = DockStyle.Fill, ThousandsSeparator = true };
                _actual[i].ValueChanged += delegate { Recalculate(); };
                table.Controls.Add(_actual[i], 2, i + 1);
                table.Controls.Add(Cell(session.Baseline.Factors[i].ToString("G12")), 3, i + 1);
                _factors[i] = Cell("—"); table.Controls.Add(_factors[i], 4, i + 1);
            }
            layout.Controls.Add(table, 0, 1); layout.Controls.Add(_status, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(close); buttons.Controls.Add(_apply); buttons.Controls.Add(_read);
            buttons.Controls.Add(new Button { Text = "항목 연결", AutoSize = true, DialogResult = DialogResult.Retry });
            layout.Controls.Add(buttons, 0, 3); Controls.Add(layout); CancelButton = close;
            _status.Text = "선택한 전역 변수 4개만 변경합니다: " + string.Join(", ", session.Baseline.Variables) + "\r\n새 계수는 [적용 및 카메라 저장]을 눌러야 반영됩니다.";
            _read.Click += delegate { BeginRead(); };
            _apply.Click += delegate { Apply(); };
            _timer.Tick += delegate { Poll(); }; _timer.Start();
        }
        private static Label Cell(string text) { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }; }
        private string PixelBasis(int i)
        { return _session.RadiusInputs[i] ? " (반지름)" : ""; }
        private void BeginRead()
        {
            _frame = null; _requested = null; _apply.Enabled = false;
            foreach (var label in _pixels) label.Text = "대기";
            foreach (var label in _factors) label.Text = "—";
            try
            {
                _session.Arm(); _waiting = true; _deadline = DateTime.UtcNow.AddSeconds(30);
                _status.Text = "기준 제품의 검사 트리거를 보내세요. 측정값이 있는 같은 회차를 기다립니다 (30초).";
            }
            catch (Exception ex) { _waiting = false; _status.Text = ex.Message; }
        }
        private void Poll()
        {
            if (!_waiting) return;
            var frame = _session.TakeFrame();
            if (frame != null)
            {
                _waiting = false; _frame = frame;
                for (int i = 0; i < _actual.Length; i++) _pixels[i].Text = frame.Pixels[i].ToString("G9") + PixelBasis(i);
                _status.Text = "회차 " + frame.Frame + " / " + frame.Time.ToString("HH:mm:ss") + " 기준 측정값 고정. 표시된 항목의 실제 치수를 입력하세요.";
                Recalculate();
            }
            else if (DateTime.UtcNow > _deadline)
            {
                _waiting = false; _session.CancelRead();
                _status.Text = "측정값 수신 시간 초과. 기준 제품의 선/원 검출과 트리거를 확인한 후 다시 읽으세요.";
            }
        }
        private void Recalculate()
        {
            _requested = null; _apply.Enabled = false;
            for (int i = 0; i < _actual.Length; i++)
                _factors[i].Text = _frame != null && _actual[i].Value > 0 ? ((double)_actual[i].Value / _frame.Pixels[i]).ToString("G12") : "—";
            if (_frame == null || _actual.Any(a => a.Value <= 0)) return;
            try { _requested = _session.Baseline.Calculate(_frame.Pixels, _actual.Select(a => (double)a.Value).ToArray()); _apply.Enabled = true; }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
        private void Apply()
        {
            if (_requested == null) return;
            _apply.Enabled = _read.Enabled = false;
            try
            {
                CalibrationController.Apply(_session, _session.Baseline, _requested);
                MessageBox.Show(this, _session.Baseline.Count + "개 보정계수를 현재 카메라 레시피에 저장했습니다. 이후 검사는 저장한 계수로 계산합니다.", "치수 보정 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex)
            {
                _frame = null; _requested = null;
                _status.Text = "적용하지 못했습니다. 오류 내용을 확인하세요.";
                MessageBox.Show(this, ex.Message, "치수 보정", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _read.Enabled = true; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); _session.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
