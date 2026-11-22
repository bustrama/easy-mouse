# Releasing Easy Mouse

A release is made by pushing a `vX.Y.Z` tag. The `release` workflow
(`.github/workflows/release.yml`) builds everything from that commit and leaves a
**draft** GitHub Release. Nothing reaches users until you publish the draft.
The in-app updater only ever sees published, non-draft releases (and
pre-releases only if the user opted in).

Assets of a release `X.Y.Z`:

| File | What it is |
|---|---|
| `EasyMouse-Setup-X.Y.Z.exe` | Inno Setup installer (per-machine, asks for administrator) |
| `EasyMouse-X.Y.Z-win-x64-portable.zip` | The same self-contained app folder, no installer, no firewall rule |
| `SHA256SUMS` | `sha256sum` output for the two files above |
| `SHA256SUMS.sig` | 64-byte raw Ed25519 signature of `SHA256SUMS`; the updater checks it |
| build attestation | GitHub provenance for all four files (`gh attestation verify`) |

## One-time setup

### 1. The update signing key

The app embeds the public key (`UpdateTrust.PublicKey` in `src/App/Updates/UpdateTrust.cs`).
The private key signs `SHA256SUMS` in CI. Generate a pair **on your own machine** (OpenSSL 3):

```bash
openssl genpkey -algorithm ed25519 -out update-signing-private.pem
# the 32 raw public-key bytes as hex (the last 32 bytes of the DER public key), for UpdateTrust.cs:
openssl pkey -in update-signing-private.pem -pubout -outform DER | tail -c 32 | xxd -p -c 64
```

Put that 64-character hex string into `UpdateTrust.PublicKey` (Git Bash on Windows has `openssl`, `tail` and `xxd`).

Store the private key:

- Repository **Settings > Environments > New environment `release`**. Add yourself as a *required reviewer*
  (every release then waits for your approval) and restrict *deployment tags* to `v*`.
- In that environment add the **environment secret** `UPDATE_SIGNING_KEY` whose value is the whole PEM file
  (including the `-----BEGIN/END PRIVATE KEY-----` lines). Do not use a repository-wide secret: an environment secret is
  only released to the protected `release` job.
- Keep one offline backup (password manager or encrypted drive), then delete the PEM from the working machine.
- **Never commit the private key**, paste it into an issue, or put it in the vault or a chat.

The workflow refuses to release if the secret is missing, and it verifies the fresh signature against the public key in
`UpdateTrust.cs`; a mismatching secret fails the run instead of publishing an update that every app would reject.

### 2. Code signing (planned)

Authenticode signing through [SignPath Foundation](https://signpath.org/) is planned for open-source projects. After the
project is approved, enable the marked `HOOK` steps in `release.yml` (sign the app files before the installer is built,
then the installer). Keep the API token in a secret and the organisation id in a variable; never in the file.
Until then the binaries are unsigned, Windows SmartScreen will warn, and the release notes should say so.

### 3. Repository settings

Branch protection on `main`; tags `v*` restricted to maintainers (Settings > Rules > Rulesets > tag rule).

## Cutting a release

1. Make sure `main` is green (`ci-dotnet`, `ci-web`) and contains everything. Only commits on `main` can be released.
2. Decide the version (semantic versioning: `1.4.0`, or `1.4.0-rc.1` for a pre-release; a tag with a `-suffix` makes a
   pre-release automatically). It must be higher than the last release: the updater never downgrades.
3. Tag and push **the commit on `main`**:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a v1.4.0 -m "Easy Mouse 1.4.0"
   git push origin v1.4.0
   ```

4. Open the **Actions** tab, run `release`. It tests, builds the web client, publishes the app self-contained for
   `win-x64` (version stamped from the tag into the assemblies, and `/api/info` reports it), builds the installer and the
   zip, then the `release` job waits for your approval, signs `SHA256SUMS`, attests the files and creates the draft.
5. Open the draft on GitHub. Check the generated notes (edit them; users read them in Settings > Updates as plain text,
   so keep them short and without Markdown tricks), and check the four assets are there.
6. **Test the draft before publishing** (see below), then press **Publish release**. For a pre-release leave
   "Set as a pre-release" on. Within a day installed apps that have update checks on will offer it.

If something is wrong after publishing: unpublish (back to draft) or delete the release. The updater only follows the
latest published release, so users stop seeing it. Fix, then release a **new higher version**; never reuse a tag.

## Verifying a release (you, or a user who wants to)

```powershell
# checksums (from the folder with the downloaded files)
Get-FileHash .\EasyMouse-Setup-1.4.0.exe -Algorithm SHA256
Get-Content .\SHA256SUMS

# the Ed25519 signature (OpenSSL 3); pub.pem is the public key from UpdateTrust.cs wrapped as SubjectPublicKeyInfo
openssl pkeyutl -verify -pubin -inkey pub.pem -rawin -in SHA256SUMS -sigfile SHA256SUMS.sig

# GitHub build attestation
gh attestation verify .\EasyMouse-Setup-1.4.0.exe --repo bustrama/easy-mouse
```

Test the installer on a clean Windows 11 VM or machine: install, check the firewall rule
(`netsh advfirewall firewall show rule name="Easy Mouse (phone remote)"`: program, TCP 8765, Private, LocalSubnet), pair a
phone, then run the installer again over it with `/SILENT` (the update path) and confirm the app restarts and the pairing
survives. Uninstall and confirm the rule and files are gone and `%APPDATA%\EasyMouse` is kept (unless you chose to remove it).

## Building the installer locally

Needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) and the .NET SDK from `global.json`.

```powershell
npm --prefix web ci --ignore-scripts; npm --prefix web run build
dotnet publish src/App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:Version=1.4.0 -o publish/win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.4.0 /DSourceDir=publish\win-x64 installer\EasyMouse.iss
```

The installer lands in `dist\`. Installer switches: `/SILENT` (progress only) or `/VERYSILENT`, `/SUPPRESSMSGBOXES`,
`/NORESTART`, `/TASKS="desktopicon,autostart"`, `/RELAUNCH=1` (start the app afterwards, used by the updater), and for the
uninstaller `/REMOVEDATA=1` to also delete `%APPDATA%\EasyMouse` in a silent uninstall.

## Rotating or losing the signing key

The app trusts exactly one key. To rotate: generate a new pair, put the **new public key** in `UpdateTrust.cs`, and release
that version signed with the **old** key (installed apps still trust it). Then replace the `UPDATE_SIGNING_KEY` secret and
sign later releases with the new key. Apps that skipped the rotation release can no longer verify later updates and need a
manual download of the installer from the Releases page; say so in the notes.

If the key is lost or you suspect a leak, rotate as above as soon as possible, tell users in the release notes and in a
security advisory, and consider unpublishing releases signed after the suspected leak.
