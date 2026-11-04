#nullable disable
namespace FM26.VisualDiagnostics;

// H173 (0.7.1), retaining H171 cycle blending: per-actor locomotion selection for pack v2 (H170 FIFA17 locomotion set). Pure CLR (no Unity types), executable
// in PolicyTests. Driven in FM MATCH time only. Every threshold is a DECLARED VISUAL HEURISTIC (reported), not a FIFA/FM contract.
public sealed class FifaLocomotion
{
    public const int Idle = 0, Walk = 1, Jog = 2, Run = 3, Sprint = 4;
    public static readonly string[] TierNames = { "idle", "walk", "jog", "run", "sprint" };
    // Historical H171 stateless helper bounds (not the H173 runtime mapping). Upper speed bound of tiers idle/walk/jog/run (m/s, match time); idle < 0.25, walk <= 2.3, jog <= 4.0, run <= 6.0, sprint above.
    public static readonly double[] TierUpperMetersPerSecond = { 0.25, 2.3, 4.0, 6.0 };
    public const double TierHysteresisMetersPerSecond = 0.3;
    public const double IdleAfterSeconds = 0.2;
    public const double MinimumRate = 0.6, MaximumRate = 1.5;
    public const int DirectionNone = 0, Forward = 1, StrafeRight = 2, StrafeLeft = 3, Backward = 4;
    public static readonly string[] DirectionNames = { "none", "forward", "strafe-right", "strafe-left", "backward" };
    public const double ForwardMaxDegrees = 45.0, BackwardMinDegrees = 135.0, DirectionHysteresisDegrees = 10.0;
    public const double DirectionalBelowMetersPerSecond = 4.0;
    public const double MinimumMoveMeters = 0.01;
    public const double TierFadeSeconds = 0.25, DirectionFadeSeconds = 0.3;

    private readonly FifaPackHeader _pack;
    private int _tier = Idle, _direction = DirectionNone;
    private double _below;
    public const double TierDwellSeconds = 0.45, DirectionDwellSeconds = 0.45;
    private int _candidateTier = -1, _candidateDirection = -1;
    private double _tierCandidateSeconds, _directionCandidateSeconds, _tierAge, _directionAge;
    private static int Dwell(int current, int desired, double dt, double minimum, double age, ref int candidate, ref double seconds)
    {
        if (desired == current) { candidate = -1; seconds = 0; return current; }
        if (candidate != desired) { candidate = desired; seconds = 0; }
        seconds += dt;
        return seconds >= minimum - 1e-12 && age >= minimum - 1e-12 ? desired : current;
    }
    public const double SpeedFilterSeconds = 0.3;
    private bool _speedReady;
    public double FilteredSpeed { get; private set; }
    public const double DirectionFilterSeconds = 0.4;
    private bool _directionReady;
    private double _headingX, _headingZ;
    public double? FilteredAngle { get; private set; }
    private double? FilterDirection(double? angle, double dt)
    {
        if (!angle.HasValue) return FilteredAngle;
        double r = angle.Value * Math.PI / 180, x = Math.Sin(r), z = Math.Cos(r);
        double a = 1 - Math.Exp(-dt / DirectionFilterSeconds);
        _headingX = _directionReady ? _headingX + a * (x - _headingX) : x;
        _headingZ = _directionReady ? _headingZ + a * (z - _headingZ) : z;
        _directionReady = true;
        // The exponential mean need not be unit length. Only its argument is used; exact cancellation holds the last angle.
        if (_headingX * _headingX + _headingZ * _headingZ > 1e-12) FilteredAngle = Math.Atan2(_headingX, _headingZ) * 180 / Math.PI;
        return FilteredAngle;
    }
    // Per clip (V2Clips order), copied once from the validated header: keys, keys per cycle, cycle origin, idle flag.
    private readonly string[] _names; private readonly FifaPackClip[] _clips; private readonly int[] _keys; private readonly double[] _kpc, _origin; private readonly bool[] _moving;
    private int _target, _from = -1, _pending = -1;
    private long _pendingApplications;
    private readonly long[] _dwellHistogram = new long[6];
    private double _processedSeconds, _sinceSwitch;
    private double _phase, _fromPhase, _weight = 1.0, _fadeSeconds;
    // Bounded stats (fixed arrays, no per-frame allocation).
    private readonly long[] _clipFrames, _tierFrames = new long[5], _directionFrames = new long[5];
    private long _updates, _tierFades, _directionFades, _rebaselineSnaps, _rebaselines, _blendFrames, _fallbackFrames, _rateLow, _rateHigh;
    private double _minRate = double.NaN, _maxRate = double.NaN;

