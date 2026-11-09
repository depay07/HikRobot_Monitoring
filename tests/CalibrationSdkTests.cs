using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
class CalibrationSdkTests
{
    static Assembly app,sdk,callbacks,calculator;static int checks;
    static BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static void Set(object o,string name,object v){var f=o.GetType().GetField(name,F);if(v!=null && f.FieldType.IsPrimitive)v=Convert.ChangeType(v,f.FieldType);f.SetValue(o,v);}
    static object Call(object o,string name,params object[] a){return o.GetType().GetMethod(name,F).Invoke(o,a);}
    static void Check(bool x,string text){if(!x)throw new Exception(text);checks++;}
    static object Create(out object[] sources)
    {
        var t=app.GetType("SC6000DualMonitor.RemoteCalibrationSession");var c=FormatterServices.GetUninitializedObject(t);
        Set(c,"_gate",new object());Set(c,"_frames",Activator.CreateInstance(app.GetType("SC6000DualMonitor.CalibrationFrameCollector")));
        var array=Array.CreateInstance(t.GetField("_sources",F).FieldType.GetElementType(),4);sources=new object[4];
        for(int i=0;i<4;i++){sources[i]=FormatterServices.GetUninitializedObject(calculator.GetType("CalculatorModuleCs.CalculatorModuleTool"));array.SetValue(sources[i],i);}
        Set(c,"_sources",array);Set(c,"_fields",new[]{"L2LAbsDist","L2LAbsDist","L2LAbsDist","CircleRadius"});Set(c,"_armed",true);return c;
    }
    static void Send(object c,object source,int slot,uint frame,int status=1,int self=0,int error=0,int count=1)
    {
        Type rt=sdk.GetType("VM.PlatformSDKCS.ImvsSdkDefine+IMVS_MODULE_RESULT_INFO_LIST_EX_Data");object raw=Activator.CreateInstance(rt);
        Set(raw,"nExecuteCount",frame);Set(raw,"bIsSelfExecute",self);Set(raw,"nErrorCode",error);
        Type ft=rt.GetField("pInfo").FieldType.GetElementType();object field=Activator.CreateInstance(ft);
        Set(field,"strParamName",slot==3?"CircleRadius":"L2LAbsDist");Set(field,"nValueStatus",status);Set(field,"nValueNum",count);Set(field,"pFloatValue",new[]{100f+slot});
        var fields=Array.CreateInstance(ft,1);fields.SetValue(field,0);Set(raw,"pInfo",fields);
        var args=Activator.CreateInstance(callbacks.GetType("VMControls.Interface.ValueEventArgs"));Set(args,"Value",raw);Call(c,"OnSource",source,args);
    }
    static int Main(string[] args)
    {
        try
        {
            string folder=Path.GetFullPath(args[1]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            app=Assembly.LoadFrom(Path.GetFullPath(args[0]));sdk=Assembly.LoadFrom(Path.Combine(folder,"VM.PlatformSDKCS.dll"));callbacks=Assembly.LoadFrom(Path.Combine(folder,"VMControls.Interface.dll"));calculator=Assembly.LoadFrom(Path.Combine(folder,"CalculatorModuleCs.dll"));
            object[] sources;var c=Create(out sources);
            Send(c,sources[0],0,10);Send(c,sources[1],1,11);Send(c,sources[2],2,10);Send(c,sources[3],3,10);
            Check(Call(c,"TakeFrame")==null,"different frame is not combined");
            Send(c,sources[1],1,10,0);Check(Call(c,"TakeFrame")==null,"invalid parameter status rejected despite positive float");
            Send(c,sources[1],1,10,1,1);Check(Call(c,"TakeFrame")==null,"manual self run rejected");
            Send(c,sources[1],1,10,1,0,1);Check(Call(c,"TakeFrame")==null,"error result rejected");
            Send(c,sources[1],1,10,1,0,0,2);Check(Call(c,"TakeFrame")==null,"ambiguous values rejected");
            Send(c,sources[1],1,10);var result=Call(c,"TakeFrame");
            Check(result!=null && (uint)result.GetType().GetField("Frame").GetValue(result)==10,"valid same-frame snapshot");
            var pixels=(double[])result.GetType().GetField("Pixels").GetValue(result);Check(pixels[3]==103,"raw radius preserved without doubling");
            for(int i=0;i<4;i++)Send(c,sources[i],i,12);Check(Call(c,"TakeFrame")==null,"later triggers cannot replace frozen reference snapshot");
            Console.WriteLine("PASS: "+checks+" calibration installed-SDK callback checks (no camera calls)");return 0;
        }
        catch(Exception ex){while(ex!=null){Console.WriteLine(ex.GetType().Name+": "+ex.Message);ex=ex.InnerException;}return 1;}
    }
}
