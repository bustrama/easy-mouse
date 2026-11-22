using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyMouse.App.Logic;
using EasyMouse.App.ViewModels;
using EasyMouse.App.Views;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.App.Tests;

/// <summary>
/// Renders the real windows (real XAML, view models, controller and Kestrel listener) to PNG files for design review.
/// Opt-in: set <c>EASYMOUSE_SHOTS_DIR</c>. Without it the test does nothing, so CI stays headless.
/// </summary>
public class ScreenshotHarness
{
    [Fact]
    public void Render_windows_to_png_when_EASYMOUSE_SHOTS_DIR_is_set()
    {
        var dir = Environment.GetEnvironmentVariable("EASYMOUSE_SHOTS_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var thread = new Thread(() => { try { Run(dir); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Screenshot run failed", failure);
    }

    private static void Run(string dir)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/EasyMouse;component/Resources/Theme.xaml") });
        app.Resources["B2V"] = new System.Windows.Controls.BooleanToVisibilityConverter();

        var real = NetworkProbe_Collect().FirstOrDefault(a => NetworkAnalyzer.Candidates([a]).Count == 1) ?? throw new InvalidOperationException("no LAN adapter to bind");
        var temp = Path.Combine(Path.GetTempPath(), "em-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        new SettingsStore(Path.Combine(temp, "settings.json")).Save(new AppSettings { Port = 18765, StartWithWindows = true });
        // User remotes for the Remotes window: one that needs review (text, link, program, lamp), one invalid.
        Directory.CreateDirectory(Path.Combine(temp, "remotes"));
        File.WriteAllText(Path.Combine(temp, "remotes", "living-room.json"), """
            { "schema": 1, "id": "living-room", "name": "Living room", "description": "Lamp and a quick link to the family calendar.",
              "layout": { "cols": 2, "items": [
                { "type": "button", "label": "Sign in", "action": { "do": "text", "text": "hunter2 and a long line of text that is cut short when it is shown to you in the review list" } },
                { "type": "button", "label": "Calendar", "action": { "do": "openUrl", "url": "https://calendar.example.com/family" } },
                { "type": "button", "label": "Start player", "action": { "do": "launch", "exe": "player", "args": ["--fullscreen"] } },
                { "type": "button", "label": "Lamp on", "action": { "do": "http", "method": "POST", "url": "http://192.168.1.20/api/on?token=${secret.hue}" } },
                { "type": "button", "label": "Mute", "action": { "do": "media", "cmd": "mute" } } ] } }
            """);
        File.WriteAllText(Path.Combine(temp, "remotes", "broken.json"), """{ "schema": 1, "id": "Broken One", "name": "Broken", "layout": { "cols": 2, "items": [ { "type": "button", "action": { "do": "shell" } } ] } }""");

        NetCategory category = NetCategory.Private;
        var controller = new AppController(new AppPaths(temp), new AppEnvironment
        {
            RemotesEnabled = true, // the harness still renders the (disabled) Remotes window
            SystemIntegration = false,
            MachineName = "OFFICE-PC",
            Protector = new XorProtector(),
            Probe = () => new NetworkSnapshot([real], new Dictionary<string, NetCategory> { [real.Id] = category }),
            UpdateHandler = new FakeGitHub { Respond = _ => FakeGitHub.Ok(FakeGitHub.ReleaseJson("v1.2.0", body: "- Faster pointer response on Wi-Fi\n- Hebrew labels in the QR window\n- Fixes the tray icon after sleep")) },
        });
        Await(controller.StartAsync());
        Pump(300);
        Await(controller.Updates.CheckAsync(manual: true));

        var qr = new QrWindow(controller, () => { });
        var settings = new SettingsWindow(controller);
        qr.ShowAndActivate();
        Pump(400);

        // ---- QR (English), then the approval dialog while a phone pairs over the real listener
        Save(qr, dir, "01-qr-en");
        var ws = new Uri($"ws://{real.Address}:18765/ws");
        var secret = controller.Core.Pairing.CurrentSecret!.Secret;
        var phone = Await(Task.Run(() => TestClient.ConnectAsync(ws)));
        var paired = Task.Run(() => phone.PairAsync(secret, "Dana's iPhone"));
        WaitFor(() => controller.Approvals.Current is not null);
        Pump(300);
        var approve = new ApproveWindow(controller.Approvals.Current!);
        approve.Show();
        Pump(250);
        Save(approve, dir, "02-approve-en-allow-disabled");
        Save(qr, dir, "03-qr-en-pending");
        Pump(1200);
        Save(approve, dir, "04-approve-en-allow-enabled");
        controller.Approvals.Current!.Allow();
        var credentials = Await(paired);
        Pump(300);
        approve.Close();

        // ---- a connected phone and two others for the Devices page
        var now = DateTimeOffset.UtcNow;
        controller.Core.Devices.Store.Add(new Device("pixel", "Pixel 8", new byte[32], now.AddDays(-40), now.AddDays(-1)));
        controller.Core.Devices.Store.Add(new Device("tablet", "Living room tablet", new byte[32], now.AddDays(-120), now.AddDays(-80)));
        var connected = Await(Task.Run(() => TestClient.ConnectAsync(ws)));
        Await(Task.Run(() => connected.AuthenticateAsync(credentials)));
        Pump(500);
        Save(qr, dir, "05-qr-en-connected");
        controller.ShowQr(); // "Show connection QR"
        Pump(300);
        Save(qr, dir, "05b-qr-en-connected-qr-requested");
        controller.HideQr();
        Pump(200);

        foreach (var page in new[] { SettingsPage.General, SettingsPage.Network, SettingsPage.Security, SettingsPage.Devices, SettingsPage.Updates, SettingsPage.About })
        {
            settings.ShowPage(page);
            Pump(300);
            Save(settings, dir, $"06-settings-en-{page.ToString().ToLowerInvariant()}");
        }

        // ---- Remotes window: needs review, then approved, then Hebrew
        var remotes = new RemotesWindow(controller);
        remotes.ShowAndActivate();
        Pump(500);
        Save(remotes, dir, "11-remotes-en-review");
        remotes.Height = 1000;
        Pump(300);
        Save(remotes, dir, "12-remotes-en-review-tall");
        remotes.Height = 680;

        // ---- Help screen: this network turns Public
        category = NetCategory.Public;
        Await(controller.RefreshNetworkAsync());
        Pump(400);
        Save(qr, dir, "07-help-en-public");

        // ---- Hebrew, right to left
        category = NetCategory.Private;
        Await(controller.RefreshNetworkAsync());
        Await(controller.UpdateSettingsAsync(s => s with { Language = "he" }));
        Pump(300);
        qr.Close();
        var qrHe = new QrWindow(controller, () => { });
        qrHe.ShowAndActivate();
        Pump(500);
        Save(qrHe, dir, "08-qr-he");
        controller.ShowQr();
        Pump(300);
        Save(qrHe, dir, "08b-qr-he-qr-requested");
        controller.HideQr();
        Pump(200);
        foreach (var page in new[] { SettingsPage.General, SettingsPage.Devices, SettingsPage.Updates })
        {
            settings.ShowPage(page);
            Pump(300);
            Save(settings, dir, $"09-settings-he-{page.ToString().ToLowerInvariant()}");
        }
        remotes.Height = 1000;
        remotes.ShowAndActivate();
        Pump(500);
        Save(remotes, dir, "13-remotes-he");
        remotes.AllowClose = true; remotes.Close();
        category = NetCategory.Public;
        Await(controller.RefreshNetworkAsync());
        Pump(300);
        Save(qrHe, dir, "10-help-he-public");

        qrHe.AllowClose = true; settings.AllowClose = true;
        qrHe.Close(); settings.Close();
        Await(connected.DisposeAsync().AsTask());
        Await(phone.DisposeAsync().AsTask());
        Await(controller.DisposeAsync().AsTask());
        Directory.Delete(temp, true);
    }

    private static IReadOnlyList<AdapterInfo> NetworkProbe_Collect() => EasyMouse.App.Platform.NetworkProbe.Collect().Adapters;

    private static void Save(Window window, string dir, string name)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) Pump(25);
        Assert.True(condition());
    }

    private static T Await<T>(Task<T> task)
    {
        while (!task.IsCompleted) Pump(20);
        return task.GetAwaiter().GetResult();
    }

    private static void Await(Task task)
    {
        while (!task.IsCompleted) Pump(20);
        task.GetAwaiter().GetResult();
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ 0x5A))];
        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }
}
