using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SC6000DualMonitor;
class NgRateTests
{
    class Done { public int Camera; public NgRecord Record; public byte[] Preview; public long Start; public Exception Error; }
    [STAThread] static void Main(string[] args)
    {
        string root=Path.Combine(Path.GetTempPath(),"ng-rate-"+Guid.NewGuid().ToString("N"));
        try
        {
            const int width=1280,height=900,perCamera=100; int cameraCount=args.Length>0?int.Parse(args[0]):2;
            byte[] pixels=new byte[width*height*4]; new Random(42).NextBytes(pixels);
            for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
            var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();
            var stores=new[] {new NgHistoryStore(Path.Combine(root,"cam1")),new NgHistoryStore(Path.Combine(root,"cam2"))};
            var strips=new[] {new NgFilmStrip(new NgHistoryStore(Path.Combine(root,"empty1"))),new NgFilmStrip(new NgHistoryStore(Path.Combine(root,"empty2")))};
            using(var form=new Form {ClientSize=new Size(400,900),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            {
                strips[0].Dock=DockStyle.Left;strips[0].Width=200;strips[1].Dock=DockStyle.Fill;
                form.Controls.Add(strips[1]);form.Controls.Add(strips[0]);form.Show();Application.DoEvents();
                var done=new ConcurrentQueue<Done>(); var tasks=new List<Task>();
                int[] pending=new int[2],peak=new int[2],saved=new int[2],drops=new int[2];
                var latencies=new List<double>();var clock=Stopwatch.StartNew();long next=0;int frame=0;int errors=0;
                while(frame<perCamera || pending[0]+pending[1]>0)
                {
                    if(clock.ElapsedMilliseconds>60000)throw new Exception("Timed out");
                    if(frame<perCamera && clock.ElapsedMilliseconds>=next)
                    {
                        for(int cam=0;cam<cameraCount;cam++)
                        {
                            if(pending[cam]>=8 || (pending[cam]+1L)*width*height*4>64L*1024*1024) {drops[cam]++;continue;}
                            int camera=cam;var record=new NgRecord{Camera="Camera "+cam,TimeUtc=DateTime.UtcNow,Frame=(uint)frame,Recipe="test.solx"};
                            long started=clock.ElapsedMilliseconds;pending[cam]++;peak[cam]=Math.Max(peak[cam],pending[cam]);
                            tasks.Add(Task.Run(()=>
                            {
                                var result=new Done{Camera=camera,Record=record,Start=started};
                                try {var png=FrontendCapture.EncodePng(bitmap);stores[camera].Save(png,record);using(var stream=new MemoryStream(png,false))result.Preview=NgFilmStrip.PrepareThumbnail(stream);}
                                catch(Exception e){result.Error=e;}
                                done.Enqueue(result);
                            }));
                        }
                        frame++;next+=100;
                    }
                    Done item;
                    while(done.TryDequeue(out item))
                    {
                        pending[item.Camera]--;
                        if(item.Error!=null) {errors++;Console.WriteLine(item.Error.Message);continue;}
                        strips[item.Camera].AddRecord(item.Record,item.Preview);saved[item.Camera]++;
                        latencies.Add(clock.ElapsedMilliseconds-item.Start);
                    }
                    Application.DoEvents();System.Threading.Thread.Sleep(1);
                }
                Task.WaitAll(tasks.ToArray());latencies.Sort();
                Console.WriteLine(cameraCount+" cameras x 10 NG/s x 10s; 1280x900 random pixels; full PNG encoding + disk + preview UI (SDK/capture excluded)");
                Console.WriteLine("Saved="+saved[0]+","+saved[1]+"; drops="+drops[0]+","+drops[1]+"; errors="+errors+"; peak pending="+peak[0]+","+peak[1]);
                Console.WriteLine("Latency ms: median="+latencies[latencies.Count/2]+" p95="+latencies[(int)(latencies.Count*.95)]+" max="+latencies[latencies.Count-1]+"; elapsed="+clock.ElapsedMilliseconds);
                if(saved[0]!=100 || (cameraCount==2 && saved[1]!=100) || errors!=0)Environment.ExitCode=1;
            }
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
