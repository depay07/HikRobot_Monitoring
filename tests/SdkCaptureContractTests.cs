using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
class SdkCaptureContractTests
{
    static BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static int countEvents;
    static void CountResult(object sender, EventArgs args) { countEvents++; }
    static void Check(bool yes,string text) {if(!yes)throw new Exception(text);checks++;}
    static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(o=>(ushort)o.Value);
        var il=method.GetMethodBody().GetILAsByteArray(); int i=0;
        while(i<il.Length)
        {
            ushort key=il[i++];if(key==0xfe)key=(ushort)(0xfe00|il[i++]);var op=codes[key];
            if(op.OperandType==OperandType.InlineMethod)yield return method.Module.ResolveMethod(BitConverter.ToInt32(il,i));
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:i++;break;
                case OperandType.InlineVar:i+=2;break;
                case OperandType.InlineI8:case OperandType.InlineR:i+=8;break;
                case OperandType.InlineSwitch:i+=4+4*BitConverter.ToInt32(il,i);break;
                default:i+=4;break;
            }
        }
    }
    [STAThread] static void Main(string[] args)
    {
        string sdk=Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){string p=Path.Combine(sdk,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var app=Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var frontend=Assembly.LoadFrom(Path.Combine(sdk,"FrontendUI.Design.dll"));
        var view=frontend.GetType("FrontendUI.Design.Controls.ImageControl",true);
        Check(view.GetField("curExecuteCount",F).FieldType==typeof(uint),"displayed frame contract");
        Check(view.GetField("OwnerProcessGUID",F).FieldType==typeof(string),"procedure association contract");
        var image=view.GetField("ImageView",F).FieldType;
        Check(image.GetProperty("Source")!=null && image.GetProperty("ImageDataSource")!=null,"rendered image availability contract");
        var end=image.GetProperty("OnSetImageEnd"); var parameters=end.PropertyType.GetMethod("Invoke").GetParameters();
        Check(parameters.Length==2 && parameters[0].ParameterType==typeof(object) && parameters[1].ParameterType==typeof(string),"post-render callback contract");
        var calls=Calls(app.GetType("SC6000DualMonitor.ViewerForm").GetMethod("Connect",F)).ToArray();
        Check(calls.Any(m=>m.Name=="GetSolutionInstanceToDevice"),"startup attaches camera");
        Check(!calls.Any(m=>m.Name=="Load" && m.DeclaringType.FullName=="VM.Core.VmSolution"),"startup must not force configured recipe");
        var pathCalls=Calls(app.GetType("SC6000DualMonitor.RemoteSolutionSession").GetProperty("CurrentPath").GetGetMethod()).ToArray();
        Check(pathCalls.Any(m=>m.Name=="ReadDevicePath") && !pathCalls.Any(m=>m.Name=="get_SolutionPath"),"native camera path instead of stale managed cache");
        var replayType=app.GetType("SC6000DualMonitor.RemoteReplayDevice");
        var replayCalls=Calls(replayType.GetMethod("Run")).ToArray();
        Check(replayCalls.Any(m=>m.Name=="SetImageData") && !replayCalls.Any(m=>m.Name=="SetImagePath"),"send PC pixels, never remote PC path");
        Check(replayCalls.Any(m=>m.Name=="Run" && m.GetParameters().Any(p=>p.ParameterType==typeof(uint).MakeByRefType())),"SDK returns exact replay frame");
        Check(!replayCalls.Any(m=>m.Name=="Save" || m.Name=="Load"),"replay does not load or save recipes");
        string config=Path.GetTempFileName();
        try
        {
            File.WriteAllText(config,"CAMERAS=2\n[CAMERA1]\nRAW_IMAGE_ROOT=D:\\images\\camera1\n[CAMERA2]\nRAW_IMAGE_ROOT=E:\\카메라2 원본\nREPLAY_IMAGE_MODULE=Image Source1\n");
            var ini=app.GetType("SC6000DualMonitor.IniConfig"); var loaded=ini.GetMethod("Load").Invoke(null,new object[]{config});
            var cam1=ini.GetMethod("GetCamera").Invoke(loaded,new object[]{1}); var cam2=ini.GetMethod("GetCamera").Invoke(loaded,new object[]{2});
            Check((string)cam1.GetType().GetProperty("RawImageRoot").GetValue(cam1,null)=="D:\\images\\camera1","camera1 FTP root parsed");
            Check((string)cam2.GetType().GetProperty("RawImageRoot").GetValue(cam2,null)=="E:\\카메라2 원본","camera2 independent Unicode root parsed");
            Check((string)cam2.GetType().GetProperty("ReplayImageModule").GetValue(cam2,null)=="Image Source1","optional source name parsed");
        }
        finally {File.Delete(config);}
        var sessionType=app.GetType("SC6000DualMonitor.RemoteInspectionSession");
        var session=FormatterServices.GetUninitializedObject(sessionType);
        foreach(string fieldName in new[]{"_callbackGate","_ngFrames","_replayResults","_replayFrames"})
        {
            var field=sessionType.GetField(fieldName,F); field.SetValue(session,Activator.CreateInstance(field.FieldType,true));
        }
        var resultEvent=sessionType.GetEvent("ResultArrived");
        resultEvent.AddEventHandler(session,Delegate.CreateDelegate(resultEvent.EventHandlerType,typeof(SdkCaptureContractTests).GetMethod("CountResult",BindingFlags.Static|BindingFlags.NonPublic)));
        var callbackType=Assembly.LoadFrom(Path.Combine(sdk,"VMControls.Interface.dll")).GetType("VMControls.Interface.ValueEventArgs");
        var callback=Activator.CreateInstance(callbackType); callbackType.GetField("ExecuteCount").SetValue(callback,42u);
        var completed=sessionType.GetMethod("OnProcedureCompleted",F);
        sessionType.GetMethod("SuppressProductionResults").Invoke(session,new object[]{true});
        completed.Invoke(session,new object[]{null,callback}); Check(countEvents==0,"replay completion excluded from production counter");
        sessionType.GetMethod("SuppressProductionResults").Invoke(session,new object[]{false});
        completed.Invoke(session,new object[]{null,callback}); Check(countEvents==0,"late replay completion still excluded after resume");
        callbackType.GetField("ExecuteCount").SetValue(callback,43u);
        // No procedure was constructed: a new production frame reports a read error instead of contacting hardware.
        completed.Invoke(session,new object[]{null,callback}); Check(countEvents==1,"new production frames accepted after resume");
        Console.WriteLine("PASS: "+checks+" installed SDK contracts and startup IL checks (no camera calls)");
    }
}
