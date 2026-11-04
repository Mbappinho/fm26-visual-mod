namespace FM26.VisualDiagnostics;

public static class GpuCapturePolicy
{
    public const int MaximumBytes = 1024 * 1024;
    public const int TimeoutFrames = 120;

    public static int ExpectedByteCount(int entities, int bones, int width, int height,
        bool rgbaFloat, bool deviceSupported, bool formatSupported)
    {
        var expectedHeight = (long)entities * 2;
        var bytes = (long)width * height * 16;
        if (!rgbaFloat || !deviceSupported || !formatSupported || entities <= 0 || bones <= 0 || bones > 255
            || width != bones || height != expectedHeight || bytes <= 0 || bytes > MaximumBytes)
            throw new ArgumentException("Unsupported or out-of-bounds GPU pose texture.");
        return (int)bytes;
    }

    public static bool CanCopy(bool done, bool error, bool sameContext, int elapsedFrames,
        int layers, int bytes, int expectedBytes)
        => done && !error && sameContext && elapsedFrames >= 0 && elapsedFrames < TimeoutFrames
           && layers == 1 && expectedBytes > 0 && expectedBytes <= MaximumBytes
           && expectedBytes % 4 == 0 && bytes == expectedBytes;

    public static float[] CopyOwned(int length, Func<int, float> read)
    {
        if (length <= 0 || length > MaximumBytes / 4) throw new ArgumentException("Unbounded readback.");
        var owned = new float[length];
        for (var index = 0; index < length; index++) owned[index] = read(index);
        return owned;
    }

    public static byte[] ToBytes(float[] owned)
    {
        if (!BitConverter.IsLittleEndian) throw new NotSupportedException("Only little-endian float snapshots are supported.");
        if (owned.Length <= 0 || owned.Length > MaximumBytes / 4) throw new ArgumentException("Unbounded snapshot.");
        var bytes = new byte[owned.Length * 4];
        Buffer.BlockCopy(owned, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
