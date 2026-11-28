using Meloberry.Domain.Errors;

namespace Meloberry.Domain.Common;

public enum ReorderMode { Exchange, Shift }

public interface IIndexedEntity
{
    string Id { get; }
    int Index { get; }
    void SetIndexInternal(int index);
}

/// <summary>
/// Shared 1-based indexing and reordering invariant logic used by Session Components,
/// Quiz Questions, and Article Blocks.
/// </summary>
internal static class IndexedListReorderer
{
    public static Result<int> InsertAt<T>(List<T> items, T newItem, int? targetIndex) where T : IIndexedEntity
    {
        int insertIndex = targetIndex ?? (items.Count + 1);
        if (insertIndex < 1 || insertIndex > items.Count + 1)
            return IndexErrors.OutOfIndexBounds(insertIndex, 1, items.Count + 1);

        foreach (var existing in items)
            if (existing.Index >= insertIndex) existing.SetIndexInternal(existing.Index + 1);

        newItem.SetIndexInternal(insertIndex);
        items.Add(newItem);
        SortInPlace(items);
        return insertIndex;
    }

    public static Result RemoveAndShiftUp<T>(List<T> items, string id, Error? notFoundError = null) where T : IIndexedEntity
    {
        var target = items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (target is null)
            return notFoundError ?? new Error("Index.ItemNotFound", $"Item '{id}' was not found.", ErrorType.NotFound);

        int removedIndex = target.Index;
        items.Remove(target);
        foreach (var remaining in items)
            if (remaining.Index > removedIndex) remaining.SetIndexInternal(remaining.Index - 1);

        SortInPlace(items);
        return Result.Success();
    }

    public static Result Reorder<T>(List<T> items, string id, int targetIndex, ReorderMode mode, Error? notFoundError = null) where T : IIndexedEntity
    {
        var source = items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (source is null)
            return notFoundError ?? new Error("Index.ItemNotFound", $"Item '{id}' was not found.", ErrorType.NotFound);

        if (targetIndex < 1 || targetIndex > items.Count)
            return IndexErrors.OutOfIndexBounds(targetIndex, 1, items.Count);

        int originalIndex = source.Index;
        if (originalIndex == targetIndex) return Result.Success();

        if (mode == ReorderMode.Exchange)
        {
            items.FirstOrDefault(x => x.Index == targetIndex)?.SetIndexInternal(originalIndex);
        }
        else if (originalIndex < targetIndex)
        {
            foreach (var item in items)
                if (item.Index > originalIndex && item.Index <= targetIndex) item.SetIndexInternal(item.Index - 1);
        }
        else
        {
            foreach (var item in items)
                if (item.Index >= targetIndex && item.Index < originalIndex) item.SetIndexInternal(item.Index + 1);
        }

        source.SetIndexInternal(targetIndex);
        SortInPlace(items);
        return Result.Success();
    }

    public static Result ApplyExactOrder<T>(List<T> items, IReadOnlyList<string> orderedIds, string entityLabel)
        where T : IIndexedEntity
    {
        if (orderedIds.Count != items.Count)
        {
            return new Error(
                "Reorder.CountMismatch",
                $"Expected {items.Count} {entityLabel} ID(s) in order list, received {orderedIds.Count}.",
                ErrorType.Validation);
        }

        var byId = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reordered = new List<T>(items.Count);

        for (int i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            if (!seen.Add(id))
                return new Error("Reorder.DuplicateId", $"Duplicate ID '{id}' in order list.", ErrorType.Validation);
            if (!byId.TryGetValue(id, out var item))
                return new Error("Reorder.UnknownId", $"Unknown {entityLabel} ID '{id}' in order list.", ErrorType.NotFound);

            item.SetIndexInternal(i + 1);
            reordered.Add(item);
        }

        items.Clear();
        items.AddRange(reordered);
        return Result.Success();
    }

    public static void SortInPlace<T>(List<T> items) where T : IIndexedEntity =>
        items.Sort((a, b) => a.Index.CompareTo(b.Index));
}
