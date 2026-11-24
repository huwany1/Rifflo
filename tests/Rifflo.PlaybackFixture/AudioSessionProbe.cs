using System.Runtime.InteropServices;

internal static class AudioSessionProbe
{
    public static object Read()
    {
        var sessions = GetDefaultSessions(out var id);
        sessions.GetCount(out var count);
        var observations = new List<object>();
        for (var index = 0; index < count; index++)
        {
            sessions.GetSession(index, out var session);
            var control = (ISessionControl)session;
            control.GetProcessId(out var pid);
            control.GetState(out var state);
            control.GetSessionIdentifier(out var identifierPointer);
            var identifier = Marshal.PtrToStringUni(identifierPointer);
            Marshal.FreeCoTaskMem(identifierPointer);
            var peak = 0f;
            ((IAudioMeter)session).GetPeakValue(out peak);
            ((ISimpleAudioVolume)session).GetMute(out var muted);
            observations.Add(new { pid, state, peak, muted, identifier });
        }

        return new { endpointId = id, sessions = observations };
    }

    public static object SetMute(uint pid, bool muted)
    {
        var sessions = GetDefaultSessions(out var id);
        sessions.GetCount(out var count);
        var changed = 0;
        var context = Guid.Empty;
        for (var index = 0; index < count; index++)
        {
            sessions.GetSession(index, out var session);
            var control = (ISessionControl)session;
            control.GetProcessId(out var sessionPid);
            control.GetState(out var state);
            if (sessionPid != pid || state != 1)
                continue;

            var volume = (ISimpleAudioVolume)session;
            volume.SetMute(muted, in context);
            volume.GetMute(out var actual);
            if (actual == muted)
                changed++;
        }

        return new { endpointId = id, pid, muted, changed };
    }

    private static ISessionEnumerator GetDefaultSessions(out string id)
    {
        var devices = (IDeviceEnumerator)new DeviceEnumerator();
        devices.GetDefaultAudioEndpoint(0, 1, out var device);
        device.GetId(out id);
        var managerId = typeof(ISessionManager).GUID;
        device.Activate(in managerId, 23, 0, out var managerObject);
        ((ISessionManager)managerObject).GetSessionEnumerator(out var sessions);
        return sessions;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out nint collection);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(in Guid iid, uint contexts, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int mode, out nint store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManager
    {
        void GetAudioSessionControl(in Guid id, uint flags, out nint control);
        void GetSimpleAudioVolume(in Guid id, uint flags, out nint volume);
        void GetSessionEnumerator(out ISessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionControl
    {
        void GetState(out int state);
        void GetDisplayName(out nint name);
        void SetDisplayName(nint name, in Guid eventContext);
        void GetIconPath(out nint path);
        void SetIconPath(nint path, in Guid eventContext);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(in Guid group, in Guid eventContext);
        void RegisterAudioSessionNotification(nint client);
        void UnregisterAudioSessionNotification(nint client);
        void GetSessionIdentifier(out nint id);
        void GetSessionInstanceIdentifier(out nint id);
        void GetProcessId(out uint processId);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeter
    {
        void GetPeakValue(out float peak);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float volume, in Guid eventContext);
        void GetMasterVolume(out float volume);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, in Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
