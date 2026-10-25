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
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 56, Text = "NG 화면 대기", ForeColor = Color.White, Padding = new Padding(6) };
        private readonly NgHistoryStore _store;
        private readonly ToolTip _tip = new ToolTip();
        private bool _loaded;
        public NgFilmStrip(NgHistoryStore store)
        {
            _store = store; Dock = DockStyle.Fill; BackColor = Color.FromArgb(33, 38, 42);
            var header = new Label { Text = "NG 기록 · 최신순", Dock = DockStyle.Top, Height = 30, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            var folder = new Button { Text = "저장 폴더 열기", Dock = DockStyle.Bottom, Height = 28 };
            folder.Click += delegate
            {
                try { Directory.CreateDirectory(_store.DirectoryPath); System.Diagnostics.Process.Start("explorer.exe", "\"" + _store.DirectoryPath + "\""); }
                catch (Exception ex) { SetStatus("폴더 열기 실패: " + ex.Message); }
            };
            Controls.Add(_items); Controls.Add(_status); Controls.Add(folder); Controls.Add(header);
            _items.SizeChanged += delegate { foreach (Control c in _items.Controls) c.Width = Math.Max(100, _items.ClientSize.Width - 24); };
        }
        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e); if (_loaded) return; _loaded = true;
            try
            {
                var records = await Task.Run(() => _store.LoadRecent(100));
                if (IsDisposed) return;
                foreach (var record in records) AddRecord(record);
            }
            catch (Exception ex) { if (!IsDisposed) SetStatus("NG 기록 읽기 실패: " + ex.Message); }
        }
        public void SetStatus(string text) { if (IsDisposed) return; _status.Text = text; _tip.SetToolTip(_status, text); }
        public void AddRecord(NgRecord record)
        {
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
            var caption = new Label { Text = record.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss.fff") + "\r\n" + Path.GetFileName(record.Recipe ?? ""), Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
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
            _items.Controls.Add(panel); _items.Controls.SetChildIndex(panel, index);
            while (_items.Controls.Count > 100) { var last = _items.Controls[_items.Controls.Count - 1]; _items.Controls.Remove(last); last.Dispose(); }
            if (index == 0) _items.ScrollControlIntoView(panel);
        }
        protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class NgImageWindow : Form
    {
        private readonly Image _image;
        private readonly PictureBox _picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Black };
        private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Black };
        private double _zoom = 1;
        public NgImageWindow(NgRecord record)
        {
            Text = "NG 저장 화면 — " + record.Camera; Width = 1100; Height = 800; StartPosition = FormStartPosition.CenterScreen;
            using (var stream = File.OpenRead(record.ImagePath)) using (var source = Image.FromStream(stream)) _image = new Bitmap(source);
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
