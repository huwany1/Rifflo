using System.IO;

namespace Rifflo.Audio;

internal sealed record ProcessIdentity(
    uint Id, uint ParentId, long CreatedAt, string ImagePath, string PackageFamily, string AppId,
    IReadOnlyList<string> WindowAppIds, string SnapshotName = "");

internal readonly record struct ActiveAudioSession(uint ProcessId, bool Muted, string? EndpointId = null, float? Peak = null);

internal enum MatchState { Missing, Ambiguous, Waiting, Muted, Matched }
internal enum MatchBasis { None, ExactIdentity, Executable, ShellRegistration, SignedKuGou }
internal enum MatchReason
{
    NoIdentity, NoActiveAudio, ConflictingIdentity, MultipleInstances, UnverifiedProcessTree, UntrustedSignature, MutedSession, Matched
}

internal readonly record struct AudioMatch(MatchState State, MatchBasis Basis, MatchReason Reason,
    ProcessIdentity? Process = null, ProcessIdentity? Instance = null, string? RegistrationTarget = null,
    IReadOnlyList<string>? EndpointIds = null);

internal static class AudioSourceMatcher
{
    public static AudioMatch Match(
        string source, IReadOnlyDictionary<uint, ProcessIdentity> processes, IReadOnlyList<ActiveAudioSession> audio,
        Func<string, IReadOnlyList<string>>? registeredTargets = null, Func<ProcessIdentity, bool>? trustedKuGou = null,
        IReadOnlyList<string>? competitors = null)
    {
        AudioMatch match;
        if (Equal(source, "kugou") && trustedKuGou is not null)
            match = MatchInferred(source, processes, audio, registeredTargets, trustedKuGou);
        else
        {
            var direct = MatchDirect(source, processes, audio);
            match = direct.State == MatchState.Missing && registeredTargets is not null
                ? MatchInferred(source, processes, audio, registeredTargets, null) : direct;
        }

        if (match.Process is { } target && competitors is { Count: > 0 } &&
            HasCompetingSource(source, target, match.Instance ?? target, processes, audio, registeredTargets, competitors))
            return new(MatchState.Ambiguous, match.Basis, MatchReason.ConflictingIdentity);

        if (match.State == MatchState.Matched && match.Process is { } matched)
        {
            var endpoints = audio.Where(session => DescendsFrom(session.ProcessId, matched.Id, processes))
                .Select(session => session.EndpointId).OfType<string>().Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            return match with { EndpointIds = endpoints };
        }

        return match;
    }

