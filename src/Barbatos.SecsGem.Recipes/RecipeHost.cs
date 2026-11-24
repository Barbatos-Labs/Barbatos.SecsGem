// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// The host's end of stream 15: it asks, and nothing ever arrives unasked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HandledMessages"/> is <strong>empty</strong> and <c>AddRecipeHost</c> declares no
/// route, because every one of stream 15's twenty-seven primaries is this end's. Stream 15 is the
/// third one-way stream here, after 13 and 8, and the three packages serving them are the only
/// ones in this repository whose host half answers nothing at all.
/// </para>
/// <para>
/// <strong>Every method returns an <see cref="RecipeAcknowledgement"/> and it is quoted</strong> -
/// <c>RMACK</c> is defined, with 0 accepted, 1 rejected and 2 to 63 reserved. After three chapters
/// of acknowledgements with no table, this one needs no inference; what it still cannot do is say
/// <em>why</em> a recipe was rejected, because there is one refusal code and the rows that would
/// have carried a reason are among the fourteen left out.
/// </para>
/// </remarks>
public sealed class RecipeHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host's end over a connection.</summary>
    /// <param name="connection">The link.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public RecipeHost(HsmsConnection connection, ILogger<RecipeHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<RecipeHost>.Instance;
    }

    /// <summary>Every primary this end answers, which is none of them.</summary>
    /// <remarks>
    /// Public so the claim can be asserted rather than left in a comment - a test does, and so
    /// does the package smoke test.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } = [];

    /// <summary>Asks what namespaces the machine has - S15F1, answered by S15F2.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them. Empty when the machine has none.</returns>
    /// <remarks>
    /// The only primary in this package with no body at all, and the way a host finds out what is
    /// there: stream 15 has no message announcing a namespace or a recipe.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> ListNamespacesAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await ExchangeAsync(
            new SecsMessage(15, 1, replyExpected: true)
            {
                Name = "RecipeNamespaceRequest",
                Item = SecsItem.L(),
            },
            cancellationToken).ConfigureAwait(false);

        var namespaces = RecipeCodec.ReadNamespaceList(reply.Item);

        if (namespaces.Count == 0 && reply.Item is { Count: > 0 })
        {
            _logger.LogWarning("S15F2 carried a body with no readable namespace in it.");
        }

        return namespaces;
    }

    /// <summary>Creates a namespace - S15F15, answered by S15F16.</summary>
    /// <param name="recipeNamespace">What to call it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);

        return AcknowledgedAsync(
            15,
            "RecipeNamespaceCreateRequest",
            RecipeCodec.NamespaceBody(recipeNamespace),
            "S15F16",
            cancellationToken);
    }

    /// <summary>Deletes a namespace - S15F17, answered by S15F18.</summary>
    /// <param name="recipeNamespace">Which one.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// <strong>The most destructive message in this package</strong>, because it names no recipe:
    /// one message could take a whole directory with it. <see cref="RecipeInMemoryStore"/> refuses
    /// a namespace that still holds recipes, which is that class's choice and not E5's - a machine
    /// of somebody else's may not.
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);

        return AcknowledgedAsync(
            17,
            "RecipeNamespaceDeleteRequest",
            RecipeCodec.NamespaceBody(recipeNamespace),
            "S15F18",
            cancellationToken);
    }

    /// <summary>Asks permission to send a recipe - S15F5, answered by S15F6.</summary>
    /// <param name="request">Which namespace, what it will be called, and how large it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine will take it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// Worth doing before a large recipe rather than after - and unlike stream 8's load inquire,
    /// this one names what is coming, because a recipe has a namespace and an identifier where a
    /// boot program is anonymous.
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(
        RecipeDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return AcknowledgedAsync(
            5,
            "RecipeDownloadRequest",
            RecipeCodec.DownloadRequestBody(request),
            "S15F6",
            cancellationToken);
    }

    /// <summary>Sends a recipe - S15F7, answered by S15F8.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">What it is called.</param>
    /// <param name="body">The recipe. Plain bytes, because <c>PPBODY</c> is a defined <c>B</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    public ValueTask<RecipeAcknowledgement> SendAsync(
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        return AcknowledgedAsync(
            7,
            "RecipeDownloadSend",
            RecipeCodec.DownloadSendBody(recipeNamespace, recipeId, body),
            "S15F8",
            cancellationToken);
    }

    /// <summary>Asks for the grant and then sends - S15F5 then S15F7.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">What it will be called.</param>
    /// <param name="body">The recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// The grant, and the acknowledgement when there was one to get - <see langword="null"/> when
    /// the grant was refused, because there was no S15F7 and so no second <c>RMACK</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a caller with the whole recipe in hand and nothing to decide in between. Keep the two
    /// apart when the point is to see the grant before committing megabytes, which is what the
    /// request exists for - the same split <c>Barbatos.SecsGem.ControlPrograms</c> makes.
    /// </remarks>
    public async ValueTask<(RecipeAcknowledgement Grant, RecipeAcknowledgement? Acknowledgement)> DownloadAsync(
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        var grant = await RequestDownloadAsync(
            new RecipeDownloadRequest
            {
                Namespace = recipeNamespace,
                Id = recipeId,
                Length = (uint)body.Length,
            },
            cancellationToken).ConfigureAwait(false);

        if (grant != RecipeAcknowledgement.Accepted)
        {
            return (grant, null);
        }

        return (grant, await SendAsync(recipeNamespace, recipeId, body, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Fetches a recipe - S15F9, answered by S15F10.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// The acknowledgement and the recipe. The recipe is <see langword="null"/> when the machine
    /// refused, which <strong>it can say</strong> - S15F10 carries a code as well as a body.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// The contrast with stream 8 is the point: S8F6 has no acknowledgement code, so a control
    /// program the machine does not have looks exactly like a present one with an empty body and
    /// <c>Barbatos.SecsGem.ControlPrograms</c> has to advise asking the revision first. Here the
    /// machine says so.
    /// </remarks>
    public async ValueTask<(RecipeAcknowledgement Acknowledgement, Recipe? Recipe)> UploadAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        using var reply = await ExchangeAsync(
            new SecsMessage(15, 9, replyExpected: true)
            {
                Name = "RecipeUploadRequest",
                Item = RecipeCodec.RecipeNameBody(recipeNamespace, recipeId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!RecipeCodec.TryReadUpload(reply.Item, out var code, out var body) || code is null)
        {
            throw new SecsException("S15F10 carried no RMACK.");
        }

        var acknowledgement = Read(code.Value, "S15F10");

        return acknowledgement == RecipeAcknowledgement.Accepted
            ? (acknowledgement, new Recipe { Namespace = recipeNamespace, Id = recipeId, Body = body })
            : (acknowledgement, null);
    }

    /// <summary>Deletes a recipe - S15F13, answered by S15F14.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    public ValueTask<RecipeAcknowledgement> DeleteAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken = default)
        => NamedAsync(13, "RecipeDeleteRequest", "S15F14", recipeNamespace, recipeId, cancellationToken);

    /// <summary>Renames a recipe - S15F19, answered by S15F20.</summary>
    /// <param name="recipeNamespace">Which namespace. A rename stays inside it.</param>
    /// <param name="recipeId">What it is called now.</param>
    /// <param name="newRecipeId">What to call it - <c>RCPIDNEW</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was renamed.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>One namespace, two identifiers</strong>, because S15F19 carries one <c>RCPSPEC</c>
    /// where S15F21 carries two. Moving a recipe between namespaces is
    /// <see cref="CopyAsync"/> and then <see cref="DeleteAsync"/>; nothing in stream 15 does it in
    /// one transaction.
    /// </para>
    /// <para>
    /// <c>RCPIDNEW</c> is undefined and this package carries it anyway, because its two readings
    /// agree: it extends the <c>RCPID</c> beside it in the body, and it is an exact anagram of the
    /// dictionary's unused <c>RCPNEWID</c>, and <strong>both are an <c>A[n]</c></strong>. The
    /// refusal this replaced said a library choosing a spelling would be correcting the source -
    /// true, and it puts no byte in doubt, because a SECS-II body is positional and the names
    /// never travel.
    /// </para>
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> RenameAsync(
        string recipeNamespace,
        string recipeId,
        string newRecipeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(newRecipeId);

        return AcknowledgedAsync(
            19,
            "RecipeRenameRequest",
            RecipeCodec.RenameBody(recipeNamespace, recipeId, newRecipeId),
            "S15F20",
            cancellationToken);
    }

    /// <summary>Copies a recipe - S15F21, answered by S15F22.</summary>
    /// <param name="sourceNamespace">Which namespace to copy from - <c>RCPSPEC</c>.</param>
    /// <param name="sourceId">Which recipe - <c>RCPID</c>.</param>
    /// <param name="destinationNamespace">Which namespace to copy into - <c>RCPSPECDST</c>. May be the source's.</param>
    /// <param name="destinationId">What to call the copy - <c>RCPIDDST</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was copied.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The copy never leaves the machine</strong>, which is the point of having it: a host
    /// that wanted a second copy of a ten-megabyte recipe in another namespace would otherwise
    /// upload it and download it again.
    /// </para>
    /// <para>
    /// Both undefined names here extend a defined name two positions earlier in the same body, and
    /// <strong>neither has a competing definition anywhere in the reference</strong> - which makes
    /// this the firmest of this package's three inferences, firmer than the rename, where an unused
    /// anagram at least exists to argue with.
    /// </para>
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> CopyAsync(
        string sourceNamespace,
        string sourceId,
        string destinationNamespace,
        string destinationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceNamespace);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(destinationNamespace);
        ArgumentNullException.ThrowIfNull(destinationId);

        return AcknowledgedAsync(
            21,
            "RecipeCopyRequest",
            RecipeCodec.CopyBody(sourceNamespace, sourceId, destinationNamespace, destinationId),
            "S15F22",
            cancellationToken);
    }

    /// <summary>Locks a recipe - S15F23, answered by S15F24.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// Whether it was locked, and the token to release it with. The token is
    /// <see langword="null"/> when the machine refused, and - with a warning in the log - when it
    /// accepted and sent none.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Keep the token.</strong> S15F25 is the only way to release the lock and it has to
    /// carry the token back, and stream 15 has no message that asks what locks a machine is
    /// holding - so a host that loses one has locked a recipe until somebody walks over to the
    /// machine.
    /// </para>
    /// <para>
    /// <strong>What a lock prevents is the machine's business.</strong> E5 defines the transaction
    /// and says nothing about what it stops; <see cref="RecipeInMemoryStore"/> refuses a download,
    /// a delete and a rename while a recipe is locked, and another machine may not. There is
    /// nothing on the wire that says which.
    /// </para>
    /// </remarks>
    public async ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        using var reply = await ExchangeAsync(
            new SecsMessage(15, 23, replyExpected: true)
            {
                Name = "RecipeLockRequest",
                Item = RecipeCodec.RecipeNameBody(recipeNamespace, recipeId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!RecipeCodec.TryReadLockReply(reply.Item, out var code, out var token) || code is null)
        {
            throw new SecsException("S15F24 carried no RMACK.");
        }

        var acknowledgement = Read(code.Value, "S15F24");

        if (acknowledgement != RecipeAcknowledgement.Accepted)
        {
            return (acknowledgement, null);
        }

        if (token is null)
        {
            _logger.LogWarning(
                "S15F24 accepted the lock on {Namespace}/{Id} and carried no RCPLOCKID, so there is nothing to release it with.",
                recipeNamespace,
                recipeId);
        }

        // The token is already copied out of the reply - RecipeLockToken keeps its own bytes -
        // so it outlives the message this using block is about to release.
        return (acknowledgement, token);
    }

    /// <summary>Releases a lock - S15F25, answered by S15F26.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="token">The token <see cref="LockAsync"/> handed back.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was released.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// The token goes back out as the item it came in, which is what makes carrying an undefined
    /// name safe here: the machine that minted it is the only end that has to understand it. There
    /// is no code for "wrong token" - S15F26 carries one <c>RMACK</c> - so a refusal here is a
    /// plain no.
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> UnlockAsync(
        string recipeNamespace,
        string recipeId,
        RecipeLockToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(token);

        return AcknowledgedAsync(
            25,
            "RecipeUnlockRequest",
            RecipeCodec.UnlockBody(recipeNamespace, recipeId, token),
            "S15F26",
            cancellationToken);
    }

    /// <summary>Selects a recipe for the next run - S15F31, answered by S15F32.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was selected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    /// <remarks>
    /// One of the two pairs here outside the function range stream 19 shares with this stream, so
    /// one of the two a reader cannot get wrong by picking the wrong standard.
    /// </remarks>
    public ValueTask<RecipeAcknowledgement> SelectAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken = default)
        => NamedAsync(31, "RecipeSelectRequest", "S15F32", recipeNamespace, recipeId, cancellationToken);

    /// <summary>Aborts a recipe operation - S15F53, answered by S15F54.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether anything was aborted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RMACK</c>.</exception>
    public ValueTask<RecipeAcknowledgement> AbortAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken = default)
        => NamedAsync(53, "RecipeOperationAbortRequest", "S15F54", recipeNamespace, recipeId, cancellationToken);

    // ---------------------------------------------------------------- the pieces

    private ValueTask<RecipeAcknowledgement> NamedAsync(
        byte function,
        string name,
        string what,
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        return AcknowledgedAsync(
            function,
            name,
            RecipeCodec.RecipeNameBody(recipeNamespace, recipeId),
            what,
            cancellationToken);
    }

    private async ValueTask<RecipeAcknowledgement> AcknowledgedAsync(
        byte function,
        string name,
        SecsItem body,
        string what,
        CancellationToken cancellationToken)
    {
        using var reply = await ExchangeAsync(
            new SecsMessage(15, function, replyExpected: true) { Name = name, Item = body },
            cancellationToken).ConfigureAwait(false);

        var code = RecipeCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"{what} carried no RMACK.");

        return Read(code, what);
    }

    private RecipeAcknowledgement Read(byte code, string what)
    {
        if (code < (byte)RecipeAcknowledgement.Other)
        {
            return (RecipeAcknowledgement)code;
        }

        // The table names 2 to 63 as reserved, so a code in that range is something this reference
        // anticipated and did not define - a refusal with no reason, and the code goes to the log.
        _logger.LogInformation(
            "{What} carried RMACK {Code}, which the table reserves and does not name.",
            what,
            code);

        return RecipeAcknowledgement.Other;
    }

    private async ValueTask<SecsMessage> ExchangeAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        // The primary is built by the caller and of no use afterwards, so it is disposed here. The
        // reply belongs to whoever reads it, which is this class in every case.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
