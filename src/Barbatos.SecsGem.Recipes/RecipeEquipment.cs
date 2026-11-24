// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// The machine's end of stream 15: it answers, and it never asks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Takes no connection</strong>, because stream 15 gives the equipment no primary to send.
/// All twenty-seven primaries are the host's and all twenty-seven replies are this end's, which
/// makes stream 15 the <strong>third one-way stream</strong> in this repository after 8 and 13 -
/// and like those two, nothing here ever arrives at a host unasked. There is no message that says
/// a machine's recipes changed.
/// </para>
/// <para>
/// <strong>Thirteen of the stream's twenty-seven pairs.</strong> Nine are every pair whose primary
/// and reply are made entirely of defined items. Four more carry exactly one undefined name
/// each - the rename, the copy, the lock and the unlock - and they are here because of
/// <em>where</em> that name sits: in the rename and the copy it extends a defined name two
/// positions earlier in the same body, and in the lock it is a token this end mints and this end
/// reads back. See <c>RecipeCodec</c> and <see cref="RecipeLockToken"/> for the measurement, and
/// the package README for the fourteen pairs still out, row by row.
/// </para>
/// </remarks>
public sealed class RecipeEquipment
{
    private readonly IRecipeStore _store;
    private readonly ILogger _logger;

    /// <summary>Initialises the machine's end over a store.</summary>
    /// <param name="store">The recipes this machine has.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public RecipeEquipment(IRecipeStore store, ILogger<RecipeEquipment>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<RecipeEquipment>.Instance;
    }

    /// <summary>Every primary this end answers.</summary>
    /// <remarks>
    /// Thirteen of stream 15's twenty-seven pairs. <strong>Five of the thirteen</strong> - the
    /// copy, the lock, the unlock, the select and the operation abort - lie outside the function
    /// range stream 19 reaches at all, since stream 19 stops at F20; it was two of nine before the
    /// rename and the copy went in. Worth knowing when reading a tool's manual: see the package
    /// README on the name clash.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (15, 1),    // Recipe Namespace Request
        (15, 5),    // Recipe Download Request
        (15, 7),    // Recipe Download Send
        (15, 9),    // Recipe Upload Request
        (15, 13),   // Recipe Delete Request
        (15, 15),   // Recipe Namespace Create Request
        (15, 17),   // Recipe Namespace Delete Request
        (15, 19),   // Recipe Rename Request
        (15, 21),   // Recipe Copy Request
        (15, 23),   // Recipe Lock Request
        (15, 25),   // Recipe Unlock Request
        (15, 31),   // Recipe Select Request
        (15, 53),   // Recipe Operation Abort Request
    ];

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>The store is asked before the reply bit is looked at, so a recipe sent without it is still stored.</remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 15
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the recipe handler with nothing to answer it.",
                transaction.S,
                transaction.F);

            return;
        }

        using (reply)
        {
            if (transaction.ReplyExpected)
            {
                await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
        => transaction.F switch
        {
            1 => await OnNamespacesAsync(cancellationToken).ConfigureAwait(false),
            5 => await OnDownloadRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            7 => await OnDownloadSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            9 => await OnUploadAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            13 => await OnDeleteAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            15 => await OnCreateNamespaceAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            17 => await OnDeleteNamespaceAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            19 => await OnRenameAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            21 => await OnCopyAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            23 => await OnLockAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            25 => await OnUnlockAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            31 => await OnSelectAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            53 => await OnAbortAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    private async ValueTask<SecsMessage> OnNamespacesAsync(CancellationToken cancellationToken)
    {
        // S15F1 carries no items at all, so there is nothing to misread and nothing to refuse.
        // The one pair in this package with no request body.
        var namespaces = await _store.ListNamespacesAsync(cancellationToken).ConfigureAwait(false);

        return Reply(2, "RecipeNamespaceData", RecipeCodec.NamespaceListBody(namespaces));
    }

    private async ValueTask<SecsMessage> OnDownloadRequestAsync(
        SecsMessage message,
        CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadDownloadRequest(message.Item, out var request) || request is null)
        {
            return Refuse(6, "RecipeDownloadGrant", "S15F5");
        }

        var acknowledgement = await _store.RequestDownloadAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(6, "RecipeDownloadGrant", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnDownloadSendAsync(
        SecsMessage message,
        CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadDownloadSend(
            message.Item,
            out var recipeNamespace,
            out var recipeId,
            out var body))
        {
            return Refuse(8, "RecipeDownloadAcknowledge", "S15F7");
        }

        var acknowledgement = await _store
            .SaveAsync(recipeNamespace, recipeId, body, cancellationToken)
            .ConfigureAwait(false);

        return Reply(8, "RecipeDownloadAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnUploadAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRecipeName(message.Item, out var recipeNamespace, out var recipeId))
        {
            _logger.LogWarning("S15F9 arrived with a body this end could not read; answering RMACK Rejected.");

            return Reply(
                10,
                "RecipeUploadData",
                RecipeCodec.UploadBody(RecipeAcknowledgement.Rejected, default));
        }

        var (acknowledgement, body) = await _store
            .LoadAsync(recipeNamespace, recipeId, cancellationToken)
            .ConfigureAwait(false);

        // The code and the body travel together, which is what makes this the one pair here whose
        // refusal a host cannot mistake for an empty recipe.
        return Reply(10, "RecipeUploadData", RecipeCodec.UploadBody(acknowledgement, body));
    }

    private async ValueTask<SecsMessage> OnDeleteAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRecipeName(message.Item, out var recipeNamespace, out var recipeId))
        {
            return Refuse(14, "RecipeDeleteAcknowledge", "S15F13");
        }

        var acknowledgement = await _store
            .DeleteAsync(recipeNamespace, recipeId, cancellationToken)
            .ConfigureAwait(false);

        return Reply(14, "RecipeDeleteAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnCreateNamespaceAsync(
        SecsMessage message,
        CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadNamespace(message.Item, out var recipeNamespace))
        {
            return Refuse(16, "RecipeNamespaceCreateAcknowledge", "S15F15");
        }

        var acknowledgement = await _store
            .CreateNamespaceAsync(recipeNamespace, cancellationToken)
            .ConfigureAwait(false);

        return Reply(16, "RecipeNamespaceCreateAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnDeleteNamespaceAsync(
        SecsMessage message,
        CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadNamespace(message.Item, out var recipeNamespace))
        {
            return Refuse(18, "RecipeNamespaceDeleteAcknowledge", "S15F17");
        }

        var acknowledgement = await _store
            .DeleteNamespaceAsync(recipeNamespace, cancellationToken)
            .ConfigureAwait(false);

        return Reply(18, "RecipeNamespaceDeleteAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnRenameAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRename(
            message.Item,
            out var recipeNamespace,
            out var recipeId,
            out var newRecipeId))
        {
            return Refuse(20, "RecipeRenameAcknowledge", "S15F19");
        }

        var acknowledgement = await _store
            .RenameAsync(recipeNamespace, recipeId, newRecipeId, cancellationToken)
            .ConfigureAwait(false);

        return Reply(20, "RecipeRenameAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnCopyAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadCopy(
            message.Item,
            out var sourceNamespace,
            out var sourceId,
            out var destinationNamespace,
            out var destinationId))
        {
            return Refuse(22, "RecipeCopyAcknowledge", "S15F21");
        }

        var acknowledgement = await _store
            .CopyAsync(sourceNamespace, sourceId, destinationNamespace, destinationId, cancellationToken)
            .ConfigureAwait(false);

        return Reply(22, "RecipeCopyAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnLockAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRecipeName(message.Item, out var recipeNamespace, out var recipeId))
        {
            _logger.LogWarning("S15F23 arrived with a body this end could not read; answering RMACK Rejected.");

            return Reply(
                24,
                "RecipeLockAcknowledge",
                RecipeCodec.LockReplyBody(RecipeAcknowledgement.Rejected, token: null));
        }

        var (acknowledgement, token) = await _store
            .LockAsync(recipeNamespace, recipeId, cancellationToken)
            .ConfigureAwait(false);

        var granted = acknowledgement == RecipeAcknowledgement.Accepted;

        if (granted && token is null)
        {
            // Worth saying out loud, because the wire cannot: S15F24 would go out with a code
            // meaning yes and an empty item, and a host would hold a lock with nothing to release
            // it. The store's contract says to return a token or refuse.
            _logger.LogWarning(
                "A store accepted S15F23 for {Namespace}/{Id} and minted no token, so S15F24 "
                + "carries a lock nothing can release.",
                recipeNamespace,
                recipeId);
        }

        // The token goes out only with an acceptance. A refused lock that carried one would hand
        // a host something that looks like a lock it has, which is worse than the plain no that
        // S15F24's single refusal code is for.
        return Reply(
            24,
            "RecipeLockAcknowledge",
            RecipeCodec.LockReplyBody(acknowledgement, granted ? token : null));
    }

    private async ValueTask<SecsMessage> OnUnlockAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadUnlock(message.Item, out var recipeNamespace, out var recipeId, out var token)
            || token is null)
        {
            return Refuse(26, "RecipeUnlockAcknowledge", "S15F25");
        }

        var acknowledgement = await _store
            .UnlockAsync(recipeNamespace, recipeId, token, cancellationToken)
            .ConfigureAwait(false);

        return Reply(26, "RecipeUnlockAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnSelectAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRecipeName(message.Item, out var recipeNamespace, out var recipeId))
        {
            return Refuse(32, "RecipeSelectAcknowledge", "S15F31");
        }

        var acknowledgement = await _store
            .SelectAsync(recipeNamespace, recipeId, cancellationToken)
            .ConfigureAwait(false);

        return Reply(32, "RecipeSelectAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    private async ValueTask<SecsMessage> OnAbortAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!RecipeCodec.TryReadRecipeName(message.Item, out var recipeNamespace, out var recipeId))
        {
            return Refuse(54, "RecipeOperationAbortAcknowledge", "S15F53");
        }

        var acknowledgement = await _store
            .AbortAsync(recipeNamespace, recipeId, cancellationToken)
            .ConfigureAwait(false);

        return Reply(54, "RecipeOperationAbortAcknowledge", RecipeCodec.CodeItem(acknowledgement));
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(15, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read; answering RMACK Rejected.",
            primary);

        return Reply(function, name, RecipeCodec.CodeItem(RecipeAcknowledgement.Rejected));
    }
}
