using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    // CSV column labels remain fixed even when the user renames modules in VisionMaster.
    internal sealed class CsvModuleMapping
    {
        public static readonly string[] ColumnNames = { "Top", "Middle", "Bottom", "Circle" };
        public string[] ModuleNames { get; private set; }
        public CsvModuleMapping(IEnumerable<string> names)
        {
            ModuleNames = names == null ? new string[4] : names.Select(s => (s ?? "").Trim()).ToArray();
            if (ModuleNames.Length != 4)
                throw new InvalidOperationException("CSV 컬럼 매핑은 Top, Middle, Bottom, Circle 4개여야 합니다.");
        }
        public bool Complete { get { return ModuleNames.All(s => !string.IsNullOrWhiteSpace(s)); } }
        public void Validate(IEnumerable<string> available)
        {
            if (!Complete) throw new InvalidOperationException("CSV 항목 설정에서 Top/Middle/Bottom/Circle 모듈을 선택하세요.");
            if (ModuleNames.Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidOperationException("같은 모듈을 두 개 이상의 CSV 컬럼에 지정할 수 없습니다.");
            var modules = new HashSet<string>(available, StringComparer.Ordinal);
            var missing = ModuleNames.Where(n => !modules.Contains(n)).ToArray();
            if (missing.Length != 0)
                throw new InvalidOperationException("현재 레시피에서 CSV 연결 모듈을 찾을 수 없습니다: " +
                    string.Join(", ", missing) + ". 이름 변경 후 CSV 항목 설정을 다시 지정하세요.");
        }
        public static CsvModuleMapping Suggest(IEnumerable<string> available)
        {
            string[] all = available.ToArray();
            string[] guesses = new string[4];
            for (int i = 0; i < ColumnNames.Length; i++)
            {
                string column = ColumnNames[i];
                string[] matches = all.Where(n => n.IndexOf(column, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                if (matches.Length == 1) guesses[i] = matches[0];
            }
            return new CsvModuleMapping(guesses);
        }
    }

    internal sealed class CsvModuleMappingStore
    {
        private readonly string _directory;
        public CsvModuleMappingStore(string camera)
        {
            var name = new string((camera ?? "camera").Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
            _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WILLB", "SC6000Monitor", "CsvHistory", name, "Mappings");
        }
        private string PathFor(string procedure, string recipe)
        {
            string key = (procedure ?? "") + "\n" + (recipe ?? "");
            using (var sha = SHA256.Create())
                return Path.Combine(_directory,
                    BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".txt");
        }
        public CsvModuleMapping Read(string procedure, string recipe)
        {
            try
            {
                string file = PathFor(procedure, recipe);
                if (!File.Exists(file)) return null;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                if (lines.Length != 4) return null;
                return new CsvModuleMapping(lines.Select(s => Encoding.UTF8.GetString(Convert.FromBase64String(s))));
            }
            catch { return null; }
        }
        public void Write(string procedure, string recipe, CsvModuleMapping mapping)
        {
            Directory.CreateDirectory(_directory);
            string file = PathFor(procedure, recipe);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, mapping.ModuleNames.Select(s =>
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(s))).ToArray(), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class CsvMappingForm : Form
    {
        private readonly ComboBox[] _choices = new ComboBox[4];
        public CsvModuleMapping SelectedMapping { get; private set; }

        public CsvMappingForm(IEnumerable<string> moduleNames, CsvModuleMapping current)
        {
            var options = moduleNames.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            Text = "CSV 항목 설정 - 실제 Format 모듈 선택";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(630, 370);
            MinimumSize = new Size(530, 330);
            Font = new Font("Segoe UI", 10);
            ShowInTaskbar = false;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(15) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            for (int i = 0; i < 4; i++)
            {
                panel.Controls.Add(new Label { Text = CsvModuleMapping.ColumnNames[i], Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft }, 0, i);
                var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                combo.Items.Add("");
                combo.Items.AddRange(options);
                string initial = current == null ? "" : current.ModuleNames[i];
                combo.SelectedItem = options.Contains(initial, StringComparer.Ordinal) ? initial : "";
                _choices[i] = combo;
                panel.Controls.Add(combo, 1, i);
            }
            var note = new Label { Dock = DockStyle.Fill, AutoSize = false,
                Text = "VisionMaster에서 모듈 이름을 바꾸어도 여기서 다시 선택하면 됩니다.\r\n" +
                       "각 컬럼에 실제 해당 치수를 출력하는 모듈을 지정하세요.",
                ForeColor = Color.FromArgb(65, 75, 85) };
            panel.Controls.Add(note, 0, 4);
            panel.SetColumnSpan(note, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "적용", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            panel.Controls.Add(buttons, 0, 5);
            panel.SetColumnSpan(buttons, 2);
            Controls.Add(panel);
            AcceptButton = save;
            CancelButton = cancel;
            save.Click += delegate
            {
                try
                {
                    var mapping = new CsvModuleMapping(_choices.Select(c => c.SelectedItem as string));
                    mapping.Validate(options);
                    SelectedMapping = mapping;
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "CSV 항목 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }
    }
}
