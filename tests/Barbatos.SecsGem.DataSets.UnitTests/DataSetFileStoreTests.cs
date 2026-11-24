// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// A data set store that outlives the process that was sent the data.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism under this is <c>DurableRecordDirectory</c>, shared as source with the three other
/// transfer stores and tested hardest in <c>RecipeFileStoreTests</c>. What is tested here is what
/// belongs to <em>stream 13</em>: the open that must come first, the length the open declares -
/// which is the one number this stream has and the one thing that cannot be measured from the
/// payload - and the state between an open and the data, which this store deliberately refuses to
/// write down.
/// </para>
/// <para>
/// These touch a real filesystem on purpose, for the reason <c>GemFileSpoolTests</c> gives.
/// </para>
/// </remarks>
public class DataSetFileStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DataSetPayload Payload(string lot)
    {
        using var item = SecsItem.L(SecsItem.A(lot), SecsItem.U4(7u));

        return DataSetPayload.From(item);
    }

    private static string[] Records(string directory)
        => [.. Directory.EnumerateFiles(directory, "*" + DataSetFileStore.RecordExtension)];

    private static async ValueTask<DataSetPayload?> ReadAsync(DataSetFileStore store, uint id)
    {
        Assert.Equal(
            DataSetGrant.Ok,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = id, Mode = DataSetOpenMode.Request },
                Token)).Grant);

        return await store.LoadAsync(new DataSetRequest { DataSetId = id, DataId = 1u }, Token);
    }

    // ----------------------------------------------------------------- what a restart keeps

    [Fact(Timeout = 15_000)]
    public async Task A_data_set_and_the_length_it_reports_come_back()
    {
        using var temporary = new TempDirectory();

        await new DataSetFileStore(temporary.Path).AddAsync(42u, Payload("LOT-1"), 2048u, Token);

        var reopened = new DataSetFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has(42u));
        Assert.Equal([42u], await reopened.ListAsync(Token));

        var status = await reopened.StatusAsync(42u, Token);

        Assert.Equal(DataSetAcknowledgement.Accepted, status.Acknowledgement);
        Assert.Equal(2048u, status.Length);

        // And the contents, which are the encoded item that arrived: DSDATA is defined nowhere, so
        // nothing here has ever looked inside it and the bytes are what go back.
        var read = await ReadAsync(reopened, 42u);

        Assert.NotNull(read);
        Assert.Equal(Payload("LOT-1").Encoded.ToArray(), read.Encoded.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_open_is_a_transaction_in_flight_and_survives_nothing()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path);

        Assert.Equal(
            DataSetGrant.Ok,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 42u, Mode = DataSetOpenMode.Send, Length = 500u },
                Token)).Grant);

        Assert.Equal(1, store.OpenCount);

        // The sharp one: between the open and the data there is a number and no data set, and this
        // store writes nothing for it. An S13F1 answered and an S13F3 that never came ends the way
        // an interruption ends.
        Assert.Empty(Records(temporary.Path));

        var reopened = new DataSetFileStore(temporary.Path);

        Assert.Equal(0, reopened.OpenCount);
        Assert.Equal(0, reopened.Count);
        Assert.False(reopened.IsOpen(42u, out _));

        // So the data that arrives after the restart is data nobody opened for.
        Assert.Equal(
            DataSetAcknowledgement.InvalidDataSet,
            await reopened.SaveAsync(
                new DataSetContent { DataSetId = 42u, DataId = 1u, Data = Payload("LOT-1") },
                Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_length_declared_and_never_sent_does_not_describe_what_is_on_the_disk()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path);

        await store.AddAsync(42u, Payload("LOT-1"), 300u, Token);

        // An open for send on a data set that already exists declares a new length - and the data
        // never comes. The length belongs to the payload that arrives with it, so the record still
        // says 300: a record whose length field disagreed with its own payload would be a machine
        // telling a host S13F16 figures it cannot stand behind.
        Assert.Equal(
            DataSetGrant.Ok,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 42u, Mode = DataSetOpenMode.Send, Length = 900u },
                Token)).Grant);

        Assert.Equal(300u, (await new DataSetFileStore(temporary.Path).StatusAsync(42u, Token)).Length);

        // And once the data does arrive, the number the open gave is the number it reports.
        Assert.Equal(
            DataSetAcknowledgement.Accepted,
            await store.SaveAsync(
                new DataSetContent { DataSetId = 42u, DataId = 1u, Data = Payload("LOT-2") },
                Token));

        Assert.Equal(900u, (await new DataSetFileStore(temporary.Path).StatusAsync(42u, Token)).Length);
        Assert.Single(Records(temporary.Path));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_data_set_replaced_is_one_record_and_the_newer_contents()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path);

        await store.AddAsync(42u, Payload("LOT-1"), 300u, Token);
        await store.AddAsync(42u, Payload("LOT-2"), 400u, Token);

        Assert.Single(Records(temporary.Path));

        var reopened = new DataSetFileStore(temporary.Path);
        var read = await ReadAsync(reopened, 42u);

        Assert.Equal(Payload("LOT-2").Encoded.ToArray(), read?.Encoded.ToArray());
        Assert.Equal(400u, (await reopened.StatusAsync(42u, Token)).Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task Identifiers_spread_across_the_range_come_back_as_themselves()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path);

        // Four bytes big-endian in the record rather than decimal text, and the edges are where a
        // width mistake would show: a uint that lost its top byte is still a plausible number.
        uint[] identifiers = [0u, 1u, 255u, 256u, 65_536u, uint.MaxValue, uint.MaxValue - 1u];

        foreach (var id in identifiers)
        {
            await store.AddAsync(id, Payload("LOT-" + id), id, Token);
        }

        var reopened = new DataSetFileStore(temporary.Path);

        Assert.Equal(identifiers.Length, reopened.Count);
        Assert.Equal([.. identifiers.Order()], await reopened.ListAsync(Token));

        foreach (var id in identifiers)
        {
            Assert.Equal(id, (await reopened.StatusAsync(id, Token)).Length);
        }
    }

    // ----------------------------------------------------------------- the rules it shares

    [Fact(Timeout = 15_000)]
    public async Task The_sequence_the_in_memory_store_enforces_is_enforced_here_too()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path);

        // A save with no open for send.
        Assert.Equal(
            DataSetAcknowledgement.InvalidDataSet,
            await store.SaveAsync(
                new DataSetContent { DataSetId = 42u, DataId = 1u, Data = Payload("LOT-1") },
                Token));

        // A load with no open for read, on a data set that is there.
        await store.AddAsync(7u, Payload("LOT-7"), 10u, Token);

        Assert.Null(await store.LoadAsync(new DataSetRequest { DataSetId = 7u, DataId = 1u }, Token));

        // An open for read on one that is not there. GRANT6 has no "no such data set", so "not
        // interested" is the only reading left.
        Assert.Equal(
            DataSetGrant.NoSpace,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 99u, Mode = DataSetOpenMode.Request },
                Token)).Grant);

        // A close of something that is not open, and of something that is.
        Assert.Equal(DataSetAcknowledgement.InvalidDataSet, await store.CloseAsync(99u, Token));
        Assert.NotNull(await ReadAsync(store, 7u));
        Assert.Equal(DataSetAcknowledgement.Accepted, await store.CloseAsync(7u, Token));

        // And the status of one that is not there.
        Assert.Equal(DataSetAcknowledgement.InvalidDataSet, (await store.StatusAsync(99u, Token)).Acknowledgement);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_declaration_counts_against_capacity_and_a_close_gives_it_back()
    {
        using var temporary = new TempDirectory();
        var store = new DataSetFileStore(temporary.Path) { Capacity = 1 };

        Assert.Equal(
            DataSetGrant.Ok,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 1u, Mode = DataSetOpenMode.Send, Length = 10u },
                Token)).Grant);

        // A host that opened one and sent nothing has asked for the slot, which is what the
        // in-memory store's null-contents entry does by sitting in the same dictionary.
        Assert.Equal(
            DataSetGrant.NoSpace,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 2u, Mode = DataSetOpenMode.Send, Length = 10u },
                Token)).Grant);

        // And closing it without data gives the slot back, which the in-memory store does not: an
        // open that closed with nothing behind it is finished. No host can see the difference -
        // a save after a close is refused either way.
        Assert.Equal(DataSetAcknowledgement.Accepted, await store.CloseAsync(1u, Token));
        Assert.Equal(
            DataSetGrant.Ok,
            (await store.OpenAsync(
                new DataSetOpenRequest { DataSetId = 2u, Mode = DataSetOpenMode.Send, Length = 10u },
                Token)).Grant);

        Assert.Equal(0, store.Count);
        Assert.Empty(Records(temporary.Path));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_that_will_not_read_refuses_the_send_and_says_so()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<DataSetFileStore>();
        var store = new DataSetFileStore(temporary.Path, logger);

        await store.AddAsync(42u, Payload("LOT-1"), 10u, Token);
        File.Delete(Records(temporary.Path)[0]);

        // S13F6 has no acknowledgement code at all - an empty DSDATA is the whole of the refusal -
        // so the log is the only place this is distinguishable from a data set nobody opened.
        Assert.Null(await ReadAsync(store, 42u));

        // On this store's own words and not on the identifier: the shared directory logs the
        // unreadable file's path as well, and a temporary directory named after a GUID contains
        // almost any digits you care to look for. A control on the service program store caught
        // the same assertion passing for that reason.
        Assert.Contains(
            logger.From(LogLevel.Warning),
            line => line.Contains("data set 42", StringComparison.Ordinal));

        // And the status still answers out of the index, so a machine that lost a data set can
        // still say which one.
        Assert.Equal(DataSetAcknowledgement.Accepted, (await store.StatusAsync(42u, Token)).Acknowledgement);
    }

    [Fact(Timeout = 15_000)]
    public void A_directory_that_is_not_there_is_made_and_an_empty_one_says_nothing()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "datasets", "deeper");

        var logger = new RecordingLogger<DataSetFileStore>();
        var store = new DataSetFileStore(path, logger);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), store.Directory);
        Assert.Equal(0, store.Count);
        Assert.Empty(logger.Entries);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task A_host_sends_a_data_set_to_one_process_and_fetches_it_from_the_next()
    {
        using var temporary = new TempDirectory();

        await using (var pair = DataSetPair.Start(new DataSetFileStore(temporary.Path)))
        {
            await pair.OpenAsync();

            using var data = SecsItem.L(SecsItem.A("LOT-1"), SecsItem.U4(7u));

            // Three transactions, which is stream 13 rather than a verbose test: the open is where
            // the host says how long it will be, because S13F3 does not carry a length.
            var opening = await pair.Host.OpenAsync(42u, DataSetOpenMode.Send, 2u, DataSetPair.Token);

            Assert.Equal(DataSetGrant.Ok, opening.Grant);
            Assert.Equal(
                DataSetAcknowledgement.Accepted,
                await pair.Host.SendAsync(42u, data, DataSetPair.Token));
            Assert.Equal(DataSetAcknowledgement.Accepted, await pair.Host.CloseAsync(42u, DataSetPair.Token));
        }

        // The equipment is gone and so is the store that answered. A new one over the same
        // directory is what a machine that was switched off and on again has.
        await using var restarted = DataSetPair.Start(new DataSetFileStore(temporary.Path));
        await restarted.OpenAsync();

        Assert.Equal([42u], await restarted.Host.ListAsync(DataSetPair.Token));

        // And three transactions the other way: the open for read, the get, the close. The store
        // wants the open first whichever store it is - the sequence is the store's rule and not
        // E5's, and a durable one has no licence to be looser about it.
        var reading = await restarted.Host.OpenAsync(42u, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        Assert.Equal(DataSetGrant.Ok, reading.Grant);
        Assert.Equal(2u, reading.Length);

        var fetched = await restarted.Host.GetAsync(42u, DataSetPair.Token);

        Assert.NotNull(fetched);

        using var decoded = fetched.Decode();

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Count);
        Assert.Equal("LOT-1", decoded.Items[0].GetString());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-datasets-" + Guid.NewGuid().ToString("N"))).FullName;

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
