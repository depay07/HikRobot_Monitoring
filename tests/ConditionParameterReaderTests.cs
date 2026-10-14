using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SC6000DualMonitor;
class ConditionParameterReaderTests
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Main()
    {
        var values = new Dictionary<string, string>();
        string[] names = { "int0", "float0", "float1", "float2", "float3", "int1" };
        string[] captions = { "홀 유무감지", "bottom_size", "middle_size", "top_size", "circle_size", "Blob 감지개수" };
        double[] mins = { 1, 13.8, 4.25, 4.75, 6.3, 0 };
        double[] maxs = { 1, 14.3, 4.75, 5.25, 6.7, 0 };
        values["DynamicParams"] = string.Join(";", names.SelectMany(n => new[] { n+"_Min,"+(n.StartsWith("int")?"int":"float"), n+"_Max,"+(n.StartsWith("int")?"int":"float"), n+"_ReMark,string" }));
        for(int i=0;i<names.Length;i++) { values[names[i]+"_Min"]=mins[i].ToString(CultureInfo.InvariantCulture); values[names[i]+"_Max"]=maxs[i].ToString(CultureInfo.InvariantCulture); values[names[i]+"_ReMark"]=captions[i]; }
        int reads=0;
        Func<string,string> read = key => { reads++; return values[key]; };
        var fallback=ConditionDefinition.Parse(ConditionDefinition.DefaultItems);
        CultureInfo.CurrentCulture=new CultureInfo("de-DE");
        var ranges=ConditionParameterReader.Read(read,fallback);
        Check(ranges.Count==6 && ranges[5].Definition.Name=="int1", "all six camera conditions");
        Check(ranges[1].Definition.Caption=="bottom_size", "camera remark overrides swapped config caption");
        Check(ranges[2].Min==4.25 && ranges[3].Max==5.25, "screenshot ranges, invariant decimal parsing");
        int previous=reads; values["float1_Min"]="4.4";
        ranges=ConditionParameterReader.Read(read,fallback);
        Check(reads>previous && ranges[2].Min==4.4,"reload performs fresh reads");
        values["float1_Min"]="not a number";
        try { ConditionParameterReader.Read(read,fallback); throw new Exception("Invalid values accepted"); } catch(InvalidOperationException) { checks++; }
        values["float1_Min"]="4.25";
        values["DynamicParams"]="";
        try { ConditionParameterReader.Read(read,fallback); throw new Exception("Empty list accepted"); } catch(InvalidOperationException) { checks++; }
        Console.WriteLine("PASS: "+checks+" camera condition reader checks");
    }
}
