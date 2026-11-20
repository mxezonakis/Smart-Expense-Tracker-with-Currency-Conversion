using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api
{
    public sealed class RequestStatistics
    {
        private readonly Lock _gate = new();
        private long _completed;
        private long _failed;
        private double _totalDurationMs;

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

        public RequestStatisticsSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new RequestStatisticsSnapshot(_completed, _failed, _totalDurationMs, _completed == 0 ? 0 : _totalDurationMs / _completed);
            }
        }
    }
}
