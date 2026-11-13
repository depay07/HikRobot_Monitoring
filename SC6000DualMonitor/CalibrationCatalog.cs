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
        { return Module.Name + " (ID " + Module.ID + ") / " + (Field == "CircleRadius" ? "원 반지름 (CircleRadius)" : "선 간 거리"); }
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
            var solution = VmSolution.Instance;
            // Mirror the SDK's LoadGlobleModules: global modules have their own inventory API.
            var globals = CalibrationDeviceData.GlobalVariables(solution.ServerSDKManagerInstance.mGlobalModuleManager.GetAllGlobalModule());
            foreach (var info in globals)
            {
                string name = CalibrationDeviceData.Text(info.strDisplayName);
                var module = solution.GetModuleByID(info.nNodeID);
                var global = module as GlobalVariableModuleTool;
                if (global == null)
                    throw new InvalidOperationException("카메라에서 전역 변수 모듈을 찾았지만 SDK 객체를 불러오지 못했습니다: " + name + " (ID " + info.nNodeID + ") / " +
                        (module == null ? "객체 없음" : module.GetType().FullName) + ". 변수를 새로 만들지 말고 뷰어를 다시 연결하세요.");
                Globals.Add(global);
            }
            if (Globals.Count == 0) throw new InvalidOperationException("카메라의 전역 모듈 목록에서 Global Variable을 찾지 못했습니다.\r\nVisionMaster 주 화면의 빠른 도구 모음에서 'var' 아이콘(Global Variable)을 열어 확인하세요.\r\n'＋ 변수 추가'로 float 단일 값 4개를 만든 뒤 Calculator 입력에 연결하고 카메라에 저장하세요.\r\n이미 설정했다면 뷰어를 다시 연결하세요. 자세한 순서: CALIBRATION.md");
            var local = procedure.GetAllModuleList();
            for (int i = 0; i < local.nNum; i++)
            {
                var module = procedure.GetModuleByID(local.astModuleInfo[i].nModuleID);
                if (module == null) continue;
                var calculator = module as CalculatorModuleTool;
                if (calculator != null) Calculators.Add(calculator);
                foreach (var output in CalibrationDeviceData.Measurements(CalibrationDeviceData.Outputs(module)))
                    Sources.Add(new CalibrationSource { Module = module, Field = output.Name });
            }
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
                    throw new InvalidOperationException(row.Label + ": 측정값과 " + row.Variable + "을 같은 Calculator 입력에 연결하세요. 원 계산 방식은 항목 연결에서 실제 레시피 식과 동일하게 선택하세요.");
            }
            return global;
        }
        public string BindingSignature()
        { return string.Join("\n", Calculators.OrderBy(c => c.ID).Select(c => c.ID + ":" + c.Name + ":" + CalibrationDeviceData.BindingSignature(c))); }
    }
}
