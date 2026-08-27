using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Converts a monotonically increasing total into the delta represented by each event. The
    /// delta is retained by total so a crew-wide fan-out of the same event produces the same
    /// answer on every participant's machine instead of only the first message getting credit.
    /// </summary>
    internal sealed class CumulativeCountTracker
    {
        private readonly Dictionary<int, int> _deltaByTotal = new Dictionary<int, int>();
        private int _highWater;

        internal int Observe(int cumulativeTotal)
        {
            // Older provider builds may omit Count. Their event still represents one action.
            if (cumulativeTotal <= 0)
                return 1;

            if (cumulativeTotal > _highWater)
            {
                int delta = cumulativeTotal - _highWater;
                _highWater = cumulativeTotal;
                _deltaByTotal[cumulativeTotal] = delta;
                return delta;
            }

            return _deltaByTotal.TryGetValue(cumulativeTotal, out int existing)
                ? existing
                : 0;
        }
    }
}
