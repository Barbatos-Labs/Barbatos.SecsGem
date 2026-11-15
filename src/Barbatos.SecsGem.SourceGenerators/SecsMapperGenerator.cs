// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Barbatos.SecsGem.SourceGenerators;

/// <summary>
/// Emits a mapping contract for every type the mapper will be asked about, read from the syntax
/// tree rather than from reflection.
/// </summary>
/// <remarks>
/// <para><strong>What it buys.</strong></para>
/// <list type="bullet">
///   <item><description>
///     <strong>Exact order.</strong> A SECS-II item list is positional, and the mapper takes its
///     positions from declaration order. Reflection reads metadata order, which matches
///     declaration order on every runtime this library targets and is not promised by the CLI
///     specification. Here the order comes from the syntax, which is the thing the programmer
///     actually wrote.
///   </description></item>
///   <item><description>
///     <strong>No reflection.</strong> Accessors are direct delegates and constructors are direct
///     calls, so a mapped type survives trimming and Native AOT without a
///     <c>DynamicallyAccessedMembers</c> annotation anywhere.
///   </description></item>
/// </list>
/// <para><strong>Which types it finds.</strong></para>
/// <list type="number">
///   <item><description>Anything carrying <c>[SecsMessage]</c>.</description></item>
///   <item><description>Anything with a member carrying <c>[SecsItem]</c> or <c>[SecsIgnore]</c>.</description></item>
///   <item><description>
///     Every <c>T</c> in a direct <c>SecsSerializer.Serialize&lt;T&gt;</c>,
///     <c>Deserialize&lt;T&gt;</c>, <c>ToMessage&lt;T&gt;</c> or <c>FromMessage&lt;T&gt;</c> call -
///     which is what keeps the promise that a plain record with no attributes at all is mapped
///     from its syntax.
///   </description></item>
///   <item><description>
///     Everything reachable from those through mapped members, unwrapping arrays, nullables and
///     sequences. A message type is rarely alone, and generating a contract for the message while
///     leaving its reports to reflection would cover the shallowest layer of exactly the types
///     that need it most.
///   </description></item>
/// </list>
/// <para>
/// Anything else still works: <c>SecsTypeInfoResolver.Default</c> asks the generated resolver
/// first and falls back to reflection, so a type from another assembly, a generic one, a struct or
/// one reached only through <c>Serialize(object, Type)</c> is mapped exactly as it was before this
/// generator existed. That is what makes the generator an optimisation rather than a requirement.
/// </para>
/// <para>
/// <strong>On incrementality.</strong> The models are turned into text inside each transform, so
/// no symbol crosses a pipeline boundary. The final node collects everything to remove the
/// duplicates two overlapping closures produce, which costs fine-grained caching - a fair trade
/// for a handful of data types, and the thing to revisit first if it ever is not.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SecsMapperGenerator : IIncrementalGenerator
{
    private const string GeneratedNamespace = "Barbatos.SecsGem.Mapping.Generated";
    private const string ToolName = "Barbatos.SecsGem.SourceGenerators";

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fromMessageAttribute = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MappedType.MessageAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (target, _) => Build(target.TargetSymbol as INamedTypeSymbol));

        var fromItemAttribute = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MappedType.ItemAttribute,
                static (node, _) => node is MemberDeclarationSyntax,
                static (target, _) => Build(target.TargetSymbol.ContainingType));

        var fromIgnoreAttribute = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MappedType.IgnoreAttribute,
                static (node, _) => node is MemberDeclarationSyntax,
                static (target, _) => Build(target.TargetSymbol.ContainingType));

        var fromSerializerCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (syntax, token) => BuildFromCall(syntax, token));

        var everything = fromMessageAttribute
            .Collect()
            .Combine(fromItemAttribute.Collect())
            .Combine(fromIgnoreAttribute.Collect())
            .Combine(fromSerializerCalls.Collect());

        context.RegisterSourceOutput(everything, static (production, sets) =>
        {
            var (((messages, items), ignores), calls) = sets;

            var emitted = new HashSet<string>(System.StringComparer.Ordinal);

            Add(messages);
            Add(items);
            Add(ignores);
            Add(calls);

            void Add(ImmutableArray<ImmutableArray<GeneratedContract>> groups)
            {
                foreach (var group in groups)
                {
                    foreach (var contract in group)
                    {
                        // Two roots whose closures overlap describe the same type twice, with the
                        // same text. The first wins; a second file of the same name would not
                        // compile.
                        if (emitted.Add(contract.HintName))
                        {
                            production.AddSource(contract.HintName, contract.Source);
                        }
                    }
                }
            }
        });
    }

    private static ImmutableArray<GeneratedContract> Build(INamedTypeSymbol? root)
    {
        if (root is null)
        {
            return ImmutableArray<GeneratedContract>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GeneratedContract>();

        foreach (var mapped in MappedType.Closure(root))
        {
            builder.Add(Emit(mapped));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<GeneratedContract> BuildFromCall(GeneratorSyntaxContext syntax, System.Threading.CancellationToken token)
    {
        if (syntax.Node is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax access)
        {
            return ImmutableArray<GeneratedContract>.Empty;
        }

        var name = access.Name.Identifier.ValueText;
        if (name is not ("Serialize" or "Deserialize" or "ToMessage" or "FromMessage"))
        {
            return ImmutableArray<GeneratedContract>.Empty;
        }

        if (syntax.SemanticModel.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method
            || method.ContainingType?.ToDisplayString() != MappedType.SerializerType
            || method.TypeArguments.Length != 1)
        {
            return ImmutableArray<GeneratedContract>.Empty;
        }

        return Build(method.TypeArguments[0] as INamedTypeSymbol);
    }

    private static GeneratedContract Emit(MappedType mapped)
    {
        var type = mapped.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var identifier = Sanitize(mapped.Symbol.ToDisplayString());
        var ordered = Order(mapped);

        var source = new StringBuilder(2048);

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("// Generated by " + ToolName + ". Member order is declaration order,");
        source.AppendLine("// taken from the syntax tree rather than from reflection metadata.");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("namespace " + GeneratedNamespace);
        source.AppendLine("{");
        source.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"" + ToolName + "\", null)]");
        source.AppendLine("    [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
        source.AppendLine("    internal static class SecsTypeInfoFor_" + identifier);
        source.AppendLine("    {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Register()");
        source.AppendLine("            => global::" + MappedType.ResolverType + ".Generated.Register(typeof(" + type + "), Create);");
        source.AppendLine();
        source.AppendLine("        private static global::Barbatos.SecsGem.Mapping.SecsTypeInfo Create(");
        source.AppendLine("            global::Barbatos.SecsGem.Mapping.SecsSerializerOptions options)");
        source.AppendLine("        {");
        source.AppendLine("            return new global::Barbatos.SecsGem.Mapping.SecsTypeInfo");
        source.AppendLine("            {");
        source.AppendLine("                Type = typeof(" + type + "),");
        source.AppendLine("                Message = " + MessageExpression(mapped) + ",");
        source.AppendLine("                Properties = new global::Barbatos.SecsGem.Mapping.SecsPropertyInfo[]");
        source.AppendLine("                {");

        foreach (var member in ordered)
        {
            AppendProperty(source, type, member.Member, member.Order);
        }

        source.AppendLine("                },");
        source.AppendLine("                CreateInstance = " + CreateInstanceExpression(mapped, type) + ",");
        source.AppendLine("                CreateWithArguments = " + CreateWithArgumentsExpression(mapped, type) + ",");
        source.AppendLine("                ConstructorParameterCount = " + (mapped.Constructor?.Parameters.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        source.AppendLine("            };");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        return new GeneratedContract("SecsTypeInfo_" + identifier + ".g.cs", source.ToString());
    }

    private static void AppendProperty(StringBuilder source, string type, MappedMember member, int order)
    {
        var memberType = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var converter = MappedType.ReadType(member.Item, "Converter")
            ?? MappedType.ReadType(member.Converter, "ConverterType")
            ?? (member.Converter?.ConstructorArguments.Length > 0 ? member.Converter.ConstructorArguments[0].Value as INamedTypeSymbol : null);

        var format = MappedType.ReadNamedFormat(member.Item);
        var label = MappedType.ReadString(member.Item, "Name");

        source.AppendLine("                    new global::Barbatos.SecsGem.Mapping.SecsPropertyInfo");
        source.AppendLine("                    {");
        source.AppendLine("                        Name = " + Literal(member.Name) + ",");
        source.AppendLine("                        PropertyType = typeof(" + memberType + "),");
        source.AppendLine("                        Order = " + order.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        source.AppendLine("                        Format = " + (format is null ? "null" : "global::Barbatos.SecsGem.SecsFormat." + format) + ",");
        source.AppendLine("                        Label = " + (label is null
            ? "options.WritePropertyLabels ? " + Literal(member.Name) + " : null"
            : Literal(label)) + ",");
        source.AppendLine("                        FixedLength = " + MappedType.ReadInt(member.Item, "Length", 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        source.AppendLine("                        Padding = " + CharLiteral(MappedType.ReadChar(member.Item, "Padding", ' ')) + ",");
        source.AppendLine("                        AsList = " + (MappedType.ReadBool(member.Item, "AsList", false) ? "true" : "false") + ",");
        source.AppendLine("                        Optional = " + (MappedType.ReadBool(member.Item, "Optional", false) ? "true" : "false") + ",");
        source.AppendLine("                        Converter = " + (converter is null
            ? "null"
            : "new " + converter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "()") + ",");
        source.AppendLine("                        Get = static instance => ((" + type + ")instance)." + member.Name + ",");
        source.AppendLine("                        Set = " + (member.Settable
            ? "static (instance, value) => ((" + type + ")instance)." + member.Name + " = (" + memberType + ")value!"
            : "null") + ",");
        source.AppendLine("                        ConstructorParameterIndex = " + member.ConstructorParameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        source.AppendLine("                    },");
    }

    /// <summary>
    /// Works out the final item order, mirroring the reflection resolver exactly.
    /// </summary>
    /// <remarks>
    /// The two resolvers producing different orders for one type would be the worst possible bug in
    /// this design - a message that is correct until somebody adds the generator package - so this
    /// deliberately reproduces the reflection resolver's rules rather than improving on them.
    /// </remarks>
    private static List<(MappedMember Member, int Order)> Order(MappedType mapped)
    {
        var ordered = new List<(MappedMember Member, int Order, int Sort, int Declaration)>(mapped.Members.Count);
        var parameterCount = mapped.Constructor?.Parameters.Length ?? 0;
        var anyExplicit = mapped.AnyExplicitOrder;

        for (var index = 0; index < mapped.Members.Count; index++)
        {
            var member = mapped.Members[index];
            var explicitOrder = member.ExplicitOrder;

            int order;
            int sort;

            if (anyExplicit)
            {
                order = explicitOrder >= 0 ? explicitOrder : 0;
                sort = explicitOrder >= 0 ? explicitOrder : int.MaxValue;
            }
            else if (member.ConstructorParameterIndex >= 0)
            {
                order = member.ConstructorParameterIndex;
                sort = member.ConstructorParameterIndex;
            }
            else
            {
                order = 0;
                sort = parameterCount > 0 ? parameterCount + 1 : 0;
            }

            ordered.Add((member, order, sort, index));
        }

        ordered.Sort((left, right) => left.Sort != right.Sort
            ? left.Sort.CompareTo(right.Sort)
            : left.Declaration.CompareTo(right.Declaration));

        return ordered.ConvertAll(entry => (entry.Member, entry.Order));
    }

    private static string MessageExpression(MappedType mapped)
    {
        var message = mapped.Message;
        if (message is null)
        {
            return "null";
        }

        var stream = message.ConstructorArguments.Length == 2 && message.ConstructorArguments[0].Value is byte s
            ? s
            : (byte)MappedType.ReadInt(message, "S", 0);

        var function = message.ConstructorArguments.Length == 2 && message.ConstructorArguments[1].Value is byte f
            ? f
            : (byte)MappedType.ReadInt(message, "F", 0);

        var builder = new StringBuilder();
        builder.Append("new global::Barbatos.SecsGem.Mapping.SecsMessageAttribute(")
            .Append(stream.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(", ")
            .Append(function.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(")");

        var name = MappedType.ReadString(message, "Name");
        var reply = MappedType.ReadNamedBool(message, "ReplyExpected");

        if (name is not null || reply is not null)
        {
            builder.Append(" { ");

            if (name is not null)
            {
                builder.Append("Name = ").Append(Literal(name));
                if (reply is not null)
                {
                    builder.Append(", ");
                }
            }

            if (reply is not null)
            {
                builder.Append("ReplyExpected = ").Append(reply.Value ? "true" : "false");
            }

            builder.Append(" }");
        }

        return builder.ToString();
    }

    private static string CreateInstanceExpression(MappedType mapped, string type)
        => mapped.HasParameterlessConstructor ? "static () => new " + type + "()" : "null";

    private static string CreateWithArgumentsExpression(MappedType mapped, string type)
    {
        if (mapped.Constructor is null)
        {
            return "null";
        }

        var builder = new StringBuilder();
        builder.Append("static args => new ").Append(type).Append('(');

        for (var index = 0; index < mapped.Constructor.Parameters.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var parameterType = mapped.Constructor.Parameters[index].Type
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            builder.Append('(').Append(parameterType).Append(")args[")
                .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("]!");
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string Literal(string value) => "\"" + value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n") + "\"";

    private static string CharLiteral(char value) => value switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        '\r' => "'\\r'",
        '\n' => "'\\n'",
        '\t' => "'\\t'",
        _ => "'" + value + "'",
    };

    private static string Sanitize(string displayName)
    {
        var builder = new StringBuilder(displayName.Length);

        foreach (var character in displayName)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>One generated file: the name it is added under, and its text.</summary>
    /// <remarks>
    /// A readonly struct of two strings, so the pipeline compares it by value and no symbol is
    /// held across a step.
    /// </remarks>
    private readonly struct GeneratedContract
    {
        public GeneratedContract(string hintName, string source)
        {
            HintName = hintName;
            Source = source;
        }

        public string HintName { get; }

        public string Source { get; }
    }
}
