# Broker smoke test

CI cannot switch desktops, so the SYSTEM broker (`src/Broker`) is checked by hand on a real Windows PC. Two ways. Neither leaves
anything behind if you follow the clean-up.

What the unit tests cover instead: the pipe protocol and its validation, rate limiting, the trust rules (caller image, pipe owner,
install path), the pipe access list, the App-side routing and client, and the Settings behaviour. See `tests/Platform.Windows.Tests/BrokerTests.cs`
and `tests/App.Tests/ControllerAndViewModelTests.cs`.

## A. With the real installer and a real phone (the end-to-end check)

1. Build the installer (`docs/releasing.md`, local build) or take one from a release, and install it. It must go under Program
   Files (the default). Check: `Get-Service EasyMouseBroker` shows **Stopped**, StartType **Manual**.
2. Open Easy Mouse > Settings > Security. The switch **Control admin windows and the UAC prompt** is off and the line under it says
   "Off. Nothing privileged is running." Turn it on: you get the warning, then Windows' consent prompt for `EasyMouse.Broker.exe`.
   Decline once first and check that nothing changed, then turn it on and accept. The line now says "On.", and
   `Get-Service EasyMouseBroker` shows **Running**, StartType **Automatic**.
3. Pair a phone. Open Start > Notepad > **Run as administrator** (accept the prompt on the PC). Click into it from the phone's
   touchpad and type from the phone's keyboard. The text must appear. With the switch **off** the same typing must not appear (and
   the app stays connected).
4. UAC prompt: on the phone press the Windows key, type `cmd`, press Ctrl+Shift+Enter. A UAC prompt appears on the secure desktop. Press
   **Left** then **Enter** (or Alt+Y) from the phone. The prompt must accept and an administrator Command Prompt opens.
5. Lock check (must NOT work): press Win+L. From the phone, type and click. Nothing may reach the lock screen. Unlock normally.
6. Clean-up: Settings switch off (Windows asks again), then uninstall from Windows Settings. Afterwards `Get-Service EasyMouseBroker`
   must say the service does not exist.

## B. Scripted, without a phone (`Test-Broker.ps1`)

Installs a **test copy** of the service under `C:\Program Files\EasyMouseBrokerTest`, with the smoke client acting as the App
(it must be named `EasyMouse.exe` in the broker's folder, because the broker accepts only that image), opens an elevated window
that logs what is typed into it, and removes everything again. It refuses to run if a service called `EasyMouseBroker` already exists
(a real install), so it cannot touch one. Windows shows one UAC prompt for Install and one for Cleanup.

```powershell
powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Prepare   # publishes the broker and the smoke client (no admin)
powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Install   # UAC prompt; service on, elevated window open
& "$env:ProgramFiles\EasyMouseBrokerTest\EasyMouse.exe" --status
& "$env:ProgramFiles\EasyMouseBrokerTest\EasyMouse.exe" --probe --type "hello" --enter --delay 5
#   --delay 5: you have 5 s to click into the elevated window; "foreground blocks normal input now: True" confirms it is in front.
#   Expected: normal SendInput says "blocked" or silently does nothing, the broker says ok, the line "hello" appears in
#   %TEMP%\EasyMouseBrokerTestWork\typed.log
& "$env:ProgramFiles\EasyMouseBrokerTest\EasyMouse.exe" --uac "$env:TEMP\uac-marker.txt"
#   Triggers a UAC prompt for `cmd /c echo ok > marker`, then presses Alt+Y through the broker. PASS = the marker file exists.
powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Cleanup   # UAC prompt; removes service, folder, window
Get-Service EasyMouseBroker -ErrorAction SilentlyContinue                # must print nothing
Test-Path "$env:ProgramFiles\EasyMouseBrokerTest"                        # must be False
```

Logs of the elevated phases: `%TEMP%\EasyMouseBrokerTestWork\install.log` and `cleanup.log`.

`EasyMouse.exe --wts` prints what the agent reads to decide whether the session is locked (`SessionFlags=1` unlocked, `0` locked).
To check the locked reading: `Start-Sleep 8; & ...\EasyMouse.exe --wts` and press Win+L within the 8 seconds.

## What has and has not been verified (update this when you run it)

| Check | Status |
|---|---|
| Unit tests (protocol, validation, trust, ACL, routing, client, settings) | Pass in CI |
| Installer compiles with the service steps (Inno Setup 6.7) | Compiled; not run |
| Broker exe starts, refuses to run outside Program Files (exit 13), `--enable` needs an administrator | Checked on a dev machine, not elevated |
| `WTSSessionInfoEx.SessionFlags` reads 1 in an unlocked session | Checked |
| Service install, switch on/off, input into an elevated window, UAC prompt, lock screen, upgrade/uninstall of the service | **Not yet run on a real machine** (needs administrator approval on the PC; the session that wrote this could not obtain it) |
