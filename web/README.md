# Easy Mouse - phone web app

Vite + Preact + TypeScript, plain CSS. Everything is bundled (Heebo included), so
`web/dist` can be embedded in the C# server with no CDN and no inline scripts or
styles (the server sends `default-src 'self'`). The wire protocol is
[`docs/protocol.md`](../docs/protocol.md); the crypto uses `@noble/ciphers` and
`@noble/hashes`, pinned exactly.

```
src/protocol/   b64, crypto (HKDF/HMAC/ChaCha20-Poly1305, frame counters), ops (frame
                encoders), fragment (#k= parsing), session (handshake state machine)
src/touch/      gesture recogniser and per-frame delta batching (pure, no DOM)
src/client.ts   app controller: pairing, reconnect, visibilitychange, input API
src/ui/         screens: Pair/Scan/Offline/Busy, Touchpad, Keyboard, Media (Remotes.tsx: disabled for 0.1, docs/remotes.md)
src/i18n/       English and Hebrew strings, RTL via <html dir>
dev/            mock server
```

## Commands

```powershell
cd web
npm ci --ignore-scripts
npm run typecheck
npm run lint
npm test            # vitest, includes ../tests/vectors.json
npm run build       # -> web/dist
```

## Run it without the C# server

The mock server speaks the real protocol (pairing, connect, AEAD frames, fake `state`
messages, two sample remotes) and serves `web/dist` with the same security headers.

```powershell
npm run build
node dev/mock-server.mjs --auto-approve       # or: npm run mock -- --auto-approve
```

It prints a pairing URL such as `http://localhost:8787/#k=...`. Open it in a browser (use
the phone viewport, or the LAN URL it prints on a real phone). The first pairing is auto
approved with `--auto-approve`; without the flag the console asks `Allow "..." (code 1234)? [y/n]`
and the code must match the one on the phone.

Options: `--port 8787`, `--host 0.0.0.0`, `--name "Office PC"`, `--fresh` (forget paired
devices), `--allow-any-origin`. Stdin commands: `y` / `n` (answer a prompt), `revoke`
(revoke all devices and close their sockets, the phone shows "scan a new code"), `new`
(print a new pairing URL). Paired devices are kept in `dev/.mock-devices.json` (git-ignored)
so reloads reconnect without a new scan. Every decrypted frame is logged to the console.

Live-reload development: start the mock server, then `npm run dev` (port 5173). Vite proxies
`/api` and `/ws` to the mock on 8787, so the page and the socket stay same-origin. Open
`http://localhost:5173/#k=<secret from the mock>`.

To see the Hebrew/RTL layout use the language switch in the Mouse tab settings (gear), or set
the browser language to Hebrew (auto-detected).

## Tests

- `crypto.test.ts`: the shared `tests/vectors.json` (generated and independently verified by the
  core: RFC sections, pairing, connect, every frame, the receive sequence), plus round trips,
  replay, reorder, tamper, wrong-direction and counter-0 rejection.
- `session.test.ts`: pairing and connect handshakes against a scripted server, busy/takeover,
  and keepalive with fake timers (no ping storm, no timeout while the user is active, busy not watched).
- `fragment.test.ts`: `#k=` parsing and removal with `history.replaceState`.
- `gestures.test.ts`: per-frame delta batching, sub-pixel carry, tap / two-finger tap /
  two-finger scroll / tap-hold drag, and button release on cancel.
- `remotes.test.ts`: remote sanitising (text-only labels, built-in icons only).

Two-finger touch cannot be driven from a desktop browser, so multi-touch behaviour is
covered by the gesture unit tests rather than by manual runs.
