using System.Runtime.InteropServices;

namespace EasyMouse.Broker;

/// <summary>
/// Keeps exactly one <see cref="Agent"/> running as SYSTEM in the active console session: a copy of the service's own token with its
/// session id changed, started on the interactive window station. The agent sits in a job object that kills it when the service's
/// handle closes, so it can never outlive the service. A crash is followed by a restart after a growing pause.
/// </summary>
internal sealed unsafe class AgentSupervisor : IDisposable
{
    private readonly string _exe;
    private readonly nint _job;
    private nint _process;
    private uint _session;
    private long _nextSpawn;
    private int _quickFailures;
    private long _spawnedAt;

    public AgentSupervisor(string exePath)
    {
        _exe = exePath;
        _job = Native.CreateJobObject(0, 0);
        if (_job == 0) throw new InvalidOperationException("Cannot create the job object.");
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        if (!Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            throw new InvalidOperationException("Cannot configure the job object.");
    }

    public void Tick()
    {
        var console = Native.WTSGetActiveConsoleSessionId();
        var now = Environment.TickCount64;

        if (_process != 0 && Native.WaitForSingleObject(_process, 0) == 0) // the agent exited on its own
        {
            Forget();
            _quickFailures = now - _spawnedAt < 10_000 ? _quickFailures + 1 : 0;
            _nextSpawn = now + Math.Min(30_000, 1000L << Math.Min(_quickFailures, 5));
        }
        if (_process != 0 && (console == Native.NoActiveSession || console != _session)) Stop();
        if (_process == 0 && console != Native.NoActiveSession && now >= _nextSpawn)
        {
            if (Spawn(console)) { _session = console; _spawnedAt = now; }
            else _nextSpawn = now + 5000;
        }
    }

    private bool Spawn(uint session)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_DUPLICATE | Native.TOKEN_QUERY | Native.TOKEN_ASSIGN_PRIMARY
            | Native.TOKEN_ADJUST_DEFAULT | Native.TOKEN_ADJUST_SESSIONID, out var own)) return false;
        nint token = 0;
        try
        {
            if (!Native.DuplicateTokenEx(own, Native.MAXIMUM_ALLOWED, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out token)) return false;
            if (!Native.SetTokenInformation(token, Native.TokenSessionId, &session, sizeof(uint))) return false;

            var commandLine = $"\"{_exe}\" --agent --session {session}";
            fixed (char* exe = _exe)
            fixed (char* command = commandLine + "\0")
            fixed (char* desktop = "winsta0\\default")
            {
                var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW), lpDesktop = desktop };
                if (!Native.CreateProcessAsUser(token, exe, command, 0, 0, false, Native.CREATE_NO_WINDOW | Native.CREATE_SUSPENDED, 0, null, startup, out var info))
                    return false;
                // Into the job before the first instruction runs; if that fails the agent must not run unsupervised.
                if (!Native.AssignProcessToJobObject(_job, info.hProcess))
                {
                    Native.TerminateProcess(info.hProcess, 1);
                    Native.CloseHandle(info.hThread);
                    Native.CloseHandle(info.hProcess);
                    return false;
                }
                Native.ResumeThread(info.hThread);
                Native.CloseHandle(info.hThread);
                _process = info.hProcess;
                return true;
            }
        }
        finally
        {
            if (token != 0) Native.CloseHandle(token);
            Native.CloseHandle(own);
        }
    }

    private void Stop()
    {
        if (_process == 0) return;
        Native.TerminateProcess(_process, 0);
        Native.WaitForSingleObject(_process, 3000);
        Forget();
    }

    private void Forget()
    {
        if (_process != 0) Native.CloseHandle(_process);
        _process = 0;
    }

    public void Dispose()
    {
        Stop();
        if (_job != 0) Native.CloseHandle(_job); // KILL_ON_JOB_CLOSE: anything still in the job dies with it
    }
}
