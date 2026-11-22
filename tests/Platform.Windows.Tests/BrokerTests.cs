using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Fakes;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.Platform.Windows.Tests;

public class BrokerProtocolTests
{
    private static BrokerCommand RoundTrip(BrokerCommand c)
    {
        var buffer = new byte[BrokerProtocol.MaxCommandBytes];
        var n = BrokerProtocol.Write(c, buffer);
        using var stream = new MemoryStream(buffer, 0, n);
        Assert.Equal(BrokerReply.Ok, BrokerProtocol.Read(stream, out var back));
        Assert.Equal(stream.Length, stream.Position); // consumed exactly one command
        return back;
    }

    [Theory]
    [InlineData(BrokerOp.Move, -1, 32767)]
    [InlineData(BrokerOp.Move, short.MinValue, 0)]
    [InlineData(BrokerOp.Wheel, 120, -240)]
    [InlineData(BrokerOp.Button, 2, 1)]
    [InlineData(BrokerOp.Key, 0x0D, 3)]
    [InlineData(BrokerOp.Key, 254, 1)]
    public void Fixed_commands_round_trip(BrokerOp op, int a, int b) => Assert.Equal(new BrokerCommand(op, a, b), RoundTrip(new BrokerCommand(op, a, b)));

    [Fact]
    public void Release_all_round_trips() => Assert.Equal(new BrokerCommand(BrokerOp.ReleaseAll), RoundTrip(new BrokerCommand(BrokerOp.ReleaseAll)));

    [Theory]
    [InlineData("a")]
    [InlineData("שלום עולם")]
    [InlineData("😀 + line\nbreak")]
    public void Text_round_trips(string text) => Assert.Equal(text, RoundTrip(new BrokerCommand(BrokerOp.Text, Text: text)).Text);

    [Fact]
    public void Text_of_exactly_the_limit_is_accepted_and_one_more_byte_is_not()
    {
        var max = new string('x', BrokerProtocol.MaxTextBytes);
        Assert.Equal(max, RoundTrip(new BrokerCommand(BrokerOp.Text, Text: max)).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => BrokerProtocol.Write(new BrokerCommand(BrokerOp.Text, Text: max + "x"), new byte[BrokerProtocol.MaxCommandBytes + 8]));

        var wire = new byte[] { (byte)BrokerOp.Text, 0x04, 0x01 }; // declares 1025 bytes
        Assert.Equal(BrokerReply.Invalid, BrokerProtocol.Read(new MemoryStream(wire), out _));
    }

    [Theory]
    [MemberData(nameof(BadFrames))]
    public void Malformed_commands_are_invalid_not_exceptions(byte[] wire)
        => Assert.Equal(BrokerReply.Invalid, BrokerProtocol.Read(new MemoryStream(wire), out _));

