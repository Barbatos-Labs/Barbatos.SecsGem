// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Immutable;
using System.Reflection;
using Barbatos.SecsGem.Mapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Barbatos.SecsGem.SourceGenerators.UnitTests;

/// <summary>
/// Compiles a snippet, runs the generator or the analyzer over it, and hands back what happened.
/// </summary>
/// <remarks>
/// A hand-rolled harness rather than <c>Microsoft.CodeAnalysis.Testing</c>, for two reasons. It
/// needs only the Roslyn package the generator already depends on, so there is no second version
/// to keep in step. And it compiles the generator's output against the <em>real</em>
/// <c>Barbatos.SecsGem.Mapper</c> assembly, which is the assertion that matters most: generated
/// code that does not compile is the classic way a source generator fails, and it fails at the
/// consumer's build rather than at ours.
/// </remarks>
internal static class Roslyn
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.None);

    /// <summary>What running the generator over a snippet produced.</summary>
    /// <param name="Sources">The generated files, in the order the generator added them.</param>
    /// <param name="GeneratorDiagnostics">Anything the generator itself reported.</param>
    /// <param name="CompilationErrors">Errors from compiling the snippet together with what was generated.</param>
    internal sealed record GeneratorRun(
        ImmutableArray<string> Sources,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationErrors)
    {
        /// <summary>Gets the one generated file whose text mentions a type, or <see langword="null"/>.</summary>
        /// <param name="typeName">The simple type name, for example <c>Person</c>.</param>
        /// <returns>The file's text, or <see langword="null"/> when nothing was generated for it.</returns>
        public string? For(string typeName)
        {
            foreach (var source in Sources)
            {
                if (source.Contains($"typeof(global::{typeName})") || source.Contains($".{typeName}), Create)"))
                {
                    return source;
                }
            }

            return null;
        }

        /// <summary>Gets a value indicating whether a contract was generated for a type.</summary>
        /// <param name="typeName">The simple or qualified type name.</param>
        /// <returns><see langword="true"/> when one was.</returns>
        public bool Covers(string typeName) => For(typeName) is not null;
    }

    /// <summary>Runs <see cref="SecsMapperGenerator"/> over a snippet.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>What was generated, and whether it compiles.</returns>
    public static GeneratorRun RunGenerator(string source)
    {
        var compilation = Compile(source);

        var driver = CSharpGeneratorDriver
            .Create(new SecsMapperGenerator())
            .WithUpdatedParseOptions(ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(run => run.GeneratedSources)
            .Select(generated => generated.SourceText.ToString())
            .ToImmutableArray();

        var errors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new GeneratorRun(sources, generatorDiagnostics, errors);
    }

    /// <summary>Runs <see cref="SecsMapperAnalyzer"/> over a snippet.</summary>
    /// <param name="source">The C# to analyse.</param>
    /// <returns>Every diagnostic the analyzer reported.</returns>
    public static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string source)
    {
        var compilation = Compile(source);

        // The snippet has to compile before its diagnostics mean anything: an analyzer run over
        // broken code reports whatever the broken code happens to look like.
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Empty(errors);

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new SecsMapperAnalyzer()));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static CSharpCompilation Compile(string source) => CSharpCompilation.Create(
        assemblyName: "Barbatos.SecsGem.Generated.Tests",
        syntaxTrees: [CSharpSyntaxTree.ParseText(source, ParseOptions)],
        references: References,
        options: new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable));

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everything already loaded covers the framework surface, and the two Barbatos assemblies
        // are pinned explicitly so the snippets see the real attributes and contracts.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Add(assembly);
        }

        Add(typeof(SecsItem).Assembly);
        Add(typeof(SecsMessageAttribute).Assembly);

        return builder.ToImmutable();

        void Add(Assembly assembly)
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || !seen.Add(assembly.Location))
            {
                return;
            }

            builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
    }
}
