#nullable disable
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FM26.VisualDiagnostics;

// H154 stage 1 — pure policies for the opt-in single-player FIFA overlay. No Unity types, no native reads.
// Every threshold below is a DECLARED VISUAL HEURISTIC (logged in reports), not a native FM/FIFA contract.
public sealed class FifaOverlayRequest
{
    public string Session { get; set; }
    public string RequestId { get; set; }
    public string Action { get; set; }
    public int ActorInstanceId { get; set; }
    public int MaximumSeconds { get; set; }
    public string PackDirectory { get; set; }
    // H160: opt-in reversible hiding of the target FM player's renderers (Renderer.forceRenderingOff only). Default false.
    public bool HideNativePlayer { get; set; }
    // H166: alternative selector ActorInstanceIds = "all-eligible" (AllEligible) or an explicit 1..24 unique nonzero list.
    // Exactly one of ActorInstanceId (legacy single form, N=1 path) / ActorInstanceIds is present.
    public int[] ActorInstanceIds { get; set; }
    public bool AllEligible { get; set; }
    public bool Multi => AllEligible || ActorInstanceIds != null;
}

public sealed class FifaPackClip
{
    public string Name { get; set; }
    public string File { get; set; }
    public int Frames { get; set; }
    public int Keys => Frames - 1;
    public long Bytes { get; set; }
    public string Sha256 { get; set; }
    // H171 (pack v2 only; v1 clips keep the defaults): declared loop/role/timing metadata.
    public bool Loop { get; set; }
    public string Tier { get; set; }
    public string Direction { get; set; }
    public double NativeSpeed { get; set; }
    public double? KeysPerCycle { get; set; }
    public int? CyclesInLoop { get; set; }
    public int? CycleOriginKey { get; set; }
}

public sealed class FifaPackHeader
{
    public int FormatVersion { get; set; } = 1; // H171: 1 = jog_F2+walk_F2 (0.6.x behaviour), 2 = H170 locomotion set
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public float[] Matrix { get; set; }
    public bool FlipWinding { get; set; }
    public double RestProxyHeight { get; set; }
    public double RestExtent { get; set; }
    public string TrianglesFile { get; set; }
    public long TrianglesBytes { get; set; }
    public string TrianglesSha256 { get; set; }
    public Dictionary<string, FifaPackClip> Clips { get; set; }
    // H160 (2): per-piece ranges from the v1 header "meshes" (builder concatenation order).
    public IReadOnlyList<FifaPackPiece> Pieces { get; set; }
}

public sealed class FifaPackPiece
{
    public string Token { get; init; }
    public string Role { get; init; }
    public int VertexStart { get; init; }
    public int VertexCount { get; init; }
    public int TriangleStart { get; init; }
    public int TriangleCount { get; init; }
}

