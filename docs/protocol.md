# Easy Mouse - Wire protocol (draft v0)

Status: draft. The Core implementation (`src/Core`, `src/Server`) and the web client
(`web/`) both follow this file. Change it in the same pull request that changes either
side. Fixed test vectors are in `tests/vectors.json` (see the end of this file).

## Transport

- `GET /` and static assets: the embedded web app.
- `GET /api/info`: `{ "name": "Office PC", "version": "0.1.0", "proto": 0 }`.
- `GET /ws` (WebSocket): all control traffic. Handshake messages are text frames (JSON);
  everything after `ready` is a binary frame, with one exception: the controller-negotiation messages
  `busy`, `takeover`, `active` and `takeover.denied` (see Connect) are text frames.
- The Host header must be the PC's own IP or `<pcname>.local`, with the port we listen on
  (the port may be omitted only when it is 80). Anything else gets `403`. IPv6 literals are
  not accepted (IPv6 is off in v1).
- A request with an `Origin` header must have exactly `http://<allowed host>[:port]`; any other
  Origin (including `null` and `https://...`) gets `403`, on the WebSocket upgrade and on plain
  requests alike. A request without `Origin` is not cross-origin (browsers omit it on same-origin
  GET; non-browser clients never send it) and still needs the device secret.
- No CORS headers, no cookies. Every response, including rejections, carries:
  `Content-Security-Policy: default-src 'self'; connect-src 'self' ws://<validated Host>; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`
  (the explicit `connect-src` covers browsers whose `'self'` does not include `ws:`),
  `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `Cross-Origin-Resource-Policy: same-origin`. Inline script and inline `<style>` are therefore
  not allowed in the web client; setting `element.style` from script is fine.

## Primitives

- KDF: HKDF-SHA256 (RFC 5869). `HKDF(ikm, salt, info, L)`. In this protocol the salt is always
  `nc | ns` (raw bytes, 32 bytes) and `info` is the ASCII label (`"code"`, `"wrap"`, `"c2s"`, `"s2c"`),
  `L` is 32 bytes unless stated.
- MAC: HMAC-SHA256, compared in constant time. Inputs are concatenated raw bytes. `"hello"`, `"c"`, `"s"`
  are their ASCII bytes. `name` is its UTF-8 bytes exactly as sent.
- AEAD: ChaCha20-Poly1305 (RFC 8439), 12-byte nonce, 16-byte tag, no associated data. Output is
  `ciphertext | tag`.
- Randomness: 128-bit nonces from a CSPRNG. Browser: `crypto.getRandomValues`
  (available on plain HTTP). Libraries: `@noble/ciphers`, `@noble/hashes`, pinned exactly.
- All text frames are JSON, UTF-8. `b64` is base64url without padding. Decoders must reject padding,
  the standard alphabet, a wrong length and non-zero trailing bits.

## Pairing (once per device, only while the QR window is open)

QR content: `http://<host>:<port>/#k=<b64 16 bytes>`. `k` is single-use, valid 2
minutes. The page reads `k`, then removes it with `history.replaceState`.

1. C->S `{ "t":"pair.hello", "name":"Dana's iPhone", "nc":"<b64 16B>", "mac":"<b64 HMAC(k, "hello"|nc|name)>" }`.
   `name` is 1-64 characters, no control characters and no bidirectional override/isolate characters,
   not only whitespace.
2. S->C `{ "t":"pair.pending", "ns":"<b64 16B>" }`. Both sides compute the confirmation code:
   `code = u32be(HKDF(k, nc|ns, "code", 4)) mod 10000`, written as exactly 4 decimal digits (zero
   padded). It is shown on the phone and in the PC dialog; the user must see the same number.
3. The PC user clicks Allow (enabled one second after the dialog appears; Deny is always possible;
   no answer within 60 seconds is a Deny). S->C
   `{ "t":"pair.ok", "id":"<b64 8B device id>", "wrap":"<b64 AEAD(key=HKDF(k, nc|ns, "wrap"), nonce=12 zero bytes, plaintext=S)>" }`
   where `S` is a fresh 256-bit device secret and `wrap` is 48 bytes (`ciphertext | tag`). The phone stores
   `id` and `S` in browser storage. Deny: `{ "t":"pair.denied" }`. After either message the server closes
   the socket with 1000; the phone then connects normally.
