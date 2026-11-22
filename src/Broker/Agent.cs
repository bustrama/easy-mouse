using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EasyMouse.Platform.Windows;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.Broker;

/// <summary>
/// Runs as SYSTEM inside the signed-in user's session and does the only privileged thing there is: inject input into whichever
/// desktop currently receives it (including the UAC prompt's secure desktop). It serves the one named pipe, one client at a time.
/// Every connection is checked before a byte is read from it (see <see cref="PeerAllowed"/>), every command is validated by
/// <see cref="BrokerProtocol.Read"/>, and every command is rate limited. It never injects while the workstation is locked.
/// Exit codes: 10 not SYSTEM, 11 no user in that session, 12 cannot create the pipe.
/// </summary>
internal static unsafe class Agent
{

    public static int Run(uint session)
    {
        using (var self = WindowsIdentity.GetCurrent())
        {
            // Started by hand by a user, or by an administrator in a console: refuse, so nobody can run this as a stand-in for the service.
            if (self.User is null || !self.User.IsWellKnown(WellKnownSidType.LocalSystemSid)) return 10;
        }
        if (!Native.WTSQueryUserToken(session, out var userToken)) return 11;
        SecurityIdentifier user;
        try
        {
            using var identity = new WindowsIdentity(userToken);
            user = identity.User ?? throw new InvalidOperationException("The session's user has no SID.");
        }
        finally { Native.CloseHandle(userToken); }

        var expectedApp = BrokerTrust.ExpectedAppImage(Environment.ProcessPath!);
        NamedPipeServerStream server;
        try
        {
            // FirstPipeInstance: if something else already owns the name, fail instead of serving next to it.
            server = NamedPipeServerStreamAcl.Create(BrokerProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.FirstPipeInstance, 1024, 1024, BrokerTrust.BuildPipeSecurity(user));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 12;
        }

        using (server)
        {
            var desktop = new InputDesktop();
            using var sink = new SendInputSink();
            while (true)
            {
                try
                {
                    server.WaitForConnection();
                    if (PeerAllowed(server, session, user, expectedApp)) Serve(server, sink, desktop, session);
                }
                catch (IOException) { /* the client went away */ }
                finally
                {
                    LiftEverything(sink, desktop);
                    try { server.Disconnect(); } catch (InvalidOperationException) { }
                }
            }
        }
    }

    /// <summary>The caller must be the installed Easy Mouse App, started by the signed-in user in this session.</summary>
    private static bool PeerAllowed(NamedPipeServerStream server, uint session, SecurityIdentifier user, string? expectedApp)
    {
        if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) || pid == 0) return false;
        if (!Native.ProcessIdToSessionId(pid, out var peerSession) || peerSession != session) return false;
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return false;
        try
        {
            if (!BrokerTrust.IsExpectedImage(ImagePath(process), expectedApp)) return false;
            return Equals(UserOf(process), user);
        }
        finally { Native.CloseHandle(process); }
    }

    private static string? ImagePath(nint process)
    {
        var buffer = stackalloc char[1024];
        var size = 1024u;
        return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static SecurityIdentifier? UserOf(nint process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return null;
        try
        {
            var buffer = stackalloc byte[256];
            if (!Native.GetTokenInformation(token, Native.TokenUser, buffer, 256, out _)) return null;
            return new SecurityIdentifier(*(nint*)buffer); // TOKEN_USER starts with the SID pointer
        }
        finally { Native.CloseHandle(token); }
    }

    private static void Serve(NamedPipeServerStream pipe, SendInputSink sink, InputDesktop desktop, uint session)
    {
        var limiter = new BrokerRateLimiter();
        var lockState = new LockState(session);
        var reply = new byte[1];
        while (true)
        {
            var read = BrokerProtocol.Read(pipe, out var command);
            reply[0] = (byte)(read == BrokerReply.Ok ? Execute(command, sink, desktop, limiter, lockState) : read);
            pipe.Write(reply, 0, 1);
            if (read != BrokerReply.Ok) return; // out of step: drop the connection rather than guess
        }
    }

    private static BrokerReply Execute(in BrokerCommand command, SendInputSink sink, InputDesktop desktop, BrokerRateLimiter limiter, LockState lockState)
    {
        if (!limiter.TryTake(command.Op)) return BrokerReply.RateLimited;
        if (lockState.IsLockedOrUnknown()) return BrokerReply.Locked;
        if (!desktop.AttachToInput()) return BrokerReply.Blocked;
        try
        {
            switch (command.Op)
            {
                case BrokerOp.Move: sink.MoveRelative(command.A, command.B); break;
                case BrokerOp.Button: sink.Button((Core.Protocol.MouseButton)command.A, command.B == 1); break;
                case BrokerOp.Wheel: sink.Wheel(command.A, command.B); break;
                case BrokerOp.Key: sink.Key((ushort)command.A, (Core.Protocol.KeyAction)command.B); break;
                case BrokerOp.Text: sink.Text(command.Text!); break;
                case BrokerOp.ReleaseAll: sink.ReleaseAll(); break;
            }
            return BrokerReply.Ok;
        }
        catch (EasyMouse.Platform.Windows.Native.InputInjectionException) { return BrokerReply.Blocked; }
        catch (Exception) { return BrokerReply.Failed; }
    }

    private static void LiftEverything(SendInputSink sink, InputDesktop desktop)
    {
        try
        {
            if (desktop.AttachToInput()) sink.ReleaseAll();
        }
        catch (Exception) { /* nothing more can be done from here */ }
    }

    /// <summary>
    /// Whether the session is locked, from Terminal Services. Unknown counts as locked: the broker would rather refuse than type
    /// into a lock screen. (Never unlocks, never handles the Windows password.)
    /// </summary>
    private sealed class LockState(uint session)
    {
        // Asked on every command, never cached: the lock screen and the UAC prompt are both on the Winlogon desktop, so a remembered
        // "unlocked" must not outlive the moment the PC locks.
        public bool IsLockedOrUnknown() => Query();

        private bool Query()
        {
            if (!Native.WTSQuerySessionInformation(0, session, Native.WTSSessionInfoEx, out var buffer, out var bytes)) return true;
            try
            {
                // WTSINFOEXW: Level (4) + padding, then WTSINFOEX_LEVEL1: SessionId (4), SessionState (4), SessionFlags (4) at offset 16.
                if (bytes < 20 || Marshal.ReadInt32(buffer, 0) != 1) return true;
                return Marshal.ReadInt32(buffer, 16) != 1; // WTS_SESSIONSTATE_UNLOCK = 1; 0 = lock; anything else unknown
            }
            finally { Native.WTSFreeMemory(buffer); }
        }
    }
}

/// <summary>Attaches the calling thread to whichever desktop currently receives input (the user's, the lock screen, or the UAC prompt's secure desktop).</summary>
internal sealed class InputDesktop
{
    private nint _current;

    public bool AttachToInput()
    {
        var desktop = Native.OpenInputDesktop(0, false, Native.GENERIC_ALL);
        if (desktop == 0) return false;
        if (!Native.SetThreadDesktop(desktop))
        {
            Native.CloseDesktop(desktop);
            return false;
        }
        if (_current != 0) Native.CloseDesktop(_current); // the thread has moved on, so the old handle can close
        _current = desktop;
        return true;
    }
}
