using System;
using System.Drawing;
using Color = System.Drawing.Color;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SC6000DualMonitor
{
    // Display-only cover. Never hide, lock, pause, or modify the SDK control beneath it.
    internal sealed class InspectionDisplayHold : IDisposable
    {
        private readonly Control _host;
        private readonly Panel _cover = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
        private readonly PictureBox _picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Label _banner = new Label { Dock = DockStyle.Top, Height = 56, ForeColor = Color.White, BackColor = Color.DarkRed, TextAlign = ContentAlignment.MiddleCenter };
        private Form _dialog;
        private int _generation;
        private DateTime _acknowledgedUtc;
        public bool IsHeld { get; private set; }
        public InspectionDisplayHold(Control host)
        {
            _host = host; _cover.Controls.Add(_picture); _cover.Controls.Add(_banner); host.Controls.Add(_cover);
        }
        public int Reserve(string camera, NgCaptureEventArgs result)
        {
            if (IsHeld || result.TimeUtc <= _acknowledgedUtc) return 0;
            int token = ++_generation; IsHeld = true;
            _banner.Text = result.Outcome + " 화면 고정 · 검사 회차 " + result.Frame + "\r\n해당 화면 확보 중 · 검사는 계속 진행됩니다.";
            _cover.Visible = true; _cover.BringToFront();
            _dialog = new Form { Text = camera + " · " + result.Outcome + " 발생", ClientSize = new Size(420, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
            var text = new Label { Dock = DockStyle.Fill, Padding = new Padding(16), Text = result.Outcome + "가 발생하여 화면을 고정했습니다.\r\n검사와 NG/ERROR 기록 저장은 계속됩니다.\r\n확인을 누르면 실시간 화면으로 돌아갑니다.\r\n\r\n" + result.Detail };
            var confirm = new Button { Text = "확인 · 실시간 화면 재개", Dock = DockStyle.Bottom, Height = 40 };
            confirm.Click += delegate { Acknowledge(); };
            _dialog.Controls.Add(text); _dialog.Controls.Add(confirm); _dialog.AcceptButton = confirm;
            _dialog.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (IsHeld && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            var bounds = HostBounds();
            _dialog.Location = new Point(bounds.Left + Math.Max(0, (bounds.Width - _dialog.Width) / 2), bounds.Top + Math.Max(0, bounds.Height - _dialog.Height - 15));
            IntPtr owner = GetAncestor(_host.Handle, 2); // Root window, including the external parent of the viewer child process.
            if (owner != IntPtr.Zero) _dialog.Show(new WindowOwner(owner)); else _dialog.Show();
            return token;
        }
        private Rectangle HostBounds() { return _host.RectangleToScreen(_host.ClientRectangle); }
        public void SetImage(int token, BitmapSource snapshot, string outcome, uint frame)
        {
            if (token == 0 || token != _generation || !IsHeld) return;
            var pixels = new FormatConvertedBitmap(snapshot, PixelFormats.Bgra32, null, 0); pixels.Freeze();
            var image = new Bitmap(pixels.PixelWidth, pixels.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { pixels.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, checked(data.Stride * image.Height), data.Stride); }
                finally { image.UnlockBits(data); }
                var previous = _picture.Image; _picture.Image = image; if (previous != null) previous.Dispose();
            }
            catch { image.Dispose(); throw; }
            _banner.Text = outcome + " 화면 고정 · 검사 회차 " + frame + "\r\n검사는 계속 진행 중입니다. 확인을 누르면 실시간 화면으로 돌아갑니다.";
            _cover.BringToFront();
        }
        public void CaptureFailed(int token, string message)
        {
            if (token != 0 && token == _generation && IsHeld) _banner.Text = "화면 고정 · " + message + "\r\n검사는 계속 진행 중입니다. 확인을 누르면 실시간 화면으로 돌아갑니다.";
        }
        public void Acknowledge() { _acknowledgedUtc = DateTime.UtcNow; Reset(); }
        public void Reset()
        {
            IsHeld = false; ++_generation; _cover.Visible = false;
            if (_dialog != null) { _dialog.Dispose(); _dialog = null; }
            var image = _picture.Image; _picture.Image = null; if (image != null) image.Dispose();
        }
        public void Dispose() { Reset(); _cover.Dispose(); }
        private sealed class WindowOwner : IWin32Window { public IntPtr Handle { get; private set; } public WindowOwner(IntPtr handle) { Handle = handle; } }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
