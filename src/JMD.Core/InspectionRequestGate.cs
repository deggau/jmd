namespace JMD.Core;

/// <summary>Allows inspection only when the pointer has moved to a new location.</summary>
public sealed class InspectionRequestGate<T>
{
    private readonly IEqualityComparer<T> _comparer;
    private T? _lastValue;
    private bool _hasValue;
    private T? _candidate;
    private TimeSpan _candidateSince;
    private bool _hasCandidate;

    public InspectionRequestGate(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public bool TryAccept(T value)
    {
        if (_hasValue && _comparer.Equals(_lastValue!, value)) return false;
        _lastValue = value;
        _hasValue = true;
        return true;
    }

    /// <summary>Accepts a new pointer value only after it has remained unchanged for the quiet period.</summary>
    public bool TryAcceptAfterDelay(T value, TimeSpan now, TimeSpan quietPeriod)
    {
        if (!_hasCandidate || !_comparer.Equals(_candidate!, value))
        {
            _candidate = value;
            _candidateSince = now;
            _hasCandidate = true;
            return false;
        }

        if (now - _candidateSince < quietPeriod) return false;
        return TryAccept(value);
    }

    public void Reset()
    {
        _hasValue = false;
        _hasCandidate = false;
    }
}
