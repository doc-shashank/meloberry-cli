using System.Text.Json.Nodes;
using Meloberry.Domain.Payloads;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record InspectIdMapDto(
    string Id,
    string? Path = null,
    string? Ref = null,
    IReadOnlyList<InspectIdMapDto>? Components = null,
    IReadOnlyList<InspectIdMapDto>? Blocks = null,
    IReadOnlyList<InspectIdMapDto>? Questions = null);

internal static class WorkspaceSpecProjector
{
    public static (JsonObject Spec, InspectIdMapDto IdMap) ProjectSession(StudySession session)
    {
        var spec = new JsonObject
        {
            ["kind"] = "session"
        };
        AddIfNotEmpty(spec, "name", session.Name);
        AddIfNotEmpty(spec, "description", session.Description);

        var compsArr = new JsonArray();
        var compMaps = new List<InspectIdMapDto>(session.Components.Count);
        foreach (var comp in session.Components)
        {
            var (compSpec, compMap) = ProjectComponent(comp);
            compsArr.Add(compSpec);
            compMaps.Add(compMap);
        }
        spec["components"] = compsArr;

        var idMap = new InspectIdMapDto(session.Id, Components: compMaps);
        return (spec, idMap);
    }

    public static (JsonObject Spec, InspectIdMapDto IdMap) ProjectQuiz(Quiz quiz)
    {
        var spec = new JsonObject
        {
            ["kind"] = "quiz",
            ["quizType"] = quiz.Type.ToString().ToLowerInvariant()
        };
        AddIfNotEmpty(spec, "name", quiz.Name);
        AddIfNotEmpty(spec, "description", quiz.Description);

        var qArr = new JsonArray();
        var qMaps = new List<InspectIdMapDto>(quiz.Questions.Count);
        foreach (var q in quiz.Questions)
        {
            var path = $"q{q.Index}";
            qArr.Add(ProjectQuestion(q));
            qMaps.Add(new InspectIdMapDto(q.Id, Path: path, Ref: q.RefKey));
        }
        spec["questions"] = qArr;

        var idMap = new InspectIdMapDto(quiz.Id, Questions: qMaps);
        return (spec, idMap);
    }

    public static (JsonObject Spec, InspectIdMapDto IdMap) ProjectComponent(SessionComponent component)
    {
        string path = $"c{component.Index}";
        string kind = component.Kind == ComponentKind.Article ? "article" : "quiz";
        var spec = new JsonObject
        {
            ["kind"] = kind
        };
        AddIfNotEmpty(spec, "ref", component.RefKey);
        AddIfNotEmpty(spec, "title", component.Title);

        if (component.Kind == ComponentKind.Quiz && component.Quiz is not null)
            spec["quizType"] = component.Quiz.Type.ToString().ToLowerInvariant();

        AddLayoutOverrides(spec, component.LayoutOverrides);

        List<InspectIdMapDto>? blockMaps = null;
        List<InspectIdMapDto>? questionMaps = null;

        if (component.Kind == ComponentKind.Article)
        {
            if (component.LinkCards.Count > 0)
            {
                var linksArr = new JsonArray();
                foreach (var card in component.LinkCards)
                {
                    var cardObj = new JsonObject { ["id"] = card.Id };
                    AddIfNotEmpty(cardObj, "title", card.Title);
                    AddIfNotEmpty(cardObj, "text", card.Text);
                    AddIfNotEmpty(cardObj, "imagePath", card.ImagePath);
                    linksArr.Add(cardObj);
                }
                spec["links"] = linksArr;
            }

            var blocksArr = new JsonArray();
            blockMaps = new List<InspectIdMapDto>(component.ArticleBlocks.Count);
            foreach (var block in component.ArticleBlocks)
            {
                var bPath = $"{path}.b{block.Index}";
                blocksArr.Add(ProjectBlock(block));
                blockMaps.Add(new InspectIdMapDto(block.Id, Path: bPath, Ref: block.RefKey));
            }
            spec["blocks"] = blocksArr;
        }
        else if (component.Quiz is not null)
        {
            var questionsArr = new JsonArray();
            questionMaps = new List<InspectIdMapDto>(component.Quiz.Questions.Count);
            foreach (var q in component.Quiz.Questions)
            {
                var qPath = $"{path}.q{q.Index}";
                questionsArr.Add(ProjectQuestion(q));
                questionMaps.Add(new InspectIdMapDto(q.Id, Path: qPath, Ref: q.RefKey));
            }
            spec["questions"] = questionsArr;
        }

        var idMap = new InspectIdMapDto(
            component.Id,
            Path: path,
            Ref: component.RefKey,
            Blocks: blockMaps,
            Questions: questionMaps);
        return (spec, idMap);
    }

