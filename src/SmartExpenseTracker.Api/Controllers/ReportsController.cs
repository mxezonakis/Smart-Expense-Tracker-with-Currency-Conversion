using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Reporting;

namespace SmartExpenseTracker.Api.Controllers
{
    /// <summary>Reports spending using persisted converted amounts.</summary>
    [ApiController]
    [Route("api/reports")]
    public sealed class ReportsController : ControllerBase
    {
        private readonly ReportService _reports;

        /// <summary>Creates the controller with its application service.</summary>
        /// <param name="reports">The application service for this controller.</param>
        public ReportsController(ReportService reports)
        {
            _reports = reports;
        }

        /// <summary>Get total spending by category.</summary>
        /// <remarks>Sums persisted converted amounts across the whole dataset in its base currency. Rates are not refreshed and categories without expenses are omitted.</remarks>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">Totals for categories with expenses, ordered by category ID; an empty dataset returns an empty array.</response>
        [HttpGet("by-category")]
        [ProducesResponseType<CategoryTotalResponse[]>(200)]
        [EndpointDescription("Sum of stored converted amounts per category, ordered by category ID.")]
        public async Task<ActionResult<CategoryTotalResponse[]>> ByCategory(CancellationToken cancellationToken)
        {
            var totals = await _reports.ByCategoryAsync(cancellationToken);
            return totals.Select(row => new CategoryTotalResponse(row.CategoryId, row.TotalAmount, row.BaseCurrency)).ToArray();
        }

        /// <summary>Get total spending by calendar period.</summary>
        /// <remarks>Groups the whole dataset by expense date and sums stored converted amounts. Weekly and monthly keys are the first calendar date of the period; no exchange-rate lookup is performed.</remarks>
        /// <param name="period">Required grouping: day, week or month (case-insensitive). Weeks start on Monday.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">Totals for periods containing expenses, ordered by period start; empty periods are omitted.</response>
        /// <response code="400">The period is missing or is not day, week or month.</response>
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
