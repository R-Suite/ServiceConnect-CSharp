using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class ExpiredTimeoutsPoller : IDisposable
    {
        private readonly IProcessManagerFinder _processManagerFinder;
        private readonly IBus _bus;
        private readonly ILogger _logger;
        readonly object _locker = new object();
        private CancellationTokenSource _tokenSource;
        private bool _disposed;

        public ExpiredTimeoutsPoller(IBus bus)
        {
            _bus = bus;
            _processManagerFinder = bus.Configuration.GetProcessManagerFinder();
            _logger = bus.Configuration.GetLogger();

            _processManagerFinder.TimeoutInserted += _processManagerFinder_TimeoutInserted;

            NextQueryUtc = DateTime.UtcNow;
        }

        public DateTime NextQueryUtc { get; private set; }

        /// <summary>
        /// Handle the event when a new timeout is requested
        /// </summary>
        /// <param name="timeoutTime"></param>
        void _processManagerFinder_TimeoutInserted(DateTime timeoutTime)
        {
            lock (_locker)
            {
                if (NextQueryUtc > timeoutTime)
                {
                    NextQueryUtc = timeoutTime;
                }
            }
        }

        private Task _pollTask;

        public void Start()
        {
            _tokenSource = new CancellationTokenSource();
            _pollTask = Poll(_tokenSource.Token);
        }

        public void Stop()
        {
            _tokenSource?.Cancel();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _tokenSource?.Cancel();
                    _tokenSource?.Dispose();
                }
                _disposed = true;
            }
        }

        async Task Poll(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    InnerPoll(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.Error("Error in ExpiredTimeoutsPoller poll", ex);
                }
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        public void InnerPoll(CancellationToken cancellationToken)
        {
            var utcNow = DateTime.UtcNow;

            if (NextQueryUtc > utcNow || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // connect to the data store and get all the expired timeouts
            TimeoutsBatch timeoutsBatch = _processManagerFinder.GetTimeoutsBatch();

            foreach (var timeoutData in timeoutsBatch.DueTimeouts)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // dispatch the timeout message
                var timeoutMsg = new TimeoutMessage(timeoutData.ProcessManagerId);
                _bus.Send(timeoutData.Destination, timeoutMsg);

                // remove dispatch timeout
                _processManagerFinder.RemoveDispatchedTimeout(timeoutData.Id);
            }

            lock (_locker)
            {
                var nextQueryTime = timeoutsBatch.NextQueryTime;

                // ensure to poll at least every minute
                var maxNextQuery = utcNow.AddMinutes(1);

                NextQueryUtc = (nextQueryTime > maxNextQuery) ? maxNextQuery : nextQueryTime;

            }
        }
    }
}
