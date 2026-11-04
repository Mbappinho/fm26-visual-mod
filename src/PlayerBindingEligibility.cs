namespace FM26.VisualDiagnostics;

// H178b zero-behavior extraction: the actual PlayerBinding FIFA eligibility property and its scalar inputs.
// The Unity partial supplies Anchors; PolicyTests supplies a CLR dictionary of anchor keys (values are never read here).
internal sealed partial class PlayerBinding
{
    public static readonly string[] RequiredAnchors =
    {
        "Character_Hips", "Character_Spine3", "Character_Head",
        "Character_LeftArm", "Character_LeftForeArm", "Character_LeftHand",
        "Character_RightArm", "Character_RightForeArm", "Character_RightHand",
        "Character_LeftUpLeg", "Character_LeftLeg", "Character_LeftFoot",
        "Character_RightUpLeg", "Character_RightLeg", "Character_RightFoot",
    };
    public int PlayerId;
    public int ShirtNumber;
    public int TeamId;
    public bool Active;
    public bool Substitute;
    public bool Goalkeeper;
    public bool Medic;
    public bool FifaEligible => FifaActorSelection.FifaEligible(ShirtNumber, Active, Substitute, Goalkeeper, Medic, PlayerId,
        RequiredAnchors.Count(name => Anchors.ContainsKey(name)), TeamId);
}
