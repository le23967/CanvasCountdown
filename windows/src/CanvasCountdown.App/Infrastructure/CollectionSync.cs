using System.Collections.ObjectModel;

namespace CanvasCountdown.App.Infrastructure;

/// <summary>
/// Brings an on-screen collection in line with a new list, item by item.
/// </summary>
/// <remarks>
/// The view model hands out whole new lists, the way the Swift one did. Giving
/// a ListView a new list resets it — scroll position, focus and selection all
/// go back to the top — so ticking one assignment complete would throw the
/// reader to the start of a long list. Updating in place keeps them where they
/// were.
/// </remarks>
internal static class CollectionSync
{
    public static void Apply<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, TKey> key)
        where TKey : notnull
    {
        var wanted = new HashSet<TKey>(source.Select(key));
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(key(target[index])))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            var itemKey = key(item);
            if (index < target.Count && EqualityComparer<TKey>.Default.Equals(key(target[index]), itemKey))
            {
                if (!EqualityComparer<T>.Default.Equals(target[index], item))
                {
                    target[index] = item;
                }
                continue;
            }

            var existing = -1;
            for (var search = index + 1; search < target.Count; search++)
            {
                if (EqualityComparer<TKey>.Default.Equals(key(target[search]), itemKey))
                {
                    existing = search;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, index);
                if (!EqualityComparer<T>.Default.Equals(target[index], item))
                {
                    target[index] = item;
                }
            }
            else
            {
                target.Insert(index, item);
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
