// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// The shapes this package tolerates but does not write, and the ones it refuses.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are played by hand here, because two real ends can only exercise what this library
/// itself emits - and stream 8's two undefined items mean there is more than usual that a real
/// machine might send differently.
/// </para>
/// <para>
/// Every message sent by hand goes out on <c>HostLink</c>, and that is not a detail: stream 8 has
/// no equipment-initiated primary at all, so a message put on the equipment's own link travels
/// away from the end under test, reaches a host with no routes, comes back S9F5 and leaves the
/// sender on a T3 that a fake clock never advances. The first version of this file did that six
/// times and the suite hung with no summary rather than failing.
/// </para>
/// </remarks>
public sealed class ControlProgramWireTests
{
    private static readonly byte[] Firmware = [0x7F, 0x45, 0x4C, 0x46];

    [Fact]
    public async Task A_program_goes_out_as_binary_and_not_as_text()
    {
        SecsFormat? sent = null;
        var length = 0;

        await using var pair = ControlProgramPair.Start(answer: transaction =>
        {
            if (transaction.F == 3)
            {
                sent = transaction.Message.Item?.Format;
                length = transaction.Message.Item?.Count ?? -1;
            }

            return new SecsMessage(8, (byte)(transaction.F + 1), replyExpected: false)
            {
                Item = SecsItem.B(0).Named("ACKC8"),
            };
        });

        await pair.OpenAsync();

        await pair.Host.SendAsync(Firmware, ControlProgramPair.Token);

        // The chapter's central claim, pinned on the wire rather than through this package's own
        // reader. CPBODY sits where PPBODY sits in stream 7's design and PPBODY is a B, so this
        // goes out as a B - and a round trip through the tolerant reader could not tell the
        // difference, which is how the first version of this suite let a control find nothing.
        Assert.Equal(SecsFormat.Binary, sent);
        Assert.Equal(Firmware.Length, length);
    }

    [Fact]
    public async Task An_identifier_goes_out_as_text()
    {
        SecsFormat? sent = null;

        await using var pair = ControlProgramPair.Start(answer: transaction =>
        {
            sent = transaction.Message.Item?.Format;

            return new SecsMessage(8, 16, replyExpected: false)
            {
                Item = SecsItem.L(
                    SecsItem.A("BOOT-A").Named("CPID"),
                    SecsItem.A("1.0").Named("SOFTREV")),
            };
        });

        await pair.OpenAsync();

        await pair.Host.RevisionAsync("BOOT-A", ControlProgramPair.Token);

        // And the other half of the same inference: CPID sits where PPID sits, and PPID is an
        // A[120].
        Assert.Equal(SecsFormat.ASCII, sent);
    }

