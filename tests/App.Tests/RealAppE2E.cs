using System.Diagnostics;
using System.Text.Json;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.App.Tests;

/// <summary>
/// Opt-in (set <c>EASYMOUSE_E2E_EXE</c> to the built EasyMouse.exe; <c>EASYMOUSE_E2E_OUT</c> to a file for the report).
/// Starts the REAL app with a seeded paired device in a temp data folder, connects as that phone over the LAN, and checks that
/// the Remotes tab layouts arrive and that real buttons run: the master volume slider (read back from the state frame, then restored)
///. It deliberately does not press keys or focus windows on the real desktop.
/// Without the variable this test does nothing, so CI stays headless.
/// </summary>
public class RealAppE2E
{
    [Fact]
    public async Task Real_app_pushes_remotes_and_runs_a_volume_button_and_fails_closed_on_focus()
    {
        var exe = Environment.GetEnvironmentVariable("EASYMOUSE_E2E_EXE");
        if (string.IsNullOrEmpty(exe)) return;
        if (!EasyMouse.Core.FeatureFlags.Remotes) return; // DEPRECATED: Remotes is disabled for 0.1 (docs/remotes.md); the real exe pushes none
        var report = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "em-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const int port = 18999;
        Process? app = null;
        try
        {
            var phoneId = EasyMouse.Core.Crypto.B64.Encode(new byte[8] { 1,2,3,4,5,6,7,8 });
            var secret = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();
            new FileDeviceStore(Path.Combine(dir, "devices.json"), new DpapiSecretProtector())
                .Add(new Device(phoneId, "E2E phone", secret, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            new SettingsStore(Path.Combine(dir, "settings.json")).Save(new AppSettings { Port = port, Language = "en" });

            var lan = NetworkProbe.Collect().Adapters.FirstOrDefault(a => NetworkAnalyzer.Candidates([a]).Count == 1)
                ?? throw new InvalidOperationException("no LAN adapter");
            report.Add($"LAN adapter {lan.Name} {lan.Address}");

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.Environment["EASYMOUSE_DATA_DIR"] = dir;
            app = Process.Start(psi)!;

            var uri = new Uri($"ws://{lan.Address}:{port}/ws");
            TestClient? phone = null; Exception? last = null;
            for (var i = 0; i < 80 && phone is null; i++)
            {
                try { phone = await TestClient.ConnectAsync(uri); }
                catch (Exception ex) { last = ex; await Task.Delay(250); }
            }
            Assert.True(phone is not null, "never connected: " + last + " exited=" + app.HasExited);
            await using var _ = phone;
            await phone!.AuthenticateAsync(new DeviceCredentials(phoneId, secret));

            string? remotesJson = null;
            JsonElement? state = null;
            for (var i = 0; i < 4 && (remotesJson is null || state is null); i++)
            {
                switch (await phone.ReceiveFrameAsync())
                {
                    case JsonMessage j: remotesJson = j.Json; break;
                    case StateMessage s: state = JsonDocument.Parse(s.Json).RootElement.Clone(); break;
                }
            }
            Assert.NotNull(remotesJson);
            var ids = JsonDocument.Parse(remotesJson!).RootElement.GetProperty("remotes").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
            report.Add($"remotes push: {remotesJson!.Length} bytes, ids [{string.Join(", ", ids)}]");
            Assert.Equal(["browser-video", "plex", "stremio", "vlc"], ids.Order().ToArray());
            Assert.DoesNotContain("\"action\"", remotesJson);

            var before = state!.Value.GetProperty("volume").GetProperty("level").GetInt32();
            var target = before == 31 ? 32 : 31;
            report.Add($"master volume before: {before}");

            async Task<(bool ok, string? err)> InvokeAsync(long rid, string remote, string item, double? value = null)
            {
                var value_ = value is null ? "" : $",\"value\":{value}";
                await phone.SendFrameAsync(new JsonMessage($"{{\"t\":\"invoke\",\"rid\":{rid},\"remote\":\"{remote}\",\"item\":\"{item}\"{value_}}}"));
                while (true)
                {
                    if (await phone.ReceiveFrameAsync() is JsonMessage { Json: var json })
                    {
                        var r = JsonDocument.Parse(json).RootElement;
                        if (r.GetProperty("t").GetString() == "result" && r.GetProperty("rid").GetInt64() == rid)
                            return (r.GetProperty("ok").GetBoolean(), r.TryGetProperty("err", out var e) ? e.GetString() : null);
                    }
                }
            }

            try
            {
                var (ok, err) = await InvokeAsync(1, "vlc", "13", target);
                report.Add($"vlc volume slider -> {target}: ok={ok} err={err}");
                Assert.True(ok);
                var seen = -1;
                for (var i = 0; i < 20 && seen != target; i++)
                    if (await phone.ReceiveFrameAsync() is StateMessage s) seen = JsonDocument.Parse(s.Json).RootElement.GetProperty("volume").GetProperty("level").GetInt32();
                report.Add($"master volume after (state frame from the PC): {seen}");
                Assert.Equal(target, seen);
            }
            finally
            {
                var (rok, rerr) = await InvokeAsync(2, "vlc", "13", before);
                report.Add($"volume restored to {before}: ok={rok} err={rerr}");
            }


            var windows = new EasyMouse.Platform.Windows.Remotes.Win32WindowSystem().MainWindows();
            report.Add($"real window enumeration: {windows.Count} windows, e.g. {string.Join("; ", windows.Take(4).Select(w => w.ExeName + " \"" + w.Title + "\""))}");
        }
        finally
        {
            if (app is { HasExited: false }) { app.Kill(true); app.WaitForExit(5000); }
            try { Directory.Delete(dir, true); } catch (IOException) { }
            if (Environment.GetEnvironmentVariable("EASYMOUSE_E2E_OUT") is { Length: > 0 } outFile) File.WriteAllLines(outFile, report);
        }
    }
}
