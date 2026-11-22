# Easy Mouse - Security

Easy Mouse lets a phone inject keyboard and mouse input into a PC. Even though it
is local-network only, it is treated as a remote-control product: security is a
design requirement, not a later phase.

## Threat model

| Attacker | Attack | Defence |
|---|---|---|
| Guest, flatmate, infected IoT device on the LAN | Connects to the port and sends commands | No command is accepted without a per-device secret. Pairing needs the one-time QR secret and a click on the PC |
| Passive sniffer who knows the Wi-Fi password | Reads keystrokes and typed passwords | All frames are AEAD-encrypted with a session key. The QR secret never crosses the network |
| Active man-in-the-middle (ARP spoofing) | Rewrites the JavaScript at page load | **Not defended in default mode.** Planned opt-in secure mode (trusted HTTPS) |
| Malicious web page in the PC's or phone's browser | DNS rebinding, CSRF, cross-origin WebSocket | Host header allow-list (own IP and `.local` name only), strict Origin check on WebSocket and POST, no cookies, no CORS headers |
| Someone photographing the QR | Pairs their own phone | Secret is single-use, expires in 2 minutes, and still needs approval on the PC |
| Racing attacker or shoulder-surfer | Gets their own request approved | Dialog shows device name and a 4-digit code that must match the phone; one pending request at a time; Deny is the default button; Allow enables after one second |
| Malicious remote file | Launches programs, calls local services | Not applicable in 0.1: Remotes is disabled and no remote file is read (docs/remotes.md). Design if re-enabled: per-remote approval of dangerous actions, pinned to the file hash |
| URL leakage | Secret ends up in history or logs | Secret is in the URL fragment, removed with `history.replaceState`; `Referrer-Policy: no-referrer` |
| Local user or process trying to use the SYSTEM broker | Connects to its pipe, squats the pipe name, replaces its files | Off by default; ACL'd pipe, caller image/user/session check, owner check on the App side, Program Files only. Details and residual risk: [Elevated windows and UAC](#elevated-windows-and-uac-the-system-broker) |

Every response carries a strict CSP (`default-src 'self'`, no inline script),
`X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff`.

## Not defended (documented limits)

