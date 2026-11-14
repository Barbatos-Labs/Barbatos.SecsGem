// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using CommunityToolkit.HighPerformance;

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    /// <summary>
    /// A value-array item - every format except <see cref="SecsFormat.List"/>,
    /// <see cref="SecsFormat.ASCII"/> and <see cref="SecsFormat.JIS8"/>.
    /// </summary>
    /// <typeparam name="T">The element type matching the item's format.</typeparam>
    /// <remarks>
    /// Owns nothing: the <see cref="Memory{T}"/> belongs to whoever built the item.
    /// <see cref="PooledMemoryItem{T}"/> is the variant that owns a rented buffer.
    /// </remarks>
    [DebuggerTypeProxy(typeof(MemoryItem<>.MemoryItemDebugView))]
    [SkipLocalsInit]
    private class MemoryItem<T> : SecsItem
        where T : unmanaged, IEquatable<T>
    {
        private readonly Memory<T> _value;

        internal MemoryItem(SecsFormat format, Memory<T> value)
            : base(format)
            => _value = value;

        public sealed override int Count => _value.Length;

        public sealed override ref TResult FirstValue<TResult>()
        {
            var span = _value.Span;
            if (span.Length * Unsafe.SizeOf<T>() < Unsafe.SizeOf<TResult>())
            {
                ThrowTooShort();
            }

            return ref Unsafe.As<T, TResult>(ref MemoryMarshal.GetReference(span));

            [DoesNotReturn]
            static void ThrowTooShort()
                => throw new IndexOutOfRangeException(
                    $"The item is empty or its payload is shorter than {typeof(TResult).Name}.");
        }

        public sealed override TResult FirstValueOrDefault<TResult>(TResult defaultValue = default)
        {
            var span = _value.Span;
            return span.Length * Unsafe.SizeOf<T>() < Unsafe.SizeOf<TResult>()
                ? defaultValue
                : Unsafe.As<T, TResult>(ref MemoryMarshal.GetReference(span));
        }

        public sealed override Memory<TResult> GetMemory<TResult>() => _value.Cast<T, TResult>();

        public sealed override ReadOnlySpan<TResult> GetSpan<TResult>()
            => MemoryMarshal.Cast<T, TResult>(_value.Span);

        public sealed override bool TryGetSpan<TResult>(out ReadOnlySpan<TResult> values)
        {
            values = MemoryMarshal.Cast<T, TResult>(_value.Span);
            return true;
        }

        public sealed override void EncodeTo(IBufferWriter<byte> writer, SecsCodecOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(options);

            var value = _value;
            if (value.IsEmpty)
            {
                WriteEmptyItemHeader(Format, writer);
                return;
            }

            var sourceBytes = MemoryMarshal.AsBytes(value.Span);
            var byteLength = sourceBytes.Length;

            WriteItemHeader(Format, byteLength, writer);

            // Copy straight into the writer's span and byte-swap in place: one pass, no scratch
            // buffer, and nothing the Large Object Heap ever sees.
            var destination = writer.GetSpan(byteLength)[..byteLength];
            sourceBytes.CopyTo(destination);
            EndiannessHelper.SwapIfNeeded(MemoryMarshal.Cast<byte, T>(destination));
            writer.Advance(byteLength);
        }

        private protected sealed override bool EqualsCore(SecsItem other)
            => Format == other.Format
            && other is MemoryItem<T> memory
            && _value.Span.SequenceEqual(memory._value.Span);

        private sealed class MemoryItemDebugView(MemoryItem<T> item)
        {
            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public T[] Values => item._value.ToArray();
        }
    }

    /// <summary>
    /// A value-array item whose payload is rented from a pool and returned on <see cref="Dispose"/>.
    /// </summary>
    /// <typeparam name="T">The element type matching the item's format.</typeparam>
    /// <remarks>
    /// The decoder picks this over <see cref="MemoryItem{T}"/> once a payload passes
    /// <see cref="SecsCodecOptions.PooledPayloadThreshold"/>, which is how a stream of large
    /// messages runs without allocating - and why disposing a received message matters.
    /// </remarks>
    private sealed class PooledMemoryItem<T> : MemoryItem<T>
        where T : unmanaged, IEquatable<T>
    {
        private readonly IMemoryOwner<T> _owner;

        internal PooledMemoryItem(SecsFormat format, IMemoryOwner<T> owner)
            : base(format, owner.Memory)
            => _owner = owner;

        protected override void DisposeCore() => _owner.Dispose();
    }
}
