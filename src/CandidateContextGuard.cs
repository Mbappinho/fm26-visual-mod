namespace FM26.VisualDiagnostics;

// Scalar-only managed observations. Never stores or reads a producer/native object.
internal sealed class CandidateContextFailure
{
    public string Predicate { get; }
    public string Stage { get; }
    public int ActualFrame { get; }
    public long QpcTicks { get; }
    public long QpcFrequency { get; }
    public int? SubjectInstanceId { get; }
    public int? Expected { get; }
    public int? Observed { get; }

    internal CandidateContextFailure(string predicate, string stage, int actualFrame, long qpcTicks,
        long qpcFrequency, int? subjectInstanceId, int? expected, int? observed)
    {
        Predicate = predicate; Stage = stage; ActualFrame = actualFrame;
        QpcTicks = qpcTicks; QpcFrequency = qpcFrequency;
        SubjectInstanceId = subjectInstanceId; Expected = expected; Observed = observed;
    }
}

// First failure wins for the entire attempt, including later Stop/cleanup publications.
internal sealed class CandidateContextGuard
{
    public CandidateContextFailure Failure { get; private set; } = null!;

    public bool Reject(string predicate, string stage, int actualFrame, long qpcTicks, long qpcFrequency,
        int? subjectInstanceId = null, int? expected = null, int? observed = null)
    {
        Failure ??= new CandidateContextFailure(predicate, stage, actualFrame, qpcTicks, qpcFrequency,
            subjectInstanceId, expected, observed);
        return false;
    }
}
