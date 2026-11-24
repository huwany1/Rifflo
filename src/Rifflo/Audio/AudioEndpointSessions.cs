using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Rifflo.Audio;

internal sealed class AudioEndpointSessions : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDeviceNotificationClient changes;
    private readonly Action sessionChanged;
    private readonly Action topologyChanged;
    private readonly List<DeviceSessions> devices = [];

    public AudioEndpointSessions(Action sessionChanged, Action topologyChanged)
    {
        this.sessionChanged = sessionChanged;
        this.topologyChanged = topologyChanged;
        try
        {
            changes = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            changes.DefaultDeviceChanged += OnDefaultChanged;
            changes.DeviceStateChanged += OnDeviceChanged;
            changes.DeviceAdded += OnDeviceChanged;
            changes.DeviceRemoved += OnDeviceChanged;
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }
    }

    public void RefreshDevices()
    {
        ClearDevices();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            var device = new DeviceSessions(endpoint, sessionChanged);
            devices.Add(device);
            device.Start();
        }
    }

    public IReadOnlyList<ActiveAudioSession> ReadActive(bool includeLevels = false)
    {
        var result = new List<ActiveAudioSession>();
        foreach (var device in devices)
            device.AddActive(result, includeLevels);
        return result;
    }

    public bool HasDevices => devices.Count != 0;

    private void OnDefaultChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        if (args.Flow == DataFlow.Render)
            topologyChanged();
    }

    private void OnDeviceChanged(object? sender, EventArgs args) => topologyChanged();

    private void ClearDevices()
    {
        foreach (var device in devices)
            DisposeSafely(device);
        devices.Clear();
    }

    public void Dispose()
    {
        changes.DefaultDeviceChanged -= OnDefaultChanged;
        changes.DeviceStateChanged -= OnDeviceChanged;
        changes.DeviceAdded -= OnDeviceChanged;
        changes.DeviceRemoved -= OnDeviceChanged;
        DisposeSafely(changes);
        ClearDevices();
        DisposeSafely(enumerator);
    }

    private static void DisposeSafely(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private sealed class DeviceSessions(MMDevice device, Action changed) : IDisposable, IAudioSessionEventsHandler
    {
        private readonly object pendingGate = new();
        private readonly Queue<AudioSessionControl> pending = [];
        private readonly Dictionary<string, AudioSessionControl> sessions = new(StringComparer.Ordinal);
        private readonly string endpointId = device.ID;
        private AudioSessionManager? manager;
        private AudioEndpointVolume? endpointVolume;
        private bool disposed;

        public void Start()
        {
            manager = device.AudioSessionManager;
            manager.OnSessionCreated += OnSessionCreated;
            endpointVolume = device.AudioEndpointVolume;
            endpointVolume.OnVolumeNotification += OnEndpointVolumeChanged;

            // GetCount enables IAudioSessionNotification delivery on this MTA worker.
            var collection = manager.Sessions;
            int count = collection.Count;
            for (int index = 0; index < count; index++)
                Register(collection[index]);
        }

        private void OnSessionCreated(object sender, AudioSessionControl session)
        {
            bool wasDisposed;
            lock (pendingGate)
            {
                wasDisposed = disposed;
                if (!wasDisposed)
                    pending.Enqueue(session);
            }
            // Avoid entering any WASAPI COM session APIs on the notification thread.
            if (wasDisposed)
                _ = Task.Run(() => DisposeSafely(session));
            else
                changed();
        }

        private void Register(AudioSessionControl session)
        {
            try
            {
                if (!session.IsSystemSoundsSession)
                {
                    var id = session.GetSessionInstanceIdentifier;
                    if (!sessions.ContainsKey(id))
                    {
                        session.RegisterEventClient(this);
                        sessions.Add(id, session);
                        return;
                    }
                }
            }
            catch (Exception error) { Debug.WriteLine(error); }

            DisposeSafely(session);
        }

        public void AddActive(List<ActiveAudioSession> result, bool includeLevels)
        {
            while (true)
            {
                AudioSessionControl? session;
                lock (pendingGate)
                {
                    if (!pending.TryDequeue(out session))
                        break;
                }
                Register(session);
            }

            var expired = new List<string>();
            var deviceMuted = endpointVolume is not null && (endpointVolume.Mute || endpointVolume.MasterVolumeLevelScalar == 0);
            foreach (var (id, session) in sessions)
            {
                try
                {
                    var state = session.State;
                    if (state == AudioSessionState.AudioSessionStateExpired)
                        expired.Add(id);
                    if (state != AudioSessionState.AudioSessionStateActive)
                        continue;

                    uint pid = session.GetProcessID;
                    if (pid != 0)
                    {
                        bool muted = deviceMuted || session.SimpleAudioVolume.Mute || session.SimpleAudioVolume.Volume == 0;
                        result.Add(new(pid, muted, endpointId, includeLevels ? ReadPeak(session) : null));
                    }
                }
                catch (Exception error) { Debug.WriteLine(error); }
            }

            foreach (var id in expired)
            {
                DisposeSafely(sessions[id]);
                sessions.Remove(id);
            }
        }

        private static float? ReadPeak(AudioSessionControl session)
        {
            try { return session.AudioMeterInformation.MasterPeakValue; }
            catch (Exception error) { Debug.WriteLine(error); return null; }
        }

        public void Dispose()
        {
            lock (pendingGate)
                disposed = true;

            if (manager is not null)
                manager.OnSessionCreated -= OnSessionCreated;
            if (endpointVolume is not null)
                endpointVolume.OnVolumeNotification -= OnEndpointVolumeChanged;
            foreach (var session in sessions.Values)
                DisposeSafely(session);
            sessions.Clear();
            while (pending.TryDequeue(out var session))
                DisposeSafely(session);
            DisposeSafely(device);
        }

        private void OnEndpointVolumeChanged(AudioVolumeNotificationData data) => changed();
        public void OnVolumeChanged(float volume, bool isMuted) => changed();
        public void OnStateChanged(AudioSessionState state) => changed();
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => changed();
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
    }
}
