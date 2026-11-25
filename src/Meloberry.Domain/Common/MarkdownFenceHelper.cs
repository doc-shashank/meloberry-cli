namespace Meloberry.Domain.Common;

public static class MarkdownFenceHelper
{
    public static bool TryOpenCodeFence(string trimmedStart, out string fenceToken)
    {
        fenceToken = string.Empty;
        if (trimmedStart.Length < 3) return false;
        char ch = trimmedStart[0];
        if (ch is not ('`' or '~')) return false;
        int count = 0;
        while (count < trimmedStart.Length && trimmedStart[count] == ch) count++;
        if (count < 3) return false;
        fenceToken = trimmedStart[..count];
        return true;
    }

    public static bool IsCloseCodeFence(string trimmed, string openingToken)
    {
        if (string.IsNullOrEmpty(openingToken) || trimmed.Length < openingToken.Length)
            return false;
        char ch = openingToken[0];
        if (trimmed[0] != ch) return false;
        int count = 0;
        while (count < trimmed.Length && trimmed[count] == ch) count++;
        return count >= openingToken.Length && trimmed[count..].Trim().Length == 0;
    }

    public static bool TryUpdateFenceState(string trimmedLine, ref string? activeFence)
    {
        if (activeFence is null)
        {
            if (!TryOpenCodeFence(trimmedLine, out var token)) return false;
            activeFence = token;
            return true;
        }

        if (!IsCloseCodeFence(trimmedLine, activeFence)) return false;
        activeFence = null;
        return true;
    }
}
