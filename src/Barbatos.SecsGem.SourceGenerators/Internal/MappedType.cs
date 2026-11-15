// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Barbatos.SecsGem.SourceGenerators;

/// <summary>
/// One mapped member, read from the syntax tree.
/// </summary>
internal sealed class MappedMember
{
    public MappedMember(ISymbol symbol, ITypeSymbol type, bool settable, AttributeData? item, AttributeData? converter)
    {
        Symbol = symbol;
        Type = type;
        Settable = settable;
        Item = item;
        Converter = converter;
    }

    public ISymbol Symbol { get; }

    public ITypeSymbol Type { get; }

    /// <summary>Gets a value indicating whether the member can be written after construction.</summary>
    public bool Settable { get; }

    public AttributeData? Item { get; }

    public AttributeData? Converter { get; }

    public string Name => Symbol.Name;

    /// <summary>Gets this member's index in the chosen constructor's parameter list, or -1.</summary>
    public int ConstructorParameterIndex { get; set; } = -1;

    /// <summary>Gets the explicit order from <c>[SecsItem(Order = ...)]</c>, or -1 when there is none.</summary>
    public int ExplicitOrder => MappedType.ReadInt(Item, "Order", -1);
}

/// <summary>
/// A type the generator will emit a mapping contract for.
/// </summary>
/// <remarks>
/// <para>
/// Everything here comes from the symbol, which means from the syntax tree, which means member
/// order is <em>declaration</em> order and is exact. That is the entire reason this generator
/// exists: reflection reads metadata order, which is right on every runtime this library targets
/// and is not something the CLI specification promises. A type split across partial files is where
/// it stops being right, and a wire format whose field order depends on which file the compiler
/// happened to read first is not a wire format.
/// </para>
/// <para>
/// <strong>Reference types only.</strong> A struct's mapped members would be written through a
/// boxed copy, and whether that copy makes it back out is a question the two resolvers could
/// answer differently. Two resolvers that differ in behaviour is the worst bug this design could
/// have, so structs are left to reflection, where that behaviour is already settled.
/// </para>
/// </remarks>
internal sealed class MappedType
{
    public const string MessageAttribute = "Barbatos.SecsGem.Mapping.SecsMessageAttribute";
    public const string ItemAttribute = "Barbatos.SecsGem.Mapping.SecsItemAttribute";
    public const string IgnoreAttribute = "Barbatos.SecsGem.Mapping.SecsIgnoreAttribute";
    public const string ConverterAttribute = "Barbatos.SecsGem.Mapping.SecsConverterAttribute";
    public const string ResolverType = "Barbatos.SecsGem.Mapping.SecsTypeInfoResolver";
    public const string SecsItemType = "Barbatos.SecsGem.SecsItem";
    public const string SerializerType = "Barbatos.SecsGem.Mapping.SecsSerializer";

    private MappedType(INamedTypeSymbol symbol, IReadOnlyList<MappedMember> members, IMethodSymbol? constructor, bool parameterless)
    {
        Symbol = symbol;
        Members = members;
        Constructor = constructor;
        HasParameterlessConstructor = parameterless;
    }

    public INamedTypeSymbol Symbol { get; }

    /// <summary>Gets the mapped members, in declaration order.</summary>
    public IReadOnlyList<MappedMember> Members { get; }

    /// <summary>Gets the constructor instances are built with, when one takes parameters.</summary>
    public IMethodSymbol? Constructor { get; }

    public bool HasParameterlessConstructor { get; }

    public AttributeData? Message => Find(Symbol.GetAttributes(), MessageAttribute);

    /// <summary>Gets a value indicating whether any member pins its own order.</summary>
    public bool AnyExplicitOrder => Members.Any(member => member.ExplicitOrder >= 0);

    /// <summary>Describes a type, or returns <see langword="null"/> when the generator will not map it.</summary>
    /// <param name="symbol">The candidate type.</param>
    /// <returns>The model, or <see langword="null"/>.</returns>
    public static MappedType? TryCreate(INamedTypeSymbol? symbol)
    {
        if (!IsMappable(symbol))
        {
            return null;
        }

        var members = CollectMembers(symbol!);
        if (members.Count == 0)
        {
            return null;
        }

        var constructor = ChooseConstructor(symbol!);
        var parameterless = symbol!.InstanceConstructors.Any(
            candidate => candidate.Parameters.Length == 0 && candidate.DeclaredAccessibility == Accessibility.Public);

        MatchConstructorParameters(members, constructor);

        // An init-only member that no constructor parameter covers is the one shape where
        // generated code and reflection would disagree: reflection can call an init accessor,
        // generated code cannot. Leave the whole type to reflection rather than ship two
        // resolvers that answer differently.
        foreach (var member in members)
        {
            if (member.ConstructorParameterIndex < 0 && IsInitOnly(member))
            {
                return null;
            }
        }

        return new MappedType(symbol!, members, constructor?.Parameters.Length > 0 ? constructor : null, parameterless);
    }

