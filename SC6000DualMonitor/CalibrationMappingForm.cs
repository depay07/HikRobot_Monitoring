using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GlobalVariableModuleCs;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationMappingForm : Form
    {
        private readonly CalibrationCatalog _catalog;
        private readonly ComboBox _global = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
        private readonly TextBox[] _labels = new TextBox[4];
        private readonly ComboBox[] _variables = new ComboBox[4], _sources = new ComboBox[4];
        private readonly Label _status = new Label { Dock = DockStyle.Fill, ForeColor = Color.DarkRed };
        public CalibrationMapping Mapping { get; private set; }
        public CalibrationMappingForm(CalibrationCatalog catalog, CalibrationMapping previous, string error)
        {
            _catalog = catalog;
            Text = "치수 보정 · 항목 연결 (레시피별)";
            ClientSize = new Size(1040, 460); MinimumSize = new Size(980, 460);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10); MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            foreach (int height in new[] { 95, 36, 190, 65, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = catalog.Camera + " | " + catalog.Procedure + " | " + catalog.Recipe +
                "\r\nVisionMaster에서 float 단일 값 전역 변수 4개를 만들고 기존 보정계수를 초기값으로 넣으세요." +
                "\r\n보정할 항목의 표시 이름, 전역 변수, 원본 픽셀 측정 모듈을 연결하세요." +
                "\r\n계수는 이 레시피에 저장됩니다. 여기서는 표시 이름과 연결만 지정합니다. 자세한 준비: CALIBRATION.md" }, 0, 0);
            layout.Controls.Add(_global, 0, 1);
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            foreach (float width in new float[] { 24, 25, 51 }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            for (int i = 0; i < 5; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            string[] headings = { "화면 표시 이름 (수정 가능)", "저장할 전역 변수", "원본 픽셀 측정 모듈" };
            for (int i = 0; i < headings.Length; i++) table.Controls.Add(new Label { Text = headings[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, i, 0);
            for (int i = 0; i < 4; i++)
            {
                var old = previous != null && previous.Rows != null && previous.Rows.Length == 4 ? previous.Rows[i] : null;
                _labels[i] = new TextBox { Text = old == null ? "측정 " + (i + 1) : old.Label, Dock = DockStyle.Fill };
                _variables[i] = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                _sources[i] = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 650 };
                _sources[i].Items.AddRange(catalog.Sources.Cast<object>().ToArray());
                if (old != null) _sources[i].SelectedItem = catalog.Sources.FirstOrDefault(s => s.Module.ID == old.SourceId && s.Module.Name == old.SourceName && s.Field == old.Field);
                table.Controls.Add(_labels[i], 0, i + 1); table.Controls.Add(_variables[i], 1, i + 1); table.Controls.Add(_sources[i], 2, i + 1);
            }
            layout.Controls.Add(table, 0, 2); layout.Controls.Add(_status, 0, 3);
            _global.SelectedIndexChanged += delegate
            {
                try
                {
                    var names = CalibrationCatalog.Variables((GlobalVariableModuleTool)_global.SelectedItem);
                    string[] defaults = { "top_scale", "middle_scale", "bottom_scale", "circle_scale" };
                    for (int i = 0; i < 4; i++)
                    {
                        _variables[i].Items.Clear(); _variables[i].Items.AddRange(names);
                        _variables[i].SelectedItem = previous != null && previous.Rows != null && previous.Rows.Length == 4 ? previous.Rows[i].Variable : defaults[i];
                    }
                    _status.Text = names.Length < 4 ? "float 전역 변수가 4개 이상 필요합니다. VisionMaster에서 준비 후 이 창을 다시 여세요." : error;
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            _global.Items.AddRange(catalog.Globals.Cast<object>().ToArray());
            _global.SelectedItem = catalog.Globals.FirstOrDefault(g => previous != null && g.ID == previous.GlobalModuleId && g.Name == previous.GlobalModuleName);
            if (_global.SelectedIndex < 0 && _global.Items.Count > 0) _global.SelectedIndex = 0;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            var ok = new Button { Text = "연결 확인 및 계속", AutoSize = true };
            ok.Click += delegate
            {
                try
                {
                    var global = _global.SelectedItem as GlobalVariableModuleTool;
                    if (global == null || _sources.Any(c => c.SelectedItem == null) || _variables.Any(c => c.SelectedItem == null))
                        throw new InvalidOperationException("네 항목의 변수와 픽셀 측정 모듈을 모두 선택하세요.");
                    Mapping = new CalibrationMapping { Camera = catalog.Camera, Procedure = catalog.Procedure, Recipe = catalog.Recipe,
                        GlobalModuleId = global.ID, GlobalModuleName = global.Name,
                        Rows = Enumerable.Range(0, 4).Select(i => { var source = (CalibrationSource)_sources[i].SelectedItem;
                            return new CalibrationMappingRow { Label = _labels[i].Text.Trim(), Variable = (string)_variables[i].SelectedItem,
                                SourceId = source.Module.ID, SourceName = source.Module.Name, Field = source.Field }; }).ToArray() };
                    catalog.Resolve(Mapping); DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok); layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout); CancelButton = cancel; AcceptButton = ok;
        }
    }
    internal static class CalibrationWorkflow
    {
        public static void Show(IWin32Window owner, RemoteInspectionSession session, string title, Func<System.Threading.Tasks.Task> refreshGraphics = null)
        {
            var catalog = session.CalibrationCatalog();
            CalibrationMapping mapping = null;
            bool configure = false;
            string error = "";
            try
            {
                mapping = CalibrationMappingStore.Load(catalog.Camera, catalog.Procedure, catalog.Recipe);
                if (mapping != null) catalog.Resolve(mapping);
            }
            catch (Exception ex) { configure = true; error = ex.Message; }
            while (true)
            {
                if (configure || mapping == null)
                {
                    using (var chooser = new CalibrationMappingForm(catalog, mapping, error))
                    {
                        if (chooser.ShowDialog(owner) != DialogResult.OK) return;
                        mapping = chooser.Mapping;
                    }
                }
                RemoteCalibrationSession target;
                try { target = session.OpenCalibration(catalog, mapping); }
                catch (Exception ex)
                {
                    // Allow correcting a stale/wrong mapping even if it cannot open an editor.
                    error = ex.Message; configure = true;
                    var fresh = session.CalibrationCatalog();
                    if (fresh.Recipe != catalog.Recipe) throw new InvalidOperationException("레시피가 변경되었습니다. 치수 보정 창을 다시 여세요.", ex);
                    catalog = fresh;
                    continue;
                }
                using (target)
                {
                    CalibrationMappingStore.Save(mapping);
                    using (var form = new CalibrationForm(target, title, refreshGraphics))
                        if (form.ShowDialog(owner) != DialogResult.Retry) return;
                }
                configure = true; error = "";
                var next = session.CalibrationCatalog();
                if (next.Recipe != catalog.Recipe) throw new InvalidOperationException("레시피가 변경되었습니다. 치수 보정 창을 다시 여세요.");
                catalog = next;
            }
        }
    }
}
