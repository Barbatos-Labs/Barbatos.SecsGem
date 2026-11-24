// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The nine pairs of stream 2 the gap measurement let in, driven end to end.
/// </summary>
/// <remarks>
/// <para>
/// Stream 2 has seven undefined names, more than any other stream in the reference, and three of
/// them were already being carried before this chapter - so the question was never whether an
/// undefined name can be served. Six of the ten remaining rows carry no gap at all, three carry
/// one and it is the acknowledgement, and one is S2F19, whose entire body is a name nobody wrote
/// down. <c>SecsCatalogStream2Tests</c> holds the measurements.
/// </para>
/// <para>
/// <strong>The service program family is not a second flavour of stream 7.</strong> A recipe can
/// be sent, fetched, verified and deleted; a service program is text that can be <em>run</em>, and
/// whose results a host reads back. S2F7 and S2F9 have no stream 7 equivalent.
/// </para>
/// </remarks>
public class GemStream2Tests
{
    private static void Model(GemEquipmentModelBuilder builder) => GemLink.DefaultModel(builder);

    private static GemPair StartWithPrograms(
        out GemInMemoryServiceProgramStore store,
        RecordingLogger<GemEquipment>? logger = null)
    {
        store = new GemInMemoryServiceProgramStore();
        return GemPair.Start(Model, servicePrograms: store);
    }

    // ----------------------------------------------------------------- the service program family

