
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
            int totalCount = Total;
            return totalCount <= 0 ? 0f : (float)Done / totalCount;
        }
    }

    public void SetTotal(int total) => Volatile.Write(ref this.total, total);
    public void Increment() => Interlocked.Increment(ref done);
    public void RequestCancel() => cancel = 1;
}

public sealed class ExtractionJob
{
    readonly Task<ExtractResult> task;

    public ExtractionProgress Progress { get; }

    public bool IsCompleted => task.IsCompleted;
    public bool IsFaulted => task.IsFaulted;
    public ExtractResult? Result => task.Status == TaskStatus.RanToCompletion ? task.Result : null;
    public Exception? Error => task.Exception?.GetBaseException();

     ExtractionJob(Task<ExtractResult> task, ExtractionProgress progress)
    {
        this.task = task;
        Progress = progress;
    }

    public static ExtractionJob Start(byte[] assetBytes, string outputDirectory, string fileNameStem, IList<string>? assetNames = null)
    {
        var progress = new ExtractionProgress();
        var extractor = new AssetExtractor();
        var task = Task.Run(() => extractor.Extract(assetBytes, outputDirectory, fileNameStem, assetNames, progress));
        return new ExtractionJob(task, progress);
    }

    public void RequestCancel() => Progress.RequestCancel();
}