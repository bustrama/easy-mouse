# Easy Mouse

Turn any phone browser into a remote for your Windows PC. Android or iPhone,
nothing to install on the phone, local network only.

1. Open Easy Mouse on the PC. A small window shows a QR code.
2. Scan it with the phone. The remote opens in the browser.
3. Click **Allow** on the PC once. The phone is remembered.

Mouse, keyboard, media controls and custom remotes (Plex, Stremio, your own).
No cloud, no accounts, no telemetry. Licence: MIT.

> Status: early design. Nothing is released yet. Windows 11 is the target;
> Windows 10 is untested.

## Docs

- [Architecture](docs/architecture.md)
- [Security](docs/security.md)
- [Wire protocol (draft)](docs/protocol.md)
- [Design source](design/) (landing page, PC window, phone app)

## Security

A phone that can move your mouse needs real protection even on a home network.
Read [docs/security.md](docs/security.md) for the threat model and known limits.
Report vulnerabilities privately, see [SECURITY.md](SECURITY.md).

## Licence

[MIT](LICENSE)