public static class FifaOverlayPolicy
{
    public const string StartAction = "start-fifa-overlay";
    public const string StopAction = "stop-fifa-overlay";
    public const string Schema = "fm26.fifa-pack.v1";
    public const int VertexCount = 1868;
    public const int TriangleCount = 2911;
    public const int KeysPerSecond = 30;
    public const int MinimumSeconds = 1;
    public const int MaximumSecondsCap = 3600; // H162: whole-match bound (was 120); deadline semantics unchanged
    public const int MaximumClipFrames = 4096;
    public const long MaximumHeaderBytes = 65536;
    public const double MaximumAbsVertex = 100.0; // shared with tools/fifa_pack.py verify (cross-checked test)
    // Declared visual heuristics.
    public const double StationaryBelowMetersPerSecond = 0.3;
    // H156 declared heuristic: walk/jog hysteresis (enter jog >= 2.7, leave jog < 2.3 m/s) replaces the single 2.5 cut.
    public const double JogEnterAtMetersPerSecond = 2.7;
    public const double JogLeaveBelowMetersPerSecond = 2.3;
    // H156 declared heuristic: standing pose = walk_F2 key 0 (pack frame 1) instead of the T-pose rest frame 0.
    // The rest frame stays in the pack (layout rest_frame=0) for verification only.
    public const string StandClip = "walk_F2";
    public const int StandFrame = 1;
    public const int RestFrame = 0;
    public const double WalkReferenceMetersPerSecond = 1.4;
    public const double JogReferenceMetersPerSecond = 3.5;
    public const double MinimumRate = 0.25;
    public const double MaximumRate = 3.0;
    public const double MaximumPlausibleSpeed = 15.0;
    public const double MaximumStepSeconds = 1.0;
    // H158: per-frame bound on the MATCH clock step (seek/skip detection), declared as 1 s wall stall bound x 4x playback.
    // H234: FM actually publishes up to 30x (live-015 published playback speed 30); there a frame longer than ~0.133 s wall exceeds
    // this step and is handled as a forward discontinuity (re-baseline) on the active path, not as a failure.
    public const double MaximumMatchStepSeconds = 4.0;
    // H158 declared: horizontal root displacement between two consecutive managed updates above this is a teleport/cut.
    // H234: with a running clock FifaMatchMotion.Observe uses max(this, MaximumPlausibleSpeed x match dt); the paused (dt 0)
    // and post-resume settle discontinuity decisions keep this fixed bound.
    public const double MaximumFrameDisplacementMeters = 3.0;
    // H158: the only time source used for speed/phase. Consumer read already used by SemanticEventObserver; literal CurrentPlaybackSpeed is reported only.
    public const string PlaybackSpeedSource = "fm-published-match-time: MatchPlaybackController.m_match.m_matchFrame (FM.Match.MatchFrame.CurrentTime, match seconds); "
        + "CurrentPlaybackSpeed reported per timeline row, not used; Unity Time.timeScale not used";
    public const double SpeedWindowSeconds = 0.25;
    public const double MinimumScale = 0.5;
    public const double MaximumScale = 2.0;
    public static readonly string[] RequiredClips = { "jog_F2", "walk_F2" };
    // H171: pack format v2 (H170 FIFA17 locomotion set). Shared with tools/fifa_pack.py (tests/test_fifa_cross_contract.py).
    public const string SchemaV2 = "fm26.fifa-pack.v2";
    public const long MaximumPackBytesV2 = 100663296;
    public const double V2MinimumNativeSpeed = 0.3;
    public const double V2MaximumNativeSpeed = 12.0;
    public const int V2MinimumKeysPerCycle = 4;
    public const string PackFolderV1 = "fifa17-player-v1", PackFolderV2 = "fifa17-player-v2";
    public static readonly (string Name, string Tier, string Direction)[] V2Clips =
    {
        ("idle", "idle", "none"), ("walk", "walk", "forward"), ("jog", "jog", "forward"), ("run", "run", "forward"), ("sprint", "sprint", "forward"),
        ("strafe_right_walk", "walk", "strafe-right"), ("strafe_right_jog", "jog", "strafe-right"), ("strafe_left_jog", "jog", "strafe-left"),
        ("back_walk", "walk", "backward"), ("back_jog", "jog", "backward"),
    };
    // H160 (2): the six H140 pieces in builder order (file "mesh-<k>-<token>_...") and their DECLARED display roles.
    public static readonly (string Token, string Role)[] PieceOrder =
        { ("body", "skin"), ("jersey", "shirt"), ("shorts", "shorts"), ("sock", "socks"), ("shoes", "shoes"), ("headlod", "head") };
    // H160 (2): fixed declared palette (RGBA, linear 0..1), one owned material per piece, same URP shader as before. Not a command field.
    public static readonly IReadOnlyDictionary<string, float[]> Palette = new Dictionary<string, float[]>
    {
        ["skin"] = new[] { 0.80f, 0.60f, 0.47f, 1f }, ["head"] = new[] { 0.80f, 0.60f, 0.47f, 1f },
        ["shirt"] = new[] { 0.10f, 0.30f, 0.78f, 1f }, ["shorts"] = new[] { 0.95f, 0.95f, 0.95f, 1f },
        ["socks"] = new[] { 0.85f, 0.15f, 0.15f, 1f }, ["shoes"] = new[] { 0.08f, 0.08f, 0.08f, 1f },
    };
    public static readonly string[] Heuristics =
    {
        "clip: speed<0.3 m/s -> stand = walk_F2 key 0 (pack frame 1, not the T-pose rest); walk_F2 <-> jog_F2 hysteresis: enter jog >=2.7, leave jog <2.3 m/s (horizontal root speed over >=0.25 MATCH seconds window)",
        "phase: keys += matchDt*30*rate, rate=speed/ref (walk 1.4, jog 3.5 m/s) bounded [0.25,3]; nearest-lower key, no interpolation; normalized phase carried on clip switch; time = FM published match time (MATCH seconds), so animation follows FM playback speed",
        "pause: match time not advancing -> clip/phase/pose held, no stop; stops: single-frame horizontal root jump > max(3 m, 15 m/s x MATCH dt) (H234: FM fast-forward publishes up to 30x), window speed > 15 m/s (MATCH seconds), match clock backwards/nonfinite or step > 4 s, wall step > 1 s",
        "scale: uniform = |Character_Head - mid(Character_LeftFoot,Character_RightFoot)| at start / pack head_to_feet_anchor_proxy; refused outside [0.5,2]",
        "pose: owned root at FM Player.transform.position, yaw only from Player.transform.forward; no offset; native player stays visible unless HideNativePlayer=true (Renderer.forceRenderingOff on its start-captured renderers, restored at stop)",
        "colour: one submesh + owned material per pack piece (body/head skin, shirt blue, shorts white, socks red, shoes dark), fixed palette, same URP shader as the selected FM player",
        "view change: all target renderers inactive (enabled unchanged, structure intact) after a guard refusal -> suspend (owned root inactive, natives untouched, pose frozen); resume when they return to their start activity (and our forceRenderingOff is intact) with match clock/position re-baselined; FM reset of forceRenderingOff -> native-hide-reset stop; partial reactivation -> failure; <=200 suspensions, <=120 s real each -> suspend-limit; structure/actor gone while suspended -> fm-match-ended",
        "pack v2 H173: speed exponential lowpass tau=0.3 MATCH seconds; idle enters below filtered 0.25 for 0.2 s, exits above 0.5; tiers use adjacent native-speed midpoints +/-0.35 m/s hysteresis and 0.45 s candidate/minimum dwell; rate=filtered speed/native speed bounded [0.6,1.5], idle 1; heuristics, not proven foot planting",
        "pack v2 direction: signed angle FM move direction over the 0.25 s match window vs FM facing, exponential unit-vector lowpass tau=0.4 MATCH seconds, 0.45 s candidate/minimum dwell; forward <=45, strafe 45..135 by sign, backpedal >135, 10 deg hysteresis; strafe/backpedal only in walk/jog below 4 m/s; fallback walk+strafe-left -> strafe_left_jog at its clamped rate",
        "pack v2 crossfade: 0.25 s (tier) / 0.3 s (direction only) match time, two clips max, incoming clip enters at the same normalised gait-cycle phase (cycle origin = declared left-stance key, keys_per_cycle from the pack), both advance by the same cycle fraction; while fading retain only one latest pending target, never interrupt the source; suspension resume / time discontinuity snap once to the target, reset filters/dwell/pending, then hold selection and phase until the speed window refills; nearest-lower key per clip, vertex lerp between the two clips",
        "time discontinuity (H164): after the full context/owned/flag guards, a finite match-clock step backwards (any amount: goal replay start; small backwards jitter is indistinguishable and treated the same) or forward > 4 s (dead-time skip, replay end) -> recorded in Discontinuities[] (<=500, then discontinuity-limit stop), match motion re-baselined (no teleport test on that frame), clip/phase held that frame, overlay keeps following the actor (native stays hidden); nonfinite clock, a teleport (single-frame jump > max(3 m, 15 m/s x MATCH dt)) without a discontinuity on the same frame, and wall stalls still fail",
    };

    // H166: multi-actor selector.
    public const int MaximumActors = 24;
    public const string AllEligibleSelector = "all-eligible";
    private static readonly HashSet<string> StartFields = new() { "Session", "RequestId", "Action", "ActorInstanceId", "ActorInstanceIds", "MaximumSeconds", "PackDirectory", "HideNativePlayer" };
    private static readonly HashSet<string> StopFields = new() { "Session", "RequestId", "Action" };

