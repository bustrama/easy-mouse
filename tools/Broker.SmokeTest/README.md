# בדיקה ידנית של Broker (עברית, צעד אחר צעד)

הבדיקה הזו נועדה לבדוק שהאפשרות "שליטה בחלונות מנהל ובהודעת UAC" באמת עובדת. אי אפשר לבדוק אותה אוטומטית, כי היא צריכה
את שולחן העבודה האמיתי ואישור מנהל. תצטרכו טלפון מחובר. הכול הפיך: בסוף מסירים.

## מה צריך
- מתקין שנבנה מהענף (`EasyMouse-Setup-<גרסה>.exe`).
- טלפון שכבר מצומד ל-Easy Mouse (או שתצמדו אותו בצעד 2).

## 1. התקנה ובדיקת השירות
1. הריצו את המתקין והתקינו בתיקייה שהוא מציע (`C:\Program Files\Easy Mouse`). אל תשנו תיקייה.
2. פתחו PowerShell והריצו: `Get-Service EasyMouseBroker`
   **צפוי:** `Status = Stopped` ובדקו גם `(Get-Service EasyMouseBroker).StartType` שהוא `Manual`. כלומר השירות קיים אבל כבוי.
3. פתחו את Easy Mouse > הגדרות > אבטחה. המתג "שליטה בחלונות מנהל ובהודעת UAC" כבוי, והשורה מתחתיו אומרת שכבוי.

## 2. הפעלה
1. לחצו על המתג. **צפוי:** חלון אזהרה בעברית שאומר שכל תוכנה שלכם יכולה לקבל הרשאות מנהל בלי בקשה. לחצו "ביטול".
   **צפוי:** המתג נשאר כבוי ושום דבר לא השתנה.
2. לחצו שוב, אשרו את האזהרה, ואז אשרו את החלון של Windows ("האם לאפשר ל-Easy Mouse Broker...").
   **צפוי:** המתג דלוק, השורה אומרת "מופעל". בדקו `Get-Service EasyMouseBroker`: `Running` ו-`Automatic`.

## 3. הקלדה לחלון מנהל
1. התחילו > Notepad (פנקס רשימות) > לחיצה ימנית > **הפעל כמנהל מערכת**. אשרו.
2. מהטלפון: הזיזו את העכבר ולחצו בתוך החלון, והקלידו טקסט.
   **צפוי:** הטקסט מופיע ב-Notepad. (בלי האפשרות הזו הוא לא היה מופיע.)
3. כבו את המתג (Windows יבקש אישור), נסו להקליד שוב מהטלפון. **צפוי:** שום דבר לא מופיע, והטלפון נשאר מחובר.
   הפעילו שוב לפני הצעד הבא.

## 4. הודעת UAC
1. מהטלפון: מקש Windows, הקלידו `cmd`, ואז Ctrl+Shift+Enter. תופיע הודעת UAC והמסך ייעמם.
2. מהטלפון לחצו חץ שמאלה ואז Enter (או Alt+Y). **צפוי:** ההודעה מאושרת ונפתח חלון Command Prompt של מנהל.

## 5. מסך נעילה (צריך להיכשל)
1. לחצו Win+L כדי לנעול. מהטלפון נסו להקליד וללחוץ.
2. **צפוי:** שום דבר לא קורה במסך הנעילה. פתחו את הנעילה רגיל במקלדת.

## 6. שדרוג
1. השאירו את המתג דלוק. התקינו מעל אותו מתקין שוב (או גרסה חדשה יותר).
2. **צפוי:** בסוף ההתקנה המתג עדיין דלוק והשירות `Running`. אם הוא כבוי היה, הוא נשאר כבוי.

## 7. הסרה וניקוי
1. כבו את המתג (אישור Windows).
2. הסירו את Easy Mouse מ"הוספה או הסרה של תוכניות".
3. בדקו: `Get-Service EasyMouseBroker` צריך להגיד שאין שירות כזה, והתיקייה `C:\Program Files\Easy Mouse` נעלמה.

אם משהו שונה ממה שכתוב כאן: רשמו איזה צעד, מה ראיתם, ושלחו.

---
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
| Installer compiles with the service steps and the folder lock-down (Inno Setup 6.7) | Compiled; not run |
| Install-location check on real files (`EasyMouse.exe --acl PATH`): the installed 0.1.0 under Program Files passes; System32 and a Temp folder do not | Checked |
| Broker exe starts, refuses to run outside Program Files (exit 13), `--enable` needs an administrator | Checked on a dev machine, not elevated |
| `WTSSessionInfoEx.SessionFlags` reads 1 in an unlocked session | Checked |
| Service install (branch installer), switch on with decline-first, typing into an elevated Notepad from a paired phone, UAC prompt approved from the phone, lock screen refusing input, upgrade with the switch on, uninstall removing the service | Run by the project owner on a Windows 11 PC; every step worked as described. A first installer build had a folder-permission bug that this run found; it is fixed |
| Fault cases (service killed while on, pipe name squatted, portable copy) and Windows 10 | Not run |
