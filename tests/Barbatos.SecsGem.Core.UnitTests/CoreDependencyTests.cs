// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Reflection;

namespace Barbatos.SecsGem.Core.UnitTests;

/// <summary>
/// What the core assembly is allowed to depend on.
/// </summary>
/// <remarks>
/// <para>
/// Every other package in this repository, and every application built on any of them, sits on
/// top of this one assembly: anything it drags in, a fab drags in. So the working agreement for
/// the repository says it must not reference a socket, a pipe transport or
/// <c>Microsoft.Extensions.*</c>.
/// </para>
/// <para>
/// That rule was broken once, by a single extension method, and it stayed broken through a
/// release's worth of work because nothing checked - the mistake only surfaced when somebody
/// read a generated <c>.nuspec</c> by eye. A reference lands in assembly metadata only when a
/// type from it is actually used, which makes this check both cheap and exact: add a file to the
/// core project that touches <c>IServiceCollection</c> and this test fails on the next build,
/// rather than in a dependency graph somebody reads six months from now.
/// </para>
/// <para>
/// What it does not check is the project file. A <c>PackageReference</c> nobody uses still ships
/// as a package dependency and leaves no trace in metadata, so it would pass here; that one has
/// to be read in the <c>.csproj</c>, where it is at least in plain sight.
/// </para>
/// </remarks>
public sealed class CoreDependencyTests
{
    private static readonly Assembly Core = typeof(SecsItem).Assembly;

    [Fact]
    public void The_assembly_being_checked_is_the_core_assembly()
    {
        // Without this, moving SecsItem elsewhere would quietly turn the checks below into
        // assertions about some other assembly - which would keep passing and mean nothing.
        Assert.Equal("Barbatos.SecsGem.Core", Core.GetName().Name);
    }

    [Theory]
    [InlineData("Microsoft.Extensions.")]
    [InlineData("System.Net.")]
    public void The_core_assembly_uses_nothing_from(string forbidden)
    {
        var offenders = Core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith(forbidden, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Barbatos.SecsGem.Core references {string.Join(", ", offenders)}. Nothing under " +
            $"{forbidden}* belongs in the foundation package - the AddSecsXxx() methods live in " +
            "the assemblies that already need a container, and a transport lives in Hsms.");
    }

    [Fact]
    public void The_core_assembly_still_uses_the_two_dependencies_it_is_allowed()
    {
        // The negative tests above would also pass on an empty assembly, so the positive case is
        // worth stating: these two are the dependencies the package documents, and the reason the
        // codec can stay off the Large Object Heap and the format contracts can be defined over
        // PipeReader and PipeWriter.
        var referenced = Core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("CommunityToolkit.HighPerformance", referenced);
        Assert.Contains("System.IO.Pipelines", referenced);
    }
}
