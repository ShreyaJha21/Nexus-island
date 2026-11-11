using System.Runtime.InteropServices;

namespace Nexus.Shell.Native;

/// <summary>
/// Which processes are playing sound right now, from Windows' audio mixer (the same list as the
/// volume mixer). A ringing or connected call plays sound, so this is a cheap way to know when it
/// is worth looking at a calling app's windows. About a millisecond per check.
/// </summary>
internal static class AudioSessions
{
    private const int ERender = 0;
    private const int RoleConsole = 0;
    private const int RoleCommunications = 2;
    private const int ClsctxAll = 0x17;
    private const int StateActive = 1;

    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);
    private static IMMDeviceEnumerator? _enumerator;
    private static readonly List<IAudioSessionManager2> Managers = [];
    private static DateTime _managersAt;

    /// <summary>
    /// True when one of <paramref name="pids"/> has an active sound session on the default or the
    /// communications speaker. The connection to each speaker is kept and renewed every 30 s (in case
    /// the default device changed), so a check only lists sessions. Call it from one thread.
    /// </summary>
    public static bool AnyActive(IReadOnlySet<int> pids)
    {
        if (pids.Count == 0)
        {
            return false;
        }

        try
        {
            if (DateTime.UtcNow - _managersAt > Refresh)
            {
                Connect();
            }

            foreach (var manager in Managers)
            {
                if (Active(manager, pids))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _managersAt = default; // reconnect next time
            return false;
        }
    }

    private static void Connect()
    {
        _managersAt = DateTime.UtcNow;
        foreach (var old in Managers)
        {
            Release(old);
        }

        Managers.Clear();
        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();

        var seen = new HashSet<string>();
        foreach (var role in new[] { RoleConsole, RoleCommunications })
        {
            IMMDevice? device = null;
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(ERender, role, out device) != 0 || device is null ||
                    device.GetId(out var id) != 0 || !seen.Add(id))
                {
                    continue; // no such device, or the same speaker as the other role
                }

                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var manager) == 0)
                {
                    Managers.Add((IAudioSessionManager2)manager);
                }
            }
            finally
            {
                Release(device);
            }
        }
    }

    private static bool Active(IAudioSessionManager2 manager, IReadOnlySet<int> pids)
    {
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions.GetCount(out var count) != 0)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0)
                {
                    continue;
                }

                try
                {
                    if (session.GetState(out var state) == 0 && state == StateActive &&
                        session.GetProcessId(out var pid) == 0 && pids.Contains((int)pid))
                    {
                        return true;
                    }
                }
                finally
                {
                    Release(session);
                }
            }

            return false;
        }
        finally
        {
            Release(sessions);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
