using System.Numerics;

namespace FM26.VisualDiagnostics;

public sealed class CandidatePoseRoi
{
    public int X => 0;
    public int Width { get; }
    public int Y { get; }
    public int Height => 2;
    public int Z => 0;
    public int Depth => 1;
    public int FloatCount => Width * 8;
    public int ByteCount => FloatCount * 4;
    internal CandidatePoseRoi(int width, int y) { Width = width; Y = y; }
}

// Pure, Unity-free rules for an explicitly unverified GPU/mesh-index hypothesis.
public sealed class CandidatePoseMotion
{
    public bool DistinctPayload { get; }
    public double MaximumDelta { get; }
    public double RmsDelta { get; }
    public int MovingJointCount { get; }
    internal CandidatePoseMotion(bool distinct, double maximum, double rms, int moving)
    { DistinctPayload = distinct; MaximumDelta = maximum; RmsDelta = rms; MovingJointCount = moving; }
}

// An attempt's cadence is immutable once chosen; no command can retune an active trial.
public sealed class CandidatePoseBudget
{
    public int SubmissionIntervalFrames { get; }
    public CandidatePoseBudget(int submissionIntervalFrames = CandidatePosePolicy.SubmissionIntervalFrames)
    {
        if (!CandidatePosePolicy.IsSubmissionInterval(submissionIntervalFrames))
            throw new ArgumentException("Submission interval must be exactly 6 or 12 frames.");
        SubmissionIntervalFrames = submissionIntervalFrames;
    }
}

public static class CandidatePosePolicy
{
    public const int MaximumRequests = 40;
    public const int SubmissionIntervalFrames = 12;
    public const int TimeoutFrames = 120;
    public const double MaximumSeconds = 10;

    public static CandidatePoseRoi Roi(int entities, int bones, int width, int height, int entityIndex,
        bool rgbaFloat, bool deviceSupported, bool formatSupported)
    {
        if (entities <= 0 || bones <= 0 || bones > 255 || width != bones || (long)height != 2L * entities
            || entityIndex < 0 || entityIndex >= entities || !rgbaFloat || !deviceSupported || !formatSupported
            || (long)width * height * 16 > 1024 * 1024)
            throw new ArgumentException("Unsupported candidate ROI or pose texture.");
        return new CandidatePoseRoi(width, entityIndex * 2);
    }

    public static bool CanCopy(bool done, bool error, int elapsedFrames, int layers, int bytes, int floats, int expectedFloats)
        => done && !error && elapsedFrames >= 0 && elapsedFrames < TimeoutFrames && layers == 1
           && expectedFloats > 0 && expectedFloats <= 255 * 8 && expectedFloats % 8 == 0
           && floats == expectedFloats && bytes == expectedFloats * 4;

    public static float[] CopyRoi(int bones, int length, Func<int, float> read)
    {
        if (bones <= 0 || bones > 255 || length != bones * 8 || read == null)
            throw new ArgumentException("Readback must be exactly one bounded two-row ROI.");
        var owned = new float[length];
        for (var i = 0; i < length; i++) owned[i] = read(i);
        return owned; // Preserve every raw float, including unused/null quaternion tails.
    }

