using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

namespace SC6000DualMonitor
{
    internal sealed class CsvModuleMapping
    {
        public readonly string[] ModuleNames;
        public CsvModuleMapping(IEnumerable<string> names)
        {
            ModuleNames = (names ?? Enumerable.Empty<string>()).ToArray();
            if (ModuleNames.Length != 4 || ModuleNames.Any(string.IsNullOrWhiteSpace) ||
                ModuleNames.Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidOperationException("Top, Middle, Bottom, Circle의 서로 다른 모듈을 4개 선택하세요.");
        }
        public void Validate(IEnumerable<string> available)
        {
            var set = new HashSet<string>(available, StringComparer.Ordinal);
            var missing = ModuleNames.Where(n => !set.Contains(n)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("현재 레시피에서 CSV 모듈을 찾을 수 없습니다: " + string.Join(", ", missing) +
                    ". 모듈 이름을 변경했다면 CSV 모듈 설정을 다시 선택하세요.");
        }
    }

    internal sealed class CsvMappingStore
    {
        private readonly string _file;
        public CsvMappingStore(string ip)
        {
            string safeIp = new string((ip ?? "camera").Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
            _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WILLB", "SC6000Monitor", "CsvHistory", safeIp, "modules.txt");
        }
        public CsvModuleMapping Read()
        {
            try
            {
                if (!File.Exists(_file)) return null;
                var names = File.ReadAllLines(_file, Encoding.UTF8);
                return new CsvModuleMapping(names);
            }
            catch { return null; }
        }
        public void Write(CsvModuleMapping mapping)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            File.WriteAllLines(_file, mapping.ModuleNames, new UTF8Encoding(false));
        }
    }

    internal sealed class CsvMappingForm : Form
    {
        private readonly ComboBox[] _choices = new ComboBox[4];
        public CsvModuleMapping Selection { get; private set; }
        public CsvMappingForm(IEnumerable<string> modules, CsvModuleMapping existing)
        {
            Text = "CSV 측정값 모듈 연결";
            Size = new Size(520, 310);
            MinimumSize = new Size(500, 290);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10);
            var names = modules.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(12) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var explain = new Label { Text = "VisionMaster 이름과 관계없이 각 측정 항목에 연결할 모듈 선택", Dock = DockStyle.Fill };
            table.Controls.Add(explain, 0, 0); table.SetColumnSpan(explain, 2);
            string[] labels = { "Top", "Middle", "Bottom", "Circle" };
            for (int i = 0; i < 4; i++)
            {
                table.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i + 1);
                var choice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                choice.Items.AddRange(names);
                if (existing != null && names.Contains(existing.ModuleNames[i])) choice.SelectedItem = existing.ModuleNames[i];
                _choices[i] = choice;
                table.Controls.Add(choice, 1, i + 1);
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "저장", AutoSize = true };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 0, 5); table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += delegate
            {
                try
                {
                    Selection = new CsvModuleMapping(_choices.Select(c => c.SelectedItem as string));
                    Selection.Validate(names);
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 모듈 선택", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
        }
    }
}
