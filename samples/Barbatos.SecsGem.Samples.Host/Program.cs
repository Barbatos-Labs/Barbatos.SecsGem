// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Globalization;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Material;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.WaferMap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Samples.Host;

/// <summary>
/// A host: active HSMS, selecting the equipment and driving a GEM conversation end to end.
/// </summary>
/// <remarks>
/// <para>Two ways to run it.</para>
/// <para>
/// With no arguments it runs <em>both</em> entities in one process over an in-memory transport -
/// a real host connection, a real equipment connection, a real <see cref="GemEquipment"/> and a
/// real <see cref="GemHost"/>, with real timers and real encoded frames and no port. That is the
/// version worth reading: the whole conversation is in one file and it either works or exits
/// non-zero.
/// </para>
/// <code>dotnet run --project samples/Barbatos.SecsGem.Samples.Host</code>
/// <para>With <c>--tcp</c> it connects to the equipment sample over a socket instead.</para>
/// <code>
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
/// </code>
/// <para>
/// Every message this host sends goes through <see cref="GemHost"/>, which is why there is no
/// table of hand-built bodies here any more and why nothing in this file compares an
/// acknowledgement byte against a literal.
/// </para>
/// </remarks>
internal static class Program
{
    private const uint ControlStateVariable = 1;
    private const uint PressureVariable = 2;
    private const uint LotIdVariable = 10;
    private const uint ProcessEndEvent = 1_001;
    private const uint PressureLimitEvent = 2_500;
    private const uint HostReport = 6_000;

    private static readonly ConcurrentQueue<string> ErrorReports = new();
    private static readonly ConcurrentQueue<string> Reports = new();

    /// <summary>
    /// Two object providers, tried in order: the carriers, and everything else this machine has.
    /// </summary>
    /// <remarks>
    /// The extension point the carrier package documents, written out. E87 gives a machine carriers
    /// and load ports; this machine also has a chamber, and stream 14 has one provider per
    /// connection - so an application with objects of its own composes them. Each provider already
    /// leaves out objects it does not have, which is what makes concatenating the answers correct
    /// rather than merely convenient.
    /// </remarks>
    private sealed class CombinedObjects(IObjectProvider carriers, IObjectProvider others) : IObjectProvider
    {
        public async ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
            =>
            [
                .. await carriers.GetObjectTypesAsync(cancellationToken),
                .. await others.GetObjectTypesAsync(cancellationToken),
            ];

        public async ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
            =>
            [
                .. await carriers.GetInstancesAsync(objectType, cancellationToken),
                .. await others.GetInstancesAsync(objectType, cancellationToken),
            ];

        public async ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
            string objectSpecification,
            string objectType,
            CancellationToken cancellationToken)
        {
            // Whichever has the object: a namelist that advertises attributes nothing answers is
            // worse than a short one.
            var held = await carriers.GetAttributesAsync(
                new ObjectAttributeRequest { Objects = [objectSpecification], Attributes = [] },
                cancellationToken);

            return held.Count > 0
                ? await carriers.GetAttributeNamesAsync(objectSpecification, objectType, cancellationToken)
                : await others.GetAttributeNamesAsync(objectSpecification, objectType, cancellationToken);
        }

