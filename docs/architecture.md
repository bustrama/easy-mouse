# Easy Mouse - Architecture

Status: proposal, approved in design discussion. Nothing is implemented yet.

Easy Mouse is an open-source (MIT) Windows app that turns any phone browser
(Android or iPhone, nothing to install) into a remote for the PC, over the
local network only. No cloud, no relay, no accounts, no telemetry.

## Flow

1. The user starts Easy Mouse (desktop or Start-menu icon, or automatically at Windows sign-in).
2. A small window shows a QR code.
3. The phone scans it and opens the web app.
4. The PC asks "Allow this device?". After Allow, the device is remembered.
5. Later visits reconnect with no QR and no approval.

## Decisions

| Area | Decision |
|---|---|
| Name / licence | Easy Mouse / MIT |
| Server | C# on .NET 10 LTS, WPF window + tray icon, Kestrel hosted in-process |
| Web client | Preact + TypeScript + Vite, plain CSS, embedded in the exe, no CDN |
| Transport | Plain HTTP + WebSocket, app-layer authentication and AEAD encryption |
| PWA install | Not in v1. Needs trusted HTTPS (see "Later: secure mode"). A bookmark / home-screen shortcut works but is not a feature we build for |
| Origin | `http://<pcname>.local:<fixed port>`, fallback to the IP address |
| Extensibility | Declarative JSON remotes only. No plugin SDK, no registry, no trust tiers in v1 |
| Media | Windows SMTC as generic layer, media virtual keys as fallback |
| Installer | Inno Setup, per-machine, installed as administrator; adds the firewall rule |
| Autostart | Setting (and installer checkbox). Starts minimised to tray |
| Elevated windows and UAC | **Not in 0.1.** Planned for phase 2: a separate small SYSTEM broker service behind a setting with a clear warning. Settings shows the option disabled ("Not available in this version") |
| Controllers | One controlling device at a time |
| Languages | Hebrew and English in every UI, with RTL support |
| Platforms | Windows 11 supported. Windows 10 is untested and documented as such |
| Out of v1 | Lock-screen unlock / typing the Windows password |

## Components

```
Phone browser (Preact)                    Windows PC
+-------------------------+  HTTP GET   +--------------------------------------+
| Touchpad / Keyboard     |<----------->| Easy Mouse.App  (normal user rights) |
| Media / JSON remotes    |  WebSocket  |  Kestrel: static web app             |
| Remote editor           |<===========>|  WS hub: auth, AEAD, rate limit      |
| device secret (storage) | binary+AEAD |  Input (SendInput) | Media (SMTC)    |
+-------------------------+             |  Action runner (remotes)             |
        ^  QR (optical)                 |  WPF: QR window, tray, approve,      |
        +-------------------------------|       devices, settings              |
                                        +------------------+-------------------+
                                                           | named pipe, ACL'd
                                        +------------------v-------------------+
                                        | Easy Mouse.Broker (SYSTEM service)   |
                                        |  PLANNED (phase 2, not in 0.1):      |
                                        |  admin windows, UAC. No network.     |
                                        +--------------------------------------+
```

The network-facing process never runs elevated. The broker is not built in 0.1; when it exists, only it will run elevated, it
listens on no socket, and it accepts only the App over an ACL-protected pipe.

## Pairing and reconnect

- QR holds `http://host:port/#k=<128-bit one-time secret>`, valid 2 minutes. The fragment never reaches the wire or logs; the page removes it with `history.replaceState`.
- The phone proves knowledge of `k`; the PC shows device name, browser and a 4-digit code that also appears on the phone. The user clicks Allow.
- The PC issues a 256-bit per-device secret, stored with DPAPI. Each connection derives a session key by nonce challenge-response; frames are AEAD with counter nonces (replay protection).
- Browser storage is per origin, so the web app prefers the stable `<pcname>.local` origin. If the IP changes and `.local` does not resolve, re-pairing costs one scan and one click.
- A fixed port keeps the origin and firewall rule stable.

## Network problems explained in plain language

Wi-Fi "client isolation" and guest networks cannot be detected reliably: the page
simply never loads on the phone. The PC uses heuristics instead. If the QR window
has been open for a while with no request from the LAN, it shows a plain message:
"Your phone cannot reach this computer. This usually happens on guest Wi-Fi, or
when the phone is on mobile data." It also detects a Public network profile, VPN
adapters and several network cards, each with a one-line fix.

## Remotes (declarative)

A remote is a JSON layout of buttons, sliders, labels and pads mapped to actions.

```json
{ "schema": 1, "id": "stremio", "name": "Stremio",
  "target": { "process": "stremio*.exe" },
  "layout": { "cols": 4, "items": [
    { "type": "button", "icon": "play", "action": { "do": "smtc", "cmd": "toggle" } },
    { "type": "button", "label": "Fullscreen",
      "action": [ { "do": "focus" }, { "do": "key", "keys": "F" } ] },
    { "type": "slider", "bind": "volume.app", "action": { "do": "volume", "scope": "app" } },
    { "type": "label", "bind": "smtc.title" } ] } }
```

Actions: `key`, `text`, `media`, `mouse`, `smtc`, `volume`, `focus`, `launch`,
`openUrl`, `http`, `delay`. There is no `shell` action. Classes and approval
rules are in `security.md`. A phone-side editor (phase 3) is a form over this schema.
Plex and Stremio start as remotes (SMTC plus key shortcuts, Plex local HTTP API).

## Media (SMTC)

