// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization.UnitTests;

/// <summary>
/// The committed documents under <c>assets/conformance</c>, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This catches the one thing the round-trip matrix cannot. A change to a writer that also changes
/// its reader passes every round trip and still breaks every consumer outside this repository - the
/// host that greps a capture, the vendor tool that reads the recipe, the fixture in somebody's own
/// test suite. The matrix proves the stack agrees with itself; these files prove it still agrees
/// with what it shipped.
/// </para>
/// <para>
/// They are also documentation that cannot go stale: to see what SML, JSON, XML, INI or the binary
/// framing actually look like for a real message, open one.
/// </para>
/// <para>
/// <strong>When a fixture is missing</strong> the test writes it and fails, saying so. Review the
/// file, commit it, and the test passes from then on. <strong>When a fixture differs</strong> the
/// test writes the new output alongside it as <c>.actual</c> so the two can be diffed - and then
/// the question to answer is whether the format change was meant.
/// </para>
/// </remarks>
public sealed class ConformanceFixtureTests
{
    /// <summary>The fixtures worth committing - the three that show the most about a format.</summary>
    private static readonly string[] Interesting = ["names-and-labels", "every-format", "text-escaping"];

    /// <summary>Every interesting fixture in every format.</summary>
    public static TheoryData<string, string> Documents
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var fixture in Interesting)
            {
                foreach (var format in Formats.All)
                {
                    data.Add(fixture, format);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task A_document_matches_the_committed_fixture(string fixture, string format)
    {
        using var message = Formats.Create(fixture);
        var actual = await Formats.WriteAsync(format, [message]);

        var extension = Formats.Descriptor(format).FileExtensions[0];
        var directory = ConformanceDirectory();
        var path = Path.Combine(directory, $"{fixture}{extension}");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, actual, Formats.Token);

            Assert.Fail(
                $"No fixture for {fixture} in {format}, so one was written to{Environment.NewLine}"
                + $"  {path}{Environment.NewLine}"
                + "Review it and commit it.");
        }

        var expected = await File.ReadAllBytesAsync(path, Formats.Token);

        if (!expected.AsSpan().SequenceEqual(actual))
        {
            var rejected = path + ".actual";
            await File.WriteAllBytesAsync(rejected, actual, Formats.Token);

            Assert.Fail(
                $"{format} no longer writes {fixture} the way the committed fixture does."
                + $"{Environment.NewLine}  expected: {path}"
                + $"{Environment.NewLine}  actual:   {rejected}"
                + $"{Environment.NewLine}The round trip still works, so this is a change to the document"
                + " a consumer outside this repository would read. Diff the two and decide whether it was meant.");
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task A_committed_fixture_reads_back_to_the_message_it_came_from(string fixture, string format)
    {
        // The other half: a fixture is only worth having if it still decodes. One that drifted into
        // being unparseable would otherwise sit in the repository looking authoritative.
        using var expected = Formats.Create(fixture);

        var path = Path.Combine(ConformanceDirectory(), $"{fixture}{Formats.Descriptor(format).FileExtensions[0]}");
        Assert.SkipWhen(!File.Exists(path), $"No committed fixture at {path} yet - the write test creates it.");

        var document = await File.ReadAllBytesAsync(path, Formats.Token);
        using var actual = Assert.Single(await Formats.ReadAsync(format, document));

        Formats.AssertSameTree(expected, actual, $"the committed {format} fixture for {fixture}");

        if (Formats.Descriptor(format).Supports(SecsFormatCapabilities.MessageNames))
        {
            Assert.Equal(expected.Name, actual.Name);
        }
    }

    /// <summary>Finds <c>assets/conformance</c> by walking up to the solution file.</summary>
    /// <returns>The absolute path.</returns>
    /// <remarks>
    /// Walking up to the <c>.slnx</c> rather than taking a relative path from the test binary,
    /// because that path differs per target framework and per configuration, and a fixture test
    /// that only works in one of them is worse than none.
    /// </remarks>
    private static string ConformanceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Barbatos.SecsGem.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "assets", "conformance");
    }
}
