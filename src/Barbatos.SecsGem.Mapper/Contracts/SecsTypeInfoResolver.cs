// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

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
/// <c>Barbatos.SecsGem.SourceGenerators</c> emits, for every type it sees, a registration that
/// calls <see cref="Register"/> from a module initialiser - so a generated contract is simply
/// present, with no attribute to add and no partial class to write.
/// </para>
/// <para>
/// This resolver sits first in <see cref="SecsTypeInfoResolver.Default"/>, which is what makes the
/// generator additive: referencing it changes no call site, and a type that gains a generated
/// contract silently stops using reflection. Without the package every type falls through to
/// reflection and nothing here is ever asked twice.
/// </para>
/// </remarks>
public sealed class GeneratedSecsTypeInfoResolver : ISecsTypeInfoResolver
{
    private readonly ConcurrentDictionary<Type, Func<SecsSerializerOptions, SecsTypeInfo>> _factories = new();
    private readonly ConcurrentDictionary<(Type Type, SecsSerializerOptions Options), SecsTypeInfo?> _cache = new();

    /// <summary>Registers a generated contract.</summary>
    /// <param name="type">The type the contract describes.</param>
    /// <param name="factory">Builds the contract for a given set of options.</param>
    /// <remarks>
    /// Called by generated code from a module initialiser. Safe to call more than once for the
    /// same type: the last registration wins, and anything already built from an earlier one is
    /// discarded, so a stale contract cannot outlive the registration that replaced it.
    /// </remarks>
    public void Register(Type type, Func<SecsSerializerOptions, SecsTypeInfo> factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[type] = factory;

        foreach (var key in _cache.Keys)
        {
            if (key.Type == type)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Gets a value indicating whether a generated contract exists for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> when the generator has seen it.</returns>
    public bool Contains(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return _factories.ContainsKey(type);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// A built contract is cached per type and per options instance, exactly as
    /// <see cref="ReflectionSecsTypeInfoResolver"/> caches its own. That symmetry is the whole
    /// point: without it the generated factory would rebuild the contract - its property array,
    /// its delegates, its labels - for every mapped value, and the generator would cost more than
    /// the reflection it was brought in to replace.
    /// </para>
    /// <para>
    /// It did, until the benchmarks measured it. <c>MapperBenchmarks</c> compared a resolver that
    /// cached against one that did not and reported the generated side slower and allocating
    /// half again as much, which is the only reason this cache exists.
    /// </para>
    /// </remarks>
    public SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);

        if (!_factories.TryGetValue(type, out var factory))
        {
            return null;
        }

        return _cache.GetOrAdd((type, options), static (key, build) => build(key.Options), factory);
    }
}
