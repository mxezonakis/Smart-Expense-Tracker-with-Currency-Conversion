using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Reporting;

namespace SmartExpenseTracker.Api.Controllers
{
    [ApiController]
    [Route("api/reports")]
    public sealed class ReportsController : ControllerBase
    {
        private readonly ReportService _reports;

        public ReportsController(ReportService reports)
        {
            _reports = reports;
        }

        [HttpGet("by-category")]
        [EndpointDescription("Sum of stored converted amounts per category, ordered by category ID.")]
        public async Task<ActionResult<CategoryTotalResponse[]>> ByCategory(CancellationToken cancellationToken)
        {
            var totals = await _reports.ByCategoryAsync(cancellationToken);
            return totals.Select(row => new CategoryTotalResponse(row.CategoryId, row.TotalAmount, row.BaseCurrency)).ToArray();
        }

        [HttpGet("by-period")]
        [ProducesResponseType<PeriodTotalResponse[]>(200)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [EndpointDescription("Sum of stored converted amounts by day, week (Monday start), or month. Ordered by period start.")]
        public async Task<ActionResult<PeriodTotalResponse[]>> ByPeriod([FromQuery] string period, CancellationToken cancellationToken)
        {
            if (ReportService.ParsePeriod(period) is not { } grouping)
            {
                ModelState.AddModelError(nameof(period), "Period must be day, week or month.");
                return ValidationProblem(ModelState);
            }

            var totals = await _reports.ByPeriodAsync(grouping, cancellationToken);
            return totals.Select(row => new PeriodTotalResponse(row.PeriodStart, row.TotalAmount, row.BaseCurrency)).ToArray();
        }
    }
}
