using Meloberry.Domain.Common;
using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Questions;

public sealed class PayloadQuestion : Question
{
    private PayloadQuestion(
        string id,
        string containerId,
        int index,
        QuestionType type,
        string payloadJson,
        Dictionary<string, string>? layoutOverrides,
        string? refKey = null)
        : base(id, containerId, index, type, layoutOverrides, refKey, payloadJson)
    {
    }

    public static Result<PayloadQuestion> Create(
        string id,
        string containerId,
        int index,
        QuestionType type,
        string payloadJson,
        Dictionary<string, string>? layoutOverrides = null,
        string? refKey = null)
    {
        if (type is QuestionType.Mcq or QuestionType.Flashcard)
            return QuestionErrors.InvalidType(type.ToString());
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new Error("Question.EmptyPayload", $"Question '{id}' ({ToWireString(type)}) requires non-empty payload JSON.", ErrorType.Validation);
        return new PayloadQuestion(id, containerId, index, type, payloadJson, layoutOverrides, refKey);
    }

    public static PayloadQuestion Rehydrate(
        string id,
        string containerId,
        int index,
        QuestionType type,
        string payloadJson,
        Dictionary<string, string>? layoutOverrides = null,
        string? refKey = null) =>
        new(id, containerId, index, type, string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson, layoutOverrides, refKey);

    public Result SetPayloadJson(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new Error("Question.EmptyPayload", $"Question '{Id}' payload JSON cannot be empty.", ErrorType.Validation);
        PayloadJson = payloadJson.Trim();
        return Result.Success();
    }
}
