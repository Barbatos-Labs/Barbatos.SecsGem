// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.Runtime.InteropServices;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.MaterialFlow;
using Barbatos.SecsGem.PerformanceFlow;
using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Productivity;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Mapping;
using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Substrates;
using Barbatos.SecsGem.Material;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.ControlPrograms;
using Barbatos.SecsGem.DataSets;
using Barbatos.SecsGem.ItemTransfer;
using Barbatos.SecsGem.Recipes;
using Barbatos.SecsGem.Subsystems;
using Barbatos.SecsGem.WaferMap;
using Microsoft.Extensions.DependencyInjection;

namespace PackageSmokeTest;

/// <summary>
/// One report, declared the way a consumer declares one: an attribute out of the mapper package
/// and nothing else. Whether the source generator wrote a contract for it is a check of its own.
/// </summary>
[SecsMessage(6, 11, Name = "EventReportSend")]
public sealed class ChamberReport
{
    /// <summary>Gets or sets the collection event.</summary>
    public uint Ceid { get; set; }

    /// <summary>Gets or sets the reading the event carries.</summary>
    public ChamberReading Reading { get; set; } = new();
}

/// <summary>
/// A type with no attribute at all, reached only through <see cref="ChamberReport.Reading"/>. The
/// generator is supposed to follow a property into it, so it is worth asserting on its own.
/// </summary>
public sealed class ChamberReading
{
    /// <summary>Gets or sets the variable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the value.</summary>
    public double Value { get; set; }
}

/// <summary>
/// Uses Barbatos.SecsGem the way somebody who has never seen this repository uses it: through
/// <c>PackageReference</c>, restored from packed <c>.nupkg</c> files.
/// </summary>
/// <remarks>
/// <para>
/// Everything else here is wired with <c>ProjectReference</c>, which answers "does the code work"
/// and says nothing about whether the packages do. The two packaging faults found so far - a
/// symbol package NuGet refused to create, and a dependency the foundation package was not allowed
/// to have - were both invisible to a few thousand passing tests, and both obvious the moment
/// somebody looked at what had actually been packed.
/// </para>
/// <para>
/// Every check runs even after one fails, so a single run reports everything that is wrong rather
/// than the first thing.
/// </para>
/// </remarks>
internal static class Program
{
    private static int failures;

