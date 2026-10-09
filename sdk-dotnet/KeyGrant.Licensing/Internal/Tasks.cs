namespace KeyGrant.Licensing.Internal;

/// <summary>Bounds on work the SDK does not control, and timers it can cancel.</summary>
internal static class Tasks
{
    /// <summary>
    /// <paramref name="work"/>, or a <see cref="TimeoutException"/> once <paramref name="bound"/> has
    /// passed, whichever comes first: the SDK's own bound, kept whatever the work does. Work left
    /// behind is observed, so a late failure of it is never an unobserved exception.
    /// </summary>
    public static async Task<T> Bounded<T>(Task<T> work, TimeSpan bound, TimeProvider time, CancellationToken cancellationToken = default)
    {
        try
        {
            return await work.WaitAsync(bound, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!work.IsCompleted)
        {
            Abandon(work);
            throw;
        }
    }

    /// <summary>Observe a task nobody waits for any more, so its failure is never unobserved.</summary>
    public static void Abandon(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// <paramref name="start"/>'s task, started at once; a synchronous throw from it (or no task at all)
    /// is the task's failure, as an async function's would be.
    /// </summary>
    public static Task<T> Start<T>(Func<Task<T>> start)
    {
        try
        {
            return start() ?? Task.FromException<T>(new InvalidOperationException("the adapter returned no task"));
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    /// <summary>
    /// Tell work the SDK abandoned (its bound passed, or its caller stopped waiting) to stop, through the
    /// token it was given. Only abandoned work: work that answered is never cancelled. Never throws: the
    /// work's own callbacks run inside <see cref="CancellationTokenSource.Cancel()"/>, and one that throws
    /// must not turn the SDK's answer into an exception.
    /// </summary>
    public static void TellToStop(CancellationTokenSource abandon)
    {
        try
        {
            abandon.Cancel();
        }
        catch (Exception)
        {
            // The work's own callback threw: it was told all the same.
        }
    }
}

/// <summary>
/// A call's grace: over when its time is up, or as soon as the call settles (<see cref="Cancel"/>), so
/// a finished call leaves no timer behind.
/// </summary>
internal sealed class GraceTimer
{
    private readonly TaskCompletionSource over = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer timer;

    public GraceTimer(TimeSpan span, TimeProvider time)
    {
        timer = time.CreateTimer(static state => ((TaskCompletionSource)state!).TrySetResult(), over, span, Timeout.InfiniteTimeSpan);
    }

    public Task Over => over.Task;

    public void Cancel()
    {
        timer.Dispose();
        over.TrySetResult();
    }
}
