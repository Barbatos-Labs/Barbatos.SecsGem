// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem;
using Barbatos.SecsGem.DataSets;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Material;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.Subsystems;
using Barbatos.SecsGem.WaferMap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Samples.Equipment;

/// <summary>
/// A piece of GEM equipment: passive HSMS, two state models, and a machine to report on.
/// </summary>
/// <remarks>
/// <para>
/// Everything E30 asks for is in the <c>AddGemEquipment</c> call. The GEM layer establishes
/// communications, keeps the control state, answers the standard message set, lets the host define
/// its own reports, and refuses a remote command unless the host is actually in charge. What this
/// file declares is only what <em>this</em> machine has: its variables, its events, its alarm and
/// its commands.
/// </para>
/// <para>
/// Compare it with phase 3's version of this file, which wired four handlers by hand and still had
/// no idea whether it was communicating.
/// </para>
/// <code>
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
/// </code>
/// </remarks>
internal static class Program
{
    // The machine's own identifiers, named rather than scattered as literals.
    internal const uint ControlStateVariable = 1;
    internal const uint PressureVariable = 2;
    internal const uint LotIdVariable = 10;
    internal const uint SetpointConstant = 100;
    internal const uint ProcessStartEvent = 1_000;
    internal const uint ProcessEndEvent = 1_001;
    internal const uint AlarmSetEvent = 2_000;
    internal const uint AlarmClearedEvent = 2_001;
    internal const uint DefaultReport = 5_000;
    internal const ulong PressureAlarm = 9_000;
    internal const uint PressureLimitEvent = 2_500;

    /// <summary>The chamber, as stream 14 names it.</summary>
    internal const string ChamberObject = "CH-1";

    /// <summary>And its type, which is this machine's own vocabulary rather than E5's.</summary>
    internal const string ChamberType = "Chamber";