4. `k` is spent by the first valid `pair.hello`, whatever the user decides, and replaced by a fresh
   secret. Five `pair.hello` with a wrong MAC also replace it. Only one request may be pending.

Failures close the socket: wrong MAC 4401, window closed or secret expired or another request
pending 4408, malformed message or invalid name 4400.

## Connect (every session)

1. C->S `{ "t":"hello", "id":"<b64 8B>", "nc":"<b64 16B>" }`
2. S->C `{ "t":"challenge", "ns":"<b64 16B>" }`. An unknown, revoked or expired `id` also gets a
   challenge, so the server does not reveal which ids exist; it fails at step 3.
3. C->S `{ "t":"auth", "mac":"<b64 HMAC(S, "c"|nc|ns)>" }`
4. S->C `{ "t":"ready", "mac":"<b64 HMAC(S, "s"|nc|ns)>" }`. The client verifies it.
   Unknown, revoked, expired `id` or a wrong MAC: the server closes the socket with code 4401 and sends
   no `ready`. The whole handshake must complete within 10 seconds (4401).
5. Both derive two keys: `Kc2s = HKDF(S, nc|ns, "c2s")`, `Ks2c = HKDF(S, nc|ns, "s2c")`.

Devices unused for 90 days expire (configurable) and are refused like unknown ones.

One controlling device at a time. After `ready`, a device that is the controller receives a state frame
(op 0x11). If another device controls, the server sends the text message `{ "t":"busy" }` instead. A busy device may send `{ "t":"takeover" }`; the PC user approves (same Allow
rules as pairing). Replies: `{ "t":"active" }` (it now controls; its old controller is closed with 4409;
a state frame follows) or `{ "t":"takeover.denied" }`. If nobody controls, takeover succeeds at once.
Binary frames from a busy device are dropped. A device that reconnects replaces its own earlier
socket (4409 to the old one) without a prompt.

## Frames after `ready`

Every WebSocket message is binary: `nonce_counter (u64 BE, 8 bytes) | ciphertext | tag (16 bytes)`.
The AEAD nonce is `dir (1 byte: 0 c2s, 1 s2c) | 3 zero bytes | counter (8 bytes)`.
The receiver requires each counter to be strictly greater than the last one it accepted and drops
anything else (replay and reorder protection). Counters start at 1; 0 is never valid. A frame that fails
authentication does not advance the counter, so a forged frame cannot lock out the real peer. Dropped
frames are dropped silently; 32 dropped frames on one socket close it (4400). A binary frame before
`ready` closes the socket with 4400, and so does a text frame after `ready` other than `{ "t":"takeover" }`
from a busy device.

Plaintext layout: `op (u8) | payload`. Integers are big-endian. Payload lengths are exact; an unknown op,
an op sent in the wrong direction, a wrong length, an out-of-range value or invalid UTF-8 is a protocol
error and closes the socket with 4400 (nothing is executed).

| op | name | payload | direction |
|---|---|---|---|
| 0x01 | move | `i16 dx, i16 dy` | C->S |
| 0x02 | button | `u8 button (0 left, 1 right, 2 middle), u8 down (1) or up (0)` | C->S |
| 0x03 | wheel | `i16 dx, i16 dy` (units of 1/120 notch) | C->S |
| 0x04 | text | UTF-8 string, max 1024 bytes | C->S |
| 0x05 | key | `u16 virtual-key, u8 flags (1 down, 2 up, 3 press)` | C->S |
| 0x06 | media | `u8 cmd (0 playpause, 1 next, 2 prev, 3 stop, 4 volup, 5 voldown, 6 mute)` | C->S |
| 0x10 | json | UTF-8 JSON control message (below) | both |
| 0x11 | state | UTF-8 JSON, a full snapshot (below) | S->C |
| 0x7F | ping | empty | both |

A client `ping` is answered with a server `ping`.

### Limits enforced by the server

- A WebSocket message larger than 4096 bytes closes the socket (4400).
- Text (0x04): at most 1024 bytes of UTF-8 (so fewer characters for Hebrew).
- Rate: a token bucket of 200 frames per second sustained with a burst of 200, counted over every
  frame received on the socket. Exceeding it closes the socket (4429).
- One controller (see Connect).
- While the PC is locked, input ops (0x01-0x06) are dropped and the phone is told through 0x11.