    [Fact(Timeout = 15_000)]
    public async Task The_inquire_grants_and_the_send_files_the_program_under_what_it_granted()
    {
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        Assert.Equal(
            GemGrant.Accepted,
            await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 7, GemPair.Token)));

        var sent = await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("PRINT 1", GemPair.Token));

        Assert.True(sent.IsAccepted);
        Assert.Equal((byte)0, sent.Code);

        // S2F3 carries no SPID, so this is the whole proof that the handshake correlated: the text
        // came back under the identifier the inquire named.
        Assert.Equal(
            "PRINT 1",
            await GemPair.SentAsync(pair.Host.RequestServiceProgramAsync(7, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_send_with_no_inquire_before_it_is_refused_and_the_log_says_why()
    {
        var log = new RecordingLogger<GemEquipment>();
        var store = new GemInMemoryServiceProgramStore();

        await using var link = GemLink.Start(Model, logger: log, servicePrograms: store);

        await link.SelectAsync();
        using (await link.EstablishAsync())
        {
        }

        using var send = new SecsMessage(2, 3, replyExpected: true)
        {
            Name = "ServiceProgramSend",
            Item = SecsItem.A("PRINT 1").Named("SPD"),
        };

        using var reply = await link.RequestAsync(send);

        // ACKC2 1, and nothing stored. The refusal is the only thing the row can say, so the
        // reason lives in the log - which is why this asserts the log and not only the byte.
        Assert.Equal(2, reply.S);
        Assert.Equal(4, reply.F);
        Assert.Equal(SecsItem.B(1), reply.Item);
        Assert.Empty(await store.ListAsync(GemLink.Token));

        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S2F3", StringComparison.Ordinal)
                && message.Contains("no S2F1", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task One_inquire_grants_one_send_and_the_second_is_refused()
    {
        await using var pair = StartWithPrograms(out var store);
        await pair.OpenAsync();

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 7, GemPair.Token));

        Assert.True((await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("ONE", GemPair.Token))).IsAccepted);

        // The grant is consumed, not held: the handshake is per transfer, so a second send with no
        // second inquire has nothing to file itself under.
        Assert.False((await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("TWO", GemPair.Token))).IsAccepted);

        Assert.Equal("ONE", await store.LoadAsync(7, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_refused_inquire_means_the_send_never_reaches_the_store()
    {
        // Written first with an in-memory store whose MaxLength refused both the inquire and the
        // save, and a control that granted the SPID anyway found nothing: the send was refused by
        // the store rather than by the missing grant, so the test could not tell the two apart.
        // This store refuses the inquire and would happily take the save, which is what makes the
        // claim checkable.
        var store = new CountingStore { Grant = GemGrant.NoSpace };

        await using var pair = GemPair.Start(Model, servicePrograms: store);
        await pair.OpenAsync();

        Assert.Equal(
            GemGrant.NoSpace,
            await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 9_000, GemPair.Token)));

        Assert.False((await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("LONGER", GemPair.Token))).IsAccepted);

        // The point: the equipment refused it on its own, without asking.
        Assert.Equal(0, store.Saves);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_the_machine_does_not_have_comes_back_as_nothing()
    {
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        // A zero-length SPD, read as null. The row carries no acknowledgement to put a refusal in,
        // and an empty service program is not a program - so nothing is lost by the ambiguity.
        Assert.Null(await GemPair.SentAsync(pair.Host.RequestServiceProgramAsync(999, GemPair.Token)));
        Assert.Null(await GemPair.SentAsync(pair.Host.ReadServiceProgramResultsAsync(999, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task Running_a_program_produces_results_a_host_can_read_back()
    {
        var store = new GemInMemoryServiceProgramStore
        {
            Run = (id, text) => $"ran {id}: {text.Length} chars",
        };

        await using var pair = GemPair.Start(Model, servicePrograms: store);
        await pair.OpenAsync();

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 7, GemPair.Token));
        await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("PRINT 1", GemPair.Token));

        Assert.True((await GemPair.SentAsync(pair.Host.RunServiceProgramAsync(7, GemPair.Token))).IsAccepted);

        // S2F7 and S2F9 are the two rows stream 7 has no equivalent of, and this is them working
        // as a pair: accepted means it started, and the results are a separate question.
        Assert.Equal(
            "ran 7: 7 chars",
            await GemPair.SentAsync(pair.Host.ReadServiceProgramResultsAsync(7, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_was_not_told_how_to_run_a_program_refuses_to_run_one()
    {
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 7, GemPair.Token));
        await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("PRINT 1", GemPair.Token));

        // The default, left alone. Nothing in memory can run anything, and answering ACKC2 nought
        // would tell a host a program had started when nothing started - the same way round as
        // AnswerExceptionAbort and for the same reason.
        Assert.False((await GemPair.SentAsync(pair.Host.RunServiceProgramAsync(7, GemPair.Token))).IsAccepted);
        Assert.Null(await GemPair.SentAsync(pair.Host.ReadServiceProgramResultsAsync(7, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task Replacing_a_program_discards_the_results_of_the_one_it_replaced()
    {
        var store = new GemInMemoryServiceProgramStore { Run = (_, text) => text };

        await using var pair = GemPair.Start(Model, servicePrograms: store);
        await pair.OpenAsync();

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 3, GemPair.Token));
        await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("OLD", GemPair.Token));
        await GemPair.SentAsync(pair.Host.RunServiceProgramAsync(7, GemPair.Token));

        Assert.Equal("OLD", await GemPair.SentAsync(pair.Host.ReadServiceProgramResultsAsync(7, GemPair.Token)));

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, 3, GemPair.Token));
        await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("NEW", GemPair.Token));

        // Whatever the old program produced is not this one's output. Leaving it would answer S2F9
        // with the results of code that is no longer on the machine.
        Assert.Null(await GemPair.SentAsync(pair.Host.ReadServiceProgramResultsAsync(7, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_directory_lists_every_identifier_the_store_holds()
    {
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        foreach (var id in (ulong[])[3, 1, 2])
        {
            await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(id, 1, GemPair.Token));
            await GemPair.SentAsync(pair.Host.SendServiceProgramAsync("X", GemPair.Token));
        }

        var listed = await GemPair.SentAsync(pair.Host.ListServiceProgramsAsync(GemPair.Token));

        Assert.Equal<uint[]>([1, 2, 3], [.. listed.Order()]);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_program_text_travels_exactly_as_it_was_given()
    {
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        // Not trimmed, unlike a PPID. PPID is an A[120] that arrives padded, where SPD is an A[n]
        // exactly as long as the program - so trimming would quietly edit a program whose last
        // line ends in a space.
        const string Program = "PRINT 1\n  REM trailing  ";

        await GemPair.SentAsync(pair.Host.InquireServiceProgramAsync(7, Program.Length, GemPair.Token));
        await GemPair.SentAsync(pair.Host.SendServiceProgramAsync(Program, GemPair.Token));

        Assert.Equal(Program, await GemPair.SentAsync(pair.Host.RequestServiceProgramAsync(7, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_refusal_code_the_reference_does_not_name_comes_back_with_its_byte()
    {
        // ACKC2 has no table at all, so this library reads nought as acceptance and everything
        // else as a refusal - and hands back the byte, because a fab whose machine distinguishes
        // its refusals has nowhere else to read them. Driven against a hand-written machine,
        // because a real GemEquipment only ever writes nought or one.
        var result = await AskMachineAsync(
            2,
            3,
            4,
            () => SecsItem.B(7).Named("ACKC2"),
            host => host.SendServiceProgramAsync("PRINT 1", GemPair.Token));

        Assert.Equal(GemServiceProgramAcknowledgement.Error, result.Acknowledgement);
        Assert.Equal((byte)7, result.Code);
    }

    // ----------------------------------------------------------------- S2F27

    [Fact(Timeout = 15_000)]
    public async Task Initiate_processing_refuses_by_default_and_accepts_when_the_machine_says_so()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // The delegate's own default. Starting processing is the machine doing something, and a
        // machine that has not been told how to start a job cannot start one.
        Assert.Equal(
            GemInitiateProcessingAcknowledgement.Error,
            await GemPair.SentAsync(pair.Host.InitiateProcessingAsync("RECIPE-1", GemPair.Token)));

        string? asked = null;

        pair.Equipment.AnswerInitiateProcessing = id =>
        {
            asked = id;
            return id == "RECIPE-1"
                ? GemInitiateProcessingAcknowledgement.Accepted
                : GemInitiateProcessingAcknowledgement.Error;
        };

        Assert.Equal(
            GemInitiateProcessingAcknowledgement.Accepted,
            await GemPair.SentAsync(pair.Host.InitiateProcessingAsync("RECIPE-1", GemPair.Token)));

        Assert.Equal("RECIPE-1", asked);

        Assert.Equal(
            GemInitiateProcessingAcknowledgement.Error,
            await GemPair.SentAsync(pair.Host.InitiateProcessingAsync("NOT-HERE", GemPair.Token)));

        // And trimmed on the way in, which only a padded PPID can show: this library's own host
        // sends an A[n] exactly as long as the identifier, so a control that removed the trim
        // changed nothing any test could see. A real machine's host pads to A[120].
        using var padded = new SecsMessage(2, 27, replyExpected: true)
        {
            Name = "InitiateProcessingRequest",
            Item = SecsItem.A("RECIPE-1" + new string(' ', 12)).Named("PPID"),
        };

        using var reply = await pair.AskEquipmentAsync(padded);

        Assert.Equal("RECIPE-1", asked);
        Assert.Equal(SecsItem.B(0), reply.Item);
    }

    // ----------------------------------------------------------------- S2F39

    [Fact(Timeout = 15_000)]
    public async Task The_multi_block_inquire_grants_by_default_and_can_be_refused()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Granted unless the machine says otherwise, which is the same way round as the host's
        // side of S6F5: a handshake whose only purpose is to let this end refuse should not refuse
        // by silence.
        Assert.Equal(
            GemGrant.Accepted,
            await GemPair.SentAsync(pair.Host.InquireMultiBlockAsync(41, 70_000, GemPair.Token)));

        (uint Data, uint Length) asked = default;

        pair.Equipment.AnswerMultiBlockInquiry = (dataId, length) =>
        {
            asked = (dataId, length);
            return length > 1_000 ? GemGrant.NoSpace : GemGrant.Accepted;
        };

        Assert.Equal(
            GemGrant.NoSpace,
            await GemPair.SentAsync(pair.Host.InquireMultiBlockAsync(41, 70_000, GemPair.Token)));

        Assert.Equal((41u, 70_000u), asked);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_malformed_inquire_is_refused_as_invalid_rather_than_as_busy()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var malformed = new SecsMessage(2, 39, replyExpected: true)
        {
            Name = "MultiBlockInquire",
            Item = SecsItem.A("not a DATAID and a DATALENGTH"),
        };

        using var reply = await pair.AskEquipmentAsync(malformed);

        // DuplicateOrInvalid is the one code GRANT has that GRANT6 has not, and it is the only one
        // of the four that fits a body this end could not read. Answering Busy would tell a host
        // to try the same malformed message again.
        Assert.Equal(2, reply.S);
        Assert.Equal(40, reply.F);
        Assert.Equal(SecsItem.B((byte)GemGrant.DuplicateOrInvalid), reply.Item);
    }

    // ----------------------------------------------------------------- S2F43

    [Fact(Timeout = 15_000)]
    public async Task Resetting_spooling_names_the_messages_it_suppresses()
    {
        await using var pair = GemPair.Start(Model, spool: new GemInMemorySpool());
        await pair.OpenAsync();

        var reply = await GemPair.SentAsync(
            pair.Host.ResetSpoolingAsync([new GemSpoolStreamReset(6, [11])], GemPair.Token));

        Assert.True(reply.IsAccepted);

        var stream = Assert.Single(reply.Streams);

        // Two levels of acknowledgement, which is what makes the row worth serving whole: RSPACK
        // for the message and a STRACK for each stream named.
        Assert.Equal((byte)6, stream.Stream);
        Assert.True(stream.Accepted);
        Assert.Equal<byte[]>([11], [.. stream.Functions]);

        Assert.Contains(((byte)6, (byte)11), pair.Equipment.SpoolSuppressedMessages);
        Assert.DoesNotContain(((byte)6, (byte)3), pair.Equipment.SpoolSuppressedMessages);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_stream_with_no_functions_names_every_message_of_that_stream()
    {
        await using var pair = GemPair.Start(Model, spool: new GemInMemorySpool());
        await pair.OpenAsync();

        await GemPair.SentAsync(
            pair.Host.ResetSpoolingAsync([new GemSpoolStreamReset(5, [])], GemPair.Token));

        // The reading is this library's, because the reference gives the row two items and no
        // words - but an entry naming a stream and no functions would otherwise mean nothing at
        // all, and that is the worse of the two readings. What "every message of stream 5" means
        // is SpoolableMessages, because those are the only ones ever held.
        foreach (var pairOf in GemEquipment.SpoolableMessages.Where(m => m.Stream == 5))
        {
            Assert.Contains(pairOf, pair.Equipment.SpoolSuppressedMessages);
        }

        Assert.DoesNotContain(((byte)6, (byte)11), pair.Equipment.SpoolSuppressedMessages);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_request_is_accepted_and_changes_nothing()
    {
        await using var pair = GemPair.Start(Model, spool: new GemInMemorySpool());
        await pair.OpenAsync();

        var reply = await GemPair.SentAsync(pair.Host.ResetSpoolingAsync([], GemPair.Token));

        // It names no stream, so it is no instruction. Reading "all of them" into it would be
        // inventing a meaning the row does not carry - which is the opposite decision from the
        // empty function list above, and the two are consistent: in each case the reading taken is
        // the one that does not leave the data pointless.
        Assert.True(reply.IsAccepted);
        Assert.Empty(reply.Streams);
        Assert.Empty(pair.Equipment.SpoolSuppressedMessages);
    }

    [Fact(Timeout = 15_000)]
    public async Task One_unreadable_entry_refuses_the_whole_message()
    {
        await using var pair = GemPair.Start(Model, spool: new GemInMemorySpool());
        await pair.OpenAsync();

        using var mixed = new SecsMessage(2, 43, replyExpected: true)
        {
            Name = "ResetSpoolingStreamsAndFunctions",
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.U1(6).Named("STRID"),
                    SecsItem.L(SecsItem.U1(11).Named("FCNID"))),

                // A stream entry that is not a list of two.
                SecsItem.A("nonsense")),
        };

        using var reply = await pair.AskEquipmentAsync(mixed);

        // RSPACK 1, even though one entry was perfectly readable and took effect. A host given
        // RSPACK 0 with a stream missing from the reply would have no way to tell which of its
        // instructions landed.
        Assert.True(reply.Item is { IsList: true, Count: 2 });
        Assert.Equal(SecsItem.B(1), reply.Item![0]);
        Assert.Equal(1, reply.Item[1].Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_suppressed_report_is_dropped_rather_than_held_and_the_drop_is_logged()
    {
        var log = new RecordingLogger<GemEquipment>();
        var spool = new GemInMemorySpool();
        var link = GemLink.Start(Model, spool: spool, logger: log);

        await using (link)
        {
            // Refused, so the equipment spools with nobody to talk to. This has to come first
            // because it consumes the S1F13 the equipment sends itself on seeing Selected - and
            // reading the reply to anything before that would read the S1F13 instead.
            await link.SelectAsync();

            using (await link.EstablishAsync(GemCommunicationAcknowledgement.Denied))
            {
            }

            await GemLink.WaitUntilAsync(
                () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
                "the equipment to be spooling with nobody to talk to");

            // The HSMS link is still selected whatever GEM makes of the conversation, so the
            // reset is answered. A fab would send it while communicating and lose the host later;
            // the order here is the harness being unable to do that, and what is under test is
            // only whether the suppression set is consulted when a report would be held.
            using (var suppress = new SecsMessage(2, 43, replyExpected: true)
            {
                Name = "ResetSpoolingStreamsAndFunctions",
                Item = SecsItem.L(
                    SecsItem.L(
                        SecsItem.U1(6).Named("STRID"),
                        SecsItem.L(SecsItem.U1(11).Named("FCNID")))),
            })
            {
                using var accepted = await link.RequestAsync(suppress);

                Assert.Equal(44, accepted.F);
            }

            Assert.False(await link.Equipment.TriggerEventAsync(1_001, GemLink.Token));

            // Dropped, not held - and the log is the only place that is visible, because a
            // suppressed report and a report nobody sent leave the same trace, which is none.
            Assert.Equal(0, link.Equipment.SpoolCountActual);

            Assert.Contains(
                log.From(LogLevel.Information),
                message => message.Contains("S6F11", StringComparison.Ordinal)
                    && message.Contains("reset spooling", StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Every_service_program_reply_comes_back_on_the_function_the_reference_pairs_it_with()
    {
        // The connection matches a reply by its system bytes and never by its function, so a
        // handler answering on the wrong one comes back through GemHost looking perfectly correct.
        // Only a frame can see it, which is a lesson this suite has learned twice.
        await using var pair = StartWithPrograms(out _);
        await pair.OpenAsync();

        (byte Primary, byte Secondary, SecsItem Body)[] rows =
        [
            (1, 2, SecsItem.L(SecsItem.U4(7u).Named("SPID"), SecsItem.U4(1u).Named("LENGTH"))),
            (3, 4, SecsItem.A("X").Named("SPD")),
            (5, 6, SecsItem.U4(7u).Named("SPID")),
            (7, 8, SecsItem.U4(7u).Named("SPID")),
            (9, 10, SecsItem.U4(7u).Named("SPID")),
            (11, 12, SecsItem.L()),
        ];

        foreach (var (primary, secondary, body) in rows)
        {
            using var request = new SecsMessage(2, primary, replyExpected: true) { Item = body };
            using var reply = await pair.AskEquipmentAsync(request);

            Assert.Equal(2, reply.S);
            Assert.Equal(secondary, reply.F);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task A_reset_whose_body_is_not_a_list_is_refused()
    {
        await using var pair = GemPair.Start(Model, spool: new GemInMemorySpool());
        await pair.OpenAsync();

        using var malformed = new SecsMessage(2, 43, replyExpected: true)
        {
            Name = "ResetSpoolingStreamsAndFunctions",
            Item = SecsItem.A("not a list of streams"),
        };

        using var reply = await pair.AskEquipmentAsync(malformed);

        // Refused, where an *empty* list is accepted - and the two have to be told apart, because
        // a body this end could not read is a different thing from a host that named no streams.
        Assert.Equal(SecsItem.B(1), reply.Item![0]);
        Assert.Empty(pair.Equipment.SpoolSuppressedMessages);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_reserved_spooling_code_folds_onto_the_boundary()
    {
        // RSPACK's drive, which cannot live in GemReservedAcknowledgementTests because S2F44
        // carries a list rather than a bare code and ResetSpoolingAsync hands back a record rather
        // than an enum. Same claim as every drive in that file: a boundary that is right in the
        // enum and unused in the reader passes every comparison and fails a fab.
        var reply = await AskMachineAsync(
            2,
            43,
            44,
            () => SecsItem.L(SecsItem.B(40).Named("RSPACK"), SecsItem.L()),
            host => host.ResetSpoolingAsync([new GemSpoolStreamReset(6, [11])], GemPair.Token));

        Assert.Equal(GemSpoolResetAcknowledgement.Error, reply.Acknowledgement);
        Assert.True(Enum.IsDefined(reply.Acknowledgement));
        Assert.False(reply.IsAccepted);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_reply_that_is_not_an_acknowledgement_and_a_list_throws()
    {
        // The guard that makes the reader's shape assumption explicit. S2F44 has two items and
        // this library will not guess at a one-item version of it: a reply it cannot read is an
        // exception rather than an empty result, because an empty result would be
        // indistinguishable from a machine that reset nothing.
        await Assert.ThrowsAsync<SecsException>(() => AskMachineAsync(
            2,
            43,
            44,
            () => SecsItem.B(0).Named("RSPACK"),
            host => host.ResetSpoolingAsync([new GemSpoolStreamReset(6, [11])], GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_stream_the_machine_refused_comes_back_with_its_own_acknowledgement_clear()
    {
        // The two levels doing something a single acknowledgement could not: the message was
        // accepted and one stream inside it was not.
        var reply = await AskMachineAsync(
            2,
            43,
            44,
            () => SecsItem.L(
                SecsItem.B(0).Named("RSPACK"),
                SecsItem.L(
                    SecsItem.L(
                        SecsItem.U1(6).Named("STRID"),
                        SecsItem.B(0).Named("STRACK"),
                        SecsItem.L(SecsItem.U1(11).Named("FCNID"))),
                    SecsItem.L(
                        SecsItem.U1(5).Named("STRID"),
                        SecsItem.B(9).Named("STRACK"),
                        SecsItem.L()))),
            host => host.ResetSpoolingAsync(
                [new GemSpoolStreamReset(6, [11]), new GemSpoolStreamReset(5, [])],
                GemPair.Token));

        Assert.True(reply.IsAccepted);
        Assert.Equal(2, reply.Streams.Count);

        Assert.True(reply.Streams[0].Accepted);
        Assert.Equal<byte[]>([11], [.. reply.Streams[0].Functions]);

        // STRACK as a flag, which is the reading LVACK gets at S2F46: nought accepted and the
        // whole non-zero range an error the reference will not explain, so there is one bit in it.
        Assert.False(reply.Streams[1].Accepted);
        Assert.Empty(reply.Streams[1].Functions);
    }

    /// <summary>A store that answers whatever it is told to and counts what it was asked.</summary>
    /// <remarks>
    /// Earned by a control: a store that refuses an inquire <em>and</em> the save that follows
    /// cannot show which of the two refused, so the equipment's own guard was untested.
    /// </remarks>
    private sealed class CountingStore : IGemServiceProgramStore
    {
        private readonly GemInMemoryServiceProgramStore _inner = new();

        public GemGrant Grant { get; set; } = GemGrant.Accepted;

        public int Saves { get; private set; }

        public ValueTask<GemGrant> InquireAsync(uint id, int length, CancellationToken cancellationToken)
            => ValueTask.FromResult(Grant);

        public ValueTask<GemServiceProgramAcknowledgement> SaveAsync(uint id, string text, CancellationToken cancellationToken)
        {
            Saves++;
            return _inner.SaveAsync(id, text, cancellationToken);
        }

        public ValueTask<string?> LoadAsync(uint id, CancellationToken cancellationToken)
            => _inner.LoadAsync(id, cancellationToken);

        public ValueTask<GemServiceProgramAcknowledgement> RunAsync(uint id, CancellationToken cancellationToken)
            => _inner.RunAsync(id, cancellationToken);

        public ValueTask<string?> ResultsAsync(uint id, CancellationToken cancellationToken)
            => _inner.ResultsAsync(id, cancellationToken);

        public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
            => _inner.ListAsync(cancellationToken);
    }

    // ----------------------------------------------------------------- the hand-written machine

    /// <summary>Puts one byte on the wire for a reply a real GemEquipment would never write.</summary>
    /// <remarks>
    /// <c>GemReservedAcknowledgementTests.DriveAsync</c>'s argument, applied to an item with no
    /// table: a real machine at the other end writes whatever its handler returns, and the handler
    /// returns the enum - so only a hand-written reply can carry a code this library does not
    /// name.
    /// </remarks>
    private static async Task<T> AskMachineAsync<T>(
        byte stream,
        byte primary,
        byte secondary,
        Func<SecsItem> body,
        Func<GemHost, ValueTask<T>> ask)
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
                Item = body(),
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

            return await ask(new GemHost(new GemHostOptions(), hostLink, clock))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), GemPair.Token);
        }
        finally
        {
            await cts.CancelAsync();

            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
