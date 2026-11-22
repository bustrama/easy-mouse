# Remotes: disabled for 0.1, scheduled for removal

**Status: DEPRECATED. Disabled for 0.1, scheduled for removal (or a redo).** Easy Mouse 0.1 ships as
Mouse, Keyboard and Media only. Nothing in the product, README or site promises Remotes.

## What is switched off

One flag per side, both `false`:

- PC: `FeatureFlags.Remotes` in `src/Core/FeatureFlags.cs` (tests can pass `AppEnvironment.RemotesEnabled = true`).
- Phone: `REMOTES_ENABLED` in `web/src/features.ts`.

With the flag off:

- The PC reads no remote files (built-in or user), creates no remotes folder, starts no folder watcher,
  and has no Remotes window, tray entry or remote-approval dialog.
- The server pushes no `{"t":"remotes"}` layouts and refuses every `invoke` with
  `{"t":"result","ok":false,"err":"unsupported"}` (the phone shows its "unsupported" toast; it no longer sends `invoke`).
- The phone has three tabs (Mouse, Keyboard, Media) and ignores `remotes` frames.

## What is still in the repository (and still built and tested)

`src/Remotes`, `remotes/` (schema and built-in definitions), `tools/RemoteLint`, `src/App` remotes UI
(`RemotesViewModel`, `RemotesWindow`, `RemoteImporter`), `web/src/ui/Remotes.tsx`, `web/src/remotes.ts`,
`src/Platform.Windows/Remotes`, `design/Phone-Remotes.dc.html`, and their tests. They carry
`DEPRECATED` notes at the entry points. The other docs (`protocol.md`, `security.md`, `architecture.md`)
describe the design as it was built; none of it is active in 0.1.

## Removing it

Delete the paths above, `FeatureFlags`, `REMOTES_ENABLED`, `AppEnvironment.RemotesEnabled`, the
`remotes` JSON messages (0x10 invoke and layouts) from the protocol, the `ci-remotes` workflow and the
remotes parts of `docs/`. Re-enabling for a test build means flipping both flags.
