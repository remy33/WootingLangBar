using HidSharp;

namespace WootingLangBar;

/// <summary>
/// Minimal client for the Wooting configuration HID interface (usage page 0xFF55 on ARM boards such as the 80HE,
/// 0x1337 on older boards). Sends the same commands Wootility sends:
/// 11 = GetCurrentKeyboardProfileIndex, 23 = ActivateProfile (RAM only; saved profiles are untouched).
/// Response layout (report id stripped): [magic, 0xDA, cmd, status, profileCount, ?, activeIndex, ...].
/// </summary>
public sealed class WootingHid : IDisposable
{
    const int VendorId = 0x31E3;
    const byte CmdGetProfile = 11;
    const byte CmdActivateProfile = 23;
    const byte StatusOk = 0x88;

    readonly HidStream _stream;
    readonly bool _multi;          // ARM firmware: report id 1, magic D1 DA, response carries report id
    readonly int _inLen;
    public string DevicePath { get; }

    WootingHid(HidDevice dev, bool multi)
    {
        _multi = multi;
        DevicePath = dev.DevicePath;
        _inLen = dev.GetMaxInputReportLength();      // before Open, so a failure here cannot leak the stream
        var opts = new OpenConfiguration();
        opts.SetOption(OpenOption.Exclusive, false);
        _stream = dev.Open(opts);
        _stream.ReadTimeout = 50;
    }

