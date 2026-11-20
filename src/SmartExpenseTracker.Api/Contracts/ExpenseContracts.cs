using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Api.Contracts
{
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
