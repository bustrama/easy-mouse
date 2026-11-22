using System.ComponentModel;
using System.Diagnostics;
using EasyMouse.App.Logic;

namespace EasyMouse.App.Updates;

public enum UpdatePhase { Idle, Checking, Downloading, Verifying, Launching }

/// <summary>Starts the (already verified) installer elevated. Replaced by a fake in tests.</summary>
public interface IInstallerLauncher
{
    /// <exception cref="UpdateException"><see cref="UpdateErrorKind.Cancelled"/> when the user declined the Windows permission prompt.</exception>
    void Launch(string installerPath);
}

/// <summary>Remembers the last release answer so "Update available" survives a restart without another request.</summary>
public interface IUpdateCache
{
    byte[]? Load();
    void Save(byte[] raw);
}

public sealed class FileUpdateCache(string path) : IUpdateCache
{
    public byte[]? Load()
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= UpdateTrust.MaxApiBytes ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(byte[] raw)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a cache; losing it only costs one request */ }
    }
}

public sealed class NullUpdateCache : IUpdateCache
{
    public byte[]? Load() => null;
    public void Save(byte[] raw) { }
}

/// <summary>
/// Opt-in update flow, never silent: <see cref="CheckAsync"/> only reads release metadata; <see cref="InstallAsync"/> is called
/// only after the user confirmed, downloads, verifies the signature and the hash, and only then starts the installer.
/// </summary>
public sealed class UpdateService
{
    private readonly GitHubHttp _http;
    private readonly UpdateVerifier _verifier;
    private readonly IInstallerLauncher _launcher;
    private readonly IUpdateCache _cache;
    private readonly Func<AppSettings> _settings;
    private readonly Func<Func<AppSettings, AppSettings>, Task> _update;
    private readonly TimeProvider _time;
    private readonly string _workRoot;
    private readonly Func<string, string?> _installerVersion;
    private readonly object _gate = new();
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public UpdateService(GitHubHttp http, UpdateVerifier verifier, IInstallerLauncher launcher, IUpdateCache cache,
        Func<AppSettings> settings, Func<Func<AppSettings, AppSettings>, Task> update, SemVer current, TimeProvider time,
        string? workRoot = null, Func<string, string?>? installerVersion = null)
    {
        _http = http; _verifier = verifier; _launcher = launcher; _cache = cache;
        _settings = settings; _update = update; Current = current; _time = time;
        _workRoot = workRoot ?? Path.GetTempPath();
        _installerVersion = installerVersion ?? (p => FileVersionInfo.GetVersionInfo(p).ProductVersion);
        LoadCached();
    }

    public SemVer Current { get; }
    public UpdatePhase Phase { get; private set; }
    /// <summary>A newer, usable release, or null.</summary>
    public ReleaseInfo? Available { get; private set; }
    public UpdateException? LastError { get; private set; }
    /// <summary>True after a check that completed and found nothing newer.</summary>
    public bool UpToDate { get; private set; }
    public double Progress { get; private set; }
    public DateTimeOffset? RateLimitedUntil { get; private set; }

    public bool Enabled => _settings().CheckForUpdates;
    public DateTimeOffset? LastChecked => _settings().LastUpdateCheckUtc;
    public bool Busy => Phase != UpdatePhase.Idle;

    /// <summary>Raised from whatever thread did the work; subscribers marshal to the UI thread.</summary>
    public event EventHandler? Changed;
    /// <summary>The verified installer was started (and its permission prompt accepted). The installer will close this app and restart it.</summary>
    public event EventHandler? InstallerStarted;
    /// <summary>True from the installer's start until the next check or install; shown as a status line.</summary>
    public bool InstallerRunning { get; private set; }

    // ------------------------------------------------------------------ checking

