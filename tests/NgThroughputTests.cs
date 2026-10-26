using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using SC6000DualMonitor;
class NgThroughputTests
{
    [STAThread] static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(), "ng-speed-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new NgHistoryStore(root);
            byte[] png;
            using(var bitmap=new Bitmap(1280,900))
            {
                var data=bitmap.LockBits(new Rectangle(0,0,1280,900),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                var pixels=new byte[data.Stride*data.Height]; new Random(42).NextBytes(pixels);
                for(int i=3;i<pixels.Length;i+=4) pixels[i]=255;
                System.Runtime.InteropServices.Marshal.Copy(pixels,0,data.Scan0,pixels.Length); bitmap.UnlockBits(data);
                using(var g=Graphics.FromImage(bitmap)) { g.FillRectangle(Brushes.Red,20,20,180,70); g.FillRectangle(Brushes.Lime,900,500,180,70); }
                using(var stream=new MemoryStream()) {bitmap.Save(stream,ImageFormat.Png);png=stream.ToArray();}
            }
            var records=new NgRecord[40]; var previews=new byte[40][]; var start=DateTime.UtcNow;
            var timer=Stopwatch.StartNew();
            Parallel.For(0,40,new ParallelOptions {MaxDegreeOfParallelism=4}, i=> { records[i]=store.Save(png,new NgRecord {TimeUtc=start.AddMilliseconds(i),Frame=(uint)i}); using(var stream=new MemoryStream(png,false)) previews[i]=NgFilmStrip.PrepareThumbnail(stream); });
            Console.WriteLine("40 PNG+XML saves and thumbnail preparations, 4 workers: "+timer.ElapsedMilliseconds+" ms; PNG bytes="+png.Length);
            using(var form=new Form {ClientSize=new Size(196,900),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var strip=new NgFilmStrip(new NgHistoryStore(Path.Combine(root,"empty"))))
            {
                form.Controls.Add(strip);form.Show();Application.DoEvents();
                long max=0;timer.Restart();
                for(int i=0;i<records.Length;i++) {var one=Stopwatch.StartNew();strip.AddRecord(records[i], previews[i]);max=Math.Max(max,one.ElapsedMilliseconds);}
                Console.WriteLine("40 UI additions: "+timer.ElapsedMilliseconds+" ms; longest addition="+max+" ms");
            }
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