    public static TheoryData<byte[]> BadFrames() => new()
    {
        new byte[] { 0 },                                  // opcode 0
        new byte[] { 7, 0, 0 },                            // unknown opcode
        new byte[] { 0x7F },                               // a phone ping is not a broker command
        new byte[] { 0x06 + 0x10 },                        // a phone json frame is not either
        new byte[] { (byte)BrokerOp.Button, 3, 0 },        // button 3
        new byte[] { (byte)BrokerOp.Button, 0, 2 },        // down = 2
        new byte[] { (byte)BrokerOp.Key, 0, 0, 3 },        // vk 0
        new byte[] { (byte)BrokerOp.Key, 0, 255, 3 },      // vk 255
        new byte[] { (byte)BrokerOp.Key, 0, 65, 0 },       // action 0
        new byte[] { (byte)BrokerOp.Key, 0, 65, 4 },       // action 4
        new byte[] { (byte)BrokerOp.Text, 0, 0 },          // empty text
        new byte[] { (byte)BrokerOp.Text, 0, 2, 0x61, 0x00 }, // NUL inside
        new byte[] { (byte)BrokerOp.Text, 0, 2, 0xC3, 0x28 }, // invalid UTF-8
        new byte[] { (byte)BrokerOp.Text, 0, 1, 0xFF },    // invalid UTF-8
        new byte[] { (byte)BrokerOp.Text, 0, 3, 0xED, 0xA0, 0x80 }, // an encoded surrogate
    };

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 0, 0 })]            // move cut short
    [InlineData(new byte[] { 5, 0 })]               // text length cut short
    [InlineData(new byte[] { 5, 0, 4, 0x61 })]      // text body cut short
    public void A_stream_that_ends_early_throws_end_of_stream(byte[] wire)
        => Assert.Throws<EndOfStreamException>(() => BrokerProtocol.Read(new MemoryStream(wire), out _));

    [Fact]
    public void Commands_arriving_one_byte_at_a_time_are_still_read()
    {
        var buffer = new byte[BrokerProtocol.MaxCommandBytes];
        var n = BrokerProtocol.Write(new BrokerCommand(BrokerOp.Text, Text: "héllo"), buffer);
        Assert.Equal(BrokerReply.Ok, BrokerProtocol.Read(new OneByteStream(buffer[..n]), out var c));
        Assert.Equal("héllo", c.Text);
    }

    [Fact]
    public void Random_bytes_never_do_anything_but_decode_or_reject_or_end()
    {
        var random = new Random(12345);
        for (var i = 0; i < 20_000; i++)
        {
            var wire = new byte[random.Next(0, 40)];
            random.NextBytes(wire);
            try
            {
                var reply = BrokerProtocol.Read(new MemoryStream(wire), out var command);
                if (reply == BrokerReply.Ok)
                {
                    // whatever was accepted must be something Write would produce again
                    Assert.True(Enum.IsDefined(command.Op));
                    var again = new byte[BrokerProtocol.MaxCommandBytes];
                    BrokerProtocol.Write(command, again);
                }
            }
            catch (EndOfStreamException) { }
        }
    }

    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pos >= data.Length || buffer.IsEmpty) return 0;
            buffer[0] = data[_pos++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class BrokerRateLimiterTests
{
    [Fact]
    public void A_burst_is_allowed_then_refused_then_refills_with_time()
    {
        long now = 0;
        var limiter = new BrokerRateLimiter(perSecond: 100, capacity: 10, () => now);
        for (var i = 0; i < 10; i++) Assert.True(limiter.TryTake(BrokerOp.Move));
        Assert.False(limiter.TryTake(BrokerOp.Move));
        now += 100; // 100 ms = 10 tokens
        for (var i = 0; i < 10; i++) Assert.True(limiter.TryTake(BrokerOp.Move));
        Assert.False(limiter.TryTake(BrokerOp.Move));
    }

    [Fact]
    public void Text_costs_more_and_the_bucket_never_overfills()
    {
        long now = 0;
        var limiter = new BrokerRateLimiter(perSecond: 1000, capacity: 400, () => now);
        var texts = 0;
        while (limiter.TryTake(BrokerOp.Text)) texts++;
        Assert.Equal((int)(400 / BrokerRateLimiter.TextCost), texts);
        now += 3_600_000; // an hour idle does not bank an hour of tokens
        var moves = 0;
        while (limiter.TryTake(BrokerOp.Move)) moves++;
        Assert.Equal(400, moves);
    }
}

public class BrokerTrustTests
{
    private const string Broker = @"C:\Program Files\Easy Mouse\EasyMouse.Broker.exe";
    private const string App = @"C:\Program Files\Easy Mouse\EasyMouse.exe";

    [Fact]
    public void The_expected_app_is_the_one_next_to_the_broker()
        => Assert.Equal(App, BrokerTrust.ExpectedAppImage(Broker));

    [Theory]
    [InlineData(App, true)]
    [InlineData(@"c:\program files\easy mouse\easymouse.exe", true)]
    [InlineData(@"C:\Program Files\Easy Mouse\sub\..\EasyMouse.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\EasyMouse.exe", false)]
    [InlineData(@"C:\Program Files\Easy Mouse\EasyMouse.exe.evil.exe", false)]
    [InlineData(@"C:\Program Files\Easy Mouse\EasyMouse.Broker.exe", false)]
    [InlineData(@"C:\Program Files\Easy Mouse\other\..\..\Easy Mouse2\EasyMouse.exe", false)]
    [InlineData(@"EasyMouse.exe", false)]
    [InlineData(@"\\?\C:\Program Files\Easy Mouse\EasyMouse.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_installed_app_image_is_accepted(string? peer, bool expected)
        => Assert.Equal(expected, BrokerTrust.IsExpectedImage(peer, App));

    [Fact]
    public void A_missing_expectation_accepts_nobody()
    {
        Assert.False(BrokerTrust.IsExpectedImage(App, null));
        Assert.False(BrokerTrust.IsExpectedImage(App, ""));
    }

    [Fact]
    public void Only_system_or_administrators_may_own_the_pipe_the_app_talks_to()
    {
        Assert.True(BrokerTrust.IsTrustedPipeOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(BrokerTrust.IsTrustedPipeOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(BrokerTrust.IsTrustedPipeOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(BrokerTrust.IsTrustedPipeOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
        Assert.False(BrokerTrust.IsTrustedPipeOwner(WindowsIdentity.GetCurrent().User)); // an ordinary user who squatted the name
        Assert.False(BrokerTrust.IsTrustedPipeOwner(null));
    }

    [Fact]
    public void The_pipe_acl_is_system_plus_exactly_one_user_and_denies_the_network()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = BrokerTrust.BuildPipeSecurity(user);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected); // nothing inherited can add anyone
        Assert.Equal(3, rules.Count);
        var network = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Deny);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), network.IdentityReference);
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).ToDictionary(r => (SecurityIdentifier)r.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, allowed[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)].PipeAccessRights);
        var userRights = allowed[user].PipeAccessRights;
        Assert.True(userRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(userRights.HasFlag(PipeAccessRights.CreateNewInstance)); // cannot create a competing server instance
        Assert.False(userRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(userRights.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.DoesNotContain(allowed.Keys, sid => sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
    }
}

public class RoutingInputSinkTests
{
    private static (RoutingInputSink Sink, RecordingInputSink Normal, RecordingInputSink Broker, Action<bool> UseBroker) Make()
    {
        var normal = new RecordingInputSink();
        var broker = new RecordingInputSink();
        var use = false;
        return (new RoutingInputSink(normal, broker, _ => use), normal, broker, v => use = v);
    }

    [Fact]
    public void Everything_goes_to_the_normal_sink_unless_the_broker_is_wanted()
    {
        var (sink, normal, broker, _) = Make();
        sink.MoveRelative(1, 2);
        sink.Button(MouseButton.Left, true);
        sink.Wheel(0, 120);
        sink.Text("x");
        sink.Key(65, KeyAction.Press);
        Assert.Equal(5, normal.Calls.Count);
        Assert.Empty(broker.Calls);
    }

    [Fact]
    public void When_wanted_input_goes_only_to_the_broker()
    {
        var (sink, normal, broker, use) = Make();
        use(true);
        sink.MoveRelative(3, 4);
        sink.Key(13, KeyAction.Press);
        Assert.Equal(["move 3 4", "key 13 Press"], broker.Calls);
        Assert.Empty(normal.Calls);
    }

    [Fact]
    public void Switching_sides_lifts_what_the_old_side_was_holding()
    {
        var (sink, normal, broker, use) = Make();
        sink.Button(MouseButton.Left, true);   // normal side
        use(true);
        sink.Button(MouseButton.Left, false);  // now the broker's turn
        Assert.Equal(1, normal.ReleaseAllCount);
        Assert.Equal(0, broker.ReleaseAllCount);
        use(false);
        sink.MoveRelative(1, 1);
        Assert.Equal(1, broker.ReleaseAllCount);
    }

    [Fact]
    public void Release_all_reaches_both_sides_even_if_one_is_blocked()
    {
        var (sink, normal, broker, _) = Make();
        normal.Blocked = false;
        // A sink that throws on ReleaseAll must not stop the other one from being asked.
        var throwing = new ThrowingReleaseSink();
        var routed = new RoutingInputSink(throwing, broker, _ => false);
        Assert.Throws<InputBlockedException>(routed.ReleaseAll);
        Assert.Equal(1, broker.ReleaseAllCount);
        sink.ReleaseAll();
        Assert.Equal(1, normal.ReleaseAllCount);
        Assert.Equal(2, broker.ReleaseAllCount);
    }

    [Fact]
    public void A_broker_refusal_surfaces_as_blocked_input_and_is_never_redirected_to_the_normal_sink()
    {
        var (sink, normal, broker, use) = Make();
        use(true);
        broker.Blocked = true;
        Assert.Throws<InputBlockedException>(() => sink.MoveRelative(1, 1));
        Assert.Empty(normal.Calls);
    }

    private sealed class ThrowingReleaseSink : IInputSink
    {
        public void MoveRelative(int dx, int dy) { }
        public void Button(MouseButton button, bool down) { }
        public void Wheel(int dx, int dy) { }
        public void Text(string text) { }
        public void Key(ushort virtualKey, KeyAction action) { }
        public void ReleaseAll() => throw new InputBlockedException("blocked");
    }
}

public class BrokerRouteTests
{
    private static BrokerRoute Route(HostStatus? status, bool enabled = true, Func<bool>? secure = null, Func<bool>? pointer = null)
        => new(() => status, () => enabled, () => 0, secure ?? (() => false), pointer ?? (() => false));

    [Fact]
    public void Off_means_never_the_broker_whatever_is_in_front()
    {
        var status = new HostStatus();
        status.Update(locked: false, elevated: true);
        var route = Route(status, enabled: false, secure: () => true, pointer: () => true);
        Assert.False(route.UseBroker(InputKind.Keyboard));
        Assert.False(route.UseBroker(InputKind.Pointer));
    }

    [Fact]
    public void An_elevated_foreground_window_sends_both_kinds_to_the_broker()
    {
        var status = new HostStatus();
        status.Update(locked: false, elevated: true);
        var route = Route(status);
        Assert.True(route.UseBroker(InputKind.Keyboard));
        Assert.True(route.UseBroker(InputKind.Pointer));
    }

    [Fact]
    public void The_uac_prompt_secure_desktop_sends_input_to_the_broker()
    {
        var status = new HostStatus();
        Assert.True(Route(status, secure: () => true).UseBroker(InputKind.Keyboard));
        Assert.True(Route(status, secure: () => true).UseBroker(InputKind.Pointer));
    }

    [Fact]
    public void An_elevated_window_under_the_pointer_matters_for_the_mouse_only()
    {
        // Windows drops a click on an elevated window without an error, so the route looks at the window under the pointer.
        var status = new HostStatus();
        var route = Route(status, pointer: () => true);
        Assert.True(route.UseBroker(InputKind.Pointer));
        Assert.False(route.UseBroker(InputKind.Keyboard));
    }

    [Fact]
    public void Ordinary_windows_stay_on_the_normal_path()
    {
        var route = Route(new HostStatus());
        Assert.False(route.UseBroker(InputKind.Keyboard));
        Assert.False(route.UseBroker(InputKind.Pointer));
    }

    [Fact]
    public void A_locked_workstation_never_uses_the_broker()
    {
        var status = new HostStatus();
        status.Update(locked: true, elevated: true);
        Assert.False(Route(status, secure: () => true, pointer: () => true).UseBroker(InputKind.Keyboard));
    }

    [Fact]
    public void Before_the_status_exists_nothing_goes_to_the_broker()
        => Assert.False(Route(null, secure: () => true).UseBroker(InputKind.Pointer));

    [Fact]
    public void The_probes_are_cached_for_a_moment_so_a_stream_of_moves_does_not_hammer_windows()
    {
        long now = 0;
        var calls = 0;
        var route = new BrokerRoute(() => new HostStatus(), () => true, () => now, () => false, () => { calls++; return false; });
        for (var i = 0; i < 50; i++) route.UseBroker(InputKind.Pointer);
        Assert.Equal(1, calls);
        now += 150;
        route.UseBroker(InputKind.Pointer);
        Assert.Equal(2, calls);
    }
}

/// <summary>The App-side client against a stand-in broker on a real named pipe in this process.</summary>
public class BrokerInputSinkTests
{
    private static string NewPipe() => "EasyMouse.Broker.Test." + Guid.NewGuid().ToString("N");

    private static BrokerInputSink Client(string pipe, bool trust = true, TimeSpan? timeout = null)
        => new(pipe, () => Environment.TickCount64, _ => trust, timeout ?? TimeSpan.FromSeconds(2));

    /// <summary>Serves one connection: reads commands and answers each with <paramref name="reply"/>.</summary>
    private static (Task Done, List<BrokerCommand> Seen) Serve(NamedPipeServerStream server, Func<BrokerCommand, BrokerReply> reply)
    {
        var seen = new List<BrokerCommand>();
        var done = Task.Run(() =>
        {
            server.WaitForConnection();
            try
            {
                while (true)
                {
                    var read = BrokerProtocol.Read(server, out var command);
                    if (read != BrokerReply.Ok) { server.WriteByte((byte)read); return; }
                    lock (seen) seen.Add(command);
                    server.WriteByte((byte)reply(command));
                    server.Flush();
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException) { }
        });
        return (done, seen);
    }

    [Fact]
    public async Task Commands_reach_the_broker_in_order_and_ok_means_done()
    {
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var (done, seen) = Serve(server, _ => BrokerReply.Ok);
        using (var client = Client(name))
        {
            client.MoveRelative(5, -6);
            client.Button(MouseButton.Right, true);
            client.Wheel(0, 120);
            client.Key(0x0D, KeyAction.Press);
            client.Text("שלום");
            client.ReleaseAll();
        }
        await done.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
        [
            new BrokerCommand(BrokerOp.Move, 5, -6), new BrokerCommand(BrokerOp.Button, 1, 1), new BrokerCommand(BrokerOp.Wheel, 0, 120),
            new BrokerCommand(BrokerOp.Key, 0x0D, 3), new BrokerCommand(BrokerOp.Text, Text: "שלום"), new BrokerCommand(BrokerOp.ReleaseAll),
        ], seen);
    }

    [Theory]
    [InlineData(BrokerReply.Blocked)]
    [InlineData(BrokerReply.Locked)]
    [InlineData(BrokerReply.RateLimited)]
    [InlineData(BrokerReply.Failed)]
    public void A_refusal_is_reported_as_blocked_input_not_swallowed(BrokerReply reply)
    {
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Serve(server, _ => reply);
        using var client = Client(name);
        var ex = Assert.Throws<BrokerException>(() => client.MoveRelative(1, 1));
        Assert.IsAssignableFrom<InputBlockedException>(ex);
        Assert.Equal(BrokerFailure.Refused, ex.Failure);
    }

    [Fact]
    public void No_broker_means_a_clear_not_running_failure_and_no_hammering()
    {
        long now = 0;
        using var client = new BrokerInputSink(NewPipe(), () => now, _ => true, TimeSpan.FromSeconds(1));
        var first = Assert.Throws<BrokerException>(() => client.MoveRelative(1, 1));
        Assert.Equal(BrokerFailure.NotRunning, first.Failure);
        var second = Assert.Throws<BrokerException>(() => client.MoveRelative(1, 1));
        Assert.Same(first, second); // answered from the last failure, no new connection attempt
        now += 1500;
        var third = Assert.Throws<BrokerException>(() => client.MoveRelative(1, 1));
        Assert.NotSame(first, third);
    }

    [Fact]
    public void A_pipe_owned_by_an_ordinary_user_is_refused_before_anything_is_sent()
    {
        var name = NewPipe();
        // Own it explicitly as this user: on an elevated runner a new pipe would otherwise be owned by Administrators, which IS trusted.
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(me);
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024, security);
        var (_, seen) = Serve(server, _ => BrokerReply.Ok);
        using var client = new BrokerInputSink(name, () => Environment.TickCount64); // the real trust rule: the owner is not SYSTEM or an administrator
        var ex = Assert.Throws<BrokerException>(() => client.Text("a password"));
        Assert.Equal(BrokerFailure.Untrusted, ex.Failure);
        Thread.Sleep(100);
        lock (seen) Assert.Empty(seen);
    }

    [Fact]
    public void The_probe_tells_missing_from_untrusted_from_healthy()
    {
        Assert.Equal(BrokerFailure.NotRunning, BrokerInputSink.Probe(NewPipe(), _ => true));
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = Task.Run(server.WaitForConnection);
        Assert.Equal(BrokerFailure.Untrusted, BrokerInputSink.Probe(name, _ => false));
    }

    [Fact]
    public async Task The_probe_reports_healthy_when_the_owner_is_trusted()
    {
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // The probe hangs up at once, so the server may see the connection end while still accepting ("pipe is being closed"): either way a client connected.
        var accepted = Task.Run(() => { try { server.WaitForConnection(); } catch (IOException) { } });
        Assert.Null(BrokerInputSink.Probe(name, _ => true));
        await accepted.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_broker_that_stops_answering_times_out()
    {
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = Task.Run(server.WaitForConnection);
        using var client = Client(name, timeout: TimeSpan.FromMilliseconds(300));
        var ex = Assert.Throws<BrokerException>(() => client.MoveRelative(1, 1));
        Assert.Equal(BrokerFailure.Timeout, ex.Failure);
        await accepted.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Release_all_does_not_connect_to_a_broker_that_was_never_used()
    {
        using var client = Client(NewPipe());
        client.ReleaseAll(); // nothing to throw, nothing to connect to
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Zero_moves_and_empty_text_send_nothing()
    {
        var name = NewPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var (done, seen) = Serve(server, _ => BrokerReply.Ok);
        using (var client = Client(name))
        {
            client.MoveRelative(0, 0);
            client.Wheel(0, 0);
            client.Text("");
            client.MoveRelative(1, 1);
        }
        await done.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([new BrokerCommand(BrokerOp.Move, 1, 1)], seen);
    }
}

public class BrokerInstallPathTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Theory]
    [InlineData(@"C:\Program Files\Easy Mouse\EasyMouse.Broker.exe", true)]
    [InlineData(@"c:\program files (x86)\Easy Mouse\EasyMouse.Broker.exe", true)]
    [InlineData(@"C:\Program Files\Easy Mouse\..\..\Users\me\EasyMouse.Broker.exe", false)] // dot-dot out of Program Files
    [InlineData(@"C:\Program Files Evil\EasyMouse.Broker.exe", false)]                       // same prefix, other folder
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"D:\Apps\Easy Mouse\EasyMouse.Broker.exe", false)]
    [InlineData(@"C:\Users\me\Downloads\EasyMouse.Broker.exe", false)]
    [InlineData(@"C:\ProgramData\Easy Mouse\EasyMouse.Broker.exe", false)]
    [InlineData(@"EasyMouse.Broker.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_folder_under_program_files_may_run_as_system(string? path, bool expected)
        => Assert.Equal(expected, BrokerTrust.IsProtectedInstallPath(path, Roots));

    [Fact]
    public void No_roots_means_nothing_is_protected()
        => Assert.False(BrokerTrust.IsProtectedInstallPath(@"C:\Program Files\x\a.exe", []));
}

/// <summary>The real pipe options the agent uses, with the real access list (for the current user instead of the console user).</summary>
public class BrokerPipeSetupTests
{
    [Fact]
    public async Task The_acl_d_first_instance_pipe_serves_its_user_and_cannot_be_shadowed()
    {
        var name = "EasyMouse.Broker.Test." + Guid.NewGuid().ToString("N");
        var me = WindowsIdentity.GetCurrent().User!;
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous, 1024, 1024, BrokerTrust.BuildPipeSecurity(me));

        // A second server under the same name (a squatter that came second, or an agent started twice) is refused.
        Assert.ThrowsAny<Exception>(() => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous).Dispose()); // the agent maps both IOException and UnauthorizedAccessException to "cannot create the pipe"

        using var client = new BrokerInputSink(name, () => Environment.TickCount64, _ => true, TimeSpan.FromSeconds(2));
        var accepted = server.WaitForConnectionAsync();
        var send = Task.Run(() => client.MoveRelative(3, 4)); // the client connects lazily on its first command
        await accepted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BrokerReply.Ok, BrokerProtocol.Read(server, out var command));
        Assert.Equal(new BrokerCommand(BrokerOp.Move, 3, 4), command);
        server.WriteByte((byte)BrokerReply.Ok);
        await send.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