`GlobalSystemMediaTransportControlsSessionManager` gives sessions, title, artist,
thumbnail, status, timeline and play/pause/next/seek for apps that register
(browsers do; Plex, Stremio and VLC coverage must be verified). Position is a
snapshot to extrapolate. Volume, subtitles and fullscreen are not in SMTC: use
Core Audio and key actions.

### Readable titles (`MediaTitleResolver`)

Some players publish junk as the SMTC title. Stremio's desktop app sends a torrent fragment
(`announce&tr=dht:<40-hex infoHash>`), not the file name. Before the title reaches the phone:

1. **Stremio:** extract the infoHash and ask Stremio's own local streaming server,
   `GET http://127.0.0.1:11470/stats.json` (hard-coded loopback host and port, 1 s timeout, no redirects, 4 MB cap,
   defensive JSON parsing, 5-minute cache per hash, 15 s for "not found"). It runs off the SMTC event path: the app name is
   shown until the answer arrives, then the title refreshes. The per-torrent endpoints (`/<hash>/stats.json`,
   `/<hash>/<fileIdx>/stats.json`) return the same file list and nothing about which file is playing, so the one
   documented endpoint is used. For a multi-file torrent the only video file is used, else the torrent name.
   **No external lookup is made** (no Cinemeta or any web service): the choice keeps the app local-first and means titles
   are exactly the file names Stremio already has.
2. **Release names** (extension, `[Group]`, CRC, 1080p/WEB-DL/x265/... tags, dotted words) go through `ReleaseNameCleaner`,
   which keeps the title, season/episode markers and year: `[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv`
   becomes `Yuusha-kei ni Shosu - 09`.
3. **Clearly junk** text (magnet or tracker fragments, URLs, 32+ hex characters) that cannot be resolved is replaced by the
   app name.

## Windows limits

- Input cannot reach elevated windows, the UAC secure desktop or the lock screen from the normal App. This is a limitation of 0.1: a planned phase 2 broker would cover elevated windows and UAC; the lock screen is out of scope.
- The App runs in the user session, never as a service.
- Firewall rule (installer): program + TCP port, Private profile, LocalSubnet. New Wi-Fi defaults to Public; the app detects it and tells the user.
- Detecting "the foreground window is elevated": a non-elevated process cannot open the token of an elevated or SYSTEM process (`OpenProcessToken` returns access denied; verified for winlogon and services), so access denied is treated as "more privileged than us", which is what UIPI cares about. Per the Win32 docs `SendInput` returns fewer events than asked when blocked (not exercised here: no elevated window available); `SendInputSink` throws `InputInjectionException` rather than hiding it.
- Lock state: `HostStatusMonitor` takes `WM_WTSSESSION_CHANGE` on a hidden window and checks `OpenInputDesktop` at start (fails while locked or on a secure desktop). The UAC secure desktop sends no foreground event to us.
- Relative mouse moves go through the user's pointer-acceleration setting, so a +2/-2 pair may not return to the exact pixel.
- SMTC's "current" session can be a paused one while another plays, so selection prefers playing sessions.
- Declare PerMonitorV2 DPI. Games and anti-cheat may ignore injected input. Unsigned input injection triggers antivirus/SmartScreen: sign releases (SignPath Foundation).

## Touch and keyboard

- Pointer events with `touch-action: none`; non-passive `touchmove` and `gesturestart` with `preventDefault` (iOS ignores `user-scalable=no`).
- Sum deltas per animation frame, send one binary frame (opcode + int16 dx/dy). No compression, `NoDelay`.
- Gestures: tap, two-finger tap (right click), two-finger drag (scroll), tap-hold drag.
- Reconnect on `visibilitychange` (iOS drops sockets in the background).
- Text goes through `KEYEVENTF_UNICODE`, layout independent, so Hebrew works. Special keys use a virtual-key row; "compose then send" covers IME and dictation.

## Repository layout

```
src/App/          WPF exe: window, tray, Kestrel host, Input, Media
src/Core/         protocol, pairing, remote schema, action runner (no Win32)
src/Remotes/      remote schema validator, catalog, approvals, action handler (no Win32)
src/Broker/       SYSTEM service (phase 2)
tests/Core.Tests/
web/              Vite + Preact client
remotes/          built-in JSON remotes + schema
installer/        Inno Setup
site/             GitHub Pages landing page
docs/             architecture.md, security.md, protocol.md, remotes.md
```

## Roadmap

1. **Phase 1:** installer, autostart, QR window and tray, pair/approve/revoke, touchpad, keyboard, media keys and volume, reconnect, network hints, Hebrew/English.
2. **Phase 2:** SMTC now-playing, remote engine, built-in Plex/Stremio/browser remotes, SYSTEM broker for admin windows and UAC (not in 0.1; the Settings option is shown disabled).
3. **Phase 3:** phone-side remote editor, import/export, signing, winget, landing page polish.
4. **Phase 4, on demand:** provider processes for deep integrations, opt-in secure mode.

Out of scope: plugin SDK, registry, trust tiers, auto-update, screen mirroring,
file transfer, gamepad, non-Windows hosts, anything over the internet.

## Later: secure mode (PWA)

Chrome installs a real PWA only from a trusted HTTPS origin. Let's Encrypt can
issue a certificate for a name that resolves to a private IP using DNS-01, but it
needs a domain, DNS automation and a way around routers that filter private
answers. Planned as opt-in: the user brings their own domain and a DNS API token,
Easy Mouse issues and renews the certificate itself. It also closes the active
man-in-the-middle gap. A project-hosted certificate service is not planned.

## Open items

- Verify SMTC reporting for Plex, Stremio and VLC.
- Verify `.local` resolution on target Android versions.
- Verify iOS home-screen storage behaviour.
- Check Easy Mouse for trademark conflicts (the name is generic).
