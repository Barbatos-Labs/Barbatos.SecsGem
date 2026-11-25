// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Text;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// A recipe store that outlives the process that filled it.
/// </summary>
/// <remarks>
/// <para>
/// The interesting tests here all involve a second store over the same directory, because that is
/// the only thing <see cref="RecipeFileStore"/> does that <see cref="RecipeInMemoryStore"/> does
/// not. The rules the two share - a grant before a send, a length that matches, a namespace that
/// exists, a name that is not taken, a lock that is honoured - are tested once each, because this
/// store writes them out again rather than sharing them, and a copied rule is a rule that can
/// drift.
/// </para>
/// <para>
/// These touch a real filesystem on purpose. A fake one would be testing the fake's idea of what
/// <c>File.Move</c>, <c>FileShare.None</c> and a reserved device name do, and those are the three
/// things the design leans on. <c>GemFileSpoolTests</c> says the same of the spool.
/// </para>
/// </remarks>
public class RecipeFileStoreTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: ETCH 40s");
    private static readonly byte[] Other = Encoding.ASCII.GetBytes("STEP 1: ETCH 60s / STEP 2: PURGE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Puts a recipe in the way a host would, because this store has no back door.</summary>
    private static async Task SeedAsync(RecipeFileStore store, string space, string id, byte[] body)
    {
        if (!store.HasNamespace(space))
        {
            Assert.Equal(RecipeAcknowledgement.Accepted, await store.CreateNamespaceAsync(space, Token));
        }

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = space, Id = id, Length = (uint)body.Length },
                Token));

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SaveAsync(space, id, body, Token));
    }

    private static string[] Records(string directory)
        => [.. Directory.EnumerateFiles(directory, "*" + RecipeFileStore.RecordExtension).Order(StringComparer.Ordinal)];

    private static string RecordPath(string directory, long sequence) => Path.Combine(
        directory,
        sequence.ToString("D19", CultureInfo.InvariantCulture) + RecipeFileStore.RecordExtension);

    // ----------------------------------------------------------------- one recipe, one record

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_comes_back_from_a_store_that_never_saw_it_arrive()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        // A second store, as a restart is: nothing is shared with the first but the directory.
        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.HasNamespace("PROD"));

        var (acknowledgement, body) = await reopened.LoadAsync("PROD", "ETCH-7", Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.Equal(Body, body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_attribute_a_host_set_survives_and_stays_in_the_recipes_own_record()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.SetAttributeAsync("PROD", "ETCH-7", "LIMITS", SecsItem.L(SecsItem.U4(10u)), Token));

        // One record still, because the attribute is inside the recipe's own: a side file would be
        // two writes with a window between them, and a restart could find a recipe without its
        // attributes or attributes without their recipe.
        Assert.Single(Records(temporary.Path));

        var reopened = new RecipeFileStore(temporary.Path);
        var value = await reopened.GetAttributeAsync("PROD", "ETCH-7", "LIMITS", Token);

        Assert.NotNull(value);
        Assert.True(value!.IsList);
        Assert.Equal(10u, value.Items[0].FirstValueOrDefault<uint>());

        // And the body is still there beside it, which is what the record format buys: fields then
        // a payload, so neither half can be written without the other.
        var (acknowledgement, body) = await reopened.LoadAsync("PROD", "ETCH-7", Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.Equal(Body, body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_owner_a_host_set_comes_back_and_a_namespace_attribute_deliberately_does_not()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        // One of these is what a host told the machine and the other is the application's own
        // wiring, which is the whole of why only one is on the disk. S15F49 is a message a host
        // sends; a namespace attribute has no row anywhere that sets one, so it reaches a store
        // the way a logger and a directory path do - on every start. The service program store
        // leaves its Run delegate out for the identical reason.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.SetOwnerAsync("PROD", "ETCH-7", SecsItem.A("mes-7"), Token));

        store.SetNamespaceAttribute("PROD", "DESCRIPTION", SecsItem.A("production recipes"));

        Assert.Equal("mes-7", store.Owner("PROD", "ETCH-7")!.GetString());
        Assert.Equal(
            "production recipes",
            (await store.GetNamespaceAttributeAsync("PROD", "DESCRIPTION", Token))!.GetString());

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal("mes-7", reopened.Owner("PROD", "ETCH-7")!.GetString());
        Assert.Null(await reopened.GetNamespaceAttributeAsync("PROD", "DESCRIPTION", Token));

        // The owner comes back as the ITEM it arrived as, not as text: the encoded item is what
        // was written down, because RCPOWNER is undefined and its rival RCPOWCODE is a BOOLEAN, so
        // a store that wrote the characters would have picked one of the two readings.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await reopened.SetOwnerAsync("PROD", "ETCH-7", SecsItem.Boolean(true), Token));

        var third = new RecipeFileStore(temporary.Path);
        var flag = third.Owner("PROD", "ETCH-7");

        Assert.Equal(SecsFormat.Boolean, flag!.Format);
        Assert.True(flag.GetSpan<bool>()[0]);
    }

    [Fact(Timeout = 20_000)]
    public async Task An_owner_follows_its_recipe_across_a_restart_and_is_dropped_when_the_recipe_goes()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        await SeedAsync(store, "DEV", "KEEP", Other);

        await store.SetOwnerAsync("PROD", "ETCH-7", SecsItem.A("mes-7"), Token);
        await store.SetOwnerAsync("DEV", "KEEP", SecsItem.A("mes-9"), Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));

        // The owner is in a side file and the attributes are in the record, so the rename has to
        // keep two things in step rather than one - which is the cost of leaving the record's
        // field count even, and the reason this is asserted after a restart and not before it.
        var renamed = new RecipeFileStore(temporary.Path);

        Assert.Null(renamed.Owner("PROD", "ETCH-7"));
        Assert.Equal("mes-7", renamed.Owner("PROD", "ETCH-8")!.GetString());

        Assert.Equal(RecipeAcknowledgement.Accepted, await renamed.CopyAsync("PROD", "ETCH-8", "DEV", "ETCH-8", Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await renamed.DeleteAsync("PROD", "ETCH-8", Token));

        var last = new RecipeFileStore(temporary.Path);

        Assert.Null(last.Owner("PROD", "ETCH-8"));
        Assert.Equal("mes-7", last.Owner("DEV", "ETCH-8")!.GetString());
        Assert.Equal("mes-9", last.Owner("DEV", "KEEP")!.GetString());
    }

    [Fact(Timeout = 20_000)]
    public async Task An_owner_naming_a_recipe_that_is_no_longer_there_is_dropped_on_adoption()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        await store.SetOwnerAsync("PROD", "ETCH-7", SecsItem.A("mes-7"), Token);

        // Somebody takes the recipe's record away and leaves the owners file behind, which is what
        // a half-restored backup looks like. The locks and the selection are read the same way:
        // the record is faithful and the judgement is in the reading, so an owner with no recipe
        // is dropped rather than adopted into a store that has nothing to attach it to.
        foreach (var record in Directory.GetFiles(temporary.Path, "*" + RecipeFileStore.RecordExtension))
        {
            File.Delete(record);
        }

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Null(reopened.Owner("PROD", "ETCH-7"));

        // And the file is still there until something rewrites it, which is why the index is what
        // answers rather than the disk.
        Assert.True(File.Exists(Path.Combine(temporary.Path, RecipeFileStore.OwnersFile)));
    }

    [Fact(Timeout = 20_000)]
    public async Task A_locked_recipe_refuses_an_owner_in_the_file_store_too()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        var (code, token) = await store.LockAsync("PROD", "ETCH-7", Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, code);
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.SetOwnerAsync("PROD", "ETCH-7", SecsItem.A("mes-7"), Token));

        // The in-memory store refuses this and so does this one, which is the rule both keep: a
        // lock whose consequences are the store's invention is worth nothing with a hole in it.
        Assert.Null(store.Owner("PROD", "ETCH-7"));
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.SetOwnerAsync("PROD", "NOT-THERE", SecsItem.A("mes-7"), Token));

        await store.UnlockAsync("PROD", "ETCH-7", token!, Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.SetOwnerAsync("PROD", "ETCH-7", SecsItem.A("mes-7"), Token));
    }

    [Fact(Timeout = 20_000)]
    public async Task A_directory_comes_out_of_the_index_and_survives_a_restart()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        await SeedAsync(store, "PROD", "CLEAN-1", Other);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), Token));

        // The length is kept beside the sequence number rather than measured off the disk, which is
        // the point: this store holds no bodies, so a directory that read every file to size it
        // would undo that. Asserted after a restart as well, because adoption is the other place
        // the two have to be written down together.
        var reopened = new RecipeFileStore(temporary.Path);

        foreach (var directory in new[]
        {
            await store.ListRecipesAsync("PROD", Token),
            await reopened.ListRecipesAsync("PROD", Token),
        })
        {
            Assert.NotNull(directory);
            Assert.Equal<string[]>(["CLEAN-1", "ETCH-7"], [.. directory!.Select(entry => entry.Id)]);
            Assert.Equal((uint)Other.Length, directory[0].Length);
            Assert.Equal((uint)Body.Length, directory[1].Length);

            Assert.Empty(directory[0].Attributes);
            Assert.Equal("days", Assert.Single(directory[1].Attributes).Value.GetString());
        }

        // And the two answers a host cannot tell apart, which the store can.
        Assert.NotNull(await reopened.ListRecipesAsync("PROD", Token));
        Assert.Null(await reopened.ListRecipesAsync("NOBODY", Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_and_a_copy_both_keep_the_length_the_directory_reports()
    {
        using var temporary = new TempDirectory();

        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Other);
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CreateNamespaceAsync("TEST", Token));

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CopyAsync("PROD", "ETCH-8", "TEST", "ETCH-8", Token));

        // Neither operation touches the body, so the length the index recorded is still the length -
        // the reason the sequence number and the length travel as one value rather than as two
        // dictionaries somebody has to keep in step.
        foreach (var space in new[] { "PROD", "TEST" })
        {
            var directory = await store.ListRecipesAsync(space, Token);

            Assert.Equal((uint)Other.Length, Assert.Single(directory!).Length);
        }

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal((uint)Other.Length, Assert.Single((await reopened.ListRecipesAsync("TEST", Token))!).Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_written_before_attributes_existed_still_reads()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        // Seeded with no attribute, so the record on the disk has exactly two fields - which is
        // what a directory written by an earlier version of this library contains. The reader
        // takes an even count of at least two rather than exactly two, so those recipes come back
        // with no attributes instead of not coming back at all.
        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Null(await reopened.GetAttributeAsync("PROD", "ETCH-7", "LIMITS", Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task One_recipe_is_one_record_however_often_it_is_sent()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        await SeedAsync(store, "PROD", "ETCH-7", Other);

        // The sequence is reused rather than spent, because the identifier is inside the record and
        // not in the file name. Two records for one recipe is the thing this layout cannot produce.
        Assert.Single(Records(temporary.Path));

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(Other, (await reopened.LoadAsync("PROD", "ETCH-7", Token)).Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_rewrites_the_record_and_does_not_make_a_second_one()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));
        Assert.Single(Records(temporary.Path));

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(RecipeAcknowledgement.Rejected, (await reopened.LoadAsync("PROD", "ETCH-7", Token)).Acknowledgement);
        Assert.Equal(Body, (await reopened.LoadAsync("PROD", "ETCH-8", Token)).Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_copy_is_a_second_record_because_it_is_a_second_recipe()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CreateNamespaceAsync("TEST", Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CopyAsync("PROD", "ETCH-7", "TEST", "ETCH-7", Token));

        Assert.Equal(2, Records(temporary.Path).Length);

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(Body, (await reopened.LoadAsync("PROD", "ETCH-7", Token)).Body.ToArray());
        Assert.Equal(Body, (await reopened.LoadAsync("TEST", "ETCH-7", Token)).Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_a_file_system_would_mangle_is_a_recipe_like_any_other()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        // The payoff of keeping identity inside the record: RCPID is an A[n] and nothing in E5
        // stops a host choosing any of these. CON and NUL are reserved device names on Windows, the
        // slashes would be directories, the dots would climb out of the directory altogether, and
        // abc against ABC are one file on a case-insensitive volume and two recipes under
        // StringComparer.Ordinal.
        string[] awkward = ["CON", "NUL.recipe", "a/b\\c", "../escape", "abc", "ABC", " ", "ETCH 7"];

        foreach (var id in awkward)
        {
            await SeedAsync(store, "PROD", id, Encoding.ASCII.GetBytes(id));
        }

        Assert.Equal(awkward.Length, store.Count);
        Assert.Equal(awkward.Length, Records(temporary.Path).Length);

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(awkward.Length, reopened.Count);

        foreach (var id in awkward)
        {
            var (acknowledgement, body) = await reopened.LoadAsync("PROD", id, Token);

            Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
            Assert.Equal(id, Encoding.ASCII.GetString(body.Span));
        }
    }

    // ----------------------------------------------------------------- what a restart keeps

    [Fact(Timeout = 15_000)]
    public async Task An_empty_namespace_survives_because_the_list_is_written_down()
    {
        using var temporary = new TempDirectory();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await new RecipeFileStore(temporary.Path).CreateNamespaceAsync("EMPTY", Token));

        // Nothing but the small file can carry this one: an empty namespace has no record to be
        // read off, which is why the namespaces are written down as well as implied.
        Assert.Empty(Records(temporary.Path));

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.True(reopened.HasNamespace("EMPTY"));
        Assert.Equal(["EMPTY"], await reopened.ListNamespacesAsync(Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_holding_a_recipe_survives_a_lost_list()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        // Losing the list is not a reason to lose a recipe, so the namespace comes back off the
        // record that carries it. A store that kept identity in the file name would have the same
        // property; one that kept the namespace only in the small file would not.
        File.Delete(Path.Combine(temporary.Path, RecipeFileStore.NamespacesFile));

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.True(reopened.HasNamespace("PROD"));
        Assert.Equal(1, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_grant_does_not_survive_and_the_send_after_a_restart_is_refused()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CreateNamespaceAsync("PROD", Token));
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
                Token));

        Assert.Equal(1, store.GrantedCount);

        // A grant is a transaction in flight: the S15F5 was answered and the S15F7 never came, so
        // it ends the way an interruption ends and the host asks again.
        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(0, reopened.GrantedCount);
        Assert.Equal(RecipeAcknowledgement.Rejected, await reopened.SaveAsync("PROD", "ETCH-7", Body, Token));
        Assert.Equal(0, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_lock_survives_and_the_token_that_took_it_still_releases_it()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        var (acknowledgement, token) = await store.LockAsync("PROD", "ETCH-7", Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(token);

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(1, reopened.LockedCount);
        Assert.True(reopened.IsLocked("PROD", "ETCH-7"));

        // And the lock is worth what it was worth before the restart.
        Assert.Equal(RecipeAcknowledgement.Rejected, await reopened.DeleteAsync("PROD", "ETCH-7", Token));
        Assert.Equal(RecipeAcknowledgement.Rejected, await reopened.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));

        // The token is the bytes that went out, so Matches is the whole comparison across a restart
        // as well as within one - and the format and the count come back null, which costs nothing
        // because nothing in this package looks inside a token.
        Assert.Equal(RecipeAcknowledgement.Rejected, await reopened.UnlockAsync("PROD", "ETCH-7", RecipeLockToken.FromText("LOCK-1"), Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await reopened.UnlockAsync("PROD", "ETCH-7", token, Token));
        Assert.Equal(0, reopened.LockedCount);

        Assert.Equal(0, new RecipeFileStore(temporary.Path).LockedCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_token_minted_after_a_restart_is_not_one_that_is_already_held()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        await SeedAsync(store, "PROD", "ETCH-8", Body);

        var (_, first) = await store.LockAsync("PROD", "ETCH-7", Token);

        // The counter starts again with the process where the locks do not, so a token of a number
        // alone would collide with one a host is still holding - and whoever held the first could
        // then release the second. The recipe in the name is what stops the pair repeating.
        var (_, second) = await new RecipeFileStore(temporary.Path).LockAsync("PROD", "ETCH-8", Token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.Matches(second));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_selection_survives_and_a_recipe_deleted_underneath_it_does_not()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SelectAsync("PROD", "ETCH-7", Token));

        var reopened = new RecipeFileStore(temporary.Path);

        Assert.Equal(("PROD", "ETCH-7"), reopened.SelectedId);
        Assert.Equal(("ETCH-7", (RecipeVersion?)null), await reopened.CurrentAsync("PROD", Token));
        Assert.Null(await reopened.CurrentAsync("TEST", Token));

        // The record goes without the store being told, which is a disk somebody has been in. A
        // selection naming a recipe that is not there is dropped rather than reported - Restore's
        // rule in the packages that have one, the judgement being in the reading.
        File.Delete(Records(temporary.Path)[0]);

        var third = new RecipeFileStore(temporary.Path);

        Assert.Null(third.SelectedId);
        Assert.Null(await third.CurrentAsync("PROD", Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_takes_the_selection_with_it_and_a_delete_clears_it()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SelectAsync("PROD", "ETCH-7", Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));

        // The in-memory store's two rules, and the small file has to agree with the index or a
        // restart would report a selection the machine is not holding.
        Assert.Equal(("ETCH-8", (RecipeVersion?)null), await new RecipeFileStore(temporary.Path).CurrentAsync("PROD", Token));

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteAsync("PROD", "ETCH-8", Token));

        // Asserted on the store that is open and not only on the next one, because the adoption
        // guard would otherwise hide it: a selection naming a recipe that is not on the disk is
        // dropped on the way in, so a delete that cleared nothing would still come back null from
        // a restart. A control found this - the durability assertion was doing both jobs and
        // therefore neither.
        Assert.Null(store.SelectedId);
        Assert.Null(await store.CurrentAsync("PROD", Token));

        Assert.Null(new RecipeFileStore(temporary.Path).SelectedId);
    }

    // ----------------------------------------------------------------- a directory somebody has been in

    [Fact(Timeout = 15_000)]
    public async Task An_unfinished_write_is_swept_and_nothing_is_adopted_from_it()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        // What a power cut during a write leaves: the rename never happened, so nothing was ever
        // told the recipe arrived.
        var partial = RecordPath(temporary.Path, 9) + RecipeFileStore.PartialExtension;
        File.Copy(Records(temporary.Path)[0], partial);

        var logger = new RecordingLogger<RecipeFileStore>();
        var reopened = new RecipeFileStore(temporary.Path, logger);

        Assert.Equal(1, reopened.Count);
        Assert.False(File.Exists(partial));
        Assert.Contains("1 unfinished write(s) removed", Assert.Single(logger.From(LogLevel.Information)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_this_version_cannot_read_is_left_alone_rather_than_deleted()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        var bytes = await File.ReadAllBytesAsync(Records(temporary.Path)[0], Token);
        bytes[0] = 99;

        var future = RecordPath(temporary.Path, 42);
        await File.WriteAllBytesAsync(future, bytes, Token);

        var logger = new RecordingLogger<RecipeFileStore>();
        var reopened = new RecipeFileStore(temporary.Path, logger);

        // A downgrade should not throw away a fab's recipes, so the record stays where it is and
        // the count says one rather than two.
        Assert.Equal(1, reopened.Count);
        Assert.True(File.Exists(future));
        Assert.Contains("1 unreadable record(s) left alone", Assert.Single(logger.From(LogLevel.Information)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_file_whose_name_is_not_a_sequence_number_is_not_this_stores_and_is_not_counted()
    {
        using var temporary = new TempDirectory();

        await SeedAsync(new RecipeFileStore(temporary.Path), "PROD", "ETCH-7", Body);

        var theirs = Path.Combine(temporary.Path, "ETCH-7" + RecipeFileStore.RecordExtension);
        File.Copy(Records(temporary.Path)[0], theirs);

        var logger = new RecordingLogger<RecipeFileStore>();
        var reopened = new RecipeFileStore(temporary.Path, logger);

        // Not adopted and not called unreadable either: this store did not write it, so it is not
        // a record of this store's that went wrong.
        Assert.Equal(1, reopened.Count);
        Assert.True(File.Exists(theirs));
        Assert.Contains("0 unreadable record(s) left alone", Assert.Single(logger.From(LogLevel.Information)));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_truncated_record_is_refused_rather_than_handed_over_short()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        var path = Records(temporary.Path)[0];
        var bytes = await File.ReadAllBytesAsync(path, Token);
        await File.WriteAllBytesAsync(path, bytes[..^4], Token);

        var logger = new RecordingLogger<RecipeFileStore>();
        var reading = new RecipeFileStore(temporary.Path, logger);

        // The body's length is in the header as well as implied by the file's, which is what makes
        // this a refusal instead of a shorter recipe. S15F7's own LENGTH makes the same check on
        // the wire; this makes it again against the disk.
        Assert.Equal(0, reading.Count);
        Assert.Equal(RecipeAcknowledgement.Rejected, (await reading.LoadAsync("PROD", "ETCH-7", Token)).Acknowledgement);

        // And the index of the store that was already open still has it, so the refusal comes with
        // a line saying which of S15F10's two identical refusals this is.
        Assert.Equal(RecipeAcknowledgement.Rejected, (await store.LoadAsync("PROD", "ETCH-7", Token)).Acknowledgement);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_load_that_cannot_read_its_record_says_so_where_the_reply_cannot()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<RecipeFileStore>();
        var store = new RecipeFileStore(temporary.Path, logger);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        File.Delete(Records(temporary.Path)[0]);

        Assert.Equal(RecipeAcknowledgement.Rejected, (await store.LoadAsync("PROD", "ETCH-7", Token)).Acknowledgement);

        Assert.Contains(
            logger.From(LogLevel.Warning),
            line => line.Contains("PROD", StringComparison.Ordinal) && line.Contains("ETCH-7", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task Two_records_for_one_recipe_keep_the_later_one()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        // This store cannot produce two - a rewrite is one file - so it is somebody having copied
        // files in. Keeping both would make the answer depend on the order the file system
        // happened to enumerate them in, which is not an answer at all.
        var first = Records(temporary.Path)[0];
        await File.WriteAllBytesAsync(
            RecordPath(temporary.Path, 5),
            await File.ReadAllBytesAsync(first, Token),
            Token);

        var logger = new RecordingLogger<RecipeFileStore>();
        var reopened = new RecipeFileStore(temporary.Path, logger);

        Assert.Equal(1, reopened.Count);
        Assert.Single(Records(temporary.Path));
        Assert.Equal(RecordPath(temporary.Path, 5), Records(temporary.Path)[0]);
        Assert.Contains("Two records hold", Assert.Single(logger.From(LogLevel.Warning)));
    }

    // ----------------------------------------------------------------- the rules it shares

    [Fact(Timeout = 15_000)]
    public async Task The_rules_the_in_memory_store_makes_up_are_made_here_too()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        // A namespace that does not exist takes no recipe.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
                Token));

        await SeedAsync(store, "PROD", "ETCH-7", Body);

        // A namespace that still holds a recipe is not deleted, and is once it does not.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.DeleteNamespaceAsync("PROD", Token));

        // A declared length that does not match what arrives is refused, and the grant is spent
        // either way - this store's SaveAsync keeps the grant on a mismatch, as the in-memory one
        // does, so the host can send the right bytes under it.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-9", Length = 4 },
                Token));
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.SaveAsync("PROD", "ETCH-9", Body, Token));
        Assert.Equal(1, store.Count);

        // A name that is taken is not written over, by a rename or by a copy or by a namespace
        // create.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.CreateNamespaceAsync("PROD", Token));
        await SeedAsync(store, "PROD", "ETCH-8", Other);
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", Token));
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.CopyAsync("PROD", "ETCH-7", "PROD", "ETCH-8", Token));
        Assert.Equal(Other, (await store.LoadAsync("PROD", "ETCH-8", Token)).Body.ToArray());

        // An abort forgets a grant and keeps what is stored; a delete needs the recipe to be there.
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.AbortAsync("PROD", "ETCH-9", Token));
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.AbortAsync("PROD", "ETCH-9", Token));
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.DeleteAsync("PROD", "NOTHING", Token));

        Assert.Equal(2, store.Count);
        Assert.Equal(2, Records(temporary.Path).Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_too_large_is_refused_at_the_request_and_nothing_is_written()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path) { MaximumLength = 8 };

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.CreateNamespaceAsync("PROD", Token));
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
                Token));

        Assert.Empty(Records(temporary.Path));

        // The limit is this machine's configuration and not anything a host said, so it is not
        // written down: the next store takes it from wherever the application sets it.
        Assert.Equal(1024u * 1024u, new RecipeFileStore(temporary.Path).MaximumLength);
    }

    [Fact(Timeout = 15_000)]
    public async Task ForgetLocksAsync_is_the_only_way_a_lock_opens_without_its_token()
    {
        using var temporary = new TempDirectory();
        var store = new RecipeFileStore(temporary.Path);

        await SeedAsync(store, "PROD", "ETCH-7", Body);
        Assert.Equal(RecipeAcknowledgement.Accepted, (await store.LockAsync("PROD", "ETCH-7", Token)).Acknowledgement);

        var logger = new RecordingLogger<RecipeFileStore>();
        var operated = new RecipeFileStore(temporary.Path, logger);

        Assert.Equal(1, await operated.ForgetLocksAsync(Token));
        Assert.Equal(0, await operated.ForgetLocksAsync(Token));
        Assert.Equal(0, operated.LockedCount);

        // A host cannot ask for this - stream 15 has no row that forces a lock open - so it is
        // logged as what it is: something nobody on the wire requested.
        Assert.Contains("forgotten by hand", Assert.Single(logger.From(LogLevel.Warning)));

        Assert.Equal(0, new RecipeFileStore(temporary.Path).LockedCount);
        Assert.Equal(RecipeAcknowledgement.Accepted, await operated.DeleteAsync("PROD", "ETCH-7", Token));
    }

    [Fact(Timeout = 15_000)]
    public void A_directory_that_is_not_there_is_made_and_an_empty_one_says_nothing()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "recipes", "deeper");

        var logger = new RecordingLogger<RecipeFileStore>();
        var store = new RecipeFileStore(path, logger);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), store.Directory);
        Assert.Equal(0, store.Count);

        // An opening line only when there was something to open: a first start on an empty
        // directory is not news, and the spool's constructor says the same.
        Assert.Empty(logger.Entries);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task A_host_downloads_to_one_process_and_uploads_from_the_next()
    {
        using var temporary = new TempDirectory();

        await using (var pair = RecipePair.Start(store: new RecipeFileStore(temporary.Path)))
        {
            await pair.OpenAsync();

            Assert.Equal(
                RecipeAcknowledgement.Accepted,
                await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

            var (grant, acknowledgement) = await pair.Host.DownloadAsync("PROD", "ETCH-7", Body, RecipePair.Token);

            Assert.Equal(RecipeAcknowledgement.Accepted, grant);
            Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);

            Assert.Equal(
                RecipeAcknowledgement.Accepted,
                await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token));
        }

        // The equipment is gone and so is the store that answered. A new one over the same
        // directory is what a machine that was switched off and on again has.
        await using var restarted = RecipePair.Start(store: new RecipeFileStore(temporary.Path));
        await restarted.OpenAsync();

        Assert.Equal(["PROD"], await restarted.Host.ListNamespacesAsync(RecipePair.Token));

        var (code, recipe) = await restarted.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, code);
        Assert.NotNull(recipe);
        Assert.Equal(Body, recipe.Body.ToArray());

        // And what the machine is set up to run is still what the host told it, which is the whole
        // argument for S15F35 being worth serving.
        Assert.Equal("ETCH-7", (await restarted.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token))?.RecipeId);
    }

    // ----------------------------------------------------------------- and in a container

    [Fact(Timeout = 15_000)]
    public async Task The_container_can_wire_a_store_over_a_directory()
    {
        using var temporary = new TempDirectory();
        await using var provider = Build(builder => builder.AddRecipeFileEquipment(temporary.Path));

        var store = Assert.IsType<RecipeFileStore>(provider.GetRequiredService<IRecipeStore>());

        Assert.Equal(temporary.Path, store.Directory);
        Assert.Same(store, provider.GetRequiredService<IRecipeStore>());

        AssertEveryRouteIsDeclared(provider);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_built_by_hand_arrives_with_the_limit_it_was_given()
    {
        using var temporary = new TempDirectory();
        var mine = new RecipeFileStore(temporary.Path) { MaximumLength = 64 };

        await using var provider = Build(builder => builder.AddRecipeEquipment(mine));

        Assert.Same(mine, provider.GetRequiredService<IRecipeStore>());

        AssertEveryRouteIsDeclared(provider);
    }

    private static ServiceProvider Build(Func<ISecsGemBuilder, ISecsGemBuilder> add)
    {
        var services = new ServiceCollection();

        add(services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false));

        return services.BuildServiceProvider();
    }

    /// <summary>The routes are the same whichever store answered, all three ways in sharing one declaration.</summary>
    private static void AssertEveryRouteIsDeclared(ServiceProvider provider)
    {
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in RecipeEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} should have a handler.");
        }

        Assert.Equal(19, RecipeEquipment.HandledMessages.Count);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-recipes-" + Guid.NewGuid().ToString("N"))).FullName;

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
