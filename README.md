# WootingLangBar

Windows tray app that shows the current Windows input language on a Wooting 80HE **light bar** or on **keys**
(for example Esc), on top of the live onboard profile. Key effects, the Fn layer colours and the Caps Lock
indicator keep working. Nothing is written to the keyboard's flash; every change is a RAM-only update, the same
path Wootility uses for its live preview.

## Download

1. Download `WootingLangBar-x.y.z-win-x64.zip` from the [latest release](https://github.com/remy33/WootingLangBar/releases/latest).
2. Unzip it into a folder of your choice (for example `C:\Tools\WootingLangBar`) and run `WootingLangBar.exe`.
   Nothing else to install: .NET is included in the exe.
3. Optional, start at logon: press Win+R, type `shell:startup`, and put a shortcut to `WootingLangBar.exe` there.

Settings live in `config.json` next to the exe (tray menu: Edit config.json, then Reload config).
The exe is not code-signed, so Windows SmartScreen may say it is unrecognized: click **More info**, then **Run anyway**.
Every release is built from this repository's source by [GitHub Actions](.github/workflows/release.yml).

## How it works

Everything is one `profiles.LEDBarProfile` protobuf uploaded with Wootility's "set LED bar profile" output report,
save flag off: the slot's own bar layers (or the global ones it inherits) underneath, the app's solid layers on top.

- **Light bar (`"mode": "bar"`)**: a solid layer over the whole bar, optionally a Caps Lock segment.
- **Keys (`"mode": "key"`)**: a solid layer per key, drawn on a **key canvas** instead of the bar
  (`RGBDataEffectSolid.keys`). Wootility never builds these, but the 80HE firmware renders them on top of the key
  effects, so animations, Fn colours and the Caps Lock indicator are untouched. (The RGB SDK per-key commands, used
  by earlier versions, put the keyboard in SDK mode: effects freeze, Fn colours and the Caps indicator stop.)
- **Both (`"mode": "both"`)**: the light bar and the keys together, in one upload.

Every 100 ms the app reads the keyboard layout of the focused window (`GetKeyboardLayout`). Every 2 s it checks
the active onboard profile; if you switched profiles (Mode key) it re-applies on the new one. Profile switching is
never blocked or undone.

Firmware quirks the app works around: a key layer stays drawn until the profile is reloaded, even after an upload
without it (so dropping a key colour reloads the profile first); a reload does not clear a bar upload; and re-sending
a stored "inherit global" profile (isEnabled = false) is ignored, so the bar is reset with isEnabled = true.
Exit, Pause and Reload therefore reload the active profile, then put back the stored bar profile of every slot
the app touched. Logoff/shutdown and `WootingLangBar.exe --exit` (used by the install and uninstall scripts) go
through the same path, so only a hard kill can leave the app's colours on the keyboard.

Caps Lock: the keyboard's own Caps Lock indicator keeps working. Optionally, `"barLeds": [8, 9]` turns the bottom two
bar LEDs white while Caps Lock is on (bar/both mode only; key mode never draws on the bar), and `"keys"` paints keys
with the Caps colour.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Builds to `dist\`, starts the tray icon, adds a Startup-folder shortcut (runs at logon). Building needs the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); the published exe only needs the .NET 10 Desktop Runtime.
Running `install.ps1` again rebuilds and restarts the app and keeps your edited `dist\config.json`.
`uninstall.ps1` restores the keyboard, stops the app and removes the shortcut.

Releases: every push to `main` that changes the app builds a new numbered release (`1.0.<run number>`, see
`VERSION_BASE` in the workflow); pull requests are only built.

## config.json

```jsonc
{
    "mode": "key", // "bar" | "key" | "both"
    "colors": { "he": "#2979FF", "en": "#E81B23", "other": "#D500F9" },
    "keys": [[0, 0]], // key mode: [row, column], row 0 = F-row, column 0 = leftmost (Esc)
    "capsLock": { "barLeds": [8, 9], "keys": [], "color": "#FFFFFF" },
    "pollMs": 100,
    "resyncMs": 2000,
}
```

Language keys: `he`, `en`, `other`, any 2-letter ISO code (`ru`, `ar`), or a hex LANGID (`0419`). Any number of
languages. Light bar LEDs are numbered 0 (top) to 9 (bottom).

## Tray menu

Status (language, Caps), Pause/Resume, Edit config.json, Reload config, Open log, Exit.

## Command line helpers

```powershell
.\dist\WootingLangBar.exe --key ROW COL [RRGGBB] [SECONDS]  # draw a key (default red, 5 s), log what it shows (find coordinates)
.\dist\WootingLangBar.exe --bar RRGGBB [--caps] [--inherit] [--slot N]   # paint the bar now (RAM) and read back what it shows
.\dist\WootingLangBar.exe --barreset                # hand the bar back to the global bar ("inherit global")
.\dist\WootingLangBar.exe --barread               # read the bar's current colours
.\dist\WootingLangBar.exe --bardump               # stored LED bar profile of the active slot and the global one (hex)
.\dist\WootingLangBar.exe --frame                 # colours every key and bar LED shows right now
.\dist\WootingLangBar.exe --reset                 # RGB SDK reset-all (leaves SDK mode, if an older version left it on)
.\dist\WootingLangBar.exe --get / --set N         # active onboard profile / activate slot N (RAM)
.\dist\WootingLangBar.exe --raw CMD B4 B5 B6 B7   # any feature command
.\dist\WootingLangBar.exe --exit                  # ask the running tray app to restore the keyboard and quit
```

Add `--quiet` to skip the message box; results always go to `%LOCALAPPDATA%\WootingLangBar\log.txt`.
Invalid arguments and HID errors are reported the same way instead of crashing.
These helpers talk to the keyboard directly, so Pause the tray app first (tray menu) or it may draw over their
result, and Resume it afterwards.

`--raw` sends any feature command to the keyboard unchecked. Commands outside the table below can change
keyboard state in ways this app does not know how to undo, so use it only when you know what the command does.

## Protocol notes (reverse-engineered from the Wootility web bundle; config HID interface, usage page 0xFF55)

Feature command, 8 bytes: `01 D1 DA <cmd> <b4> <b5> <b6> <b7>`. Reply: `D1 DA <cmd> <status> <lenLo> <lenHi> <body>`,
status `0x88` = ok, `0xFF` = error, `0x66` = unknown, `0xAA` = unsupported.

| cmd               | kind    | meaning                                                                                                                                 |
| ----------------- | ------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| 11                | feature | get active profile (body[0] = index)                                                                                                    |
| 23                | feature | activate profile, b4 = slot (RAM reload of the stored profile)                                                                          |
| 30 / 31 / 32 / 33 | feature | RGB SDK set key, reset key, reset all, init. Set key / init switch the keyboard to SDK mode; this app only uses reset all (`--reset`)   |
| 66                | feature | get stored LED bar profile, b4 = slot, b5 = namespace (reply = protobuf `LEDBarProfile`)                                                |
| 67                | feature | get global LED bar profile (reply = protobuf `GlobalLEDBarProfile { 1: repeated LEDBarEffectLayer layers }`)                            |
| 68                | feature | get light bar colours (reply body = protobuf `LEDBarReport { repeated RGB24 colors }`)                                                  |
| 74                | feature | get rendered colours: RGB565 words, key matrix row by row (6 × 17 on the 80HE), then the 10 bar LEDs                                    |
| 32                | output  | set LED bar profile (same number as reset-all, but a different report type): `D1 DA 20 <opts> <lenLo> <lenHi> <protobuf LEDBarProfile>` |

Output report: report id 1 for payloads up to 32 bytes (2 = 62, 3 = 254, 4 = 510, 5 = 1022, 6 = 2046), zero padded.
`opts` bit 0 = save to flash (never set here), bits 1-5 = profile slot, bits 6-7 = namespace.

`LEDBarProfile { 1: isEnabled bool; 2: repeated LEDBarEffectLayer layers; 3: inheritGlobal bool; 4: brightness }`,
`LEDBarEffectLayer { 1: condition; 3: profile indicator; 6: RGBDataEffectSolid solid; ... }` (later layers draw on top),
`RGBDataEffectSolid { 1: colour uint32 (0xAARRGGBB, alpha must be 0xFF); 2: LightbarCanvas { 1: start; 2: end } | 3: KeyCanvas { 1: start; 2: end } }`,
`KeyCanvas` ends are `KeyMatrixPos { 1: row; 2: column }`.

Credits: [wooting-rgb-sdk](https://github.com/WootingKb/wooting-rgb-sdk),
[wooting-desktop-sync](https://github.com/joshingaround/wooting-desktop-sync),
[wootswitch](https://github.com/clemenscodes/wootswitch).
