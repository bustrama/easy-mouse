# Easy Mouse - Independent security review (pre-release)

Reviewer: an AI code review (Claude), working from the repository at commit `2befb10` (main). Method: read the code,
reason as an attacker on the same LAN, as a malicious web page in the PC's or phone's browser, as a passive sniffer and
as an active man-in-the-middle, and prove each claim with a test or a reproduction where possible. Findings that could
not be reproduced are labelled "unverified". This is a code review, not a penetration test: nothing was run against a
real phone, a real elevated window, the lock screen or an installed build.

Fixes are in this PR, one small commit each, with regression tests. Where a fix changes behaviour the wire protocol is
unchanged; `docs/protocol.md` documents the new limits.

## Summary

| Severity | Count | Fixed here | Open |
|---|---|---|---|
| Critical | 0 | - | - |
| High | 1 (latent: remote engine not wired yet) | 1 | 0 |
| Medium | 5 (one latent) | 3 | 2 (SR-01, SR-09) |
| Low | 8 | 3 | 5 |
| Info | 8 (SR-15 to SR-22) | - | - |

No critical finding. The handshake, pairing, key derivation and frame protection held up under review (section "Reviewed
and found sound"). The serious issues are in the layers around the protocol: availability and safety of the PC side
(a dropped phone leaves keys held, an elevated window kills the session, unauthenticated socket exhaustion), and the
remote engine's approval model (key presses bypass review).

## Findings

Each entry: severity, location, attack scenario, evidence, fix, status. IDs match the `SR-xx` comments in the tests.

### SR-01 - Unknown network profile is treated as private (listener starts) - Medium - OPEN

- Location: `src/App/Logic/ListenerPlan.cs:19` (stops only for `Public`), `src/App/Platform/NetworkProbe.cs:58` (any COM failure leaves every adapter `Unknown`, with the comment "we then listen").
- Scenario: the PC is on a hostile network (hotel, cafe, shared Wi-Fi) and the Network List Manager call fails, or the adapter has no profile yet. `docs/security.md` promises the listener "stops when the active network profile is Public"; here it starts and serves pairing (when the QR window is open) and the web app to everyone in the subnet. The device secret and approval prompt still protect control, so this widens exposure rather than opening control.
- Evidence: code reading only; not reproduced (needs an NLM failure).
- Recommended fix: treat `Unknown` like `Public` after a short grace period (for example 15 s, to cover the "Identifying network" state), and show "Cannot tell whether this network is private" with the existing "Make network private" action.
- Status: open. Not changed because it alters a deliberate trade-off in the app's start-up behaviour and the UI text is owned by another session.

### SR-02 - Keys and mouse buttons stay held on the PC when the phone vanishes - Medium - FIXED (`9e761c9`)

- Location: `src/Core/Sessions/ServerSession.cs:573` (`Terminate`), `src/Platform.Windows/SendInputSink.cs` (`ReleaseAll` existed, only `AppController.EmergencyStopAsync` called it).
- Scenario: the user holds Ctrl (or a mouse button for a drag) on the phone and the phone loses Wi-Fi or its battery dies. The PC never gets a key-up; Ctrl stays down until someone presses it, so the next real click is a Ctrl-click, or a held left button keeps dragging. `docs/protocol.md` says the *client* releases on page hide and socket loss, which cannot happen when the phone is gone.
- Evidence: `HeldInputTests.Held_input_is_released_when_the_controlling_socket_drops` fails on the old code (no release call), passes now. A waiting (busy) device disconnecting does not release the controller's keys (second test).
- Fix: `IInputSink.ReleaseAll()`; `ServerSession.Terminate` calls it when a controlling session ends. Server WebSocket keep-alive timeout set to 30 s so a half-open socket ends (the timeout itself is untested, see "Could not verify").
- Residual: a key-up frame in flight while the session is closing can still be processed after the release (tiny window).

### SR-03 - Now-playing text is untrusted and unbounded - Low - FIXED (`6f64667`)

- Location: `src/Core/Sessions/ServerSession.cs:432` (`BuildStateJson`/`SanitizeText`), `src/Platform.Windows/WindowsMediaController.cs` (title/artist copied verbatim from SMTC).
- Scenario: any web page or app that owns the media session sets a very long title. The 0x11 frame exceeds the 4096-byte limit and the web client silently drops it (`web/src/protocol/session.ts`), so the phone stops getting state for that track.
- Evidence: `MediaTextTests.Huge_media_metadata_is_truncated_so_the_state_frame_stays_small` fails on the old code. I also suspected lone surrogates would make `Utf8JsonWriter` throw and close the session; that was **wrong**: the writer escapes them and the session survives, so those two cases are kept only as regression tests.
- Fix: cap at 200 characters (the web client's own limit) without splitting surrogate pairs, drop control characters, replace unpaired surrogates with U+FFFD.

### SR-04 - Unauthenticated socket and memory exhaustion from one LAN host - Medium - FIXED (`6519466`)

- Location: `src/Server/EasyMouseServer.cs:195` (WebSocket accept), Kestrel limits (`:73-80`).
- Scenario: any device on the LAN opens thousands of WebSocket or idle HTTP connections. Each costs a Kestrel connection, a 4 KB buffer, a session object and up to 10 s of handshake time (WebSocket) or 130 s keep-alive (HTTP, Kestrel default). Memory and sockets grow without bound and the legitimate phone cannot connect. The frame rate limiter only applies after `ready`.
- Evidence: `PeerLimitTests.One_peer_cannot_hold_unlimited_websockets` fails on the old code (all 200 sockets accepted).
- Fix: at most 16 WebSocket upgrades per remote address (`429` beyond); `RequestHeadersTimeout` 10 s, `KeepAliveTimeout` 30 s. A global connection cap was deliberately not added: a single host could then fill it and lock the phone out.
- Residual: still no per-peer limit on plain HTTP connections, and a flooder with many addresses on the subnet is not stopped. This is a LAN denial of service, not a control or confidentiality issue.

### SR-05 - Album-art decoder has no source limits - Low - FIXED (`dad0df6`)

- Location: `src/Platform.Windows/ArtworkReader.cs:22`.
- Scenario: a web page publishes an enormous thumbnail through its media session. It is decoded by WIC in the network-facing process with no size check.
- Evidence (measured): a 4.6 MB PNG that declares 40000x40000 pixels decoded in 2.7 s of CPU with a peak working set of about 120 MB (WIC scales while decoding, so memory stays bounded); 15000x15000 took 0.26 s. So it is CPU only, hence Low. The probe test was not committed.
- Fix: refuse sources over 8 MiB or 64 megapixels as soon as the header is read (`ArtworkReader.Accepts`, unit tested).

### SR-06 - An elevated foreground window (or UAC/lock screen) kills the phone's session on every input frame - Medium - FIXED (`fbe0269`)

- Location: `src/Platform.Windows/Native/Input.cs` (`InputInjectionException`), `src/Core/Sessions/ServerSession.cs:137` (`DispatchAsync`), `src/Server/EasyMouseServer.cs` (generic catch closes 1011).
- Scenario: Task Manager (elevated) or any admin window is in front. `SendInput` returns fewer events than asked (UIPI); `SendInputSink` throws; nothing catches it in the session, so the first mouse move closes the socket with 1011 and the web client reconnect-loops. The protocol's `host.elevated` flag exists exactly so the phone can say "input may not reach it", but the phone could never see it because it was disconnected. Not attacker-controlled, but it makes the product unusable in a common situation and fails open to a reconnect loop.
- Evidence: `BlockedInputTests.Blocked_input_is_dropped_and_the_session_stays_up` fails on the old code. Not exercised against a real elevated window.
- Fix: `InputInjectionException` derives from new `InputBlockedException` (Core); the session drops that input and stays connected.

### SR-07 - `key` actions bypass the remote review: a key-only remote can run a command with no prompt - High (latent) - FIXED (`a713809`)

- Location: `src/Remotes/Security/Security.cs:31` (`ReviewBuilder`), `ActionClassifier.Classify` (`key` is Safe), `src/Remotes/Model/KeyChord.cs`.
- Scenario: a community remote (arrives by pull request; CI lint only flags Sensitive/Dangerous actions) contains one button whose action is `Ctrl+Escape` (opens the Start menu), a 300 ms `delay`, then keys `C`, `M`, `D`, `Enter`. It is classified Safe, so the remote is `Ready` immediately and the first tap runs `cmd` without any approval. The key parser refuses the Win key, so the authors clearly meant to block shell launching; Ctrl+Escape (or `Ctrl+L` in Explorer or a browser) reaches the same place. The documented limit ("a paired phone can type Win+R") is about the paired user, but `security.md` also promises "Dangerous... prompt per remote" for third-party files, which this defeats. The remote engine is not wired into the app yet, so this is latent, but it would have shipped with that wiring.
- Evidence: `KeyChainTests` (3 of 5 fail on the old code: the spell was `Ready` and ran without approval). Now it is `NeedsReview` and the handler answers `denied` with no executor call.
- Fix (heuristic, not complete): an item that presses more than three keys in a row, or uses a Ctrl+Escape chord, is a Sensitive review item (approval pinned to the file hash). Built-in remotes and ordinary player shortcuts are unaffected (tested).
- Residual: key presses can never be made fully safe. Several items tapped in order, or one `focus` onto Explorer/a browser followed by three keys, still reach more than intended. Before wiring the engine in, decide whether `key` should require `target.process` or an allow-list of keys, and show the full key sequence in the prompt.

### SR-08 - Elevated PowerShell started by bare name - Low - FIXED (`c713394`)

- Location: `src/App/Platform/WindowsServices.cs:115` (`NetworkProfileFixer.MakePrivate`).
- Scenario: `powershell.exe` with `runas` and `UseShellExecute` is resolved by search (app folder, PATH). A planted `powershell.exe` next to a user-writable install location would be what gets elevated. The UAC prompt shows the planted path, so exploitation needs a distracted user.
- Fix: full path under `Environment.SystemDirectory`. Not unit tested (starts a real elevated process); the path was checked to exist.

### SR-09 - The remote approval prompt does not show which secrets an `http` action sends - Medium (latent) - OPEN

- Location: `src/Remotes/Security/Security.cs:59-64` (`Describe` shows `Redact(url)` = no query; headers and body are not shown), `RemoteActionHandler.ResolveHttp`.
- Scenario: once the secret store exists, an approved remote can put any stored secret (`${secret.homeassistant}`) in a header, body or query going to the one host the user approved. The user sees "POST https://x.example/path?..." and never the placeholder names, so a malicious remote can exfiltrate any stored secret to a host the user was asked to approve for another reason. No `ISecretStore` implementation other than the in-memory test one exists in the repo today.
- Recommended fix: list the secret names referenced by each action in the review item, and require a per-secret approval (store them under the approval's hash).
- Status: open; needs a UI decision and the secret store.

### SR-10 - Five wrong pairing attempts from any LAN host regenerate the QR secret - Low - OPEN (accepted design)

- Location: `src/Core/Pairing/PairingService.cs:131`.
- Scenario: while the QR window is open, a LAN host sends five `pair.hello` frames with a bad MAC; the secret is replaced, the QR on screen is stale and the user's scan fails. Repeat forever (no per-peer throttle) and pairing is effectively blocked while the attacker is present. They gain nothing else (no secret, no approval).
- Mitigation idea: count failures per peer and only regenerate for repeated failures from the same address, or rate-limit hellos per address. Left as is: it is the documented behaviour and the cost is annoyance.

### SR-11 - A paired but waiting device can spam takeover prompts - Low - OPEN

- Location: `src/Core/Sessions/ConnectionHub.cs:83`, `ServerSession.cs:94`.
- Scenario: a device that is already paired (so already trusted) loops `takeover` text frames; each denied or timed-out request immediately allows a new dialog. Text frames are not covered by the binary rate limiter.
- Fix idea: a 10 s cool-down per session after a denied takeover.

### SR-12 - Kestrel reads the `Kestrel` configuration section and environment - Low - OPEN

- Location: `src/Server/EasyMouseServer.cs:70` (`CreateSlimBuilder`).
- Evidence (reproduced, test not committed): setting `Kestrel__Endpoints__Evil__Url=http://0.0.0.0:48765` made Kestrel bind that endpoint in addition to the chosen one; the existing address guard (`StartAsync` checks the address list) then threw `InvalidOperationException: Unexpected listening addresses` and stopped the host, so the app does not keep listening there. The extra port is open for the few milliseconds between bind and teardown.
- Needs an attacker who can already set the user's environment or write `appsettings.json` next to the app, so Low. Fix idea: build the host without configuration sources (or fail before `StartAsync` by validating `IConfiguration["Kestrel"]`).

### SR-13 - Device expiry is only enforced at connect and once at start-up - Low - OPEN

- Location: `src/App/AppController.cs:149` (`PurgeExpiredAsync` is called once), `DeviceManager.FindActive`.
- A device expired after 90 days is refused at the next connect (so the stated behaviour holds), but a socket that is already open is never closed, and expired rows stay in `devices.json` until the next start. Impact is small because `LastSeenUtc` is refreshed at every connect.

### SR-14 - Key-up and button-up frames are dropped while the PC is locked - Low - UNVERIFIED

- Location: `ServerSession.DispatchAsync` (all input dropped when `Status.Locked`).
- If the user holds Ctrl or a button, the PC locks, and the phone releases, the up event is dropped. Whether Windows keeps the injected key state across the lock was not tested (needs a lock screen). SR-02's release on disconnect covers part of it; a release-on-lock hook would cover the rest.

### Info items (no action required, listed for completeness)

- SR-15 - `FindActive` unknown id skips the HMAC, so an unknown id answers marginally faster than a known one (`ServerSession.cs:245-264`). Ids are 64-bit random, so enumeration is not practical. Constant-time-ify only if the "does not reveal which ids exist" claim must hold against a timing attacker.
- SR-16 - `GET /api/info` returns the PC name to any peer that passes the Host and subnet checks (`EasyMouseServer.cs:158`). Fine for pairing UX; it is a small information leak on a shared LAN.
- SR-17 - Pair names accept U+200E/U+200F and zero-width characters (`PairingService.cs:188` blocks only the U+202A-202E and U+2066-2069 range and controls), so a name can look blank or be padded. Cosmetic.
- SR-18 - `remote-approvals.json` is plain JSON with no integrity protection, and `launch` targets accept any fully-qualified path including UNC (`RemoteActionHandler.cs:105`). Malware that can write to `%APPDATA%` can forge approvals, which is in the documented "malware on the PC" out-of-scope bucket. The user picks the program, so a UNC path is their choice.
- SR-19 - `RemoteCatalog.Resolve` reads the whole remote file on every invocation before validating its size (`Catalog.cs:95`); `Reload` enumerates all `*.json` without a count limit. Local-only DoS.
- SR-20 - The device secret lives in `localStorage` of the plain-HTTP origin (`web/src/storage.ts:17`). Documented; anything that can run script in that origin or read the phone's profile has it. The strict CSP and the absence of `innerHTML`/`dangerouslySetInnerHTML`/`eval` in `web/src` limit script injection (checked by grep).
- SR-21 - Every commit in the public history is authored as `rubik.junk@gmail.com` (11 commits). Not a secret, but it is a personal address in a public repository; rewrite or ignore as the owner prefers.
- SR-22 - Documentation and CI gaps against `docs/security.md`: the CI list names CodeQL and secret scanning (check 9) but no workflow enables them (only `dotnet list package --vulnerable` and `npm audit`); there is no Dependabot/Renovate config; "manual re-key per device" is described but not implemented; the firewall rule (Private + LocalSubnet) lives in an installer that is not in the repository. `pages.yml` checks out without `persist-credentials: false` (read-only token, low value).

## Documented limits that were confirmed (not new findings)

These need an active man-in-the-middle or equivalent and are already in `docs/security.md`; they are restated because the review confirmed them:

- The web client is served over plain HTTP, so an on-path attacker can rewrite the JavaScript at load time and take the QR secret or the device secret.
- The text control messages after `ready` (`busy`, `active`, `takeover.denied`) and WebSocket close codes are not authenticated; an on-path attacker can forge them, and the client wipes its stored credentials on a forged 4401 after being online (`web/src/client.ts:269`).
- `<pcname>.local` can be spoofed by mDNS on the LAN; the Host allow-list cannot tell the real PC from a spoofer who answers for that name, though the AEAD handshake still prevents impersonating the PC's session without the device secret.
- A silently reconnecting device replaces its own earlier session without a prompt: anyone holding that device's secret takes over silently.

## Reviewed and found sound

Verified by reading the code and, where noted, by existing or new tests.

- Pairing secret: 128-bit from `RandomNumberGenerator`, single use (spent by the first valid hello, whatever the user decides), 2-minute expiry, regenerated after five failures, accepted only while the window is open, one pending request at a time; the pending request is denied when the window closes, the socket closes or after 60 s. The 4-digit code is derived from the secret and both nonces (`HKDF`), so a party without the secret cannot compute it; its role is to detect a swapped secret, not to be brute-forced. Racing pairing against connect has no shared state.
- Key derivation: HKDF-SHA256 with salt `nc|ns` and distinct ASCII labels (`code`, `wrap`, `c2s`, `s2c`); the connect MACs use different labels (`c`/`s`) and HMAC keyed by the device secret, so there is no cross-use; `ns` is server-random per connection so keys are fresh per session; direction is in the nonce and in the key, so there is no reflection.
- Frames: 64-bit counters strictly increasing per direction, counter 0 invalid, forged frames do not advance the counter (`SecureChannel.TryOpen` authenticates before updating), send counter refuses to wrap, replay across reconnects impossible (new keys), tampered/truncated frames dropped with a 32-frame limit. Existing tests cover replay, reorder, tamper and cross-session replay.
- Constant-time comparison: every MAC check goes through `CryptographicOperations.FixedTimeEquals` (`HandshakeCrypto.MacEquals`) on the server; the client uses noble's `equalBytes`, which has no early exit (read in `node_modules`). No `==`/`SequenceEqual` on secrets anywhere in `src` (grep).
- Strict decoding: base64url rejects padding, the standard alphabet, wrong lengths and non-canonical bits; handshake JSON is flat strings only, depth 2, duplicates rejected, lone-surrogate escapes handled.
- Pre-`ready` handling: binary before ready closes 4400; handshake timeout 10 s; text after ready other than `takeover` closes 4400; oversize messages close 4400 without buffering past the limit; frame rate limit 200/s.
- Device store: DPAPI (current user, fixed entropy) around each secret; a corrupt file or undecryptable secret throws instead of silently starting empty; atomic temp-file replace; revoke removes the device first, then closes live sockets, and `OnAuthAsync` re-checks after `ready` to close the revoke-during-handshake race.
- Host/Origin: exact match of IP or `.local` name plus the listening port, `null`/`https`/credential/path origins rejected, no CORS headers; the Host header is only echoed into the CSP after it passed the allow-list. DNS rebinding therefore fails the Host check; cross-site WebSocket fails the Origin check (browsers always send Origin on WebSocket).
- Static files: served from an in-memory dictionary of embedded resources; no file-system access, so path traversal is not possible (`..` and `\` also rejected). `nosniff`, CSP, `X-Frame-Options`, `Referrer-Policy`, CORP on every response including 403.
- Binding: constructor refuses `0.0.0.0`/`::`; the address guard compares what Kestrel actually bound (SR-12 shows it works); non-subnet peers rejected; listener stops on Public profile and emergency stop (the Unknown case is SR-01).
- Input injection (`InputBuilder`, `SendInputSink`): virtual keys are 16-bit and passed through; text is UTF-16 units with surrogate validation, CR/LF/tab/backspace mapped and other controls dropped, 1024-byte cap; wheel clamped to 50 notches; held state tracked for `ReleaseAll`.
- Remote engine: strict hand-written validator (unknown fields rejected, duplicate keys rejected, depth 16, file 64 KB, 8 actions per item, delay caps); only `${secret.x}` placeholders, never in the host; hosts compared after `Uri` normalisation so numeric/hex IP forms and `localhost` are classified correctly and a non-loopback host needs approval; redirects refused, no proxy, no cookies, hard timeout; `Resolve` re-reads and re-hashes the file on every invocation and uses the same bytes for hash and parse (no TOCTOU between hash and definition); approval pinned to SHA-256 of the exact bytes; `Approve` refuses if the file changed after the prompt; a built-in id cannot be shadowed by a user file; per-remote rate limit; the phone only names an item (layout push omits `action`, `target`, `description`).
- Web client: no `innerHTML`, `dangerouslySetInnerHTML`, `eval`, links, `window.open` or `postMessage` in `web/src`; remote labels and media text are rendered as text; icons come from a fixed set; album art is a data URL built from a type byte (jpeg/png) with a validated id; the pairing fragment is removed with `history.replaceState` before use; storage access is guarded. Dependencies are pinned exactly, `npm ci --ignore-scripts`, lock file present, `npm audit` clean.
- App: the approval dialog defaults to Deny (Enter and Escape) and Allow is enabled after one second, enforced in `ApprovalRequest.Allow`, not only in the button; hotkey requires Ctrl, Alt or Win; the autostart value is quoted; the only elevated command interpolates an integer; the manifest is `asInvoker`.
- Supply chain: all five GitHub Actions are pinned to a commit SHA and each SHA was checked against its tag comment with `gh api` (all match); workflows use `permissions: contents: read` (Pages additionally `pages`/`id-token` write in the deploy job only); NuGet restore in locked mode with lock files for every project, `dotnet list package --vulnerable`, SDK pinned in `global.json`.
- Git history (`git log --all -p`): searched for API keys, tokens, private keys, passwords and absolute personal paths; nothing found. The only personal data is the author email (SR-21).

## What I could not verify

- The WebSocket `KeepAliveTimeout` of 30 s (a real silent-peer test takes more than 50 s of wall time and uses the real clock); it is configuration, not tested.
- Anything involving real Windows state: an elevated foreground window, UAC, the lock screen (SR-06 and SR-14 were tested with a fake sink that throws), the Network List Manager failing (SR-01), DPAPI roaming, the installer's firewall rule.
- Hotkey handling, tray icon and WPF windows beyond reading the code and XAML (the `Public` listener stop is unit tested in `App.Tests`, not on a real network).
- A real phone browser: the web client was reviewed in source and by its unit tests (102 passing), not on iOS/Android.
- Files only skimmed: `Audio/CoreAudioInterop.cs` and `MasterVolume.cs` (COM interop, no untrusted input reaches them except the 0-100 volume level), `ui/*.tsx` (grepped for dangerous APIs, not read line by line), `tools/*`, `design/`, `site/` (static, loads Google Fonts).
- Cryptographic primitive implementations (.NET BCL and `@noble/*`) are taken as correct; the vectors in `tests/vectors.json` are cross-checked by `tests/verify-vectors.mjs` in CI.

## Test results (at the final commit of this PR)

See the PR description for the exact output. In short: `dotnet build` clean with warnings as errors; `dotnet test`: Core 178, Platform 69, App 84 all pass; Remotes 85 of 86 pass. The one failure, `ValidatorTests.Icons_match_the_web_client_icon_set`, **already fails on untouched main** (the web icon set gained `enter`, `backspace`, `delete`, `tab`, `shift`, `win`, `arrow-*`, `space` in #8 without the validator list following) and is unrelated to security, but `ci-dotnet` will be red until the icon lists are synced. Web: typecheck, lint, build, 102 tests and `npm audit` clean.
