using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api.Controllers
{
    /// <summary>Exposes request statistics for this application instance.</summary>
    [ApiController]
    [Route("api/statistics")]
    public sealed class StatisticsController : ControllerBase
    {
        private readonly RequestStatistics _statistics;

        /// <summary>Creates the controller with its host-local collector.</summary>
        /// <param name="statistics">The thread-safe statistics collector.</param>
        public StatisticsController(RequestStatistics statistics)
        {
            _statistics = statistics;
        }

        /// <summary>Get request statistics.</summary>
        /// <remarks>Counts normally completed requests and failures with final HTTP status 400-599. Durations use a monotonic clock. GET /api/statistics reads, aborted requests and escaping exceptions are excluded. Counters reset on restart and are not shared between instances.</remarks>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">A consistent snapshot of this application instance, with zero values before any recorded requests.</response>
        [HttpGet]
        [ProducesResponseType<RequestStatisticsSnapshot>(200)]
        [EndpointDescription("Process-local completed requests, failures (HTTP 400–599), and total and mean elapsed milliseconds. Statistics reads are excluded. Counters reset on restart.")]
        public ActionResult<RequestStatisticsSnapshot> Get()
        {
            return _statistics.Snapshot();
        }
    }
}
