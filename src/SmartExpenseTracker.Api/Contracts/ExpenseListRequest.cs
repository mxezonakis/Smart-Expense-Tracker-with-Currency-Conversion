using System.ComponentModel.DataAnnotations;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Api.Contracts
{
    public sealed class ExpenseListRequest : IValidatableObject
    {
        public DateOnly? From { get; init; }
        public DateOnly? To { get; init; }

        [Range(1, int.MaxValue)]
        public int? CategoryId { get; init; }

        [DisplayFormat(ConvertEmptyStringToNull = false)]
        public string? Currency
        {
            get;
            init => field = value?.Trim();
        }

        [RegularExpression("^([Dd][Aa][Tt][Ee]|[Aa][Mm][Oo][Uu][Nn][Tt])$")]
        public string? SortBy { get; init; }

        [RegularExpression("^([Aa][Ss][Cc]|[Dd][Ee][Ss][Cc])$")]
        public string? Direction { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Currency is not null && (Currency.Length != 3 || !Currency.All(char.IsAsciiLetter)))
            {
                yield return new ValidationResult("Currency must contain three ASCII letters.", [nameof(Currency)]);
            }

            if (From > To)
            {
                yield return new ValidationResult("From must precede or equal To.", [nameof(From)]);
            }

            if (Direction is not null && SortBy is null)
            {
                yield return new ValidationResult("Direction requires a sort field.", [nameof(Direction)]);
            }
        }

        internal ExpenseQuery ToQuery() => new(From, To, CategoryId, Currency?.ToUpperInvariant(),
            SortBy is null ? null : Enum.Parse<ExpenseSort>(SortBy, ignoreCase: true),
            !string.Equals(Direction, "asc", StringComparison.OrdinalIgnoreCase));
    }
}
