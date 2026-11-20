using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Categories;

namespace SmartExpenseTracker.Api.Controllers
{
    [ApiController]
    [Route("api/categories")]
    public sealed class CategoriesController : ControllerBase
    {
        private readonly CategoryService _categories;

        public CategoriesController(CategoryService categories)
        {
            _categories = categories;
        }

        [HttpPost]
        [ProducesResponseType<CategoryResponse>(201)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(409)]
        public async Task<ActionResult<CategoryResponse>> Create(CategoryRequest request, CancellationToken cancellationToken)
        {
            var category = await _categories.CreateAsync(request.Name, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = category.Id }, ToResponse(category));
        }

        [HttpGet]
        public async Task<ActionResult<CategoryResponse[]>> List(CancellationToken cancellationToken)
        {
            var categories = await _categories.ListAsync(cancellationToken);
            return categories.Select(ToResponse).ToArray();
        }

        [HttpGet("{id:int}")]
        [ProducesResponseType<CategoryResponse>(200)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var category = await _categories.FindAsync(id, cancellationToken);
            return category is null ? NotFound() : Ok(ToResponse(category));
        }

        [HttpPut("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(404)]
        [ProducesResponseType<ProblemDetails>(409)]
        public async Task<IActionResult> Update(int id, CategoryRequest request, CancellationToken cancellationToken)
        {
            return await _categories.RenameAsync(id, request.Name, cancellationToken) ? NoContent() : NotFound();
        }

        [HttpDelete("{id:int}")]
        [ProducesResponseType<ProblemDetails>(409)]
        [ProducesResponseType(204)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            return await _categories.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
        }

        private static CategoryResponse ToResponse(Category category)
        {
            return new CategoryResponse(category.Id, category.Name);
        }
    }
}
