using System.Diagnostics;
using Rifflo.Media;

namespace Rifflo.Audio;

internal sealed record AudioTiming(int MatchGrace, int ActivationTimeout, int FirstPacketGrace, int PacketGrace,
    int EvidenceInterval, int WaitingInterval, int RetryStep, int IdentityInterval, int CheckInterval)
{
    public static AudioTiming Default { get; } = new(3000, 3000, 3000, 5000, 500, 500, 2000, 5000, 125);

    public int RetryDelay(int failures) => WaitingInterval * 2 + RetryStep * Math.Min(4, failures);
}

internal sealed record AudioObservation(AudioMatch Match, IReadOnlyList<ActiveAudioSession> Sessions);

internal interface IAudioSource : IDisposable
{
    AudioObservation Inspect(string source, AudioCompetition competition, int topology, bool includeLevels, CancellationToken token);
}

internal delegate void AudioPacket(ReadOnlySpan<byte> data, bool silent, bool discontinuity, long capturedAt);

internal interface IAudioRecorder : IAsyncDisposable
{
    event AudioPacket? Packet;
    event Action? Stopped;
    bool IsAlive { get; }
    void Start();
}

public sealed partial class AudioMonitor : IDisposable, IAsyncDisposable
{
    public const int BandCount = 16;

    private readonly object gate = new();
    // Native COM calls and recorder activation can outlive a timed-out managed wait.
    private readonly SemaphoreSlim nativeSlots = new(4, 4);
    private readonly Func<Action, Action, IAudioSource> createSource;
    private readonly Func<ProcessIdentity, CancellationToken, Task<IAudioRecorder?>> openRecorder;
    private readonly AudioTiming timing;
    private readonly bool collectDiagnostics;
    private Published view = new(new(AudioCaptureState.Idle, AudioCaptureReason.None, false, "", 0));
    private TrackState? mediaSelection;
    private Request? current;
    private Task worker = Task.CompletedTask;
    private bool disposed;
    private long generation;
    private long packetCount;
    private long fftCount;
    private long matchAttemptCount;
    private long captureAttemptCount;
    private long nativeOpenCount;
    private long sampleGeneration;
    private float bandWakeThreshold;

    internal AudioMonitor(Func<Action, Action, IAudioSource> createSource,
        Func<ProcessIdentity, CancellationToken, Task<IAudioRecorder?>> openRecorder, AudioTiming timing, bool collectDiagnostics = false)
    {
        this.createSource = createSource;
        this.openRecorder = openRecorder;
        this.timing = timing;
        this.collectDiagnostics = collectDiagnostics;
    }

    public event Action? StatusChanged;
    internal event Action? BandsAvailable;
    internal event Action<AudioCaptureEvent>? Trace;
    public AudioCaptureStatus CaptureStatus => Volatile.Read(ref view).Status;

    public bool Capturing
    {
        get
        {
            var snapshot = Volatile.Read(ref view);
            return snapshot.Status.State == AudioCaptureState.Live && snapshot.Capture is { IsApproved: true } capture &&
                capture.Analyzer.HasSignal(Stopwatch.GetTimestamp());
        }
    }

    public string Status => CaptureStatus switch
    {
        { State: AudioCaptureState.Connecting } => "正在连接播放器音频",
        { State: AudioCaptureState.Live } => "正在采集当前播放器的音频",
        { State: AudioCaptureState.Muted } => "当前播放器的音频已静音",
        { Reason: AudioCaptureReason.AmbiguousSource } => "无法唯一确认播放器音频来源",
        { Reason: AudioCaptureReason.UntrustedIdentity } => "无法验证播放器程序的签名",
        { Reason: AudioCaptureReason.SourceUnavailable } => "无法确认播放器音频来源",
        { State: AudioCaptureState.Fallback } => "当前播放器音频采集不可用",
        _ => "未采集音频"
    };

    public long PacketCount => Interlocked.Read(ref packetCount);
    public long FftCount => Interlocked.Read(ref fftCount);
    public long MatchAttemptCount => Interlocked.Read(ref matchAttemptCount);
    public long CaptureAttemptCount => Interlocked.Read(ref captureAttemptCount);

    internal AudioDiagnosticSnapshot? ReadDiagnostics()
    {
        var snapshot = Volatile.Read(ref view);
        if (snapshot.Status.State == AudioCaptureState.Idle)
            return null;

        var match = snapshot.Observation?.Match;
        var target = match?.Process;
        var instance = match?.Instance ?? target;
        return new(snapshot.Status.Source, match?.State.ToString(), match?.Basis.ToString(),
            match?.Reason.ToString() ?? snapshot.Status.Reason.ToString(), target?.Id, target?.ImagePath, target?.CreatedAt,
            instance?.Id, instance?.ImagePath, match?.RegistrationTarget, snapshot.Status.Recording,
            snapshot.Capture?.IsApproved == true, snapshot.Observation?.Sessions ?? [], snapshot.Capture?.ConnectionId,
            Interlocked.Read(ref nativeOpenCount), snapshot.Capture?.SampleGeneration, match?.EndpointIds ?? []);
    }

