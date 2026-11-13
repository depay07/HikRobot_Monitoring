using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using SC6000DualMonitor;
class CalibrationTests
{
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Reject(Action action,string message){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(message);}
    static CalibrationModel Model(){return new CalibrationModel(new[]{"윗 폭","중간 폭","아래 폭","구멍 지름"},new[]{"top_scale","middle_scale","bottom_scale","circle_scale"},new[]{.021,.022,.023,.024});}
    class Fake : ICalibrationEditorSession
    {
        public CalibrationModel Baseline{get;set;}
        public Dictionary<string,double> Values=new Dictionary<string,double>();
        public List<string> Events=new List<string>();
        public bool FailWrite,FailSave,FailEverySave,ChangeDuringSave,FailRollback,IgnoreWrite,ChangedRecipe,ChangeAfterPause,Paused,Armed;
        public int Writes; public CalibrationFrame Next;
        public string Description{get{return "192.168.10.51 | Proc1 / recipe.solx";}}
        public bool[] RadiusInputs{get{return new[]{false,false,false,true};}}
        public Fake(CalibrationModel baseline=null){Baseline=baseline??Model();for(int i=0;i<4;i++)Values[Baseline.Variables[i]]=Baseline.Factors[i];Values["unrelated_counter"]=99;}
        public CalibrationModel ReadModel(){if(ChangedRecipe)throw new InvalidOperationException("recipe changed");Events.Add("read");return new CalibrationModel(Baseline.Names,Baseline.Variables,Baseline.Variables.Select(v=>Values[v]).ToArray());}
        public void WriteModel(CalibrationModel model){if(ChangedRecipe)throw new InvalidOperationException("recipe changed");Events.Add("write");Writes++;if(FailRollback&&Writes>1)throw new Exception("restore failed");for(int i=0;i<4;i++){if(!IgnoreWrite||Writes>1)Values[model.Variables[i]]=model.Factors[i];if(FailWrite&&Writes==1&&i==1)throw new Exception("partial write");}}
        public void Pause(){Paused=true;Events.Add("pause");if(ChangeAfterPause)ChangedRecipe=true;}
        public void Resume(){Paused=false;Events.Add("resume");}
        public void Save(){Events.Add("save");if(ChangeDuringSave){ChangeDuringSave=false;Values[Baseline.Variables[0]]=.123;}if(FailSave||FailEverySave){FailSave=false;throw new Exception("save failed");}}
        public void Arm(){Armed=true;} public void CancelRead(){Armed=false;}
        public CalibrationFrame TakeFrame(){var frame=Next;Next=null;return frame;}public void Dispose(){}
    }
    static T Field<T>(object o,string n){return (T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Call(object o,string n){o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,null);}
    static CalibrationMapping Mapping(){return new CalibrationMapping{Camera="192.168.10.51",Procedure="Proc1",Recipe="/root/A.solx",GlobalModuleId=13000,GlobalModuleName="Global Variable1",Rows=Enumerable.Range(0,4).Select(i=>new CalibrationMappingRow{Label="크기 "+i,Variable="scale"+i,SourceId=(uint)i+2,SourceName="측정"+i,Field=i==3?"CircleRadius":"L2LAbsDist"}).ToArray()};}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            var baseline=Model();var pixels=new[]{500d,225,250,325};var actual=new[]{14d,4.5,5,6.5};
            var wanted=baseline.Calculate(pixels,actual);
            Check(wanted.Factors[0]==(double).028f,"14 / 500 canonical camera float");
            Check(wanted.Factors[3]==(double).02f,"6.5 / raw radius 325, no extra doubling");
            Check(wanted.Names.SequenceEqual(baseline.Names),"user names preserved");
            Check(wanted.Variables.SequenceEqual(baseline.Variables),"selected variable names preserved");
            var copy=new CalibrationModel(wanted.Names,wanted.Variables,wanted.Factors);Check(wanted.Same(copy),"float readback equality");
            var names=(string[])baseline.Names.Clone(); var vars=(string[])baseline.Variables.Clone();var factors=(double[])baseline.Factors.Clone();var isolated=new CalibrationModel(names,vars,factors); names[0]="changed";vars[0]="changed";factors[0]=12;Check(isolated.Same(baseline),"snapshot clones inputs");
            foreach(double invalid in new[]{0d,-1,double.NaN,double.PositiveInfinity})
            {
                var bad=(double[])pixels.Clone();bad[0]=invalid;Reject(()=>baseline.Calculate(bad,actual),"bad pixels");
                var size=(double[])actual.Clone();size[1]=invalid;Reject(()=>baseline.Calculate(pixels,size),"bad actual");
            }
            Reject(()=>baseline.Calculate(new[]{double.Epsilon,1d,1d,1d},actual),"overflow");
            Reject(()=>baseline.Calculate(new[]{double.MaxValue,1d,1d,1d},actual),"underflow");
            Reject(()=>new CalibrationModel(baseline.Names,new[]{"a","a","c","d"},baseline.Factors),"duplicate vars");
            Reject(()=>baseline.Calculate(new[]{1d},actual),"incomplete four measurements");
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(baseline.Calculate(pixels,actual).Same(wanted),"culture independent math");CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ko-KR");
            var f=new Fake();CalibrationController.Apply(f,baseline,wanted);Check(f.ReadModel().Same(wanted),"all four coefficients applied");Check(f.Values["unrelated_counter"]==99,"unrelated global untouched");Check(!f.Paused && f.Events.Count(e=>e=="save")==1,"save then resume");
            f=new Fake{FailWrite=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"partial write fault");Check(f.ReadModel().Same(baseline)&&!f.Paused,"partial write fully restored");Check(!f.Events.Contains("save"),"partial write never saved");
            f=new Fake{FailSave=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"save fault");Check(f.ReadModel().Same(baseline)&&f.Events.Count(e=>e=="save")==2,"restore and resave original after save failure");
            f=new Fake{FailEverySave=true};
            string restoreMessage="";try{CalibrationController.Apply(f,baseline,wanted);}catch(InvalidOperationException ex){restoreMessage=ex.Message;}
            Check(f.Paused&&f.ReadModel().Same(baseline),"runtime restore succeeds but repeated save failure stays paused");
            Check(restoreMessage.Contains("이전 실행 보정계수는 복원했으나 레시피 저장 확인에 실패"),"report runtime restoration separately from persistence failure");
            f=new Fake{ChangeDuringSave=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"save changes runtime coefficient");
            Check(f.ReadModel().Same(baseline)&&f.Events.Count(e=>e=="save")==2&&!f.Paused,"post-save runtime mismatch is restored and resaved");
            f=new Fake{IgnoreWrite=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"readback mismatch");Check(f.ReadModel().Same(baseline)&&!f.Events.Contains("save"),"readback mismatch prevents save");
            f=new Fake{FailWrite=true,FailRollback=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"rollback fault");Check(f.Paused,"failed recovery stays paused");
            f=new Fake{ChangedRecipe=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"recipe switched");Check(f.Writes==0,"no writes to changed recipe");
            f=new Fake{ChangeAfterPause=true};Reject(()=>CalibrationController.Apply(f,baseline,wanted),"recipe switches while pausing");Check(f.Writes==0,"prewrite revalidation");
            f=new Fake();f.Values[baseline.Variables[0]]=.9;Reject(()=>CalibrationController.Apply(f,baseline,wanted),"external coefficient edit");Check(f.Writes==0,"external change not overwritten");
            var wrong=new CalibrationModel(baseline.Names,new[]{"x","y","z","q"},baseline.Factors);f=new Fake();Reject(()=>CalibrationController.Apply(f,baseline,wrong),"wrong targets");Check(f.Events.Count==0,"target rejection before device access");
            // Field report: non-round float coefficients must survive apply/rollback without
            // rounding them to the decimal text shown in the variable inventory or UI.
            var fieldBaseline=new CalibrationModel(new[]{"CB","WIRE","INS","CIRCLE"},new[]{"cb_scale","wire_scale","ins_scale","circle_scale"},new[]{(double).020872f,.021026f,.020883f,.0420804382f});
            var fieldPixels=new[]{(double)(float)680.493164,(double)(float)141.59021,(double)(float)200.405609,(double)(float)154.4319305};
            var fieldActual=new[]{14.28,2.92,4.2,6.49};var fieldWanted=fieldBaseline.Calculate(fieldPixels,fieldActual);
            for(int i=0;i<4;i++)Check(Math.Abs(fieldPixels[i]*fieldWanted.Factors[i]-fieldActual[i])<.000002,"reported dimensions recover actual size after float cast "+i);
            Check(fieldWanted.Factors[0]!=(double)float.Parse(((float)fieldWanted.Factors[0]).ToString("F6",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture),"six-place display text cannot verify exact runtime float");
            f=new Fake(fieldBaseline);CalibrationController.Apply(f,fieldBaseline,fieldWanted);
            Check(f.ReadModel().Same(fieldWanted)&&!f.Paused,"reported non-round coefficients save with exact float readback");
            f=new Fake(fieldBaseline){FailSave=true};Reject(()=>CalibrationController.Apply(f,fieldBaseline,fieldWanted),"reported coefficients save fault");
            Check(f.ReadModel().Same(fieldBaseline)&&!f.Paused,"circle baseline restored exactly without decimal text rounding");
            // Field regression: the existing recipe is raw radius * circle_scale.
            // 6.49 / 154.4319305 must save ~0.042025, not the former half coefficient.
            Check(Math.Abs(fieldWanted.Factors[3]-.042024988681078)<.00000001,"circle saves full coefficient without extra diameter conversion");
            Check(Math.Abs(154.4319305*fieldWanted.Factors[3]-6.49)<.000002,"existing radius-times-scale recipe returns requested diameter");
            Check(Math.Abs(154.4319305*(fieldWanted.Factors[3]/2)-3.245)<.000002,"regression reproduces previous half-size defect");
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
            string oldJson=serializer.Serialize(Mapping());
            foreach(int oldMode in new[]{0,1,2})
            {
                string legacy=oldJson.Replace("\"Field\":\"CircleRadius\"","\"Field\":\"CircleRadius\",\"CircleFormula\":"+oldMode);
                var migrated=serializer.Deserialize<CalibrationMapping>(legacy);
                migrated.Validate();
                Check(migrated.Rows[3].Label==Mapping().Rows[3].Label && migrated.Rows[3].Variable==Mapping().Rows[3].Variable,"legacy wiring preserved without new selection "+oldMode);
                Check(!serializer.Serialize(migrated).Contains("CircleFormula"),"old circle option no longer persisted "+oldMode);
            }
            serializer.Deserialize<CalibrationMapping>(oldJson).Validate();
            var collector=new CalibrationFrameCollector();Check(collector.Add(1,0,500)==null,"wait for others");collector.Add(2,1,225);collector.Add(1,2,250);Check(collector.Add(1,3,325)==null,"no mixed execution counts");var frame=collector.Add(1,1,225);Check(frame.Frame==1&&frame.Pixels.SequenceEqual(pixels),"same frame join");collector.Clear();Check(collector.Add(3,0,double.NaN)==null,"invalid callback ignored");
            for(uint i=0;i<1000;i++)collector.Add(i,0,1);Check(collector.Add(999,1,2)==null,"bounded collector");
            var mapping=Mapping();mapping.Validate();Check(mapping.Matches("192.168.10.51","Proc1","/root/A.solx"),"mapping identity");Check(!mapping.Matches("192.168.10.52","Proc1","/root/A.solx"),"other camera rejected");
            string key=CalibrationMappingStore.Key(mapping.Camera,mapping.Procedure,mapping.Recipe);
            Check(key!=CalibrationMappingStore.Key("192.168.10.52",mapping.Procedure,mapping.Recipe),"camera-isolated key");Check(key!=CalibrationMappingStore.Key(mapping.Camera,mapping.Procedure,"/root/B.solx"),"recipe-isolated key");Check(key!=CalibrationMappingStore.Key(mapping.Camera,"Proc2",mapping.Recipe),"flow-isolated key");
            mapping.Rows[1].Variable=mapping.Rows[0].Variable;Reject(mapping.Validate,"duplicate mapping variable");mapping=Mapping();mapping.Rows[1].SourceId=mapping.Rows[0].SourceId;Reject(mapping.Validate,"duplicate measurement source");mapping=Mapping();mapping.Rows[0].Field="CalibratedDistance";Reject(mapping.Validate,"scaled measurement not raw pixel");
            f=new Fake();using(var form=new CalibrationForm(f,"카메라 1"))
            {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();Call(form,"BeginRead");Check(f.Armed,"UI arms reference capture");f.Next=frame;Call(form,"Poll");var inputs=Field<NumericUpDown[]>(form,"_actual");for(int i=0;i<4;i++)inputs[i].Value=(decimal)actual[i];Check(Field<Button>(form,"_apply").Enabled,"UI enables only with reference and four sizes");Check(Field<CalibrationModel>(form,"_requested").Same(wanted),"UI computes four globals");
                inputs[0].Value=0;Check(!Field<Button>(form,"_apply").Enabled,"invalid input disables apply");inputs[0].Value=14;
                if(args.Length>0)using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[0]);}
            }
            f=new Fake(fieldBaseline);using(var form=new CalibrationForm(f,"카메라 1 · 반지름 방식"))
            {
                Call(form,"BeginRead");var raw=(double[])fieldPixels.Clone();raw[3]=154.4319305;
                f.Next=new CalibrationFrame{Frame=10,Pixels=raw,Time=DateTime.Now};Call(form,"Poll");
                var inputs=Field<NumericUpDown[]>(form,"_actual");for(int i=0;i<4;i++)inputs[i].Value=(decimal)fieldActual[i];
                Check(Field<Label[]>(form,"_pixels")[3].Text.Contains("반지름"),"UI identifies raw radius denominator");
                Check(Math.Abs(Field<CalibrationModel>(form,"_requested").Factors[3]*raw[3]-6.49)<.000002,"UI actual diameter input generates legacy radius coefficient");
                Check(Field<Button>(form,"_apply").Enabled,"radius mode is usable in calibration UI");
            }
            Console.WriteLine("PASS: "+checks+" global calibration, transaction, mapping, callback join and UI checks (no camera writes)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
