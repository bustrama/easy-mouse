// DEPRECATED: disabled for 0.1, scheduled for removal (docs/remotes.md).
using EasyMouse.Remotes.Security;
using EasyMouse.Remotes.Validation;

// RemoteLint: validates every remotes/**/*.json (except remote.schema.json) with the same strict validator the
// app uses, and prints every sensitive and dangerous action so a reviewer sees them in the CI log.
// Exit code: 0 all valid, 1 at least one invalid file or lint problem, 2 usage error.

var root = args.Length > 0 ? args[0] : "remotes";
if (args.Length > 1 || !Directory.Exists(root))
{
    Console.Error.WriteLine("usage: RemoteLint [remotes-folder]");
    return 2;
}

var github = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
var failed = 0;
var ids = new Dictionary<string, string>(StringComparer.Ordinal);
var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
    .Where(f => !Path.GetFileName(f).Equals("remote.schema.json", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.Ordinal).ToList();

var schemaPath = Path.Combine(root, "remote.schema.json");
if (File.Exists(schemaPath))
{
    var errs = new ErrorList();
    using var schemaDoc = StrictJson.Parse(File.ReadAllBytes(schemaPath), errs);
    if (schemaDoc is null) { Report(schemaPath, "schema is not valid JSON: " + string.Join("; ", errs.Errors)); failed++; }
}

foreach (var file in files)
{
    var rel = Path.GetRelativePath(".", file).Replace('\\', '/');
    var bytes = File.ReadAllBytes(file);
    var result = RemoteValidator.Validate(bytes);
    if (!result.IsValid)
    {
        failed++;
        Console.WriteLine($"INVALID {rel}");
        foreach (var e in result.Errors) Report(rel, e);
        continue;
    }
    var remote = result.Remote!;
    var problems = new List<string>();
    if (!Path.GetFileNameWithoutExtension(file).Equals(remote.Id, StringComparison.Ordinal))
        problems.Add($"file name must match the id \"{remote.Id}\"");
    if (!ids.TryAdd(remote.Id, rel)) problems.Add($"id \"{remote.Id}\" is also used by {ids[remote.Id]}");
    foreach (var p in problems) Report(rel, p);
    if (problems.Count > 0) failed++;

    var review = ReviewBuilder.Build(remote);
    Console.WriteLine($"{(problems.Count > 0 ? "INVALID" : "OK     ")} {rel}  sha256={RemoteHash.Of(bytes)[..12]}  items={remote.Items.Count}  review={review.Count}");
    foreach (var r in review)
    {
        var line = $"  {r.Class.ToString().ToUpperInvariant(),-9} {r.Path}: {r.Kind}: {r.Summary}";
        Console.WriteLine(line);
        if (github) Console.WriteLine($"::warning file={rel}::{r.Class} action needs PC-side approval: {r.Kind}: {r.Summary}");
    }
    foreach (var h in ReviewBuilder.ExternalHosts(remote)) Console.WriteLine($"  HOST      {h} (user must approve)");
}

Console.WriteLine(failed == 0 ? $"{files.Count} remote file(s) valid." : $"{failed} problem file(s) of {files.Count}.");
return failed == 0 ? 0 : 1;

void Report(string file, string message)
{
    Console.WriteLine($"  error: {message}");
    if (github) Console.WriteLine($"::error file={file}::{message}");
}
