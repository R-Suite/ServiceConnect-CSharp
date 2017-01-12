using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class ExpiredTimeoutsPoller
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(ExpiredTimeoutsPoller));
        private readonly IProcessManagerFinder _processManagerFinder;
        private readonly IBus _bus;
        readonly object _locker = new object();
        CancellationTokenSource _tokenSource;

        public ExpiredTimeoutsPoller(IBus bus)
        {
            _bus = bus;
            _processManagerFinder = bus.Configuration.GetProcessManagerFinder();

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

        public void Start()
        {
            _tokenSource = new CancellationTokenSource();
            Poll(_tokenSource.Token);
        }

        public void Stop()
        {
            _tokenSource.Cancel();
        }

        async void Poll(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                InnerPoll(cancellationToken);
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

                Logger.DebugFormat("Polling next query is at {0}.", NextQueryUtc.ToLocalTime());
            }
        }
    }
}
