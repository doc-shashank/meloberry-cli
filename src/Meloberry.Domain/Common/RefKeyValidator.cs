using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Common;

public static class RefKeyValidator
{
    public static Result<string?> ValidateAndNormalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result<string?>.Success(null);

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Length > 32 || !IsAsciiAlphanumeric(normalized[0]))
            return AddressErrors.InvalidRef(raw);

        for (int i = 1; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (!IsAsciiAlphanumeric(c) && c != '-' && c != '_')
                return AddressErrors.InvalidRef(raw);
        }

        return Result<string?>.Success(normalized);
    }

    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
