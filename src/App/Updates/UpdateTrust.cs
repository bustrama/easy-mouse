namespace EasyMouse.App.Updates;

/// <summary>
/// Everything the updater trusts, in one place: where releases come from, which hosts it may talk to, the size limits and
/// the embedded Ed25519 public key that release signatures are checked against (the private half is a GitHub Actions secret,
/// see docs/releasing.md).
/// </summary>
public static class UpdateTrust
{
    public const string Repository = "bustrama/easy-mouse";
    /// <summary>Generic on purpose: no app version, machine name or any other identifying data goes out.</summary>
    public const string UserAgent = "EasyMouse-Updater";

    public const string ApiHost = "api.github.com";
    public static readonly Uri LatestReleaseUrl = new($"https://{ApiHost}/repos/{Repository}/releases/latest");
    public static readonly Uri ReleaseListUrl = new($"https://{ApiHost}/repos/{Repository}/releases?per_page=15");

    /// <summary>Every release asset URL must start with this exact prefix (https, github.com, this repository).</summary>
    public const string AssetUrlPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>Hosts a download may be redirected through. GitHub serves release assets from its object store.</summary>
    public static readonly IReadOnlySet<string> AssetHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public static readonly IReadOnlySet<string> ApiHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ApiHost };

    public const int MaxRedirects = 4;
    public const long MaxApiBytes = 2 * 1024 * 1024;
    public const long MaxChecksumsBytes = 64 * 1024;
    public const long MaxInstallerBytes = 200L * 1024 * 1024;
    public const int SignatureBytes = 64;
    public const int MaxNotesChars = 4000;
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Raw Ed25519 public key (32 bytes) of the release signing key.</summary>
    public static readonly byte[] PublicKey = Convert.FromHexString("0896dc86a12ac7375bdadb6dfd08e694bb0186485fd37883b917fc41ea6ca890");

    public static string InstallerName(SemVer version) => $"EasyMouse-Setup-{version}.exe";
    public const string ChecksumsName = "SHA256SUMS";
    public const string SignatureName = "SHA256SUMS.sig";
}
