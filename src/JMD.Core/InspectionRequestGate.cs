namespace JMD.Core;

/// <summary>Allows inspection only when the pointer has moved to a new location.</summary>
public sealed class InspectionRequestGate<T>
{
    private readonly IEqualityComparer<T> _comparer;
    private T? _lastValue;
    private bool _hasValue;

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

    public void Reset() => _hasValue = false;
}