    public static bool LiteralId(string value) => !string.IsNullOrEmpty(value) && value.Length <= 64
        && value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_');

    public static FifaOverlayRequest ParseRequest(JsonElement data, string session)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("FIFA overlay command must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal); // H156 S3: duplicate keys refused (no last-wins)
        foreach (var property in data.EnumerateObject())
            if (!seen.Add(property.Name)) throw new ArgumentException($"Duplicate field {property.Name}.");
        string Str(string name)
        {
            if (!data.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) throw new ArgumentException($"Missing string {name}.");
            return v.GetString();
        }
        var action = Str("Action");
        var fields = action == StartAction ? StartFields : action == StopAction ? StopFields : throw new ArgumentException("Unsupported FIFA overlay action.");
        foreach (var property in data.EnumerateObject())
            if (!fields.Contains(property.Name)) throw new ArgumentException($"Unexpected field {property.Name}.");
        var requestSession = Str("Session");
        if (string.IsNullOrEmpty(session) || requestSession != session) throw new ArgumentException("FIFA overlay command belongs to another session.");
        var requestId = Str("RequestId");
        if (!LiteralId(requestId)) throw new ArgumentException("Invalid literal request ID.");
        if (action == StopAction) return new FifaOverlayRequest { Session = requestSession, RequestId = requestId, Action = action };
        bool single = data.TryGetProperty("ActorInstanceId", out var actor), multi = data.TryGetProperty("ActorInstanceIds", out var actors);
        if (single == multi) throw new ArgumentException("Exactly one of ActorInstanceId / ActorInstanceIds is required.");
        int actorId = 0; int[] actorIds = null; bool allEligible = false;
        if (single && (actor.ValueKind != JsonValueKind.Number || !actor.TryGetInt32(out actorId) || actorId == 0))
            throw new ArgumentException("ActorInstanceId must be one nonzero integer.");
        if (multi)
        {
            if (actors.ValueKind == JsonValueKind.String && actors.GetString() == AllEligibleSelector) allEligible = true;
            else if (actors.ValueKind == JsonValueKind.Array && actors.GetArrayLength() >= 1 && actors.GetArrayLength() <= MaximumActors)
            {
                var ids = new List<int>();
                foreach (var item in actors.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var id) || id == 0 || ids.Contains(id))
                        throw new ArgumentException("ActorInstanceIds must hold unique nonzero integers.");
                    ids.Add(id);
                }
                actorIds = ids.ToArray();
            }
            else throw new ArgumentException($"ActorInstanceIds must be \"{AllEligibleSelector}\" or an array of 1..{MaximumActors} unique nonzero integers.");
        }
        if (!data.TryGetProperty("MaximumSeconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var maximum)
            || maximum < MinimumSeconds || maximum > MaximumSecondsCap)
            throw new ArgumentException($"MaximumSeconds must be an integer in [{MinimumSeconds},{MaximumSecondsCap}].");
        string pack = null;
        if (data.TryGetProperty("PackDirectory", out var packValue))
        {
            if (packValue.ValueKind != JsonValueKind.String) throw new ArgumentException("PackDirectory must be a string.");
            pack = packValue.GetString();
            if (!LocalDrivePath(pack))
                throw new ArgumentException("PackDirectory must be a bounded local drive-letter absolute path (no UNC/device/relative/traversal/ADS).");
        }
        bool hide = false;
        if (data.TryGetProperty("HideNativePlayer", out var hideValue))
        {
            if (hideValue.ValueKind != JsonValueKind.True && hideValue.ValueKind != JsonValueKind.False) throw new ArgumentException("HideNativePlayer must be a JSON boolean.");
            hide = hideValue.GetBoolean();
        }
        return new FifaOverlayRequest { Session = requestSession, RequestId = requestId, Action = action, ActorInstanceId = actorId, MaximumSeconds = maximum, PackDirectory = pack,
            HideNativePlayer = hide, ActorInstanceIds = actorIds, AllEligible = allEligible };
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // H156 S1: accept only "X:\seg\seg" on a drive letter. Refuses UNC (\\host), device/NT namespaces (\\?\, \\.\, \??\),
    // forward slashes, drive-relative (C:x), rooted-relative (\x), relative, '.'/'..'/'...' and empty segments,
    // trailing space/dot, ':' streams (ADS), wildcard/control characters and DOS device names.
    public static bool LocalDrivePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4 || path.Length > 240) return false;
        char d = path[0];
        if (!((d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z')) || path[1] != ':' || path[2] != '\\') return false;
        if (path.IndexOf('/') >= 0 || path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || path.Any(char.IsControl)) return false;
        foreach (var segment in path.Substring(3).Split('\\'))
        {
            if (segment.Length == 0 || segment.All(c => c == '.') || segment.EndsWith(' ') || segment.EndsWith('.')) return false;
            if (ReservedDeviceNames.Contains(segment.Split('.')[0].TrimEnd(' '))) return false;
        }
        return Path.IsPathFullyQualified(path);
    }

    public static string DefaultPackDirectory(string localAppData)
    {
        if (string.IsNullOrEmpty(localAppData) || !Path.IsPathFullyQualified(localAppData)) throw new ArgumentException("LOCALAPPDATA unavailable.");
        return Path.Combine(localAppData, "fm26-visual-mod", "packs", PackFolderV1);
    }

    // H171: default pack = the v2 folder when its header file exists, otherwise the v1 folder (0.6.x behaviour). An explicit
    // PackDirectory is never redirected. headerExists is File.Exists in the plugin (injected for tests).
    public static string ResolveDefaultPackDirectory(string localAppData, Func<string, bool> headerExists)
    {
        var v1 = DefaultPackDirectory(localAppData);
        var v2 = Path.Combine(localAppData, "fm26-visual-mod", "packs", PackFolderV2);
        return headerExists != null && headerExists(Path.Combine(v2, "fifa-pack.json")) ? v2 : v1;
    }

    private static JsonElement Prop(JsonElement e, string name, JsonValueKind kind)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != kind) throw new ArgumentException($"Pack header field {name} invalid.");
        return v;
    }
    private static int Int(JsonElement e, string name) => Prop(e, name, JsonValueKind.Number).TryGetInt32(out var v) ? v : throw new ArgumentException($"Pack header {name} not int.");
    private static long Long(JsonElement e, string name) => Prop(e, name, JsonValueKind.Number).TryGetInt64(out var v) ? v : throw new ArgumentException($"Pack header {name} not int64.");
    private static string File(JsonElement e)
    {
        var f = Prop(e, "file", JsonValueKind.String).GetString();
        if (string.IsNullOrEmpty(f) || f.Length > 64 || !f.All(c => ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) || c == '-' || c == '_' || c == '.') || f.StartsWith('.') || f.Contains(".."))
            throw new ArgumentException("Pack payload file name must be a plain local name.");
        return f;
    }
    private static string Sha(JsonElement e)
    {
        var s = Prop(e, "sha256", JsonValueKind.String).GetString();
        if (s == null || s.Length != 64 || !s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new ArgumentException("Pack sha256 malformed.");
        return s;
    }

    public static FifaPackHeader ValidateHeader(JsonElement header)
    {
        var schema = Prop(header, "schema", JsonValueKind.String).GetString();
        int format = schema == Schema ? 1 : schema == SchemaV2 ? 2 : throw new ArgumentException("Unsupported pack schema.");
        if (Int(header, "vertex_count") != VertexCount || Int(header, "triangle_count") != TriangleCount) throw new ArgumentException("Pack mesh shape mismatch.");
        if (Int(header, "keys_per_second") != KeysPerSecond) throw new ArgumentException("Pack key rate mismatch.");
        var axis = Prop(header, "axis_mapping", JsonValueKind.Object);
        var rows = Prop(axis, "matrix", JsonValueKind.Array);
        var matrix = new float[9];
        if (rows.GetArrayLength() != 3) throw new ArgumentException("Axis matrix must be 3x3.");
        int r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3) throw new ArgumentException("Axis matrix must be 3x3.");
            int c = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Number || !cell.TryGetInt32(out var v) || v < -1 || v > 1) throw new ArgumentException("Axis matrix must be a signed permutation.");
                matrix[r * 3 + c++] = v;
            }
            r++;
        }
        for (int i = 0; i < 3; i++)
        {
            if (Enumerable.Range(0, 3).Count(j => matrix[i * 3 + j] != 0) != 1 || Enumerable.Range(0, 3).Count(j => matrix[j * 3 + i] != 0) != 1)
                throw new ArgumentException("Axis matrix must be a signed permutation.");
        }
        int det = (int)(matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[7]) - matrix[1] * (matrix[3] * matrix[8] - matrix[5] * matrix[6]) + matrix[2] * (matrix[3] * matrix[7] - matrix[4] * matrix[6]));
        if (Int(axis, "determinant") != det) throw new ArgumentException("Declared axis determinant inconsistent.");
        var flipElement = axis.TryGetProperty("flip_winding", out var f) && (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False) ? f.GetBoolean() : throw new ArgumentException("flip_winding missing.");
        if (flipElement != (det < 0)) throw new ArgumentException("flip_winding inconsistent with determinant.");
        var heights = Prop(header, "rest_height", JsonValueKind.Object);
        double proxy = Prop(heights, "head_to_feet_anchor_proxy", JsonValueKind.Number).GetDouble();
        double extent = Prop(heights, "extent", JsonValueKind.Number).GetDouble();
        if (!double.IsFinite(proxy) || proxy < 0.5 || proxy > 5 || !double.IsFinite(extent) || extent < proxy) throw new ArgumentException("Pack rest height implausible.");
        var tri = Prop(header, "triangles", JsonValueKind.Object);
        if (Int(tri, "count") != TriangleCount || Long(tri, "bytes") != (long)TriangleCount * 3 * 2) throw new ArgumentException("Triangle payload shape mismatch.");
        var clips = new Dictionary<string, FifaPackClip>();
        var clipObject = Prop(header, "clips", JsonValueKind.Object);
        if (format == 2) clips = ValidateV2Clips(clipObject);
        else if (clipObject.EnumerateObject().Count() != RequiredClips.Length) throw new ArgumentException("Pack clip set must be exactly jog_F2 + walk_F2.");
        foreach (var name in format == 2 ? Array.Empty<string>() : RequiredClips)
        {
            var clip = Prop(clipObject, name, JsonValueKind.Object);
            int frames = Int(clip, "frames");
            if (frames < 2 || frames > MaximumClipFrames) throw new ArgumentException($"Clip {name} frame count out of bounds.");
            if (Int(clip, "rest_frame") != 0 || Int(clip, "first_key_frame") != 1) throw new ArgumentException($"Clip {name} layout unsupported.");
            long bytes = Long(clip, "bytes");
            if (bytes != (long)frames * VertexCount * 3 * 4) throw new ArgumentException($"Clip {name} byte shape mismatch.");
            clips[name] = new FifaPackClip { Name = name, File = File(clip), Frames = frames, Bytes = bytes, Sha256 = Sha(clip) };
        }
        return new FifaPackHeader
        {
            FormatVersion = format, VertexCount = VertexCount, TriangleCount = TriangleCount, Matrix = matrix, FlipWinding = flipElement,
            RestProxyHeight = proxy, RestExtent = extent, TrianglesFile = File(tri), TrianglesBytes = (long)TriangleCount * 6,
            TrianglesSha256 = Sha(tri), Clips = clips, Pieces = ValidatePieces(header),
        };
    }

    // H171: pack v2 clip rules (same as tools/fifa_pack.py _v2_clip_errors): exactly the V2Clips set, v1 layout/bytes per clip,
    // keys = frames-1, frame_range length = keys, loop true, declared tier/direction, idle speed 0 and no cycle fields; moving
    // clips: native speed in [0.3,12] m/s, integer cycles >= 1, keys_per_cycle = keys/cycles in [4, keys], 0 <= origin < keys;
    // all clip payloads together <= MaximumPackBytesV2.
    private static Dictionary<string, FifaPackClip> ValidateV2Clips(JsonElement clipObject)
    {
        if (clipObject.EnumerateObject().Count() != V2Clips.Length) throw new ArgumentException("Pack v2 clip set must be exactly the declared locomotion clips.");
        var clips = new Dictionary<string, FifaPackClip>(); long total = 0;
        foreach (var (name, tier, direction) in V2Clips)
        {
            var clip = Prop(clipObject, name, JsonValueKind.Object);
            int frames = Int(clip, "frames");
            if (frames < 2 || frames > MaximumClipFrames) throw new ArgumentException($"Clip {name} frame count out of bounds.");
            if (Int(clip, "rest_frame") != 0 || Int(clip, "first_key_frame") != 1) throw new ArgumentException($"Clip {name} layout unsupported.");
            long bytes = Long(clip, "bytes");
            if (bytes != (long)frames * VertexCount * 3 * 4) throw new ArgumentException($"Clip {name} byte shape mismatch.");
            total += bytes;
            int keys = Int(clip, "keys");
            if (keys != frames - 1) throw new ArgumentException($"Clip {name} keys mismatch.");
            var range = Prop(clip, "frame_range", JsonValueKind.Array);
            if (range.GetArrayLength() != 2 || !range[0].TryGetInt32(out var r0) || !range[1].TryGetInt32(out var r1) || r0 < 0 || r1 - r0 + 1 != keys)
                throw new ArgumentException($"Clip {name} frame range mismatch.");
            if (!clip.TryGetProperty("loop", out var loop) || loop.ValueKind != JsonValueKind.True) throw new ArgumentException($"Clip {name} must be a loop.");
            if (Prop(clip, "tier", JsonValueKind.String).GetString() != tier || Prop(clip, "direction", JsonValueKind.String).GetString() != direction)
                throw new ArgumentException($"Clip {name} declared role mismatch.");
            double speed = Prop(clip, "native_speed_mps", JsonValueKind.Number).GetDouble();
            bool Absent(string field) => !clip.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null;
            var result = new FifaPackClip { Name = name, File = File(clip), Frames = frames, Bytes = bytes, Sha256 = Sha(clip), Loop = true, Tier = tier, Direction = direction };
            if (tier == "idle")
            {
                if (!double.IsFinite(speed) || speed != 0) throw new ArgumentException($"Clip {name} idle native speed must be 0.");
                if (!Absent("keys_per_cycle") || !Absent("cycles_in_loop") || !Absent("cycle_origin_key")) throw new ArgumentException($"Clip {name} idle has no cycle.");
            }
            else
            {
                if (!double.IsFinite(speed) || speed < V2MinimumNativeSpeed || speed > V2MaximumNativeSpeed) throw new ArgumentException($"Clip {name} native speed out of the declared domain.");
                int cycles = Int(clip, "cycles_in_loop");
                double kpc = Prop(clip, "keys_per_cycle", JsonValueKind.Number).GetDouble();
                if (cycles < 1 || !double.IsFinite(kpc) || Math.Abs(kpc - (double)keys / cycles) > 1e-9 || kpc < V2MinimumKeysPerCycle || kpc > keys)
                    throw new ArgumentException($"Clip {name} cycle declaration inconsistent.");
                int origin = Int(clip, "cycle_origin_key");
                if (origin < 0 || origin >= keys) throw new ArgumentException($"Clip {name} cycle origin out of range.");
                result.NativeSpeed = speed; result.KeysPerCycle = kpc; result.CyclesInLoop = cycles; result.CycleOriginKey = origin;
            }
            clips[name] = result;
        }
        if (total > MaximumPackBytesV2) throw new ArgumentException("Pack v2 payload exceeds the declared memory bound.");
        return clips;
    }

    // H160 (2): "meshes" must list exactly the six pieces in builder order, contiguous vertex ranges from 0, exact sums.
    private static IReadOnlyList<FifaPackPiece> ValidatePieces(JsonElement header)
    {
        var meshes = Prop(header, "meshes", JsonValueKind.Array);
        if (meshes.GetArrayLength() != PieceOrder.Length) throw new ArgumentException("Pack must list exactly six mesh pieces.");
        var pieces = new List<FifaPackPiece>(); int vertex = 0, triangle = 0, k = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            var file = File(mesh);
            if (!file.StartsWith($"mesh-{k + 1}-{PieceOrder[k].Token}_", StringComparison.Ordinal)) throw new ArgumentException($"Mesh piece {k + 1} is not {PieceOrder[k].Token}.");
            int offset = Int(mesh, "vertex_offset"), count = Int(mesh, "vertex_count"), triangles = Int(mesh, "triangle_count");
            if (offset != vertex || count < 1 || triangles < 1) throw new ArgumentException("Mesh piece ranges must be contiguous and non-empty.");
            pieces.Add(new FifaPackPiece { Token = PieceOrder[k].Token, Role = PieceOrder[k].Role, VertexStart = offset, VertexCount = count, TriangleStart = triangle, TriangleCount = triangles });
            vertex += count; triangle += triangles; k++;
        }
        if (vertex != VertexCount || triangle != TriangleCount) throw new ArgumentException("Mesh pieces do not cover the pack mesh exactly.");
        return pieces;
    }

    // H160 (2): one submesh index array per piece (global vertex indices kept); every triangle must stay in its piece's vertex range.
    public static int[][] SplitPieces(int[] triangles, IReadOnlyList<FifaPackPiece> pieces)
    {
        if (triangles == null || pieces == null || triangles.Length != pieces.Sum(p => p.TriangleCount) * 3) throw new ArgumentException("Triangle total does not match the pieces.");
        var result = new int[pieces.Count][];
        for (int k = 0; k < pieces.Count; k++)
        {
            var p = pieces[k];
            var slice = new int[p.TriangleCount * 3];
            Array.Copy(triangles, p.TriangleStart * 3, slice, 0, slice.Length);
            foreach (var v in slice)
                if (v < p.VertexStart || v >= p.VertexStart + p.VertexCount) throw new ArgumentException($"Triangle of piece {p.Token} references a vertex outside its range.");
            result[k] = slice;
        }
        return result;
    }

    public static void RequirePayload(byte[] data, string sha256, long bytes)
    {
        if (data == null || data.LongLength != bytes) throw new InvalidOperationException("Pack payload size mismatch.");
        var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (actual != sha256) throw new InvalidOperationException("Pack payload sha256 mismatch.");
    }

    public static int[] DecodeTriangles(byte[] data, int vertexCount, bool flipWinding)
    {
        if (data == null || data.Length % 6 != 0) throw new ArgumentException("Triangle payload is not uint16 triples.");
        var result = new int[data.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int v = data[i * 2] | (data[i * 2 + 1] << 8);
            if (v >= vertexCount) throw new ArgumentException("Triangle index out of range.");
            result[i] = v;
        }
        if (flipWinding)
            for (int t = 0; t < result.Length; t += 3) (result[t + 1], result[t + 2]) = (result[t + 2], result[t + 1]);
        return result;
    }

    public static float[] DecodeFrames(byte[] data, int frames, int vertexCount)
    {
        if (data == null || frames < 1 || vertexCount < 1 || data.LongLength != (long)frames * vertexCount * 12) throw new ArgumentException("Frame payload shape mismatch.");
        var values = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, values, 0, data.Length); // little-endian host (x64) as the pack format.
        foreach (var v in values)
            if (!float.IsFinite(v) || Math.Abs(v) > MaximumAbsVertex) throw new ArgumentException("Nonfinite/unbounded pack vertex; not repaired.");
        return values;
    }

    public static Vector3 ApplyAxis(float[] m, Vector3 v) => new(
        m[0] * v.X + m[1] * v.Y + m[2] * v.Z, m[3] * v.X + m[4] * v.Y + m[5] * v.Z, m[6] * v.X + m[7] * v.Y + m[8] * v.Z);

    public static float[] ApplyAxis(float[] m, float[] xyz)
    {
        if (xyz == null || xyz.Length % 3 != 0) throw new ArgumentException("xyz triples expected.");
        var result = new float[xyz.Length];
        for (int i = 0; i < xyz.Length; i += 3)
        {
            var o = ApplyAxis(m, new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]));
            result[i] = o.X; result[i + 1] = o.Y; result[i + 2] = o.Z;
        }
        return result;
    }

    public static string SelectClip(double speed, string previous)
    {
        if (previous != "stand" && previous != "walk_F2" && previous != "jog_F2") throw new ArgumentException("Unknown previous clip.");
        if (!double.IsFinite(speed) || speed < 0 || speed > MaximumPlausibleSpeed) throw new ArgumentException("Implausible horizontal speed; overlay stops rather than guessing.");
        if (speed < StationaryBelowMetersPerSecond) return "stand";
        double jogThreshold = previous == "jog_F2" ? JogLeaveBelowMetersPerSecond : JogEnterAtMetersPerSecond;
        return speed < jogThreshold ? "walk_F2" : "jog_F2";
    }

    public static double PlaybackRate(string clip, double speed)
    {
        if (clip == "stand") return 0;
        double reference = clip == "walk_F2" ? WalkReferenceMetersPerSecond : clip == "jog_F2" ? JogReferenceMetersPerSecond : throw new ArgumentException("Unknown clip.");
        if (!double.IsFinite(speed) || speed < 0) throw new ArgumentException("Invalid speed.");
        return Math.Clamp(speed / reference, MinimumRate, MaximumRate);
    }

    public static double HorizontalSpeed(Vector3 from, Vector3 to, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("Speed window must be positive.");
        double dx = (double)to.X - from.X, dz = (double)to.Z - from.Z;
        return Math.Sqrt(dx * dx + dz * dz) / seconds;
    }

    public static double AdvancePhase(double phaseKeys, double dtSeconds, double rate, int keys)
    {
        if (keys < 1 || !double.IsFinite(phaseKeys) || phaseKeys < 0 || phaseKeys >= keys) throw new ArgumentException("Phase outside clip domain.");
        // H158: dtSeconds is MATCH time (FM CurrentTime); bounded by the match step, not the 1 s wall stall bound.
        if (!double.IsFinite(dtSeconds) || dtSeconds < 0 || dtSeconds > MaximumMatchStepSeconds) throw new ArgumentException("External clock step backwards or stalled.");
        if (!double.IsFinite(rate) || rate < 0 || rate > MaximumRate) throw new ArgumentException("Rate out of declared bounds.");
        double next = phaseKeys + dtSeconds * KeysPerSecond * rate;
        next %= keys;
        return next >= keys ? 0 : next;
    }

    public static double CarryPhase(double phaseKeys, int fromKeys, int toKeys)
    {
        if (fromKeys <= 0 || toKeys <= 0) return 0;
        double carried = phaseKeys / fromKeys * toKeys;
        return carried >= toKeys || carried < 0 || !double.IsFinite(carried) ? 0 : carried;
    }

    public static int FrameIndex(string clip, double phaseKeys, int keys)
    {
        if (clip == "stand") return StandFrame;
        if (clip != "walk_F2" && clip != "jog_F2") throw new ArgumentException("Unknown display clip.");
        if (keys < 1 || !double.IsFinite(phaseKeys) || phaseKeys < 0 || phaseKeys >= keys) throw new ArgumentException("Phase outside clip domain.");
        return 1 + (int)Math.Floor(phaseKeys);
    }

    public static double UniformScale(double fmHeadToFeet, double packProxy)
    {
        if (!double.IsFinite(fmHeadToFeet) || !double.IsFinite(packProxy) || packProxy <= 0) throw new ArgumentException("Scale inputs invalid.");
        double scale = fmHeadToFeet / packProxy;
        if (scale < MinimumScale || scale > MaximumScale) throw new ArgumentException("Visual fit scale outside declared bounds; refused, not clamped.");
        return scale;
    }

    public static double YawDegrees(Vector3 forward)
    {
        double h = Math.Sqrt((double)forward.X * forward.X + (double)forward.Z * forward.Z);
        if (!double.IsFinite(h) || h < 1e-3) throw new ArgumentException("Root forward has no horizontal heading.");
        return Math.Atan2(forward.X, forward.Z) * 180.0 / Math.PI;
    }

    public static bool WithinWindow(long now, long start, long frequency, int seconds)
    {
        if (frequency <= 0 || seconds < MinimumSeconds || seconds > MaximumSecondsCap) return false;
        decimal elapsed = (decimal)now - start;
        return elapsed >= 0 && elapsed < (decimal)frequency * seconds;
    }

    public static bool CanCertifyCleanup(int requestedFrame, int observedFrame, int alive, int unknown, bool allocationUncertain) =>
        observedFrame > requestedFrame && alive == 0 && unknown == 0 && !allocationUncertain;

    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public const string ViewChangedReason = "fm-view-changed";

    // H160 (4): literal CurrentPlaybackSpeed is report-only; a nonfinite value would make JsonSerializer throw and lose the
    // final report, so it is reported as null with an explicit flag (never repaired, never used in a computation).
    public static (float? Value, bool NonFinite) ReportablePlaybackSpeed(float value) => float.IsFinite(value) ? (value, false) : (null, true);

    // H160 (3): stop classification. A GUARD refusal (context/flags predicate, not a write/clock/teleport failure) followed by
    // an observed FM presentation change (every target renderer readable, enabled unchanged, all now inactive in hierarchy,
    // at least one active at start; root/scene/controller identity intact) is a normal end with a null Error. Anything else
    // keeps the H156 classification. Renderer tuple = (BeforeEnabled, BeforeActive, AfterEnabled, AfterActive), null = unreadable.
    public static (string Reason, string Error) ClassifyStop(bool timeout, bool guardRefusal, bool structureIntact,
        IReadOnlyList<(bool BeforeEnabled, bool BeforeActive, bool? AfterEnabled, bool? AfterActive)> renderers, string error)
    {
        if (timeout) return ("stop-time-budget", null);
        bool cut = guardRefusal && structureIntact && renderers != null && renderers.Count > 0 && renderers.Any(r => r.BeforeActive)
            && renderers.All(r => r.AfterEnabled.HasValue && r.AfterActive.HasValue && r.AfterEnabled.Value == r.BeforeEnabled && !r.AfterActive.Value);
        return cut ? (ViewChangedReason, null) : ("owned-update-failure", error);
    }

    // H160: enabled/activeInHierarchy always compared with the start capture; forceRenderingOff compared only when it is
    // observed (HideNativePlayer runs), against the ledger's expectation (true while hidden, recorded before value otherwise).
    // H168: typed per-frame flag guard (same verdict as OriginalPassPolicy.OriginalFlagsUnchanged over the serialised array:
    // every element's Status is exactly "unchanged"), without a per-actor per-frame JSON serialisation.
    public static bool FlagsUnchanged(IReadOnlyList<FifaRendererFlagReport> flags)
    {
        if (flags == null) throw new ArgumentException("Renderer flags unavailable.");
        for (int i = 0; i < flags.Count; i++) if (flags[i] == null || !string.Equals(flags[i].Status, "unchanged", StringComparison.Ordinal)) return false;
        return true;
    }

    public static string RendererFlagStatus(bool beforeEnabled, bool beforeActive, bool afterEnabled, bool afterActive, bool expectedForceOff, bool? afterForceOff) =>
        afterEnabled == beforeEnabled && afterActive == beforeActive && (afterForceOff == null || afterForceOff == expectedForceOff) ? "unchanged" : "changed";
}