    public Task SelectAsync(string source, CancellationToken token) => SelectAsync(source, AudioCompetition.Empty, token);

    internal Task SelectAsync(string source, AudioCompetition competition, CancellationToken token, long? expectedSessionId = null)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled(token);
        if (string.IsNullOrWhiteSpace(source))
        {
            Stop();
            return Task.CompletedTask;
        }

        Request next;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!CanSelect(source, competition) || (expectedSessionId.HasValue && expectedSessionId != mediaSelection?.SessionId))
                return Task.CompletedTask;

            if (current is { IsCurrent: true } selected && string.Equals(selected.Source, source, StringComparison.OrdinalIgnoreCase) &&
                selected.Competition.Equals(competition))
                return selected.Ready.Task.WaitAsync(token);

            next = new(this, source, competition, mediaSelection?.SessionId ?? 0, ++generation, token);
            ClearRequest(new(new(AudioCaptureState.Connecting, AudioCaptureReason.None, false, source, next.Generation)));
            Volatile.Write(ref current, next);
            var run = Task.Run(() => RunAsync(next));
            worker = worker.IsCompletedSuccessfully ? run : Task.WhenAll(worker, run);
        }

        Notify();
        return next.Ready.Task;
    }

    public void Stop() => Cancel(null);

    internal void ObserveMediaSelection(TrackState state)
    {
        lock (gate)
        {
            if (disposed)
                return;

            mediaSelection = state;
            if (current is not { } selected)
                return;

            var now = Stopwatch.GetTimestamp();
            var observedAt = state.ObservedAt == 0 ? now : state.ObservedAt;
            var transition = selected.Transition;
            if (MatchesSelection(selected, state))
            {
                if (state.IsTransition && view is { Status.State: AudioCaptureState.Live, Capture: { } capture } &&
                    capture.IsAlive)
                {
                    if (transition is { PlayingAt: 0 })
                    {
                        if (now < transition.Deadline)
                            return;
                    }
                    else if (now < TrackTransition.DeadlineFrom(observedAt))
                    {
                        transition = new(observedAt);
                        selected.SetTransition(transition);
                        capture.SuspendForTransition(now);
                        _ = ExpireTransitionAsync(selected, transition);
                        return;
                    }
                }
                else if (CanSelect(selected.Source, selected.Competition))
                {
                    if (transition is null)
                        return;
                    if (now < transition.Deadline)
                    {
                        if (transition.PlayingAt == 0)
                            selected.ResumeTransition(transition, observedAt);
                        return;
                    }
                }
            }

            ClearRequest(IdleView());
        }

        Notify();
    }

    private bool CanSelect(string source, AudioCompetition competition) => mediaSelection is null ||
        (mediaSelection is { State: PlayState.Playing, AudioEligible: true, IsTransition: false } observed &&
            !string.IsNullOrWhiteSpace(observed.Title) && MediaSelection.SourceEquals(source, observed.Source) &&
            observed.AudioCompetition.Equals(competition));

    private static bool MatchesSelection(Request request, TrackState state) => state.AudioEligible &&
        state.SessionId == request.SessionId && MediaSelection.SourceEquals(request.Source, state.Source) &&
        state.AudioCompetition.Equals(request.Competition);

    private async Task ExpireTransitionAsync(Request request, TrackTransition transition)
    {
        try
        {
            while (Stopwatch.GetTimestamp() < transition.Deadline)
            {
                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), transition.Deadline).TotalMilliseconds;
                await Task.Delay(Math.Max(1, (int)Math.Ceiling(remaining)), request.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { return; }

        CancelTransition(request, transition);
    }

    private void CancelTransition(Request request, TrackTransition? expected = null, long? evidence = null, Capture? capture = null)
    {
        lock (gate)
        {
            if (!request.IsCurrent || (evidence.HasValue && request.Evidence != evidence) || request.Transition is not { } transition ||
                (expected is not null && !ReferenceEquals(expected, transition)) ||
                (capture is not null && !ReferenceEquals(capture, view.Capture)))
                return;
            ClearRequest(IdleView());
        }

        Notify();
    }

    private bool Approve(Request request, Capture capture, long evidence, int topology)
    {
        lock (gate)
        {
            if (!request.IsCurrent || request.Evidence != evidence || request.Topology != topology)
                return false;

            if (request.Transition is not { } transition)
                return capture.Approve(evidence);
            if (transition.PlayingAt == 0 || Stopwatch.GetTimestamp() >= transition.Deadline || !capture.IsAlive)
                return false;

            if (!capture.Approve(evidence, transition.PlayingAt))
                return false;
            request.CompleteTransition();
            return true;
        }
    }

    private void Cancel(Request? expected)
    {
        lock (gate)
        {
            if (current is null || (expected is not null && !ReferenceEquals(current, expected)))
                return;

            ClearRequest(IdleView());
        }

        Notify();
    }

    private Published IdleView() => new(new(AudioCaptureState.Idle, AudioCaptureReason.None, false, "", generation));

    private void ClearRequest(Published replacement)
    {
        var previous = current;
        var oldCapture = view.Capture;
        Volatile.Write(ref current, null);
        Volatile.Write(ref view, replacement);
        if (previous is null)
            return;

        oldCapture?.Release();
        previous.Ready.TrySetCanceled();
        previous.Cancellation.Cancel();
    }

    public void ReadBands(float[] destination, double sensitivity) =>
        ReadBands(destination, sensitivity, Stopwatch.GetTimestamp());

    internal bool ReadBands(float[] destination, double sensitivity, long now) =>
        ReadBands(destination, sensitivity, now, out _);

    internal bool ReadBands(float[] destination, double sensitivity, long now, out long readGeneration)
    {
        readGeneration = 0;
        var snapshot = Volatile.Read(ref view);
        if (snapshot.Status.State != AudioCaptureState.Live || snapshot.Capture is not { } capture)
        {
            Array.Clear(destination);
            return false;
        }

        var active = capture.ReadBands(destination, sensitivity, now, out var approved, out readGeneration);
        var latest = Volatile.Read(ref view);
        if (!ReferenceEquals(capture, latest.Capture) || latest.Status.State != AudioCaptureState.Live ||
            (approved && !capture.IsApproved))
        {
            Array.Clear(destination);
            readGeneration = 0;
            return false;
        }

        return active;
    }

    internal void BandsSubmitted(long readGeneration)
    {
        if (readGeneration != 0)
            Volatile.Read(ref view).Capture?.BandsSubmitted(readGeneration);
    }

    internal void WaitForBands(double sensitivity)
    {
        Volatile.Write(ref bandWakeThreshold, SpectrumEnvelope.DisplayThreshold(sensitivity));
        var snapshot = Volatile.Read(ref view);
        if (snapshot is { Status.State: AudioCaptureState.Live, Capture: { } capture })
            TryWakeForBands(capture, Stopwatch.GetTimestamp());
    }

    internal void CancelBandWait() => Volatile.Write(ref bandWakeThreshold, 0);

    private void TryWakeForBands(Capture capture, long now)
    {
        var threshold = Volatile.Read(ref bandWakeThreshold);
        if (threshold > 0 && capture.IsApproved && capture.Analyzer.HasDisplaySignal(threshold, now) &&
            Interlocked.CompareExchange(ref bandWakeThreshold, 0, threshold) == threshold)
            BandsAvailable?.Invoke();
    }

    private async Task RunAsync(Request request)
    {
        using (request.ExternalToken.UnsafeRegister(static state =>
        {
            var selection = (Request)state!;
            selection.Owner.Cancel(selection);
        }, request))
        {
            var selection = new Selection(this, request);
            try { await selection.RunAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!request.IsCurrent) { }
            finally
            {
                await selection.CloseAsync().ConfigureAwait(false);
                Cancel(request);
                lock (gate)
                    request.Cancellation.Dispose();
            }
        }
    }

    private Task<T> StartNative<T>(Func<Task<T>> action)
    {
        if (!nativeSlots.Wait(0))
            throw new InvalidOperationException("All audio operation slots are still in use.");

        return Task.Run(async () =>
        {
            try { return await action().ConfigureAwait(false); }
            finally { nativeSlots.Release(); }
        });
    }

    private void Publish(Request request, AudioCaptureState state, AudioCaptureReason reason,
        Capture? capture, AudioObservation? observation)
    {
        bool changed;
        lock (gate)
        {
            if (!request.IsCurrent || request.Transition is not null)
                return;

            var status = new AudioCaptureStatus(state, reason, capture is not null, request.Source, request.Generation);
            changed = view.Status != status;
            Volatile.Write(ref view, new(status, capture, observation));
            if (state is AudioCaptureState.Live or AudioCaptureState.Muted or AudioCaptureState.Fallback)
                request.Ready.TrySetResult();
        }

        if (changed)
            Notify();
    }

    private void Notify()
    {
        try { StatusChanged?.Invoke(); }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private void EmitTrace(string kind, Capture capture, long? at = null, bool? silent = null, int? peakBand = null)
    {
        try
        {
            Trace?.Invoke(new(kind, at ?? Stopwatch.GetTimestamp(), capture.Source, capture.RequestGeneration,
                capture.SampleGeneration, capture.ConnectionId, Interlocked.Read(ref nativeOpenCount), capture.Process.Id,
                capture.Process.CreatedAt, silent, peakBand, capture.EndpointIds));
        }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        Stop();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await worker.ConfigureAwait(false);
    }

    private sealed record Published(AudioCaptureStatus Status, Capture? Capture = null, AudioObservation? Observation = null);

    private sealed class TrackTransition(long startedAt)
    {
        private long playingAt;

        public long Deadline { get; } = DeadlineFrom(startedAt);
        public long PlayingAt => Volatile.Read(ref playingAt);

        public void Resume(long at) => Volatile.Write(ref playingAt, at);

        public static long DeadlineFrom(long start) => start + Stopwatch.Frequency * TrackState.TransitionMilliseconds / 1000;
    }

    private sealed class Request
    {
        private readonly SemaphoreSlim wake = new(0, 1);
        private long evidence;
        private int topology;
        private TrackTransition? transition;

        public Request(AudioMonitor owner, string source, AudioCompetition competition, long sessionId, long generation,
            CancellationToken externalToken)
        {
            Owner = owner;
            Source = source;
            Competition = competition;
            SessionId = sessionId;
            Generation = generation;
            ExternalToken = externalToken;
            Token = Cancellation.Token;
        }

        public AudioMonitor Owner { get; }
        public string Source { get; }
        public AudioCompetition Competition { get; }
        public long SessionId { get; }
        public long Generation { get; }
        public CancellationToken ExternalToken { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCurrent => ReferenceEquals(this, Volatile.Read(ref Owner.current)) && !ExternalToken.IsCancellationRequested;
        public long Evidence => Volatile.Read(ref evidence);
        public int Topology => Volatile.Read(ref topology);
        public TrackTransition? Transition => Volatile.Read(ref transition);

        public void SetTransition(TrackTransition next)
        {
            Interlocked.Increment(ref evidence);
            Volatile.Write(ref transition, next);
            Signal();
        }

        public void ResumeTransition(TrackTransition retained, long at)
        {
            Interlocked.Increment(ref evidence);
            retained.Resume(at);
            Signal();
        }

        public void CompleteTransition() => Volatile.Write(ref transition, null);

        public void Observe(bool devicesChanged)
        {
            lock (Owner.gate)
            {
                if (!IsCurrent)
                    return;

                Interlocked.Increment(ref evidence);
                if (devicesChanged)
                    Interlocked.Increment(ref topology);
            }
            if (devicesChanged)
                Owner.CancelTransition(this);
            Signal();
        }

        public void Signal()
        {
            if (wake.CurrentCount != 0)
                return;
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        public Task WaitAsync(int milliseconds) => wake.WaitAsync(milliseconds, Token);
    }

    private sealed class Selection(AudioMonitor owner, Request request)
    {
        private const int UnfinishedSourceLimit = 2;
        private readonly AudioTiming timing = owner.timing;
        private readonly List<Task> retiredSources = [];
        private SourceLease? source;
        private Task? abandonedRecorder;
        private Task? drainingRecorder;
        private Capture? capture;
        private AudioObservation? observation;
        private AudioCaptureState state = AudioCaptureState.Connecting;
        private AudioCaptureReason reason;
        private AudioCaptureReason missingReason = AudioCaptureReason.MatchTimeout;
        private long waitingUntil = Environment.TickCount64 + owner.timing.MatchGrace;
        private long nextMatch;
        private long lastAttempt = long.MinValue / 2;
        private long inspectedEvidence = -1;
        private long expiryRecheck = long.MinValue;
        private int topology;
        private int failures;

        public async Task RunAsync()
        {
            while (request.IsCurrent)
            {
                var now = Environment.TickCount64;
                var revision = request.Topology;
                if (revision != topology)
                {
                    DropCapture();
                    observation = null;
                    topology = revision;
                    waitingUntil = now + timing.MatchGrace;
                    nextMatch = 0;
                    if (state != AudioCaptureState.Fallback)
                        ChangeState(AudioCaptureState.Connecting);
                }

                var evidence = request.Evidence;
                var transition = request.Transition;
                bool newEvidence = evidence != inspectedEvidence;
                bool evidenceDue = newEvidence && (transition is not null || now - lastAttempt >= timing.EvidenceInterval);
                bool matchDue = now >= nextMatch || evidenceDue;
                CheckHealth(now, newEvidence, matchDue);
                if (!request.IsCurrent)
                    return;
                if (state == AudioCaptureState.Connecting && capture is null && now >= waitingUntil)
                    Fail(missingReason);

                if (now >= nextMatch || evidenceDue)
                {
                    await MatchAsync(now, evidence).ConfigureAwait(false);
                    continue;
                }
                var delay = request.Transition is { PlayingAt: > 0 }
                    ? Math.Min(timing.CheckInterval, 25) : timing.CheckInterval;
                await request.WaitAsync(delay).ConfigureAwait(false);
            }
        }

        private void CheckHealth(long now, bool hasNewEvidence, bool matchDue)
        {
            if (capture is not { } currentCapture)
                return;

            if (!currentCapture.IsAlive)
            {
                Fail(AudioCaptureReason.CaptureFailed);
                return;
            }

            if (request.Transition is not null)
                return;

            var received = currentCapture.LastPacket;
            var deadline = received == long.MinValue
                ? currentCapture.StartedAt + timing.FirstPacketGrace
                : received + timing.PacketGrace;
            if (now >= deadline)
            {
                // Recheck a new endpoint event, which can explicitly identify muted audio.
                if (hasNewEvidence && expiryRecheck != deadline)
                {
                    if (matchDue)
                        expiryRecheck = deadline;
                    return;
                }

                Fail(AudioCaptureReason.NoPackets);
                return;
            }

            if (received != long.MinValue && currentCapture.IsApproved)
            {
                failures = 0;
                if (state != AudioCaptureState.Live)
                    ChangeState(AudioCaptureState.Live);
            }
        }

        private async Task MatchAsync(long now, long evidence)
        {
            long wait = state == AudioCaptureState.Connecting && capture is null ? waitingUntil - now : timing.MatchGrace;
            if (request.Transition is { } retained)
                wait = Math.Min(wait, (long)Math.Ceiling(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), retained.Deadline).TotalMilliseconds));
            int inspectBudget = (int)Math.Clamp(wait, 1, timing.MatchGrace);
            lastAttempt = now;
            inspectedEvidence = evidence;
            nextMatch = now + timing.EvidenceInterval;
            AudioObservation inspected;
            try { inspected = await InspectAsync(inspectBudget).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (IsValid(evidence))
                {
                    observation = null;
                    Fail(AudioCaptureReason.MatchTimeout);
                }
                return;
            }
            catch (Exception error) when (!request.Token.IsCancellationRequested)
            {
                Debug.WriteLine(error);
                if (IsValid(evidence))
                {
                    observation = null;
                    Fail(AudioCaptureReason.CaptureFailed);
                }
                return;
            }

            if (!IsValid(evidence))
                return;

            observation = inspected;
            var match = inspected.Match;
            if (request.Transition is { } transition)
            {
                // Audio sessions can briefly become inactive between media tracks. Keep the
                // open recorder gated until an active, unambiguous ownership check succeeds.
                if (match.State == MatchState.Waiting)
                {
                    nextMatch = Environment.TickCount64 + (transition.PlayingAt == 0 ? timing.EvidenceInterval : 25);
                    return;
                }
                if (match.State != MatchState.Matched || capture is null || capture.EndpointIds.Count == 0 ||
                    !capture.Matches(match))
                {
                    owner.CancelTransition(request, transition, evidence);
                    return;
                }
                if (transition.PlayingAt == 0)
                {
                    nextMatch = Environment.TickCount64 + timing.IdentityInterval;
                    return;
                }
            }

            switch (match.State)
            {
                case MatchState.Muted:
                    DropCapture();
                    failures = 0;
                    nextMatch = Environment.TickCount64 + timing.IdentityInterval;
                    ChangeState(AudioCaptureState.Muted);
                    return;
                case MatchState.Ambiguous:
                    Fail(match.Reason == MatchReason.UntrustedSignature
                        ? AudioCaptureReason.UntrustedIdentity : AudioCaptureReason.AmbiguousSource);
                    return;
                case MatchState.Missing when match.Reason == MatchReason.UntrustedSignature:
                    Fail(AudioCaptureReason.UntrustedIdentity);
                    return;
                case MatchState.Waiting:
                case MatchState.Missing:
                    WaitForSource(match);
                    return;
            }

            if (match.Process is not { } target)
            {
                Fail(AudioCaptureReason.SourceUnavailable);
                return;
            }

            if (capture is not null && !capture.Matches(match))
                DropCapture();

            if (capture is null)
            {
                if (state != AudioCaptureState.Connecting)
                    waitingUntil = Environment.TickCount64 + timing.MatchGrace;
                if (state != AudioCaptureState.Fallback)
                    ChangeState(AudioCaptureState.Connecting);
                try { capture = await ActivateAsync(target, match, evidence).ConfigureAwait(false); }
                catch (Exception error) when (!request.Token.IsCancellationRequested) { Debug.WriteLine(error); }

                if (!IsValid(evidence))
                {
                    DropCapture();
                    return;
                }

                if (capture is null)
                {
                    Fail(AudioCaptureReason.CaptureFailed);
                    return;
                }
            }

            if (!owner.Approve(request, capture, evidence, topology))
                return;
            nextMatch = Environment.TickCount64 + timing.IdentityInterval;
            Publish();
        }

        private void WaitForSource(AudioMatch match)
        {
            missingReason = match.State == MatchState.Waiting ? AudioCaptureReason.MatchTimeout : AudioCaptureReason.SourceUnavailable;
            var now = Environment.TickCount64;
            if (state == AudioCaptureState.Fallback)
            {
                Fail(missingReason == AudioCaptureReason.MatchTimeout ? reason : missingReason);
                return;
            }

            if (capture is not null && match.State == MatchState.Waiting)
            {
                // A short inactive window between tracks invalidates samples, but a recorder for
                // the same verified process can be approved again if its audio session resumes.
                capture.Suspend();
                nextMatch = now + timing.WaitingInterval;
                Publish();
                return;
            }

            if (state != AudioCaptureState.Connecting || capture is not null)
            {
                DropCapture();
                waitingUntil = now + timing.MatchGrace;
                ChangeState(AudioCaptureState.Connecting);
            }

            nextMatch = now + timing.WaitingInterval;
            Publish();
        }

        private async Task<AudioObservation> InspectAsync(int budget)
        {
            retiredSources.RemoveAll(static retired => retired.IsCompleted);
            // Matching opens no recorder. A second query can recover a source whose first
            // lookup is still stuck, while each request can retain only two unfinished queries.
            if (retiredSources.Count >= UnfinishedSourceLimit)
                throw new TimeoutException("The previous audio source lookups have not returned.");

            var lease = source ??= new SourceLease(owner, request);
            var revision = topology;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            var attemptToken = cancellation.Token;
            Task<AudioObservation>? pending = null;
            try
            {
                pending = owner.StartNative(() =>
                {
                    attemptToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref owner.matchAttemptCount);
                    return Task.FromResult(lease.Backend.Inspect(request.Source, request.Competition, revision,
                        owner.collectDiagnostics, attemptToken));
                });
                return await pending.WaitAsync(TimeSpan.FromMilliseconds(budget), request.Token).ConfigureAwait(false);
            }
            catch
            {
                cancellation.Cancel();
                source = null;
                retiredSources.Add(lease.Retire(pending));
                throw;
            }
        }

        private async Task<Capture?> ActivateAsync(ProcessIdentity target, AudioMatch match, long evidence)
        {
            if (abandonedRecorder is { IsCompleted: false })
                return null;

            var deadline = Environment.TickCount64 + timing.ActivationTimeout;
            if (drainingRecorder is { IsCompleted: false })
                await drainingRecorder.WaitAsync(TimeSpan.FromMilliseconds(timing.ActivationTimeout), request.Token)
                    .ConfigureAwait(false);

            long budget = deadline - Environment.TickCount64;
            if (budget <= 0 || !IsValid(evidence))
                return null;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            var attemptToken = cancellation.Token;
            var pending = owner.StartNative(async () =>
            {
                attemptToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref owner.captureAttemptCount);
                var device = await owner.openRecorder(target, attemptToken).ConfigureAwait(false);
                if (device is null)
                    return null;

                var connectionId = Interlocked.Increment(ref owner.nativeOpenCount);
                var opened = new Capture(owner, request, target, match.Instance ?? target, match.EndpointIds ?? [], device, connectionId);
                owner.EmitTrace("AudioNativeOpened", opened);
                try
                {
                    if (!attemptToken.IsCancellationRequested && IsValid(evidence))
                    {
                        opened.Start();
                        if (!attemptToken.IsCancellationRequested && IsValid(evidence))
                            return opened;
                    }
                }
                catch
                {
                    await opened.Release().ConfigureAwait(false);
                    throw;
                }
                await opened.Release().ConfigureAwait(false);
                return null;
            });

            try
            {
                return await pending.WaitAsync(TimeSpan.FromMilliseconds(budget), request.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                cancellation.Cancel();
                abandonedRecorder = ReleaseLateRecorderAsync(pending);
                throw;
            }
        }

        private static async Task ReleaseLateRecorderAsync(Task<Capture?> pending)
        {
            try
            {
                if (await pending.ConfigureAwait(false) is { } late)
                    await late.Release().ConfigureAwait(false);
            }
            catch (Exception error) { Debug.WriteLine(error); }
        }

        private bool IsValid(long evidence) => request.IsCurrent && request.Topology == topology && request.Evidence == evidence;

        private void Fail(AudioCaptureReason failure)
        {
            if (request.Transition is { } transition)
            {
                owner.CancelTransition(request, transition);
                return;
            }

            DropCapture();
            failures = Math.Min(4, failures + 1);
            nextMatch = Environment.TickCount64 + timing.RetryDelay(failures);
            ChangeState(AudioCaptureState.Fallback, failure);
        }

        private void ChangeState(AudioCaptureState next, AudioCaptureReason nextReason = AudioCaptureReason.None)
        {
            state = next;
            reason = nextReason;
            Publish();
        }

        private void Publish() => owner.Publish(request, state, reason, capture, observation);

        private void DropCapture()
        {
            if (capture is not null)
                drainingRecorder = capture.Release();
            capture = null;
        }

        public async Task CloseAsync()
        {
            var closingRecorder = capture?.Release() ?? drainingRecorder ?? Task.CompletedTask;
            var closingSource = source?.Retire() ?? Task.CompletedTask;
            try
            {
                await Task.WhenAll(closingRecorder, closingSource)
                    .WaitAsync(TimeSpan.FromMilliseconds(timing.ActivationTimeout)).ConfigureAwait(false);
            }
            catch (TimeoutException error) { Debug.WriteLine(error); }
        }
    }

    private sealed class SourceLease
    {
        private int retired;

        public SourceLease(AudioMonitor owner, Request request)
        {
            Backend = owner.createSource(() => Observe(false), () => Observe(true));

            void Observe(bool topology)
            {
                if (Volatile.Read(ref retired) == 0)
                    request.Observe(topology);
            }
        }

        public IAudioSource Backend { get; }

        public Task Retire(Task<AudioObservation>? pending = null)
        {
            Volatile.Write(ref retired, 1);
            return Task.Run(async () =>
            {
                try
                {
                    if (pending is not null)
                        await pending.ConfigureAwait(false);
                }
                catch (Exception error) { Debug.WriteLine(error); }
                finally
                {
                    try { Backend.Dispose(); }
                    catch (Exception error) { Debug.WriteLine(error); }
                }
            });
        }
    }

    private sealed class Capture(AudioMonitor owner, Request request, ProcessIdentity process, ProcessIdentity instance,
        IReadOnlyList<string> endpointIds, IAudioRecorder recorder, long connectionId)
    {
        private readonly object packetGate = new();
        private long sampleGeneration = Interlocked.Increment(ref owner.sampleGeneration);
        private long approvedEpoch = -1;
        private long lastPacket = long.MinValue;
        private long discardBefore;
        private long resetWindow;
        private bool awaitFreshTimestamp;
        private bool firstPacket;
        private bool firstFft;
        private bool firstBands;
        private int stopped;
        private Task? closing;

        public ProcessIdentity Process { get; } = process;
        public IReadOnlyList<string> EndpointIds { get; } = endpointIds;
        public string Source => request.Source;
        public long RequestGeneration => request.Generation;
        public long ConnectionId { get; } = connectionId;
        public long SampleGeneration => Volatile.Read(ref sampleGeneration);
        public SpectrumAnalyzer Analyzer { get; } = new();
        private SpectrumEnvelope Envelope { get; set; } = null!;
        public long StartedAt { get; private set; }
        public long LastPacket => Volatile.Read(ref lastPacket);
        public bool IsApproved => Volatile.Read(ref approvedEpoch) == request.Evidence &&
            Volatile.Read(ref stopped) == 0 && request.IsCurrent;
        public bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref stopped) != 0)
                    return false;
                try { return recorder.IsAlive; }
                catch (Exception error) { Debug.WriteLine(error); return false; }
            }
        }

        public void Start()
        {
            Envelope = new(Analyzer);
            recorder.Packet += OnPacket;
            recorder.Stopped += OnStopped;
            recorder.Start();
            StartedAt = Environment.TickCount64;
        }

        public bool Matches(AudioMatch match) => match.Process is { } target && SameInstance(Process, target) &&
            SameInstance(instance, match.Instance ?? target) && EndpointIds.SequenceEqual(match.EndpointIds ?? [], StringComparer.Ordinal);

        private static bool SameInstance(ProcessIdentity left, ProcessIdentity right) =>
            left.Id == right.Id && left.CreatedAt == right.CreatedAt &&
            string.Equals(left.ImagePath, right.ImagePath, StringComparison.OrdinalIgnoreCase);

        public bool Approve(long evidence, long playingAt = 0)
        {
            lock (packetGate)
            {
                if (Volatile.Read(ref stopped) != 0)
                    return false;
                if (playingAt != 0)
                {
                    discardBefore = playingAt;
                    awaitFreshTimestamp = true;
                    owner.EmitTrace("AudioResumeVerified", this);
                }
                Volatile.Write(ref approvedEpoch, evidence);
                return true;
            }
        }

        public void Suspend() => Volatile.Write(ref approvedEpoch, -1);

        public void SuspendForTransition(long now)
        {
            lock (packetGate)
            {
                Suspend();
                Volatile.Write(ref sampleGeneration, Interlocked.Increment(ref owner.sampleGeneration));
                Envelope.Suspend(now);
                Analyzer.Reset();
                resetWindow = Analyzer.WindowSequence;
                firstPacket = firstFft = firstBands = false;
                owner.EmitTrace("AudioSuspended", this, now);
            }
        }

        public bool ReadBands(float[] destination, double sensitivity, long now, out bool approved, out long readGeneration)
        {
            lock (packetGate)
            {
                approved = IsApproved;
                var moving = Envelope.Read(destination, sensitivity, now, approved);
                readGeneration = approved && Envelope.ConsumedWindowSequence > resetWindow ? SampleGeneration : 0;
                return moving;
            }
        }

        public void BandsSubmitted(long readGeneration)
        {
            if (owner.Trace is null)
                return;

            lock (packetGate)
            {
                if (!firstBands && IsApproved && readGeneration == SampleGeneration)
                {
                    firstBands = true;
                    owner.EmitTrace("AudioFirstBands", this, peakBand: Analyzer.PeakBand);
                }
            }
        }

        private void OnPacket(ReadOnlySpan<byte> data, bool silent, bool discontinuity, long capturedAt)
        {
            var epoch = Volatile.Read(ref approvedEpoch);
            var sequence = SampleGeneration;
            if (data.Length < sizeof(short) * 2 || !IsApproved)
                return;

            int windows;
            long now;
            lock (packetGate)
            {
                if (!IsApproved || epoch != approvedEpoch || sequence != SampleGeneration)
                    return;

                if (discardBefore != 0 && (capturedAt == 0 ? awaitFreshTimestamp : capturedAt < discardBefore))
                    return;
                awaitFreshTimestamp = false;

                Interlocked.Increment(ref owner.packetCount);
                now = Stopwatch.GetTimestamp();
                if (!firstPacket)
                {
                    firstPacket = true;
                    owner.EmitTrace("AudioFirstPacket", this, now, silent);
                }
                windows = Analyzer.Push(data, silent, discontinuity, now);
                if (windows != 0)
                {
                    Interlocked.Add(ref owner.fftCount, windows);
                    if (!firstFft)
                    {
                        firstFft = true;
                        owner.EmitTrace("AudioFirstFft", this, peakBand: Analyzer.PeakBand);
                    }
                }

                if (Interlocked.Exchange(ref lastPacket, Environment.TickCount64) == long.MinValue)
                    request.Signal();
            }

            if (windows != 0)
                owner.TryWakeForBands(this, now);
        }

        private void OnStopped()
        {
            Volatile.Write(ref stopped, 1);
            owner.CancelTransition(request, capture: this);
            request.Signal();
        }

        public Task Release()
        {
            lock (packetGate)
            {
                if (closing is not null)
                    return closing;

                Volatile.Write(ref stopped, 1);
                recorder.Packet -= OnPacket;
                recorder.Stopped -= OnStopped;
                return closing = Task.Run(async () =>
                {
                    try { await recorder.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { Debug.WriteLine(error); }
                    finally { owner.EmitTrace("AudioNativeClosed", this); }
                });
            }
        }
    }
}

internal sealed record AudioDiagnosticSnapshot(string Source, string? State, string? Basis, string? Reason,
    uint? TargetProcessId, string? TargetImagePath, long? TargetCreatedAt, uint? InstanceProcessId, string? InstanceImagePath,
    string? RegistrationTarget, bool Recording, bool Approved, IReadOnlyList<ActiveAudioSession> Sessions,
    long? ConnectionId, long NativeOpenCount, long? SampleGeneration, IReadOnlyList<string> EndpointIds);

internal sealed record AudioCaptureEvent(string Kind, long Timestamp, string Source, long RequestGeneration,
    long SampleGeneration, long ConnectionId, long NativeOpenCount, uint ProcessId, long ProcessCreatedAt,
    bool? Silent, int? PeakBand, IReadOnlyList<string> EndpointIds);
