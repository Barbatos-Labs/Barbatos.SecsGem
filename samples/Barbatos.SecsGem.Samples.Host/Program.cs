// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Globalization;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Productivity;
using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Material;
using Barbatos.SecsGem.MaterialFlow;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.PerformanceFlow;
using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Substrates;
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

    /// <summary>E10 availability, published the way a fab actually publishes it: as a status variable.</summary>
    /// <remarks>
    /// The point of the E10 section below. Neither E10 nor E79 defines a message, so the numbers
    /// reach a host as whatever the equipment already publishes - and a GEM status variable is
    /// what that is. Nothing in the productivity package knows this variable exists.
    /// </remarks>
    private const uint AvailabilityVariable = 3;
    private const uint ProcessEndEvent = 1_001;
    private const uint PressureLimitEvent = 2_500;
    private const uint HostReport = 6_000;

    private static readonly ConcurrentQueue<string> ErrorReports = new();
    private static readonly ConcurrentQueue<string> Reports = new();

    /// <summary>Stream 16's state reports, kept apart from the GEM ones.</summary>
    /// <remarks>
    /// Its own queue because the GEM queue is drained as the conversation goes, and an S16F11 that
    /// arrived while a handler was emptying it would be printed as a collection event. They are
    /// different things from different standards.
    /// </remarks>
    private static readonly ConcurrentQueue<string> ProcessReports = new();

    /// <summary>What this sample needs to walk E87, E90, E94 and E40, bundled into one parameter.</summary>
    /// <param name="Host">The host side of E87, which decides.</param>
    /// <param name="Machine">The carrier state machines, which in this mode are in the same process.</param>
    /// <param name="Substrates">The substrate tracker, which has no host side at all: E90 has no messages.</param>
    /// <param name="Jobs">The control jobs, which have no host side either: E94's commands are stream 14's.</param>
    /// <param name="Processes">The process jobs - E40, the one of the four with a stream of its own.</param>
    /// <param name="ProcessHost">The host side of E40, which is a real class because stream 16 has one.</param>
    /// <param name="Bridge">The E87-E90 join, which turned the slot map into these substrates.</param>
    /// <param name="Performance">E116's modules, which account for where this machine's time went.</param>
    /// <param name="Clock">
    /// The clock those durations are measured on - the sample's own, because a demonstration that
    /// waited twenty real minutes to show twenty minutes of etching would not be a demonstration.
    /// It is the same seam a test uses, which is the point worth taking away.
    /// </param>
    /// <param name="Flow">The E116-E10 join, which turns those modules into a shift.</param>
    /// <param name="CarrierJobs">The E87-E94 join, which tells a lot that its boxes turned up.</param>
    private sealed record MaterialDemo(
        CarrierHost Host,
        CarrierManager Machine,
        SubstrateTracker Substrates,
        ControlJobManager Jobs,
        ProcessJobManager Processes,
        ProcessJobHost ProcessHost,
        CarrierSubstrateBridge Bridge,
        PerformanceTracker Performance,
        SampleClock Clock,
        PerformanceProductivityBridge Flow,
        ControlJobCarrierBridge CarrierJobs);

    /// <summary>
    /// A clock the sample moves by hand.
    /// </summary>
    /// <remarks>
    /// Twelve lines, and it is the whole reason E116's figures in this sample are readable: every
    /// duration the performance tracker reports comes from a <see cref="TimeProvider"/>, so a
    /// sample can hand it one that jumps and a test can hand it <c>FakeTimeProvider</c>. Nothing
    /// in the package knows the difference.
    /// </remarks>
    private sealed class SampleClock(DateTimeOffset start) : TimeProvider
    {
        private long _stamp;

        /// <inheritdoc/>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => start.AddTicks(_stamp);

        /// <inheritdoc/>
        public override long GetTimestamp() => _stamp;

        /// <summary>Moves the clock on.</summary>
        /// <param name="by">How far.</param>
        public void Advance(TimeSpan by) => _stamp += by.Ticks;
    }

    /// <summary>The carrier this sample walks through E87.</summary>
    private const string CarrierName = "CAR-001";

    /// <summary>Where this machine processes a wafer, as E90 names the place.</summary>
    private const string ChuckLocation = "CHUCK";

    /// <summary>The machine as a whole, as an E116 module.</summary>
    private const string MachineModule = "EQUIPMENT";

    /// <summary>The chamber that does the work, as an E116 module.</summary>
    /// <remarks>
    /// The same piece of hardware the stream 14 chamber object further up describes, seen from the
    /// other end: that one says what it is set to, this one says what it has been doing with its
    /// time.
    /// </remarks>
    private const string ChamberModule = "PM1";

    /// <summary>What a slot of the carrier is called as a substrate location.</summary>
    /// <param name="slot">The slot, counting from one.</param>
    /// <returns>Its location name.</returns>
    /// <remarks>
    /// E90 leaves the naming to the equipment, which is why this is a method in the sample rather
    /// than something the package decides: a fab that names its slots differently changes this and
    /// nothing else.
    /// </remarks>
    private static string SlotLocation(int slot) => $"{CarrierName}-SLOT-{slot:00}";

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

    /// <summary>The recipe this lot runs, as a <c>PPID</c>.</summary>
    /// <remarks>
    /// Named rather than written five times: the remote command below starts it, E94 carries it as
    /// the control job's processing specification, and E40 carries it as the process job's
    /// <c>PPID</c> - and a sample where those three disagreed would be demonstrating nothing.
    /// </remarks>
    private const string StartedProgram = "RECIPE-7";

    /// <summary>The wafer this sample takes all the way through.</summary>
    /// <remarks>
    /// Slot three of the carrier, which E90 tracks and E40 runs: a process job's material and a
    /// substrate are the same wafer seen by two standards, and correlating them is the
    /// application's business - this line is that correlation.
    /// </remarks>
    private const string Wafer = CarrierName + "-03";

    /// <summary>The process job the machine runs one wafer of this lot under.</summary>
    /// <remarks>
    /// A control job is the lot; a process job is one piece of work inside it. This sample runs one
    /// of each, which is the smallest arrangement that shows why they are two standards.
    /// </remarks>
    private const string ProcessJobName = "PJ-1";

    /// <summary>The control job the host queues for this carrier.</summary>
    /// <remarks>
    /// A host may name a job or leave the machine to; this one asks, because a sample that printed
    /// a generated name would not show that S14F6 carries the name back either way.
    /// </remarks>
    private const string JobName = "CJ-1";

    /// <summary>The chamber, as stream 14 names it.</summary>
    /// <summary>The second chamber, which is the reason E39's enhanced stream 2 exists.</summary>
    /// <remarks>
    /// The first one is <see cref="ChamberModule"/>, and it is deliberately the same constant the
    /// E116 modules use: one piece of hardware, addressed by three standards in this sample - as a
    /// stream 14 object with a setpoint, as an E116 module with an account of its time, and here
    /// as the owner of its own copy of a machine-wide status variable.
    /// </remarks>
    private const string SecondChamber = "PM2";

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

    /// <summary>The tool E10 is accounting for, so the status variable above can read it.</summary>
    private static ProductivityTracker? _productivity;

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

        // And E39's other half, which is eight more primaries on stream 2 - none of them one of
        // the nine GemEquipment already declared above, which is the arrangement worth seeing in a
        // sample: both sets on one routing table with nothing lost.
        EnhancedServicesEquipment? pendingEnhanced = null;

        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingEnhanced!.HandleAsync(transaction, token));
        }

        // And E87: five primaries on stream 3, with the carrier and load port attributes going out
        // through the object services above.
        CarrierEquipment? pendingCarriers = null;

        foreach (var (stream, function) in CarrierEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingCarriers!.HandleAsync(transaction, token));
        }

        // And E40, which is the one of the four material standards with a stream of its own:
        // twelve primaries on stream 16, with a job's attributes going out through the same object
        // services.
        ProcessJobEquipment? pendingProcesses = null;

        foreach (var (stream, function) in ProcessJobEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pendingProcesses!.HandleAsync(transaction, token));
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

        // And two of stream 16, which is the only thing any of E87, E90, E94 or E40 gives a host
        // to answer: a process job changed state, and here is the whole queue.
        ProcessJobHost? pendingProcessHost = null;

        foreach (var (stream, function) in ProcessJobHost.HandledMessages)
        {
            hostRoutes.OnPrimary(stream, function, (transaction, token) => pendingProcessHost!.HandleAsync(transaction, token));
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

        // E39's other half: the same SVID the GEM model below publishes machine-wide, but held
        // per chamber. This is the whole point of the enhanced stream 2 in four lines - a cluster
        // tool has two pressures and S1F3 has nowhere to put the question which one.
        var chamberData = new EnhancedInMemoryProvider();

        chamberData.SetVariable(ChamberModule, EnhancedVariableKind.StatusVariable, PressureVariable, SecsItem.F8(1_013.25d));
        chamberData.SetVariable(SecondChamber, EnhancedVariableKind.StatusVariable, PressureVariable, SecsItem.F8(2.7d));
        chamberData.SetVariable(ChamberModule, EnhancedVariableKind.EquipmentConstant, 7, SecsItem.U4(450));
        chamberData.SetAlarm(
            SecondChamber,
            new EnhancedAlarm { Id = 9000, Code = 0x82, Text = "Chamber pressure out of range" });
        chamberData.SetDiagnostic(ChamberModule, 99, SecsItem.L(SecsItem.A("PUMP"), SecsItem.U4(17)));

        // The command a host can address to one chamber. The control state is not checked here,
        // and this is the one line of the sample that says so out loud: this package cannot see
        // GEM's control state, so an equipment that performs work gates it itself, exactly as
        // GemRemoteCommand.RequiresRemoteControl does above.
        chamberData.SetCommand(
            SecondChamber,
            "PP-SELECT",
            request => request.Parameters.Count == 0
                ? EnhancedCommandResult.From(EnhancedCommandAcknowledgement.InvalidParameter)
                : EnhancedCommandResult.From(EnhancedCommandAcknowledgement.CompletionLater));

        // E87's state machines, with one load port, in service and clear.
        var carrierManager = new CarrierManager();

        carrierManager.AddPort(1);
        carrierManager.SetServiceStatus(1, inService: true);

        // E90's state machines. The slots are locations, and so is the chuck the wafers are
        // processed on: anything that holds one substrate at a time is a location.
        var substrateTracker = new SubstrateTracker();

        // Only the chuck: the twenty-five slot locations used to be declared here in a loop, and
        // the bridge below declares each one as it needs it.
        substrateTracker.AddLocation(ChuckLocation);

        // E94's state machine. Empty, because a control job is a host's instruction: this one
        // arrives as an S14F5 further down.
        var controlJobs = new ControlJobManager();

        // E40's state machine, with room for four jobs. Empty for the same reason E94's is: a
        // process job arrives as an S16F1.
        var processJobs = new ProcessJobManager { JobSpace = 4 };

        // E116's modules, on the sample's own clock. Two of them: the machine as a whole and the
        // chamber that does the work - a flat pair, because E116 does not say how one module's
        // state follows from another's and this package will not guess.
        var moduleClock = new SampleClock(new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero));
        var performance = new PerformanceTracker(moduleClock);

        // E10 and E79 on the same clock, because they are the same day seen from further back:
        // E116 says what a chamber was doing this minute, E10 says what the tool was for the
        // shift. One tracker, because E10 measures a piece of equipment and this is one.
        _productivity = new ProductivityTracker(moduleClock);

        // And the join between them, on the module that is the tool. The two mapping dictionaries
        // are the whole of what a fab has to say: the task types E116 already records carry the
        // intent, and the blocked reason codes are this machine's own words.
        var performanceFlow = new PerformanceProductivityBridge(
            performance,
            _productivity,
            MachineModule,
            new PerformanceProductivityOptions
            {
                BlockedStates =
                {
                    ["NO_MATERIAL"] = EquipmentState.Standby,
                    ["NO_OPERATOR"] = EquipmentState.Standby,
                    ["INTERLOCK"] = EquipmentState.UnscheduledDowntime,
                },
                TaskStates = { [PerformanceTaskTypes.Maintenance] = EquipmentState.ScheduledDowntime },
            });

        performanceFlow.Attach();

        performance.AddModule(MachineModule);
        performance.AddModule(ChamberModule);

        // One provider per connection, six things to answer for - carriers and load ports,
        // substrates and their locations, control jobs, process jobs, performance modules, and
        // this machine's own chamber. The composite is the library's, which is the whole point of
        // it existing: this sample used to carry its own.
        var equipmentObjects = new ObjectServices(
            equipmentLink,
            new CompositeObjectProvider(
                new CarrierObjectProvider(carrierManager),
                new SubstrateObjectProvider(substrateTracker),
                new ControlJobObjectProvider(controlJobs),
                new ProcessJobObjectProvider(processJobs),
                new PerformanceObjectProvider(performance),
                machineObjects));

        var hostObjects = new ObjectServices(hostLink);

        // The asking end of E39's enhanced stream 2. It declares nothing, because every primary in
        // that range travels host to equipment.
        var enhancedHost = new EnhancedServicesHost(hostLink);
        var carrierEquipment = new CarrierEquipment(carrierManager);
        var carrierHost = new CarrierHost(hostLink);
        var processEquipment = new ProcessJobEquipment(processJobs, equipmentLink);
        var processHost = new ProcessJobHost(hostLink);

        // The E87-E90 join, which this sample used to write by hand: a verified slot map becomes
        // one substrate per occupied slot, named the way SlotLocation and the wafer names below
        // already named them. Attached, so nothing has to remember to call it.
        var bridge = new CarrierSubstrateBridge(
            carrierManager,
            substrateTracker,
            new CarrierSubstrateOptions
            {
                SlotLocationName = (_, slot) => SlotLocation(slot),
                SubstrateName = (carrier, slot) => $"{carrier}-{slot:00}",
            });

        bridge.Attach();

        // And the other join in the same package: E40's endings reaching E90's wafers, so the
        // second job below - the one somebody stops part way - leaves a lot that adds up.
        var jobFlow = new ProcessJobSubstrateBridge(processJobs, substrateTracker);

        jobFlow.Attach();

        // And the third, between E87 and E94: a control job finding out that the carriers it
        // named have turned up. The two defaults left alone are the two that matter - every
        // identifier a job names is taken for a carrier, so material that has not arrived holds
        // the lot up, and a stopped carrier does not quietly complete it.
        var carrierJobs = new ControlJobCarrierBridge(carrierManager, controlJobs);

        carrierJobs.Attach();

        carrierJobs.Readied += (_, args) => Console.WriteLine(
            $"   [cj] {args.Result.ControlJobId}: {args.Result.Count} carrier(s) in"
            + (args.Result.IsAccepted ? " -> the lot may run" : $" and the job says no ({args.Result.Reason})"));

        carrierJobs.Stalled += (_, args) => Console.WriteLine(
            $"   [cj] {args.Result.ControlJobId} will not finish by itself: {args.Result.Reason}");

        // The three lines E40 wants and no library can write for you: a state change becomes an
        // S16F11, sent off the manager's lock, with the two values copied inside the handler
        // because the arguments are only alive there.
        processJobs.JobStateChanged += (_, args) =>
        {
            var (job, state) = (args.Job.ProcessJobId, args.Current);

            _ = Task.Run(async () =>
            {
                try
                {
                    await processEquipment.ReportStateAsync(job, state).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.WriteLine($"   [equipment] S16F11 for {job} did not get through: {exception.Message}");
                }
            });
        };

        processHost.JobStateReported += (_, args) => ProcessReports.Enqueue(
            $"S16F11 <- {args.Report.ProcessJobId} is {args.Report.State} at {args.Report.At:HH:mm:ss}");

        // E116's two events, printed rather than published: a CEID belongs to the equipment that
        // defines it, so this is the few lines an application with a GEM layer writes instead.
        performance.StateChanged += (_, args) => Console.WriteLine(
            $"   [ept] {args.Module.ModuleId} {args.Previous} -> {args.Current}"
            + (args.Module.BlockedReason is { } why ? $" ({why.Code}: {why.Text})" : string.Empty)
            + (args.Module.CurrentTask is { } task ? $" [{task.Name}]" : string.Empty));

        performance.TaskEnded += (_, args) => Console.WriteLine(
            $"   [ept] {args.Task.Name} on {args.Module.ModuleId} took {args.Elapsed}, {args.Blocked} of it blocked"
            + (args.Completed ? string.Empty : " - abandoned"));

        pendingEquipment = equipment;
        pendingHost = host;
        pendingMapper = mapper;
        pendingMapHost = mapHost;
        pendingMaterial = materialEquipment;
        pendingMaterialHost = materialHost;
        pendingObjects = equipmentObjects;
        pendingEnhanced = new EnhancedServicesEquipment(chamberData);
        pendingCarriers = carrierEquipment;
        pendingProcesses = processEquipment;
        pendingProcessHost = processHost;

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
                enhancedHost,
                new MaterialDemo(
                    carrierHost,
                    carrierManager,
                    substrateTracker,
                    controlJobs,
                    processJobs,
                    processHost,
                    bridge,
                    performance,
                    moduleClock,
                    performanceFlow,
                    carrierJobs),
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

            // E10's availability as an ordinary status variable, which is the whole answer to
            // "where do these numbers go": neither E10 nor E79 has a message, so they go wherever
            // the equipment already publishes things. The read delegate is three lines and the
            // productivity package knows nothing about it.
            .AddStatusVariable(
                AvailabilityVariable,
                "Availability",
                () => SecsItem.F8(_productivity?.Report().AvailabilityEfficiency ?? 0d),
                "ratio")
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

                // Over a socket the equipment is the other sample, which does not serve E39's
                // enhanced stream 2 - so the host does not ask it.
                enhanced: null,
                flow: null,
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
        EnhancedServicesHost? enhanced,
        MaterialDemo? flow,
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
                var instances = await objects.GetInstancesAsync(type, cancellationToken);

                // Two per type is enough to show the shape. This machine has twenty-six substrate
                // locations once the carrier is mapped, and a sample that printed them all would
                // bury everything after it.
                foreach (var instance in instances.Take(2))
                {
                    var names = await objects.GetAttributeNamesAsync(instance, type, cancellationToken);

                    Console.WriteLine($"   S14F15 -> {type} {instance}: {string.Join(", ", names)}");
                }

                if (instances.Count > 2)
                {
                    Console.WriteLine($"   S14F15 -> {type}: and {instances.Count - 2} more");
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

        if (enhanced is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and asks the same question of one chamber rather than of the machine: E39 --");
            Console.WriteLine();

            // The same SVID three times. GEM's S1F3 can only ask the machine, and the machine has
            // one answer; the two chambers have their own, and the whole of the difference between
            // the two messages is an OBJSPEC in front.
            using (var wide = await host.ReadStatusAsync([PressureVariable], cancellationToken))
            {
                var value = wide.Item is { IsList: true, Count: > 0 } body
                    ? body.Items[0].FirstValueOrDefault<double>()
                    : 0d;

                Console.WriteLine($"   S1F3   -> the machine's Pressure is {value:F2} mbar");
            }

            foreach (var chamber in new[] { ChamberModule, SecondChamber })
            {
                var read = 0d;

                var answered = await enhanced.GetStatusVariablesAsync(
                    chamber,
                    [PressureVariable],
                    values => read = values[0].IsList ? double.NaN : values[0].FirstValueOrDefault<double>(),
                    cancellationToken);

                Console.WriteLine($"   S2F57  -> {chamber}'s Pressure is {read:F2} mbar");
                ok &= answered == 1 && !double.IsNaN(read);
            }

            // And the placeholder rule, which is the one trap in this half of the stream: the
            // reply carries values and no identifiers, so a variable the machine does not have is
            // a zero-length item in its place and never a gap.
            await enhanced.GetStatusVariablesAsync(
                ChamberModule,
                [PressureVariable, 7777],
                values => Console.WriteLine(
                    $"   S2F57  -> two asked for, {values.Count} back, and the missing one is "
                    + $"{(values[1].Count == 0 ? "an empty item in its place" : "something else")}"),
                cancellationToken);

            // A command addressed to a chamber. HCACK 4 is a success - the recipe will be selected
            // and the machine will say so with an event - which is why IsAccepted exists.
            var selected = await enhanced.CommandAsync(
                SecondChamber,
                "PP-SELECT",
                [new ObjectParameter { Name = "PPID", Value = StartedProgram }],
                dataId: 1,
                cancellationToken);

            Console.WriteLine(
                $"   S2F49  -> PP-SELECT on {SecondChamber}: HCACK={selected.Acknowledgement}"
                + $" ({(selected.IsAccepted ? "accepted" : "refused")})");
            ok &= selected.IsAccepted;

            // And the code the catalogue kept for this stream and this stream only. Nothing in GEM
            // can return HCACK 6: S2F41 commands the equipment, and there is only one of those.
            var nowhere = await enhanced.CommandAsync("PM9", "PP-SELECT", cancellationToken: cancellationToken);

            Console.WriteLine($"   S2F49  -> the same command to PM9: HCACK={nowhere.Acknowledgement}");
            ok &= nowhere.Acknowledgement == EnhancedCommandAcknowledgement.NoSuchObject;

            // An alarm list scoped to one chamber, which S5F5 cannot do either.
            var alarms = await enhanced.GetAlarmsAsync(SecondChamber, [], cancellationToken);

            foreach (var alarm in alarms)
            {
                Console.WriteLine(
                    $"   S2F61  -> {SecondChamber} ALID={alarm.Id} {(alarm.IsSet ? "SET" : "CLEARED")}: {alarm.Text}");
            }

            ok &= alarms.Count == 1 && (await enhanced.GetAlarmsAsync(ChamberModule, [], cancellationToken)).Count == 0;

            // A report tied to a chamber: the report itself is GEM's, defined with S2F33 further
            // up, and this says whose copy of those variables it should carry.
            var bound = await enhanced.BindReportAsync(1, ChamberModule, cancellationToken);

            Console.WriteLine($"   S2F51  -> report 1 bound to {ChamberModule}: RPACK={bound.Acknowledgement}");
            ok &= bound.IsAccepted;

            // Twice says nothing, so it is denied - and RPACK has no definition in the catalogue
            // at all, which is why the raw byte comes back beside the enum.
            var again = await enhanced.BindReportAsync(1, ChamberModule, cancellationToken);

            Console.WriteLine($"   S2F51  -> and again: RPACK={again.Code} ({again.Acknowledgement})");
            ok &= !again.IsAccepted;
        }

        if (flow is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and a carrier turns up that only the host can vouch for --");
            Console.WriteLine();

            // The machine's own doings. In this mode the machine is in the same process, so the
            // sample drives its state machines the way a load port sensor and an identifier reader
            // would.
            var machine = flow.Machine;

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
            var proceed = await flow.Host.ProceedWithCarrierAsync(CarrierName, cancellationToken);

            Console.WriteLine($"   S3F17  -> ProceedWithCarrier CAACK={proceed.Acknowledgement}");
            ok &= proceed.IsAccepted;

            Console.WriteLine($"   [machine] access {(machine.BeginAccess(CarrierName).IsAccepted ? "begun" : "still refused")}");
            ok &= machine.FindCarrier(CarrierName)?.AccessStatus == CarrierAccessStatus.InAccess;

            // Asking again is refused, and the refusal names the state - which is what makes
            // CAACK 2 worth reading rather than guessing at.
            var again = await flow.Host.ProceedWithCarrierAsync(CarrierName, cancellationToken);

            Console.WriteLine($"   S3F17  -> again: CAACK={again.Acknowledgement} ({again.ErrorText})");
            ok &= again.Acknowledgement == CarrierAcknowledgement.CannotPerformNow;

            Console.WriteLine($"   S3F23  -> {CarrierName} is at location {await flow.Host.FindCarrierAsync(CarrierName, cancellationToken)}");
            Console.WriteLine($"   S3F25  -> port 1 holds {await flow.Host.ReadCarrierIdAsync(1, cancellationToken) ?? "nothing it will name"}");

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

            Console.WriteLine();
            Console.WriteLine("-- and the wafers inside it, one at a time --");
            Console.WriteLine();

            // What a mapped carrier turns into: one substrate per occupied slot, each at the slot
            // it came out of. This sample wrote that loop by hand twice; it is the bridge's now,
            // and it ran back when the host agreed the slot map - which is the moment a wafer
            // becomes something to track.
            var substrates = flow.Substrates;
            var loaded = flow.Bridge.SubstratesOf(CarrierName).Count;

            Console.WriteLine($"   [machine] {loaded} substrates from the slot map, all AtSource and NeedsProcessing");
            Console.WriteLine($"   [machine] {substrates.Locations.Count} locations: the chuck and one per occupied slot");
            ok &= loaded == 23 && substrates.Substrates.Count == 23;

            // The two empty slots are the two that are not there, and nobody declared a location
            // for them either: a slot with no wafer needs none.
            ok &= substrates.FindSubstrate($"{CarrierName}-13") is null
                && substrates.FindLocation(SlotLocation(13)) is null;

            if (objects is not null)
            {
                Console.WriteLine();
                Console.WriteLine("-- the host asks for the lot to run: E94 --");
                Console.WriteLine();

                // A control job is *created* rather than commanded into being, which is what the
                // actionable half of stream 14 is for - and the only reason this library has it.
                // E94 defines no message of its own, so this is the whole of how a lot is asked
                // for: an object, a command, an attribute read.
                var queued = await objects.CreateAsync(
                    "ControlJob",
                    [
                        new ObjectAttribute { Name = "ObjID", Value = SecsItem.A(JobName) },
                        new ObjectAttribute { Name = "CarrierInputSpec", Value = SecsItem.L(SecsItem.A(CarrierName)) },
                        new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A(StartedProgram) },
                    ],
                    cancellationToken);

                Console.WriteLine($"   S14F5  -> OBJACK={queued.Acknowledgement}, the machine calls it {queued.ObjectId}");
                ok &= queued.Acknowledgement == ObjectAcknowledgement.Successful && queued.ObjectId == JobName;

                // RCMD is an A item, so a command is a string and the vocabulary is E94's.
                var selected = await objects.ActAsync(
                    JobName,
                    "ControlJob",
                    ControlJobCommands.Select,
                    cancellationToken: cancellationToken);

                Console.WriteLine($"   S14F9  -> Select OBJACK={selected}");
                ok &= selected == ObjectAcknowledgement.Successful;

                // And then the one transition no host command can stand in for: the carriers are
                // in and the wafers are mapped. The E87-E94 join is what decides that, and this
                // is the one case it cannot see for itself - this lot's carrier arrived long
                // before anybody asked for the lot, so there is no carrier transition left to
                // notice. Hence the call, which is quiet when the material is not there and so
                // needs no condition around it. The section at the end shows the other order,
                // where nobody calls anything.
                ok &= flow.CarrierJobs.Ready(JobName).IsAccepted;

                // Not that the join was guessing: it found the carrier, at its port, with both
                // verifications agreed - and this job starts itself because nobody asked for a
                // host-initiated one, which is the only thing StartMethod decides.
                ok &= flow.CarrierJobs.Check(JobName).Count == 1;

                await objects.GetAttributesAsync(
                    JobName,
                    "ControlJob",
                    ["State", "Completion", "CarrierInputSpec", "ProcessingCtrlSpec"],
                    values => Console.WriteLine($"   S14F1  -> {Describe(values)}"),
                    cancellationToken);

                ok &= flow.Jobs.Find(JobName)?.State == ControlJobState.Executing;

                // A command the state machine will not have. OBJACK has five codes and none of
                // them means "not from that state", so a refusal arrives as Error and the reason
                // stays on the machine - which is what E94 gives up by having no messages.
                var refused = await objects.ActAsync(
                    JobName,
                    "ControlJob",
                    ControlJobCommands.Start,
                    cancellationToken: cancellationToken);

                Console.WriteLine($"   S14F9  -> Start OBJACK={refused}, because it is already running");
                ok &= refused == ObjectAcknowledgement.Error;

                Console.WriteLine();
                Console.WriteLine("-- and one piece of that lot becomes a process job: E40 --");
                Console.WriteLine();

                // E40 is the one of these four standards with a stream of its own, so this is a
                // transaction rather than an object action: identifier, recipe, material.
                var queuedJob = await flow.ProcessHost.CreateAsync(
                    ProcessJobName,
                    StartedProgram,
                    [Wafer],
                    cancellationToken);

                Console.WriteLine($"   S16F1  -> PJACK={queuedJob}, {ProcessJobName} is queued for {Wafer}");
                ok &= queuedJob == ProcessJobAcknowledgement.Accepted;

                // S16F1 carries three items, so everything else about the job is an attribute -
                // and this one is to wait for a host rather than start itself. That is the whole
                // of E40's division of labour: stream 16 drives, stream 14 describes.
                var described = await objects.SetAttributesAsync(
                    ProcessJobName,
                    ProcessJobObjectProvider.ProcessJobType,
                    [new ObjectAttribute { Name = "PRPROCESSSTART", Value = SecsItem.Boolean(false) }],
                    cancellationToken);

                Console.WriteLine($"   S14F3  -> PRPROCESSSTART=false OBJACK={described}");
                ok &= described == ObjectAcknowledgement.Successful;

                // The machine takes the job up and gets it ready. Each of these raises an S16F11
                // of its own, which is why the queue of reports below has more than one line.
                ok &= flow.Processes.BeginSetup(ProcessJobName).IsAccepted;
                ok &= flow.Processes.SetupComplete(ProcessJobName).IsAccepted;
                ok &= flow.Processes.Find(ProcessJobName)?.State == ProcessJobState.WaitingForStart;

                var started = await flow.ProcessHost.CommandAsync(
                    ProcessJobName,
                    ProcessJobCommands.Start,
                    cancellationToken);

                Console.WriteLine($"   S16F3  -> START CMDA={started}");
                ok &= started == ProcessJobCommandAcknowledgement.Performed;

                // A command this machine has never heard of is a different answer from one it
                // cannot do yet, and CMDA is defined with both - which is the one thing E40 can
                // tell a host that E94 cannot.
                var nonsense = await flow.ProcessHost.CommandAsync(ProcessJobName, "TELEPORT", cancellationToken);

                Console.WriteLine($"   S16F3  -> TELEPORT CMDA={nonsense}, as against CannotPerformNow");
                ok &= nonsense == ProcessJobCommandAcknowledgement.DoesNotExist;
            }

            // One wafer through, with the gate E90 exists for shown on the way.
            var early = substrates.BeginProcessing(Wafer);

            Console.WriteLine($"   [machine] work refused: {early.Reason}");
            ok &= !early.IsAccepted;

            substrates.Describe(Wafer, lotId: StartedLot, substrateType: "Wafer");

            ok &= substrates.MoveTo(Wafer, ChuckLocation).IsAccepted;
            ok &= substrates.BeginProcessing(Wafer).IsAccepted;

            if (objects is not null)
            {
                await objects.GetAttributesAsync(
                    Wafer,
                    "Substrate",
                    ["SubstrateState", "SubstrateProcessingState", "SlotID", "LotID"],
                    values => Console.WriteLine($"   S14F1  -> {Describe(values)}"),
                    cancellationToken);
            }

            ok &= substrates.EndProcessing(Wafer, SubstrateProcessingState.Processed).IsAccepted;
            ok &= substrates.MoveTo(Wafer, SlotLocation(3)).IsAccepted;

            if (objects is not null)
            {
                await objects.GetAttributesAsync(
                    Wafer,
                    "Substrate",
                    ["SubstrateState", "SubstrateProcessingState"],
                    values => Console.WriteLine($"   S14F1  -> {Describe(values)}"),
                    cancellationToken);
            }

            var wafer = substrates.FindSubstrate(Wafer);

            // AtDestination now, where this used to read AtSource: the bridge gave the wafer its
            // own slot as a destination, so going back there is the machine letting go of it
            // rather than putting it down where it found it. E90 refuses to move it again, and
            // that is the whole point of a destination.
            ok &= wafer?.ProcessingState == SubstrateProcessingState.Processed
                && wafer.TransportState == SubstrateTransportState.AtDestination
                && !substrates.MoveTo(Wafer, ChuckLocation).IsAccepted;

            if (objects is not null)
            {
                Console.WriteLine();
                Console.WriteLine("-- the wafer is done, so the process job is --");
                Console.WriteLine();

                // The machine's own ending, as against the three a host can ask for. It is also
                // the fourth S16F11 this job has produced.
                ok &= flow.Processes.MaterialProcessed(ProcessJobName).IsAccepted;

                var finished = await flow.ProcessHost.GetStatusAsync(ProcessJobName, cancellationToken);

                Console.WriteLine(
                    $"   S16F5  -> {finished?.ProcessJobId} is {finished?.State} (JOBSTATE {finished?.StateCode}), recipe {finished?.RecipeId}");

                // The code is PRSTATE's own number, which is the one value in this package that
                // came out of the catalogue rather than out of a reading of it.
                ok &= finished?.State == ProcessJobState.ProcessComplete
                    && finished.StateCode == (byte)ProcessJobState.ProcessComplete;

                var place = await flow.ProcessHost.GetQueuePositionAsync(ProcessJobName, cancellationToken);

                Console.WriteLine($"   S16F19 -> QPOSN={place?.Position}, because a finished job is not waiting");
                ok &= place?.Position == 0;

                var dropped = await flow.ProcessHost.DeselectAsync(ProcessJobName, cancellationToken);

                Console.WriteLine($"   S16F9  -> PJACK={dropped}, {flow.Processes.Jobs.Count} process job(s) left");
                ok &= dropped == ProcessJobAcknowledgement.Accepted && flow.Processes.Jobs.Count == 0;

                Console.WriteLine();
                Console.WriteLine("-- and a second job that somebody stops part way --");
                Console.WriteLine();

                // Two more wafers out of the same carrier, so there is something to half-run. The
                // interesting state is a lot where one wafer is on the chuck and one is not.
                const string Stopped = "PJ-2";
                var pair = new[] { $"{CarrierName}-05", $"{CarrierName}-06" };

                ok &= await flow.ProcessHost.CreateAsync(Stopped, StartedProgram, pair, cancellationToken)
                    == ProcessJobAcknowledgement.Accepted;

                ok &= flow.Processes.BeginSetup(Stopped).IsAccepted
                    && flow.Processes.SetupComplete(Stopped).IsAccepted
                    && substrates.MoveTo(pair[0], ChuckLocation).IsAccepted
                    && substrates.BeginProcessing(pair[0]).IsAccepted;

                Console.WriteLine($"   [machine] {pair[0]} is {substrates.FindSubstrate(pair[0])?.ProcessingState}, {pair[1]} is {substrates.FindSubstrate(pair[1])?.ProcessingState}");

                var halted = await flow.ProcessHost.CommandAsync(Stopped, ProcessJobCommands.Stop, cancellationToken);

                Console.WriteLine($"   S16F3  -> STOP CMDA={halted}, and the job is {flow.Processes.Find(Stopped)?.State}");
                ok &= halted == ProcessJobCommandAcknowledgement.Performed;

                ok &= flow.Processes.StopComplete(Stopped).IsAccepted;

                // The join E40 and E90 do not make themselves: the wafer that was being worked on
                // ends the way the job ended, and the one nobody got to is skipped. Without the
                // bridge both would sit as they were for ever, because E90 will not let anything
                // move them once the job is over.
                Console.WriteLine($"   [machine] {pair[0]} is now {substrates.FindSubstrate(pair[0])?.ProcessingState}, {pair[1]} is {substrates.FindSubstrate(pair[1])?.ProcessingState}");

                ok &= substrates.FindSubstrate(pair[0])?.ProcessingState == SubstrateProcessingState.Stopped
                    && substrates.FindSubstrate(pair[1])?.ProcessingState == SubstrateProcessingState.Skipped;

                ok &= await flow.ProcessHost.DeselectAsync(Stopped, cancellationToken)
                    == ProcessJobAcknowledgement.Accepted;

                // Four state changes, each a transaction of its own rather than a collection event
                // somebody had to define: queued to setting up, to waiting, to processing, to
                // complete. This is what E40 has that E90 and E94 do not.
                await WaitUntilAsync(() => ProcessReports.Count >= 4, "the machine's four S16F11 reports to arrive");

                while (ProcessReports.TryDequeue(out var line))
                {
                    Console.WriteLine($"   {line}");
                }

                Console.WriteLine();
                Console.WriteLine("-- the lot is done, so the control job is --");
                Console.WriteLine();

                // The machine's own ending, as against the three a host can ask for: every piece
                // of material was dealt with. A host reads which one it was.
                ok &= flow.Jobs.MaterialFinished(JobName).IsAccepted;

                await objects.GetAttributesAsync(
                    JobName,
                    "ControlJob",
                    ["State", "Completion"],
                    values => Console.WriteLine($"   S14F1  -> {Describe(values)}"),
                    cancellationToken);

                // And only now can it be forgotten: deleting a live job would leave its material
                // with nothing accounting for it.
                var gone = await objects.DeleteAsync(JobName, "ControlJob", cancellationToken);

                Console.WriteLine($"   S14F7  -> OBJACK={gone}, {flow.Jobs.Jobs.Count} job(s) left on the machine");
                ok &= gone == ObjectAcknowledgement.Successful && flow.Jobs.Jobs.Count == 0;

                Console.WriteLine();
                Console.WriteLine("-- and the machine says where its time went: E116 --");
                Console.WriteLine();

                // Driven by hand, and that is not a shortcut - it is the point. Nothing above
                // reports what E116 reports: E40 knows a job is processing and E90 knows a wafer
                // is in process, and neither of them knows the chamber spent four minutes of it
                // standing there waiting for the next wafer. That is the fact this standard
                // exists for, and only the machine has it.
                //
                // In a real equipment these calls live inside the handlers that moved the job;
                // here they are gathered in one place so the printout reads as one story, on the
                // sample's clock so that twenty minutes of etching takes no real time at all.
                ok &= flow.Performance.BeginTask(ChamberModule, StartedProgram, PerformanceTaskTypes.Process)
                    .IsAccepted;

                flow.Clock.Advance(TimeSpan.FromMinutes(12));

                ok &= flow.Performance.Block(ChamberModule, "NO_MATERIAL", "the next wafer is late").IsAccepted;

                // The task is still the chamber's, which is the rule the whole package turns on:
                // these four minutes are charged to this recipe as well as to this module.
                ok &= flow.Performance.FindModule(ChamberModule)?.HasTask == true;

                flow.Clock.Advance(TimeSpan.FromMinutes(4));

                ok &= flow.Performance.Unblock(ChamberModule).IsAccepted;

                flow.Clock.Advance(TimeSpan.FromMinutes(8));

                ok &= flow.Performance.EndTask(ChamberModule).IsAccepted;

                // And then half an hour with nothing to run, which is the factory's problem
                // rather than the machine's - the distinction E116 is for.
                flow.Clock.Advance(TimeSpan.FromMinutes(36));

                if (flow.Performance.Statistics(ChamberModule) is { } spent)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"   {ChamberModule}: {spent.TimeExecuting} working, {spent.TimeBlocked} stuck, "
                        + $"{spent.TimeIdle} waiting for work - {spent.ExecutingShare:P0} utilised");

                    // Twenty working, four stuck, thirty-six idle: the three add up to the window,
                    // which is the invariant the package is built to keep.
                    ok &= spent.Total == spent.TimeIdle + spent.TimeExecuting + spent.TimeBlocked
                        && spent.TasksCompleted == 1;
                }
                else
                {
                    ok = false;
                }

                Console.WriteLine();

                await objects.GetAttributesAsync(
                    ChamberModule,
                    PerformanceObjectProvider.ModuleType,
                    ["EPTState", "TimeExecuting", "TimeBlocked", "TimeIdle"],
                    values => Console.WriteLine($"   S14F1  -> {Describe(values)}"),
                    cancellationToken);

                // And the refusal that makes the figures worth reading. Everything else in this
                // sample has at least one attribute a host owns; a module has none, because a
                // host that could write Executing here could record this chamber as productive
                // while it stood empty.
                var told = await objects.SetAttributesAsync(
                    ChamberModule,
                    PerformanceObjectProvider.ModuleType,
                    [new ObjectAttribute { Name = "EPTState", Value = SecsItem.A("Executing") }],
                    cancellationToken);

                Console.WriteLine($"   S14F3  -> OBJACK={told} - a module's state is observed, not assigned");
                ok &= told == ObjectAcknowledgement.InvalidAttribute;

                Console.WriteLine();
                Console.WriteLine("-- and the shift adds up: E10, and E79's OEE on top of it --");
                Console.WriteLine();

                // A clean shift from here, on the same clock E116 has been using: the two
                // standards are the same day seen from different distances.
                var tool = _productivity!;

                ok &= tool.Reset().IsAccepted;

                // Four hours nobody staffed, two hours of planned maintenance, four hours with
                // nothing to run, ten hours producing, two hours broken, two hours qualifying the
                // new pump. A hundred wafers out, ninety good, five minutes each at the tool's
                // theoretical rate.
                (EquipmentState State, string Reason, int Hours)[] shift =
                [
                    (EquipmentState.NonScheduled, "night shift unstaffed", 4),
                    (EquipmentState.ScheduledDowntime, "preventive maintenance", 2),
                    (EquipmentState.Standby, "no material", 4),
                    (EquipmentState.Productive, string.Empty, 10),
                    (EquipmentState.UnscheduledDowntime, "pump failed", 2),
                    (EquipmentState.Engineering, "qualifying the new pump", 2),
                ];

                foreach (var (state, reason, hours) in shift)
                {
                    if (state is EquipmentState.NonScheduled)
                    {
                        // Already there: a tracker starts non-scheduled, which is the only honest
                        // opening state for a tool nobody has said anything about yet.
                        flow.Clock.Advance(TimeSpan.FromHours(hours));
                        continue;
                    }

                    ok &= tool.MoveTo(state, reason).IsAccepted;
                    flow.Clock.Advance(TimeSpan.FromHours(hours));

                    if (state is EquipmentState.Productive)
                    {
                        tool.RecordUnits(100, 90, TimeSpan.FromMinutes(5));
                        tool.RecordCycles(100);
                    }
                }

                var shiftReport = tool.Report();

                Console.WriteLine(
                    $"   {shiftReport.Productive} producing, {shiftReport.Standby} on standby, "
                    + $"{shiftReport.Downtime} down, {shiftReport.NonScheduled} not scheduled");

                // The two denominators, which is the distinction E10 exists to force. Both are
                // right; they answer different questions.
                Console.WriteLine(
                    $"   available  {shiftReport.Availability:P0} of the time we asked for the tool, "
                    + $"{shiftReport.AvailabilityEfficiency:P0} of the whole day");
                Console.WriteLine(
                    $"   utilised   {shiftReport.OperationalUtilisation:P0} / {shiftReport.TotalUtilisation:P0}");
                Console.WriteLine(
                    $"   MTBF {shiftReport.MeanTimeBetweenFailures}, MTTR {shiftReport.MeanTimeToRepair}, "
                    + $"MCBF {shiftReport.MeanCyclesBetweenFailures:F0} over {shiftReport.Failures} failure(s)");
                Console.WriteLine(
                    $"   OEE {shiftReport.OverallEquipmentEfficiency:P1} = "
                    + $"{shiftReport.AvailabilityEfficiency:P1} x {shiftReport.OperationalEfficiency:P1} x "
                    + $"{shiftReport.RateEfficiency:P1} x {shiftReport.QualityEfficiency:P1}");

                // The six buckets add up to the window, and the four factors multiply out to the
                // whole - the two invariants the package is built to keep, checked here against a
                // day the sample actually lived rather than one a test made up.
                ok &= shiftReport.Total == TimeSpan.FromHours(24)
                    && shiftReport.NonScheduled + shiftReport.UnscheduledDowntime
                        + shiftReport.ScheduledDowntime + shiftReport.Engineering
                        + shiftReport.Standby + shiftReport.Productive == shiftReport.Total
                    && Math.Abs(
                        (shiftReport.AvailabilityEfficiency * shiftReport.OperationalEfficiency
                            * shiftReport.RateEfficiency * shiftReport.QualityEfficiency)
                        - shiftReport.OverallEquipmentEfficiency) < 1e-9;

                // And where the number actually goes. E10 has no message, so availability is a GEM
                // status variable like any other - read with S1F3, by a host that has never heard
                // of E10.
                using (var read = await host.ReadStatusAsync([AvailabilityVariable], cancellationToken))
                {
                    var published = read.Item is { IsList: true, Count: > 0 } body
                        ? body.Items[0].FirstValueOrDefault<double>()
                        : -1d;

                    Console.WriteLine($"   S1F3   -> the host reads Availability = {published:F4}");
                    ok &= Math.Abs(published - shiftReport.AvailabilityEfficiency) < 1e-6;
                }

                Console.WriteLine();
                Console.WriteLine("-- and then nobody types it in: E116 drives E10 --");
                Console.WriteLine();

                // Everything above moved the E10 tracker by hand. This moves nothing but E116, on
                // the module that is the tool, and the shift follows - which is the whole of what
                // the join is for.
                flow.Flow.MappingMissing += (_, args) => Console.WriteLine(
                    $"   [flow] nothing maps {args.PerformanceState}"
                    + (args.TaskType.Length > 0 ? $" '{args.TaskType}'" : string.Empty)
                    + (args.BlockedReason.Length > 0 ? $" '{args.BlockedReason}'" : string.Empty)
                    + $", so it is charged to {args.Charged}");

                ok &= tool.Reset().IsAccepted;
                ok &= flow.Flow.Apply().IsAccepted;

                Console.WriteLine($"   the tool is {tool.State} because {MachineModule} is idle and the shift is staffed");

                (string What, Action Do, int Hours)[] followed =
                [
                    ("the machine starts a lot",
                        () => flow.Performance.BeginTask(MachineModule, StartedProgram, PerformanceTaskTypes.Process),
                        9),
                    ("the next carrier is late",
                        () => flow.Performance.Block(MachineModule, "NO_MATERIAL", "the load port is empty"),
                        3),
                    ("and then somebody opens the door",
                        () => flow.Performance.Block(MachineModule, "INTERLOCK", "maintenance hatch"),
                        1),
                    ("the door is shut again",
                        () => flow.Performance.Unblock(MachineModule),
                        10),
                    ("and the lot is finished",
                        () => flow.Performance.EndTask(MachineModule),
                        1),
                ];

                foreach (var (what, act, hours) in followed)
                {
                    act();

                    Console.WriteLine($"   {what,-34} -> E10 is now {tool.State}");
                    flow.Clock.Advance(TimeSpan.FromHours(hours));
                }

                var followedReport = tool.Report();

                Console.WriteLine();
                Console.WriteLine(
                    $"   {followedReport.Productive} producing, {followedReport.Standby} on standby, "
                    + $"{followedReport.UnscheduledDowntime} broken - and nobody touched the E10 tracker");
                Console.WriteLine(
                    $"   available {followedReport.Availability:P1}, "
                    + $"{followedReport.Failures} failure(s), MTTR {followedReport.MeanTimeToRepair}");

                // The interlock is the point. E116 never moved for it - a blocked module stays
                // blocked - and E10 had to, because a door somebody opened is the tool's fault and
                // a late carrier is not.
                ok &= followedReport.Total == TimeSpan.FromHours(24)
                    && followedReport.Productive == TimeSpan.FromHours(19)
                    && followedReport.Standby == TimeSpan.FromHours(4)
                    && followedReport.UnscheduledDowntime == TimeSpan.FromHours(1)
                    && followedReport.Failures == 1
                    && followedReport.Repairs == 1;
            }

            Console.WriteLine();
            Console.WriteLine("-- and a second lot, asked for before its boxes arrive: E87 drives E94 --");
            Console.WriteLine();

            {
                // The other order, and the one a fab usually runs in: the host asks for a lot and
                // the material handler brings the boxes afterwards. Nothing in this section calls
                // the control job manager at all after Select - the join does it.
                const string NextJob = "CJ-2";
                const string LeftBox = "CAR-002";
                const string RightBox = "CAR-003";

                machine.AddPort(2);
                machine.SetServiceStatus(2, inService: true);
                machine.SetTransferState(2, PortTransferState.ReadyToLoad);
                machine.AddPort(3);
                machine.SetServiceStatus(3, inService: true);
                machine.SetTransferState(3, PortTransferState.ReadyToLoad);

                ok &= flow.Jobs.Create(NextJob, [LeftBox, RightBox]).IsAccepted;
                ok &= flow.Jobs.Select(NextJob).IsAccepted;

                // Nothing has arrived, so the answer names both boxes rather than starting.
                var waiting = flow.CarrierJobs.Check(NextJob);

                Console.WriteLine($"   [cj] {NextJob} selected, and {waiting.Refused.Count} of 2 carrier(s) are not here yet");
                ok &= !waiting.IsAccepted && waiting.Refused.Count == 2;

                // One box up. Still one short, and the join says nothing: a quorum is the point.
                machine.PlaceCarrier(2, LeftBox);
                machine.ReadCarrierId(LeftBox, verified: true);
                machine.ReadSlotMap(LeftBox, SlotMap, verified: true);

                Console.WriteLine($"   [cj] {LeftBox} in, and {NextJob} is {flow.Jobs.Find(NextJob)?.State} - one box short");
                ok &= flow.Jobs.Find(NextJob)?.State == ControlJobState.Selected;

                // And the second. This is the line the whole package is for.
                machine.PlaceCarrier(3, RightBox);
                machine.ReadCarrierId(RightBox, verified: true);
                machine.ReadSlotMap(RightBox, SlotMap, verified: true);

                Console.WriteLine($"   [cj] {RightBox} in, and {NextJob} is {flow.Jobs.Find(NextJob)?.State} - nobody called E94");
                ok &= flow.Jobs.Find(NextJob)?.State == ControlJobState.Executing;

                // The other direction is a method, and the README says why: both managers raise
                // inside their own lock, so only one of the two may listen.
                ok &= flow.CarrierJobs.ClaimCarriers(NextJob).Count == 2;

                // One box finishes and one is stopped - a jammed robot, a wafer somebody pulled.
                machine.EndAccess(LeftBox);
                machine.EndAccess(RightBox, stopped: true);

                // So the lot does not complete itself, and the bridge has already printed why:
                // MaterialFinished records Completed, and a stopped box says the opposite.
                Console.WriteLine($"   [cj] {NextJob} is still {flow.Jobs.Find(NextJob)?.State}, waiting for the host");
                ok &= flow.Jobs.Find(NextJob)?.State == ControlJobState.Executing;

                // Which the host does, with the ending that tells the truth. On the machine side
                // here rather than over an S14F9 - that path is two hundred lines up, and what
                // this section is about is which of these calls nobody had to make.
                ok &= flow.Jobs.Command(NextJob, ControlJobCommands.Abort).IsAccepted;

                Console.WriteLine($"   [cj] the host aborts it, and {NextJob} ended {flow.Jobs.Find(NextJob)?.Completion}");
                ok &= flow.Jobs.Find(NextJob)?.Completion == ControlJobCompletion.Aborted;

                // And now the other direction, which here has nothing to do - the point worth
                // keeping. The machine let both boxes go itself, and that is what ended the job,
                // so the job's ending has nothing left to carry back. E87 says so for each of
                // them rather than pretending: an access that is already over cannot be ended.
                var released = flow.CarrierJobs.ReleaseCarriers(NextJob);

                Console.WriteLine(
                    $"   [cj] {released.Count} carrier(s) to release, {released.Refused.Count} already done with "
                    + $"- {LeftBox} is {machine.FindCarrier(LeftBox)?.AccessStatus}, "
                    + $"{RightBox} is {machine.FindCarrier(RightBox)?.AccessStatus}");

                // ReleaseCarriers earns its keep the other way round: a host that aborts a lot
                // while the machine still holds the boxes. Then these two would be InAccess and
                // both would come back stopped. Which direction ran first is the whole difference,
                // and neither standard can tell on its own.
                ok &= released.Count == 0
                    && released.Refused.Count == 2
                    && machine.FindCarrier(LeftBox)?.AccessStatus == CarrierAccessStatus.CarrierComplete
                    && machine.FindCarrier(RightBox)?.AccessStatus == CarrierAccessStatus.CarrierStopped;
            }

            Console.WriteLine();
            Console.WriteLine("-- and then the carrier leaves --");
            Console.WriteLine();

            // The lot is done, so the carrier leaves - and the port says so rather than the
            // application having to remember.
            machine.EndAccess(CarrierName);

            var out01 = await flow.Host.CarrierOutAsync(CarrierName, cancellationToken);

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
            var firmness = string.Empty;

            if (e.OffendingHeader.Length == MessageHeader.Length)
            {
                var header = MessageHeader.Parse(e.OffendingHeader.Span);
                offending = SecsCatalog.Describe(header.Stream, header.Function);

                // And how much of that stream the catalogue actually knows, which is the honest
                // footnote to naming a message it has never seen implemented. S18 is one of the
                // nine streams the source behind this table documented none of - so the name is
                // an index into E5 and not something to write a codec from.
                if (SecsCatalog.TryGetStream(header.Stream, out var stream)
                    && stream!.Provenance.IsEntirelyRepresentative)
                {
                    firmness = $" - and S{header.Stream} is {stream.Provenance.MessageCount} row(s) "
                        + "the reference documented none of";
                }
            }
            else if (e.Description.Length > 0)
            {
                offending = e.Description;
            }

            ErrorReports.Enqueue(
                $"{SecsCatalog.Describe(9, e.Function)} came back: {offending} is not implemented{firmness}");
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
                // "(none)" rather than nothing at all, because a zero-length item is how this
                // stack says a value is absent - E94's Completion while a job is still running,
                // for one - and a blank after an equals sign reads like a bug.
                SecsFormat.ASCII => value.Count == 0 ? "(none)" : value.GetString(),
                SecsFormat.U1 or SecsFormat.Binary => value.Count == 0
                    ? "(none)"
                    : value.FirstValueOrDefault<byte>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.U2 => value.FirstValueOrDefault<ushort>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.U4 => value.FirstValueOrDefault<uint>().ToString(CultureInfo.InvariantCulture),
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