// H160 (1): pure ledger of the opt-in native hide (Renderer.forceRenderingOff only, target renderers captured at start).
// Write intent is recorded BEFORE each native write so a throwing setter still obliges restoration; restoration is
// certified only by a later readback equal to the recorded before value. Pure CLR, executable in PolicyTests.
public sealed class FifaHiddenRenderer
{
    public int RendererInstanceId { get; init; }
    public bool BeforeForceRenderingOff { get; init; }
    public bool BeforeEnabled { get; init; }
    public bool BeforeActiveInHierarchy { get; init; }
    public bool HideWriteAttempted { get; internal set; }
    public bool? AfterHideForceRenderingOff { get; internal set; }
    public bool RestoreWriteAttempted { get; internal set; }
    public bool? RestoredForceRenderingOff { get; internal set; }
    // H168 (S2): FM destroyed this hidden renderer with the actor's departure confirmed (session intact): nothing left to restore.
    public bool DestroyedByFm { get; internal set; }
    public string Status => BeforeForceRenderingOff ? "untouched-already-off" : !HideWriteAttempted ? "not-hidden" : DestroyedByFm ? "destroyed-by-fm"
        : !RestoreWriteAttempted ? "hidden" : RestoredForceRenderingOff == BeforeForceRenderingOff ? "restored" : "restore-unverified";
}

