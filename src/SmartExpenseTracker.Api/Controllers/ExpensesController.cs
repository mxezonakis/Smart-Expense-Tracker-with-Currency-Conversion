using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Api.Controllers
{
    /// <summary>Manages expenses and their stored currency conversions.</summary>
    [ApiController]
    [Route("api/expenses")]
    public sealed class ExpensesController : ControllerBase
    {
        private readonly ExpenseService _expenses;

        /// <summary>Creates the controller with its application service.</summary>
        /// <param name="expenses">The application service for this controller.</param>
        public ExpensesController(ExpenseService expenses)
        {
            _expenses = expenses;
        }

        /// <summary>Create an expense with a conversion snapshot.</summary>
        /// <remarks>Converts to the configured dataset base currency using eligible provider-published rates. Base-currency expenses use rate 1 without a provider call. The expense date does not select historical rates.</remarks>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">The original amount, currency, category, date and optional description.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="201">The expense and stored conversion were created; Location identifies the new resource.</response>
        /// <response code="400">The input is invalid, the category is missing, or the currency is unsupported by an eligible rate table.</response>
        /// <response code="409">The dataset base currency conflicts with configuration, or the category changed concurrently.</response>
        /// <response code="503">No eligible exchange rate is available.</response>
        [HttpPost]
        [ProducesResponseType<ExpenseResponse>(201)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(409)]
        [ProducesResponseType<ProblemDetails>(503)]
        public async Task<ActionResult<ExpenseResponse>> Create(ExpenseRequest request, CancellationToken cancellationToken)
        {
            var expense = await _expenses.CreateAsync(request.ToInput(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = expense.Id }, ExpenseResponse.FromExpense(expense));
        }

        /// <summary>List, filter and sort expenses.</summary>
        /// <remarks>Date boundaries are inclusive. Currency matches the original currency after trimming and invariant uppercasing. Amount sorting uses stored converted amounts. An explicit sort defaults to descending; ties use ascending ID. Without a sort, ID is ascending. No pagination or exchange-rate lookup is performed.</remarks>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">Optional filters combined with AND, plus deterministic sorting.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">All matching expenses with their stored conversions, or an empty array.</response>
        /// <response code="400">A query value, date range or sort combination is invalid.</response>
        [HttpGet]
        [EndpointDescription("Returns all matches in ascending ID order by default. From and To are inclusive. " +
            "CategoryId and original Currency filters can be combined. SortBy accepts date or amount (stored converted amount). " +
            "Direction accepts asc or desc and defaults to desc for an explicit sort. Ties use ascending ID.")]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ExpenseResponse[]>(200)]
        public async Task<ActionResult<ExpenseResponse[]>> List([FromQuery] ExpenseListRequest request, CancellationToken cancellationToken)
        {
            var expenses = await _expenses.ListAsync(request.ToQuery(), cancellationToken);
            return expenses.Select(ExpenseResponse.FromExpense).ToArray();
        }

        /// <summary>Get an expense and its stored conversion.</summary>
        /// <remarks>Retrieval does not refresh or recalculate the exchange rate.</remarks>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">The expense with its persisted conversion snapshot.</response>
        /// <response code="404">The expense does not exist.</response>
        [HttpGet("{id:int}")]
        [ProducesResponseType<ExpenseResponse>(200)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var expense = await _expenses.FindAsync(id, cancellationToken);
            return expense is null ? NotFound() : Ok(ExpenseResponse.FromExpense(expense));
        }

        /// <summary>Update an expense.</summary>
        /// <remarks>Recalculates the conversion only when the original amount or normalized currency changes. Category, date and description edits preserve the stored rate, converted amount and provider metadata.</remarks>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">The replacement expense fields.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="204">The expense was updated.</response>
        /// <response code="400">The input is invalid, the category is missing, or the currency is unsupported by an eligible rate table.</response>
        /// <response code="404">The expense does not exist.</response>
        /// <response code="409">The dataset base currency conflicts with configuration, or a concurrent modification prevents the update.</response>
        /// <response code="503">A required replacement exchange rate is unavailable.</response>
        [HttpPut("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(404)]
        [ProducesResponseType<ProblemDetails>(409)]
        [ProducesResponseType<ProblemDetails>(503)]
        public async Task<IActionResult> Update(int id, ExpenseRequest request, CancellationToken cancellationToken)
        {
            return await _expenses.UpdateAsync(id, request.ToInput(), cancellationToken) ? NoContent() : NotFound();
        }

        /// <summary>Delete an expense.</summary>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="204">The expense was deleted.</response>
        /// <response code="404">The expense does not exist.</response>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            return await _expenses.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
        }
    }
}