    private static bool IsInitOnly(MappedMember member)
        => member.Symbol is IPropertySymbol { SetMethod.IsInitOnly: true };

    /// <summary>Gets a value indicating whether the generator can map a type at all.</summary>
    /// <param name="symbol">The candidate.</param>
    /// <returns><see langword="true"/> when it is a non-generic reference type declared in this compilation.</returns>
    /// <remarks>
    /// <para>
    /// Generic types are excluded because a contract is registered against one closed
    /// <see cref="Type"/>, and the generator cannot know which closures a program will use.
    /// Reflection handles them, which is what the fallback is for.
    /// </para>
    /// <para>
    /// So are types from other assemblies: a contract is built from declaration order, and
    /// declaration order comes from syntax this compilation does not have.
    /// </para>
    /// </remarks>
    public static bool IsMappable(INamedTypeSymbol? symbol) =>
        symbol is not null
        && symbol.TypeKind == TypeKind.Class
        && !symbol.IsAbstract
        && !symbol.IsStatic
        && !symbol.IsAnonymousType
        && !symbol.IsGenericType
        && symbol.SpecialType == SpecialType.None
        && symbol.DeclaringSyntaxReferences.Length > 0
        && symbol.ToDisplayString() != SecsItemType
        && symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    /// <summary>Walks the member types a mapped type refers to, so they get contracts too.</summary>
    /// <param name="root">The type to start from.</param>
    /// <returns>Every mappable type reachable from it, including itself, each once.</returns>
    /// <remarks>
    /// A message type is rarely alone: an S6F11 holds a list of reports, and each report holds a
    /// list of values. Generating a contract for the message and leaving its parts to reflection
    /// would make the generator cover the shallowest layer of exactly the types that need it most.
    /// </remarks>
    public static IReadOnlyList<MappedType> Closure(INamedTypeSymbol? root)
    {
        var found = new List<MappedType>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();

        if (root is not null)
        {
            queue.Enqueue(root);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            var mapped = TryCreate(current);
            if (mapped is null)
            {
                continue;
            }

            found.Add(mapped);

            foreach (var member in mapped.Members)
            {
                var referenced = Unwrap(member.Type);
                if (referenced is INamedTypeSymbol named && IsMappable(named) && !seen.Contains(named))
                {
                    queue.Enqueue(named);
                }
            }
        }

        return found;
    }

