using System.Reflection;
using System.Numerics;

// Reflection keeps the existing unwired PolicyTests project compilable.
internal static class CandidatePosePolicyTests
{
    private static int _assertions;
    private static Type Policy => Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.CandidatePosePolicy")
        ?? throw new Exception("FAIL: candidate pose policy is missing.");
    private static object Call(string method, params object[] args)
        => (Policy.GetMethod(method) ?? throw new Exception($"FAIL: candidate policy {method} is missing.")).Invoke(null, args)!;
    private static void Equal(object expected, object actual, string label)
    {
        _assertions++;
        if (!Equals(expected, actual)) throw new Exception($"FAIL: {label}: expected {expected}, got {actual}.");
    }
    private static void Reject(string method, params object[] args)
    {
        _assertions++;
        try { Call(method, args); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { return; }
        throw new Exception($"FAIL: {method} accepted unsafe input.");
    }
    public static void Run()
    {
        _assertions = 0;
        Budget();
        ExplicitCadence();
        Roi();
        NameIndex();
        Motion();
        SegmentSanity();
        Console.WriteLine($"PASS: {_assertions} candidate pose policy assertions.");
    }
    private static void SegmentSanity()
    {
        var p = new[] {Vector3.Zero,new Vector3(0,0.5f,0),new Vector3(0,1,0)};
        var edges = new[] {0,1,1,2};
        var lengths = (float[])Call("CheckLengths", p, edges, null!);
        Equal(0.5f, lengths[0], "first actual raw segment length");
        Equal(0.5f, lengths[1], "second actual raw segment length");
        var okay = (float[])Call("CheckLengths", new[] {Vector3.Zero,new Vector3(0,0.6f,0),new Vector3(0,1.1f,0)}, edges, lengths);
        Equal(0.6f, okay[0], "sanity never normalizes length");
        Reject("CheckLengths", new[] {Vector3.Zero,Vector3.Zero}, new[] {0,1}, null!);
        Reject("CheckLengths", new[] {Vector3.Zero,new Vector3(0,2,0)}, new[] {0,1}, null!);
        Reject("CheckLengths", p, new[] {0,3}, null!);
        Reject("CheckLengths", p, new[] {0}, null!);
        Reject("CheckLengths", new[] {Vector3.Zero,new Vector3(0,0.8f,0),new Vector3(0,1.3f,0)}, edges, lengths);
        Reject("CheckLengths", p, edges, new[] {float.NaN,0.5f});
        Reject("CheckLengths", p, edges, new[] {0.5f});
    }
    private static void Motion()
    {
        var raw = new float[32];
        raw[4] = 0.1f; raw[5] = 1; raw[6] = 0.2f;
        raw[12] = 0.5f; raw[13] = 1.5f; raw[14] = 0.3f;
        var p = (Vector3[])Call("Positions", raw, 4, new[] {1,3});
        Equal(new Vector3(0.1f,1,0.2f), p[0], "T block exact XYZ");
        Equal(new Vector3(0.5f,1.5f,0.3f), p[1], "literal nonconsecutive index");
        raw[4] = 2;
        Equal(0.1f, p[0].X, "positions independently owned");
        Reject("Positions", raw, 4, new[] {4});
        Reject("Positions", raw, 4, new[] {1,1});
        Reject("Positions", new float[31], 4, new[] {1});
        raw[4] = float.NaN;
        Reject("Positions", raw, 4, new[] {1});
        raw[4] = 100;
        Reject("Positions", raw, 4, new[] {1});
        var before = new[] {new Vector3(0,1,0),new Vector3(0.5f,1.5f,0)};
        var translated = before.Select(x => x + new Vector3(2,1,-1)).ToArray();
        var a = (Vector3[])Call("RelativeTo", before, 0);
        var b = (Vector3[])Call("RelativeTo", translated, 0);
        Equal(Vector3.Zero, a[0], "copied hips is origin");
        Equal(a[1], b[1], "common-space root translation excluded");
        var still = Call("MeasureMotion", a, b, true);
        Equal(0d, still.GetType().GetProperty("MaximumDelta")!.GetValue(still)!, "no articulation from translation");
        b[1].X += 0.25f;
        var moving = Call("MeasureMotion", a, b, true);
        Equal(0.25d, moving.GetType().GetProperty("MaximumDelta")!.GetValue(moving)!, "real copied relative joint delta");
        Equal(1, moving.GetType().GetProperty("MovingJointCount")!.GetValue(moving)!, "moving joints");
        var rms = (double)moving.GetType().GetProperty("RmsDelta")!.GetValue(moving)!;
        Equal(true, Math.Abs(rms - Math.Sqrt(0.0625 / 2)) < 1e-10, "RMS uses all named joints");
        var duplicate = Call("MeasureMotion", a, b, false);
        Equal(false, duplicate.GetType().GetProperty("DistinctPayload")!.GetValue(duplicate)!, "duplicate payload excluded");
        Equal(0d, duplicate.GetType().GetProperty("MaximumDelta")!.GetValue(duplicate)!, "duplicate never proves motion");
        Reject("RelativeTo", before, 2);
        Reject("RelativeTo", new[] {new Vector3(float.PositiveInfinity,0,0)}, 0);
        Reject("MeasureMotion", a, new Vector3[1], true);
    }
    private static void NameIndex()
    {
        var names = new string[] {"Root", "Hips", null!, "Hand"};
        var refs = new[] {-12, -20, 0, -50};
        var required = new[] {"Hand", "Hips"};
        var indices = (int[])Call("NameIndices", names, refs, required, 7);
        Equal("3,1", string.Join(",", indices), "literal mesh indices, not compacted or sorted");
        Equal(true, Call("SameBoneOrder", names, refs, (string[])names.Clone(), (int[])refs.Clone()), "signed identities stable");
        Equal(false, Call("SameBoneOrder", names, refs, new string[] {"Root","Hand",null!,"Hips"}, new[] {-12,-50,0,-20}), "reordered mesh");
        Equal(false, Call("SameBoneOrder", names, refs, names, new[] {-12,-21,0,-50}), "changed reference");
        Equal(false, Call("SameBoneOrder", names, refs, new string[] {"Root","hips",null!,"Hand"}, refs), "case changed");
        Equal(false, Call("SameBoneOrder", names, refs, new string[] {"Root","Hips","Hand"}, new[] {-12,-20,-50}), "hole compacted");
        Reject("NameIndices", names, refs, new[] {"hips"}, 7);
        Reject("NameIndices", names, refs, required, 3);
        Reject("NameIndices", new[] {"Hips", "Hips"}, new[] {-20,-21}, new[] {"Hips"}, 7);
        Reject("NameIndices", new[] {"Hips", "Hand"}, new[] {-20,-20}, required, 7);
        Reject("NameIndices", new[] {"Hips", "Hand"}, new[] {-20,0}, required, 7);
        Reject("NameIndices", names, refs, new[] {"Hips","Hips"}, 7);
        Reject("NameIndices", names, new[] {-12,-20,0}, required, 7);
        Reject("NameIndices", new string[256], new int[256], required, 255);
    }
    private static void Roi()
    {
        var roi = Call("Roi", 43, 126, 126, 86, 12, true, true, true);
        foreach (var pair in new (string Property, int Value)[]
        { ("X", 0), ("Width", 126), ("Y", 24), ("Height", 2), ("Z", 0), ("Depth", 1), ("FloatCount", 1008), ("ByteCount", 4032) })
            Equal(pair.Value, roi.GetType().GetProperty(pair.Property)!.GetValue(roi)!, pair.Property);
        Equal(0, Call("Roi", 2, 7, 7, 4, 0, true, true, true).GetType().GetProperty("Y")!.GetValue(Call("Roi", 2, 7, 7, 4, 0, true, true, true))!, "first slot");
        var last = Call("Roi", 2, 7, 7, 4, 1, true, true, true);
        Equal(2, last.GetType().GetProperty("Y")!.GetValue(last)!, "last slot");
        Equal(56, last.GetType().GetProperty("FloatCount")!.GetValue(last)!, "nonconstant bone stride");
        foreach (var args in new object[][]
        {
            new object[] {0,126,126,86,12,true,true,true}, new object[] {43,0,0,86,12,true,true,true},
            new object[] {43,256,256,86,12,true,true,true}, new object[] {43,126,125,86,12,true,true,true},
            new object[] {43,126,126,85,12,true,true,true}, new object[] {43,126,126,86,-1,true,true,true},
            new object[] {43,126,126,86,43,true,true,true}, new object[] {43,126,126,86,12,false,true,true},
            new object[] {43,126,126,86,12,true,false,true}, new object[] {43,126,126,86,12,true,true,false},
            new object[] {int.MaxValue,126,126,int.MaxValue,12,true,true,true}
        }) Reject("Roi", args);
        Equal(true, Call("CanCopy", true, false, 2, 1, 4032, 1008, 1008), "exact completion");
        foreach (var args in new object[][]
        {
            new object[] {false,false,2,1,4032,1008,1008}, new object[] {true,true,2,1,4032,1008,1008},
            new object[] {true,false,120,1,4032,1008,1008}, new object[] {true,false,-1,1,4032,1008,1008},
            new object[] {true,false,2,2,4032,1008,1008}, new object[] {true,false,2,1,4031,1008,1008},
            new object[] {true,false,2,1,4032,1007,1008}, new object[] {true,false,2,1,0,0,0}
        }) Equal(false, Call("CanCopy", args), "reject completion");
        var source = new float[16]; source[0] = 7; source[8] = -0f; source[9] = float.NaN;
        var copy = (float[])Call("CopyRoi", 2, source.Length, (Func<int,float>)(i => source[i]));
        source[0] = 99;
        Equal(7f, copy[0], "CLR ownership");
        Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(copy[8]), "zero quaternion preserved");
        Equal(true, float.IsNaN(copy[9]), "raw floats never repaired");
        Reject("CopyRoi", 2, 15, (Func<int,float>)(i => source[i]));
    }
    private static void ExplicitCadence()
    {
        string Decide(int frame, int start, int last, int count, bool pending, int pendingFrame, double seconds, int interval)
            => (string)Call("DecideForInterval", frame, start, last, count, pending, pendingFrame, seconds, interval);
        foreach (var interval in new[] {6, 12})
        {
            Equal("submit", Decide(100,100,-1,0,false,0,0,interval), $"{interval}: first request");
            Equal("wait", Decide(100+interval-1,100,100,1,false,100,1,interval), $"{interval}: exclusive pacing");
            Equal("submit", Decide(100+interval,100,100,1,false,100,1,interval), $"{interval}: boundary pacing");
            Equal("wait", Decide(100+interval,100,100,1,true,100,1,interval), $"{interval}: one pending");
            Equal("wait", Decide(219,100,100,40,true,100,9.999,interval), $"{interval}: fortieth may complete");
            Equal("stop-timeout", Decide(220,100,100,40,true,100,9.999,interval), $"{interval}: timeout at 120");
            Equal("stop-duration", Decide(220,100,100,40,true,100,10,interval), $"{interval}: duration before timeout");
            Equal("stop-budget", Decide(220,100,100,40,false,100,9.999,interval), $"{interval}: budget 40");
            Equal("submit", Decide(100+interval,100,100,39,false,100,9.999,interval), $"{interval}: final allowed request");
            Equal("stop-duration", Decide(100+interval,100,100,39,false,100,10,interval), $"{interval}: strict 10s");
            foreach (var history in new (int Frame,int Start,int Last,int Count,bool Pending,int Ticket,double Seconds)[]
            {
                (99,100,-1,0,false,0,0), (100,-1,-1,0,false,0,0),
                (100,100,100,-1,false,100,0), (100,100,100,41,false,100,0),
                (100,100,100,0,false,100,0), (100,100,-1,1,false,0,0),
                (100,100,99,1,false,99,0), (100,100,101,1,false,101,0),
                (100,100,-1,0,true,100,0), (106,100,100,1,true,101,0),
                (100,100,-1,0,false,0,double.NaN), (100,100,-1,0,false,0,double.PositiveInfinity),
                (100,100,-1,0,false,0,-0.01)
            })
                Equal("stop-clock", Decide(history.Frame,history.Start,history.Last,history.Count,history.Pending,history.Ticket,history.Seconds,interval), $"{interval}: invalid history");
        }
        foreach (var invalid in new[] {int.MinValue,-12,-1,0,1,5,7,11,13,int.MaxValue})
            Equal("stop-interval", Decide(100,100,-1,0,false,0,0,invalid), "invalid cadence fails closed");
        Equal(1, Policy.GetMethods().Count(m => m.Name == "Decide"), "legacy reflection remains unambiguous");
        var budgetType = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.CandidatePoseBudget")
            ?? throw new Exception("FAIL: immutable candidate budget is missing.");
        Equal(12, budgetType.GetProperty("SubmissionIntervalFrames")!.GetValue(Activator.CreateInstance(budgetType, new object[] {12}))!, "default budget 12");
        Equal(6, budgetType.GetProperty("SubmissionIntervalFrames")!.GetValue(Activator.CreateInstance(budgetType, new object[] {6}))!, "explicit budget 6");
        Equal(false, budgetType.GetProperty("SubmissionIntervalFrames")!.CanWrite, "cadence immutable");
        foreach (var invalid in new[] {-1,0,5,7,13,int.MaxValue})
        {
            _assertions++;
            try { Activator.CreateInstance(budgetType, new object[] {invalid}); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { continue; }
            throw new Exception("FAIL: immutable candidate budget accepted unsafe cadence.");
        }
    }
    private static void Budget()
    {
        string Decide(int frame, int start, int last, int count, bool pending, int pendingFrame, double seconds)
            => (string)Call("Decide", frame, start, last, count, pending, pendingFrame, seconds);
        Equal("submit", Decide(100, 100, -1, 0, false, 0, 0), "first explicit request");
        Equal("wait", Decide(105, 100, 100, 1, false, 100, 0.1), "six-frame pacing");
        Equal("wait", Decide(106, 100, 100, 1, false, 100, 0.2), "six-frame minimum preserved, ghost pacing is longer");
        Equal("submit", Decide(112, 100, 100, 1, false, 100, 0.2), "twelve-frame readable-trial interval boundary");
        Equal("wait", Decide(106, 100, 100, 1, true, 100, 0.2), "one pending request");
        Equal("wait", Decide(219, 100, 100, 1, true, 100, 1), "timeout exclusive bound");
        Equal("stop-timeout", Decide(220, 100, 100, 1, true, 100, 1), "120-frame timeout");
        Equal("stop-duration", Decide(106, 100, 100, 1, true, 100, 10), "wall clock includes pending");
        Equal("stop-budget", Decide(340, 100, 334, 40, false, 334, 2), "request cap");
        Equal("wait", Decide(340, 100, 334, 40, true, 334, 2), "last request can complete");
        Equal("submit", Decide(346, 100, 334, 39, false, 334, 9.999), "last allowed paced request");
        Equal("stop-clock", Decide(99, 100, -1, 0, false, 0, 0), "frame rewind");
        Equal("stop-clock", Decide(100, 100, -1, 0, false, 0, double.NaN), "invalid time");
        Equal("stop-clock", Decide(100, 100, -1, 0, false, 0, double.PositiveInfinity), "infinite time");
        Equal("stop-clock", Decide(100, 100, -1, 0, false, 0, -0.01), "negative time");
        Equal("stop-clock", Decide(100, 100, 101, 1, false, 101, 0), "future submission");
        Equal("stop-clock", Decide(100, 100, -1, 0, true, 100, 0), "pending without submission");
        Equal("stop-clock", Decide(100, 100, 100, 41, false, 100, 0), "invalid budget history");
        Equal("stop-clock", Decide(106, 100, 100, 1, true, 101, 0.2), "wrong pending ticket");
    }
}
