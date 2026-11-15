// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Samples.Host;

/// <summary>
/// A host: active HSMS, selecting the equipment and running a GEM opening sequence.
/// </summary>
/// <remarks>
/// <para>Two ways to run it.</para>
/// <para>
/// With no arguments it runs <em>both</em> entities in one process over an in-memory transport -
/// real state machines, real timers, real encoded frames, no port. That is the version worth
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
    private static readonly ConcurrentQueue<string> ErrorReports = new();

    private static async Task<int> Main(string[] args)
        => args.Contains("--tcp") ? await RunOverTcpAsync() : await RunInOneProcessAsync();

    // ------------------------------------------------------------------ in one process

    private static async Task<int> RunInOneProcessAsync()
    {
        Console.WriteLine("Barbatos.SecsGem - a host and a piece of equipment in one process");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        var options = new SecsGemOptions
        {
            DeviceId = 7,
            LinkTestInterval = TimeSpan.FromSeconds(30),
        };

        // Two transports wired to each other. Both halves are real pipes, so each connection sees
        // the same segmented reads it would see on a socket.
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var equipmentRoutes = EquipmentHandlers.RegisterOn(new SecsMessageRouterBuilder());
        var hostRoutes = HostRoutes(new SecsMessageRouterBuilder());

        await using var equipment = new HsmsConnection(options, passive, equipmentRoutes.Build());
        await using var host = new HsmsConnection(options, active, hostRoutes.Build());

        equipment.StateChanged += (_, state) => Console.WriteLine($"   [equipment] {state}");
        host.StateChanged += (_, state) => Console.WriteLine($"   [host]      {state}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = Task.WhenAll(host.RunAsync(cts.Token), equipment.RunAsync(cts.Token));

        try
        {
            await WaitUntilAsync(() => host.IsSelected && equipment.IsSelected, "both ends to be selected");

            var ok = await ConversationAsync(host, equipment, cts.Token);

            Console.WriteLine();
            Console.WriteLine("-- decode paths --");
            Console.WriteLine();
            Console.WriteLine($"   host      : {host.FastPathFrames} fast, {host.SlowPathFrames} slow");
            Console.WriteLine($"   equipment : {equipment.FastPathFrames} fast, {equipment.SlowPathFrames} slow");
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
            .OnPrimary(6, 11, EquipmentHandlers.EventReportAsync)
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

    /// <summary>The GEM opening sequence, and then one message the equipment does not implement.</summary>
    private static async Task<bool> ConversationAsync(
        HsmsConnection host,
        HsmsConnection? equipment,
        CancellationToken cancellationToken)
    {
        var ok = true;

        Console.WriteLine();
        Console.WriteLine("-- the host talks --");
        Console.WriteLine();

        // S1F13: establish communications. The first thing a host says once the link is selected.
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
            ok &= reply is { S: 1, F: 2 };
        }

        using (var request = SampleMessages.HostCommand("START", ("LOTID", "LOT-2026-1115"), ("PPID", "RECIPE-7")))
        using (var reply = await host.SendAsync(request, cancellationToken))
        {
            var hcack = reply.Item?[0].FirstValueOrDefault<byte>();
            Console.WriteLine($"   S2F41 -> {reply}: HCACK={hcack}");
            ok &= hcack == 0;
        }

        // Nothing implements S1F3, so the equipment answers S9F5 rather than leaving the host to
        // wait out T3. Posted with W=0, because an S9 is what comes back instead of a secondary.
        Console.WriteLine();
        Console.WriteLine("-- and asks for something that is not there --");
        Console.WriteLine();

        using (var unimplemented = new SecsMessage(1, 3, replyExpected: false) { Item = SecsItem.U4(1u) })
        {
            await host.PostAsync(unimplemented, cancellationToken);
        }

        await WaitUntilAsync(() => !ErrorReports.IsEmpty, "an error report to come back");

        while (ErrorReports.TryDequeue(out var report))
        {
            Console.WriteLine($"   {report}");
        }

        if (equipment is not null)
        {
            Console.WriteLine();
            Console.WriteLine("-- and the equipment reports an event --");
            Console.WriteLine();

            using var report = SampleMessages.EventReportSend(7001);
            using var acknowledgement = await equipment.SendAsync(report, cancellationToken);

            var ackc6 = acknowledgement.Item?.FirstValueOrDefault<byte>();
            Console.WriteLine($"   S6F11 -> {acknowledgement}: ACKC6={ackc6}");
            ok &= ackc6 == 0;
        }

        return ok;
    }

    private static SecsMessageRouterBuilder HostRoutes(SecsMessageRouterBuilder routes) => routes
        .OnPrimary(6, 11, EquipmentHandlers.EventReportAsync)
        .OnPrimary(9, 5, RecordErrorReportAsync)
        .OnPrimary(9, 3, RecordErrorReportAsync);

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
            // Both loops are being torn down on purpose.
        }
    }
}
