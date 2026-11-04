using System.Reflection;

// H238: a session-context change while suspended (fm-view-changed) ends the segment normally (fm-context-changed).
if (Environment.GetEnvironmentVariable("H238_CASE") is string h238 && h238.Length > 0) { H238SuspendedContextTests.Run(h238); return 0; }
H238SuspendedContextTests.Run();

// H234: per-frame teleport bound scaled with MATCH dt (FM fast-forward up to 30x, live-015).
if (Environment.GetEnvironmentVariable("H234_CASE") is string h234 && h234.Length > 0) { H234FastPlaybackTests.Run(h234); return 0; }
H234FastPlaybackTests.Run();

// H211: fast AUTO restart after a roster-changed stop (next segment seeded with the roster watch's confirmed selection).
if (Environment.GetEnvironmentVariable("H211_CASE") is string h211 && h211.Length > 0) { H211FastRestartTests.Run(h211); return 0; }
H211FastRestartTests.Run();

// H209: AUTO start gate on visible players + actor-part context predicate latch (live-014).
if (Environment.GetEnvironmentVariable("H209_CASE") is string h209 && h209.Length > 0) { H209HiddenViewTests.Run(h209); return 0; }
H209HiddenViewTests.Run();

// H206: half-time FM session-context change (session predicate latch + fm-context-changed), shared partial executed.
if (Environment.GetEnvironmentVariable("H206_CASE") is string h206 && h206.Length > 0) { H206SessionContextTests.Run(h206); return 0; }
H206SessionContextTests.Run();

// H203: post-resume settle window (key-moment repositioning with a running clock) = discontinuity (pure).
if (Environment.GetEnvironmentVariable("H203_CASE") is string h203 && h203.Length > 0) { H203ResumeSettleTests.Run(h203); return 0; }
H203ResumeSettleTests.Run();

// H200: paused-clock FM repositioning (key-moment highlights) = discontinuity (pure).
if (Environment.GetEnvironmentVariable("H200_CASE") is string h200 && h200.Length > 0) { H200RepositionTests.Run(h200); return 0; }
H200RepositionTests.Run();

// H191: overlay segments (pure policies).
if (Environment.GetEnvironmentVariable("H191_CASE") is string h191 && h191.Length > 0) { H191SegmentTests.Run(h191); return 0; }
H191SegmentTests.Run();

// H186: pure auto host step (copied observations only).
if (Environment.GetEnvironmentVariable("H186_CASE") is string h186 && h186.Length > 0) { H186AutoHostTests.Run(h186); return 0; }
H186AutoHostTests.Run();

if (Environment.GetEnvironmentVariable("H185_CASE") is string h185 && h185.Length > 0) { H185NativeObservationTests.Run(h185); return 0; }
H185NativeObservationTests.Run();

// H184a: actual unused coordinator entry point, copied observations only.
if (Environment.GetEnvironmentVariable("H184_CASE") is string h184 && h184.Length > 0) { H184aAutoCoordinatorTests.Run(h184); return 0; }
H184aAutoCoordinatorTests.Run();

// H182e: pure configuration bytes only; no file/host integration.
if (Environment.GetEnvironmentVariable("H182_CASE")?.StartsWith("config-") == true) { H182eAutoConfigurationTests.Run(Environment.GetEnvironmentVariable("H182_CASE")); return 0; }
H182eAutoConfigurationTests.Run();

// H182d: executable pure reservation tests, with no host submission.
if (Environment.GetEnvironmentVariable("H182_CASE")?.StartsWith("attempt-") == true) { H182dAutoAttemptTests.Run(Environment.GetEnvironmentVariable("H182_CASE")); return 0; }
H182dAutoAttemptTests.Run();

// H182c: executable pure candidate tests (no host activation).
if (Environment.GetEnvironmentVariable("H182_CASE")?.StartsWith("candidate-") == true) { H182cAutoCandidateTests.Run(Environment.GetEnvironmentVariable("H182_CASE")); return 0; }
H182cAutoCandidateTests.Run();
// H182: focused offline decision tests; the full suite also runs these cases.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("H182_CASE"))) { H182AutoActivationTests.Run(Environment.GetEnvironmentVariable("H182_CASE")); return 0; }

// H178a: focused goalkeeper case (TDD cycles); the full entry point below also runs every H178 case.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("H178_CASE"))) { H178Tests.Run(Environment.GetEnvironmentVariable("H178_CASE")); return 0; }

if (Environment.GetEnvironmentVariable("H173_CASE") == "budget") { FifaOverlayMultiTests.Run(); return 0; }
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("H173_CASE"))) { H173Tests.Run(Environment.GetEnvironmentVariable("H173_CASE")); return 0; }

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("H173_REPLAY"))) { H173Replay.Run(Environment.GetEnvironmentVariable("H173_REPLAY")); return 0; }

// H166: focused run of the multi-actor tests only (TDD cycles); the full entry point below is unchanged.
if (Environment.GetEnvironmentVariable("FIFA_MULTI_ONLY") == "1") { FifaOverlayMultiTests.Run(); return 0; }
// H171: focused run of the locomotion tests only (TDD cycles).
if (Environment.GetEnvironmentVariable("FIFA_LOCO_ONLY") == "1") { FifaLocomotionTests.Run(); H173Tests.Run(); return 0; }
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIFA_LOCO_BENCH"))) { FifaLocomotionBench.Run(Environment.GetEnvironmentVariable("FIFA_LOCO_BENCH")); return 0; }

var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.DiagnosticsPolicy");
if (type == null)
{
    Console.Error.WriteLine("FAIL: match capture policy has not been implemented.");
    return 1;
}
var method = type.GetMethod("CanCapture")!;
var cases = new (bool HasRoot, int MeshCount, bool Expected)[]
{
    (false, 0, false),
    (false, 40, false),
    (true, 0, false),
    (true, -1, false),
    (true, 1, true),
    (true, 60, true),
};
foreach (var item in cases)
{
    var actual = (bool)method.Invoke(null, new object[] { item.HasRoot, item.MeshCount })!;
    if (actual != item.Expected)
        throw new Exception($"Capture policy failed: {item}");
}
Console.WriteLine($"PASS: {cases.Length} match capture policy cases.");
VisualPolicyTests.Run();
GeometryTests.Run();
PoseOwnerPolicyTests.Run();
GpuCapturePolicyTests.Run();
BoneMappingPolicyTests.Run();
CandidatePosePolicyTests.Run();
GhostDisplayPolicyTests.Run();
CandidateContextGuardTests.Run();
SemanticEventPolicyTests.Run();
OriginalPassPolicyTests.Run();
AnticipatoryPassPolicyTests.Run();
FifaOverlayPolicyTests.Run();
FifaOverlayLifecycleTests.Run();
FifaOverlaySuspensionTests.Run();
FifaOverlayDiscontinuityTests.Run();
FifaOverlayMultiTests.Run();
FifaLocomotionTests.Run();
H173Tests.Run();
H178Tests.Run(null);
H182AutoActivationTests.Run();
return 0;
