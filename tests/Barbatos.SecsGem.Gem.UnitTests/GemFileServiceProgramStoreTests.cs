// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Service programs on a disk, and the one judgement this store had to make.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism is <c>DurableRecordDirectory</c>, shared as source with four packages and tested
/// hardest in <c>RecipeFileStoreTests</c>. What is tested here is what belongs to <em>stream 2's
/// service programs</em>: the text comes back, and the <strong>results of a run do not</strong> -
/// because a result is what the machine produced, and a restarted machine has produced nothing.
/// </para>
/// <para>
/// This is the last store in the repository that kept what a host sent and lost it on a restart, so
/// the claim the suite can now make - every one of them is durable - is only as good as this file.
/// </para>
/// </remarks>
public class GemFileServiceProgramStoreTests
{
    private const string Program = "PRINT 1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "Pressure", () => SecsItem.F8(1_013.25d), "mbar");

    private static string[] Records(string directory)
        => [.. Directory.EnumerateFiles(directory, "*" + GemFileServiceProgramStore.RecordExtension)];

    private static GemFileServiceProgramStore Store(string directory, ILogger<GemFileServiceProgramStore>? logger = null)
        => new(directory, logger)
        {
            Run = (id, text) => $"SPID {id}: {text.Length} characters, exit 0",
        };

    // ----------------------------------------------------------------- what a restart keeps

