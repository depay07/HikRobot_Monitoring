using System;
using SC6000DualMonitor;
class CurrentRecipeSaveTests
{
    static int checks;
    static void Check(bool ok) { if (!ok) throw new Exception("Check failed"); checks++; }
    static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception("Expected refusal"); }
    static void Main()
    {
        string current = "/root/vmtempfiles/ftp/solution/Model_C.solx", written = null;
        int writes = 0;
        var save = new CurrentRecipeSave(() => current, p => { writes++; written = p; return p; });
        save.Begin(); save.Save(); Check(written == current && writes == 1);
        // Both a later save and rollback retain the same filename.
        save.Save(); Check(written == current && writes == 2);
        current = "/root/vmtempfiles/ftp/solution/Model_D.solx";
        Reject(save.Save); Check(writes == 2);
        foreach (string bad in new[] { null, "", "Model_C.solx", "/root/solution/", "/root/a.png" })
        { current = bad; Reject(save.Begin); Reject(save.Save); }
        Check(writes == 2);
        current = "/root/vmtempfiles/ftp/solution/검사.solx";
        save.Begin(); save.Save(); Check(written == current);
        current = @"D:\solution\Model_C.sol";
        save.Begin(); save.Save(); Check(written == current);
        var failed = new CurrentRecipeSave(() => current, p => null);
        failed.Begin(); Reject(failed.Save);
        var sdkFailure = new CurrentRecipeSave(() => current, p => { throw new InvalidOperationException("SDK save failed"); });
        sdkFailure.Begin(); Reject(sdkFailure.Save);
        Console.WriteLine("PASS: " + checks + " current recipe overwrite checks");
    }
}
