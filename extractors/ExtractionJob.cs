
namespace DotrAssetModder;

public sealed class ExtractionProgress
{
    int total;
    int done;
    volatile int cancel;

    public int Total => Volatile.Read(ref total);
    public int Done => Volatile.Read(ref done);
    public bool IsCancellationRequested => cancel != 0;

    public float Fraction
    {
        get
        {
            int t = Total;
            return t <= 0 ? 0f : (float)Done / t;
        }
    }

    public void SetTotal(int total) => Volatile.Write(ref this.total, total);
    public void Increment() => Interlocked.Increment(ref done);
    public void RequestCancel() => cancel = 1;
}

public sealed class ExtractionJob
{
    private readonly Task<ExtractResult> _task;

    public ExtractionProgress Progress { get; }

    public bool IsCompleted => _task.IsCompleted;
    public bool IsFaulted => _task.IsFaulted;
    public ExtractResult? Result =>
        _task.Status == TaskStatus.RanToCompletion ? _task.Result : null;
    public System.Exception? Error => _task.Exception?.GetBaseException();

    private ExtractionJob(Task<ExtractResult> task, ExtractionProgress progress)
    {
        _task = task;
        Progress = progress;
    }

    public static ExtractionJob Start(
        byte[] data, string outDir, string stem, IList<string>? names = null)
    {
        var progress = new ExtractionProgress();
        var extractor = new AssetExtractor();
        var task = Task.Run(() => extractor.Extract(data, outDir, stem, names, progress));
        return new ExtractionJob(task, progress);
    }

    public void RequestCancel() => Progress.RequestCancel();
}