    private static async Task<int> Main()
    {
        Console.WriteLine($"Barbatos.SecsGem package smoke test on {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        await Run("core: a tree survives encode and decode", CoreRoundTripAsync).ConfigureAwait(false);
        await Run("core: the packaged assembly pulls in no container", NoContainerInCoreAsync).ConfigureAwait(false);
        await Run("spokes: one AddSecsXxx() per format is the whole wiring", HubThroughSpokesAsync).ConfigureAwait(false);
        await Run("generator: the analyzer package wrote a contract", GeneratedContractAsync).ConfigureAwait(false);
        await Run("catalog: the embedded table came out of the package", CatalogAsync).ConfigureAwait(false);
        await Run("gem + hsms: the container resolves the equipment", GemResolvesAsync).ConfigureAwait(false);
        await Run("gem: the host side resolves on a builder of its own", GemHostResolvesAsync).ConfigureAwait(false);
        await Run("wafermap: stream 12 resolves at both ends without the gem package", WaferMapResolvesAsync).ConfigureAwait(false);
        await Run("datasets: stream 13 resolves at both ends, and the host declares nothing", DataSetsResolveAsync).ConfigureAwait(false);
        await Run("subsystems: stream 18 resolves at both ends, which declare disjoint sets", SubsystemsResolveAsync).ConfigureAwait(false);
        await Run("controlprograms: stream 8 resolves at both ends, and a program is bytes rather than an item", ControlProgramsResolveAsync).ConfigureAwait(false);
        await Run("itemtransfer: stream 21 resolves from one registration both ends call", ItemTransferResolvesAsync).ConfigureAwait(false);
        await Run("recipes: stream 15 resolves at both ends, and a recipe is bytes with a namespace", RecipesResolveAsync).ConfigureAwait(false);
        await Run("transfer stores: four packages keep what a host sent, and find it again", DurableTransferStoresAsync).ConfigureAwait(false);
        await Run("gem: four stores survive a restart, and a run's results do not", DurableProcessProgramStoresAsync).ConfigureAwait(false);
        await Run("material: stream 3 resolves at both ends, which declare different messages", MaterialResolvesAsync).ConfigureAwait(false);
        await Run("objects: stream 14 declares its primaries only when this end has objects", ObjectServicesResolveAsync).ConfigureAwait(false);
        await Run("carriers: E87 declares both halves, and the state machine refuses", CarriersResolveAsync).ConfigureAwait(false);
        await Run("carriers + substrates: E87 and E90 in one machine, both answering stream 14", CarriersAndSubstratesComposeAsync).ConfigureAwait(false);
        await Run("controljobs: E94 drives its state machine through stream 14, beside E87 and E90", ControlJobsResolveAsync).ConfigureAwait(false);
        await Run("processjobs: E40 declares stream 16 and all four standards answer stream 14", ProcessJobsResolveAsync).ConfigureAwait(false);
        await Run("materialflow: a verified slot map turns into substrates by itself", MaterialFlowResolvesAsync).ConfigureAwait(false);
        await Run("materialflow: a stopped process job settles the lot it was running", ProcessJobFlowResolvesAsync).ConfigureAwait(false);
        await Run("performancetracking: E116 accounts for a module's time and refuses to be told its state", PerformanceTrackingResolvesAsync).ConfigureAwait(false);
        await Run("objects: E39's other stream answers per object, beside GEM's own nine functions of it", EnhancedServicesResolveAsync).ConfigureAwait(false);
        await Run("productivity: E10's buckets add up and E79's four factors multiply out", ProductivityResolvesAsync).ConfigureAwait(false);
        await Run("performanceflow: a shift driven through E116 lands in E10 with no code between", PerformanceFlowResolvesAsync).ConfigureAwait(false);
        await Run("materialflow: a control job waits for its carriers and starts on the last one", ControlJobCarrierResolvesAsync).ConfigureAwait(false);
        await Run("state: six state machines written down, read back, and honest about the outage", StateSurvivesARestartAsync).ConfigureAwait(false);

        Console.WriteLine();

        if (failures == 0)
        {
            Console.WriteLine("all checks passed");
            return 0;
        }

        Console.WriteLine($"{failures} check(s) failed");
        return 1;
    }

    private static async Task Run(string what, Func<Task> check)
    {
        try
        {
            await check().ConfigureAwait(false);
            Console.WriteLine($"  ok    {what}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"  FAIL  {what}");
            Console.WriteLine($"        {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Assert(bool condition, string whatWentWrong)
    {
        if (!condition)
        {
            throw new InvalidOperationException(whatWentWrong);
        }
    }

    /// <summary>The item model and the binary codec, with no container and no socket in sight.</summary>
    private static Task CoreRoundTripAsync()
    {
        using var message = new SecsMessage(6, 11, replyExpected: true)
        {
            Name = "EventReportSend",
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.U4(7_001u),
                SecsItem.L(SecsItem.A("PRESSURE"), SecsItem.F8(1013.25))),
        };

        var writer = new ArrayBufferWriter<byte>();
        message.Item!.EncodeTo(writer);

        using var decoded = SecsItem.Decode(writer.WrittenMemory);

        Assert(decoded.Equals(message.Item), "the decoded tree is not equal to the encoded one");
        Assert(decoded[1].FirstValueOrDefault<uint>() == 7_001u, "the CEID did not survive the round trip");
        Assert(decoded[2][0].GetString() == "PRESSURE", "the variable name did not survive the round trip");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The rule this repository states about its foundation package, checked against the assembly
    /// a consumer actually restored rather than the one built here.
    /// </summary>
    private static Task NoContainerInCoreAsync()
    {
        var core = typeof(SecsItem).Assembly;

        Assert(
            string.Equals(core.GetName().Name, "Barbatos.SecsGem.Core", StringComparison.Ordinal),
            $"SecsItem came from {core.GetName().Name}, so this check is looking at the wrong assembly");

        var offenders = core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert(offenders.Length == 0, $"the packaged core assembly references {string.Join(", ", offenders)}");

        return Task.CompletedTask;
    }

    /// <summary>
    /// No <c>AddSecsSerialization()</c>: each spoke registers the hub it plugs into, so four calls
    /// give five formats and one registry.
    /// </summary>
    private static async Task HubThroughSpokesAsync()
    {
        await using var provider = new ServiceCollection()
            .AddSecsSml()
            .AddSecsJson()
            .AddSecsXml()
            .AddSecsIni()
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<ISecsFormatRegistry>();
        var ids = registry.Formats.Select(descriptor => descriptor.Id).Order(StringComparer.Ordinal).ToArray();
        var expected = new[] { "ini", "json", "secs2", "sml", "xml" };

        Assert(
            ids.SequenceEqual(expected, StringComparer.Ordinal),
            $"the registry holds [{string.Join(", ", ids)}] rather than the five expected formats");

        var transcoder = provider.GetRequiredService<SecsTranscoder>();
        var (json, report) = await transcoder
            .TranscodeTextAsync(SecsFormatIds.Sml, "S1F1 W .", SecsFormatIds.Json)
            .ConfigureAwait(false);

        Assert(report.MessageCount == 1, $"sml -> json carried {report.MessageCount} message(s)");

        var back = await transcoder.ReadTextAsync(SecsFormatIds.Json, json).ConfigureAwait(false);

        try
        {
            Assert(back.Count == 1, $"reading the json back gave {back.Count} message(s)");
            Assert(back[0].S == 1 && back[0].F == 1, $"S1F1 came back as S{back[0].S}F{back[0].F}");
            Assert(back[0].ReplyExpected, "the W bit was lost on the way through json");
        }
        finally
        {
            foreach (var message in back)
            {
                message.Dispose();
            }
        }
    }

    /// <summary>
    /// The generator travels as content under <c>analyzers/dotnet/cs</c>, which is the one part of
    /// the packaging a <c>ProjectReference</c> build never exercises.
    /// </summary>
    /// <remarks>
    /// <c>SecsSerializer</c> falls back to reflection when no generated contract exists, so
    /// serializing would succeed either way. Asking the generated resolver directly is the only
    /// way to find out whether the analyzer ran at all.
    /// </remarks>
    private static Task GeneratedContractAsync()
    {
        Assert(
            SecsTypeInfoResolver.Generated.Contains(typeof(ChamberReport)),
            "no generated contract for ChamberReport: the compiler did not load the analyzer from analyzers/dotnet/cs");

        Assert(
            SecsTypeInfoResolver.Generated.Contains(typeof(ChamberReading)),
            "no generated contract for ChamberReading, so the generator did not follow a property into an unattributed type");

        var report = new ChamberReport
        {
            Ceid = 7_001u,
            Reading = new ChamberReading { Name = "PRESSURE", Value = 1013.25 },
        };

        using var item = SecsSerializer.Serialize(report);
        var round = SecsSerializer.Deserialize<ChamberReport>(item);

        Assert(round is not null, "deserializing gave null");
        Assert(round!.Ceid == 7_001u, $"the CEID came back as {round.Ceid}");
        Assert(
            string.Equals(round.Reading.Name, "PRESSURE", StringComparison.Ordinal),
            $"the nested reading came back as '{round.Reading.Name}'");

        using var fromAttribute = SecsSerializer.ToMessage(report);

        Assert(
            fromAttribute.S == 6 && fromAttribute.F == 11,
            $"[SecsMessage(6, 11)] produced S{fromAttribute.S}F{fromAttribute.F}");

        return Task.CompletedTask;
    }

    /// <summary>The 40 KB reference table is an embedded resource, so it travels inside the assembly.</summary>
    private static Task CatalogAsync()
    {
        Assert(
            SecsCatalog.Messages.Count >= 500,
            $"the catalogue loaded {SecsCatalog.Messages.Count} messages, so the embedded resource is truncated or missing");

        Assert(SecsCatalog.NameOf(6, 11) is not null, "S6F11 has no name in the catalogue");

        using var message = new SecsMessage(6, 11, replyExpected: true);
        var described = message.Describe();

        Assert(
            described.StartsWith("S6F11 W ", StringComparison.Ordinal),
            $"Describe() returned '{described}'");

        // Provenance comes out of the packed resource too, and it is computed at load time from
        // both halves of that file - the message table and the item dictionary - so a resource
        // that packed only part of itself would show up here rather than as a wrong answer later.
        Assert(SecsCatalog.TryGetStream(16, out var sixteen), "stream 16 is not in the catalogue");
        // The dictionary's half of the same question, also computed at load time from the packed
        // resource: 213 of the 375 definitions are named by no message, and a stocker standard
        // came apart on two of them.
        Assert(
            SecsCatalog.Provenance.UnusedDefinitions.Count == 213
                && SecsCatalog.Provenance.UndefinedDataItems.Count == 74,
            $"the catalogue provenance came out as '{SecsCatalog.Provenance}'");

        Assert(
            SecsCatalog.TryGetDataItem("LOCID", out var locationId) && locationId!.IsNamedByNoMessage,
            "LOCID is either missing from the packed dictionary or reported as named by a message");

        // The fourth measure, which needs both halves of the resource at once: it pairs names the
        // messages use against names the dictionary defines, so a resource that packed one half
        // would report no rivals at all rather than failing to load.
        Assert(
            SecsCatalog.Provenance.RivalSpellings.Count == 8,
            $"the catalogue found {SecsCatalog.Provenance.RivalSpellings.Count} rival spelling(s), not eight");

        Assert(
            SecsCatalog.Provenance.DetailedUnusedDefinitions.Count == 44,
            $"the catalogue found {SecsCatalog.Provenance.DetailedUnusedDefinitions.Count} unused definitions with code tables, not forty-four");

        // And the per-stream view, which is what a chapter consults: all eight belong to the
        // recipe streams and stream 21 - the chapter before them - has none.
        Assert(
            SecsCatalog.TryGetStream(15, out var fifteen) && fifteen!.RivalSpellings.Count == 7,
            "stream 15 does not report its seven rival spellings");

        Assert(
            SecsCatalog.TryGetStream(21, out var twentyOne) && twentyOne!.RivalSpellings.Count == 0,
            "stream 21 reports a rival spelling, which no stream outside the recipe region has");

        Assert(
            sixteen!.Provenance.IsEntirelyRepresentative,
            $"S16 reports {sixteen.Provenance.DefinedCount} defined message(s), not none");
        Assert(
            sixteen.Provenance.UndefinedDataItems.Contains("PJID", StringComparer.Ordinal),
            "S16's undefined item names do not include PJID, so the item dictionary did not load");

        // The third measure, also computed at load time and from both halves at once: a name's
        // bucket depends on whether the dictionary defines it AND whether another stream names it,
        // so a resource that packed one half would put every name in the wrong place.
        Assert(
            sixteen.Anchoring.HasNoOwnVocabulary && sixteen.Anchoring.DataItemCount == 13,
            $"S16 anchoring came out as '{sixteen.Anchoring}'");

        Assert(
            SecsCatalog.TryGetDataItem("GRANT", out var grant)
                && grant!.Streams.Count == 5
                && grant.Streams.Contains((byte)8),
            "GRANT's stream list did not survive the packed resource");

        // And the one line that closed the recipe question, which only exists if both the stream
        // table and its names came through.
        Assert(
            SecsCatalog.Provenance.StreamNameClashes.Count == 1
                && SecsCatalog.Provenance.StreamNameClashes[0].Name == "Recipe Management",
            "the stream name clash is missing, so the stream table packed without its names");

        Assert(SecsCatalog.TryGetStream(9, out var nine), "stream 9 is not in the catalogue");
        Assert(
            nine!.Provenance.DefinedCount == nine.Provenance.MessageCount,
            $"S9 reports {nine.Provenance.DefinedCount} of {nine.Provenance.MessageCount} defined");
        Assert(nine.Provenance.UndefinedDataItems.Count == 0, "S9 names an item the dictionary lacks");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The deepest dependency graph in the stack - Gem on Hsms on Core, plus the options, logging
    /// and hosting abstractions - resolved from a container the consumer brought itself.
    /// </summary>
    private static async Task GemResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddGemEquipment(
                model => model
                    .AddStatusVariable(1u, "Pressure", () => SecsItem.F8(1013.25))
                    .AddEvent(1_000u, "ProcessStart")
                    .AddReport(5_000u, 1u),
                runAsHostedService: false)

            // Both halves of stream 7, which is also the check that the two opt-in capabilities
            // can be registered together: they share S7F17 and S7F19, and a stream and function
            // may be declared only once, so an overlap would throw while the router was built.
            .AddGemProcessPrograms()
            .AddGemFormattedProcessPrograms()
            .AddGemProcessProgramMatrices()
            .AddGemTerminal()
            .AddGemTraces()
            .AddGemLimits()

            // E5's own stream 2 capability, which the stream 2 chapter added. Registered here as
            // well as declared, so this file exercises the extension method and not only the list.
            .AddGemServicePrograms()
            .AddGemSpooling();

        await using var provider = services.BuildServiceProvider();

        var equipment = provider.GetRequiredService<GemEquipment>();

        Assert(GemEquipment.HandledMessages.Count > 0, "the equipment declares no handled messages");
        Assert(equipment.Model.Variables.Count > 0, "the model has no variables");

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            router.TryGetHandler(1, 13, out _),
            "the router has no handler for S1F13, so the equipment's handled messages never reached the routing table");

        // Stream 6, which the stream 6 chapter finished. A ProjectReference build resolves these
        // from bin/ and cannot see a packaging mistake, and five chapters running this file is what
        // caught a declaration that never left the package - so the three rows that went in are
        // named, and so are the two refusals that are about a field the row turns on.
        Assert(
            GemEquipment.ProcessProgramMessages.Count == 11,
            $"the packed stream 7 declares {GemEquipment.ProcessProgramMessages.Count} primaries, not eleven - S7F43 is the eleventh");

        Assert(
            GemEquipment.ProcessProgramMessages.Contains(((byte)7, (byte)43)),
            "the packed stream 7 is missing the command cancel");

        // S1F9 is the last row of stream 1 and went in five chapters after the gapless sweep left
        // it out, so the packaged routing table is where its absence would show first.
        foreach (var (stream, function) in new[] { ((byte)6, (byte)7), ((byte)6, (byte)17), ((byte)6, (byte)27), ((byte)1, (byte)9) })
        {
            Assert(
                GemEquipment.HandledMessages.Contains((stream, function)),
                $"the equipment does not declare S{stream}F{function}");

            Assert(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} is declared and never reached the routing table");
        }

        Assert(
            GemHost.HandledMessages.Contains(((byte)6, (byte)9)),
            "the host does not declare S6F9, the one stream 6 row the machine pushes without a CEID");

        // S6F25 is the newest of the four rows that reach EventReportReceived, and the only one
        // whose refusal was about a shape. A packaged host that does not declare it answers S9F5
        // for a row E5 defines.
        Assert(
            GemHost.HandledMessages.Contains(((byte)6, (byte)25)),
            "the host does not declare S6F25, the notification report send");

        // S7F35 is the formatted half's own directory and the fifth message of that capability, so
        // a packaging mistake would show as a list that is one short.
        Assert(
            GemEquipment.FormattedProcessProgramMessages.Count == 5,
            $"the packed formatted half declares {GemEquipment.FormattedProcessProgramMessages.Count} primaries, not five");

        Assert(
            GemEquipment.FormattedProcessProgramMessages.Contains(((byte)7, (byte)35)),
            "the formatted half does not declare S7F35, its own directory");

        // And stream 7's third capability, which is the newest list in the package: three rows and
        // a store of its own. A packaging mistake here would leave a resolved matrix store with
        // nothing routed to it.
        Assert(
            GemEquipment.ProcessProgramMatrixMessages.Count == 3,
            $"the packed matrix capability declares {GemEquipment.ProcessProgramMatrixMessages.Count} primaries, not three");

        foreach (var (stream, function) in GemEquipment.ProcessProgramMatrixMessages)
        {
            Assert(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} is declared and never reached the routing table");
        }

        Assert(
            provider.GetService<IGemProcessProgramMatrixStore>() is not null,
            "the matrix store did not resolve from the packaged assembly");

        // The separation the capability exists for: a matrix is not a process program, so none of
        // its three rows may appear in the lists that answer for programs.
        foreach (var pair in GemEquipment.ProcessProgramMatrixMessages)
        {
            Assert(
                !GemEquipment.ProcessProgramMessages.Contains(pair)
                    && !GemEquipment.FormattedProcessProgramMessages.Contains(pair),
                $"S{pair.Stream}F{pair.Function} is declared by more than one capability");
        }

        // Stream 2, which the stream 2 chapter finished. The counts are here rather than only in
        // the test suite because a ProjectReference build resolves these from bin/ and cannot see
        // a packaging mistake - which is how five chapters running this file caught a declaration
        // that never left the package.
        Assert(
            GemEquipment.ServiceProgramMessages.Count == 6,
            $"the equipment declares {GemEquipment.ServiceProgramMessages.Count} service program message(s), not six");

        Assert(
            GemEquipment.SpoolMessages.Count == 2,
            $"the equipment declares {GemEquipment.SpoolMessages.Count} spool message(s), not two");

        Assert(
            GemEquipment.SpoolableMessages.Count == 9,
            $"the equipment says {GemEquipment.SpoolableMessages.Count} message(s) can be spooled, not nine");

        // Opt-in, which is what keeping them out of HandledMessages is for: a stream and function
        // may be declared only once, so an overlap would throw while the router was being built.
        foreach (var declared in GemEquipment.ServiceProgramMessages.Concat(GemEquipment.SpoolMessages))
        {
            Assert(
                !GemEquipment.HandledMessages.Contains(declared),
                $"S{declared.Stream}F{declared.Function} is declared twice over");

            Assert(
                router.TryGetHandler(declared.Stream, declared.Function, out _),
                $"S{declared.Stream}F{declared.Function} was registered and never reached the routing table");
        }

        Assert(
            provider.GetService<IGemServiceProgramStore>() is not null,
            "AddGemServicePrograms registered no store");

        foreach (var (stream, function) in new[] { ((byte)2, (byte)27), ((byte)2, (byte)39) })
        {
            Assert(
                GemEquipment.HandledMessages.Contains((stream, function)),
                $"the equipment does not declare S{stream}F{function}");

            Assert(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} is declared and never reached the routing table");
        }

        // S2F19 is the one row of stream 2 that stays out, on RSTC - which is the whole of its
        // body and a name the reference never defines.
        Assert(
            !GemEquipment.HandledMessages.Contains(((byte)2, (byte)19)),
            "the equipment declares S2F19, which is refused with a reason recorded");

        foreach (var function in new byte[] { 21, 25, 29 })
        {
            Assert(
                !GemEquipment.HandledMessages.Contains(((byte)6, function)),
                $"the equipment declares S6F{function}, which is refused with a reason recorded");
        }

        Assert(
            router.TryGetHandler(7, 3, out _) && router.TryGetHandler(7, 23, out _),
            "the router is missing one half of stream 7, so an opt-in capability did not declare its messages");

        Assert(
            router.TryGetHandler(10, 3, out _) && router.TryGetHandler(10, 9, out _),
            "the router is missing terminal services, so AddGemTerminal did not declare its messages");

        Assert(
            router.TryGetHandler(2, 23, out _),
            "the router has no handler for S2F23, so AddGemTraces did not declare it");

        Assert(
            router.TryGetHandler(2, 45, out _) && router.TryGetHandler(2, 47, out _),
            "the router is missing limits monitoring, so AddGemLimits did not declare its messages");

        // The deepest body this stack builds, through the packaged assembly: a band that cannot be
        // a band is refused where it is written.
        var band = new GemVariableLimit(1, 900d, 1_100d);

        Assert(band.Contains(1_100d), "a bound is inclusive, so the band should contain its upper");
        Assert(!band.Contains(1_100.5d), "the band should not contain a value above its upper");

        try
        {
            _ = new GemVariableLimit(2, 1_100d, 900d);
            Assert(false, "an upper below its lower was accepted as a band");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected.
        }

        var terminal = (GemInMemoryTerminal)provider.GetRequiredService<IGemTerminal>();

        Assert(
            await terminal.DisplayAsync(new GemTerminalMessage(0, ["from the package"]), CancellationToken.None)
                .ConfigureAwait(false) == GemTerminalAcknowledgement.Accepted,
            "the in-memory terminal refused a message");

        Assert(terminal.Displayed.Count == 1, $"the terminal kept {terminal.Displayed.Count} message(s)");

        var formatted = new GemFormattedProcessProgram(
            "RECIPE-7",
            "BRBTOS",
            "1.0.0",
            [new GemProcessProgramCommand(12, ["SETPOINT", "350.0"])]);

        var store = provider.GetRequiredService<IGemFormattedProcessProgramStore>();

        Assert(
            await store.SaveAsync(formatted, CancellationToken.None).ConfigureAwait(false)
                == GemProcessProgramAcknowledgement.Accepted,
            "the formatted store refused a program it should have taken");

        var read = await store.LoadAsync("RECIPE-7", CancellationToken.None).ConfigureAwait(false);

        Assert(read is not null, "the formatted store lost the program it had just accepted");
        Assert(read!.Commands.Count == 1, $"the program came back with {read.Commands.Count} command(s)");
        Assert(read.Commands[0].Code == 12, $"CCODE came back as {read.Commands[0].Code}");
    }

    /// <summary>
    /// The host half of the GEM package, which needs a container of its own.
    /// </summary>
    /// <remarks>
    /// Not the same <c>ServiceCollection</c> as the check above, and that is the point: both sides
    /// declare S1F1 and S1F13, the routing table refuses a pair twice, and one process answering as
    /// both ends of one link is not a thing E30 describes. Two connections means two builders.
    /// </remarks>
    private static async Task GemHostResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddGemHost(configure: options => options.ModelName = "SMOKE-HOST");

        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<GemHost>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(!host.IsCommunicating, "a host that has not established communications says it has");
        Assert(GemHost.HandledMessages.Count > 0, "the host declares no messages");

        foreach (var (stream, function) in GemHost.HandledMessages)
        {
            Assert(
                router.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddGemHost did not declare it");
        }
    }

    /// <summary>
    /// The wafer map package, which is the one that depends on Hsms without depending on Gem.
    /// </summary>
    /// <remarks>
    /// That independence is a packaging claim, not a code one: a consumer that references this
    /// package and never references Gem has to be able to map wafers, and a stray dependency in
    /// the <c>.nuspec</c> would be invisible to every test in the repository. Both ends go in one
    /// container here because stream 12 gives them no pair in common - except S12F19, which is why
    /// the host end gets a builder of its own.
    /// </remarks>
    private static async Task WaferMapResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddWaferMapEquipment();

        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<WaferMapEquipment>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            router.TryGetHandler(12, 19, out _),
            "the router has no handler for S12F19, so AddWaferMapEquipment did not declare it");

        Assert(
            !router.TryGetHandler(12, 1, out _),
            "the equipment claimed S12F1, which is a message it is the one that sends");

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddWaferMapHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<WaferMapHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(WaferMapHost.HandledMessages.Count == 10, $"the host declares {WaferMapHost.HandledMessages.Count} message(s), not ten");

        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            Assert(
                hostRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddWaferMapHost did not declare it");
        }

        // And the store out of the packaged assembly actually holds a map, filter and all.
        var maps = hostProvider.GetRequiredService<IWaferMapStore>();

        Assert(
            await maps.SaveSetupAsync(
                new WaferMapSetup { MaterialId = "W01", NullBinCode = '.', RowCount = 1, ColumnCount = 3 },
                CancellationToken.None).ConfigureAwait(false) == WaferMapSetupAcknowledgement.Accepted,
            "the in-memory store refused a setup it should have taken");

        Assert(
            await maps.SavePointDataAsync(
                new WaferMapPointData
                {
                    MaterialId = "W01",
                    Points =
                    [
                        new WaferMapPoint
                        {
                            Position = new WaferMapPosition(0, 0),
                            Bins = new byte[] { 1, (byte)'.', 2 },
                        },
                    ],
                },
                CancellationToken.None).ConfigureAwait(false) == WaferMapDataAcknowledgement.Accepted,
            "the in-memory store refused a map it should have taken");

        var filtered = await maps.LoadPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W01", BinSelection = WaferMapBinSelection.NonNullOnly },
            CancellationToken.None).ConfigureAwait(false);

        Assert(filtered is not null, "the store lost the map it had just accepted");
        Assert(filtered!.Points.Count == 2, $"the null bin was not filtered out: {filtered.Points.Count} point(s) came back");
        Assert(filtered.Points[1].Position.X == 2, $"the split run lost its coordinate: X came back as {filtered.Points[1].Position.X}");
    }

    /// <summary>
    /// The data set package, whose host end declares no message at all.
    /// </summary>
    /// <remarks>
    /// Two claims only a restore from the real <c>.nupkg</c> can check. One is the dependency:
    /// stream 13 is no part of E30, so a consumer that never references Gem has to be able to move
    /// data sets. The other is the registration - <c>AddDataSetHost</c> adds a type and no route,
    /// which is unique in this repository and exactly the sort of thing a packaging mistake would
    /// turn into a silent S9F5.
    /// </remarks>
    private static async Task DataSetsResolveAsync()
    {
        var equipmentServices = new ServiceCollection();

        equipmentServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddDataSetEquipment();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<DataSetEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            DataSetEquipment.HandledMessages.Count == 7,
            $"the equipment declares {DataSetEquipment.HandledMessages.Count} message(s), not seven");

        foreach (var (stream, function) in DataSetEquipment.HandledMessages)
        {
            Assert(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddDataSetEquipment did not declare it");
        }

        Assert(
            !equipmentRouter.TryGetHandler(13, 9, out _),
            "the equipment declared S13F9, the blocked send this package deliberately leaves out");

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddDataSetHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<DataSetHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(DataSetHost.HandledMessages.Count == 0, "the host declares a message, and stream 13 gives it none");

        for (byte function = 1; function <= 16; function++)
        {
            Assert(
                !hostRouter.TryGetHandler(13, function, out _),
                $"the router has a handler for S13F{function} at a host, so AddDataSetHost declared one");
        }

        // And the payload, which is the package's whole answer to an item the dictionary never
        // defines: encoded on the way in, and the same bytes on the way out.
        using var contents = SecsItem.L(
            SecsItem.A("recipe-7"),
            SecsItem.U4(1, 2, 3));

        var expected = contents.ToByteArray();
        var payload = DataSetPayload.From(contents);

        var store = equipmentProvider.GetRequiredService<IDataSetStore>();

        var opening = await store.OpenAsync(
            new DataSetOpenRequest { DataSetId = 41, Mode = DataSetOpenMode.Send, Length = (uint)expected.Length },
            CancellationToken.None).ConfigureAwait(false);

        Assert(opening.IsGranted, $"the in-memory store refused an open with {opening.Grant}");

        Assert(
            await store.SaveAsync(
                new DataSetContent { DataSetId = 41, DataId = 1, Data = payload },
                CancellationToken.None).ConfigureAwait(false) == DataSetAcknowledgement.Accepted,
            "the store refused a data set it had just granted room for");

        var status = await store.StatusAsync(41, CancellationToken.None).ConfigureAwait(false);

        Assert(status.Exists, "the store lost the data set it had just accepted");
        Assert(status.Length == (uint)expected.Length, $"the store reported a length of {status.Length}");

        using var again = payload.Decode();

        Assert(again is not null, "the payload decoded to nothing");
        Assert(
            again!.ToByteArray().AsSpan().SequenceEqual(expected),
            "the data set did not survive the round trip through its encoded form");
    }

    /// <summary>
    /// The control program package, whose body is bytes where the data set package's is an item.
    /// </summary>
    /// <remarks>
    /// Three claims only a restore from the real <c>.nupkg</c> can check. The dependency: stream 8
    /// is E5 and no part of E30, so a controller that never references Gem has to work. The
    /// registration: <c>AddControlProgramHost</c> adds a type and no route, which it shares with
    /// <c>AddDataSetHost</c> and nothing else. And the distinction between the two packages'
    /// bodies, which is the thing most easily got wrong by a consumer that uses both.
    /// </remarks>
    private static async Task ControlProgramsResolveAsync()
    {
        var equipmentServices = new ServiceCollection();

        equipmentServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddControlProgramEquipment();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<ControlProgramEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            ControlProgramEquipment.HandledMessages.Count == 6,
            $"the equipment declares {ControlProgramEquipment.HandledMessages.Count} message(s), not six");

        foreach (var (stream, function) in ControlProgramEquipment.HandledMessages)
        {
            Assert(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddControlProgramEquipment did not declare it");
        }

        Assert(
            !equipmentRouter.TryGetHandler(8, 7, out _) && !equipmentRouter.TryGetHandler(8, 9, out _),
            "the equipment declared the blocked transfer, whose block items the reference never defines");

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddControlProgramHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<ControlProgramHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(ControlProgramHost.HandledMessages.Count == 0, "the host declares a message, and stream 8 gives it none");

        for (byte function = 1; function <= 16; function++)
        {
            Assert(
                !hostRouter.TryGetHandler(8, function, out _),
                $"the router has a handler for S8F{function} at a host, so AddControlProgramHost declared one");
        }

        // The two code tables, one quoted and one read off its siblings.
        Assert((byte)ControlProgramGrant.DuplicateOrInvalid == 3, "GRANT 3 moved");
        Assert(Enum.GetNames<ControlProgramAcknowledgement>().Length == 2, "ACKC8 grew a third member");

        var store = equipmentProvider.GetRequiredService<IControlProgramStore>();

        byte[] firmware = [0x7F, 0x45, 0x4C, 0x46];

        var grant = await store.InquireAsync((uint)firmware.Length, CancellationToken.None).ConfigureAwait(false);

        Assert(grant == ControlProgramGrant.Accepted, $"the store refused an inquire: {grant}");

        var acknowledgement = await store.SaveAsync(firmware, CancellationToken.None).ConfigureAwait(false);

        Assert(
            acknowledgement == ControlProgramAcknowledgement.Accepted,
            $"the store refused a program it had just granted room for: {acknowledgement}");

        if (store is ControlProgramInMemoryStore memory)
        {
            var held = memory.Find(ControlProgramInMemoryStore.BootProgramId);

            Assert(held is not null, "the store lost the program it had just accepted");

            // The distinction worth a packaging test: these bytes are the program. The same C#
            // type in Barbatos.SecsGem.DataSets holds an encoded item instead, because that
            // stream's payload has no format to read off.
            Assert(
                held!.Body.Span.SequenceEqual(firmware),
                "the program came back as something other than the bytes that went in");

            memory.Add("NAMED", firmware, "1.4.2");

            var listed = await memory.ListAsync(CancellationToken.None).ConfigureAwait(false);

            Assert(listed.Count == 1 && listed[0] == "NAMED", "the directory listed the nameless boot program");
            Assert(
                await memory.RevisionAsync("NAMED", CancellationToken.None).ConfigureAwait(false) == "1.4.2",
                "the store lost the revision it was given");
        }
    }

    /// <summary>
    /// The recipe package, whose host half declares nothing and whose body is plain bytes.
    /// </summary>
    /// <remarks>
    /// Four claims only a restore from the real <c>.nupkg</c> can check. The dependency, since
    /// stream 15 is E5 and no part of E30. The <strong>empty host half</strong>, which is the
    /// third such registration here and the kind of claim a <c>ProjectReference</c> build cannot
    /// distinguish from a route that silently failed to register. That <strong>nothing in the
    /// package answers stream 19</strong> - the other standard also called Recipe Management,
    /// whose F11 deletes where this stream's verifies. And the <strong>lock</strong>, because
    /// <c>RCPLOCKID</c> travels as an encoded item and a packaging mistake that dropped
    /// <c>RecipeLockToken</c> would leave the four newest pairs unusable while every
    /// <c>ProjectReference</c> build stayed green.
    /// </remarks>
    private static async Task RecipesResolveAsync()
    {
        var equipmentServices = new ServiceCollection();

        equipmentServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddRecipeEquipment();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<RecipeEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            RecipeEquipment.HandledMessages.Count == 14,
            $"the equipment declares {RecipeEquipment.HandledMessages.Count} message(s), not fourteen");

        foreach (var (stream, function) in RecipeEquipment.HandledMessages)
        {
            Assert(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddRecipeEquipment did not declare it");
        }

        // S15F35 is the newest of the fourteen and the only reply in this package that carries no
        // acknowledgement, so a packaging mistake would show as a route that is simply absent.
        Assert(
            RecipeEquipment.HandledMessages.Contains(((byte)15, (byte)35)),
            "the packed equipment does not declare S15F35, the current recipe");

        // And the type the version is carried in, which is public API: a recipe version is the
        // encoded item that arrived, because RCPVER is defined nowhere.
        Assert(
            RecipeVersion.FromText("1.4.2").ToString() == "1.4.2",
            "the packed RecipeVersion does not print the text it was given");

        // The rename, the copy, the lock and the unlock, refused for six chapters and now served:
        // each carries one undefined name, and in three of the four that name extends a defined one
        // sitting in the same body two positions earlier.
        foreach (byte function in (byte[])[19, 21, 23, 25])
        {
            Assert(
                equipmentRouter.TryGetHandler(15, function, out _),
                $"the router has no handler for S15F{function}, which this package serves");
        }

        // And the recipe directory, which is still out and is the gap a user feels: its reply needs
        // RCPATTRNAME and RCPATTRVAL, whose twins in the dictionary disagree about the format.
        Assert(
            !equipmentRouter.TryGetHandler(15, 3, out _) && !equipmentRouter.TryGetHandler(15, 11, out _),
            "the equipment declared the directory or the verify, whose fields the reference does not describe");

        // And the other Recipe Management stream, which is a different standard.
        for (byte function = 1; function <= 20; function++)
        {
            Assert(
                !equipmentRouter.TryGetHandler(19, function, out _),
                $"the equipment declared S19F{function}, which belongs to the other Recipe Management stream");
        }

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddRecipeHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<RecipeHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(RecipeHost.HandledMessages.Count == 0, "the host declares a message, and stream 15 gives it none");

        for (byte function = 1; function <= 54; function++)
        {
            Assert(
                !hostRouter.TryGetHandler(15, function, out _),
                $"the router has a handler for S15F{function} at a host, so AddRecipeHost declared one");
        }

        // RMACK is defined, which is what lets this enum have a third member honestly - unlike
        // ItemAcknowledgement, whose item has no table at all.
        Assert(Enum.GetNames<RecipeAcknowledgement>().Length == 3, "RMACK's table changed shape");
        Assert((byte)RecipeAcknowledgement.Other == 2, "the reserved range no longer starts at 2");

        // The lock token, which is an encoded item because RCPLOCKID is a spelling of nothing. A
        // round trip through the packaged assembly is worth a line because the whole design rests
        // on it: whatever a machine minted comes back unchanged.
        var token = RecipeLockToken.FromText("LOCK-1");

        using (var decoded = token.Decode())
        {
            Assert(decoded is not null, "a lock token decoded to nothing");
            Assert(decoded!.GetString() == "LOCK-1", $"a lock token came back as {decoded.GetString()}");
        }

        Assert(token.Matches(RecipeLockToken.FromText("LOCK-1")), "two tokens with the same bytes did not match");
        Assert(!token.Matches(RecipeLockToken.FromText("LOCK-2")), "two tokens with different bytes matched");

        var store = equipmentProvider.GetRequiredService<IRecipeStore>();

        Assert(store is RecipeInMemoryStore, $"the default store is {store.GetType().Name}, not the in-memory one");

        Assert(
            await store.CreateNamespaceAsync("PROD", CancellationToken.None).ConfigureAwait(false)
                == RecipeAcknowledgement.Accepted,
            "the store refused to create a namespace");

        byte[] recipe = [0x53, 0x54, 0x45, 0x50];

        var granted = await store
            .RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)recipe.Length },
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert(granted == RecipeAcknowledgement.Accepted, $"the store refused a download request: {granted}");

