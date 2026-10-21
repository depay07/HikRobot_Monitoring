using System;
using System.Collections.Generic;
using SC6000DualMonitor;
class NgReasonTests
{
    static void Check(bool ok) { if (!ok) throw new Exception("NG reason test failed"); }
    static void Main()
    {
        var defs = ConditionDefinition.Parse("float1:Middle_Size;int0:홀 유무검사;int1:Blob");
        var values = new Dictionary<string,int?> { {"float1Out",0}, {"int0Out",0}, {"int1Out",0} };
        string result = NgReasonReader.Read(defs, n => values[n]);
        Check(result == "Middle_Size 에러\r\n홀 유무검사 에러\r\nBlob 에러");
        values["float1Out"] = 1;
        Check(!NgReasonReader.Read(defs, n => values[n]).Contains("Middle_Size"));
        values["int0Out"] = null;
        Check(NgReasonReader.Read(defs, n => values[n]).Contains("판정 확인 불가: 홀 유무검사"));
        values["int0Out"] = 99;
        Check(!NgReasonReader.Read(defs, n => values[n]).Contains("홀 유무검사 에러"));
        Check(NgReasonReader.Read(defs, n => { throw new Exception(); }).Contains("판정 확인 불가"));
        Check(NgReasonReader.Read(defs, n => 1).Contains("NG 사유 확인 불가"));
        var a = new InspectionCounter(); var b = new InspectionCounter();
        a.Record(new InspectionResultEventArgs(InspectionOutcome.Ng, "NG\r\n" + result));
        Check(a.Read().Ng == 1 && b.Read().Total == 0);
        a.Record(new InspectionResultEventArgs(InspectionOutcome.Ok, "OK"));
        Check(a.Read().LastResult == "OK" && a.Read().LastOutcome == InspectionOutcome.Ok && a.Read().Ng == 1);
        a.Reset(); Check(a.Read().LastOutcome == null);
        Console.WriteLine("PASS: float/int/hole/blob, multiple failures, missing/unknown outputs, OK clearing, camera isolation");
    }
}
