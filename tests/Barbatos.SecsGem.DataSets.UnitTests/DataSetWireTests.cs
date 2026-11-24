// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// The shapes this package tolerates but does not write, and the ones it refuses.
/// </summary>
/// <remarks>
/// Both ends are played by hand here, because two real ends can only exercise what this library
/// itself emits. A machine that sends a directory as one flat item, or a code the table never
/// named, has to be written out.
/// </remarks>
public sealed class DataSetWireTests
{
    [Fact]
    public async Task A_grant_outside_the_table_reads_as_other()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 2, replyExpected: false)
        {
            Item = SecsItem.B(7).Named("GRANT6"),
        });

        await pair.OpenAsync();

        var opening = await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 8, DataSetPair.Token);

        // The table reserves 3 to 63, and none of them is the zero of this enum - which would turn
        // somebody else's refusal into permission to send.
        Assert.Equal(DataSetGrant.Other, opening.Grant);
        Assert.False(opening.IsGranted);
    }

    [Fact]
    public async Task An_acknowledgement_outside_the_table_reads_as_other()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 4, replyExpected: false)
        {
            Item = SecsItem.B(9).Named("ACKC13"),
        });

        await pair.OpenAsync();

        using var data = SecsItem.A("x");

        // ACKC13 declares no reserved range at all - it lists four codes and stops - so this is
        // this library covering for a table rather than quoting one.
        Assert.Equal(
            DataSetAcknowledgement.Other,
            await pair.Host.SendAsync(41, data, DataSetPair.Token));
    }

    [Fact]
    public async Task A_grant_with_a_length_beside_it_is_read_either_way()
    {
        await using var pair = DataSetPair.Start(answer: transaction => new SecsMessage(13, (byte)(transaction.F + 1), replyExpected: false)
        {
            // S13F2's row carries only the grant; a machine that sent S13F6's shape anyway is
            // still saying something this end can read.
            Item = SecsItem.L(
                SecsItem.B(0).Named("GRANT6"),
                SecsItem.U4(640).Named("LENGTH")),
        });

        await pair.OpenAsync();

        var opening = await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 8, DataSetPair.Token);

        Assert.Equal(DataSetGrant.Ok, opening.Grant);
        Assert.Equal(640u, opening.Length);
    }

    [Fact]
    public async Task A_directory_sent_as_one_flat_item_is_read()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 12, replyExpected: false)
        {
            // Nesting is the one thing a flat item list cannot pin down, so a machine carrying
            // every identifier in one U4 is reading the same row differently, not wrongly.
            Item = SecsItem.U4(3, 4, 5).Named("DSID"),
        });

        await pair.OpenAsync();

        Assert.Equal<uint>([3, 4, 5], await pair.Host.ListAsync(DataSetPair.Token));
    }

    [Fact]
    public async Task A_directory_whose_entries_are_each_wrapped_is_read()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 12, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(SecsItem.U4(11).Named("DSID")),
                SecsItem.L(SecsItem.U4(12).Named("DSID"))),
        });

        await pair.OpenAsync();

        Assert.Equal<uint>([11, 12], await pair.Host.ListAsync(DataSetPair.Token));
    }

    [Fact]
    public async Task A_directory_entry_that_is_not_a_number_is_skipped()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 12, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.U4(11).Named("DSID"),
                SecsItem.A("not an identifier"),
                SecsItem.U4(13).Named("DSID")),
        });

        await pair.OpenAsync();

        // Skipped rather than read as zero, because zero is a data set identifier.
        Assert.Equal<uint>([11, 13], await pair.Host.ListAsync(DataSetPair.Token));
    }

    [Fact]
    public async Task A_grant_reply_with_no_code_throws()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 2, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.OpenAsync(41, DataSetOpenMode.Send, 8, DataSetPair.Token).AsTask());

        Assert.Contains("GRANT6", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_reply_that_is_not_a_status_throws()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 16, replyExpected: false)
        {
            Item = SecsItem.A("no status here"),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.StatusAsync(41, DataSetPair.Token).AsTask());

        Assert.Contains("S13F16", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_set_reply_that_echoes_another_identifier_is_still_read()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 8, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.U4(999).Named("DSID"),
                SecsItem.U4(888).Named("DATAID"),
                SecsItem.A("contents of something else").Named("DSDATA")),
        });

        await pair.OpenAsync();

        var payload = await pair.Host.GetAsync(41, DataSetPair.Token);

        // Logged and not thrown, the same choice Objects makes about an S2F64 that echoes the
        // wrong DATAID: a host that wants to act on the mismatch has the log, and a host that
        // wanted the data has the data.
        Assert.NotNull(payload);

        using var item = payload.Decode();
        Assert.NotNull(item);
        Assert.Equal("contents of something else", item.GetString());

        // And the mismatch is reported, which is the only thing this rule does: filing one data
        // set's contents under another's identifier is the mistake worth warning about.
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("echoed data set 999", StringComparison.Ordinal)
                && message.Contains("asked for 41", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_status_reply_about_another_data_set_is_reported()
    {
        await using var pair = DataSetPair.Start(answer: _ => new SecsMessage(13, 16, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.U4(999).Named("DSID"),
                SecsItem.U4(64).Named("LENGTH"),
                SecsItem.B(0).Named("ACKC13")),
        });

        await pair.OpenAsync();

        var status = await pair.Host.StatusAsync(41, DataSetPair.Token);

        // The identifier that comes back is the one the equipment reported on, not the one asked
        // about - anything else would have this library relabel somebody else's answer.
        Assert.Equal(999u, status.DataSetId);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("data set 999", StringComparison.Ordinal)
                && message.Contains("about 41", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_open_declaring_no_length_still_names_the_data_set()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        // A machine that sent a bare DSID where E5 asks for a list of two. Refusing the whole open
        // would lose the identifier as well, which is the only part this end can act on.
        using var primary = new SecsMessage(13, 1, replyExpected: true)
        {
            Name = "OpenDataSetSend",
            Item = SecsItem.U4(41).Named("DSID"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, DataSetPair.Token);

        Assert.Equal(13, reply.S);
        Assert.Equal(2, reply.F);
        Assert.Equal<byte?>(0, reply.Item?.FirstValueOrDefault<byte>());
        Assert.True(pair.Memory.IsOpen(41, out var mode));
        Assert.Equal(DataSetOpenMode.Send, mode);
    }

    [Fact]
    public async Task A_send_body_of_two_items_is_refused_rather_than_guessed()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 8, DataSetPair.Token);

        // The second of two could be the correlation number or it could be the data, and DSDATA
        // has no format to tell them apart by. Guessing would store a host's correlation number as
        // somebody's data set.
        using var primary = new SecsMessage(13, 3, replyExpected: true)
        {
            Name = "DataSetSend",
            Item = SecsItem.L(
                SecsItem.U4(41).Named("DSID"),
                SecsItem.A("is this the data or the correlation number")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, DataSetPair.Token);

        Assert.Equal(4, reply.F);
        Assert.Equal((byte)DataSetAcknowledgement.InvalidData, reply.Item?.FirstValueOrDefault<byte>());
        Assert.Null(pair.Memory.Get(41));
    }

    [Fact]
    public async Task An_unreadable_open_is_refused_with_the_only_final_code_that_table_has()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(13, 1, replyExpected: true)
        {
            Name = "OpenDataSetSend",
            Item = SecsItem.A("not an identifier at all"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, DataSetPair.Token);

        // Not Busy, which would invite a retry of a message that will break again; GRANT6 has no
        // word for "that made no sense".
        Assert.Equal(2, reply.F);
        Assert.Equal((byte)DataSetGrant.NoSpace, reply.Item?.FirstValueOrDefault<byte>());
        Assert.Equal(0, pair.Memory.OpenCount);
    }

    [Fact]
    public async Task An_unreadable_status_request_is_answered_with_nothing()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(13, 15, replyExpected: true)
        {
            Name = "DataSetStatusRequest",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, DataSetPair.Token);

        // S13F16 has nowhere to put a refusal, and naming data set zero would answer a question
        // nobody asked - so the reply is empty.
        Assert.Equal(16, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task A_function_outside_the_seven_is_logged_and_left_alone()
    {
        // Only reachable if an application routes a function the package does not declare, since
        // the router answers S9F5 for anything undeclared - so the harness routes one. The
        // previous chapter had the same branch and no way to reach it; this is that way.
        await using var pair = DataSetPair.Start(alsoRoute: [(13, 9)]);
        await pair.OpenAsync();

        // No reply bit: the handler has nothing to answer S13F9 with, and E5 gives this package no
        // reply to send, so asking for one would leave the sender on a T3 that a fake clock never
        // elapses.
        using var primary = new SecsMessage(13, 9, replyExpected: false)
        {
            Name = "DataSetSendBlock",
            Item = SecsItem.L(
                SecsItem.U4(41).Named("DSID"),
                SecsItem.U4(1).Named("DATAID"),
                SecsItem.U2(1).Named("BLKNUM"),
                SecsItem.A("one block").Named("DSDATA")),
        };

        await pair.HostLink.PostAsync(primary, DataSetPair.Token);
        await DataSetPair.WaitUntilAsync(() => pair.Asked.Count > 0, "the equipment to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await DataSetPair.WaitUntilAsync(
            () => pair.Log.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        // What the branch changes is the explanation an operator reads, and nothing else: the store
        // is untouched and no reply went out.
        Assert.Contains((byte)13, pair.Asked.Select(asked => asked.Stream));
        Assert.Null(pair.Memory.Get(41));
        Assert.Contains(
            pair.Log.From(LogLevel.Warning),
            message => message.Contains("S13F9", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_from_another_stream_is_logged_and_left_alone()
    {
        // The stream guard, same shape and same reachability: routed by hand, because nothing in
        // this package declares a message outside stream 13.
        await using var pair = DataSetPair.Start(alsoRoute: [(99, 1)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(99, 1, replyExpected: false)
        {
            Name = "NothingToDoWithDataSets",
            Item = SecsItem.L(),
        };

        await pair.HostLink.PostAsync(primary, DataSetPair.Token);
        await DataSetPair.WaitUntilAsync(() => pair.Asked.Count > 0, "the equipment to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await DataSetPair.WaitUntilAsync(
            () => pair.Log.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        Assert.Contains(
            pair.Log.From(LogLevel.Warning),
            message => message.Contains("S99F1", StringComparison.Ordinal));
    }
}
