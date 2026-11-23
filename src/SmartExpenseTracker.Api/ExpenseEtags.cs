using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Api
{
    internal static class ExpenseEtags
    {
        internal static string Format(byte[] version) => $"\"{Convert.ToBase64String(version)}\"";

        internal static byte[] RequireIfMatch(IHeaderDictionary headers)
        {
            if (!headers.ContainsKey("If-Match"))
            {
                throw new ExpensePreconditionRequiredException();
            }

            var values = headers.IfMatch;
            if (values.Count != 1 || values[0] is not { Length: <= 128 } raw)
            {
                throw InvalidTag();
            }

            var tag = raw.Trim(' ', '\t');
            if (tag.Length != 14 || tag[0] != '"' || tag[^1] != '"')
            {
                throw InvalidTag();
            }

            var version = new byte[8];
            if (!Convert.TryFromBase64Chars(tag.AsSpan(1, 12), version, out var written) || written != version.Length)
            {
                throw InvalidTag();
            }

            if (Format(version) != tag)
            {
                throw InvalidTag();
            }

            return version;
        }

        private static ExpenseValidationException InvalidTag()
        {
            return new ExpenseValidationException("If-Match", "Supply exactly one strong ETag returned by this resource.");
        }
    }

    internal sealed class ExpensePreconditionRequiredException : Exception
    {
    }
}
