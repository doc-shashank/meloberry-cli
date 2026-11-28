using System.Globalization;
using System.Text;
using Meloberry.Domain.Common;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Scaffolding;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Diagnostics;

/// <summary>
/// Projects persisted sessions, components, blocks, quizzes, and questions back into canonical
/// Modular Markdown compatible with <c>mby apply</c> round-tripping.
/// </summary>
public static class WorkspaceMarkdownProjector
{
    public static Dictionary<string, string> ProjectSessionToFiles(StudySession session)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>(session.Components.Count);

        foreach (var comp in session.Components)
        {
            var slug = Slugify(comp.RefKey ?? comp.Title, comp.Kind == ComponentKind.Article ? $"article_{comp.Index}" : $"quiz_{comp.Index}");
            var fileName = $"{comp.Index:D2}_{slug}.md";
            order.Add(fileName);
            files[fileName] = ProjectComponentToMarkdown(comp);
        }

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: session");
        if (!string.IsNullOrWhiteSpace(session.Name))
            sb.AppendLine($"name: {YamlQuote(session.Name)}");
        if (!string.IsNullOrWhiteSpace(session.Description))
            AppendYamlField(sb, "", "description", session.Description);
        if (order.Count > 0)
        {
            sb.AppendLine("order:");
            foreach (var f in order)
                sb.AppendLine($"  - {f}");
        }
        sb.AppendLine("---");
        files["_session.md"] = sb.ToString();
        return files;
    }

    public static Dictionary<string, string> ProjectQuizToFiles(Quiz quiz)
    {
        var slug = Slugify(quiz.Name, "quiz");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{slug}.md"] = ProjectQuizToMarkdown(quiz, isStandalone: true, layoutOverrides: null, refKey: null)
        };
    }

    public static string ProjectComponentToMarkdown(SessionComponent component)
    {
        if (component.Kind == ComponentKind.Quiz && component.Quiz is not null)
            return ProjectQuizToMarkdown(component.Quiz, isStandalone: false, component.LayoutOverrides, component.RefKey, component.Title);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: article");
        if (!string.IsNullOrWhiteSpace(component.Title))
            sb.AppendLine($"title: {YamlQuote(component.Title)}");
        if (!string.IsNullOrWhiteSpace(component.RefKey))
            sb.AppendLine($"ref: {component.RefKey}");
        AppendLayoutFrontmatter(sb, component.LayoutOverrides);
        sb.AppendLine("---");
        sb.AppendLine();

        var chunks = new List<string>();
        foreach (var block in component.ArticleBlocks)
            chunks.Add(ProjectBlockToMarkdown(block));
        foreach (var link in component.LinkCards)
            chunks.Add(ProjectLinkCardToMarkdown(link));

        sb.Append(string.Join("\n\n", chunks));
        if (chunks.Count > 0) sb.AppendLine();
        return sb.ToString();
    }

    public static string ProjectQuizToMarkdown(
        Quiz quiz,
        bool isStandalone,
        IReadOnlyDictionary<string, string>? layoutOverrides,
        string? refKey,
        string? componentTitle = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: quiz");
        sb.AppendLine($"quizType: {quiz.Type.ToString().ToLowerInvariant()}");
        if (isStandalone)
        {
            if (!string.IsNullOrWhiteSpace(quiz.Name))
                sb.AppendLine($"name: {YamlQuote(quiz.Name)}");
            if (!string.IsNullOrWhiteSpace(quiz.Description))
                AppendYamlField(sb, "", "description", quiz.Description);
        }
        else if (!string.IsNullOrWhiteSpace(componentTitle))
        {
            sb.AppendLine($"title: {YamlQuote(componentTitle)}");
        }
        if (!string.IsNullOrWhiteSpace(refKey))
            sb.AppendLine($"ref: {refKey}");
        AppendLayoutFrontmatter(sb, layoutOverrides);
        sb.AppendLine("---");
        sb.AppendLine();

        var chunks = quiz.Questions.Select(q => ProjectQuestionToMarkdown(q)).ToList();
        sb.Append(string.Join("\n\n", chunks));
        if (chunks.Count > 0) sb.AppendLine();
        return sb.ToString();
    }

    public static string ProjectBlockToMarkdown(ArticleBlock block)
    {
        switch (block.Kind)
        {
            case ArticleBlockKind.Text:
                return block.TextContent ?? string.Empty;

            case ArticleBlockKind.Image:
            {
                var alt = (block.Alt ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
                var target = MarkdownSmartWrapper.FormatMarkdownImageTarget(block.ImagePath ?? string.Empty);
                return string.IsNullOrWhiteSpace(block.Caption)
                    ? $"![{alt}]({target})"
                    : $"![{alt}]({target} \"{EscapeAttr(block.Caption)}\")";
            }

            case ArticleBlockKind.Callout:
            {
                var attrs = new List<string>();
                if (!string.IsNullOrWhiteSpace(block.RefKey)) attrs.Add($"ref=\"{block.RefKey}\"");
                var tone = block.CalloutTone.HasValue ? ArticleBlock.CalloutToneToString(block.CalloutTone.Value) : "pearl";
                attrs.Add($"tone=\"{tone}\"");
                if (!string.IsNullOrWhiteSpace(block.CalloutTitle)) attrs.Add($"title=\"{EscapeAttr(block.CalloutTitle)}\"");
                return $":::callout{{{string.Join(' ', attrs)}}}\n{block.TextContent ?? string.Empty}\n:::";
            }

            case ArticleBlockKind.Table:
            {
                var attrs = new List<string>();
                if (!string.IsNullOrWhiteSpace(block.RefKey)) attrs.Add($"ref=\"{block.RefKey}\"");
                var tbl = Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(block.PayloadJson, out var parsed) && parsed is not null
                    ? parsed
                    : new TableBlockPayload(block.CalloutTitle, null, false, [], [], [], [], null);

                var title = tbl.Title ?? block.CalloutTitle;
                if (!string.IsNullOrWhiteSpace(title)) attrs.Add($"title=\"{EscapeAttr(title)}\"");
                if (!string.IsNullOrWhiteSpace(tbl.Caption)) attrs.Add($"caption=\"{EscapeAttr(tbl.Caption)}\"");
                if (tbl.StickyFirstCol) attrs.Add("stickyFirstCol=true");
                if (tbl.HideableCols is { Count: > 0 }) attrs.Add($"hideableCols=\"{string.Join(',', tbl.HideableCols)}\"");

                var sb = new StringBuilder();
                sb.AppendLine(attrs.Count == 0 ? ":::table" : $":::table{{{string.Join(' ', attrs)}}}");
                AppendMarkdownTable(sb, tbl.Headers ?? [], tbl.Alignments ?? [], tbl.Rows ?? []);
                if (!string.IsNullOrWhiteSpace(tbl.Footnote))
                {
                    sb.AppendLine(":::footnote");
                    sb.AppendLine(tbl.Footnote);
                }
                sb.Append(":::");
                return sb.ToString();
            }

            case ArticleBlockKind.DiagramExplore:
            {
                var attrs = new List<string>();
                if (!string.IsNullOrWhiteSpace(block.RefKey)) attrs.Add($"ref=\"{block.RefKey}\"");
                Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(block.PayloadJson, out var diag);
                var img = !string.IsNullOrWhiteSpace(block.ImagePath) ? block.ImagePath : (diag?.ImagePath ?? string.Empty);
                attrs.Add($"image=\"{EscapeAttr(img)}\"");
                var title = diag?.Title ?? block.CalloutTitle;
                var caption = diag?.Caption ?? block.Caption;
                if (!string.IsNullOrWhiteSpace(title)) attrs.Add($"title=\"{EscapeAttr(title)}\"");
                if (!string.IsNullOrWhiteSpace(caption)) attrs.Add($"caption=\"{EscapeAttr(caption)}\"");
                if (!string.IsNullOrWhiteSpace(block.Alt)) attrs.Add($"alt=\"{EscapeAttr(block.Alt)}\"");

                var sb = new StringBuilder();
                sb.AppendLine($":::diagram-explore{{{string.Join(' ', attrs)}}}");
                sb.AppendLine("boxes:");
                foreach (var hs in diag?.Hotspots ?? [])
                {
                    sb.AppendLine($"  - label: {YamlQuote(hs.Label)}");
                    AppendYamlField(sb, "    ", "note", hs.Note);
                    sb.AppendLine(FormattableString.Invariant($"    rect: [{hs.X:G}, {hs.Y:G}, {hs.W:G}, {hs.H:G}]"));
                }
                sb.Append(":::");
                return sb.ToString();
            }

            case ArticleBlockKind.Reveal:
            {
                var attrs = new List<string>();
                if (!string.IsNullOrWhiteSpace(block.RefKey)) attrs.Add($"ref=\"{block.RefKey}\"");
                Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(block.PayloadJson, out var rev);
                var title = rev?.Title ?? block.CalloutTitle;
                if (!string.IsNullOrWhiteSpace(title)) attrs.Add($"title=\"{EscapeAttr(title)}\"");
                if (rev?.ShowFirst == true) attrs.Add("showFirst=true");

                var sb = new StringBuilder();
                sb.AppendLine(attrs.Count == 0 ? ":::reveal" : $":::reveal{{{string.Join(' ', attrs)}}}");
                foreach (var st in rev?.Steps ?? [])
                {
                    sb.AppendLine($"> {st.Heading}");
                    sb.AppendLine(st.Body);
                }
                sb.Append(":::");
                return sb.ToString();
            }

            default:
                return block.TextContent ?? string.Empty;
        }
    }

    public static string ProjectLinkCardToMarkdown(ArticleLinkCard link)
    {
        var attrs = new List<string> { $"id=\"{EscapeAttr(link.Id)}\"" };
        if (!string.IsNullOrWhiteSpace(link.Title)) attrs.Add($"title=\"{EscapeAttr(link.Title)}\"");
        if (!string.IsNullOrWhiteSpace(link.ImagePath)) attrs.Add($"image=\"{EscapeAttr(link.ImagePath)}\"");

        var sb = new StringBuilder();
        sb.AppendLine($":::link{{{string.Join(' ', attrs)}}}");
        if (!string.IsNullOrWhiteSpace(link.Text))
            sb.AppendLine(link.Text);
        sb.Append(":::");
        return sb.ToString();
    }

    public static string ProjectQuestionToMarkdown(Question question, string fence = ":::")
    {
        var sb = new StringBuilder();
        var refAttr = fence == ":::" && !string.IsNullOrWhiteSpace(question.RefKey) ? $"ref=\"{question.RefKey}\"" : null;

        if (question is McqQuestion mcq)
        {
            var attrs = new List<string>();
            if (refAttr is not null) attrs.Add(refAttr);
            if (mcq.IsMultiCorrect) attrs.Add("multi=true");
            if (mcq.MaxOptions != McqQuestion.DefaultMaxOptions) attrs.Add($"maxOptions={mcq.MaxOptions}");
            if (!string.IsNullOrWhiteSpace(mcq.ImagePath)) attrs.Add($"image=\"{EscapeAttr(mcq.ImagePath)}\"");

            sb.AppendLine(attrs.Count == 0 ? $"{fence}mcq" : $"{fence}mcq{{{string.Join(' ', attrs)}}}");
            if (!string.IsNullOrWhiteSpace(mcq.Text)) sb.AppendLine(mcq.Text);
            foreach (var opt in mcq.Options)
            {
                var mark = opt.IsCorrect ? "x" : " ";
                var imgPart = !string.IsNullOrWhiteSpace(opt.ImagePath)
                    ? $"![]({MarkdownSmartWrapper.FormatMarkdownImageTarget(opt.ImagePath)})"
                    : string.Empty;
                var txtPart = opt.Text ?? string.Empty;
                var lineBody = string.IsNullOrEmpty(imgPart)
                    ? txtPart
                    : (string.IsNullOrEmpty(txtPart) ? imgPart : $"{imgPart} {txtPart}");
                var optLines = lineBody.Replace("\r\n", "\n").Split('\n');
                sb.AppendLine($"- [{mark}] {optLines[0]}");
                for (int k = 1; k < optLines.Length; k++)
                    sb.AppendLine($"  {optLines[k]}");
            }
            AppendExplanationAndLayout(sb, mcq.Explanation, fence == ":::" ? mcq.LayoutOverrides : null, fence);
            sb.Append(fence);
            return sb.ToString();
        }

        if (question is FlashcardQuestion card)
        {
            var attrs = new List<string>();
            if (refAttr is not null) attrs.Add(refAttr);
            attrs.Add($"frontPosition=\"{(card.Front.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage")}\"");
            attrs.Add($"backPosition=\"{(card.Back.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage")}\"");

            sb.AppendLine($"{fence}flashcard{{{string.Join(' ', attrs)}}}");
            sb.AppendLine($"{fence}front");
            if (!string.IsNullOrWhiteSpace(card.Front.ImagePath))
                sb.AppendLine($"![]({MarkdownSmartWrapper.FormatMarkdownImageTarget(card.Front.ImagePath)})");
            if (!string.IsNullOrWhiteSpace(card.Front.Text))
                sb.AppendLine(card.Front.Text);

            sb.AppendLine($"{fence}back");
            if (!string.IsNullOrWhiteSpace(card.Back.ImagePath))
                sb.AppendLine($"![]({MarkdownSmartWrapper.FormatMarkdownImageTarget(card.Back.ImagePath)})");
            if (!string.IsNullOrWhiteSpace(card.Back.Text))
                sb.AppendLine(card.Back.Text);

            if (fence == ":::") AppendLayoutSubSection(sb, card.LayoutOverrides, fence);
            sb.Append(fence);
            return sb.ToString();
        }

        switch (question.Type)
        {
            case QuestionType.FillBlank:
            {
                Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(question.PayloadJson, out var fb);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (fb is not null)
                {
                    attrs.Add($"fuzzy={(fb.Fuzzy ? "true" : "false")}");
                    attrs.Add($"caseSensitive={(fb.CaseSensitive ? "true" : "false")}");
                    if (!string.IsNullOrWhiteSpace(fb.Placeholder)) attrs.Add($"placeholder=\"{EscapeAttr(fb.Placeholder)}\"");
                }
                sb.AppendLine(attrs.Count == 0 ? $"{fence}fill-blank" : $"{fence}fill-blank{{{string.Join(' ', attrs)}}}");
                sb.AppendLine(fb?.Template ?? string.Empty);
                AppendExplanationAndLayout(sb, fb?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.AssertionReason:
            {
                Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(question.PayloadJson, out var ar);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (ar is not null)
                {
                    attrs.Add($"answer=\"{ar.Answer}\"");
                    if (!string.IsNullOrWhiteSpace(ar.Scheme)) attrs.Add($"scheme=\"{ar.Scheme}\"");
                }
                sb.AppendLine($"{fence}assertion-reason{{{string.Join(' ', attrs)}}}");
                sb.AppendLine($"A: {ar?.Assertion ?? string.Empty}");
                sb.AppendLine($"R: {ar?.Reason ?? string.Empty}");
                AppendExplanationAndLayout(sb, ar?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.SpotError:
            {
                Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(question.PayloadJson, out var se);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (se is not null)
                {
                    if (!string.IsNullOrWhiteSpace(se.ImagePath)) attrs.Add($"image=\"{EscapeAttr(se.ImagePath)}\"");
                    if (se.RequireFix) attrs.Add("requireFix=true");
                    if (se.AllowNoError) attrs.Add("allowNoError=true");
                    if (se.NoError) attrs.Add("noError=true");
                }
                sb.AppendLine(attrs.Count == 0 ? $"{fence}spot-error" : $"{fence}spot-error{{{string.Join(' ', attrs)}}}");
                sb.AppendLine(se?.Template ?? string.Empty);
                AppendExplanationAndLayout(sb, se?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.Differentiation:
            {
                Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(question.PayloadJson, out var diff);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                attrs.Add($"mode=\"{diff?.Mode ?? "fill"}\"");
                if (diff is not null)
                {
                    if (!string.IsNullOrWhiteSpace(diff.Title)) attrs.Add($"title=\"{EscapeAttr(diff.Title)}\"");
                    if (diff.Bank) attrs.Add("bank=true");
                    if (!diff.Fuzzy) attrs.Add("fuzzy=false");
                    if (diff.CaseSensitive) attrs.Add("caseSensitive=true");
                    if (diff.AllowNoError) attrs.Add("allowNoError=true");
                    if (!string.IsNullOrWhiteSpace(diff.Placeholder)) attrs.Add($"placeholder=\"{EscapeAttr(diff.Placeholder)}\"");
                }
                sb.AppendLine($"{fence}diff{{{string.Join(' ', attrs)}}}");
                AppendMarkdownTable(sb, diff?.Headers ?? [], diff?.Alignments ?? [], diff?.Rows ?? []);
                AppendExplanationAndLayout(sb, diff?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.Mechanism:
            {
                Phase1PayloadCodec.TryDeserialize<MechanismPayload>(question.PayloadJson, out var mech);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (!string.IsNullOrWhiteSpace(mech?.Title)) attrs.Add($"title=\"{EscapeAttr(mech.Title)}\"");
                sb.AppendLine(attrs.Count == 0 ? $"{fence}mechanism" : $"{fence}mechanism{{{string.Join(' ', attrs)}}}");
                var groups = (mech?.Steps ?? []).Where(s => !s.IsDistractor).GroupBy(s => s.OrderGroup).OrderBy(g => g.Key);
                foreach (var g in groups)
                {
                    var items = g.ToList();
                    if (items.Count == 1)
                    {
                        sb.AppendLine($"{g.Key}. {items[0].Text}");
                    }
                    else
                    {
                        for (int idx = 0; idx < items.Count; idx++)
                        {
                            char letter = (char)('a' + (idx % 26));
                            sb.AppendLine($"{g.Key}{letter}. {items[idx].Text}");
                        }
                    }
                }
                foreach (var d in (mech?.Steps ?? []).Where(s => s.IsDistractor))
                    sb.AppendLine($"! {d.Text}");
                AppendExplanationAndLayout(sb, mech?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.RelabelImage:
            {
                Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(question.PayloadJson, out var rel);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                attrs.Add($"image=\"{EscapeAttr(rel?.ImagePath ?? string.Empty)}\"");
                attrs.Add($"mode=\"{rel?.Mode ?? "bank"}\"");
                attrs.Add($"style=\"{rel?.Style ?? "solid"}\"");
                if (rel is not null)
                {
                    if (!rel.Fuzzy) attrs.Add("fuzzy=false");
                    if (rel.CaseSensitive) attrs.Add("caseSensitive=true");
                }
                sb.AppendLine($"{fence}relabel{{{string.Join(' ', attrs)}}}");
                if (!string.IsNullOrWhiteSpace(rel?.Prompt)) AppendYamlField(sb, "", "prompt", rel.Prompt);
                if (rel?.Distractors is { Count: > 0 })
                {
                    sb.AppendLine("distractors:");
                    foreach (var d in rel.Distractors) sb.AppendLine($"  - {YamlQuote(d)}");
                }
                sb.AppendLine("boxes:");
                foreach (var b in rel?.Boxes ?? [])
                {
                    sb.AppendLine($"  - label: {YamlQuote(b.Label)}");
                    if (b.Aliases is { Count: > 0 })
                        sb.AppendLine($"    aliases: [{string.Join(", ", b.Aliases.Select(YamlQuote))}]");
                    sb.AppendLine(FormattableString.Invariant($"    rect: [{b.X:G}, {b.Y:G}, {b.W:G}, {b.H:G}]"));
                }
                AppendExplanationAndLayout(sb, rel?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.Classify:
            {
                Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(question.PayloadJson, out var cls);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (!string.IsNullOrWhiteSpace(cls?.Title)) attrs.Add($"title=\"{EscapeAttr(cls.Title)}\"");
                sb.AppendLine(attrs.Count == 0 ? $"{fence}classify" : $"{fence}classify{{{string.Join(' ', attrs)}}}");
                foreach (var b in cls?.Buckets ?? [])
                {
                    sb.AppendLine($"[{b.Name}]");
                    foreach (var it in (cls?.Items ?? []).Where(x => x.BucketId == b.BucketId))
                        sb.AppendLine($"- {it.Text}");
                }
                AppendExplanationAndLayout(sb, cls?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.Match:
            {
                Phase1PayloadCodec.TryDeserialize<MatchPayload>(question.PayloadJson, out var mt);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (!string.IsNullOrWhiteSpace(mt?.Title)) attrs.Add($"title=\"{EscapeAttr(mt.Title)}\"");
                sb.AppendLine(attrs.Count == 0 ? $"{fence}match" : $"{fence}match{{{string.Join(' ', attrs)}}}");
                foreach (var p in mt?.Pairs ?? [])
                    sb.AppendLine($"- {p.Left} :: {p.Right}");
                foreach (var d in mt?.Distractors ?? [])
                    sb.AppendLine($"! {d}");
                AppendExplanationAndLayout(sb, mt?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.Numeric:
            {
                Phase1PayloadCodec.TryDeserialize<NumericPayload>(question.PayloadJson, out var nm);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                attrs.Add(FormattableString.Invariant($"answer={nm?.Answer ?? 0}"));
                if (nm is not null)
                {
                    if (nm.Tolerance > 0)
                    {
                        var tolStr = string.Equals(nm.ToleranceMode, "percent", StringComparison.OrdinalIgnoreCase)
                            ? FormattableString.Invariant($"{nm.Tolerance}%")
                            : FormattableString.Invariant($"{nm.Tolerance}");
                        attrs.Add($"tolerance=\"{tolStr}\"");
                    }
                    if (!string.IsNullOrWhiteSpace(nm.ToleranceMode)) attrs.Add($"toleranceMode=\"{nm.ToleranceMode}\"");
                    if (!string.IsNullOrWhiteSpace(nm.Unit)) attrs.Add($"unit=\"{EscapeAttr(nm.Unit)}\"");
                    if (!string.IsNullOrWhiteSpace(nm.Placeholder)) attrs.Add($"placeholder=\"{EscapeAttr(nm.Placeholder)}\"");
                    if (!string.IsNullOrWhiteSpace(nm.ImagePath)) attrs.Add($"image=\"{EscapeAttr(nm.ImagePath)}\"");
                }
                sb.AppendLine($"{fence}numeric{{{string.Join(' ', attrs)}}}");
                sb.AppendLine(nm?.Prompt ?? string.Empty);
                AppendExplanationAndLayout(sb, nm?.Explanation, fence == ":::" ? question.LayoutOverrides : null, fence);
                sb.Append(fence);
                return sb.ToString();
            }

            case QuestionType.CaseGroup:
            {
                Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(question.PayloadJson, out var cs);
                var attrs = new List<string>();
                if (refAttr is not null) attrs.Add(refAttr);
                if (!string.IsNullOrWhiteSpace(cs?.Title)) attrs.Add($"title=\"{EscapeAttr(cs.Title)}\"");
                if (!string.IsNullOrWhiteSpace(cs?.ImagePath)) attrs.Add($"image=\"{EscapeAttr(cs.ImagePath)}\"");
                if (!string.IsNullOrWhiteSpace(cs?.Caption)) attrs.Add($"caption=\"{EscapeAttr(cs.Caption)}\"");
                sb.AppendLine(attrs.Count == 0 ? ":::case" : $":::case{{{string.Join(' ', attrs)}}}");
                if (!string.IsNullOrWhiteSpace(cs?.Stem)) sb.AppendLine(cs.Stem);
                foreach (var sub in cs?.Questions ?? [])
                {
                    if (sub is null) continue;
                    if (Quiz.TryParseQuestionType(sub.Type, out var subType) && subType != QuestionType.CaseGroup)
                    {
                        var domainSub = sub.RehydrateToDomainQuestion(subType, $"{question.Id}_sub{sub.Index}", question.ContainerId);
                        sb.AppendLine(ProjectQuestionToMarkdown(domainSub, fence: "::::"));
                    }
                }
                AppendExplanationAndLayout(sb, cs?.Explanation, question.LayoutOverrides, ":::");
                sb.Append(":::");
                return sb.ToString();
            }

            default:
                return string.Empty;
        }
    }

    private static void AppendMarkdownTable(
        StringBuilder sb,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> alignments,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (headers.Count == 0) return;
        sb.AppendLine("| " + string.Join(" | ", headers.Select(EscapeTableCell)) + " |");
        var seps = new string[headers.Count];
        for (int i = 0; i < headers.Count; i++)
        {
            var align = i < alignments.Count ? alignments[i]?.ToLowerInvariant() : null;
            seps[i] = align switch
            {
                "center" => ":---:",
                "right" => "---:",
                _ => "---"
            };
        }
        sb.AppendLine("| " + string.Join(" | ", seps) + " |");
        foreach (var row in rows)
            sb.AppendLine("| " + string.Join(" | ", row.Select(EscapeTableCell)) + " |");
    }

    private static string EscapeTableCell(string? rawCell)
    {
        if (string.IsNullOrEmpty(rawCell)) return string.Empty;
        var text = rawCell.Replace("\r\n", "<br>", StringComparison.Ordinal)
                          .Replace("\r", "<br>", StringComparison.Ordinal)
                          .Replace("\n", "<br>", StringComparison.Ordinal);

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (Phase1PayloadRules.TrySkipInlineCodeSpan(text, i, out int afterCode))
            {
                sb.Append(text, i, afterCode - i);
                i = afterCode;
                continue;
            }
            if (Phase1PayloadRules.TrySkipMathSpan(text, i, out int afterMath))
            {
                sb.Append(text, i, afterMath - i);
                i = afterMath;
                continue;
            }
            if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
            {
                int close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    sb.Append(text, i, (close + 2) - i);
                    i = close + 2;
                    continue;
                }
            }
            if (text[i] == '|' && !Phase1PayloadRules.IsEscaped(text, i))
            {
                sb.Append(@"\|");
                i++;
                continue;
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private static void AppendExplanationAndLayout(
        StringBuilder sb,
        string? explanation,
        IReadOnlyDictionary<string, string>? overrides,
        string fence)
    {
        if (!string.IsNullOrWhiteSpace(explanation))
        {
            sb.AppendLine($"{fence}explanation");
            sb.AppendLine(explanation.Trim());
        }
        AppendLayoutSubSection(sb, overrides, fence);
    }

    private static void AppendLayoutSubSection(StringBuilder sb, IReadOnlyDictionary<string, string>? overrides, string fence)
    {
        if (overrides is not { Count: > 0 }) return;
        sb.AppendLine($"{fence}layout");
        foreach (var kv in overrides)
            sb.AppendLine($"{kv.Key}: {YamlQuote(kv.Value)}");
    }

    private static void AppendLayoutFrontmatter(StringBuilder sb, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is not { Count: > 0 }) return;
        sb.AppendLine("layoutOverrides:");
        foreach (var kv in overrides)
            sb.AppendLine($"  {kv.Key}: {YamlQuote(kv.Value)}");
    }

    private static void AppendYamlField(StringBuilder sb, string indent, string key, string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.Contains('\n'))
        {
            sb.AppendLine($"{indent}{key}: {YamlQuote(normalized)}");
            return;
        }

        sb.AppendLine($"{indent}{key}: |");
        foreach (var line in normalized.Split('\n'))
            sb.AppendLine($"{indent}  {line}");
    }

    private static string YamlQuote(string value) =>
        "\"" + value.Replace("\r\n", " ", StringComparison.Ordinal)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string EscapeAttr(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\r', ' ')
             .Replace('\n', ' ')
             .Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Slugify(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var chars = raw.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        collapsed = collapsed.Trim('_');
        return string.IsNullOrWhiteSpace(collapsed) ? fallback : (collapsed.Length > 28 ? collapsed[..28] : collapsed);
    }
}
