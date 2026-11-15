// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
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

        await using var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build());
        await using var hostLink = new HsmsConnection(options, active, hostRoutes.Build());
        await using var equipment = new GemEquipment(
            gemOptions,
            BuildModel(),
            equipmentLink,
            formattedProcessPrograms: new GemInMemoryFormattedProcessProgramStore(),
            terminal: new GemInMemoryTerminal());
        await using var host = new GemHost(hostOptions, hostLink);

        // Neither end of stream 12 is disposable: no state, no timer, nothing to release.
        var mapper = new WaferMapEquipment(equipmentLink);
        var maps = new WaferMapInMemoryStore();
        var mapHost = new WaferMapHost(maps, hostLink);

        pendingEquipment = equipment;
        pendingHost = host;
        pendingMapper = mapper;
        pendingMapHost = mapHost;

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

            var ok = await ConversationAsync(host, hostLink, equipment, mapper, mapHost, cts.Token);

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
            .AddWaferMapHost();

        await using var provider = services.BuildServiceProvider();
        var link = provider.GetRequiredService<HsmsConnection>();
        var host = provider.GetRequiredService<GemHost>();

        Subscribe(host);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var running = link.RunAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => link.IsSelected, "the equipment to select");

            var ok = await ConversationAsync(host, link, equipment: null, mapper: null, mapHost: null, cts.Token);

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

        Console.WriteLine();
        Console.WriteLine("-- and asks for a stream that is not there --");
        Console.WriteLine();

        // The one message here that goes to the connection rather than through GemHost, because
        // GemHost covers what the equipment side of this library implements and stream 3 is not
        // part of that. A host that needs a message outside the set still has the link: the helper
        // is a layer over it, not a wall in front of it.
        //
        // Material status, not stream 7: the equipment sample answers stream 7 now, and this probe
        // stopped working the day it started. Stream 3 is one neither mode implements. Posted with
        // W=0, because an S9 is what comes back instead of a secondary.
        using (var absent = new SecsMessage(3, 1, replyExpected: false) { Item = SecsItem.U4(1u) })
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
