// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The four pairs that carry an undefined name - the rename, the copy, the lock and the unlock.
/// </summary>
/// <remarks>
/// <para>
/// These were refused for six chapters, and the reason was the rival-spelling measure: the names
/// they reach for are spellings of definitions nothing uses, so a library picking one would be
/// correcting the source. What the measure also records, and nobody had read, is whether the
/// definition being rivalled is one the <em>messages</em> use - and in all three of the rename's
/// and the copy's cases it is, sitting in the same body two positions earlier. The measurements are
/// in <c>SecsCatalogStream15Tests</c>; this file is what they let the package do.
/// </para>
/// <para>
/// <strong>The lock is a different argument again.</strong> <c>RCPLOCKID</c> is a spelling of
/// nothing at all, so it is carried as the item that arrived - and that is lossless here because
/// the machine mints the token and the machine reads it back. The test that matters for the whole
/// design is <see cref="The_token_goes_back_out_as_the_item_it_arrived_in"/>: a machine that mints
/// a number gets a number back.
/// </para>
/// </remarks>
public sealed class RecipeMutationTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\n");

    // ------------------------------------------------------------------- the rename

    [Fact(Timeout = 15_000)]
    public async Task A_rename_moves_the_recipe_and_leaves_the_old_name_gone()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token));

        Assert.Null(pair.Store.Find("PROD", "ETCH-7"));
        Assert.Equal(Body, pair.Store.Find("PROD", "ETCH-8")!.Body.ToArray());
        Assert.Equal(1, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_onto_a_name_that_is_taken_is_refused_and_changes_nothing()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("PROD", "ETCH-8", new byte[] { 1, 2, 3 });

        await pair.OpenAsync();

        // Refused rather than overwriting, because a host that got an acceptance would believe
        // both recipes still existed. S15F20 has one refusal code, so the store is where the
        // reason can be pinned - which is why this is tested against the store and not the wire.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token));

        Assert.Equal(Body, pair.Store.Find("PROD", "ETCH-7")!.Body.ToArray());
        Assert.Equal<byte[]>([1, 2, 3], pair.Store.Find("PROD", "ETCH-8")!.Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_to_the_name_it_already_has_is_refused_by_the_same_rule()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // No special case in the store for this: the name is taken, by the recipe itself. Worth a
        // test because the alternative - an acceptance meaning "nothing to do" - would be the only
        // place in this package where an acceptance reported no change.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-7", RecipePair.Token));

        Assert.Equal(1, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_of_a_recipe_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();

        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token));

        Assert.Equal(0, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_selection_follows_a_rename_where_a_delete_clears_it()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token);
        await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token);

        // The machine is still set up to run the same bytes, so reporting nothing selected would
        // be false - which is the opposite of what a delete does, and the pair of rules is the
        // point. Nothing in stream 15 reports what is selected, so this is only visible in the
        // store: S15F35/F36 would say, and it is one of the fourteen pairs left out.
        Assert.Equal("ETCH-8", pair.Store.Selected!.Id);
        Assert.Equal(Body, pair.Store.Selected.Body.ToArray());

        await pair.Host.DeleteAsync("PROD", "ETCH-8", RecipePair.Token);

        Assert.Null(pair.Store.Selected);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_rename_goes_out_as_three_text_items_in_the_order_the_row_gives()
    {
        SecsFormat?[] formats = [null, null, null];
        string[] values = ["", "", ""];

        await using var pair = RecipePair.Start(answerEquipment: transaction =>
        {
            if (transaction.F == 19 && transaction.Message.Item is { Count: 3 } sent)
            {
                for (var index = 0; index < 3; index++)
                {
                    formats[index] = sent.Items[index].Format;
                    values[index] = sent.Items[index].GetString();
                }
            }

            return new SecsMessage(15, 20, replyExpected: false)
            {
                Item = SecsItem.B(0).Named("RMACK"),
            };
        });

        await pair.OpenAsync();

        await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token);

        // RCPIDNEW is an A[n] on both of its readings - it extends the RCPID beside it, and it is
        // an anagram of the unused RCPNEWID - so this is the one assertion that the rivalry put no
        // byte in doubt. Pinned on the wire because the reader accepts a number as its digits, so
        // a round trip could not tell an A from a U4.
        Assert.Equal<SecsFormat?[]>([SecsFormat.ASCII, SecsFormat.ASCII, SecsFormat.ASCII], formats);
        Assert.Equal<string[]>(["PROD", "ETCH-7", "ETCH-8"], values);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_body_of_two_items_is_refused_rather_than_read_as_a_rename_to_nothing()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // Two items is the delete's body, and five rows of this stream carry it - so a reader that
        // tolerated two here would rename to an empty identifier on a message that meant something
        // else entirely.
        using var primary = new SecsMessage(15, 19, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(20, reply.F);
        Assert.Equal(1, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(Body, pair.Store.Find("PROD", "ETCH-7")!.Body.ToArray());

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F19", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_with_an_empty_new_name_is_refused_by_the_reader_and_logged()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // Three items with nothing in the third. The store would refuse this too, so the thing
        // being tested is WHICH end refused: the reader, which logs that it could not read the
        // body, where the store would answer the same RMACK silently. One refusal code means the
        // log is the only place the two are distinguishable.
        using var primary = new SecsMessage(15, 19, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID"),
                SecsItem.A(string.Empty).Named("RCPIDNEW")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(1, reply.Item!.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F19", StringComparison.Ordinal));
    }

    // --------------------------------------------------------------------- the copy

    [Fact(Timeout = 15_000)]
    public async Task A_copy_leaves_the_source_and_makes_the_destination()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.AddNamespace("DEV");

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-7", RecipePair.Token));

        Assert.Equal(Body, pair.Store.Find("PROD", "ETCH-7")!.Body.ToArray());
        Assert.Equal(Body, pair.Store.Find("DEV", "ETCH-7")!.Body.ToArray());
        Assert.Equal(2, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_copy_inside_one_namespace_is_allowed()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // Which is what says RCPSPECDST is a namespace and nothing stranger: the source's own
        // namespace is a legal destination, and the row carries two of them precisely so a copy
        // can cross where a rename cannot.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CopyAsync("PROD", "ETCH-7", "PROD", "ETCH-8", RecipePair.Token));

        Assert.Equal(2, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_copy_into_a_namespace_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // The rule a download already keeps, for the same reason: a recipe that landed in a
        // namespace S15F2 does not list would be present to the store and invisible to a host.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-7", RecipePair.Token));

        Assert.False(pair.Store.HasNamespace("DEV"));
        Assert.Equal(1, pair.Store.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_copy_onto_an_existing_recipe_is_refused_and_does_not_overwrite()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("DEV", "ETCH-7", new byte[] { 9, 9 });

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-7", RecipePair.Token));

        Assert.Equal<byte[]>([9, 9], pair.Store.Find("DEV", "ETCH-7")!.Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_copy_goes_out_as_the_source_pair_then_the_destination_pair()
    {
        string[] values = ["", "", "", ""];
        var count = 0;

        await using var pair = RecipePair.Start(answerEquipment: transaction =>
        {
            if (transaction.F == 21 && transaction.Message.Item is { } sent)
            {
                count = sent.Count;

                for (var index = 0; index < Math.Min(4, sent.Count); index++)
                {
                    values[index] = sent.Items[index].GetString();
                }
            }

            return new SecsMessage(15, 22, replyExpected: false)
            {
                Item = SecsItem.B(0).Named("RMACK"),
            };
        });

        await pair.OpenAsync();

        await pair.Host.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-9", RecipePair.Token);

        // Four items, and the order is the row's: both undefined names extend the defined name two
        // positions earlier, which is the whole reading. Getting the pairs the other way round
        // would copy backwards and the acknowledgement would say yes.
        Assert.Equal(4, count);
        Assert.Equal<string[]>(["PROD", "ETCH-7", "DEV", "ETCH-9"], values);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_copy_with_an_empty_destination_name_is_refused_by_the_reader_and_logged()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 21, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID"),
                SecsItem.A("DEV").Named("RCPSPECDST"),
                SecsItem.A(string.Empty).Named("RCPIDDST")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // A destination namespace may be blank, for the reason a source one may - a machine with
        // one unnamed namespace - but a destination identifier may not, because there would be
        // nothing to call the copy.
        Assert.Equal(1, reply.Item!.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F21", StringComparison.Ordinal));
    }

    // --------------------------------------------------------------------- the lock

    [Fact(Timeout = 15_000)]
    public async Task A_lock_hands_back_a_token_and_the_token_releases_it()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(token);
        Assert.False(token!.IsEmpty);
        Assert.True(pair.Store.IsLocked("PROD", "ETCH-7"));
        Assert.Equal(1, pair.Store.LockedCount);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.UnlockAsync("PROD", "ETCH-7", token, RecipePair.Token));

        Assert.False(pair.Store.IsLocked("PROD", "ETCH-7"));
        Assert.Equal(0, pair.Store.LockedCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_lock_on_a_locked_recipe_is_refused_and_carries_no_token()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // Refused rather than handed the token again, because S15F23 says nowhere who is asking -
        // so a second grant would let the second asker release the first's lock.
        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(token);
        Assert.Equal(1, pair.Store.LockedCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_lock_on_a_recipe_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();

        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(token);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unlock_with_the_wrong_token_is_refused_and_the_lock_stays()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("PROD", "ETCH-8", Body);

        await pair.OpenAsync();

        var (_, mine) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);
        var (_, other) = await pair.Host.LockAsync("PROD", "ETCH-8", RecipePair.Token);

        Assert.False(mine!.Matches(other));

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.UnlockAsync("PROD", "ETCH-7", other!, RecipePair.Token));

        Assert.True(pair.Store.IsLocked("PROD", "ETCH-7"));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unlock_of_a_recipe_nobody_locked_is_refused()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("PROD", "ETCH-8", Body);

        await pair.OpenAsync();

        var (_, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // One refusal code for "not locked" and for "not your lock", because S15F26 carries one
        // RMACK and a second answer would be a vocabulary this library invented.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.UnlockAsync("PROD", "ETCH-8", token!, RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_locked_recipe_is_not_deleted_and_is_not_sent_over()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        var (_, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // E5 says a lock exists and never says what it prevents, so all three of these are the
        // store's rules. A lock that stopped none of them would be a lock in name only.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token));

        var (grant, acknowledgement) = await pair.Host.DownloadAsync(
            "PROD",
            "ETCH-7",
            new byte[] { 4, 5, 6 },
            RecipePair.Token);

        // Refused at the grant, which is the cheap place and the whole purpose of S15F5 - so no
        // second transaction happened at all.
        Assert.Equal(RecipeAcknowledgement.Rejected, grant);
        Assert.Null(acknowledgement);

        Assert.Equal(Body, pair.Store.Find("PROD", "ETCH-7")!.Body.ToArray());

        await pair.Host.UnlockAsync("PROD", "ETCH-7", token!, RecipePair.Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_lock_taken_between_the_grant_and_the_send_stops_the_recipe()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 3 },
                RecipePair.Token));

        var (_, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // The lock is checked at the send as well as at the grant, for the reason the namespace
        // is: two transactions mean the machine's state can change in between, and S15F23 could
        // have arrived in the gap.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SendAsync("PROD", "ETCH-7", new byte[] { 7, 8, 9 }, RecipePair.Token));

        await pair.Host.UnlockAsync("PROD", "ETCH-7", token!, RecipePair.Token);

        // And the grant is still there, which is where this differs from the namespace check: a
        // lock is temporary, so a host that releases it can send under the grant it already has.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SendAsync("PROD", "ETCH-7", new byte[] { 7, 8, 9 }, RecipePair.Token));

        Assert.Equal<byte[]>([7, 8, 9], pair.Store.Find("PROD", "ETCH-7")!.Body.ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_machine_that_refuses_a_lock_and_sends_a_token_anyway_is_read_as_no_lock()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 24, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.B(1).Named("RMACK"),
                SecsItem.A("LOCK-X").Named("RCPLOCKID")),
        });

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // The other half of the suppression this package's own machine does: a host must not keep
        // a token that came with a refusal either, because the next S15F25 would present a lock
        // nobody granted. Only a machine of somebody else's can produce this, which is why it is
        // played by hand.
        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(token);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_locked_recipe_is_still_uploaded_and_still_copied()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.AddNamespace("DEV");

        await pair.OpenAsync();

        await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // A lock stops a recipe being changed and not being read, which is the line the store
        // draws and the reason it is worth a test: nothing in the reference draws it.
        var (acknowledgement, recipe) = await pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.Equal(Body, recipe!.Body.ToArray());

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-7", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_refused_lock_puts_the_code_and_an_empty_item_on_the_wire()
    {
        var count = 0;
        SecsFormat? second = null;
        var secondCount = -1;

        await using var pair = RecipePair.Start();

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 23, replyExpected: true)
        {
            Item = RecipeCodecProbe.Name("PROD", "ETCH-7"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        count = reply.Item!.Count;
        second = reply.Item.Items[1].Format;
        secondCount = reply.Item.Items[1].Count;

        // S15F10's shape exactly - a code and then a payload - which is what settled the nesting
        // for this row. An empty list rather than a missing item, so a host reading position one
        // finds something there and reads no lock from it.
        Assert.Equal(2, count);
        Assert.Equal(1, reply.Item.Items[0].FirstValueOrDefault<byte>());
        Assert.Equal(SecsFormat.List, second);
        Assert.Equal(0, secondCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_token_goes_back_out_as_the_item_it_arrived_in()
    {
        SecsFormat? format = null;
        uint value = 0;

        await using var pair = RecipePair.Start(answerEquipment: transaction => transaction.F switch
        {
            23 => new SecsMessage(15, 24, replyExpected: false)
            {
                // A machine that mints a number, which the in-memory store never does. Nothing in
                // the reference says a lock identifier is text, so this is as legal as any other
                // item - and a library that had read a format off a sibling would turn it into
                // "90210" and hand the machine back something it never issued.
                Item = SecsItem.L(
                    SecsItem.B(0).Named("RMACK"),
                    SecsItem.U4(90210).Named("RCPLOCKID")),
            },
            _ => Captured(transaction),
        });

        await pair.OpenAsync();

        var (_, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(SecsFormat.U4, token!.Format);
        Assert.Equal(1, token.Count);

        await pair.Host.UnlockAsync("PROD", "ETCH-7", token, RecipePair.Token);

        Assert.Equal(SecsFormat.U4, format);
        Assert.Equal(90210u, value);

        SecsMessage Captured(SecsTransaction transaction)
        {
            if (transaction.F == 25 && transaction.Message.Item is { Count: 3 } sent)
            {
                format = sent.Items[2].Format;
                value = sent.Items[2].FirstValueOrDefault<uint>();
            }

            return new SecsMessage(15, 26, replyExpected: false)
            {
                Item = SecsItem.B(0).Named("RMACK"),
            };
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task A_machine_that_accepts_a_lock_and_sends_no_token_is_logged_at_the_host()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 24, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.B(0).Named("RMACK"),
                SecsItem.L().Named("RCPLOCKID")),
        });

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        // The acknowledgement is reported as it arrived - this library does not overrule a machine
        // - but the host has a lock it cannot release and nothing on the wire says so, so the log
        // is the only place it can be said.
        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.Null(token);

        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("no RCPLOCKID", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_accepts_a_lock_and_mints_no_token_is_logged_at_the_machine()
    {
        await using var pair = RecipePair.Start(store: new AgreeableStore());

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.Null(token);

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("minted no token", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_refuses_a_lock_and_mints_one_anyway_does_not_put_it_on_the_wire()
    {
        await using var pair = RecipePair.Start(store: new ContraryStore());

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 23, replyExpected: true)
        {
            Item = RecipeCodecProbe.Name("PROD", "ETCH-7"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // **Read off the wire and not through this package's host**, which is the point: the host
        // discards a token that came with a refusal too, and a test that went through it would
        // pass whether or not this end suppressed anything. A negative control found exactly that.
        // Both ends have to do it, because either one can be talking to somebody else's.
        Assert.Equal(1, reply.Item!.Items[0].FirstValueOrDefault<byte>());
        Assert.Equal(SecsFormat.List, reply.Item.Items[1].Format);
        Assert.Equal(0, reply.Item.Items[1].Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_lock_reply_with_no_acknowledgement_throws()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 24, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.B().Named("RMACK")),
        });

        await pair.OpenAsync();

        // An RMACK with no value in it has said nothing, and zero means accepted here - so falling
        // through to a default would turn silence into a lock.
        var thrown = await Assert.ThrowsAsync<SecsException>(
            async () => await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Contains("S15F24", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- the token

    [Fact(Timeout = 15_000)]
    public void A_token_matches_only_the_same_bytes()
    {
        var mine = RecipeLockToken.FromText("LOCK-1");

        Assert.True(mine.Matches(RecipeLockToken.FromText("LOCK-1")));
        Assert.False(mine.Matches(RecipeLockToken.FromText("LOCK-2")));
        Assert.False(mine.Matches(null));

        // The bytes are the whole comparison because there is no defined format to canonicalise
        // against. A token read out and re-encoded in another format does not match, and that is
        // the right answer rather than a shortcoming: nothing in the reference says the two mean
        // the same thing.
        using var number = SecsItem.U4(1);

        Assert.False(RecipeLockToken.From(number).Matches(RecipeLockToken.FromText("1")));
    }

    [Fact(Timeout = 15_000)]
    public void A_token_wrapped_from_bytes_reports_what_the_caller_said_and_nothing_more()
    {
        var minted = RecipeLockToken.FromText("LOCK-1");

        // The way back in for a store that wrote its locks to disk. Neither header is read off the
        // bytes, because reading them would mean decoding an item this method exists to avoid
        // touching - so a caller who knows says so, and one who does not leaves both unknown.
        var wrapped = RecipeLockToken.FromEncoded(minted.Encoded);

        Assert.Null(wrapped.Format);
        Assert.Null(wrapped.Count);
        Assert.False(wrapped.IsEmpty);
        Assert.True(wrapped.Matches(minted));

        using var decoded = wrapped.Decode();

        Assert.Equal(SecsFormat.ASCII, decoded!.Format);
        Assert.Equal("LOCK-1", decoded.GetString());

        // An empty token has no bytes at all, which is not the same as an item carrying no values:
        // that one still has a format byte and a length byte in front of it.
        var nothing = RecipeLockToken.FromEncoded(default);

        Assert.True(nothing.IsEmpty);
        Assert.Null(nothing.Decode());

        using var blank = SecsItem.A(string.Empty);

        Assert.True(RecipeLockToken.From(blank).IsEmpty);
        Assert.False(RecipeLockToken.From(blank).Encoded.IsEmpty);
    }

    // ----------------------------------------------------- the four defaulted members

    [Fact(Timeout = 15_000)]
    public async Task A_store_written_against_the_nine_members_still_compiles_and_refuses_the_four()
    {
        IRecipeStore store = new NineMemberStore();

        // The four that arrived after this interface shipped are default implementations, so a
        // store written against the first nine is not broken by them - and the default is the
        // refusal E5 has a code for, rather than an exception, because a machine that will not
        // rename a recipe is saying no and not failing.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.CopyAsync("PROD", "ETCH-7", "DEV", "ETCH-7", RecipePair.Token));

        var (acknowledgement, token) = await store.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(token);

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await store.UnlockAsync("PROD", "ETCH-7", RecipeLockToken.FromText("LOCK-1"), RecipePair.Token));
    }

    /// <summary>The body five rows of this stream share, built here because the codec is internal.</summary>
    private static class RecipeCodecProbe
    {
        internal static SecsItem Name(string recipeNamespace, string recipeId)
            => SecsItem.L(
                SecsItem.A(recipeNamespace).Named("RCPSPEC"),
                SecsItem.A(recipeId).Named("RCPID"));
    }

    /// <summary>A store with only the nine members the interface shipped with.</summary>
    private sealed class NineMemberStore : IRecipeStore
    {
        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(RecipeDownloadRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SaveAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult((RecipeAcknowledgement.Rejected, default(ReadOnlyMemory<byte>)));

        public ValueTask<RecipeAcknowledgement> DeleteAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SelectAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> AbortAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);
    }

    /// <summary>A store that accepts a lock and mints nothing, which the contract forbids.</summary>
    private sealed class AgreeableStore : NineMemberStoreBase
    {
        public override ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<(RecipeAcknowledgement, RecipeLockToken?)>((RecipeAcknowledgement.Accepted, null));
    }

    /// <summary>A store that refuses a lock and mints one anyway.</summary>
    private sealed class ContraryStore : NineMemberStoreBase
    {
        public override ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<(RecipeAcknowledgement, RecipeLockToken?)>(
                (RecipeAcknowledgement.Rejected, RecipeLockToken.FromText("LOCK-X")));
    }

    /// <summary>Refuses everything, so a derived store only writes the member under test.</summary>
    private abstract class NineMemberStoreBase : IRecipeStore
    {
        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(RecipeDownloadRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SaveAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult((RecipeAcknowledgement.Rejected, default(ReadOnlyMemory<byte>)));

        public ValueTask<RecipeAcknowledgement> DeleteAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SelectAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> AbortAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public abstract ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken);
    }
}