    private static bool HasCompetingSource(string source, ProcessIdentity target, ProcessIdentity instance,
        IReadOnlyDictionary<uint, ProcessIdentity> processes, IReadOnlyList<ActiveAudioSession> audio,
        Func<string, IReadOnlyList<string>>? registeredTargets, IReadOnlyList<string> competitors)
    {
        var includedImages = audio.Where(session => DescendsFrom(session.ProcessId, target.Id, processes))
            .Select(session => processes[session.ProcessId].ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        includedImages.Add(target.ImagePath);
        includedImages.Add(instance.ImagePath);

        foreach (var competitor in competitors)
        {
            if (Equal(source, competitor))
                continue;

            if (processes.Values.Any(process => HasExactIdentity(process, competitor) && OverlapsInstance(process.Id)))
                return true;

            var other = MatchDirect(competitor, processes, audio);
            if (other.Process is { } identified)
            {
                if (OverlapsInstance(identified.Id))
                    return true;

                // Another verified, audible process tree can run the very same executable.
                continue;
            }

            // A Shell target can identify the executable used by a competing session. It
            // cannot link a window or tab to a particular audio stream from that executable.
            if (registeredTargets?.Invoke(competitor).Any(includedImages.Contains) == true ||
                (ExecutableName(competitor) is { } executable && includedImages.Any(path =>
                    Path.IsPathFullyQualified(competitor) ? Equal(path, competitor) : Equal(Path.GetFileName(path), executable))))
                return true;
        }

        return false;

        bool OverlapsInstance(uint processId) =>
            DescendsFrom(processId, instance.Id, processes, allowUnverified: true) ||
            DescendsFrom(instance.Id, processId, processes, allowUnverified: true);
    }

    private static AudioMatch MatchDirect(
        string source, IReadOnlyDictionary<uint, ProcessIdentity> processes, IReadOnlyList<ActiveAudioSession> audio)
    {
        var basis = MatchBasis.ExactIdentity;
        if (processes.Values.Any(process => process.CreatedAt == 0 && HasExactIdentity(process, source)))
            return new(MatchState.Ambiguous, basis, MatchReason.UnverifiedProcessTree);

        var anchors = processes.Values.Where(process => process.CreatedAt != 0 && HasExactIdentity(process, source)).ToArray();
        if (anchors.Length == 0 && ExecutableName(source) is { } executable)
        {
            basis = MatchBasis.Executable;
            bool fullPath = Path.IsPathFullyQualified(source);
            if (processes.Values.Any(process => process.CreatedAt == 0 && Equal(process.SnapshotName, executable)))
                return new(MatchState.Ambiguous, basis, MatchReason.UnverifiedProcessTree);

            anchors = processes.Values.Where(process => fullPath
                ? Equal(process.ImagePath, source)
                : Equal(Path.GetFileName(process.ImagePath), executable)).ToArray();
        }

        if (anchors.Length == 0)
            return new(MatchState.Missing, MatchBasis.None, MatchReason.NoIdentity);

        var roots = anchors.Where(anchor => !anchors.Any(ancestor => ancestor.Id != anchor.Id &&
            DescendsFrom(anchor.Id, ancestor.Id, processes))).ToArray();
        if (roots.Length != 1)
            return new(MatchState.Ambiguous, basis, MatchReason.MultipleInstances);

        var root = roots[0];
        if (HasConflictingIdentity(root, source))
            return new(MatchState.Ambiguous, basis, MatchReason.ConflictingIdentity);

        var related = audio.Where(session => DescendsFrom(session.ProcessId, root.Id, processes,
            allowUnverified: true)).ToArray();
        if (related.Length == 0)
            return new(MatchState.Waiting, basis, MatchReason.NoActiveAudio);

        if (related.Any(session => !DescendsFrom(session.ProcessId, root.Id, processes) ||
            !IsRelated(processes[session.ProcessId], root, source)))
            return new(MatchState.Ambiguous, basis, MatchReason.UnverifiedProcessTree);

        // Process loopback can include audio before per-session attenuation. Do not mix a muted
        // session into the visualizer when another active session in the same tree is unmuted.
        if (related.Any(session => session.Muted))
            return new(MatchState.Muted, basis, MatchReason.MutedSession, root);

        return new(MatchState.Matched, basis, MatchReason.Matched, root);
    }

    private static AudioMatch MatchInferred(string source, IReadOnlyDictionary<uint, ProcessIdentity> processes,
        IReadOnlyList<ActiveAudioSession> audio, Func<string, IReadOnlyList<string>>? registeredTargets,
        Func<ProcessIdentity, bool>? trustedKuGou)
    {
        var trusted = new Dictionary<uint, bool>();
        var branches = new List<(ProcessIdentity[] Chain, int SignedThrough, bool Exact)>();
        foreach (uint pid in audio.Select(session => session.ProcessId).Distinct())
        {
            var chain = VerifiedAncestry(pid, processes).ToArray();
            int signed = -1;
            if (trustedKuGou is not null)
            {
                while (signed + 1 < chain.Length && IsTrusted(chain[signed + 1]))
                    signed++;
            }

            var verified = chain.Take(signed + 1);
            bool exact = verified.Any(process => HasExactIdentity(process, source)) &&
                !verified.Any(process => HasDifferentIdentity(process, source));
            branches.Add((chain, signed, exact));
        }

        // The genuine app's exact identity takes precedence over Shell shortcuts. An asserted
        // identity on an unsigned process cannot suppress a legitimate registered match.
        var targets = new HashSet<string>(branches.Any(branch => branch.Exact) ? [] : registeredTargets?.Invoke(source) ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 && trustedKuGou is null)
            return new(MatchState.Missing, MatchBasis.None, MatchReason.NoIdentity);

        var candidates = new List<InferredAudio>();
        var disputed = new List<(uint Anchor, MatchReason Reason)>();
        bool signatureRejected = false;

        foreach (var (chain, signed, exact) in branches)
        {
            int registered = Array.FindLastIndex(chain, process => targets.Contains(process.ImagePath));

            if (trustedKuGou is null)
            {
                if (registered < 0)
                    continue;
                var anchor = chain[registered];
                if (chain.Take(registered + 1).Any(process => HasDifferentIdentity(process, source)))
                    disputed.Add((anchor.Id, MatchReason.ConflictingIdentity));
                else if (!IsRelated(chain[0], anchor, source))
                    disputed.Add((anchor.Id, MatchReason.UnverifiedProcessTree));
                else
                    candidates.Add(new(chain, registered, false, anchor.ImagePath));
                continue;
            }

            if (signed < registered)
            {
                var reason = IsTrusted(chain[registered]) ? MatchReason.UnverifiedProcessTree : MatchReason.UntrustedSignature;
                disputed.Add((chain[registered].Id, reason));
                continue;
            }

            if (signed < 0)
            {
                // A filename cannot establish trust. It can explain this rejection.
                signatureRejected |= (chain.Length != 0 && IsKuGouEntryPoint(chain[0])) ||
                    chain.Any(process => HasExactIdentity(process, source));
                continue;
            }

            // A signed helper can prove membership through a verified entry point. Without that
            // ancestry, only a trusted KuGou entry point or a registered executable can stand alone.
            if (!exact && registered < 0 && !chain.Take(signed + 1).Any(IsKuGouEntryPoint))
            {
                disputed.Add((chain[signed].Id, MatchReason.UnverifiedProcessTree));
                continue;
            }

            if (chain.Take(signed + 1).Any(process => HasDifferentIdentity(process, source)))
            {
                disputed.Add((chain[signed].Id, MatchReason.ConflictingIdentity));
                continue;
            }

            candidates.Add(new(chain, signed, exact, registered >= 0 ? chain[registered].ImagePath : null));
        }

        var registrationTarget = candidates.Select(candidate => candidate.RegistrationTarget).FirstOrDefault(path => path is not null);
        var basis = trustedKuGou is not null ? MatchBasis.SignedKuGou : MatchBasis.None;
        if (candidates.Any(candidate => candidate.Exact))
            basis = MatchBasis.ExactIdentity;
        else if (registrationTarget is not null || (candidates.Count == 0 && targets.Count != 0))
            basis = MatchBasis.ShellRegistration;

        if (candidates.Count == 0)
        {
            if (disputed.Count != 0)
                return new(MatchState.Ambiguous, basis, disputed[0].Reason);
            if (signatureRejected)
                return new(MatchState.Missing, basis, MatchReason.UntrustedSignature);
            return new(MatchState.Waiting, basis, MatchReason.NoActiveAudio);
        }

        var instance = candidates[0].Anchor;
        if (candidates.Any(candidate => candidate.Anchor.Id != instance.Id))
            return new(MatchState.Ambiguous, basis, MatchReason.MultipleInstances);
        if (disputed.Any(dispute => dispute.Anchor != instance.Id))
            return new(MatchState.Ambiguous, basis, MatchReason.ConflictingIdentity);

        var approvedPids = candidates.Select(candidate => candidate.Chain[0].Id).ToHashSet();
        // Walk from the first audio PID to its anchor. The first shared process is the smallest
        // tree that includes all confirmed audio PIDs for the instance.
        var target = candidates[0].Chain.Take(candidates[0].AnchorIndex + 1).First(process =>
            candidates.All(candidate => candidate.Chain.Take(candidate.AnchorIndex + 1).Any(member => member.Id == process.Id)));
        var included = audio.Where(session => DescendsFrom(session.ProcessId, target.Id, processes, allowUnverified: true)).ToArray();
        if (included.Any(session => !approvedPids.Contains(session.ProcessId)))
            return new(MatchState.Ambiguous, basis, MatchReason.UnverifiedProcessTree);
        if (included.Any(session => session.Muted))
            return new(MatchState.Muted, basis, MatchReason.MutedSession, target, instance, registrationTarget);

        return new(MatchState.Matched, basis, MatchReason.Matched, target, instance, registrationTarget);

        bool IsTrusted(ProcessIdentity process)
        {
            if (!trusted.TryGetValue(process.Id, out bool verified))
                trusted[process.Id] = verified = trustedKuGou!(process);
            return verified;
        }
    }

    private static IEnumerable<ProcessIdentity> VerifiedAncestry(uint pid, IReadOnlyDictionary<uint, ProcessIdentity> processes)
    {
        long childCreatedAt = long.MaxValue;
        while (processes.TryGetValue(pid, out var process) && process.CreatedAt > 0 &&
            process.CreatedAt < childCreatedAt && process.ImagePath.Length != 0)
        {
            yield return process;
            childCreatedAt = process.CreatedAt;
            pid = process.ParentId;
        }
    }

    private static bool HasDifferentIdentity(ProcessIdentity process, string source) =>
        (process.AppId.Length != 0 && !Equal(process.AppId, source)) ||
        process.WindowAppIds.Any(id => !Equal(id, source));

    private static bool IsKuGouEntryPoint(ProcessIdentity process) => Equal(Path.GetFileName(process.ImagePath), "KuGou.exe");

    private sealed record InferredAudio(ProcessIdentity[] Chain, int AnchorIndex, bool Exact, string? RegistrationTarget)
    {
        public ProcessIdentity Anchor => Chain[AnchorIndex];
    }

    public static string? ExecutableName(string source)
    {
        if (!source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            (Path.GetFileName(source) != source && !Path.IsPathFullyQualified(source)))
            return null;

        return Path.GetFileName(source);
    }

    private static bool HasExactIdentity(ProcessIdentity process, string source) =>
        Equal(process.AppId, source) || process.WindowAppIds.Any(id => Equal(id, source));

    private static bool HasConflictingIdentity(ProcessIdentity process, string source) =>
        process.WindowAppIds.Count > 1 ||
        (source.Contains('!') && process.AppId.Length != 0 && !Equal(process.AppId, source)) ||
        (process.WindowAppIds.Any(id => Equal(id, source)) &&
            process.AppId.Contains('!') && !Equal(process.AppId, source));

    private static bool IsRelated(ProcessIdentity process, ProcessIdentity root, string source)
    {
        if (HasConflictingIdentity(process, source))
            return false;
        if (process.Id == root.Id || HasExactIdentity(process, source))
            return true;
        if (process.WindowAppIds.Count != 0 || (process.AppId.Length != 0 && !Equal(process.AppId, root.AppId)))
            return false;

        return Equal(process.ImagePath, root.ImagePath) ||
            (root.PackageFamily.Length != 0 && Equal(process.PackageFamily, root.PackageFamily));
    }

    private static bool DescendsFrom(uint processId, uint ancestorId, IReadOnlyDictionary<uint, ProcessIdentity> processes,
        bool allowUnverified = false)
    {
        // A missing process identity may still have a parent link in the Toolhelp snapshot. Such
        // links can disqualify a capture target, but cannot authenticate an audio stream.
        int remaining = processes.Count;
        while (processes.TryGetValue(processId, out var process))
        {
            if (processId == ancestorId)
                return true;

            if (--remaining == 0 || !processes.TryGetValue(process.ParentId, out var parent))
                return false;
            bool verified = process.CreatedAt != 0 && parent.CreatedAt != 0;
            if (verified ? parent.CreatedAt >= process.CreatedAt : !allowUnverified)
                return false;

            processId = parent.Id;
        }

        return false;
    }

    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
