// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization.UnitTests;

/// <summary>
/// Every format converted to every other format - all twenty-five ordered pairs.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the hub-and-spoke design exists to pass. Five formats means twenty ways to get
/// something wrong between two of them, and hand-written pairwise tests would cover the pairs
/// somebody thought of. A matrix covers the ones nobody did - which, in a format converter, is
/// where the bugs are.
/// </para>
/// <para>
/// The five pairs where a format meets itself are not padding. They are the only cases with nothing
/// in between to blame: a reader and a writer that are supposed to agree exactly, and do not.
/// </para>
/// <para>
/// <strong>What the baseline is.</strong> Every test measures the conversion against what the
/// <em>source</em> format preserved, not against the fixture. A conversion cannot restore what the
/// source could not hold in the first place, so comparing against the fixture would turn one
/// known limitation - SECS-II binary carries no message names - into twenty spurious failures.
/// </para>
/// </remarks>
public sealed class FormatMatrixTests
{
    /// <summary>
    /// Which warning code each lost capability produces.
    /// </summary>
    /// <remarks>
    /// Written out rather than computed, so this is a restatement of the contract and not a copy of
    /// the implementation. <see cref="SecsFormatCapabilities.Indentation"/> is deliberately absent:
    /// a format that cannot indent has lost nothing a caller needs warning about.
    /// </remarks>
    private static readonly (SecsFormatCapabilities Capability, string Code)[] WarningCodes =
    [
        (SecsFormatCapabilities.MultipleMessages, "MULTIPLE_MESSAGES_UNSUPPORTED"),
        (SecsFormatCapabilities.MessageNames, "MESSAGE_NAMES_DROPPED"),
        (SecsFormatCapabilities.ItemLabels, "ITEM_LABELS_DROPPED"),
        (SecsFormatCapabilities.Comments, "COMMENTS_DROPPED"),
        (SecsFormatCapabilities.ExactIntegers, "LARGE_INTEGERS_INEXACT"),
        (SecsFormatCapabilities.BinaryPayload, "BINARY_PAYLOAD_LOSSY"),
    ];

    [Theory]
    [MemberData(nameof(Formats.PairsByFixture), MemberType = typeof(Formats))]
    public async Task An_item_tree_survives_every_conversion(string source, string target, string fixture)
    {
        using var original = Formats.Create(fixture);

        var sourceBytes = await Formats.WriteAsync(source, [original]);
        var baseline = Assert.Single(await Formats.ReadAsync(source, sourceBytes));

        using (baseline)
        {
            var (targetBytes, report) = await Formats.TranscodeAsync(source, sourceBytes, target);
            using var actual = Assert.Single(await Formats.ReadAsync(target, targetBytes));

            Formats.AssertSameTree(baseline, actual, $"{source} -> {target} [{fixture}]");

            Assert.Equal(1, report.MessageCount);
            Assert.Equal(source, report.SourceFormat.Id);
            Assert.Equal(target, report.TargetFormat.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Formats.Pairs), MemberType = typeof(Formats))]
    public async Task A_document_of_several_messages_survives_every_conversion(string source, string target)
    {
        var originals = Formats.CreateDocument();

        try
        {
            var sourceBytes = await Formats.WriteAsync(source, originals);
            var baseline = await Formats.ReadAsync(source, sourceBytes);

            try
            {
                var (targetBytes, report) = await Formats.TranscodeAsync(source, sourceBytes, target);
                var actual = await Formats.ReadAsync(target, targetBytes);

                try
                {
                    Assert.Equal(originals.Count, baseline.Count);
                    Assert.Equal(baseline.Count, actual.Count);
                    Assert.Equal(baseline.Count, report.MessageCount);

                    for (var index = 0; index < baseline.Count; index++)
                    {
                        Formats.AssertSameTree(
                            baseline[index],
                            actual[index],
                            $"{source} -> {target}, message {index + 1} of {baseline.Count}");
                    }
                }
                finally
                {
                    actual.ForEach(message => message.Dispose());
                }
            }
            finally
            {
                baseline.ForEach(message => message.Dispose());
            }
        }
        finally
        {
            originals.ForEach(message => message.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Formats.Pairs), MemberType = typeof(Formats))]
    public async Task A_message_name_survives_exactly_as_far_as_the_target_claims(string source, string target)
    {
        using var original = Formats.Create("names-and-labels");

        var sourceBytes = await Formats.WriteAsync(source, [original]);
        var baseline = Assert.Single(await Formats.ReadAsync(source, sourceBytes));

        using (baseline)
        {
            var (targetBytes, report) = await Formats.TranscodeAsync(source, sourceBytes, target);
            using var actual = Assert.Single(await Formats.ReadAsync(target, targetBytes));

            var targetKeeps = Formats.Descriptor(target).Supports(SecsFormatCapabilities.MessageNames);
            var sourceHad = Formats.Descriptor(source).Supports(SecsFormatCapabilities.MessageNames);

            // The declared capability and the observed behaviour have to agree. A descriptor that
            // claims more than its writer delivers is worse than no descriptor at all: it is a
            // promise a caller acts on.
            Assert.Equal(targetKeeps ? baseline.Name : null, actual.Name);
            Assert.Equal(sourceHad && !targetKeeps, Warned(report, "MESSAGE_NAMES_DROPPED"));
        }
    }

