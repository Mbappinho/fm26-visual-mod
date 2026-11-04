#nullable disable
using System.Diagnostics;
using System.Text.Json;
using FM26.VisualDiagnostics;

// H171: managed benchmark of the pack v2 locomotion path (selector + phase-synced two-clip blend into a reused per-actor
// buffer) for 20 actors, on the user's REAL private pack v2 when present (validated exactly like the plugin's LoadPack:
// header rules, sizes, sha256, finite frames, axis), else on a synthetic pack. Unity upload/normals/bounds are NOT measured
// here (they need the game); live FrameCost stays the authority. Run with FIFA_LOCO_BENCH=<output json path>.
internal static class FifaLocomotionBench
{
    public static void Run(string output)
    {
        var lad = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var dir = Path.Combine(lad ?? "", "fm26-visual-mod", "packs", "fifa17-player-v2");
        var load = Stopwatch.StartNew();
        FifaPackHeader pack; var frames = new Dictionary<string, float[]>(); string source;
        if (File.Exists(Path.Combine(dir, "fifa-pack.json")))
        {
            var headerBytes = File.ReadAllBytes(Path.Combine(dir, "fifa-pack.json"));
            using (var doc = JsonDocument.Parse(headerBytes)) pack = FifaOverlayPolicy.ValidateHeader(doc.RootElement);
            foreach (var clip in pack.Clips.Values)
            {
                var data = File.ReadAllBytes(Path.Combine(dir, clip.File));
                FifaOverlayPolicy.RequirePayload(data, clip.Sha256, clip.Bytes);
                frames[clip.Name] = FifaOverlayPolicy.ApplyAxis(pack.Matrix, FifaOverlayPolicy.DecodeFrames(data, clip.Frames, pack.VertexCount));
            }
            source = "real private pack v2 (validated with the plugin's C# rules)";
        }
        else
        {
            using (var doc = JsonDocument.Parse(FifaLocomotionTests.HeaderV2().ToJsonString())) pack = FifaOverlayPolicy.ValidateHeader(doc.RootElement);
            foreach (var clip in pack.Clips.Values) frames[clip.Name] = new float[clip.Frames * pack.VertexCount * 3];
            source = "synthetic header (no private pack v2 found)";
        }
        load.Stop();
        const int actors = 20, warm = 600, measured = 3600; const double fps = 60, playback = 1.75;
        var locos = new FifaLocomotion[actors]; var buffers = new System.Numerics.Vector3[actors][];
        var traces = new FifaMotionTrace[actors];
        for (int i = 0; i < Math.Min(actors, FifaMotionTrace.MaximumActors); i++) traces[i] = new FifaMotionTrace();
        for (int i = 0; i < actors; i++) { locos[i] = new FifaLocomotion(pack); buffers[i] = new System.Numerics.Vector3[pack.VertexCount]; }
        var samples = new double[measured]; long blendFrames = 0, blendActorFrames = 0;
        long allocated = 0;
        for (int f = 0; f < warm + measured; f++)
        {
            if (f == warm) allocated = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp(); int blending = 0;
            for (int i = 0; i < actors; i++)
            {
                // per-actor synthetic FM motion: speed sweeps 0..8 m/s, heading turns relative to a fixed facing (forward/strafe/back)
                double t = f / fps * playback + i * 3.7;
                double speed = Math.Max(0, 4.0 + 4.0 * Math.Sin(t * 0.21 + i));
                double angle = Math.Sin(t * 0.13 + i * 0.5) * Math.PI;
                double dt = playback / fps;
                var l = locos[i];
                l.Update(dt, speed, Math.Sin(angle) * speed * 0.25, Math.Cos(angle) * speed * 0.25, 0, 1);
                traces[i]?.Add(f, t, dt, speed, Math.Sin(angle) * speed * 0.25, Math.Cos(angle) * speed * 0.25, 0, 1, 0, true, false, false, l);
                if (l.Blending) { blending++; FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(frames[l.FromClip], l.FromFrame, frames[l.TargetClip], l.TargetFrame, l.Weight, pack.VertexCount, buffers[i]); }
                else FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(frames[l.TargetClip], l.TargetFrame, pack.VertexCount, buffers[i]);
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (f >= warm) { samples[f - warm] = ms; if (blending > 0) { blendFrames++; blendActorFrames += blending; } }
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var sorted = samples.OrderBy(x => x).ToArray();
        double P(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
        var stats = locos.Select(l => l.Stats()).ToArray();
        var report = new
        {
            Source = source, PackDirectory = dir, PackLoadAndValidateMilliseconds = load.Elapsed.TotalMilliseconds,
            DecodedFloatBytes = frames.Values.Sum(a => (long)a.Length * 4), Actors = actors, MeasuredFrames = measured, AssumedFps = fps, AssumedPlaybackSpeed = playback,
            P50Milliseconds = P(0.50), P95Milliseconds = P(0.95), P99Milliseconds = P(0.99), MaxMilliseconds = sorted[^1],
            BlendFrames = blendFrames, BlendActorFrames = blendActorFrames, AllocatedBytesDuringMeasurement = allocated,
            TierFades = stats.Sum(s => s.TierFades), DirectionFades = stats.Sum(s => s.DirectionFades), MidFadeSnaps = stats.Sum(s => s.MidFadeSnaps),
            ClipFrames = stats.SelectMany(s => s.ClipFrames).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            Scope = "managed selector + vertex blend/copy only; Unity Mesh.vertices upload, RecalculateNormals/Bounds and rendering not measured (live FrameCost)",
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"BENCH: {source}; 20 actors p50 {report.P50Milliseconds:F4} ms, p95 {report.P95Milliseconds:F4} ms, max {report.MaxMilliseconds:F4} ms; blend frames {blendFrames}/{measured}; allocated {allocated} B; load {report.PackLoadAndValidateMilliseconds:F0} ms");
    }
}
