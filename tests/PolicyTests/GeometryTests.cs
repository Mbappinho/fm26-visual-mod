using System.Collections;
using System.Numerics;
using System.Reflection;

internal static class GeometryTests
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.PrismGeometry");
        if (type == null) throw new Exception("FAIL: original procedural prism geometry is missing.");
        var create = type.GetMethod("Create")!;
        foreach (var sides in new[] { 3, 8, 12 })
        {
            var data = create.Invoke(null, new object[] { sides })!;
            var vertices = (Vector3[])data.GetType().GetProperty("Vertices")!.GetValue(data)!;
            var indices = (int[])data.GetType().GetProperty("Indices")!.GetValue(data)!;
            if (indices.Length % 3 != 0 || indices.Any(index => index < 0 || index >= vertices.Length))
                throw new Exception("Triangle indices must be valid.");
            var edges = new Dictionary<(int, int), int>();
            for (var at = 0; at < indices.Length; at += 3)
            {
                var a = indices[at]; var b = indices[at + 1]; var c = indices[at + 2];
                var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (normal.LengthSquared() < 0.000001f) throw new Exception("Degenerate triangle.");
                var center = (vertices[a] + vertices[b] + vertices[c]) / 3;
                if (Vector3.Dot(normal, center) <= 0) throw new Exception("Faces must point outwards.");
                foreach (var edge in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = edge.Item1 < edge.Item2 ? edge : (edge.Item2, edge.Item1);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }
            if (edges.Values.Any(count => count != 2)) throw new Exception("Geometry must be watertight.");
        }
        try { create.Invoke(null, new object[] { 2 }); throw new Exception("Invalid side count accepted."); }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS: original geometry validity, winding, watertightness and invalid-input cases.");
    }
}
