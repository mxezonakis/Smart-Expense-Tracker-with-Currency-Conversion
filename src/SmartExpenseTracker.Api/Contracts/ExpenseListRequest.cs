using System.ComponentModel.DataAnnotations;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Api.Contracts
{
    /// <summary>Optional expense filters and sorting. All filters are combined with AND; all matches are returned.</summary>
    public sealed class ExpenseListRequest : IValidatableObject
    {
        /// <summary>Inclusive earliest expense date in yyyy-MM-dd format.</summary>
        public DateOnly? From { get; init; }
        /// <summary>Inclusive latest expense date in yyyy-MM-dd format; must not precede From.</summary>
        public DateOnly? To { get; init; }

        /// <summary>Filter by a positive category identifier.</summary>
        [Range(1, int.MaxValue)]
        public int? CategoryId { get; init; }

        /// <summary>Filter by original currency: exactly three ASCII letters after trimming, normalized with ToUpperInvariant. Blank is invalid.</summary>
        [DisplayFormat(ConvertEmptyStringToNull = false)]
        public string? Currency
        {
            get;
            init => field = value?.Trim();
        }

        /// <summary>Sort by date or amount (stored converted amount), case-insensitive. Omit for ascending ID order.</summary>
        [RegularExpression("^([Dd][Aa][Tt][Ee]|[Aa][Mm][Oo][Uu][Nn][Tt])$")]
        public string? SortBy { get; init; }

        /// <summary>Sort direction: asc or desc, case-insensitive. Requires SortBy; an explicit sort defaults to desc. Ties use ascending ID.</summary>
        [RegularExpression("^([Aa][Ss][Cc]|[Dd][Ee][Ss][Cc])$")]
        public string? Direction { get; init; }

        /// <summary>Checks currency syntax, inclusive date bounds and the sort combination.</summary>
        /// <param name="validationContext">The validation context supplied by model validation.</param>
        /// <returns>Validation failures, or an empty sequence for a valid query.</returns>
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
