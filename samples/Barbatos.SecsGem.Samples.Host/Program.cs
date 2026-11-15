// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Samples.Shared;
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
/// a real host connection, a real equipment connection and a real <see cref="GemEquipment"/> on
/// top of it, with real timers and real encoded frames and no port. That is the version worth
/// reading: the whole conversation is in one file and it either works or exits non-zero.
/// </para>
/// <code>dotnet run --project samples/Barbatos.SecsGem.Samples.Host</code>
/// <para>With <c>--tcp</c> it connects to the equipment sample over a socket instead.</para>
/// <code>
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
/// </code>
/// </remarks>
internal static class Program
{
    private const uint ControlStateVariable = 1;
    private const uint PressureVariable = 2;
    private const uint LotIdVariable = 10;
    private const uint ProcessEndEvent = 1_001;
    private const uint HostReport = 6_000;

    private static readonly ConcurrentQueue<string> ErrorReports = new();
    private static readonly ConcurrentQueue<string> Reports = new();

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

        // Two transports wired to each other. Both halves are real pipes, so each connection sees
        // the same segmented reads it would see on a socket.
        var (active, passive) = InMemorySecsTransport.CreatePair();

        // The cycle the GEM registration solves with a deferred factory, written out by hand
        // because this sample has no container: the router needs the handlers, the handlers need
        // the equipment, and the equipment needs the connection the router is being built for.
        GemEquipment? pending = null;
        var equipmentRoutes = new SecsMessageRouterBuilder();

        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            equipmentRoutes.OnPrimary(stream, function, (transaction, token) => pending!.HandleAsync(transaction, token));
        }

        await using var equipmentLink = new HsmsConnection(options, passive, equipmentRoutes.Build());
        await using var host = new HsmsConnection(options, active, HostRoutes().Build());
        await using var equipment = new GemEquipment(gemOptions, BuildModel(), equipmentLink);
        pending = equipment;

        equipmentLink.StateChanged += (_, state) => Console.WriteLine($"   [equipment link] {state}");
        host.StateChanged += (_, state) => Console.WriteLine($"   [host link]      {state}");
        equipment.CommunicationStateChanged += (_, state) => Console.WriteLine($"   [equipment comms]   {state}");
        equipment.ControlStateChanged += (_, state) => Console.WriteLine($"   [equipment control] {state}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = Task.WhenAll(
            host.RunAsync(cts.Token),
            equipmentLink.RunAsync(cts.Token),
            equipment.RunAsync(cts.Token));

        try
        {
            await WaitUntilAsync(() => host.IsSelected && equipmentLink.IsSelected, "both ends to be selected");

            // The equipment sends its own S1F13 as soon as the link is selected; the host answers
            // it from the routing table below. This is the moment a selected link becomes a GEM
            // conversation, and nothing may be reported before it.
            await WaitUntilAsync(() => equipment.IsCommunicating, "the equipment to establish communications");

            var ok = await ConversationAsync(host, equipment, cts.Token);

            Console.WriteLine();
            Console.WriteLine("-- decode paths --");
            Console.WriteLine();
            Console.WriteLine($"   host      : {host.FastPathFrames} fast, {host.SlowPathFrames} slow");
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
    {
        var pressure = 1_013.25d;

        return new GemEquipmentModelBuilder()
            .AddStatusVariable(ControlStateVariable, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(PressureVariable, "Pressure", () => SecsItem.F8(pressure), "mbar")
            .AddDataVariable(LotIdVariable, "LotId", () => SecsItem.A("LOT-2026-1115"))
            .AddEvent(ProcessEndEvent, "ProcessEnd")
            .AddRemoteCommand("START", (context, _) =>
            {
                var lot = context.GetText("LOTID");

                return ValueTask.FromResult(string.IsNullOrWhiteSpace(lot)
                    ? GemHostCommandAcknowledgement.InvalidParameter
                    : GemHostCommandAcknowledgement.CompletionLater);
            })
            .Build();
    }

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
            .OnPrimary(1, 13, AcceptCommunicationsAsync)
            .OnPrimary(6, 11, RecordEventReportAsync)
            .OnPrimary(5, 1, RecordAlarmReportAsync)
            .OnPrimary(9, 5, RecordErrorReportAsync)
            .OnPrimary(9, 3, RecordErrorReportAsync);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<HsmsConnection>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var running = host.RunAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => host.IsSelected, "the equipment to select");

            var ok = await ConversationAsync(host, equipment: null, cts.Token);

            Console.WriteLine();
            Console.WriteLine($"   host: {host.FastPathFrames} fast, {host.SlowPathFrames} slow");
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

    /// <summary>Establish, identify, read, configure reporting, command, and ask for something absent.</summary>
    private static async Task<bool> ConversationAsync(
        HsmsConnection host,
        GemEquipment? equipment,
        CancellationToken cancellationToken)
    {
        var ok = true;

        Console.WriteLine();
        Console.WriteLine("-- the host opens the conversation --");
        Console.WriteLine();

        // S1F13 from this side too. The equipment has already established from its end, so this is
        // the second half of a handshake that E30 lets either side start.
        using (var request = SampleMessages.EstablishCommunications())
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var commack = reply.Item?[0].FirstValueOrDefault<byte>();
            var model = reply.Item?[1][0].GetString()?.Trim();
            Console.WriteLine($"   S1F13 -> {reply}: COMMACK={commack}, MDLN={model}");
            ok &= commack == 0;
        }

        using (var request = SampleMessages.AreYouThere())
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            Console.WriteLine($"   S1F1  -> {reply}: {reply.Item}");
            ok &= reply is { S: 1, F: 2 } && reply.Item?.Count == 2;
        }

        // S1F3: read the status variables by identifier.
        using (var request = new SecsMessage(1, 3, replyExpected: true)
        {
            Name = "SelectedEquipmentStatusRequest",
            Item = SecsItem.L(SecsItem.U4(ControlStateVariable), SecsItem.U4(PressureVariable)),
        })
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            Console.WriteLine($"   S1F3  -> {reply}: {reply.Item}");
            ok &= reply.Item?.Count == 2;
        }

        Console.WriteLine();
        Console.WriteLine("-- and configures its own reporting --");
        Console.WriteLine();

        // S2F33: the host defines the report it wants, rather than taking whatever the equipment
        // happens to offer. This is the part of GEM that makes equipment usable by a host nobody
        // wrote it for.
        using (var request = new SecsMessage(2, 33, replyExpected: true)
        {
            Name = "DefineReport",
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(
                    SecsItem.U4(HostReport),
                    SecsItem.L(SecsItem.U4(PressureVariable), SecsItem.U4(LotIdVariable))))),
        })
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var drack = reply.Item?.FirstValueOrDefault<byte>();
            Console.WriteLine($"   S2F33 -> {reply}: DRACK={drack}");
            ok &= drack == 0;
        }

        // S2F35: and says which event it belongs to.
        using (var request = new SecsMessage(2, 35, replyExpected: true)
        {
            Name = "LinkEventReport",
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(
                    SecsItem.U4(ProcessEndEvent),
                    SecsItem.L(SecsItem.U4(HostReport))))),
        })
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var lrack = reply.Item?.FirstValueOrDefault<byte>();
            Console.WriteLine($"   S2F35 -> {reply}: LRACK={lrack}");
            ok &= lrack == 0;
        }

        // S2F37: and turns it on.
        using (var request = new SecsMessage(2, 37, replyExpected: true)
        {
            Name = "EnableDisableEventReport",
            Item = SecsItem.L(SecsItem.Boolean(true), SecsItem.L(SecsItem.U4(ProcessEndEvent))),
        })
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var erack = reply.Item?.FirstValueOrDefault<byte>();
            Console.WriteLine($"   S2F37 -> {reply}: ERACK={erack}");
            ok &= erack == 0;
        }

        Console.WriteLine();
        Console.WriteLine("-- then tells the equipment to do something --");
        Console.WriteLine();

        using (var request = SampleMessages.HostCommand("START", ("LOTID", "LOT-2026-1115"), ("PPID", "RECIPE-7")))
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var hcack = reply.Item?[0].FirstValueOrDefault<byte>();
            Console.WriteLine($"   S2F41 -> {reply}: HCACK={hcack}");

            // 0 is "done" and 4 is "accepted, completion will be a collection event". Both are
            // success; anything else is not.
            ok &= hcack is 0 or 4;
        }

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

        // Stream 7 is not in the routing table at all, so the equipment answers S9F3 rather than
        // leaving the host to wait out T3. Posted with W=0, because an S9 is what comes back
        // instead of a secondary.
        Console.WriteLine();
        Console.WriteLine("-- and asks for a stream that is not there --");
        Console.WriteLine();

        using (var absent = new SecsMessage(7, 1, replyExpected: false) { Item = SecsItem.A("RECIPE-404") })
        {
            await host.PostAsync(absent, cancellationToken);
        }

        await WaitUntilAsync(() => !ErrorReports.IsEmpty, "an error report to come back");

        while (ErrorReports.TryDequeue(out var report))
        {
            Console.WriteLine($"   {report}");
        }

        return ok;
    }

    private static SecsMessageRouterBuilder HostRoutes() => new SecsMessageRouterBuilder()
        .OnPrimary(1, 13, AcceptCommunicationsAsync)
        .OnPrimary(6, 11, RecordEventReportAsync)
        .OnPrimary(5, 1, RecordAlarmReportAsync)
        .OnPrimary(9, 5, RecordErrorReportAsync)
        .OnPrimary(9, 3, RecordErrorReportAsync);

    /// <summary>Answers the equipment's S1F13, which is how a host agrees to talk.</summary>
    private static async ValueTask AcceptCommunicationsAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var reply = new SecsMessage(1, 14, replyExpected: false)
        {
            Name = "EstablishCommunicationsRequestAcknowledge",
            Item = SecsItem.L(
                SecsItem.B(0).Named("COMMACK"),
                SecsItem.L(SecsItem.A("HOST", 6), SecsItem.A("0.1.0"))),
        };

        await transaction.ReplyAsync(reply, cancellationToken);
    }

    /// <summary>Records an event report and acknowledges it.</summary>
    private static async ValueTask RecordEventReportAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
    {
        var body = transaction.Message.Item;
        var ceid = body is { Count: 3 } ? body[1].FirstValueOrDefault<uint>() : 0u;
        var reports = body is { Count: 3 } ? body[2].Count : 0;

        Reports.Enqueue($"S6F11 arrived: CEID={ceid}, {reports} report(s) -> {body}");

        if (transaction.ReplyExpected)
        {
            using var reply = new SecsMessage(6, 12, replyExpected: false) { Item = SecsItem.B(0).Named("ACKC6") };
            await transaction.ReplyAsync(reply, cancellationToken);
        }
    }

    /// <summary>Records an alarm report and acknowledges it.</summary>
    private static async ValueTask RecordAlarmReportAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
    {
        var body = transaction.Message.Item;

        if (body is { Count: 3 })
        {
            var set = (body[0].FirstValueOrDefault<byte>() & 0x80) != 0;
            Reports.Enqueue($"S5F1 arrived: ALID={body[1].FirstValueOrDefault<uint>()} {(set ? "SET" : "CLEARED")} - {body[2].GetString()}");
        }

        if (transaction.ReplyExpected)
        {
            using var reply = new SecsMessage(5, 2, replyExpected: false) { Item = SecsItem.B(0).Named("ACKC5") };
            await transaction.ReplyAsync(reply, cancellationToken);
        }
    }

    /// <summary>Records an incoming Stream 9 report, decoding the MHEAD it points at.</summary>
    private static ValueTask RecordErrorReportAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        var body = transaction.Message.Item;
        var mhead = body is null ? default : body.GetSpan<byte>();
        var offending = mhead.Length == MessageHeader.Length
            ? MessageHeader.Parse(mhead).ToString()
            : "an unknown message";

        ErrorReports.Enqueue($"S{transaction.S}F{transaction.F} came back: {offending} is not implemented");
        return ValueTask.CompletedTask;
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
