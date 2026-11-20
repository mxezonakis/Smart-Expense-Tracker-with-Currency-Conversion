using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Application.Categories;

namespace SmartExpenseTracker.Api
{
    internal sealed class CategoryExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problems;

        public CategoryExceptionHandler(IProblemDetailsService problems)
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
