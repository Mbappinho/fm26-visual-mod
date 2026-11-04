#nullable disable
using System.Text.Json;
using System.Text.Json.Nodes;
using FM26.VisualDiagnostics;

// H173: H171 instantaneous-state tests run retained H171Locomotion as the OLD replay control; H173Tests runs current source.
// Pack validation, static helper checks, blend, motion and allocation tests remain on current source.
// H171 (0.7.0): pack format v2 (H170 locomotion set) and the pure locomotion selector/blender. Executable, pure CLR.
internal static class FifaLocomotionTests
{
    private static int _assertions;
    private static void Check(bool okay, string label) { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(Action action, string label)
    {
        _assertions++;
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: accepted " + label);
    }

    public static void Run()
    {
        PackV2(); Tiers(); Directions(); Fades(); BlendAndMotion(); FrameCostBlend(); /*RUN*/
        Console.WriteLine($"PASS: {_assertions} FIFA locomotion v2 assertions (pure CLR).");
    }

    private static readonly (string Name, string Tier, string Direction, int Frames, int? Cycles, double Speed)[] Clips =
    {
        ("idle", "idle", "none", 169, null, 0.0), ("walk", "walk", "forward", 60, 2, 1.898), ("jog", "jog", "forward", 50, 2, 3.2),
        ("run", "run", "forward", 45, 2, 4.72), ("sprint", "sprint", "forward", 29, 2, 7.948), ("strafe_right_walk", "walk", "strafe-right", 57, 3, 1.353),
        ("strafe_right_jog", "jog", "strafe-right", 35, 3, 3.435), ("strafe_left_jog", "jog", "strafe-left", 25, 1, 3.624),
        ("back_walk", "walk", "backward", 44, 2, 1.106), ("back_jog", "jog", "backward", 40, 2, 3.129),
    };

    internal static FifaPackHeader PackHeader() => FifaOverlayPolicy.ValidateHeader(E(HeaderV2()));
    private static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;
    private const int Idle = FifaLocomotion.Idle, Walk = FifaLocomotion.Walk, Jog = FifaLocomotion.Jog, RunT = FifaLocomotion.Run, Sprint = FifaLocomotion.Sprint;

    // L2: speed tiers idle<0.25, walk<=2.3, jog<=4.0, run<=6.0, sprint above; going UP at the boundary, going DOWN only 0.3 m/s
    // below it (hysteresis); never down into idle (idle needs 0.2 s of match time below 0.25, applied by Update);
    // rate = speed / native speed clamped [0.7, 1.4], idle rate 1.
    private static void Tiers()
    {
        Check(FifaLocomotion.SelectTier(Idle, 0.24) == Idle && FifaLocomotion.SelectTier(Idle, 0.25) == Walk, "idle below 0.25, walk from 0.25");
        Check(FifaLocomotion.SelectTier(Walk, 2.3) == Walk && FifaLocomotion.SelectTier(Walk, 2.31) == Jog, "walk <= 2.3, jog above");
        Check(FifaLocomotion.SelectTier(Jog, 4.0) == Jog && FifaLocomotion.SelectTier(Jog, 4.01) == RunT, "jog <= 4.0, run above");
        Check(FifaLocomotion.SelectTier(RunT, 6.0) == RunT && FifaLocomotion.SelectTier(RunT, 6.01) == Sprint, "run <= 6.0, sprint above");
        Check(FifaLocomotion.SelectTier(Idle, 7.0) == Sprint && FifaLocomotion.SelectTier(Walk, 5.0) == RunT, "several tiers up at once");
        Check(FifaLocomotion.SelectTier(Jog, 2.01) == Jog && FifaLocomotion.SelectTier(Jog, 1.99) == Walk, "jog -> walk only below 2.3-0.3");
        Check(FifaLocomotion.SelectTier(RunT, 3.71) == RunT && FifaLocomotion.SelectTier(RunT, 3.69) == Jog, "run -> jog only below 4.0-0.3");
        Check(FifaLocomotion.SelectTier(Sprint, 5.71) == Sprint && FifaLocomotion.SelectTier(Sprint, 5.69) == RunT, "sprint -> run only below 6.0-0.3");
        Check(FifaLocomotion.SelectTier(Sprint, 3.0) == Jog && FifaLocomotion.SelectTier(Sprint, 1.0) == Walk, "several tiers down at once, each with hysteresis");
        Check(FifaLocomotion.SelectTier(Sprint, 0.0) == Walk && FifaLocomotion.SelectTier(Walk, 0.1) == Walk, "never down into idle without the idle delay");
        Reject(() => FifaLocomotion.SelectTier(5, 1.0), "unknown tier");
        Reject(() => FifaLocomotion.SelectTier(Walk, double.NaN), "nonfinite speed");
        Reject(() => FifaLocomotion.SelectTier(Walk, -0.1), "negative speed");
        Reject(() => FifaLocomotion.SelectTier(Walk, 15.01), "implausible speed");

        var pack = PackHeader();
        Check(FifaLocomotion.ClipRate(pack.Clips["idle"], 3.0) == 1.0, "idle rate 1");
        Check(Near(FifaLocomotion.ClipRate(pack.Clips["jog"], 3.52), 1.1), "rate = speed / native");
        Check(FifaLocomotion.ClipRate(pack.Clips["walk"], 0.3) == 0.6 && FifaLocomotion.ClipRate(pack.Clips["sprint"], 15.0) == 1.5, "current H173 rate clamped [0.6,1.5]");
        Reject(() => FifaLocomotion.ClipRate(pack.Clips["walk"], double.PositiveInfinity), "nonfinite speed rate");

        // idle delay through the stateful selector: 0.2 s of MATCH time below 0.25 m/s (frame steps of 0.05 s)
        var loco = new H171Locomotion(pack);
        Check(loco.Tier == Idle && loco.TargetClip == "idle" && loco.TargetFrame == 1 && loco.FromClip == null, "starts idle on idle key 0");
        loco.Update(0.05, 1.5, 0, 0.075, 0, 1);
        Check(loco.Tier == Walk && loco.TargetClip == "walk", "walk at 1.5 m/s");
        for (int i = 0; i < 3; i++) { loco.Update(0.05, 0.1, 0, 0.005, 0, 1); Check(loco.Tier == Walk, "still walk before 0.2 s below 0.25"); }
        loco.Update(0.05, 0.1, 0, 0.005, 0, 1);
        Check(loco.Tier == Idle && loco.TargetClip == "idle", "idle after 0.2 s below 0.25");
        loco.Update(0.05, 0.3, 0, 0.015, 0, 1);
        Check(loco.Tier == Walk, "idle -> walk immediately at 0.25+");
        loco.Update(0.05, 0.1, 0, 0.005, 0, 1); loco.Update(0.05, 0.1, 0, 0.005, 0, 1); loco.Update(0.05, 0.3, 0, 0.015, 0, 1);
        loco.Update(0.05, 0.1, 0, 0.005, 0, 1); loco.Update(0.05, 0.1, 0, 0.005, 0, 1);
        Check(loco.Tier == Walk, "the idle timer restarts whenever the speed comes back to 0.25+");
        Reject(() => loco.Update(0, 1.0, 0, 0.05, 0, 1), "zero match step (paused frames are not passed)");
        Reject(() => loco.Update(4.01, 1.0, 0, 0.05, 0, 1), "match step above the 4 s bound");
        Reject(() => loco.Update(0.05, double.NaN, 0, 0.05, 0, 1), "nonfinite speed update");
        Reject(() => new H171Locomotion(FifaOverlayPolicy.ValidateHeader(E(V1Header()))), "v1 pack has no locomotion set");
    }

    private const int None = FifaLocomotion.DirectionNone, Fwd = FifaLocomotion.Forward, Right = FifaLocomotion.StrafeRight, Left = FifaLocomotion.StrafeLeft, Back = FifaLocomotion.Backward;

    // L3: direction = signed angle (move direction over the match-time window vs FM facing, + = to the actor's right, Unity axes):
    // forward <= 45, strafe 45..135 by sign, backpedal > 135; 10 deg declared hysteresis around the current class; strafe/backpedal
    // only below 4 m/s and in walk/jog (run/sprint always forward, idle none); no usable move vector keeps the current class.
    // Missing (tier, direction) combos use declared fallbacks (walk + strafe-left -> strafe_left_jog at its clamped rate).
    private static void Directions()
    {
        Check(Near(FifaLocomotion.SignedAngle(1, 0, 0, 1).Value, 90) && Near(FifaLocomotion.SignedAngle(-1, 0, 0, 1).Value, -90), "+x is right when facing +z");
        Check(Near(FifaLocomotion.SignedAngle(0, -1, 0, 1).Value, 180) && Near(FifaLocomotion.SignedAngle(0, 1, 0, 1).Value, 0), "backwards 180, forward 0");
        Check(Near(FifaLocomotion.SignedAngle(0, -1, 1, 0).Value, 90), "facing +x: -z is right");
        Check(Near(FifaLocomotion.SignedAngle(-1, -1, 0, 1).Value, -135), "back-left diagonal");
        Check(FifaLocomotion.SignedAngle(0.001, 0.001, 0, 1) == null, "move below the declared minimum => no direction");
        Check(FifaLocomotion.SignedAngle(1, 0, 0, 0) == null, "facing without a horizontal heading => no direction");
        Check(FifaLocomotion.SignedAngle(double.NaN, 0, 0, 1) == null, "nonfinite move => no direction");

        int D(int current, double? angle, double speed = 1.5, int tier = Walk) => FifaLocomotion.SelectDirection(current, angle, speed, tier);
        Check(D(None, 44.9) == Fwd && D(None, 45.1) == Right && D(None, -45.1) == Left && D(None, 135.1) == Back && D(None, -134.9) == Left, "raw classes from none");
        Check(D(Fwd, 54) == Fwd && D(Fwd, 56) == Right && D(Fwd, -56) == Left && D(Fwd, 146) == Back, "leaving forward needs 45+10");
        Check(D(Right, 36) == Right && D(Right, 34) == Fwd && D(Right, 144) == Right && D(Right, 146) == Back && D(Right, -90) == Left, "strafe keeps 35..145");
        Check(D(Back, 126) == Back && D(Back, 124) == Right && D(Back, -124) == Left && D(Back, -179) == Back, "leaving backpedal needs 135-10");
        Check(D(Left, 90, 4.0) == Fwd && D(Back, 180, 3.99, Jog) == Back, "strafe/backpedal only below 4 m/s");
        Check(D(Right, 90, 3.8, RunT) == Fwd && D(Back, 180, 7, Sprint) == Fwd, "run/sprint always forward");
        Check(D(Fwd, 90, 0.1, Idle) == None, "idle has no direction");
        Check(D(Right, null) == Right && D(None, null) == Fwd && D(Back, null, 5, RunT) == Fwd, "no usable move vector keeps the class (forward from none)");
        Reject(() => D(7, 0), "unknown direction");

        Check(FifaLocomotion.ClipFor(Walk, Fwd) == ("walk", false) && FifaLocomotion.ClipFor(Walk, Right) == ("strafe_right_walk", false)
              && FifaLocomotion.ClipFor(Walk, Back) == ("back_walk", false), "walk clips");
        Check(FifaLocomotion.ClipFor(Walk, Left) == ("strafe_left_jog", true), "declared fallback: walk strafe-left -> strafe_left_jog");
        Check(FifaLocomotion.ClipFor(Jog, Fwd) == ("jog", false) && FifaLocomotion.ClipFor(Jog, Right) == ("strafe_right_jog", false)
              && FifaLocomotion.ClipFor(Jog, Left) == ("strafe_left_jog", false) && FifaLocomotion.ClipFor(Jog, Back) == ("back_jog", false), "jog clips");
        Check(FifaLocomotion.ClipFor(RunT, Fwd) == ("run", false) && FifaLocomotion.ClipFor(Sprint, Fwd) == ("sprint", false) && FifaLocomotion.ClipFor(Idle, None) == ("idle", false), "run/sprint/idle");
        Reject(() => FifaLocomotion.ClipFor(RunT, Right), "run has no strafe clip (selector never asks)");
        Reject(() => FifaLocomotion.ClipFor(Idle, Fwd), "idle has no direction");

        var pack = PackHeader();
        var loco = new H171Locomotion(pack);
        loco.Update(0.05, 1.5, 0.375, 0, 0, 1);
        Check(loco.TargetClip == "strafe_right_walk" && loco.Direction == Right && !loco.Fallback, "moving +x while facing +z at 1.5 => strafe-right walk");
        loco = new H171Locomotion(pack);
        loco.Update(0.05, 1.5, -0.375, 0, 0, 1);
        Check(loco.TargetClip == "strafe_left_jog" && loco.Direction == Left && loco.Fallback && Near(loco.Rate, 0.7), "strafe-left walk falls back to strafe_left_jog at the clamped rate");
        loco = new H171Locomotion(pack);
        loco.Update(0.05, 3.0, 0, -0.75, 0, 1);
        Check(loco.TargetClip == "back_jog" && loco.Direction == Back && Near(loco.Rate, 3.0 / pack.Clips["back_jog"].NativeSpeed), "backpedal jog");
        loco = new H171Locomotion(pack);
        loco.Update(0.05, 5.0, 1.25, 0, 0, 1);
        Check(loco.TargetClip == "run" && loco.Direction == Fwd, "sideways at 5 m/s => forward run (no strafe at run speed)");
    }

    private static double Cycle(FifaPackClip c, double phase) { double u = (phase - c.CycleOriginKey.Value) / c.KeysPerCycle.Value; return u - Math.Floor(u); }

    // L4: crossfades in match time: 0.25 s on a tier change, 0.3 s on a direction-only change; two clips at most; the incoming clip
    // starts at the SAME normalised gait-cycle phase (cycle origin = declared left-stance key) and both clips advance by the same
    // cycle fraction while fading (phase-normalised two-clip lerp); idle -> moving starts at the cycle origin; a new target during a
    // fade drops the older source (declared snap, counted); Snap() (suspension/discontinuity rebaseline) ends any fade at once.
    private static void Fades()
    {
        var pack = PackHeader(); var walk = pack.Clips["walk"]; var jog = pack.Clips["jog"];
        var loco = new H171Locomotion(pack);
        Check(loco.Weight == 1.0 && !loco.Blending && loco.FromFrame == -1, "no fade at start");
        loco.Update(0.05, 1.5, 0, 0.375, 0, 1);
        double rateWalk = 1.5 / walk.NativeSpeed;
        Check(loco.FromClip == "idle" && loco.TargetClip == "walk" && loco.Blending && Near(loco.Weight, 0.2), "idle -> walk fades over 0.25 s (tier)");
        Check(Near(loco.TargetPhase, walk.CycleOriginKey.Value + 0.05 * 30 * rateWalk) && loco.TargetFrame == 1 + (int)Math.Floor(loco.TargetPhase), "walk enters at its cycle origin, then advances at its rate");
        Check(Near(loco.FromPhase, 1.5) && loco.FromFrame == 2, "idle keeps playing at rate 1 while fading out");
        for (int i = 0; i < 3; i++) loco.Update(0.05, 1.5, 0, 0.375, 0, 1);
        Check(loco.Blending && Near(loco.Weight, 0.8), "weight grows with match time");
        loco.Update(0.05, 1.5, 0, 0.375, 0, 1);
        Check(!loco.Blending && loco.FromClip == null && loco.Weight == 1.0 && loco.FromFrame == -1, "fade ends after 0.25 s");
        for (int i = 0; i < 37; i++) loco.Update(0.05, 1.5, 0, 0.375, 0, 1);
        Check(loco.TargetPhase >= 0 && loco.TargetPhase < walk.Keys, "phase wraps inside the loop");

        double before = loco.TargetPhase, rateJog = 3.0 / jog.NativeSpeed;
        loco.Update(0.05, 3.0, 0, 0.75, 0, 1);
        double expectedJog = (jog.CycleOriginKey.Value + Cycle(walk, before) * jog.KeysPerCycle.Value + 0.05 * 30 * rateJog) % jog.Keys;
        Check(loco.FromClip == "walk" && loco.TargetClip == "jog" && Near(loco.TargetPhase, expectedJog, 1e-9), "walk -> jog enters at the same gait-cycle phase");
        Check(Near(Cycle(walk, loco.FromPhase), Cycle(jog, loco.TargetPhase), 1e-9), "both clips at the same normalised cycle phase");
        loco.Update(0.05, 3.0, 0, 0.75, 0, 1); loco.Update(0.05, 3.1, 0, 0.775, 0, 1);
        Check(loco.Blending && Near(Cycle(walk, loco.FromPhase), Cycle(jog, loco.TargetPhase), 1e-9), "phase sync kept while fading (same cycle fraction per step)");

        // mid-fade new target: walk->jog fading, then a direction change to strafe-right jog => the older walk source is dropped
        Check(Near(loco.Weight, 0.6), "walk->jog at 0.6");
        loco.Update(0.05, 3.0, 0.75, 0, 0, 1);
        Check(loco.FromClip == "jog" && loco.TargetClip == "strafe_right_jog" && Near(loco.Weight, 0.05 / 0.3), "direction change fades over 0.3 s from the previous target");
        var s = loco.Stats();
        Check(s.MidFadeSnaps == 1 && s.TierFades == 2 && s.DirectionFades == 1, "fade/snap counters");
        loco.Snap();
        Check(!loco.Blending && loco.FromClip == null && loco.TargetClip == "strafe_right_jog" && loco.Weight == 1.0, "rebaseline snap ends the fade, target kept");
        s = loco.Stats();
        Check(s.RebaselineSnaps == 1 && s.Rebaselines == 1, "rebaseline counters");
        loco.Snap();
        Check(loco.Stats().RebaselineSnaps == 1 && loco.Stats().Rebaselines == 2, "a snap without a fade only counts the rebaseline");

        // moving -> idle: idle enters at key 0 and the moving source keeps its own rate
        loco = new H171Locomotion(pack);
        for (int i = 0; i < 10; i++) loco.Update(0.05, 1.5, 0, 0.375, 0, 1);
        double walkPhase = loco.TargetPhase;
        for (int i = 0; i < 4; i++) loco.Update(0.05, 0.1, 0, 0.005, 0, 1);
        Check(loco.TargetClip == "idle" && loco.FromClip == "walk" && Near(loco.TargetPhase, 1.5), "idle enters at key 0 after the idle delay");
        Check(Near(loco.FromPhase, (walkPhase + 3 * 0.05 * 30 * 0.7 + 0.05 * 30 * 0.7) % walk.Keys), "walk source keeps its clamped rate while fading out");

        s = loco.Stats();
        Check(s.ClipFrames["walk"] == 13 && s.ClipFrames["idle"] == 1 && s.ClipFrames.Count == 10 && s.TierFrames["walk"] == 13 && s.DirectionFrames["forward"] == 13
              && s.DirectionFrames["none"] == 1 && s.BlendFrames == 5 && s.Updates == 14, "bounded per-clip/tier/direction frame counters");
        Check(Near(s.MinimumRate.Value, 0.7) && Near(s.MaximumRate.Value, 1.5 / walk.NativeSpeed) && s.RateClampedLow == 3 && s.RateClampedHigh == 0, "rate stats (moving clips only)");
    }

    // L5: two-clip vertex lerp into the reused per-actor buffer (no allocation, steady state measured), and the FM move vector of
    // the match-time speed window (FifaMatchMotion.WindowDeltaX/Z, reset by Rebaseline) that feeds the direction.
    private static void BlendAndMotion()
    {
        var a = new float[] { 0, 0, 0, 0, 0, 0, /*frame1*/ 1, 2, 3, 4, 5, 6 };
        var b = new float[] { 9, 9, 9, 9, 9, 9, /*frame1*/ 3, 6, 9, 0, 1, 2, /*frame2*/ 7, 7, 7, 7, 7, 7 };
        var dest = new System.Numerics.Vector3[2];
        FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 1, 0.25, 2, dest);
        Check(dest[0] == new System.Numerics.Vector3(1.5f, 3f, 4.5f) && dest[1] == new System.Numerics.Vector3(3f, 4f, 5f), "from*(1-w) + target*w");
        FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 2, 1.0, 2, dest);
        Check(dest[0] == new System.Numerics.Vector3(7, 7, 7) && dest[1] == new System.Numerics.Vector3(7, 7, 7), "weight 1 = target exactly");
        FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 2, 0.0, 2, dest);
        Check(dest[0] == new System.Numerics.Vector3(1, 2, 3) && dest[1] == new System.Numerics.Vector3(4, 5, 6), "weight 0 = source exactly");
        Reject(() => FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 1, double.NaN, 2, new System.Numerics.Vector3[2]), "nonfinite weight");
        Reject(() => FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 1, 1.01, 2, new System.Numerics.Vector3[2]), "weight above 1");
        Reject(() => FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 2, b, 1, 0.5, 2, new System.Numerics.Vector3[2]), "source frame out of range");
        Reject(() => FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 3, 0.5, 2, new System.Numerics.Vector3[2]), "target frame out of range");
        Reject(() => FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(a, 1, b, 1, 0.5, 2, new System.Numerics.Vector3[3]), "destination length");
        Reject(() => FifaVertexCopy.BlendFrames<double>(a, 1, b, 1, 0.5, 2, new double[2]), "element is not three floats");

        // steady-state allocation: selector + blend for 20 actors over 600 frames (JIT warmed first)
        var pack = PackHeader();
        var frames = new float[60 * 1868 * 3];
        var locos = Enumerable.Range(0, 20).Select(_ => new FifaLocomotion(pack)).ToArray();
        var buffer = new System.Numerics.Vector3[1868];
        void Frames(int count, int start)
        {
            for (int f = start; f < start + count; f++)
                foreach (var l in locos)
                {
                    double speed = 1.0 + 3.0 * (0.5 + 0.5 * Math.Sin(f * 0.05)); double angle = f * 0.02;
                    l.Update(1.0 / 60, speed, Math.Sin(angle) * speed * 0.25, Math.Cos(angle) * speed * 0.25, 0, 1);
                    int ft = Math.Min(l.TargetFrame, 59), ff = Math.Min(Math.Max(l.FromFrame, 1), 59);
                    if (l.Blending) FifaVertexCopy.BlendFrames<System.Numerics.Vector3>(frames, ff, frames, ft, l.Weight, 1868, buffer);
                    else FifaVertexCopy.CopyFrame<System.Numerics.Vector3>(frames, ft, 1868, buffer);
                }
        }
        Frames(120, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Frames(600, 120);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"no per-frame allocation in the locomotion/blend path ({allocated} bytes)");
        Check(locos.All(l => l.Stats().BlendFrames > 0 && l.Stats().TierFades > 0), "the allocation run really blended");

        var m = new FifaMatchMotion(0, new System.Numerics.Vector3(0, 0, 0));
        m.Observe(0.1, new System.Numerics.Vector3(0.1f, 0, 0.2f));
        Check(m.WindowDeltaX == 0 && m.WindowDeltaZ == 0, "no window yet => no move vector");
        m.Observe(0.3, new System.Numerics.Vector3(0.3f, 0, 0.6f));
        Check(Near(m.WindowDeltaX, 0.3, 1e-6) && Near(m.WindowDeltaZ, 0.6, 1e-6) && Near(m.Speed, Math.Sqrt(0.45) / 0.3, 1e-6), "move vector over the same window as the speed");
        m.Rebaseline(5, new System.Numerics.Vector3(9, 0, 9));
        Check(m.WindowDeltaX == 0 && m.WindowDeltaZ == 0, "rebaseline clears the move vector");
    }

    // L6: FrameCost reports the frames on which at least one actor blended two clips (and the blended actor-frames), same budget guard.
    private static void FrameCostBlend()
    {
        var cost = new FifaFrameCost();
        Check(!cost.Add(1.0, 3) && !cost.Add(2.0, 0) && !cost.Add(1.5), "blend-aware Add keeps the guard semantics; legacy Add unchanged");
        var s = cost.Stats();
        Check(s.Frames == 3 && s.BlendFrames == 1 && s.BlendActorFrames == 3 && s.MaxMilliseconds == 2.0, "blend frames counted");
        Reject(() => cost.Add(1.0, -1), "negative blended actor count");
        bool tripped = false;
        for (int i = 0; i < FifaFrameCost.ConsecutiveFrames; i++) tripped = cost.Add(9.0, 20);
        Check(tripped && cost.Stats().BlendFrames == 1 + FifaFrameCost.ConsecutiveFrames, "perf guard still trips after 60 consecutive frames over 8 ms");
    }

    private static JsonObject V1Header()
    {
        var h = HeaderV2();
        h["schema"] = "fm26.fifa-pack.v1";
        h["clips"] = new JsonObject { ["jog_F2"] = new JsonObject { ["file"] = "clip-jog_F2.f32le.bin", ["frames"] = 50, ["rest_frame"] = 0, ["first_key_frame"] = 1, ["bytes"] = 50L * 1868 * 12, ["sha256"] = new string('a', 64) },
            ["walk_F2"] = new JsonObject { ["file"] = "clip-walk_F2.f32le.bin", ["frames"] = 60, ["rest_frame"] = 0, ["first_key_frame"] = 1, ["bytes"] = 60L * 1868 * 12, ["sha256"] = new string('a', 64) } };
        return h;
    }

    internal static JsonObject HeaderV2()
    {
        var clips = new JsonObject();
        foreach (var c in Clips)
        {
            int keys = c.Frames - 1;
            clips[c.Name] = new JsonObject
            {
                ["file"] = $"clip-{c.Name}.f32le.bin", ["frames"] = c.Frames, ["rest_frame"] = 0, ["first_key_frame"] = 1, ["keys"] = keys,
                ["bytes"] = (long)c.Frames * 1868 * 12, ["sha256"] = new string('a', 64), ["loop"] = true, ["frame_range"] = new JsonArray(0, keys - 1),
                ["tier"] = c.Tier, ["direction"] = c.Direction, ["native_speed_mps"] = c.Speed,
                ["keys_per_cycle"] = c.Cycles.HasValue ? JsonValue.Create((double)keys / c.Cycles.Value) : null,
                ["cycles_in_loop"] = c.Cycles.HasValue ? JsonValue.Create(c.Cycles.Value) : null,
                ["cycle_origin_key"] = c.Cycles.HasValue ? JsonValue.Create(Math.Min(10, keys - 1)) : null,
            };
        }
        var meshes = new JsonArray();
        int offset = 0;
        foreach (var (file, v, t) in new[] { ("mesh-1-body_2_0_0_mesh.json", 732, 1140), ("mesh-2-jersey_2_0_0_0_0_mesh.json", 268, 417), ("mesh-3-shorts_2_0_0_mesh.json", 220, 338),
                     ("mesh-4-sock_2_0_0_mesh.json", 214, 308), ("mesh-5-shoes_2_0_mesh.json", 140, 196), ("mesh-6-headlod_2_0_mesh.json", 294, 512) })
        {
            meshes.Add(new JsonObject { ["file"] = file, ["vertex_offset"] = offset, ["vertex_count"] = v, ["triangle_count"] = t });
            offset += v;
        }
        return new JsonObject
        {
            ["schema"] = "fm26.fifa-pack.v2", ["vertex_count"] = 1868, ["triangle_count"] = 2911, ["keys_per_second"] = 30,
            ["axis_mapping"] = new JsonObject { ["matrix"] = new JsonArray(new JsonArray(-1, 0, 0), new JsonArray(0, 1, 0), new JsonArray(0, 0, 1)), ["determinant"] = -1, ["flip_winding"] = true },
            ["rest_height"] = new JsonObject { ["extent"] = 1.84, ["head_to_feet_anchor_proxy"] = 1.5266 },
            ["triangles"] = new JsonObject { ["file"] = "triangles.u16le.bin", ["count"] = 2911, ["bytes"] = 2911 * 6, ["sha256"] = new string('b', 64) },
            ["clips"] = clips, ["meshes"] = meshes,
        };
    }

    private static JsonElement E(JsonObject o) => JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    private static JsonObject Mutate(Action<JsonObject> m) { var h = HeaderV2(); m(h); return h; }
    private static JsonObject ClipOf(JsonObject h, string name) => (JsonObject)h["clips"]![name]!;

    // L1: pack v2 accepted with its declared timing/role metadata; every v2 constraint refused one at a time (same rules as
    // tools/fifa_pack.py verify, tests/test_fifa_pack_v2.py). v1 keeps its exact old contract (FormatVersion 1).
    private static void PackV2()
    {
        var pack = FifaOverlayPolicy.ValidateHeader(E(HeaderV2()));
        Check(pack.FormatVersion == 2 && pack.Clips.Count == 10, "v2 header accepted with the ten locomotion clips");
        var walk = pack.Clips["walk"];
        Check(walk.Loop && walk.Tier == "walk" && walk.Direction == "forward" && walk.NativeSpeed == 1.898 && walk.KeysPerCycle == 29.5
              && walk.CyclesInLoop == 2 && walk.CycleOriginKey == 10 && walk.Keys == 59, "walk metadata parsed");
        var idle = pack.Clips["idle"];
        Check(idle.Tier == "idle" && idle.NativeSpeed == 0 && idle.KeysPerCycle == null && idle.CycleOriginKey == null, "idle has no cycle");
        Check(pack.Clips["strafe_left_jog"].Direction == "strafe-left" && pack.Clips["back_jog"].Direction == "backward", "directions parsed");
        var cases = new Dictionary<string, Action<JsonObject>>
        {
            ["missing sprint"] = h => ((JsonObject)h["clips"]!).Remove("sprint"),
            ["extra clip"] = h => ((JsonObject)h["clips"]!)["turn_135"] = ClipOf(h, "walk").DeepClone(),
            ["loop false"] = h => ClipOf(h, "jog")["loop"] = false,
            ["tier swapped"] = h => ClipOf(h, "run")["tier"] = "jog",
            ["direction swapped"] = h => ClipOf(h, "strafe_right_jog")["direction"] = "strafe-left",
            ["idle speed"] = h => ClipOf(h, "idle")["native_speed_mps"] = 0.5,
            ["speed low"] = h => ClipOf(h, "walk")["native_speed_mps"] = 0.1,
            ["speed high"] = h => ClipOf(h, "sprint")["native_speed_mps"] = 13.0,
            ["kpc mismatch"] = h => ClipOf(h, "walk")["keys_per_cycle"] = 4.5,
            ["kpc missing"] = h => ClipOf(h, "walk")["keys_per_cycle"] = null,
            ["cycles zero"] = h => ClipOf(h, "walk")["cycles_in_loop"] = 0,
            ["cycles float"] = h => ClipOf(h, "walk")["cycles_in_loop"] = 2.5,
            ["kpc below 4"] = h => { ClipOf(h, "strafe_left_jog")["cycles_in_loop"] = 8; ClipOf(h, "strafe_left_jog")["keys_per_cycle"] = 3.0; },
            ["idle kpc"] = h => ClipOf(h, "idle")["keys_per_cycle"] = 2.0,
            ["origin out of range"] = h => ClipOf(h, "walk")["cycle_origin_key"] = 59,
            ["origin negative"] = h => ClipOf(h, "walk")["cycle_origin_key"] = -1,
            ["origin missing"] = h => ClipOf(h, "walk")["cycle_origin_key"] = null,
            ["keys mismatch"] = h => ClipOf(h, "walk")["keys"] = 58,
            ["range mismatch"] = h => ClipOf(h, "walk")["frame_range"] = new JsonArray(0, 59),
            ["frames bytes"] = h => ClipOf(h, "jog")["frames"] = 51,
            ["total bytes"] = h => { foreach (var n in new[] { "idle", "walk" }) { ClipOf(h, n)["frames"] = 4000; ClipOf(h, n)["bytes"] = 4000L * 1868 * 12; ClipOf(h, n)["keys"] = 3999; ClipOf(h, n)["frame_range"] = new JsonArray(0, 3998); } ClipOf(h, "walk")["keys_per_cycle"] = 3999 / 2.0; }, // H171 mutant fix: only the 96 MiB bound refuses this
            ["v1 clip names"] = h => h["clips"] = new JsonObject { ["jog_F2"] = ClipOf(h, "jog").DeepClone(), ["walk_F2"] = ClipOf(h, "walk").DeepClone() },
        };
        foreach (var (label, m) in cases) Reject(() => FifaOverlayPolicy.ValidateHeader(E(Mutate(m))), "v2 " + label);
        // total-bytes control: the mutation above is refused only by the 96 MiB bound (one 4000-frame clip alone is accepted)
        var one = Mutate(h => { ClipOf(h, "idle")["frames"] = 4000; ClipOf(h, "idle")["bytes"] = 4000L * 1868 * 12; ClipOf(h, "idle")["keys"] = 3999; ClipOf(h, "idle")["frame_range"] = new JsonArray(0, 3998); });
        Check(FifaOverlayPolicy.ValidateHeader(E(one)).Clips["idle"].Frames == 4000, "one large clip under the bound accepted");
        var walkOnly = Mutate(h => { var w = ClipOf(h, "walk"); w["frames"] = 3600; w["bytes"] = 3600L * 1868 * 12; w["keys"] = 3599; w["frame_range"] = new JsonArray(0, 3598); w["keys_per_cycle"] = 3599 / 2.0; });
        Check(FifaOverlayPolicy.ValidateHeader(E(walkOnly)).Clips["walk"].Frames == 3600, "a large walk alone is valid (control: the total-bytes case fails only on the bound)");

        // Default pack directory: v2 when its header exists, else v1 (old behaviour); explicit PackDirectory unchanged.
        var lad = @"C:\Profiles\x\AppData\Local";
        var v2 = Path.Combine(lad, "fm26-visual-mod", "packs", "fifa17-player-v2");
        var v1 = Path.Combine(lad, "fm26-visual-mod", "packs", "fifa17-player-v1");
        Check(FifaOverlayPolicy.ResolveDefaultPackDirectory(lad, p => p == Path.Combine(v2, "fifa-pack.json")) == v2, "v2 preferred when present");
        Check(FifaOverlayPolicy.ResolveDefaultPackDirectory(lad, p => false) == v1, "v1 when no v2 header");
        Check(FifaOverlayPolicy.ResolveDefaultPackDirectory(lad, p => p == Path.Combine(v1, "fifa-pack.json")) == v1, "v1 only");
        Reject(() => FifaOverlayPolicy.ResolveDefaultPackDirectory("relative", p => true), "relative LOCALAPPDATA");
        Check(FifaOverlayPolicy.DefaultPackDirectory(lad) == v1, "legacy DefaultPackDirectory unchanged (v1)");
    }
}
