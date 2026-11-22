# Contributing

Thanks for helping. Easy Mouse injects input into a PC from a phone, so changes are
reviewed with security in mind. Small, focused pull requests are easiest to review.

## Requirements

- Windows 11 and the .NET SDK pinned in [`global.json`](global.json) (10.0.x). The app is WPF and Windows-only.
- Node.js 22 or newer (CI uses 22; the dev mock server uses 24's native TypeScript support, so use 24 for `web/dev`).

## Build and test

Build the phone web app first. The server embeds `web/dist` when it exists; without it the
server serves a stub page instead of the remote.

```powershell
cd web
npm ci
npm run build
cd ..
```

Then the .NET solution:

```powershell
dotnet build
dotnet test
```

Run the PC app from source:

```powershell
dotnet run --project src/App
```

Web client checks (from `web/`): `npm run typecheck`, `npm run lint`, `npm test`.

Remote definitions (DEPRECATED: Remotes is disabled for 0.1 and scheduled for removal, see docs/remotes.md) are still linted with:

```powershell
dotnet run --project tools/RemoteLint -- remotes
```

Try the phone UI without the PC app, against the mock server (see [web/README.md](web/README.md)):

```powershell
cd web
npm run build
npm run mock -- --auto-approve
```

CI (`.github/workflows`) runs the same commands. Lock files are enforced
(`dotnet restore --locked-mode`), so commit `packages.lock.json` changes with dependency changes.

## Repository layout

| Path | What |
| --- | --- |
| `src/Core` | Protocol, pairing, crypto, sessions, device store. No Windows dependencies. |
| `src/Server` | Kestrel host: serves the embedded web client and the WebSocket. |
| `src/Platform.Windows` | SendInput, media/volume, lock and elevation detection. |
| `src/Remotes` | Declarative remote engine (validation, hashing, execution). |
| `src/App` | WPF app: QR window, approval, tray, Settings, network hints. |
| `web/` | Phone web app (Vite, Preact, TypeScript) and `web/dev` mock server. |
| `remotes/` | Remote JSON schema and built-in remotes. |
| `site/` | Landing page (GitHub Pages). |
| `docs/` | Architecture, security model, wire protocol. |
| `tests/` | xUnit projects per source project, shared `vectors.json`. |
| `tools/` | Remote linter, platform smoke test, icon generator. |

## Commits and pull requests

- [Conventional commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`, `perf:`, `ci:` with an optional scope, e.g. `fix(app): ...`.
- Add or update tests with behaviour changes. Do not weaken a test to make it pass.
- Update [docs](docs/) when behaviour or the protocol changes. Do not document features that do not exist.
- Open a pull request against `main`; the template has a short checklist.

## Security expectations

- Local network only: no cloud calls, accounts, analytics or telemetry. Nothing is fetched from a CDN at runtime.
- The network-facing process never runs elevated.
- Crypto, pairing and framing follow [docs/protocol.md](docs/protocol.md) and are covered by shared test vectors. Do not roll your own primitives, and do not log secrets or key material.
- Treat everything from the phone as untrusted input: validate, bound sizes and rates.
- New dependencies need a reason; they are pinned and lock files are checked.
- Report vulnerabilities privately, see [SECURITY.md](SECURITY.md). Do not open public issues for them.
