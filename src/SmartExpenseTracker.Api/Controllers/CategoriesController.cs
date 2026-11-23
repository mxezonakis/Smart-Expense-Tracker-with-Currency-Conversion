using Microsoft.AspNetCore.Mvc;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Persistence.Categories;

namespace SmartExpenseTracker.Api.Controllers
{
    /// <summary>Manages expense categories.</summary>
    [ApiController]
    [Route("api/categories")]
    public sealed class CategoriesController : ControllerBase
    {
        private readonly CategoryService _categories;

        /// <summary>Creates the controller with its application service.</summary>
        /// <param name="categories">The application service for this controller.</param>
        public CategoriesController(CategoryService categories)
        {
            _categories = categories;
        }

        /// <summary>Create a category.</summary>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">The category name.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="201">The category was created; Location identifies the new resource.</response>
        /// <response code="400">The name or request body is invalid.</response>
        /// <response code="409">A category with the same case-insensitive, accent-sensitive name already exists.</response>
        [HttpPost]
        [ProducesResponseType<CategoryResponse>(201)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(409)]
        public async Task<ActionResult<CategoryResponse>> Create(CategoryRequest request, CancellationToken cancellationToken)
        {
            var category = await _categories.CreateAsync(request.Name, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = category.Id }, ToResponse(category));
        }

        /// <summary>List categories.</summary>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">All categories in ascending ID order, or an empty array.</response>
        [HttpGet]
        [ProducesResponseType<CategoryResponse[]>(200)]
        public async Task<ActionResult<CategoryResponse[]>> List(CancellationToken cancellationToken)
        {
            var categories = await _categories.ListAsync(cancellationToken);
            return categories.Select(ToResponse).ToArray();
        }

        /// <summary>Get a category.</summary>
        /// <param name="id">The category identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="200">The category.</response>
        /// <response code="404">The category does not exist.</response>
        [HttpGet("{id:int}")]
        [ProducesResponseType<CategoryResponse>(200)]
        [ProducesResponseType<ProblemDetails>(404)]
        public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken cancellationToken)
        {
            var category = await _categories.FindAsync(id, cancellationToken);
            return category is null ? NotFound() : Ok(ToResponse(category));
        }

        /// <summary>Update a category name.</summary>
        /// <param name="id">The category identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <param name="request">The replacement category name.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="204">The category was updated.</response>
        /// <response code="400">The name or request body is invalid.</response>
        /// <response code="404">The category does not exist.</response>
        /// <response code="409">Another category already uses this name.</response>
        [HttpPut("{id:int}")]
        [ProducesResponseType(204)]
        [ProducesResponseType<ValidationProblemDetails>(400)]
        [ProducesResponseType<ProblemDetails>(404)]
        [ProducesResponseType<ProblemDetails>(409)]
        public async Task<IActionResult> Update(int id, CategoryRequest request, CancellationToken cancellationToken)
        {
            return await _categories.RenameAsync(id, request.Name, cancellationToken) ? NoContent() : NotFound();
        }

        /// <summary>Delete a category.</summary>
        /// <remarks>A category can be deleted only after its expenses have been removed or reassigned.</remarks>
        /// <param name="id">The category identifier.</param>
        /// <param name="cancellationToken">Cancels the request and supported database or provider operations.</param>
        /// <returns>The HTTP result described below.</returns>
        /// <response code="204">The category was deleted.</response>
        /// <response code="404">The category does not exist.</response>
        /// <response code="409">Expenses still reference this category.</response>
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
