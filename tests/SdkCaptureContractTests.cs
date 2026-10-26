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
        Check(app.GetType("SC6000DualMonitor.RemoteReplayDevice")==null && app.GetType("SC6000DualMonitor.ReplayControl")==null,"camera replay implementation removed");
        Check(!app.GetReferencedAssemblies().Any(a=>a.Name=="ImageSourceModuleCs"),"no image source mutation dependency");
        var film=app.GetType("SC6000DualMonitor.NgFilmStrip");
        Check(film.GetEvent("ReplayRequested")==null && film.GetProperty("ReplayMode")==null,"no replay events or mode");
        var viewer=app.GetType("SC6000DualMonitor.ViewerForm");
        Check(viewer.GetMethod("RunReplay",F)==null && viewer.GetMethod("ChangeReplayMode",F)==null,"no replay handlers in viewer");
        Console.WriteLine("PASS: "+checks+" installed SDK contracts and startup IL checks (no camera calls)");
    }
}
