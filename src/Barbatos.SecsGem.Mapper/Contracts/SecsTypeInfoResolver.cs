// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// The resolvers the mapper ships with, and how to combine them.
/// </summary>
public static class SecsTypeInfoResolver
{
    /// <summary>The reflection resolver: reads a type's shape from its metadata at run time.</summary>
    public static ISecsTypeInfoResolver Reflection { get; } = new ReflectionSecsTypeInfoResolver();

    /// <summary>The generated resolver: serves contracts the source generator emitted at compile time.</summary>
    public static GeneratedSecsTypeInfoResolver Generated { get; } = new();

    /// <summary>
    /// The default chain: generated contracts first, reflection as the fallback.
    /// </summary>
    /// <remarks>
    /// The order is the whole design. A type the generator has seen is mapped from its syntax
    /// tree - exact declaration order, no reflection, trim-safe. Anything else still works,
    /// through reflection, which is what keeps a type from a third-party assembly or one built
    /// at run time usable at all.
    /// </remarks>
    public static ISecsTypeInfoResolver Default { get; } = Combine(Generated, Reflection);

    /// <summary>Chains resolvers, first match wins.</summary>
    /// <param name="resolvers">The resolvers, in priority order.</param>
    /// <returns>A resolver that asks each in turn.</returns>
    public static ISecsTypeInfoResolver Combine(params ISecsTypeInfoResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        return resolvers.Length == 1 ? resolvers[0] : new ChainResolver(resolvers);
    }

    private sealed class ChainResolver(ISecsTypeInfoResolver[] resolvers) : ISecsTypeInfoResolver
    {
        public SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)
        {
            foreach (var resolver in resolvers)
            {
                var info = resolver.GetTypeInfo(type, options);
                if (info is not null)
                {
                    return info;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Serves the contracts the source generator emitted.
/// </summary>
/// <remarks>
/// <para>
/// Phase 5 fills this in. The generator will emit, for every type it sees, a registration that
/// calls <see cref="Register"/> from a module initialiser - so a generated contract is simply
/// present, with no attribute to add and no partial class to write.
/// </para>
/// <para>
/// The type exists now, and sits first in <see cref="SecsTypeInfoResolver.Default"/>, so adding
/// the generator later is additive: no existing call site changes, and a type that gains a
/// generated contract silently stops using reflection.
/// </para>
/// </remarks>
public sealed class GeneratedSecsTypeInfoResolver : ISecsTypeInfoResolver
{
    private readonly Dictionary<Type, Func<SecsSerializerOptions, SecsTypeInfo>> _factories = [];

    /// <summary>Registers a generated contract.</summary>
    /// <param name="type">The type the contract describes.</param>
    /// <param name="factory">Builds the contract for a given set of options.</param>
    /// <remarks>Called by generated code. Safe to call more than once for the same type; the last wins.</remarks>
    public void Register(Type type, Func<SecsSerializerOptions, SecsTypeInfo> factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_factories)
        {
            _factories[type] = factory;
        }
    }

    /// <summary>Gets a value indicating whether a generated contract exists for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> when the generator has seen it.</returns>
    public bool Contains(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        lock (_factories)
        {
            return _factories.ContainsKey(type);
        }
    }

    /// <inheritdoc/>
    public SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);

        Func<SecsSerializerOptions, SecsTypeInfo>? factory;
        lock (_factories)
        {
            if (!_factories.TryGetValue(type, out factory))
            {
                return null;
            }
        }

        return factory(options);
    }
}