    /// <summary>Reads an integer from a named attribute argument.</summary>
    public static int ReadInt(AttributeData? attribute, string name, int fallback)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value is int number ? number : fallback;
    }

    /// <summary>Reads a boolean from a named attribute argument.</summary>
    public static bool ReadBool(AttributeData? attribute, string name, bool fallback)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value is bool flag ? flag : fallback;
    }

    /// <summary>Reads a string from a named attribute argument.</summary>
    public static string? ReadString(AttributeData? attribute, string name)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value as string;
    }

    /// <summary>Reads a char from a named attribute argument.</summary>
    public static char ReadChar(AttributeData? attribute, string name, char fallback)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value is char character ? character : fallback;
    }

    /// <summary>Reads a type from a named attribute argument.</summary>
    public static INamedTypeSymbol? ReadType(AttributeData? attribute, string name)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value as INamedTypeSymbol;
    }

    /// <summary>Reads a boolean that may be absent, for an attribute property of type <c>bool?</c>.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <param name="name">The argument name.</param>
    /// <returns>The value, or <see langword="null"/> when it was not set.</returns>
    /// <remarks>
    /// <c>SecsMessageAttribute.ReplyExpected</c> is a <c>bool?</c> whose unset state means "work it
    /// out from the function number", so the three states have to be kept apart.
    /// </remarks>
    public static bool? ReadNamedBool(AttributeData? attribute, string name)
    {
        var value = ReadNamed(attribute, name);
        return value?.Value is bool flag ? flag : null;
    }

    /// <summary>Reads the SECS-II format an item attribute pins, as the enum member's name.</summary>
    /// <param name="attribute">The <c>[SecsItem]</c> attribute, or <see langword="null"/>.</param>
    /// <returns>The member name, for example <c>U4</c>, or <see langword="null"/> when no format was pinned.</returns>
    /// <remarks>
    /// The name rather than the number, so the generated code reads
    /// <c>SecsFormat.U4</c> and stays readable - and so a format whose numeric value changed would
    /// break the build rather than quietly mean something else.
    /// </remarks>
    public static string? ReadNamedFormat(AttributeData? attribute)
    {
        if (attribute is null)
        {
            return null;
        }

        // SecsItemAttribute(SecsFormat) sets it positionally; the property sets it by name.
        var constant = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0]
            : ReadNamed(attribute, "Format");

        if (constant is null || constant.Value.Value is null)
        {
            return null;
        }

        var enumType = constant.Value.Type as INamedTypeSymbol;
        if (enumType?.TypeKind != TypeKind.Enum)
        {
            // A nullable SecsFormat? arrives as the underlying enum once it has a value; anything
            // else is not a format and is better left inferred than guessed at.
            enumType = (constant.Value.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        }

        if (enumType?.TypeKind != TypeKind.Enum)
        {
            return null;
        }

        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { HasConstantValue: true } field
                && Equals(field.ConstantValue, constant.Value.Value))
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>Finds an attribute by its fully qualified metadata name.</summary>
    public static AttributeData? Find(IEnumerable<AttributeData> attributes, string metadataName)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() == metadataName)
            {
                return attribute;
            }
        }

        return null;
    }

    private static TypedConstant? ReadNamed(AttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return null;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
            {
                return argument.Value;
            }
        }

        return null;
    }

    /// <summary>Collects mapped members in declaration order, base-declared first.</summary>
    private static List<MappedMember> CollectMembers(INamedTypeSymbol symbol)
    {
        var levels = new List<INamedTypeSymbol>();
        for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            levels.Insert(0, current);
        }

        var members = new List<MappedMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var level in levels)
        {
            // GetMembers() returns members in source order within one declaration, which is the
            // whole point. For a partial class the order across parts is the order the compiler
            // read the files in, which is why BSG001 exists rather than a guess being made here.
            foreach (var member in level.GetMembers())
            {
                if (member.DeclaredAccessibility != Accessibility.Public
                    || member.IsStatic
                    || Find(member.GetAttributes(), IgnoreAttribute) is not null)
                {
                    continue;
                }

                ITypeSymbol type;
                bool settable;

                switch (member)
                {
                    case IPropertySymbol property when property.GetMethod is not null && property.Parameters.Length == 0:
                        type = property.Type;

                        // An init-only setter cannot be called from generated code outside an
                        // object initialiser, while reflection can call it. Rather than let the
                        // two resolvers disagree about whether such a member is writable, a type
                        // that has one which no constructor parameter covers is left to
                        // reflection entirely - see TryCreate.
                        settable = property.SetMethod is { IsInitOnly: false };
                        break;

                    case IFieldSymbol field when !field.IsReadOnly && !field.IsConst && field.AssociatedSymbol is null:
                        type = field.Type;
                        settable = true;
                        break;

                    default:
                        continue;
                }

                if (!seen.Add(member.Name))
                {
                    continue;
                }

                members.Add(new MappedMember(
                    member,
                    type,
                    settable,
                    Find(member.GetAttributes(), ItemAttribute),
                    Find(member.GetAttributes(), ConverterAttribute)));
            }
        }

        return members;
    }

    /// <summary>Picks the constructor instances are built with: parameterless if there is one, otherwise the widest.</summary>
    private static IMethodSymbol? ChooseConstructor(INamedTypeSymbol symbol)
    {
        IMethodSymbol? widest = null;

        foreach (var constructor in symbol.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (constructor.Parameters.Length == 0)
            {
                return constructor;
            }

            if (widest is null || constructor.Parameters.Length > widest.Parameters.Length)
            {
                widest = constructor;
            }
        }

        return widest;
    }

    /// <summary>
    /// Matches constructor parameters to members by name, which is what makes a positional record
    /// exact: its parameter list <em>is</em> its declaration order.
    /// </summary>
    private static void MatchConstructorParameters(List<MappedMember> members, IMethodSymbol? constructor)
    {
        if (constructor is null || constructor.Parameters.Length == 0)
        {
            return;
        }

        var byName = new Dictionary<string, MappedMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            byName[member.Name] = member;
        }

        // The declaration index is captured before anything is reordered: using IndexOf inside the
        // comparison would be reading a list that the sort is in the middle of rearranging.
        var declarationIndex = new Dictionary<MappedMember, int>();
        for (var index = 0; index < members.Count; index++)
        {
            declarationIndex[members[index]] = index;
        }

        for (var index = 0; index < constructor.Parameters.Length; index++)
        {
            if (!byName.TryGetValue(constructor.Parameters[index].Name, out var member))
            {
                // One parameter that is not a mapped member and the whole match is off, so the
                // constructor is not used for ordering at all - exactly as the reflection resolver
                // decides it.
                foreach (var candidate in members)
                {
                    candidate.ConstructorParameterIndex = -1;
                }

                return;
            }

            member.ConstructorParameterIndex = index;
        }

        // The constructor's parameters come first, in their own order; anything else keeps its
        // declaration order behind them.
        var parameterCount = constructor.Parameters.Length;
        members.Sort((left, right) => Rank(left).CompareTo(Rank(right)));

        int Rank(MappedMember member) => member.ConstructorParameterIndex >= 0
            ? member.ConstructorParameterIndex
            : parameterCount + declarationIndex[member];
    }

    /// <summary>Unwraps a nullable, an array or a sequence down to the type the mapper will walk into.</summary>
    private static ITypeSymbol? Unwrap(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return Unwrap(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.IsGenericType && named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            {
                return Unwrap(named.TypeArguments[0]);
            }

            if (named.IsGenericType && named.TypeArguments.Length == 1 && named.AllInterfaces
                .Any(candidate => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T))
            {
                return Unwrap(named.TypeArguments[0]);
            }
        }

        return type;
    }
}
