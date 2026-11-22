using SmartExpenseTracker.Application.Expenses;

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
            if (values.Count == 1 && values[0] is { Length: <= 128 } raw)
            {
                var tag = raw.Trim(' ', '\t');
                Span<byte> version = stackalloc byte[8];
                if (tag.Length == 14 && tag[0] == '"' && tag[^1] == '"' &&
                    Convert.TryFromBase64Chars(tag.AsSpan(1, 12), version, out var written) && written == 8 &&
                    Convert.ToBase64String(version) == tag[1..^1])
                {
                    return version.ToArray();
                }
            }

            throw new ExpenseValidationException("If-Match", "Supply exactly one strong ETag returned by this resource.");
        }
    }

    internal sealed class ExpensePreconditionRequiredException : Exception
    {
    }
}
