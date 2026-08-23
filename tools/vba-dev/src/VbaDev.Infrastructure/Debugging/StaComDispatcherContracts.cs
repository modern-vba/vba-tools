namespace VbaDev.Infrastructure.Debugging;

internal interface IStaComDispatcher : IAsyncDisposable
{
    Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}

internal interface IStaComDispatcherFactory
{
    IStaComDispatcher Create();
}

internal sealed class StaComDispatcherFactory : IStaComDispatcherFactory
{
    public IStaComDispatcher Create() => new StaComDispatcher();
}
