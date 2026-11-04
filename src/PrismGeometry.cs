using System.Numerics;

namespace FM26.VisualDiagnostics;

public sealed class PrismData
{
    public Vector3[] Vertices { get; }
    public int[] Indices { get; }
    public PrismData(Vector3[] vertices, int[] indices) { Vertices = vertices; Indices = indices; }
}

public static class PrismGeometry
{
    public static PrismData Create(int sides)
    {
        if (sides < 3 || sides > 64) throw new ArgumentOutOfRangeException(nameof(sides));
        var vertices = new Vector3[sides * 2 + 2];
        var indices = new List<int>();
        for (var index = 0; index < sides; index++)
        {
            var angle = index * MathF.PI * 2 / sides;
            var x = MathF.Cos(angle); var z = MathF.Sin(angle);
            vertices[index] = new Vector3(x, -0.5f, z);
            vertices[index + sides] = new Vector3(x, 0.5f, z);
        }
        var bottom = sides * 2; var top = bottom + 1;
        vertices[bottom] = new Vector3(0, -0.5f, 0);
        vertices[top] = new Vector3(0, 0.5f, 0);
        for (var index = 0; index < sides; index++)
        {
            var next = (index + 1) % sides;
            indices.AddRange(new[] { index, index + sides, next, next, index + sides, next + sides });
            indices.AddRange(new[] { bottom, index, next, top, next + sides, index + sides });
        }
        return new PrismData(vertices, indices.ToArray());
    }
}
