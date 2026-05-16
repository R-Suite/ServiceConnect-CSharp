namespace ServiceConnect.Interfaces;

/// <summary>
/// A filter that inspects or modifies messages as they pass through the pipeline.
/// </summary>
public interface IFilter
{
    /// <summary>
    /// Processes the given envelope. Returns <see cref="FilterAction.Continue"/> to continue
    /// pipeline execution, or <see cref="FilterAction.Stop"/> to block the message and stop
    /// further pipeline execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filters that need the bus should take <see cref="IBus"/> as a constructor dependency
    /// and be registered in DI (the previous <c>IFilter.Bus</c> property was never populated
    /// by the pipeline and returned null at runtime).
    /// </para>
    /// <para>
    /// <b>Lifetime.</b> The pipeline resolves <see cref="IFilter"/> via
    /// <c>IServiceProvider.GetRequiredService</c> per dispatch. Register filters as
    /// Scoped to share state across the inbound stages of a single message (the
    /// dispatcher's scope flows through <c>ConsumeScopeAccessor</c> so all stages see
    /// the same instance), or as Transient for stateless filters. Singleton filters are
    /// permitted but the filter author owns thread-safety — multiple dispatches may
    /// invoke the same instance concurrently. Avoid storing per-message state on a
    /// Singleton filter (it will be observed by unrelated messages).
    /// </para>
    /// <para>
    /// <b>Exception contract.</b> The pipeline does not wrap or suppress exceptions thrown
    /// from <see cref="ProcessAsync"/>; the exception propagates to the pipeline's caller.
    /// Observable behaviour therefore differs by stage:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Outgoing filters:</b> the exception propagates to the caller of
    ///     <c>IBus.PublishAsync</c> / <c>SendAsync</c> / <c>SendToManyAsync</c> /
    ///     <c>SendRequestAsync</c> / <c>SendRequestMultiAsync</c> / <c>PublishRequestAsync</c> /
    ///     <c>RouteAsync</c>. The message is not published.</description></item>
    ///   <item><description><b>BeforeConsumingFilters:</b> the dispatcher catches the exception
    ///     and reports <see cref="ConsumeEventResult"/> <c>Success=false</c> — the message goes
    ///     to retry/error per the queue's configured behaviour.</description></item>
    ///   <item><description><b>OnConsumedSuccessfullyFilters:</b> the dispatcher's outer catch
    ///     flips a successful handler dispatch to <c>Success=false</c>, sending the message
    ///     to retry/error and re-invoking the handler on redelivery — duplicating any side
    ///     effects the handler already produced. Filter authors should treat these stages as
    ///     "do not throw"; emit logging or metrics instead and let the message stay acked.</description></item>
    ///   <item><description><b>AfterConsumingFilters:</b> the dispatcher swallows the exception
    ///     and logs at Warning. The message remains acked.</description></item>
    /// </list>
    /// <para>
    /// Bottom line: outgoing and BeforeConsuming filters MAY throw to reject a message; the
    /// OnConsumedSuccessfully and AfterConsuming stages SHOULD NOT throw — the handler has
    /// already committed its side effects and turning that into a retry is a duplicate-work bug.
    /// </para>
    /// </remarks>
    Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