        public async ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
            ObjectAttributeRequest request,
            CancellationToken cancellationToken)
            =>
            [
                .. await carriers.GetAttributesAsync(request, cancellationToken),
                .. await others.GetAttributesAsync(request, cancellationToken),
            ];

        public async ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
            ObjectAttributeWrite write,
            CancellationToken cancellationToken)
        {
            var mine = await carriers.SetAttributesAsync(write, cancellationToken);

            // An object the carriers do not have gets the same write offered to the other
            // provider, rather than a refusal that only means "not mine".
            if (mine.Count > 0 && mine[0].Acknowledgement is not ObjectAcknowledgement.InvalidObjectInstance)
            {
                return mine;
            }

            return await others.SetAttributesAsync(write, cancellationToken);
        }
    }

    /// <summary>The two ends of E87 this sample drives, bundled so the conversation takes one parameter rather than two.</summary>
    /// <param name="Host">The host side, which decides.</param>
    /// <param name="Machine">The machine's state machines, which in this mode are in the same process.</param>
    private sealed record CarrierDemo(CarrierHost Host, CarrierManager Machine);

    /// <summary>The carrier this sample walks through E87.</summary>
    private const string CarrierName = "CAR-001";

    /// <summary>Twenty-five slots with two of them empty, as one byte each.</summary>
    /// <remarks>
    /// The codes are the machine's own: E87 leaves the vocabulary to the equipment and the
    /// catalogue has no entry for <c>SLOTMAP</c> at all. Zero is empty here, which is the only
    /// reading this stack takes.
    /// </remarks>
    private static readonly byte[] SlotMap =
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
    ];

    /// <summary>The chamber, as stream 14 names it.</summary>
    private const string ChamberObject = "CH-1";

    /// <summary>And its type. Both are the machine's own vocabulary: E5 does not define object types.</summary>
    private const string ChamberType = "Chamber";

    /// <summary>The lot the remote command below starts, and so the wafer the map comes back for.</summary>
    /// <remarks>
    /// Named rather than written twice: in stream 12 the material identifier is how the map is
    /// addressed, and a host that asked for one lot and looked up another would find nothing and
    /// blame the equipment.
    /// </remarks>
    private const string StartedLot = "LOT-2026-1115";

    /// <summary>The loopback chamber's pressure, which the conversation moves to cross a band.</summary>
    private static double _pressure = 1_013.25d;

    private static async Task<int> Main(string[] args)
        => args.Contains("--tcp") ? await RunOverTcpAsync() : await RunInOneProcessAsync();

    // ------------------------------------------------------------------ in one process

    private static async Task<int> RunInOneProcessAsync()
    {
        Console.WriteLine("Barbatos.SecsGem - a host and GEM equipment in one process");
        Console.WriteLine("==========================================================");
        Console.WriteLine();

        var options = new SecsGemOptions
        {
            DeviceId = 7,
            LinkTestInterval = TimeSpan.FromSeconds(30),
        };

        var gemOptions = new GemOptions
        {
            ModelName = "BRBTOS-1",
            SoftwareRevision = "0.1.0",
            DefaultControlState = GemControlState.OnlineRemote,
        };

        var hostOptions = new GemHostOptions
        {
            ModelName = "BRBTOS-HOST",
            SoftwareRevision = "0.1.0",
        };

        // Two transports wired to each other. Both halves are real pipes, so each connection sees
        // the same segmented reads it would see on a socket.
        var (active, passive) = InMemorySecsTransport.CreatePair();

        // The cycle the registrations solve with a deferred factory, written out by hand twice
        // because this sample has no container: a router needs its handlers, the handlers need the
        // GEM object, and the GEM object needs the connection the router is being built for.
        GemEquipment? pendingEquipment = null;
        GemHost? pendingHost = null;

        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingEquipment!.HandleAsync(transaction, token));
        }

        // And the formatted half of stream 7, declared on its own because it is a capability of its
        // own - the conversation below sends a structured recipe. Equipment that holds no recipes
        // leaves these out and answers S9F3 for stream 7, which is the honest answer.
        foreach (var (stream, function) in GemEquipment.FormattedProcessProgramMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingEquipment!.HandleAsync(transaction, token));
        }

        // And terminal services, so the host below can put something in front of an operator.
        foreach (var (stream, function) in GemEquipment.TerminalMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingEquipment!.HandleAsync(transaction, token));
        }

        // And traces and limits, the two capabilities that need nothing registered with them.
        foreach (var (stream, function) in GemEquipment.TraceMessages.Concat(GemEquipment.LimitMessages))
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingEquipment!.HandleAsync(transaction, token));
        }

        // Wafer mapping, which runs the other way round: in stream 12 the equipment sends and the
        // host answers, so what the equipment declares is one message - the error report the host
        // may send it about a map.
        WaferMapEquipment? pendingMapper = null;
        WaferMapHost? pendingMapHost = null;

        foreach (var (stream, function) in WaferMapEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingMapper!.HandleAsync(transaction, token));
        }

        // And material status, which is the one stream here where both ends send: the machine
        // answers the host's five questions and reports four things of its own.
        MaterialEquipment? pendingMaterial = null;
        MaterialHost? pendingMaterialHost = null;

        foreach (var (stream, function) in MaterialEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingMaterial!.HandleAsync(transaction, token));
        }

        // And object services. The machine has the objects, so the machine declares the stream;
        // the host below only asks, which needs nothing declared at all.
        ObjectServices? pendingObjects = null;

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingObjects!.HandleAsync(transaction, token));
        }

        // And E87: five primaries on stream 3, with the carrier and load port attributes going out
        // through the object services above.
        CarrierEquipment? pendingCarriers = null;

        foreach (var (stream, function) in CarrierEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingCarriers!.HandleAsync(transaction, token));
        }

        // The host's side of the same arrangement. S1F13, S1F1, S5F1, S6F11, S6F13 and stream 9 -
        // everything a machine may send a host unprompted.
        var hostRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in GemHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) => pendingHost!.HandleAsync(transaction, token));
        }

        // And all ten primaries of stream 12, which is where the asymmetry shows: the host end of
        // wafer mapping answers nearly the whole stream, because the equipment is the one sending.
        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) => pendingMapHost!.HandleAsync(transaction, token));
        }

        foreach (var (stream, function) in MaterialHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) => pendingMaterialHost!.HandleAsync(transaction, token));
        }

        await using var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build());
        await using var hostLink = new HsmsConnection(options, active, hostRoutes.Build());
        await using var equipment = new GemEquipment(
            gemOptions,
            BuildModel(),
            equipmentLink,
            formattedProcessPrograms: new GemInMemoryFormattedProcessProgramStore(),
            terminal: new GemInMemoryTerminal());
        await using var host = new GemHost(hostOptions, hostLink);

        // Neither end of stream 12 is disposable, and neither end of stream 3: no state, no timer,
        // nothing to release.
        var mapper = new WaferMapEquipment(equipmentLink);
        var maps = new WaferMapInMemoryStore();
        var mapHost = new WaferMapHost(maps, hostLink);

        // The machine is holding the lot the remote command below will start, at load port 1.
        var inventory = new MaterialInMemorySource();
        inventory.Load(StartedLot, location: 1, quantity: 25, timeToCompletion: 1_517);

        var materialEquipment = new MaterialEquipment(inventory, equipmentLink);
        var materialHost = new MaterialHost(hostLink);

        // The machine as objects: a chamber with two attributes and a load port with one. The
        // types and the attribute names are this machine's own - E5 does not define object types,
        // which is why no library can.
        var machineObjects = new ObjectInMemoryProvider();
        machineObjects.Add(ChamberObject, ChamberType);
        machineObjects.Set(ChamberObject, "Pressure", SecsItem.F8(1_013.25d));
        machineObjects.Set(ChamberObject, "Setpoint", SecsItem.F8(1_013.25d));
        machineObjects.Add("PORT-1", "LoadPort");
        machineObjects.Set("PORT-1", "State", SecsItem.A("ReadyToLoad"));

        // E87's state machines, with one load port, in service and clear.
        var carrierManager = new CarrierManager();

        carrierManager.AddPort(1);
        carrierManager.SetServiceStatus(1, inService: true);

        // One provider per connection, so the machine's own objects and E87's are composed - which
        // is the arrangement the carrier package documents for an application that has both.
        var equipmentObjects = new ObjectServices(
            equipmentLink,
            new CombinedObjects(new CarrierObjectProvider(carrierManager), machineObjects));

        var hostObjects = new ObjectServices(hostLink);
        var carrierEquipment = new CarrierEquipment(carrierManager);
        var carrierHost = new CarrierHost(hostLink);

        pendingEquipment = equipment;
        pendingHost = host;
        pendingMapper = mapper;
        pendingMapHost = mapHost;
        pendingMaterial = materialEquipment;
        pendingMaterialHost = materialHost;
        pendingObjects = equipmentObjects;
        pendingCarriers = carrierEquipment;

        Subscribe(host);

        equipmentLink.StateChanged += (_, state) => Console.WriteLine($"   [equipment link] {state}");
        hostLink.StateChanged += (_, state) => Console.WriteLine($"   [host link]      {state}");
        equipment.CommunicationStateChanged += (_, state) => Console.WriteLine($"   [equipment comms]   {state}");
        equipment.ControlStateChanged += (_, state) => Console.WriteLine($"   [equipment control] {state}");
        host.CommunicationStateChanged += (_, state) => Console.WriteLine($"   [host comms]        {state}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = Task.WhenAll(
            hostLink.RunAsync(cts.Token),
            equipmentLink.RunAsync(cts.Token),
            equipment.RunAsync(cts.Token));

        try
        {
            await WaitUntilAsync(() => hostLink.IsSelected && equipmentLink.IsSelected, "both ends to be selected");

            // The equipment sends its own S1F13 as soon as the link is selected; the host answers
            // it from its routing table. This is the moment a selected link becomes a GEM
            // conversation, and nothing may be reported before it.
            await WaitUntilAsync(() => equipment.IsCommunicating, "the equipment to establish communications");

            var ok = await ConversationAsync(
                host,
                hostLink,
                equipment,
                mapper,
                mapHost,
                materialEquipment,
                materialHost,
                hostObjects,
                new CarrierDemo(carrierHost, carrierManager),
                cts.Token);

            Console.WriteLine();
            Console.WriteLine("-- decode paths --");
            Console.WriteLine();
            Console.WriteLine($"   host      : {hostLink.FastPathFrames} fast, {hostLink.SlowPathFrames} slow");
            Console.WriteLine($"   equipment : {equipmentLink.FastPathFrames} fast, {equipmentLink.SlowPathFrames} slow");
            Console.WriteLine();
            Console.WriteLine(ok ? "OK" : "UNEXPECTED");

            return ok ? 0 : 1;
        }
        finally
        {
            await cts.CancelAsync();
            await Quietly(running);
        }
    }

    /// <summary>What the loopback equipment publishes.</summary>
    private static GemEquipmentModel BuildModel()
        => new GemEquipmentModelBuilder()
            .AddStatusVariable(ControlStateVariable, "ControlState", () => SecsItem.U1(5))

            // The limit event is the machine's to name, because S2F45 carries no CEID: a host sets
            // the band it cares about and the equipment says what it calls leaving it.
            .AddStatusVariable(
                PressureVariable,
                "Pressure",
                () => SecsItem.F8(Volatile.Read(ref _pressure)),
                "mbar",
                PressureLimitEvent)
            .AddDataVariable(LotIdVariable, "LotId", () => SecsItem.A("LOT-2026-1115"))
            .AddEvent(ProcessEndEvent, "ProcessEnd")
            .AddEvent(PressureLimitEvent, "PressureLimitCrossed")
            .AddRemoteCommand("START", (context, _) =>
            {
                var lot = context.GetText("LOTID");

                return ValueTask.FromResult(string.IsNullOrWhiteSpace(lot)
                    ? GemHostCommandAcknowledgement.InvalidParameter
                    : GemHostCommandAcknowledgement.CompletionLater);
            })
            .Build();

    // ------------------------------------------------------------------ over a socket

    private static async Task<int> RunOverTcpAsync()
    {
        Console.WriteLine("Barbatos.SecsGem - host (active) connecting to 127.0.0.1:5000, device 7");
        Console.WriteLine();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss.fff ";
        }));

        services
            .AddSecsGem(configuration: null, options =>
            {
                options.DeviceId = 7;
                options.Mode = HsmsConnectionMode.Active;
                options.IpAddress = "127.0.0.1";
                options.Port = 5000;
                options.LinkTestInterval = TimeSpan.FromSeconds(15);
            })
            .UseTcp()

            // One call in place of the five OnPrimary lines this used to need, and it declares
            // stream 9 as well - which the hand-written version did not, so an S9F7 from the
            // machine used to be answered with an S9F5 about the S9F7.
            .AddGemHost(configure: host => host.ModelName = "BRBTOS-HOST")

            // So a prober on the other end of the socket gets an answer in stream 12. This mode
            // cannot drive that exchange - the equipment is the active party there, and it is in
            // another process - but it can answer it.
            .AddWaferMapHost()

            // And the four things a machine says about its material without being asked. The five
            // questions in the other direction are this end sending, which needs nothing declared.
            .AddMaterialHost()

            // And object services for an end that only asks: no provider, so nothing is declared
            // and a prober asking this host about its objects gets an honest S9F5.
            .AddObjectServices();

        await using var provider = services.BuildServiceProvider();
        var link = provider.GetRequiredService<HsmsConnection>();
        var host = provider.GetRequiredService<GemHost>();

        Subscribe(host);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var running = link.RunAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => link.IsSelected, "the equipment to select");

            var ok = await ConversationAsync(
                host,
                link,
                equipment: null,
                mapper: null,
                mapHost: null,
                materialEquipment: null,
                materialHost: null,
                objects: provider.GetRequiredService<ObjectServices>(),
                carriers: null,
                cts.Token);

            // And then wait for the prober to send a map, which is the half of stream 12 this mode
            // can take part in. The equipment sample sends one when a lot finishes, and the lot the
            // remote command above started takes a tick or two to run - so without this wait the
            // host would hang up a quarter of a second after asking for the work.
            var maps = (WaferMapInMemoryStore)provider.GetRequiredService<IWaferMapStore>();

            Console.WriteLine();
            Console.WriteLine("-- and waits for the prober to map the wafer it was given --");
            Console.WriteLine();

            await WaitUntilAsync(() => maps.Count > 0, "a wafer map to arrive from the equipment");

            var arrived = await maps.LoadRowDataAsync(new WaferMapDataRequest { MaterialId = StartedLot }, cts.Token);

            Console.WriteLine($"   S12F7 <- {arrived?.Rows.Count ?? 0} row(s) for {StartedLot}");
            ok &= arrived is not null && arrived.Rows.Count > 0;

            Console.WriteLine();
            Console.WriteLine($"   host: {link.FastPathFrames} fast, {link.SlowPathFrames} slow");
            Console.WriteLine(ok ? "OK" : "UNEXPECTED");

            return ok ? 0 : 1;
        }
        finally
        {
            await cts.CancelAsync();
            await Quietly(running);
        }
    }

    // ------------------------------------------------------------------ the conversation

    /// <summary>Establish, identify, read, configure reporting, command, send a recipe, and ask for something absent.</summary>
    /// <param name="host">The GEM host. Everything this conversation sends goes through it.</param>
    /// <param name="link">
    /// The connection underneath. Needed for the one message at the end that <see cref="GemHost"/>
    /// has no method for, which is the point of that part.
    /// </param>
    /// <param name="equipment">The loopback equipment, or null when talking to another process.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether every answer was the one expected.</returns>
    private static async Task<bool> ConversationAsync(
        GemHost host,
        HsmsConnection link,
        GemEquipment? equipment,
        WaferMapEquipment? mapper,
        WaferMapHost? mapHost,
        MaterialEquipment? materialEquipment,
        MaterialHost? materialHost,
        ObjectServices? objects,
        CarrierDemo? carriers,
        CancellationToken cancellationToken)
    {
        var ok = true;

        Console.WriteLine();
        Console.WriteLine("-- the host opens the conversation --");
        Console.WriteLine();

        // S1F13 from this side too. The equipment has already established from its end, so this is
        // the second half of a handshake that E30 lets either side start.
        var commack = await host.EstablishCommunicationsAsync(cancellationToken);
        Console.WriteLine($"   S1F13 -> COMMACK={commack}");
        ok &= commack == GemCommunicationAcknowledgement.Accepted;

        // Null is not a failure: E30 has off-line equipment answer S1F1 with a zero-length list
        // rather than its identity, which is how a host learns the machine is alive but not
        // available without asking a second question.
        var identity = await host.AreYouThereAsync(cancellationToken);
        Console.WriteLine($"   S1F1  -> {identity?.ToString() ?? "a zero-length list: the machine is off-line"}");
        ok &= identity is not null;

        using (var status = await host.ReadStatusAsync([ControlStateVariable, PressureVariable], cancellationToken))
        {
            // One of the four replies whose body only the equipment knows the shape of, so it
            // arrives as the message and this sample disposes it. Position two is the second thing
            // asked for, because E5 answers an unknown identifier with a zero-length item in its
            // place rather than by leaving it out.
            var state = status.Item![0].FirstValueOrDefault<byte>();
            var pressure = status.Item[1].FirstValueOrDefault<double>();

            Console.WriteLine($"   S1F3  -> CONTROLSTATE={state}, PRESSURE={pressure:F2}");
            ok &= status.Item.Count == 2;
        }

        // And a namelist, which has a shape E5 pins down - so it arrives decoded, and an
        // identifier the machine does not have says so rather than coming back as a blank.
        var namelist = await host.ReadStatusNamelistAsync([PressureVariable, 999], cancellationToken);
        Console.WriteLine($"   S1F11 -> {string.Join("; ", namelist.Select(v => v.Exists ? v.ToString() : $"{v.Id} is not one of mine"))}");
        ok &= namelist.Count == 2;

        Console.WriteLine();
        Console.WriteLine("-- and configures its own reporting --");
        Console.WriteLine();

        // The host defines the report it wants rather than taking whatever the equipment happens to
        // offer. This is the part of GEM that makes equipment usable by a host nobody wrote it for.
        var drack = await host.DefineReportsAsync([(HostReport, [PressureVariable, LotIdVariable])], cancellationToken);
        Console.WriteLine($"   S2F33 -> DRACK={drack}");
        ok &= drack == GemDefineReportAcknowledgement.Accepted;

        var lrack = await host.LinkEventsAsync([(ProcessEndEvent, [HostReport])], cancellationToken);
        Console.WriteLine($"   S2F35 -> LRACK={lrack}");
        ok &= lrack == GemLinkReportAcknowledgement.Accepted;

        var erack = await host.EnableEventsAsync(true, [ProcessEndEvent], cancellationToken);
        Console.WriteLine($"   S2F37 -> ERACK={erack}");
        ok &= erack == GemEnableEventAcknowledgement.Accepted;

        Console.WriteLine();
        Console.WriteLine("-- then tells the equipment to do something --");
        Console.WriteLine();

        var hcack = await host.SendCommandAsync(
            "START",
            [("LOTID", StartedLot), ("PPID", "RECIPE-7")],
            cancellationToken);

        Console.WriteLine($"   S2F41 -> HCACK={hcack}");

        // Accepted is "done" and CompletionLater is "accepted, completion will be a collection
        // event". Both are success, which is the fact a host reading the raw byte is one typo away
        // from getting backwards.
        ok &= hcack is GemHostCommandAcknowledgement.Accepted or GemHostCommandAcknowledgement.CompletionLater;

        if (equipment is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- the equipment reports the event the host asked for --");
            Console.WriteLine();

            var sent = await equipment.TriggerEventAsync(ProcessEndEvent, cancellationToken);
            Console.WriteLine($"   S6F11 sent: {sent}");
            ok &= sent;

            await WaitUntilAsync(() => !Reports.IsEmpty, "the event report to arrive at the host");

            while (Reports.TryDequeue(out var report))
            {
                Console.WriteLine($"   {report}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("-- and sends a recipe the standard gives a shape to --");
        Console.WriteLine();

        // A formatted process program is PPID, the model and revision it was written for, and a
        // list of CCODE commands - structure E5 defines, where S7F3 carries a block of bytes whose
        // meaning is private between this host and this machine.
        var recipe = new GemFormattedProcessProgram(
            "RECIPE-7",
            "BRBTOS-1",
            "0.1.0",
            [
                new(12, ["SETPOINT", "350.0"]),
                new(13, ["RAMP", "5.0"]),
            ]);

        var ackc7 = await host.SendFormattedProcessProgramAsync(recipe, cancellationToken);
        Console.WriteLine($"   S7F23 -> ACKC7={ackc7}");
        ok &= ackc7 == GemProcessProgramAcknowledgement.Accepted;

        var back = await host.RequestFormattedProcessProgramAsync("RECIPE-7", cancellationToken);
        Console.WriteLine($"   S7F25 -> {back?.ToString() ?? "no such program"}");
        ok &= back?.Commands.Count == 2;

        if (equipment is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and asks to be told when the pressure leaves a band --");
            Console.WriteLine();

            // The band is the host's; the event is the machine's, because S2F45 carries no CEID.
            var limits = await host.DefineVariableLimitsAsync(
                [new GemVariableLimitDefinition(PressureVariable, [new GemVariableLimit(1, 900d, 1_100d)])],
                cancellationToken);

            Console.WriteLine($"   S2F45 -> {limits}");
            ok &= limits.Accepted;

            var attributes = await host.ReadVariableLimitsAsync([], cancellationToken);
            Console.WriteLine($"   S2F47 -> {string.Join("; ", attributes.Select(a => $"{a}, {a.Limits[0]}"))}");
            ok &= attributes.Count == 1;

            var crossings = 0;

            void OnLimit(object? sender, GemEventReportEventArgs e)
            {
                if (e.EventId == PressureLimitEvent)
                {
                    Interlocked.Increment(ref crossings);
                    Console.WriteLine($"   S6F11 -> CEID {e.EventId}: the pressure left the band");
                }
            }

            host.EventReportReceived += OnLimit;

            try
            {
                ok &= await host.LinkEventsAsync([(PressureLimitEvent, [HostReport])], cancellationToken)
                    == GemLinkReportAcknowledgement.Accepted;

                ok &= await host.EnableEventsAsync(true, [PressureLimitEvent], cancellationToken)
                    == GemEnableEventAcknowledgement.Accepted;

                // The first check records which side the value is on - there is nothing to have
                // crossed from - and the second is the crossing.
                await equipment.CheckLimitsAsync(PressureVariable, cancellationToken);

                Volatile.Write(ref _pressure, 1_200d);
                await equipment.CheckLimitsAsync(PressureVariable, cancellationToken);

                await WaitUntilAsync(() => Volatile.Read(ref crossings) == 1, "the crossing to reach the host");

                // And wandering further out is not another crossing, which is what makes the band
                // a deadband rather than a threshold.
                Volatile.Write(ref _pressure, 5_000d);
                await equipment.CheckLimitsAsync(PressureVariable, cancellationToken);

                ok &= Volatile.Read(ref crossings) == 1;
            }
            finally
            {
                host.EventReportReceived -= OnLimit;
            }
        }

        if (equipment is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and asks for a value on an interval rather than asking for it --");
            Console.WriteLine();

            // Four samples half a second apart, two to a message: the equipment samples on its own
            // clock and sends a group when it has one. Unlike the tests, this runs on the real
            // clock, so it takes the two seconds it says it will.
            var groups = 0;
            var samples = 0;

            void OnTrace(object? sender, GemTraceDataEventArgs e)
            {
                // Read inside the handler: the samples are a view into the message that arrived.
                Interlocked.Increment(ref groups);
                Interlocked.Add(ref samples, e.Samples.Count);

                Console.WriteLine($"   S6F1  -> {e}, last value {e.Samples[^1][0].FirstValueOrDefault<double>():F2}");
            }

            host.TraceDataReceived += OnTrace;

            try
            {
                var tiaack = await host.StartTraceAsync(
                    new GemTraceRequest(1, TimeSpan.FromMilliseconds(500), 4, 2, [PressureVariable]),
                    cancellationToken);

                Console.WriteLine($"   S2F23 -> TIAACK={tiaack}");
                ok &= tiaack == GemTraceAcknowledgement.Accepted;

                await WaitUntilAsync(() => Volatile.Read(ref groups) == 2, "both trace groups to arrive");

                ok &= Volatile.Read(ref samples) == 4;
            }
            finally
            {
                host.TraceDataReceived -= OnTrace;
            }
        }

        Console.WriteLine();
        Console.WriteLine("-- and has a word with whoever is standing at the machine --");
        Console.WriteLine();

        // Accepted means the equipment took it for display, not that anybody has read it. The
        // other two codes are worth telling apart: WillNotDisplay is a decision the machine made,
        // TerminalNotAvailable is a fault, and only the second is worth retrying.
        var display = await host.DisplayTerminalMessageAsync(
            ["Lot LOT-2026-1115 is loaded.", string.Empty, "Press START when the cassette is seated."],
            terminalId: 0,
            cancellationToken);

        Console.WriteLine($"   S10F5 -> ACKC10={display}");
        ok &= display == GemTerminalAcknowledgement.Accepted;

        if (equipment is not null)
        {
            // And back the other way. This is the one inbound message with a person waiting on the
            // other end of it, which is why the host's handler gets to answer it rather than
            // having an acknowledgement sent on its behalf.
            var typed = await equipment.SendTerminalRequestAsync("cassette jammed in load port 2", 0, cancellationToken);

            Console.WriteLine($"   S10F1 -> ACKC10={typed}");
            ok &= typed == GemTerminalAcknowledgement.Accepted;

            while (Reports.TryDequeue(out var report))
            {
                Console.WriteLine($"   {report}");
            }
        }

        if (mapper is not null && mapHost is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and the prober maps a wafer, which runs the other way round --");
            Console.WriteLine();

            // Every message in this block is sent by the equipment. Stream 12 is E5 rather than
            // E30 and inverts the directions: a prober knows which die it probed, so it is the
            // active party and the host answers.
            var setup = await mapper.SendSetupAsync(
                new WaferMapSetup
                {
                    MaterialId = "W01",
                    IdType = WaferMapIdType.WaferIdAndOrientation,
                    FirstLocation = 2,
                    FlatRotation = 270,
                    OriginLocation = 3,
                    ReferencePoints = [new WaferMapPosition(-15_000, 0), new WaferMapPosition(15_000, 60_000)],
                    DieUnits = "um",
                    DieSizeX = 5_400,
                    DieSizeY = 7_200,
                    RowCount = 3,
                    ColumnCount = 4,
                    NullBinCode = '.',
                    ProductId = "BRBTOS-A7",
                },
                cancellationToken);

            Console.WriteLine($"   S12F1  -> SDACK={setup}");
            ok &= setup == WaferMapSetupAcknowledgement.Accepted;

            // The cheap place to be refused: the inquire carries the location count, so a host
            // that is not ready says so before a quarter of a million bins arrive rather than
            // after.
            var grant = await mapper.InquireTransmitAsync(
                new WaferMapTransmitInquiry
                {
                    MaterialId = "W01",
                    MapFormat = "1",
                    MapLocationCount = 12,
                },
                cancellationToken);

            Console.WriteLine($"   S12F5  -> GRNT1={grant}");
            ok &= grant == WaferMapTransmitGrant.Granted;

            // Three rows, and the middle one probed right to left - which is an increment of -1
            // and why row data needs no coordinate per bin.
            var sent = await mapper.SendRowDataAsync(
                new WaferMapRowData
                {
                    MaterialId = "W01",
                    IdType = WaferMapIdType.WaferIdAndOrientation,
                    Rows =
                    [
                        new WaferMapRow { Row = 0, StartColumn = 0, Increment = 1, Bins = new byte[] { 1, 1, 2, 1 } },
                        new WaferMapRow { Row = 1, StartColumn = 3, Increment = -1, Bins = new byte[] { 1, 4, 1, 1 } },
                        new WaferMapRow { Row = 2, StartColumn = 0, Increment = 1, Bins = new byte[] { 1, 1, (byte)'.', (byte)'.' } },
                    ],
                },
                cancellationToken);

            Console.WriteLine($"   S12F7  -> MDACK={sent}");
            ok &= sent == WaferMapDataAcknowledgement.Accepted;

            // And read back what the host now holds, which is the same map through the other half
            // of the codec.
            var mapBack = await mapper.RequestRowDataAsync(
                new WaferMapDataRequest { MaterialId = "W01" },
                cancellationToken);

            Console.WriteLine($"   S12F13 -> {mapBack?.Rows.Count ?? 0} row(s) came back");
            ok &= mapBack is not null && mapBack.Rows.Count == 3;

            if (mapBack is not null)
            {
                foreach (var row in mapBack.Rows)
                {
                    var bins = string.Join(string.Empty, row.Bins.ToArray().Select(bin => bin == (byte)'.' ? '.' : (char)('0' + bin)));
                    Console.WriteLine($"      row {row.Row,2} from column {row.StartColumn} step {row.Increment,2}: {bins}");
                }
            }

            // The host confirms the transfer, which is what MAPER 0 is for: S12F19 expects no
            // reply, so for a map sent with none of the acknowledged functions it is the only
            // confirmation either end gets.
            WaferMapError? reported = null;
            mapper.ErrorReported += (_, args) => reported = args.Error;

            await mapHost.ReportErrorAsync(WaferMapError.ReceivedCorrectly, 0, cancellationToken);
            await WaitUntilAsync(() => reported is not null, "the map error report to arrive");

            Console.WriteLine($"   S12F19 <- MAPER={reported}");
            ok &= reported == WaferMapError.ReceivedCorrectly;
        }

        if (materialEquipment is not null && materialHost is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and asks the machine what material it is holding --");
            Console.WriteLine();

            // Stream 3, where both ends send. These three are the host asking.
            foreach (var piece in await materialHost.RequestStatusAsync(cancellationToken))
            {
                Console.WriteLine($"   S3F1  -> {piece.MaterialId} at location {piece.Location}, quantity {piece.Quantity}");
            }

            foreach (var piece in await materialHost.RequestTimeToCompletionAsync(cancellationToken))
            {
                // A number, in whatever unit the machine counts in: TTC has no unit in the
                // catalogue, so nothing here turns it into a duration.
                Console.WriteLine($"   S3F3  -> {piece.MaterialId} has {piece.TimeToCompletion} left");
            }

            var where = await materialHost.FindMaterialAsync(StartedLot, cancellationToken);

            Console.WriteLine($"   S3F9  -> {StartedLot} is at {where?.Location.ToString(CultureInfo.InvariantCulture) ?? "nowhere this machine knows"}");
            ok &= where is not null;

            // Then the two-step that stream 3 is built around: the transfer is agreed to, and the
            // movement is it actually happening. A host that treated S3F12 as the arrival would
            // report material in a place no robot has reached yet.
            MaterialMovementEventArgs? moved = null;
            materialHost.MaterialMoved += (_, args) => moved = args;

            var taken = await materialHost.RequestTransferAsync(
                new MaterialTransfer { MaterialId = StartedLot, Destination = 9 },
                cancellationToken);

            Console.WriteLine($"   S3F11 -> ACKC3={taken}");
            ok &= taken == MaterialAcknowledgement.Accepted;

            var acknowledged = await materialEquipment.ReportMovementAsync(
                new MaterialMovement { MaterialId = StartedLot, Location = 1, Destination = 9 },
                cancellationToken);

            await WaitUntilAsync(() => moved is not null, "the movement event to arrive");

            Console.WriteLine($"   S3F35 <- {moved!.Movement.MaterialId}: {moved.Movement.Location} -> {moved.Movement.Destination} (ACKC3={acknowledged})");
            ok &= acknowledged == MaterialAcknowledgement.Accepted && moved.Movement.Destination == 9;

            // And the machine agrees with itself: the accepted transfer moved it.
            var after = await materialHost.FindMaterialAsync(StartedLot, cancellationToken);

            Console.WriteLine($"   S3F9  -> {StartedLot} is now at {after?.Location.ToString(CultureInfo.InvariantCulture) ?? "nowhere"}");
            ok &= after?.Location == 9;
        }

        if (objects is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and reads the machine as objects with attributes --");
            Console.WriteLine();

            // Stream 14 asks nothing about who is the host: either end may ask, and this end has
            // no objects of its own, so it only asks. The object types are the machine's own -
            // E5 does not restrict them, and a chamber with attributes is the stream used for
            // what it is.
            var types = await objects.GetObjectTypesAsync(cancellationToken);

            Console.WriteLine($"   S14F13 -> {(types.Count == 0 ? "no object types" : string.Join(", ", types))}");
            ok &= types.Count > 0;

            foreach (var type in types)
            {
                foreach (var instance in await objects.GetInstancesAsync(type, cancellationToken))
                {
                    var names = await objects.GetAttributeNamesAsync(instance, type, cancellationToken);

                    Console.WriteLine($"   S14F15 -> {type} {instance}: {string.Join(", ", names)}");
                }
            }

            // One attribute, read and then written. ATTRDATA has no format of its own, so what
            // comes back is whatever the machine put there - here a float, printed as one.
            var before = string.Empty;

            await objects.GetAttributesAsync(
                ChamberObject,
                ChamberType,
                ["Setpoint"],
                values => before = Describe(values),
                cancellationToken);

            Console.WriteLine($"   S14F1  -> {before}");

            var acknowledgement = await objects.SetAttributesAsync(
                ChamberObject,
                ChamberType,
                [new ObjectAttribute { Name = "Setpoint", Value = SecsItem.F8(1_000d) }],
                cancellationToken);

            Console.WriteLine($"   S14F3  -> OBJACK={acknowledgement}");
            ok &= acknowledgement == ObjectAcknowledgement.Successful;

            var after = string.Empty;

            await objects.GetAttributesAsync(
                ChamberObject,
                ChamberType,
                ["Setpoint"],
                values => after = Describe(values),
                cancellationToken);

            Console.WriteLine($"   S14F1  -> {after}");
            ok &= after.Contains("1000", StringComparison.Ordinal);

            // An object the machine does not have is not a failure: S14F2 has no acknowledgement
            // code in it, so an empty list is all it can say.
            var found = await objects.GetAttributesAsync(
                "CH-99",
                ChamberType,
                ["Setpoint"],
                _ => { },
                cancellationToken);

            Console.WriteLine($"   S14F1  -> CH-99: {(found ? "found" : "no such object")}");
            ok &= !found;
        }

        if (carriers is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and a carrier turns up that only the host can vouch for --");
            Console.WriteLine();

            // The machine's own doings. In this mode the machine is in the same process, so the
            // sample drives its state machines the way a load port sensor and an identifier reader
            // would.
            var machine = carriers.Machine;

            machine.SetTransferState(1, PortTransferState.ReadyToLoad);
            machine.PlaceCarrier(1, CarrierName);

            // The identifier was read and this machine cannot vouch for it: nothing bound it to
            // the port, so E87 leaves the carrier waiting rather than guessing.
            machine.ReadCarrierId(CarrierName, verified: null);
            machine.ReadSlotMap(CarrierName, SlotMap, verified: true);

            var carrier = machine.FindCarrier(CarrierName);

            Console.WriteLine($"   [machine] {CarrierName} on port 1: identifier {carrier?.IdStatus}, slot map {carrier?.SlotMapStatus}");
            ok &= carrier?.IdStatus == CarrierIdStatus.WaitingForHost;

            // Which the machine refuses to work on, and says why.
            var tooEarly = machine.BeginAccess(CarrierName);

            Console.WriteLine($"   [machine] access refused: {tooEarly.ErrorText}");
            ok &= !tooEarly.IsAccepted;

            // So the host decides. This is the whole of E87 in one message.
            var proceed = await carriers.Host.ProceedWithCarrierAsync(CarrierName, cancellationToken);

            Console.WriteLine($"   S3F17  -> ProceedWithCarrier CAACK={proceed.Acknowledgement}");
            ok &= proceed.IsAccepted;

            Console.WriteLine($"   [machine] access {(machine.BeginAccess(CarrierName).IsAccepted ? "begun" : "still refused")}");
            ok &= machine.FindCarrier(CarrierName)?.AccessStatus == CarrierAccessStatus.InAccess;

            // Asking again is refused, and the refusal names the state - which is what makes
            // CAACK 2 worth reading rather than guessing at.
            var again = await carriers.Host.ProceedWithCarrierAsync(CarrierName, cancellationToken);

            Console.WriteLine($"   S3F17  -> again: CAACK={again.Acknowledgement} ({again.ErrorText})");
            ok &= again.Acknowledgement == CarrierAcknowledgement.CannotPerformNow;

            Console.WriteLine($"   S3F23  -> {CarrierName} is at location {await carriers.Host.FindCarrierAsync(CarrierName, cancellationToken)}");
            Console.WriteLine($"   S3F25  -> port 1 holds {await carriers.Host.ReadCarrierIdAsync(1, cancellationToken) ?? "nothing it will name"}");

            // And the slot map, over stream 14 - which is where E87 puts it, and why this package
            // does not implement S3F27. Twenty-five slots carried as an attribute, no format
            // invented for them.
            if (objects is not null)
            {
                await objects.GetAttributesAsync(
                    CarrierName,
                    "Carrier",
                    ["SlotMap", "SubstrateCount", "AccessStatus"],
                    values =>
                    {
                        var slots = values.Attributes.First(attribute => attribute.Name == "SlotMap");
                        var count = values.Attributes.First(attribute => attribute.Name == "SubstrateCount");
                        var status = values.Attributes.First(attribute => attribute.Name == "AccessStatus");

                        Console.WriteLine(
                            $"   S14F1  -> {CarrierName}: {slots.Value.Count} slots, "
                            + $"{count.Value.FirstValueOrDefault<byte>()} substrates, {status.Value.GetString()}");
                    },
                    cancellationToken);
            }

            // The lot is done, so the carrier leaves - and the port says so rather than the
            // application having to remember.
            machine.EndAccess(CarrierName);

            var out01 = await carriers.Host.CarrierOutAsync(CarrierName, cancellationToken);

            Console.WriteLine($"   S3F33  -> CarrierOut ACKC3={out01}, port 1 is {machine.FindPort(1)?.TransferState}");
            ok &= out01 == CarrierRequestAcknowledgement.Accepted
                && machine.FindPort(1)?.TransferState == PortTransferState.ReadyToUnload;
        }

        Console.WriteLine();
        Console.WriteLine("-- and asks for a stream that is not there --");
        Console.WriteLine();

        // The one message here that goes to the connection rather than through GemHost, because
        // GemHost covers what the equipment side of this library implements and stream 18 is not
        // part of that. A host that needs a message outside the set still has the link: the helper
        // is a layer over it, not a wall in front of it.
        //
        // Subsystem control, which is the third stream this probe has used: it was stream 7 until
        // the equipment answered stream 7, and stream 3 until the material package answered that.
        // So the rule, for whoever adds the next stream: this has to point at a stream nothing in
        // this repository implements, and the set of those keeps shrinking. If the sample stops
        // printing an S9F3 here, that is why. Posted with W=0, because an S9 is what comes back
        // instead of a secondary.
        using (var absent = new SecsMessage(18, 1, replyExpected: false) { Item = SecsItem.U4(1u) })
        {
            await link.PostAsync(absent, cancellationToken);
        }

        await WaitUntilAsync(() => !ErrorReports.IsEmpty, "an error report to come back");

        while (ErrorReports.TryDequeue(out var report))
        {
            Console.WriteLine($"   {report}");
        }

        return ok;
    }

    /// <summary>Wires the three things a machine may say unprompted into the queues above.</summary>
    /// <param name="host">The host.</param>
    /// <remarks>
    /// Three event handlers in place of three routing-table entries that each had to parse a body
    /// and build an acknowledgement. The acknowledgement is still this host's decision - every one
    /// of these event arguments carries a settable code - but the default is the right one and the
    /// parsing is done.
    /// </remarks>
    private static void Subscribe(GemHost host)
    {
        host.EventReportReceived += (_, e) =>
        {
            var values = 0;

            foreach (var report in e.Reports)
            {
                values += report.Values.Count;
            }

            Reports.Enqueue($"S6F11 arrived: CEID={e.EventId}, {e.Reports.Count} report(s), {values} value(s)");
        };

        host.AlarmReceived += (_, e) => Reports.Enqueue($"S5F1 arrived: {e.Alarm}");

        host.TerminalRequestReceived += (_, e) => Reports.Enqueue($"S10F1 arrived: {e}");

        host.ErrorReported += (_, e) =>
        {
            // Barbatos.SecsGem.Catalog earning its place. Without it this line reads
            // "S9F3 came back: S7F1 is not implemented", which is correct and makes you go and
            // look both of them up.
            var offending = "an unknown message";

            if (e.OffendingHeader.Length == MessageHeader.Length)
            {
                var header = MessageHeader.Parse(e.OffendingHeader.Span);
                offending = SecsCatalog.Describe(header.Stream, header.Function);
            }
            else if (e.Description.Length > 0)
            {
                offending = e.Description;
            }

            ErrorReports.Enqueue($"{SecsCatalog.Describe(9, e.Function)} came back: {offending} is not implemented");
        };
    }

    /// <summary>Prints one object's attributes, whatever shape their values turn out to be.</summary>
    /// <param name="values">What came back.</param>
    /// <returns>A line for the console.</returns>
    /// <remarks>
    /// Called inside the callback that reads a get, which is the only place the values are alive:
    /// an attribute value is a view into the reply, and the reply is gone when the method returns.
    /// This builds a string there and then rather than keeping the items.
    /// </remarks>
    private static string Describe(ObjectAttributeValues values)
    {
        List<string> parts = [];

        foreach (var attribute in values.Attributes)
        {
            var value = attribute.Value;

            parts.Add($"{attribute.Name} = " + (value.Format switch
            {
                SecsFormat.F8 => value.FirstValueOrDefault<double>().ToString("0.##", CultureInfo.InvariantCulture),
                SecsFormat.F4 => value.FirstValueOrDefault<float>().ToString("0.##", CultureInfo.InvariantCulture),
                SecsFormat.ASCII => value.GetString(),
                _ when value.IsList => $"a list of {value.Count}",
                _ => $"{value.Format}[{value.Count}]",
            }));
        }

        return $"{values.ObjectSpecification}: {(parts.Count == 0 ? "no attributes" : string.Join(", ", parts))}";
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every loop is being torn down on purpose.
        }
    }
}
