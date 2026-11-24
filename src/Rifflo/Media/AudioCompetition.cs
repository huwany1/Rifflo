namespace Rifflo.Media;

internal sealed class AudioCompetition : IEquatable<AudioCompetition>
{
    public static AudioCompetition Empty { get; } = new([]);

    private AudioCompetition(string[] sources) => Sources = Array.AsReadOnly(sources);

    public IReadOnlyList<string> Sources { get; }

    public static AudioCompetition FromSources(IEnumerable<string> sources)
    {
        var ordered = sources.Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return ordered.Length == 0 ? Empty : new(ordered);
    }

    public bool Equals(AudioCompetition? other) => ReferenceEquals(this, other) ||
        (other is not null && Sources.SequenceEqual(other.Sources, StringComparer.OrdinalIgnoreCase));

    public override bool Equals(object? obj) => obj is AudioCompetition other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var source in Sources)
            hash.Add(source, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }
}
