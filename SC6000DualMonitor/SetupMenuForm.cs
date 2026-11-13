using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class SetupMenuForm : Form
    {
        public bool RecipeFilesChanged { get; private set; }
        public SetupMenuForm(RemoteInspectionSession session, CameraSettings settings, Func<System.Threading.Tasks.Task> refreshGraphics = null)
        {
            Text = settings.Title + " | Set Up";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(new Label { Text = session.CameraProcedureDescription, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var conditions = new Button { Text = "OK/NG 설정 조건", Dock = DockStyle.Fill };
            var blob = new Button { Text = "BLOB 조건 설정", Dock = DockStyle.Fill };
            var close = new Button { Text = "닫기", Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            layout.Controls.Add(conditions, 0, 1); layout.Controls.Add(blob, 0, 2); layout.Controls.Add(close, 0, 6);
            var exposure = new Button { Text = "노출 / 게인 설정 (Exposure / Gain)", Dock = DockStyle.Fill };
            layout.Controls.Add(exposure, 0, 4);
            exposure.Click += delegate {
                using (var form = new ExposureSetupForm(settings.Title, session.GetExposureSessions,
                    message => CameraLog.Write(settings.Ip, "ExposureGain", message))
                    { RefreshGraphicsAfterSave = refreshGraphics }) form.ShowDialog(this);
            };
            var files = new Button { Text = "카메라 레시피 파일 관리", Dock = DockStyle.Fill };
            layout.Controls.Add(files, 0, 5);
            files.Click += delegate
            {
                try
                {
                    using (var ftp = new RemoteRecipeFileSession(settings.Ip, settings.Password))
                    {
                        var manager = new RecipeFileManager(ftp, settings.SolutionDirectory);
                        using (var form = new RecipeFilesForm(manager, settings.Title + " · " + settings.Ip,
                            message => CameraLog.Write(settings.Ip, "RecipeFileManager", message)))
                        { form.ShowDialog(this); RecipeFilesChanged |= form.Changed; }
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "카메라 레시피 파일 관리", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            var calibration = new Button { Text = "치수 보정", Dock = DockStyle.Fill };
            layout.Controls.Add(calibration, 0, 3);
            calibration.Click += delegate
            {
                try
                {
                    CalibrationWorkflow.Show(this, session, settings.Title, refreshGraphics);
                }
                catch (Exception ex)
                {
                    CameraLog.Write(settings.Ip, "CalibrationOpenFailed", ex.ToString());
                    MessageBox.Show(this, ex.Message + "\r\n\r\n진단 로그: " + CameraLog.PathFor(settings.Ip),
                        "치수 보정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(layout); CancelButton = close;
            conditions.Click += delegate { using (var form = new InspectionSetupForm(session, settings.Title)
                { RefreshGraphicsAfterSave = refreshGraphics }) form.ShowDialog(this); };
            blob.Click += delegate { using (var form = new BlobSetupForm(settings.Title, delegate {
                var targets = new List<BlobEditorTarget>();
                foreach (var choice in session.GetBlobModules())
                    targets.Add(new BlobEditorTarget { Name = choice.Module.Name, DisplayName = choice.ToString(), Session = new RemoteBlobSession(session, choice) });
                return targets;
            }, settings.BlobModuleName) { RefreshGraphicsAfterSave = refreshGraphics }) form.ShowDialog(this); };
        }
    }
}
