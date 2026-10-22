using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WootingLangBar;

static class Program
{
    public const string SingleInstanceName = "WootingLangBar.SingleInstance";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) { Cli(args); return; }
        using var mutex = new Mutex(true, SingleInstanceName, out bool first);
        if (!first) return;
        Application.Run(new TrayApp());
    }

    /// <summary>
    /// WootingLangBar --get | --set N | --key ROW COL | --reset | --raw CMD B4 B5 B6 B7   [--quiet]
    /// Result goes to the log and (without --quiet) a message box.
    /// </summary>
    static void Cli(string[] args)
    {
        string result;
        try { result = RunCli(args); }
        catch (Exception e) { result = $"error: {e.Message}"; }
        try { File.AppendAllText(TrayApp.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} cli {string.Join(' ', args)}: {result}\r\n"); } catch { }
        if (Array.IndexOf(args, "--quiet") < 0) MessageBox.Show(result, "WootingLangBar");
    }

    static string ColorHexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The colour a key shows right now, from the firmware's rendered frame (6 key rows, then the light bar).</summary>
    static string RenderedKey(WootingHid kb, int row, int col)
    {
        var w = kb.GetRenderedColors();
        int cols = w == null ? 0 : (w.Length - WootingHid.LightbarLeds) / 6;
        return w == null || row >= 6 || col >= cols ? "unreadable" : ColorHexOf(WootingHid.FromRgb565(w[row * cols + col]));
    }

    static string RunCli(string[] args)
    {
        if (args[0] == "--exit")
        {
            // Ask the running tray instance to exit cleanly (it restores the keyboard first), then wait for it to go.
            if (!EventWaitHandle.TryOpenExisting(TrayApp.ExitEventName, out var ev)) return "WootingLangBar is not running";
            using (ev) ev.Set();
            for (int i = 0; i < 150; i++)
            {
                Thread.Sleep(100);
                if (!Mutex.TryOpenExisting(SingleInstanceName, out var m)) return "exited";
                m.Dispose();
            }
            return "exit requested, but the app is still running";
        }

        string[] commands = { "--get", "--set", "--key", "--bar", "--bardump", "--frame", "--barreset", "--barread", "--reset", "--raw", "--quiet" };
        if (Array.IndexOf(commands, args[0]) < 0) return $"unknown command \"{args[0]}\" (see \"Command line helpers\" in README.md)";

        using var kb = WootingHid.TryOpen(_ => { });
        if (kb == null) return "Wooting keyboard not found";

        if (args[0] == "--set")
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int slot) || slot < 1 || slot > 32) return "usage: --set N   (slot 1..32)";
            bool ok = kb.ActivateProfile(slot - 1);
            Thread.Sleep(200);
            var now = kb.GetActiveProfile();
            return $"ActivateProfile({slot}): {(ok ? "ok" : "rejected")}; active now: slot {now + 1}";
        }
        if (args[0] == "--key")
        {
            // --key ROW COL [RRGGBB] [SECONDS]: draws the colour on that key (same method as key mode) and logs what the key
            // really shows 4x a second, then reloads the profile. Use it to find a key's coordinates.
            if (args.Length < 3 || !int.TryParse(args[1], out int row) || !int.TryParse(args[2], out int col)) return "usage: --key ROW COL [RRGGBB] [SECONDS]";
            if (!Config.KeyInRange(row, col)) return $"--key: row must be 0..7 and column 0..31, got [{row},{col}]";
            var c = args.Length > 3 && !args[3].StartsWith("--") ? Config.ParseColor(args[3]) : Color.Red;
            int secs = args.Length > 4 && int.TryParse(args[4], out int s) ? Math.Clamp(s, 1, 300) : 5;
            int cur = kb.GetActiveProfile() ?? 0;
            bool ok = kb.SetLedBarLayers(cur, new[] { WootingHid.KeyLayer(c, row, col) }, keepBase: true);
            string? last = null;
            var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < secs)
            {
                Thread.Sleep(250);
                string now = RenderedKey(kb, row, col) + ", Caps key " + RenderedKey(kb, 3, 0) + (LangDetect.CapsLockOn() ? " (Caps on)" : "");
                if (now == last) continue;
                try { File.AppendAllText(TrayApp.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.f} key +{(DateTime.Now - t0).TotalSeconds:0.0}s: [{row},{col}] shows {now}\r\n"); } catch { }
                last = now;
            }
            kb.RestoreLighting(cur, Array.Empty<int>());
            Thread.Sleep(300);
            return $"key [{row},{col}] {ColorHexOf(c)} for {secs} s on slot {cur + 1}: {(ok ? "sent" : "failed")}; key after restore: {RenderedKey(kb, row, col)}";
        }
        if (args[0] == "--bar")
        {
            // --bar RRGGBB : light bar solid colour in RAM (Wootility preview path), then read back what the bar shows
            if (args.Length < 2 || args[1].StartsWith("--")) return "usage: --bar RRGGBB [--caps] [--inherit] [--slot N]";
            var c = Config.ParseColor(args[1]);
            int si = Array.IndexOf(args, "--slot");
            int cur;
            if (si < 0) cur = kb.GetActiveProfile() ?? 0;
            else if (si + 1 < args.Length && int.TryParse(args[si + 1], out int n) && n >= 1 && n <= 32) cur = n - 1;
            else return "--slot: slot must be 1..32";
            var layers = new List<(Color, int, int)> { (c, 0, WootingHid.LightbarLeds - 1) };
            if (Array.IndexOf(args, "--caps") >= 0) layers.Add((Color.White, 8, 9));
            bool ok = kb.SetLightbar(layers, cur, diagnostics: true, inheritGlobal: Array.IndexOf(args, "--inherit") >= 0);
            Thread.Sleep(400);
            var cols = kb.GetLightbarColors();
            return $"SetLightbar({args[1]}) slot {cur + 1}: {(ok ? "sent" : "failed")} [{kb.LastSent}] replies: {kb.LastReplies}; bar reports: "
                 + (cols == null ? "no reply" : string.Join(" ", cols.Select(x => $"#{x.R:X2}{x.G:X2}{x.B:X2}")));
        }
        if (args[0] == "--bardump")
        {
            int cur = kb.GetActiveProfile() ?? 0;
            var p = kb.GetLedBarProfile(cur);
            var g = kb.GetGlobalLedBarProfile();
            return $"slot {cur + 1} LEDBarProfile: {(p == null ? "no reply" : BitConverter.ToString(p))}\r\nglobal: {(g == null ? "no reply" : BitConverter.ToString(g))}";
        }
        if (args[0] == "--frame")
        {
            var w = kb.GetRenderedColors();
            return w == null ? "no reply" : $"{w.Length} words: " + string.Join(" ", w.Select(x => { var c = WootingHid.FromRgb565(x); return $"{c.R:X2}{c.G:X2}{c.B:X2}"; }));
        }
        if (args[0] == "--barreset")
        {
            int cur = kb.GetActiveProfile() ?? 0;
            return $"ResetLightbar slot {cur + 1}: {(kb.ResetLightbar(cur, diagnostics: true) ? "sent" : "failed")}";
        }
        if (args[0] == "--barread")
        {
            var cols = kb.GetLightbarColors();
            return "bar reports: " + (cols == null ? "no reply" : string.Join(" ", cols.Select(x => $"#{x.R:X2}{x.G:X2}{x.B:X2}")));
        }
        if (args[0] == "--reset")
            return "ResetAll: " + (kb.ResetAll() ? "ok" : "rejected");
        if (args[0] == "--raw")
        {
            if (args.Length < 6) return "usage: --raw CMD B4 B5 B6 B7   (decimal or 0x hex)";
            byte P(string s) => Convert.ToByte(s, s.StartsWith("0x") ? 16 : 10);
            return kb.Raw(P(args[1]), P(args[2]), P(args[3]), P(args[4]), P(args[5]));
        }

        var active = kb.GetActiveProfile();     // --get
        return active == null ? "no reply from keyboard" : $"Active profile: slot {active + 1}";
    }
}

sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    readonly NotifyIcon _icon = new();
    readonly ToolStripMenuItem _pause = new("Pause");
    readonly ToolStripMenuItem _status = new("…") { Enabled = false };
    readonly SynchronizationContext _ui;
    readonly CancellationTokenSource _cts = new();
    public static string LogPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WootingLangBar");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "log.txt");
        }
    }
    readonly string _logPath = LogPath;
    volatile bool _paused;
    volatile bool _reload;
    readonly Thread _loop;
    (string text, string icon, Color color)? _lastStatus;     // loop thread only

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        _pause.Click += (_, _) => { _paused = !_paused; _pause.Text = _paused ? "Resume" : "Pause"; };
        var openCfg = new ToolStripMenuItem("Edit config.json");
        openCfg.Click += (_, _) => OpenFile(Config.FilePath);
        var reload = new ToolStripMenuItem("Reload config");
        reload.Click += (_, _) => _reload = true;
        var openLog = new ToolStripMenuItem("Open log");
        openLog.Click += (_, _) => OpenFile(_logPath);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Exit();
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), _pause, openCfg, reload, openLog, new ToolStripSeparator(), exit });
        _icon.ContextMenuStrip = menu;
        _icon.Icon = MakeIcon("--", Color.Gray);
        _icon.Text = "Wooting LangBar";
        _icon.Visible = true;
        _ui = new WindowsFormsSynchronizationContext();

        // "WootingLangBar.exe --exit" (install/uninstall scripts) sets this event; logoff/shutdown also exit cleanly,
        // so the keyboard is restored instead of being left with our colours on it.
        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => _ui.Post(_ => Exit(), null), null, Timeout.Infinite, true);
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Exit();

        _loop = new Thread(Loop) { IsBackground = true, Name = "wooting-loop" };
        _loop.Start();
    }

    public const string ExitEventName = "WootingLangBar.ExitRequest";
    readonly EventWaitHandle _exitEvent;
    bool _exiting;                 // UI thread only

    /// <summary>Stops the loop (which restores the keyboard), then leaves the message loop. Safe to call more than once.</summary>
    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _cts.Cancel();
        _loop.Join(10000);
        _icon.Visible = false;
        ExitThread();
    }

    /// <summary>Opens a file in its associated app, or in Notepad when none is set (e.g. no app for .json).</summary>
    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; } catch { }
        try { Process.Start("notepad.exe", $"\"{path}\""); }
        catch (Exception e) { MessageBox.Show($"Cannot open {path}: {e.Message}", "WootingLangBar"); }
    }

    void Log(string msg)
    {
        try
        {
            if (new FileInfo(_logPath) is { Exists: true, Length: > 512 * 1024 }) File.WriteAllText(_logPath, "");
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }

    /// <summary>Updates tray text and icon; no-op when nothing changed, so a status repeated every poll costs nothing.</summary>
    void SetStatus(string text, string iconText, Color color)
    {
        if (_lastStatus == (text, iconText, color)) return;
        _lastStatus = (text, iconText, color);
        try
        {
            _ui.Post(_ =>
            {
                try
                {
                    _status.Text = text;
                    var tip = "Wooting LangBar: " + text;
                    _icon.Text = tip.Length > 63 ? tip[..63] : tip;
                    _icon.Icon = MakeIcon(iconText, color);     // cached icons: never disposed
                }
                catch (Exception e) { Log("status error: " + e.Message); }
            }, null);
        }
        catch { }
    }

    // Icons are built once per (text, colour) and kept for the life of the process. UI thread only.
    static readonly Dictionary<(string, Color), Icon> IconCache = new();

    static Icon MakeIcon(string text, Color color)
    {
        if (IconCache.TryGetValue((text, color), out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 1, 1, 30, 30);
            using var f = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            var fg = color.GetBrightness() > 0.6f ? Brushes.Black : Brushes.White;
            g.DrawString(text, f, fg, (32 - sz.Width) / 2, (32 - sz.Height) / 2);
        }
        // Icon.FromHandle does not own the HICON: clone into an owned Icon and destroy the original handle.
        IntPtr h = bmp.GetHicon();
        Icon icon;
        try { using var tmp = Icon.FromHandle(h); icon = (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
        IconCache[(text, color)] = icon;
        return icon;
    }

    Config LoadConfig()
    {
        try
        {
            var c = Config.Load();
            foreach (var w in c.Warnings) Log("config warning: " + w);
            Log($"config: mode={c.Mode} keys={string.Join(" ", c.Keys.Select(k => $"[{k[0]},{k[1]}]"))} colors={string.Join(",", c.Colors.Select(kv => kv.Key + "=" + kv.Value))}");
            _configError = false;
            return c;
        }
        catch (Exception e) { Log("config error: " + e.Message); SetStatus("config.json error", "!", Color.Red); _configError = true; return new Config(); }
    }

    bool _configError;             // loop thread only: config.json failed to load, so the status keeps saying so

    /// <summary>Sleeps unless Exit is requested; returns true when cancelled.</summary>
    bool Wait(int ms) => _cts.Token.WaitHandle.WaitOne(ms);

    void Loop()
    {
        var ct = _cts.Token;
        var cfg = LoadConfig();
        Log("started");
        WootingHid? kb = null;
        int failures = 0;
        var ind = new IndicatorState();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reload)
                {
                    if (kb != null && ind.Applied) Release(kb, ind);     // if this throws, _reload stays set and is retried
                    _reload = false;
                    cfg = LoadConfig();
                    ind.Lang = -1;                                      // re-apply with the new config
                }
                if (_paused)
                {
                    if (kb != null && ind.Applied) { Release(kb, ind); Log("paused: keyboard restored"); }
                    SetStatus("paused", "||", Color.Gray); Wait(300); continue;
                }
                if (kb == null)
                {
                    kb = WootingHid.TryOpen(Log);
                    if (kb == null) { SetStatus("keyboard not found", "--", Color.Gray); Wait(2000); continue; }
                    Log("opened " + kb.DevicePath);
                }

                IndicatorStep(kb, cfg, ind);
                failures = 0;
                Wait(cfg.PollMs);
            }
            catch (Exception e)
            {
                failures++;
                Log($"error ({failures}): {e.Message}");
                SetStatus("keyboard busy / disconnected", "!", Color.OrangeRed);
                if (failures >= 3) { try { kb?.Dispose(); } catch { } kb = null; failures = 0; }
                // Keep what may still be on the keyboard, so Pause, Reload and Exit still restore it; re-apply everything.
                ind.Lang = -1;
                Wait(2000);
            }
        }
        if (kb != null)
        {
            try { Release(kb, ind); Log("exit: keyboard restored"); }
            catch (Exception e) { Log("restore on exit failed: " + e.Message); }
        }
        kb?.Dispose();
        Log("stopped");
    }

    // ---------- bar / key mode: paint the light bar and/or keys on top of the live profile (RAM only) ----------

    sealed class IndicatorState
    {
        public int Lang = -1;
        public bool Caps;
        public bool Applied;          // something of ours is currently shown on the keyboard
        public int Slot = -1;         // active onboard profile last seen (a change means the keyboard reloaded its stored profile)
        public DateTime LastSync = DateTime.MinValue;
        public HashSet<(int, int)> KeysDrawn = new();   // keys our layers paint on the active slot
        public HashSet<int> Uploaded = new();          // slots that got an upload since the last restore
    }

    /// <summary>
    /// Everything (bar colour, key colours, Caps Lock segment) goes to the keyboard as one LED bar profile upload: the
    /// slot's own bar layers underneath, ours on top. Key colours are solid layers on a key canvas, so the firmware keeps
    /// running the key effects, the Fn layer colours and its Caps Lock indicator (the RGB SDK per-key commands froze all three).
    /// </summary>
    void IndicatorStep(WootingHid kb, Config cfg, IndicatorState st)
    {
        int lang = LangDetect.CurrentLangId();
        bool capsOn = (cfg.CapsOnBar || cfg.CapsOnKeys) && LangDetect.CapsLockOn();
        bool changed = lang != st.Lang || capsOn != st.Caps;
        bool resync = DateTime.UtcNow - st.LastSync > TimeSpan.FromMilliseconds(cfg.ResyncMs);
        if (!changed && !resync && st.Applied) return;

        int slot = kb.GetActiveProfile() ?? st.Slot;
        bool reloaded = slot != st.Slot && st.Slot != -1;      // Mode key / Fn+N: RAM state was replaced by the stored profile
        if (reloaded) { st.KeysDrawn.Clear(); Log($"profile switched to slot {slot + 1}, re-applying"); }
        st.Slot = slot;
        // A resync only re-uploads when something changed or the keyboard reloaded its profile; otherwise it just checked the slot.
        if (changed || reloaded || !st.Applied)
        {
            var color = cfg.ColorFor(lang);
            var capsColor = cfg.CapsLock == null ? Color.White : Config.ParseColor(cfg.CapsLock.Color);

            var layers = new List<byte[]>();
            if (cfg.BarMode && color.HasValue) layers.Add(WootingHid.BarLayer(color.Value, 0, WootingHid.LightbarLeds - 1));
            if (capsOn && cfg.CapsOnBar) layers.Add(WootingHid.BarLayer(capsColor, cfg.CapsLock!.BarLeds.Min(), cfg.CapsLock.BarLeds.Max()));
            var keys = new Dictionary<(int, int), Color>();
            if (cfg.KeyMode && color.HasValue) foreach (var k in cfg.Keys) keys[(k[0], k[1])] = color.Value;
            if (capsOn && cfg.CapsOnKeys) foreach (var k in cfg.CapsLock!.Keys) keys[(k[0], k[1])] = capsColor;
            layers.AddRange(keys.Select(kv => WootingHid.KeyLayer(kv.Value, kv.Key.Item1, kv.Key.Item2)));

            int s = Math.Max(slot, 0);
            // The firmware keeps drawing a key layer until the profile is reloaded, even after an upload without it.
            if (st.KeysDrawn.Any(k => !keys.ContainsKey(k))) { kb.ActivateProfile(s); st.KeysDrawn.Clear(); }
            if (layers.Count > 0)
            {
                if (!kb.SetLedBarLayers(s, layers, keepBase: true)) throw new IOException("LED profile upload failed");
                st.Uploaded.Add(s);
            }
            else if (st.Uploaded.Contains(s) && !kb.RestoreLedBar(s)) throw new IOException("light bar restore failed");
            st.KeysDrawn = keys.Keys.ToHashSet();

            if (changed)
            {
                string desc = LangDetect.Describe(lang);
                Log($"lang {desc} (0x{lang:X4}) caps={capsOn} -> {(color.HasValue ? ColorHex(color.Value) : "profile lighting")} on {(cfg.BarMode ? "bar" : "")}{(cfg.BarMode && cfg.KeyMode ? "+" : "")}{(cfg.KeyMode ? "keys" : "")}");
                SetStatus($"{desc}{(capsOn ? " + CAPS" : "")}{(_configError ? " (config.json error)" : "")}", LangDetect.Iso639(lang).ToUpper(), color ?? Color.DimGray);
            }
        }
        st.Lang = lang; st.Caps = capsOn; st.Applied = true; st.LastSync = DateTime.UtcNow;
    }

    /// <summary>
    /// Puts the keyboard back exactly as Wootility left it: profile reload, then the stored light bar profile in every
    /// slot we uploaded to (and the active one, in case state was lost after an error).
    /// </summary>
    static void Release(WootingHid kb, IndicatorState st)
    {
        kb.RestoreLighting(kb.GetActiveProfile() ?? Math.Max(st.Slot, 0), st.Uploaded);
        st.Uploaded.Clear(); st.KeysDrawn.Clear(); st.Applied = false; st.Lang = -1;
    }

    static string ColorHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
