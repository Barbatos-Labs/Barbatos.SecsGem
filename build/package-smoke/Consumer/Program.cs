// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.Runtime.InteropServices;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Mapping;
using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Material;
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
        await Run("material: stream 3 resolves at both ends, which declare different messages", MaterialResolvesAsync).ConfigureAwait(false);

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
            .AddGemTerminal()
            .AddGemTraces()
            .AddGemLimits();

        await using var provider = services.BuildServiceProvider();

        var equipment = provider.GetRequiredService<GemEquipment>();

        Assert(GemEquipment.HandledMessages.Count > 0, "the equipment declares no handled messages");
        Assert(equipment.Model.Variables.Count > 0, "the model has no variables");

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            router.TryGetHandler(1, 13, out _),
            "the router has no handler for S1F13, so the equipment's handled messages never reached the routing table");

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
            .AddMaterialEquipment();

        await using var equipmentProvider = equipmentServices.BuildServiceProvider();

        _ = equipmentProvider.GetRequiredService<MaterialEquipment>();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            equipmentRouter.TryGetHandler(3, 1, out _) && equipmentRouter.TryGetHandler(3, 11, out _),
            "the router is missing one of the host's questions, so AddMaterialEquipment did not declare them");

        Assert(
            !equipmentRouter.TryGetHandler(3, 5, out _),
            "the equipment claimed S3F5, which is a message it is the one that sends");

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
}
