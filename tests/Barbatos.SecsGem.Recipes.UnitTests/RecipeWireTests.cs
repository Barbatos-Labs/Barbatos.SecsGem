// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The shapes this package tolerates but does not write, the ones it refuses, and what it puts on
/// the wire.
/// </summary>
/// <remarks>
/// <para>
/// The machine's end is played by hand where a reply has to be wrong, because two real ends can
/// only exercise what this library itself emits.
/// </para>
/// <para>
/// Every message sent by hand goes out on <c>HostLink</c>, because every primary in stream 15 is
/// the host's. The other link reaches an end with no route, comes back S9F5 and leaves the sender
/// on a T3 a fake clock never advances - a suite that hangs instead of failing, which has cost
/// this repository two chapters.
/// </para>
/// </remarks>
public sealed class RecipeWireTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\n");

    // ------------------------------------------------------- what this package writes

    [Fact(Timeout = 15_000)]
    public async Task The_recipe_body_goes_out_as_the_binary_the_dictionary_says_PPBODY_is()
    {
        SecsFormat? format = null;
        var count = 0;

        await using var pair = RecipePair.Start(answerEquipment: transaction =>
        {
            if (transaction.F == 7 && transaction.Message.Item is { Count: 3 } sent)
            {
                format = sent.Items[2].Format;
                count = sent.Items[2].Count;
            }

            return new SecsMessage(15, 8, replyExpected: false)
            {
                Item = SecsItem.B(0).Named("RMACK"),
            };
        });

        await pair.OpenAsync();

        await pair.Host.SendAsync("PROD", "ETCH-7", Body, RecipePair.Token);

        // Pinned on the wire rather than through this package's own reader, because the reader
        // accepts text as well as binary - so a round trip could not tell a B from an A. That is
        // not hypothetical: it is how the first negative control of the stream 8 chapter found
        // nothing. PPBODY is a defined B and this is the test that says so.
        Assert.Equal(SecsFormat.Binary, format);
        Assert.Equal(Body.Length, count);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_namespace_and_the_identifier_go_out_as_text()
    {
        SecsFormat? namespaceFormat = null;
        SecsFormat? idFormat = null;

        await using var pair = RecipePair.Start(answerEquipment: transaction =>
        {
            if (transaction.F == 13 && transaction.Message.Item is { Count: 2 } sent)
            {
                namespaceFormat = sent.Items[0].Format;
                idFormat = sent.Items[1].Format;
            }

            return new SecsMessage(15, 14, replyExpected: false)
            {
                Item = SecsItem.B(0).Named("RMACK"),
            };
        });

        await pair.OpenAsync();

        await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token);

        // RCPSPEC and RCPID are both defined as A[n], and both are stream 15's own vocabulary -
        // no other stream names RCPSPEC - so nothing in this repository can contradict them and
        // nothing can confirm them either. Pinned here for that reason.
        Assert.Equal(SecsFormat.ASCII, namespaceFormat);
        Assert.Equal(SecsFormat.ASCII, idFormat);
    }

    // ------------------------------------------------------------- the acknowledgement

    [Fact(Timeout = 15_000)]
    public async Task A_reserved_acknowledgement_code_reads_as_other_and_is_logged()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.B(40).Named("RMACK"),
        });

        await pair.OpenAsync();

        // The table names 2 to 63 as reserved, so 40 is something the reference anticipated and
        // did not define. It must not be cast to an enum member that does not exist, and it must
        // not read as the zero that means accepted.
        Assert.Equal(
            RecipeAcknowledgement.Other,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.Contains(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("RMACK 40", StringComparison.Ordinal)
                && message.Contains("reserves", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_named_rejection_is_not_logged()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.B(1).Named("RMACK"),
        });

        await pair.OpenAsync();

        // One is the refusal the table names, so there is nothing to report about it. The log is
        // for codes the item cannot account for.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.DoesNotContain(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("reserves", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_reply_with_no_acknowledgement_throws()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token).AsTask());

        Assert.Contains("S15F14", thrown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_acknowledgement_item_with_no_value_in_it_throws()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.B().Named("RMACK"),
        });

        await pair.OpenAsync();

        // Zero means accepted, so an item with no value in it must not fall through to one. A
        // machine that sent B,0 said nothing, and nothing is not yes. The same guard stream 21
        // needed, and here it protects a recipe write rather than a transfer.
        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token).AsTask());

        Assert.Contains("S15F14", thrown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_acknowledgement_wrapped_in_a_list_of_one_is_read()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.B(0).Named("RMACK")),
        });

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_acknowledgement_sent_as_an_unsigned_number_is_read()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 14, replyExpected: false)
        {
            Item = SecsItem.U1(1).Named("RMACK"),
        });

        await pair.OpenAsync();

        // RMACK is defined as a B[1], so a machine sending a U1 is being loose rather than meaning
        // something else.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));
    }

    // ----------------------------------------------------------------- the namespaces

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_list_sent_as_one_bare_specification_is_read()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 2, replyExpected: false)
        {
            Item = SecsItem.A("PROD").Named("RCPSPEC"),
        });

        await pair.OpenAsync();

        // A machine with one namespace not bothering to wrap it carries the same answer as a list
        // of one, and nesting is the one thing a flat item list cannot pin down.
        Assert.Equal<string[]>(["PROD"], [.. await pair.Host.ListNamespacesAsync(RecipePair.Token)]);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_list_whose_entries_are_each_wrapped_is_read()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(SecsItem.A("DEV").Named("RCPSPEC")),
                SecsItem.L(SecsItem.A("PROD").Named("RCPSPEC"))),
        });

        await pair.OpenAsync();

        Assert.Equal<string[]>(["DEV", "PROD"], [.. await pair.Host.ListNamespacesAsync(RecipePair.Token)]);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_with_no_name_is_dropped_from_the_list()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A(string.Empty).Named("RCPSPEC"),
                SecsItem.A("   ").Named("RCPSPEC"),
                SecsItem.A("PROD").Named("RCPSPEC")),
        });

        await pair.OpenAsync();

        // Blanks as well as empty: a namespace nobody can name is not one a host can then create a
        // recipe in, and keeping it would put a row in a directory that no later message can
        // address.
        Assert.Equal<string[]>(["PROD"], [.. await pair.Host.ListNamespacesAsync(RecipePair.Token)]);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_list_with_nothing_readable_in_it_is_reported()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 2, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.L(SecsItem.A("A"), SecsItem.A("B"))),
        });

        await pair.OpenAsync();

        // A list of two inside a list is neither a specification nor a wrapped one, so it is
        // dropped - and the log is the only place that can say the body was not empty.
        Assert.Empty(await pair.Host.ListNamespacesAsync(RecipePair.Token));

        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("S15F2", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_create_sent_as_a_list_of_one_is_read()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 15, replyExpected: true)
        {
            Name = "RecipeNamespaceCreateRequest",
            Item = SecsItem.L(SecsItem.A("PROD").Named("RCPSPEC")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(16, reply.F);
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.True(pair.Store.HasNamespace("PROD"));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_create_with_no_name_is_refused_and_reported()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 15, replyExpected: true)
        {
            Name = "RecipeNamespaceCreateRequest",
            Item = SecsItem.A("   ").Named("RCPSPEC"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // A namespace nobody can name is one no later message can address, so creating it would
        // put a row in S15F2's answer that nothing can then use. Refused at the codec, which is
        // the only place that can tell blank from absent - and asserted here because a store that
        // happily created an empty name would answer with the same byte.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Empty(await pair.Store.ListNamespacesAsync(RecipePair.Token));

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F15", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_upload_whose_code_cannot_be_read_throws()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 10, replyExpected: false)
        {
            // A list of two where the code should be: not absent, which the length guard already
            // catches, but present and unreadable. Zero means accepted, so this must throw rather
            // than fall through to one and hand back a recipe nobody sent.
            Item = SecsItem.L(
                SecsItem.L(SecsItem.A("A"), SecsItem.A("B")),
                SecsItem.B(1, 2, 3).Named("PPBODY")),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token).AsTask());

        Assert.Contains("S15F10", thrown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unreadable_namespace_create_is_refused_and_reported()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 15, replyExpected: true)
        {
            Name = "RecipeNamespaceCreateRequest",
            Item = SecsItem.L(SecsItem.A("A"), SecsItem.A("B")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        // The log is asserted, not just the code, and it is the only way to assert this at all:
        // RMACK has one refusal, so "I could not read that" and "there is already one of those"
        // come back as the same byte.
        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F15", StringComparison.Ordinal)
                && message.Contains("could not read", StringComparison.Ordinal));

        Assert.False(pair.Store.HasNamespace("A"));
    }

    // -------------------------------------------------------------------- the transfer

    [Fact(Timeout = 15_000)]
    public async Task An_unreadable_download_request_is_refused_and_reported()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 5, replyExpected: true)
        {
            Name = "RecipeDownloadRequest",
            Item = SecsItem.A("not a download request"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(6, reply.F);
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F5", StringComparison.Ordinal));

        Assert.Equal(0, pair.Store.GrantedCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_download_request_short_of_its_length_is_read_as_a_length_of_zero()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 5, replyExpected: true)
        {
            Name = "RecipeDownloadRequest",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // Read rather than refused, which is the opposite of the two-item download send below -
        // and the difference is what the missing field would be mistaken for. Here it is LENGTH, a
        // host's estimate at the end of a fixed three; there it is the recipe itself.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        // And a zero length then means only an empty recipe will be accepted, which is the store's
        // length check doing its job rather than a tolerance hiding it.
        Assert.Equal(1, pair.Store.GrantedCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_download_send_of_two_items_is_refused_rather_than_guessed()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        // Granted for SIX bytes, which is the length of the identifier - deliberately, because a
        // codec that read the second item as the recipe would then hand the store a six-byte body
        // and the length check would wave it through. The first version of this test declared the
        // real length, and the control that broke the codec found nothing: the store covered for
        // it. A guard between the codec and the assertion is how three controls went quiet in the
        // stream 8 chapter too.
        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 6 },
            RecipePair.Token);

        // Two items could be a name with no recipe or a recipe with no namespace. Guessing would
        // store an identifier as somebody's process.
        using var primary = new SecsMessage(15, 7, replyExpected: true)
        {
            Name = "RecipeDownloadSend",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(8, reply.F);
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Store.Find("PROD", "ETCH-7"));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_sent_as_text_is_taken()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        var text = "STEP 1: pump down";

        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)text.Length },
            RecipePair.Token);

        using var primary = new SecsMessage(15, 7, replyExpected: true)
        {
            Name = "RecipeDownloadSend",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID"),
                SecsItem.A(text).Named("PPBODY")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // PPBODY is defined as a B, so a machine writing a recipe as an A is being loose rather
        // than meaning something else - and most recipes are text anyway. Taken, and the bytes are
        // the characters.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Equal<byte[]>(
            Encoding.ASCII.GetBytes(text),
            [.. pair.Store.Find("PROD", "ETCH-7")!.Body.Span]);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_sent_as_a_list_is_refused()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        // Granted for ZERO bytes, for the same reason the two-item test above declares six: a
        // codec that accepted a list would hand the store an empty body, and a non-zero declared
        // length would make the store refuse it for the wrong reason. The refusal has to come from
        // the codec, so the length check must not be able to cover.
        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = 0 },
            RecipePair.Token);

        using var primary = new SecsMessage(15, 7, replyExpected: true)
        {
            Name = "RecipeDownloadSend",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID"),
                SecsItem.L(SecsItem.A("step")).Named("PPBODY")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // A list is the one shape a recipe body cannot be. Streams 13 and 21 carry a list happily,
        // because their payloads are whatever item arrived - this one is a B, so a list is a
        // machine sending something else entirely.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Store.Find("PROD", "ETCH-7"));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_recipe_is_a_recipe()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "EMPTY", Length = 0 },
            RecipePair.Token);

        // Declared zero and sent zero, so the store's length check passes and the recipe exists
        // with no body. A legitimate thing to send, and the store decides - not the codec.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SendAsync("PROD", "EMPTY", default, RecipePair.Token));

        Assert.True(pair.Store.Find("PROD", "EMPTY")?.IsEmpty);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_upload_refusal_need_not_carry_a_body()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 10, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.B(1).Named("RMACK")),
        });

        await pair.OpenAsync();

        // A machine that refused and did not bother with an empty PPBODY has still answered. This
        // is the pair that carries a code and data together, so the code alone is enough.
        var (acknowledgement, recipe) = await pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Rejected, acknowledgement);
        Assert.Null(recipe);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_upload_with_no_acknowledgement_throws()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 10, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.UploadAsync("PROD", "ETCH-7", RecipePair.Token).AsTask());

        Assert.Contains("S15F10", thrown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_upload_accepted_with_an_empty_body_is_an_empty_recipe()
    {
        await using var pair = RecipePair.Start(answerEquipment: _ => new SecsMessage(15, 10, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.B(0).Named("RMACK"),
                SecsItem.B().Named("PPBODY")),
        });

        await pair.OpenAsync();

        var (acknowledgement, recipe) = await pair.Host.UploadAsync("PROD", "EMPTY", RecipePair.Token);

        // Accepted and empty, which this stream can express and stream 8 cannot: there, an absent
        // program and an empty one are the same message.
        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(recipe);
        Assert.True(recipe!.IsEmpty);
    }

    // ----------------------------------------------------------------- the identifiers

    [Fact(Timeout = 15_000)]
    public async Task A_body_with_no_identifier_is_refused_and_reported()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 13, replyExpected: true)
        {
            Name = "RecipeDeleteRequest",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A(string.Empty).Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Rejected,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F13", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_body_with_an_empty_namespace_is_read()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add(string.Empty, "ETCH-7", Body);

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 13, replyExpected: true)
        {
            Name = "RecipeDeleteRequest",
            Item = SecsItem.L(
                SecsItem.A(string.Empty).Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // The identifier has to be there; the namespace may be blank, because a machine with one
        // unnamed namespace is the shape S15F1 reports as a single blank specification. Whether it
        // knows that namespace is the store's business, not the codec's.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Store.Find(string.Empty, "ETCH-7"));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_sent_as_a_number_is_read_as_its_digits()
    {
        await using var pair = RecipePair.Start();
        pair.Store.Add("7", "41", Body);

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 13, replyExpected: true)
        {
            Name = "RecipeDeleteRequest",
            Item = SecsItem.L(
                SecsItem.U4(7).Named("RCPSPEC"),
                SecsItem.U4(41).Named("RCPID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // Both items are defined as A, so a machine sending numbers is being loose rather than
        // meaning something else.
        Assert.Equal<byte?>(
            (byte)RecipeAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.Null(pair.Store.Find("7", "41"));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_request_carrying_a_body_is_still_answered()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        // S15F1 carries no items at all, so there is nothing to misread and nothing to refuse -
        // the one pair in this package with no request body. A machine that sent something anyway
        // is asking the same question.
        using var primary = new SecsMessage(15, 1, replyExpected: true)
        {
            Name = "RecipeNamespaceRequest",
            Item = SecsItem.A("ignored"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(2, reply.F);
        Assert.Equal(1, reply.Item?.Count);
    }

    // -------------------------------------------------------- outside the nineteen

    [Fact(Timeout = 15_000)]
    public async Task A_function_outside_the_nineteen_is_logged_and_left_alone()
    {
        // Only reachable if an application routes a function the package does not declare, since
        // the router answers S9F5 for anything undeclared - so the harness routes one. S15F11 is
        // the recipe verify, and it is the one refusal here with nothing left to re-read: a
        // recipe checksum is CHECKSUM in stream 15, CKSUM in stream 19 and CHKSUM in stream 21,
        // and the reference defines none of the three, so there is no rival to correct and
        // nothing to compare against. Nor is it a field that could be carried untouched, which is
        // what the producer-and-reader re-read confirmed: the machine has to RECOMPUTE a checksum
        // to answer at all, and stream 7 shows what the alternative looks like - its three verify
        // rows send the whole body and compare bytes, and all three are served here. It used to be
        // the rename, then the directory, then the owner, and all three went in.
        await using var pair = RecipePair.Start(alsoRoute: [(15, 11)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 11, replyExpected: false)
        {
            Name = "RecipeVerifyRequest",
            Item = SecsItem.A("PROD").Named("RCPSPEC"),
        };

        await pair.HostLink.PostAsync(primary, RecipePair.Token);
        await RecipePair.WaitUntilAsync(() => pair.Asked.Count > 0, "the machine to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await RecipePair.WaitUntilAsync(
            () => pair.EquipmentLog.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F11", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_message_from_another_stream_is_logged_and_left_alone()
    {
        await using var pair = RecipePair.Start(alsoRoute: [(99, 1)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(99, 1, replyExpected: false)
        {
            Name = "NothingToDoWithRecipes",
            Item = SecsItem.L(),
        };

        await pair.HostLink.PostAsync(primary, RecipePair.Token);
        await RecipePair.WaitUntilAsync(() => pair.Asked.Count > 0, "the machine to be asked");

        // And then for the LOG, which is what the assertion below reads. The harness records
        // the message before the handler runs, so a wait on that queue is a wait on the wrong
        // thing: the suite won the race almost every time and lost it once under load.
        await RecipePair.WaitUntilAsync(
            () => pair.EquipmentLog.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        // Without the stream being checked, function 1 of any stream would be read as a namespace
        // request and answered as S15F2 - so this assertion is what holds the check in place.
        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S99F1", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_sent_with_no_reply_bit_is_still_stored()
    {
        await using var pair = RecipePair.Start();
        pair.Store.AddNamespace("PROD");

        await pair.OpenAsync();

        await pair.Host.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
            RecipePair.Token);

        using var primary = new SecsMessage(15, 7, replyExpected: false)
        {
            Name = "RecipeDownloadSend",
            Item = SecsItem.L(
                SecsItem.A("PROD").Named("RCPSPEC"),
                SecsItem.A("ETCH-7").Named("RCPID"),
                SecsItem.B(Body).Named("PPBODY")),
        };

        await pair.HostLink.PostAsync(primary, RecipePair.Token);
        await RecipePair.WaitUntilAsync(
            () => pair.Store.Find("PROD", "ETCH-7") is not null,
            "the recipe to be stored");

        // The reply bit says whether the host wants an answer, not whether the work should happen.
        // A handler that read it first would drop a recipe a host had already sent.
        Assert.Equal<byte[]>(Body, [.. pair.Store.Find("PROD", "ETCH-7")!.Body.Span]);
    }
}
