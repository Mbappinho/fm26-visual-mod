#nullable disable
namespace FM26.VisualDiagnostics;

// H156: pure lifecycle state machine of the FIFA overlay (no Unity types). FifaOverlay.cs delegates every
// start/stop/cleanup/publication/exclusion decision here so the transitions are executable in PolicyTests.
public sealed class FifaOverlayLifecycle
{
    public bool Armed { get; private set; }
    public bool Active { get; private set; }
    public bool CleanupRequested { get; private set; }
    public bool Finalized { get; private set; }
    public bool Published { get; private set; }
    public bool PublicationFailed => Finalized && !Published;
    public bool CleanupVerified { get; private set; }
    public bool PublishObligation => Armed;
    public string Reason { get; private set; }
    public string Error { get; private set; }
    public int CleanupFrame { get; private set; }
    public long CleanupQpc { get; private set; }
    // Exclusion is released only by a later-frame VERIFIED cleanup whose report was actually written (multi-pass-contract L58).
    public bool Blocked => Active || (CleanupRequested && !(CleanupVerified && Published));

    // Called first in Start: an existing request-bound report refuses before any publish obligation exists.
    public void Arm(bool reportExists)
    {
        if (Armed) throw new InvalidOperationException("FIFA overlay lifecycle already armed.");
        if (reportExists) throw new InvalidOperationException("Request-bound FIFA overlay report already exists.");
        Armed = true;
    }

    public void Activate()
    {
        if (!Armed || CleanupRequested || Active) throw new InvalidOperationException("FIFA overlay lifecycle cannot activate.");
        Active = true;
    }

    // One publication attempt; records the verified cleanup and whether the request-bound report was actually written.
    public void RecordPublication(bool verified, bool written)
    {
        if (!CleanupRequested || Finalized) throw new InvalidOperationException("FIFA overlay publication out of order.");
        Finalized = true; CleanupVerified = verified; Published = written;
    }

    public bool RequestStop(string reason, string error, int frame, long qpc)
    {
        if (!Armed || CleanupRequested) return false;
        Active = false; CleanupRequested = true; Reason = reason; Error = error; CleanupFrame = frame; CleanupQpc = qpc;
        return true;
    }

    public bool ShouldPublish(int frame, long now, long frequency, bool verified)
    {
        if (!CleanupRequested || Finalized || frame <= CleanupFrame) return false;
        return verified || (long)frame - CleanupFrame >= 120 || (decimal)now - CleanupQpc >= (decimal)frequency * 5;
    }
}

// H156 B1: request-bound final report writer, same pattern as OriginalPassDemo/MultiPass finals:
// bounded bytes, CreateNew temporary, Flush(true), NON-overwriting Move, temporary always removed.
public static class FifaOverlayReport
{
    public const long MaximumBytes = 8L * 1024 * 1024;
    public static string FileName(string requestId) => $"fifa-overlay-{requestId}.json";

    public static void WriteNew(string target, byte[] bytes, long maximumBytes)
    {
        if (bytes == null || bytes.LongLength > maximumBytes) throw new InvalidOperationException("FIFA overlay report exceeds output bound.");
        if (File.Exists(target)) throw new InvalidOperationException("Request-bound FIFA overlay report already exists; never overwritten.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            try { File.Move(temporary, target, false); }
            catch (IOException e) when (File.Exists(target)) { throw new InvalidOperationException("Request-bound FIFA overlay report appeared concurrently; never overwritten.", e); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
