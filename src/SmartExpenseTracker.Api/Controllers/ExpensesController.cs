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
        /// <response code="201">The expense and stored conversion were created; Location identifies the resource and ETag identifies its version.</response>
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
            var snapshot = await _expenses.CreateAsync(request.ToInput(), cancellationToken);
            Response.Headers.ETag = ExpenseEtags.Format(snapshot.Version);
            return CreatedAtAction(nameof(Get), new { id = snapshot.Expense.Id }, ExpenseResponse.FromExpense(snapshot.Expense));
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
        /// <remarks>Retrieval does not refresh or recalculate the exchange rate. Save the strong ETag response header and send it as If-Match when updating or deleting this expense. List results do not include individual ETags; retrieve the individual resource first.</remarks>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">The expense with its persisted conversion snapshot and matching ETag response header.</response>
        /// <response code="404">The expense does not exist.</response>
        [HttpGet("{id:int}")]
        [ProducesResponseType<ExpenseResponse>(200)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var snapshot = await _expenses.FindAsync(id, cancellationToken);
            if (snapshot is null)
            {
                return NotFound();
            }

            Response.Headers.ETag = ExpenseEtags.Format(snapshot.Version);
            return Ok(ExpenseResponse.FromExpense(snapshot.Expense));
        }

        /// <summary>Update an expense.</summary>
        /// <remarks>Requires exactly one specific strong If-Match ETag obtained from GET or POST. Recalculates the conversion only when the original amount or normalized currency changes. Category, date and description edits preserve the stored rate, converted amount and provider metadata. Unchanged normalized values return 200 without a write and with the initially read ETag. This path does not detect every concurrent change after the initial version check. Weak tags, wildcard and multiple tags are unsupported.</remarks>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">The replacement expense fields.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">No persisted values changed; updated is false, reason is unchanged and ETag is the initially read version.</response>
        /// <response code="204">The expense was updated; ETag contains the new version.</response>
        /// <response code="400">The input or If-Match is invalid or unsupported, the category is missing, or the currency is unsupported by an eligible rate table.</response>
        /// <response code="404">The expense does not exist.</response>
        /// <response code="409">The dataset base currency conflicts with configuration.</response>
        /// <response code="412">The supplied ETag is stale, or the expense changed or was deleted during the update.</response>
        /// <response code="428">The required If-Match header is missing.</response>
        /// <response code="503">A required replacement exchange rate is unavailable.</response>
        [HttpPut("{id:int}")]
        [ProducesResponseType<ExpenseUnchangedResponse>(200)]
        [ProducesResponseType(204)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(404)]
        [ProducesResponseType<ProblemDetails>(409)]
        [ProducesResponseType<ProblemDetails>(412)]
        [ProducesResponseType<ProblemDetails>(428)]
        [ProducesResponseType<ProblemDetails>(503)]
        public async Task<IActionResult> Update(int id, ExpenseRequest request, CancellationToken cancellationToken)
        {
            var expectedVersion = ExpenseEtags.RequireIfMatch(Request.Headers);
            var result = await _expenses.UpdateAsync(id, request.ToInput(), expectedVersion, cancellationToken);
            if (result is null)
            {
                return NotFound();
            }

            Response.Headers.ETag = ExpenseEtags.Format(result.Version);
            return result.Updated ? NoContent() : Ok(new ExpenseUnchangedResponse(false, "unchanged"));
        }

        /// <summary>Delete an expense.</summary>
        /// <remarks>Requires exactly one specific strong If-Match ETag. Deletes atomically only if the version still matches. An absent resource also returns 412 because the supplied precondition cannot match. Weak tags, wildcard and multiple tags are unsupported.</remarks>
        /// <param name="id">The expense identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="204">The expense was deleted.</response>
        /// <response code="400">If-Match is malformed or unsupported.</response>
        /// <response code="412">The supplied ETag does not match a current expense, including an already deleted expense.</response>
        /// <response code="428">The required If-Match header is missing.</response>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(412)]
        [ProducesResponseType<ProblemDetails>(428)]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            var expectedVersion = ExpenseEtags.RequireIfMatch(Request.Headers);
            await _expenses.DeleteAsync(id, expectedVersion, cancellationToken);
            return NoContent();
        }
    }
}