    public FifaLocomotion(FifaPackHeader pack)
    {
        if (pack == null || pack.FormatVersion != 2 || pack.Clips == null) throw new ArgumentException("Locomotion needs a validated pack v2.");
        foreach (var (name, _, _) in FifaOverlayPolicy.V2Clips)
            if (!pack.Clips.ContainsKey(name)) throw new ArgumentException($"Pack v2 clip {name} missing.");
        _pack = pack;
        int n = FifaOverlayPolicy.V2Clips.Length;
        _names = new string[n]; _clips = new FifaPackClip[n]; _keys = new int[n]; _kpc = new double[n]; _origin = new double[n]; _moving = new bool[n]; _clipFrames = new long[n];
        for (int i = 0; i < n; i++)
        {
            var c = pack.Clips[FifaOverlayPolicy.V2Clips[i].Name];
            _names[i] = FifaOverlayPolicy.V2Clips[i].Name; _clips[i] = c; _keys[i] = c.Keys; _moving[i] = c.Tier != "idle";
            if (_moving[i] && (!c.KeysPerCycle.HasValue || !c.CycleOriginKey.HasValue)) throw new ArgumentException($"Clip {c.Name} has no declared cycle.");
            _kpc[i] = c.KeysPerCycle ?? 0; _origin[i] = c.CycleOriginKey ?? 0;
        }
        _target = Index("idle");
    }

    private int Index(string clip)
    {
        for (int i = 0; i < _names.Length; i++) if (string.Equals(_names[i], clip, StringComparison.Ordinal)) return i;
        throw new ArgumentException("Unknown pack v2 clip.");
    }

    public string TargetClip => _names[_target];
    public int TargetIndex => _target;
    public int SourceIndex => _from;
    public int PendingIndex => _pending;
    public int TargetFrame => 1 + (int)Math.Floor(_phase);
    public double TargetPhase => _phase;
    public string FromClip => _from < 0 ? null : _names[_from];
    public int FromFrame => _from < 0 ? -1 : 1 + (int)Math.Floor(_fromPhase);
    public double FromPhase => _fromPhase;
    // Weight of the TARGET clip (1 = no fade); vertex = from * (1 - Weight) + target * Weight.
    public double Weight => _weight;
    public bool Blending => _from >= 0;

    public int Tier => _tier;
    public int Direction => _direction;
    public bool Fallback { get; private set; }
    public double Rate { get; private set; } = 1.0;

