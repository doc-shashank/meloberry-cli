namespace Meloberry.Domain.Common;

public static class TokenEstimator
{
    /// <summary>
    /// Fast, zero-dependency BPE-aware token count approximation for Markdown and JSON playbooks.
    /// Matches standard GPT/Claude cl100k/o200k token density without shipping a heavy tokenizer binary.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                var run = 0;
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r'))
                {
                    run++;
                    i++;
                }

                if (run > 1)
                {
                    tokens += Math.Max(1, (run - 1) / 4);
                }

                continue;
            }

            if (ch == '\n')
            {
                tokens++;
                i++;
                continue;
            }

            if (char.IsLetter(ch))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                var len = i - start;
                tokens += len <= 6 ? 1 : (int)Math.Ceiling(len / 4.5);
                continue;
            }

            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                var len = i - start;
                tokens += Math.Max(1, (int)Math.Ceiling(len / 3.0));
                continue;
            }

            var punct = ch;
            while (i < text.Length && text[i] == punct)
            {
                i++;
            }

            tokens++;
        }

        return Math.Max(1, tokens);
    }

    public static string MakeCodeFence(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "```";
        }

        var maxRun = 0;
        var run = 0;
        foreach (var ch in content)
        {
            if (ch == '`')
            {
                run++;
                if (run > maxRun)
                {
                    maxRun = run;
                }
            }
            else
            {
                run = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }
}
