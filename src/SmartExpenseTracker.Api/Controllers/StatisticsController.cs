using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api.Controllers
{
    [ApiController]
    [Route("api/statistics")]
    public sealed class StatisticsController : ControllerBase
    {
        private readonly RequestStatistics _statistics;

        public StatisticsController(RequestStatistics statistics)
        {
            _statistics = statistics;
        }

        [HttpGet]
        [EndpointDescription("Process-local completed requests, failures (HTTP 400–599), and total and mean elapsed milliseconds. Statistics reads are excluded. Counters reset on restart.")]
        public ActionResult<RequestStatisticsSnapshot> Get()
        {
            return _statistics.Snapshot();
        }
    }
}