    public static (JsonObject Spec, InspectIdMapDto IdMap) ProjectArticleBlock(ArticleBlock block, string path) =>
        (ProjectBlock(block), new InspectIdMapDto(block.Id, Path: path, Ref: block.RefKey));

    public static (JsonObject Spec, InspectIdMapDto IdMap) ProjectQuestionWithIdMap(Question question, string path) =>
        (ProjectQuestion(question), new InspectIdMapDto(question.Id, Path: path, Ref: question.RefKey));

    private static JsonObject ProjectBlock(ArticleBlock block)
    {
        var obj = new JsonObject();
        switch (block.Kind)
        {
            case ArticleBlockKind.Text:
                obj["kind"] = "text";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                obj["text"] = block.TextContent ?? string.Empty;
                return obj;

            case ArticleBlockKind.Image:
                obj["kind"] = "image";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                obj["imagePath"] = block.ImagePath ?? string.Empty;
                AddIfNotEmpty(obj, "caption", block.Caption);
                AddIfNotEmpty(obj, "alt", block.Alt);
                return obj;

            case ArticleBlockKind.Callout:
                obj["kind"] = "callout";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                obj["tone"] = block.CalloutTone.HasValue
                    ? ArticleBlock.CalloutToneToString(block.CalloutTone.Value)
                    : "pearl";
                AddIfNotEmpty(obj, "title", block.CalloutTitle);
                obj["text"] = block.TextContent ?? string.Empty;
                return obj;

            case ArticleBlockKind.Table:
                obj["kind"] = "table";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                if (Phase1PayloadCodec.TryDeserialize<TableBlockPayload>(block.PayloadJson, out var tbl) && tbl is not null)
                {
                    AddIfNotEmpty(obj, "title", tbl.Title ?? block.CalloutTitle);
                    AddIfNotEmpty(obj, "caption", tbl.Caption);
                    obj["stickyFirstCol"] = tbl.StickyFirstCol;
                    if (tbl.HideableCols is { Count: > 0 })
                        obj["hideableCols"] = ToIntArray(tbl.HideableCols);
                    obj["headers"] = ToStringArray(tbl.Headers ?? []);
                    if (tbl.Alignments is { Count: > 0 })
                        obj["alignments"] = ToStringArray(tbl.Alignments);
                    obj["rows"] = ToStringMatrix(tbl.Rows ?? []);
                    AddIfNotEmpty(obj, "footnote", tbl.Footnote);
                }
                else
                {
                    AddIfNotEmpty(obj, "title", block.CalloutTitle);
                    obj["headers"] = new JsonArray();
                    obj["rows"] = new JsonArray();
                }
                return obj;

            case ArticleBlockKind.DiagramExplore:
                obj["kind"] = "diagram-explore";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                if (Phase1PayloadCodec.TryDeserialize<DiagramExplorePayload>(block.PayloadJson, out var diag) && diag is not null)
                {
                    var imgPath = !string.IsNullOrWhiteSpace(block.ImagePath) ? block.ImagePath : diag.ImagePath;
                    obj["imagePath"] = imgPath ?? string.Empty;
                    AddIfNotEmpty(obj, "title", diag.Title ?? block.CalloutTitle);
                    AddIfNotEmpty(obj, "caption", diag.Caption ?? block.Caption);
                    AddIfNotEmpty(obj, "alt", block.Alt);
                    var hotspotsArr = new JsonArray();
                    foreach (var hs in diag.Hotspots ?? [])
                    {
                        hotspotsArr.Add(new JsonObject
                        {
                            ["label"] = hs.Label,
                            ["note"] = hs.Note,
                            ["rect"] = new JsonArray(hs.X, hs.Y, hs.W, hs.H)
                        });
                    }
                    obj["hotspots"] = hotspotsArr;
                }
                else
                {
                    obj["imagePath"] = block.ImagePath ?? string.Empty;
                    AddIfNotEmpty(obj, "title", block.CalloutTitle);
                    AddIfNotEmpty(obj, "caption", block.Caption);
                    AddIfNotEmpty(obj, "alt", block.Alt);
                    obj["hotspots"] = new JsonArray();
                }
                return obj;

            case ArticleBlockKind.Reveal:
                obj["kind"] = "reveal";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                if (Phase1PayloadCodec.TryDeserialize<RevealBlockPayload>(block.PayloadJson, out var rev) && rev is not null)
                {
                    AddIfNotEmpty(obj, "title", rev.Title ?? block.CalloutTitle);
                    obj["showFirst"] = rev.ShowFirst;
                    var stepsArr = new JsonArray();
                    foreach (var st in rev.Steps ?? [])
                    {
                        stepsArr.Add(new JsonObject
                        {
                            ["heading"] = st.Heading,
                            ["body"] = st.Body
                        });
                    }
                    obj["steps"] = stepsArr;
                }
                else
                {
                    AddIfNotEmpty(obj, "title", block.CalloutTitle);
                    obj["showFirst"] = false;
                    obj["steps"] = new JsonArray();
                }
                return obj;

            default:
                obj["kind"] = "text";
                AddIfNotEmpty(obj, "ref", block.RefKey);
                obj["text"] = block.TextContent ?? string.Empty;
                return obj;
        }
    }