    private static void RequireSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed < 0 || speed > FifaOverlayPolicy.MaximumPlausibleSpeed) throw new ArgumentException("Implausible horizontal speed; overlay stops rather than guessing.");
    }

    // Historical stateless helper retained for comparison; runtime uses NativeTier. Tier with hysteresis: up as soon as the speed passes the current tier's upper bound; down only once it is 0.3 m/s below the
    // lower tier's upper bound (several tiers at once). Never down into idle here: idle needs IdleAfterSeconds below 0.25 (Update).
    public static int SelectTier(int current, double speed)
    {
        if (current < Idle || current > Sprint) throw new ArgumentException("Unknown locomotion tier.");
        RequireSpeed(speed);
        int raw = Idle;
        while (raw < Sprint && !(speed < TierUpperMetersPerSecond[raw] || (raw > Idle && speed <= TierUpperMetersPerSecond[raw]))) raw++;
        if (current == Idle || raw > current) return raw;
        int tier = current;
        while (tier > Math.Max(raw, Walk) && speed < TierUpperMetersPerSecond[tier - 1] - TierHysteresisMetersPerSecond) tier--;
        return tier;
    }

    public const double NativeTierHysteresis = 0.35;
    private int NativeTier(double speed)
    {
        if (_tier == Idle && speed <= 0.5) return Idle;
        // Direction-specific walk/jog midpoint where both native clips exist; left-walk has no measured clip, so use forward.
        string walk = _direction == StrafeRight ? "strafe_right_walk" : _direction == Backward ? "back_walk" : "walk";
        string jog = _direction == StrafeRight ? "strafe_right_jog" : _direction == Backward ? "back_jog" : "jog";
        double low = (_pack.Clips[walk].NativeSpeed + _pack.Clips[jog].NativeSpeed) * .5;
        double mid = (_pack.Clips["jog"].NativeSpeed + _pack.Clips["run"].NativeSpeed) * .5;
        double high = (_pack.Clips["run"].NativeSpeed + _pack.Clips["sprint"].NativeSpeed) * .5;
        int tier = Math.Max(Walk, _tier);
        double Bound(int t) => t == Walk ? low : t == Jog ? mid : high;
        while (tier < Sprint && speed > Bound(tier) + NativeTierHysteresis) tier++;
        while (tier > Walk && speed < Bound(tier - 1) - NativeTierHysteresis) tier--;
        return tier;
    }

    // Signed angle (degrees, (-180, 180]) from FM facing to the FM horizontal move direction, + = towards the actor's right
    // (Unity left-handed: facing +Z, right = +X). null = no usable move vector or facing (the caller keeps its class).
    public static double? SignedAngle(double moveX, double moveZ, double faceX, double faceZ)
    {
        if (!double.IsFinite(moveX) || !double.IsFinite(moveZ) || !double.IsFinite(faceX) || !double.IsFinite(faceZ)) return null;
        if (Math.Sqrt(moveX * moveX + moveZ * moveZ) < MinimumMoveMeters || Math.Sqrt(faceX * faceX + faceZ * faceZ) < 1e-3) return null;
        double delta = (Math.Atan2(moveX, moveZ) - Math.Atan2(faceX, faceZ)) * 180.0 / Math.PI;
        while (delta <= -180.0) delta += 360.0;
        while (delta > 180.0) delta -= 360.0;
        return delta;
    }

    // Direction class with 10 deg hysteresis around the current class; strafe/backpedal only in walk/jog below 4 m/s.
    public static int SelectDirection(int current, double? angle, double speed, int tier)
    {
        if (current < DirectionNone || current > Backward) throw new ArgumentException("Unknown locomotion direction.");
        if (tier < Idle || tier > Sprint) throw new ArgumentException("Unknown locomotion tier.");
        RequireSpeed(speed);
        if (tier == Idle) return DirectionNone;
        if (tier >= Run || speed >= DirectionalBelowMetersPerSecond) return Forward;
        if (!angle.HasValue || !double.IsFinite(angle.Value)) return current == DirectionNone ? Forward : current;
        double forwardMax = current == Forward ? ForwardMaxDegrees + DirectionHysteresisDegrees
            : current == StrafeRight || current == StrafeLeft ? ForwardMaxDegrees - DirectionHysteresisDegrees : ForwardMaxDegrees;
        double backwardMin = current == Backward ? BackwardMinDegrees - DirectionHysteresisDegrees
            : current == StrafeRight || current == StrafeLeft ? BackwardMinDegrees + DirectionHysteresisDegrees : BackwardMinDegrees;
        double a = Math.Abs(angle.Value);
        if (a <= forwardMax) return Forward;
        if (a > backwardMin) return Backward;
        return angle.Value > 0 ? StrafeRight : StrafeLeft;
    }

    // (tier, direction) -> pack v2 clip; Fallback = a declared substitute for a combination the pack does not hold.
    public static (string Clip, bool Fallback) ClipFor(int tier, int direction)
    {
        switch (tier)
        {
            case Idle when direction == DirectionNone: return ("idle", false);
            case Walk when direction == Forward: return ("walk", false);
            case Walk when direction == StrafeRight: return ("strafe_right_walk", false);
            case Walk when direction == StrafeLeft: return ("strafe_left_jog", true); // declared fallback (no strafe-left walk in the set)
            case Walk when direction == Backward: return ("back_walk", false);
            case Jog when direction == Forward: return ("jog", false);
            case Jog when direction == StrafeRight: return ("strafe_right_jog", false);
            case Jog when direction == StrafeLeft: return ("strafe_left_jog", false);
            case Jog when direction == Backward: return ("back_jog", false);
            case Run when direction == Forward: return ("run", false);
            case Sprint when direction == Forward: return ("sprint", false);
            default: throw new ArgumentException("No locomotion clip for this tier/direction.");
        }
    }

    // rate = speed / declared native in-place speed, clamped [0.6, 1.5]; idle plays at 1.
    public static double ClipRate(FifaPackClip clip, double speed)
    {
        if (clip == null) throw new ArgumentException("Clip unavailable.");
        RequireSpeed(speed);
        if (clip.Tier == "idle") return 1.0;
        if (!(clip.NativeSpeed > 0)) throw new ArgumentException("Clip has no native speed.");
        return Math.Clamp(speed / clip.NativeSpeed, MinimumRate, MaximumRate);
    }

    // One processed (not paused) match-time step. moveX/moveZ = FM horizontal displacement over the speed window; faceX/faceZ =
    // FM root forward (both Unity axes).
    public void Update(double matchDt, double speed, double moveX, double moveZ, double faceX, double faceZ)
    {
        if (!double.IsFinite(matchDt) || matchDt < 0 || matchDt > FifaOverlayPolicy.MaximumMatchStepSeconds) throw new ArgumentException("Locomotion match step outside [0, 4] s.");
        RequireSpeed(speed);
        if (matchDt == 0) return; // Frozen match clock: no phase, filter, candidate/dwell or statistics advancement.
        FilteredSpeed = _speedReady ? FilteredSpeed + (speed - FilteredSpeed) * (1 - Math.Exp(-matchDt / SpeedFilterSeconds)) : speed;
        _speedReady = true;
        speed = FilteredSpeed;
        _tierAge += matchDt; _directionAge += matchDt; _processedSeconds += matchDt; _sinceSwitch += matchDt;
        int tier = NativeTier(speed);
        if (speed < TierUpperMetersPerSecond[Idle]) { _below += matchDt; if (_below >= IdleAfterSeconds - 1e-12 && _tierAge >= TierDwellSeconds - 1e-12) tier = Idle; }
        else _below = 0;
        tier = Dwell(_tier, tier, matchDt, tier == Idle ? 0 : TierDwellSeconds, _tierAge, ref _candidateTier, ref _tierCandidateSeconds);
        int direction = SelectDirection(_direction, FilterDirection(SignedAngle(moveX, moveZ, faceX, faceZ), matchDt), speed, tier);
        if (tier != Idle && tier < Run) direction = Dwell(_direction, direction, matchDt, DirectionDwellSeconds, _directionAge, ref _candidateDirection, ref _directionCandidateSeconds);
        else { _candidateDirection = -1; _directionCandidateSeconds = 0; }
        if (tier != Idle && direction == DirectionNone) direction = Forward;
        if (tier != _tier) _tierAge = 0;
        if (direction != _direction) _directionAge = 0;
        _tier = tier; _direction = direction;
        var (clip, fallback) = ClipFor(tier, direction);
        Fallback = fallback;
        int target = Index(clip);
        if (target == _target) _pending = -1;
        else if (_from >= 0) _pending = target; // One latest target, no source replacement while a fade is running.
        else
        {
            if (_pending == target) _pendingApplications++;
            _pending = -1;
            StartFade(target, _clips[target].Tier != _clips[_target].Tier);
        }
        Rate = ClipRate(_clips[_target], speed);
        // Advance: a moving target moves by du = dt*30*rate/kpc cycles; a moving source moves by the SAME du (phase-normalised);
        // idle (no cycle) plays at rate 1; a moving source fading into idle keeps its own clamped rate.
        double du = _moving[_target] ? matchDt * FifaOverlayPolicy.KeysPerSecond * Rate / _kpc[_target] : 0;
        _phase = Wrap(_moving[_target] ? _phase + du * _kpc[_target] : _phase + matchDt * FifaOverlayPolicy.KeysPerSecond, _keys[_target]);
        if (_from >= 0)
        {
            double step = _moving[_target] && _moving[_from] ? du * _kpc[_from]
                : matchDt * FifaOverlayPolicy.KeysPerSecond * (_moving[_from] ? ClipRate(_clips[_from], speed) : 1.0);
            _fromPhase = Wrap(_fromPhase + step, _keys[_from]);
            _weight += matchDt / _fadeSeconds;
            if (_weight >= 1.0 - 1e-9) { _from = -1; _weight = 1.0; }
        }
        Count(speed);
    }

    // New target, only after the previous fade completed: the current target becomes the source; the incoming
    // clip enters at the source's normalised gait-cycle phase (idle source => cycle origin; idle target => key 0).
    private void StartFade(int target, bool tierChanged)
    {
        if (_from >= 0) throw new InvalidOperationException("Cannot interrupt a locomotion fade.");
        int bin = _sinceSwitch < .25 ? 0 : _sinceSwitch < .5 ? 1 : _sinceSwitch < 1 ? 2 : _sinceSwitch < 2 ? 3 : _sinceSwitch < 5 ? 4 : 5;
        _dwellHistogram[bin]++; _sinceSwitch = 0;
        if (tierChanged) _tierFades++; else _directionFades++;
        _from = _target; _fromPhase = _phase; _weight = 0; _fadeSeconds = tierChanged ? TierFadeSeconds : DirectionFadeSeconds;
        double phase = 0;
        if (_moving[target])
        {
            double u = 0;
            if (_moving[_from]) { u = (_fromPhase - _origin[_from]) / _kpc[_from]; u -= Math.Floor(u); }
            phase = _origin[target] + u * _kpc[target];
        }
        _target = target; _phase = Wrap(phase, _keys[target]);
    }

    // Suspension resume / FM time discontinuity: the pose snaps to the current target (no fade across a cut).
    public void Snap()
    {
        _rebaselines++;
        _pending = -1; _speedReady = false; _directionReady = false;
        FilteredSpeed = 0; FilteredAngle = null; _headingX = 0; _headingZ = 0;
        _below = 0; _candidateTier = -1; _candidateDirection = -1;
        _tierCandidateSeconds = 0; _directionCandidateSeconds = 0; _tierAge = 0; _directionAge = 0;
        if (_from >= 0) { _rebaselineSnaps++; _from = -1; _weight = 1.0; }
    }

    private static double Wrap(double phase, int keys)
    {
        if (!double.IsFinite(phase) || keys < 1) throw new ArgumentException("Locomotion phase nonfinite.");
        phase %= keys;
        if (phase < 0) phase += keys;
        return phase >= keys ? 0 : phase;
    }

    private void Count(double speed)
    {
        _updates++; _clipFrames[_target]++; _tierFrames[_tier]++; _directionFrames[_direction]++;
        if (_from >= 0) _blendFrames++;
        if (Fallback) _fallbackFrames++;
        if (!_moving[_target]) return;
        double raw = speed / _clips[_target].NativeSpeed;
        if (raw < MinimumRate) _rateLow++; else if (raw > MaximumRate) _rateHigh++;
        if (double.IsNaN(_minRate) || Rate < _minRate) _minRate = Rate;
        if (double.IsNaN(_maxRate) || Rate > _maxRate) _maxRate = Rate;
    }

    // Report-time snapshot (allocates; never called per frame).
    public FifaLocomotionStats Stats()
    {
        var clips = new Dictionary<string, long>(); for (int i = 0; i < _names.Length; i++) clips[_names[i]] = _clipFrames[i];
        var tiers = new Dictionary<string, long>(); for (int i = 0; i < TierNames.Length; i++) tiers[TierNames[i]] = _tierFrames[i];
        var directions = new Dictionary<string, long>(); for (int i = 0; i < DirectionNames.Length; i++) directions[DirectionNames[i]] = _directionFrames[i];
        return new FifaLocomotionStats
        {
            PendingApplications = _pendingApplications, DwellHistogram = (long[])_dwellHistogram.Clone(), SwitchesPerMatchSecond = _processedSeconds > 0 ? (_tierFades + _directionFades) / _processedSeconds : 0,
            Updates = _updates, ClipFrames = clips, TierFrames = tiers, DirectionFrames = directions, TierFades = _tierFades, DirectionFades = _directionFades,
            MidFadeSnaps = 0, Rebaselines = _rebaselines, RebaselineSnaps = _rebaselineSnaps, BlendFrames = _blendFrames, FallbackFrames = _fallbackFrames,
            MinimumRate = double.IsNaN(_minRate) ? null : _minRate, MaximumRate = double.IsNaN(_maxRate) ? null : _maxRate, RateClampedLow = _rateLow, RateClampedHigh = _rateHigh,
            CurrentClip = TargetClip, CurrentTier = TierNames[_tier], CurrentDirection = DirectionNames[_direction],
        };
    }
}

public sealed class FifaLocomotionStats
{
    public long PendingApplications { get; init; }
    public long[] DwellHistogram { get; init; }
    public double SwitchesPerMatchSecond { get; init; }
    public long Updates { get; init; }
    public IReadOnlyDictionary<string, long> ClipFrames { get; init; }
    public IReadOnlyDictionary<string, long> TierFrames { get; init; }
    public IReadOnlyDictionary<string, long> DirectionFrames { get; init; }
    public long TierFades { get; init; }
    public long DirectionFades { get; init; }
    public long MidFadeSnaps { get; init; }
    public long Rebaselines { get; init; }
    public long RebaselineSnaps { get; init; }
    public long BlendFrames { get; init; }
    public long FallbackFrames { get; init; }
    public double? MinimumRate { get; init; }
    public double? MaximumRate { get; init; }
    public long RateClampedLow { get; init; }
    public long RateClampedHigh { get; init; }
    public string CurrentClip { get; init; }
    public string CurrentTier { get; init; }
    public string CurrentDirection { get; init; }
}
