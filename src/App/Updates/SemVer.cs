namespace EasyMouse.App.Updates;

/// <summary>Semantic version (major.minor.patch[-prerelease][+build]) with strict parsing; build metadata is ignored when comparing.</summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly string[] _pre;

    private SemVer(int major, int minor, int patch, string[] pre)
    {
        Major = major; Minor = minor; Patch = patch; _pre = pre;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => _pre.Length > 0;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;
        var s = text;
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(s[(plus + 1)..], numericNoLeadingZero: false)) return false;
            s = s[..plus];
        }
        string[] pre = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var preText = s[(dash + 1)..];
            if (!ValidIdentifiers(preText, numericNoLeadingZero: true)) return false;
            pre = preText.Split('.');
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!Number(parts[0], out var major) || !Number(parts[1], out var minor) || !Number(parts[2], out var patch)) return false;
        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    public static SemVer Parse(string text) => TryParse(text, out var v) ? v : throw new FormatException($"Not a semantic version: {text}");

    private static bool Number(string part, out int value)
    {
        value = 0;
        if (part.Length is 0 or > 9) return false;
        if (part.Length > 1 && part[0] == '0') return false;
        foreach (var c in part) if (c is < '0' or > '9') return false;
        value = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool ValidIdentifiers(string text, bool numericNoLeadingZero)
    {
        if (text.Length == 0) return false;
        foreach (var id in text.Split('.'))
        {
            if (id.Length == 0) return false;
            foreach (var c in id)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-')) return false;
            if (numericNoLeadingZero && id.All(char.IsAsciiDigit) && id.Length > 1 && id[0] == '0') return false;
        }
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;       // 1.0.0 > 1.0.0-rc.1
        if (other._pre.Length == 0) return -1;
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i]; var b = other._pre[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            if (an && bn)
            {
                var d = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b); // digit strings, no overflow
                if (d != 0) return d;
            }
            else if (an) return -1;
            else if (bn) return 1;
            else
            {
                var d = string.CompareOrdinal(a, b);
                if (d != 0) return d;
            }
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as SemVer);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (_pre.Length > 0 ? "-" + string.Join('.', _pre) : "");

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
}
