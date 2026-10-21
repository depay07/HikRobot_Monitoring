using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
class ConditionCallbackTests
{
    static BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    static object New(Type t) { return Activator.CreateInstance(t); }
    static void Set(object x, string n, object v) { x.GetType().GetField(n).SetValue(x,v); }
    [STAThread] static void Main(string[] args) { try { Run(args); } catch (Exception ex) { while(ex != null) { Console.WriteLine(ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace); ex = ex.InnerException; } Environment.Exit(1); } }
    static void Run(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
            string path=Path.Combine(@"C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly",new AssemblyName(e.Name).Name+".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var asm=Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var sdk=Assembly.Load("VM.PlatformSDKCS");
        var ui=Assembly.Load("VMControls.Interface");
        var sessionType=asm.GetType("SC6000DualMonitor.RemoteInspectionSession");
        var session=FormatterServices.GetUninitializedObject(sessionType);
        foreach(var n in new[]{"_callbackGate","_conditionFrames","_frameOrder"}) {
            var f=sessionType.GetField(n,F); f.SetValue(session,New(f.FieldType));
        }
        var d=asm.GetType("SC6000DualMonitor.ConditionDefinition");
        var defs=Array.CreateInstance(d,3);
        string[] names={"float1","int0","int1"}; string[] captions={"Middle_Size","홀 유무검사","Blob"};
        for(int i=0;i<3;i++) defs.SetValue(Activator.CreateInstance(d,new object[]{names[i],captions[i]}),i);
        sessionType.GetField("_activeDefinitions",F).SetValue(session,defs);
        var fieldType=sdk.GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_RESULT_INFO_EX_Data");
        var frameType=sdk.GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_RESULT_INFO_LIST_EX_Data");
        var fields=Array.CreateInstance(fieldType,4);
        for(int i=0;i<3;i++) {
            var item=New(fieldType); Set(item,"strParamName","%"+names[2-i]+"Out%");
            Set(item,"nValueNum",1); Set(item,"pIntValue",new[]{0}); fields.SetValue(item,i);
        }
        var overall=New(fieldType); Set(overall,"strParamName","StrResult");
        var stringType=fieldType.GetField("pStringValue").FieldType.GetElementType();
        var strings=Array.CreateInstance(stringType,1); var str=New(stringType); Set(str,"strValue","NG"); strings.SetValue(str,0);
        Set(overall,"pStringValue",strings); fields.SetValue(overall,3);
        var frame=New(frameType); Set(frame,"pInfo",fields); Set(frame,"nExecuteCount",42u);
        var evt=New(ui.GetType("VMControls.Interface.ValueEventArgs"));
        evt.GetType().GetField("Value").SetValue(evt,frame);
        var method=sessionType.GetMethod("OnConditionCompleted",F); method.Invoke(session,new[]{session,evt});
        var cache=(IDictionary)sessionType.GetField("_conditionFrames",F).GetValue(session);
        var result=cache[42u]; string detail=(string)result.GetType().GetProperty("Detail").GetValue(result,null);
        if(detail!="NG\r\nMiddle_Size 에러\r\n홀 유무검사 에러\r\nBlob 에러") throw new Exception(detail);
        // SDK arrays may be reused after returning from the callback: the cached strings must remain immutable.
        Set(overall,"pStringValue",null);
        for(uint i=43;i<90;i++) { Set(frame,"nExecuteCount",i); evt.GetType().GetField("Value").SetValue(evt,frame); method.Invoke(session,new[]{session,evt}); }
        if(cache.Count>32 || cache.Contains(42u)) throw new Exception("Unbounded frame cache");
        Set(frame,"bIsSelfExecute",1u); Set(frame,"nExecuteCount",100u); evt.GetType().GetField("Value").SetValue(evt,frame); method.Invoke(session,new[]{session,evt});
        if(cache.Contains(100u)) throw new Exception("Manual module run contaminated production cache");
        Console.WriteLine("PASS: real SDK callback structures, shuffled named outputs, integer/float failures, bounded run cache, self-run exclusion");
    }
}


