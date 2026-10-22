using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WootingLangBar;

public sealed class CapsLockConfig
{
    [JsonPropertyName("barLeds")] public List<int> BarLeds { get; set; } = new();
    [JsonPropertyName("keys")] public List<int[]> Keys { get; set; } = new();
    [JsonPropertyName("color")] public string Color { get; set; } = "#FFFFFF";
}

public sealed class Config
{
    /// <summary>"bar" (light bar), "key" (recolour keys), or "both".</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "bar";

    /// <summary>Colour per language, shared by the bar and the keys.</summary>
    [JsonPropertyName("colors")] public Dictionary<string, string> Colors { get; set; } = new();

    // key mode
    [JsonPropertyName("keys")] public List<int[]> Keys { get; set; } = new() { new[] { 3, 0 } };
    [JsonPropertyName("capsLock")] public CapsLockConfig? CapsLock { get; set; }

    [JsonPropertyName("pollMs")] public int PollMs { get; set; } = 100;
    [JsonPropertyName("resyncMs")] public int ResyncMs { get; set; } = 2000;

    /// <summary>Entries that were dropped or corrected while loading; the tray app writes them to the log.</summary>
    [JsonIgnore] public List<string> Warnings { get; } = new();

    bool BothMode => string.Equals(Mode, "both", StringComparison.OrdinalIgnoreCase);
    public bool BarMode => BothMode || string.Equals(Mode, "bar", StringComparison.OrdinalIgnoreCase);
    public bool KeyMode => BothMode || string.Equals(Mode, "key", StringComparison.OrdinalIgnoreCase);
    public bool CapsOnBar => BarMode && CapsLock is { BarLeds.Count: > 0 };     // key mode leaves the light bar alone
    public bool CapsOnKeys => CapsLock is { Keys.Count: > 0 };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        var opts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), opts) ?? new Config();
        var warn = cfg.Warnings;

        // JSON "null" for any list/dictionary becomes an empty one instead of a crash later on.
        cfg.Colors ??= new();
        cfg.Keys ??= new();
        if (string.IsNullOrWhiteSpace(cfg.Mode)) { warn.Add("mode is empty; using \"bar\""); cfg.Mode = "bar"; }
        else if (!cfg.BarMode && !cfg.KeyMode) { warn.Add($"mode \"{cfg.Mode}\" is not bar/key/both; using \"bar\""); cfg.Mode = "bar"; }

        if (cfg.PollMs < 50) { warn.Add($"pollMs {cfg.PollMs} is below the minimum; using 50"); cfg.PollMs = 50; }
        if (cfg.ResyncMs < 500) { warn.Add($"resyncMs {cfg.ResyncMs} is below the minimum; using 500"); cfg.ResyncMs = 500; }

        cfg.Keys = ValidKeys(cfg.Keys, "keys", warn);

        foreach (var (k, v) in cfg.Colors.ToList())
            if (!TryParseColor(v, out _)) { warn.Add($"colors.{k}: \"{v}\" is not a colour (use #RRGGBB or a .NET colour name); ignored"); cfg.Colors.Remove(k); }

        if (cfg.CapsLock != null)
        {
            var cl = cfg.CapsLock;
            cl.Keys = ValidKeys(cl.Keys ?? new(), "capsLock.keys", warn);
            cl.BarLeds ??= new();
            var leds = cl.BarLeds.Where(i => i >= 0 && i < WootingHid.LightbarLeds).ToList();
            if (leds.Count != cl.BarLeds.Count) warn.Add($"capsLock.barLeds: LEDs outside 0..{WootingHid.LightbarLeds - 1} ignored");
            cl.BarLeds = leds;
            if (!TryParseColor(cl.Color, out _)) { warn.Add($"capsLock.color: \"{cl.Color}\" is not a colour; using #FFFFFF"); cl.Color = "#FFFFFF"; }
        }
        return cfg;
    }

    /// <summary>Keeps only well-formed [row, column] pairs inside the key-id address space (row 0..7, column 0..31).</summary>
    static List<int[]> ValidKeys(List<int[]> keys, string name, List<string> warn)
    {
        var ok = new List<int[]>();
        foreach (var k in keys)
        {
            if (k is { Length: 2 } && k[0] >= 0 && k[0] < 8 && k[1] >= 0 && k[1] < 32) ok.Add(k);
            else warn.Add($"{name}: [{(k == null ? "" : string.Join(",", k))}] is not a [row, column] pair in range; ignored");
        }
        return ok;
    }

    /// <summary>Finds the config entry ("he", "en", "ru", "0419", ... or "other") that applies to a Windows LANGID.</summary>
    static string? MatchKey(int langId, IEnumerable<string> keys)
    {
        int primary = langId & 0x3FF;
        string hex = langId.ToString("X4");
        string iso = LangDetect.Iso639(langId);
        string? other = null;
        foreach (var key in keys)
        {
            var k = key.Trim().ToLowerInvariant();
            bool match = k switch
            {
                "he" or "hebrew" => primary == 0x0D,
                "en" or "english" => primary == 0x09,
                "other" => false,
                _ => string.Equals(k, hex, StringComparison.OrdinalIgnoreCase) || (k.Length == 2 && iso == k),
            };
            if (match) return key;
            if (k == "other") other = key;
        }
        return other;
    }

    /// <summary>Colour for a LANGID, or null when nothing applies (indicator then shows the profile's own lighting).</summary>
    public Color? ColorFor(int langId)
    {
        var k = MatchKey(langId, Colors.Keys);
        return k == null ? null : ParseColor(Colors[k]);
    }

    /// <summary>Accepts "#RRGGBB" / "RRGGBB" or a known .NET colour name ("Red", "DodgerBlue").</summary>
    public static bool TryParseColor(string? s, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, null, out int v))
        {
            color = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
            return true;
        }
        var named = Color.FromName(s);
        if (!named.IsKnownColor) return false;      // FromName returns an all-zero (black) colour for unknown names
        color = named;
        return true;
    }

    public static Color ParseColor(string s)
        => TryParseColor(s, out var c) ? c : throw new FormatException($"\"{s}\" is not a colour (use #RRGGBB or a .NET colour name)");
}
