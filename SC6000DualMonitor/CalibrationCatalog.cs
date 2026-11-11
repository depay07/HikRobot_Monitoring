using System;
using System.Collections.Generic;
using System.Linq;
using CalculatorModuleCs;
using GlobalVariableModuleCs;
using VM.Core;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationSource
    {
        public VmModule Module;
        public string Field;
        public override string ToString()
        { return Module.Name + " (ID " + Module.ID + ") / " + (Field == "CircleRadius" ? "원 지름 (반지름 × 2)" : "선 간 거리"); }
    }
    internal sealed class CalibrationCatalog
    {
        public readonly string Camera, Procedure, Recipe;
        public readonly List<GlobalVariableModuleTool> Globals = new List<GlobalVariableModuleTool>();
        public readonly List<CalibrationSource> Sources = new List<CalibrationSource>();
        public readonly List<CalculatorModuleTool> Calculators = new List<CalculatorModuleTool>();
        public CalibrationCatalog(string camera, VmProcedure procedure)
        {
            Camera = camera; Procedure = procedure.Name;
            Recipe = new RemoteSolutionSession(null, null).CurrentPath;
            if (string.IsNullOrWhiteSpace(Recipe)) throw new InvalidOperationException("현재 카메라 레시피 경로를 확인할 수 없습니다.");
            var all = VmSolution.Instance.GetAllModuleList();
            for (int i = 0; i < all.nNum; i++)
            {
                var global = VmSolution.Instance.GetModuleByID(all.astModuleInfo[i].nModuleID) as GlobalVariableModuleTool;
                if (global != null) Globals.Add(global);
            }
            var local = procedure.GetAllModuleList();
            for (int i = 0; i < local.nNum; i++)
            {
                var module = procedure.GetModuleByID(local.astModuleInfo[i].nModuleID);
                if (module == null) continue;
                var calculator = module as CalculatorModuleTool;
                if (calculator != null) Calculators.Add(calculator);
                foreach (var output in CalibrationDeviceData.Outputs(module))
                    if (output.Name == "L2LAbsDist" || output.Name == "CircleRadius")
                        Sources.Add(new CalibrationSource { Module = module, Field = output.Name });
            }
            if (Globals.Count == 0) throw new InvalidOperationException("레시피에 Global Variable 모듈이 없습니다. VisionMaster에서 float 보정계수 4개를 만들고 계산식에 연결한 후 카메라에 저장하세요. CALIBRATION.md의 준비 절차를 참고하세요.");
        }
        public static string[] Variables(GlobalVariableModuleTool global)
        {
            return global.GetAllGlobalVar().Where(v => string.Equals(v.strValueType, "float", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.strValueName).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        }
        public GlobalVariableModuleTool Resolve(CalibrationMapping mapping)
        {
            mapping.Validate();
            if (!mapping.Matches(Camera, Procedure, Recipe)) throw new InvalidOperationException("항목 연결의 카메라/레시피가 다릅니다. 다시 연결하세요.");
            var global = Globals.SingleOrDefault(g => g.ID == mapping.GlobalModuleId && g.Name == mapping.GlobalModuleName);
            if (global == null) throw new InvalidOperationException("연결된 Global Variable 모듈이 변경되었습니다.");
            var variables = Variables(global);
            var links = Calculators.ToDictionary(c => c.ID, c => CalibrationDeviceData.Subscriptions(c));
            foreach (var row in mapping.Rows)
            {
                if (!variables.Contains(row.Variable, StringComparer.Ordinal)) throw new InvalidOperationException(row.Variable + ": float 전역 변수가 없습니다.");
                var source = Sources.SingleOrDefault(s => s.Module.ID == row.SourceId && s.Module.Name == row.SourceName && s.Field == row.Field);
                if (source == null) throw new InvalidOperationException(row.Label + ": 연결된 픽셀 측정 모듈이 변경되었습니다.");
                // Both operands must reach the same Calculator. Formula semantics are configured in VisionMaster.
                if (!links.Any(pair => pair.Value.Any(l => CalibrationDeviceData.ScalarLink(l, pair.Key, global.ID, row.Variable)) &&
                    pair.Value.Any(l => CalibrationDeviceData.ScalarLink(l, pair.Key, row.SourceId, row.Field))))
                    throw new InvalidOperationException(row.Label + ": 측정값과 " + row.Variable + "을 같은 Calculator 입력에 연결하세요. 계산식은 거리 × 계수, 원은 반지름 × 2 × 계수입니다.");
            }
            return global;
        }
        public string BindingSignature()
        { return string.Join("\n", Calculators.OrderBy(c => c.ID).Select(c => c.ID + ":" + c.Name + ":" + CalibrationDeviceData.BindingSignature(c))); }
    }
}
