using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Media.Imaging;
using SC6000DualMonitor;
class FailureHoldTests
{
    static int checks;
    static void Check(bool yes,string text){if(!yes)throw new Exception(text);checks++;}
    static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
    [STAThread]static void Main()
    {
        var matcher=new NgFrameMatcher();
        Check(matcher.Record(1,null,false),"ERROR does not wait for condition callback");
        Check(matcher.Outcome(1)=="ERROR","execution failure labeled ERROR");
        Check(!matcher.Record(1,true,false),"late condition callback cannot duplicate ERROR");
        Check(!matcher.Record(2,false,true),"OK not captured");
        Check(!matcher.Record(3,false,null,true) && matcher.Record(3,null,true),"condition failure captured when procedure completes");
        Check(matcher.Outcome(3)=="ERROR","condition failure labeled ERROR");
        Check(!matcher.Record(4,null,true) && matcher.Record(4,true,null),"NG works in reverse callback order");
        Check(matcher.Outcome(4)=="NG","NG label preserved");
        string root=Path.Combine(Path.GetTempPath(),"failure-hold-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var form=new Form{ClientSize=new Size(1000,600),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var host=new Panel{Dock=DockStyle.Left,Width=800})
            using(var otherHost=new Panel{Dock=DockStyle.Fill})
            using(var element=new ElementHost{Dock=DockStyle.Fill})
            {
                var view=new FrontendUI.Design.Controls.ImageControl();element.Child=view;host.Controls.Add(element);form.Controls.Add(otherHost);form.Controls.Add(host);form.Show();Application.DoEvents();
                using(var capture=new FrontendCapture(element))
                using(var hold=new InspectionDisplayHold(host))
                using(var other=new InspectionDisplayHold(otherHost))
                {
                    view.Publish(10);Application.DoEvents();BitmapSource first;string message;
                    Check(capture.TryGetSnapshot(10,"procedure-test",out first,out message),"first failed frame captured");
                    int token=hold.Reserve("Camera 1",new NgCaptureEventArgs{Frame=10,Outcome="NG",TimeUtc=DateTime.UtcNow});
                    hold.SetImage(token,first,"NG",10);Application.DoEvents();
                    Check(hold.IsHeld && !other.IsHeld,"only failed camera frozen");
                    var dialog=Field<Form>(hold,"_dialog");Check(dialog.Visible && !dialog.Modal,"confirmation does not block processing");
                    Check(element.Visible && view.IsVisible && form.Enabled,"SDK display remains enabled and visible under cover");
                    var shown=Field<PictureBox>(hold,"_picture").Image;
                    Check(((Bitmap)shown).GetPixel(50,50).R>240,"frozen image is first NG frame");
                    view.ImageView.ChangeFirstColor(System.Windows.Media.Brushes.Yellow);view.Publish(11);Application.DoEvents();
                    BitmapSource next;Check(capture.TryGetSnapshot(11,"procedure-test",out next,out message),"later trigger still renders and captures while held");
                    int ignored=hold.Reserve("Camera 1",new NgCaptureEventArgs{Frame=11,Outcome="ERROR",TimeUtc=DateTime.UtcNow});
                    hold.SetImage(ignored,next,"ERROR",11);
                    Check(ignored==0 && ReferenceEquals(shown,Field<PictureBox>(hold,"_picture").Image),"later failure cannot replace held frame or stack dialogs");
                    var store=new NgHistoryStore(root);
                    var record=store.Save(FrontendCapture.EncodeJpeg(next),new NgRecord{Frame=11,Outcome="ERROR",Detail="module failure",TimeUtc=DateTime.UtcNow},".jpg");
                    Check(store.LoadRecent(10).Single().Outcome=="ERROR" && store.LoadRecent(10).Single().Detail=="module failure","ERROR saved and restored while screen held");
                    int otherToken=other.Reserve("Camera 2",new NgCaptureEventArgs{Frame=11,Outcome="ERROR",TimeUtc=DateTime.UtcNow});other.SetImage(otherToken,next,"ERROR",11);
                    dialog.Controls.OfType<Button>().Single().PerformClick();Application.DoEvents();
                    Check(!hold.IsHeld && other.IsHeld,"confirm resumes only selected camera");
                    Check(element.Visible && !Field<Panel>(hold,"_cover").Visible,"confirm reveals live SDK display");
                    hold.SetImage(token,first,"NG",10);Check(!hold.IsHeld,"late snapshot cannot refreeze an acknowledged event");
                    int fresh=hold.Reserve("Camera 1",new NgCaptureEventArgs{Frame=12,Outcome="ERROR",TimeUtc=DateTime.UtcNow.AddMilliseconds(1)});
                    Check(fresh!=0 && hold.IsHeld,"next failure requires a new acknowledgement");
                    var missing=FrontendCapture.ErrorWithoutImage(12,"camera error");hold.SetImage(fresh,missing,"ERROR",12);
                    Check(missing.IsFrozen && missing.PixelWidth==960,"missing-frame ERROR gets explicit notice instead of another frame");
                    var missingRecord=store.Save(FrontendCapture.EncodeJpeg(missing),new NgRecord{Frame=12,Outcome="ERROR",ViewName="검사 사진 없음",TimeUtc=DateTime.UtcNow},".jpg");
                    Check(store.LoadRecent(10).Any(r=>r.Frame==12 && r.ViewName=="검사 사진 없음"),"missing-image error remains reviewable in history");
                    hold.Reset();Check(!hold.IsHeld,"recipe reset clears hold");
                    using(var strip=new NgFilmStrip(store))
                    {
                        strip.AddRecord(record);var items=Field<FlowLayoutPanel>(strip,"_items");
                        Check(items.Controls[0].Controls.OfType<Label>().Single().Text.StartsWith("ERROR"),"filmstrip visibly labels ERROR");
                    }
                }
            }
            Console.WriteLine("PASS: "+checks+" failure capture and display-only hold checks");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
