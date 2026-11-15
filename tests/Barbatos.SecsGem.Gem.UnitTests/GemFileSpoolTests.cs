// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// A spool that outlives the process that filled it.
/// </summary>
/// <remarks>
/// <para>
/// The interesting tests here all involve a second spool over the same directory, because that is
/// the only thing a durable spool does that an in-memory one does not. Everything else - capacity,
/// the overflow policies, the order, peek and remove being separate - is behaviour
/// <see cref="GemInMemorySpool"/> already has, and is tested again only where the disk could break
/// it.
/// </para>
/// <para>
/// These touch a real filesystem on purpose. A fake one would test the fake's idea of what
/// <c>File.Move</c> and <c>FileShare.None</c> do, and those are exactly the two things the design
/// leans on.
/// </para>
/// </remarks>
public class GemFileSpoolTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(7));

    private static void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "Pressure", () => SecsItem.F8(1_013.25d), "mbar")
            .AddEvent(1_000, "ProcessEnd")
            .AddReport(5_000, 1);

    private static GemSpooledMessage Held(
        byte stream,
        byte function,
        DateTimeOffset at,
        SecsItem? item = null,
        string? name = null)
    {
        using var message = new SecsMessage(stream, function, replyExpected: false)
        {
            Name = name,
            Item = item,
        };

        return GemSpooledMessage.From(message, at);
    }

    private static string RecordPath(string directory, long sequence) => Path.Combine(
        directory,
        sequence.ToString("D19", CultureInfo.InvariantCulture) + GemFileSpool.RecordExtension);

    // ----------------------------------------------------------------- one message, one file

    [Fact]
    public async Task A_message_comes_back_exactly_as_it_went_in()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path);

        Assert.True(await spool.TryAppendAsync(
            Held(6, 11, Noon, SecsItem.L(SecsItem.A("LOT-1"), SecsItem.F8(1_013.25d)), "EventReportSend"),
            token));

        var held = await spool.TryPeekAsync(token);

        Assert.NotNull(held);
        Assert.Equal(6, held.Stream);
        Assert.Equal(11, held.Function);
        Assert.False(held.ReplyExpected);
        Assert.Equal("EventReportSend", held.Name);

        // The offset, not just the instant. A host reads SpoolStartTime to work out how much
        // history it is being offered, and a spool that quietly moved everything to UTC would
        // answer that question in a different zone from the one the equipment reports in.
        Assert.Equal(Noon, held.SpooledAt);
        Assert.Equal(TimeSpan.FromHours(7), held.SpooledAt.Offset);

        using var message = held.ToMessage();

        Assert.Equal("LOT-1", message.Item![0].GetString());
        Assert.Equal(1_013.25d, message.Item[1].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_message_with_no_body_and_a_message_with_no_name_both_survive()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path);

        Assert.True(await spool.TryAppendAsync(Held(1, 1, Noon), token));

        var held = await spool.TryPeekAsync(token);

        Assert.NotNull(held);
        Assert.True(held.Body.IsEmpty);

        // Null, not empty: the two are stored differently, because a message that had no name and
        // one named "" are not the same message coming back.
        Assert.Null(held.Name);

        Assert.True(await spool.RemoveHeadAsync(token));
        Assert.True(await spool.TryAppendAsync(Held(1, 1, Noon, name: string.Empty), token));

        var named = await spool.TryPeekAsync(token);

        Assert.NotNull(named);
        Assert.Equal(string.Empty, named.Name);
    }

    [Fact]
    public async Task A_body_too_big_for_the_small_object_heap_survives()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var payload = new byte[100_000];

        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 & 0xFF);
        }

        var spool = new GemFileSpool(temporary.Path);

        Assert.True(await spool.TryAppendAsync(Held(6, 11, Noon, SecsItem.B(payload)), token));

        var held = await spool.TryPeekAsync(token);

        Assert.NotNull(held);

        using var message = held.ToMessage();

        // Byte for byte. The write goes straight from the message's own memory rather than through
        // a buffer sized to hold the whole record, which is the only part of the write path a
        // smaller body never exercises.
        Assert.Equal(payload, message.Item!.GetSpan<byte>().ToArray());
    }

    [Fact]
    public async Task A_name_longer_than_the_format_allows_is_refused_where_it_is_given()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path);
        var message = Held(6, 11, Noon, name: new string('x', 0xFFFF));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await spool.TryAppendAsync(message, token));

        // And nothing was written. A refusal that left a file behind would be adopted as a record
        // by the next spool over this directory.
        Assert.Equal(0, await spool.CountAsync(token));
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    // ----------------------------------------------------------------- the restart

    [Fact]
    public async Task A_new_spool_over_the_same_directory_finds_what_the_last_one_held()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var before = new GemFileSpool(temporary.Path);

        foreach (var lot in new[] { "LOT-1", "LOT-2", "LOT-3" })
        {
            Assert.True(await before.TryAppendAsync(Held(6, 11, Noon, SecsItem.A(lot)), token));
        }

        Assert.Equal(3, await before.CountAsync(token));

        // The process goes away. Nothing is handed over, nothing is closed: this is an upgrade, a
        // crash or a power cut, and the only thing the next spool has is the directory.
        var after = new GemFileSpool(temporary.Path);

        Assert.Equal(3, await after.CountAsync(token));

        foreach (var lot in new[] { "LOT-1", "LOT-2", "LOT-3" })
        {
            var held = await after.TryPeekAsync(token);

            Assert.NotNull(held);

            using var message = held.ToMessage();

            // In order, which is the whole reason the records are named with a sequence rather than
            // a timestamp: three reports raised inside the same millisecond still have an order,
            // and a host replaying a spool is reading a sequence of events.
            Assert.Equal(lot, message.Item!.GetString());
            Assert.True(await after.RemoveHeadAsync(token));
        }

        Assert.Equal(0, await after.CountAsync(token));
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public async Task A_spool_that_reopens_does_not_reuse_a_sequence_number()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var before = new GemFileSpool(temporary.Path);

        Assert.True(await before.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("FIRST")), token));
        Assert.True(await before.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("SECOND")), token));
        Assert.True(await before.RemoveHeadAsync(token));

        var after = new GemFileSpool(temporary.Path);

        Assert.True(await after.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("THIRD")), token));

        // SECOND then THIRD. A spool that restarted its numbering at zero would file THIRD in front
        // of the message that was already there, and hand a host its history backwards.
        foreach (var expected in new[] { "SECOND", "THIRD" })
        {
            var held = await after.TryPeekAsync(token);

            Assert.NotNull(held);

            using var message = held.ToMessage();

            Assert.Equal(expected, message.Item!.GetString());
            Assert.True(await after.RemoveHeadAsync(token));
        }
    }

    [Fact]
    public async Task An_unfinished_write_is_not_a_record()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // What a power cut between the write and the rename leaves behind. TryAppendAsync never
        // returned, so nothing was ever told the message was held - which makes this litter rather
        // than data, and deleting it the honest thing to do.
        var partial = Path.Combine(
            temporary.Path,
            1.ToString("D19", CultureInfo.InvariantCulture) + GemFileSpool.PartialExtension);

        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2, 3 }, token);

        var spool = new GemFileSpool(temporary.Path);

        Assert.Equal(0, await spool.CountAsync(token));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task A_file_this_spool_did_not_write_is_left_where_it_is()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var stranger = Path.Combine(temporary.Path, "notes" + GemFileSpool.RecordExtension);

        await File.WriteAllTextAsync(stranger, "something an operator left here", token);

        var spool = new GemFileSpool(temporary.Path);

        // Not a sequence number, so not a record. Deleting a file this spool cannot account for
        // would make it the wrong kind of thing to point at a directory.
        Assert.Equal(0, await spool.CountAsync(token));
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public async Task A_record_that_cannot_be_read_is_dropped_rather_than_retried()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await File.WriteAllBytesAsync(RecordPath(temporary.Path, 1), new byte[40], token);

        var good = new GemFileSpool(temporary.Path);

        Assert.True(await good.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("REAL")), token));
        Assert.Equal(2, await good.CountAsync(token));

        var held = await good.TryPeekAsync(token);

        // The forty zero bytes are not a record, and a spool that kept trying them would never
        // deliver the message behind them: every S6F23 would stop on the same file.
        Assert.NotNull(held);

        using var message = held.ToMessage();

        Assert.Equal("REAL", message.Item!.GetString());
        Assert.Equal(1, good.Unreadable);
        Assert.Equal(1, await good.CountAsync(token));
        Assert.False(File.Exists(RecordPath(temporary.Path, 1)));
    }

    // ----------------------------------------------------------------- the bounds still hold

    [Theory]
    [InlineData(GemSpoolOverflowPolicy.DiscardNewest, "LOT-1")]
    [InlineData(GemSpoolOverflowPolicy.DiscardOldest, "LOT-2")]
    public async Task A_full_spool_keeps_what_its_policy_says_and_the_directory_agrees(
        GemSpoolOverflowPolicy overflow,
        string expected)
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path, capacity: 2, overflow: overflow);

        foreach (var lot in new[] { "LOT-1", "LOT-2" })
        {
            Assert.True(await spool.TryAppendAsync(Held(6, 11, Noon, SecsItem.A(lot)), token));
        }

        var accepted = await spool.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("LOT-3")), token);

        Assert.Equal(overflow == GemSpoolOverflowPolicy.DiscardOldest, accepted);
        Assert.Equal(1, spool.Discarded);

        // The count and the directory, because they are two different claims: one is an index in
        // memory and the other is what survives a restart, and a policy that dropped a message
        // from the first but not the second would resurrect it.
        Assert.Equal(2, await spool.CountAsync(token));
        Assert.Equal(2, Directory.GetFiles(temporary.Path, "*" + GemFileSpool.RecordExtension).Length);

        var held = await spool.TryPeekAsync(token);

        Assert.NotNull(held);

        using var message = held.ToMessage();

        Assert.Equal(expected, message.Item!.GetString());
    }

    [Fact]
    public async Task Purging_leaves_the_directory_empty()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path);

        foreach (var lot in new[] { "LOT-1", "LOT-2", "LOT-3" })
        {
            Assert.True(await spool.TryAppendAsync(Held(6, 11, Noon, SecsItem.A(lot)), token));
        }

        await spool.PurgeAsync(token);

        Assert.Equal(0, await spool.CountAsync(token));
        Assert.Null(await spool.TryPeekAsync(token));

        // A purge that only cleared the index would hand the host everything it had just thrown
        // away, the next time the equipment started.
        Assert.Empty(Directory.GetFiles(temporary.Path, "*" + GemFileSpool.RecordExtension));
        Assert.Equal(0, await new GemFileSpool(temporary.Path).CountAsync(token));
    }

    [Fact]
    public void A_capacity_of_nothing_is_refused_and_the_directory_is_created()
    {
        using var temporary = new TempDirectory();

        var nested = Path.Combine(temporary.Path, "spool", "records");

        Assert.Throws<ArgumentOutOfRangeException>(() => new GemFileSpool(nested, capacity: 0));
        Assert.Throws<ArgumentNullException>(() => new GemFileSpool(null!));

        var spool = new GemFileSpool(nested);

        // Created, and reported as a full path: a relative path that worked at start-up and not
        // after a working-directory change is the kind of thing that only shows up in a fab.
        Assert.True(Directory.Exists(nested));
        Assert.Equal(Path.GetFullPath(nested), spool.Directory);
    }

    // ----------------------------------------------------------------- what the equipment does with it

    [Fact]
    public async Task The_equipment_adopts_a_spool_that_outlived_it()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var raised = Noon;

        // Two reports held by the equipment that was here before this one.
        var before = new GemFileSpool(temporary.Path);

        Assert.True(await before.TryAppendAsync(Held(6, 11, raised, SecsItem.A("LOT-1")), token));
        Assert.True(await before.TryAppendAsync(Held(6, 11, raised.AddMinutes(1), SecsItem.A("LOT-2")), token));

        await using var link = GemLink.Start(Model, spool: new GemFileSpool(temporary.Path));

        await GemLink.WaitUntilAsync(
            () => link.Equipment.SpoolCountActual == 2,
            "the equipment to adopt the spool it was given");

        // Active, not Inactive. S6F23 Transmit is refused when the equipment thinks nothing is
        // held, so without this the records would sit on the disk with no way to ask for them.
        Assert.Equal(GemSpoolState.Active, link.Equipment.SpoolState);

        // And the episode started when the oldest message was held, not when this process did.
        // E30 has a host read SpoolStartTime to work out how much history it is being offered.
        Assert.Equal(raised, link.Equipment.SpoolStartTime);

        await link.OpenAsync();

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        foreach (var lot in new[] { "LOT-1", "LOT-2" })
        {
            using var report = await link.ReceiveAsync();

            Assert.Equal(6, report.S);
            Assert.Equal(11, report.F);
            Assert.Equal(lot, report.Item!.GetString());
        }

        await GemLink.WaitUntilAsync(
            () => link.Equipment.SpoolCountActual == 0,
            "the spool to be handed over");

        Assert.Equal(GemSpoolState.Inactive, link.Equipment.SpoolState);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*" + GemFileSpool.RecordExtension));
    }

    [Fact]
    public async Task A_held_message_whose_body_is_not_an_item_tree_does_not_block_the_one_behind_it()
    {
        using var temporary = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        var spool = new GemFileSpool(temporary.Path);

        // A record this spool reads back perfectly well and cannot send: the header is right, the
        // lengths are right, and the body is not an item tree. Bit rot, or a bug in whatever wrote
        // it. The spool stores bytes, so only the equipment can find out.
        Assert.True(await spool.TryAppendAsync(
            new GemSpooledMessage(6, 11, false, new byte[] { 0xFF, 0xFF, 0xFF }, Noon, "Corrupt"),
            token));

        Assert.True(await spool.TryAppendAsync(Held(6, 11, Noon, SecsItem.A("LOT-2")), token));

        var reopened = new GemFileSpool(temporary.Path);

        await using var link = GemLink.Start(Model, spool: reopened);

        await GemLink.WaitUntilAsync(
            () => link.Equipment.SpoolCountActual == 2,
            "the equipment to adopt the spool it was given");

        await link.OpenAsync();

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        // The second one arrives. Keeping the undecodable record at the head would stall every
        // S6F23 from then on against the same three bytes, and the message behind it would never
        // be delivered at all.
        using var report = await link.ReceiveAsync();

        Assert.Equal("LOT-2", report.Item!.GetString());

        await GemLink.WaitUntilAsync(
            () => link.Equipment.SpoolCountActual == 0,
            "both records to leave the spool");

        // And the spool read the bad record perfectly well: nothing was dropped on the way out of
        // the file. That is what makes this the equipment's catch rather than the spool's, and it
        // is the distinction worth pinning down - the two failures look identical from the host.
        Assert.Equal(0, reopened.Unreadable);

        Assert.Empty(Directory.GetFiles(temporary.Path, "*" + GemFileSpool.RecordExtension));
    }

    /// <summary>A directory of its own per test, removed afterwards.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-spool-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A test that cannot clean up after itself should not fail for it.
            }
        }
    }
}