    public static int[] NameIndices(string[] names, int[] references, string[] requiredNames, int gpuBones)
    {
        if (names == null || references == null || names.Length == 0 || names.Length > 255
            || names.Length > gpuBones || gpuBones > 255 || references.Length != names.Length
            || requiredNames == null || requiredNames.Length == 0 || requiredNames.Length > names.Length)
            throw new ArgumentException("Invalid bounded mesh-order descriptor.");
        var seenReferences = new HashSet<int>();
        for (var i = 0; i < names.Length; i++)
        {
            if (references[i] == 0 ? names[i] != null : string.IsNullOrEmpty(names[i]) || !seenReferences.Add(references[i]))
                throw new ArgumentException("Inconsistent null slot or duplicate bone reference.");
        }
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new int[requiredNames.Length];
        for (var n = 0; n < requiredNames.Length; n++)
        {
            if (string.IsNullOrEmpty(requiredNames[n]) || !seenNames.Add(requiredNames[n]))
                throw new ArgumentException("Required names must be unique and literal.");
            var match = -1;
            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], requiredNames[n], StringComparison.Ordinal)) continue;
                if (match != -1 || references[i] == 0) throw new ArgumentException("Ambiguous required bone.");
                match = i;
            }
            if (match < 0) throw new ArgumentException("Required bone absent from original mesh order.");
            result[n] = match;
        }
        return result; // Null holes and signed identities never shift the original mesh index.
    }

    public static bool SameBoneOrder(string[] beforeNames, int[] beforeReferences, string[] afterNames, int[] afterReferences)
    {
        if (beforeNames == null || beforeReferences == null || afterNames == null || afterReferences == null
            || beforeNames.Length == 0 || beforeNames.Length > 255 || beforeNames.Length != beforeReferences.Length
            || afterNames.Length != afterReferences.Length || beforeNames.Length != afterNames.Length) return false;
        for (var i = 0; i < beforeNames.Length; i++)
            if (beforeReferences[i] != afterReferences[i] || !string.Equals(beforeNames[i], afterNames[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public static Vector3[] Positions(float[] roi, int bones, int[] indices)
    {
        if (roi == null || bones <= 0 || bones > 255 || roi.Length != bones * 8
            || indices == null || indices.Length == 0 || indices.Length > bones)
            throw new ArgumentException("Invalid owned ROI position descriptor.");
        var seen = new HashSet<int>();
        var result = new Vector3[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var k = indices[i];
            if (k < 0 || k >= bones || !seen.Add(k)) throw new ArgumentException("Invalid literal candidate index.");
            result[i] = new Vector3(roi[4 * k], roi[4 * k + 1], roi[4 * k + 2]);
        }
        ValidatePositions(result);
        return result; // Only T.xyz is a candidate; Q is neither consumed nor normalized.
    }

    public static Vector3[] RelativeTo(Vector3[] positions, int originIndex)
    {
        ValidatePositions(positions);
        if (originIndex < 0 || originIndex >= positions.Length) throw new ArgumentException("Missing copied origin.");
        var origin = positions[originIndex];
        return positions.Select(p => p - origin).ToArray();
    }

    public static CandidatePoseMotion MeasureMotion(Vector3[] previous, Vector3[] current, bool distinctPayload)
    {
        ValidatePositions(previous); ValidatePositions(current);
        if (previous.Length != current.Length) throw new ArgumentException("Changed joint order/length.");
        if (!distinctPayload) return new CandidatePoseMotion(false, 0, 0, 0);
        double maximum = 0, sumSquared = 0;
        var moving = 0;
        for (var i = 0; i < previous.Length; i++)
        {
            // Double arithmetic avoids inventing motion through float world-root cancellation.
            var x = (double)current[i].X - previous[i].X;
            var y = (double)current[i].Y - previous[i].Y;
            var z = (double)current[i].Z - previous[i].Z;
            var squared = x * x + y * y + z * z;
            var distance = Math.Sqrt(squared);
            maximum = Math.Max(maximum, distance); sumSquared += squared;
            if (distance > 0.0001) moving++;
        }
        return new CandidatePoseMotion(true, maximum, Math.Sqrt(sumSquared / previous.Length), moving);
    }

    public static float[] CheckLengths(Vector3[] positions, int[] endpoints, float[] firstSampleLengths)
    {
        ValidatePositions(positions);
        if (endpoints == null || endpoints.Length == 0 || endpoints.Length > 510 || endpoints.Length % 2 != 0
            || (firstSampleLengths != null && firstSampleLengths.Length != endpoints.Length / 2))
            throw new ArgumentException("Invalid bounded segment topology.");
        var lengths = new float[endpoints.Length / 2];
        for (var edge = 0; edge < lengths.Length; edge++)
        {
            var a = endpoints[2 * edge]; var b = endpoints[2 * edge + 1];
            if (a < 0 || b < 0 || a >= positions.Length || b >= positions.Length || a == b)
                throw new ArgumentException("Invalid segment endpoint.");
            var length = Vector3.Distance(positions[a], positions[b]);
            if (!float.IsFinite(length) || length < 0.01f || length > 1.5f)
                throw new ArgumentException("Candidate segment length outside experimental sanity bounds.");
            if (firstSampleLengths != null)
            {
                var reference = firstSampleLengths[edge];
                if (!float.IsFinite(reference) || reference < 0.01f || reference > 1.5f
                    || length / reference < 0.65f || length / reference > 1.35f)
                    throw new ArgumentException("Candidate segment length changed implausibly; refuse rather than correct.");
            }
            lengths[edge] = length;
        }
        return lengths;
    }

    private static void ValidatePositions(Vector3[] positions)
    {
        if (positions == null || positions.Length == 0 || positions.Length > 255)
            throw new ArgumentException("Unbounded candidate positions.");
        foreach (var p in positions)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || p.LengthSquared() > 16)
                throw new ArgumentException("Nonfinite or implausibly large candidate pose; no corrective scaling.");
    }

    public static string Decide(int frame, int startFrame, int lastSubmittedFrame, int submittedRequestCount,
        bool pending, int pendingSubmittedFrame, double elapsedSeconds)
        => DecideForInterval(frame, startFrame, lastSubmittedFrame, submittedRequestCount,
            pending, pendingSubmittedFrame, elapsedSeconds, SubmissionIntervalFrames);

    public static bool IsSubmissionInterval(int interval) => interval == 6 || interval == 12;

    // Distinct method name preserves existing reflection callers of the seven-argument Decide.
    public static string DecideForInterval(int frame, int startFrame, int lastSubmittedFrame, int submittedRequestCount,
        bool pending, int pendingSubmittedFrame, double elapsedSeconds, int submissionIntervalFrames)
    {
        if (!IsSubmissionInterval(submissionIntervalFrames)) return "stop-interval";
        if (frame < startFrame || startFrame < 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0
            || submittedRequestCount < 0 || submittedRequestCount > MaximumRequests
            || (submittedRequestCount == 0 ? lastSubmittedFrame != -1 : lastSubmittedFrame < startFrame || lastSubmittedFrame > frame)
            || (pending && (submittedRequestCount == 0 || pendingSubmittedFrame != lastSubmittedFrame))) return "stop-clock";
        if (elapsedSeconds >= MaximumSeconds) return "stop-duration";
        if (pending) return (long)frame - pendingSubmittedFrame >= TimeoutFrames ? "stop-timeout" : "wait";
        if (submittedRequestCount >= MaximumRequests) return "stop-budget";
        return submittedRequestCount == 0 || (long)frame - lastSubmittedFrame >= submissionIntervalFrames ? "submit" : "wait";
    }
}
