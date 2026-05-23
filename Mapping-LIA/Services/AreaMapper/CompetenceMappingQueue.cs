using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mapping_LIA.Services.AreaMapper;

/// <summary>
/// In-memory queue used for larger text uploads that should not block the HTTP request.
/// </summary>
/// <remarks>
/// This queue is process-local. Restarting the API or running multiple instances
/// loses/splits job status, which is acceptable for the current internal tool but
/// should be revisited before scaling out.
/// </remarks>
public interface ICompetenceMappingQueue
{
    Guid Enqueue(IEnumerable<string> inputs);
    bool TryGet(Guid jobId, out MappingJobStatus status);
}

/// <summary>
/// Polling snapshot returned to the frontend while a batch mapping job runs.
/// </summary>
public record MappingJobStatus(
    Guid JobId,
    int Total,
    int Processed,
    int Succeeded,
    int Failed,
    bool IsCompleted,
    DateTime StartedAt,
    DateTime? FinishedAt,
    IReadOnlyList<MapResponse> Results,
    IReadOnlyList<string> Errors
);

internal record MappingJob(Guid JobId, IReadOnlyList<string> Inputs);

internal sealed class CompetenceMappingQueue : ICompetenceMappingQueue
{
    private const int MaxTrackedJobs = 100;
    private static readonly TimeSpan CompletedJobRetention = TimeSpan.FromHours(2);

    internal readonly Channel<MappingJob> Channel = System.Threading.Channels.Channel.CreateUnbounded<MappingJob>();
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public Guid Enqueue(IEnumerable<string> inputs)
    {
        PruneCompletedJobs();

        var list = inputs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        var id = Guid.NewGuid();
        _jobs[id] = new JobState(id, list.Count);

        Channel.Writer.TryWrite(new MappingJob(id, list));
        return id;
    }

    public bool TryGet(Guid jobId, out MappingJobStatus status)
    {
        status = default!;
        if (!_jobs.TryGetValue(jobId, out var s)) return false;

        status = s.ToStatus();
        return true;
    }

    internal JobState GetState(Guid jobId) => _jobs[jobId];

    /// <summary>
    /// Keeps completed job status from growing without bound in long-running app instances.
    /// </summary>
    /// <remarks>
    /// Status is retained briefly so the frontend can poll after completion, then
    /// old completed jobs are discarded on the next enqueue. Active jobs are not
    /// removed.
    /// </remarks>
    private void PruneCompletedJobs()
    {
        var now = DateTime.UtcNow;
        var completed = _jobs
            .Where(job => job.Value.IsCompleted)
            .OrderBy(job => job.Value.FinishedAt ?? job.Value.StartedAt)
            .ToList();

        foreach (var job in completed)
        {
            var finishedAt = job.Value.FinishedAt ?? job.Value.StartedAt;
            if (now - finishedAt > CompletedJobRetention || _jobs.Count > MaxTrackedJobs)
            {
                _jobs.TryRemove(job.Key, out _);
            }
        }
    }

    internal sealed class JobState
    {
        public Guid JobId { get; }
        public int Total { get; }
        public int Processed;
        public int Succeeded;
        public int Failed;
        public bool IsCompleted;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime? FinishedAt;

        private readonly ConcurrentQueue<MapResponse> _results = new();
        private readonly ConcurrentQueue<string> _errors = new();

        public JobState(Guid id, int total) { JobId = id; Total = total; }

        public void AddResult(MapResponse r) { _results.Enqueue(r); Interlocked.Increment(ref Succeeded); }
        public void AddError(string e) { _errors.Enqueue(e); Interlocked.Increment(ref Failed); }

        public MappingJobStatus ToStatus() =>
            new(JobId, Total, Processed, Succeeded, Failed, IsCompleted, StartedAt, FinishedAt,
                _results.ToList(), _errors.ToList());
    }
}

/// <summary>
/// Single background worker that processes queued competence batches sequentially.
/// </summary>
/// <remarks>
/// Sequential processing keeps Azure OpenAI usage predictable and avoids several
/// concurrent requests racing on the same competence name. Throughput is lower by
/// design.
/// </remarks>
public sealed class CompetenceMappingWorker : BackgroundService
{
    private readonly ILogger<CompetenceMappingWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CompetenceMappingQueue _queue;

    public CompetenceMappingWorker(
        ILogger<CompetenceMappingWorker> logger,
        IServiceScopeFactory scopeFactory,
        ICompetenceMappingQueue queue)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _queue = (CompetenceMappingQueue)queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Channel.Reader.ReadAllAsync(stoppingToken))
        {
            var state = _queue.GetState(job.JobId);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mapper = scope.ServiceProvider.GetRequiredService<IAreaMapperService>();

                // Distinct within job to avoid wasting LLM calls
                var inputs = job.Inputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // Process each distinct input; state from GetState is the one in _jobs
                foreach (var input in inputs)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var r = await mapper.MapCompetenceAsync(input, stoppingToken);
                        Interlocked.Increment(ref state.Processed);

                        if (r.Success && r.Response != null) state.AddResult(r.Response);
                        else state.AddError($"{input}: {r.ErrorMessage}");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref state.Processed);
                        state.AddError($"{input}: {ex.Message}");
                        _logger.LogError(ex, "Job {JobId}: error mapping '{Input}'", job.JobId, input);
                    }
                }
            }
            finally
            {
                state.IsCompleted = true;
                state.FinishedAt = DateTime.UtcNow;
            }
        }
    }
}
