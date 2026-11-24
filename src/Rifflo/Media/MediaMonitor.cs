using System.Diagnostics;
using System.Threading.Channels;
using Windows.Foundation;
using Windows.Media.Control;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Rifflo.Media;

public sealed class MediaMonitor : IDisposable, IAsyncDisposable
{
    // Projected WinRT events can arrive on different threads. Only the reader mutates session state.
    private readonly Channel<Action> updates = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<MediaSession, Session> sessions = [];
    private readonly PlaybackTransition transition = new();
    private SessionManager? manager;
    private Task? worker;
    private Timer? transitionTimer;
    private long nextId;
    private long? currentId;
    private long? selectedId;
    private TrackState published = TrackState.Empty;
    private IReadOnlyList<string> sources = Array.Empty<string>();
    private string preferredSource = "";
    private int disposed;

    public event Action<TrackState>? Changed;
    public event Action? SourcesChanged;

    public IReadOnlyList<string> Sources => Volatile.Read(ref sources);
    internal string? TestSourcePrefix { private get; init; }

    public string PreferredSource
    {
        get => Volatile.Read(ref preferredSource);
        set
        {
            var source = value?.Trim() ?? "";
            if (!MediaSelection.SourceEquals(Interlocked.Exchange(ref preferredSource, source), source))
                PostTimed(PublishSelection);
        }
    }

    public Task StartAsync()
    {
        lock (started)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            worker ??= Task.Run(RunAsync);
            return started.Task;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            manager = await SessionManager.RequestAsync().AsTask(lifetime.Token).ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0)
                return;

            manager.SessionsChanged += SessionsChanged;
            manager.CurrentSessionChanged += CurrentSessionChanged;
            RefreshSessions(Stopwatch.GetTimestamp());
            started.TrySetResult();

