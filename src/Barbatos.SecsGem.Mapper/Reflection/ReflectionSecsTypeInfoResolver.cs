// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Discovers a type's mapping contract from its metadata at run time.
/// </summary>
/// <remarks>
/// <para>
/// The fallback resolver: it maps any type, including one from an assembly you do not control
/// and one built at run time, with no attributes and no code generation. The contract it
/// produces is cached per type and per options instance, so the cost is paid once.
/// </para>
/// <para>
/// <strong>Where member order comes from</strong>, in priority:
/// </para>
/// <list type="number">
///   <item><description><see cref="SecsItemAttribute.Order"/>, when any member has one.</description></item>
///   <item><description>
///     Constructor parameter order, when the type's only usable constructor takes parameters
///     whose names all match mapped members. This is what makes a positional
///     <see langword="record"/> exact: its parameter list <em>is</em> its declaration order,
///     and the runtime does guarantee that order.
///   </description></item>
///   <item><description>
///     Metadata order, base-declared members first. Correct in practice on every runtime this
///     library targets, but not something the CLI specification promises - a type split across
///     partial files is the case where it can surprise you. That is the gap the source
///     generator closes, and why analyzer BSG001 points at these types.
///   </description></item>
/// </list>
/// </remarks>
public sealed class ReflectionSecsTypeInfoResolver : ISecsTypeInfoResolver
{
    private readonly ConcurrentDictionary<(Type Type, SecsSerializerOptions Options), SecsTypeInfo?> _cache = new();

