using System.Numerics;

namespace FM26.VisualDiagnostics;

// Visual separation/volume only. Never alter the actual owned ROI or correct a pose.
public static class GhostDisplayPolicy
{
    public const float Separation = 1.5f;
    public static Vector3 DisplayOffset(Vector3 cameraRight)
    {
        if (!Finite(cameraRight) || MathF.Abs(cameraRight.LengthSquared()-1) > 0.0001f)
            throw new ArgumentException("Observed camera-right direction must already be finite and unit length.");
        return cameraRight * Separation; // No inferred pose normalization or height correction.
    }
    public static Vector3[] ApplyDisplayOffset(Vector3[] originalWorld, Vector3 offset)
    {
        if (originalWorld == null || originalWorld.Length == 0 || originalWorld.Length > 255
            || !Finite(offset) || MathF.Abs(offset.Length()-Separation) > 0.0001f)
            throw new ArgumentException("Unbounded display positions or unsupported literal offset.");
        var result = new Vector3[originalWorld.Length];
        for (var i=0;i<result.Length;i++)
        {
            if (!Finite(originalWorld[i])) throw new ArgumentException("Nonfinite original world candidate.");
            result[i]=originalWorld[i]+offset;
        }
        return result;
    }
    public static float SegmentRadius(int edge)
    {
        if (edge<0 || edge>=14) throw new ArgumentException("Unknown original segment.");
        return edge==0 ? 0.14f : edge==1 ? 0.045f : 0.065f;
    }
    public static Vector3 PointScale(int anchor)
    {
        if (anchor<0 || anchor>=15) throw new ArgumentException("Unknown original joint.");
        return anchor==2 ? new Vector3(0.12f,0.24f,0.12f) : new Vector3(0.055f,0.11f,0.055f);
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
