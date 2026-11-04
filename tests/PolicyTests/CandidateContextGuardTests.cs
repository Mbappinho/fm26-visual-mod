using System.Reflection;
using System.Text.Json;

internal static class CandidateContextGuardTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.CandidateContextGuard")
            ?? throw new Exception("FAIL: first-failing candidate context guard is missing.");
        var assertions = 0;
        void Equal(object? expected, object? actual, string label)
        {
            assertions++;
            if (!Equals(expected, actual)) throw new Exception($"FAIL: {label}: expected {expected}, got {actual}.");
        }
        foreach (var stage in new[] {"start", "update", "late"})
        {
            var guard = Activator.CreateInstance(type)!;
            var failure = type.GetProperty("Failure")!;
            var reject = type.GetMethod("Reject")!;
            Equal(null, failure.GetValue(guard), "no invented failure before a predicate rejects");
            Equal(false, reject.Invoke(guard, new object?[] {"actor-pointer-changed",stage,101,123456789L,10000000L,-27,12,13}), "failure returns false");
            var first = failure.GetValue(guard)!;
            object? Field(string name) => first.GetType().GetProperty(name)!.GetValue(first);
            Equal("actor-pointer-changed", Field("Predicate"), "precise stable code");
            Equal(stage, Field("Stage"), "literal callback stage");
            Equal(101, Field("ActualFrame"), "actual observation frame, not request frame");
            Equal(123456789L, Field("QpcTicks"), "actual QPC observation");
            Equal(10000000L, Field("QpcFrequency"), "QPC frequency retained");
            Equal(-27, Field("SubjectInstanceId"), "negative cached identity preserved");
            Equal(12, Field("Expected"), "bounded expected primitive fact");
            Equal(13, Field("Observed"), "bounded already observed primitive fact");
            Equal(false, reject.Invoke(guard, new object?[] {"active-scene-name-changed","late",102,123456790L,10000000L,null,null,null}), "later failure rejects too");
            Equal(true, ReferenceEquals(first, failure.GetValue(guard)), "stop/cleanup cannot overwrite first failure");
            var json = JsonSerializer.SerializeToElement(first);
            Equal(8, json.EnumerateObject().Count(), "bounded scalar-only diagnostic fields");
            Equal("actor-pointer-changed", json.GetProperty("Predicate").GetString(), "serialization preserves precise reason");
            Equal(false, first.GetType().GetProperties().Any(p => p.CanWrite), "retained failure immutable");
        }
        Console.WriteLine($"PASS: {assertions} first-failing context guard assertions.");
    }
}