        var acknowledgement = await store
            .SaveAsync("PROD", "ETCH-7", recipe, CancellationToken.None)
            .ConfigureAwait(false);

        Assert(
            acknowledgement == RecipeAcknowledgement.Accepted,
            $"the store refused a recipe it had just granted room for: {acknowledgement}");

        // The distinction worth a packaging test: these bytes are the recipe, because stream 15
        // carries it in stream 7's own PPBODY. The same C# shape in Barbatos.SecsGem.DataSets and
        // Barbatos.SecsGem.ItemTransfer holds an encoded item instead.
        // And what a lock is worth, which is entirely this store's invention: E5 defines the
        // transaction and never says what it prevents.
        var (locked, held) = await store.LockAsync("PROD", "ETCH-7", CancellationToken.None).ConfigureAwait(false);

        Assert(locked == RecipeAcknowledgement.Accepted, $"the store refused to lock a recipe it holds: {locked}");
        Assert(held is not null, "the store accepted a lock and minted no token");

        Assert(
            await store.DeleteAsync("PROD", "ETCH-7", CancellationToken.None).ConfigureAwait(false)
                == RecipeAcknowledgement.Rejected,
            "the store deleted a locked recipe");

        Assert(
            await store.UnlockAsync("PROD", "ETCH-7", held!, CancellationToken.None).ConfigureAwait(false)
                == RecipeAcknowledgement.Accepted,
            "the store refused the token it had just issued");

        var (upload, body) = await store
            .LoadAsync("PROD", "ETCH-7", CancellationToken.None)
            .ConfigureAwait(false);

        Assert(upload == RecipeAcknowledgement.Accepted, "the store lost the recipe it had just accepted");
        Assert(
            body.Span.SequenceEqual(recipe),
            "the recipe came back as something other than the bytes that went in");

        // And the guard that matters most: a namespace holding recipes is not deleted, because
        // S15F17 names no recipe at all.
        Assert(
            await store.DeleteNamespaceAsync("PROD", CancellationToken.None).ConfigureAwait(false)
                == RecipeAcknowledgement.Rejected,
            "the store deleted a namespace that still held a recipe");

