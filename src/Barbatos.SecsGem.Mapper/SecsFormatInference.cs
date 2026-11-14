// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections;

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// The C# type to <see cref="SecsFormat"/> rules.
/// </summary>
/// <remarks>
/// <para>
/// The convention half of convention-over-configuration. Each CLR integer type has exactly one
/// SECS-II format of the same width and signedness, so the mapping is not a guess:
/// </para>
/// <list type="table">
///   <item><term><see cref="string"/>, <see cref="char"/></term><description><see cref="SecsFormat.ASCII"/> (or JIS-8 per options)</description></item>
///   <item><term><see cref="bool"/></term><description><see cref="SecsFormat.Boolean"/></description></item>
///   <item><term><see cref="sbyte"/> / <see cref="short"/> / <see cref="int"/> / <see cref="long"/></term><description>I1 / I2 / I4 / I8</description></item>
///   <item><term><see cref="byte"/> / <see cref="ushort"/> / <see cref="uint"/> / <see cref="ulong"/></term><description>U1 / U2 / U4 / U8</description></item>
///   <item><term><see cref="float"/> / <see cref="double"/></term><description>F4 / F8</description></item>
///   <item><term>an <see cref="Enum"/></term><description>the format of its underlying type</description></item>
///   <item><term><see cref="byte"/>[], <see cref="Memory{T}"/> of byte</term><description><see cref="SecsFormat.Binary"/></description></item>
/// </list>
/// <para>
/// The one deliberate ambiguity is <see cref="byte"/>[]: it could be <see cref="SecsFormat.U1"/>
/// or <see cref="SecsFormat.Binary"/>. It maps to Binary, because a byte array in a C# model is
/// nearly always opaque data rather than a list of small numbers. Say
/// <c>[SecsItem(SecsFormat.U1)]</c> where it is not.
/// </para>
/// </remarks>
public static class SecsFormatInference
{
    /// <summary>Strips <see cref="Nullable{T}"/> from a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The underlying type, or <paramref name="type"/> itself.</returns>
    public static Type Unwrap(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    /// <summary>Infers the format for a scalar type.</summary>
    /// <param name="type">The type, with <see cref="Nullable{T}"/> already stripped.</param>
    /// <param name="options">The serializer options, which choose between ASCII and JIS-8.</param>
    /// <param name="format">The inferred format.</param>
    /// <returns><see langword="true"/> when <paramref name="type"/> is a scalar this mapper understands.</returns>
    public static bool TryInferScalar(Type type, SecsSerializerOptions options, out SecsFormat format)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);

        if (type.IsEnum)
        {
            return TryInferScalar(type.GetEnumUnderlyingType(), options, out format);
        }