public sealed class FifaNativeHideLedger
{
    public const int MaximumRenderers = 256;
    private readonly List<FifaHiddenRenderer> _entries = new();
    private bool _restoring;
    public IReadOnlyList<FifaHiddenRenderer> Entries => _entries;
    public bool Hidden => _entries.Any(e => e.HideWriteAttempted);
    public IEnumerable<FifaHiddenRenderer> RestorePlan => _entries.Where(e => e.HideWriteAttempted).ToArray();
    public bool RestorationVerified => _entries.Where(e => e.HideWriteAttempted).All(e => e.DestroyedByFm || e.RestoreWriteAttempted && e.RestoredForceRenderingOff == e.BeforeForceRenderingOff);

    private FifaHiddenRenderer Get(int id) => _entries.FirstOrDefault(e => e.RendererInstanceId == id) ?? throw new InvalidOperationException("Renderer not in the start capture.");

    public void Record(int id, bool beforeForceOff, bool beforeEnabled, bool beforeActive)
    {
        if (id == 0 || Hidden || _restoring || _entries.Count >= MaximumRenderers || _entries.Any(e => e.RendererInstanceId == id))
            throw new InvalidOperationException("Native hide ledger capture refused (zero/duplicate id, bound, or after a write).");
        _entries.Add(new FifaHiddenRenderer { RendererInstanceId = id, BeforeForceRenderingOff = beforeForceOff, BeforeEnabled = beforeEnabled, BeforeActiveInHierarchy = beforeActive });
    }