    [Fact]
    public async Task A_reserved_grant_code_reads_as_other()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 2, replyExpected: false)
        {
            Item = SecsItem.B(40).Named("GRANT"),
        });

        await pair.OpenAsync();

        // The table reserves 4 to 63, and none of them is the zero of this enum - which would turn
        // somebody else's refusal into permission to send firmware.
        Assert.Equal(
            ControlProgramGrant.Other,
            await pair.Host.InquireAsync(16, ControlProgramPair.Token));
    }

    [Fact]
    public async Task An_acknowledgement_above_one_is_still_a_refusal_and_is_logged()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 4, replyExpected: false)
        {
            Item = SecsItem.B(9).Named("ACKC8"),
        });

        await pair.OpenAsync();

        // ACKC8 has no table of values at all, so there is nothing to call a 9 but a refusal. The
        // code goes to the log because that is the only place left for it.
        Assert.Equal(
            ControlProgramAcknowledgement.Denied,
            await pair.Host.SendAsync(Firmware, ControlProgramPair.Token));

        Assert.Contains(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("ACKC8 9", StringComparison.Ordinal)
                && message.Contains("no table of values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_acknowledgement_of_one_is_a_refusal_and_is_not_logged()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 4, replyExpected: false)
        {
            Item = SecsItem.B(1).Named("ACKC8"),
        });

        await pair.OpenAsync();

        // One is the refusal this package names, so there is nothing to report about it - the log
        // is for codes the item cannot account for.
        Assert.Equal(
            ControlProgramAcknowledgement.Denied,
            await pair.Host.SendAsync(Firmware, ControlProgramPair.Token));

        Assert.DoesNotContain(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("ACKC8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_program_sent_as_text_is_read_as_its_characters()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 6, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("BOOT-A").Named("CPID"),
                SecsItem.A("MZ90").Named("CPBODY")),
        });

        await pair.OpenAsync();

        var program = await pair.Host.RequestAsync("BOOT-A", ControlProgramPair.Token);

        // Writing follows PPBODY and is always a B. Reading is more generous: a machine that sends
        // firmware as text is unusual but is still saying something, and refusing it would throw
        // away a program over its envelope.
        Assert.NotNull(program);
        Assert.Equal<byte[]>("MZ90"u8.ToArray(), program.Body.ToArray());
    }

    [Fact]
    public async Task A_program_sent_in_a_format_with_no_bytes_in_it_is_refused()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.InquireAsync(4, ControlProgramPair.Token);

        using var primary = new SecsMessage(8, 3, replyExpected: true)
        {
            Name = "BootControlProgramSend",
            Item = SecsItem.F4(1.5f).Named("CPBODY"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        // A float is not a program and this end will not pretend it took one. ACKC8's one refusal
        // cannot say why, so the log does.
        Assert.Equal(4, reply.F);
        Assert.Equal<byte?>(
            (byte)ControlProgramAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.Log.From(LogLevel.Warning),
            message => message.Contains("CPBODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_send_whose_body_is_a_list_is_refused()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.InquireAsync(4, ControlProgramPair.Token);

        // S8F3's body is one item. A list is a machine sending some other message, not a program
        // wrapped for convenience - and there is no identifier beside it to suggest otherwise.
        using var primary = new SecsMessage(8, 3, replyExpected: true)
        {
            Name = "BootControlProgramSend",
            Item = SecsItem.L(SecsItem.B(Firmware).Named("CPBODY")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        Assert.Equal<byte?>(
            (byte)ControlProgramAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId));
    }

    [Fact]
    public async Task A_send_whose_body_is_a_list_is_refused_even_when_the_lengths_agree()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        // Zero declared, so the in-memory store's length check cannot tell a list from an empty
        // program - both measure nothing. That is the whole point of this test: a control that
        // made the codec take a list found nothing until the lengths were made to agree, because
        // the store's arithmetic was covering for the codec. A store without that check - and an
        // application's own need not have one - would have filed a list as an empty program.
        await pair.Host.InquireAsync(0, ControlProgramPair.Token);

        using var primary = new SecsMessage(8, 3, replyExpected: true)
        {
            Name = "BootControlProgramSend",
            Item = SecsItem.L(),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        Assert.Equal<byte?>(
            (byte)ControlProgramAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId));
    }

    [Fact]
    public async Task A_zero_length_program_is_taken_to_the_store_rather_than_refused_by_the_codec()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.InquireAsync(0, ControlProgramPair.Token);

        // A host is entitled to send nothing, and ACKC8 is the place to object to it - so the codec
        // reads it and the store decides. Here the declared length is zero too, so it is accepted.
        Assert.Equal(
            ControlProgramAcknowledgement.Accepted,
            await pair.Host.SendAsync(default, ControlProgramPair.Token));

        var held = pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId);

        Assert.NotNull(held);
        Assert.True(held.IsEmpty);
    }

    [Fact]
    public async Task A_directory_sent_as_one_bare_identifier_is_read()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 12, replyExpected: false)
        {
            // A machine with one program that did not bother with a list.
            Item = SecsItem.A("ONLY-ONE").Named("CPID"),
        });

        await pair.OpenAsync();

        Assert.Equal<string>(["ONLY-ONE"], await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task A_directory_whose_entries_are_each_wrapped_is_read()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 12, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(SecsItem.A("A").Named("CPID")),
                SecsItem.L(SecsItem.A("B").Named("CPID"))),
        });

        await pair.OpenAsync();

        Assert.Equal<string>(["A", "B"], await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task A_directory_entry_with_no_text_in_it_is_skipped()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 12, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("A").Named("CPID"),
                SecsItem.A(string.Empty).Named("CPID"),

                // Blanks and not just empty, and that is the case the length filter actually
                // catches: a zero-length item is refused a step earlier, so a control that removed
                // the filter found nothing until this entry was here. An identifier is trimmed
                // when it is read, so three spaces arrive as nothing at all.
                SecsItem.A("   ").Named("CPID"),
                SecsItem.A("B").Named("CPID")),
        });

        await pair.OpenAsync();

        // Skipped rather than kept: an empty identifier is the boot program in this package, and a
        // directory that claimed one would be saying something E5 does not.
        Assert.Equal<string>(["A", "B"], await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task A_directory_with_a_body_nothing_can_be_read_from_is_reported()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 12, replyExpected: false)
        {
            Item = SecsItem.U4(7, 8, 9),
        });

        await pair.OpenAsync();

        var listed = await pair.Host.ListAsync(ControlProgramPair.Token);

        // A U4 of three values is not three identifiers. It reads as one word - "7" - because that
        // is what the tolerant reader does with a number, and the log says the body was odd.
        Assert.Single(listed);
        Assert.Equal("7", listed[0]);
    }

    [Fact]
    public async Task A_grant_reply_with_no_code_throws()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 2, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.InquireAsync(16, ControlProgramPair.Token).AsTask());

        Assert.Contains("GRANT", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_reply_that_is_not_a_status_throws()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 16, replyExpected: false)
        {
            Item = SecsItem.A("no status here"),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.RevisionAsync("BOOT-A", ControlProgramPair.Token).AsTask());

        Assert.Contains("S8F16", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_about_another_program_is_read_and_reported()
    {
        await using var pair = ControlProgramPair.Start(answer: _ => new SecsMessage(8, 6, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("SOMETHING-ELSE").Named("CPID"),
                SecsItem.B(Firmware).Named("CPBODY")),
        });

        await pair.OpenAsync();

        var program = await pair.Host.RequestAsync("BOOT-A", ControlProgramPair.Token);

        // Logged and not thrown, the same choice Objects makes about an S2F64 that echoes the wrong
        // DATAID - but the mistake here is worse than most, which is why it is worth a line: it
        // would flash one program under another's name.
        Assert.NotNull(program);
        Assert.Equal("SOMETHING-ELSE", program.Id);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("SOMETHING-ELSE", StringComparison.Ordinal)
                && message.Contains("BOOT-A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_inquire_is_refused_with_the_only_code_that_is_not_about_space()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(8, 1, replyExpected: true)
        {
            Name = "BootControlProgramLoadInquire",
            Item = SecsItem.A("not a length"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        // GRANT 3 is "duplicate identifier / invalid", and the second half of that is the nearest
        // the table comes to "that made no sense". Not Busy, which would invite a retry of a
        // message that will break again, and not NoSpace, which would be a lie about the machine.
        Assert.Equal(2, reply.F);
        Assert.Equal<byte?>(
            (byte)ControlProgramGrant.DuplicateOrInvalid,
            reply.Item?.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_unreadable_request_is_answered_with_nothing()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(8, 5, replyExpected: true)
        {
            Name = "ControlProgramRequest",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        // S8F6 has nowhere to put a refusal, and naming a program this end could not read would
        // answer a question nobody asked.
        Assert.Equal(6, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task An_unreadable_abort_is_refused()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(8, 13, replyExpected: true)
        {
            Name = "ControlProgramTransferAbort",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        Assert.Equal(14, reply.F);
        Assert.Equal<byte?>(
            (byte)ControlProgramAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_identifier_sent_as_a_number_is_read_as_its_digits()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("7", Firmware, "1.0");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        // CPID is read off PPID, which is an A - so a machine sending a number is tolerated by
        // rendering it, which is what every text field in this stack does. Unlike stream 18's
        // SSID, there is no second reading to protect: PPID settles the format, so a number here
        // is a machine being loose rather than a machine meaning something else.
        using var primary = new SecsMessage(8, 15, replyExpected: true)
        {
            Name = "ControlProgramStatusRequest",
            Item = SecsItem.U4(7).Named("CPID"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ControlProgramPair.Token);

        Assert.Equal(16, reply.F);
        Assert.Equal("1.0", reply.Item?.Items[1].GetString());
    }

    [Fact]
    public async Task A_function_outside_the_six_is_logged_and_left_alone()
    {
        // Only reachable if an application routes a function the package does not declare, since
        // the router answers S9F5 for anything undeclared - so the harness routes one. S8F7 is the
        // blocked send, which is exactly the thing this package leaves out.
        await using var pair = ControlProgramPair.Start(alsoRoute: [(8, 7)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(8, 7, replyExpected: false)
        {
            Name = "ControlProgramSendBlock",
            Item = SecsItem.L(
                SecsItem.A("BOOT-A").Named("CPID"),
                SecsItem.U2(1).Named("BLKNUM"),
                SecsItem.B(Firmware).Named("CPBODY")),
        };

        await pair.HostLink.PostAsync(primary, ControlProgramPair.Token);
        await ControlProgramPair.WaitUntilAsync(() => pair.Asked.Count > 0, "the equipment to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await ControlProgramPair.WaitUntilAsync(
            () => pair.Log.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        Assert.Contains(
            pair.Log.From(LogLevel.Warning),
            message => message.Contains("S8F7", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));

        Assert.Null(pair.Memory.Find("BOOT-A"));
    }

    [Fact]
    public async Task A_message_from_another_stream_is_logged_and_left_alone()
    {
        await using var pair = ControlProgramPair.Start(alsoRoute: [(99, 1)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(99, 1, replyExpected: false)
        {
            Name = "NothingToDoWithControlPrograms",
            Item = SecsItem.L(),
        };

        await pair.HostLink.PostAsync(primary, ControlProgramPair.Token);
        await ControlProgramPair.WaitUntilAsync(() => pair.Asked.Count > 0, "the equipment to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await ControlProgramPair.WaitUntilAsync(
            () => pair.Log.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        Assert.Contains(
            pair.Log.From(LogLevel.Warning),
            message => message.Contains("S99F1", StringComparison.Ordinal));
    }
}
