#nullable disable
using System.Text.Json;
using FM26.VisualDiagnostics;

// Executes the two actual C# implementations. H171Locomotion is the retained source with TYPE NAMES ONLY changed.
internal static class H173Replay
{
    private readonly record struct Input(double Dt, double Speed, double X, double Z, double Fx, double Fz);
    internal static void Run(string evidenceDirectory)
    {
        var p = FifaLocomotionTests.PackHeader();
        string header = Path.Combine(evidenceDirectory, "fifa-pack-v2-header.json");
        if (File.Exists(header)) p = FifaOverlayPolicy.ValidateHeader(JsonDocument.Parse(File.ReadAllBytes(header)).RootElement);
        var cases = new List<object>();
        foreach (string scenario in new[] { "threshold-noise", "turning", "stop-start", "wrap-180", "mixed" })
        {
            var inputs = new List<Input>();
            for (int f = 0; f < 120 * 120; f++)
            {
                double t = f / 120.0, speed = 2.3 + 0.55 * Math.Sin(t * 37) + 0.13 * Math.Cos(t * 83), angle = 45 + 22 * Math.Sin(t * 29);
                if (scenario == "turning") { speed = 2.9; angle = 150 * Math.Sin(t * 0.45) + 12 * Math.Sin(t * 51); }
                if (scenario == "stop-start") { speed = t % 12 < 4 ? 0.23 + 0.18 * Math.Sin(t * 43) : 1.8; angle = 0; }
                if (scenario == "wrap-180") { speed = 2.9; angle = 180 + 5 * Math.Sin(t * 43); }
                if (scenario == "mixed") { speed = 4 + 3.8 * Math.Sin(t * 0.6) + 0.2 * Math.Sin(t * 41); angle = 180 * Math.Sin(t * 0.23); }
                double r = angle * Math.PI / 180;
                inputs.Add(new Input(1.0 / 120, speed, Math.Sin(r) * speed * 0.25, Math.Cos(r) * speed * 0.25, 0, 1));
            }
            cases.Add(Compare("SYNTHETIC-120Hz-" + scenario, inputs, p));
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidenceDirectory, "fifa-overlay-live-007.json")));
        int sampleCount = 0, segments = 0;
        var speedDeltas = new List<double>(); var angleDeltas = new List<double>();
        foreach (var actor in doc.RootElement.GetProperty("Actors").EnumerateArray())
        {
            JsonElement? prev = null; double? previousDerivedAngle = null;
            var inputs = new List<Input>();
            foreach (var row in actor.GetProperty("Timeline").EnumerateArray())
            {
                sampleCount++;
                if (row.GetProperty("Kind").GetString() != "sample") { prev = null; continue; }
                if (prev.HasValue)
                {
                    var a = prev.Value; double dt = row.GetProperty("MatchTimeSeconds").GetDouble() - a.GetProperty("MatchTimeSeconds").GetDouble();
                    bool contiguous = row.GetProperty("FrameCount").GetInt32() - a.GetProperty("FrameCount").GetInt32() == 10 && dt > 0 && dt <= 4;
                    if (!contiguous) { if (inputs.Count > 0) { cases.Add(Compare("SAMPLED-SURROGATE-" + actor.GetProperty("ActorInstanceId") + "-segment-" + segments++, inputs, p)); inputs.Clear(); } previousDerivedAngle = null; }
                    else
                    {
                        var pos = row.GetProperty("RootPosition"); var before = a.GetProperty("RootPosition"); var face = row.GetProperty("RootForward");
                        double speed = row.GetProperty("MatchSpeedMetersPerSecond").GetDouble();
                        double x = pos.GetProperty("X").GetDouble() - before.GetProperty("X").GetDouble(), z = pos.GetProperty("Z").GetDouble() - before.GetProperty("Z").GetDouble();
                        double fx = face.GetProperty("X").GetDouble(), fz = face.GetProperty("Z").GetDouble();
                        inputs.Add(new Input(dt, speed, x, z, fx, fz));
                        speedDeltas.Add(Math.Abs(speed - a.GetProperty("MatchSpeedMetersPerSecond").GetDouble()));
                        double? angle = FifaLocomotion.SignedAngle(x, z, fx, fz);
                        if (angle.HasValue && previousDerivedAngle.HasValue) { double d = Math.Abs(angle.Value - previousDerivedAngle.Value); angleDeltas.Add(Math.Min(d, 360 - d)); }
                        previousDerivedAngle = angle;
                    }
                }
                prev = row;
            }
            if (inputs.Count > 0) cases.Add(Compare("SAMPLED-SURROGATE-" + actor.GetProperty("ActorInstanceId") + "-segment-" + segments++, inputs, p));
        }
        double Percentile(List<double> v, double q) { v.Sort(); return v.Count == 0 ? 0 : v[(int)Math.Floor((v.Count - 1) * q)]; }
        var result = new { Claim = "Offline actual-source comparison; sampled rows are NOT an exact dense replay. Missing window vectors reconstructed from adjacent sampled positions; inter-row dt replaces unretained steps; gaps reset state. SYNTHETIC signals are not FM evidence.", SampleRows = sampleCount, Segments = segments, SampleSpeedDeltaP50 = Percentile(speedDeltas, .5), SampleSpeedDeltaP95 = Percentile(speedDeltas, .95), DerivedAngleDeltaP50 = Percentile(angleDeltas, .5), DerivedAngleDeltaP95 = Percentile(angleDeltas, .95), Cases = cases };
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "replay-results.json"), JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
    internal static int VerifyTrace(JsonElement trace, FifaPackHeader pack)
    {
        if (trace.GetProperty("Schema").GetString() != "fm26.fifa-motion-trace.v1" || trace.GetProperty("SelectorVersion").GetString() != "0.7.1") throw new ArgumentException("Unsupported trace contract.");
        var columns = new FifaMotionTraceReport().Columns;
        if (!trace.GetProperty("Columns").EnumerateArray().Select(x => x.GetString()).SequenceEqual(columns)) throw new ArgumentException("Trace columns differ.");
        var l = new FifaLocomotion(pack); var scratch = new FifaMotionTrace(); int checkedRows = 0;
        foreach (var row in trace.GetProperty("Rows").EnumerateArray())
        {
            var a = row.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (double?)null : x.GetDouble()).ToArray();
            if (a.Length != columns.Length) throw new ArgumentException("Trace row width differs.");
            int resets = checked((int)a[8].Value), flags = checked((int)a[9].Value);
            if (resets < 0 || resets > 501 || a[8] != resets || flags < 0 || flags > 7 || a[9] != flags) throw new ArgumentException("Trace event flags outside producer bounds.");
            for (int i = 0; i < resets; i++) l.Snap();
            if ((flags & 1) != 0 && (flags & 6) == 0) l.Update(a[2].Value, a[3].Value, a[4].Value, a[5].Value, a[6].Value, a[7].Value);
            scratch.Add((int)a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value, a[5].Value, a[6].Value, a[7].Value, resets, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, l);
            checkedRows++;
        }
        var computed = scratch.Snapshot(); int n = 0;
        foreach (var row in trace.GetProperty("Rows").EnumerateArray())
        {
            if (n >= computed.Rows.Length) throw new ArgumentException("Oversized trace.");
            var values = computed.Rows[n].Values(); int i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                double? expected = cell.ValueKind == JsonValueKind.Null ? null : cell.GetDouble();
                if (i >= 10 && expected != values[i]) throw new InvalidOperationException($"Trace mismatch row {n}, column {columns[i]}: recorded {expected}, replay {values[i]}.");
                i++;
            }
            n++;
        }
        if (checkedRows > FifaMotionTrace.Capacity || trace.GetProperty("TotalRows").GetInt64() - trace.GetProperty("DroppedRows").GetInt64() != checkedRows) throw new ArgumentException("Trace counts differ.");
        return checkedRows;
    }
    private static object Compare(string name, List<Input> inputs, FifaPackHeader pack)
    {
        var old = new H171Locomotion(pack); var next = new FifaLocomotion(pack); double seconds = 0;
        foreach (var i in inputs) { old.Update(i.Dt, i.Speed, i.X, i.Z, i.Fx, i.Fz); next.Update(i.Dt, i.Speed, i.X, i.Z, i.Fx, i.Fz); seconds += i.Dt; }
        var a = old.Stats(); var b = next.Stats();
        return new { Name = name, Inputs = inputs.Count, Seconds = seconds,
            Old = new { Switches = a.TierFades + a.DirectionFades, SwitchesPerSecond = (a.TierFades + a.DirectionFades) / seconds, a.MidFadeSnaps, ClampLowPercent = 100.0 * a.RateClampedLow / a.Updates, ClampHighPercent = 100.0 * a.RateClampedHigh / a.Updates },
            New = new { Switches = b.TierFades + b.DirectionFades, SwitchesPerSecond = (b.TierFades + b.DirectionFades) / seconds, b.MidFadeSnaps, ClampLowPercent = 100.0 * b.RateClampedLow / b.Updates, ClampHighPercent = 100.0 * b.RateClampedHigh / b.Updates } };
    }
}