    public bool NeedsHide(int id) { var e = Get(id); return !_restoring && !e.BeforeForceRenderingOff && !e.HideWriteAttempted; }

    public void BeginHide(int id)
    {
        var e = Get(id);
        if (_restoring || e.BeforeForceRenderingOff || e.HideWriteAttempted) throw new InvalidOperationException("Native hide write refused.");
        e.HideWriteAttempted = true;
    }

    public void ConfirmHide(int id, bool readback)
    {
        var e = Get(id);
        if (!e.HideWriteAttempted) throw new InvalidOperationException("Hide readback without a hide write.");
        e.AfterHideForceRenderingOff = readback;
        if (!readback) throw new InvalidOperationException("forceRenderingOff readback after hide is not true.");
    }

    public bool ExpectedForceRenderingOff(int id) { var e = Get(id); return e.HideWriteAttempted && !e.RestoreWriteAttempted || e.BeforeForceRenderingOff; }

    public void BeginRestore(int id)
    {
        var e = Get(id);
        if (!e.HideWriteAttempted) throw new InvalidOperationException("Restore of a renderer that was never written.");
        _restoring = true; e.RestoreWriteAttempted = true;
    }

    // H168 (S2): the restore intent found the renderer destroyed. Only a CONFIRMED departure of the actor with the session
    // intact (actor-left) makes that "destroyed-by-fm" (nothing left to restore, not a failure); while the actor is still
    // present (session stop, suspend-limit end) it stays the 0.5.1 failure.
    public void RendererDestroyed(int id, bool actorDeparted)
    {
        var e = Get(id);
        if (!e.RestoreWriteAttempted || e.DestroyedByFm || e.RestoredForceRenderingOff.HasValue) throw new InvalidOperationException("Destroyed-renderer record refused (no restore intent, or already recorded).");
        if (!actorDeparted) throw new InvalidOperationException("Hidden native renderer died before restoration.");
        e.DestroyedByFm = true;
    }

