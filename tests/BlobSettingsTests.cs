using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SC6000DualMonitor;
class BlobSettingsTests
{
    static int checks;
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    static void Throws(Action action, string text) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(text); }
    class Fake : IBlobSettingsSession
    {
        public BlobSettings Values = new BlobSettings { Low = 0, High = 80, MinArea = 100, MaxArea = 150000, AreaEnabled = true, ThresholdMode = 3 };
        public string TargetDescription { get { return "192.168.10.51 | Proc1 / Blob Analysis1 (ID 18)"; } }
        public int Writes, Saves, Reads, FailReadAt;
        public bool Paused, Resumed, FailFirstWrite, FailAllWrites, FailFirstSave, MismatchOnce, ChangeOnPause;
        public BlobSettings Read() { Reads++; if (Reads == FailReadAt) throw new InvalidOperationException("read failed"); var v=Values.Copy(); if(MismatchOnce && Writes>0) { MismatchOnce=false; v.High=255; } return v; }
        public void Write(BlobSettings v) { Writes++; Values.Low=v.Low; if(FailAllWrites || (FailFirstWrite && Writes==1)) throw new InvalidOperationException("write failed"); Values=v.Copy(); }
        public void Pause() { Paused=true; if(ChangeOnPause) Values.High=90; }
        public void Save() { Saves++; if(FailFirstSave && Saves==1) throw new InvalidOperationException("save failed"); }
        public void Resume() { Paused=false; Resumed=true; }
    }
    static BlobSettings Changed(Fake f) { var v=f.Values.Copy(); v.Low=20; v.High=100; v.MinArea=200; v.MaxArea=90000; v.AreaEnabled=false; return v; }
    static void Logic()
    {
        var f=new Fake(); var baseline=f.Read(); var requested=Changed(f);
        BlobSettingsController.Apply(f,baseline,requested); Check(f.Values.Same(requested)&&f.Saves==1&&f.Resumed,"readback/save/resume");
        f=new Fake(); baseline=f.Read(); requested=Changed(f); requested.Low=101;
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"inverted threshold"); Check(f.Writes==0&&!f.Paused,"invalid input untouched");
        requested=Changed(f); requested.High=256; Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"threshold bounds");
        requested=Changed(f); requested.MinArea=0; Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"area bounds");
        requested=Changed(f); requested.MinArea=requested.MaxArea+1; Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"area inversion");
        f=new Fake(); baseline=f.Read(); requested=Changed(f); f.Values.High=81;
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"stale edit"); Check(f.Writes==0,"stale edit no writes");
        f=new Fake{ChangeOnPause=true}; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"change during pause"); Check(f.Writes==0&&f.Resumed&&f.Values.High==90,"preserve external change");
        f=new Fake{FailFirstWrite=true}; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"partial write"); Check(f.Values.Same(baseline)&&f.Resumed&&f.Saves==0,"partial rollback");
        f=new Fake{FailFirstSave=true}; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"save failure"); Check(f.Values.Same(baseline)&&f.Saves==2&&f.Resumed,"persist rollback");
        f=new Fake{MismatchOnce=true}; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"readback mismatch"); Check(f.Values.Same(baseline)&&f.Resumed,"mismatch rollback");
        f=new Fake{FailAllWrites=true}; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"rollback failure"); Check(f.Paused&&!f.Resumed,"failed recovery stays paused");
        f=new Fake(); f.Values.ThresholdMode=2; baseline=f.Read(); requested=Changed(f);
        Throws(()=>BlobSettingsController.Apply(f,baseline,requested),"other mode cannot edit low/high");
        requested=baseline.Copy(); requested.AreaEnabled=false; BlobSettingsController.Apply(f,baseline,requested);
        Check(!f.Values.AreaEnabled&&f.Values.High==80&&f.Values.ThresholdMode==2,"area allowed without changing threshold mode");
    }
    static object Field(object form,string name) { return form.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form); }
    static object Call(object form,string name) { return form.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null); }
    [STAThread] static void Main(string[] args)
    {
        Logic();
        var f=new Fake(); var targets=new List<BlobEditorTarget>{new BlobEditorTarget{Name="Blob Analysis1",DisplayName="Blob Analysis1 (ID 18)",Session=f}};
        using(var form=new BlobSetupForm("1번 카메라",()=>targets,""))
        {
            Call(form,"Discover"); Check(((ComboBox)Field(form,"_modules")).SelectedIndex==0,"single module auto-selected");
            Check(((NumericUpDown)Field(form,"_high")).Value==80,"camera value shown");
            ((NumericUpDown)Field(form,"_high")).Value=100; Call(form,"Apply"); Check(f.Values.High==100&&f.Saves==1,"form apply");
            f.Values.High=110; Call(form,"ReadCamera"); Check(((NumericUpDown)Field(form,"_high")).Value==110,"form reload");
            f.Values.ThresholdMode=2; Call(form,"ReadCamera"); Check(!((NumericUpDown)Field(form,"_low")).Enabled,"non-double disables thresholds");
            f.Values.ThresholdMode=3; Call(form,"ReadCamera");
            form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Application.DoEvents(); form.PerformLayout();
            if(args.Length>0) using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height)); bitmap.Save(args[0]); }
            form.Hide();
            f.FailReadAt=f.Reads+1; Check(!(bool)Call(form,"ReadCamera")&&!((Button)Field(form,"_apply")).Enabled,"read failure disables save");
        }
        var second=new Fake(); targets.Add(new BlobEditorTarget{Name="Blob Analysis2",DisplayName="Blob Analysis2 (ID 21)",Session=second});
        using(var form=new BlobSetupForm("1번 카메라",()=>targets,"")) { Call(form,"Discover"); Check(((ComboBox)Field(form,"_modules")).SelectedIndex==-1,"multiple modules require selection"); }
        using(var form=new BlobSetupForm("2번 카메라",()=>targets,"Blob Analysis2")) {
            Call(form,"Discover"); Check(((ComboBox)Field(form,"_modules")).SelectedIndex==1,"configured module selected");
            ((NumericUpDown)Field(form,"_high")).Value=120; Call(form,"Apply"); Check(second.Values.High==120&&f.Values.High==110,"independent target values");
        }
        using(var form=new BlobSetupForm("1번 카메라",()=>targets,"wrong")) { Call(form,"Discover"); Check(((ComboBox)Field(form,"_modules")).SelectedIndex==-1,"invalid name does not choose wrong module"); }
        using(var form=new BlobSetupForm("1번 카메라",()=>new List<BlobEditorTarget>(),"")) { Call(form,"Discover"); Check(!((Button)Field(form,"_apply")).Enabled,"no module cannot apply"); }
        Console.WriteLine("PASS: "+checks+" BLOB controller and UI checks");
    }
}
