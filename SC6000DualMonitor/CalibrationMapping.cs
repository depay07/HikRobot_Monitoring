using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SC6000DualMonitor
{
    internal sealed class CalibrationMapping
    {
        public string Camera { get; set; }
        public string Procedure { get; set; }
        public string Recipe { get; set; }
        public uint GlobalModuleId { get; set; }
        public string GlobalModuleName { get; set; }
        public CalibrationMappingRow[] Rows { get; set; }
        public void Validate() { Validate(true); }
        public void Validate(bool requireCircleFormula)
        {
            if (string.IsNullOrWhiteSpace(Camera) || string.IsNullOrWhiteSpace(Procedure) || string.IsNullOrWhiteSpace(Recipe) ||
                string.IsNullOrWhiteSpace(GlobalModuleName) || Rows == null || Rows.Length != 4 || Rows.Any(r => r == null ||
                    string.IsNullOrWhiteSpace(r.Label) || string.IsNullOrWhiteSpace(r.Variable) || string.IsNullOrWhiteSpace(r.SourceName) ||
                    (r.Field != "L2LAbsDist" && r.Field != "CircleRadius")) || Rows.Select(r => r.Variable).Distinct(StringComparer.Ordinal).Count() != 4 ||
                Rows.Select(r => r.SourceId + ":" + r.Field).Distinct().Count() != 4)
                throw new InvalidOperationException("서로 다른 전역 변수와 측정 모듈 4개를 연결하고 표시 이름을 입력하세요.");
            foreach (var row in Rows.Where(r => r.Field == "CircleRadius"))
            {
                if (row.CircleFormula == CircleCalibrationFormula.Unspecified && !requireCircleFormula) continue;
                if (row.CircleFormula != CircleCalibrationFormula.RadiusTimesScale && row.CircleFormula != CircleCalibrationFormula.DiameterTimesScale)
                    throw new InvalidOperationException(row.Label + ": 항목 연결에서 현재 레시피의 원 계산 방식을 선택하세요 (반지름 × 계수 또는 반지름 × 2 × 계수). 이전 연결에는 이 정보가 없어 한 번 확인해야 합니다.");
            }
        }
        public bool Matches(string camera, string procedure, string recipe)
        { return Camera == camera && Procedure == procedure && Recipe == recipe; }
    }
    internal enum CircleCalibrationFormula { Unspecified = 0, RadiusTimesScale = 1, DiameterTimesScale = 2 }
    internal sealed class CalibrationMappingRow
    {
        public string Label { get; set; }
        public string Variable { get; set; }
        public uint SourceId { get; set; }
        public string SourceName { get; set; }
        public string Field { get; set; }
        public CircleCalibrationFormula CircleFormula { get; set; }
        [ScriptIgnore]
        public double PixelMultiplier
        {
            get
            {
                if (Field != "CircleRadius") return 1;
                if (CircleFormula == CircleCalibrationFormula.RadiusTimesScale) return 1;
                if (CircleFormula == CircleCalibrationFormula.DiameterTimesScale) return 2;
                throw new InvalidOperationException("원 계산 방식이 지정되지 않았습니다.");
            }
        }
    }
    // Local files store wiring only, NEVER coefficients. Actual coefficients belong to the camera recipe.
    internal static class CalibrationMappingStore
    {
        internal static string Key(string camera, string procedure, string recipe)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    new JavaScriptSerializer().Serialize(new[] { camera, procedure, recipe })))).Replace("-", "");
        }
        private static string PathFor(string camera, string procedure, string recipe)
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SC6000Monitor", "CalibrationMappings", Key(camera, procedure, recipe) + ".json"); }
        public static CalibrationMapping Load(string camera, string procedure, string recipe)
        {
            string path = PathFor(camera, procedure, recipe);
            if (!File.Exists(path)) return null;
            var mapping = new JavaScriptSerializer().Deserialize<CalibrationMapping>(File.ReadAllText(path, Encoding.UTF8));
            if (mapping == null || !mapping.Matches(camera, procedure, recipe)) throw new InvalidOperationException("보정 항목 연결 파일의 카메라/레시피가 일치하지 않습니다.");
            // Preserve old labels/wiring so the chooser only needs the missing circle convention.
            // Resolve and Save still require an explicit convention before any camera write.
            mapping.Validate(false); return mapping;
        }
        public static void Save(CalibrationMapping mapping)
        {
            mapping.Validate();
            string path = PathFor(mapping.Camera, mapping.Procedure, mapping.Recipe);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(mapping), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
