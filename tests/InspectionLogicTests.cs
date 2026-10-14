using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SC6000DualMonitor;

internal static class InspectionLogicTests
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }
    private static void Throws(Action action, string text) { try { action(); } catch (InvalidOperationException) { _checks++; return; } throw new Exception(text); }
    private sealed class FakeSession : IInspectionSettingsSession
    {
        public IList<ConditionRange> Values;
        public bool Paused, Resumed;
        public int Writes, Saves;
        public int FailWriteAt;
        public bool FailAllWrites, FailSaveOnce, MismatchOnce;
        public FakeSession(IList<ConditionRange> values) { Values = values.ToList(); }
        public IList<ConditionRange> ReadRanges()
        {
            if (MismatchOnce && Writes > 0) { MismatchOnce = false; return new[] { new ConditionRange(Values[0].Definition, -1, -1) }; }
            return Values.ToList();
        }
        public void WriteRange(ConditionRange range)
        {
            Writes++;
            if (FailAllWrites || Writes == FailWriteAt) throw new InvalidOperationException("write failure");
            int index = Values.ToList().FindIndex(r => r.Definition.Name == range.Definition.Name);
            Values[index] = range;
        }
        public void Pause() { Paused = true; }
        public void Resume() { Paused = false; Resumed = true; }
        public void Save() { Saves++; if (FailSaveOnce) { FailSaveOnce = false; throw new InvalidOperationException("save failure"); } }
    }
    private static void Main()
    {
        var definitions = ConditionDefinition.Parse(ConditionDefinition.DefaultItems);
        Check(definitions.Count == 5 && definitions[3].IsInteger, "default mapping");
        Throws(() => ConditionDefinition.Parse("float0:A;float0:B"), "duplicate mapping");
        Throws(() => ConditionDefinition.Parse("bad:A"), "invalid mapping");
        Throws(() => new ConditionRange(definitions[0], 3, 2).Validate(), "inverted bounds");
        Throws(() => new ConditionRange(definitions[0], double.NaN, 2).Validate(), "NaN");
        Throws(() => new ConditionRange(definitions[3], 1.1, 2).Validate(), "integer fraction");
        Throws(() => new ConditionRange(definitions[3], 0, (double)int.MaxValue + 1).Validate(), "integer overflow");
        Check(InspectionCounter.Classify(true, " OK ") == InspectionOutcome.Ok, "OK mapping");
        Check(InspectionCounter.Classify(true, "ng") == InspectionOutcome.Ng, "NG mapping");
        Check(InspectionCounter.Classify(false, "OK") == InspectionOutcome.Error, "error overrides stale OK");
        Check(InspectionCounter.Classify(true, "1") == InspectionOutcome.Error, "never guess numeric mapping");
        var counter = new InspectionCounter();
        Parallel.For(0, 3000, i => counter.Record(new InspectionResultEventArgs((InspectionOutcome)(i % 3), "test")));
        var count = counter.Read();
        Check(count.Ok == 1000 && count.Ng == 1000 && count.Errors == 1000 && count.Total == 3000, "concurrent counter");
        counter.Reset(); Check(counter.Read().Total == 0, "reset");
        var before = new[] { new ConditionRange(definitions[0], 13.8, 14.3), new ConditionRange(definitions[4], 6.3, 6.7) };
        var after = new[] { new ConditionRange(definitions[0], 15, 16), new ConditionRange(definitions[4], 7, 8) };
        var success = new FakeSession(before); InspectionSettingsController.Apply(success, after);
        Check(success.Values[0].Matches(after[0]) && success.Saves == 1 && success.Resumed, "apply readback save resume");
        var partial = new FakeSession(before) { FailWriteAt = 2 };
        Throws(() => InspectionSettingsController.Apply(partial, after), "partial failure");
        Check(partial.Values[0].Matches(before[0]) && partial.Values[1].Matches(before[1]) && partial.Resumed && partial.Saves == 0, "partial rollback");
        var badSave = new FakeSession(before) { FailSaveOnce = true };
        Throws(() => InspectionSettingsController.Apply(badSave, after), "save failure");
        Check(badSave.Values[0].Matches(before[0]) && badSave.Saves == 2 && badSave.Resumed, "restore persisted state after failed save");
        var badReadback = new FakeSession(before) { MismatchOnce = true };
        Throws(() => InspectionSettingsController.Apply(badReadback, after), "readback mismatch");
        Check(badReadback.Values[0].Matches(before[0]) && badReadback.Resumed, "readback rollback");
        var brokenRecovery = new FakeSession(before) { FailAllWrites = true };
        Throws(() => InspectionSettingsController.Apply(brokenRecovery, after), "recovery failure");
        Check(brokenRecovery.Paused && !brokenRecovery.Resumed, "failed recovery remains stopped");
        var invalid = new FakeSession(before);
        Throws(() => InspectionSettingsController.Apply(invalid, new[] { new ConditionRange(definitions[0], 2, 1) }), "validation before writes");
        Check(invalid.Writes == 0 && !invalid.Paused, "invalid input cannot touch camera");
        string directory = Path.Combine(Path.GetTempPath(), "willb-model-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PartNumberStore(directory);
            Check(store.Read("camera1", "/recipe/A.solx", "default") == "default", "initial part default");
            store.Write("camera1", "/recipe/A.solx", "PP25003-3(C-L) & <제품>");
            Check(store.Read("camera1", "/recipe/A.solx", "") == "PP25003-3(C-L) & <제품>", "XML roundtrip");
            store.Write("camera1", "/recipe/A.solx", "updated");
            Check(store.Read("camera1", "/recipe/A.solx", "") == "updated", "atomic overwrite");
            Check(store.Read("camera2", "/recipe/A.solx", "other") == "other", "camera isolation");
            Check(store.Read("camera1", "/recipe/B.solx", "other") == "other", "recipe isolation");
            Throws(() => store.Write("camera1", "", "wrong"), "unknown path cannot save");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + _checks + " inspection logic checks");
    }
}