    /// <inheritdoc/>
    public SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);

        return _cache.GetOrAdd((type, options), static key => Build(key.Type, key.Options));
    }

    private static SecsTypeInfo? Build(Type type, SecsSerializerOptions options)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsArray || type.IsAbstract || type.IsInterface)
        {
            return null;
        }

        var members = CollectMembers(type, options);
        var constructor = ChooseConstructor(type);
        var parameters = constructor?.GetParameters() ?? [];

        AssignOrder(type, members, parameters, options);

        var properties = members
            .OrderBy(static m => m.Order)
            .ThenBy(static m => m.Fallback)
            .Select(static m => m.Info)
            .ToArray();

        var parameterless = type.GetConstructor(Type.EmptyTypes);

        return new SecsTypeInfo
        {
            Type = type,
            Message = type.GetCustomAttribute<SecsMessageAttribute>(inherit: true),
            Properties = properties,
            CreateInstance = parameterless is not null
                ? () => parameterless.Invoke(null)
                : type.IsValueType
                    ? () => Activator.CreateInstance(type)!
                    : null,
            CreateWithArguments = parameters.Length > 0 && constructor is not null
                ? args => constructor.Invoke(args)
                : null,
            ConstructorParameterCount = parameters.Length,
        };
    }

    private static List<MutableMember> CollectMembers(Type type, SecsSerializerOptions options)
    {
        var members = new List<MutableMember>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null
                || property.GetIndexParameters().Length > 0
                || property.GetCustomAttribute<SecsIgnoreAttribute>(inherit: true) is not null)
            {
                continue;
            }

            members.Add(Describe(
                property.Name,
                property.PropertyType,
                property.GetCustomAttribute<SecsItemAttribute>(inherit: true),
                ResolveConverter(property.GetCustomAttribute<SecsConverterAttribute>(inherit: true), property.GetCustomAttribute<SecsItemAttribute>(inherit: true)),
                instance => property.GetValue(instance),
                property.SetMethod is not null ? (instance, value) => property.SetValue(instance, value) : null,
                Depth(type, property.DeclaringType),
                property.MetadataToken,
                options));
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsInitOnly || field.GetCustomAttribute<SecsIgnoreAttribute>(inherit: true) is not null)
            {
                continue;
            }

            members.Add(Describe(
                field.Name,
                field.FieldType,
                field.GetCustomAttribute<SecsItemAttribute>(inherit: true),
                ResolveConverter(field.GetCustomAttribute<SecsConverterAttribute>(inherit: true), field.GetCustomAttribute<SecsItemAttribute>(inherit: true)),
                instance => field.GetValue(instance),
                (instance, value) => field.SetValue(instance, value),
                Depth(type, field.DeclaringType),
                field.MetadataToken,
                options));
        }

        return members;
    }

    private static MutableMember Describe(
        string name,
        Type memberType,
        SecsItemAttribute? attribute,
        ISecsConverter? converter,
        Func<object, object?> get,
        Action<object, object?>? set,
        int depth,
        int metadataToken,
        SecsSerializerOptions options)
        => new()
        {
            Order = attribute is { Order: >= 0 } ? attribute.Order : int.MaxValue,
            Fallback = (depth * 1_000_000L) + metadataToken,
            Info = new SecsPropertyInfo
            {
                Name = name,
                PropertyType = memberType,
                Order = attribute is { Order: >= 0 } ? attribute.Order : 0,
                Format = attribute?.Format,
                Label = attribute?.Name ?? (options.WritePropertyLabels ? name : null),
                FixedLength = attribute?.Length ?? 0,
                Padding = attribute?.Padding ?? ' ',
                AsList = attribute?.AsList ?? false,
                Optional = attribute?.Optional ?? false,
                Converter = converter,
                Get = get,
                Set = set,
            },
        };

    private static ISecsConverter? ResolveConverter(SecsConverterAttribute? converterAttribute, SecsItemAttribute? itemAttribute)
    {
        var converterType = itemAttribute?.Converter ?? converterAttribute?.ConverterType;
        if (converterType is null)
        {
            return null;
        }

        return Activator.CreateInstance(converterType) as ISecsConverter
            ?? throw new SecsMappingException($"{converterType.Name} does not implement {nameof(ISecsConverter)} or has no public parameterless constructor.");
    }

    /// <summary>
    /// Picks the constructor to build instances with: a parameterless one if there is one,
    /// otherwise the widest.
    /// </summary>
    private static ConstructorInfo? ChooseConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length == 0)
        {
            return null;
        }

        var parameterless = Array.Find(constructors, static c => c.GetParameters().Length == 0);
        if (parameterless is not null)
        {
            return parameterless;
        }

        return constructors
            .OrderByDescending(static c => c.GetParameters().Length)
            .First();
    }

    private static void AssignOrder(
        Type type,
        List<MutableMember> members,
        ParameterInfo[] parameters,
        SecsSerializerOptions options)
    {
        var anyExplicit = members.Exists(static m => m.Order != int.MaxValue);

        if (options.PropertyOrdering == SecsPropertyOrdering.Explicit)
        {
            var missing = members.Find(static m => m.Order == int.MaxValue);
            if (missing is not null)
            {
                throw new SecsMappingException(
                    $"{type.Name}.{missing.Info.Name} has no [SecsItem(Order = ...)], and {nameof(SecsSerializerOptions.PropertyOrdering)} is {nameof(SecsPropertyOrdering.Explicit)}.");
            }

            return;
        }

        if (anyExplicit)
        {
            return;
        }

        if (parameters.Length == 0)
        {
            // Metadata order. Fallback already encodes "base-declared first, then metadata token".
            for (var i = 0; i < members.Count; i++)
            {
                members[i].Order = 0;
            }

            return;
        }

        // Constructor parameter order, when every parameter names a mapped member. That is the
        // shape of a positional record, and the one case where the runtime guarantees the order.
        var byName = new Dictionary<string, MutableMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            byName[member.Info.Name] = member;
        }

        var matched = new List<(MutableMember Member, int Index)>(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterName = parameters[i].Name;
            if (parameterName is null || !byName.TryGetValue(parameterName, out var member))
            {
                matched.Clear();
                break;
            }

            matched.Add((member, i));
        }

        if (matched.Count != parameters.Length)
        {
            for (var i = 0; i < members.Count; i++)
            {
                members[i].Order = 0;
            }

            return;
        }

        foreach (var (member, index) in matched)
        {
            member.Order = index;
            member.Info = new SecsPropertyInfo
            {
                Name = member.Info.Name,
                PropertyType = member.Info.PropertyType,
                Order = index,
                Format = member.Info.Format,
                Label = member.Info.Label,
                FixedLength = member.Info.FixedLength,
                Padding = member.Info.Padding,
                AsList = member.Info.AsList,
                Optional = member.Info.Optional,
                Converter = member.Info.Converter,
                Get = member.Info.Get,
                Set = member.Info.Set,
                ConstructorParameterIndex = index,
            };
        }

        // Members that are not constructor parameters sort after the ones that are.
        foreach (var member in members)
        {
            if (member.Info.ConstructorParameterIndex < 0)
            {
                member.Order = parameters.Length + 1;
            }
        }
    }

    /// <summary>Counts how far <paramref name="declaring"/> sits above <paramref name="type"/>, so base members sort first.</summary>
    private static int Depth(Type type, Type? declaring)
    {
        if (declaring is null || declaring == type)
        {
            return int.MaxValue / 2;
        }

        var depth = 0;
        for (var current = type; current is not null && current != declaring; current = current.BaseType)
        {
            depth++;
        }

        return (int.MaxValue / 2) - depth;
    }

    private sealed class MutableMember
    {
        public int Order { get; set; }

        public long Fallback { get; init; }

        public SecsPropertyInfo Info { get; set; } = null!;
    }
}
