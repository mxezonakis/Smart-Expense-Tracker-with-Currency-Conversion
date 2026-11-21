using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api
{
    /// <summary>Maintains atomic process-local request counters and duration totals.</summary>
    public sealed class RequestStatistics
    {
        private readonly Lock _gate = new();
        private long _completed;
        private long _failed;
        private double _totalDurationMs;

        /// <summary>Records one completed request under a short synchronization lock.</summary>
        /// <param name="statusCode">The final HTTP status; 400-599 counts as a failure.</param>
        /// <param name="duration">The nonnegative elapsed pipeline duration.</param>
        public void Record(int statusCode, TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            lock (_gate)
            {
                _completed++;
                if (statusCode is >= 400 and <= 599)
                {
                    _failed++;
                }

                _totalDurationMs += duration.TotalMilliseconds;
            }
        }

        /// <summary>Reads a consistent snapshot of the counters and elapsed durations.</summary>
        /// <returns>The current totals and mean duration, or zero values before any records.</returns>
        public RequestStatisticsSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new RequestStatisticsSnapshot(_completed, _failed, _totalDurationMs, _completed == 0 ? 0 : _totalDurationMs / _completed);
            }
        }
    }
}
