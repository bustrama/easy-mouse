<#
.SYNOPSIS
  Installs a TEST copy of the Easy Mouse Broker service, opens an elevated window to type into, and removes everything again.
  Needs an administrator (Windows shows one UAC prompt per phase). See README.md in this folder.

.EXAMPLE
  # 1. build + install (UAC prompt) ; 2. run the checks as a normal user ; 3. clean up (UAC prompt)
  powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Prepare
  powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Install
  & "$env:ProgramFiles\EasyMouseBrokerTest\EasyMouse.exe" --probe --type "hello" --enter --delay 3
  powershell -File tools\Broker.SmokeTest\Test-Broker.ps1 -Phase Cleanup
#>
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Install', 'Cleanup')][string]$Phase,
    [string]$Work = (Join-Path $env:TEMP 'EasyMouseBrokerTestWork')
)

$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:ProgramFiles 'EasyMouseBrokerTest'     # an administrator-only location: the broker runs files from here as SYSTEM
$Service = 'EasyMouseBroker'
$Repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Publish = Join-Path $Work 'publish'
$TypedLog = Join-Path $Work 'typed.log'

function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

function Invoke-Elevated([string]$phase) {
    # One UAC prompt. -Wait so the caller knows it is done.
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Phase', $phase, '-Work', "`"$Work`"")
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
}

switch ($Phase) {
    'Prepare' {
        New-Item -ItemType Directory -Force $Work | Out-Null
        Remove-Item -Recurse -Force $Publish -ErrorAction SilentlyContinue
        dotnet publish "$Repo\src\Broker" -c Release -r win-x64 --self-contained true -o $Publish | Out-Host
        dotnet publish "$Repo\tools\Broker.SmokeTest" -c Release -r win-x64 --self-contained true -o $Publish | Out-Host
        # The broker only accepts a client whose image is EasyMouse.exe in its own folder.
        Move-Item "$Publish\EasyMouse.Broker.SmokeTest.exe" "$Publish\EasyMouse.exe" -Force
        Write-Host "Prepared in $Publish"
    }
    'Install' {
        if (-not (Test-Admin)) { Invoke-Elevated 'Install'; return }
        Start-Transcript -Path (Join-Path $Work 'install.log') -Force | Out-Null
        try {
        if (Get-Service $Service -ErrorAction SilentlyContinue) { throw "A service named $Service already exists. Is Easy Mouse installed with the broker? Not touching it." }
        if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
        Copy-Item $Publish $Root -Recurse
        & sc.exe create $Service binPath= "`"$Root\EasyMouse.Broker.exe`" --service" start= demand DisplayName= "Easy Mouse Broker (TEST)" | Out-Host
        & "$Root\EasyMouse.Broker.exe" --enable
        if ($LASTEXITCODE -ne 0) { throw "--enable failed ($LASTEXITCODE)" }
        Remove-Item $TypedLog -ErrorAction SilentlyContinue
        # An elevated window that logs every line typed into it: proof that input reached an administrator window.
        $command = "`$Host.UI.RawUI.WindowTitle='EM-TARGET'; while(`$true){ `$l = Read-Host 'type here (elevated)'; Add-Content -Path '$TypedLog' -Value `$l }"
        Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', $command
        Start-Sleep -Seconds 2
        (New-Object -ComObject WScript.Shell).AppActivate('EM-TARGET') | Out-Null
        Get-Service $Service | Out-Host
        Write-Host "Installed. Elevated target window open; typed lines go to $TypedLog"
        Start-Sleep -Seconds 2
        } catch { Write-Host "INSTALL FAILED: $_"; } finally { Stop-Transcript | Out-Null }
    }
    'Cleanup' {
        if (-not (Test-Admin)) { Invoke-Elevated 'Cleanup'; return }
        Start-Transcript -Path (Join-Path $Work 'cleanup.log') -Force | Out-Null
        try {
        Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'EM-TARGET' } | Stop-Process -Force
        if (Get-Service $Service -ErrorAction SilentlyContinue) {
            if (Test-Path "$Root\EasyMouse.Broker.exe") { & "$Root\EasyMouse.Broker.exe" --disable }
            & sc.exe stop $Service 2>$null | Out-Null
            & sc.exe delete $Service | Out-Host
        }
        Start-Sleep -Seconds 1
        Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
        $left = @()
        if (Get-Service $Service -ErrorAction SilentlyContinue) { $left += 'service' }
        if (Test-Path $Root) { $left += 'folder' }
        if ($left) { Write-Warning "Left behind: $($left -join ', ')" } else { Write-Host 'Cleaned up: no service, no folder.' }
        Start-Sleep -Seconds 2
        } catch { Write-Host "CLEANUP FAILED: $_"; } finally { Stop-Transcript | Out-Null }
    }
}
