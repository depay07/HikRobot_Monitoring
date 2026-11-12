using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SC6000DualMonitor;

class RecipeFileManagerTests
{
    static int checks;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++; }
    static void Reject(Action action,string message) { try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(message); }
    const string Root="/root/vmtempfiles/ftp/solution";
    const string Active=Root+"/현재 검사.solx", Old=Root+"/이전 검사.solx", Spare=Root+"/예비.solx";
    class Fake : IRecipeFileSession
    {
        public string Current=Active;
        public Dictionary<string,RecipeFile> Files=new Dictionary<string,RecipeFile>();
        public int Deletes,Renames,CurrentReads;
        public bool FailList,FailExists,IgnoreDelete,FailDeleteAfterRemoving;
        public Action AfterList;
        public string CurrentPath { get { CurrentReads++;return Current; } }
        public Fake(){foreach(string p in new[]{Active,Old,Spare})Files.Add(p,new RecipeFile{Path=p,Size=1024,Modified="Nov 12 10:30"});}
        public IList<RecipeFile> List(string directory)
        {if(FailList)throw new InvalidOperationException("network unavailable");var r=Files.Values.Select(f=>new RecipeFile{Path=f.Path,Size=f.Size,Modified=f.Modified}).ToArray();if(AfterList!=null)AfterList();return r;}
        public bool Exists(string path){if(FailExists)throw new InvalidOperationException("existence query failed");return Files.ContainsKey(path);}
        public void Delete(string path){Deletes++;if(!IgnoreDelete)Files.Remove(path);if(FailDeleteAfterRemoving)throw new InvalidOperationException("reply lost");}
        public void Rename(string from,string to){Renames++;var f=Files[from];Files.Remove(from);f.Path=to;Files.Add(to,f);}
    }
    static RecipeFileManager Manager(Fake f){var m=new RecipeFileManager(f,null);m.Refresh();return m;}
    static T Field<T>(object o,string n){return (T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Wait(Func<bool> ready){var watch=Stopwatch.StartNew();while(!ready()){if(watch.ElapsedMilliseconds>5000)throw new Exception("UI wait timeout");Application.DoEvents();Thread.Sleep(1);}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Check(RecipeFilePaths.Normalize("/root//recipes/")=="/root/recipes","normalize separators");
            Check(RecipeFilePaths.Directory("/one.solx")=="/"&&RecipeFilePaths.Normalize("/")=="/","root folder");
            Check(RecipeFilePaths.Normalize("C:\\")=="C:/"&&RecipeFilePaths.Same("C:/A.solx","c:\\a.solx"),"Windows camera path case");
            Check(!RecipeFilePaths.Same("/A.solx","/a.solx"),"Linux names case sensitive");
            foreach(string path in new[]{"","relative.solx","/root/../active.solx","/root/./active.solx","/root/a\n.solx","//server/file.solx"})Reject(()=>RecipeFilePaths.Normalize(path),"invalid path "+path);
            foreach(string name in new[]{"","../current","bad/name","bad\\name","bad:name","bad?name","bad\nname",".","..","trailing.",new string('한',100)})Reject(()=>RecipeFilePaths.Renamed(Old,name),"invalid rename "+name);
            Check(RecipeFilePaths.Renamed(Old,"한글 제품 02")==Root+"/한글 제품 02.solx","Korean name keeps extension");
            Check(RecipeFilePaths.Renamed(Old,"새이름.solx")==Root+"/새이름.solx","pasted extension not doubled");
            var f=new Fake();var m=Manager(f);Check(m.Snapshot.Files[0].Path==Active&&m.Snapshot.Directory==Root,"active first and device folder default");
            Reject(()=>m.Delete(Active),"active delete blocked");Reject(()=>m.Rename(Active,"new"),"active rename blocked");Check(f.Deletes==0&&f.Renames==0,"protected actions never reach FTP");
            Reject(()=>m.Delete(Root+"/missing.solx"),"unknown target blocked");Reject(()=>m.Delete("/another/이전 검사.solx"),"outside folder blocked");
            m.Delete(Old);Check(f.Deletes==1&&!f.Files.ContainsKey(Old)&&f.Files.ContainsKey(Active),"only chosen file deleted");
            Reject(()=>m.Delete(Spare),"must refresh after mutation");Check(m.Snapshot==null,"snapshot invalidated after write");
            m.Refresh();string renamed=m.Rename(Spare,"새 제품");Check(f.Files.ContainsKey(renamed)&&f.Renames==1&&!f.Files.ContainsKey(Spare),"rename postcondition");
            f=new Fake();m=Manager(f);Reject(()=>m.Rename(Old,"예비"),"collision blocked");Check(f.Renames==0,"collision cannot overwrite");
            Reject(()=>m.Rename(Old,"이전 검사"),"same name rejected");
            f.Current=Old;Reject(()=>m.Delete(Old),"selected file became active after confirmation");Check(f.Deletes==0,"new active file untouched");
            f=new Fake();m=Manager(f);f.AfterList=()=>f.Current=Old;Reject(()=>m.Delete(Old),"active switched during fresh list");Check(f.Deletes==0,"last-moment active check");
            f=new Fake();m=Manager(f);f.Files[Old].Size++;Reject(()=>m.Delete(Old),"target file replaced");Check(f.Deletes==0,"changed file untouched");
            f=new Fake();m=Manager(f);f.Files.Remove(Old);Reject(()=>m.Delete(Old),"target vanished");
            f=new Fake();m=Manager(f);f.FailList=true;Reject(()=>m.Refresh(),"refresh offline");Check(m.Snapshot==null,"no stale list after offline refresh");
            f=new Fake();m=Manager(f);f.Current="";Reject(()=>m.Delete(Old),"unknown current prevents delete");
            f=new Fake();m=Manager(f);f.FailExists=true;Reject(()=>m.Rename(Old,"새 이름"),"collision query failure");Check(f.Renames==0,"query failure is never interpreted as target absent");
            f=new Fake{IgnoreDelete=true};m=Manager(f);Reject(()=>m.Delete(Old),"no false delete success");Check(m.Snapshot==null,"unverified delete invalidates list");
            f=new Fake{FailDeleteAfterRemoving=true};m=Manager(f);Reject(()=>m.Delete(Old),"server removed but response lost");Check(f.Deletes==1&&m.Snapshot==null&&!f.Files.ContainsKey(Old),"no destructive retry after uncertain result");
            f=new Fake();f.AfterList=()=>f.Current=Spare;Reject(()=>Manager(f),"recipe switch during initial refresh");
            f=new Fake();f.Files.Add("/etc/not-a-recipe.txt",new RecipeFile{Path="/etc/not-a-recipe.txt"});Reject(()=>Manager(f),"unexpected list entry rejected");
            f=new Fake();m=new RecipeFileManager(f,null);
            using(var form=new RecipeFilesForm(m,"1번 카메라 · 192.168.10.51"))
            {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();
                var grid=Field<DataGridView>(form,"_grid");Wait(()=>grid.Rows.Count==3&&!Field<bool>(form,"_busy"));
                Check(grid.SelectedRows.Count==0&&!Field<Button>(form,"_delete").Enabled,"no default destructive selection");
                grid.Rows[0].Selected=true;Check(!Field<Button>(form,"_delete").Enabled&&!Field<Button>(form,"_rename").Enabled,"active row disables action buttons");
                grid.ClearSelection();grid.Rows[1].Selected=true;Check(Field<Button>(form,"_delete").Enabled&&Field<Button>(form,"_rename").Enabled,"inactive selected row enables actions");
                var search=Field<TextBox>(form,"_search");search.Text="이전";Check(grid.Rows.Count==1&&grid.SelectedRows.Count==0,"search filters and clears old selection");search.Text="";
                if(args.Length>0)using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[0]);}
                Check(f.Deletes==0&&f.Renames==0,"selection/search/preview never modifies camera");form.Close();
            }
            Console.WriteLine("PASS: "+checks+" recipe file protection, errors, naming and UI checks (fake camera only)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
