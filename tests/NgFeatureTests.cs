using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SC6000DualMonitor;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Drawing.Size;

namespace FrontendUI.Design.Controls
{
    internal sealed class ImageControl : Grid
    {
        internal MockImageView ImageView;
        private uint curExecuteCount;
        private string OwnerProcessGUID = "procedure-test";
        public ImageControl()
        {
            ImageView = new MockImageView(); Children.Add(ImageView);
        }
        public void Publish(uint frame) { curExecuteCount = frame; ImageView.OnSetImageEnd?.Invoke(ImageView, "True"); }
    }
    internal sealed class MockImageView : Border
    {
        public delegate void SetImageEnd(object sender, string msg);
        public SetImageEnd OnSetImageEnd { get; set; }
        public object ImageDataSource { get; set; } = new object();
        public ImageSource Source { get; set; } = new DrawingImage();
        public void ChangeFirstColor(System.Windows.Media.Brush brush) { ((System.Windows.Shapes.Rectangle)((Canvas)Child).Children[0]).Fill = brush; }
        public MockImageView()
        {
            var canvas = new Canvas { Background = Brushes.DimGray };
            canvas.Children.Add(new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = Brushes.Red });
            var green = new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = Brushes.Lime }; Canvas.SetLeft(green, 100); canvas.Children.Add(green);
            var blue = new System.Windows.Shapes.Rectangle { Width = 100, Height = 100, Fill = Brushes.Blue }; Canvas.SetLeft(blue, 200); canvas.Children.Add(blue);
            var text = new TextBlock { Text = "NG  Middle_Size", FontSize = 32, Foreground = Brushes.Orange }; Canvas.SetTop(text, 130); canvas.Children.Add(text);
            Child = canvas;
        }
    }
}
class NgFeatureTests
{
    static int checks;
    static void Check(bool yes, string text) { if (!yes) { Console.WriteLine("FAIL: " + text); throw new Exception(text); } checks++; }
    [STAThread] static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "sc6000-ng-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Run(root,args); Console.WriteLine("PASS: " + checks + " NG capture/history/viewer checks"); }
        finally { Directory.Delete(root,true); }
    }
    static void Run(string root,string[] args)
    {
        var matcher = new NgFrameMatcher();
        Check(!matcher.Record(1,true,null),"wait for completion"); Check(matcher.Record(1,null,true),"NG then completion");
        Check(!matcher.Record(1,true,true),"no duplicate"); Check(!matcher.Record(2,null,true),"wait for condition");
        Check(matcher.Record(2,true,null),"completion then NG"); Check(!matcher.Record(3,false,true),"OK excluded");
        Check(!matcher.Record(4,true,false),"execution error excluded");
        Check(!matcher.Record(5,true,null) && !matcher.Record(6,null,true),"never pair different frames");
        matcher.Clear(); Check(!matcher.Record(5,null,true),"clear on recipe switch");
        var store = new NgHistoryStore(root);
        byte[] png; string message;
        using(var form = new Forms.Form { ClientSize = new Size(800,500), StartPosition = Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-20000,-20000) })
        using(var host = new ElementHost { Dock = Forms.DockStyle.Fill })
        {
            var view = new FrontendUI.Design.Controls.ImageControl(); host.Child = view; form.Controls.Add(host); form.Show(); Forms.Application.DoEvents();
            using(var capture = new FrontendCapture(host))
            {
                Check(capture.HasViews,"SDK compatible view discovery");
                Check(!capture.TryCapture(42,"procedure-test",out png,out message),"no image before rendered callback");
                view.Publish(42); Forms.Application.DoEvents();
                Check(!capture.TryCapture(42,"other-procedure",out png,out message),"wrong procedure excluded");
                Check(capture.TryCapture(42,"procedure-test",out png,out message),"capture completed image");
                using(var stream = new MemoryStream(png)) using(var image = new Bitmap(stream))
                {
                    Check(image.GetPixel(50,50).R>240 && image.GetPixel(50,50).G<10,"red graphics retained");
                    Check(image.GetPixel(150,50).G>240 && image.GetPixel(150,50).R<10,"green graphics retained");
                    Check(image.GetPixel(250,50).B>240 && image.GetPixel(250,50).R<10,"blue graphics retained");
                }
                var a=store.Save(png,new NgRecord {Camera="Camera 1",Recipe="/camera/model_2.solx",TimeUtc=DateTime.UtcNow.AddMinutes(-1),Frame=42,ViewName="검사 화면"});
                var b=store.Save(png,new NgRecord {Camera="Camera 1",Recipe="/camera/model_3.solx",TimeUtc=DateTime.UtcNow,Frame=43,ViewName="검사 화면"});
                Check(a.ImagePath!=b.ImagePath,"unique file names");
                var records=new NgHistoryStore(root).LoadRecent(100);
                Check(records.Count==2 && records[0].Frame==43,"restart and newest first");
                Check(records[1].Recipe==a.Recipe,"recipe preserved in metadata");
                File.WriteAllText(Path.Combine(root,"zzz.xml"),"<broken>");
                Check(store.LoadRecent(100).Count==2,"corrupt history skipped");
                Check(store.LoadRecent(1).Single().Frame==43,"display bound");
                view.ImageView.ChangeFirstColor(Brushes.Yellow);
                view.Publish(44); Forms.Application.DoEvents();
                Check(capture.TryCapture(42,"procedure-test",out png,out message),"late NG result can retrieve frame after next trigger");
                using(var stream = new MemoryStream(png)) using(var image = new Bitmap(stream))
                    Check(image.GetPixel(50,50).R>240 && image.GetPixel(50,50).G<10,"late NG keeps its red graphics, never next frame yellow");
                view.ImageView.Source=null; view.Publish(45); Check(!capture.TryCapture(45,"procedure-test",out png,out message),"blank checkerboard is not an NG photo");
                using(var window=new NgImageWindow(a)) { window.Show(); Forms.Application.DoEvents(); Check(window.Visible,"separate history window"); window.Close(); }
                using(var strip=new NgFilmStrip(store))
                using(var sample=new Forms.Form { ClientSize=new Size(250,650), StartPosition=Forms.FormStartPosition.Manual, Location=new System.Drawing.Point(-20000,-20000) })
                {
                    sample.Controls.Add(strip); sample.Show(); strip.AddRecord(a);
                    var preview = System.Threading.Tasks.Task.Run(() => { using(var stream=File.OpenRead(b.ImagePath)) return NgFilmStrip.PrepareThumbnail(stream); }).GetAwaiter().GetResult();
                    using(var stream=new MemoryStream(preview)) using(var thumb=new Bitmap(stream)) Check(thumb.Width==170 && thumb.Height==115,"worker prepares bounded thumbnail");
                    File.Move(b.ImagePath,b.ImagePath+".hold");
                    try { strip.AddRecord(b,preview); }
                    finally { File.Move(b.ImagePath+".hold",b.ImagePath); }
                    Forms.Application.DoEvents();
                    var list=(Forms.FlowLayoutPanel)typeof(NgFilmStrip).GetField("_items",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(strip);
                    Check(((NgRecord)list.Controls[0].Tag).Frame==43,"newest thumbnail on top");
                    var picture=list.Controls[0].Controls.OfType<Forms.PictureBox>().Single();
                    typeof(Forms.Control).GetMethod("OnDoubleClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(picture,new object[]{EventArgs.Empty});
                    Check(Forms.Application.OpenForms.Cast<Forms.Form>().Any(f=>f is NgImageWindow),"double click opens image");
                    foreach(var popup in Forms.Application.OpenForms.Cast<Forms.Form>().OfType<NgImageWindow>().ToArray()) popup.Close();
                    Check(!strip.Controls.OfType<Forms.ComboBox>().Any(),"no replay mode selector");
                    view.ImageView.Source = new DrawingImage(); view.Publish(46); Forms.Application.DoEvents();
                    Check(capture.TryCapture(46,"procedure-test",out png,out message),"new live frame can be captured after closing history image");
                    if(args.Length>0) using(var screenshot=new Bitmap(sample.Width,sample.Height)) { sample.DrawToBitmap(screenshot,sample.ClientRectangle); screenshot.Save(args[0]); }
                }
                view.Publish(70); view.Publish(71); Forms.Application.DoEvents();
                Check(!capture.TryCapture(70,"procedure-test",out png,out message),"never relabel a frame replaced before screenshot");
                Check(capture.TryCapture(71,"procedure-test",out png,out message),"capture newest rendered frame in burst");
                for(uint frame=100;frame<170;frame++) { view.Publish(frame); Forms.Application.DoEvents(); }
                Check(!capture.TryCapture(42,"procedure-test",out png,out message),"old frame evicted at bounded capacity");
                Check(capture.TryCapture(169,"procedure-test",out png,out message),"recent frame survives capacity eviction");
                BitmapSource frozen;
                Check(capture.TryGetSnapshot(169,"procedure-test",out frozen,out message) && frozen.IsFrozen,"snapshot is safe to hand to disk worker");
                var encoded=System.Threading.Tasks.Task.Run(()=>FrontendCapture.EncodePng(frozen)).GetAwaiter().GetResult();
                using(var stream=new MemoryStream(encoded)) using(var image=new Bitmap(stream))
                    Check(image.GetPixel(50,50).G>240,"background PNG encoding preserves frame colors");
                var jpeg=System.Threading.Tasks.Task.Run(()=>FrontendCapture.EncodeJpeg(frozen)).GetAwaiter().GetResult();
                using(var stream=new MemoryStream(jpeg)) using(var image=new Bitmap(stream))
                {
                    double dpi = PresentationSource.FromVisual(view).CompositionTarget.TransformToDevice.M11;
                    var yellow = image.GetPixel((int)(50*dpi),(int)(50*dpi));
                    var green = image.GetPixel((int)(150*dpi),(int)(50*dpi));
                    var blue = image.GetPixel((int)(250*dpi),(int)(50*dpi));
                    Check(image.Width==frozen.PixelWidth && image.Height==frozen.PixelHeight,"JPEG retains full screenshot resolution");
                    Check(yellow.R>230 && yellow.G>230 && yellow.B<25,"JPEG retains the yellow graphic from this frame");
                    Check(green.G>230 && green.R<25,"JPEG retains green graphics");
                    Check(blue.B>230 && blue.R<25,"JPEG retains blue graphics");
                }
                var jpgRecord=store.Save(jpeg,new NgRecord {TimeUtc=DateTime.UtcNow,Frame=169},".jpg");
                Check(store.LoadRecent(100).Any(r=>r.ImagePath==jpgRecord.ImagePath),"JPEG history reloads alongside legacy PNG records");
                var directPreview=System.Threading.Tasks.Task.Run(()=>FrontendCapture.CreateThumbnail(frozen)).GetAwaiter().GetResult();
                using(var stream=new MemoryStream(directPreview)) using(var image=new Bitmap(stream))
                    Check(image.Width<=170 && image.Height<=115,"direct snapshot thumbnail stays small without full image compression");
            }
        }
        using(var form=new Forms.Form {ClientSize=new Size(800,500),StartPosition=Forms.FormStartPosition.Manual,Location=new System.Drawing.Point(-20000,-20000)})
        using(var host=new ElementHost {Dock=Forms.DockStyle.Fill})
        {
            var grid=new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
            var first=new FrontendUI.Design.Controls.ImageControl(); var second=new FrontendUI.Design.Controls.ImageControl();
            grid.Children.Add(first); grid.Children.Add(second); Grid.SetRow(second,1); host.Child=grid; form.Controls.Add(host); form.Show(); Forms.Application.DoEvents();
            using(var capture=new FrontendCapture(host))
            {
                first.Publish(200); Forms.Application.DoEvents();
                first.Publish(201); Forms.Application.DoEvents();
                Check(!capture.TryCapture(200,"procedure-test",out png,out message),"multi-view waits for matching second view");
                second.Publish(200); Forms.Application.DoEvents();
                Check(capture.TryCapture(200,"procedure-test",out png,out message),"views from same frame combine despite staggered updates");
            }
        }
        Check(NgHistoryStore.DefaultDirectory("192.168.10.51")!=NgHistoryStore.DefaultDirectory("192.168.10.52"),"camera histories isolated");
        string notDirectory=Path.Combine(root,"blocked");File.WriteAllText(notDirectory,"x");
        bool failed=false;try {new NgHistoryStore(notDirectory).Save(new byte[]{1},new NgRecord{TimeUtc=DateTime.UtcNow});}catch(IOException){failed=true;}
        Check(failed,"disk failure surfaced");
    }
}

