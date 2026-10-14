using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using SC6000DualMonitor;
class SetupReloadTests {
class Session : IInspectionSettingsSession {
public IList<ConditionRange> Values = new List<ConditionRange> {
new ConditionRange(new ConditionDefinition("float0","Z"),0.00000004,1),
new ConditionRange(new ConditionDefinition("float1","A"),4.25,4.75)};
public int Reads, FailReadAt; public bool Saved;
public IList<ConditionRange> ReadRanges() { Reads++; if(Reads==FailReadAt) throw new Exception("offline"); return new List<ConditionRange>(Values); }
public void WriteRange(ConditionRange r) { for(int i=0;i<Values.Count;i++) if(Values[i].Definition.Name==r.Definition.Name) Values[i]=r; }
public void Pause(){} public void Resume(){} public void Save(){Saved=true;}
}
static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
[STAThread] static void Main() {
var flags=BindingFlags.Instance|BindingFlags.NonPublic;
var read=typeof(InspectionSetupForm).GetMethod("ReadCamera",flags);
var apply=typeof(InspectionSetupForm).GetMethod("Apply",flags);
var session=new Session(); using(var form=new InspectionSetupForm(session,"Camera 1")) {
var grid=(DataGridView)typeof(InspectionSetupForm).GetField("_grid",flags).GetValue(form);
Check((bool)read.Invoke(form,null),"Initial read");
Check(double.Parse(Convert.ToString(grid.Rows[0].Cells["Min"].Value))==0.00000004,"Precision preserved");
foreach(DataGridViewColumn col in grid.Columns) Check(col.SortMode==DataGridViewColumnSortMode.NotSortable,"Manual sorting disabled");
// Even programmatic reordering must not move values to another condition.
grid.Sort(grid.Columns["Item"],ListSortDirection.Ascending);
grid.Rows[0].Cells["Min"].Value="4.4";
apply.Invoke(form,null);
Check(session.Saved && session.Values[1].Min==4.4 && session.Values[0].Min==0.00000004,"Correct condition after row reorder");
session.FailReadAt=session.Reads+1;
Check(!(bool)read.Invoke(form,null) && grid.Rows.Count==0,"Read failure clears stale grid");
Check(!((Button)typeof(InspectionSetupForm).GetField("_apply",flags).GetValue(form)).Enabled,"Read failure disables apply");
}
session=new Session(); using(var form=new InspectionSetupForm(session,"Camera 1")) {
read.Invoke(form,null); session.FailReadAt=6; apply.Invoke(form,null);
var state=(Label)typeof(InspectionSetupForm).GetField("_state",flags).GetValue(form);
Check(session.Saved && state.Text.Contains("재조회 실패") && !state.Text.Contains("저장 완료"),"Post-save refresh failure must not report success");
}
Console.WriteLine("PASS: precision, row identity, failed reload, failed post-save refresh");
}
}
