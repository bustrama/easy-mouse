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
| Malicious remote file | Launches programs, calls local services | Per-remote approval of dangerous actions, pinned to the file hash |
| URL leakage | Secret ends up in history or logs | Secret is in the URL fragment, removed with `history.replaceState`; `Referrer-Policy: no-referrer` |

Every response carries a strict CSP (`default-src 'self'`, no inline script),
`X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff`.

## Not defended (documented limits)

- An active man-in-the-middle on the LAN who rewrites the web app on load.
- A stolen, unlocked, already-paired phone (use revoke / "Disconnect all").
- Malware already running on the PC.
- A paired phone can type `Win+R` and any command. Remote-file guards stop
  third-party files and make abuse visible; they are not a boundary against a
  paired device.

## Pairing and credentials

- QR secret: 128-bit, single-use, 2-minute expiry, regenerated after use or five failed attempts. Pairing is accepted only while the QR window is open.
- Per-device credential: 256-bit symmetric secret, DPAPI-protected on the PC. A key pair is not used: without `crypto.subtle` (unavailable on plain HTTP) the browser cannot hold a non-extractable key.
- Rotation: manual re-key per device; devices unused for 90 days expire (configurable). No per-connection rotation, because a lost message would desynchronise both sides.
- Revocation: per device and "revoke all", effective immediately and closing live sockets.
- Cryptography uses a pinned, audited library (for example noble) and no home-made primitives.

## Transport

- Plain HTTP with app-layer AEAD. Self-signed HTTPS would add nothing against an active attacker because users learn to click through the warning.
- Listen only on the selected LAN interface address, never `0.0.0.0`. Rebind on network change. No UPnP. IPv6 off in v1.
- The listener stops when the active network profile is Public. The firewall rule is Private + LocalSubnet, and the server also rejects peers outside the interface subnet.

## Command safety

- Rate limits on input frames per second and on text length per message. One controlling device at a time.
- The tray icon changes while a device is connected, with a toast on each connect.
- A global hotkey and a tray item close all sockets and stop the listener until re-enabled.
- On session lock, input stops and the phone is told. Easy Mouse never unlocks the PC and never stores the Windows password.
- Remote actions by class:
  - Safe: `key`, `media`, `mouse`, `smtc`, `volume`, `focus`.
  - Sensitive: `text` macros, `openUrl` (http/https only).
  - Dangerous: `launch`, `http`. They prompt on the PC per remote, pinned to the file hash, so any edit prompts again.
  - There is no `shell` action.
  - Implementation (`src/Remotes`): a remote with any Sensitive or Dangerous action is "needs review" and none of its actions run until the user approves it; the approval is pinned to the SHA-256 of the exact file bytes and the executor re-checks the hash on every invocation. Safe-only remotes run without a prompt. `http` hosts other than loopback and each `launch` program are recorded in the approval; the phone only names an item.
- `launch` runs only executables the user picked on the PC, with fixed arguments shown in the prompt. `http` allows loopback plus approved hosts, no redirects, with a timeout; secrets stay on the PC and are referenced as `${secret.name}`.
- Remote labels are rendered as text nodes and icons come from a built-in set, so a remote cannot inject script or load external URLs.

## Elevated windows and UAC (planned, phase 2, not in 0.1)

**Not available in 0.1.** The Settings option "Control admin windows and the UAC prompt" is shown disabled with "Not available in this version", and nothing in the app acts on it. A phone cannot type into admin windows, approve UAC prompts or use the lock screen. What follows is the design for a later version.

Input to admin windows and the UAC secure desktop requires a SYSTEM component.

- The network-facing App never runs elevated. A small `Easy Mouse.Broker` service does only input injection, listens on no socket, and accepts only the App over a named pipe with a restricted ACL.
- The capability would be a setting with a clear warning that a paired phone can then approve UAC prompts.
- Consequence to state plainly: with this on, a paired phone is effectively an administrator on the PC. Keep the broker small, audited and free of parsing logic.

## Supply chain

- Releases are built only by CI from tagged commits, signed through SignPath Foundation, and published with SHA-256 sums and GitHub build attestations.
- Deterministic .NET builds, SDK pinned in `global.json`.
- NuGet lock files in locked mode, `npm ci --ignore-scripts`, GitHub Actions pinned by commit SHA, minimal dependencies (QR and crypto libraries pinned exactly).
- The web client is embedded in the signed exe; no CDN.
- Community remotes arrive by pull request with a CI lint that flags dangerous actions. They are data, and the PC prompt is the enforcement.
- Branch protection, required review, `SECURITY.md` with private vulnerability reporting. No auto-update in v1.

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
