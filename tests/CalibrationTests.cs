using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SC6000DualMonitor;
class CalibrationTests
{
    static int checks;
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;}
    static void Reject(Action action,string text){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(text);}
    static string[] Fixture()
    {
        string[] factors={"0.02079658287","0.02109399915","0.02041996395","0.0421407222"};
        return Enumerable.Range(0,4).Select(i=>new JavaScriptSerializer().Serialize(new Dictionary<string,object> {
            {"Name",CalibrationModel.Names[i]}, {"Expression","<float:<base64 "+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CalculatorInput"+i))+" />>[0]*"+factors[i]},
            {"Initial",0}, {"Summation",true}, {"Index",i}, {"Type","float"}, {"RemarkInfo","preserve annotation"} })).ToArray();
    }
    class Fake : ICalibrationEditorSession
    {
        public CalibrationModel Current; public CalibrationModel Baseline {get;set;}
        public string Description {get{return "192.168.10.51 | Proc1 / Variable Calculation1";}}
        public List<string> Events=new List<string>();public bool FailWrite, FailSave, FailRollback, CorruptRead, Armed;
        public int Writes;public CalibrationFrame Next;
        public CalibrationModel ReadModel(){Events.Add("read");return Current;}
        public void WriteModel(CalibrationModel m){Events.Add("write");Writes++;if(FailRollback&&Writes>1)throw new Exception("rollback fail");Current=m;if(FailWrite&&Writes==1)throw new Exception("partial write");if(CorruptRead&&Writes==1)Current=Baseline;}
        public void Pause(){Events.Add("pause");}public void Resume(){Events.Add("resume");}
        public void Save(){Events.Add("save");if(FailSave){FailSave=false;throw new Exception("disk fail");}}
        public void Arm(){Armed=true;}public void CancelRead(){Armed=false;}
        public CalibrationFrame TakeFrame(){var x=Next;Next=null;return x;}public void Dispose(){}
    }
    static T Field<T>(object o,string n){return (T)o.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o);}
    static void Call(object o,string n){o.GetType().GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(o,null);}
    [STAThread]static void Main(string[] args)
    {
        var baseline=new CalibrationModel(Fixture());var pixels=new[]{700d,225,250,162.5};var actual=new[]{14d,4.5,5,6.5};
        var requested=baseline.Calculate(pixels,actual);
        Check(Math.Abs(requested.Factors[0]-.02)<1e-12,"14mm / 700 pixels");
        Check(Math.Abs(requested.Factors[3]-.04)<1e-12,"diameter divided by radius; no extra factor two");
        Check(requested.Factors[1]==.02 && requested.Factors[2]==.02,"independent middle and bottom");
        Check(baseline.Factors[0]==.02079658287,"preview does not mutate baseline");
        Check(requested.JsonInOrder().All(j=>j.Contains("preserve annotation")&&j.Contains("Summation")),"preserve metadata");
        Check(baseline.Same(new CalibrationModel(Fixture())),"baseline round trip");
        Check(!baseline.Same(requested),"coefficient changes detected");
        Reject(()=>baseline.Calculate(new[]{0d,1,1,1},actual),"zero pixels rejected");
        Reject(()=>baseline.Calculate(new[]{double.NaN,1,1,1},actual),"NaN rejected");
        Reject(()=>baseline.Calculate(pixels,new[]{-1d,1,1,1}),"negative physical size rejected");
        Reject(()=>baseline.Calculate(pixels,new[]{double.PositiveInfinity,1,1,1}),"infinity rejected");
        var bad=Fixture();bad[0]=bad[0].Replace("*0.02079658287","+0.02079658287");Reject(()=>new CalibrationModel(bad),"unsupported formula not overwritten");
        var collector=new CalibrationFrameCollector();
        Check(collector.Add(10,0,700)==null,"partial waits");collector.Add(11,1,225);collector.Add(10,2,250);collector.Add(10,3,162.5);
        Check(collector.Add(11,0,700)==null,"different frame cannot complete frame 10");
        var frame=collector.Add(10,1,225);Check(frame.Frame==10&&frame.Pixels.SequenceEqual(pixels),"same frame joins four raw values");
        collector.Add(12,0,700);collector.Clear();collector.Add(12,1,225);collector.Add(12,2,250);Check(collector.Add(12,3,162.5)==null,"re-read clears stale samples");
        Check(collector.Add(12,0,-700)==null,"invalid detection cannot complete sample");
        var good=new Fake{Current=baseline,Baseline=baseline};CalibrationController.Apply(good,baseline,requested);
        Check(good.Current.Same(requested)&&good.Events.Count(e=>e=="save")==1&&good.Events.Last()=="resume","apply verify save resume");
        var stale=new Fake{Current=requested,Baseline=baseline};Reject(()=>CalibrationController.Apply(stale,baseline,requested),"stale baseline rejected");Check(!stale.Events.Contains("pause"),"stale settings not paused or written");
        var writeFail=new Fake{Current=baseline,Baseline=baseline,FailWrite=true};Reject(()=>CalibrationController.Apply(writeFail,baseline,requested),"partial write raises");Check(writeFail.Current.Same(baseline)&&writeFail.Events.Last()=="resume"&&!writeFail.Events.Contains("save"),"partial write rolls back");
        var saveFail=new Fake{Current=baseline,Baseline=baseline,FailSave=true};Reject(()=>CalibrationController.Apply(saveFail,baseline,requested),"save failure raises");Check(saveFail.Current.Same(baseline)&&saveFail.Events.Count(e=>e=="save")==2,"restore saved recipe after save failure");
        var recoveryFail=new Fake{Current=baseline,Baseline=baseline,FailWrite=true,FailRollback=true};Reject(()=>CalibrationController.Apply(recoveryFail,baseline,requested),"failed rollback raises");Check(!recoveryFail.Events.Contains("resume"),"do not resume with unknown coefficients");
        var mismatch=new Fake{Current=baseline,Baseline=baseline,CorruptRead=true};Reject(()=>CalibrationController.Apply(mismatch,baseline,requested),"readback mismatch raises");Check(!mismatch.Events.Contains("save"),"do not save unverified coefficients");
        var ui=new Fake{Current=baseline,Baseline=baseline};
        using(var form=new CalibrationForm(ui,"1번 카메라"))
        {
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
            var fields=Field<NumericUpDown[]>(form,"_actual");var apply=Field<Button>(form,"_apply");
            Check(fields.Length==4&&!apply.Enabled,"four physical inputs; no apply before snapshot");
            Call(form,"BeginRead");Check(ui.Armed,"UI requests fresh trigger");ui.Next=frame;Call(form,"Poll");
            for(int i=0;i<4;i++)fields[i].Value=(decimal)actual[i];
            Check(apply.Enabled,"valid four sizes and snapshot enable apply");
            fields[0].Value=0;Check(!apply.Enabled,"invalid edit disables apply");fields[0].Value=14;
            Check(Field<Label[]>(form,"_factors")[3].Text=="0.04","circle preview matches radius formula");
            if(args.Length>0)using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[0]);}
            Call(form,"BeginRead");Check(!apply.Enabled,"new read invalidates old preview");form.Close();
        }
        Console.WriteLine("PASS: "+checks+" calibration math, frame association, rollback, and UI checks (no camera calls)");
    }
}
