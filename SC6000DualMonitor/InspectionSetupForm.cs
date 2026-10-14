using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class InspectionSetupForm : Form
    {
        private readonly IInspectionSettingsSession _session;
        private readonly DataGridView _grid;
        private readonly Button _apply;
        private readonly Label _state;
        private IList<ConditionRange> _ranges;
        public InspectionSetupForm(IInspectionSettingsSession session, string title)
        {
            _session = session;
            Text = title + " | Set Up";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(580, 380);
            Size = new Size(860, 490);
            Font = new Font("Segoe UI", 10);
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = ((_session as IInspectionTargetInfo) == null ? "" : "대상: " + ((IInspectionTargetInfo)_session).TargetDescription + "\r\n") + "카메라의 현재 허용 범위입니다. 단위는 레시피의 계산값과 동일합니다.\r\n생산 트리거를 멈춘 상태에서 적용하세요. 적용 시 검사 흐름을 일시 정지합니다." }, 0, 0);
            _grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false, BackgroundColor = Color.White };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "검사 항목", ReadOnly = true, FillWeight = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "조건 이름", ReadOnly = true, FillWeight = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Min", HeaderText = "최소값", FillWeight = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "최대값", FillWeight = 100 });
            _grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            layout.Controls.Add(_grid, 0, 1);
            _state = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(20, 63, 110), AutoEllipsis = true };
            layout.Controls.Add(_state, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
            _apply = new Button { Text = "적용 및 카메라 저장", AutoSize = true };
            var reload = new Button { Text = "카메라 값 다시 읽기", AutoSize = true };
            buttons.Controls.Add(close); buttons.Controls.Add(_apply); buttons.Controls.Add(reload);
            layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
            CancelButton = close;
            reload.Click += delegate { ReadCamera(); };
            _apply.Click += delegate { Apply(); };
            Shown += delegate { ReadCamera(); };
        }
        private void ReadCamera()
        {
            _apply.Enabled = false;
            _grid.CancelEdit();
            _grid.Rows.Clear();
            _ranges = null;
            _state.Text = "카메라에서 조건 목록과 허용 범위를 조회 중...";
            _state.Refresh();
            try
            {
                _ranges = _session.ReadRanges();
                _grid.Rows.Clear();
                foreach (var range in _ranges)
                    _grid.Rows.Add(range.Definition.Caption, range.Definition.Name, Format(range.Min, range.Definition.IsInteger), Format(range.Max, range.Definition.IsInteger));
                _state.Text = DateTime.Now.ToString("HH:mm:ss") + " 카메라 조회 완료 (" + _ranges.Count + "개)";
                _apply.Enabled = true;
            }
            catch (Exception ex) { ShowFailure(ex); }
        }
        private static string Format(double value, bool integer) { return value.ToString(integer ? "0" : "0.######", CultureInfo.CurrentCulture); }
        private static double Parse(object value, string caption)
        {
            double number;
            // Allow local decimal separators, without silently accepting thousands separators.
            if (!double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.CurrentCulture, out number) &&
                !double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new InvalidOperationException(caption + ": 숫자를 입력하세요.");
            return number;
        }
        private void Apply()
        {
            try
            {
                if (!_grid.EndEdit()) throw new InvalidOperationException("입력값을 확인하세요.");
                var requested = new List<ConditionRange>();
                for (int i = 0; i < _ranges.Count; i++)
                {
                    var definition = _ranges[i].Definition;
                    var row = _grid.Rows[i];
                    requested.Add(new ConditionRange(definition, Parse(row.Cells["Min"].Value, definition.Caption), Parse(row.Cells["Max"].Value, definition.Caption)));
                }
                Enabled = false;
                _state.Text = "카메라에 적용·확인·저장 중..."; _state.Refresh();
                InspectionSettingsController.Apply(_session, requested);
                ReadCamera();
                _state.Text = "카메라 적용 및 레시피 저장 완료";
            }
            catch (Exception ex) { ShowFailure(ex); }
            finally { Enabled = true; }
        }
        private void ShowFailure(Exception ex)
        {
            _state.Text = "실패: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Set Up", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
