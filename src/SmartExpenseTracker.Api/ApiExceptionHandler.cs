using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Persistence.Categories;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Api
{
    internal sealed class ApiExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problems;

        public ApiExceptionHandler(IProblemDetailsService problems)
        {
            _problems = problems;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            ProblemDetails? problem = exception switch
            {
                InvalidCategoryNameException invalid => new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    ["name"] = [invalid.Message]
                })
                {
                    Status = StatusCodes.Status400BadRequest
                },
                DuplicateCategoryNameException => new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "A category with this name already exists."
                },
                ExpenseValidationException invalid => new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [invalid.Field] = [invalid.Message]
                })
                {
                    Status = StatusCodes.Status400BadRequest
                },
                ExpenseConflictException conflict => new ProblemDetails { Status = 409, Title = conflict.Message },
                ExpensePreconditionRequiredException => new ProblemDetails
                {
                    Status = 428,
                    Title = "An If-Match header is required for this operation."
                },
                ExpensePreconditionFailedException => new ProblemDetails
                {
                    Status = 412,
                    Title = "The expense no longer matches the supplied precondition."
                },
                RateUnavailableException => new ProblemDetails { Status = 503, Title = "No eligible exchange rate is available." },
                _ => null
            };

            if (problem is null)
            {
                return false;
            }

            httpContext.Response.StatusCode = problem.Status!.Value;
            if (!await _problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem }))
            {
                httpContext.Response.ContentLength = 0;
            }

            return true;
        }
    }
}
