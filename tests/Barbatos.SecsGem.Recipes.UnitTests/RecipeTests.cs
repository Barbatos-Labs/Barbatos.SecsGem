// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The nine pairs, through a real host and a real equipment over one link.
/// </summary>
public class RecipeTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\nSTEP 2: etch 40s\n");

    [Fact]
    public async Task The_namespaces_come_back_in_order()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");
        pair.Store.AddNamespace("DEV");

        await pair.OpenAsync();

        // Ordered so a host can diff two answers. The identifiers are the two ends' own business -
        // RCPSPEC has no vocabulary in the reference at all.
        Assert.Equal<string[]>(
            ["DEV", "PROD"],
            [.. await pair.Host.ListNamespacesAsync(RecipePair.Token)]);
    }

    [Fact]
    public async Task A_machine_with_no_namespaces_answers_with_none()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListNamespacesAsync(RecipePair.Token));
    }

    [Fact]
    public async Task A_namespace_is_created_and_deleted()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

        Assert.True(pair.Store.HasNamespace("PROD"));

        // Creating one twice is refused, because a host that got an acceptance would believe it
        // had an empty namespace and the next thing it does may be to fill one that is not.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteNamespaceAsync("PROD", RecipePair.Token));

        Assert.False(pair.Store.HasNamespace("PROD"));
    }

    [Fact]
    public async Task A_namespace_that_still_holds_recipes_is_not_deleted()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // The guard that matters most in this package: S15F17 names no recipe, so one message
        // could take a whole directory with it. This store's choice, not E5's - S15F18 has one
        // refusal code and no way to explain itself, so the host has to delete the recipes first.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.DeleteNamespaceAsync("PROD", RecipePair.Token));

        Assert.NotNull(pair.Store.Find("PROD", "ETCH-7"));

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteNamespaceAsync("PROD", RecipePair.Token));
    }

    [Fact]
    public async Task A_recipe_crosses_unchanged_once_it_has_been_granted()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        var grant = await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
            RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, grant);
        Assert.Equal(1, pair.Store.GrantedCount);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SendAsync("PROD", "ETCH-7", Body, RecipePair.Token));

        // Plain bytes, and the same bytes. PPBODY is stream 7's own defined B, so a recipe is the
        // shape a control program is and not the shape a data set or a transferred item is.
        var held = pair.Store.Find("PROD", "ETCH-7");

        Assert.NotNull(held);
        Assert.Equal<byte[]>(Body, [.. held!.Body.Span]);
        Assert.Equal(0, pair.Store.GrantedCount);
    }

    [Fact]
    public async Task The_two_transactions_can_be_run_as_one()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        var (grant, acknowledgement) = await pair.Host.DownloadAsync("PROD", "ETCH-7", Body, RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, grant);
        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(pair.Store.Find("PROD", "ETCH-7"));
    }

    [Fact]
    public async Task A_refused_grant_means_there_is_no_second_acknowledgement()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        // No namespace, so the request is refused - and there was no S15F7, so there is no second
        // RMACK to report. Null rather than a guess at one.
        var (grant, acknowledgement) = await pair.Host.DownloadAsync("NOPE", "ETCH-7", Body, RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, grant);
        Assert.Null(acknowledgement);
        Assert.Equal(0, pair.Store.Count);
    }

    [Fact]
    public async Task A_recipe_for_a_namespace_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        // Stream 15 is the only stream of the four transfer streams here with a two-level space,
        // so it is the only one that can refuse for this reason at all.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "NOPE", Id = "ETCH-7", Length = 4 },
                RecipePair.Token));

        Assert.Equal(0, pair.Store.GrantedCount);
    }

    [Fact]
    public async Task A_send_nobody_granted_is_refused()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SendAsync("PROD", "ETCH-7", Body, RecipePair.Token));

        Assert.Equal(0, pair.Store.Count);
    }

    [Fact]
    public async Task A_recipe_that_is_not_the_length_it_declared_is_refused()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 4 },
            RecipePair.Token);

        // The check streams 13 and 21 cannot make: PPBODY is a B and therefore has a byte count,
        // where DSDATA and ITEMDATA have no format to measure. Barbatos.SecsGem.ControlPrograms
        // makes the same check for the same reason.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SendAsync("PROD", "ETCH-7", Body, RecipePair.Token));

        Assert.Equal(0, pair.Store.Count);
    }

    [Fact]
    public async Task A_recipe_larger_than_the_store_takes_is_refused_before_it_is_sent()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");
        pair.Store.MaximumLength = 8;

        await pair.OpenAsync();

        // Which is the whole point of S15F5 existing: refused on the declared length, before the
        // bytes cross. Stream 15's own way of reporting space - S15F39/F40 - needs SPACEAVAIL and
        // SPACETOTAL, both undefined, so a host cannot ask how much there is.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 4096 },
                RecipePair.Token));
    }

    [Fact]
    public async Task A_recipe_comes_back_with_an_acknowledgement_of_its_own()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        var (acknowledgement, recipe) = await pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(recipe);
        Assert.Equal<byte[]>(Body, [.. recipe!.Body.Span]);
        Assert.Equal("PROD/ETCH-7 (35 bytes)", recipe.ToString());
    }

    [Fact]
    public async Task A_recipe_the_machine_has_not_got_is_refused_and_not_guessed_at()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        // The contrast with stream 8 that earns this package its upload: S8F6 carries no code, so
        // a control program the machine does not have looks exactly like a present one with an
        // empty body - and Barbatos.SecsGem.ControlPrograms has to advise asking the revision
        // first. S15F10 carries RMACK and PPBODY together, so the machine simply says no.
        var (acknowledgement, recipe) = await pair.Host.UploadAsync("PROD", "NOPE", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(recipe);
    }

    [Fact]
    public async Task A_recipe_is_selected_and_the_selection_survives_the_reply()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Equal("ETCH-7", pair.Store.Selected?.Id);

        // Selecting is not running. Stream 15 has no message that starts a process, and S15F33 -
        // which would say whether one is running - is out, because RCPSTATE is undefined while the
        // dictionary's own RCPSTAT goes unused by any message.
        Assert.Equal(1, pair.Store.Count);
    }

    [Fact]
    public async Task Selecting_a_recipe_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SelectAsync("PROD", "NOPE", RecipePair.Token));

        Assert.Null(pair.Store.Selected);
    }

    [Fact]
    public async Task Deleting_the_selected_recipe_leaves_nothing_selected()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        // Because a host reading Selected afterwards would otherwise be told the machine is set up
        // to run something it no longer has.
        Assert.Null(pair.Store.Selected);
    }

    [Fact]
    public async Task Deleting_a_recipe_twice_is_refused_the_second_time()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));
    }

    [Fact]
    public async Task An_abort_forgets_the_grant_and_keeps_what_is_stored()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 4 },
            RecipePair.Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.AbortAsync("PROD", "ETCH-7", RecipePair.Token));

        // Aborting is not deleting: S15F13 deletes a recipe and S15F17 deletes a namespace, and an
        // abort that did either would be doing their job.
        Assert.Equal(0, pair.Store.GrantedCount);
        Assert.NotNull(pair.Store.Find("PROD", "ETCH-7"));

        // And the transfer it forgot can no longer be completed.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SendAsync("PROD", "ETCH-7", new byte[4], RecipePair.Token));
    }

    [Fact]
    public async Task An_abort_with_nothing_in_flight_is_refused()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.AbortAsync("PROD", "ETCH-7", RecipePair.Token));
    }

    [Fact]
    public async Task The_host_sends_every_primary_the_equipment_declares()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.ListNamespacesAsync(RecipePair.Token);
        await pair.Host.CreateNamespaceAsync("DEV", RecipePair.Token);
        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "DEV", Id = "NEW", Length = (uint)Body.Length },
            RecipePair.Token);
        await pair.Host.SendAsync("DEV", "NEW", Body, RecipePair.Token);
        await pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token);
        await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token);
        await pair.Host.AbortAsync("PROD", "ETCH-7", RecipePair.Token);
        await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token);
        await pair.Host.DeleteNamespaceAsync("PROD", RecipePair.Token);

        // "Every" asserted rather than claimed: nine methods, nine declared primaries, and the
        // host half declares none of its own.
        Assert.Equal(
            [.. RecipeEquipment.HandledMessages.Order()],
            pair.Asked.Distinct().Order());
    }

    [Fact]
    public void The_equipment_declares_nine_of_the_twenty_seven_pairs()
    {
        Assert.Equal(9, RecipeEquipment.HandledMessages.Count);

        // The nine whose primary and reply are made entirely of defined items. Measured in
        // SecsCatalogStream15Tests, not asserted from memory.
        Assert.Equal<byte[]>(
            [1, 5, 7, 9, 13, 15, 17, 31, 53],
            [.. RecipeEquipment.HandledMessages.Select(pair => pair.Function)]);

        Assert.All(RecipeEquipment.HandledMessages, pair => Assert.Equal(15, pair.Stream));

        // And the host declares nothing at all, because stream 15 runs one way.
        Assert.Empty(RecipeHost.HandledMessages);
    }

    [Fact]
    public void The_acknowledgement_is_quoted_rather_than_inferred()
    {
        // Three members, and the third is honest here where ItemAcknowledgement's absence of one
        // was honest there: RMACK has a table, and that table names a reserved range, so a code
        // this library cannot interpret is a distinction the source makes.
        Assert.Equal(3, Enum.GetValues<RecipeAcknowledgement>().Length);
        Assert.Equal(0, (byte)RecipeAcknowledgement.Accepted);
        Assert.Equal(1, (byte)RecipeAcknowledgement.Rejected);

        // Two, because that is where the reserved range starts - not an arbitrary sentinel.
        Assert.Equal(2, (byte)RecipeAcknowledgement.Other);
    }

    [Fact]
    public void A_recipe_describes_itself_for_a_log()
    {
        Assert.Equal(
            "PROD/ETCH-7 (35 bytes)",
            new Recipe { Namespace = "PROD", Id = "ETCH-7", Body = Body }.ToString());

        // "bytes" is honest here, unlike in stream 21's Item: PPBODY is a B, so the body has a
        // byte count the reference accounts for.
        Assert.True(new Recipe { Namespace = "PROD", Id = "ETCH-7" }.IsEmpty);
    }

    [Fact]
    public void A_download_request_describes_itself_for_a_log()
    {
        Assert.Equal(
            "PROD/ETCH-7, 35 bytes declared",
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 35 }.ToString());
    }
}
