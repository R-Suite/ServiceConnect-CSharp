using System;

namespace R.MessageBus.Interfaces
{
    public interface IBus : IDisposable
    {   
        /// <summary>
        /// Contains the Bus configuration.
        /// </summary>
        IConfiguration Configuration { get; set; }

        /// <summary>
        /// Sets up the Bus to start consuming messages on the given queue.
        /// </summary>
        /// <param name="queue">The name of the queue to consume messages on.</param>
        void StartConsuming(string queue = null);

        /// <summary>
        /// Stop consuming messages.
        /// </summary>
        void StopConsuming();
    }
}