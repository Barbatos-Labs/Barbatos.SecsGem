// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Stream 7: how a recipe gets onto a machine, and off it again.
/// </summary>
/// <remarks>
/// The whole of stream 7 is opt-in, so the first thing worth testing is that equipment which does
/// not manage process programs does not pretend to.
/// </remarks>
public class GemProcessProgramTests
{
    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "State", () => SecsItem.U1(5));

    private static SecsItem Program(string id, params byte[] body)
        => SecsItem.L(SecsItem.A(id), SecsItem.B(body));

    // ----------------------------------------------------------------- opt-in

    [Fact(Timeout = 15_000)]
    public void Stream_seven_is_not_in_the_core_declaration()
    {
        // Equipment without a store should get S9F3 for stream 7 - "I do not do that" - rather than
        // an acknowledgement code meaning no. Keeping the two lists apart is what makes that
        // possible, so a pair leaking into the core set would be a real regression.
        foreach (var pair in GemEquipment.ProcessProgramMessages)
        {
            Assert.DoesNotContain(pair, GemEquipment.HandledMessages);
        }

        // Eleven: the five rows the reference documents, five it illustrates that the stream 7
        // sweep measured and implemented - the directory, the verify, the availability question and
        // the two program-related remote commands - and the command cancel, which the sweep refused
        // and a later re-read overturned. The first three name only items the documented five
        // already carry; the commands borrow RCMD, CPNAME and CPVAL from stream 2, where this
        // library already serves them. Either way nothing is inferred, in a stream with no gaps.
        //
        // The cancel was refused on the ground that it "needs a register of commands in flight
        // that neither this library nor the reference has" - which is what an application must
        // track, not what the row carries. S7F43 is item for item S7F37.
        Assert.Equal(11, GemEquipment.ProcessProgramMessages.Count);
        Assert.All(GemEquipment.ProcessProgramMessages, p => Assert.Equal(7, p.Stream));
        Assert.All(GemEquipment.ProcessProgramMessages, p => Assert.True(p.Function % 2 == 1));

        // The program-related commands are here and not in the core set, although the command table
        // they use is the core set's. Stream 7 is declared by equipment that manages programs: a
        // machine with no recipes should answer S9F3 for the whole stream rather than refuse one
        // function of it.
        Assert.Contains(((byte)7, (byte)37), GemEquipment.ProcessProgramMessages);
        Assert.Contains(((byte)7, (byte)41), GemEquipment.ProcessProgramMessages);
    }

    // ----------------------------------------------------------------- S7F1

    [Fact(Timeout = 15_000)]
    public async Task A_new_program_is_granted()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 1, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.U4(128u)),
        });

        Assert.Equal(2, reply.F);
        Assert.Equal((byte)GemProcessProgramGrant.Ok, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_already_stored_is_not_granted_again()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = Program("RECIPE-7", 1, 2, 3),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var reply = await link.RequestAsync(new SecsMessage(7, 1, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.U4(3u)),
        });

        // Not an error: the host is told it already sent this one, and may delete and resend.
        Assert.Equal((byte)GemProcessProgramGrant.AlreadyHave, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_too_large_for_the_store_is_refused_before_the_bytes_are_sent()
    {
        await using var link = GemLink.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore { MaxBodyLength = 16 });

        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 1, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("BIG"), SecsItem.U4(1_000_000u)),
        });

        // This is the point of S7F1 existing at all: a megabyte is refused on the strength of the
        // length the host declared, not after it has crossed the wire.
        Assert.Equal((byte)GemProcessProgramGrant.NoSpace, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_identifier_is_refused()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 1, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(string.Empty), SecsItem.U4(8u)),
        });

        Assert.Equal((byte)GemProcessProgramGrant.InvalidId, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_declared_length_is_read_in_whatever_format_the_host_chose()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 1, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.U1(8)),
        });

        Assert.Equal((byte)GemProcessProgramGrant.Ok, reply.Item!.FirstValueOrDefault<byte>());
    }

    // ----------------------------------------------------------------- S7F3 and S7F5

    [Fact(Timeout = 15_000)]
    public async Task A_program_survives_the_round_trip_byte_for_byte()
    {
        var body = new byte[512];

        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i * 7 & 0xFF);
        }

        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = Program("RECIPE-7", body),
        });

        Assert.Equal(4, sent.F);
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 5, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        Assert.Equal(6, read.F);
        Assert.Equal(2, read.Item!.Count);
        Assert.Equal("RECIPE-7", read.Item[0].GetString());
        Assert.Equal(body, read.Item[1].GetSpan<byte>().ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_nobody_sent_comes_back_as_a_zero_length_list()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 5, replyExpected: true)
        {
            Item = SecsItem.A("NOPE"),
        });

        // S7F6 has no acknowledgement code to carry a refusal, so the empty body is the refusal.
        Assert.True(reply.Item!.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_body_that_is_not_binary_is_a_length_error_rather_than_a_crash()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.A("not binary")),
        });

        // ACKC7 has no code for "that body made no sense", and a stored empty program would be
        // worse than a refusal.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.LengthError, reply.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 5, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        Assert.Equal(0, read.Item!.Count);
    }

    // ----------------------------------------------------------------- S7F17 and S7F19

    [Fact(Timeout = 15_000)]
    public async Task Deleting_a_program_removes_it_and_deleting_a_missing_one_says_so()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true) { Item = Program("A", 1) });
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var deleted = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("A")),
        });

        Assert.Equal(18, deleted.F);
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, deleted.Item!.FirstValueOrDefault<byte>());

        using var again = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("A")),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.NotFound, again.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_delete_list_means_every_program()
    {
        var store = new GemInMemoryProcessProgramStore();

        await using var link = GemLink.Start(Model, programs: store);
        await link.OpenAsync();

        foreach (var id in new[] { "A", "B", "C" })
        {
            using var sent = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true) { Item = Program(id, 1) });
            Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());
        }

        Assert.Equal(3, store.Count);

        using var deleted = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true) { Item = SecsItem.L() });

        // E5 is explicit that an empty list means all of them, and it is the one place where
        // answering the literal request would do far less than the host asked for.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, deleted.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(0, store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_current_program_list_names_what_is_stored()
    {
        await using var link = GemLink.Start(Model, programs: new GemInMemoryProcessProgramStore());
        await link.OpenAsync();

        using var empty = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        Assert.Equal(20, empty.F);
        Assert.Equal(0, empty.Item!.Count);

        foreach (var id in new[] { "RECIPE-B", "RECIPE-A" })
        {
            using var sent = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true) { Item = Program(id, 1) });
            Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());
        }

        using var listed = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        Assert.Equal(2, listed.Item!.Count);
        Assert.Equal("RECIPE-A", listed.Item[0].GetString());
        Assert.Equal("RECIPE-B", listed.Item[1].GetString());
    }

    // ----------------------------------------------------------------- the store on its own

    [Fact(Timeout = 15_000)]
    public async Task The_in_memory_store_enforces_its_capacity()
    {
        var store = new GemInMemoryProcessProgramStore { Capacity = 2 };

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("A", new byte[] { 1 }, TestContext.Current.CancellationToken));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("B", new byte[] { 2 }, TestContext.Current.CancellationToken));

        Assert.Equal(GemProcessProgramGrant.NoSpace, await store.InquireAsync("C", 1, TestContext.Current.CancellationToken));
        Assert.Equal(GemProcessProgramAcknowledgement.PermissionNotGranted, await store.SaveAsync("C", new byte[] { 3 }, TestContext.Current.CancellationToken));

        // Replacing one that is already there is not a new slot, so it still fits.
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("A", new byte[] { 9 }, TestContext.Current.CancellationToken));
        Assert.Equal(2, store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_in_memory_store_copies_what_it_is_given()
    {
        var store = new GemInMemoryProcessProgramStore();
        var body = new byte[] { 1, 2, 3 };

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("A", body, TestContext.Current.CancellationToken));

        // The caller's buffer belongs to the message it arrived in, and that message is disposed as
        // soon as the handler returns. A store that kept the reference would hand back whatever the
        // pool put there next.
        body[0] = 0xFF;

        var stored = await store.LoadAsync("A", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored!.Value.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_longer_than_E5_allows_is_refused()
    {
        var store = new GemInMemoryProcessProgramStore();
        var tooLong = new string('x', GemInMemoryProcessProgramStore.MaxIdLength + 1);

        Assert.Equal(GemProcessProgramGrant.InvalidId, await store.InquireAsync(tooLong, 1, TestContext.Current.CancellationToken));
        Assert.Equal(GemProcessProgramGrant.InvalidId, await store.InquireAsync(string.Empty, 1, TestContext.Current.CancellationToken));
    }
}