### 0x10 json (remote action invocation)

The phone names an item of a remote; it never sends the action itself. The PC looks the action up in its
own remote definitions (so dangerous actions stay pinned to the remote file hash).

C->S:

```json
{
  "type": "object",
  "required": ["t", "rid", "remote", "item"],
  "additionalProperties": true,
  "properties": {
    "t": { "const": "invoke" },
    "rid": { "type": "integer", "minimum": 0, "description": "client-chosen request id, echoed in the result" },
    "remote": { "type": "string", "minLength": 1, "maxLength": 64 },
    "item": { "type": "string", "minLength": 1, "maxLength": 64, "description": "id of the button/slider in the remote" },
    "value": { "type": "number", "description": "slider position or other numeric argument" }
  }
}
```

S->C, once per invocation:

```json
{
  "type": "object",
  "required": ["t", "rid", "ok"],
  "properties": {
    "t": { "const": "result" },
    "rid": { "type": "integer", "minimum": 0 },
    "ok": { "type": "boolean" },
    "err": { "type": "string", "description": "short code: unsupported, busy (too many in flight), locked (PC is locked), denied" }
  }
}
```

Invalid JSON, any other `t`, a negative `rid`, a non-numeric `value` or too-long strings close the
socket (4400). At most 4 invocations run at once per socket; more get `{ok:false, err:"busy"}`. Until the
remote engine exists (phase 2) every invocation gets `{ok:false, err:"unsupported"}`.

### 0x11 state

Sent right after the device becomes the controller and again whenever any part changes. Always a full
snapshot, so the client replaces its copy.

```json
{
  "type": "object",
  "required": ["t", "media", "volume", "host"],
  "properties": {
    "t": { "const": "state" },
    "media": {
      "oneOf": [
        { "type": "null", "description": "nothing playing or unknown" },
        {
          "type": "object",
          "required": ["status"],
          "properties": {
            "status": { "enum": ["none", "playing", "paused", "stopped"] },
            "title": { "type": "string" },
            "artist": { "type": "string" },
            "positionMs": { "type": "integer", "minimum": 0, "description": "snapshot; extrapolate while playing" },
            "durationMs": { "type": "integer", "minimum": 0 }
          }
        }
      ]
    },
    "volume": {
      "oneOf": [
        { "type": "null" },
        {
          "type": "object",
          "required": ["level", "muted"],
          "properties": {
            "level": { "type": "integer", "minimum": 0, "maximum": 100 },
            "muted": { "type": "boolean" }
          }
        }
      ]
    },
    "host": {
      "type": "object",
      "required": ["locked", "elevated"],
      "properties": {
        "locked": { "type": "boolean", "description": "PC is locked: input is being dropped" },
        "elevated": { "type": "boolean", "description": "foreground window is elevated: input may not reach it" }
      }
    }
  }
}
```

## Errors

The server closes with: 1000 normal (pairing finished or denied), 4400 malformed, 4401 unauthorised,
revoked, expired or handshake timeout, 4408 pairing window closed or secret expired or request pending,
4409 replaced by another controller (or by the same device reconnecting), 4429 rate limited, 1011 internal
error. A revoked device's live sockets are closed with 4401 at once.

## Test vectors

`tests/vectors.json` (generated by `tests/Core.Tests/VectorsTests.cs`, which also checks it is current)
holds, with all byte values as lowercase hex (plus `...B64` where a value goes on the wire):

- `rfc`: RFC 5869 A.1 (HKDF-SHA256), RFC 4231 case 1 (HMAC-SHA256), RFC 8439 2.8.2 (ChaCha20-Poly1305).
  These come from the RFCs, not from our code.
- `pairing`: `k, nc, ns`, device name (ASCII and Hebrew), the pair MAC, the 4-digit code, the wrap key,
  the device secret and `wrap`.
- `connect`: device secret, id, `nc, ns`, both connect MACs and both session keys.
- `frames`: sealed frames in both directions (counters, plaintext, full frame) covering every op.
- `receiveSequenceC2S`: a sequence of frames for the server's receiver with the expected accept/drop
  (replay, gap, reorder, tampered tag, counter 0).

Everything except the RFC section was also reproduced with Node's `crypto` module by
`tests/verify-vectors.mjs` (run in CI), so the vectors do not depend on the C# implementation alone.
The web client's unit tests must pass the same file.