    [Theory]
    [MemberData(nameof(Formats.Pairs), MemberType = typeof(Formats))]
    public async Task Item_labels_survive_exactly_as_far_as_the_target_claims(string source, string target)
    {
        using var original = Formats.Create("names-and-labels");

        var sourceBytes = await Formats.WriteAsync(source, [original]);
        var baseline = Assert.Single(await Formats.ReadAsync(source, sourceBytes));

        using (baseline)
        {
            var (targetBytes, report) = await Formats.TranscodeAsync(source, sourceBytes, target);
            using var actual = Assert.Single(await Formats.ReadAsync(target, targetBytes));

            var targetKeeps = Formats.Descriptor(target).Supports(SecsFormatCapabilities.ItemLabels);
            var sourceHad = Formats.Descriptor(source).Supports(SecsFormatCapabilities.ItemLabels);

            var expected = targetKeeps
                ? Formats.Labels(baseline.Item)
                : Formats.Labels(baseline.Item).ConvertAll(_ => (string?)null);

            Assert.Equal(expected, Formats.Labels(actual.Item));
            Assert.Equal(sourceHad && !targetKeeps, Warned(report, "ITEM_LABELS_DROPPED"));
        }
    }

    [Theory]
    [MemberData(nameof(Formats.Pairs), MemberType = typeof(Formats))]
    public async Task The_report_names_exactly_the_capabilities_the_target_lacks(string source, string target)
    {
        using var original = Formats.Create("every-format");

        var sourceBytes = await Formats.WriteAsync(source, [original]);
        var (_, report) = await Formats.TranscodeAsync(source, sourceBytes, target);

        var expected = ExpectedWarnings(Formats.Descriptor(source), Formats.Descriptor(target));
        var actual = report.Warnings.Select(warning => warning.Code).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length == 0, report.IsLossless);
    }

    [Theory]
    [MemberData(nameof(Formats.Ids), MemberType = typeof(Formats))]
    public async Task Converting_a_format_to_itself_loses_nothing(string id)
    {
        using var original = Formats.Create("every-format");

        var sourceBytes = await Formats.WriteAsync(id, [original]);
        var (_, report) = await Formats.TranscodeAsync(id, sourceBytes, id);

        // A format cannot lack a capability it has, so this is lossless by construction - which is
        // worth asserting, because it is also the one case where a warning would mean the
        // comparison itself is wrong.
        Assert.True(report.IsLossless, $"{id} -> {id} reported {string.Join(", ", report.Warnings)}");
    }

    [Theory]
    [MemberData(nameof(Formats.Ids), MemberType = typeof(Formats))]
    public async Task Writing_the_same_document_twice_gives_the_same_bytes(string id)
    {
        var first = Formats.CreateDocument();
        var second = Formats.CreateDocument();

        try
        {
            // Determinism matters beyond tidiness: a conformance fixture in version control that
            // changes byte for byte between runs is a fixture nobody can review.
            Assert.Equal(await Formats.WriteAsync(id, first), await Formats.WriteAsync(id, second));
        }
        finally
        {
            first.ForEach(message => message.Dispose());
            second.ForEach(message => message.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Formats.Ids), MemberType = typeof(Formats))]
    public void Every_format_registers_both_a_reader_and_a_writer(string id)
    {
        // A spoke that registers one and forgets the other is a format that converts in one
        // direction and throws in the other.
        Assert.True(Formats.Transcoder.Registry.TryGetReader(id, out var reader));
        Assert.True(Formats.Transcoder.Registry.TryGetWriter(id, out var writer));

        Assert.Equal(id, reader!.Descriptor.Id);
        Assert.Equal(id, writer!.Descriptor.Id);
        Assert.Equal(reader.Descriptor, writer.Descriptor);
    }

    [Theory]
    [MemberData(nameof(Formats.Ids), MemberType = typeof(Formats))]
    public void Every_format_resolves_from_each_of_its_file_extensions(string id)
    {
        var descriptor = Formats.Descriptor(id);

        Assert.NotEmpty(descriptor.FileExtensions);

        foreach (var extension in descriptor.FileExtensions)
        {
            Assert.True(
                Formats.Transcoder.Registry.TryResolveFormatId($"capture{extension}", out var resolved),
                $"{extension} resolved to no format.");

            Assert.Equal(id, resolved);
        }
    }

    [Fact]
    public void An_unregistered_extension_resolves_to_nothing()
    {
        Assert.False(Formats.Transcoder.Registry.TryResolveFormatId("capture.yaml", out _));
    }

    [Fact]
    public async Task An_unregistered_format_is_a_clear_failure_rather_than_an_empty_document()
    {
        using var original = Formats.Create("empty");
        var bytes = await Formats.WriteAsync(SecsFormatIds.Sml, [original]);

        var error = await Assert.ThrowsAsync<SecsFormatException>(
            async () => await Formats.TranscodeAsync(SecsFormatIds.Sml, bytes, "yaml"));

        Assert.Contains("No writer is registered", error.Message, StringComparison.Ordinal);
        Assert.Contains("sml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_writes_sixty_four_bit_integers_as_strings_by_default()
    {
        var json = await ExtremesAsJsonAsync(largeIntegersAsStrings: true);

        // Quoted, so a JavaScript consumer gets a string it can parse with BigInt rather than a
        // number it will silently round to 18446744073709552000.
        Assert.Contains("\"18446744073709551615\"", json, StringComparison.Ordinal);
        Assert.Contains("\"-9223372036854775808\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_writes_them_bare_when_told_to()
    {
        var json = await ExtremesAsJsonAsync(largeIntegersAsStrings: false);

        Assert.Contains("18446744073709551615", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"18446744073709551615\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Json_round_trips_sixty_four_bit_integers_exactly_either_way(bool largeIntegersAsStrings)
    {
        using var original = Formats.Create("extremes");

        var sourceBytes = await Formats.WriteAsync(SecsFormatIds.Sml, [original]);
        var (jsonBytes, report) = await Formats.TranscodeAsync(
            SecsFormatIds.Sml,
            sourceBytes,
            SecsFormatIds.Json,
            writeOptions: new SecsWriteOptions { WriteLargeIntegersAsStrings = largeIntegersAsStrings });

        using var actual = Assert.Single(await Formats.ReadAsync(SecsFormatIds.Json, jsonBytes));

        // Both ways. JSON *text* has arbitrary-precision numbers, and Utf8JsonReader parsing into
        // a ulong never goes near a double, so this stack's own round trip is exact whatever the
        // option says.
        // The I8 fixture is MinValue, -1, 0, 1, MaxValue; the U8 one is 0, 1, MaxValue.
        Assert.Equal(ulong.MaxValue, actual.Item?[1].GetSpan<ulong>()[2]);
        Assert.Equal(long.MinValue, actual.Item?[0].GetSpan<long>()[0]);
        Assert.Equal(long.MaxValue, actual.Item?[0].GetSpan<long>()[4]);

        // And the warning stands regardless, because it is about the document rather than about
        // this round trip: JSON numbers are doubles to most of the world, and a recipe that leaves
        // here as a bare 2^64-1 comes back from a browser as something else. A capability declared
        // against what the format guarantees - rather than against what one writer happens to
        // manage - is the one a caller can act on.
        Assert.True(Warned(report, "LARGE_INTEGERS_INEXACT"));
    }

    private static async Task<string> ExtremesAsJsonAsync(bool largeIntegersAsStrings)
    {
        using var original = Formats.Create("extremes");

        var sourceBytes = await Formats.WriteAsync(SecsFormatIds.Sml, [original]);
        var (jsonBytes, _) = await Formats.TranscodeAsync(
            SecsFormatIds.Sml,
            sourceBytes,
            SecsFormatIds.Json,
            writeOptions: new SecsWriteOptions { WriteLargeIntegersAsStrings = largeIntegersAsStrings });

        return System.Text.Encoding.UTF8.GetString(jsonBytes);
    }

    private static bool Warned(SecsTranscodeReport report, string code)
        => report.Warnings.Any(warning => string.Equals(warning.Code, code, StringComparison.Ordinal));

    private static string[] ExpectedWarnings(SecsFormatDescriptor source, SecsFormatDescriptor target) =>
    [
        .. WarningCodes
            .Where(entry => source.Supports(entry.Capability) && !target.Supports(entry.Capability))
            .Select(entry => entry.Code)
            .Order(StringComparer.Ordinal),
    ];
}
