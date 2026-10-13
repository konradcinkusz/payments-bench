namespace PaymentsBench.Infrastructure.Persistence;

/// <summary>
/// A one-shot signal that the schema is ready. The migration service completes it (or faults it
/// if the migration fails); every other hosted service that touches the database awaits it first.
/// </summary>
public sealed class MigrationGate
{
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A gate that is already open, for storage that needs no migration.</summary>
    public static MigrationGate Completed()
    {
        var gate = new MigrationGate();
        gate.Complete();
        return gate;
    }

    public bool IsCompleted => _signal.Task.IsCompletedSuccessfully;

    public bool IsFaulted => _signal.Task.IsFaulted;

    public Exception? Failure => _signal.Task.Exception?.InnerException;

    public void Complete() => _signal.TrySetResult();

    public void Fail(Exception exception) => _signal.TrySetException(exception);

    /// <summary>Completes when the schema is ready, throws the migration's failure if it failed.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _signal.Task.WaitAsync(cancellationToken);
}
