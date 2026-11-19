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
using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Mapping;
using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Substrates;
using Barbatos.SecsGem.Material;
using Barbatos.SecsGem.Objects;
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
        await Run("objects: stream 14 declares its primaries only when this end has objects", ObjectServicesResolveAsync).ConfigureAwait(false);
        await Run("carriers: E87 declares both halves, and the state machine refuses", CarriersResolveAsync).ConfigureAwait(false);
        await Run("carriers + substrates: E87 and E90 in one machine, both answering stream 14", CarriersAndSubstratesComposeAsync).ConfigureAwait(false);
        await Run("controljobs: E94 drives its state machine through stream 14, beside E87 and E90", ControlJobsResolveAsync).ConfigureAwait(false);
        await Run("processjobs: E40 declares stream 16 and all four standards answer stream 14", ProcessJobsResolveAsync).ConfigureAwait(false);
        await Run("materialflow: a verified slot map turns into substrates by itself", MaterialFlowResolvesAsync).ConfigureAwait(false);
        await Run("materialflow: a stopped process job settles the lot it was running", ProcessJobFlowResolvesAsync).ConfigureAwait(false);
        await Run("performancetracking: E116 accounts for a module's time and refuses to be told its state", PerformanceTrackingResolvesAsync).ConfigureAwait(false);

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

        // Eleven since E94: the seven reading primaries plus create, delete and the two actions,
        // which the control job package is the first standard here to need.
        Assert(ObjectServices.HandledMessages.Count == 11, $"the stream declares {ObjectServices.HandledMessages.Count} primaries, not eleven");

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
            .AddCarrierEquipment(manager => manager.AddPort(1));

        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<CarrierEquipment>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert(
            router.TryGetHandler(3, 17, out _) && router.TryGetHandler(3, 33, out _),
            "the router is missing a carrier primary, so AddCarrierEquipment did not declare them");

        Assert(
            router.TryGetHandler(14, 1, out _) && router.TryGetHandler(14, 17, out _),
            "the router is missing object services, so a host could never read a slot map");

        Assert(
            provider.GetRequiredService<IObjectProvider>() is CarrierObjectProvider,
            "stream 14 is not answered out of the carriers");

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
}
