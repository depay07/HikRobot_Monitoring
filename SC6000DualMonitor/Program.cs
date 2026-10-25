using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using VM.Core;
using VM.PlatformSDKCS;

namespace SC6000DualMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string baseDir = Path.GetDirectoryName(exePath);
                string iniPath = Path.Combine(baseDir, "config.ini");

                if (!File.Exists(iniPath))
                {
                    MessageBox.Show(
                        "config.ini 파일을 찾을 수 없습니다.\r\n\r\n" + iniPath,
                        "SC6000 Monitor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                IniConfig config = IniConfig.Load(iniPath);

                if (args.Any(a => string.Equals(a, "--viewer", StringComparison.OrdinalIgnoreCase)))
                {
                    int slot = ReadSlot(args);
                    int cameraCount = config.CameraCount;

                    if (slot < 1 || slot > cameraCount)
                        return;

                    CameraSettings settings = config.GetCamera(slot);
                    Application.Run(new ViewerForm(settings, ReadParentHandle(args)));
                    return;
                }

                // Each SC6000 is opened in a separate process because each process owns
                // its own VmSolution.Instance singleton.
                Application.Run(new MonitorForm(config, exePath, baseDir));
            }
            catch (Exception ex)
            {
                VmException vmEx = ex as VmException;
                if (vmEx != null)
                {
                    MessageBox.Show(
                        "VisionMaster SDK error. Error Code: 0x" + vmEx.errorCode.ToString("X"),
                        "SC6000 Monitor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    MessageBox.Show(ex.ToString(), "SC6000 Monitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static int ReadSlot(string[] args)
        {
            foreach (string a in args)
            {
                if (a.StartsWith("--slot=", StringComparison.OrdinalIgnoreCase))
                {
                    int slot;
                    if (int.TryParse(a.Substring("--slot=".Length), out slot) && slot >= 1)
                        return slot;
                }
            }
            return 1;
        }

        private static IntPtr ReadParentHandle(string[] args)
        {
            foreach (string arg in args)
            {
                long handle;
                if (arg.StartsWith("--parent=", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(arg.Substring(9), out handle) && handle != 0)
                    return new IntPtr(handle);
            }
            throw new InvalidOperationException("뷰어는 메인 모니터링 창에서 실행해야 합니다.");
        }
    }

    internal sealed class CameraSettings
    {
        public string Ip { get; set; }
        public string Password { get; set; }
        public string Title { get; set; }
        public string SolutionPath { get; set; }
        public string SolutionDirectory { get; set; }
        public string SolutionPassword { get; set; }
        public int MonitorIndex { get; set; }
        public string ProcedureName { get; set; }
        public string ConditionModuleName { get; set; }
        public string BlobModuleName { get; set; }
        public string ConditionItems { get; set; }
        public string PartNumber { get; set; }
        public string RawImageRoot { get; set; }
        public string ReplayImageModule { get; set; }
    }

    internal sealed class IniConfig
    {
        private readonly Dictionary<string, string> _values;

        public int CameraCount { get; private set; }
        public string InspectionText { get; private set; }
        public int MonitorIndex { get; private set; }

        private IniConfig(Dictionary<string, string> values)
        {
            _values = values;

            int cameras;
            if (!TryGetInt("CAMERAS", out cameras))
                cameras = 2;

            if (cameras < 1 || cameras > 4)
                throw new InvalidOperationException("CAMERAS 값은 1부터 4까지 사용할 수 있습니다.");

            CameraCount = cameras;
            InspectionText = Get("INSPECTION_TEXT", string.Empty);

            int monitorIndex;
            if (!TryGetInt("MONITOR", out monitorIndex))
                monitorIndex = 0;
            MonitorIndex = Math.Max(0, monitorIndex);
        }

        public static IniConfig Load(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string currentSection = string.Empty;

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                int colon = line.IndexOf(':');
                int split;
                if (eq < 0) split = colon;
                else if (colon < 0) split = eq;
                else split = Math.Min(eq, colon);

                if (split <= 0)
                    continue;

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();

                // Remove optional matching quotes.
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (!string.IsNullOrEmpty(currentSection))
                    values[currentSection + "." + key] = value;
                else
                    values[key] = value;
            }

            return new IniConfig(values);
        }

        public CameraSettings GetCamera(int slot)
        {
            string section = "CAMERA" + slot;
            string ip = Get(section + ".IP", string.Empty);

            return new CameraSettings
            {
                Ip = ip.Trim(),
                Password = Get(section + ".PASSWORD", string.Empty),
                SolutionPath = Get(section + ".SOLUTION_PATH", string.Empty).Trim(),
                SolutionDirectory = Get(section + ".SOLUTION_DIRECTORY", string.Empty),
                SolutionPassword = Get(section + ".SOLUTION_PASSWORD", string.Empty),
                Title = Get(section + ".TITLE", "SC6000 Camera " + slot),
                ProcedureName = Get(section + ".PROCEDURE", "Proc1"),
                ConditionModuleName = Get(section + ".CONDITION_MODULE", "Condition Detection1"),
                BlobModuleName = Get(section + ".BLOB_MODULE", string.Empty).Trim(),
                ConditionItems = Get(section + ".CONDITION_ITEMS", ConditionDefinition.DefaultItems),
                PartNumber = Get(section + ".PART_NO", string.Empty),
                RawImageRoot = Get(section + ".RAW_IMAGE_ROOT", string.Empty).Trim(),
                ReplayImageModule = Get(section + ".REPLAY_IMAGE_MODULE", string.Empty).Trim(),
                MonitorIndex = MonitorIndex
            };
        }

        private string Get(string key, string defaultValue)
        {
            string value;
            return _values.TryGetValue(key, out value) ? value : defaultValue;
        }

        private bool TryGetInt(string key, out int value)
        {
            string text;
            if (_values.TryGetValue(key, out text))
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            value = 0;
            return false;
        }
    }
}
