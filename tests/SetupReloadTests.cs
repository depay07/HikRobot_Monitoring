using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using SC6000DualMonitor;
class SetupReloadTests {
class Session : IInspectionSettingsSession {
public double Min=4.25; public bool Fail;
public IList<ConditionRange> ReadRanges() { if(Fail) throw new Exception("offline"); return new[]{new ConditionRange(new ConditionDefinition("float1","middle_size"),Min,4.75)}; }
public void WriteRange(ConditionRange r){} public void Pause(){} public void Resume(){} public void Save(){}
}
[STAThread] static void Main() {
var session=new Session(); using(var form=new InspectionSetupForm(session,"Camera 1")) {
var flags=BindingFlags.Instance|BindingFlags.NonPublic;
var read=typeof(InspectionSetupForm).GetMethod("ReadCamera",flags);
var grid=(DataGridView)typeof(InspectionSetupForm).GetField("_grid",flags).GetValue(form);
read.Invoke(form,null); if(grid.Rows.Count!=1 || Convert.ToString(grid.Rows[0].Cells["Min"].Value)!="4.25") throw new Exception("Initial read");
session.Min=4.4; read.Invoke(form,null); if(Convert.ToString(grid.Rows[0].Cells["Min"].Value)!="4.4") throw new Exception("Reload did not update grid");
Console.WriteLine("PASS: Set Up initial read and changed camera value reload");
}
}
}