    /// <summary>The map of a wafer this machine has just finished with.</summary>
    /// <param name="lotId">The lot, which stands in for a wafer identifier here.</param>
    /// <returns>A three-by-four map as map data type 1.</returns>
    /// <remarks>
    /// Row data is the compact format: a row carries its number, the column its first bin belongs
    /// to and the step between bins, so the bins themselves need no coordinates. The middle row is
    /// probed right to left, which is what an increment of -1 says - and the easiest field in
    /// stream 12 to lose by writing it unsigned.
    /// </remarks>
    internal static WaferMapRowData MapOf(string lotId)
        => new()
        {
            MaterialId = string.IsNullOrEmpty(lotId) ? "W01" : lotId,
            IdType = WaferMapIdType.WaferId,
            Rows =
            [
                new WaferMapRow { Row = 0, StartColumn = 0, Increment = 1, Bins = new byte[] { 1, 1, 2, 1 } },
                new WaferMapRow { Row = 1, StartColumn = 3, Increment = -1, Bins = new byte[] { 1, 4, 1, 1 } },
                new WaferMapRow { Row = 2, StartColumn = 0, Increment = 1, Bins = new byte[] { 1, 1, 1, 1 } },
            ],
        };

    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss.fff ";
        });

        builder.Services.AddSingleton<Chamber>();

        builder.Services
            .AddSecsGem(builder.Configuration, options =>
            {
                options.DeviceId = 7;
                options.Mode = HsmsConnectionMode.Passive;
                options.IpAddress = "127.0.0.1";
                options.Port = 5000;

                // Short enough to watch in a console. A fab would leave these at their defaults.
                options.LinkTestInterval = TimeSpan.FromSeconds(15);
            })
            .UseTcp()
            .AddGemEquipment(
                (model, provider) =>
                {
                    // The overload that takes the provider, because a status variable reads the
                    // machine and the machine is a service.
                    var chamber = provider.GetRequiredService<Chamber>();

                    model
                        .AddStatusVariable(ControlStateVariable, "ControlState", () => SecsItem.U1((byte)chamber.ControlState))
                        .AddStatusVariable(PressureVariable, "Pressure", () => SecsItem.F8(chamber.Pressure), "mbar", PressureLimitEvent)
                        .AddDataVariable(LotIdVariable, "LotId", () => SecsItem.A(chamber.LotId))

                        .AddConstant(new GemEquipmentConstant
                        {
                            Id = SetpointConstant,
                            Name = "PressureSetpoint",
                            Units = "mbar",
                            Read = () => SecsItem.F8(chamber.Setpoint),

                            // Writable, with the range check where only the machine knows it.
                            Write = item =>
                            {
                                var candidate = item.FirstValueOrDefault<double>();

                                if (candidate is < 100d or > 2_000d)
                                {
                                    return false;
                                }

                                chamber.Setpoint = candidate;
                                return true;
                            },
                        })

                        .AddEvent(ProcessStartEvent, "ProcessStart")
                        .AddEvent(ProcessEndEvent, "ProcessEnd")
                        .AddEvent(AlarmSetEvent, "PressureAlarmSet")
                        .AddEvent(AlarmClearedEvent, "PressureAlarmCleared")

                        // What a crossing of a host-set pressure band is called. E30 leaves the
                        // event to the equipment because S2F45 carries no CEID: the band is the
                        // host's and the name is the machine's.
                        .AddEvent(PressureLimitEvent, "PressureLimitCrossed")

                        // A default report, which a host is free to replace entirely with S2F33.
                        .AddReport(DefaultReport, ControlStateVariable, PressureVariable, LotIdVariable)

                        .AddAlarm(new GemAlarm
                        {
                            Id = PressureAlarm,
                            Text = "Chamber pressure outside the setpoint window",
                            SetEventId = AlarmSetEvent,
                            ClearEventId = AlarmClearedEvent,
                        })

                        .AddRemoteCommand("START", (context, _) =>
                        {
                            var lot = context.GetText("LOTID");

                            if (string.IsNullOrWhiteSpace(lot))
                            {
                                return ValueTask.FromResult(GemHostCommandAcknowledgement.InvalidParameter);
                            }

                            if (chamber.IsRunning)
                            {
                                return ValueTask.FromResult(GemHostCommandAcknowledgement.AlreadyInDesiredCondition);
                            }

                            chamber.Start(lot);

                            // Accepted and not finished. Completion arrives as ProcessEnd, which is
                            // what HCACK 4 promises - answering 0 here would tell the host a wafer
                            // had been processed before the chamber had pumped down.
                            return ValueTask.FromResult(GemHostCommandAcknowledgement.CompletionLater);
                        })

                        .AddRemoteCommand("STOP", (_, _) =>
                        {
                            if (!chamber.IsRunning)
                            {
                                return ValueTask.FromResult(GemHostCommandAcknowledgement.AlreadyInDesiredCondition);
                            }

                            chamber.Stop();
                            return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
                        });
                },
                builder.Configuration,
                gem =>
                {
                    gem.ModelName = "BRBTOS-1";
                    gem.SoftwareRevision = "0.1.0";
                    gem.EstablishCommunicationsRetryInterval = TimeSpan.FromSeconds(10);
                    gem.DefaultControlState = GemControlState.OnlineRemote;
                })

            // Stream 7, with the in-memory store. Take this line out and the equipment stops
            // answering S7 entirely - the connection reports S9F3 for it, which is the honest
            // answer for a machine that does not hold recipes. Real equipment supplies its own
            // IGemProcessProgramStore over whatever it already persists.
            .AddGemProcessPrograms()

            // The other half of stream 7, opt-in separately: S7F23 carries a recipe as structure
            // E5 defines - PPID, the model and revision it was written for, and a list of CCODE
            // commands - where S7F3 carries one as bytes this machine happens to understand.
            // Equipment may offer either half, both, or neither.
            .AddGemFormattedProcessPrograms()

            // Terminal services: the host can put text in front of whoever is standing at the
            // machine. GemInMemoryTerminal keeps the messages and shows them to nobody, which is
            // all a sample can do - real equipment implements IGemTerminal over its HMI. Leave the
            // line out and stream 10 answers S9F3, which is the honest answer for a machine with
            // no screen rather than ACKC10 2 for every message.
            .AddGemTerminal()

            // Trace data collection: the host asks for a variable on an interval and the equipment
            // samples on its own clock. Nothing to register with it - a trace samples the status
            // variables declared above - so this line is the whole capability.
            .AddGemTraces()

            // Limits monitoring: a host sets the band it cares about for the pressure, and the
            // runner below asks the equipment to check it after every drift. Without the check,
            // nothing in this layer would ever notice - a status variable is a delegate it reads
            // on demand, not something it watches.
            .AddGemLimits()

            // Spooling to a directory, bounded by SpoolCapacity. Reports raised while no host is
            // listening are held, and handed over only when the host asks with S6F23 - it may also
            // tell the equipment to throw them away, which is a decision a host that has just come
            // up is entitled to.
            //
            // AddGemSpooling() instead would hold them in memory: one word shorter, and it loses
            // them when this process stops. Worth trying the difference. Start this with no host
            // and wait a minute or two - the chamber's pressure wanders out of its window on its
            // own, and the alarm it raises with nobody to tell is what gets held. Then stop it,
            // look in spool/ beside the binary, and start it again: the log says how many records
            // it adopted, and SpoolStartTime is when the oldest alarm happened rather than when
            // this process did.
            .AddGemFileSpooling(Path.Combine(AppContext.BaseDirectory, "spool"))

            // Wafer mapping, which is a different standard in the same socket: E5 stream 12 rather
            // than E30, so it sits beside the GEM layer rather than inside it. It declares one
            // message - S12F19, the error report a host may send about a map - because every other
            // message in the stream is one this end sends. The runner below sends a map when a lot
            // finishes.
            .AddWaferMapEquipment()

            // Data set transfer, E5 stream 13: the one stream where this end answers everything
            // and starts nothing. There is no message for "I have a new data set", so the runner
            // below puts one in the store and the host finds it by asking.
            .AddDataSetEquipment()

            // And stream 18, which is this machine's own sub-parts: the chamber and the load lock,
            // each commandable and readable on its own. Six primaries arrive here and two go the
            // other way - the events and alarms a sub-part raises - so this is the second stream
            // in the stack where both ends send.
            .AddSubsystemEquipment()

            // And material status, the other stream a machine answers that GEM does not cover:
            // five questions about what it is holding, and four reports of its own. The runner
            // below loads the lot into the inventory while it processes and reports it moving out.
            .AddMaterialEquipment()

            // And object services with a provider, which is what makes this end able to answer
            // stream 14 rather than only ask: the runner below keeps the chamber object in step
            // with the chamber itself.
            .AddObjectProvider()
            .RunAsHostedService();

        builder.Services.AddHostedService<ChamberRunner>();

        var app = builder.Build();

        var connection = app.Services.GetRequiredService<HsmsConnection>();
        var equipment = app.Services.GetRequiredService<GemEquipment>();

        connection.StateChanged += (_, state) => Console.WriteLine($"   [link]    {state}");
        equipment.CommunicationStateChanged += (_, state) => Console.WriteLine($"   [comms]   {state}");
        equipment.ControlStateChanged += (_, state) => Console.WriteLine($"   [control] {state}");

        Console.WriteLine("Barbatos.SecsGem - GEM equipment (passive) on 127.0.0.1:5000, device 7");
        Console.WriteLine("Waiting for a host. Ctrl+C to stop.");
        Console.WriteLine();

        await app.RunAsync();
    }
}

