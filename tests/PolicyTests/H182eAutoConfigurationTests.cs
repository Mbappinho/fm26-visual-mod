using FM26.VisualDiagnostics;
using System.Text;

internal static class H182eAutoConfigurationTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: H182e " + message);
    }
    private static bool Parse(string text) => FifaAutoActivationConfiguration.ParseEnabled(Encoding.UTF8.GetBytes(text));
    public static void Run(string? name = null)
    {
        var cases = new (string Name, Action Test)[]
        {
            ("config-defaults", Defaults),
            ("config-intrinsic", Intrinsic),
            ("config-unknown", Unknown),
            ("config-duplicate", Duplicate),
            ("config-byte-cap", ByteCap),
            ("config-enabled", () => Check(Parse("{\"Enabled\":true}"), "explicit true must enable opt-in")),
        };
        int ran = 0;
        foreach (var c in cases)
        {
            if (name != null && name != c.Name) continue;
            c.Test(); ran++; Console.WriteLine("H182e PASS: " + c.Name);
        }
        if (name != null && ran != 1) throw new Exception("Unknown H182e case " + name);
    }
    private static void Refused(byte[] bytes, string message)
    {
        try { FifaAutoActivationConfiguration.ParseEnabled(bytes); }
        catch (Exception e) when (e is System.Text.Json.JsonException || e is InvalidOperationException || e is ArgumentException)
        {
            Console.WriteLine("H182e refusal: " + message + " => " + e.GetType().FullName);
            return;
        }
        Check(false, message);
    }
    private static void Refused(string text, string message) => Refused(Encoding.UTF8.GetBytes(text), message);
    private static void Intrinsic()
    {
        foreach (var text in new[] { "null", "true", "false", "0", "\"text\"", "[]", "[{}]" })
            Refused(text, "nonobject refused: " + text);
        foreach (var value in new[] { "null", "0", "1", "\"true\"", "[]", "{}" })
            Refused("{\"Enabled\":" + value + "}", "nonboolean refused: " + value);
        foreach (var text in new[] { "", " ", "{", "{\"Enabled\":", "{\"Enabled\":True}", "{\"Enabled\":true,}", "{/*comment*/}", "{}{}", "{}x", "{Enabled:true}" })
            Refused(text, "malformed JSON refused: " + text);
        Check(Parse(" \r\n{ \"Enabled\" : true } \t"), "JSON whitespace accepted");
        Check(Parse("{\"\\u0045nabled\":true}"), "escaped known key accepted");
        var bytes = Encoding.UTF8.GetBytes("{\"Enabled\":true}");
        var copy = bytes.ToArray();
        Check(FifaAutoActivationConfiguration.ParseEnabled(bytes), "byte API accepted");
        Check(bytes.SequenceEqual(copy), "caller bytes unchanged");
    }
    private static void Unknown()
    {
        Refused("{\"Enabled\":true,\"Other\":false}", "unknown property must refuse");
        foreach (var key in new[] { "enabled", "ENABLED", " Enabled", "Enabled ", "PackPath", "ActorInstanceIds", "Session", "RequestId", "MaximumSeconds", "HideNativePlayer", "é" })
        {
            Refused("{\"" + key + "\":true}", "unknown key alone: " + key);
            Refused("{\"" + key + "\":null,\"Enabled\":true}", "unknown key before Enabled: " + key);
            Refused("{\"Enabled\":false,\"" + key + "\":{}}", "unknown key after disabled: " + key);
        }
    }
    private static void Duplicate()
    {
        Refused("{\"Enabled\":false,\"Enabled\":true}", "duplicate property must refuse");
        foreach (var first in new[] { "true", "false" })
            foreach (var last in new[] { "true", "false" })
            {
                Refused("{\"Enabled\":" + first + ",\"Enabled\":" + last + "}", "duplicate Boolean pair " + first + "/" + last);
                Refused("{\"\\u0045nabled\":" + first + ",\"Enabled\":" + last + "}", "escaped duplicate first");
                Refused("{\"Enabled\":" + first + ",\"\\u0045nabled\":" + last + "}", "escaped duplicate last");
            }
    }
    private static byte[] Padded(string json, int size) => Encoding.UTF8.GetBytes(json + new string(' ', size - Encoding.UTF8.GetByteCount(json)));
    private static void TooLarge(byte[] bytes, string message)
    {
        try { FifaAutoActivationConfiguration.ParseEnabled(bytes); }
        catch (ArgumentException e) when (e.ParamName == "utf8Json")
        {
            Console.WriteLine("H182e byte gate: " + message + " => " + bytes.Length);
            return;
        }
        Check(false, message);
    }
    private static void ByteCap()
    {
        var boundary = Padded("{\"Enabled\":true}", 4096);
        Check(boundary.Length == 4096, "boundary fixture really 4096 bytes");
        Check(FifaAutoActivationConfiguration.ParseEnabled(boundary), "4096-byte valid object accepted");
        var over = Padded("{\"Enabled\":true}", 4097);
        Check(over.Length == 4097, "over fixture really 4097 bytes");
        TooLarge(over, "4097-byte valid object must refuse");
        Check(!FifaAutoActivationConfiguration.ParseEnabled(Padded("{}", 4096)), "4096-byte empty object disabled");
        Check(!FifaAutoActivationConfiguration.ParseEnabled(Padded("{\"Enabled\":false}", 4096)), "4096-byte false object disabled");
        TooLarge(Padded("{}", 4097), "oversize empty object refused");
        TooLarge(Padded("{\"Enabled\":false}", 4097), "oversize false object refused");
        TooLarge(new byte[4097], "byte gate precedes malformed JSON");
        var unicode = "{\"" + new string('é', 2048) + "\":true}";
        var unicodeBytes = Encoding.UTF8.GetBytes(unicode);
        Check(unicode.Length < 4096 && unicodeBytes.Length > 4096, "UTF8 byte count differs from character count");
        TooLarge(unicodeBytes, "byte gate counts UTF8 bytes before schema");
        Console.WriteLine("H182e UTF8 fixture: chars=" + unicode.Length + " bytes=" + unicodeBytes.Length);
    }
    private static void Defaults()
    {
        Check(!FifaAutoActivationConfiguration.ParseEnabled(null!), "absent must disable");
        Check(!Parse("{}"), "empty object must disable");
        Check(!Parse("{\"Enabled\":false}"), "explicit false must disable");
    }
}
