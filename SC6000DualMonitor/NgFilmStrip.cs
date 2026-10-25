using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class NgFilmStrip : UserControl
    {
        private readonly FlowLayoutPanel _items = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 56, Text = "NG 화면 대기", ForeColor = Color.White, Padding = new Padding(6) };
        private readonly NgHistoryStore _store;
        private readonly ToolTip _tip = new ToolTip();
        private bool _loaded;
        private readonly ComboBox _mode = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _refresh = new Button { Dock = DockStyle.Top, Height = 28, Text = "목록 새로고침", BackColor = SystemColors.Control, ForeColor = Color.Black, UseVisualStyleBackColor = true };
        private readonly Timer _refreshTimer = new Timer { Interval = 5000 };
        private bool _changing, _reading, _busy;
        private string _rawRoot, _camera;
        private int _generation;
        public bool ReplayMode { get; private set; }
        public Func<bool, bool> ChangeMode;
        public event Action<NgRecord> ReplayRequested;
        public void ConfigureOriginals(string root, string camera) { _rawRoot = root; _camera = camera; }
        public void SetBusy(bool busy) { _busy = busy; _items.Enabled = _mode.Enabled = _refresh.Enabled = !busy; }
        public void ResetMode()
        {
            _changing = true; ReplayMode = false; _mode.SelectedIndex = 0; _changing = false;
            _generation++; ClearItems(); ReloadRecords();
        }
        public NgFilmStrip(NgHistoryStore store)
        {
            _store = store; Dock = DockStyle.Fill; BackColor = Color.FromArgb(33, 38, 42);
            var header = new Label { Text = "NG 기록 · 최신순", Dock = DockStyle.Top, Height = 30, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            var folder = new Button { Text = "저장 폴더 열기", Dock = DockStyle.Bottom, Height = 28, BackColor = SystemColors.Control, ForeColor = Color.Black, UseVisualStyleBackColor = true };
            folder.Click += delegate
            {
                try
                {
                    string directory = ReplayMode ? _rawRoot : _store.DirectoryPath;
                    if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("RAW_IMAGE_ROOT를 설정하세요.");
                    if (!ReplayMode) Directory.CreateDirectory(directory);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + directory + "\"");
                }
                catch (Exception ex) { SetStatus("폴더 열기 실패: " + ex.Message); }
            };
            _mode.Items.AddRange(new object[] { "뷰어 모드", "재검사 모드" }); _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += delegate
            {
                if (_changing) return;
                bool requested = _mode.SelectedIndex == 1;
                if (ChangeMode == null || !ChangeMode(requested))
                { _changing = true; _mode.SelectedIndex = ReplayMode ? 1 : 0; _changing = false; return; }
                ReplayMode = requested; _generation++; ClearItems(); ReloadRecords();
            };
            _refresh.Click += delegate { ReloadRecords(); };
            _refreshTimer.Tick += delegate { if (ReplayMode && !_busy) ReloadRecords(); };
            Controls.Add(_items); Controls.Add(_status); Controls.Add(folder); Controls.Add(_refresh); Controls.Add(_mode); Controls.Add(header);
            _items.SizeChanged += delegate { foreach (Control c in _items.Controls) c.Width = Math.Max(100, _items.ClientSize.Width - 24); };
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); if (_loaded) return; _loaded = true;
            ReloadRecords(); _refreshTimer.Start();
        }
        private void ClearItems()
        {
            while (_items.Controls.Count > 0) { var item = _items.Controls[0]; _items.Controls.Remove(item); item.Dispose(); }
        }
        private async void ReloadRecords()
        {
            if (_reading || IsDisposed) return;
            _reading = true; int generation = _generation; bool replay = ReplayMode;
            try
            {
                var records = await Task.Run(() => replay ? FtpOriginals.LoadRecent(_rawRoot, _camera, 100) : _store.LoadRecent(100));
                if (IsDisposed || generation != _generation) return;
                if (replay && _items.Controls.Count == records.Count && _items.Controls.Cast<Control>().Select(c => (NgRecord)c.Tag)
                    .Zip(records, (a, b) => a.ImagePath == b.ImagePath && a.TimeUtc == b.TimeUtc && a.FileLength == b.FileLength).All(same => same))
                {
                    if (records.Count == 0) SetStatus("FTP 원본 없음\r\n연도\\월\\일\\NG 폴더를 확인하세요.");
                    return; // Keep scroll position and result/error text when FTP contents have not changed.
                }
                if (replay) ClearItems();
                foreach (var record in records) AddRecord(record);
                if (replay) SetStatus("FTP 원본 " + records.Count + "개 · 최신순\r\n한 번 클릭: 현재 레시피로 재검사");
            }
            catch (Exception ex) { if (!IsDisposed) SetStatus("NG 기록 읽기 실패: " + ex.Message); }
            finally { _reading = false; if (!IsDisposed && generation != _generation) ReloadRecords(); }
        }
        public void SetStatus(string text) { if (IsDisposed) return; _status.Text = text; _tip.SetToolTip(_status, text); }
        public void AddRecord(NgRecord record)
        {
            if (record.IsOriginal != ReplayMode) return;
            foreach (Control item in _items.Controls) if (((NgRecord)item.Tag).ImagePath == record.ImagePath) return;
            Image thumbnail;
            try
            {
                using (var stream = File.OpenRead(record.ImagePath))
                using (var source = Image.FromStream(stream))
                {
                    thumbnail = new Bitmap(170, 115);
                    using (var g = Graphics.FromImage(thumbnail))
                    {
                        g.Clear(Color.Black); double scale = Math.Min(170.0 / source.Width, 115.0 / source.Height);
                        int w = (int)(source.Width * scale), h = (int)(source.Height * scale);
                        g.DrawImage(source, (170 - w) / 2, (115 - h) / 2, w, h);
                    }
                }
            }
            catch (Exception ex) { SetStatus("NG 이미지 읽기 실패: " + ex.Message); return; }
            var panel = new Panel { Width = Math.Max(100, _items.ClientSize.Width - 24), Height = 151, Tag = record, Margin = new Padding(4), BackColor = Color.FromArgb(65, 45, 45) };
            var picture = new PictureBox { Dock = DockStyle.Fill, Image = thumbnail, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand };
            var caption = new Label { Text = record.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") + "\r\n" + Path.GetFileName(record.IsOriginal ? record.ImagePath : record.Recipe ?? ""), Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
            EventHandler open = delegate
            {
                if (ReplayMode) return;
                try { new NgImageWindow(record).Show(); }
                catch (Exception ex) { SetStatus("이미지 열기 실패: " + ex.Message); }
            };
            picture.DoubleClick += open; caption.DoubleClick += open;
            EventHandler replayClick = delegate { if (ReplayMode && !_busy) ReplayRequested?.Invoke(record); };
            picture.Click += replayClick; caption.Click += replayClick;
            panel.Disposed += delegate { thumbnail.Dispose(); };
            panel.Controls.Add(picture); panel.Controls.Add(caption);
            _tip.SetToolTip(picture, record.IsOriginal ? record.ImagePath + "\r\n클릭: 재검사" : record.Description + "\r\n더블클릭: 확대");
            int index = 0;
            foreach (Control item in _items.Controls)
            {
                var other = (NgRecord)item.Tag;
                if (other.TimeUtc > record.TimeUtc || (other.TimeUtc == record.TimeUtc && string.Compare(other.ImagePath, record.ImagePath, StringComparison.OrdinalIgnoreCase) < 0)) index++;
            }
            _items.Controls.Add(panel); _items.Controls.SetChildIndex(panel, index);
            while (_items.Controls.Count > 100) { var last = _items.Controls[_items.Controls.Count - 1]; _items.Controls.Remove(last); last.Dispose(); }
            if (index == 0) _items.ScrollControlIntoView(panel);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _refreshTimer.Dispose(); _tip.Dispose(); } base.Dispose(disposing); }
    }

    internal sealed class NgImageWindow : Form
    {
        private readonly Image _image;
        private readonly PictureBox _picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Black };
        private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Black };
        private double _zoom = 1;
        public NgImageWindow(NgRecord record) : this(record, File.ReadAllBytes(record.ImagePath), null) { }
        public NgImageWindow(NgRecord record, byte[] png, string result)
        {
            Text = (result == null ? "NG 저장 화면 — " : "재검사 결과 — " + result + " — ") + record.Camera; Width = 1100; Height = 800; StartPosition = FormStartPosition.CenterScreen;
            using (var stream = new MemoryStream(png)) using (var source = Image.FromStream(stream)) _image = new Bitmap(source);
            _picture.Image = _image; _scroll.Controls.Add(_picture);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
            foreach (var text in new[] { "화면 맞춤", "1:1", "+", "−" })
            {
                var button = new Button { Text = text, AutoSize = true };
                button.Click += delegate
                {
                    if (text == "화면 맞춤") { _picture.Dock = DockStyle.Fill; _picture.SizeMode = PictureBoxSizeMode.Zoom; return; }
                    _zoom = text == "1:1" ? 1 : Math.Max(.1, Math.Min(8, _zoom * (text == "+" ? 1.25 : .8)));
                    _picture.Dock = DockStyle.None; _picture.SizeMode = PictureBoxSizeMode.StretchImage;
                    _picture.Size = new Size((int)(_image.Width * _zoom), (int)(_image.Height * _zoom));
                };
                bar.Controls.Add(button);
            }
            Controls.Add(_scroll); Controls.Add(bar);
            Controls.Add(new Label { Text = record.Description, Dock = DockStyle.Top, Height = 65, Padding = new Padding(6), AutoEllipsis = true });
        }
        protected override void Dispose(bool disposing) { if (disposing) { _picture.Image = null; _image.Dispose(); } base.Dispose(disposing); }
    }
}
