using System;
using System.Drawing;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    // Draw into the available header rectangle: logo aspect ratio is never stretched.
    internal sealed class MonitorHeader : Control
    {
        private readonly Image _logo;
        private readonly string _inspection;
        public MonitorHeader(Image logo, string inspection)
        {
            _logo = logo;
            _inspection = inspection ?? string.Empty;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 20 || ClientSize.Height < 10) return;
            int pad = Math.Max(4, (int)(Height * .15));
            int availableHeight = Math.Max(1, Height - 2 * pad);
            int logoWidth;
            var color = Color.FromArgb(20, 63, 110);
            if (_logo != null)
            {
                double scale = Math.Min((double)availableHeight / _logo.Height, (Width * .35) / _logo.Width);
                logoWidth = Math.Max(1, (int)(_logo.Width * scale));
                int logoHeight = Math.Max(1, (int)(_logo.Height * scale));
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_logo, new Rectangle(pad, (Height - logoHeight) / 2, logoWidth, logoHeight));
            }
            else
            {
                using (var font = new Font("Segoe UI", Math.Max(8, Height * .38f), FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    logoWidth = Math.Min((int)(Width * .35), TextRenderer.MeasureText("DAEKYUNG TECHNO", font).Width);
                    TextRenderer.DrawText(e.Graphics, "DAEKYUNG TECHNO", font, new Rectangle(pad, 0, logoWidth, Height), color,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                }
            }
            if (string.IsNullOrWhiteSpace(_inspection)) return;
            int left = pad + logoWidth + pad * 2;
            var bounds = new Rectangle(left, 0, Math.Max(1, Width - left - pad), Height);
            float size = Math.Max(8, Height * .34f);
            using (var trial = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                int measured = TextRenderer.MeasureText(_inspection, trial).Width;
                if (measured > bounds.Width) size = Math.Max(8, size * bounds.Width / measured);
            }
            using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, _inspection, font, bounds, color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
