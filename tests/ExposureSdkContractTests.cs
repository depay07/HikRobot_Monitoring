using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

// Static contract check against the installed VM4.4.50 binary, not a mock SDK.
// Reflection-only loading never invokes ParamItem.Value (which calls the camera).
// Offsets below are intentionally version-specific: a changed SDK must be reviewed.
class ExposureSdkContractTests
{
    static int checks;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    static void Bytes(byte[] il, int offset, params byte[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            if (offset + i >= il.Length || il[offset + i] != expected[i])
                throw new Exception("SDK IL contract changed at " + (offset + i).ToString("X4"));
        checks++;
    }
    static int Main(string[] args)
    {
        try
        {
            string sdk = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (sender, e) => {
                string file = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.ReflectionOnlyLoadFrom(file) : Assembly.ReflectionOnlyLoad(e.Name);
            };
            string path = Path.Combine(sdk, "GlobalSmartCameraModuleCs.dll");
            var assembly = Assembly.ReflectionOnlyLoadFrom(path);
            var item = assembly.GetType("GlobalSmartCameraModuleCs.GlobalSmartCameraParamItem", true);
            var getter = item.GetMethod("GetGlobalSmartCameraDoubleValue");
            byte[] il = getter.GetMethodBody().GetILAsByteArray();
            // ldloc.0; ldlen; conv.i4; ldc.i4.2; cgt; stloc.1; ldloc.1;
            // brfalse.s -> scalar/error branch at IL_0067.
            Bytes(il, 0x18, 0x06, 0x8e, 0x69, 0x18, 0xfe, 0x02, 0x0b, 0x07, 0x2c, 0x45);
            Bytes(il, 0x41, 0x06, 0x17, 0x9a, 0x28); // field[1] -> Double.Parse
            Bytes(il, 0x4f, 0x06, 0x18, 0x9a, 0x28); // field[2] -> Double.Parse
            Bytes(il, 0x5c, 0x06, 0x16, 0x9a, 0x28); // field[0] -> Double.Parse
            Check(getter.Module.ResolveField(BitConverter.ToInt32(il, 0x4a)).Name == "MinValue", "field 1 is minimum");
            Check(getter.Module.ResolveField(BitConverter.ToInt32(il, 0x58)).Name == "MaxValue", "field 2 is maximum");
            var param = assembly.GetType("GlobalSmartCameraModuleCs.GlobalSmartCameraParam", true);
            foreach (string name in new[] { "ExposureTime", "Gain" })
            {
                var method = param.GetProperty(name).GetGetMethod();
                byte[] body = method.GetMethodBody().GetILAsByteArray();
                Bytes(body, 7, 0x6f); // callvirt
                Check(method.Module.ResolveMethod(BitConverter.ToInt32(body, 8)).MetadataToken == getter.MetadataToken,
                    name + " uses the extended-range reader");
            }
            var xml = new XmlDocument();
            xml.Load(Path.Combine(sdk, "Module(sp)/x64/Global/GlobalSmartCameraModule/GlobalSmartCameraModuleAlgorithmTab.xml"));
            foreach (string name in new[] { "AutoExposureMode", "AutoGainMode" })
                Check(xml.SelectSingleNode("//*[@Name='" + name + "']/EnumEntrys/EnumEntry[@Name='OFF']/Value").InnerText == "0",
                    name + " OFF remains 0");
            using (var hash = SHA256.Create()) using (var file = File.OpenRead(path))
                Console.WriteLine("SDK SHA256 " + BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant());
            Console.WriteLine("PASS: " + checks + " installed SDK IL/XML exposure contract checks (no camera calls)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
