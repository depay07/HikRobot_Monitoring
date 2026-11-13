using System;
using System.Collections.Generic;
using System.Linq;
using SC6000DualMonitor;

class FtpRecipeTests
{
    const string Folder = "/root/vmtempfiles/ftp/solution";
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static string Fails(Action action) { try { action(); } catch(Exception ex) { checks++; return ex.Message; } throw new Exception("Expected failure"); }
    class Api : ICameraFtpApi
    {
        public List<string> Calls = new List<string>();
        public List<FtpFileRecord> Files = new List<FtpFileRecord>();
        public string Ip, User, Password, LastDirectory, LastDelete, LastFrom, LastTo;
        public uint Port; public bool Encoded, FailList, FailDelete, ThrowProtocol; public int ProtocolReturn, AuthError, DeleteError, Deletes, Disconnects;
        public int Initialize() { Calls.Add("init"); return 0; }
        public int SetProtocol(int protocol) { Calls.Add("protocol:" + protocol); if(ThrowProtocol)throw new InvalidOperationException("protocol invocation failed"); return ProtocolReturn; }
        public int Activation(string ip) { Calls.Add("activation"); return 0; }
        public int Connect(string ip, uint port, string user, string password, ref IntPtr handle, bool encoded)
        { Calls.Add("connect"); Ip=ip; Port=port; User=user; Password=password; Encoded=encoded; handle=new IntPtr(7); return AuthError; }
        public int Disconnect(IntPtr h) { Disconnects++; Calls.Add("disconnect"); return 0; }
        public IList<FtpFileRecord> List(IntPtr h, string directory)
        { Calls.Add("list"); LastDirectory=directory; if(FailList)throw new Exception("query error"); return Files.ToArray(); }
        public int Delete(IntPtr h, string path)
        { Deletes++; LastDelete=path; if(DeleteError!=0)return DeleteError; if(FailDelete)return 123; Files.RemoveAll(f=>f.Name==RecipeFilePaths.Name(path)); return 0; }
        public int Rename(IntPtr h, string from, string to)
        { LastFrom=from;LastTo=to;Files.Single(f=>f.Name==RecipeFilePaths.Name(from)).Name=RecipeFilePaths.Name(to);return 0; }
    }
    static FtpFileRecord File(string name, string mode="-rw-r--r--", int links=1)
    { return new FtpFileRecord { Name=name,Permissions=mode,Links=links,Size=100,Modified="timestamp" }; }
    static FtpRecipeSession Session(Api api, Func<string> current=null, string password="config-password", Action<string> log=null)
    { return new FtpRecipeSession("192.168.10.51", password, current ?? (()=>Folder+"/active.solx"), ()=>api, log); }
    static int Main()
    {
        try
        {
            var a=new Api(); a.Files.AddRange(new[]{File("active.solx"),File("한글 검사.solx"),File("dir.solx","drwx"),File("link.solx","lrwx"),File("hard.solx",links:2),File("config.ini"),File("unknown.solx","")});
            var logs=new List<string>();
            using(var s=Session(a,log:logs.Add))
            {
                Check(a.Calls.Count==0,"opening dialog does not connect synchronously");
                var list=s.List(Folder); Check(list.Count==2,"only regular unlinked recipes offered");
                Check(a.Calls.Take(5).SequenceEqual(new[]{"init","protocol:2","activation","connect","list"}),"native tool handshake order FTPES first");
                Check(a.Ip=="192.168.10.51"&&a.Port==21&&a.User=="vmftp"&&a.Password=="config-password"&&!a.Encoded,"config password sent unchanged with vmftp/21");
                Check(a.LastDirectory=="/solution"&&list[1].Path==Folder+"/한글 검사.solx","query path translated but canonical path retained");
                Check(s.Exists(Folder+"/dir.solx")&&s.Exists(Folder+"/link.solx"),"collision checks include directories and links");
                Check(!s.Exists(Folder+"/absent.solx"),"absent only after successful list");
                Check(a.Calls.Count(c=>c=="connect")==1,"reuse dedicated handle");
                var manager=new RecipeFileManager(s,null);manager.Refresh();
                Fails(()=>manager.Delete(Folder+"/active.solx"));Check(a.Deletes==0,"active recipe protected");
                Fails(()=>manager.Rename(Folder+"/한글 검사.solx","dir"));Check(a.LastFrom==null,"never overwrite directory");
                manager.Rename(Folder+"/한글 검사.solx","새 이름");
                Check(a.LastFrom==Folder+"/한글 검사.solx"&&a.LastTo==Folder+"/새 이름.solx","rename uses full device paths");
                manager.Refresh();manager.Delete(Folder+"/새 이름.solx");
                Check(a.Deletes==1&&a.LastDelete==Folder+"/새 이름.solx","delete full path then verifies absence");
                Check(logs.All(l=>!l.Contains("config-password")),"password not logged");
            }
            Check(a.Disconnects==1,"dispose disconnects only dedicated handle");
            a=new Api { AuthError=99 };using(var s=Session(a)) {
                Check(Fails(()=>s.List(Folder)).Contains("00000063"),"native login status retained");
                Check(!a.Calls.Contains("list")&&a.Disconnects==1,"failed login never lists and releases partial handle");
            }
            foreach(int undefinedReturn in new[]{33,unchecked((int)0xF34902D0)}) {
                a=new Api { ProtocolReturn=undefinedReturn };using(var s=Session(a)) {
                    s.List(Folder);
                    Check(a.Calls.SequenceEqual(new[]{"init","protocol:2","activation","connect","list"}),"undefined protocol return is not a failure; FTPES only");
                }
            }
            a=new Api { ThrowProtocol=true };using(var s=Session(a)) {
                Fails(()=>s.List(Folder));Check(!a.Calls.Contains("connect")&&!a.Calls.Contains("activation"),"protocol invocation exception still stops login without plain FTP fallback");
            }
            a=new Api { FailList=true };using(var s=Session(a)) {
                Fails(()=>s.Exists(Folder+"/absent.solx"));Check(a.Disconnects==1,"query failure never treated as absence");
            }
            a=new Api { FailDelete=true };using(var s=Session(a)) {
                Fails(()=>s.Delete(Folder+"/old.solx"));Check(a.Deletes==1&&a.Disconnects==1,"ambiguous mutation never retried");
                s.List(Folder);Check(a.Calls.Count(c=>c=="connect")==2&&a.Deletes==1,"explicit refresh reconnects without repeating delete");
            }
            a=new Api { DeleteError=unchecked((int)0xE0001309) };a.Files.Add(File("한글 검사.solx"));using(var s=Session(a)) {
                string message=Fails(()=>s.Delete(Folder+"/한글 검사.solx"));
                Check(message.Contains("E0001309")&&message.Contains(Folder+"/한글 검사.solx")&&message.Contains("접근을 거부"),"access-denied error retains status and exact target");
                Check(!message.Contains("PASSWORD")&&!message.Contains("config-password"),"delete denial does not misdiagnose login or reveal credential");
                Check(a.Deletes==1&&a.Files.Count==1&&a.Disconnects==1,"denial leaves mock file intact without destructive retry");
            }
            a=new Api();using(var s=Session(a,password:new string('x',64))) {s.List(Folder);Check(!a.Encoded,"64-character config password is still plaintext not E1");}
            a=new Api();using(var s=Session(a,password:"")) {Fails(()=>s.List(Folder));Check(a.Calls.Count==0,"missing password does not attempt anonymous login");}
            a=new Api();using(var s=Session(a,current:()=>{throw new Exception("SDK disconnected");})) {
                Fails(()=>s.List(Folder));Check(a.Calls.Count==0,"lost viewer attachment stops FTP before login");
            }
            foreach(var path in new[]{"/root/secret.solx","/root/vmtempfiles/ftp-other/a.solx",Folder+"/../a.solx","/solution/a.solx"}) Fails(()=>CameraFtpPaths.Full(path));
            Check(CameraFtpPaths.Relative(CameraFtpPaths.Root)=="/","FTP root mapped exactly");
            a=new Api();a.Files.Add(File("../wrong.solx"));using(var s=Session(a)){Fails(()=>s.List(Folder));}
            a=new Api();a.Files.Add(File("old.solx"));string active=Folder+"/active.solx";
            using(var s=Session(a,()=>active)){var m=new RecipeFileManager(s,null);m.Refresh();active=Folder+"/old.solx";Fails(()=>m.Delete(active));Check(a.Deletes==0,"recipe switched during dialog protected");}
            a=new Api();var b=new Api();using(var one=Session(a))using(var two=new FtpRecipeSession("192.168.10.52","second-password",()=>Folder+"/active.solx",()=>b,null)) {
                one.List(Folder);two.List(Folder);Check(a.Password=="config-password"&&b.Password=="second-password"&&b.Ip=="192.168.10.52","camera IP and password independent");
            }
            Console.WriteLine("PASS: "+checks+" FTPES session, paths, credentials and protection checks (mock server)");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
