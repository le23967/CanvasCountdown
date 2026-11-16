using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// An immutable list that compares by its contents.
/// </summary>
/// <remarks>
/// The macOS models are Swift structs, which copy on assignment and compare
/// their arrays element by element. A C# record holding a <c>List</c> does
/// neither: two copies share one list, and equality compares references. Both
/// break code ported from Swift in ways that compile and then misbehave — a
/// settings form that thinks nothing changed, or an edit that leaks into the
/// saved copy before Save. Models hold their sequences in this type instead.
/// </remarks>
[JsonConverter(typeof(ValueListJsonConverterFactory))]
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public readonly struct ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly ImmutableArray<T> items;

    public ValueList(IEnumerable<T> items)
    {
        this.items = items is ImmutableArray<T> array ? array : items.ToImmutableArray();
    }

    private ValueList(ImmutableArray<T> items)
    {
        this.items = items;
    }

    public static ValueList<T> Empty => new(ImmutableArray<T>.Empty);

    private ImmutableArray<T> Items => items.IsDefault ? ImmutableArray<T>.Empty : items;

    public int Count => Items.Length;

    public bool IsEmpty => Items.Length == 0;

    public T this[int index] => Items[index];

    public ValueList<T> Add(T item) => new(Items.Add(item));

    public ValueList<T> AddRange(IEnumerable<T> values) => new(Items.AddRange(values));

    public ValueList<T> Insert(int index, T item) => new(Items.Insert(index, item));

    public ValueList<T> RemoveAt(int index) => new(Items.RemoveAt(index));

    public ValueList<T> RemoveAll(Predicate<T> match) => new(Items.RemoveAll(match));

    public ValueList<T> SetItem(int index, T item) => new(Items.SetItem(index, item));

    public int FindIndex(Predicate<T> match)
    {
        for (var index = 0; index < Items.Length; index++)
        {
            if (match(Items[index]))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>
    /// Swift's <c>move(fromOffsets:toOffset:)</c>: the items at
    /// <paramref name="sourceIndices"/> are taken out, keeping their order, and
    /// put back in front of the item that was at <paramref name="destination"/>
    /// before anything moved (or at the end, when it is <see cref="Count"/>).
    /// </summary>
    public ValueList<T> Move(IEnumerable<int> sourceIndices, int destination)
    {
        var current = Items;
        var sources = sourceIndices.Where(index => index >= 0 && index < current.Length).Distinct().Order().ToArray();
        if (sources.Length == 0)
        {
            return this;
        }
        var moving = sources.Select(index => current[index]).ToArray();
        var sourceSet = sources.ToHashSet();
        var remaining = current.Where((_, index) => !sourceSet.Contains(index)).ToList();
        var clampedDestination = Math.Clamp(destination, 0, current.Length);
        var insertAt = clampedDestination - sources.Count(index => index < clampedDestination);
        remaining.InsertRange(insertAt, moving);
        return new ValueList<T>(remaining);
    }

    public ImmutableArray<T> ToImmutableArray() => Items;

    public bool Equals(ValueList<T> other)
    {
        var mine = Items;
        var theirs = other.Items;
        if (mine.Length != theirs.Length)
        {
            return false;
        }
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < mine.Length; index++)
        {
            if (!comparer.Equals(mine[index], theirs[index]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueList<T> left, ValueList<T> right) => left.Equals(right);

    public static bool operator !=(ValueList<T> left, ValueList<T> right) => !left.Equals(right);

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();

    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());

    public static ValueList<T> ToValueList<T>(this IEnumerable<T> source) => new(source);
}

/// <summary>
/// An immutable set that compares by its contents, for the same reason as
/// <see cref="ValueList{T}"/>: Swift's <c>Set</c> is a value.
/// </summary>
[JsonConverter(typeof(ValueSetJsonConverterFactory))]
[CollectionBuilder(typeof(ValueSet), nameof(ValueSet.Create))]
public readonly struct ValueSet<T> : IReadOnlyCollection<T>, IEquatable<ValueSet<T>>
    where T : notnull
{
    private readonly ImmutableHashSet<T>? items;

    public ValueSet(IEnumerable<T> items)
    {
        this.items = items.ToImmutableHashSet();
    }

    private ValueSet(ImmutableHashSet<T> items)
    {
        this.items = items;
    }

    public static ValueSet<T> Empty => new(ImmutableHashSet<T>.Empty);

    private ImmutableHashSet<T> Items => items ?? ImmutableHashSet<T>.Empty;

    public int Count => Items.Count;

    public bool IsEmpty => Items.Count == 0;

    public bool Contains(T item) => Items.Contains(item);

    public ValueSet<T> Add(T item) => new(Items.Add(item));

    public ValueSet<T> Remove(T item) => new(Items.Remove(item));

    public ValueSet<T> Union(IEnumerable<T> other) => new(Items.Union(other));

    public ValueSet<T> Except(IEnumerable<T> other) => new(Items.Except(other));

    public ValueSet<T> Intersect(IEnumerable<T> other) => new(Items.Intersect(other));

    public ValueSet<T> Where(Func<T, bool> predicate) => new(Items.Where(predicate));

    public bool IsSubsetOf(IEnumerable<T> other) => Items.IsSubsetOf(other);

    public bool Overlaps(IEnumerable<T> other) => Items.Overlaps(other);

    public bool Equals(ValueSet<T> other) => Items.SetEquals(other.Items);

    public override bool Equals(object? obj) => obj is ValueSet<T> other && Equals(other);

    public override int GetHashCode()
    {
        // Order-independent, so equal sets hash alike however they were built.
        var combined = 0;
        foreach (var item in Items)
        {
            combined ^= EqualityComparer<T>.Default.GetHashCode(item) * 397;
        }
        return HashCode.Combine(combined, Items.Count);
    }

    public static bool operator ==(ValueSet<T> left, ValueSet<T> right) => left.Equals(right);

    public static bool operator !=(ValueSet<T> left, ValueSet<T> right) => !left.Equals(right);

    public ImmutableHashSet<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

    /// <summary>
    /// The members in a stable order, for writing out and for display. Strings
    /// sort ordinally, as Swift's <c>sorted()</c> does, not by the current
    /// culture, so the order cannot change with the machine's language.
    /// </summary>
    public IReadOnlyList<T> Sorted()
    {
        IComparer<T> comparer = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)StringComparer.Ordinal
            : Comparer<T>.Default;
        return Items.OrderBy(item => item, comparer).ToArray();
    }

    public override string ToString() => $"{{{string.Join(", ", Sorted())}}}";
}

public static class ValueSet
{
    public static ValueSet<T> Create<T>(ReadOnlySpan<T> items) where T : notnull => new(items.ToArray());

    public static ValueSet<T> ToValueSet<T>(this IEnumerable<T> source) where T : notnull => new(source);
}

internal sealed class ValueListJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(ValueListJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class ValueListJsonConverter<T> : JsonConverter<ValueList<T>>
    {
        public override ValueList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return ValueList<T>.Empty;
            }
            var items = JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? [];
            return new ValueList<T>(items);
        }

        public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.ToImmutableArray().ToArray(), options);
    }
}

internal sealed class ValueSetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueSet<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(ValueSetJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class ValueSetJsonConverter<T> : JsonConverter<ValueSet<T>>
        where T : notnull
    {
        public override ValueSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return ValueSet<T>.Empty;
            }
            var items = JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? [];
            return new ValueSet<T>(items);
        }

        // Written sorted so the same set always produces the same file.
        public override void Write(Utf8JsonWriter writer, ValueSet<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.Sorted(), options);
    }
}
