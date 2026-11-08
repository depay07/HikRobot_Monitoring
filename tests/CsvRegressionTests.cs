using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
class CsvRegressionTests
{
    static Assembly app;
    static BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static int checks;
    static Type T(string name) { return app.GetType("SC6000DualMonitor." + name, true); }
    static object Field(object obj, string name) { return obj.GetType().GetField(name,F).GetValue(obj); }
    static void Set(object obj, string name, object value) { obj.GetType().GetField(name,F).SetValue(obj,value); }
    static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name,F).Invoke(obj,args); }
    static void Check(bool value,string text) { if(!value)throw new Exception(text);checks++; }
    static void Wait(Func<bool> value,string text) { for(int i=0;i<3000 && !value();i++)Thread.Sleep(10);Check(value(),text); }
    static object Capture(List<object> rows)
    {
        var type=T("CsvModuleCapture");var result=FormatterServices.GetUninitializedObject(type);
        foreach(string name in new[]{"_gate","_modules","_pending","_emitted","_emittedOrder","_subscriptions"})
        {var f=type.GetField(name,F);f.SetValue(result,Activator.CreateInstance(f.FieldType));}
        Set(result,"_names",new[]{"Top","Middle","Bottom","Circle"});
        var ev=type.GetEvent("SampleReady");
        ev.AddEventHandler(result,Delegate.CreateDelegate(ev.EventHandlerType,rows,typeof(List<object>).GetMethod("Add")));
        return result;
    }
    static void Finish(object capture,uint frame,string outcome) { Call(capture,"ProcedureFinished",frame,Enum.Parse(T("InspectionOutcome"),outcome)); }
    static void Values(object capture,uint frame,string prefix)
    {var p=Call(capture,"GetPending",frame);var values=(string[])Field(p,"Values");for(int i=0;i<4;i++)values[i]=prefix+i;}
    static void Callback(object capture, object sender, uint frame, string name, string value, byte self)
    {
        Type type=Assembly.Load("VM.PlatformSDKCS").GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_RESULT_INFO_LIST_EX_Data",true);
        object result=Activator.CreateInstance(type);Set(result,"nExecuteCount",frame);
        var selfField=type.GetField("bIsSelfExecute");selfField.SetValue(result,Convert.ChangeType(self,selfField.FieldType));
        Type fieldType=type.GetField("pInfo").FieldType.GetElementType();
        object field=Activator.CreateInstance(fieldType);Set(field,"strParamName",name);
        Type stringType=fieldType.GetField("pStringValue").FieldType.GetElementType();
        object str=Activator.CreateInstance(stringType);Set(str,"strValue",value);
        var strings=Array.CreateInstance(stringType,1);strings.SetValue(str,0);Set(field,"pStringValue",strings);
        var fields=Array.CreateInstance(fieldType,1);fields.SetValue(field,0);Set(result,"pInfo",fields);
        var args=Activator.CreateInstance(Assembly.Load("VMControls.Interface").GetType("VMControls.Interface.ValueEventArgs",true));Set(args,"Value",result);
        Call(capture,"OnModuleResult",sender,args);
    }
    static object Sample(uint frame)
    {
        var s=Activator.CreateInstance(T("CsvInspectionSample"));Set(s,"Frame",frame);Set(s,"Time",DateTime.Now);
        Set(s,"Values",new[]{"-1.25","4.25","5.0","6.5"});Set(s,"Result","NG");Set(s,"Detail","OK");return s;
    }
    [STAThread] static int Main(string[] args)
    { try { Run(args); return 0; } catch(Exception ex) { while(ex != null) { Console.WriteLine(ex.GetType().FullName + ": " + ex.Message); Console.WriteLine(ex.StackTrace); ex=ex.InnerException; } return 1; } }
    static void Run(string[] args)
    {
        string sdk=Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        app=Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var rows=new List<object>();var c=Capture(rows);
        Values(c,10,"a");Values(c,11,"b");Finish(c,11,"Ng");Finish(c,10,"Ok");Finish(c,10,"Ok");
        Check(rows.Count==2,"one row per completed frame despite duplicate callback");
        Check((uint)Field(rows[0],"Frame")==11 && ((string[])Field(rows[0],"Values"))[0]=="b0","no cross-frame values");
        Check((string)Field(rows[1],"Result")=="OK","OK classification retained");
        Finish(c,12,"Error");
        var pending=Call(c,"GetPending",(uint)12);Set(pending,"CreatedUtc",DateTime.UtcNow.AddSeconds(-3));Call(c,"CheckExpired",new object[]{null});
        Check(rows.Count==3 && (string)Field(rows[2],"Result")=="ERROR","ERROR emits row without measurements");
        Check(((string)Field(rows[2],"Detail")).StartsWith("MISSING:"),"missing measurements explicitly labelled");
        Finish(c,13,"Ng");((IDisposable)c).Dispose();Check(rows.Count==4,"stop flushes completed pending row");
        var callbackRows=new List<object>();var callbackCapture=Capture(callbackRows);
        var senders=new[]{new object(),new object(),new object(),new object()};
        var modules=(IDictionary)Field(callbackCapture,"_modules");for(int i=0;i<4;i++)modules.Add(senders[i],i);
        Finish(callbackCapture,20,"Ng");
        for(int i=0;i<4;i++)Callback(callbackCapture,senders[i],20,"format_out",(i+1).ToString(),0);
        Check(callbackRows.Count==1 && ((string[])Field(callbackRows[0],"Values"))[3]=="4","SDK format_out callbacks arriving after procedure join by frame");
        Finish(callbackCapture,21,"Ok");
        for(int i=0;i<4;i++)Callback(callbackCapture,senders[i],21,"StrResult","wrong",0);
        Check(callbackRows.Count==1,"unrelated string output is never used as a measurement");
        for(int i=0;i<4;i++)Callback(callbackCapture,senders[i],21,"format_out","manual",1);
        Check(callbackRows.Count==1,"self-run callbacks excluded");
        ((IDisposable)callbackCapture).Dispose();
        var moduleSdk=Assembly.LoadFrom(Path.Combine(sdk,"FormatModuleCs.dll"));
        Check(moduleSdk.GetType("FormatModuleCs.FormatResult").GetProperty("format_out")!=null,"installed SDK Format output contract");
        var map=Activator.CreateInstance(T("CsvModuleMapping"),new object[]{new[]{"a","b","c","d"}});
        var store=Activator.CreateInstance(T("CsvMappingStore"),new object[]{"csv-test-"+Guid.NewGuid().ToString("N")});
        Call(store,"Write","Proc1","/recipeA.solx",map);
        Check(Call(store,"Read","Proc1","/recipeA.solx")!=null,"mapping saved");
        Check(Call(store,"Read","Proc1","/recipeB.solx")==null,"different recipe cannot inherit mapping");
        Check(Call(store,"Read","Proc2","/recipeA.solx")==null,"different procedure cannot inherit mapping");
        bool rejected=false;try{Call(map,"Validate",new object[]{new[]{"a","renamed","c","d"}});}catch(TargetInvocationException){rejected=true;}Check(rejected,"renamed module requires explicit remapping");
        string testRoot=Path.Combine(Path.GetFullPath(args[2]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
        string blocked=Path.Combine(testRoot,"data");File.WriteAllText(blocked,"deliberate blocked directory");
        var w=Activator.CreateInstance(T("CsvInspectionWriter"),new object[]{"csv-test"});Set(w,"_folder",blocked);
        Func<bool> error=()=> (bool)w.GetType().GetProperty("HasError").GetValue(w,null);
        Call(w,"Enqueue",Sample(1),"=bad,\"part\"","/recipeA.solx");Wait(error,"disk failure is visible");
        File.Delete(blocked);
        for(uint i=2;i<=1001;i++)Call(w,"Enqueue",Sample(i),"part","/recipeA.solx");
        Wait(()=>((long)Field(w,"_saved"))==1001,"burst 1000 rows drains without drops; failed row retries");
        ((IDisposable)w).Dispose();
        string[] lines=File.ReadAllLines(Directory.GetFiles(blocked,"*.csv")[0]);
        Check(lines.Length==1002,"header plus every row exactly once after retry");
        Check(lines[1].Contains(",1,") && lines[1].Contains(",-1.25,"),"failed first row retained; negative measurement remains numeric");
        Check(lines[1].Contains("\"'=bad,\"\"part\"\"\""),"formula and CSV quote escaping");
        string failedFolder=Path.Combine(testRoot,"shutdown-blocked");File.WriteAllText(failedFolder,"blocked");
        var recovery=Activator.CreateInstance(T("CsvInspectionWriter"),new object[]{"csv-recovery-test"});Set(recovery,"_folder",failedFolder);
        Call(recovery,"Enqueue",Sample(2000),"part","/recipeA.solx");
        Wait(()=>Field(recovery,"_error")!=null,"shutdown fault reproduced");
        Set(recovery,"_stop",true);((AutoResetEvent)Field(recovery,"_signal")).Set();
        Wait(()=>((string)Field(recovery,"_error")??"").StartsWith("미저장 결과 복구 파일: "),"shutdown failure creates a recovery CSV");
        string recoveryFile=((string)Field(recovery,"_error")).Substring("미저장 결과 복구 파일: ".Length);
        Check(File.ReadAllLines(recoveryFile).Length==2,"recovery CSV contains unsaved row");
        ((Thread)Field(recovery,"_worker")).Join();Set(recovery,"_error",null);((IDisposable)recovery).Dispose();
        var pane=(Control)Activator.CreateInstance(T("InspectionPane"),new object[]{null});
        pane.Size=new System.Drawing.Size(720,116);pane.CreateControl();pane.PerformLayout();
        Check(((Control)Field(pane,"_part")).Width>250,"CSV button leaves PART NO room in dual camera pane");
        Check(!((CheckBox)Field(pane,"_csvEnabled")).Checked,"CSV opt-in; default disabled");
        pane.Dispose();
        Console.WriteLine("PASS: "+checks+" CSV regression checks; artifacts: "+testRoot);
    }
}
