// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Barbatos.SecsGem.SourceGenerators;

/// <summary>
/// Reports the one mapping shape whose wire order is not well defined.
/// </summary>
/// <remarks>
/// <para>
/// A mapped type gets its item order from one of two places: <c>[SecsItem(Order = ...)]</c> on
/// every member, or declaration order on none of them. Both are exact.
/// </para>
/// <para>
/// A type that does half of each is not. The reflection resolver puts the ordered members first,
/// in their order, and then the rest in metadata order; the source generator puts the rest in
/// syntax order. Those agree today on the runtimes this library targets, and they are two
/// different rules - so the day they stop agreeing, the symptom is a message that was correct
/// until somebody installed a package. That is what this reports.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SecsMapperAnalyzer : DiagnosticAnalyzer
{
    /// <summary>BSG001: some mapped members pin their order and others do not.</summary>
    public static readonly DiagnosticDescriptor PartialOrdering = new(
        id: "BSG001",
        title: "Order is set on some mapped members but not all",
        messageFormat: "'{0}.{1}' has no [SecsItem(Order = ...)] while other mapped members of '{0}' do; set it on every mapped member or on none",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A SECS-II item list is positional. Set Order on every mapped member and the positions are "
            + "exactly what you wrote; set it on none and they are declaration order, which is also exact. "
            + "Set it on some and the unordered members fall back to a different rule in each resolver - "
            + "metadata order under reflection, syntax order under the source generator - so the item list "
            + "can change the day the generator package is added.",
        helpLinkUri: "https://github.com/Barbatos-Labs/Barbatos.SecsGem/blob/main/src/Barbatos.SecsGem.Mapper/README.md#bsg001");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(PartialOrdering);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return;
        }

        var members = Collect(type);
        if (members.Count < 2)
        {
            // One member has nothing to be out of order with.
            return;
        }

        var ordered = 0;
        foreach (var member in members)
        {
            if (member.Order >= 0)
            {
                ordered++;
            }
        }

        if (ordered == 0 || ordered == members.Count)
        {
            return;
        }

        foreach (var member in members)
        {
            if (member.Order >= 0)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                PartialOrdering,
                member.Symbol.Locations.Length > 0 ? member.Symbol.Locations[0] : Location.None,
                type.Name,
                member.Symbol.Name));
        }
    }

    /// <summary>
    /// Collects the members of a type the mapper would map, and only for a type it would map at all.
    /// </summary>
    /// <remarks>
    /// A type is treated as mapped when it carries <c>[SecsMessage]</c> or has a member carrying
    /// <c>[SecsItem]</c>. Without that test every DTO in the solution would be analysed, and a
    /// warning about SECS-II ordering on a type that has nothing to do with SECS-II is noise that
    /// teaches people to turn warnings off.
    /// </remarks>
    private static List<(ISymbol Symbol, int Order)> Collect(INamedTypeSymbol type)
    {
        var members = new List<(ISymbol Symbol, int Order)>();
        var mapped = MappedType.Find(type.GetAttributes(), MappedType.MessageAttribute) is not null;

        foreach (var member in type.GetMembers())
        {
            if (member.DeclaredAccessibility != Accessibility.Public
                || member.IsStatic
                || MappedType.Find(member.GetAttributes(), MappedType.IgnoreAttribute) is not null)
            {
                continue;
            }

            var isMappable = member switch
            {
                IPropertySymbol property => property.GetMethod is not null && property.Parameters.Length == 0,
                IFieldSymbol field => !field.IsReadOnly && !field.IsConst && field.AssociatedSymbol is null,
                _ => false,
            };

            if (!isMappable)
            {
                continue;
            }

            var item = MappedType.Find(member.GetAttributes(), MappedType.ItemAttribute);
            if (item is not null)
            {
                mapped = true;
            }

            members.Add((member, MappedType.ReadInt(item, "Order", -1)));
        }

        return mapped ? members : [];
    }
}