    /// <summary>True when the daily automatic check should run now.</summary>
    public bool IsDue()
    {
        var now = _time.GetUtcNow();
        var s = _settings();
        if (!s.CheckForUpdates || Busy || now < _nextAttempt) return false;
        return s.LastUpdateCheckUtc is not { } last || now - last >= UpdateTrust.CheckInterval || last > now.AddMinutes(5);
    }

    /// <summary>Checks GitHub for a newer release. Automatic checks stay quiet when offline; manual ones record the reason.</summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!Begin(UpdatePhase.Checking)) return;
        try
        {
            var includePre = _settings().IncludePrereleases;
            if (manual && RateLimitedUntil is { } until && until > _time.GetUtcNow())
                throw new UpdateException(UpdateErrorKind.RateLimited, "Still rate limited.", retryAfter: until);

            var url = includePre ? UpdateTrust.ReleaseListUrl : UpdateTrust.LatestReleaseUrl;
            var raw = await _http.GetBytesAsync(url, UpdateTrust.ApiHosts, UpdateTrust.MaxApiBytes, UpdateTrust.ApiTimeout,
                "application/vnd.github+json", ct).ConfigureAwait(false);
            var best = ReleaseParser.SelectBest(raw, includePre); // throws Malformed for junk
            _cache.Save(raw);

            var now = _time.GetUtcNow();
            lock (_gate)
            {
                Available = best is not null && best.Version > Current ? best : null;
                UpToDate = Available is null;
                LastError = null;
                RateLimitedUntil = null;
                _nextAttempt = DateTimeOffset.MinValue;
            }
            await _update(s => s with { LastUpdateCheckUtc = now }).ConfigureAwait(false);
        }
        catch (UpdateException ex)
        {
            Fail(ex, manual);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { End(); }
    }

