using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Json;

internal static class JsonNodeReaders
{
    public static bool TryGetProp(JsonObject obj, string name, out JsonNode? value)
    {
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public static bool TryReadNullableString(JsonObject obj, string key, out string? value, out Error? err)
    {
        value = null;
        err = null;
        if (!TryGetProp(obj, key, out var node))
            return false;
        if (node is null)
            return true;
        if (node.GetValueKind() != JsonValueKind.String)
        {
            err = new Error("Patch.InvalidValue", $"'{key}' must be a string or null.", ErrorType.Validation, Path: key);
            return false;
        }
        var s = node.GetValue<string>();
        value = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return true;
    }

    public static bool TryReadRequiredString(JsonObject obj, string key, out string value, out Error? err, bool trim = true)
    {
        value = string.Empty;
        err = null;
        if (!TryGetProp(obj, key, out var node))
            return false;
        if (node is null || node.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetValue<string>()))
        {
            err = new Error("Patch.InvalidValue", $"'{key}' must be a non-empty string.", ErrorType.Validation, Path: key);
            return false;
        }
        value = trim ? node.GetValue<string>().Trim() : node.GetValue<string>();
        return true;
    }

    public static bool TryReadBool(JsonObject obj, string key, out bool value, out Error? err)
    {
        value = false;
        err = null;
        if (!TryGetProp(obj, key, out var node))
            return false;
        if (node is null || node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            err = new Error("Patch.InvalidValue", $"'{key}' must be a boolean.", ErrorType.Validation, Path: key);
            return false;
        }
        value = node.GetValue<bool>();
        return true;
    }

    public static bool TryReadDouble(JsonObject obj, string key, out double value, out Error? err)
    {
        value = 0;
        err = null;
        if (!TryGetProp(obj, key, out var node))
            return false;
        if (node is null || node.GetValueKind() != JsonValueKind.Number || !double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            err = new Error("Patch.InvalidValue", $"'{key}' must be a finite number.", ErrorType.Validation, Path: key);
            return false;
        }
        return true;
    }

    public static bool TryReadStringArray(
        JsonObject obj,
        string key,
        bool allowNull,
        bool allowEmpty,
        out List<string>? values,
        out Error? err)
    {
        values = null;
        err = null;
        if (!TryGetProp(obj, key, out var node))
            return false;
        if (node is null)
        {
            if (allowNull) return true;
            err = new Error("Patch.InvalidValue", $"'{key}' must be an array of strings.", ErrorType.Validation, Path: key);
            return false;
        }
        if (node is not JsonArray arr)
        {
            err = new Error("Patch.InvalidValue", $"'{key}' must be an array of strings.", ErrorType.Validation, Path: key);
            return false;
        }
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is null || item.GetValueKind() != JsonValueKind.String)
            {
                err = new Error("Patch.InvalidValue", $"'{key}' entries must be strings.", ErrorType.Validation, Path: key);
                return false;
            }
            list.Add(item.GetValue<string>().Trim());
        }
        if (!allowEmpty && list.Count == 0)
        {
            err = new Error("Patch.InvalidValue", $"'{key}' cannot be empty.", ErrorType.Validation, Path: key);
            return false;
        }
        values = list;
        return true;
    }

    public static Result<(double X, double Y, double W, double H)> ReadNormalizedRect(JsonObject obj, string path)
    {
        if (TryGetProp(obj, "rect", out var rectNode) && rectNode is not null)
        {
            if (rectNode is not JsonArray arr || arr.Count != 4)
                return new Error("Patch.InvalidValue", $"'{path}.rect' must be a 4-number array [x, y, w, h].", ErrorType.Validation, Path: path);
            var nums = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (arr[i] is null || arr[i]!.GetValueKind() != JsonValueKind.Number || !double.TryParse(arr[i]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i]))
                    return new Error("Patch.InvalidValue", $"'{path}.rect' entries must be numbers.", ErrorType.Validation, Path: path);
            }
            return (nums[0], nums[1], nums[2], nums[3]);
        }

        if (!TryReadDouble(obj, "x", out var x, out var ex))
            return ex ?? new Error("Patch.InvalidValue", $"'{path}' must specify rect:[x,y,w,h] or x,y,w,h.", ErrorType.Validation, Path: path);
        if (!TryReadDouble(obj, "y", out var y, out var ey))
            return ey ?? new Error("Patch.InvalidValue", $"'{path}' must specify rect:[x,y,w,h] or x,y,w,h.", ErrorType.Validation, Path: path);
        if (!TryReadDouble(obj, "w", out var w, out var ew))
            return ew ?? new Error("Patch.InvalidValue", $"'{path}' must specify rect:[x,y,w,h] or x,y,w,h.", ErrorType.Validation, Path: path);
        if (!TryReadDouble(obj, "h", out var h, out var eh))
            return eh ?? new Error("Patch.InvalidValue", $"'{path}' must specify rect:[x,y,w,h] or x,y,w,h.", ErrorType.Validation, Path: path);

        return (x, y, w, h);
    }
}