    [Fact(Timeout = 15_000)]
    public async Task A_service_program_comes_back_from_a_store_that_never_saw_it_arrive()
    {
        using var temporary = new TempDirectory();

        Assert.Equal(
            GemServiceProgramAcknowledgement.Accepted,
            await Store(temporary.Path).SaveAsync(7u, Program, Token));

        var reopened = Store(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has(7u));
        Assert.Equal([7u], await reopened.ListAsync(Token));
        Assert.Equal(Program, await reopened.LoadAsync(7u, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_results_of_a_run_do_not_survive_because_the_machine_has_not_run_anything()
    {
        using var temporary = new TempDirectory();
        var store = Store(temporary.Path);

        await store.SaveAsync(7u, Program, Token);

        Assert.Equal(GemServiceProgramAcknowledgement.Accepted, await store.RunAsync(7u, Token));
        Assert.Equal("SPID 7: 7 characters, exit 0", await store.ResultsAsync(7u, Token));
        Assert.True(store.HasResults(7u));

        // The judgement. Everything else here is what a host told the machine and comes back; a
        // result is what the machine produced, and a restarted machine has produced nothing - so
        // answering S2F9 out of a file would be claiming a run that never happened. The host sends
        // S2F7 again, and the program is still there to run.
        var reopened = Store(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(0, reopened.ResultCount);
        Assert.False(reopened.HasResults(7u));
        Assert.Null(await reopened.ResultsAsync(7u, Token));

        Assert.Equal(GemServiceProgramAcknowledgement.Accepted, await reopened.RunAsync(7u, Token));
        Assert.Equal("SPID 7: 7 characters, exit 0", await reopened.ResultsAsync(7u, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Replacing_a_program_is_one_record_and_takes_its_results_with_it()
    {
        using var temporary = new TempDirectory();
        var store = Store(temporary.Path);

        await store.SaveAsync(7u, Program, Token);
        await store.RunAsync(7u, Token);

        Assert.True(store.HasResults(7u));

        // The in-memory store's rule, which is the smaller version of the one above: whatever the
        // old program produced is not this one's results, and leaving them would answer S2F9 with
        // the output of code that is no longer here.
        Assert.Equal(GemServiceProgramAcknowledgement.Accepted, await store.SaveAsync(7u, "PRINT 22", Token));
        Assert.False(store.HasResults(7u));
        Assert.Single(Records(temporary.Path));

        var reopened = Store(temporary.Path);

        Assert.Equal("PRINT 22", await reopened.LoadAsync(7u, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_run_after_a_restart_runs_the_text_that_is_on_the_disk()
    {
        using var temporary = new TempDirectory();

        await Store(temporary.Path).SaveAsync(7u, "PRINT 1234567890", Token);

        // Not the text it remembers, because it remembers none: the run reads the record. A store
        // that had kept the text in memory and reloaded nothing would report the wrong length here.
        var reopened = Store(temporary.Path);

        Assert.Equal(GemServiceProgramAcknowledgement.Accepted, await reopened.RunAsync(7u, Token));
        Assert.Equal("SPID 7: 16 characters, exit 0", await reopened.ResultsAsync(7u, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Identifiers_spread_across_the_range_come_back_as_themselves()
    {
        using var temporary = new TempDirectory();
        var store = Store(temporary.Path);

        uint[] identifiers = [0u, 1u, 255u, 256u, 65_536u, uint.MaxValue - 1u, uint.MaxValue];

        foreach (var id in identifiers)
        {
            await store.SaveAsync(id, "PRINT " + id, Token);
        }

        var reopened = Store(temporary.Path);

        Assert.Equal(identifiers.Length, reopened.Count);
        Assert.Equal([.. identifiers.Order()], await reopened.ListAsync(Token));

        foreach (var id in identifiers)
        {
            Assert.Equal("PRINT " + id, await reopened.LoadAsync(id, Token));
        }
    }

    // ----------------------------------------------------------------- the rules it shares

    [Fact(Timeout = 15_000)]
    public async Task The_rules_the_in_memory_store_makes_up_are_made_here_too()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileServiceProgramStore(temporary.Path) { Capacity = 1, MaxLength = 8 };

        // The inquire refuses on length and on capacity, and a replacement takes no new room -
        // DuplicateOrInvalid is deliberately not used for an overwrite, because a store that
        // refused every one could never be updated.
        Assert.Equal(GemGrant.NoSpace, await store.InquireAsync(7u, 9, Token));
        Assert.Equal(GemGrant.Accepted, await store.InquireAsync(7u, 8, Token));
        Assert.Equal(GemServiceProgramAcknowledgement.Accepted, await store.SaveAsync(7u, Program, Token));
        Assert.Equal(GemGrant.Accepted, await store.InquireAsync(7u, 8, Token));
        Assert.Equal(GemGrant.NoSpace, await store.InquireAsync(8u, 8, Token));

        Assert.Equal(GemServiceProgramAcknowledgement.Error, await store.SaveAsync(8u, Program, Token));
        Assert.Equal(GemServiceProgramAcknowledgement.Error, await store.SaveAsync(7u, "PRINT 123456789", Token));

        // A run of something that is not there, and a run this store cannot perform: the default
        // delegate refuses, because a directory cannot run anything either.
        Assert.Equal(GemServiceProgramAcknowledgement.Error, await store.RunAsync(9u, Token));
        Assert.Equal(GemServiceProgramAcknowledgement.Error, await store.RunAsync(7u, Token));
        Assert.Null(await store.ResultsAsync(7u, Token));

        // And the limits are configuration, not anything a host said.
        var reopened = new GemFileServiceProgramStore(temporary.Path);

        Assert.Equal(64, reopened.Capacity);
        Assert.Equal(64 * 1024, reopened.MaxLength);
        Assert.Equal(1, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_that_will_not_read_gives_no_text_and_says_so()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<GemFileServiceProgramStore>();
        var store = Store(temporary.Path, logger);

        await store.SaveAsync(7u, Program, Token);
        File.Delete(Records(temporary.Path)[0]);

        // S2F6 carries the text and no acknowledgement, so an empty one is the whole of the refusal
        // and the log is the only place this differs from a program nobody asked about.
        Assert.Null(await store.LoadAsync(7u, Token));

        // Matched on this store's own words rather than on the identifier, which a control caught:
        // the shared directory logs the unreadable file's path too, and a temporary directory named
        // after a GUID contains almost any digit you care to look for. An assertion a stray hex
        // character can satisfy is not an assertion.
        Assert.Contains(
            logger.From(LogLevel.Warning),
            line => line.Contains("service program 7", StringComparison.Ordinal));

        // And a run of it fails for the same reason rather than running stale text.
        Assert.Equal(GemServiceProgramAcknowledgement.Error, await store.RunAsync(7u, Token));

        // The directory still answers out of the index, so a machine that lost a program can still
        // say which one it has lost.
        Assert.Equal([7u], await store.ListAsync(Token));
    }

    [Fact(Timeout = 15_000)]
    public void A_directory_that_is_not_there_is_made_and_an_empty_one_says_nothing()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "service", "deeper");

        var logger = new RecordingLogger<GemFileServiceProgramStore>();
        var store = new GemFileServiceProgramStore(path, logger);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), store.Directory);
        Assert.Equal(0, store.Count);
        Assert.Empty(logger.Entries);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_service_program_store_is_not_a_process_program_store()
    {
        using var temporary = new TempDirectory();

        await Store(temporary.Path).SaveAsync(7u, Program, Token);

        // The extension claim again, and it matters as much here: a service program's record is an
        // identifier and a payload like every other, and its payload is text that a plain process
        // program store would serve as a PPBODY without blinking.
        Assert.Equal(0, new GemFileProcessProgramStore(temporary.Path).Count);
        Assert.Equal(0, new GemFileProcessProgramMatrixStore(temporary.Path).Count);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task A_host_sends_a_service_program_to_one_process_and_runs_it_in_the_next()
    {
        using var temporary = new TempDirectory();

        await using (var pair = GemPair.Start(Model, servicePrograms: Store(temporary.Path)))
        {
            await pair.OpenAsync();

            // The inquire is not optional: S2F3 carries no SPID, so the identifier a program is
            // filed under is the one granted here - and that pending identifier lives in the
            // equipment, not in the store, which is why a restart needs the pair again and not a
            // record.
            Assert.Equal(GemGrant.Accepted, await pair.Host.InquireServiceProgramAsync(7u, Program.Length, GemPair.Token));

            var stored = await pair.Host.SendServiceProgramAsync(Program, GemPair.Token);

            Assert.True(stored.IsAccepted);
            Assert.True((await pair.Host.RunServiceProgramAsync(7u, GemPair.Token)).IsAccepted);
            Assert.Equal("SPID 7: 7 characters, exit 0", await pair.Host.ReadServiceProgramResultsAsync(7u, GemPair.Token));
        }

        // The equipment is gone and so is the store that answered. A new one over the same
        // directory is what a machine that was switched off and on again has.
        await using var restarted = GemPair.Start(Model, servicePrograms: Store(temporary.Path));
        await restarted.OpenAsync();

        Assert.Equal([7u], await restarted.Host.ListServiceProgramsAsync(GemPair.Token));
        Assert.Equal(Program, (await restarted.Host.RequestServiceProgramAsync(7u, GemPair.Token)));

        // The results are gone with the process that produced them. S2F10 carries the text and no
        // acknowledgement, so "none" is a zero-length item - and `GemHost` reads that as null
        // rather than as an empty result, which is the distinction worth having: a program that ran
        // and printed nothing would answer with an empty string. Sending S2F7 again is all it
        // takes, because the program itself is still there.
        Assert.Null(await restarted.Host.ReadServiceProgramResultsAsync(7u, GemPair.Token));
        Assert.True((await restarted.Host.RunServiceProgramAsync(7u, GemPair.Token)).IsAccepted);
        Assert.Equal("SPID 7: 7 characters, exit 0", await restarted.Host.ReadServiceProgramResultsAsync(7u, GemPair.Token));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-sp-" + Guid.NewGuid().ToString("N"))).FullName;

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
