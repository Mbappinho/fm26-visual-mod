#nullable disable
using System.Text.Json;
using System.Text.Json.Serialization;
namespace FM26.VisualDiagnostics;

// Bounded dense selector-input PREFIX, not rendered/native motion proof. Only two actors are enabled by the host.
// All storage is allocated once. Values()/Snapshot()/JSON allocate only when producing the final report.
public sealed class FifaMotionTrace
{
    public const int Capacity = 3200, MaximumActors = 2;
    private readonly FifaMotionTraceRow[] _rows = new FifaMotionTraceRow[Capacity];
    private int _count;
    private long _total;
    public void Add(int frame, double matchTime, double dt, double speed, double mx, double mz, double fx, double fz,
        int resets, bool windowReady, bool paused, bool discontinuity, FifaLocomotion loco)
    {
        _total++;
        if (_count == Capacity) return;
        _rows[_count++] = new FifaMotionTraceRow { Frame = frame, MatchTime = matchTime, Dt = dt, RawSpeed = speed, MoveX = mx, MoveZ = mz, FaceX = fx, FaceZ = fz,
            Rebaselines = resets, Flags = (windowReady ? 1 : 0) | (paused ? 2 : 0) | (discontinuity ? 4 : 0), FilteredSpeed = loco.FilteredSpeed,
            RawAngle = FifaLocomotion.SignedAngle(mx, mz, fx, fz), FilteredAngle = loco.FilteredAngle, Tier = loco.Tier, Direction = loco.Direction,
            Target = loco.TargetIndex, Source = loco.SourceIndex, Pending = loco.PendingIndex, Phase = loco.TargetPhase, SourcePhase = loco.FromPhase, Weight = loco.Weight, Rate = loco.Rate };
    }
    public FifaMotionTraceReport Snapshot() => new() { TotalRows = _total, DroppedRows = _total - _count, Rows = _rows.AsSpan(0, _count).ToArray() };
}
public sealed class FifaMotionTraceReport
{
    public string Schema => "fm26.fifa-motion-trace.v1";
    public string SelectorVersion => "0.7.1";
    public string InitialState => "constructor: idle clip, phase 0, no source/pending, empty filters/dwell/statistics; clip indices follow FifaOverlayPolicy.V2Clips; pack metadata/hash in enclosing report";
    public string Semantics => "prefix of every v2 WritePose selector attempt before Unity upload; apply Rebaselines Snap calls first; Update only if flags bit0 windowReady, not bit1 paused, not bit2 discontinuity; no omitted selector calls within prefix; missing suffix is NOT replayable";
    public string[] Columns => new[] { "Frame", "MatchTime", "Dt", "RawSpeed", "MoveX", "MoveZ", "FaceX", "FaceZ", "Rebaselines", "Flags", "FilteredSpeed", "RawAngle", "FilteredAngle", "Tier", "Direction", "Target", "Source", "Pending", "Phase", "SourcePhase", "Weight", "Rate" };
    public int Capacity => FifaMotionTrace.Capacity;
    public long TotalRows { get; init; }
    public long DroppedRows { get; init; }
    public FifaMotionTraceRow[] Rows { get; init; }
}
[JsonConverter(typeof(FifaMotionTraceRowConverter))]
public struct FifaMotionTraceRow
{
    public int Frame, Rebaselines, Flags, Tier, Direction, Target, Source, Pending;
    public double MatchTime, Dt, RawSpeed, MoveX, MoveZ, FaceX, FaceZ, FilteredSpeed, Phase, SourcePhase, Weight, Rate;
    public double? RawAngle, FilteredAngle;
    public double?[] Values() => new double?[] { Frame, MatchTime, Dt, RawSpeed, MoveX, MoveZ, FaceX, FaceZ, Rebaselines, Flags, FilteredSpeed, RawAngle, FilteredAngle, Tier, Direction, Target, Source, Pending, Phase, SourcePhase, Weight, Rate };
}
public sealed class FifaMotionTraceRowConverter : JsonConverter<FifaMotionTraceRow>
{
    public override FifaMotionTraceRow Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException("Capture-only report row; offline replay reads its declared numeric columns.");
    public override void Write(Utf8JsonWriter writer, FifaMotionTraceRow value, JsonSerializerOptions options)
    {
        // One compact numeric array even inside the indented final envelope; no per-frame serialization.
        writer.WriteRawValue(JsonSerializer.Serialize(value.Values()), skipInputValidation: false);
    }
}