            await foreach (var update in updates.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref disposed) != 0)
                    break;

                try { update(); }
                catch (Exception error) { Debug.WriteLine(error); }
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0) { }
        catch (Exception error)
        {
            if (!started.TrySetException(error))
                Debug.WriteLine(error);
        }
        finally
        {
            transitionTimer?.Dispose();
            if (manager is not null)
            {
                IgnoreWinRtFailure(() => manager.SessionsChanged -= SessionsChanged);
                IgnoreWinRtFailure(() => manager.CurrentSessionChanged -= CurrentSessionChanged);
                manager = null;
            }

            foreach (var session in sessions.Values)
                Unsubscribe(session);
            sessions.Clear();
            updates.Writer.TryComplete();
            started.TrySetCanceled();
        }
    }

    private void SessionsChanged(SessionManager sender, SessionsChangedEventArgs args) => PostTimed(RefreshSessions);

    private void CurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) => PostTimed(observedAt =>
    {
        RefreshCurrent();
        PublishSelection(observedAt);
    });

    private void RefreshSessions(long observedAt)
    {
        var available = manager!.GetSessions().ToHashSet()
            .Select(handle => (Handle: handle, Source: sessions.TryGetValue(handle, out var existing) ? existing.Source : ReadSource(handle)))
            .Where(session => TestSourcePrefix is not { Length: > 0 } prefix || session.Source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(session => session.Handle, session => session.Source);
        var previous = sessions.Values.Select(session => session.Candidate).ToArray();
        var incoming = available.Values.ToLookup(source => source, StringComparer.OrdinalIgnoreCase);
        var removed = sessions.Values.Where(session => !available.ContainsKey(session.Handle)).ToDictionary(session => session.Id);
        foreach (var session in removed.Values)
        {
            Unsubscribe(session);
            sessions.Remove(session.Handle);
        }

        foreach (var (handle, source) in available)
        {
            if (sessions.ContainsKey(handle))
                continue;

            var previousPlayback = PlayState.None;
            // Windows can recreate all wrappers when one app joins or leaves. Preserve the stable
            // selection when the old wrapper still reports an open session for the unique source.
            Session? session = null;
            if (incoming[source].Count() == 1 && MediaSelection.FindUniqueSource(previous, source) is { } previousId &&
                removed.TryGetValue(previousId, out var continuing))
            {
                previousPlayback = ReadPlayback(continuing.Handle);
                if (previousPlayback is not (PlayState.Closed or PlayState.None))
                {
                    session = continuing;
                    session.Handle = handle;
                }
            }
            session ??= new Session(handle, ++nextId, source);

            sessions.Add(handle, session);
            IgnoreWinRtFailure(() => handle.PlaybackInfoChanged += PlaybackInfoChanged);
            IgnoreWinRtFailure(() => handle.MediaPropertiesChanged += MediaPropertiesChanged);
            session.State = ReadPlayback(handle);

            // A previously closed wrapper gives no evidence that the old title still applies.
            bool retainingMetadata = session.State is PlayState.Playing or PlayState.Paused && session.State == previousPlayback;
            if (!retainingMetadata)
                session.Title = session.Artist = "";
            ReadMetadata(session, retainingMetadata);
        }

        var nextSources = sessions.Values.Select(session => session.Source)
            .Where(source => source.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!sources.SequenceEqual(nextSources, StringComparer.OrdinalIgnoreCase))
        {
            Volatile.Write(ref sources, Array.AsReadOnly(nextSources));
            SourcesChanged?.Invoke();
        }

        RefreshCurrent();
        PublishSelection(observedAt);
    }

    private void RefreshCurrent()
    {
        var current = manager!.GetCurrentSession();
        if (current is null)
        {
            currentId = null;
            return;
        }

        // GSMTC can return a different WinRT object for the system-current session. Its app
        // identity only tells us which tracked session is current when the app has exactly one.
        currentId = sessions.TryGetValue(current, out var tracked) ? tracked.Id :
            MediaSelection.FindUniqueSource(sessions.Values.Select(session => session.Candidate), ReadSource(current));
    }

    private void PlaybackInfoChanged(MediaSession sender, PlaybackInfoChangedEventArgs args) => PostTimed(observedAt =>
    {
        if (!sessions.TryGetValue(sender, out var session))
            return;

        session.State = ReadPlayback(sender);
        PublishSelection(observedAt);
    });

    private void MediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) => Post(() =>
    {
        if (sessions.TryGetValue(sender, out var session))
            ReadMetadata(session);
    });

    private void ReadMetadata(Session session, bool retainingMetadata = false)
    {
        var generation = session.Generation.Begin();
        CancelMetadata(session);
        try
        {
            session.Pending = session.Handle.TryGetMediaPropertiesAsync();
            _ = ReadMetadataAsync(session, generation, session.Pending, retainingMetadata);
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            PostTimed(observedAt => CompleteMetadata(session, generation, "", "", "", 0, observedAt));
        }
    }

    private async Task ReadMetadataAsync(Session session, long generation,
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties> pending, bool retainingMetadata)
    {
        var title = "";
        var artist = "";
        var album = "";
        var number = 0;
        try
        {
            var properties = retainingMetadata
                ? await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)
                : await pending;
            title = properties?.Title?.Trim() ?? "";
            artist = properties?.Artist?.Trim() ?? "";
            album = properties?.AlbumTitle?.Trim() ?? "";
            number = properties?.TrackNumber ?? 0;
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { IgnoreWinRtFailure(pending.Cancel); }
        catch (Exception error) { Debug.WriteLine(error); }

        PostTimed(observedAt => CompleteMetadata(session, generation, title, artist, album, number, observedAt));
    }

    private void CompleteMetadata(Session session, long generation, string title, string artist, string album, int number, long observedAt)
    {
        if (sessions.TryGetValue(session.Handle, out var tracked) && ReferenceEquals(tracked, session) &&
            session.Generation.IsCurrent(generation))
        {
            session.Pending = null;
            if (title.Length != 0)
            {
                session.CaptionIdentity.Observe(session.LastCaption == (title, artist), album, number);
                session.LastCaption = (title, artist);
            }
            session.Title = title;
            session.Artist = artist;
            PublishSelection(observedAt);
        }
    }

    private void PublishSelection(long observedAt)
    {
        if (transition.Expire(observedAt) is { } expired)
        {
            StopTransitionTimer();
            Publish(expired);
        }

        var candidates = sessions.Values.Select(session => session.Candidate).ToArray();
        var held = candidates.FirstOrDefault(transition.CanHold);
        var selected = MediaSelection.Pick(candidates, PreferredSource, currentId, selectedId, held.Id);
        selectedId = selected?.Id;

        var next = TrackState.Empty;
        if (selected is { } candidate)
        {
            var session = sessions.Values.First(entry => entry.Id == candidate.Id);
            next = new TrackState(session.Source, session.Title, session.Artist, session.State)
            {
                SessionId = session.Id,
                AudioEligible = MediaSelection.CanIdentifyAudio(candidate with { State = PlayState.Playing }, candidates),
                AudioCompetition = MediaSelection.FindAudioCompetition(candidate, candidates),
                CaptionRevision = session.CaptionIdentity.Revision
            };
        }

        var previous = transition.Current;
        next = transition.Observe(next, observedAt);
        if (next.IsTransition && !previous.IsTransition)
            ScheduleTransition(transition.Generation);
        else if (previous.IsTransition && !next.IsTransition)
            StopTransitionTimer();
        Publish(next);
    }

    private void ScheduleTransition(long generation)
    {
        StopTransitionTimer();
        var remaining = transition.Remaining(Stopwatch.GetTimestamp());
        var due = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)));
        transitionTimer = new Timer(_ => PostTimed(observedAt => CheckTransition(generation, observedAt)), null, due, Timeout.InfiniteTimeSpan);
    }

    private void StopTransitionTimer()
    {
        transitionTimer?.Dispose();
        transitionTimer = null;
    }

    private void CheckTransition(long generation, long observedAt)
    {
        if (!transition.Current.IsTransition || transition.Generation != generation)
            return;

        if (transition.Expire(observedAt, generation) is { } expired)
        {
            StopTransitionTimer();
            Publish(expired);
        }
        else
            ScheduleTransition(generation);
    }

    private void Publish(TrackState next)
    {
        if (ReferenceEquals(next, published) || Volatile.Read(ref disposed) != 0)
            return;

        published = next;
        Changed?.Invoke(next);
    }

    private void Unsubscribe(Session session)
    {
        IgnoreWinRtFailure(() => session.Handle.PlaybackInfoChanged -= PlaybackInfoChanged);
        IgnoreWinRtFailure(() => session.Handle.MediaPropertiesChanged -= MediaPropertiesChanged);
        CancelMetadata(session);
    }

    private static void CancelMetadata(Session session)
    {
        if (session.Pending is { } pending)
            IgnoreWinRtFailure(pending.Cancel);
        session.Pending = null;
    }

    private static PlayState ReadPlayback(MediaSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus switch
            {
                PlaybackStatus.Playing => PlayState.Playing,
                PlaybackStatus.Paused => PlayState.Paused,
                PlaybackStatus.Stopped => PlayState.Stopped,
                PlaybackStatus.Changing => PlayState.Changing,
                PlaybackStatus.Closed => PlayState.Closed,
                _ => PlayState.None
            };
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            return PlayState.None;
        }
    }

    private static string ReadSource(MediaSession session)
    {
        try { return session.SourceAppUserModelId?.Trim() ?? ""; }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            return "";
        }
    }

    private static void IgnoreWinRtFailure(Action action)
    {
        try { action(); }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private void Post(Action update)
    {
        if (Volatile.Read(ref disposed) == 0)
            updates.Writer.TryWrite(update);
    }

    private void PostTimed(Action<long> update)
    {
        var observedAt = Stopwatch.GetTimestamp();
        Post(() => update(observedAt));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        updates.Writer.TryComplete();
        started.TrySetCanceled();
        lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? active;
        lock (started)
            active = worker;
        if (active is not null)
            await active.ConfigureAwait(false);
    }

    private sealed class Session(MediaSession handle, long id, string source)
    {
        public MediaSession Handle { get; set; } = handle;
        public long Id { get; } = id;
        public string Source { get; } = source;
        public PlayState State { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public (string Title, string Artist) LastCaption { get; set; } = ("", "");
        public IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties>? Pending { get; set; }
        public MetadataGeneration Generation;
        public CaptionTrackIdentity CaptionIdentity;

        public MediaCandidate Candidate => new(Id, Source, State, Title.Length != 0);
    }
}
