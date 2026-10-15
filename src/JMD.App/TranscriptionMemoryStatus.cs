namespace JMD.App;

internal static class TranscriptionMemoryStatus
{
    private static long _currentExternalModelBytes;
    private static long _lastExternalModelPeakBytes;
    private static int _activeExternalModels;

    public static long CurrentExternalModelBytes => Interlocked.Read(ref _currentExternalModelBytes);
    public static long LastExternalModelPeakBytes => Interlocked.Read(ref _lastExternalModelPeakBytes);
    public static bool HasActiveExternalModels => Volatile.Read(ref _activeExternalModels) > 0;

    public static IDisposable BeginExternalModel()
    {
        Interlocked.Exchange(ref _currentExternalModelBytes, 0);
        Interlocked.Exchange(ref _lastExternalModelPeakBytes, 0);
        Interlocked.Increment(ref _activeExternalModels);
        return new Activity();
    }

    public static void ReportExternalModelWorkingSet(long bytes)
    {
        Interlocked.Exchange(ref _currentExternalModelBytes, Math.Max(0, bytes));
        UpdatePeak(bytes);
    }

    private static void UpdatePeak(long bytes)
    {
        var observed = Interlocked.Read(ref _lastExternalModelPeakBytes);
        while (bytes > observed)
        {
            var prior = Interlocked.CompareExchange(ref _lastExternalModelPeakBytes, bytes, observed);
            if (prior == observed) break;
            observed = prior;
        }
    }

    private sealed class Activity : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Exchange(ref _currentExternalModelBytes, 0);
            Interlocked.Decrement(ref _activeExternalModels);
        }
    }
}
