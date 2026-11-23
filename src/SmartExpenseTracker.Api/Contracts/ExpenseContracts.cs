using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartExpenseTracker.Application.Expenses;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Api.Contracts
{
    /// <summary>Original expense fields used for creation and replacement.</summary>
    /// <param name="Amount">Positive original amount from 0.01 to 99999999.99 in whole cents.</param>
    /// <param name="Currency">Three ASCII letters, trimmed and normalized to uppercase. Foreign currencies must be present in an eligible rate table.</param>
    /// <param name="CategoryId">The identifier of an existing category.</param>
    /// <param name="Date">Expense calendar date in yyyy-MM-dd format. Future dates are accepted; this does not request historical rates.</param>
    /// <param name="Description">Optional text, trimmed to at most 500 UTF-16 code units. Omitted, null or blank becomes null.</param>
    public sealed record ExpenseRequest(
        [property: JsonRequired, JsonConverter(typeof(ExpenseAmountConverter))]
        [property: Description("Positive original amount in whole cents, up to 99999999.99.")]
        decimal Amount,
        [property: JsonRequired, Description("Three ASCII currency letters. Surrounding whitespace is trimmed.")] string Currency,
        [property: JsonRequired] int CategoryId,
        [property: JsonRequired, Description("ISO calendar date. Future dates are accepted.")] DateOnly Date,
        [property: Description("Optional, up to 500 UTF-16 code units after trimming. Blank becomes null.")] string? Description)
    {
        internal ExpenseInput ToInput() => new ExpenseInput(Amount, Currency, CategoryId, Date, Description);
    }

    /// <summary>A validated replacement with no effective persisted changes.</summary>
    /// <param name="Updated">False because no database update was performed.</param>
    /// <param name="Reason">The reason no update was performed: unchanged.</param>
    public sealed record ExpenseUnchangedResponse(bool Updated, string Reason);

    // Decimal parsing can round excess significant digits before application validation sees them.
    internal sealed class ExpenseAmountConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var amount))
            {
                throw new JsonException("A decimal amount is required.");
            }

            using var raw = JsonDocument.ParseValue(ref reader);
            if (!JsonElement.DeepEquals(raw.RootElement, JsonSerializer.SerializeToElement(amount)))
            {
                throw new JsonException("The amount exceeds decimal precision.");
            }

            return amount;
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>A stored expense and its conversion snapshot.</summary>
    /// <remarks>Reads and metadata-only edits preserve this snapshot. Rates are provider-published observations, not continuously streaming quotes.</remarks>
    /// <param name="Id">The database-generated expense identifier.</param>
    /// <param name="Amount">The original amount in whole cents.</param>
    /// <param name="Currency">The normalized original currency code.</param>
    /// <param name="CategoryId">The referenced category identifier.</param>
    /// <param name="Date">The expense calendar date in yyyy-MM-dd format.</param>
    /// <param name="Description">The trimmed optional description, or null.</param>
    /// <param name="BaseCurrency">The dataset base currency used for this conversion; EUR by default.</param>
    /// <param name="AppliedRate">Base-currency units per original-currency unit, rounded half-to-even to 12 decimal places. Base-to-base conversion uses 1.</param>
    /// <param name="ConvertedAmount">Original amount multiplied by the stored applied rate, rounded once to cents using half-to-even rounding. May be zero for a small positive expense.</param>
    /// <param name="Provider">The exchange-rate provider name, or null for base-to-base conversion.</param>
    /// <param name="PublishedAt">The original provider publication timestamp in UTC, or null for base-to-base conversion. This is not the expense date or retrieval time.</param>
    /// <param name="IsFallback">True when the expense uses a last-known rate after refresh failure or when the latest rate for its currency cannot fit the expense precision. Base-to-base conversions use false.</param>
    public sealed record ExpenseResponse(int Id, decimal Amount, string Currency, int CategoryId, DateOnly Date,
        string? Description, string BaseCurrency, decimal AppliedRate, decimal ConvertedAmount, string? Provider,
        DateTimeOffset? PublishedAt, bool IsFallback)
    {
        internal static ExpenseResponse FromExpense(Expense expense)
        {
            var snapshot = expense.Conversion;
            return new ExpenseResponse(expense.Id, expense.Amount, expense.Currency, expense.CategoryId, expense.Date,
                expense.Description, snapshot.BaseCurrency, snapshot.AppliedRate, snapshot.ConvertedAmount,
                snapshot.Provider, snapshot.PublishedAt, snapshot.IsFallback);
        }
    }
}
