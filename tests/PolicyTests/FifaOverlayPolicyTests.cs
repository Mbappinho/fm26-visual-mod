#nullable disable
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using FM26.VisualDiagnostics;

// H154 stage 1: pure FIFA overlay policies (no Unity types). Synthetic inputs only.
internal static class FifaOverlayPolicyTests
{
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(Action action, string label)
    {
        _assertions++;
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: accepted " + label);
    }
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static void Run()
    {
        Request(); Header(); Payloads(); Axis(); Clip(); Phase(); Scale(); Window();
        DuplicateKeys(); PackPaths(); MutantKillers();
        MatchTimeLive(); MatchTimePause(); MatchClockDomain(); FrameJump();
        HideNativeRequest(); HideLedger(); ViewChange(); Pieces(); PlaybackSpeedDomain();
        Console.WriteLine($"PASS: {_assertions} FIFA overlay stage-1 policy assertions (pure CLR, synthetic).");
    }

    private static string Start(string extra = "") =>
        "{\"Session\":\"S1\",\"RequestId\":\"fifa-1\",\"Action\":\"start-fifa-overlay\",\"ActorInstanceId\":-42,\"MaximumSeconds\":60" + extra + "}";

    private static void Request()
    {
        var r = FifaOverlayPolicy.ParseRequest(Json(Start()), "S1");
        Check(r.Action == "start-fifa-overlay" && r.ActorInstanceId == -42 && r.MaximumSeconds == 60 && r.PackDirectory == null, "valid start");
        var p = FifaOverlayPolicy.ParseRequest(Json(Start(",\"PackDirectory\":\"D:\\\\packs\\\\fifa17-player-v1\"")), "S1");
        Check(p.PackDirectory == "D:\\packs\\fifa17-player-v1", "absolute pack override");
        var s = FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"fifa-1\",\"Action\":\"stop-fifa-overlay\"}"), "S1");
        Check(s.Action == "stop-fifa-overlay" && s.ActorInstanceId == 0, "valid stop");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start()), "S2"), "other session");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace("fifa-1", "bad id!")), "S1"), "non-literal request id");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace("fifa-1", new string('a', 65))), "S1"), "long request id");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace("-42", "0")), "S1"), "zero actor");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace("-42", "\"-42\"")), "S1"), "string actor");
        foreach (var seconds in new[] { "0", "3601", "1.5", "-3", "null" }) // H162: declared range 1..3600 (was 1..120)
            Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace(":60", ":" + seconds)), "S1"), "seconds " + seconds);
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"Extra\":1")), "S1"), "unknown field");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"PackDirectory\":\"relative\\\\pack\"")), "S1"), "relative pack");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"PackDirectory\":\"D:\\\\a\\\\..\\\\b\"")), "S1"), "dotdot pack");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start().Replace("start-fifa-overlay", "start-candidate")), "S1"), "other action");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"x\",\"Action\":\"stop-fifa-overlay\",\"ActorInstanceId\":5}"), "S1"), "stop with actor");
        Check(FifaOverlayPolicy.DefaultPackDirectory("C:\\Profiles\\u\\AppData\\Local") == Path.Combine("C:\\Profiles\\u\\AppData\\Local", "fm26-visual-mod", "packs", "fifa17-player-v1"), "default pack path");
        Reject(() => FifaOverlayPolicy.DefaultPackDirectory(""), "missing LOCALAPPDATA");
    }

    // H160 (1): optional HideNativePlayer, strict JSON bool, default false; unknown/duplicate fields still refused.
    private static void HideNativeRequest()
    {
        Check(!FifaOverlayPolicy.ParseRequest(Json(Start()), "S1").HideNativePlayer, "HideNativePlayer defaults to false (native player stays visible)");
        Check(FifaOverlayPolicy.ParseRequest(Json(Start(",\"HideNativePlayer\":true")), "S1").HideNativePlayer, "HideNativePlayer true accepted");
        Check(!FifaOverlayPolicy.ParseRequest(Json(Start(",\"HideNativePlayer\":false")), "S1").HideNativePlayer, "HideNativePlayer false accepted");
        foreach (var bad in new[] { "\"true\"", "1", "0", "null", "[true]", "{}" })
            Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"HideNativePlayer\":" + bad)), "S1"), "non-bool HideNativePlayer " + bad);
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"HideNativePlayer\":true,\"HideNativePlayer\":true")), "S1"), "duplicate HideNativePlayer");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"HideNativePlayer\":true,\"Colours\":\"red\"")), "S1"), "unknown field still refused (no Colours field)");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"hideNativePlayer\":true")), "S1"), "field names are case-sensitive");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"x\",\"Action\":\"stop-fifa-overlay\",\"HideNativePlayer\":true}"), "S1"), "stop with HideNativePlayer");
    }

    // H160 (1): exact before-state, write-intent recorded BEFORE the native write, restore plan = every attempted write,
    // restoration certified only by a readback equal to the recorded before value.
    private static void HideLedger()
    {
        var l = new FifaNativeHideLedger();
        l.Record(-10, false, true, true); l.Record(-11, false, true, false); l.Record(-12, true, true, true);
        Reject(() => l.Record(-10, false, true, true), "duplicate renderer id");
        Reject(() => l.Record(0, false, true, true), "zero renderer id");
        Check(!l.Hidden && l.RestorationVerified, "nothing written: nothing to restore");
        Check(l.NeedsHide(-10) && l.NeedsHide(-11) && !l.NeedsHide(-12), "already forceRenderingOff renderers are left untouched");
        Check(!l.ExpectedForceRenderingOff(-10) && l.ExpectedForceRenderingOff(-12), "expected flag before hide = recorded before value");
        l.BeginHide(-10);
        Reject(() => l.Record(-13, false, true, true), "no new renderer after the first write");
        Reject(() => l.BeginHide(-12), "never write an already-off renderer");
        Reject(() => l.BeginHide(-99), "never write an unrecorded renderer");
        Check(l.Hidden && !l.RestorationVerified, "write intent alone (even if the setter throws) obliges restoration");
        Check(l.ExpectedForceRenderingOff(-10), "hidden renderer expected forceRenderingOff=true during the run");
        Reject(() => l.ConfirmHide(-10, false), "hide readback must be true");
        l.BeginHide(-11); l.ConfirmHide(-11, true);
        Check(l.RestorePlan.Select(e => e.RendererInstanceId).SequenceEqual(new[] { -10, -11 }), "restore plan = attempted writes only, in capture order");
        Check(l.RestorePlan.All(e => e.BeforeForceRenderingOff == false), "restore writes the recorded before value");
        foreach (var e in l.RestorePlan) l.BeginRestore(e.RendererInstanceId);
        Reject(() => l.BeginHide(-11), "no hide after restoration started");
        Check(!l.ExpectedForceRenderingOff(-10), "after restore: expected = before");
        l.RecordRestoreReadback(-10, false);
        Check(!l.RestorationVerified, "one restored readback missing => not verified");
        l.RecordRestoreReadback(-11, null);
        Check(!l.RestorationVerified, "dead/unreadable renderer is not certified restored");
        l.RecordRestoreReadback(-11, true);
        Check(!l.RestorationVerified, "readback still forced off => not restored");
        l.RecordRestoreReadback(-11, false);
        Check(l.RestorationVerified, "all attempted writes read back at their before value");
        var statuses = l.Entries.Select(e => e.Status).ToArray();
        Check(statuses.SequenceEqual(new[] { "restored", "restored", "untouched-already-off" }), "per-renderer status: " + string.Join(",", statuses));
        var fresh = new FifaNativeHideLedger(); fresh.Record(-1, false, true, true); fresh.BeginHide(-1);
        Reject(() => fresh.RecordRestoreReadback(-1, false), "readback before the restore write is not a restoration");
        var bound = new FifaNativeHideLedger();
        for (int i = 1; i <= FifaNativeHideLedger.MaximumRenderers; i++) bound.Record(-i, false, true, true);
        Reject(() => bound.Record(-100000, false, true, true), "bounded renderer ledger");
        // Renderer flag status: enabled/active always compared; forceRenderingOff compared only when observed (hide mode).
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, true, true, false, null) == "unchanged", "default mode: forceRenderingOff not read");
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, true, false, false, null) == "changed", "active change");
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, false, true, false, null) == "changed", "enabled change");
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, true, true, true, true) == "unchanged", "hidden as expected during the run");
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, true, true, true, false) == "changed", "someone else cleared our forceRenderingOff");
        Check(FifaOverlayPolicy.RendererFlagStatus(true, true, true, true, false, true) == "changed", "forceRenderingOff left set at the end");
    }

    // H160 (3): an FM presentation change (all previously active target renderers inactive in hierarchy, enabled unchanged,
    // same root/scene/controller) observed after a GUARD refusal is a normal end: StopReason fm-view-changed, Error null.
    private static void ViewChange()
    {
        static (bool, bool, bool?, bool?) R(bool be, bool ba, bool? ae, bool? aa) => (be, ba, ae, aa);
        var cut = new[] { R(true, true, true, false), R(true, true, true, false), R(true, false, true, false) };
        var v = FifaOverlayPolicy.ClassifyStop(false, true, true, cut, "FIFA overlay root/scene/actor context changed.");
        Check(v.Reason == "fm-view-changed" && v.Error == null, "presentation cut after a guard refusal is a normal end with null Error");
        var t = FifaOverlayPolicy.ClassifyStop(true, false, true, cut, "closed");
        Check(t.Reason == "stop-time-budget" && t.Error == null, "deadline keeps its own clean reason");
        void Failure((bool, bool, bool?, bool?)[] rs, bool guard, bool structure, string label)
        {
            var f = FifaOverlayPolicy.ClassifyStop(false, guard, structure, rs, "boom");
            Check(f.Reason == "owned-update-failure" && f.Error == "boom", label);
        }
        Failure(cut, false, true, "non-guard (write/clock/teleport) failure stays a failure even if renderers went inactive");
        Failure(cut, true, false, "root/scene/controller identity lost: failure, not a view change");
        Failure(new[] { R(true, true, true, false), R(true, true, true, true) }, true, true, "one target renderer still active: failure");
        Failure(new[] { R(true, true, false, false), R(true, true, true, false) }, true, true, "enabled changed: failure");
        Failure(new[] { R(true, true, null, null), R(true, true, true, false) }, true, true, "dead/unreadable renderer: failure");
        Failure(new[] { R(true, false, true, false) }, true, true, "no renderer was active before: not an observed cut");
        Failure(Array.Empty<(bool, bool, bool?, bool?)>(), true, true, "empty inventory: failure");
        Failure(new[] { R(true, true, true, true) }, true, true, "guard refusal with renderers unchanged: failure");
        // Real live-002 (24/10, 0.3.2): FM set ActiveInHierarchy=false on all 37 active target renderers, enabled unchanged.
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "fifa-overlay-live-002.flags.json")));
        var live = doc.RootElement.GetProperty("Flags").EnumerateArray().Select(f => R(f.GetProperty("BeforeEnabled").GetBoolean(), f.GetProperty("BeforeActiveInHierarchy").GetBoolean(),
            f.GetProperty("AfterEnabled").GetBoolean(), f.GetProperty("AfterActiveInHierarchy").GetBoolean())).ToArray();
        Check(live.Length == 39 && live.Count(x => x.Item2) == 37, "live-002 fixture: 39 renderers, 37 active at start");
        var lv = FifaOverlayPolicy.ClassifyStop(false, true, true, live, doc.RootElement.GetProperty("Error").GetString());
        Check(lv.Reason == "fm-view-changed" && lv.Error == null, "live-002 stop reclassified as fm-view-changed (was owned-update-failure)");
    }

    // H160 (2): per-piece ranges derived from the v1 header "meshes" (no pack rebuild), validated strictly; fixed palette.
    private static void Pieces()
    {
        var h = FifaOverlayPolicy.ValidateHeader(Json(HeaderText()));
        Check(h.Pieces.Select(x => x.Role).SequenceEqual(new[] { "skin", "shirt", "shorts", "socks", "shoes", "head" }), "six pieces in builder order with declared roles");
        Check(h.Pieces.Select(x => x.TriangleStart).SequenceEqual(new[] { 0, 1140, 1557, 1895, 2203, 2399 }), "triangle starts = cumulative counts");
        Check(h.Pieces.Select(x => x.VertexStart).SequenceEqual(new[] { 0, 732, 1000, 1220, 1434, 1574 }), "vertex starts");
        Check(h.Pieces.Sum(x => x.TriangleCount) == 2911 && h.Pieces.Sum(x => x.VertexCount) == 1868, "pieces cover the mesh exactly");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: ""))), "v1 header without meshes refused (piece colouring needs ranges)");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText(drop: 5)))), "five pieces");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 1 ? (e.File, e.Offset + 1, e.V, e.T) : e)))), "non-contiguous vertex offset");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 0 ? (e.File, e.Offset, e.V, e.T + 1) : e)))), "triangle counts do not sum to 2911");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 5 ? (e.File, e.Offset, e.V - 1, e.T) : e)))), "vertex counts do not sum to 1868");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 1 ? (RealPieces[2].File, e.Offset, e.V, e.T) : e)))), "piece order/name mismatch");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 0 ? ("..\\\\mesh-1-body_x.json", e.Offset, e.V, e.T) : e)))), "non-plain piece file name");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(meshes: MeshesText((i, e) => i == 0 ? (e.File, e.Offset, 0, 0) : e)))), "empty piece");
        // Triangle payload must keep every triangle of piece k inside piece k's vertex range (the real pack does: H160 check).
        var pieces = h.Pieces;
        var tri = new List<int>();
        foreach (var piece in pieces) for (int t = 0; t < piece.TriangleCount; t++) tri.AddRange(new[] { piece.VertexStart, piece.VertexStart + 1, piece.VertexStart + piece.VertexCount - 1 });
        var split = FifaOverlayPolicy.SplitPieces(tri.ToArray(), pieces);
        Check(split.Length == 6 && split.Select(s => s.Length / 3).SequenceEqual(pieces.Select(x => x.TriangleCount)), "submesh index arrays per piece");
        Check(split[1][0] == 732 && split[5][^1] == 1867, "global indices preserved (no rebasing)");
        var leak = tri.ToArray(); leak[1140 * 3] = 731; // first jersey triangle references a body vertex
        Reject(() => FifaOverlayPolicy.SplitPieces(leak, pieces), "triangle crossing pieces refused");
        Reject(() => FifaOverlayPolicy.SplitPieces(tri.Take(tri.Count - 3).ToArray(), pieces), "triangle total mismatch");
        // Fixed declared palette (no command field), opaque, finite, in [0,1], shoes darker than shirt.
        Check(FifaOverlayPolicy.Palette.Count == 6 && pieces.All(x => FifaOverlayPolicy.Palette.ContainsKey(x.Role)), "palette covers every role");
        Check(FifaOverlayPolicy.Palette.Values.All(c => c.Length == 4 && c.All(v => float.IsFinite(v) && v >= 0 && v <= 1) && c[3] == 1f), "palette RGBA in [0,1], opaque");
        Check(FifaOverlayPolicy.Palette["shoes"].Take(3).Sum() < 0.5f && FifaOverlayPolicy.Palette["skin"].SequenceEqual(FifaOverlayPolicy.Palette["head"]), "dark shoes; head uses skin tone");
        Check(FifaOverlayPolicy.Palette.Values.Select(c => string.Join(",", c)).Distinct().Count() == 5, "five distinct colours (skin shared by body and head)");
    }

    // H160 (4, H159 suggestion 1): a nonfinite literal CurrentPlaybackSpeed is reported as null + flag, never thrown at serialization.
    private static void PlaybackSpeedDomain()
    {
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var r = FifaOverlayPolicy.ReportablePlaybackSpeed(bad);
            Check(r.Value == null && r.NonFinite, "nonfinite playback speed -> null + flag: " + bad);
            var json = JsonSerializer.Serialize(new { PublishedPlaybackSpeed = r.Value, PublishedPlaybackSpeedNonFinite = r.NonFinite });
            Check(json == "{\"PublishedPlaybackSpeed\":null,\"PublishedPlaybackSpeedNonFinite\":true}", "row serializes without throwing: " + json);
        }
        var ok = FifaOverlayPolicy.ReportablePlaybackSpeed(1.75f);
        Check(ok.Value == 1.75f && !ok.NonFinite, "finite literal kept as is");
        Reject(() => JsonSerializer.Serialize(new { X = float.NaN }), "control: raw NaN float makes the serializer throw (the H159 finding)");
    }

    // H156 S3: duplicate keys refused like MultiPassContract/SemanticEventPolicy (no last-wins reinterpretation).
    private static void DuplicateKeys()
    {
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"RequestId\":\"fifa-1\",\"Action\":\"stop-fifa-overlay\",\"Action\":\"start-fifa-overlay\",\"ActorInstanceId\":-42,\"MaximumSeconds\":60}"), "S1"), "duplicate Action");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json(Start(",\"ActorInstanceId\":-7")), "S1"), "duplicate ActorInstanceId");
        Reject(() => FifaOverlayPolicy.ParseRequest(Json("{\"Session\":\"S1\",\"Session\":\"S1\",\"RequestId\":\"fifa-1\",\"Action\":\"stop-fifa-overlay\"}"), "S1"), "duplicate Session (same value)");
        Check(FifaOverlayPolicy.ParseRequest(Json(Start()), "S1").ActorInstanceId == -42, "unique keys still accepted");
    }

    // H156 S1: only local drive-letter absolute paths; UNC, device, \\?\, \\.\, relative, traversal, ADS refused.
    private static void PackPaths()
    {
        string Pack(string path) => Start(",\"PackDirectory\":" + JsonSerializer.Serialize(path));
        foreach (var bad in new[]
                 {
                     @"\\attacker\share\pack", @"\\localhost\C$\p", @"\\?\UNC\host\share\p", @"\\?\C:\packs\p", @"\\.\C:\packs\p", @"\\.\PhysicalDrive0",
                     "//host/share/p", @"\??\C:\p", @"C:packs\p", @"\packs\p", @"packs\p", @"C:\a\..\b", @"C:\a\.\b", @"C:\a\...\b", @"C:\p:ads", @"C:\packs\p ",
                     @"C:\packs\p.", @"C:\packs\NUL", @"C:\packs\com1\x", @"C:\packs\CON.txt", @"C:\a\\b",
                 })
            Reject(() => FifaOverlayPolicy.ParseRequest(Json(Pack(bad)), "S1"), "pack path " + bad);
        foreach (var good in new[] { @"D:\packs\fifa17-player-v1", @"C:\Profiles\u\AppData\Local\fm26-visual-mod\packs\fifa17-player-v1", @"E:\p q\x.y" })
            Check(FifaOverlayPolicy.ParseRequest(Json(Pack(good)), "S1").PackDirectory == good, "local drive path " + good);
    }

    // H156: tests for the 4 non-equivalent H155 surviving mutants (M06, M07, M12, M14).
    private static void MutantKillers()
    {
        // M06: rest_frame / first_key_frame layout is enforced (stand/rest frames are addressed by this layout).
        var text = HeaderText();
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(text.Replace("\"rest_frame\":0,\"first_key_frame\":1}", "\"rest_frame\":1,\"first_key_frame\":1}"))), "rest_frame != 0");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(text.Replace("\"rest_frame\":0,\"first_key_frame\":1}", "\"rest_frame\":0,\"first_key_frame\":2}"))), "first_key_frame != 1");
        // M07: |v| <= 100 bound, finite values above it refused (inclusive boundary accepted).
        float[] Decode(float x) { var raw = new byte[24]; Buffer.BlockCopy(new[] { 0f, 1f, 2f, x, 0f, 0f }, 0, raw, 0, 24); return FifaOverlayPolicy.DecodeFrames(raw, 1, 2); }
        Check(Decode(100f)[3] == 100f && Decode(-100f)[3] == -100f, "|v| == 100 accepted");
        Reject(() => Decode(100.01f), "finite vertex above 100");
        Reject(() => Decode(-1e30f), "finite vertex below -100");
        // M12: unknown retained handles never certify cleanup.
        Check(!FifaOverlayPolicy.CanCertifyCleanup(10, 11, 0, 1, false), "unknown handle prevents cleanup certification");
        // M14: non-diagonal, non-symmetric signed permutation (Z-up source) distinguishes M@v from M^T@v.
        float[] zUp = { 1, 0, 0, 0, 0, 1, 0, -1, 0 }; // Unity x=x, y=z, z=-y
        var o = FifaOverlayPolicy.ApplyAxis(zUp, new Vector3(1, 2, 3));
        Check(o == new Vector3(1, 3, -2), "row-major M@v (not transposed)");
        var bulk = FifaOverlayPolicy.ApplyAxis(zUp, new[] { 1f, 2f, 3f });
        Check(bulk.SequenceEqual(new[] { 1f, 3f, -2f }), "bulk row-major M@v");
        var h = FifaOverlayPolicy.ValidateHeader(Json(HeaderText(matrix: "[[1,0,0],[0,0,1],[0,-1,0]]", det: 1, flip: false)));
        Check(h.Matrix.SequenceEqual(zUp), "header rows parsed row-major for a non-diagonal matrix");
        // H156 verify alignment: exact clip set (plugin and tools/fifa_pack.py verify refuse extra clips alike).
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(text.Replace("\"clips\":{", "\"clips\":{\"walk_F3\":{},"))), "extra clip");
        Check(FifaOverlayPolicy.MaximumAbsVertex == 100.0, "shared vertex bound constant");
    }

    internal static string HeaderText(string matrix = "[[-1,0,0],[0,1,0],[0,0,1]]", int det = -1, bool flip = true,
        int vertices = 1868, int triangles = 2911, int jogFrames = 51, long? jogBytes = null, string file = "clip-jog_F2.f32le.bin",
        string sha = null, double proxy = 1.654, int keys = 30, string schema = "fm26.fifa-pack.v1", bool walk = true, string meshes = null)
    {
        meshes ??= MeshesText();
        sha ??= new string('a', 64);
        jogBytes ??= (long)jogFrames * 1868 * 12;
        string walkClip = walk ? ",\"walk_F2\":{\"file\":\"clip-walk_F2.f32le.bin\",\"frames\":525,\"bytes\":" + (525L * 1868 * 12) + ",\"sha256\":\"" + new string('b', 64) + "\",\"rest_frame\":0,\"first_key_frame\":1}" : "";
        return "{\"schema\":\"" + schema + "\",\"vertex_count\":" + vertices + ",\"triangle_count\":" + triangles + ",\"keys_per_second\":" + keys +
               ",\"axis_mapping\":{\"matrix\":" + matrix + ",\"determinant\":" + det + ",\"flip_winding\":" + (flip ? "true" : "false") + "}" +
               ",\"rest_height\":{\"extent\":1.84,\"head_to_feet_anchor_proxy\":" + proxy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" +
               ",\"triangles\":{\"file\":\"triangles.u16le.bin\",\"count\":" + triangles + ",\"bytes\":" + (triangles * 6) + ",\"sha256\":\"" + new string('c', 64) + "\"}" +
               ",\"clips\":{\"jog_F2\":{\"file\":\"" + file + "\",\"frames\":" + jogFrames + ",\"bytes\":" + jogBytes + ",\"sha256\":\"" + sha + "\",\"rest_frame\":0,\"first_key_frame\":1}" + walkClip + "}" + (meshes == "" ? "" : ",\"meshes\":" + meshes) + "}";
    }

    // H160 (2): the real pack's per-piece layout (header "meshes", builder concatenation order), synthetic names only.
    internal static readonly (string File, int V, int T)[] RealPieces =
    {
        ("mesh-1-body_2_0_0_mesh.json", 732, 1140), ("mesh-2-jersey_2_0_0_0_0_mesh.json", 268, 417), ("mesh-3-shorts_2_0_0_mesh.json", 220, 338),
        ("mesh-4-sock_2_0_0_mesh.json", 214, 308), ("mesh-5-shoes_2_0_mesh.json", 140, 196), ("mesh-6-headlod_2_0_mesh.json", 294, 512),
    };

    internal static string MeshesText(Func<int, (string File, int Offset, int V, int T), (string File, int Offset, int V, int T)> edit = null, int drop = -1)
    {
        var parts = new List<string>(); int offset = 0;
        for (int i = 0; i < RealPieces.Length; i++)
        {
            (string File, int Offset, int V, int T) e = (RealPieces[i].File, offset, RealPieces[i].V, RealPieces[i].T); offset += RealPieces[i].V;
            if (edit != null) e = edit(i, e);
            if (i == drop) continue;
            parts.Add("{\"file\":\"" + e.File + "\",\"vertex_offset\":" + e.Offset + ",\"vertex_count\":" + e.V + ",\"triangle_count\":" + e.T + "}");
        }
        return "[" + string.Join(",", parts) + "]";
    }

    private static void Header()
    {
        var h = FifaOverlayPolicy.ValidateHeader(Json(HeaderText()));
        Check(h.VertexCount == 1868 && h.TriangleCount == 2911 && h.FlipWinding && h.Clips.Count == 2, "valid header");
        Check(h.Clips["jog_F2"].Frames == 51 && h.Clips["jog_F2"].Keys == 50 && h.Clips["walk_F2"].Keys == 524, "clip key counts");
        Check(Math.Abs(h.RestProxyHeight - 1.654) < 1e-12, "rest proxy height");
        Check(h.Matrix.SequenceEqual(new float[] { -1, 0, 0, 0, 1, 0, 0, 0, 1 }), "row-major matrix");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(schema: "other"))), "schema");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(vertices: 1867))), "vertex count");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(triangles: 2910))), "triangle count");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(keys: 60))), "keys per second");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(jogBytes: 51L * 1868 * 12 + 4))), "clip byte shape");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(jogFrames: 1, jogBytes: 1868 * 12))), "rest only");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(file: "..\\\\evil.bin"))), "path traversal file");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(sha: "ABC"))), "bad sha");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(walk: false))), "walk missing");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(proxy: 0.1))), "absurd rest height");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(matrix: "[[-1,0,0],[0,1,0],[0,1,0]]"))), "non-permutation");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(matrix: "[[0.5,0,0],[0,1,0],[0,0,1]]"))), "scaled matrix");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(flip: false))), "flip inconsistent with det");
        Reject(() => FifaOverlayPolicy.ValidateHeader(Json(HeaderText(det: 1))), "declared det inconsistent");
        var ok = FifaOverlayPolicy.ValidateHeader(Json(HeaderText(matrix: "[[1,0,0],[0,1,0],[0,0,1]]", det: 1, flip: false)));
        Check(!ok.FlipWinding, "proper rotation keeps winding");
    }

    private static void Payloads()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        FifaOverlayPolicy.RequirePayload(bytes, Sha(bytes), 4);
        _assertions++;
        Reject(() => FifaOverlayPolicy.RequirePayload(bytes, Sha(bytes), 5), "size mismatch");
        Reject(() => FifaOverlayPolicy.RequirePayload(bytes, new string('0', 64), 4), "hash mismatch");
        var tri = new byte[] { 0, 0, 1, 0, 2, 0, 2, 0, 1, 0, 3, 0 };
        var plain = FifaOverlayPolicy.DecodeTriangles(tri, 4, false);
        Check(plain.SequenceEqual(new[] { 0, 1, 2, 2, 1, 3 }), "source winding");
        var flipped = FifaOverlayPolicy.DecodeTriangles(tri, 4, true);
        Check(flipped.SequenceEqual(new[] { 0, 2, 1, 2, 3, 1 }), "mirror flips each triangle winding");
        Reject(() => FifaOverlayPolicy.DecodeTriangles(tri, 3, false), "index out of range");
        Reject(() => FifaOverlayPolicy.DecodeTriangles(new byte[] { 0, 0, 1 }, 4, false), "odd size");
        var frame = new float[] { 1, 2, 3, 4, 5, 6 };
        var raw = new byte[frame.Length * 4];
        Buffer.BlockCopy(frame, 0, raw, 0, raw.Length);
        var decoded = FifaOverlayPolicy.DecodeFrames(raw, 1, 2);
        Check(decoded.SequenceEqual(frame), "float32 LE decode");
        BitConverter.GetBytes(float.NaN).CopyTo(raw, 4);
        Reject(() => FifaOverlayPolicy.DecodeFrames(raw, 1, 2), "nonfinite vertex not repaired");
        Reject(() => FifaOverlayPolicy.DecodeFrames(new byte[20], 1, 2), "frame shape");
    }

    private static void Axis()
    {
        float[] m = { -1, 0, 0, 0, 1, 0, 0, 0, 1 };
        // FIFA rest landmarks (from data shape): left foot +X, head +Y, toes +Z.
        var left = FifaOverlayPolicy.ApplyAxis(m, new Vector3(0.13f, 0.043f, 0.015f));
        var right = FifaOverlayPolicy.ApplyAxis(m, new Vector3(-0.13f, 0.043f, 0.015f));
        var head = FifaOverlayPolicy.ApplyAxis(m, new Vector3(0, 1.697f, 0.078f));
        var toe = FifaOverlayPolicy.ApplyAxis(m, new Vector3(0.143f, 0.031f, 0.182f));
        Check(left.X < 0 && right.X > 0, "Unity left foot at -X");
        Check(head.Y > left.Y + 1.5f, "head up +Y");
        Check(toe.Z > left.Z, "toes forward +Z");
        var all = FifaOverlayPolicy.ApplyAxis(m, new[] { 0.13f, 0.043f, 0.015f, 0, 1.697f, 0.078f });
        Check(all.Length == 6 && all[0] == -0.13f && all[4] == 1.697f, "bulk mapping");
    }

    private static void Clip()
    {
        // H156 evolution: declared standing pose + walk/jog hysteresis (enter jog >= 2.7, leave jog < 2.3 m/s).
        Check(FifaOverlayPolicy.SelectClip(0, "stand") == "stand" && FifaOverlayPolicy.SelectClip(0.29, "jog_F2") == "stand", "stationary stand");
        Check(FifaOverlayPolicy.SelectClip(0.3, "stand") == "walk_F2" && FifaOverlayPolicy.SelectClip(2.69, "walk_F2") == "walk_F2", "walk below jog entry");
        Check(FifaOverlayPolicy.SelectClip(2.7, "walk_F2") == "jog_F2" && FifaOverlayPolicy.SelectClip(2.7, "stand") == "jog_F2", "enter jog at 2.7");
        Check(FifaOverlayPolicy.SelectClip(2.5, "stand") == "walk_F2" && FifaOverlayPolicy.SelectClip(2.5, "walk_F2") == "walk_F2", "2.5 no longer switches from walk");
        Check(FifaOverlayPolicy.SelectClip(2.3, "jog_F2") == "jog_F2" && FifaOverlayPolicy.SelectClip(2.5, "jog_F2") == "jog_F2", "stay jog while >= 2.3");
        Check(FifaOverlayPolicy.SelectClip(2.29, "jog_F2") == "walk_F2", "leave jog below 2.3");
        Check(FifaOverlayPolicy.SelectClip(9, "walk_F2") == "jog_F2", "fast jog");
        Reject(() => FifaOverlayPolicy.SelectClip(1, "rest"), "unknown previous clip");
        Reject(() => FifaOverlayPolicy.SelectClip(double.NaN, "stand"), "nan speed");
        Reject(() => FifaOverlayPolicy.SelectClip(-0.1, "stand"), "negative speed");
        Reject(() => FifaOverlayPolicy.SelectClip(40, "jog_F2"), "implausible speed");
        Check(FifaOverlayPolicy.StandClip == "walk_F2" && FifaOverlayPolicy.StandFrame == 1 && FifaOverlayPolicy.RestFrame == 0, "stand = walk_F2 key 0 (pack frame 1); rest frame kept at 0");
        Check(FifaOverlayPolicy.PlaybackRate("stand", 0.1) == 0, "stand frozen");
        Check(Math.Abs(FifaOverlayPolicy.PlaybackRate("walk_F2", 1.4) - 1) < 1e-12, "walk reference rate");
        Check(Math.Abs(FifaOverlayPolicy.PlaybackRate("jog_F2", 7.0) - 2) < 1e-12, "jog ratio");
        Check(FifaOverlayPolicy.PlaybackRate("walk_F2", 0.3) == 0.25, "lower rate bound declared");
        Check(FifaOverlayPolicy.PlaybackRate("jog_F2", 20) == 3, "upper rate bound declared");
        Reject(() => FifaOverlayPolicy.PlaybackRate("walk_F3", 1), "unknown clip");
        var speed = FifaOverlayPolicy.HorizontalSpeed(new Vector3(0, 0, 0), new Vector3(3, 50, 4), 2);
        Check(Math.Abs(speed - 2.5) < 1e-6, "horizontal-only speed");
        Reject(() => FifaOverlayPolicy.HorizontalSpeed(Vector3.Zero, Vector3.One, 0), "zero dt");
    }

    // H158: real live timeline (session 20261023-211452, user-confirmed FM playback 1.75x), DERIVED fixture of the read-only report.
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "FM26.VisualDiagnostics.csproj"))) return d.FullName;
        throw new Exception("repo root not found");
    }

    // Per-frame reconstruction between the 10-frame rows: linear in QPC and XZ (declared derived test input, not native data).
    internal static List<(double Wall, Vector3 Position)> LiveFrames()
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "fifa-overlay-live-001.timeline.json")));
        double f = doc.RootElement.GetProperty("QpcFrequency").GetDouble();
        var rows = doc.RootElement.GetProperty("Rows").EnumerateArray().Select(r => (Frame: r.GetProperty("FrameCount").GetInt32(),
            Qpc: r.GetProperty("QpcTicks").GetInt64(), X: r.GetProperty("X").GetSingle(), Z: r.GetProperty("Z").GetSingle())).ToList();
        var frames = new List<(double, Vector3)>();
        long q0 = rows[0].Qpc;
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            var a = rows[i]; var b = rows[i + 1]; int n = b.Frame - a.Frame;
            for (int k = 0; k < n; k++)
            {
                double t = (double)k / n;
                frames.Add(((a.Qpc - q0 + t * (b.Qpc - a.Qpc)) / f, new Vector3((float)(a.X + t * (b.X - a.X)), 0, (float)(a.Z + t * (b.Z - a.Z)))));
            }
        }
        var last = rows[^1]; frames.Add(((last.Qpc - q0) / f, new Vector3(last.X, 0, last.Z)));
        return frames;
    }

    private static void MatchTimeLive()
    {
        var frames = LiveFrames();
        Check(frames.Count == 111, "live fixture reconstructs 111 frames (12 rows 9415..9525, 11 intervals x 10 + 1)");
        double Max(double playback)
        {
            var motion = new FifaMatchMotion(0, frames[0].Position);
            double max = 0; string clip = "stand";
            foreach (var (wall, p) in frames.Skip(1))
            {
                motion.Observe(wall * playback, p);
                clip = FifaOverlayPolicy.SelectClip(motion.Speed, clip);
                max = Math.Max(max, motion.Speed);
            }
            return max;
        }
        double wallMax = Max(1.0), matchMax = Max(1.75);
        Check(wallMax > 14.5 && wallMax < 15, "control: wall-time window reproduces the live 14.9 m/s climb");
        Check(Math.Abs(matchMax * 1.75 - wallMax) < 0.1 * wallMax, "match-time speed ~ wall speed / 1.75 (window is 0.25 MATCH s, so not bit-proportional)");
        Check(matchMax > 8 && matchMax < 9, "live sprint at 1.75x measured ~8.5 m/s in match time, accepted");
    }

    private static void MatchTimePause()
    {
        var motion = new FifaMatchMotion(10, Vector3.Zero);
        for (int i = 1; i <= 30; i++) motion.Observe(10 + i / 60.0, new Vector3(i * 0.1f, 0, 0)); // 6 m/s
        double before = motion.Speed;
        Check(!motion.Paused && Math.Abs(motion.MatchDt - 1 / 60.0) < 1e-9, "running: match dt per frame");
        for (int i = 0; i < 400; i++) motion.Observe(10.5, new Vector3(3f, 0, 0)); // FM paused: match clock frozen, root frozen
        Check(motion.Paused && motion.MatchDt == 0, "paused when match time does not advance (no stop)");
        Check(motion.Speed == before, "speed held while paused (pose frozen, no decay to stand)");
        for (int i = 1; i <= 30; i++) motion.Observe(10.5 + i / 60.0, new Vector3(3f + i * 0.05f, 0, 0)); // 3 m/s after resume
        Check(!motion.Paused && Math.Abs(motion.Speed - 3) < 0.05, "resume measures the new speed");
    }

    private static void MatchClockDomain()
    {
        FifaMatchMotion Fresh() { var m = new FifaMatchMotion(100, Vector3.Zero); m.Observe(100.1, new Vector3(0.5f, 0, 0)); return m; }
        Reject(() => Fresh().Observe(100.05, Vector3.Zero), "match clock backwards (replay/rewind) stops");
        Reject(() => Fresh().Observe(double.NaN, Vector3.Zero), "nonfinite match clock");
        Reject(() => Fresh().Observe(double.PositiveInfinity, Vector3.Zero), "infinite match clock");
        Reject(() => new FifaMatchMotion(double.NaN, Vector3.Zero), "nonfinite start match clock");
        Reject(() => Fresh().Observe(100.1 + 4.01, new Vector3(0.5f, 0, 0)), "match clock jump > 4 s in one frame (seek/skip) stops");
        var ok = Fresh(); ok.Observe(100.1 + 3.9, new Vector3(0.5f, 0, 0));
        Check(Math.Abs(ok.MatchDt - 3.9) < 1e-9, "large but bounded step (fast playback hitch) accepted");
    }

    private static void FrameJump()
    {
        FifaMatchMotion Running()
        {
            var m = new FifaMatchMotion(0, Vector3.Zero);
            for (int i = 1; i <= 30; i++) m.Observe(i * 1.75 / 60, new Vector3(0, 0, (float)(i * 1.75 / 60 * 8.5)));
            return m;
        }
        var t = 30 * 1.75 / 60; var z = (float)(t * 8.5);
        Reject(() => Running().Observe(t + 1.75 / 60, new Vector3(0, 0, z + 4f)), "teleport: 4 m in one frame stops");
        Reject(() => Running().Observe(t + 1.75 / 60, new Vector3(3.01f, 0, z)), "single-frame horizontal displacement > 3 m stops");
        Reject(() => Running().Observe(t, new Vector3(0, 0, z + 4f)), "teleport while paused (match clock frozen) still stops");
        var edge = Running(); edge.Observe(t + 1.75 / 60, new Vector3(0, 0, z + 3f));
        // H160 (H159 suggestion 5): assert the observed values instead of Check(true) no-ops.
        Check(Math.Abs(edge.FrameDisplacement - 3.0) < 1e-6 && !edge.Paused, "exactly 3 m in one frame accepted (strictly greater stops)");
        var vertical = Running(); vertical.Observe(t + 1.75 / 60, new Vector3(0, 10f, z + 0.2f));
        Check(Math.Abs(vertical.FrameDisplacement - 0.2) < 1e-5 && !vertical.Paused, "vertical root change is not a horizontal jump (only 0.2 m horizontal counted)");
        // Ordinary sprints at several FM playback speeds and frame rates: never a stop.
        foreach (var (playback, fps) in new[] { (1.0, 60.0), (1.75, 60.0), (2.0, 60.0), (2.0, 30.0), (4.0, 60.0), (4.0, 30.0) })
        {
            var m = new FifaMatchMotion(0, Vector3.Zero); string clip = "stand"; double max = 0;
            for (int i = 1; i <= (int)(3 * fps); i++)
            {
                double mt = i * playback / fps;
                m.Observe(mt, new Vector3((float)(mt * 8.5 * 0.6), 0, (float)(mt * 8.5 * 0.8)));
                clip = FifaOverlayPolicy.SelectClip(m.Speed, clip); max = Math.Max(max, m.Speed);
            }
            Check(clip == "jog_F2" && Math.Abs(max - 8.5) < 1e-3, $"8.5 m/s sprint at {playback}x / {fps} fps accepted, measured in match time");
        }
        // Live rows fed as coarse 10-frame steps (2.54 m max per step): still accepted.
        var rows = LiveFrames().Where((_, i) => i % 10 == 0).ToList();
        var coarse = new FifaMatchMotion(0, rows[0].Position);
        foreach (var (wall, p) in rows.Skip(1)) coarse.Observe(wall * 1.75, p);
        Check(coarse.Speed > 8 && coarse.Speed < 9, "live rows as coarse steps accepted");
        // Window rule kept: a sustained speed > 15 m/s measured in MATCH time still stops (via SelectClip).
        var fast = new FifaMatchMotion(0, Vector3.Zero);
        for (int i = 1; i <= 30; i++) fast.Observe(i / 60.0, new Vector3(0, 0, i * 16f / 60));
        Reject(() => FifaOverlayPolicy.SelectClip(fast.Speed, "jog_F2"), "16 m/s over the match-time window stops");
    }

    private static void Phase()
    {
        Check(Math.Abs(FifaOverlayPolicy.AdvancePhase(0, 1.0 / 30, 1, 50) - 1) < 1e-9, "one key per 1/30 s at rate 1");
        Check(Math.Abs(FifaOverlayPolicy.AdvancePhase(49.5, 1.0 / 30, 1, 50) - 0.5) < 1e-9, "loop wrap");
        Check(Math.Abs(FifaOverlayPolicy.AdvancePhase(10, 0.1, 2, 50) - 16) < 1e-9, "speed ratio scales advance");
        Check(FifaOverlayPolicy.AdvancePhase(10, 0.5, 0, 50) == 10, "rate 0 frozen");
        Reject(() => FifaOverlayPolicy.AdvancePhase(0, -0.01, 1, 50), "backwards clock");
        Reject(() => FifaOverlayPolicy.AdvancePhase(0, 4.5, 1, 50), "stall gap (H158: bound is the MATCH step 4 s; was 2 s wall)");
        Check(FifaOverlayPolicy.MaximumMatchStepSeconds == 4.0, "declared match step bound (1 s wall stall bound x 4x playback)");
        Check(Math.Abs(FifaOverlayPolicy.AdvancePhase(0, 1.5, 1, 50) - 45) < 1e-9, "H158: phase advances by MATCH dt (0.75 s wall hitch at 2x = 1.5 match s) without stopping");
        Reject(() => FifaOverlayPolicy.AdvancePhase(50, 0.01, 1, 50), "phase domain");
        Check(FifaOverlayPolicy.FrameIndex("stand", 33.9, 50) == FifaOverlayPolicy.StandFrame, "stand shows walk_F2 key 0 (pack frame 1), not the T-pose rest");
        Reject(() => FifaOverlayPolicy.FrameIndex("rest", 0, 50), "rest is no longer a display state");
        Check(FifaOverlayPolicy.FrameIndex("jog_F2", 0, 50) == 1 && FifaOverlayPolicy.FrameIndex("jog_F2", 49.99, 50) == 50, "nearest-lower key, no interpolation");
        Check(Math.Abs(FifaOverlayPolicy.CarryPhase(25, 50, 524) - 262) < 1e-9, "normalized phase carried across clips");
        Check(FifaOverlayPolicy.CarryPhase(25, 50, 0) == 0, "stand has no phase");
    }

    private static void Scale()
    {
        Check(Math.Abs(FifaOverlayPolicy.UniformScale(1.654, 1.654) - 1) < 1e-12, "same height scale 1");
        Check(Math.Abs(FifaOverlayPolicy.UniformScale(1.8194, 1.654) - 1.1) < 1e-9, "taller FM player");
        Reject(() => FifaOverlayPolicy.UniformScale(0.5, 1.654), "absurd small fit");
        Reject(() => FifaOverlayPolicy.UniformScale(4, 1.654), "absurd large fit");
        Reject(() => FifaOverlayPolicy.UniformScale(double.NaN, 1.654), "nan fit");
        Check(Math.Abs(FifaOverlayPolicy.YawDegrees(new Vector3(1, 0.3f, 0)) - 90) < 1e-4, "yaw +X = 90");
        Check(Math.Abs(FifaOverlayPolicy.YawDegrees(new Vector3(0, -0.2f, 1))) < 1e-4, "yaw +Z = 0");
        Reject(() => FifaOverlayPolicy.YawDegrees(new Vector3(0, 1, 0)), "vertical forward");
    }

    private static void Window()
    {
        Check(FifaOverlayPolicy.WithinWindow(100 + 59 * 10, 100, 10, 60), "inside window");
        Check(!FifaOverlayPolicy.WithinWindow(100 + 60 * 10, 100, 10, 60), "deadline exclusive");
        Check(!FifaOverlayPolicy.WithinWindow(99, 100, 10, 60), "backwards clock");
        Check(!FifaOverlayPolicy.WithinWindow(long.MaxValue, long.MinValue, 10, 60), "overflow-safe");
        // H162: whole-match bound 1..3600 s declared; deadline semantics unchanged.
        Check(FifaOverlayPolicy.MaximumSecondsCap == 3600, "declared cap 3600 s");
        Check(FifaOverlayPolicy.ParseRequest(Json(Start().Replace(":60", ":3600")), "S1").MaximumSeconds == 3600, "3600 s accepted");
        Check(FifaOverlayPolicy.ParseRequest(Json(Start().Replace(":60", ":121")), "S1").MaximumSeconds == 121, "121 s accepted (was refused)");
        Check(FifaOverlayPolicy.WithinWindow(100 + 3599L * 10, 100, 10, 3600) && !FifaOverlayPolicy.WithinWindow(100 + 3600L * 10, 100, 10, 3600), "3600 s window, deadline exclusive");
        Check(!FifaOverlayPolicy.WithinWindow(100, 100, 10, 3601), "out-of-range budget never inside a window");
        Check(FifaOverlayPolicy.CanCertifyCleanup(10, 11, 0, 0, false) && !FifaOverlayPolicy.CanCertifyCleanup(10, 10, 0, 0, false)
              && !FifaOverlayPolicy.CanCertifyCleanup(10, 11, 1, 0, false) && !FifaOverlayPolicy.CanCertifyCleanup(10, 11, 0, 0, true),
              "later-frame cleanup certification");
    }
}
