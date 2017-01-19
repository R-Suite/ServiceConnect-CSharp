using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ServiceConnect.Persistance.InMemory
{
    public class SlidingDetails
    {
        /// <summary>
        /// Initialise une nouvelle instance de <see cref="SlidingDetails"/> class.
        /// </summary>
        /// <param name="relativeExpiry">The relative expiry.</param>
        public SlidingDetails(TimeSpan relativeExpiry)
        {
            RelativeExpiry = relativeExpiry;
            Slide();
        }

        private TimeSpan RelativeExpiry { get; set; }

        private DateTime ExpireAt { get; set; }

        /// <summary>
        /// Determines whether this instance can expire the specified try after.
        /// </summary>
        /// <param name="tryAfter">The try after.</param>
        /// <returns></returns>
        public bool CanExpire(out TimeSpan tryAfter)
        {
            tryAfter = (ExpireAt - DateTime.Now);
            return (0 > tryAfter.Ticks);
        }

        /// <summary>
        /// Slides this instance.
        /// </summary>
        public void Slide()
        {
            ExpireAt = DateTime.Now.Add(RelativeExpiry);
        }
    }
}
