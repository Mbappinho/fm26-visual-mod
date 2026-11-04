namespace FM26.VisualDiagnostics;

// Offline pure configuration seam. No host calls this API; true is not activation permission.
// Null denotes an absent configuration. No file access or runtime policy belongs here.
public static class FifaAutoActivationConfiguration
{
    public static bool ParseEnabled(byte[] utf8Json)
    {
        if (utf8Json == null) return false;
        if (utf8Json.Length > 4096) throw new ArgumentException("Autoactivation configuration exceeds 4096 bytes.", nameof(utf8Json));
        using var document = System.Text.Json.JsonDocument.Parse(utf8Json);
        bool seenEnabled = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != "Enabled") throw new System.Text.Json.JsonException("Unknown autoactivation configuration property.");
            if (seenEnabled) throw new System.Text.Json.JsonException("Duplicate autoactivation configuration property.");
            seenEnabled = true;
        }
        return document.RootElement.TryGetProperty("Enabled", out var enabled) && enabled.GetBoolean();
    }
}
