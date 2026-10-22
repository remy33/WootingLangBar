using System.Globalization;
using System.Runtime.InteropServices;

namespace WootingLangBar;

public static class LangDetect
{
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int l, t, r, b;
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    /// <summary>LANGID of the keyboard layout used by the window that currently has input focus.</summary>
    public static int CurrentLangId()
    {
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        IntPtr hwnd = IntPtr.Zero;
        if (GetGUIThreadInfo(0, ref gti))
            hwnd = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : gti.hwndActive;
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        uint tid = hwnd == IntPtr.Zero ? 0 : GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        long hkl = GetKeyboardLayout(tid).ToInt64();
        return (int)(hkl & 0xFFFF);
    }

    public static bool CapsLockOn() => (GetKeyState(0x14) & 1) != 0;

    public static string Iso639(int langId)
    {
        try { return new CultureInfo(langId).TwoLetterISOLanguageName.ToLowerInvariant(); }
        catch { return "??"; }
    }

    public static string Describe(int langId)
    {
        try { return new CultureInfo(langId).EnglishName; }
        catch { return "LANGID 0x" + langId.ToString("X4"); }
    }
}