    public static WootingHid? TryOpen(Action<string> log)
    {
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId))
        {
            try
            {
                var pages = d.GetReportDescriptor().DeviceItems
                    .SelectMany(i => i.Usages.GetAllValues()).Select(u => (int)(u >> 16)).Distinct().ToList();
                if (pages.Contains(0xFF55)) return new WootingHid(d, true);
                if (pages.Contains(0x1337)) return new WootingHid(d, false);
            }
            catch (Exception e) { if (!e.Message.Contains("report descriptor")) log($"skip {d.DevicePath}: {e.Message}"); }
        }
        return null;
    }

    /// <summary>
    /// Response layout on the multi-report interface: [magic, 0xDA, cmd, status, lenLo, lenHi, body...].
    /// Returns the body, or null when there is no reply or the status is not 0x88.
    /// </summary>
    byte[]? Body(byte[]? r)
    {
        if (r == null || r.Length < 6 || r[3] != StatusOk) return null;
        int len = _multi ? r[4] | (r[5] << 8) : r[4];
        int off = _multi ? 6 : 5;
        if (off + len > r.Length) len = r.Length - off;
        return r.Skip(off).Take(len).ToArray();
    }

    /// <summary>0-based active profile, or null on error.</summary>
    public int? GetActiveProfile()
    {
        var body = Body(Command(CmdGetProfile));
        return body == null || body.Length < 1 ? null : body[0];
    }

    // ---------- light bar (same path as Wootility's live preview: LEDBarProfile upload with save=0) ----------

    const byte CmdSetLedBarProfile = 32;   // output report, body = protobuf profiles.LEDBarProfile
    const byte CmdGetLedBarColors = 68;    // feature command, reply = protobuf profiles.LEDBarReport
    public const int LightbarLeds = 10;    // canvas start 0, end 9 on the 80HE

    /// <summary>Current colours of the light bar LEDs, as reported by the keyboard.</summary>
    public List<System.Drawing.Color>? GetLightbarColors()
    {
        var body = Body(Command(CmdGetLedBarColors));
        if (body == null) return null;
        var list = new List<System.Drawing.Color>();
        foreach (var (field, wire, bytes, _) in Proto.Fields(body))
            if (field == 1 && wire == 2 && bytes != null)
            {
                int r = 0, g = 0, b = 0;
                foreach (var (f2, w2, _, v) in Proto.Fields(bytes)) { if (f2 == 1) r = (int)v; else if (f2 == 2) g = (int)v; else if (f2 == 3) b = (int)v; }
                list.Add(System.Drawing.Color.FromArgb(r & 255, g & 255, b & 255));
            }
        return list;
    }

    // Colour word is 0xAARRGGBB; alpha 0 renders black, so alpha is always 0xFF.
    static uint Argb(System.Drawing.Color c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>LEDBarEffectLayer { solid { colour, lightbar canvas [start..end] } }.</summary>
    public static byte[] BarLayer(System.Drawing.Color c, int start, int end)
    {
        var canvas = Proto.Concat(Proto.Varint(1, (ulong)start), Proto.Varint(2, (ulong)end));     // LightbarCanvas {start, end}
        return Proto.Len(6, Proto.Concat(Proto.Varint(1, Argb(c)), Proto.Len(2, canvas)));         // solid {colour, lightbar}
    }

    /// <summary>
    /// LEDBarEffectLayer { solid { colour, key canvas [key..key] } }. The firmware draws it on that key on top of the
    /// key effects, without the RGB SDK mode (which freezes effects, the Fn layer colours and the Caps Lock indicator).
    /// Wootility never builds these, but the 80HE firmware renders them.
    /// </summary>
    public static byte[] KeyLayer(System.Drawing.Color c, int row, int col)
    {
        var pos = Proto.Concat(Proto.Varint(1, (ulong)row), Proto.Varint(2, (ulong)col));          // KeyMatrixPos {row, column}
        var canvas = Proto.Concat(Proto.Len(1, pos), Proto.Len(2, pos));                           // KeyCanvas {start, end}
        return Proto.Len(6, Proto.Concat(Proto.Varint(1, Argb(c)), Proto.Len(3, canvas)));         // solid {colour, keys}
    }

    /// <summary>
    /// Uploads a LEDBarProfile made of solid layers (bottom first) covering LED ranges [start..end], RAM only.
    /// With <paramref name="diagnostics"/> the call also waits ~300 ms and records the keyboard's replies in <see cref="LastReplies"/>.
    /// </summary>
    public bool SetLightbar(IEnumerable<(System.Drawing.Color colour, int start, int end)> layers, int profileIndex, bool diagnostics = false, bool inheritGlobal = false)
    {
        var parts = new List<byte[]> { Proto.Varint(1, 1) };                                       // isEnabled = true
        foreach (var (c, start, end) in layers) parts.Add(Proto.Len(2, BarLayer(c, start, end)));  // layers
        parts.Add(Proto.Varint(3, inheritGlobal ? 1u : 0u));                                       // inheritGlobal
        return SendProfileReport(CmdSetLedBarProfile, profileIndex, save: false, Proto.Concat(parts.ToArray()), diagnostics);
    }

    /// <summary>
    /// Uploads <paramref name="layers"/> (LEDBarEffectLayer messages, drawn in order, last on top) to a slot, RAM only.
    /// With <paramref name="keepBase"/> the slot's stored bar layers go underneath (the global ones when the slot
    /// inherits them), so the bar keeps its normal look. A slot whose bar is off (isEnabled = false) gets no base
    /// layers, so the bar stays dark apart from our own layers.
    /// </summary>
    public bool SetLedBarLayers(int profileIndex, IEnumerable<byte[]> layers, bool keepBase)
    {
        var parts = new List<byte[]> { Proto.Varint(1, 1) };                                       // isEnabled = true
        if (keepBase)
        {
            var stored = GetLedBarProfile(profileIndex) ?? throw new IOException("no reply to get LED bar profile");
            var (enabled, inherits) = BarFlags(stored);
            if (enabled && inherits)
            {
                var global = GetGlobalLedBarProfile() ?? throw new IOException("no reply to get global LED bar profile");
                parts.AddRange(Proto.Fields(global).Where(f => f.field == 1 && f.bytes != null).Select(f => Proto.Len(2, f.bytes!)));
            }
            else if (enabled)
                parts.AddRange(Proto.Fields(stored).Where(f => f.field == 2 && f.bytes != null).Select(f => Proto.Len(2, f.bytes!)));
            if (!inherits) parts.AddRange(Proto.Fields(stored).Where(f => f.field == 4 && f.wire == 0).Select(f => Proto.Varint(4, f.value)));   // Brightness
        }
        parts.AddRange(layers.Select(l => Proto.Len(2, l)));
        parts.Add(Proto.Varint(3, 0));                                                             // inheritGlobal = false
        return SendProfileReport(CmdSetLedBarProfile, profileIndex, save: false, Proto.Concat(parts.ToArray()), false);
    }

    /// <summary>(isEnabled, inheritGlobal) of a stored LEDBarProfile.</summary>
    static (bool enabled, bool inherits) BarFlags(byte[] stored)
    {
        bool inherits = false, enabled = false;
        foreach (var (field, _, _, value) in Proto.Fields(stored)) { if (field == 3) inherits = value != 0; else if (field == 1) enabled = value != 0; }
        return (enabled, inherits);
    }

    /// <summary>
    /// Puts a slot's stored LED bar profile back in RAM. The firmware keeps showing our upload when sent a stored copy
    /// with isEnabled = false, so a bar that is off gets an enabled profile with no layers (dark), and an enabled slot
    /// that inherits the global bar gets the "inherit global" reset.
    /// </summary>
    public bool RestoreLedBar(int profileIndex)
    {
        var stored = GetLedBarProfile(profileIndex);
        if (stored == null) return ResetLightbar(profileIndex);
        var (enabled, inherits) = BarFlags(stored);
        if (!enabled) return SendProfileReport(CmdSetLedBarProfile, profileIndex, save: false, Proto.Concat(Proto.Varint(1, 1), Proto.Varint(3, 0)), false);
        if (inherits) return ResetLightbar(profileIndex);
        return SendProfileReport(CmdSetLedBarProfile, profileIndex, save: false, stored, false);
    }

    /// <summary>
    /// Puts the keyboard back as Wootility left it: a reload of the active profile (the only thing that makes the
    /// firmware stop drawing key layers), then the stored light bar profile for every slot given (a reload does not
    /// clear a bar upload, so this comes last).
    /// </summary>
    public void RestoreLighting(int activeSlot, IEnumerable<int> uploadedSlots)
    {
        ActivateProfile(activeSlot);
        foreach (var s in uploadedSlots.Append(activeSlot).Distinct()) RestoreLedBar(s);
    }

    const byte CmdGetLedBarProfile = 66;        // feature, b4 = slot, b5 = namespace; reply = protobuf LEDBarProfile
    const byte CmdGetGlobalLedBarProfile = 67;  // feature; reply = protobuf GlobalLEDBarProfile

    /// <summary>Raw LEDBarProfile protobuf stored for a slot (the saved one, not what a RAM upload changed), or null.</summary>
    public byte[]? GetLedBarProfile(int profileIndex) => Body(CommandRaw(CmdGetLedBarProfile, (byte)profileIndex, 0, 0, 0));

    /// <summary>Raw GlobalLEDBarProfile protobuf, or null.</summary>
    public byte[]? GetGlobalLedBarProfile() => Body(Command(CmdGetGlobalLedBarProfile));

    const byte CmdGetRenderedColors = 74;       // feature; reply = RGB565 words: key matrix row by row, then the light bar LEDs

    /// <summary>What the LEDs show right now (the firmware's rendered frame) as raw RGB565 words, or null.</summary>
    public ushort[]? GetRenderedColors()
    {
        var body = Body(Command(CmdGetRenderedColors));
        if (body == null) return null;
        var words = new ushort[body.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(body[2 * i] | (body[2 * i + 1] << 8));
        return words;
    }

    /// <summary>RGB565 to colour, the way Wootility expands it.</summary>
    public static System.Drawing.Color FromRgb565(ushort v)
    {
        int r = (v & 0xF800) >> 8, g = (v & 0x07E0) >> 3, b = (v & 0x1F) << 3;
        return System.Drawing.Color.FromArgb(r == 0 ? 0 : r + 7, g == 0 ? 0 : g + 3, b == 0 ? 0 : b + 7);
    }

    /// <summary>Restores the light bar to "inherit global" with no layers (what Wootility sends when the bar is untouched).</summary>
    public bool ResetLightbar(int profileIndex, bool diagnostics = false)
    {
        var profile = Proto.Concat(Proto.Varint(1, 1), Proto.Varint(3, 1));
        return SendProfileReport(CmdSetLedBarProfile, profileIndex, save: false, profile, diagnostics);
    }

    /// <summary>Wootility "profile" output report: [magic lo, magic hi, cmd, options, lenLo, lenHi, body]. Options: bit0 save, bits1-5 slot, bits6-7 namespace.</summary>
    bool SendProfileReport(byte cmd, int profileIndex, bool save, byte[] body, bool collectReplies)
    {
        byte options = (byte)((save ? 1 : 0) | ((profileIndex & 31) << 1));
        var payload = new List<byte> { (byte)(_multi ? 0xD1 : 0xD0), 0xDA, cmd, options };
        if (_multi) { payload.Add((byte)(body.Length & 255)); payload.Add((byte)(body.Length >> 8)); } else payload.Add((byte)body.Length);
        payload.AddRange(body);
        int reportId, size;
        int n = payload.Count;
        if (!_multi) { if (n > 128) return false; reportId = 0; size = 128; }
        else if (n <= 32) { reportId = 1; size = 32; }
        else if (n <= 62) { reportId = 2; size = 62; }
        else if (n <= 254) { reportId = 3; size = 254; }
        else if (n <= 510) { reportId = 4; size = 510; }
        else if (n <= 1022) { reportId = 5; size = 1022; }
        else if (n <= 2046) { reportId = 6; size = 2046; }
        else return false;
        var buf = new byte[size + 1];
        buf[0] = (byte)reportId;
        payload.CopyTo(buf, 1);
        _stream.Write(buf, 0, buf.Length);
        LastSent = BitConverter.ToString(buf, 0, Math.Min(buf.Length, n + 1));
        if (!collectReplies) { LastReplies = ""; return true; }     // any reply is drained by the next feature command

        // diagnostics: collect whatever the keyboard answers within ~300 ms
        var replies = new List<string>();
        var rb = new byte[_inLen];
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < deadline)
        {
            int got; try { got = _stream.Read(rb, 0, rb.Length); } catch (TimeoutException) { continue; }
            if (got > 0) replies.Add(BitConverter.ToString(rb, 0, Math.Min(got, 16)));
        }
        LastReplies = replies.Count == 0 ? "(none)" : string.Join(" | ", replies);
        return true;
    }

    public string LastSent { get; private set; } = "";
    public string LastReplies { get; private set; } = "";

    public bool ActivateProfile(int slot0)
    {
        var r = Command(CmdActivateProfile, (byte)slot0);
        return r != null && r[3] == StatusOk;
    }

    // RGB SDK "reset all": clears per-key overrides and leaves SDK mode (frozen effects, no Fn colours, no Caps
    // indicator). This app no longer uses the SDK commands (30 set key, 33 init); kept for --reset recovery only.
    const byte CmdResetAll = 32;

    public bool ResetAll() { var r = CommandRaw(CmdResetAll, 0, 0, 0, 0); return r != null && r[3] == StatusOk; }

    /// <summary>Sends an arbitrary command with 4 raw argument bytes (bytes 4..7 of the report); returns the response as hex.</summary>
    public string Raw(byte cmd, byte b4, byte b5, byte b6, byte b7)
    {
        var r = CommandRaw(cmd, b4, b5, b6, b7);
        return r == null ? "no reply" : BitConverter.ToString(r.Take(16).ToArray());
    }

    byte[]? Command(byte cmd, byte arg = 0) => CommandRaw(cmd, arg, 0, 0, 0);

    byte[]? CommandRaw(byte cmd, byte b4, byte b5, byte b6, byte b7)
    {
        var buf = new byte[_inLen];
        for (int i = 0; i < 32; i++)                       // drain unsolicited / stale reports
        {
            try { if (_stream.Read(buf, 0, buf.Length) <= 0) break; } catch (TimeoutException) { break; }
        }
        var rep = new byte[] { (byte)(_multi ? 1 : 0), (byte)(_multi ? 0xD1 : 0xD0), 0xDA, cmd, b4, b5, b6, b7 };
        _stream.SetFeature(rep);
        var deadline = DateTime.UtcNow.AddMilliseconds(800);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            try { n = _stream.Read(buf, 0, buf.Length); } catch (TimeoutException) { continue; }
            if (n <= 0) continue;
            int off = _multi ? 1 : 0;
            if (n - off >= 5 && buf[off] == rep[1] && buf[off + 1] == 0xDA && buf[off + 2] == cmd)
                return buf.Skip(off).Take(n - off).ToArray();
        }
        return null;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Just enough protobuf wire format for the light-bar messages.</summary>
static class Proto
{
    public static byte[] Varint(int field, ulong value) => Concat(new[] { (byte)(field << 3) }, Raw(value));
    public static byte[] Len(int field, byte[] bytes) => Concat(new[] { (byte)((field << 3) | 2) }, Raw((ulong)bytes.Length), bytes);
    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] Raw(ulong v) { var l = new List<byte>(); do { byte b = (byte)(v & 0x7F); v >>= 7; l.Add((byte)(v != 0 ? b | 0x80 : b)); } while (v != 0); return l.ToArray(); }

    /// <summary>
    /// Enumerates (field, wireType, lengthDelimitedBytes, varintValue) over a message. Lengths are clamped to the buffer
    /// and truncated fixed-width fields end the enumeration, so a malformed reply can never throw.
    /// </summary>
    public static IEnumerable<(int field, int wire, byte[]? bytes, ulong value)> Fields(byte[] m)
    {
        int i = 0;
        while (i < m.Length)
        {
            ulong tag = ReadVarint(m, ref i);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: yield return (field, wire, null, ReadVarint(m, ref i)); break;
                case 1: if (i + 8 > m.Length) yield break; i += 8; yield return (field, wire, null, 0); break;
                case 2:
                {
                    int len = (int)Math.Min(ReadVarint(m, ref i), (ulong)(m.Length - i));
                    var b = new byte[len];
                    Array.Copy(m, i, b, 0, len);
                    i += len;
                    yield return (field, wire, b, 0);
                    break;
                }
                case 5: if (i + 4 > m.Length) yield break; { uint v = BitConverter.ToUInt32(m, i); i += 4; yield return (field, wire, null, v); } break;
                default: yield break;
            }
        }
    }

    static ulong ReadVarint(byte[] m, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (i < m.Length) { byte b = m[i++]; if (shift < 64) v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; }
        return v;
    }
}
