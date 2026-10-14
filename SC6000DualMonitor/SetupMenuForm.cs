using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SC6000DualMonitor
{
    internal sealed class SetupMenuForm : Form
    {
        public SetupMenuForm(RemoteInspectionSession session, CameraSettings settings)
        {
            Text = settings.Title + " | Set Up";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 230);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(new Label { Text = session.CameraProcedureDescription, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var conditions = new Button { Text = "OK/NG 설정 조건", Dock = DockStyle.Fill };
            var blob = new Button { Text = "BLOB 조건 설정", Dock = DockStyle.Fill };
            var close = new Button { Text = "닫기", Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            layout.Controls.Add(conditions, 0, 1); layout.Controls.Add(blob, 0, 2); layout.Controls.Add(close, 0, 3);
            Controls.Add(layout); CancelButton = close;
            conditions.Click += delegate { using (var form = new InspectionSetupForm(session, settings.Title)) form.ShowDialog(this); };
            blob.Click += delegate { using (var form = new BlobSetupForm(settings.Title, delegate {
                var targets = new List<BlobEditorTarget>();
                foreach (var choice in session.GetBlobModules())
                    targets.Add(new BlobEditorTarget { Name = choice.Module.Name, DisplayName = choice.ToString(), Session = new RemoteBlobSession(session, choice) });
                return targets;
            }, settings.BlobModuleName)) form.ShowDialog(this); };
        }
    }
}
