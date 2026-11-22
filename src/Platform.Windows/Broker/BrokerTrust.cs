using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EasyMouse.Platform.Windows.Broker;

/// <summary>
/// Who may talk to whom, as pure functions (unit tested). The broker accepts only the installed Easy Mouse App run by the user who
/// is signed in at the console; the App talks only to a pipe that SYSTEM or an administrator created.
/// </summary>
public static class BrokerTrust
{
    /// <summary>The App image, given the broker image: they are installed side by side, so EasyMouse.Broker.exe belongs to the EasyMouse.exe in the same folder.</summary>
    public static string? ExpectedAppImage(string brokerImagePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(brokerImagePath));
        return dir is null ? null : Path.Combine(dir, "EasyMouse.exe");
    }

    /// <summary>True when the connecting process runs exactly the expected image (full path, case-insensitive).</summary>
    public static bool IsExpectedImage(string? peerImagePath, string? expectedImagePath)
    {
        if (string.IsNullOrWhiteSpace(peerImagePath) || string.IsNullOrWhiteSpace(expectedImagePath)) return false;
        if (!Path.IsPathFullyQualified(peerImagePath) || !Path.IsPathFullyQualified(expectedImagePath)) return false;
        return string.Equals(Path.GetFullPath(peerImagePath), Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The broker runs files as SYSTEM, so it must live where only administrators can write: under Program Files. A copy somewhere a
    /// normal user can write to (D:Apps, a Downloads folder) would let that user replace the binary and become SYSTEM.
    /// </summary>
    public static bool IsProtectedInstallPath(string? path, IEnumerable<string> protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The App checks the owner of the pipe it connected to: only SYSTEM or the Administrators group can have created an object they
    /// own, so a pipe a normal user squatted under the same name (to read what is typed) is refused.
    /// </summary>
    public static bool IsTrustedPipeOwner(SecurityIdentifier? owner)
        => owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>
    /// The pipe's access list: SYSTEM full control, the signed-in user read and write (not "create instance"), the network denied,
    /// and nobody else. Inheritance is off so nothing else can be added by a parent.
    /// </summary>
    public static PipeSecurity BuildPipeSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}
