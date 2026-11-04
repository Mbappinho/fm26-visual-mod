using System.Numerics;
using System.Reflection;

internal static class GhostDisplayPolicyTests
{
    private static int _assertions;
    private static Type Policy => Assembly.GetExecutingAssembly().GetType("FM26.VisualDiagnostics.GhostDisplayPolicy")
        ?? throw new Exception("FAIL: visible ghost policy missing.");
    private static object Call(string method, params object[] args) => Policy.GetMethod(method)!.Invoke(null,args)!;
    private static void Check(bool okay,string label)
    { _assertions++; if (!okay) throw new Exception("FAIL: " + label); }
    private static void Reject(string method, params object[] args)
    { _assertions++; try { Call(method,args); } catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { return; } throw new Exception("FAIL: unsafe ghost input accepted"); }
    public static void Run()
    {
        var offset=(Vector3)Call("DisplayOffset",Vector3.UnitX);
        Check(offset==new Vector3(1.5f,0,0),"literal display offset, not pose scaling");
        var source=new[]{new Vector3(0,1,0),new Vector3(0.2f,1.5f,0.1f)};
        var shifted=(Vector3[])Call("ApplyDisplayOffset",source,offset);
        Check(shifted[0]==source[0]+offset,"only owned display position changes");
        Check(source[0]==new Vector3(0,1,0),"input world pose not mutated");
        Check(MathF.Abs(Vector3.Distance(source[0],source[1])-Vector3.Distance(shifted[0],shifted[1]))<0.000001f,"display separation preserves segment length within float precision");
        Reject("DisplayOffset",Vector3.Zero);
        Reject("DisplayOffset",new Vector3(2,0,0));
        Reject("DisplayOffset",new Vector3(float.NaN,0,0));
        Reject("ApplyDisplayOffset",source,new Vector3(100,0,0));
        Check((float)Call("SegmentRadius",0)==0.14f,"original torso width");
        Check((float)Call("SegmentRadius",2)==0.065f,"readable limb width");
        Check((Vector3)Call("PointScale",2)==new Vector3(0.12f,0.24f,0.12f),"owned head volume");
        Reject("PointScale",15);
        Reject("SegmentRadius",14);
        Console.WriteLine($"PASS: {_assertions} original ghost display assertions.");
    }
}