        await DurableRecipesResolveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The store that outlives the process, which is the second way into the same registration.
    /// </summary>
    /// <remarks>
    /// Two claims, and the first is a packaging one in the strict sense: <c>AddRecipeFileEquipment</c>
    /// is an extension method on a builder from another package, so a consumer restoring the real
    /// <c>.nupkg</c> is the only thing that proves it is reachable at all. The second is the whole
    /// point of the type - a <em>second</em> store over the same directory answers for a recipe the
    /// first one took - and it needs two instances, which no in-memory store can be asked for.
    /// </remarks>
    /// <summary>
    /// Stream 7's three stores, which is the fifth package the shared mechanism is compiled into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three claims a restored <c>.nupkg</c> is the only thing that can check. That the three
    /// registrations are reachable at all - they are extension methods on a builder from another
    /// package. That a <em>second</em> store over one directory answers for what the first was
    /// given. And that the <strong>extensions keep the three apart</strong>, which is the claim
    /// with teeth: all three records are an identifier and a payload, so a store pointed at
    /// another's directory would otherwise serve its programs - a matrix coming back from S7F5 as a
    /// plain process program is the mistake the separate stores exist to prevent.
    /// </para>
    /// <para>
    /// A formatted program round trip also proves the codec that writes the S7F23 body is in the
    /// packed assembly, since that codec is how the record is written.
    /// </para>
    /// </remarks>
    private static async Task DurableProcessProgramStoresAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "barbatos-smoke-pp-" + Guid.NewGuid().ToString("N"));

        try
        {
            var plain = Path.Combine(root, "plain");
            var formatted = Path.Combine(root, "formatted");
            var matrices = Path.Combine(root, "matrices");

            var services = new ServiceCollection();

            services
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddGemEquipment(model => model.AddStatusVariable(1, "Pressure", () => SecsItem.F8(1d), "mbar"))
                .AddGemFileProcessPrograms(plain)
                .AddGemFileFormattedProcessPrograms(formatted)
                .AddGemFileProcessProgramMatrices(matrices);

            await using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<GemEquipment>();

            var programs = provider.GetRequiredService<IGemProcessProgramStore>();
            var structured = provider.GetRequiredService<IGemFormattedProcessProgramStore>();
            var matrix = provider.GetRequiredService<IGemProcessProgramMatrixStore>();

            Assert(programs is GemFileProcessProgramStore, $"the plain registration produced {programs.GetType().Name}");
            Assert(structured is GemFileFormattedProcessProgramStore, $"the formatted registration produced {structured.GetType().Name}");
            Assert(matrix is GemFileProcessProgramMatrixStore, $"the matrix registration produced {matrix.GetType().Name}");

            byte[] body = [0x53, 0x54, 0x45, 0x50];

            Assert(
                await programs.SaveAsync("RECIPE-7", body, CancellationToken.None).ConfigureAwait(false)
                    == GemProcessProgramAcknowledgement.Accepted,
                "the plain store refused a program");

            Assert(
                await structured.SaveAsync(
                    new GemFormattedProcessProgram("RECIPE-9", "BRBTOS-1", "1.4.2", [new(12, ["SETPOINT", "350.0"])]),
                    CancellationToken.None).ConfigureAwait(false) == GemProcessProgramAcknowledgement.Accepted,
                "the formatted store refused a program");

            Assert(
                await matrix.SaveAsync("MATRIX-1", body, CancellationToken.None).ConfigureAwait(false)
                    == GemProcessProgramAcknowledgement.Accepted,
                "the matrix store refused a matrix");

            // The restart, as far as one process can have one.
            var reopenedPlain = new GemFileProcessProgramStore(plain);
            var reopenedFormatted = new GemFileFormattedProcessProgramStore(formatted);
            var reopenedMatrix = new GemFileProcessProgramMatrixStore(matrices);

            Assert(reopenedPlain.Count == 1, $"a second plain store found {reopenedPlain.Count} program(s), not one");
            Assert(reopenedFormatted.Count == 1, $"a second formatted store found {reopenedFormatted.Count}, not one");
            Assert(reopenedMatrix.Count == 1, $"a second matrix store found {reopenedMatrix.Count}, not one");

            var fetched = await reopenedPlain.LoadAsync("RECIPE-7", CancellationToken.None).ConfigureAwait(false);

            Assert(
                fetched is not null && fetched.Value.Span.SequenceEqual(body),
                "the program came back from the disk as something else");

            Assert(
                await reopenedPlain.InquireAsync("RECIPE-7", body.Length, CancellationToken.None).ConfigureAwait(false)
                    == GemProcessProgramGrant.AlreadyHave,
                "a restarted plain store did not know it already had the program");

            var program = await reopenedFormatted.LoadAsync("RECIPE-9", CancellationToken.None).ConfigureAwait(false);

            Assert(program is { Commands.Count: 1 }, "the formatted program came back without its command");
            Assert(program!.SoftwareRevision == "1.4.2", $"the revision came back as {program.SoftwareRevision}");

            // And the claim with teeth: each store finds nothing in the others' directories.
            Assert(new GemFileProcessProgramStore(matrices).Count == 0, "the plain store adopted a matrix");
            Assert(new GemFileProcessProgramMatrixStore(plain).Count == 0, "the matrix store adopted a program");
            Assert(new GemFileFormattedProcessProgramStore(plain).Count == 0, "the formatted store adopted a plain program");

            // ---------------------------------------------------------------- stream 2
            // The fourth store in this package, and the one with a judgement rather than a format:
            // the text survives and the results of a run do not, because a result is what the
            // machine produced and a restarted machine has produced nothing.
            var servicePrograms = Path.Combine(root, "service");
            var serviceServices = new ServiceCollection();

            serviceServices
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddGemEquipment(model => model.AddStatusVariable(1, "Pressure", () => SecsItem.F8(1d), "mbar"))
                .AddGemFileServicePrograms(servicePrograms);

            await using var serviceProvider = serviceServices.BuildServiceProvider();

            _ = serviceProvider.GetRequiredService<GemEquipment>();

            var service = serviceProvider.GetRequiredService<IGemServiceProgramStore>();

            Assert(service is GemFileServiceProgramStore, $"the service registration produced {service.GetType().Name}");

            // The run delegate is the machine's own wiring, so the registration leaves it refusing
            // and a consumer sets it - which is the shape worth checking from a package.
            ((GemFileServiceProgramStore)service).Run = (id, text) => $"SPID {id}: {text.Length}";

            Assert(
                await service.SaveAsync(7u, "PRINT 1", CancellationToken.None).ConfigureAwait(false)
                    == GemServiceProgramAcknowledgement.Accepted,
                "the service program store refused a program");

            Assert(
                await service.RunAsync(7u, CancellationToken.None).ConfigureAwait(false)
                    == GemServiceProgramAcknowledgement.Accepted,
                "the service program store refused to run a program it holds");

            Assert(
                await service.ResultsAsync(7u, CancellationToken.None).ConfigureAwait(false) == "SPID 7: 7",
                "the results of a run came back wrong");

            var reopenedService = new GemFileServiceProgramStore(servicePrograms);

            Assert(reopenedService.Count == 1, $"a second service store found {reopenedService.Count} program(s), not one");
            Assert(
                await reopenedService.LoadAsync(7u, CancellationToken.None).ConfigureAwait(false) == "PRINT 1",
                "the service program came back from the disk as something else");

            Assert(reopenedService.ResultCount == 0, "a second service store restored the results of a run");
            Assert(
                await reopenedService.ResultsAsync(7u, CancellationToken.None).ConfigureAwait(false) is null,
                "a restarted machine reported results of a run it never made");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A smoke test that cannot tidy up after itself should not fail for it.
            }
        }
    }

    /// <summary>
    /// The three stores beside the recipes' one, each over a directory of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One check for three packages, because what is being checked is the same claim three times:
    /// the registration is reachable from a restored <c>.nupkg</c>, and a <em>second</em> store over
    /// one directory answers for what the first was given. The second half needs two instances,
    /// which no in-memory store can be asked for, and the first half is a packaging claim in the
    /// strict sense - these are extension methods on a builder from another package.
    /// </para>
    /// <para>
    /// The mechanism under all four is one compile-linked source file, so this also proves it
    /// reached all four assemblies: a <c>Compile Include</c> that resolved in the repository and not
    /// in the package would build here and fail at the first record.
    /// </para>
    /// </remarks>
    private static async Task DurableTransferStoresAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "barbatos-smoke-transfer-" + Guid.NewGuid().ToString("N"));

        try
        {
            // ---------------------------------------------------------------- stream 8
            var programs = Path.Combine(root, "programs");
            var firmwareServices = new ServiceCollection();

            firmwareServices
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddControlProgramFileEquipment(programs);

            await using (var provider = firmwareServices.BuildServiceProvider())
            {
                _ = provider.GetRequiredService<ControlProgramEquipment>();

                var store = provider.GetRequiredService<IControlProgramStore>();

                Assert(store is ControlProgramFileStore, $"the file store registration produced {store.GetType().Name}");

                byte[] firmware = [0x7F, 0x45, 0x4C, 0x46];

                // AddAsync and not a transaction, because S8F3 is anonymous: this is the only way a
                // named program exists at all, which is the thing to check reaches a consumer.
                await ((ControlProgramFileStore)store)
                    .AddAsync("BOOT-A", firmware, "1.4.2", CancellationToken.None)
                    .ConfigureAwait(false);

                var reopened = new ControlProgramFileStore(programs);

                Assert(reopened.Count == 1, $"a second control program store found {reopened.Count} program(s), not one");
                Assert(
                    await reopened.RevisionAsync("BOOT-A", CancellationToken.None).ConfigureAwait(false) == "1.4.2",
                    "a second control program store lost the revision");

                var body = await reopened.LoadAsync("BOOT-A", CancellationToken.None).ConfigureAwait(false);

                Assert(
                    body is not null && body.Value.Span.SequenceEqual(firmware),
                    "the firmware came back from the disk as something else");
            }

            // ---------------------------------------------------------------- stream 13
            var dataSets = Path.Combine(root, "datasets");
            var dataSetServices = new ServiceCollection();

            dataSetServices
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddDataSetFileEquipment(dataSets);

            await using (var provider = dataSetServices.BuildServiceProvider())
            {
                _ = provider.GetRequiredService<DataSetEquipment>();

                var store = provider.GetRequiredService<IDataSetStore>();

                Assert(store is DataSetFileStore, $"the file store registration produced {store.GetType().Name}");

                using var contents = SecsItem.L(SecsItem.A("chamber-log"), SecsItem.U4(1, 2, 3));

                await ((DataSetFileStore)store)
                    .AddAsync(41u, DataSetPayload.From(contents), 64u, CancellationToken.None)
                    .ConfigureAwait(false);

                var reopened = new DataSetFileStore(dataSets);

                Assert(reopened.Count == 1, $"a second data set store found {reopened.Count} data set(s), not one");
                Assert(reopened.OpenCount == 0, "a second data set store restored an open");

                var status = await reopened.StatusAsync(41u, CancellationToken.None).ConfigureAwait(false);

                Assert(status.Length == 64u, $"the length came back as {status.Length}, not 64");

                // The open must come first whichever store it is - the sequence is the store's rule.
                var opening = await reopened
                    .OpenAsync(new DataSetOpenRequest { DataSetId = 41u, Mode = DataSetOpenMode.Request }, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert(opening.IsGranted, "a second data set store would not open what it adopted");

                var payload = await reopened
                    .LoadAsync(new DataSetRequest { DataSetId = 41u, DataId = 1u }, CancellationToken.None)
                    .ConfigureAwait(false);

                using var decoded = payload?.Decode();

                Assert(decoded is { Count: 2 }, "the data set came back from the disk as something else");
            }

            // ---------------------------------------------------------------- stream 21
            var items = Path.Combine(root, "items");
            var itemServices = new ServiceCollection();

            itemServices
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddItemFileTransfer(items);

            await using (var provider = itemServices.BuildServiceProvider())
            {
                _ = provider.GetRequiredService<ItemTransferPeer>();

                var store = provider.GetRequiredService<IItemStore>();

                Assert(store is ItemFileStore, $"the file store registration produced {store.GetType().Name}");

                using var revision = SecsItem.A("1.4.2");

                // An item with attributes and no contents, which is the state only this stream has
                // and the one a record with no payload exists for.
                await ((ItemFileStore)store)
                    .AddAsync(
                        new Item { Id = "RECIPE-7", Type = "recipe", Length = 3u },
                        attributes: [new ItemAttribute { Name = "Revision", Value = revision }],
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);

                var reopened = new ItemFileStore(items);

                Assert(reopened.Count == 1, $"a second item store found {reopened.Count} item(s), not one");
                Assert(reopened.GrantedCount == 0, "a second item store restored a transfer grant");
                Assert(
                    await reopened.ContentsAsync("RECIPE-7", CancellationToken.None).ConfigureAwait(false) is null,
                    "an item that was only listed came back carrying contents");

                var attributes = await reopened
                    .AttributesAsync(new ItemAttributeRequest { ItemId = "RECIPE-7" }, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert(attributes.Count == 1, $"a second item store found {attributes.Count} attribute(s), not one");
                Assert(
                    attributes[0].Value.GetString() == "1.4.2",
                    "an attribute came back from the disk as something else");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A smoke test that cannot tidy up after itself should not fail for it.
            }
        }
    }

    private static async Task DurableRecipesResolveAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "barbatos-smoke-recipes-" + Guid.NewGuid().ToString("N"));

        try
        {
            var services = new ServiceCollection();

            services
                .AddSecsGem()
                .UseInMemoryTransport()
                .AddRecipeFileEquipment(directory);

            await using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<RecipeEquipment>();

            var store = provider.GetRequiredService<IRecipeStore>();

            Assert(store is RecipeFileStore, $"the file store registration produced {store.GetType().Name}");

            var router = provider.GetRequiredService<ISecsMessageRouter>();

            foreach (var (stream, function) in RecipeEquipment.HandledMessages)
            {
                Assert(
                    router.TryGetHandler(stream, function, out _),
                    $"the router has no handler for S{stream}F{function}, so AddRecipeFileEquipment did not declare it");
            }

            byte[] recipe = [0x53, 0x54, 0x45, 0x50, 0x20, 0x31];

            Assert(
                await store.CreateNamespaceAsync("PROD", CancellationToken.None).ConfigureAwait(false)
                    == RecipeAcknowledgement.Accepted,
                "the file store refused to create a namespace");

            Assert(
                await store
                    .RequestDownloadAsync(
                        new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)recipe.Length },
                        CancellationToken.None)
                    .ConfigureAwait(false) == RecipeAcknowledgement.Accepted,
                "the file store refused a download request");

            Assert(
                await store.SaveAsync("PROD", "ETCH-7", recipe, CancellationToken.None).ConfigureAwait(false)
                    == RecipeAcknowledgement.Accepted,
                "the file store refused a recipe it had just granted room for");

            // The restart, as far as a single process can have one: nothing is shared with the
            // store above but the directory.
            var reopened = new RecipeFileStore(directory);

            Assert(reopened.Count == 1, $"a store over the same directory found {reopened.Count} recipe(s), not one");
            Assert(reopened.HasNamespace("PROD"), "a store over the same directory lost the namespace");
            Assert(reopened.GrantedCount == 0, "a store over the same directory restored a download grant");

            var (upload, body) = await reopened
                .LoadAsync("PROD", "ETCH-7", CancellationToken.None)
                .ConfigureAwait(false);

            Assert(upload == RecipeAcknowledgement.Accepted, "a store over the same directory would not hand the recipe back");
            Assert(
                body.Span.SequenceEqual(recipe),
                "the recipe came back from the disk as something other than the bytes that went in");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A smoke test that cannot tidy up after itself should not fail for it.
            }
        }
    }

    /// <summary>
    /// The item transfer package, which has one end type and one registration.
    /// </summary>
    /// <remarks>
    /// Three claims only a restore from the real <c>.nupkg</c> can check. The dependency, since
    /// stream 21 is E5 and no part of E30. The <strong>single registration</strong>, because this
    /// is one of two packages here that both ends of a link call the same method on - a consumer
    /// looking for <c>AddItemTransferHost</c> will not find one, and the package has to make that
    /// obvious rather than puzzling. And the cycle: the end needs the connection, the connection
    /// needs the router, and the router is where the end is declared, so a resolve that works
    /// against <c>bin/</c> but not against the package would show up here and nowhere else.
    /// </remarks>
    private static async Task ItemTransferResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddItemTransfer();

        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ItemTransferPeer>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            ItemTransferPeer.HandledMessages.Count == 6,
            $"the end declares {ItemTransferPeer.HandledMessages.Count} message(s), not six");

        foreach (var (stream, function) in ItemTransferPeer.HandledMessages)
        {
            Assert(
                router.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddItemTransfer did not declare it");
        }

        // The blocked transfer and the checksum pair, neither of which this package implements.
        foreach (byte function in (byte[])[9, 11, 13, 19])
        {
            Assert(
                !router.TryGetHandler(21, function, out _),
                $"the end declared S21F{function}, which the reference leaves undefined where it counts");
        }

        // The grant table this package shares with two others. One item cannot mean two things in
        // one repository, and a restore is where a version skew between packages would show.
        Assert(
            (byte)ItemTransferGrant.DuplicateOrInvalid == (byte)ControlProgramGrant.DuplicateOrInvalid,
            "GRANT 3 means different things in two packages of the same version");

        Assert(Enum.GetNames<ItemAcknowledgement>().Length == 2, "ACKC21 grew a third member");

        var store = provider.GetRequiredService<IItemStore>();

        Assert(
            store is ItemInMemoryStore,
            $"the default store is {store.GetType().Name}, not the in-memory one");

        var granted = await store
            .RequestAsync(new ItemTransferRequest { ItemId = "ITEM", DataId = 1, Length = 1 }, CancellationToken.None)
            .ConfigureAwait(false);

        Assert(granted == ItemTransferGrant.Accepted, $"the store refused a transfer: {granted}");

        using var contents = SecsItem.A("the item itself");

        var acknowledgement = await store
            .SaveAsync("ITEM", 1, ItemPayload.From(contents), CancellationToken.None)
            .ConfigureAwait(false);

        Assert(
            acknowledgement == ItemAcknowledgement.Accepted,
            $"the store refused an item it had just granted room for: {acknowledgement}");

        var held = ((ItemInMemoryStore)store).Contents("ITEM");

        Assert(held is not null, "the store lost the item it had just accepted");

        // The distinction worth a packaging test, and the counterpart of the one stream 8 makes:
        // these bytes are an encoded item, header and all, because ITEMDATA has no format to read
        // off. Decoding has to give back what went in.
        using var decoded = held!.Decode();

        Assert(
            decoded is not null && decoded.GetString() == "the item itself",
            "the item came back as something other than what went in");
    }

    /// <summary>
    /// The subsystem package, whose two ends declare disjoint sets.
    /// </summary>
    /// <remarks>
    /// Two claims only a restore from the real <c>.nupkg</c> can check. One is the dependency:
    /// stream 18 is E5 and no part of E30, so a subsystem controller that never references Gem has
    /// to work. The other is the split - six primaries at the equipment and two at the host, with
    /// nothing in common - because nothing at runtime would complain if a machine declared the
    /// wrong one.
    /// </remarks>
    private static async Task SubsystemsResolveAsync()
    {
        var equipmentServices = new ServiceCollection();

        equipmentServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddSubsystemEquipment();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<SubsystemEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            SubsystemEquipment.HandledMessages.Count == 6,
            $"the equipment declares {SubsystemEquipment.HandledMessages.Count} message(s), not six");

        foreach (var (stream, function) in SubsystemEquipment.HandledMessages)
        {
            Assert(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddSubsystemEquipment did not declare it");
        }

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddSubsystemHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<SubsystemHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            SubsystemHost.HandledMessages.Count == 2,
            $"the host declares {SubsystemHost.HandledMessages.Count} message(s), not two");

        foreach (var (stream, function) in SubsystemHost.HandledMessages)
        {
            Assert(
                hostRouter.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddSubsystemHost did not declare it");

            Assert(
                !equipmentRouter.TryGetHandler(stream, function, out _),
                $"the equipment claimed S{stream}F{function}, which it is the one that sends");
        }

        foreach (var (stream, function) in SubsystemEquipment.HandledMessages)
        {
            Assert(
                !hostRouter.TryGetHandler(stream, function, out _),
                $"the host claimed S{stream}F{function}, which it is the one that sends");
        }

        // And the identifier, which is the package's answer to an SSID the dictionary never
        // defines: a number and a name that read alike are two subsystems.
        var store = equipmentProvider.GetRequiredService<ISubsystemStore>();

        Assert(
            SubsystemId.Numbered(7) != SubsystemId.Named("7"),
            "a numbered identifier compared equal to a name that reads the same");

        Assert(!default(SubsystemId).IsNumber, "the default identifier is a number, so an unset one addresses subsystem zero");

        if (store is SubsystemInMemoryStore memory)
        {
            memory.Add(SubsystemId.Named("CHAMBER-A"), "Process chamber A", "Idle");
            memory.Set(SubsystemId.Named("CHAMBER-A"), 1001, "23.9");

            var listed = await memory.ListAsync(CancellationToken.None).ConfigureAwait(false);

            Assert(listed.Count == 1, $"the store listed {listed.Count} subsystem(s), not one");
            Assert(listed[0].State == "Idle", $"the store reported the state as '{listed[0].State}'");

            var written = await memory.WriteAsync(
                new SubsystemVariable { Subsystem = SubsystemId.Named("CHAMBER-A"), VariableId = 1001, Value = "25.0" },
                CancellationToken.None).ConfigureAwait(false);

            Assert(
                written == SubsystemAcknowledgement.Accepted,
                $"the store refused a write to a variable it has: {written}");

            var unknown = await memory.WriteAsync(
                new SubsystemVariable { Subsystem = SubsystemId.Numbered(7), VariableId = 1, Value = "x" },
                CancellationToken.None).ConfigureAwait(false);

            Assert(
                unknown == SubsystemAcknowledgement.UnknownVariable,
                $"the store accepted a write to a subsystem it does not have: {unknown}");
        }
    }

    /// <summary>
    /// The material package, whose two ends declare different messages.
    /// </summary>
    /// <remarks>
    /// Stream 3 is the one where both ends send primaries, so the two registrations declare
    /// disjoint sets - five and four - and the routing table could not catch the two being mixed
    /// up. Two containers, as a fab has two processes.
    /// </remarks>
    private static async Task MaterialResolvesAsync()
    {
        var equipmentServices = new ServiceCollection();

        equipmentServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddMaterialEquipment()
            .AddMaterialHandoff()

            // And the job half, which the stream 4 chapter added: a separate registration because
            // these four pairs are directional where the seventeen above travel either way.
            .AddMaterialTransferJobs();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<MaterialEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            equipmentRouter.TryGetHandler(3, 1, out _) && equipmentRouter.TryGetHandler(3, 11, out _),
            "the router is missing one of the host's questions, so AddMaterialEquipment did not declare them");

        Assert(
            !equipmentRouter.TryGetHandler(3, 5, out _),
            "the equipment claimed S3F5, which is a message it is the one that sends");

        // Stream 4's handoff, which is neither end of anything: the same seventeen primaries are
        // declared wherever AddMaterialHandoff is called, and this end also sends all seventeen.
        // Two protocols in one type - the interlocked handshake's nine and the handoff protocol's
        // eight - with disjoint function numbers.
        _ = equipmentProvider.GetRequiredService<MaterialHandoff>();
        _ = equipmentProvider.GetRequiredService<MaterialTransferJobs>();

        Assert(
            MaterialHandoff.HandledMessages.Count == 17,
            $"the packed handoff declares {MaterialHandoff.HandledMessages.Count} primaries, not all seventeen of stream 4");

        Assert(
            MaterialHandoff.HandledMessages.Contains(((byte)4, (byte)27))
                && MaterialHandoff.HandledMessages.Contains(((byte)4, (byte)41)),
            "the packed handoff is missing the handoff protocol, so only the interlocked handshake came out of the package");

        // The job half is a type of its own, because unlike the seventeen above these four pairs
        // are directional - so the handoff list must still not carry them.
        Assert(
            !MaterialHandoff.HandledMessages.Contains(((byte)4, (byte)19))
                && !MaterialHandoff.HandledMessages.Contains(((byte)4, (byte)25)),
            "the packed handoff declares a transfer job row, which belongs to MaterialTransferJobs");

        Assert(
            MaterialTransferJobs.EquipmentMessages.Count == 3,
            $"the packed job half declares {MaterialTransferJobs.EquipmentMessages.Count} equipment primaries, not three");

        Assert(
            MaterialTransferJobs.HostMessages.Count == 1,
            $"the packed job half declares {MaterialTransferJobs.HostMessages.Count} host primaries, not one");

        // Stream 4 whole: seventeen plus four is every primary the reference gives it, and the
        // three lists share nothing - a stream and function may be declared only once.
        Assert(
            MaterialHandoff.HandledMessages.Count
                + MaterialTransferJobs.EquipmentMessages.Count
                + MaterialTransferJobs.HostMessages.Count == 21,
            "the packed stream 4 is not served whole");

        Assert(
            !MaterialTransferJobs.EquipmentMessages.Intersect(MaterialTransferJobs.HostMessages).Any()
                && !MaterialTransferJobs.EquipmentMessages.Intersect(MaterialHandoff.HandledMessages).Any()
                && !MaterialTransferJobs.HostMessages.Intersect(MaterialHandoff.HandledMessages).Any(),
            "two of the packed stream 4 lists declare the same primary");

        // And neither acknowledgement of the job half is interpreted, which is the decision the
        // capability turns on and the one a packaging mistake could not show but an API change
        // could: an IsAccepted back on this type would mean the rule had been forgotten.
        Assert(
            typeof(MaterialTransferJobResult).GetProperty("IsAccepted") is null,
            "MaterialTransferJobResult has an IsAccepted again, and neither TRACK nor ERRCODE is tabled");

        foreach (var (stream, function) in MaterialHandoff.HandledMessages)
        {
            Assert(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} has no handler, so AddMaterialHandoff did not declare the handoff");
        }

        // And the enums the dictionary gave its own tables of values to, out of the packed
        // assembly: an equipment-specific code must not arrive as acceptance.
        Assert(
            (byte)MaterialSendAcknowledgement.WillAccept == 0 && (byte)MaterialSendAcknowledgement.Other == 5,
            "RSACK's codes did not travel as E5 numbers them");

        Assert(
            (byte)MaterialReceiveAcknowledgement.Granted == 0 && (byte)MaterialReceiveAcknowledgement.Other == 2,
            "RRACK's codes did not travel as E5 numbers them");

        var hostServices = new ServiceCollection();

        hostServices
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddMaterialHost();

        await using var hostProvider = hostServices.BuildServiceProvider();

        _ = hostProvider.GetRequiredService<MaterialHost>();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            hostRouter.TryGetHandler(3, 5, out _) && hostRouter.TryGetHandler(3, 35, out _),
            "the router is missing one of the equipment's reports, so AddMaterialHost did not declare them");

        Assert(
            !hostRouter.TryGetHandler(3, 1, out _),
            "the host claimed S3F1, which is a message it is the one that sends");

        // And the inventory out of the packaged assembly: a transfer it accepts moves the material,
        // which is the one piece of behaviour in that class worth checking from outside.
        var inventory = (MaterialInMemorySource)equipmentProvider.GetRequiredService<IMaterialSource>();

        Assert(inventory.Load("LOT-SMOKE", location: 1, quantity: 25), "the inventory refused material it had room for");

        Assert(
            await inventory.TransferAsync(
                new MaterialTransfer { MaterialId = "LOT-SMOKE", Destination = 9 },
                CancellationToken.None).ConfigureAwait(false) == MaterialAcknowledgement.Accepted,
            "the inventory denied a transfer of material it holds");

        var moved = await inventory.FindAsync("LOT-SMOKE", CancellationToken.None).ConfigureAwait(false);

        Assert(moved is not null, "the inventory lost material it had just moved");
        Assert(moved!.Location == 9, $"the transfer did not move it: location is {moved.Location}");

        Assert(
            await inventory.TransferAsync(
                new MaterialTransfer { MaterialId = "nobody", Destination = 9 },
                CancellationToken.None).ConfigureAwait(false) == MaterialAcknowledgement.Denied,
            "the inventory accepted a transfer of material it does not hold");
    }

    /// <summary>
    /// The object services package, where the registration encodes a decision.
    /// </summary>
    /// <remarks>
    /// Stream 14 is symmetric, so what separates the two registrations is whether this end has
    /// objects to answer about - and that shows up in the routing table rather than in the API.
    /// Worth checking from a packaged assembly, because it is the one thing about this package a
    /// consumer gets wrong by guessing.
    /// </remarks>
    private static async Task ObjectServicesResolveAsync()
    {
        var askOnly = new ServiceCollection();

        askOnly
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddObjectServices();

        await using var askProvider = askOnly.BuildServiceProvider();

        var asker = askProvider.GetRequiredService<ObjectServices>();
        var askRouter = askProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(!asker.CanAnswer, "an end with no object provider says it can answer");
        Assert(
            !askRouter.TryGetHandler(14, 1, out _),
            "an end with no objects declared S14F1, so it will answer GetAttr with an empty list instead of S9F5");

        var withObjects = new ServiceCollection();

        withObjects
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddObjectProvider();

        await using var objectProvider = withObjects.BuildServiceProvider();

        Assert(objectProvider.GetRequiredService<ObjectServices>().CanAnswer, "an end with a provider says it cannot answer");

        var router = objectProvider.GetRequiredService<ISecsMessageRouter>();

        // Twelve since the gapless sweep: eleven of stream 14 - the seven reading primaries plus
        // create, delete and the two actions - and S1F19, stream 1's one E39 row. That row is here
        // rather than in the GEM package because its items are stream 14's, and it is served rather
        // than refused as a duplicate of S14F1 because its reply can say why it failed.
        Assert(ObjectServices.HandledMessages.Count == 14, $"the stream declares {ObjectServices.HandledMessages.Count} primaries, not fourteen");

        Assert(
            ObjectServices.HandledMessages.Contains(((byte)14, (byte)11))
                && ObjectServices.HandledMessages.Contains(((byte)14, (byte)25)),
            "the packed object services lost the attribute query or the error report");

        Assert(
            !ObjectServices.HandledMessages.Contains(((byte)14, (byte)23)),
            "the packed object services declares S14F23, whose ENABLE has no definition at all");

        Assert(
            ObjectServices.HandledMessages.Contains(((byte)1, (byte)19)),
            "the packed object services lost S1F19, so stream 1's E39 row did not come out of the package");

        Assert(
            !askRouter.TryGetHandler(1, 19, out _),
            "an end with no objects declared S1F19, so it will answer an attribute get instead of S9F5");

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert(
                router.TryGetHandler(stream, function, out _),
                $"the router has no handler for S{stream}F{function}, so AddObjectProvider did not declare it");
        }

        // And an attribute value out of the packaged assembly: ATTRDATA has no format of its own,
        // so the thing worth checking is that a structure survives being stored and read back.
        var objects = (ObjectInMemoryProvider)objectProvider.GetRequiredService<IObjectProvider>();

        objects.Add("CAR-SMOKE", "Carrier");

        using (var slotMap = SecsItem.L(SecsItem.A("1"), SecsItem.U1(1, 2, 3)))
        {
            Assert(objects.Set("CAR-SMOKE", "SlotMap", slotMap), "the provider refused an attribute on an object it holds");
        }

        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["CAR-SMOKE"], Attributes = ["SlotMap"] },
            CancellationToken.None).ConfigureAwait(false);

        Assert(values.Count == 1, $"the provider returned {values.Count} object(s), not one");

        var attribute = values[0].Attributes[0];

        Assert(attribute.Name == "SlotMap", $"the attribute came back as {attribute.Name}");
        Assert(attribute.Value.IsList && attribute.Value.Count == 2, "the structure did not survive the store");
        Assert(attribute.Value.Items[1].Count == 3, "the inner list lost its values");

        attribute.Value.Dispose();
    }

    /// <summary>
    /// The carrier package, which is the one that enforces rather than carries.
    /// </summary>
    /// <remarks>
    /// Two things only a packaged assembly can check. One: registering the equipment declares both
    /// halves of E87 - the five carrier primaries and the seven of object services - because a
    /// machine that declared only the first five would answer every action and never give up a
    /// slot map. Two: the state machines actually refuse, which is the whole of what this package
    /// is for and would be a silent no-op if the build had dropped something.
    /// </remarks>
    private static async Task CarriersResolveAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager =>
            {
                manager.AddPort(1);
                manager.AddStorageLocation(101);
                manager.AddStorageLocation(102);
            });

        await using var provider = services.BuildServiceProvider();

        var equipment = provider.GetRequiredService<CarrierEquipment>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The four added in the chapters that read E87's remaining stream 3: declared out of the
        // packed assembly, which is the only place a declaration list that did not travel shows
        // up as a message nobody answers.
        Assert(
            router.TryGetHandler(3, 19, out _)
                && router.TryGetHandler(3, 21, out _)
                && router.TryGetHandler(3, 27, out _)
                && router.TryGetHandler(3, 31, out _),
            "S3F19, S3F21, S3F27 or S3F31 has no handler, so the equipment will answer S9F5 for a request it implements");

        Assert(
            CarrierEquipment.HandledMessages.Count == 9,
            $"the packed equipment declares {CarrierEquipment.HandledMessages.Count} primaries, not all nine of E87's stream 3");

        Assert(
            router.TryGetHandler(3, 17, out _) && router.TryGetHandler(3, 33, out _),
            "the router is missing a carrier primary, so AddCarrierEquipment did not declare them");

        Assert(
            router.TryGetHandler(14, 1, out _) && router.TryGetHandler(14, 17, out _),
            "the router is missing object services, so a host could never read a slot map");

        Assert(
            provider.GetRequiredService<IObjectProvider>() is CarrierObjectProvider,
            "stream 14 is not answered out of the carriers");

        // The third object type, out of the packed assembly: a storage location is an object as
        // well as an S3F31 row, and the objspec convention is public API that has to travel.
        var carrierObjects = (CarrierObjectProvider)provider.GetRequiredService<IObjectProvider>();
        var types = await carrierObjects.GetObjectTypesAsync(CancellationToken.None).ConfigureAwait(false);

        Assert(
            types.Contains(CarrierObjectProvider.StorageLocationType),
            $"the object types are [{string.Join(", ", types)}], with no storage location among them");

        Assert(
            CarrierObjectProvider.StorageSpecification(101) == "StorageLocation101",
            $"the objspec convention came out as '{CarrierObjectProvider.StorageSpecification(101)}'");

        Assert(
            CarrierObjectProvider.TryReadStorageSpecification("StorageLocation101") == 101,
            "the objspec convention does not read its own output back");

        // And the rules, through the packaged assembly: a carrier nobody has verified cannot be
        // accessed, and the refusal says which state refused it.
        var manager = provider.GetRequiredService<CarrierManager>();

        Assert(manager.SetServiceStatus(1, inService: true).IsAccepted, "the port refused to go in service");
        Assert(manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted, "the port refused to be ready to load");
        Assert(manager.PlaceCarrier(1, "CAR-SMOKE").IsAccepted, "the port refused a carrier it was ready for");

        var tooEarly = manager.BeginAccess("CAR-SMOKE");

        Assert(!tooEarly.IsAccepted, "an unverified carrier was accessed");
        Assert(
            tooEarly.Acknowledgement == CarrierAcknowledgement.CannotPerformNow,
            $"the refusal came back as {tooEarly.Acknowledgement}");

        Assert(tooEarly.ErrorText.Contains("not verified", StringComparison.Ordinal), $"the refusal said '{tooEarly.ErrorText}'");

        Assert(manager.ReadCarrierId("CAR-SMOKE", verified: true).IsAccepted, "the identifier was refused");
        Assert(manager.ReadSlotMap("CAR-SMOKE", new byte[] { 1, 1, 0 }, verified: true).IsAccepted, "the slot map was refused");
        Assert(manager.BeginAccess("CAR-SMOKE").IsAccepted, "a verified carrier was not accessed");

        var carrier = manager.FindCarrier("CAR-SMOKE");

        Assert(carrier is not null, "the carrier went missing");
        Assert(carrier!.AccessStatus == CarrierAccessStatus.InAccess, $"the carrier is {carrier.AccessStatus}");
        Assert(carrier.SubstrateCount == 2, $"the slot map counted {carrier.SubstrateCount} substrates, not two");

        // And what S3F20 would say about it, out of the packed assembly: the default reading of
        // CARRIERSTATUS is the access status by name, which is also what stream 14 serves.
        Assert(
            equipment.StatusOf(carrier) == nameof(CarrierAccessStatus.InAccess),
            $"CARRIERSTATUS would have said '{equipment.StatusOf(carrier)}'");

        // And the storage locations, which are places rather than ports. Checked through the
        // packaged assembly because a carrier in one answers S3F24 with the shelf, and that
        // default is the line of this chapter nothing else would notice if it travelled wrong.
        Assert(manager.SetStorageServiceStatus(101, inService: true).IsAccepted, "the shelf refused to go in service");
        Assert(
            manager.SetStorageTransferState(101, PortTransferState.ReadyToLoad).IsAccepted,
            "the shelf refused to be ready to load");
        Assert(manager.StoreCarrier(101, "CAR-SHELF").IsAccepted, "the shelf refused a carrier it was ready for");
        Assert(
            manager.FindCarrier("CAR-SHELF")?.StorageLocation == 101,
            "the carrier does not know which shelf it is in");

        var stranded = manager.SetStorageServiceStatus(101, inService: false);

        Assert(!stranded.IsAccepted, "an occupied shelf went out of service, stranding the carrier in it");

        // A carrier is in one place: CAR-SMOKE is on port 1, so a shelf that is ready for it still
        // will not take it.
        Assert(manager.SetStorageServiceStatus(102, inService: true).IsAccepted, "the second shelf refused to go in service");
        Assert(
            manager.SetStorageTransferState(102, PortTransferState.ReadyToLoad).IsAccepted,
            "the second shelf refused to be ready to load");

        var twoPlaces = manager.StoreCarrier(102, "CAR-SMOKE");

        Assert(!twoPlaces.IsAccepted, "a carrier on a port was put on a shelf as well");
        Assert(
            twoPlaces.ErrorText.Contains("one place", StringComparison.Ordinal),
            $"the refusal said '{twoPlaces.ErrorText}'");
    }

    /// <summary>
    /// E87 and E90 in one machine, which is the thing no single package can check.
    /// </summary>
    /// <remarks>
    /// Stream 14 has one provider per connection, and two packages each contribute one. Until they
    /// composed, the second registration quietly did nothing - a machine would answer carrier
    /// attributes and no substrate attributes at all, with nothing anywhere saying so. Worth a
    /// check from a packaged assembly, because it is the one fault in this stack that a passing
    /// build and a passing test run would both have missed.
    /// </remarks>
    private static async Task CarriersAndSubstratesComposeAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker =>
            {
                tracker.AddLocation("SLOT-1");
                tracker.AddLocation("CHUCK");
            });

        await using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<SubstrateTracker>();

        Assert(tracker.Add("W01", "SLOT-1", slotId: 3).IsAccepted, "the tracker refused a substrate at an empty location");
        Assert(tracker.MoveTo("W01", "CHUCK").IsAccepted, "the substrate could not be moved to work");
        Assert(tracker.BeginProcessing("W01").IsAccepted, "work could not start on a substrate at work");

        var tooLate = tracker.MoveTo("W01", "SLOT-1");

        Assert(!tooLate.IsAccepted, "a substrate in process was moved");
        Assert(tooLate.Reason.Contains("InProcess", StringComparison.Ordinal), $"the refusal said '{tooLate.Reason}'");

        // The composition: one provider behind the connection, answering for both standards.
        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert(objects is not null, "the machine has no object provider at all");
        Assert(objects is CompositeObjectProvider, $"the provider is a {objects!.GetType().Name}, so one package lost");

        var types = await objects.GetObjectTypesAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var expected in new[] { "Carrier", "LoadPort", "Substrate", "SubstrateLocation" })
        {
            Assert(types.Contains(expected, StringComparer.Ordinal), $"stream 14 does not answer for {expected}");
        }

        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["W01"], Attributes = ["SubstrateProcessingState", "SlotID"] },
            CancellationToken.None).ConfigureAwait(false);

        Assert(values.Count == 1, $"the substrate came back {values.Count} time(s)");

        var read = values[0].Attributes;

        Assert(read.Count == 2, $"the substrate answered {read.Count} attribute(s), not two");

        foreach (var attribute in read)
        {
            if (attribute.Name == "SlotID")
            {
                Assert(attribute.Value.FirstValueOrDefault<byte>() == 3, "the slot came back wrong");
            }
            else
            {
                Assert(
                    attribute.Value.GetString() == nameof(SubstrateProcessingState.InProcess),
                    $"the processing state came back as {attribute.Value.GetString()}");
            }

            attribute.Value.Dispose();
        }
    }

    private static async Task ControlJobsResolveAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
            .AddControlJobs();

        await using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<ControlJobManager>();

        Assert(manager.Create("CJ-1", ["CAR-001"]).IsAccepted, "the manager refused a new control job");

        var tooEarly = manager.Start("CJ-1");

        Assert(!tooEarly.IsAccepted, "a queued control job was started");
        Assert(tooEarly.Reason.Contains("Queued", StringComparison.Ordinal), $"the refusal said '{tooEarly.Reason}'");

        // Three packages, one stream, five object types. A packaging mistake that left one of them
        // out of the composite would show up here and nowhere in the test suite.
        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert(objects is CompositeObjectProvider composite && composite.Providers.Count == 3,
            "stream 14 is not answered by all three packages");

        var types = await objects!.GetObjectTypesAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var expected in new[] { "Carrier", "LoadPort", "Substrate", "SubstrateLocation", "ControlJob" })
        {
            Assert(types.Contains(expected, StringComparer.Ordinal), $"stream 14 does not answer for {expected}");
        }

        // And the actionable half, which is what E94 needed built: an action has to find the one
        // provider of three that owns the object.
        var acted = await objects.ActAsync(
            new ObjectActionRequest { Objects = ["CJ-1"], Command = ControlJobCommands.Select },
            CancellationToken.None).ConfigureAwait(false);

        Assert(acted.Count == 1, $"the action came back {acted.Count} time(s)");
        Assert(acted[0].Acknowledgement == ObjectAcknowledgement.Successful, $"the action answered {acted[0].Acknowledgement}");
        Assert(manager.Find("CJ-1")?.State == ControlJobState.Selected, "the command did not reach the state machine");

        var created = await objects.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-2") }],
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert(created.Acknowledgement == ObjectAcknowledgement.Successful, $"the create answered {created.Acknowledgement}");
        Assert(created.ObjectId == "CJ-2", $"the machine named the job '{created.ObjectId}'");
        Assert(manager.Find("CJ-2") is not null, "the created job is not in the manager");
    }

    private static async Task ProcessJobsResolveAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
            .AddControlJobs()
            .AddProcessJobEquipment(manager => manager.JobSpace = 4);

        await using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<ProcessJobManager>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Stream 16 is E40's own, and a ProjectReference build cannot tell whether the handler
        // registration survived packing.
        Assert(router.TryGetHandler(16, 1, out _), "S16F1 has no handler");
        Assert(router.TryGetHandler(16, 23, out _), "S16F23 has no handler");
        Assert(ProcessJobEquipment.HandledMessages.Count == 12, $"E40 declares {ProcessJobEquipment.HandledMessages.Count} primaries, not twelve");

        Assert(manager.Create("PJ-1", "RECIPE-7", ["W01"]).IsAccepted, "the manager refused a new process job");

        var tooEarly = manager.Start("PJ-1");

        Assert(!tooEarly.IsAccepted, "a queued process job was started");
        Assert(tooEarly.Reason.Contains("Queued", StringComparison.Ordinal), $"the refusal said '{tooEarly.Reason}'");

        // The transient states, which are E40's own shape: a pause is accepted, not completed.
        Assert(manager.BeginSetup("PJ-1").IsAccepted, "setup could not begin");
        Assert(manager.SetupComplete("PJ-1").IsAccepted, "setup could not finish");
        Assert(manager.Pause("PJ-1").IsAccepted, "a processing job could not be paused");
        Assert(manager.Find("PJ-1")?.State == ProcessJobState.Pausing, "a pause landed somewhere other than Pausing");
        Assert(manager.PauseComplete("PJ-1").IsAccepted, "the machine could not finish pausing");

        // Four packages, one stream, six object types. A packaging mistake that left one out of
        // the composite would show up here and nowhere in the test suite.
        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert(objects is CompositeObjectProvider composite && composite.Providers.Count == 4,
            "stream 14 is not answered by all four packages");

        var types = await objects!.GetObjectTypesAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var expected in new[]
        {
            "Carrier",
            "LoadPort",
            "Substrate",
            "SubstrateLocation",
            "ControlJob",
            ProcessJobObjectProvider.ProcessJobType,
        })
        {
            Assert(types.Contains(expected, StringComparer.Ordinal), $"stream 14 does not answer for {expected}");
        }

        // And the state reads as PRSTATE's code, which is the one number in this package that
        // came out of the catalogue rather than out of a reading of it.
        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["PJ-1"], Attributes = ["PRSTATE"] },
            CancellationToken.None).ConfigureAwait(false);

        Assert(values.Count == 1, $"the job came back {values.Count} time(s)");

        var attribute = values[0].Attributes[0];

        Assert(attribute.Name == "PRSTATE", $"the attribute came back as {attribute.Name}");
        Assert(
            attribute.Value.FirstValueOrDefault<byte>() == (byte)ProcessJobState.Paused,
            $"the state came back as {attribute.Value.FirstValueOrDefault<byte>()}, not {(byte)ProcessJobState.Paused}");

        attribute.Value.Dispose();
    }

    private static async Task MaterialFlowResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager =>
            {
                manager.AddPort(1);
                manager.SetServiceStatus(1, inService: true);
            })
            .AddSubstrateTracking(tracker => tracker.AddLocation("CHUCK"))
            .AddCarrierSubstrateBridge(options => options.SubstrateType = "300mm Si");

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<CarrierSubstrateBridge>();
        var carriers = provider.GetRequiredService<CarrierManager>();
        var substrates = provider.GetRequiredService<SubstrateTracker>();

        // Attached as soon as it resolves, which a ProjectReference build cannot tell apart from
        // a registration that packed wrong.
        Assert(bridge.IsAttached, "the bridge resolved detached");
        Assert(bridge.Options.SubstrateType == "300mm Si", "the registration's options did not reach the bridge");

        Assert(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted, "port 1 would not take a carrier");
        Assert(carriers.PlaceCarrier(1, "CAR-001").IsAccepted, "the carrier could not be placed");
        Assert(carriers.ReadCarrierId("CAR-001", verified: true).IsAccepted, "the identifier would not verify");

        // Not yet: an unread map is not a bill of materials.
        Assert(substrates.Substrates.Count == 0, "substrates appeared before the slot map was read");

        Assert(
            carriers.ReadSlotMap("CAR-001", new byte[] { 1, 1, 0, 1, 1 }, verified: true).IsAccepted,
            "the slot map would not verify");

        // Four wafers out of five slots, and not a line of application code in between.
        Assert(substrates.Substrates.Count == 4, $"the slot map became {substrates.Substrates.Count} substrates, not four");
        Assert(bridge.SubstratesOf("CAR-001").Count == 4, "the bridge does not remember what it created");

        var wafer = substrates.FindSubstrate("CAR-001-04");

        Assert(wafer is not null, "slot four's wafer is not there under the name the convention gives it");
        Assert(wafer!.LocationId == "CAR-001-SLOT-04", $"it is at {wafer.LocationId}");
        Assert(wafer.DestinationLocationId == "CAR-001-SLOT-04", "it has nowhere to go back to");
        Assert(wafer.SubstrateType == "300mm Si", $"its type is '{wafer.SubstrateType}'");
        Assert(substrates.FindSubstrate("CAR-001-03") is null, "the empty slot became a wafer");
    }

    private static async Task ProcessJobFlowResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddSubstrateTracking(tracker =>
            {
                tracker.AddLocation("CHUCK");
                tracker.AddLocation("SLOT-1");
                tracker.AddLocation("SLOT-2");
            })
            .AddProcessJobEquipment()
            .AddProcessJobSubstrateBridge();

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ProcessJobSubstrateBridge>();
        var substrates = provider.GetRequiredService<SubstrateTracker>();
        var jobs = provider.GetRequiredService<ProcessJobManager>();

        Assert(bridge.IsAttached, "the process job bridge resolved detached");

        Assert(substrates.Add("W01", "SLOT-1", slotId: 1).IsAccepted, "W01 could not be tracked");
        Assert(substrates.Add("W02", "SLOT-2", slotId: 2).IsAccepted, "W02 could not be tracked");
        Assert(jobs.Create("PJ-1", "RECIPE-7", ["W01", "W02"]).IsAccepted, "the job could not be created");

        Assert(jobs.BeginSetup("PJ-1").IsAccepted, "setup could not begin");
        Assert(jobs.SetupComplete("PJ-1").IsAccepted, "setup could not finish");
        Assert(substrates.MoveTo("W01", "CHUCK").IsAccepted, "W01 could not reach the chuck");
        Assert(substrates.BeginProcessing("W01").IsAccepted, "work could not start on W01");

        Assert(jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted, "the job could not be stopped");
        Assert(jobs.StopComplete("PJ-1").IsAccepted, "the machine could not finish stopping");

        // The whole point of the join: E40's ending reached E90's wafers with nobody calling
        // anything, and the two wafers ended differently because they were in different states.
        Assert(
            substrates.FindSubstrate("W01")?.ProcessingState == SubstrateProcessingState.Stopped,
            $"W01 is {substrates.FindSubstrate("W01")?.ProcessingState}, not Stopped");

        Assert(
            substrates.FindSubstrate("W02")?.ProcessingState == SubstrateProcessingState.Skipped,
            $"W02 is {substrates.FindSubstrate("W02")?.ProcessingState}, not Skipped");
    }

    private static async Task PerformanceTrackingResolvesAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddSubstrateTracking(tracker => tracker.AddLocation("CHUCK"))
            .AddPerformanceTracking(tracker =>
            {
                tracker.AddModule("EQUIPMENT");
                tracker.AddModule("PM1");
            });

        await using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<PerformanceTracker>();

        Assert(tracker.Modules.Count == 2, $"the tracker has {tracker.Modules.Count} module(s), not two");

        // Two packages answering stream 14 from one feed, which is the thing a ProjectReference
        // build cannot check: a provider missing from the packed assembly resolves to nothing here.
        var providers = provider.GetServices<IObjectProvider>().ToArray();

        Assert(providers.Length == 2, $"{providers.Length} object provider(s) registered, not two");
        Assert(
            providers.OfType<PerformanceObjectProvider>().Any(),
            "the E116 object provider is not among the registered providers");

        Assert(tracker.BeginTask("PM1", "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted, "the task could not begin");
        Assert(!tracker.BeginTask("PM1", "RECIPE-8").IsAccepted, "a second task began on an executing module");
        Assert(tracker.Block("PM1", "NO_MATERIAL", "waiting for the next wafer").IsAccepted, "the module could not be blocked");
        Assert(tracker.FindModule("PM1")?.HasTask == true, "the block threw away the task it interrupted");
        Assert(!tracker.Block("PM1", "NO_MATERIAL").IsAccepted, "the same block was accepted twice");
        Assert(tracker.Unblock("PM1").IsAccepted, "the module could not be unblocked");
        Assert(
            tracker.FindModule("PM1")?.State == PerformanceState.Executing,
            $"PM1 is {tracker.FindModule("PM1")?.State} after unblocking, not Executing");
        Assert(tracker.EndTask("PM1").IsAccepted, "the task could not end");

        var statistics = tracker.Statistics("PM1");

        Assert(statistics is not null, "a declared module has no statistics");
        Assert(statistics!.TasksCompleted == 1, $"{statistics.TasksCompleted} task(s) completed, not one");
        Assert(statistics.Total == statistics.TimeIdle + statistics.TimeExecuting + statistics.TimeBlocked,
            "the three times do not add up to the window");

        // Read-only on purpose, and this is the one assertion in the file about a refusal that is
        // a design decision rather than a state machine: a host that could set an EPT state could
        // record a chamber as productive while it stood empty.
        var module = new PerformanceObjectProvider(tracker);

        var written = await module.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["PM1"],
                ObjectType = PerformanceObjectProvider.ModuleType,
                Attributes = [new ObjectAttribute { Name = "EPTState", Value = SecsItem.A("Executing") }],
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert(
            written.Count == 1 && written[0].Acknowledgement == ObjectAcknowledgement.InvalidAttribute,
            "a host was allowed to set an EPT state");
    }

    private static async Task EnhancedServicesResolveAsync()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()

            // GEM and both halves of E39 on one machine. GemEquipment declares nine functions of
            // stream 2 and the enhanced set declares eight others, which only a restore from the
            // real .nupkg files can prove do not tread on each other.
            .AddGemEquipment(
                model => model.AddStatusVariable(1u, "ControlState", () => SecsItem.U1(5)),
                runAsHostedService: false)
            .AddObjectProvider()
            .AddEnhancedServiceProvider<EnhancedInMemoryProvider>();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();
        var equipment = provider.GetRequiredService<EnhancedServicesEquipment>();

        Assert(
            EnhancedServicesEquipment.HandledMessages.Count == 8,
            $"the enhanced set declares {EnhancedServicesEquipment.HandledMessages.Count} primaries, not eight");

        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            Assert(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} has no handler");
        }

        foreach (var (stream, function) in GemEquipment.HandledMessages.Concat(ObjectServices.HandledMessages))
        {
            Assert(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} lost its handler");
        }

        Assert(
            equipment.Provider is EnhancedInMemoryProvider,
            $"the equipment resolved with {equipment.Provider.GetType().Name}, not the in-memory provider");

        var store = (EnhancedInMemoryProvider)equipment.Provider;

        store.SetVariable("PM1", EnhancedVariableKind.StatusVariable, 1234, SecsItem.U4(250));
        store.SetCommand("PM1", "START", _ => EnhancedCommandResult.From(EnhancedCommandAcknowledgement.CompletionLater));

        // The two codes worth checking from a packed assembly: HCACK 4 is a success, and HCACK 6
        // is the code the catalogue kept for a stream that can address an object.
        var started = await store.CommandAsync(
            new EnhancedCommandRequest { ObjectSpecification = "PM1", Command = "START" },
            CancellationToken.None).ConfigureAwait(false);

        var nowhere = await store.CommandAsync(
            new EnhancedCommandRequest { ObjectSpecification = "PM9", Command = "START" },
            CancellationToken.None).ConfigureAwait(false);

        Assert(started.IsAccepted, $"HCACK {started.Acknowledgement} did not read as accepted");
        Assert(
            nowhere.Acknowledgement == EnhancedCommandAcknowledgement.NoSuchObject,
            $"a command to an undeclared object answered {nowhere.Acknowledgement}, not NoSuchObject");

        // And the positional rule, which is the one thing a caller can get silently wrong.
        var values = await store.GetStatusVariablesAsync(
            new EnhancedValueRequest { ObjectSpecification = "PM1", Ids = [1234, 7777] },
            CancellationToken.None).ConfigureAwait(false);

        Assert(values.Count == 2, $"{values.Count} value(s) came back for two identifiers");
        Assert(values[1].Count == 0, "a variable the machine does not have was not an empty item");
    }

    private static async Task ProductivityResolvesAsync()
    {
        var services = new ServiceCollection();
        var clock = new SmokeClock(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        // Before AddSecsGem, so the application's clock wins: the whole reason this registration
        // hangs off the SECS builder is that it takes the container's TimeProvider.
        services.AddSingleton<TimeProvider>(clock);

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddProductivityTracking(tracker => tracker.MoveTo(EquipmentState.Standby, "waiting"));

        await using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<ProductivityTracker>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(tracker.State == EquipmentState.Standby, $"the tracker resolved {tracker.State}, not Standby");

        // Neither standard defines a message, so nothing may be declared - which only a restore
        // from the packed assemblies can show, because a stray handler would resolve from bin.
        for (byte function = 1; function < 64; function += 2)
        {
            Assert(
                !router.TryGetHandler(2, function, out _),
                $"S2F{function} was declared by a package with no messages");
        }

        clock.Tick(TimeSpan.FromHours(4));
        Assert(tracker.MoveTo(EquipmentState.Productive).IsAccepted, "the tool could not start producing");
        clock.Tick(TimeSpan.FromHours(10));
        tracker.RecordUnits(100, 90, TimeSpan.FromMinutes(5));
        tracker.RecordCycles(100);
        Assert(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted, "the tool could not fail");
        clock.Tick(TimeSpan.FromHours(2));
        Assert(tracker.MoveTo(EquipmentState.Standby, "fixed").IsAccepted, "the tool could not be fixed");
        clock.Tick(TimeSpan.FromHours(8));

        var report = tracker.Report();

        Assert(report.Total == TimeSpan.FromHours(24), $"the window is {report.Total}, not a day");
        Assert(
            report.NonScheduled + report.UnscheduledDowntime + report.ScheduledDowntime
            + report.Engineering + report.Standby + report.Productive == report.Total,
            "E10's six buckets do not add up to the window");

        Assert(report.Failures == 1 && report.Repairs == 1, $"{report.Failures} failure(s), {report.Repairs} repair(s)");
        Assert(report.MeanTimeToRepair == TimeSpan.FromHours(2), $"MTTR came out {report.MeanTimeToRepair}");

        // E79's chain telescopes, so the product of the four factors is a free check on each.
        var product = report.AvailabilityEfficiency
            * report.OperationalEfficiency
            * report.RateEfficiency
            * report.QualityEfficiency;

        Assert(
            Math.Abs(product - report.OverallEquipmentEfficiency) < 1e-9,
            $"the four factors multiply to {product} and OEE is {report.OverallEquipmentEfficiency}");

        Assert(
            Math.Abs(report.OverallEquipmentEfficiency - (450d / 1_440d)) < 1e-9,
            $"OEE came out {report.OverallEquipmentEfficiency}, not 450 theoretical minutes in a day");
    }

    private static async Task PerformanceFlowResolvesAsync()
    {
        const string tool = "EQUIPMENT";

        var services = new ServiceCollection();
        var clock = new SmokeClock(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        services.AddSingleton<TimeProvider>(clock);

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddPerformanceTracking(tracker =>
            {
                tracker.AddModule(tool);
                tracker.AddModule("PM1");
            })
            .AddProductivityTracking()
            .AddPerformanceProductivityBridge(
                tool,
                options => options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby);

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<PerformanceProductivityBridge>();
        var performance = provider.GetRequiredService<PerformanceTracker>();
        var productivity = provider.GetRequiredService<ProductivityTracker>();

        Assert(bridge.IsAttached, "the bridge resolved detached");
        Assert(bridge.ModuleId == tool, $"the bridge follows '{bridge.ModuleId}', not '{tool}'");

        // Already in line, which only a resolve can show: an idle module on a scheduled tool is
        // standby, and E10 would otherwise sit in non-scheduled until the first transition.
        Assert(
            productivity.State == EquipmentState.Standby,
            $"E10 resolved {productivity.State}, not Standby");

        var missing = 0;

        bridge.MappingMissing += (_, _) => missing++;

        clock.Tick(TimeSpan.FromHours(2));
        Assert(
            performance.BeginTask(tool, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted,
            "the module could not begin its task");

        // The task type is the intent, so this needed no mapping at all.
        Assert(
            productivity.State == EquipmentState.Productive,
            $"a Process task left E10 in {productivity.State}, not Productive");

        clock.Tick(TimeSpan.FromHours(10));
        Assert(performance.Block(tool, "NO_MATERIAL").IsAccepted, "the module could not be blocked");
        Assert(
            productivity.State == EquipmentState.Standby,
            $"a mapped NO_MATERIAL left E10 in {productivity.State}, not Standby");

        clock.Tick(TimeSpan.FromHours(2));

        // And the reason changing, which E116 reports without moving - the half a bridge watching
        // only transitions would miss entirely.
        Assert(performance.Block(tool, "INTERLOCK").IsAccepted, "the reason could not change");
        Assert(
            productivity.State == EquipmentState.UnscheduledDowntime,
            $"an unmapped INTERLOCK left E10 in {productivity.State}, not UnscheduledDowntime");

        Assert(missing == 1, $"{missing} mapping(s) reported missing, not one");

        clock.Tick(TimeSpan.FromHours(10));

        var report = productivity.Report();

        Assert(report.Total == TimeSpan.FromHours(24), $"the window is {report.Total}, not a day");
        Assert(report.Productive == TimeSpan.FromHours(10), $"productive came out {report.Productive}");
        Assert(report.Standby == TimeSpan.FromHours(4), $"standby came out {report.Standby}");
        Assert(
            report.UnscheduledDowntime == TimeSpan.FromHours(10),
            $"downtime came out {report.UnscheduledDowntime}");
        Assert(report.Failures == 1, $"{report.Failures} failure(s), not one");

        // Nobody touched the E10 tracker in any of that.
        Assert(
            report.NonScheduled + report.UnscheduledDowntime + report.ScheduledDowntime
            + report.Engineering + report.Standby + report.Productive == report.Total,
            "E10's six buckets do not add up to the window");
    }

    /// <summary>
    /// The E87-E94 join, out of the packed assemblies: a lot that waits for its boxes.
    /// </summary>
    /// <remarks>
    /// Worth a smoke check of its own because this join spans two package boundaries that a
    /// <c>ProjectReference</c> build cannot see - <c>MaterialFlow</c> gained a dependency on
    /// <c>ControlJobs</c> in this chapter, and a <c>.nuspec</c> that forgot it would restore and
    /// then fail here rather than in the suite.
    /// </remarks>
    private static async Task ControlJobCarrierResolvesAsync()
    {
        const string job = "CJ-1";
        const string left = "CAR-001";
        const string right = "CAR-002";

        var services = new ServiceCollection();

        services
            .AddSecsGem()
            .UseInMemoryTransport()
            .AddCarrierEquipment(manager =>
            {
                foreach (var portId in (byte[])[1, 2])
                {
                    manager.AddPort(portId);
                    manager.SetServiceStatus(portId, inService: true);
                    manager.SetTransferState(portId, PortTransferState.ReadyToLoad);
                }
            })
            .AddControlJobs(jobs => jobs.Create(job, [left, right]))
            .AddControlJobCarrierBridge();

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ControlJobCarrierBridge>();
        var carriers = provider.GetRequiredService<CarrierManager>();
        var jobs = provider.GetRequiredService<ControlJobManager>();

        Assert(bridge.IsAttached, "the bridge resolved detached");
        Assert(jobs.Select(job).IsAccepted, "the job could not be selected");

        byte[] slotMap = [1, 1, 0, 1];

        Arrive(1, left);

        // One box short, and this is the claim: a quorum, not the first carrier through the door.
        Assert(
            jobs.Find(job)!.State == ControlJobState.Selected,
            $"one carrier left the job {jobs.Find(job)!.State}, not Selected");

        var waiting = bridge.Check(job);

        Assert(!waiting.IsAccepted, "the material counted as ready with one carrier missing");
        Assert(
            waiting.Refused.TryGetValue(right, out var why) && why.Contains("has not arrived", StringComparison.Ordinal),
            $"the missing carrier was reported as '{(waiting.Refused.TryGetValue(right, out var text) ? text : "nothing")}'");

        Arrive(2, right);

        Assert(
            jobs.Find(job)!.State == ControlJobState.Executing,
            $"both carriers left the job {jobs.Find(job)!.State}, not Executing");

        Assert(bridge.ClaimCarriers(job).Count == 2, "the job could not claim both carriers");

        var stalls = 0;

        bridge.Stalled += (_, _) => stalls++;

        Assert(carriers.EndAccess(left).IsAccepted, "the first carrier could not be finished");
        Assert(carriers.EndAccess(right, stopped: true).IsAccepted, "the second carrier could not be stopped");

        // A stopped box does not quietly complete a lot, and it does not pass in silence either.
        Assert(stalls == 1, $"{stalls} stall(s) reported, not one");
        Assert(
            jobs.Find(job)!.State == ControlJobState.Executing,
            $"a stopped carrier left the job {jobs.Find(job)!.State}, not Executing");

        Assert(
            jobs.Command(job, ControlJobCommands.Abort).IsAccepted,
            "the host could not abort the job");

        Assert(
            jobs.Find(job)!.Completion == ControlJobCompletion.Aborted,
            $"the job ended {jobs.Find(job)!.Completion}, not Aborted");

        void Arrive(byte portId, string carrierId)
        {
            Assert(carriers.PlaceCarrier(portId, carrierId).IsAccepted, $"{carrierId} could not be placed");
            Assert(carriers.ReadCarrierId(carrierId, verified: true).IsAccepted, $"{carrierId}'s label could not be read");
            Assert(carriers.ReadSlotMap(carrierId, slotMap, verified: true).IsAccepted, $"{carrierId} could not be mapped");
        }
    }

    /// <summary>
    /// Every state machine written down and read back, out of the packed assemblies.
    /// </summary>
    /// <remarks>
    /// Worth a check of its own because the snapshot types are the one part of this stack a
    /// consumer is expected to serialize itself - so they have to be reachable, public and
    /// settable from outside the assembly that defines them, which a ProjectReference build and an
    /// internals-free test suite both check less directly than a consumer does.
    /// </remarks>
    private static Task StateSurvivesARestartAsync()
    {
        var clock = new SmokeClock(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        var carriers = new CarrierManager();

        carriers.AddPort(1);
        Assert(carriers.SetServiceStatus(1, inService: true).IsAccepted, "port 1 could not be put in service");
        Assert(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted, "port 1 could not be readied");
        Assert(carriers.PlaceCarrier(1, "CAR-001").IsAccepted, "CAR-001 could not be placed");
        Assert(carriers.ReadCarrierId("CAR-001", verified: true).IsAccepted, "CAR-001's label could not be read");
        Assert(
            carriers.ReadSlotMap("CAR-001", new byte[] { 1, 1, 0, 1 }, verified: true).IsAccepted,
            "CAR-001 could not be mapped");
        Assert(carriers.BeginAccess("CAR-001").IsAccepted, "CAR-001 could not be accessed");

        var restartedCarriers = new CarrierManager();

        restartedCarriers.AddPort(1);

        var carrierReport = restartedCarriers.Restore(carriers.Capture());

        Assert(carrierReport.IsAccepted, $"the carrier snapshot was refused: {carrierReport.Reason}");

        var box = restartedCarriers.FindCarrier("CAR-001");

        Assert(box is not null, "CAR-001 did not come back");
        Assert(box!.IdStatus == CarrierIdStatus.IdNotRead, $"CAR-001 came back {box.IdStatus}, not unread");
        Assert(
            box.AccessStatus == CarrierAccessStatus.CarrierStopped,
            $"CAR-001 came back {box.AccessStatus}, not stopped");

        // E94: a lot that was running ends the way an interruption ends.
        var jobs = new ControlJobManager();

        Assert(jobs.Create("CJ-1", ["CAR-001"]).IsAccepted, "CJ-1 could not be created");
        Assert(jobs.Create("CJ-2", ["CAR-002"]).IsAccepted, "CJ-2 could not be created");
        Assert(jobs.Select("CJ-1").IsAccepted, "CJ-1 could not be selected");
        Assert(jobs.MaterialReady("CJ-1").IsAccepted, "CJ-1's material could not become ready");

        var restartedJobs = new ControlJobManager();

        Assert(restartedJobs.Restore(jobs.Capture()).IsAccepted, "the control job snapshot was refused");
        Assert(
            restartedJobs.Find("CJ-1")!.Completion == ControlJobCompletion.Aborted,
            $"CJ-1 came back {restartedJobs.Find("CJ-1")!.Completion}, not aborted");
        Assert(restartedJobs.Queue.Count == 1, $"the queue came back with {restartedJobs.Queue.Count} job(s), not one");

        // And the two that answer the outage differently, which is this chapter's point.
        var performance = new PerformanceTracker(clock);

        performance.AddModule("PM1");
        Assert(performance.BeginTask("PM1", "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted, "PM1 could not start");
        clock.Tick(TimeSpan.FromHours(2));

        var shift = new ProductivityTracker(clock);

        Assert(shift.MoveTo(EquipmentState.Productive).IsAccepted, "the tool could not go productive");
        clock.Tick(TimeSpan.FromHours(2));

        var modules = performance.Capture();
        var window = shift.Capture();

        clock.Tick(TimeSpan.FromHours(3));

        var restartedPerformance = new PerformanceTracker(clock);

        restartedPerformance.AddModule("PM1");

        var performanceReport = restartedPerformance.Restore(modules);
        var statistics = restartedPerformance.Statistics("PM1")!;

        Assert(
            performanceReport.Unaccounted == TimeSpan.FromHours(3),
            $"E116 reported {performanceReport.Unaccounted} unaccounted for, not three hours");
        Assert(statistics.TasksAbandoned == 1, $"{statistics.TasksAbandoned} task(s) abandoned, not one");
        Assert(
            statistics.TimeExecuting == TimeSpan.FromHours(4),
            $"E116 kept {statistics.TimeExecuting} of executing time, so the outage was charged to a state");

        var restartedShift = new ProductivityTracker(clock);
        var shiftReport = restartedShift.Restore(window);
        var report = restartedShift.Report();

        Assert(
            shiftReport.UnaccountedChargedTo == EquipmentState.UnscheduledDowntime,
            $"E10 charged the outage to {shiftReport.UnaccountedChargedTo}");
        Assert(
            report.Total == report.NonScheduled + report.UnscheduledDowntime + report.ScheduledDowntime
                + report.Engineering + report.Standby + report.Productive,
            "E10's six buckets stopped adding up to the window across a restart");

        return Task.CompletedTask;
    }

    /// <summary>A clock the smoke test moves by hand, so a day takes no time to measure.</summary>
    private sealed class SmokeClock(DateTimeOffset start) : TimeProvider
    {
        private long _stamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => start.AddTicks(_stamp);

        public override long GetTimestamp() => _stamp;

        public void Tick(TimeSpan by) => _stamp += by.Ticks;
    }
}