/// <summary>The machine: a little state and a pressure that drifts.</summary>
internal sealed class Chamber
{
    private readonly Random _random = new(Seed: 20_261_115);

    /// <summary>Gets the chamber pressure, in millibars.</summary>
    public double Pressure { get; private set; } = 1_013.25d;

    /// <summary>Gets or sets the pressure the recipe asks for.</summary>
    public double Setpoint { get; set; } = 1_013.25d;

    /// <summary>Gets the lot currently loaded, or an empty string.</summary>
    public string LotId { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether a lot is being processed.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Gets the GEM control state, as a status variable reports it.</summary>
    public byte ControlState { get; set; } = (byte)GemControlState.OnlineRemote;

    /// <summary>Gets a value indicating whether the pressure has wandered out of its window.</summary>
    public bool IsOutOfRange => Math.Abs(Pressure - Setpoint) > 50d;

    /// <summary>Loads a lot and starts processing it.</summary>
    /// <param name="lotId">The lot.</param>
    public void Start(string lotId)
    {
        LotId = lotId;
        IsRunning = true;
    }

    /// <summary>Stops processing.</summary>
    public void Stop() => IsRunning = false;

    /// <summary>Moves the pressure a little, the way a real one does.</summary>
    public void Drift() => Pressure += (_random.NextDouble() - 0.45d) * 40d;
}

/// <summary>
/// Drives the machine, and reports what it does.
/// </summary>
/// <remarks>
/// Note what is <em>not</em> here: no check on whether the link is up, no check on whether an event
/// is enabled, no decision about which reports to attach. <c>TriggerEventAsync</c> answers all
/// three and returns <see langword="false"/> when there was nothing to send, because a host that
/// disabled an event has said it does not want it and that is not a fault worth handling.
/// </remarks>
internal sealed class ChamberRunner(
    GemEquipment equipment,
    WaferMapEquipment mapping,
    MaterialEquipment material,
    IMaterialSource inventory,
    IDataSetStore dataSets,
    SubsystemEquipment subsystems,
    ISubsystemStore subsystemStore,
    IObjectProvider objects,
    Chamber chamber,
    ILogger<ChamberRunner> logger) : BackgroundService
{
    /// <summary>Where a lot sits while it is being processed, in this machine's own numbering.</summary>
    private const uint ProcessLocation = 1;

    /// <summary>And where it goes when it is done.</summary>
    private const uint UnloadLocation = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var alarmRaised = false;

        // A data set this machine has, for the host to find with S13F11 and fetch with S13F7.
        // Stream 13 has no primary that announces one, so this is the only way it can exist: put
        // it in the store and wait to be asked. The contents are a SecsItem of this machine's own
        // choosing, because DSDATA has no defined format for anybody to agree on.
        // The machine's sub-parts, which stream 18 has no message to create - so they come from
        // configuration here, the way ports and modules do everywhere else in this repository.
        // One is named and one is numbered, because SSID has no dictionary entry and a real fab
        // has both kinds of machine on the floor.
        if (subsystemStore is SubsystemInMemoryStore parts)
        {
            parts.Add(SubsystemId.Named("CHAMBER-A"), "Process chamber A", "Idle");
            parts.Set(SubsystemId.Named("CHAMBER-A"), 1001, "23.9");
            parts.Set(SubsystemId.Named("CHAMBER-A"), 1002, "4.1", isReadOnly: true);

            parts.Add(SubsystemId.Numbered(2), "Load lock", "Vented");

            parts.SetCodeCondition(new SubsystemCodeCondition
            {
                TargetId = "READER-1",
                Acknowledgement = SubsystemAcknowledgement.Accepted,
                MaterialId = "W01-2026",
                Status = 0,
                Condition = "read first time",
            });

            parts.AnswerCommand = command => new SubsystemCommandResult
            {
                Subsystem = command.Subsystem,

                // PUMP is slow, so it is accepted and not claimed as finished - which is what
                // HCACK 4 is for, and the code in this stream most easily read backwards.
                Acknowledgement = command.Command == "PUMP"
                    ? SubsystemCommandAcknowledgement.CompletionLater
                    : SubsystemCommandAcknowledgement.InvalidCommand,
            };
        }

        if (dataSets is DataSetInMemoryStore store)
        {
            using var log = SecsItem.L(
                SecsItem.A("chamber-log"),
                SecsItem.U4(1, 2, 3, 4),
                SecsItem.A("23.9 degC, 4.1 mTorr"));

            store.Add(41, DataSetPayload.From(log), length: 64);
        }

        // The machine as objects, for stream 14: a chamber with two attributes. The type and the
        // names are this machine's own - E5 does not define object types, which is why no library
        // can - and a host that asks S14F13 finds them without being told they exist.
        if (objects is ObjectInMemoryProvider model)
        {
            model.Add(Program.ChamberObject, Program.ChamberType);
            model.Set(Program.ChamberObject, "Pressure", SecsItem.F8(chamber.Pressure));
            model.Set(Program.ChamberObject, "Setpoint", SecsItem.F8(chamber.Setpoint));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            chamber.Drift();
            chamber.ControlState = (byte)equipment.ControlState;

            // The object model follows the machine rather than being a copy of it taken once: a
            // host reading CH-1 Pressure over stream 14 should see what the pressure is now.
            if (objects is ObjectInMemoryProvider current)
            {
                current.Set(Program.ChamberObject, "Pressure", SecsItem.F8(chamber.Pressure));
                current.Set(Program.ChamberObject, "Setpoint", SecsItem.F8(chamber.Setpoint));
            }

            // The moment a value changed, which is the moment to check it against whatever band
            // the host asked about. Reports nothing until a host has set one.
            await equipment.CheckLimitsAsync(Program.PressureVariable, stoppingToken).ConfigureAwait(false);

            if (chamber.IsOutOfRange != alarmRaised)
            {
                alarmRaised = chamber.IsOutOfRange;

                var sent = alarmRaised
                    ? await equipment.SetAlarmAsync(Program.PressureAlarm, stoppingToken).ConfigureAwait(false)
                    : await equipment.ClearAlarmAsync(Program.PressureAlarm, stoppingToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Pressure alarm {State} at {Pressure:F2} mbar (reported: {Sent}).",
                    alarmRaised ? "set" : "cleared",
                    chamber.Pressure,
                    sent);

                // The same alarm again, about the chamber as a sub-part rather than about the
                // machine: stream 5 says this tool has a problem and stream 18 says which part of
                // it does. The second is the half of stream 18 this end initiates, and the same
                // message carries the clear.
                if (equipment.IsCommunicating)
                {
                    try
                    {
                        await subsystems.ReportAlarmAsync(
                            new SubsystemAlarm
                            {
                                Subsystem = SubsystemId.Named("CHAMBER-A"),
                                AlarmId = (uint)Program.PressureAlarm,
                                IsSet = alarmRaised,
                                Category = SubsystemAlarmCategory.ParameterControlError,
                                Text = "Chamber pressure out of range",
                            },
                            stoppingToken).ConfigureAwait(false);
                    }
                    catch (SecsException exception)
                    {
                        // A host that never registered stream 18 answers S9F3 and no secondary, so
                        // this end waits out T3. Worth logging and not worth stopping over.
                        logger.LogWarning("The host did not take the subsystem alarm: {Reason}", exception.Message);
                    }
                }
            }

            if (!chamber.IsRunning)
            {
                continue;
            }

            // The lot is in the chamber, so it is in the inventory: a host that asks S3F1 while
            // this one is running should see it rather than an empty machine.
            if (inventory is MaterialInMemorySource holdings)
            {
                holdings.Load(chamber.LotId, ProcessLocation, quantity: 25);
            }

            // And the chamber's own state follows the chamber, so a host asking S18F3 sees what
            // the part is doing rather than what the machine is doing.
            if (subsystemStore is SubsystemInMemoryStore running)
            {
                running.SetState(SubsystemId.Named("CHAMBER-A"), "Processing");
                running.Set(SubsystemId.Named("CHAMBER-A"), 1001, chamber.Pressure.ToString("F2", CultureInfo.InvariantCulture));
            }

            if (await equipment.TriggerEventAsync(Program.ProcessEndEvent, stoppingToken).ConfigureAwait(false))
            {
                logger.LogInformation("Reported ProcessEnd for {LotId}.", chamber.LotId);
            }

            // And the map of what was just probed. Stream 12 runs the other way round from GEM:
            // this end sends and the host answers, so unlike a report there is nothing to spool
            // and nobody to hold it for. With no host there is nothing to send it to, which is
            // what IsCommunicating is being asked here.
            if (equipment.IsCommunicating)
            {
                try
                {
                    var taken = await mapping
                        .SendRowDataAsync(Program.MapOf(chamber.LotId), stoppingToken)
                        .ConfigureAwait(false);

                    logger.LogInformation("Sent the wafer map for {LotId} (MDACK: {Taken}).", chamber.LotId, taken);
                }
                catch (SecsException exception)
                {
                    // A host that has not registered stream 12 answers S9F3 and never a secondary,
                    // so this end waits out T3 and gets a SecsTimeoutException. Worth logging and
                    // not worth stopping over: the wafers keep coming either way.
                    logger.LogWarning("The host did not take the wafer map: {Reason}", exception.Message);
                }

                // And the lot leaving the chamber, which in stream 3 is a movement rather than a
                // status change: it has gone from one location to another, and a host tracking
                // where material is needs both numbers.
                try
                {
                    var taken = await material
                        .ReportMovementAsync(
                            new MaterialMovement
                            {
                                MaterialId = chamber.LotId,
                                Location = ProcessLocation,
                                Destination = UnloadLocation,
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    logger.LogInformation("Reported {LotId} moving to the unload port (ACKC3: {Taken}).", chamber.LotId, taken);
                }
                catch (SecsException exception)
                {
                    logger.LogWarning("The host did not take the movement report: {Reason}", exception.Message);
                }
            }

            if (inventory is MaterialInMemorySource emptying)
            {
                emptying.Unload(chamber.LotId);
            }

            chamber.Stop();
        }
    }
}