    /// <summary>Background loop: a short delay after start, then a cheap local "is it due" test every 15 minutes.</summary>
    public async Task RunSchedulerAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (IsDue()) await CheckAsync(manual: false, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Re-evaluates the cached answer after the pre-release option changed (no request).</summary>
    public void Reevaluate()
    {
        lock (_gate)
        {
            if (Available is { Prerelease: true } && !_settings().IncludePrereleases) Available = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LoadCached()
    {
        var raw = _cache.Load();
        if (raw is null) return;
        try
        {
            var best = ReleaseParser.SelectBest(raw, _settings().IncludePrereleases);
            if (best is not null && best.Version > Current) Available = best;
        }
        catch (UpdateException) { /* unreadable cache: ignore, the next check refreshes it */ }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>
    /// Downloads, verifies and starts the installer for <see cref="Available"/>. Call only after the user confirmed.
    /// Any verification failure leaves nothing running and the error in <see cref="LastError"/>.
    /// </summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        var release = Available;
        if (release is null || !Begin(UpdatePhase.Downloading)) return;
        string? dir = null;
        var launched = false;
        try
        {
            if (!(release.Version > Current))
                throw new UpdateException(UpdateErrorKind.Downgrade, "Refusing to install a version that is not newer.");

            dir = Path.Combine(_workRoot, "EasyMouse-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var installerPath = Path.Combine(dir, release.Installer.Name);

            var sums = await _http.GetBytesAsync(release.Checksums.Url, UpdateTrust.AssetHosts, UpdateTrust.MaxChecksumsBytes,
                UpdateTrust.ApiTimeout, "application/octet-stream", ct).ConfigureAwait(false);
            var sig = await _http.GetBytesAsync(release.Signature.Url, UpdateTrust.AssetHosts, UpdateTrust.SignatureBytes,
                UpdateTrust.ApiTimeout, "application/octet-stream", ct).ConfigureAwait(false);

            await using (var file = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await _http.GetToStreamAsync(release.Installer.Url, UpdateTrust.AssetHosts, UpdateTrust.MaxInstallerBytes,
                    UpdateTrust.DownloadTimeout, "application/octet-stream", file,
                    (done, total) => SetProgress(total is > 0 ? (double)done / total.Value : 0), ct).ConfigureAwait(false);
            }

            Phase = UpdatePhase.Verifying;
            Raise();
            var expected = _verifier.SignedHashFor(sums, sig, release.Installer.Name);

            // Hold the file open (read-only share) from hashing until the installer process exists, so it cannot be swapped in between.
            using var held = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            UpdateVerifier.VerifyHash(held, expected);
            if (_installerVersion(installerPath) != release.Version.ToString())
                throw new UpdateException(UpdateErrorKind.Downgrade, "The installer's own version does not match the release.");
            if (!(release.Version > Current))
                throw new UpdateException(UpdateErrorKind.Downgrade, "Refusing to install a version that is not newer.");

            Phase = UpdatePhase.Launching;
            Raise();
            _launcher.Launch(installerPath);
            launched = true;
            InstallerRunning = true;
        }
        catch (UpdateException ex) { Fail(ex, manual: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(new UpdateException(UpdateErrorKind.InstallerFailed, "Could not prepare the installer: " + ex.Message, ex), manual: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (!launched && dir is not null) TryDelete(dir);
            End();
        }
        if (launched) InstallerStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes leftovers of earlier updates (the installer cannot delete its own folder while it runs).</summary>
    public void CleanOldDownloads()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_workRoot, "EasyMouse-update-*"))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) TryDelete(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ state

    private bool Begin(UpdatePhase phase)
    {
        lock (_gate)
        {
            if (Phase != UpdatePhase.Idle) return false;
            Phase = phase;
            Progress = 0;
            InstallerRunning = false;
            if (phase == UpdatePhase.Downloading) LastError = null;
        }
        Raise();
        return true;
    }

    private void End()
    {
        lock (_gate) { Phase = UpdatePhase.Idle; Progress = 0; }
        Raise();
    }

    private void Fail(UpdateException ex, bool manual)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            LastError = ex;
            if (ex.Kind == UpdateErrorKind.RateLimited)
            {
                RateLimitedUntil = ex.RetryAfter is { } r && r > now ? r : now.AddHours(1);
                _nextAttempt = RateLimitedUntil.Value;
            }
            else if (!manual)
            {
                _nextAttempt = now + UpdateTrust.RetryAfterFailure; // no error spam: one quiet retry an hour later
            }
            if (ex.Kind == UpdateErrorKind.Cancelled) LastError = null;
        }
    }

    private void SetProgress(double value)
    {
        if (value - Progress < 0.02 && value < 1) return;
        Progress = value;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Starts the installer the way a double-click does. The Inno Setup exe is <c>asInvoker</c> and asks Windows for elevation itself
/// (the UAC prompt), keeping this unelevated parent alive: that is what lets it start the new app afterwards with normal user
/// rights. Do NOT use the "runas" verb here, it would make the relaunched app elevated.
/// The app does not quit by itself: the installer closes it (<c>EasyMouse.exe --quit</c>) once the user has agreed to the prompt,
/// so declining the prompt leaves the running app untouched.
/// </summary>
public sealed class InstallerLauncher : IInstallerLauncher
{
    public const string SilentArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";
    private const int ErrorCancelled = 1223;
    /// <summary>A silent install takes much longer than this; a Setup that ends sooner was declined at the UAC prompt or failed to start.</summary>
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(8);

    public void Launch(string installerPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installerPath, SilentArguments) { UseShellExecute = true });
            if (process is null) throw new UpdateException(UpdateErrorKind.InstallerFailed, "The installer did not start.");
            if (process.WaitForExit(EarlyExitWindow) && process.ExitCode != 0)
                throw new UpdateException(UpdateErrorKind.Cancelled, $"The installer ended early (exit code {process.ExitCode}), most likely because the permission prompt was declined.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new UpdateException(UpdateErrorKind.Cancelled, "The permission prompt was declined.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new UpdateException(UpdateErrorKind.InstallerFailed, "The installer could not be started: " + ex.Message, ex);
        }
    }
}