    public void RecordRestoreReadback(int id, bool? value)
    {
        var e = Get(id);
        if (!e.RestoreWriteAttempted || e.DestroyedByFm) throw new InvalidOperationException("Restore readback without a restore write.");
        e.RestoredForceRenderingOff = value;
    }
}

// H158: horizontal root speed measured in MATCH seconds (FM published MatchFrame.CurrentTime), not wall time,
// so FM playback speed (1x/1.75x/2x...) does not inflate the measured player speed. Pure CLR, no Unity types.
public sealed class FifaMatchMotion
{
    private readonly Queue<(double Time, Vector3 Position)> _samples = new();
    public const int MaximumSamples = 256;
    public double Speed { get; private set; }
    public double WindowSeconds { get; private set; }
    public double MatchDt { get; private set; }
    public bool Paused { get; private set; }
    public double FrameDisplacement { get; private set; }
    // H171: horizontal FM displacement over the same match-time window as Speed (direction of travel for the locomotion selector).
    public double WindowDeltaX { get; private set; }
    public double WindowDeltaZ { get; private set; }
    private double _lastTime;
    private Vector3 _lastPosition;
    // H164: classification baseline of the next clock read and the horizontal jump from the last accepted root (read only).
    public double LastTime => _lastTime;
    public Vector3 LastPosition => _lastPosition;
    public double DisplacementTo(Vector3 position)
    {
        double dx = (double)position.X - _lastPosition.X, dz = (double)position.Z - _lastPosition.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public FifaMatchMotion(double startMatchTime, Vector3 startPosition)
    {
        if (!double.IsFinite(startMatchTime)) throw new InvalidOperationException("Match clock nonfinite.");
        _samples.Enqueue((startMatchTime, startPosition)); _lastTime = startMatchTime; _lastPosition = startPosition;
    }

    // H162: called ONLY when resuming after an FM view-change suspension: the replay/cut jump and a backwards clock become the
    // new baseline. Without an intervening suspension Observe keeps refusing backwards clocks and teleports.
    public void Rebaseline(double matchTime, Vector3 position)
    {
        if (!double.IsFinite(matchTime)) throw new InvalidOperationException("Match clock nonfinite.");
        _samples.Clear(); _samples.Enqueue((matchTime, position)); _lastTime = matchTime; _lastPosition = position;
        Speed = 0; WindowSeconds = 0; MatchDt = 0; FrameDisplacement = 0; Paused = false; WindowDeltaX = 0; WindowDeltaZ = 0;
    }

    public void Observe(double matchTime, Vector3 position)
    {
        double dt = matchTime - _lastTime;
        if (!double.IsFinite(matchTime) || !double.IsFinite(dt) || dt < 0 || dt > FifaOverlayPolicy.MaximumMatchStepSeconds)
            throw new InvalidOperationException("Match clock stepped backwards, jumped or is nonfinite.");
        MatchDt = dt; _lastTime = matchTime;
        double dx = (double)position.X - _lastPosition.X, dz = (double)position.Z - _lastPosition.Z;
        FrameDisplacement = Math.Sqrt(dx * dx + dz * dz); _lastPosition = position;
        // H158 per-frame jump test (declared): a horizontal root jump above the bound between two consecutive updates is a teleport/cut.
        // H234 (0.10.6): bound = max(3 m, 15 m/s x match dt). At FM fast-forward (up to 30x; live-015: 1.1636 s match per frame) an
        // ordinary jog moves > 3 m per update; for match steps <= 0.2 s the 3 m floor is unchanged; dt 0 keeps 3 m.
        if (!double.IsFinite(FrameDisplacement) || FrameDisplacement > Math.Max(FifaOverlayPolicy.MaximumFrameDisplacementMeters, FifaOverlayPolicy.MaximumPlausibleSpeed * dt))
            throw new InvalidOperationException("Root teleport: single-frame horizontal displacement exceeds the declared bound; overlay stops.");
        Paused = MatchDt == 0; // FM paused: match clock frozen => hold speed/pose, never a stop
        if (Paused) return;
        _samples.Enqueue((matchTime, position));
        while (_samples.Count > MaximumSamples) _samples.Dequeue();
        (double Time, Vector3 Position) reference = default; bool found = false;
        foreach (var sample in _samples)
        {
            double age = matchTime - sample.Time;
            if (age >= FifaOverlayPolicy.SpeedWindowSeconds) { reference = sample; found = true; } else break;
        }
        if (!found) return;
        while (_samples.Count > 0 && _samples.Peek().Time < reference.Time) _samples.Dequeue();
        WindowSeconds = matchTime - reference.Time;
        Speed = FifaOverlayPolicy.HorizontalSpeed(reference.Position, position, WindowSeconds);
        WindowDeltaX = (double)position.X - reference.Position.X; WindowDeltaZ = (double)position.Z - reference.Position.Z;
    }
}
