// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// The shapes this package tolerates but does not write, and the ones it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The right end is played by hand where a reply has to be wrong, because two real peers can only
/// exercise what this library itself emits - and stream 21's five undefined names mean there is
/// more than usual that a real machine might send differently.
/// </para>
/// <para>
/// Every message sent by hand goes out on <c>LeftLink</c>, which is the link of the end that
/// initiates it: a message put on the other end's link travels away from the end under test,
/// reaches a peer with no route for it, comes back S9F5 and leaves the sender on a T3 that a fake
/// clock never advances. That mistake hangs a suite rather than failing it, and it has cost this
/// repository two chapters already.
/// </para>
/// </remarks>
public sealed class ItemTransferWireTests
{
    [Fact]
    public async Task A_reserved_grant_code_reads_as_other()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 6, replyExpected: false)
        {
            Item = SecsItem.B(40).Named("GRANT"),
        });

        await pair.OpenAsync();

        var (grant, _) = await pair.Left.RequestAsync("ITEM", 4, ItemPair.Token);

        // The table reserves 4 to 63, and none of them is the zero of this enum - which would turn
        // somebody else's refusal into permission to send.
        Assert.Equal(ItemTransferGrant.Other, grant);
    }

    [Fact]
    public async Task An_acknowledgement_above_one_is_still_a_refusal_and_is_logged()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 18, replyExpected: false)
        {
            Item = SecsItem.B(9).Named("DELACK"),
        });

        await pair.OpenAsync();

        // DELACK has no table of values at all, so there is nothing to call a 9 but a refusal, and
        // the code goes to the log because that is the only place left for it.
        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.DeleteAsync("ITEM", ItemPair.Token));

        Assert.Contains(
            pair.LeftLog.From(LogLevel.Information),
            message => message.Contains("code 9", StringComparison.Ordinal)
                && message.Contains("no table of values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_acknowledgement_of_one_is_a_refusal_and_is_not_logged()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 16, replyExpected: false)
        {
            Item = SecsItem.B(1).Named("ABTACK"),
        });

        await pair.OpenAsync();

        // One is the refusal this package names, so there is nothing to report about it: the log is
        // for codes the item cannot account for.
        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.AbortAsync("ITEM", 1, ItemPair.Token));

        Assert.DoesNotContain(
            pair.LeftLog.From(LogLevel.Information),
            message => message.Contains("no table of values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_directory_whose_entries_are_laid_out_flat_is_read()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 2, replyExpected: false)
        {
            // Nesting is the one thing a flat item list cannot pin down, so a machine that laid
            // three fields per item end to end is reading the same row differently.
            Item = SecsItem.L(
                SecsItem.A("A").Named("ITEMID"),
                SecsItem.A("log").Named("ITEMTYPE"),
                SecsItem.U4(1).Named("LENGTH"),
                SecsItem.A("B").Named("ITEMID"),
                SecsItem.A("recipe").Named("ITEMTYPE"),
                SecsItem.U4(2).Named("LENGTH")),
        });

        await pair.OpenAsync();

        var items = await pair.Left.ListAsync(cancellationToken: ItemPair.Token);

        Assert.Equal(2, items.Count);
        Assert.Equal("A", items[0].Id);
        Assert.Equal("recipe", items[1].Type);
        Assert.Equal(2u, items[1].Length);
    }

    [Fact]
    public async Task A_flat_directory_whose_last_entry_is_short_drops_it()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("A").Named("ITEMID"),
                SecsItem.A("log").Named("ITEMTYPE"),
                SecsItem.U4(1).Named("LENGTH"),
                SecsItem.A("B").Named("ITEMID")),
        });

        await pair.OpenAsync();

        // Four fields is one entry and a fragment. Dropped for the reason the nested shape's short
        // entry is - a row with no length is not a directory row - and worth its own test because
        // the flat reader walks in strides and the nested one walks per entry, so the rule is
        // written twice and could be got right in only one of them.
        Assert.Equal("A", Assert.Single(await pair.Left.ListAsync(cancellationToken: ItemPair.Token)).Id);
    }

    [Fact]
    public async Task A_directory_entry_short_of_its_three_fields_is_dropped()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.A("A").Named("ITEMID"),
                    SecsItem.A("log").Named("ITEMTYPE"),
                    SecsItem.U4(1).Named("LENGTH")),
                SecsItem.L(SecsItem.A("B").Named("ITEMID"))),
        });

        await pair.OpenAsync();

        // Dropped rather than padded: an item with no length is not a directory row, and inventing
        // a zero would tell the asking end the item is empty.
        Assert.Equal("A", Assert.Single(await pair.Left.ListAsync(cancellationToken: ItemPair.Token)).Id);
    }

    [Fact]
    public async Task A_directory_entry_with_no_identifier_is_dropped()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.A(string.Empty).Named("ITEMID"),
                    SecsItem.A("log").Named("ITEMTYPE"),
                    SecsItem.U4(1).Named("LENGTH")),
                SecsItem.L(
                    SecsItem.A("   ").Named("ITEMID"),
                    SecsItem.A("log").Named("ITEMTYPE"),
                    SecsItem.U4(1).Named("LENGTH")),
                SecsItem.L(
                    SecsItem.A("B").Named("ITEMID"),
                    SecsItem.A("log").Named("ITEMTYPE"),
                    SecsItem.U4(2).Named("LENGTH"))),
        });

        await pair.OpenAsync();

        // Blanks as well as empty, and that is the case the length check actually catches: a
        // zero-length item reads as nothing a step earlier, while three spaces survive to be
        // trimmed. An item nobody can name is not an item.
        Assert.Equal("B", Assert.Single(await pair.Left.ListAsync(cancellationToken: ItemPair.Token)).Id);
    }

    [Fact]
    public async Task A_directory_with_a_body_nothing_can_be_read_from_is_reported()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 2, replyExpected: false)
        {
            Item = SecsItem.A("not a directory at all"),
        });

        await pair.OpenAsync();

        Assert.Empty(await pair.Left.ListAsync(cancellationToken: ItemPair.Token));
        Assert.Contains(
            pair.LeftLog.From(LogLevel.Warning),
            message => message.Contains("S21F2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attribute_list_that_is_not_one_throws()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 4, replyExpected: false)
        {
            Item = SecsItem.A("no attributes here"),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Left.AttributesAsync(
                new ItemAttributeRequest { ItemId = "ITEM" },
                attributes => attributes.Count,
                ItemPair.Token).AsTask());

        Assert.Contains("S21F4", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attribute_reply_about_another_item_is_read_and_reported()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 4, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("SOMETHING-ELSE").Named("ITEMID"),
                SecsItem.L(SecsItem.L(
                    SecsItem.A("Temperature").Named("ATTRID"),
                    SecsItem.F8(1.5d).Named("ATTRDATA")))),
        });

        await pair.OpenAsync();

        var names = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "ITEM" },
            attributes => attributes.Select(attribute => attribute.Name).ToArray(),
            ItemPair.Token);

        // Logged and not thrown, the same choice Objects makes about an S2F64 that echoes the
        // wrong DATAID: a caller that wants to act on the mismatch has the log, and one that
        // wanted the attributes has them.
        Assert.Equal<string[]>(["Temperature"], names);
        Assert.Contains(
            pair.LeftLog.From(LogLevel.Warning),
            message => message.Contains("SOMETHING-ELSE", StringComparison.Ordinal)
                && message.Contains("ITEM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attribute_with_no_name_is_dropped()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 4, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("ITEM").Named("ITEMID"),
                SecsItem.L(
                    SecsItem.L(
                        SecsItem.A(string.Empty).Named("ATTRID"),
                        SecsItem.F8(1.5d).Named("ATTRDATA")),
                    SecsItem.L(
                        SecsItem.A("Operator").Named("ATTRID"),
                        SecsItem.A("A. Operator").Named("ATTRDATA")))),
        });

        await pair.OpenAsync();

        // An attribute nobody can name is a value with nothing to file it under.
        Assert.Equal<string[]>(
            ["Operator"],
            await pair.Left.AttributesAsync(
                new ItemAttributeRequest { ItemId = "ITEM" },
                attributes => attributes.Select(attribute => attribute.Name).ToArray(),
                ItemPair.Token));
    }

    [Fact]
    public async Task A_grant_reply_with_no_code_throws()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 6, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Left.RequestAsync("ITEM", 4, ItemPair.Token).AsTask());

        Assert.Contains("GRANT", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_request_with_no_body_at_all_asks_for_everything()
    {
        await using var pair = ItemPair.Start();
        pair.RightStore.Add(new Item { Id = "A", Type = "log" });
        pair.RightStore.Add(new Item { Id = "B", Type = "recipe" });

        await pair.OpenAsync();

        // A machine that asked for a directory with a zero-length list rather than an empty
        // ITEMTYPE is asking the same question, and this end answers it rather than refusing over
        // the envelope.
        using var primary = new SecsMessage(21, 1, replyExpected: true)
        {
            Name = "ItemDirectoryRequest",
            Item = SecsItem.L(),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal(2, reply.F);
        Assert.Equal(2, reply.Item?.Count);
    }

    [Fact]
    public async Task An_unreadable_directory_request_is_answered_with_nothing()
    {
        await using var pair = ItemPair.Start();
        pair.RightStore.Add(new Item { Id = "A", Type = "log" });

        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 1, replyExpected: true)
        {
            Name = "ItemDirectoryRequest",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        // S21F2 has nowhere to put a refusal, and answering with the whole directory would be
        // answering a question this end could not read.
        Assert.Equal(2, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task An_unreadable_attribute_request_is_answered_with_nothing()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 3, replyExpected: true)
        {
            Name = "ItemAttributesRequest",
            Item = SecsItem.L(SecsItem.A(string.Empty).Named("ITEMID")),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal(4, reply.F);
        Assert.Equal(0, reply.Item?.Count);
    }

    [Fact]
    public async Task An_unreadable_transfer_request_is_refused_with_the_only_code_that_is_not_about_space()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 5, replyExpected: true)
        {
            Name = "ItemTransferRequest",
            Item = SecsItem.A("not a transfer request"),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        // GRANT 3 is "duplicate identifier / invalid", and the second half of that is the nearest
        // the table comes to "that made no sense". Not Busy, which would invite a retry of a
        // message that will break again, and not NoSpace, which would be a lie about the machine.
        Assert.Equal(6, reply.F);
        Assert.Equal<byte?>(
            (byte)ItemTransferGrant.DuplicateOrInvalid,
            reply.Item?.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_data_send_of_two_items_is_refused_rather_than_guessed()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        var (_, dataId) = await pair.Left.RequestAsync("ITEM", 4, ItemPair.Token);

        // The second of two could be the correlation number or it could be the data, and ITEMDATA
        // has no format to tell them apart by. Guessing would store a correlation number as
        // somebody's item.
        using var primary = new SecsMessage(21, 7, replyExpected: true)
        {
            Name = "ItemDataSend",
            Item = SecsItem.L(
                SecsItem.A("ITEM").Named("ITEMID"),
                SecsItem.U4(dataId).Named("DATAID")),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal(8, reply.F);
        Assert.Equal<byte?>(
            (byte)ItemAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.RightStore.Contents("ITEM"));
    }

    [Fact]
    public async Task An_abort_sent_as_a_bare_identifier_is_read()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        // Granted through the store rather than over the link, and that is the point: a bare
        // identifier reads as a correlation number of zero, while this library's own numbering
        // starts at one - so the only transfer a bare abort can match is one granted with a zero,
        // and a test that went over the link could not tell the body being read from the store
        // refusing it. Both answer ABTACK 1.
        await pair.RightStore.RequestAsync(
            new ItemTransferRequest { ItemId = "ITEM", DataId = 0 },
            ItemPair.Token);

        using var primary = new SecsMessage(21, 15, replyExpected: true)
        {
            Name = "TransferAbortRequest",
            Item = SecsItem.A("ITEM").Named("ITEMID"),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal(16, reply.F);
        Assert.Equal<byte?>(
            (byte)ItemAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Equal(0, pair.RightStore.GrantedCount);
    }

    [Fact]
    public async Task A_transfer_request_short_of_its_length_is_granted_with_a_length_of_zero()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 5, replyExpected: false)
        {
            Name = "ItemTransferRequest",
            Item = SecsItem.L(
                SecsItem.A("ITEM").Named("ITEMID"),
                SecsItem.U4(9).Named("DATAID")),
        };

        // Posted, not sent, and that is deliberate. A reader that indexed the third field without
        // checking for it would throw inside the handler, which answers nothing at all - and a
        // test waiting for S21F6 would then sit out a T3 that a fake clock never advances, so the
        // suite would hang instead of failing. Measured: it takes the run from half a second to
        // over two minutes with no summary. Watching the store instead fails in seconds and names
        // the test.
        await pair.LeftLink.PostAsync(primary, ItemPair.Token);
        await ItemPair.WaitUntilAsync(
            () => pair.RightStore.GrantedCount == 1,
            "the transfer to be granted");

        // Read rather than refused, which is the opposite of the two-item data send above - and
        // the difference is what the missing field would be mistaken for. Here it is LENGTH, a
        // sender's estimate at the end of a fixed three; there it is the payload, which could be
        // confused with the correlation number because ITEMDATA has no format of its own.
        Assert.Equal(0, pair.RightStore.Count);
    }

    [Fact]
    public async Task An_item_sent_with_no_reply_bit_is_still_stored()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        var (_, dataId) = await pair.Left.RequestAsync("ITEM", 1, ItemPair.Token);

        using var primary = new SecsMessage(21, 7, replyExpected: false)
        {
            Name = "ItemDataSend",
            Item = SecsItem.L(
                SecsItem.A("ITEM").Named("ITEMID"),
                SecsItem.U4(dataId).Named("DATAID"),
                SecsItem.A("the item itself").Named("ITEMDATA")),
        };

        await pair.LeftLink.PostAsync(primary, ItemPair.Token);
        await ItemPair.WaitUntilAsync(
            () => pair.RightStore.Find("ITEM") is not null,
            "the item to be stored");

        // The reply bit says whether the sender wants an answer, not whether the work should
        // happen. A handler that read it first would drop an item a machine had already sent.
        Assert.False(pair.RightStore.Contents("ITEM")!.IsEmpty);
    }

    [Fact]
    public async Task An_unreadable_delete_is_refused_and_reported()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 17, replyExpected: true)
        {
            Name = "ItemDeleteRequest",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal(18, reply.F);
        Assert.Equal<byte?>(
            (byte)ItemAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        // The log is asserted, not just the code, and it is the only way to assert this at all:
        // DELACK has two values here, so "I could not read that" and "there is no such item" come
        // back as the same byte. A machine that read the first element of a two-element list and
        // deleted it would answer identically too - that is the mistake the warning catches.
        Assert.Contains(
            pair.RightLog.From(LogLevel.Warning),
            message => message.Contains("S21F17", StringComparison.Ordinal)
                && message.Contains("could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delete_whose_identifier_is_empty_is_refused_and_reported()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 17, replyExpected: true)
        {
            Name = "ItemDeleteRequest",
            Item = SecsItem.A(string.Empty).Named("ITEMID"),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal<byte?>(
            (byte)ItemAcknowledgement.Denied,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.RightLog.From(LogLevel.Warning),
            message => message.Contains("S21F17", StringComparison.Ordinal)
                && message.Contains("could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_acknowledgement_item_with_no_value_in_it_throws()
    {
        await using var pair = ItemPair.Start(answerRight: _ => new SecsMessage(21, 18, replyExpected: false)
        {
            Item = SecsItem.B().Named("DELACK"),
        });

        await pair.OpenAsync();

        // Zero is acceptance in all three of this stream's acknowledgement items, and that reading
        // comes off forty-one of the reference's forty-two tabled siblings rather than off a table
        // of their own - so an item with no value in it must not fall through to a zero. A machine
        // that sent B,0 said nothing, and nothing is not yes.
        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Left.DeleteAsync("ITEM", ItemPair.Token).AsTask());

        Assert.Contains("S21F18", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identifier_sent_as_a_number_is_read_as_its_digits()
    {
        await using var pair = ItemPair.Start();
        pair.RightStore.Add(new Item { Id = "7", Type = "log" });

        await pair.OpenAsync();

        // ITEMID is defined as an A, so a machine sending a number is being loose rather than
        // meaning something else - unlike stream 18's SSID, where the format was the identity
        // because nothing defined it.
        using var primary = new SecsMessage(21, 17, replyExpected: true)
        {
            Name = "ItemDeleteRequest",
            Item = SecsItem.U4(7).Named("ITEMID"),
        };

        using var reply = await pair.LeftLink.SendAsync(primary, ItemPair.Token);

        Assert.Equal<byte?>(
            (byte)ItemAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.RightStore.Find("7"));
    }

    [Fact]
    public async Task A_function_outside_the_six_is_logged_and_left_alone()
    {
        // Only reachable if an application routes a function the package does not declare, since
        // the router answers S9F5 for anything undeclared - so the harness routes one. S21F11 is
        // the blocked send, which is exactly what this package leaves out.
        await using var pair = ItemPair.Start(alsoRouteRight: [(21, 11)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(21, 11, replyExpected: false)
        {
            Name = "ItemBlockSend",
            Item = SecsItem.L(
                SecsItem.A("ITEM").Named("ITEMID"),
                SecsItem.U2(1).Named("BLKNUM"),
                SecsItem.Boolean(false).Named("BLKEND"),
                SecsItem.A("one block").Named("ITEMDATA")),
        };

        await pair.LeftLink.PostAsync(primary, ItemPair.Token);
        await ItemPair.WaitUntilAsync(() => pair.AskedRight.Count > 0, "the right end to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await ItemPair.WaitUntilAsync(
            () => pair.RightLog.From(LogLevel.Warning).Count > 0,
            "the right end to say why");

        Assert.Null(pair.RightStore.Find("ITEM"));

        // The log is the whole effect, so it is the whole assertion: nothing else distinguishes a
        // function this end does not implement from one it implements and had nothing to do about.
        Assert.Contains(
            pair.RightLog.From(LogLevel.Warning),
            message => message.Contains("S21F11", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_from_another_stream_is_logged_and_left_alone()
    {
        await using var pair = ItemPair.Start(alsoRouteRight: [(99, 1)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(99, 1, replyExpected: false)
        {
            Name = "NothingToDoWithItems",
            Item = SecsItem.L(),
        };

        await pair.LeftLink.PostAsync(primary, ItemPair.Token);
        await ItemPair.WaitUntilAsync(() => pair.AskedRight.Count > 0, "the right end to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await ItemPair.WaitUntilAsync(
            () => pair.RightLog.From(LogLevel.Warning).Count > 0,
            "the right end to say why");

        Assert.Equal(0, pair.RightStore.Count);

        // Without the stream being checked, function 1 of any stream would be read as a directory
        // request and answered as S21F2 - so this assertion is what holds the check in place.
        Assert.Contains(
            pair.RightLog.From(LogLevel.Warning),
            message => message.Contains("S99F1", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }
}
