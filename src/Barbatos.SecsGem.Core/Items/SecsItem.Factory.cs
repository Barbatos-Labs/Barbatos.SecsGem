// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    private static readonly SecsItem SharedEmptyList = new ListItem([]);
    private static readonly SecsItem SharedEmptyAscii = new StringItem(SecsFormat.ASCII, string.Empty);
    private static readonly SecsItem SharedEmptyJis8 = new StringItem(SecsFormat.JIS8, string.Empty);
    private static readonly SecsItem SharedEmptyBinary = new MemoryItem<byte>(SecsFormat.Binary, Memory<byte>.Empty);
    private static readonly SecsItem SharedEmptyBoolean = new MemoryItem<bool>(SecsFormat.Boolean, Memory<bool>.Empty);
    private static readonly SecsItem SharedEmptyI1 = new MemoryItem<sbyte>(SecsFormat.I1, Memory<sbyte>.Empty);
    private static readonly SecsItem SharedEmptyI2 = new MemoryItem<short>(SecsFormat.I2, Memory<short>.Empty);
    private static readonly SecsItem SharedEmptyI4 = new MemoryItem<int>(SecsFormat.I4, Memory<int>.Empty);
    private static readonly SecsItem SharedEmptyI8 = new MemoryItem<long>(SecsFormat.I8, Memory<long>.Empty);
    private static readonly SecsItem SharedEmptyU1 = new MemoryItem<byte>(SecsFormat.U1, Memory<byte>.Empty);
    private static readonly SecsItem SharedEmptyU2 = new MemoryItem<ushort>(SecsFormat.U2, Memory<ushort>.Empty);
    private static readonly SecsItem SharedEmptyU4 = new MemoryItem<uint>(SecsFormat.U4, Memory<uint>.Empty);
    private static readonly SecsItem SharedEmptyU8 = new MemoryItem<ulong>(SecsFormat.U8, Memory<ulong>.Empty);
    private static readonly SecsItem SharedEmptyF4 = new MemoryItem<float>(SecsFormat.F4, Memory<float>.Empty);
    private static readonly SecsItem SharedEmptyF8 = new MemoryItem<double>(SecsFormat.F8, Memory<double>.Empty);

    /// <summary>Gets the shared empty item of <paramref name="format"/>.</summary>
    /// <param name="format">The format.</param>
    /// <returns>A cached, immutable empty item - no allocation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format.</exception>
    /// <remarks>
    /// Empty items are extremely common in SECS traffic (<c>&lt;L[0]&gt;</c> as an acknowledgement
    /// body, <c>&lt;A[0]&gt;</c> for an absent string), so they are singletons. Call
    /// <see cref="Named(string?)"/> on one and you get a copy, never a relabelled singleton.
    /// </remarks>
    public static SecsItem Empty(SecsFormat format) => format switch
    {
        SecsFormat.List => SharedEmptyList,
        SecsFormat.ASCII => SharedEmptyAscii,
        SecsFormat.JIS8 => SharedEmptyJis8,
        SecsFormat.Binary => SharedEmptyBinary,
        SecsFormat.Boolean => SharedEmptyBoolean,
        SecsFormat.I1 => SharedEmptyI1,
        SecsFormat.I2 => SharedEmptyI2,
        SecsFormat.I4 => SharedEmptyI4,
        SecsFormat.I8 => SharedEmptyI8,
        SecsFormat.U1 => SharedEmptyU1,
        SecsFormat.U2 => SharedEmptyU2,
        SecsFormat.U4 => SharedEmptyU4,
        SecsFormat.U8 => SharedEmptyU8,
        SecsFormat.F4 => SharedEmptyF4,
        SecsFormat.F8 => SharedEmptyF8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), (int)format, "Not a SECS-II item format."),
    };

    private static bool IsSharedEmpty(SecsItem item) => ReferenceEquals(item, Empty(item.Format));

    private static SecsItem CloneEmpty(SecsFormat format) => format switch
    {
        SecsFormat.List => new ListItem([]),
        SecsFormat.ASCII => new StringItem(SecsFormat.ASCII, string.Empty),
        SecsFormat.JIS8 => new StringItem(SecsFormat.JIS8, string.Empty),
        SecsFormat.Binary => new MemoryItem<byte>(SecsFormat.Binary, Memory<byte>.Empty),
        SecsFormat.Boolean => new MemoryItem<bool>(SecsFormat.Boolean, Memory<bool>.Empty),
        SecsFormat.I1 => new MemoryItem<sbyte>(SecsFormat.I1, Memory<sbyte>.Empty),
        SecsFormat.I2 => new MemoryItem<short>(SecsFormat.I2, Memory<short>.Empty),
        SecsFormat.I4 => new MemoryItem<int>(SecsFormat.I4, Memory<int>.Empty),
        SecsFormat.I8 => new MemoryItem<long>(SecsFormat.I8, Memory<long>.Empty),
        SecsFormat.U1 => new MemoryItem<byte>(SecsFormat.U1, Memory<byte>.Empty),
        SecsFormat.U2 => new MemoryItem<ushort>(SecsFormat.U2, Memory<ushort>.Empty),
        SecsFormat.U4 => new MemoryItem<uint>(SecsFormat.U4, Memory<uint>.Empty),
        SecsFormat.U8 => new MemoryItem<ulong>(SecsFormat.U8, Memory<ulong>.Empty),
        SecsFormat.F4 => new MemoryItem<float>(SecsFormat.F4, Memory<float>.Empty),
        SecsFormat.F8 => new MemoryItem<double>(SecsFormat.F8, Memory<double>.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(format), (int)format, "Not a SECS-II item format."),
    };

    // ----------------------------------------- List ------------------------------------------

    /// <summary>Creates a <see cref="SecsFormat.List"/> item.</summary>
    /// <param name="items">The child items, in wire order. The list takes ownership of them.</param>
    /// <returns>The list item.</returns>
    public static SecsItem L(params SecsItem[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Length == 0 ? SharedEmptyList : new ListItem(items);
    }

    /// <summary>Creates a <see cref="SecsFormat.List"/> item.</summary>
    /// <param name="items">The child items, in wire order. The list takes ownership of them.</param>
    /// <returns>The list item.</returns>
    public static SecsItem L(IEnumerable<SecsItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return L([.. items]);
    }

    // ----------------------------------------- Text ------------------------------------------

    /// <summary>Creates an <see cref="SecsFormat.ASCII"/> item.</summary>
    /// <param name="value">The text. <see langword="null"/> and empty both give an empty item.</param>
    /// <returns>The text item.</returns>
    public static SecsItem A(string? value)
        => string.IsNullOrEmpty(value) ? SharedEmptyAscii : new StringItem(SecsFormat.ASCII, value);

    /// <summary>Creates a <see cref="SecsFormat.JIS8"/> item.</summary>
    /// <param name="value">The text. <see langword="null"/> and empty both give an empty item.</param>
    /// <returns>The text item.</returns>
    public static SecsItem J(string? value)
        => string.IsNullOrEmpty(value) ? SharedEmptyJis8 : new StringItem(SecsFormat.JIS8, value);

    /// <summary>
    /// Creates an <see cref="SecsFormat.ASCII"/> item padded or truncated to exactly
    /// <paramref name="length"/> characters.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="length">The fixed length the equipment expects.</param>
    /// <param name="padding">The character to pad with. Defaults to a space.</param>
    /// <returns>The text item.</returns>
    /// <remarks>
    /// GEM is full of fixed-width ASCII fields - <c>A[16]</c> timestamps, <c>A[6]</c> model
    /// numbers - and equipment that rejects anything else. This is the one-line way to satisfy
    /// them without string arithmetic at every call site.
    /// </remarks>
    public static SecsItem A(string? value, int length, char padding = ' ')
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return SharedEmptyAscii;
        }

        value ??= string.Empty;
        return new StringItem(
            SecsFormat.ASCII,
            value.Length == length ? value
                : value.Length > length ? value[..length]
                : value.PadRight(length, padding));
    }

    // ---------------------------------- Binary and Boolean -----------------------------------

    /// <summary>Creates a <see cref="SecsFormat.Binary"/> item.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The item.</returns>
    public static SecsItem B(params byte[] value) => B(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.Binary"/> item.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The item.</returns>
    public static SecsItem B(IEnumerable<byte> value) => B([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.Binary"/> item over memory the caller owns.</summary>
    /// <param name="value">The bytes. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem B(Memory<byte> value)
        => value.IsEmpty ? SharedEmptyBinary : new MemoryItem<byte>(SecsFormat.Binary, value);

    /// <summary>Creates a <see cref="SecsFormat.Binary"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem B(IMemoryOwner<byte> owner) => new PooledMemoryItem<byte>(SecsFormat.Binary, owner);

    /// <summary>Creates a <see cref="SecsFormat.Boolean"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem Boolean(params bool[] value) => Boolean(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.Boolean"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem Boolean(IEnumerable<bool> value) => Boolean([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.Boolean"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem Boolean(Memory<bool> value)
        => value.IsEmpty ? SharedEmptyBoolean : new MemoryItem<bool>(SecsFormat.Boolean, value);

    /// <summary>Creates a <see cref="SecsFormat.Boolean"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem Boolean(IMemoryOwner<bool> owner) => new PooledMemoryItem<bool>(SecsFormat.Boolean, owner);

    // --------------------------------------- Integers ----------------------------------------

    /// <summary>Creates an <see cref="SecsFormat.I1"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I1(params sbyte[] value) => I1(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.I1"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I1(IEnumerable<sbyte> value) => I1([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.I1"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem I1(Memory<sbyte> value)
        => value.IsEmpty ? SharedEmptyI1 : new MemoryItem<sbyte>(SecsFormat.I1, value);

    /// <summary>Creates an <see cref="SecsFormat.I1"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem I1(IMemoryOwner<sbyte> owner) => new PooledMemoryItem<sbyte>(SecsFormat.I1, owner);

    /// <summary>Creates an <see cref="SecsFormat.I2"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I2(params short[] value) => I2(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.I2"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I2(IEnumerable<short> value) => I2([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.I2"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem I2(Memory<short> value)
        => value.IsEmpty ? SharedEmptyI2 : new MemoryItem<short>(SecsFormat.I2, value);

    /// <summary>Creates an <see cref="SecsFormat.I2"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem I2(IMemoryOwner<short> owner) => new PooledMemoryItem<short>(SecsFormat.I2, owner);

    /// <summary>Creates an <see cref="SecsFormat.I4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I4(params int[] value) => I4(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.I4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I4(IEnumerable<int> value) => I4([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.I4"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem I4(Memory<int> value)
        => value.IsEmpty ? SharedEmptyI4 : new MemoryItem<int>(SecsFormat.I4, value);

    /// <summary>Creates an <see cref="SecsFormat.I4"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem I4(IMemoryOwner<int> owner) => new PooledMemoryItem<int>(SecsFormat.I4, owner);

    /// <summary>Creates an <see cref="SecsFormat.I8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I8(params long[] value) => I8(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.I8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem I8(IEnumerable<long> value) => I8([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.I8"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem I8(Memory<long> value)
        => value.IsEmpty ? SharedEmptyI8 : new MemoryItem<long>(SecsFormat.I8, value);

    /// <summary>Creates an <see cref="SecsFormat.I8"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem I8(IMemoryOwner<long> owner) => new PooledMemoryItem<long>(SecsFormat.I8, owner);

    /// <summary>Creates a <see cref="SecsFormat.U1"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U1(params byte[] value) => U1(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.U1"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U1(IEnumerable<byte> value) => U1([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.U1"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem U1(Memory<byte> value)
        => value.IsEmpty ? SharedEmptyU1 : new MemoryItem<byte>(SecsFormat.U1, value);

    /// <summary>Creates a <see cref="SecsFormat.U1"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem U1(IMemoryOwner<byte> owner) => new PooledMemoryItem<byte>(SecsFormat.U1, owner);

    /// <summary>Creates a <see cref="SecsFormat.U2"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U2(params ushort[] value) => U2(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.U2"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U2(IEnumerable<ushort> value) => U2([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.U2"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem U2(Memory<ushort> value)
        => value.IsEmpty ? SharedEmptyU2 : new MemoryItem<ushort>(SecsFormat.U2, value);

    /// <summary>Creates a <see cref="SecsFormat.U2"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem U2(IMemoryOwner<ushort> owner) => new PooledMemoryItem<ushort>(SecsFormat.U2, owner);

    /// <summary>Creates a <see cref="SecsFormat.U4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U4(params uint[] value) => U4(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.U4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U4(IEnumerable<uint> value) => U4([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.U4"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem U4(Memory<uint> value)
        => value.IsEmpty ? SharedEmptyU4 : new MemoryItem<uint>(SecsFormat.U4, value);

    /// <summary>Creates a <see cref="SecsFormat.U4"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem U4(IMemoryOwner<uint> owner) => new PooledMemoryItem<uint>(SecsFormat.U4, owner);

    /// <summary>Creates a <see cref="SecsFormat.U8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U8(params ulong[] value) => U8(value.AsMemory());

    /// <summary>Creates a <see cref="SecsFormat.U8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem U8(IEnumerable<ulong> value) => U8([.. value]);

    /// <summary>Creates a <see cref="SecsFormat.U8"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem U8(Memory<ulong> value)
        => value.IsEmpty ? SharedEmptyU8 : new MemoryItem<ulong>(SecsFormat.U8, value);

    /// <summary>Creates a <see cref="SecsFormat.U8"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem U8(IMemoryOwner<ulong> owner) => new PooledMemoryItem<ulong>(SecsFormat.U8, owner);

    // ------------------------------------ Floating point -------------------------------------

    /// <summary>Creates an <see cref="SecsFormat.F4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem F4(params float[] value) => F4(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.F4"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem F4(IEnumerable<float> value) => F4([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.F4"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem F4(Memory<float> value)
        => value.IsEmpty ? SharedEmptyF4 : new MemoryItem<float>(SecsFormat.F4, value);

    /// <summary>Creates an <see cref="SecsFormat.F4"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem F4(IMemoryOwner<float> owner) => new PooledMemoryItem<float>(SecsFormat.F4, owner);

    /// <summary>Creates an <see cref="SecsFormat.F8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem F8(params double[] value) => F8(value.AsMemory());

    /// <summary>Creates an <see cref="SecsFormat.F8"/> item.</summary>
    /// <param name="value">The values.</param>
    /// <returns>The item.</returns>
    public static SecsItem F8(IEnumerable<double> value) => F8([.. value]);

    /// <summary>Creates an <see cref="SecsFormat.F8"/> item over memory the caller owns.</summary>
    /// <param name="value">The values. Not copied.</param>
    /// <returns>The item.</returns>
    public static SecsItem F8(Memory<double> value)
        => value.IsEmpty ? SharedEmptyF8 : new MemoryItem<double>(SecsFormat.F8, value);

    /// <summary>Creates an <see cref="SecsFormat.F8"/> item that owns a rented buffer.</summary>
    /// <param name="owner">The buffer owner. The item disposes it.</param>
    /// <returns>The item.</returns>
    public static SecsItem F8(IMemoryOwner<double> owner) => new PooledMemoryItem<double>(SecsFormat.F8, owner);
}
