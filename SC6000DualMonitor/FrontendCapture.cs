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
        private bool _disposed;
        public event Action Rendered;
        public string Availability { get; private set; }
        public bool HasViews { get { return _views.Count > 0; } }
        public string Describe()
        {
            return "영상 컨트롤: " + _views.Count + "개\r\n" + string.Join("\r\n", _views.Select(v =>
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
            view.CompletedFrame = (uint)view.FrameField.GetValue(view.Control);
            // Let WPF finish shape bindings/layout before rendering the screenshot.
            view.Control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { if (!_disposed) Rendered?.Invoke(); }));
        }
        public bool TryCapture(uint frame, string procedureGuid, out byte[] png, out string message)
        {
            png = null;
            var views = _views.Where(v => v.Control.IsVisible && string.Equals((string)v.OwnerField.GetValue(v.Control), procedureGuid, StringComparison.Ordinal)).ToList();
            if (views.Count == 0) { message = "해당 검사 흐름의 영상 화면을 찾을 수 없습니다."; return false; }
            if (views.Any(v => v.CompletedFrame != frame || (uint)v.FrameField.GetValue(v.Control) != frame || v.ImageDataProperty.GetValue(v.Image, null) == null || v.SourceProperty.GetValue(v.Image, null) == null))
            { message = "NG 회차와 화면 회차가 일치하지 않습니다."; return false; }
            var images = new List<BitmapSource>();
            foreach (var view in views)
            {
                view.Control.UpdateLayout();
                if (view.Control.ActualWidth < 2 || view.Control.ActualHeight < 2) { message = "영상 화면 크기를 확인할 수 없습니다."; return false; }
                images.Add(CaptureVisual(view.Control));
            }
            if (views.Any(v => v.CompletedFrame != frame || (uint)v.FrameField.GetValue(v.Control) != frame || v.ImageDataProperty.GetValue(v.Image, null) == null || v.SourceProperty.GetValue(v.Image, null) == null))
            { message = "캡처 중 검사 화면이 변경되었습니다."; return false; }
            int width = images.Max(i => i.PixelWidth), height = images.Sum(i => i.PixelHeight);
            if ((long)width * height > 16000000) throw new InvalidOperationException("NG 캡처 화면이 너무 큽니다.");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                int y = 0;
                foreach (var image in images) { dc.DrawImage(image, new Rect(0, y, image.PixelWidth, image.PixelHeight)); y += image.PixelHeight; }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            using (var stream = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); png = stream.ToArray();
            }
            message = views.Count == 1 ? "검사 화면" : "검사 화면 " + views.Count + "개";
            return true;
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
        }
    }
}

