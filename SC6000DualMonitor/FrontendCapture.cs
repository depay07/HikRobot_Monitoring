using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SC6000DualMonitor
{
    // Adapter for the installed VM 4.4.50 frontend. Only reads SDK state; never stops acquisition.
    // OnSetImageEnd runs after the SDK's own handler updates the colored shapes.
    internal sealed class FrontendCapture : IDisposable
    {
        private sealed class View
        {
            public FrameworkElement Control, Image;
            public PropertyInfo EndProperty, ImageDataProperty, SourceProperty;
            public FieldInfo FrameField, OwnerField;
            public Delegate Handler;
            public uint? CompletedFrame;
        }
        private readonly List<View> _views = new List<View>();
        private sealed class Snapshot
        {
            public View View;
            public uint Frame;
            public string Procedure;
            public BitmapSource Image;
            public long Bytes;
        }
        private readonly LinkedList<Snapshot> _snapshots = new LinkedList<Snapshot>();
        private const long SnapshotBudget = 64L * 1024 * 1024;
        private long _snapshotBytes, _evicted;
        private string _snapshotError;
        private bool _disposed;
        public event Action Rendered;
        public string Availability { get; private set; }
        public bool HasViews { get { return _views.Count > 0; } }
        public string Describe()
        {
            return "화면 임시 보관: " + _snapshots.Count + "개 / " + (_snapshotBytes / 1024 / 1024) + "MB / 오래된 화면 제거: " + _evicted +
                "\r\n최근 화면 확보 오류: " + (_snapshotError ?? "없음") + "\r\n영상 컨트롤: " + _views.Count + "개\r\n" + string.Join("\r\n", _views.Select(v =>
                "흐름 ID=" + v.OwnerField.GetValue(v.Control) + " / 표시=" + v.Control.IsVisible +
                " / 현재 회차=" + v.FrameField.GetValue(v.Control) + " / 렌더 완료=" + (v.CompletedFrame.HasValue ? v.CompletedFrame.Value.ToString() : "없음")));
        }
        public FrontendCapture(Forms.Control frontend)
        {
            foreach (var host in FormsChildren(frontend).OfType<ElementHost>())
            {
                if (host.Child == null) continue;
                foreach (var element in Visuals(host.Child).OfType<FrameworkElement>())
                {
                    if (element.GetType().FullName != "FrontendUI.Design.Controls.ImageControl") continue;
                    var type = element.GetType();
                    var imageField = type.GetField("ImageView", BindingFlags.Instance | BindingFlags.NonPublic);
                    var frameField = type.GetField("curExecuteCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    var ownerField = type.GetField("OwnerProcessGUID", BindingFlags.Instance | BindingFlags.NonPublic);
                    var image = imageField == null ? null : imageField.GetValue(element) as FrameworkElement;
                    var end = image == null ? null : image.GetType().GetProperty("OnSetImageEnd");
                    var imageData = image == null ? null : image.GetType().GetProperty("ImageDataSource");
                    var sourceProperty = image == null ? null : image.GetType().GetProperty("Source");
                    if (frameField == null || frameField.FieldType != typeof(uint) || ownerField == null || end == null || imageData == null || sourceProperty == null) continue;
                    var handler = Delegate.CreateDelegate(end.PropertyType, this, GetType().GetMethod("ImageEnded", BindingFlags.Instance | BindingFlags.NonPublic));
                    var view = new View { Control = element, Image = image, FrameField = frameField, OwnerField = ownerField, EndProperty = end, ImageDataProperty = imageData, SourceProperty = sourceProperty, Handler = handler };
                    end.SetValue(image, Delegate.Combine((Delegate)end.GetValue(image, null), handler), null);
                    _views.Add(view);
                }
            }
            Availability = _views.Count == 0 ? "NG 캡처 연결 실패: 지원하는 검사 영상 컨트롤을 찾지 못했습니다." : "NG 화면 저장 대기";
        }
        private void ImageEnded(object sender, string success)
        {
            if (_disposed) return;
            bool rendered;
            if (!bool.TryParse(success, out rendered) || !rendered) return;
            var view = _views.FirstOrDefault(v => ReferenceEquals(v.Image, sender));
            if (view == null) return;
            if (view.ImageDataProperty.GetValue(view.Image, null) == null || view.SourceProperty.GetValue(view.Image, null) == null) { view.CompletedFrame = null; return; }
            uint frame = (uint)view.FrameField.GetValue(view.Control);
            string procedure = (string)view.OwnerField.GetValue(view.Control);
            view.CompletedFrame = frame;
            // Binding updates precede Render; the SDK schedules the next image at Background.
            // Preserve this frame before waiting for NG/procedure callbacks or the WinForms timer.
            view.Control.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate
            {
                if (_disposed) return;
                try
                {
                    if (!Matches(view, frame, procedure)) return;
                    view.Control.UpdateLayout();
                    if (view.Control.ActualWidth < 2 || view.Control.ActualHeight < 2) return;
                    var image = CaptureVisual(view.Control);
                    if (!Matches(view, frame, procedure)) return;
                    var previous = _snapshots.First;
                    while (previous != null)
                    {
                        var next = previous.Next;
                        if (previous.Value.View == view && previous.Value.Frame == frame)
                        { _snapshotBytes -= previous.Value.Bytes; _snapshots.Remove(previous); }
                        previous = next;
                    }
                    long bytes = (long)image.PixelWidth * image.PixelHeight * 4;
                    _snapshots.AddLast(new Snapshot { View = view, Frame = frame, Procedure = procedure, Image = image, Bytes = bytes });
                    _snapshotBytes += bytes;
                    while (_snapshots.Count > 64 || _snapshotBytes > SnapshotBudget)
                    { _snapshotBytes -= _snapshots.First.Value.Bytes; _snapshots.RemoveFirst(); _evicted++; }
                    Rendered?.Invoke();
                }
                catch (Exception ex) { _snapshotError = ex.Message; System.Diagnostics.Trace.WriteLine(ex); }
            }));
        }
        private static bool Matches(View view, uint frame, string procedure)
        {
            return view.Control.IsVisible && view.CompletedFrame == frame && (uint)view.FrameField.GetValue(view.Control) == frame &&
                string.Equals((string)view.OwnerField.GetValue(view.Control), procedure, StringComparison.Ordinal) &&
                view.ImageDataProperty.GetValue(view.Image, null) != null && view.SourceProperty.GetValue(view.Image, null) != null;
        }
        public bool TryCapture(uint frame, string procedureGuid, out byte[] png, out string message)
        {
            png = null;
            BitmapSource bitmap;
            if (!TryGetSnapshot(frame, procedureGuid, out bitmap, out message)) return false;
            png = EncodePng(bitmap); return true;
        }
        public bool TryGetSnapshot(uint frame, string procedureGuid, out BitmapSource bitmap, out string message)
        {
            bitmap = null;
            var views = _views.Where(v => v.Control.IsVisible && string.Equals((string)v.OwnerField.GetValue(v.Control), procedureGuid, StringComparison.Ordinal)).ToList();
            if (views.Count == 0) { message = "해당 검사 흐름의 영상 화면을 찾을 수 없습니다."; return false; }
            var images = new List<BitmapSource>();
            foreach (var view in views)
            {
                var saved = _snapshots.FirstOrDefault(s => s.View == view && s.Frame == frame && s.Procedure == procedureGuid);
                if (saved == null) { message = "NG 회차 " + frame + "의 보관 화면 없음 (화면 미수신 또는 보관 한도 초과)"; return false; }
                images.Add(saved.Image);
            }
            message = views.Count == 1 ? "검사 화면" : "검사 화면 " + views.Count + "개";
            if (images.Count == 1) { bitmap = images[0]; return true; }
            int width = images.Max(i => i.PixelWidth), height = images.Sum(i => i.PixelHeight);
            if ((long)width * height > 16000000) throw new InvalidOperationException("NG 캡처 화면이 너무 큽니다.");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                int y = 0;
                foreach (var image in images) { dc.DrawImage(image, new Rect(0, y, image.PixelWidth, image.PixelHeight)); y += image.PixelHeight; }
            }
            var combined = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); combined.Render(drawing); combined.Freeze();
            bitmap = combined; return true;
        }
        public static byte[] CreateThumbnail(BitmapSource bitmap)
        {
            if (!bitmap.IsFrozen) throw new InvalidOperationException("고정된 캡처 화면만 처리할 수 있습니다.");
            double scale = Math.Min(170.0 / bitmap.PixelWidth, 115.0 / bitmap.PixelHeight);
            var thumbnail = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            thumbnail.Freeze();
            return EncodePng(thumbnail);
        }
        public static byte[] EncodeJpeg(BitmapSource bitmap)
        {
            if (!bitmap.IsFrozen) throw new InvalidOperationException("고정된 캡처 화면만 저장할 수 있습니다.");
            using (var stream = new MemoryStream())
            {
                var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0); rgb.Freeze();
                var encoder = new JpegBitmapEncoder { QualityLevel = 95 }; encoder.Frames.Add(BitmapFrame.Create(rgb)); encoder.Save(stream); return stream.ToArray();
            }
        }
        public static byte[] EncodePng(BitmapSource bitmap)
        {
            if (!bitmap.IsFrozen) throw new InvalidOperationException("고정된 캡처 화면만 저장할 수 있습니다.");
            using (var stream = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); return stream.ToArray();
            }
        }
        internal static BitmapSource CaptureVisual(FrameworkElement view)
        {
            var source = PresentationSource.FromVisual(view);
            double dpi = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            int w = Math.Max(1, (int)Math.Ceiling(view.ActualWidth * dpi)), h = Math.Max(1, (int)Math.Ceiling(view.ActualHeight * dpi));
            if ((long)w * h > 16000000) throw new InvalidOperationException("캡처 크기가 제한을 초과했습니다.");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, w, h));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Freeze();
            return bitmap;
        }
        private static IEnumerable<Forms.Control> FormsChildren(Forms.Control root)
        { yield return root; foreach (Forms.Control child in root.Controls) foreach (var node in FormsChildren(child)) yield return node; }
        private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var node in Visuals(VisualTreeHelper.GetChild(root, i))) yield return node; }
        public void Dispose()
        {
            _disposed = true; Rendered = null;
            foreach (var view in _views)
                view.EndProperty.SetValue(view.Image, Delegate.Remove((Delegate)view.EndProperty.GetValue(view.Image, null), view.Handler), null);
            _views.Clear();
            _snapshots.Clear(); _snapshotBytes = 0;
        }
    }
}

