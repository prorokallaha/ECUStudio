using System.Collections.Concurrent;
using System.Threading.Channels;
using ECUStudio.Core;

namespace ECUStudio.Application.Analysis;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<JobStatus>))]
public enum JobStatus { Queued, Running, Completed, Failed }

public sealed record JobEvent(Guid JobId, string Kind, StepProgress? Step, JobStatus Status, string? Error = null, Guid? AnalysisId = null);

/// <summary>Tracks long-running jobs and fans out progress to SSE subscribers. Late subscribers get the history replayed.</summary>
public sealed class JobTracker
{
    private sealed class Job
    {
        public readonly List<JobEvent> History = [];
        public readonly List<Channel<JobEvent>> Subscribers = [];
        public JobStatus Status = JobStatus.Queued;
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public Guid Create()
    {
        var id = Guid.NewGuid();
        _jobs[id] = new Job();
        return id;
    }

    public JobStatus? Status(Guid id) => _jobs.TryGetValue(id, out var j) ? j.Status : null;

    public IReadOnlyList<JobEvent> History(Guid id) => _jobs.TryGetValue(id, out var j) ? j.History.ToList() : [];

    public void Publish(JobEvent e)
    {
        if (!_jobs.TryGetValue(e.JobId, out var job)) return;
        lock (job.Lock)
        {
            job.Status = e.Status;
            job.History.Add(e);
            foreach (var s in job.Subscribers) s.Writer.TryWrite(e);
            if (e.Status is JobStatus.Completed or JobStatus.Failed)
                foreach (var s in job.Subscribers) s.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<JobEvent> Subscribe(Guid id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(id, out var job)) yield break;
        var channel = Channel.CreateUnbounded<JobEvent>();
        List<JobEvent> replay;
        lock (job.Lock)
        {
            replay = job.History.ToList();
            if (job.Status is JobStatus.Completed or JobStatus.Failed) channel.Writer.TryComplete();
            else job.Subscribers.Add(channel);
        }
        foreach (var e in replay) yield return e;
        await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
        lock (job.Lock) job.Subscribers.Remove(channel);
    }

    public IProgress<StepProgress> ProgressFor(Guid id) => new SyncProgress(s => Publish(new JobEvent(id, "step", s, JobStatus.Running)));

    private sealed class SyncProgress(Action<StepProgress> action) : IProgress<StepProgress>
    {
        public void Report(StepProgress value) => action(value);
    }
}
