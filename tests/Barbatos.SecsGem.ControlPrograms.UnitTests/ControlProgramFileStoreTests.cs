// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// A control program store that outlives the process that was given the firmware.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism under this is <c>DurableRecordDirectory</c>, shared as source with the three other
/// transfer stores, and it is tested hardest in <c>RecipeFileStoreTests</c> - the sweep, the
/// truncation, the duplicate records, the identifiers a file system would mangle. What is tested
/// here is what belongs to <em>stream 8</em>: the anonymous boot program, the revision beside it,
/// the single grant, and the one thing this stream cannot do at all - name a program.
/// </para>
/// <para>
/// These touch a real filesystem on purpose, for the reason <c>GemFileSpoolTests</c> gives: a fake
/// one would be testing the fake's idea of what <c>File.Move</c> does.
/// </para>
/// </remarks>
public class ControlProgramFileStoreTests
{
    private static readonly byte[] Firmware = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01];
    private static readonly byte[] Newer = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x02, 0x03];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Records(string directory)
        => [.. Directory.EnumerateFiles(directory, "*" + ControlProgramFileStore.RecordExtension)];

    // ----------------------------------------------------------------- what a restart keeps

    [Fact(Timeout = 15_000)]
    public async Task A_named_program_and_its_revision_come_back()
    {
        using var temporary = new TempDirectory();

        await new ControlProgramFileStore(temporary.Path).AddAsync("BOOT-A", Firmware, "1.4.2", Token);

        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has("BOOT-A"));
        Assert.Equal(["BOOT-A"], await reopened.ListAsync(Token));
        Assert.Equal("1.4.2", await reopened.RevisionAsync("BOOT-A", Token));
        Assert.Equal(Firmware, (await reopened.LoadAsync("BOOT-A", Token))?.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_boot_program_comes_back_and_is_still_in_no_directory()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync((uint)Firmware.Length, Token));
        Assert.Equal(ControlProgramAcknowledgement.Accepted, await store.SaveAsync(Firmware, Token));

        var reopened = new ControlProgramFileStore(temporary.Path);

        // S8F12 lists what a host can ask for by name, and S8F3 sent no name - so the boot program
        // is held, fetchable under the empty identifier, and in no listing. The same rule the
        // in-memory store makes, and it has to survive the disk or a restart would publish it.
        Assert.Equal(1, reopened.Count);
        Assert.Empty(await reopened.ListAsync(Token));
        Assert.Equal(Firmware, (await reopened.LoadAsync(ControlProgramFileStore.BootProgramId, Token))?.ToArray());

        // And no revision, because S8F3 carries no SOFTREV and inventing one would answer S8F10
        // with a number nobody sent.
        Assert.Null(await reopened.RevisionAsync(ControlProgramFileStore.BootProgramId, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_grant_does_not_survive_and_the_send_after_a_restart_is_denied()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync((uint)Firmware.Length, Token));
        Assert.Equal((uint)Firmware.Length, store.GrantedLength);

        // An S8F1 answered and an S8F3 that never came is a transaction in flight, so it ends the
        // way an interruption ends and the host inquires again.
        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Null(reopened.GrantedLength);
        Assert.Equal(ControlProgramAcknowledgement.Denied, await reopened.SaveAsync(Firmware, Token));
        Assert.Equal(0, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_replaced_is_one_record_and_the_newer_bytes()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        await store.AddAsync("BOOT-A", Firmware, "1.4.2", Token);
        await store.AddAsync("BOOT-A", Newer, "1.5.0", Token);

        Assert.Single(Records(temporary.Path));

        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal("1.5.0", await reopened.RevisionAsync("BOOT-A", Token));
        Assert.Equal(Newer, (await reopened.LoadAsync("BOOT-A", Token))?.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_boot_program_and_a_named_one_are_two_records_and_two_programs()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        await store.AddAsync("BOOT-A", Firmware, "1.4.2", Token);
        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync((uint)Newer.Length, Token));
        Assert.Equal(ControlProgramAcknowledgement.Accepted, await store.SaveAsync(Newer, Token));

        // The empty identifier is an identifier like any other here, which is only true because it
        // lives inside the record: a store that put the name in the file name would have had to
        // invent something for a program that has none.
        Assert.Equal(2, Records(temporary.Path).Length);

        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(["BOOT-A"], await reopened.ListAsync(Token));
        Assert.Equal(Firmware, (await reopened.LoadAsync("BOOT-A", Token))?.ToArray());
        Assert.Equal(Newer, (await reopened.LoadAsync("", Token))?.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_a_file_system_would_mangle_is_a_program_like_any_other()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        // CPID is an A[n] with no vocabulary in the reference, so a host may send any of these.
        string[] awkward = ["CON", "PRN.program", "a/b\\c", "../escape", "boot", "BOOT", "BOOT A"];

        foreach (var id in awkward)
        {
            await store.AddAsync(id, Encoding.ASCII.GetBytes(id), id, Token);
        }

        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(awkward.Length, reopened.Count);
        Assert.Equal(awkward.Length, Records(temporary.Path).Length);

        foreach (var id in awkward)
        {
            Assert.Equal(id, Encoding.ASCII.GetString((await reopened.LoadAsync(id, Token))!.Value.Span));
            Assert.Equal(id, await reopened.RevisionAsync(id, Token));
        }
    }

    // ----------------------------------------------------------------- the rules it shares

    [Fact(Timeout = 15_000)]
    public async Task A_length_that_does_not_match_is_denied_and_nothing_is_written()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync(99u, Token));

        // The check stream 13 cannot make: CPBODY is a B, so the bytes have a count. The grant goes
        // with the refusal, as the in-memory store's does.
        Assert.Equal(ControlProgramAcknowledgement.Denied, await store.SaveAsync(Firmware, Token));
        Assert.Null(store.GrantedLength);
        Assert.Empty(Records(temporary.Path));
        Assert.Equal(0, new ControlProgramFileStore(temporary.Path).Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_beyond_the_limits_is_refused_at_the_inquire()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path) { MaxLength = 8, Capacity = 1 };

        Assert.Equal(ControlProgramGrant.NoSpace, await store.InquireAsync(9u, Token));
        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync(8u, Token));

        // Capacity counts named programs and leaves the boot program out of the reckoning, which is
        // why a machine holding one has room for the next.
        await store.AddAsync("BOOT-A", Firmware, cancellationToken: Token);
        await store.AddAsync("BOOT-B", Firmware, cancellationToken: Token);

        Assert.Equal(ControlProgramGrant.NoSpace, await store.InquireAsync(8u, Token));

        // And the limits are configuration rather than anything a host said, so they are not
        // written down: the next store takes them from wherever the application sets them.
        var reopened = new ControlProgramFileStore(temporary.Path);

        Assert.Equal(4 * 1024 * 1024, reopened.MaxLength);
        Assert.Equal(16, reopened.Capacity);

        // What is on the disk is what is on the disk: a directory holding more than this machine is
        // configured for is a fact, not something to hide.
        Assert.Equal(2, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_machine_that_already_holds_a_boot_program_is_never_out_of_room_for_one()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path) { Capacity = 1 };

        // One named program against a capacity of one, so the reckoning is at its limit - and the
        // boot slot is the "+ 1" that leaves room for an S8F3.
        await store.AddAsync("BOOT-A", Firmware, "1.4.2", Token);

        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync((uint)Firmware.Length, Token));
        Assert.Equal(ControlProgramAcknowledgement.Accepted, await store.SaveAsync(Firmware, Token));
        Assert.Equal(2, store.Count);

        // The half a control found missing: once the boot program is held the check is skipped
        // altogether, because the only thing another S8F3 can do is replace the program the machine
        // already has. A store that counted it would answer NoSpace to a send that needs no room -
        // and would do it after a restart as well, so the adopted store is asked too.
        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync((uint)Firmware.Length, Token));
        Assert.Equal(
            ControlProgramGrant.Accepted,
            await new ControlProgramFileStore(temporary.Path) { Capacity = 1 }.InquireAsync(
                (uint)Firmware.Length,
                Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_abort_needs_a_grant_and_leaves_the_firmware_alone()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        await store.AddAsync("BOOT-A", Firmware, "1.4.2", Token);

        Assert.Equal(ControlProgramAcknowledgement.Denied, await store.AbortAsync("BOOT-A", Token));
        Assert.Equal(ControlProgramGrant.Accepted, await store.InquireAsync(4u, Token));
        Assert.Equal(ControlProgramAcknowledgement.Accepted, await store.AbortAsync("BOOT-A", Token));
        Assert.Null(store.GrantedLength);

        // Stream 8 has no message that deletes a program, so an abort that threw one away would be
        // inventing one.
        Assert.Equal(Firmware, (await store.LoadAsync("BOOT-A", Token))?.ToArray());
        Assert.Single(Records(temporary.Path));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_that_will_not_read_refuses_the_upload_and_says_so()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<ControlProgramFileStore>();
        var store = new ControlProgramFileStore(temporary.Path, logger);

        await store.AddAsync("BOOT-A", Firmware, "1.4.2", Token);
        File.Delete(Records(temporary.Path)[0]);

        // S8F6 has no acknowledgement item at all - an empty CPBODY is the whole of the refusal -
        // so the log is the only place this is distinguishable from a program nobody asked for.
        Assert.Null(await store.LoadAsync("BOOT-A", Token));
        Assert.Contains(
            logger.From(LogLevel.Warning),
            line => line.Contains("BOOT-A", StringComparison.Ordinal));

        // And the revision still answers, because it is in the index and not in the file: a machine
        // that lost a program can still say which one it has lost.
        Assert.Equal("1.4.2", await store.RevisionAsync("BOOT-A", Token));
    }

    [Fact(Timeout = 15_000)]
    public void A_directory_that_is_not_there_is_made_and_an_empty_one_says_nothing()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "programs", "deeper");

        var logger = new RecordingLogger<ControlProgramFileStore>();
        var store = new ControlProgramFileStore(path, logger);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), store.Directory);
        Assert.Equal(0, store.Count);
        Assert.Empty(logger.Entries);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task A_host_sends_firmware_to_one_process_and_the_next_one_still_has_it()
    {
        using var temporary = new TempDirectory();

        await using (var pair = ControlProgramPair.Start(new ControlProgramFileStore(temporary.Path)))
        {
            await pair.OpenAsync();

            // SendProgramAsync rather than SendAsync, because the store wants the inquire first:
            // S8F3 on its own is a program nobody made room for, and this store denies it exactly
            // as the in-memory one does.
            var (grant, acknowledgement) = await pair.Host.SendProgramAsync(Firmware, ControlProgramPair.Token);

            Assert.Equal(ControlProgramGrant.Accepted, grant);
            Assert.Equal(ControlProgramAcknowledgement.Accepted, acknowledgement);
        }

        // The equipment is gone and so is the store that answered. A new one over the same
        // directory is what a machine that was switched off and on again has.
        var adopted = new ControlProgramFileStore(temporary.Path);

        await using var restarted = ControlProgramPair.Start(adopted);
        await restarted.OpenAsync();

        // Asserted through the store rather than over the wire, and that is not a shortcut - see
        // the next test: the boot program has no name a host can ask with, so the machine holding
        // it is the only place the fact is observable.
        Assert.Equal(Firmware, (await adopted.LoadAsync(ControlProgramFileStore.BootProgramId, Token))?.ToArray());

        // And a named program, which only the store's own API can create, does come back over the
        // wire - both the bytes and the revision a host is advised to ask for first.
        await adopted.AddAsync("BOOT-A", Newer, "1.5.0", Token);

        var fetched = await restarted.Host.RequestAsync("BOOT-A", ControlProgramPair.Token);

        Assert.Equal(Newer, fetched?.Body.ToArray());
        Assert.Equal("1.5.0", await restarted.Host.RevisionAsync("BOOT-A", ControlProgramPair.Token));
        Assert.Equal(["BOOT-A"], await restarted.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task The_boot_program_is_write_only_over_the_wire_whichever_store_holds_it()
    {
        using var temporary = new TempDirectory();
        var store = new ControlProgramFileStore(temporary.Path);

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        var (grant, acknowledgement) = await pair.Host.SendProgramAsync(Firmware, ControlProgramPair.Token);

        Assert.Equal(ControlProgramGrant.Accepted, grant);
        Assert.Equal(ControlProgramAcknowledgement.Accepted, acknowledgement);

        // S8F5 carries a CPID and this package reads an empty one as a body that names nothing, so
        // the anonymous program cannot be asked for by the empty identifier it is held under - that
        // identifier is this library's own convention for a slot, not a name E5 gives a host. The
        // store has the firmware either way, which is what makes this a property of the stream
        // rather than of the store: the in-memory one is in exactly the same position.
        Assert.Null(await pair.Host.RequestAsync(ControlProgramFileStore.BootProgramId, ControlProgramPair.Token));
        Assert.Equal(Firmware, (await store.LoadAsync(ControlProgramFileStore.BootProgramId, Token))?.ToArray());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-programs-" + Guid.NewGuid().ToString("N"))).FullName;

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
