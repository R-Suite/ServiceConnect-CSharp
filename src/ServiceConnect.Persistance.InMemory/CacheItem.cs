using System;

namespace ServiceConnect.Persistance.InMemory
{
    public class CacheItem
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CacheItem"/> class.
        /// </summary>
        public CacheItem() { }

        /// <summary>
        /// Initialise une nouvelle instance de <see cref="CacheItem"/> class.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="priority">The priority.</param>
        /// <param name="relativeExpiry">The relative expiry.</param>
        public CacheItem(object value, CacheItemPriority priority, TimeSpan? relativeExpiry = null)
        {
            Value = value;
            Priority = priority;
            RelativeExpiry = relativeExpiry;
        }

        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        /// <value>
        /// The value.
        /// </value>
        public object Value { get; set; }

        /// <summary>
        /// Gets or sets the priority.
        /// </summary>
        /// <value>
        /// The priority.
        /// </value>
        public CacheItemPriority Priority { get; set; }

        /// <summary>
        /// Gets or sets the relative expiry.
        /// </summary>
        /// <value>
        /// The relative expiry.
        /// </value>
        public TimeSpan? RelativeExpiry { get; set; }
    }
}
