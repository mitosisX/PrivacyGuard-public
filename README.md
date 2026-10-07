<p align="center">
  <img src="PrivacyGuard.App/Assets/logo.png" width="72" alt="PrivacyGuard logo" />
</p>

<h1 align="center">PrivacyGuard</h1>

<p align="center">
  Live on-screen redaction for Windows. Covers wallet addresses, emails, API keys, IP addresses,
  network names and anything else you choose, in any app, as it appears and while you scroll.
</p>

---

## Why

Streamers, developers and anyone who shares their screen leak things by accident: a crypto
address in a wallet tab, an API key in a terminal, a customer's email in a dashboard, a Wi-Fi
name in a settings page. Browser extensions only protect web pages, and "streamer modes" only
protect the one app that has one.

PrivacyGuard watches the whole screen, reads it on your PC, and draws solid boxes over anything
that matches your rules, in any app.

## What it does

- **Works in any app.** Browsers, terminals, editors, chat apps, File Explorer, games in windowed mode.
- **Follows text while you scroll.** Boxes move with the text, at the text's size.
- **Remembers what it has found.** A secret that shows up again where it was before (switching
  back to a tab, re-opening a panel) is covered about two frames after it appears, before it is read again.
- **Your rules.**
  - Text, with optional fuzzy matching that tolerates OCR mistakes (`0` for `O`, `l` for `I`).
  - Regular expressions.
  - Built-in detectors: email, IPv4, IPv6, phone, credit card, URL, API keys and tokens, crypto addresses.
  - App or window rules: cover a whole window by its title or app name (for example MetaMask).
- **Test lab.** Paste text and see exactly what each rule would hide.
- **Found on this PC.** Suggests rules for things that are easy to leak by accident: your username, computer name, Wi-Fi network and local IP address.
- **Solid boxes in a colour you pick.** Never blur or pixelation, which can be reversed.
- **Light.** About 2% CPU when idle and around 60 MB of extra memory on a typical PC.

## Privacy

- Everything runs locally with Windows' built-in text recognition. Nothing is uploaded, and there is no telemetry.
- Rules are encrypted for your Windows account (DPAPI) in `%APPDATA%\PrivacyGuard`.
- The log never contains screen text. Items it has already found are kept in memory only, never on disk.

## Limits (please read)

PrivacyGuard **reduces** the risk of showing something sensitive. It does not guarantee it.

- **Brand-new text is visible for a moment.** Windows shows a frame before any app can see it,
  so text that has never been seen before stays uncovered for a few frames (typically 50 to 70 ms) while it is read.
- **Hard scrolling.** Text that has just scrolled into view can show for a few frames before it is covered.
- **Taskbar panels cannot be covered.** Quick Settings, Start and notifications draw above every app.
  If your Wi-Fi name must stay off stream, record a single window in OBS instead of the whole display.
- **Also not covered.** Admin windows, the lock screen and full-screen exclusive games.
- **Text recognition is not perfect.** Very small text, unusual fonts and text cut off at a window edge can be missed.
  Text inside images and video is read too, but less reliably.
- **Windows 11.** Built and tested on Windows 11. Windows 10 (version 2004 or later) may work but is untested. It needs an OCR language installed (English is standard).

## Using it with OBS

Use **Display Capture** (the whole screen) so the boxes are recorded. Window Capture records an
app on its own, without the boxes.

## Build from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/mitosisX/PrivacyGuard-public.git
cd PrivacyGuard-public
dotnet build -c Release
dotnet test
.\PrivacyGuard.App\bin\Release\net10.0-windows10.0.22621.0\PrivacyGuard.App.exe
```

Visual Studio 2022 or later with the ".NET desktop development" workload works too. Open `PrivacyGuard.App.slnx`.

## How it works

1. **Capture.** Each visible window is captured on its own with Windows Graphics Capture, so the
   engine always sees the real text under the boxes. Frames are shrunk and read on the GPU.
2. **Change and scroll detection.** Each frame is compared with the last one. Scrolls are measured
   to the pixel, so existing boxes simply move, and only newly revealed content needs reading.
3. **Reading.** New content is read with Windows OCR (`Windows.Media.Ocr`) and matched against your rules.
4. **Tracking.** Each covered item is followed by template matching between frames, with a short
   look-ahead so boxes stay on moving text.
5. **Drawing.** One click-through, always-on-top window shaped exactly like the boxes. Screen
   recorders that capture the whole display record the boxes like any other window.

| Project | Contents |
|---|---|
| `PrivacyGuard.Core` | Rules, matching, encrypted storage. No Windows UI code. |
| `PrivacyGuard.Engine` | Capture, change detection, OCR, tracking and the overlay. |
| `PrivacyGuard.App` | The WPF app (WPF UI, tray icon). |
| `PrivacyGuard.Tests` | Unit tests (xUnit). |

### Debug switches

| Environment variable | Effect |
|---|---|
| `PRIVACYGUARD_DEBUG_STATS=1` | Logs timings and counts (never text) to `%APPDATA%\PrivacyGuard\engine.log`. |
| `PRIVACYGUARD_PRESENT_LAG_MS` | Overrides the display lag used to place moving boxes (default 30). |

## Contributing

Issues and pull requests are welcome. Reports of missed text are most useful with the app name,
font size and Windows display scaling. Please never attach screenshots that contain real secrets.

## License

[Apache License 2.0](LICENSE). Provided as is, without warranty of any kind: see the license for details.

© 2026 Omega Msiska · [github.com/mitosisx](https://github.com/mitosisx)
