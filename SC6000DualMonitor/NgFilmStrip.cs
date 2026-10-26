using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class NgFilmStrip : UserControl
    {
        private readonly FlowLayoutPanel _items = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 56, Text = "NG/ERROR 화면 대기", ForeColor = Color.White, Padding = new Padding(6) };
        private readonly NgHistoryStore _store;
        private readonly ToolTip _tip = new ToolTip();
        private bool _loaded;
        public Func<string> CaptureDiagnostics;
        public NgFilmStrip(NgHistoryStore store)
        {
            _store = store; Dock = DockStyle.Fill; BackColor = Color.FromArgb(33, 38, 42);
            var header = new Label { Text = "NG / ERROR · 최신순", Dock = DockStyle.Top, Height = 30, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            var folder = new Button { Text = "저장 폴더 열기", Dock = DockStyle.Bottom, Height = 28, BackColor = SystemColors.Control, ForeColor = Color.Black, UseVisualStyleBackColor = true };
            var diagnose = new Button { Text = "저장 상태 확인", Dock = DockStyle.Bottom, Height = 28, BackColor = SystemColors.Control, ForeColor = Color.Black, UseVisualStyleBackColor = true };
            diagnose.Click += delegate
            {
                try { MessageBox.Show(this, "화면 저장 폴더: " + _store.DirectoryPath + "\r\n현재 상태: " + _status.Text + "\r\n\r\n" +
                    (CaptureDiagnostics == null ? "" : CaptureDiagnostics()), "NG/ERROR 저장 상태"); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 상태 조회 실패"); }
            };
            folder.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(_store.DirectoryPath);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _store.DirectoryPath, UseShellExecute = true, Verb = "open" });
                    SetStatus("저장 폴더: " + _store.DirectoryPath);
                }
                catch (Exception ex)
                {
                    string message = "폴더 열기 실패: " + ex.Message + "\r\n경로: " + _store.DirectoryPath;
                    SetStatus(message); MessageBox.Show(this, message, "저장 폴더 열기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            Controls.Add(_items); Controls.Add(_status); Controls.Add(diagnose); Controls.Add(folder); Controls.Add(header);
            _items.SizeChanged += delegate { foreach (Control c in _items.Controls) c.Width = Math.Max(100, _items.ClientSize.Width - 24); };
        }
        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e); if (_loaded) return; _loaded = true;
            try
            {
                var records = await Task.Run(() =>
                {
                    var result = new System.Collections.Generic.List<Tuple<NgRecord, byte[]>>();
                    foreach (var record in _store.LoadRecent(100))
                    {
                        try { using (var stream = File.OpenRead(record.ImagePath)) result.Add(Tuple.Create(record, PrepareThumbnail(stream))); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("NG thumbnail: " + ex); result.Add(Tuple.Create(record, (byte[])null)); }
                    }
                    return result;
                });
                if (IsDisposed) return;
                foreach (var record in records)
                    if (record.Item2 != null) AddRecord(record.Item1, record.Item2);
                    else SetStatus("일부 NG 기록의 이미지를 읽을 수 없습니다.");
            }
            catch (Exception ex) { if (!IsDisposed) SetStatus("NG/ERROR 기록 읽기 실패: " + ex.Message); }
        }
        public void SetStatus(string text) { if (IsDisposed) return; _status.Text = text; _tip.SetToolTip(_status, text); }
        // Full-size decoding/scaling belongs on a worker, not the camera display thread.
        public static byte[] PrepareThumbnail(Stream stream)
        {
            using (var source = Image.FromStream(stream))
            using (var thumbnail = new Bitmap(170, 115))
            {
                using (var g = Graphics.FromImage(thumbnail))
                {
                    g.Clear(Color.Black); double scale = Math.Min(170.0 / source.Width, 115.0 / source.Height);
                    int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
                    g.DrawImage(source, (170 - w) / 2, (115 - h) / 2, w, h);
                }
                using (var output = new MemoryStream())
                { thumbnail.Save(output, System.Drawing.Imaging.ImageFormat.Png); return output.ToArray(); }
            }
        }
        public void AddRecord(NgRecord record)
        {
            try { using (var stream = File.OpenRead(record.ImagePath)) AddRecord(record, PrepareThumbnail(stream)); }
            catch (Exception ex) { SetStatus("NG 이미지 읽기 실패: " + ex.Message); }
        }
        public void AddRecord(NgRecord record, byte[] preview)
        {
            if (IsDisposed) return;
            foreach (Control item in _items.Controls) if (ReferenceEquals(item.Tag, record) || (!string.IsNullOrEmpty(record.ImagePath) && ((NgRecord)item.Tag).ImagePath == record.ImagePath)) return;
            Image thumbnail;
            using (var stream = new MemoryStream(preview, false))
            using (var source = Image.FromStream(stream)) thumbnail = new Bitmap(source);
            var panel = new Panel { Width = Math.Max(100, _items.ClientSize.Width - 24), Height = 151, Tag = record, Margin = new Padding(4), BackColor = Color.FromArgb(65, 45, 45) };
            var picture = new PictureBox { Dock = DockStyle.Fill, Image = thumbnail, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand };
            var caption = new Label { Text = record.Outcome + " · " + record.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") + "\r\n" + Path.GetFileName(record.Recipe ?? ""), Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            EventHandler open = delegate
            {
                try { new NgImageWindow(record).Show(); }
                catch (Exception ex) { SetStatus("이미지 열기 실패: " + ex.Message); }
            };
            picture.DoubleClick += open; caption.DoubleClick += open;
            panel.Disposed += delegate { thumbnail.Dispose(); };
            panel.Controls.Add(picture); panel.Controls.Add(caption);
            _tip.SetToolTip(picture, record.Description + "\r\n더블클릭: 확대");
            int index = 0;
            foreach (Control item in _items.Controls) if (((NgRecord)item.Tag).TimeUtc > record.TimeUtc) index++;
            _items.SuspendLayout();
            try
            {
                _items.Controls.Add(panel); _items.Controls.SetChildIndex(panel, index);
                while (_items.Controls.Count > 100) { var last = _items.Controls[_items.Controls.Count - 1]; _items.Controls.Remove(last); last.Dispose(); }
            }
            finally { _items.ResumeLayout(true); }
            if (index == 0) _items.ScrollControlIntoView(panel);
        }
        protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class NgImageWindow : Form
    {
        private Image _image;
        private readonly NgRecord _record;
        private readonly TaskCompletionSource<bool> _closed = new TaskCompletionSource<bool>();
        private readonly Label _loading = new Label { Dock = DockStyle.Fill, Text = "이미지를 준비하고 있습니다. 잠시 기다려 주세요.", ForeColor = Color.White, BackColor = Color.Black, TextAlign = ContentAlignment.MiddleCenter };
        private readonly PictureBox _picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Black };
        private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Black };
        private double _zoom = 1;
        public NgImageWindow(NgRecord record)
        {
            Text = record.Outcome + " 저장 화면 — " + record.Camera; Width = 1100; Height = 800; StartPosition = FormStartPosition.CenterScreen;
            _record = record;
            _scroll.Controls.Add(_picture); _scroll.Controls.Add(_loading); _loading.BringToFront();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
            foreach (var text in new[] { "화면 맞춤", "1:1", "+", "−" })
            {
                var button = new Button { Text = text, AutoSize = true };
                button.Click += delegate
                {
                    if (_image == null) return;
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
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var ready = _record.Ready;
                if (await Task.WhenAny(ready, _closed.Task) != ready || IsDisposed || Disposing) return;
                if (!await ready) throw new IOException(_record.SaveError ?? "NG 이미지 저장에 실패했습니다.");
                var image = await Task.Run(() =>
                {
                    using (var stream = File.OpenRead(_record.ImagePath))
                    using (var source = Image.FromStream(stream)) return new Bitmap(source);
                });
                if (IsDisposed || Disposing) { image.Dispose(); return; }
                _image = image; _picture.Image = image; _loading.Visible = false;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing) _loading.Text = "이미지를 열 수 없습니다.\r\n" + ex.Message;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _closed.TrySetResult(true); _picture.Image = null; if (_image != null) { _image.Dispose(); _image = null; } }
            base.Dispose(disposing);
        }
    }
}
