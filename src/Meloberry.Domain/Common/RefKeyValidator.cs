using System.Text;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Common;

public static class RefKeyValidator
{
    public const int MaxLength = 32;
    public const string Pattern = "^[a-z0-9][a-z0-9_-]{0,31}$";

    public static Result<string?> ValidateAndNormalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result<string?>.Success(null);

        var trimmed = raw.Trim();
        var normalized = trimmed.ToLowerInvariant();
        if (normalized.Length > MaxLength || !IsAsciiAlphanumeric(normalized[0]))
            return AddressErrors.InvalidRef(trimmed, SuggestSlug(trimmed));

        for (int i = 1; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (!IsAsciiAlphanumeric(c) && c != '-' && c != '_')
                return AddressErrors.InvalidRef(trimmed, SuggestSlug(trimmed));
        }

        if (IsReservedPositionalSlug(normalized))
            return AddressErrors.ReservedRef(trimmed, SuggestSlug(trimmed));

        return Result<string?>.Success(normalized);
    }

    public static bool IsReservedPositionalSlug(string normalized)
    {
        if (normalized.Length < 2 || (normalized[0] != 'c' && normalized[0] != 'q'))
            return false;
        for (int i = 1; i < normalized.Length; i++)
        {
            if (normalized[i] < '0' || normalized[i] > '9')
                return false;
        }
        return true;
    }

    public static string SuggestSlug(string? raw, string fallbackPrefix = "ref") =>
        BuildBoundedSlug(raw, fallbackPrefix);

    public static string GenerateSlug(string? seed, string fallbackPrefix, ISet<string> usedRefs)
    {
        string candidate = BuildBoundedSlug(seed, fallbackPrefix);
        if (usedRefs.Add(candidate))
            return candidate;

        for (int suffixNum = 2; ; suffixNum++)
        {
            string suffix = "-" + suffixNum;
            int maxBaseLen = Math.Max(1, MaxLength - suffix.Length);
            string basePart = candidate.Length > maxBaseLen
                ? candidate[..maxBaseLen].TrimEnd('-', '_')
                : candidate;
            if (basePart.Length == 0 || !IsAsciiAlphanumeric(basePart[0]))
                basePart = "r";
            string numbered = basePart + suffix;
            if (usedRefs.Add(numbered))
                return numbered;
        }
    }

    private static string BuildBoundedSlug(string? seed, string fallbackPrefix)
    {
        string normalized = NormalizeToSlugChars(seed);
        string fallback = NormalizeToSlugChars(fallbackPrefix);
        if (fallback.Length == 0 || !IsAsciiAlphanumeric(fallback[0]))
            fallback = "item";

        if (normalized.Length == 0 || !IsAsciiAlphanumeric(normalized[0]) || IsReservedPositionalSlug(normalized))
            normalized = normalized.Length == 0 ? fallback : $"{fallback}-{normalized}".TrimEnd('-', '_');

        if (normalized.Length <= MaxLength)
            return normalized;

        string hash4 = ComputeFnv1a16Hex(normalized);
        string prefix = normalized[..Math.Min(27, normalized.Length)].TrimEnd('-', '_');
        if (prefix.Length == 0 || !IsAsciiAlphanumeric(prefix[0]))
            prefix = fallback[..Math.Min(fallback.Length, 27)];

        return $"{prefix}-{hash4}";
    }

    private static string NormalizeToSlugChars(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        bool prevSep = false;
        foreach (char rawChar in input.Trim().ToLowerInvariant())
        {
            if (IsAsciiAlphanumeric(rawChar))
            {
                sb.Append(rawChar);
                prevSep = false;
            }
            else if (rawChar == '_')
            {
                if (sb.Length > 0 && !prevSep)
                {
                    sb.Append('_');
                    prevSep = true;
                }
            }
            else if (!prevSep && sb.Length > 0)
            {
                sb.Append('-');
                prevSep = true;
            }
        }

        return sb.ToString().Trim('-', '_');
    }

    private static string ComputeFnv1a16Hex(string value)
    {
        uint hash = 2166136261u;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        ushort folded = (ushort)((hash >> 16) ^ (hash & 0xFFFFu));
        return folded.ToString("x4");
    }

    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
