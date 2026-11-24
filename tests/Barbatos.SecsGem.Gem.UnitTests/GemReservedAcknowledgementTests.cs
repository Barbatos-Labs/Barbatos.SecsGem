// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// A machine answering with a code E5 reserves, driven through every reader that can meet one.
/// </summary>
/// <remarks>
/// <para>
/// The companion to <c>GemAcknowledgementsAgreeWithTheReferenceTests</c>, and the half that cannot
/// be faked. That file compares each enum's boundary with its item's table; this one makes a
/// machine send 40 and checks what the host hands back. A boundary that is right in the enum and
/// unused in the reader passes the comparison and fails a fab.
/// </para>
/// <para>
/// <strong>The machine is played by hand</strong> for the reason the stream 18 chapter found when
/// it fixed <c>HCACK</c>: a real <c>GemEquipment</c> writes whatever a handler returns, and a
/// handler returns the enum - so the first version of that test returned <c>Other</c>, the machine
/// wrote 7, the cast gave <c>Other</c> back, and it passed with the fix reverted. Only a
/// hand-written reply can put 40 on the wire.
/// </para>
/// </remarks>
public class GemReservedAcknowledgementTests
{
    private const byte Reserved = 40;

    [Fact(Timeout = 15_000)]
    public async Task A_reserved_code_folds_onto_the_boundary_for_every_reader_that_can_meet_one()
    {
        // Twelve readers, each with the primary it sends, the reply the machine writes back, and
        // the member the arriving 40 must become. Driven in one test because the harness is the
        // expensive part and the assertion is one line each.
        await DriveAsync(1, 15, 16, "OFLACK",
            host => host.RequestOfflineAsync(GemPair.Token),
            (byte)GemOfflineAcknowledgement.Other);

        await DriveAsync(1, 17, 18, "ONLACK",
            host => host.RequestOnlineAsync(GemPair.Token),
            (byte)GemOnlineAcknowledgement.Other);

        await DriveAsync(2, 31, 32, "TIACK",
            host => host.SetClockAsync(DateTimeOffset.UnixEpoch, GemPair.Token),
            (byte)GemTimeAcknowledgement.Other);

        await DriveAsync(5, 3, 4, "ACKC5",
            host => host.EnableAlarmAsync(true, 1, GemPair.Token),
            (byte)GemAlarmAcknowledgement.Error);

        await DriveAsync(7, 1, 2, "PPGNT",
            host => host.InquireProcessProgramAsync("PP-1", 16, GemPair.Token),
            (byte)GemProcessProgramGrant.Other);

        await DriveAsync(7, 17, 18, "ACKC7",
            host => host.DeleteProcessProgramsAsync(["PP-1"], GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        // ACKC7 is read at SEVEN sites and the first version of this test drove one of them, so a
        // control that moved the boundary at another found nothing. The rule is per site and not
        // per item, and counting them is how this file earns its keep: when the matrix chapter
        // added two readers it also found two that had never been driven at all - the plain verify
        // and the formatted one, both written chapters earlier, both passing every comparison.
        await DriveAsync(7, 3, 4, "ACKC7",
            host => host.SendProcessProgramAsync("PP-1", new byte[] { 1, 2 }, GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        await DriveAsync(7, 9, 10, "ACKC7",
            host => host.VerifyProcessProgramAsync("PP-1", new byte[] { 1, 2 }, GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        await DriveAsync(7, 23, 24, "ACKC7",
            host => host.SendFormattedProcessProgramAsync(
                new GemFormattedProcessProgram("PP-1", "BRBTOS", "1.0", []),
                GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        await DriveAsync(7, 27, 28, "ACKC7",
            host => host.VerifyFormattedProcessProgramAsync(
                new GemFormattedProcessProgram("PP-1", "BRBTOS", "1.0", []),
                GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        // The matrix family's two, which is what brought the count to seven.
        await DriveAsync(7, 11, 12, "ACKC7",
            host => host.SendProcessProgramMatrixAsync("MATRIX-1", new byte[] { 1, 2 }, GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        await DriveAsync(7, 15, 16, "ACKC7",
            host => host.VerifyProcessProgramMatrixAsync("MATRIX-1", new byte[] { 1, 2 }, GemPair.Token),
            (byte)GemProcessProgramAcknowledgement.Other);

        // And PPGNT's four, which were in the same state: one driven, three not. S7F31 and S7F33
        // read the item for a question it was not written for - availability, and a formatted
        // load - so a reserved code reaching either of them is exactly as unnamed as it is at
        // S7F1, and nothing was checking.
        await DriveAsync(7, 13, 14, "PPGNT",
            host => host.InquireProcessProgramMatrixAsync("MATRIX-1", 16, GemPair.Token),
            (byte)GemProcessProgramGrant.Other);

        await DriveAsync(7, 31, 32, "PPGNT",
            host => host.IsProcessProgramAvailableAsync("PP-1", GemPair.Token),
            (byte)GemProcessProgramGrant.Other);

        await DriveAsync(7, 33, 34, "PPGNT",
            host => host.InquireFormattedProcessProgramAsync("PP-1", 16, GemPair.Token),
            (byte)GemProcessProgramGrant.Other);

        // And the rest, so every reader with a reserved range is exercised at least once. ACKC10
        // is read at three sites too; one stands for them because they share a line.
        await DriveAsync(2, 15, 16, "EAC",
            host => host.WriteConstantsAsync([], GemPair.Token),
            (byte)GemEquipmentConstantAcknowledgement.Other);

        await DriveAsync(2, 33, 34, "DRACK",
            host => host.DefineReportsAsync([], GemPair.Token),
            (byte)GemDefineReportAcknowledgement.Other);

        await DriveAsync(2, 35, 36, "LRACK",
            host => host.LinkEventsAsync([], GemPair.Token),
            (byte)GemLinkReportAcknowledgement.Other);

        await DriveAsync(2, 37, 38, "ERACK",
            host => host.EnableEventsAsync(true, [], GemPair.Token),
            (byte)GemEnableEventAcknowledgement.Other);

        await DriveAsync(10, 3, 4, "ACKC10",
            host => host.DisplayTerminalMessageAsync("hello", 0, GemPair.Token),
            (byte)GemTerminalAcknowledgement.Other);

        // The fourteenth, added by the chapter that implemented S2F21 - and added without a member
        // at the boundary, which is the hole this whole file exists to catch. It was found by the
        // shared-item comparison instead, because CMDA is also read in
        // Barbatos.SecsGem.ProcessJobs, and that package had it too.
        await DriveAsync(2, 21, 22, "CMDA",
            host => host.SendRemoteCommandAsync("START", GemPair.Token),
            (byte)GemRemoteCommandAcknowledgement.Other);

        // And CMDA is read at three sites now, which the stream 7 sweep discovered the way this
        // file was built to discover things: a control moved the boundary at one of the two new
        // ones and the whole suite stayed green. The same hole ACKC7 had, in the same shape - one
        // site driven, three sites written - so all three are here.
        await DriveAsync(7, 37, 38, "CMDA",
            host => host.SendProgramCommandAsync("RECIPE-1", "START", [], GemPair.Token),
            (byte)GemRemoteCommandAcknowledgement.Other);

        await DriveAsync(7, 41, 42, "CMDA",
            host => host.SendProgramCommandAsync("RECIPE-1", "START", [("LOTID", "LOT-1")], GemPair.Token),
            (byte)GemRemoteCommandAcknowledgement.Other);

        // The fourth CMDA site, added by the chapter that served S7F43. The rule is per site and
        // not per item, so a fourth reader needs a fourth line here however many times the enum
        // and the table have been compared.
        await DriveAsync(7, 43, 44, "CMDA",
            host => host.CancelProgramCommandAsync("RECIPE-1", "START", GemPair.Token),
            (byte)GemRemoteCommandAcknowledgement.Other);

        // The spool reply, which was undriven until the stream 6 chapter went looking - and the
        // rule that predicted the hole is the one above: a site is driven, not an item. The fold
        // was correct all along, so this line never caught anything; what it stops is a later
        // chapter deciding that an enum with no catch-all *member* needs no catch-all behaviour,
        // since Denied is both the only refusal and the whole reserved range.
        //
        // Also the one drive here whose item name was wrong when it was written. GemHost logged
        // the arriving code as RSPACK, an item belonging to S2F44; S6F24 carries SPACK, which the
        // reference leaves undefined, and E30 and this package's own equipment half both call the
        // field RSDA.
        await DriveAsync(6, 23, 24, "RSDA",
            host => host.RequestSpooledDataAsync(GemSpooledDataRequest.Transmit, GemPair.Token),
            (byte)GemSpooledDataAcknowledgement.Denied);

        // GRANT, which the stream 2 chapter brought into this package as its fourth reader in the
        // repository - and at two sites in one chapter, which is the shape the per-site rule warns
        // about: one drive would have left the other free to move its boundary.
        await DriveAsync(2, 1, 2, "GRANT",
            host => host.InquireServiceProgramAsync(7, 7, GemPair.Token),
            (byte)GemGrant.Other);

        await DriveAsync(2, 39, 40, "GRANT",
            host => host.InquireMultiBlockAsync(41, 70_000, GemPair.Token),
            (byte)GemGrant.Other);

        // RSPACK's drive is not here and that is deliberate: S2F44 carries a list, not a bare
        // code, so DriveAsync cannot write the reply - and ResetSpoolingAsync hands back a record
        // rather than an enum, so it does not fit the signature either. It is driven in
        // GemStream2Tests.A_reserved_spooling_code_folds_onto_the_boundary, and this note is here
        // so a sweep counting sites against this file does not conclude there are none.
    }

    /// <summary>Drives one reader against a machine that answers <see cref="Reserved"/>.</summary>
    /// <param name="stream">The stream of the primary the host sends.</param>
    /// <param name="primary">Its function.</param>
    /// <param name="secondary">The reply's function.</param>
    /// <param name="item">The acknowledgement item's name.</param>
    /// <param name="ask">The host call under test.</param>
    /// <param name="expected">The member the reserved code must fold onto.</param>
    /// <returns>A task that completes when the exchange has been checked.</returns>
    private static async Task DriveAsync<T>(
        byte stream,
        byte primary,
        byte secondary,
        string item,
        Func<GemHost, ValueTask<T>> ask,
        byte expected)
        where T : struct, Enum
    {
        var options = new SecsGemOptions
        {
            DeviceId = 7,
            T3 = TimeSpan.FromSeconds(45),
            LinkTestInterval = TimeSpan.FromMinutes(5),
        };

        var clock = new FakeTimeProvider();
        var (active, passive) = InMemorySecsTransport.CreatePair();

        var machineRoutes = new SecsMessageRouterBuilder();

        machineRoutes.OnPrimary(stream, primary, async (transaction, token) =>
        {
            using var reply = new SecsMessage(stream, secondary, replyExpected: false)
            {
                Item = SecsItem.B(Reserved).Named(item),
            };

            await transaction.ReplyAsync(reply, token).ConfigureAwait(false);
        });

        await using var hostLink = new HsmsConnection(options, active, new SecsMessageRouterBuilder().Build(), clock);
        await using var machineLink = new HsmsConnection(options, passive, machineRoutes.Build(), clock);

        using var cts = new CancellationTokenSource();
        var running = Task.WhenAll(hostLink.RunAsync(cts.Token), machineLink.RunAsync(cts.Token));

        try
        {
            for (var attempt = 0; attempt < 400 && !(hostLink.IsSelected && machineLink.IsSelected); attempt++)
            {
                await Task.Delay(1, GemPair.Token);
            }

            Assert.True(hostLink.IsSelected && machineLink.IsSelected, "the pair did not select");

            var log = new RecordingLogger<GemHost>();

            // Bounded on the real clock, which a control earned: this file exists to catch an
            // undriven reader, and until the stream 7 chapter it could hang instead of failing.
            // A control that made a host send the wrong function left the hand-built machine with
            // nothing to answer, so the call waited out a T3 the FakeTimeProvider never advances
            // and the whole run died with no summary - which reads as "the control found nothing".
            var answer = await ask(new GemHost(new GemHostOptions(), hostLink, clock, log))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), GemPair.Token);

            // Three assertions, and the second is the one that matters: before this chapter the
            // cast produced 40 itself, which is not a member of any of these enums.
            Assert.Equal(expected, Convert.ToByte(answer, System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(Enum.IsDefined(answer), $"S{stream}F{secondary} gave back an unnamed {item} value");
            Assert.NotEqual((byte)0, Convert.ToByte(answer, System.Globalization.CultureInfo.InvariantCulture));

            // And the fourth, which a control earned: folding the range onto one member throws the
            // arriving code away, so the log is the only place it survives. Without this assertion
            // deleting the log line changed nothing a test could see.
            Assert.Contains(
                log.From(LogLevel.Information),
                message => message.Contains($"{item} {Reserved}", StringComparison.Ordinal)
                    && message.Contains("reserves", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();

            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how the loops stop.
            }

            await active.DisposeAsync();
            await passive.DisposeAsync();
        }
    }
}
