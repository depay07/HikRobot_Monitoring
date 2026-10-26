using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SC6000DualMonitor;
class NgPreviewTests
{
    static int checks;
    static void Check(bool yes,string message){if(!yes)throw new Exception(message);checks++;}
    static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
    static void Pump(Func<bool> done){var clock=Stopwatch.StartNew();while(!done() && clock.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(1);}Check(done(),"async operation completed");}
    [STAThread]static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"ng-preview-"+Guid.NewGuid().ToString("N"));
        try
        {
            byte[] png;using(var bitmap=new Bitmap(300,200))
            {using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Red);g.FillRectangle(Brushes.Lime,150,0,150,200);}using(var stream=new MemoryStream()){bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);png=stream.ToArray();}}
            byte[] thumb;using(var stream=new MemoryStream(png))thumb=NgFilmStrip.PrepareThumbnail(stream);
            var record=new NgRecord{TimeUtc=DateTime.UtcNow,Frame=1};record.BeginSave();
            var second=new NgRecord{TimeUtc=DateTime.UtcNow.AddMilliseconds(1),Frame=2};second.BeginSave();
            var store=new NgHistoryStore(root);
            using(var form=new Form{ClientSize=new Size(200,700),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var strip=new NgFilmStrip(new NgHistoryStore(Path.Combine(root,"empty"))))
            {
                form.Controls.Add(strip);form.Show();Application.DoEvents();
                strip.AddRecord(record,thumb);strip.AddRecord(second,thumb);strip.AddRecord(second,thumb);
                var items=Field<FlowLayoutPanel>(strip,"_items");Check(items.Controls.Count==2,"distinct pending records, no duplicate");
                Check(((NgRecord)items.Controls[0].Tag)==second,"pending latest thumbnail first");
                Check(!record.Ready.IsCompleted && record.ImagePath==null,"thumbnail visible before full image is saved");
                using(var popup=new NgImageWindow(record))
                {
                    popup.Show();Application.DoEvents();
                    Check(Field<Label>(popup,"_loading").Visible,"waiting notice shown immediately");
                    Check(Field<PictureBox>(popup,"_picture").Image==null,"no premature read before save completes");
                    Task.Run(()=>{store.Save(png,record);record.FinishSave();});
                    Pump(()=>Field<PictureBox>(popup,"_picture").Image!=null);
                    var image=(Bitmap)Field<PictureBox>(popup,"_picture").Image;
                    Check(image.Width==300 && image.Height==200,"full resolution loaded");
                    Check(image.GetPixel(25,25).R==255 && image.GetPixel(200,25).G==255,"colored screenshot preserved");
                    Check(!Field<Label>(popup,"_loading").Visible,"waiting notice dismissed after image ready");
                }
                using(var failed=new NgImageWindow(second))
                {
                    failed.Show();second.FinishSave("test disk failure");
                    Pump(()=>Field<Label>(failed,"_loading").Text.Contains("test disk failure"));
                    Check(Field<PictureBox>(failed,"_picture").Image==null,"failed save is not shown as another image");
                }
                var closed=new NgRecord();closed.BeginSave();
                using(var early=new NgImageWindow(closed)){early.Show();early.Close();closed.FinishSave("closed");Application.DoEvents();Check(early.IsDisposed,"waiting window can be closed");}
                var older=store.LoadRecent(100);Check(older.Count==1 && older[0].Ready.Result,"persisted PNG remains readable");
            }
            Console.WriteLine("PASS: "+checks+" preview-first and async image window checks");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
