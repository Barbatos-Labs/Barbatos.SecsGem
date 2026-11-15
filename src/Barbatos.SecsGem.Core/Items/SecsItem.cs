// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem;

/// <summary>
/// A node in a SECS-II item tree: either a list of child items, or an array of values of one
/// <see cref="SecsFormat"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is the <em>hub</em> of the whole library. Binary SECS-II, SML, JSON, XML, INI and
/// plain C# objects are all spokes that convert to and from an item tree, which is why adding a
/// new format never touches this assembly.
/// </para>
/// <para>
/// Instances come from the factory methods on this class (<see cref="L(SecsItem[])"/>,
/// <see cref="A(string?)"/>, <see cref="U4(uint[])"/>, ...) rather than from a constructor, so
/// the implementation can pick a representation - inline array, pooled buffer, interned string -
/// without that choice leaking into the public API.
/// </para>
/// <para>
/// <strong>Ownership.</strong> An item decoded from the wire above
/// <see cref="SecsCodecOptions.PooledPayloadThreshold"/> borrows its buffer from
/// <see cref="ArrayPool{T}"/>, so <see cref="Dispose"/> must be called - normally by disposing the
/// <see cref="SecsMessage"/> that owns the tree, which disposes its children recursively.
/// Disposing twice is safe. Items built in memory from user data own nothing and ignore
/// <see cref="Dispose"/>.
/// </para>
/// <para>
/// <strong>Immutability.</strong> Unlike secs4net, a list's children cannot be replaced after
/// construction. That removes a whole class of double-dispose and aliasing bug, and it is what
/// lets an item be handed to several readers at once.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1063:Implement IDisposable correctly",
    Justification = "Items hold pooled managed memory and no unmanaged handle, so there is no finalizer to co-ordinate with. Disposal is forwarded to DisposeCore by the sealed Dispose.")]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public abstract partial class SecsItem : IEquatable<SecsItem>, IDisposable
{
    private bool _disposed;

    private protected SecsItem(SecsFormat format) => Format = format;

    /// <summary>Gets the item's SECS-II format.</summary>
    public SecsFormat Format { get; }

    /// <summary>
    /// Gets or sets an optional name for this item, used by the formats that can carry one.
    /// </summary>
    /// <remarks>
    /// SECS-II itself has no item names: the wire format is purely positional. INI needs a name
    /// to address an item, and XML and SML can both carry one, so the label rides along on the
    /// hub to make those conversions loss-free. It is ignored by the binary codec and by
    /// equality - two items that differ only in their label are equal.
    /// </remarks>
    public string? Label { get; set; }

    /// <summary>
    /// Gets the item's length: the number of child items for a list, the number of elements for
    /// a value array, the number of characters for text.
    /// </summary>
    public abstract int Count { get; }

    /// <summary>Gets a value indicating whether this item is a <see cref="SecsFormat.List"/>.</summary>
    public virtual bool IsList => false;

    /// <summary>Gets the child items of a list.</summary>
    /// <exception cref="NotSupportedException">This item is not a list.</exception>
    public virtual ReadOnlySpan<SecsItem> Items => throw NotSupported();

    /// <summary>Gets the child item at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The child item.</returns>
    /// <exception cref="NotSupportedException">This item is not a list.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public virtual SecsItem this[int index] => throw NotSupported();

    /// <summary>Gets a reference to the first element, reinterpreted as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The value type to read.</typeparam>
    /// <returns>A reference into the item's own storage - writing through it mutates the item.</returns>
    /// <exception cref="NotSupportedException">This item is a list or text.</exception>
    /// <exception cref="IndexOutOfRangeException">The item is empty, or shorter than <typeparamref name="T"/>.</exception>
    public virtual ref T FirstValue<T>()
        where T : unmanaged, IEquatable<T>
        => throw NotSupported();

    /// <summary>Gets the first element reinterpreted as <typeparamref name="T"/>, or a default.</summary>
    /// <typeparam name="T">The value type to read.</typeparam>
    /// <param name="defaultValue">What to return when the item is empty or too short.</param>
    /// <returns>The first element, or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="NotSupportedException">This item is a list or text.</exception>
    public virtual T FirstValueOrDefault<T>(T defaultValue = default)
        where T : unmanaged, IEquatable<T>
        => throw NotSupported();

    /// <summary>Gets the item's payload as a writable <see cref="Memory{T}"/> over its own storage.</summary>
    /// <typeparam name="T">The element type to reinterpret the payload as.</typeparam>
    /// <returns>The payload. No copy is made.</returns>
    /// <exception cref="NotSupportedException">This item is a list or text.</exception>
    public virtual Memory<T> GetMemory<T>()
        where T : unmanaged, IEquatable<T>
        => throw NotSupported();

    /// <summary>Gets the item's payload as a read-only span over its own storage.</summary>
    /// <typeparam name="T">The element type to reinterpret the payload as.</typeparam>
    /// <returns>The payload. No copy is made.</returns>
    /// <exception cref="NotSupportedException">This item is a list or text.</exception>
    public virtual ReadOnlySpan<T> GetSpan<T>()
        where T : unmanaged, IEquatable<T>
        => throw NotSupported();

    /// <summary>Tries to view the payload as <typeparamref name="T"/> without throwing.</summary>
    /// <typeparam name="T">The element type to reinterpret the payload as.</typeparam>
    /// <param name="values">The payload, or empty when this item holds no value array.</param>
    /// <returns><see langword="true"/> when the item holds a value array.</returns>
    /// <remarks>
    /// The non-throwing counterpart of <see cref="GetSpan{T}"/>, for code walking a tree of mixed
    /// formats where a wrong guess is expected rather than exceptional.
    /// </remarks>
    public virtual bool TryGetSpan<T>(out ReadOnlySpan<T> values)
        where T : unmanaged, IEquatable<T>
    {
        values = default;
        return false;
    }

    /// <summary>Gets the item's text.</summary>
    /// <returns>The decoded string.</returns>
    /// <exception cref="NotSupportedException">This item is not <see cref="SecsFormat.ASCII"/> or <see cref="SecsFormat.JIS8"/>.</exception>
    public virtual string GetString() => throw NotSupported();

    /// <summary>Sets <see cref="Label"/> and returns this item, for use inside a factory expression.</summary>
    /// <param name="label">The label, or <see langword="null"/> to clear it.</param>
    /// <returns>This item.</returns>
    /// <remarks>
    /// Shared empty items are returned by the factories as singletons; labelling one would label
    /// it for every caller, so this method copies such an item before labelling it.
    /// </remarks>
    public SecsItem Named(string? label)
    {
        if (IsSharedEmpty(this) && label is not null)
        {
            var copy = CloneEmpty(Format);
            copy.Label = label;
            return copy;
        }

        Label = label;
        return this;
    }

    /// <summary>Writes this item to <paramref name="writer"/> in SECS-II binary form, using <see cref="SecsCodecOptions.Default"/>.</summary>
    /// <param name="writer">The destination.</param>
    public void EncodeTo(IBufferWriter<byte> writer) => EncodeTo(writer, SecsCodecOptions.Default);

    /// <summary>Writes this item to <paramref name="writer"/> in SECS-II binary form.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="options">Text encodings and limits.</param>
    public abstract void EncodeTo(IBufferWriter<byte> writer, SecsCodecOptions options);

    /// <summary>Encodes this item to a new byte array. Intended for tests and diagnostics.</summary>
    /// <returns>The encoded bytes.</returns>
    /// <remarks>
    /// Allocates. On a hot path, encode into the caller's <see cref="IBufferWriter{T}"/> with
    /// <see cref="EncodeTo(IBufferWriter{byte})"/> instead.
    /// </remarks>
    public byte[] ToByteArray()
    {
        using var buffer = new ArrayPoolBufferWriter<byte>();
        EncodeTo(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Releases the pooled memory this item borrowed, and that of its children.</summary>
    /// <remarks>
    /// <para>Safe to call more than once.</para>
    /// <para>
    /// Also safe to call on one of the shared empty items that <see cref="Empty(SecsFormat)"/>
    /// returns, where it does nothing at all. Those are process-wide singletons, and a caller who
    /// merely received one - from <c>SecsItem.L()</c>, from <c>SecsItem.A(null)</c>, or by
    /// decoding an empty item off the wire - has no way of knowing it is not theirs to release.
    /// Letting such a call mark the singleton spent would be a trap: one disposed message would
    /// quietly poison every empty item in the process.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed || IsSharedEmpty(this))
        {
            return;
        }

        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases whatever this item owns. The default implementation owns nothing.</summary>
    protected virtual void DisposeCore()
    {
    }

    /// <summary>Compares two items by format and payload.</summary>
    /// <param name="left">The left item.</param>
    /// <param name="right">The right item.</param>
    /// <returns><see langword="true"/> when both are equal, or both <see langword="null"/>.</returns>
    public static bool operator ==(SecsItem? left, SecsItem? right)
        => ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    /// <summary>Compares two items by format and payload.</summary>
    /// <param name="left">The left item.</param>
    /// <param name="right">The right item.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(SecsItem? left, SecsItem? right) => !(left == right);

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj) => Equals(obj as SecsItem);

    /// <summary>Compares this item with another by format and payload, ignoring <see cref="Label"/>.</summary>
    /// <param name="other">The item to compare with.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    public bool Equals(SecsItem? other) => other is not null && (ReferenceEquals(this, other) || EqualsCore(other));

    /// <inheritdoc/>
    /// <remarks>
    /// Derived from the format and length only. Hashing a multi-megabyte payload to put an item
    /// in a dictionary is rarely what anyone wants, and equality still does the full comparison.
    /// </remarks>
    public sealed override int GetHashCode() => HashCode.Combine(Format, Count);

    /// <inheritdoc/>
    public sealed override string ToString() => $"<{Format.ToSmlToken()} [{Count}]>";

    /// <summary>Compares the payload with that of <paramref name="other"/>.</summary>
    /// <param name="other">The item to compare with. Never <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the payloads match.</returns>
    private protected abstract bool EqualsCore(SecsItem other);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NotSupportedException NotSupported([CallerMemberName] string? member = null)
        => new($"{member} is not supported on an item whose {nameof(Format)} is {Format}.");

    private string DebuggerDisplay => Format switch
    {
        SecsFormat.List => $"L[{Count}]",
        SecsFormat.ASCII or SecsFormat.JIS8 => $"{Format.ToSmlToken()}[{Count}] '{GetString()}'",
        _ => $"{Format.ToSmlToken()}[{Count}]",
    };
}
