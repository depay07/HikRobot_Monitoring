using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// Inspect the compiled production methods and installed SDK. No camera calls.
class ProcedureResumeContractTests
{
    class Instruction { public int Offset, Next; public OpCode Op; public object Operand; }
    static int checks;
    static void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
    static List<Instruction> Decode(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
        var bytes = method.GetMethodBody().GetILAsByteArray(); var result = new List<Instruction>();
        for (int i = 0; i < bytes.Length; )
        {
            var item = new Instruction { Offset = i }; ushort key = bytes[i++];
            if (key == 0xfe) key = (ushort)(0xfe00 | bytes[i++]);
            item.Op = codes[key];
            switch (item.Op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.InlineMethod: item.Operand = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, i)); i += 4; break;
                case OperandType.InlineField: item.Operand = method.Module.ResolveField(BitConverter.ToInt32(bytes, i)); i += 4; break;
                case OperandType.ShortInlineBrTarget: item.Operand = i + 1 + (sbyte)bytes[i]; i++; break;
                case OperandType.InlineBrTarget: item.Operand = i + 4 + BitConverter.ToInt32(bytes, i); i += 4; break;
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i++; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(bytes, i); break;
                default: i += 4; break;
            }
            item.Next = i; result.Add(item);
        }
        return result;
    }
    static bool Calls(Instruction item, string name) { var m = item.Operand as MethodBase; return m != null && m.Name == name; }
    static void GuardedContinuousWrite(MethodInfo method, OpCode value)
    {
        var body = Decode(method);
        var calls = body.Select((item, i) => new { item, i }).Where(p => Calls(p.item, "set_ContinuousRunEnable")).ToArray();
        Check(calls.Length == 1, method.Name + ": one continuous command site");
        int call = calls[0].i;
        Check(body[call - 1].Op == value, method.Name + ": expected constant command, never restore false after resume");
        Check(body.Take(call).Select((item, i) => new { item, i }).Any(p =>
            p.item.Operand is FieldInfo && ((FieldInfo)p.item.Operand).Name == "_wasContinuous" &&
            (body[p.i + 1].Op == OpCodes.Brfalse || body[p.i + 1].Op == OpCodes.Brfalse_S) &&
            (int)body[p.i + 1].Operand > body[call].Offset), method.Name + ": external-trigger mode skips setter");
    }
    static void Main(string[] args)
    {
        string sdk = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (s, e) => {
            string path = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.ReflectionOnlyLoadFrom(path) : Assembly.ReflectionOnlyLoad(e.Name);
        };
        var app = Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(args[0]));
        var owner = app.GetType("SC6000DualMonitor.RemoteInspectionSession", true);
        GuardedContinuousWrite(owner.GetMethod("Pause"), OpCodes.Ldc_I4_0);
        GuardedContinuousWrite(owner.GetMethod("Resume"), OpCodes.Ldc_I4_1);
        var resume = Decode(owner.GetMethod("Resume"));
        Check(resume.Any(i => Calls(i, "set_IsEnabled")), "restore procedure enable state");
        Check(!resume.Any(i => Calls(i, "ReconnectAsync") || Calls(i, "SetReportModuleResult_V2") || Calls(i, "LoadFrontendSource")), "resume does not reset graphics or callback subscriptions");
        foreach (string name in new[] { "RemoteExposureSession", "RemoteCalibrationSession", "RemoteBlobSession" })
            Check(Decode(app.GetType("SC6000DualMonitor." + name).GetMethod("Resume"))
                .Any(i => i.Operand is MethodBase && ((MethodBase)i.Operand).DeclaringType == owner && Calls(i, "Resume")), name + " shares fixed resume");
        var core = Assembly.ReflectionOnlyLoadFrom(Path.Combine(sdk, "VM.Core.dll"));
        var setter = core.GetType("VM.Core.VmProcedure").GetProperty("ContinuousRunEnable").GetSetMethod();
        Check(Decode(setter).Any(i => Calls(i, "RefreshUIStopExecute")), "SDK false setter has stop side effect");
        Check(Decode(setter).Any(i => Calls(i, "ContinousRunStart")), "SDK true setter starts continuous execution");
        var platform = Assembly.ReflectionOnlyLoadFrom(Path.Combine(sdk, "VM.PlatformSDKCS.dll"));
        var process = platform.GetType("VM.PlatformSDKCS.ProcessManager");
        Check(Decode(process.GetMethod("StopExecute")).Any(i => Calls(i, "IMVS_StopExecute_V30")), "SDK stop invokes device stop API");
        Check(app.GetType("SC6000DualMonitor.FrontendGraphics").GetMethod("ReconnectAsync") == null, "automatic graphics reconnect remains removed");
        Console.WriteLine("PASS: " + checks + " compiled resume and installed SDK contracts (no camera calls)");
    }
}
