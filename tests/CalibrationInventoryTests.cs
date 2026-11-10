using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SC6000DualMonitor;
using VM.Core;
using VM.PlatformSDKCS;
class CalibrationInventoryTests
{
    static int checks;
    static void Check(bool x,string message){if(!x)throw new Exception(message);checks++;}
    static void Reject(Action action,string message){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(message);}
    static byte[] Bytes(string s,int size){var b=new byte[size];Encoding.UTF8.GetBytes(s).CopyTo(b,0);return b;}
    static ImvsSdkDefine.IMVS_MODULE_IO_INFO Info(string name,string type="float",string extra=""){
        return new ImvsSdkDefine.IMVS_MODULE_IO_INFO{nDataType=1,szParamName=Bytes(name,128),szParamType=Bytes(type,128),szExtraData=Bytes(extra,512)};
    }
    static CalibrationOutput[] Inventory(params ImvsSdkDefine.IMVS_MODULE_IO_INFO[] items){
        int stride=Marshal.SizeOf(typeof(ImvsSdkDefine.IMVS_MODULE_IO_INFO));
        var list=new ImvsSdkDefine.IMVS_MODULE_IO_INFO_LIST{nNum=items.Length,pstModuIoInfo=Marshal.AllocHGlobal(stride*items.Length)};
        try{for(int i=0;i<items.Length;i++)Marshal.StructureToPtr(items[i],IntPtr.Add(list.pstModuIoInfo,stride*i),false);return CalibrationDeviceData.Decode(list);}
        finally{Marshal.FreeHGlobal(list.pstModuIoInfo);list.pstModuIoInfo=IntPtr.Zero;GC.SuppressFinalize(list);}
    }
    static string Json(string name,string type,int index){return new JavaScriptSerializer().Serialize(new Dictionary<string,object>{
        {"Name",name},{"Type",type},{"Index",index},{"Expression", type=="float"?"<float:<base64 Q2FsY3VsYXRvcklucHV0MA== />>[0]*0.02":"1+2"},{"Initial",0},{"Summation",false}});}
    static void Run(){
        // Exercise the actual installed SDK's parser, without constructing an SDK engine/device.
        var module=(VmModule)FormatterServices.GetUninitializedObject(typeof(VmModule));
        var collection=new VmIOCollection();
        var parse=typeof(VmModule).GetMethod("AnalysisIOInfo",BindingFlags.Instance|BindingFlags.NonPublic);
        parse.Invoke(module,new object[]{Info("%var0%"),collection});
        parse.Invoke(module,new object[]{Info("%웹두께%"),collection});
        Check(collection.Count==2 && collection.Any(x=>x.Name=="%var0%"),"installed SDK retains template var0 after loading renamed output");
        // Device has renamed row + an additional non-calibration row unknown to the managed cache.
        var native=Inventory(Info("ModuStatus","int"),Info("%웹두께%"),Info("%extra_count%","int"));
        var reads=new List<string>();
        Func<string,string> read=n=>{reads.Add(n);return Json(n,n=="extra_count"?"int":"float",n=="extra_count"?1:0);};
        var model=CalibrationDeviceData.ReadItems(()=>native,read,null);
        Check(reads.SequenceEqual(new[]{"웹두께","extra_count"}),"stale var0 is never queried; actual added item is read");
        Check(model.Names.SequenceEqual(new[]{"웹두께"})&&model.PreservedCount==1,"renamed row and extra calculation preserved");
        var updated=model.Calculate(new[]{700d},new[]{14d});
        Check(updated.JsonInOrder()[1]==model.JsonInOrder()[1],"extra expression remains byte-for-byte in normalized model");
        Check(updated.Factors[0]==.02,"calibration uses real renamed item");
        // A genuine device item called var0 must never be skipped merely because of its name.
        var realVar=Inventory(Info("%var0%"),Info("%웹두께%"));
        Reject(()=>CalibrationDeviceData.ReadItems(()=>realVar,n=>{if(n=="var0")throw new InvalidOperationException("read failed");return read(n);},null),"real unreadable var0 blocks complete model");
        var validVar=CalibrationDeviceData.ReadItems(()=>Inventory(Info("%var0%")),n=>Json(n,"float",0),null);
        Check(validVar.Names[0]=="var0","genuine var0 remains editable");
        int count=0;
        Reject(()=>CalibrationDeviceData.ReadItems(()=>++count==1?native:Inventory(Info("%웹두께%")),read,null),"item removal during read rejected");
        count=0;
        Reject(()=>CalibrationDeviceData.ReadItems(()=>++count==1?native:Inventory(Info("%웹두께%"),Info("%extra_count%","int"),Info("%new_item%")),read,null),"item addition during read rejected");
        Reject(()=>CalibrationDeviceData.ReadItems(()=>native,n=>Json("wrong","float",0),null),"payload name mismatch rejected");
        Reject(()=>CalibrationDeviceData.ReadItems(()=>native,n=>Json(n,"int",0),null),"payload type mismatch rejected");
        Reject(()=>Inventory(Info("%x%"),Info("%x%")),"duplicate native names rejected");
        Reject(()=>CalibrationDeviceData.Decode(new ImvsSdkDefine.IMVS_MODULE_IO_INFO_LIST{nNum=1}),"missing native pointer rejected");
        var composite=Inventory(Info("%웹두께%"),Info("%point%","POINT"),Info("%preview%","string","IsResultShow=true"));
        Check(composite.Where(x=>x.IsItem).Select(x=>x.ItemName).SequenceEqual(new[]{"웹두께","point"}),"top-level composite kept, display result excluded");
        count=0;
        var reversed=CalibrationDeviceData.ReadItems(()=>++count==1?native:native.Reverse().ToArray(),read,null);
        Check(reversed.Same(model),"harmless native inventory order changes tolerated");
        var link=new ImvsSdkDefine.IMVS_SUBSCRIBE_INFO{nSubModuleID=13,strSubInput=Bytes("%Q2FsY3VsYXRvcklucHV0MA==%",256),nPubModuleID=10,strPubOutput=Bytes("L2LAbsDist",256),strIndex=Bytes("All",256),nIsIndexConstant=1};
        const string token="Q2FsY3VsYXRvcklucHV0MA==";
        var picked=CalibrationDeviceData.MeasurementSource(13,token,new[]{link});
        Check(picked.HasValue && picked.Value.nPubModuleID==10,"device source mapping follows recipe module ID");
        link.strPubOutput=Bytes("CircleRadius",256);
        Check(CalibrationDeviceData.MeasurementSource(13,token,new[]{link}).HasValue,"radius source accepted");
        Check(!CalibrationDeviceData.MeasurementSource(13,token,new[]{link,link}).HasValue,"ambiguous source rejected");
        Check(!CalibrationDeviceData.MeasurementSource(14,token,new[]{link}).HasValue,"other module source rejected");
        link.nIsPubConstant=1;
        Check(!CalibrationDeviceData.MeasurementSource(13,token,new[]{link}).HasValue,"constant is not a pixel measurement");
        link.nIsPubConstant=0;link.strIndex=Bytes("1",256);
        Check(!CalibrationDeviceData.MeasurementSource(13,token,new[]{link}).HasValue,"nonzero array selection rejected");
        link.strIndex=Bytes("All",256);link.nIsIndexConstant=0;
        Check(!CalibrationDeviceData.MeasurementSource(13,token,new[]{link}).HasValue,"variable index rejected");
        Check(!Inventory(Info("%preview%","string","IsResultShow = true;"))[0].IsItem,"display metadata whitespace handled");
        Console.WriteLine("PASS: "+checks+" inventory checks including installed SDK stale-var0 reproduction; no camera calls");
    }
    static int Main(string[] args){
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(args[0],new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        try{Run();return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
