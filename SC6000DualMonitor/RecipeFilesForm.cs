using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class RecipeFilesForm : Form
    {
        private readonly RecipeFileManager _manager;
        private readonly string _camera;
        private readonly Action<string> _log;
        private readonly Label _current = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _folder = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly DataGridView _grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly Button _refresh = Button("새로고침"), _rename = Button("이름 변경"), _delete = Button("선택 파일 삭제"), _close = Button("닫기");
        private RecipeFileSnapshot _snapshot;
        private bool _busy;
        public bool Changed { get; private set; }
        private static Button Button(string text) { return new Button { Text = text, AutoSize = true, MinimumSize = new Size(112, 42), Padding = new Padding(8, 4, 8, 4) }; }
        public RecipeFilesForm(RecipeFileManager manager, string camera, Action<string> log = null)
        {
            _manager = manager; _camera = camera; _log = log;
            Text = camera + " | 카메라 레시피 파일 관리";
            Font = new Font("맑은 고딕", 11); StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 580); MinimumSize = new Size(760, 500); ShowInTaskbar = false; MinimizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 38, 28, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            _current.Font = new Font(Font, FontStyle.Bold); _current.Text = "현재 사용 중: 확인 중…";
            layout.Controls.Add(_current, 0, 0); layout.Controls.Add(_folder, 0, 1);
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(new Label { Text = "파일명 검색", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); searchRow.Controls.Add(_search, 1, 0);
            layout.Controls.Add(searchRow, 0, 2);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "레시피 파일명", FillWeight = 60, MinimumWidth = 210 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "상태", FillWeight = 20, MinimumWidth = 135 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "크기", FillWeight = 20, MinimumWidth = 90 });
            foreach (DataGridViewColumn column in _grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            _grid.RowTemplate.Height = 38; _grid.ColumnHeadersHeight = 38; _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            layout.Controls.Add(_grid, 0, 3);
            layout.Controls.Add(new Label { Text = "파일을 선택한 뒤 아래 버튼을 누르세요. ‘현재 사용 중’ 파일은 보호됩니다.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray }, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { _close, _delete, _rename, _refresh }); layout.Controls.Add(buttons, 0, 6);
            Controls.Add(layout); CancelButton = _close;
            _search.TextChanged += delegate { ShowFiles(); };
            _grid.SelectionChanged += delegate { UpdateButtons(); };
            _close.Click += delegate { Close(); };
            _refresh.Click += async delegate { await RefreshAsync(); };
            _delete.Click += async delegate { await DeleteAsync(); };
            _rename.Click += async delegate { await RenameAsync(); };
            Shown += async delegate { await RefreshAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (_busy) { e.Cancel = true; _status.Text = "카메라 요청 처리 중입니다. 잠시 기다려 주세요."; } };
            UpdateButtons();
        }
        private RecipeFile Selected { get { return _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as RecipeFile : null; } }
        private void UpdateButtons()
        {
            var selected = Selected;
            bool allowed = !_busy && _snapshot != null && selected != null && !RecipeFilePaths.Same(selected.Path, _snapshot.Current);
            _delete.Enabled = _rename.Enabled = allowed;
            _refresh.Enabled = _close.Enabled = _search.Enabled = _grid.Enabled = !_busy;
        }
        private void ShowFiles()
        {
            _grid.Rows.Clear();
            if (_snapshot != null)
            {
                var files = _snapshot.Files.Where(f => f.Name.IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var file in files)
                {
                    bool current = RecipeFilePaths.Same(file.Path, _snapshot.Current);
                    int row = _grid.Rows.Add(file.Name, current ? "현재 사용 중" : "", file.Size < 0 ? "—" : (file.Size / 1024d).ToString("N1") + " KB");
                    _grid.Rows[row].Tag = file;
                    _grid.Rows[row].Cells[0].ToolTipText = file.Path;
                    if (current) _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(229, 244, 235);
                }
                _status.Text = _grid.Rows.Count + "개 표시 / 전체 " + _snapshot.Files.Length + "개";
            }
            _grid.ClearSelection(); UpdateButtons();
        }
        internal Task RefreshAsync() { return RunAsync(null, "목록을 불러오는 중입니다…"); }
        private async Task RunAsync(Func<string> operation, string waiting)
        {
            if (_busy) return;
            _busy = true; _status.ForeColor = SystemColors.ControlText; _status.Text = waiting; UpdateButtons();
            string completed = null;
            try
            {
                _snapshot = await Task.Run(() => { if (operation != null) completed = operation(); return _manager.Refresh(); });
                _current.Text = "현재 사용 중: " + RecipeFilePaths.Name(_snapshot.Current);
                _folder.Text = "카메라 폴더: " + _snapshot.Directory;
                ShowFiles();
                if (completed != null) _status.Text = completed + " · 전체 " + _snapshot.Files.Length + "개";
            }
            catch (Exception ex)
            {
                _snapshot = null; _grid.Rows.Clear(); _current.Text = "현재 사용 중: 다시 확인 필요";
                _status.ForeColor = Color.Firebrick;
                _status.Text = completed == null ? "처리 결과를 확인하세요. 새로고침 후 다시 선택할 수 있습니다." : completed + " · 목록 재조회 실패";
                if (_log != null) _log(ex.ToString());
                MessageBox.Show(this, (completed == null ? "" : completed + "\r\n\r\n") + ex.Message +
                    (operation == null ? "" : "\r\n\r\n카메라 목록을 새로고침하여 결과를 확인하세요."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _busy = false; UpdateButtons(); }
        }
        private async Task DeleteAsync()
        {
            var file = Selected; if (file == null || !_delete.Enabled) return;
            if (MessageBox.Show(this, _camera + "\r\n\r\n" + file.Name + "\r\n\r\n이 파일을 카메라에서 영구 삭제할까요? 삭제 후에는 되돌릴 수 없습니다.",
                "레시피 파일 삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Changed = true;
            await RunAsync(() => { _manager.Delete(file.Path); return "삭제 완료: " + file.Name; }, "파일을 삭제하는 중입니다…");
        }
        private async Task RenameAsync()
        {
            var file = Selected; if (file == null || !_rename.Enabled) return;
            using (var dialog = new RecipeRenameForm(_camera, file.Name))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string stem = dialog.NewName; Changed = true;
                await RunAsync(() => "이름 변경 완료: " + RecipeFilePaths.Name(_manager.Rename(file.Path, stem)), "파일 이름을 변경하는 중입니다…");
            }
        }
    }
    internal sealed class RecipeRenameForm : Form
    {
        private readonly TextBox _name = new TextBox { Dock = DockStyle.Fill };
        public string NewName { get { return _name.Text; } }
        public RecipeRenameForm(string camera, string fileName)
        {
            Text = "레시피 이름 변경"; Font = new Font("맑은 고딕", 11); ClientSize = new Size(620, 285);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            foreach (int height in new[] { 58, 30, 42, 54, 45 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = camera + "\r\n현재 이름: " + fileName, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            layout.Controls.Add(new Label { Text = "새 이름 (확장자는 자동으로 유지됩니다)", Dock = DockStyle.Fill }, 0, 1);
            _name.Text = fileName.Substring(0, fileName.LastIndexOf('.')); layout.Controls.Add(_name, 0, 2);
            layout.Controls.Add(new Label { Text = "이름 변경 후 뷰어의 모델명·치수 보정·CSV 항목 연결을\r\n다시 지정해야 할 수 있습니다.", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 36 };
            var apply = new Button { Text = "이름 변경", AutoSize = true, Height = 36 };
            buttons.Controls.Add(cancel); buttons.Controls.Add(apply); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            CancelButton = cancel; AcceptButton = apply;
            apply.Click += delegate { try { RecipeFilePaths.Renamed("/" + fileName, _name.Text); DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            Shown += delegate { _name.Focus(); _name.SelectAll(); };
        }
    }
}