        switch (type)
        {
            case var t when t == typeof(string) || t == typeof(char):
                format = options.DefaultTextFormat;
                return true;
            case var t when t == typeof(bool):
                format = SecsFormat.Boolean;
                return true;
            case var t when t == typeof(sbyte):
                format = SecsFormat.I1;
                return true;
            case var t when t == typeof(short):
                format = SecsFormat.I2;
                return true;
            case var t when t == typeof(int):
                format = SecsFormat.I4;
                return true;
            case var t when t == typeof(long):
                format = SecsFormat.I8;
                return true;
            case var t when t == typeof(byte):
                format = SecsFormat.U1;
                return true;
            case var t when t == typeof(ushort):
                format = SecsFormat.U2;
                return true;
            case var t when t == typeof(uint):
                format = SecsFormat.U4;
                return true;
            case var t when t == typeof(ulong):
                format = SecsFormat.U8;
                return true;
            case var t when t == typeof(float):
                format = SecsFormat.F4;
                return true;
            case var t when t == typeof(double):
                format = SecsFormat.F8;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>Gets a value indicating whether a type is an opaque byte payload.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> for <see cref="byte"/>[], <see cref="Memory{T}"/> and <see cref="ReadOnlyMemory{T}"/> of byte.</returns>
    public static bool IsBinaryPayload(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type == typeof(byte[])
            || type == typeof(Memory<byte>)
            || type == typeof(ReadOnlyMemory<byte>);
    }

    /// <summary>Gets the element type of an enumerable type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The element type, or <see langword="null"/> when <paramref name="type"/> is not a collection worth walking.</returns>
    /// <remarks>
    /// <see cref="string"/> is excluded on purpose: it is an <see cref="IEnumerable{T}"/> of
    /// <see cref="char"/>, and treating it as a collection would turn every name in a message
    /// into a list of one-character items.
    /// </remarks>
    public static Type? GetCollectionElementType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(string) || IsBinaryPayload(type))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            // Memory<T> and ReadOnlyMemory<T> are deliberately absent: they are not
            // IEnumerable, so claiming them here would promise a walk the mapper cannot do.
            // Memory<byte> is still handled - as a binary payload, by IsBinaryPayload.
            if (definition == typeof(List<>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
    }

    /// <summary>Builds a value item of <paramref name="format"/> from boxed scalar values.</summary>
    /// <param name="format">The target format.</param>
    /// <param name="values">The values, each convertible to the format's element type.</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is a list or text format.</exception>
    public static SecsItem CreateValueItem(SecsFormat format, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return format switch
        {
            SecsFormat.Boolean => SecsItem.Boolean(Convert<bool>(values)),
            SecsFormat.Binary => SecsItem.B(Convert<byte>(values)),
            SecsFormat.U1 => SecsItem.U1(Convert<byte>(values)),
            SecsFormat.U2 => SecsItem.U2(Convert<ushort>(values)),
            SecsFormat.U4 => SecsItem.U4(Convert<uint>(values)),
            SecsFormat.U8 => SecsItem.U8(Convert<ulong>(values)),
            SecsFormat.I1 => SecsItem.I1(Convert<sbyte>(values)),
            SecsFormat.I2 => SecsItem.I2(Convert<short>(values)),
            SecsFormat.I4 => SecsItem.I4(Convert<int>(values)),
            SecsFormat.I8 => SecsItem.I8(Convert<long>(values)),
            SecsFormat.F4 => SecsItem.F4(Convert<float>(values)),
            SecsFormat.F8 => SecsItem.F8(Convert<double>(values)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a numeric or boolean format."),
        };

        static T[] Convert<T>(IReadOnlyList<object?> values)
            where T : struct
        {
            var result = new T[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                result[i] = value is null
                    ? default
                    : (T)System.Convert.ChangeType(UnwrapEnum(value), typeof(T), CultureInfo.InvariantCulture);
            }

            return result;
        }

        static object UnwrapEnum(object value)
            => value.GetType().IsEnum
                ? System.Convert.ChangeType(value, value.GetType().GetEnumUnderlyingType(), CultureInfo.InvariantCulture)
                : value;
    }

    /// <summary>Reads an item's elements as boxed values of <paramref name="elementType"/>.</summary>
    /// <param name="item">The value item.</param>
    /// <param name="elementType">The CLR type each element should become.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The elements.</returns>
    public static object?[] ReadValues(SecsItem item, Type elementType, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(options);

        var target = Unwrap(elementType);

        // A text item never becomes an array of values: it is one string, however long.
        if (item.Format.IsText())
        {
            return [ConvertScalar(item.GetString(), target, options)];
        }

        var boxed = item.Format switch
        {
            SecsFormat.Boolean => Box(item.GetSpan<bool>()),
            SecsFormat.Binary or SecsFormat.U1 => Box(item.GetSpan<byte>()),
            SecsFormat.U2 => Box(item.GetSpan<ushort>()),
            SecsFormat.U4 => Box(item.GetSpan<uint>()),
            SecsFormat.U8 => Box(item.GetSpan<ulong>()),
            SecsFormat.I1 => Box(item.GetSpan<sbyte>()),
            SecsFormat.I2 => Box(item.GetSpan<short>()),
            SecsFormat.I4 => Box(item.GetSpan<int>()),
            SecsFormat.I8 => Box(item.GetSpan<long>()),
            SecsFormat.F4 => Box(item.GetSpan<float>()),
            SecsFormat.F8 => Box(item.GetSpan<double>()),
            _ => throw new SecsMappingException($"Cannot read values from a {item.Format} item."),
        };

        for (var i = 0; i < boxed.Length; i++)
        {
            boxed[i] = ConvertScalar(boxed[i], target, options);
        }

        return boxed;

        static object?[] Box<T>(ReadOnlySpan<T> values)
            where T : unmanaged, IEquatable<T>
        {
            var result = new object?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                result[i] = values[i];
            }

            return result;
        }
    }

    /// <summary>Converts one boxed value to a target CLR type, applying the mapper's coercion rules.</summary>
    /// <param name="value">The value.</param>
    /// <param name="target">The type to produce, with <see cref="Nullable{T}"/> already stripped.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The converted value.</returns>
    /// <exception cref="SecsMappingException">The value cannot become <paramref name="target"/>.</exception>
    public static object? ConvertScalar(object? value, Type target, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        if (value is null)
        {
            return target.IsValueType ? Activator.CreateInstance(target) : null;
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (target.IsEnum)
            {
                return value is string text
                    ? Enum.Parse(target, text, ignoreCase: true)
                    : Enum.ToObject(target, System.Convert.ChangeType(value, target.GetEnumUnderlyingType(), CultureInfo.InvariantCulture));
            }

            if (target == typeof(string))
            {
                return System.Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is string s)
            {
                if (!options.AllowStringToNumber)
                {
                    throw new SecsMappingException(
                        $"An ASCII item cannot fill a {target.Name}. Set {nameof(SecsSerializerOptions.AllowStringToNumber)} if the equipment really sends text here.");
                }

                if (target == typeof(bool))
                {
                    return s is "T" or "t" or "1" || bool.Parse(s);
                }

                return System.Convert.ChangeType(s.Trim(), target, CultureInfo.InvariantCulture);
            }

            if (target == typeof(bool))
            {
                return System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
            }

            return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new SecsMappingException($"Cannot convert '{value}' to {target.Name}: {ex.Message}", ex);
        }
    }
}