    private static JsonObject ProjectQuestion(Question question, bool isCaseSubQuestion = false)
    {
        var obj = new JsonObject
        {
            ["type"] = Question.ToWireString(question.Type)
        };
        if (!isCaseSubQuestion)
        {
            AddIfNotEmpty(obj, "ref", question.RefKey);
            AddLayoutOverrides(obj, question.LayoutOverrides);
        }

        if (question is McqQuestion mcq)
        {
            obj["text"] = mcq.Text;
            AddIfNotEmpty(obj, "imagePath", mcq.ImagePath);
            AddIfNotEmpty(obj, "explanation", mcq.Explanation);
            obj["maxOptions"] = mcq.MaxOptions;
            obj["isMultiCorrect"] = mcq.IsMultiCorrect;
            var optsArr = new JsonArray();
            foreach (var o in mcq.Options)
            {
                var optObj = new JsonObject
                {
                    ["index"] = o.Index
                };
                AddIfNotEmpty(optObj, "text", o.Text);
                AddIfNotEmpty(optObj, "imagePath", o.ImagePath);
                optObj["isCorrect"] = o.IsCorrect;
                optsArr.Add(optObj);
            }
            obj["options"] = optsArr;
            return obj;
        }

        if (question is FlashcardQuestion card)
        {
            obj["front"] = ProjectFlashcardSide(card.Front);
            obj["back"] = ProjectFlashcardSide(card.Back);
            return obj;
        }

        switch (question.Type)
        {
            case QuestionType.FillBlank:
                if (Phase1PayloadCodec.TryDeserialize<FillBlankPayload>(question.PayloadJson, out var fb) && fb is not null)
                {
                    obj["text"] = fb.Template;
                    obj["fuzzy"] = fb.Fuzzy;
                    obj["caseSensitive"] = fb.CaseSensitive;
                    AddIfNotEmpty(obj, "placeholder", fb.Placeholder);
                    AddIfNotEmpty(obj, "explanation", fb.Explanation);
                }
                break;

            case QuestionType.AssertionReason:
                if (Phase1PayloadCodec.TryDeserialize<AssertionReasonPayload>(question.PayloadJson, out var ar) && ar is not null)
                {
                    obj["assertion"] = ar.Assertion;
                    obj["reason"] = ar.Reason;
                    obj["answer"] = ar.Answer;
                    AddIfNotEmpty(obj, "scheme", ar.Scheme);
                    AddIfNotEmpty(obj, "explanation", ar.Explanation);
                }
                break;

            case QuestionType.SpotError:
                if (Phase1PayloadCodec.TryDeserialize<SpotErrorPayload>(question.PayloadJson, out var se) && se is not null)
                {
                    obj["text"] = se.Template;
                    AddIfNotEmpty(obj, "imagePath", se.ImagePath);
                    obj["requireFix"] = se.RequireFix;
                    obj["allowNoError"] = se.AllowNoError;
                    obj["noError"] = se.NoError;
                    AddIfNotEmpty(obj, "explanation", se.Explanation);
                }
                break;

            case QuestionType.Differentiation:
                if (Phase1PayloadCodec.TryDeserialize<DifferentiationPayload>(question.PayloadJson, out var diff) && diff is not null)
                {
                    obj["mode"] = diff.Mode;
                    AddIfNotEmpty(obj, "title", diff.Title);
                    obj["bank"] = diff.Bank;
                    obj["fuzzy"] = diff.Fuzzy;
                    obj["caseSensitive"] = diff.CaseSensitive;
                    obj["allowNoError"] = diff.AllowNoError;
                    AddIfNotEmpty(obj, "placeholder", diff.Placeholder);
                    obj["headers"] = ToStringArray(diff.Headers ?? []);
                    if (diff.Alignments is { Count: > 0 })
                        obj["alignments"] = ToStringArray(diff.Alignments);
                    obj["rows"] = ToStringMatrix(diff.Rows ?? []);
                    AddIfNotEmpty(obj, "explanation", diff.Explanation);
                }
                break;

            case QuestionType.Mechanism:
                if (Phase1PayloadCodec.TryDeserialize<MechanismPayload>(question.PayloadJson, out var mech) && mech is not null)
                {
                    AddIfNotEmpty(obj, "title", mech.Title);
                    var stepsArr = new JsonArray();
                    foreach (var s in mech.Steps ?? [])
                    {
                        stepsArr.Add(new JsonObject
                        {
                            ["stepId"] = s.StepId,
                            ["orderGroup"] = s.OrderGroup,
                            ["text"] = s.Text,
                            ["isDistractor"] = s.IsDistractor
                        });
                    }
                    obj["steps"] = stepsArr;
                    AddIfNotEmpty(obj, "explanation", mech.Explanation);
                }
                break;

            case QuestionType.RelabelImage:
                if (Phase1PayloadCodec.TryDeserialize<RelabelImagePayload>(question.PayloadJson, out var rel) && rel is not null)
                {
                    obj["imagePath"] = rel.ImagePath ?? string.Empty;
                    AddIfNotEmpty(obj, "prompt", rel.Prompt);
                    obj["mode"] = rel.Mode;
                    obj["style"] = rel.Style;
                    var boxesArr = new JsonArray();
                    foreach (var b in rel.Boxes ?? [])
                    {
                        var bObj = new JsonObject
                        {
                            ["label"] = b.Label
                        };
                        if (b.Aliases is { Count: > 0 })
                            bObj["aliases"] = ToStringArray(b.Aliases);
                        bObj["rect"] = new JsonArray(b.X, b.Y, b.W, b.H);
                        boxesArr.Add(bObj);
                    }
                    obj["boxes"] = boxesArr;
                    if (rel.Distractors is { Count: > 0 })
                        obj["distractors"] = ToStringArray(rel.Distractors);
                    obj["fuzzy"] = rel.Fuzzy;
                    obj["caseSensitive"] = rel.CaseSensitive;
                    AddIfNotEmpty(obj, "explanation", rel.Explanation);
                }
                break;

            case QuestionType.Classify:
                if (Phase1PayloadCodec.TryDeserialize<ClassifyPayload>(question.PayloadJson, out var cls) && cls is not null)
                {
                    AddIfNotEmpty(obj, "title", cls.Title);
                    var bucketsArr = new JsonArray();
                    foreach (var b in cls.Buckets ?? [])
                    {
                        bucketsArr.Add(new JsonObject
                        {
                            ["bucketId"] = b.BucketId,
                            ["name"] = b.Name
                        });
                    }
                    obj["buckets"] = bucketsArr;
                    var itemsArr = new JsonArray();
                    foreach (var it in cls.Items ?? [])
                    {
                        itemsArr.Add(new JsonObject
                        {
                            ["bucketId"] = it.BucketId,
                            ["text"] = it.Text
                        });
                    }
                    obj["items"] = itemsArr;
                    AddIfNotEmpty(obj, "explanation", cls.Explanation);
                }
                break;

            case QuestionType.Match:
                if (Phase1PayloadCodec.TryDeserialize<MatchPayload>(question.PayloadJson, out var mt) && mt is not null)
                {
                    AddIfNotEmpty(obj, "title", mt.Title);
                    var pairsArr = new JsonArray();
                    foreach (var p in mt.Pairs ?? [])
                    {
                        pairsArr.Add(new JsonObject
                        {
                            ["left"] = p.Left,
                            ["right"] = p.Right
                        });
                    }
                    obj["pairs"] = pairsArr;
                    if (mt.Distractors is { Count: > 0 })
                        obj["distractors"] = ToStringArray(mt.Distractors);
                    AddIfNotEmpty(obj, "explanation", mt.Explanation);
                }
                break;

            case QuestionType.Numeric:
                if (Phase1PayloadCodec.TryDeserialize<NumericPayload>(question.PayloadJson, out var nm) && nm is not null)
                {
                    obj["prompt"] = nm.Prompt;
                    obj["answer"] = nm.Answer;
                    obj["tolerance"] = nm.Tolerance;
                    obj["toleranceMode"] = nm.ToleranceMode;
                    AddIfNotEmpty(obj, "unit", nm.Unit);
                    AddIfNotEmpty(obj, "placeholder", nm.Placeholder);
                    AddIfNotEmpty(obj, "imagePath", nm.ImagePath);
                    AddIfNotEmpty(obj, "explanation", nm.Explanation);
                }
                break;

            case QuestionType.CaseGroup:
                if (Phase1PayloadCodec.TryDeserialize<CaseGroupPayload>(question.PayloadJson, out var cs) && cs is not null)
                {
                    AddIfNotEmpty(obj, "title", cs.Title);
                    obj["stem"] = cs.Stem;
                    AddIfNotEmpty(obj, "imagePath", cs.ImagePath);
                    AddIfNotEmpty(obj, "caption", cs.Caption);
                    var subArr = new JsonArray();
                    foreach (var sub in cs.Questions ?? [])
                    {
                        if (sub is null) continue;
                        if (Quiz.TryParseQuestionType(sub.Type, out var subType) && subType != QuestionType.CaseGroup)
                        {
                            var domainSub = sub.RehydrateToDomainQuestion(subType, $"{question.Id}_sub{sub.Index}", question.ContainerId);
                            subArr.Add(ProjectQuestion(domainSub, isCaseSubQuestion: true));
                        }
                    }
                    obj["questions"] = subArr;
                    AddIfNotEmpty(obj, "explanation", cs.Explanation);
                }
                break;
        }

        return obj;
    }

    private static JsonObject ProjectFlashcardSide(FlashcardSide side)
    {
        var obj = new JsonObject();
        AddIfNotEmpty(obj, "text", side.Text);
        AddIfNotEmpty(obj, "imagePath", side.ImagePath);
        obj["position"] = side.Position == MediaPosition.TextAboveImage ? "aboveImage" : "belowImage";
        return obj;
    }

    private static void AddLayoutOverrides(JsonObject obj, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0) return;
        var ovObj = new JsonObject();
        foreach (var kv in overrides)
            ovObj[kv.Key] = kv.Value;
        obj["layoutOverrides"] = ovObj;
    }

    private static void AddIfNotEmpty(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            obj[key] = value;
    }

    private static JsonArray ToStringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static JsonArray ToIntArray(IEnumerable<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static JsonArray ToStringMatrix(IEnumerable<IReadOnlyList<string>> rows)
    {
        var arr = new JsonArray();
        foreach (var row in rows) arr.Add(ToStringArray(row));
        return arr;
    }
}
