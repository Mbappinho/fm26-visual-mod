namespace FM26.VisualDiagnostics;

public static class PoseOwnerPolicy
{
    public static bool IsUsable(bool initialized, bool shuttingDown, bool buffersCreated, int entities, int bones, int worlds, int values)
        => initialized && !shuttingDown && buffersCreated && entities > 0 && bones > 0 && worlds >= entities && values > 0;

    public static bool HasMatchingNativeService(bool leftKnownWrapper, bool rightKnownWrapper, long leftInstance, long rightInstance)
        => leftKnownWrapper && rightKnownWrapper && leftInstance != 0 && leftInstance == rightInstance;

    public static bool HasMatchingIdentity(int index, int tableLength, ushort characterId, ushort tableId, bool sameService)
        => index >= 0 && index < tableLength && characterId != ushort.MaxValue && characterId == tableId && sameService;
}