- An active man-in-the-middle on the LAN who rewrites the web app on load.
- A stolen, unlocked, already-paired phone (use revoke / "Disconnect all").
- Malware already running on the PC.
- Malware running as the signed-in user while admin control is on (see [Elevated windows and UAC](#elevated-windows-and-uac-the-system-broker)): it can reach the broker through the Easy Mouse process.
- A leak of the update signing key together with control of the release channel (see [Updates](#updates)); the key can be rotated with a new app release.
- A paired phone can type `Win+R` and any command. Remote-file guards stop
  third-party files and make abuse visible; they are not a boundary against a
  paired device.

## Pairing and credentials

- QR secret: 128-bit, single-use, 2-minute expiry, regenerated after use or five failed attempts. Pairing is accepted only while the QR is actually shown: while a phone is connected the app hides the QR, closes pairing and discards the secret until the user clicks "Show connection QR".
  A pairing request that is already waiting for the user's answer keeps pairing open: a phone that reconnects meanwhile does not deny it; pairing closes when that request is answered. The QR counts as shown only while its window is visible and not minimized.
- Per-device credential: 256-bit symmetric secret, DPAPI-protected on the PC. A key pair is not used: without `crypto.subtle` (unavailable on plain HTTP) the browser cannot hold a non-extractable key.
- Rotation: manual re-key per device; devices unused for 90 days expire (configurable). No per-connection rotation, because a lost message would desynchronise both sides.
- Revocation: per device and "revoke all", effective immediately and closing live sockets.
- Cryptography uses a pinned, audited library (for example noble) and no home-made primitives.

## Transport

- Plain HTTP with app-layer AEAD. Self-signed HTTPS would add nothing against an active attacker because users learn to click through the warning.
- Listen only on the selected LAN interface address, never `0.0.0.0`. Rebind on network change. No UPnP. IPv6 off in v1.
- The listener stops when the active network profile is Public. The firewall rule is Private + LocalSubnet, and the server also rejects peers outside the interface subnet.
- If Windows cannot report the network at all (adapter enumeration fails), the app treats it as "no network": it stops listening, logs the reason and tries again on the next 10-second poll. It never assumes the network is Private. (A network whose profile category cannot be read stays "unknown" and keeps listening, relying on the firewall rule.)
- The firewall rule is scoped to the program, TCP, Private profile and local subnet, with no fixed port, so changing the port in Settings does not need a new rule.

## Command safety

- Rate limits on input frames per second and on text length per message. One controlling device at a time.
- The tray icon changes while a device is connected, with a toast on each connect.
- A global hotkey and a tray item close all sockets and stop the listener until re-enabled.
- On session lock, input stops and the phone is told. Easy Mouse never unlocks the PC and never stores the Windows password.
- Remote actions by class (DEPRECATED: Remotes is disabled for 0.1 and scheduled for removal, docs/remotes.md; none of this is reachable in 0.1):
  - Safe: `key`, `media`, `mouse`, `smtc`, `volume`, `focus`. Exception: an item that presses more than three keys in a row or uses Ctrl+Escape (which opens the Start menu or Task Manager) is Sensitive, because a chain of key presses can type a command.
  - Sensitive: `text` macros, `openUrl` (http/https only).
  - Dangerous: `launch`, `http`. They prompt on the PC per remote, pinned to the file hash, so any edit prompts again.
  - There is no `shell` action.
  - The approval prompt shows every reason a remote needs review: each sensitive or dangerous action, the full text of a macro (nothing cut off, line breaks shown as symbols), launch arguments, the program a `focus` step brings to the front, and key chains or Ctrl+Escape spelled out key by key. Invisible and direction-changing (Unicode format) characters such as U+202E are refused in labels, texts, macro text and launch arguments by the validator, and the prompt escapes them as U+XXXX if one ever appears.
  - If an approval or a revoke cannot be written to disk, the user is told; an approval that is not saved is not active.
  - Implementation (`src/Remotes`): a remote with any Sensitive or Dangerous action is "needs review" and none of its actions run until the user approves it; the approval is pinned to the SHA-256 of the exact file bytes and the executor re-checks the hash on every invocation. Safe-only remotes run without a prompt. `http` hosts other than loopback and each `launch` program are recorded in the approval; the phone only names an item.
- `launch` runs only executables the user picked on the PC, with fixed arguments shown in the prompt. `http` allows loopback plus approved hosts, no redirects, with a timeout; secrets stay on the PC and are referenced as `${secret.name}`.
- Remote labels are rendered as text nodes and icons come from a built-in set, so a remote cannot inject script or load external URLs.

## Diagnostic log

The app writes failures it survives (an update check that threw, a network probe that failed, settings that could not be saved, an unexpected error on the UI thread) to `easymouse.log` in `%APPDATA%asyMouse`. It holds only messages and exception text: no device secrets, QR secrets, remote contents or typed text. It is capped at about 256 KB (the older half is dropped when it grows past that).

## Elevated windows and UAC (the SYSTEM broker)

Windows does not let a normal app send input to an administrator window or to the UAC prompt (UIPI, and the prompt lives on a
secure desktop). Doing it needs a SYSTEM component. Easy Mouse has one, off by default, behind **Settings > Security > Control admin
windows and the UAC prompt**. Read the consequence first:

> **With this on, a paired phone can approve UAC prompts, which makes it effectively an administrator on the PC.** Anyone holding a
> paired phone could install software or change system settings. It is a per-PC choice the user makes on the PC; the phone can neither
> switch it on nor off.

### What exists

- `Easy Mouse Broker` is a Windows **service** (`src/Broker`, `EasyMouse.Broker.exe`, installed next to the app by the installer under
  Program Files, removed on uninstall). It runs as SYSTEM but does nothing except keep one **agent** process alive: a copy of itself,
  started as SYSTEM in the signed-in user's session (session 0 cannot inject into a user's desktop). The service listens on nothing.
- The agent serves one **named pipe**, `\\.\pipe\EasyMouse.Broker`. It attaches its thread to whichever desktop currently receives input
  (`OpenInputDesktop` + `SetThreadDesktop`, which is how it reaches the UAC prompt) and calls the same `SendInput` code the App uses. It
  never opens a network socket, reads or writes no files, starts no processes, and logs nothing.
- The App never runs elevated. It sends input to the broker **only when the feature is on and normal input would be blocked**: the
  foreground window is elevated (keys), the window under the pointer is elevated (mouse; Windows drops such clicks without an error, so the
  App looks), or a secure desktop is showing. Everything else keeps going through the unprivileged `SendInput` path.

### Who can switch it on

The switch **is the service**: "on" means start type Automatic and running, "off" means stopped and set to manual. Turning it on or
off needs an administrator, so the App shows its warning on the PC and then runs `EasyMouse.Broker.exe --enable` (or `--disable`) with the
`runas` verb: Windows' own consent prompt, for that exact file. Declining changes nothing. There is no settings file to edit, no registry
value a normal user can write, and no message in the phone protocol that touches it. A fresh install leaves it off, and an update keeps
what the user had chosen. Settings shows the real state (see "No pretending" below).

### The broker's attack surface

| Who | What they try | What stops it |
|---|---|---|
| Another local user, or a process of another session | Connects to the pipe | The pipe's access list grants only SYSTEM and the signed-in console user (read/write, not "create instance"); the network is explicitly denied. The agent also checks the caller's session |
| A process of the same user that is not Easy Mouse | Connects and types | The agent resolves the caller's process id and accepts only the image `EasyMouse.exe` in the broker's own folder (full path, case-insensitive), run by the console user, in the console session. Everything else is disconnected before one byte is read |
| A local user who runs `EasyMouse.Broker.exe --agent` or `--service` by hand | Gets a SYSTEM-level helper | The agent refuses unless its token is SYSTEM; both modes refuse to run from outside Program Files |
| A local user who creates the pipe name first (to read what the phone types) | Squats `EasyMouse.Broker` | The agent creates it as the first instance and exits if it cannot. The App checks the **owner** of the pipe it connected to: only SYSTEM or the Administrators group can have created an object they own, so a user's pipe is refused before anything is sent |
| A user who can write to the install folder | Replaces the SYSTEM binary | The broker only runs, and `--enable` only runs, from under Program Files (administrators only). The installer registers the service only in such a folder. For a copy elsewhere the switch says the helper is not installed |
| A compromised or buggy App | Sends garbage or floods | The wire format is six fixed operations (move, button, wheel, key, text, release-all) with exact lengths: text is capped at 1024 bytes of valid UTF-8 without NUL, key codes 1..254, buttons 0..2. A malformed command is answered `Invalid` and the connection is closed. A token bucket limits it to about 1000 commands per second (text costs 25). There is no command that runs a program, reads data or changes a setting |
| Anything | Uses the broker at the lock screen | The agent asks Terminal Services whether the session is locked before every injection and treats "unknown" as locked. It never unlocks, never stores or types a Windows password, and the App also drops input while locked |

### Residual risk (what is not defended)

- **Same-user malware.** The caller check proves the connection comes from the installed `EasyMouse.exe`, not that the code inside it is
  honest. Any process running as the signed-in user can inject code into a same-user process (or start `EasyMouse.exe` suspended and
  patch it) and so pass the path, user and session checks. While the feature is on, such malware can drive the UAC prompt and is
  effectively an administrator **without** a prompt of its own. This is the real price of the feature. The defences are that it is off by
  default, that turning it on needs a deliberate administrator approval, and that it can be turned off again. Code signing would let the
  broker also verify the caller's signature, which raises the bar but does not remove injection; it is not in place yet.
- **A paired phone is as powerful as an administrator.** With the feature on, a paired phone can approve UAC. Pairing, revoke and
  "Disconnect all" are the controls, as for everything else in this document.
- **The in-app warning is a warning, not a gate.** It is a normal dialog; the gate is Windows' consent prompt, which only an
  administrator can accept.
- **The agent runs with the full SYSTEM token.** It could drop privileges after opening the desktops; it does not, to keep it small.
  Its only input is the validated command set above.
- **The lock screen is not supported**, by design and by enforcement (see the table). The lock-state check relies on
  `WTSSessionInfoEx.SessionFlags`. It reads "unlocked" in a normal session (checked); the "locked" reading is documented behaviour but not
  verified on this build, see `tools/Broker.SmokeTest/README.md` for the manual check. Any failure to read it counts as locked.
- `host.elevated` in the phone's state frame still means "the foreground window is elevated". With the broker on, input reaches it anyway.

### No pretending

If the service is missing (portable zip, other folder, failed install) the switch is disabled and says so. If it is on but not running,
or its pipe does not answer, or answers from something that is not the broker, Settings shows "Turned on, but ... not running", and every
input to an admin window is refused and dropped; it is never sent down the normal path as if that worked.

### Testing

The pipe protocol, validation, rate limiting, trust rules, access list, routing and the App-side client are unit tested. What CI cannot do
is switch desktops, so `tools/Broker.SmokeTest` is a manual tool that installs a test copy, types into an elevated window and approves a
real UAC prompt through the broker. Its README lists the exact steps and what has and has not been checked on a real machine.

## Updates

Updates come from GitHub Releases and are **opt-in, verified and confirmed by the user, never silent**. This is the only
traffic that leaves the LAN: HTTPS requests to `api.github.com` (release metadata) and, when the user clicks Update,
`github.com` (assets, which GitHub serves through `objects.githubusercontent.com` or `release-assets.githubusercontent.com`).
The README and the Settings page say so. The requests carry a generic `EasyMouse-Updater` user agent and nothing else that
identifies the user or the PC: no account, no cookies, no version, no machine name.

What the user controls: Settings > Updates has "Check for updates" (default **on**, at most once a day, plus "Check now") and
"Include pre-releases" (default off). An available update shows in the tray and in Settings. It is downloaded only after the
user clicks Update and confirms. The installer then asks Windows for administrator rights itself (it is started like a double-click,
with normal rights, and Inno Setup requests the elevation), so Windows shows its own UAC prompt. While the build is unsigned that
prompt says "Unknown publisher"; the checks below are what vouch for the file until code signing is in place. The app itself
keeps running with normal user rights, and declining the prompt leaves the running app untouched.

Verification, in this order, each step failing closed with a clear message and nothing run:

1. Every URL must be HTTPS on the default port, without credentials, on an allow-listed host. Redirects are followed by hand
   and each hop is checked. Release asset URLs must be exactly `https://github.com/bustrama/easy-mouse/releases/download/<tag>/<file>`.
2. Size limits (API 2 MB, checksum list 64 KB, signature exactly 64 bytes, installer 200 MB) and timeouts.
3. `SHA256SUMS.sig` must be a valid **Ed25519** signature of `SHA256SUMS` under the public key compiled into the app
   (`src/App/Updates/UpdateTrust.cs`). The checksum list is parsed only after its signature is valid.
4. The SHA-256 of the downloaded installer must equal the signed entry for `EasyMouse-Setup-<version>.exe`. The file is held open
   read-only from hashing until the installer process has ended (that includes the time the UAC prompt is open), so it cannot be
   swapped in between hashing and the installer reading itself.
5. The installer's own file version must equal the release version, and the version must be strictly newer than the running one.

Signing and key handling: the Ed25519 private key exists only as the `UPDATE_SIGNING_KEY` secret of the GitHub Actions
environment `release` (and an offline backup held by the maintainer); it is never committed. The release workflow signs
`SHA256SUMS` with it and then checks the result against the public key embedded in the app, so a wrong secret fails the release
instead of shipping an update every app would reject. Steps to generate, store and rotate the key are in
[releasing.md](releasing.md). The updater's signature is independent of Authenticode: code signing (SignPath Foundation, planned)
protects against SmartScreen warnings and tampering after installation; the Ed25519 signature protects the update channel.

| Attacker | Attack | Defence |
|---|---|---|
| Compromised GitHub release (stolen maintainer account or token) | Uploads a malicious installer to a release | The attacker cannot produce a valid `SHA256SUMS.sig` without the private key (a secret of a protected environment, not an account credential), so every app refuses it. Residual: an attacker who also steals the signing key |
| Compromised CI run | Builds a tampered installer and signs it | Releases are built only from a tagged commit that is on `main`; actions are pinned by commit SHA; the key is available only to the `release` job, which does not build code. GitHub build attestations let anyone check which workflow produced the files |
| Downgrade or replay | Serves an old, genuinely signed release (with known bugs) as the "latest" | Versions must be strictly newer than the running one; the signed list must contain the exact installer name for the tag, so an old list replayed under a newer tag fails; the installer's embedded version must match |
| Network man-in-the-middle | Tampers with the download | HTTPS with the system trust store only, HTTP and foreign hosts refused, and the content is verified by signature and hash regardless of the transport |
| Malicious redirect or hostile API response | Points the download at another host, a huge file, or crafts JSON to crash or trick the parser | Host allow-list on every hop, canonical asset URLs, size caps, strict JSON shape checks (anything unexpected is skipped or an error), release notes shown as plain text with control and bidi characters removed |
| Local malware in the same session | Swaps the downloaded installer after verification | The verified file is held open without write sharing until the installer starts. Malware already running as the user can do far more than this; it is listed under "Not defended" |
| GitHub rate limiting or outage | Makes the app spam errors or hammer the API | One request per day, an hourly quiet retry after a failed automatic check, a pause until the reset time on rate limits, no error pop-ups for offline |

## Supply chain

- Releases are built only by CI from tagged commits on `main`, code-signed through SignPath Foundation (planned; the workflow has a marked hook), and published with SHA-256 sums, an Ed25519 signature of those sums, and GitHub build attestations (see [Updates](#updates)).
- Deterministic .NET builds, SDK pinned in `global.json`.
- NuGet lock files in locked mode, `npm ci --ignore-scripts`, GitHub Actions pinned by commit SHA, minimal dependencies (QR and crypto libraries pinned exactly).
- The web client is embedded in the signed exe; no CDN.
- Community remotes arrive by pull request with a CI lint that flags dangerous actions. They are data, and the PC prompt is the enforcement.
- Branch protection, required review, `SECURITY.md` with private vulnerability reporting. Updates are opt-in, verified and user-confirmed, never silent (see [Updates](#updates)).

## CI security checks

1. Unauthenticated and wrong-secret WebSocket connections are rejected.
2. A wrong Host header (rebinding) and a foreign Origin are rejected.
3. An expired or reused QR secret fails; five failures regenerate it.
4. Replayed and tampered frames are dropped.
5. A revoked device is disconnected at once and cannot reconnect.
6. The listener is not bound to `0.0.0.0` and stops on a Public profile (mocked).
7. The remote schema rejects unknown actions; a dangerous action without approval does not run; an edited file prompts again.
8. Fuzz the frame parser and the remote JSON parser.
9. `dotnet list package --vulnerable`, `npm audit`, CodeQL, secret scanning.
10. Lock files unchanged after restore; the CSP header is present.
11. The broker rejects any caller other than the App and never opens a network listener.
