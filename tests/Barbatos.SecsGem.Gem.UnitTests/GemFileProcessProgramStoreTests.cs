// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Stream 7's three stores, on a disk.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism under all three is <c>DurableRecordDirectory</c>, shared as source with the four
/// transfer packages and tested hardest in <c>RecipeFileStoreTests</c> - the sweep, the truncation,
/// the duplicate records, the identifiers a file system would mangle. What is tested here is what
/// belongs to <em>stream 7</em>: a grant that is computed rather than remembered, a formatted
/// program written down as the body S7F23 carries, a matrix that no message can delete, and the
/// three record extensions that stop one store adopting another's programs.
/// </para>
/// <para>
/// One file for three stores, because the last two claims belong to none of them on its own.
/// </para>
/// </remarks>
public class GemFileProcessProgramStoreTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down");
    private static readonly byte[] Newer = Encoding.ASCII.GetBytes("STEP 1: pump down / STEP 2: etch");
    private static readonly byte[] Matrix = [0x01, 0x02, 0x03, 0x04];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "Pressure", () => SecsItem.F8(1_013.25d), "mbar");

    private static GemFormattedProcessProgram Formatted(string id, string revision = "1.4.2")
        => new(
            id,
            "BRBTOS-1",
            revision,
            [
                new GemProcessProgramCommand(1, ["PRESSURE", "4.0"]),
                new GemProcessProgramCommand(2, ["TIME", "40"]),
            ]);

    private static string[] Records(string directory, string extension)
        => [.. Directory.EnumerateFiles(directory, "*" + extension)];

    // ----------------------------------------------------------------- the plain store

    [Fact(Timeout = 15_000)]
    public async Task A_process_program_comes_back_from_a_store_that_never_saw_it_arrive()
    {
        using var temporary = new TempDirectory();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await new GemFileProcessProgramStore(temporary.Path).SaveAsync("RECIPE-7", Body, Token));

        var reopened = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has("RECIPE-7"));
        Assert.Equal(["RECIPE-7"], await reopened.ListAsync(Token));
        Assert.Equal(Body, (await reopened.LoadAsync("RECIPE-7", Token))?.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_grant_is_computed_and_not_remembered_so_a_restart_still_says_already_have()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(GemProcessProgramGrant.Ok, await store.InquireAsync("RECIPE-7", Body.Length, Token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("RECIPE-7", Body, Token));

        // The difference from every other store in this repository: stream 7's grant is an answer
        // about what is held rather than a promise to be kept, so there is nothing in flight for a
        // restart to end - and the restarted machine says AlreadyHave because the program is there.
        var reopened = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(GemProcessProgramGrant.AlreadyHave, await reopened.InquireAsync("RECIPE-7", Body.Length, Token));
        Assert.Equal(GemProcessProgramGrant.Ok, await reopened.InquireAsync("RECIPE-8", Body.Length, Token));
        Assert.Equal(GemProcessProgramGrant.InvalidId, await reopened.InquireAsync(string.Empty, 1, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_replaced_is_one_record_and_the_newer_bytes()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("RECIPE-7", Body, Token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("RECIPE-7", Newer, Token));

        Assert.Single(Records(temporary.Path, GemFileProcessProgramStore.RecordExtension));

        var reopened = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(Newer, (await reopened.LoadAsync("RECIPE-7", Token))?.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_delete_and_a_delete_all_take_the_records_with_them()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramStore(temporary.Path);

        await store.SaveAsync("RECIPE-7", Body, Token);
        await store.SaveAsync("RECIPE-8", Body, Token);

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.DeleteAsync("RECIPE-7", Token));
        Assert.Equal(GemProcessProgramAcknowledgement.NotFound, await store.DeleteAsync("RECIPE-7", Token));
        Assert.Single(Records(temporary.Path, GemFileProcessProgramStore.RecordExtension));

        // S7F17 with an empty list means all of them, which on a disk is as many deletes as there
        // are programs - and the next open has to find nothing, or a delete-all would come back.
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.DeleteAllAsync(Token));
        Assert.Empty(Records(temporary.Path, GemFileProcessProgramStore.RecordExtension));
        Assert.Equal(0, new GemFileProcessProgramStore(temporary.Path).Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_delete_the_disk_refuses_is_a_refusal_and_not_a_silent_success()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<GemFileProcessProgramStore>();
        var store = new GemFileProcessProgramStore(temporary.Path, logger);

        await store.SaveAsync("RECIPE-7", Body, Token);

        // Holding the record open is enough on Windows, which is the whole reason this claim is
        // testable at all rather than a paragraph nobody can check.
        await using var held = new FileStream(
            Records(temporary.Path, GemFileProcessProgramStore.RecordExtension)[0],
            new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.None });

        // PermissionNotGranted, which is the nearest honest code: ACKC7 has nothing meaning "I
        // could not". And the program keeps its place, so the store and the disk agree - a store
        // that reported Accepted here would have the next open quietly bring the program back.
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await store.DeleteAsync("RECIPE-7", Token));

        Assert.Equal(1, store.Count);
        Assert.Equal(["RECIPE-7"], await store.ListAsync(Token));

        // The same for the delete-all S7F17 means with an empty list, which is as many deletes as
        // there are programs and reports the refusal if any of them is refused.
        Assert.Equal(GemProcessProgramAcknowledgement.PermissionNotGranted, await store.DeleteAllAsync(Token));
        Assert.Equal(1, store.Count);
        Assert.Contains("would not delete", Assert.Single(logger.From(LogLevel.Warning)));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_a_file_system_would_mangle_is_a_program_like_any_other()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramStore(temporary.Path);

        // PPID is an A[n] capped at 120 characters and otherwise left to the two ends, so a host
        // may send any of these - and the longest one is the cap, which is where a store that put
        // the identifier in a path would also meet the file system's own limits.
        string[] awkward =
        [
            "CON",
            "LPT1.processprogram",
            "a/b\\c",
            "../escape",
            "recipe",
            "RECIPE",
            "RECIPE 7",
            new('P', GemInMemoryProcessProgramStore.MaxIdLength),
        ];

        foreach (var id in awkward)
        {
            Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync(id, Encoding.ASCII.GetBytes(id), Token));
        }

        var reopened = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(awkward.Length, reopened.Count);
        Assert.Equal(awkward.Length, Records(temporary.Path, GemFileProcessProgramStore.RecordExtension).Length);

        foreach (var id in awkward)
        {
            Assert.Equal(id, Encoding.ASCII.GetString((await reopened.LoadAsync(id, Token))!.Value.Span));
        }

        // And one character more is not a PPID at all, which is E5's cap rather than this store's.
        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await reopened.SaveAsync(new string('P', GemInMemoryProcessProgramStore.MaxIdLength + 1), Body, Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_limits_are_configuration_and_what_is_adopted_is_a_fact()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramStore(temporary.Path) { Capacity = 1, MaxBodyLength = 4 };

        Assert.Equal(GemProcessProgramGrant.NoSpace, await store.InquireAsync("RECIPE-7", 5, Token));
        Assert.Equal(GemProcessProgramAcknowledgement.LengthError, await store.SaveAsync("RECIPE-7", Body, Token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("RECIPE-7", Matrix, Token));
        Assert.Equal(GemProcessProgramGrant.NoSpace, await store.InquireAsync("RECIPE-8", 1, Token));
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await store.SaveAsync("RECIPE-8", Matrix, Token));

        // Not written down: they are this machine's configuration and not anything a host said.
        var reopened = new GemFileProcessProgramStore(temporary.Path);

        Assert.Equal(64, reopened.Capacity);
        Assert.Equal(1024 * 1024, reopened.MaxBodyLength);
        Assert.Equal(1, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_that_will_not_read_refuses_the_upload_and_says_so()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<GemFileProcessProgramStore>();
        var store = new GemFileProcessProgramStore(temporary.Path, logger);

        await store.SaveAsync("RECIPE-7", Body, Token);
        File.Delete(Records(temporary.Path, GemFileProcessProgramStore.RecordExtension)[0]);

        // S7F6 carries no acknowledgement at all - an empty PPBODY is the whole of the refusal - so
        // the log is the only place this differs from a program nobody asked about.
        Assert.Null(await store.LoadAsync("RECIPE-7", Token));
        Assert.Contains(logger.From(LogLevel.Warning), line => line.Contains("RECIPE-7", StringComparison.Ordinal));

        // And the directory still answers, because it is the index: a machine that lost a program
        // can still say which one it has lost.
        Assert.Equal(["RECIPE-7"], await store.ListAsync(Token));
    }

    // ----------------------------------------------------------------- the formatted store

    [Fact(Timeout = 15_000)]
    public async Task A_formatted_program_comes_back_with_its_model_revision_and_commands()
    {
        using var temporary = new TempDirectory();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await new GemFileFormattedProcessProgramStore(temporary.Path).SaveAsync(Formatted("RECIPE-7"), Token));

        var reopened = new GemFileFormattedProcessProgramStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(["RECIPE-7"], await reopened.ListAsync(Token));

        var program = await reopened.LoadAsync("RECIPE-7", Token);

        Assert.NotNull(program);
        Assert.Equal("BRBTOS-1", program.ModelName);
        Assert.Equal("1.4.2", program.SoftwareRevision);
        Assert.Equal(2, program.Commands.Count);
        Assert.Equal(1, program.Commands[0].Code);
        Assert.Equal(["PRESSURE", "4.0"], program.Commands[0].Parameters);
        Assert.Equal(["TIME", "40"], program.Commands[1].Parameters);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_formatted_program_is_written_down_as_the_body_S7F23_carries()
    {
        using var temporary = new TempDirectory();

        await new GemFileFormattedProcessProgramStore(temporary.Path).SaveAsync(Formatted("RECIPE-7"), Token);

        // Read without the store, to pin what is actually on the disk: the record's payload is the
        // item the codec writes for the wire - PPID, MDLN, SOFTREV and the commands - rather than a
        // second format invented for a file and kept in step with the first by hand.
        var bytes = await File.ReadAllBytesAsync(
            Records(temporary.Path, GemFileFormattedProcessProgramStore.RecordExtension)[0],
            Token);

        // The record is a header, one field (the identifier) and the payload; the payload starts
        // where the field ends, which is what the shared format lays down.
        var payload = bytes.AsMemory(8 + 4 + "RECIPE-7".Length + 4);

        using var item = SecsItem.Decode(payload);

        Assert.True(item.IsList);
        Assert.Equal(4, item.Count);
        Assert.Equal("RECIPE-7", item.Items[0].GetString());
        Assert.Equal("BRBTOS-1", item.Items[1].GetString());
        Assert.Equal("1.4.2", item.Items[2].GetString());
        Assert.Equal(2, item.Items[3].Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_whose_payload_is_not_a_formatted_program_is_left_where_it_is()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileFormattedProcessProgramStore(temporary.Path);

        await store.SaveAsync(Formatted("RECIPE-7"), Token);

        var path = Records(temporary.Path, GemFileFormattedProcessProgramStore.RecordExtension)[0];
        var bytes = await File.ReadAllBytesAsync(path, Token);

        // The framing stays valid and the payload stops being a program: an item that is text where
        // a list belongs. This store has to decode to know it has a program at all, which is a
        // wider net than the other two cast - and the answer is still to refuse and leave the file.
        using var text = SecsItem.A("not a program");

        var encoded = text.ToByteArray();
        var record = new byte[bytes.Length - (bytes.Length - 8 - 4 - "RECIPE-7".Length - 4) + encoded.Length];

        bytes.AsSpan(0, 8 + 4 + "RECIPE-7".Length + 4).CopyTo(record);
        encoded.CopyTo(record, 8 + 4 + "RECIPE-7".Length + 4);
        record[8 + 4 + "RECIPE-7".Length + 3] = (byte)encoded.Length;

        await File.WriteAllBytesAsync(path, record, Token);

        var logger = new RecordingLogger<GemFileFormattedProcessProgramStore>();
        var reopened = new GemFileFormattedProcessProgramStore(temporary.Path, logger);

        Assert.Equal(0, reopened.Count);
        Assert.True(File.Exists(path));
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("1 unreadable record(s) left alone", StringComparison.Ordinal));

        // And the line says WHICH refusal, which is why the acknowledgement is kept rather than
        // compared away: LengthError is a body that is not the shape E5 defines, where
        // ModeUnsupported is the right shape carrying something this version does not represent -
        // what a record from a later version would look like.
        Assert.Contains(
            logger.From(LogLevel.Warning),
            line => line.Contains(
                GemProcessProgramAcknowledgement.LengthError.ToString(),
                StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_formatted_store_deletes_the_way_the_plain_one_does()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileFormattedProcessProgramStore(temporary.Path) { Capacity = 1 };

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync(Formatted("RECIPE-7"), Token));
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await store.SaveAsync(Formatted("RECIPE-8"), Token));

        // A replacement is not a new program, so it is allowed at capacity - and it keeps one
        // record, with the newer revision in it.
        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await store.SaveAsync(Formatted("RECIPE-7", "1.5.0"), Token));

        Assert.Single(Records(temporary.Path, GemFileFormattedProcessProgramStore.RecordExtension));
        Assert.Equal(
            "1.5.0",
            (await new GemFileFormattedProcessProgramStore(temporary.Path).LoadAsync("RECIPE-7", Token))?.SoftwareRevision);

        Assert.Equal(GemProcessProgramAcknowledgement.NotFound, await store.DeleteAsync("RECIPE-8", Token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.DeleteAllAsync(Token));
        Assert.Empty(Records(temporary.Path, GemFileFormattedProcessProgramStore.RecordExtension));
    }

    // ----------------------------------------------------------------- the matrix store

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_comes_back_and_only_the_machine_can_remove_it()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramMatrixStore(temporary.Path);

        Assert.Equal(GemProcessProgramGrant.Ok, await store.InquireAsync("MATRIX-1", Matrix.Length, Token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync("MATRIX-1", Matrix, Token));

        var reopened = new GemFileProcessProgramMatrixStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has("MATRIX-1"));
        Assert.Equal(Matrix, (await reopened.LoadAsync("MATRIX-1", Token))?.ToArray());
        Assert.Equal(GemProcessProgramGrant.AlreadyHave, await reopened.InquireAsync("MATRIX-1", 1, Token));

        // S7F17 names process programs and this store is deliberately not in it, so without Forget
        // a matrix would live on the disk for ever. Nothing on the wire reaches it.
        Assert.False(reopened.Forget("MATRIX-2"));
        Assert.True(reopened.Forget("MATRIX-1"));
        Assert.Equal(0, new GemFileProcessProgramMatrixStore(temporary.Path).Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_store_at_capacity_answers_the_code_the_table_has_for_it()
    {
        using var temporary = new TempDirectory();
        var durable = new GemFileProcessProgramMatrixStore(temporary.Path) { Capacity = 1 };

        await durable.SaveAsync("MATRIX-1", Matrix, Token);

        // ACKC7 code 3 is matrix overflow, and these are the only rows it can belong to. Both
        // stores answer it; the in-memory one answered PermissionNotGranted until this store was
        // written beside it, which is what found a member nothing in the repository ever sent.
        var memory = new GemInMemoryProcessProgramMatrixStore { Capacity = 1 };

        await memory.SaveAsync("MATRIX-1", Matrix, Token);

        Assert.Equal(
            GemProcessProgramAcknowledgement.MatrixOverflow,
            await durable.SaveAsync("MATRIX-2", Matrix, Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.MatrixOverflow,
            await memory.SaveAsync("MATRIX-2", Matrix, Token));

        // And the plain store keeps the generic refusal, because code 3 is not about it.
        var programs = new GemFileProcessProgramStore(temporary.Path) { Capacity = 1 };

        await programs.SaveAsync("RECIPE-7", Body, Token);

        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await programs.SaveAsync("RECIPE-8", Body, Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task And_a_host_told_a_matrix_will_not_fit_is_told_so_on_the_wire()
    {
        using var temporary = new TempDirectory();
        var store = new GemFileProcessProgramMatrixStore(temporary.Path) { Capacity = 1 };

        await store.SaveAsync("MATRIX-1", Matrix, Token);

        await using var link = GemLink.Start(Model, matrices: store);
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 11, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("MATRIX-2"), SecsItem.B(Matrix)),
        });

        // Driven rather than compared, because a code that is right in the enum and never sent is
        // the hole this repository has found four times.
        Assert.Equal(12, sent.F);
        Assert.Equal(
            (byte)GemProcessProgramAcknowledgement.MatrixOverflow,
            sent.Item!.FirstValueOrDefault<byte>());
    }

    // ----------------------------------------------------------------- and the three together

    [Fact(Timeout = 15_000)]
    public async Task Three_stores_over_one_directory_keep_their_records_apart()
    {
        using var temporary = new TempDirectory();

        var programs = new GemFileProcessProgramStore(temporary.Path);
        var formatted = new GemFileFormattedProcessProgramStore(temporary.Path);
        var matrices = new GemFileProcessProgramMatrixStore(temporary.Path);

        await programs.SaveAsync("RECIPE-7", Body, Token);
        await formatted.SaveAsync(Formatted("RECIPE-7"), Token);
        await matrices.SaveAsync("RECIPE-7", Matrix, Token);

        // The same identifier three times, three records, three extensions. The records are
        // indistinguishable - an identifier and a payload - so the extension is the whole of what
        // stops a matrix coming back from S7F5 as a plain program, which is the matrix chapter's
        // finding carried onto a disk.
        Assert.Equal(3, Directory.EnumerateFiles(temporary.Path).Count());

        var reopenedPrograms = new GemFileProcessProgramStore(temporary.Path);
        var reopenedFormatted = new GemFileFormattedProcessProgramStore(temporary.Path);
        var reopenedMatrices = new GemFileProcessProgramMatrixStore(temporary.Path);

        Assert.Equal(1, reopenedPrograms.Count);
        Assert.Equal(1, reopenedFormatted.Count);
        Assert.Equal(1, reopenedMatrices.Count);

        Assert.Equal(Body, (await reopenedPrograms.LoadAsync("RECIPE-7", Token))?.ToArray());
        Assert.Equal(Matrix, (await reopenedMatrices.LoadAsync("RECIPE-7", Token))?.ToArray());
        Assert.Equal("BRBTOS-1", (await reopenedFormatted.LoadAsync("RECIPE-7", Token))?.ModelName);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_pointed_at_another_stores_directory_finds_nothing()
    {
        using var programsDirectory = new TempDirectory();
        using var matricesDirectory = new TempDirectory();
        using var formattedDirectory = new TempDirectory();

        await new GemFileProcessProgramStore(programsDirectory.Path).SaveAsync("RECIPE-7", Body, Token);
        await new GemFileProcessProgramMatrixStore(matricesDirectory.Path).SaveAsync("MATRIX-1", Matrix, Token);
        await new GemFileFormattedProcessProgramStore(formattedDirectory.Path).SaveAsync(Formatted("RECIPE-9"), Token);

        // The same claim from the other side, and the one that would have mattered: a machine
        // misconfigured to point its plain store at the matrix directory serves nothing rather than
        // serving matrices as process programs.
        Assert.Equal(0, new GemFileProcessProgramStore(matricesDirectory.Path).Count);
        Assert.Equal(0, new GemFileProcessProgramMatrixStore(programsDirectory.Path).Count);

        // And the pairing where the extension is the ONLY thing standing in the way, which a
        // control found this test was not making: a formatted record's payload is a well-formed
        // item and a plain store will take **any** bytes as a PPBODY, so nothing but the name of
        // the file stops it serving somebody else's structured recipe as a block of bytes. The
        // other direction is protected twice over - a plain body is not usually a decodable item -
        // so it proves less than it looks.
        Assert.Equal(0, new GemFileProcessProgramStore(formattedDirectory.Path).Count);
        Assert.Equal(0, new GemFileProcessProgramMatrixStore(formattedDirectory.Path).Count);
        Assert.Equal(0, new GemFileFormattedProcessProgramStore(programsDirectory.Path).Count);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task A_host_sends_a_recipe_to_one_process_and_fetches_it_from_the_next()
    {
        using var plain = new TempDirectory();
        using var formatted = new TempDirectory();

        await using (var pair = GemPair.Start(
            Model,
            programs: new GemFileProcessProgramStore(plain.Path),
            formatted: new GemFileFormattedProcessProgramStore(formatted.Path)))
        {
            await pair.OpenAsync();

            Assert.Equal(
                GemProcessProgramGrant.Ok,
                await pair.Host.InquireProcessProgramAsync("RECIPE-7", Body.Length, GemPair.Token));

            Assert.Equal(
                GemProcessProgramAcknowledgement.Accepted,
                await pair.Host.SendProcessProgramAsync("RECIPE-7", Body, GemPair.Token));

            Assert.Equal(
                GemProcessProgramAcknowledgement.Accepted,
                await pair.Host.SendFormattedProcessProgramAsync(Formatted("RECIPE-9"), GemPair.Token));
        }

        // The equipment is gone and so are the stores that answered. New ones over the same
        // directories are what a machine that was switched off and on again has.
        await using var restarted = GemPair.Start(
            Model,
            programs: new GemFileProcessProgramStore(plain.Path),
            formatted: new GemFileFormattedProcessProgramStore(formatted.Path));

        await restarted.OpenAsync();

        // One directory over both halves, which is the equipment's design and not this test's
        // convenience: S7F19 asks what process programs the machine has, and a formatted one is a
        // process program. So a single answer shows both stores having adopted their directories.
        Assert.Equal(
            ["RECIPE-7", "RECIPE-9"],
            await restarted.Host.ReadProcessProgramDirectoryAsync(GemPair.Token));

        var fetched = await restarted.Host.RequestProcessProgramAsync("RECIPE-7", GemPair.Token);

        Assert.Equal(Body, fetched?.ToArray());

        // And the machine still says it has the formatted one, which is the answer S7F1 gives about
        // a program nobody has to send twice.
        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await restarted.Host.InquireProcessProgramAsync("RECIPE-7", Body.Length, GemPair.Token));

        var back = await restarted.Host.RequestFormattedProcessProgramAsync("RECIPE-9", GemPair.Token);

        Assert.Equal(2, back?.Commands.Count);
        Assert.Equal("1.4.2", back?.SoftwareRevision);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-pp-" + Guid.NewGuid().ToString("N"))).FullName;

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
