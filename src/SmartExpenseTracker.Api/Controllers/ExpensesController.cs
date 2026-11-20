using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Api.Controllers
{
    [ApiController]
    [Route("api/expenses")]
    public sealed class ExpensesController : ControllerBase
    {
        private readonly ExpenseService _expenses;

        public ExpensesController(ExpenseService expenses)
        {
            _expenses = expenses;
        }

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

        [HttpGet]
        public async Task<ActionResult<ExpenseResponse[]>> List(CancellationToken cancellationToken)
        {
            var expenses = await _expenses.ListAsync(cancellationToken);
            return expenses.Select(ExpenseResponse.FromExpense).ToArray();
        }

        [HttpGet("{id:int}")]
        [ProducesResponseType<ExpenseResponse>(200)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var expense = await _expenses.FindAsync(id, cancellationToken);
            return expense is null ? NotFound() : Ok(ExpenseResponse.FromExpense(expense));
        }

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

        [HttpDelete("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            return await _expenses.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
        }
    }
}
