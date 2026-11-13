using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
class CalibrationMappingUiTests {
 static Assembly app,calc;static int checks;
 const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static void Check(bool b,string m){if(!b)throw new Exception(m);checks++;}
 static void Set(object o,string n,object v){for(Type t=o.GetType();t!=null;t=t.BaseType){var f=t.GetField(n,F|BindingFlags.DeclaredOnly);if(f!=null){f.SetValue(o,v);return;}}o.GetType().GetProperty(n,F).SetValue(o,v,null);}
 static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
 static object New(string type){return Activator.CreateInstance(app.GetType("SC6000DualMonitor."+type));}
 static Form Open(int mode) {
  var catalog=FormatterServices.GetUninitializedObject(app.GetType("SC6000DualMonitor.CalibrationCatalog"));
  Set(catalog,"Camera","192.168.10.51");Set(catalog,"Procedure","Proc1");Set(catalog,"Recipe","/root/solution/inspection.solx");
  foreach(string name in new[]{"Globals","Sources","Calculators"})Set(catalog,name,Activator.CreateInstance(catalog.GetType().GetField(name,F).FieldType));
  var mapping=New("CalibrationMapping");Set(mapping,"Camera","192.168.10.51");Set(mapping,"Procedure","Proc1");Set(mapping,"Recipe","/root/solution/inspection.solx");
  var rows=Array.CreateInstance(app.GetType("SC6000DualMonitor.CalibrationMappingRow"),4);
  for(int i=0;i<4;i++){
   var module=FormatterServices.GetUninitializedObject(calc.GetType("CalculatorModuleCs.CalculatorModuleTool"));Set(module,"name",i==3?"원 사이즈검사":"L2L Measurement"+(i+1));Set(module,"ID",(uint)i+2);
   var source=New("CalibrationSource");Set(source,"Module",module);Set(source,"Field",i==3?"CircleRadius":"L2LAbsDist");((IList)Get(catalog,"Sources")).Add(source);
   var row=New("CalibrationMappingRow");Set(row,"SourceId",(uint)i+2);Set(row,"SourceName",module.GetType().GetProperty("Name").GetValue(module,null));Set(row,"Field",i==3?"CircleRadius":"L2LAbsDist");Set(row,"Label",new[]{"CB","WIRE","INS","CIRCLE"}[i]);Set(row,"Variable",new[]{"cb_scale","wire_scale","ins_scale","circle_scale"}[i]);
   Set(row,"CircleFormula",Enum.ToObject(app.GetType("SC6000DualMonitor.CircleCalibrationFormula"),i==3?mode:0));rows.SetValue(row,i);
  }
  Set(mapping,"Rows",rows);
  return (Form)Activator.CreateInstance(app.GetType("SC6000DualMonitor.CalibrationMappingForm"),new[]{catalog,mapping,""});
 }
 [STAThread]static int Main(string[] args){try{
  string sdk=Path.GetFullPath(args[1]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  app=Assembly.LoadFrom(Path.GetFullPath(args[0]));calc=Assembly.LoadFrom(Path.Combine(sdk,"CalculatorModuleCs.dll"));
  foreach(int mode in new[]{0,1,2})using(var form=Open(mode)){
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
   var choices=(ComboBox[])Get(form,"_circleFormulas");var sources=(ComboBox[])Get(form,"_sources");
   Check(choices[3].Enabled,"circle choice enabled");Check(!choices[0].Enabled && !choices[1].Enabled && !choices[2].Enabled,"distance rows disable circle mode");Check(choices[3].SelectedIndex==mode-1,"legacy remains unselected and explicit modes restored: "+mode);
   var labels=(TextBox[])Get(form,"_labels");Check(labels[3].Text=="CIRCLE" && sources[3].SelectedIndex==3,"previous labels and source survive migration");
   choices[3].SelectedIndex=0;Check(choices[3].Text.Contains("×2 없음"),"legacy choice text explicit");
   if(mode==1 && args.Length>2)using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(args[2]);}
   sources[3].SelectedIndex=0;Check(!choices[3].Enabled && choices[3].SelectedIndex==-1,"changing to distance clears stale circle mode");
   sources[3].SelectedIndex=3;Check(choices[3].Enabled && choices[3].SelectedIndex==-1,"changing back requires fresh selection");
  }
  Console.WriteLine("PASS: "+checks+" calibration mapping UI checks (no camera calls)");return 0;
 }catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
