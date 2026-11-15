// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Samples.Equipment;

/// <summary>
/// A piece of equipment: passive HSMS, waiting to be selected, answering what it implements.
/// </summary>
/// <remarks>
/// <para>
/// The whole thing is the <c>AddSecsGem</c> call below. The connection binds the port, waits for a
/// host, completes the Select handshake, answers Linktest, enforces T3 through T8 and reports S9Fx
/// on its own. What this file adds is only the four messages this equipment claims to implement.
/// </para>
/// <para>
/// Run the host sample against it:
/// </para>
/// <code>
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
/// dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
/// </code>
/// </remarks>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss.fff ";
        });

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

            // The routing table, which is also the declaration of what this equipment understands.
            // A host asking for anything else gets S9F5, or S9F3 for a stream that is not here at
            // all, with nothing further to write.
            .OnPrimary(1, 1, EquipmentHandlers.AreYouThereAsync)
            .OnPrimary(1, 13, EquipmentHandlers.EstablishCommunicationsAsync)
            .OnPrimary(2, 41, EquipmentHandlers.HostCommandAsync)
            .RunAsHostedService();

        builder.Services.AddHostedService<EventReporter>();

        var app = builder.Build();

        var connection = app.Services.GetRequiredService<HsmsConnection>();
        connection.StateChanged += (_, state) => Console.WriteLine($"   [equipment] {state}");

        Console.WriteLine("Barbatos.SecsGem - equipment (passive) on 127.0.0.1:5000, device 7");
        Console.WriteLine("Waiting for a host. Ctrl+C to stop.");
        Console.WriteLine();

        await app.RunAsync();
    }
}

/// <summary>
/// Sends an event report every few seconds, once there is somewhere to send it.
/// </summary>
/// <remarks>
/// The <see cref="HsmsConnection.IsSelected"/> check is the whole of the guard. Posting while the
/// link is down throws rather than queueing, which is deliberate: a report about something that
/// happened thirty seconds ago is not worth delivering, and a queue of them is how a reconnection
/// turns into a flood.
/// </remarks>
internal sealed class EventReporter(HsmsConnection connection, ILogger<EventReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var eventId = 7001u;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!connection.IsSelected)
            {
                continue;
            }

            try
            {
                using var report = SampleMessages.EventReportSend(eventId++);
                using var acknowledgement = await connection.SendAsync(report, stoppingToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Reported event {EventId}; the host answered {Reply} with ACKC6 {Ackc6}.",
                    eventId - 1,
                    acknowledgement,
                    acknowledgement.Item?.FirstValueOrDefault<byte>());
            }
            catch (SecsTimeoutException ex)
            {
                logger.LogWarning("The host did not answer the event report: {Timer} expired.", ex.Timer);
            }
            catch (SecsException ex)
            {
                logger.LogWarning("Could not report the event: {Reason}", ex.Message);
            }
        }
    }
}
