// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
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
                        .AddStatusVariable(PressureVariable, "Pressure", () => SecsItem.F8(chamber.Pressure), "mbar")
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
    Chamber chamber,
    ILogger<ChamberRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var alarmRaised = false;

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
            }

            if (!chamber.IsRunning)
            {
                continue;
            }

            if (await equipment.TriggerEventAsync(Program.ProcessEndEvent, stoppingToken).ConfigureAwait(false))
            {
                logger.LogInformation("Reported ProcessEnd for {LotId}.", chamber.LotId);
            }

            chamber.Stop();
        }
    }
}
