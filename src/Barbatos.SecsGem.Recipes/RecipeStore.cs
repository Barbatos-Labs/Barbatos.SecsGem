// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// The recipes an equipment holds, and what it will do with them.
/// </summary>
/// <remarks>
/// Nine methods, one per pair this package implements. Each is awaited inside the transaction the
/// host is waiting on, so none should be slow - writing a recipe to flash while a host holds its
/// T3 open is how a download times out.
/// </remarks>
public interface IRecipeStore
{
    /// <summary>Lists the namespaces this machine has - S15F1.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them. Empty is a legitimate answer.</returns>
    /// <remarks>
    /// <c>RCPSPEC</c> is an <c>A[n]</c> and the reference gives it no vocabulary, so what a
    /// namespace is called is the two ends' own business and nothing here interprets it.
    /// </remarks>
    ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken);

    /// <summary>Creates a namespace - S15F15.</summary>
    /// <param name="recipeNamespace">What to call it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was created.</returns>
    ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken);

    /// <summary>Deletes a namespace - S15F17.</summary>
    /// <param name="recipeNamespace">Which one.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    /// <remarks>
    /// <strong>The more destructive of this package's two deletes</strong>, and the reason
    /// <see cref="RecipeInMemoryStore"/> refuses a namespace that still holds recipes. That refusal
    /// is the store's choice, not E5's - S15F18 carries one refusal code and no way to say why.
    /// </remarks>
    ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken);

    /// <summary>Decides whether a recipe may be sent - S15F5.</summary>
    /// <param name="request">Which namespace, what it will be called, and how large the host says it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host may send it.</returns>
    /// <remarks>The cheap place to say no: after the grant, the next message is the whole recipe.</remarks>
    ValueTask<RecipeAcknowledgement> RequestDownloadAsync(
        RecipeDownloadRequest request,
        CancellationToken cancellationToken);

    /// <summary>Takes a recipe the host sent - S15F7.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">What it is called.</param>
    /// <param name="body">The recipe, already copied out of the arriving message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was stored.</returns>
    ValueTask<RecipeAcknowledgement> SaveAsync(
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);

    /// <summary>Hands a recipe back - S15F9, answered by S15F10.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it could be read, and the recipe when it could.</returns>
    /// <remarks>
    /// <strong>The one reply in this package that carries a code and data together</strong>, so an
    /// upload can be refused without the host having to guess from an empty body. Stream 8's S8F6
    /// has no code at all, which is why <c>Barbatos.SecsGem.ControlPrograms</c> advises asking the
    /// revision first; nothing like that is needed here.
    /// </remarks>
    ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken);

    /// <summary>Deletes a recipe - S15F13.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    ValueTask<RecipeAcknowledgement> DeleteAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken);

    /// <summary>Selects a recipe for the next run - S15F31.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was selected.</returns>
    /// <remarks>
    /// Selecting is not running: stream 15 has no message that starts a process, and S15F33 - the
    /// execution status, which would say whether one is running - is among the rows this package
    /// leaves out, because <c>RCPSTATE</c> is undefined while the dictionary's <c>RCPSTAT</c> goes
    /// unused.
    /// </remarks>
    ValueTask<RecipeAcknowledgement> SelectAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken);

    /// <summary>Aborts whatever recipe operation is in flight - S15F53.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether anything was aborted.</returns>
    /// <remarks>Aborting is not deleting - S15F13 is the message that deletes a recipe, and S15F17 the one that deletes a namespace.</remarks>
    ValueTask<RecipeAcknowledgement> AbortAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A recipe store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a controller that passes recipes straight on. Anything that
/// must survive a restart writes its own store; there is no snapshot pair here, for the reason the
/// other transfer packages give - a recipe is not state a machine claims about itself.
/// </para>
/// <para>
/// <strong>Four of its rules are its own and not E5's</strong>, because S15F6, F8, F14, F16, F18,
/// F32 and F54 each carry one refusal code and no way to say why. Each is tested where it is made:
/// </para>
/// <list type="bullet">
/// <item><description>A download must be granted before it is sent, and the grant names the recipe.</description></item>
/// <item><description><c>LENGTH</c> is compared with what arrives - possible here because <c>PPBODY</c> is a <c>B</c>.</description></item>
/// <item><description>A namespace must exist before a recipe goes into it.</description></item>
/// <item><description>A namespace that still holds recipes is not deleted.</description></item>
/// </list>
/// <para>
/// The third and fourth have no counterpart in streams 8, 13 or 21, because stream 15 is the only
/// one of the four with a <strong>two-level space</strong>: it can create and delete the container
/// as well as the contents.
/// </para>
/// </remarks>
public sealed class RecipeInMemoryStore : IRecipeStore
{
    private readonly ConcurrentDictionary<string, bool> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Namespace, string Id), ReadOnlyMemory<byte>> _recipes = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), uint> _granted = new();

    /// <summary>Gets or sets the largest recipe this store will take. Default one mebibyte.</summary>
    /// <remarks>
    /// A declared length beyond this is refused at S15F5, which is what the request exists for.
    /// Stream 15's own way of reporting space - S15F39/F40 - needs <c>SPACEAVAIL</c> and
    /// <c>SPACETOTAL</c>, both undefined, so it is not implemented and a host cannot ask.
    /// </remarks>
    public uint MaximumLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many recipes are held, across every namespace.</summary>
    public int Count => _recipes.Count;

    /// <summary>Gets how many downloads have been granted and not yet sent.</summary>
    public int GrantedCount => _granted.Count;

    /// <summary>Gets the recipe most recently selected, or <see langword="null"/> when none has been.</summary>
    /// <remarks>
    /// Observable because selecting is the one operation in this package whose whole effect is a
    /// change of state rather than a transfer - S15F32 says it happened and nothing in the stream
    /// asks what is selected, S15F35/F36 needing the undefined <c>RCPVER</c>.
    /// </remarks>
    public Recipe? Selected { get; private set; }

    /// <summary>Adds a namespace without a transaction, for a simulator or a test.</summary>
    /// <param name="recipeNamespace">What to call it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> is <see langword="null"/>.</exception>
    public void AddNamespace(string recipeNamespace)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);

        _namespaces[recipeNamespace] = true;
    }

    /// <summary>Puts a recipe in without a transaction, for a simulator or a test.</summary>
    /// <param name="recipeNamespace">Which namespace. Created if it is not there.</param>
    /// <param name="recipeId">What it is called.</param>
    /// <param name="body">The recipe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recipeNamespace"/> or <paramref name="recipeId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// How a machine offers a recipe a host can go and fetch, which stream 15 has no message for:
    /// there is no "I have a new recipe" primary, so the host finds out with S15F1 and - for the
    /// recipes inside a namespace - would need S15F3, whose reply this package does not implement.
    /// </remarks>
    public void Add(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        _namespaces[recipeNamespace] = true;
        _recipes[(recipeNamespace, recipeId)] = body;
    }

    /// <summary>Gets a recipe, or <see langword="null"/> when there is none.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <returns>It.</returns>
    public Recipe? Find(string recipeNamespace, string recipeId)
        => _recipes.TryGetValue((recipeNamespace, recipeId), out var body)
            ? new Recipe { Namespace = recipeNamespace, Id = recipeId, Body = body }
            : null;

    /// <summary>Gets whether a namespace is there.</summary>
    /// <param name="recipeNamespace">Which one.</param>
    /// <returns>Whether it is.</returns>
    public bool HasNamespace(string recipeNamespace) => _namespaces.ContainsKey(recipeNamespace);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered so a host can diff two answers.
        IReadOnlyList<string> listed = [.. _namespaces.Keys.OrderBy(name => name, StringComparer.Ordinal)];

        return ValueTask.FromResult(listed);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        cancellationToken.ThrowIfCancellationRequested();

        // Creating one that is already there is refused rather than silently accepted, because a
        // host that gets an acceptance will believe it created an empty namespace - and the next
        // thing it does may be to fill one that already has recipes in it.
        return ValueTask.FromResult(
            _namespaces.TryAdd(recipeNamespace, true)
                ? RecipeAcknowledgement.Accepted
                : RecipeAcknowledgement.Rejected);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_namespaces.ContainsKey(recipeNamespace))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // The guard that matters most in this package: a namespace holding recipes is not deleted,
        // because S15F17 names no recipe and a host that meant to clear one directory would
        // otherwise take every recipe in it with one message. E5 does not say this; the store does,
        // and S15F18 has no code to explain it, so the host has to delete the recipes first.
        if (_recipes.Keys.Any(key => string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal)))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        _namespaces.TryRemove(recipeNamespace, out _);

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(
        RecipeDownloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_namespaces.ContainsKey(request.Namespace))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        if (request.Length > MaximumLength)
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        _granted[(request.Namespace, request.Id)] = request.Length;

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> SaveAsync(
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_granted.TryGetValue((recipeNamespace, recipeId), out var declared))
        {
            // Nothing was granted for this recipe, so nothing was offered.
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // And the namespace is checked again, not just at the grant: two transactions means the
        // machine's state can change in between, and S15F17 could have taken the namespace away
        // while this download was in flight. Without this a recipe would land in a namespace that
        // S15F2 does not list - present to Find and invisible to a host.
        if (!_namespaces.ContainsKey(recipeNamespace))
        {
            _granted.TryRemove((recipeNamespace, recipeId), out _);

            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // And the length is checked against what actually arrived, which streams 13 and 21 cannot
        // do: PPBODY is a B and therefore has a byte count, where DSDATA and ITEMDATA have no
        // format to measure. Barbatos.SecsGem.ControlPrograms makes the same check for the same
        // reason.
        if (declared != body.Length)
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        _granted.TryRemove((recipeNamespace, recipeId), out _);
        _recipes[(recipeNamespace, recipeId)] = body;

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _recipes.TryGetValue((recipeNamespace, recipeId), out var body)
                ? (RecipeAcknowledgement.Accepted, body)
                : (RecipeAcknowledgement.Rejected, default));
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> DeleteAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        _granted.TryRemove((recipeNamespace, recipeId), out _);

        if (!_recipes.TryRemove((recipeNamespace, recipeId), out _))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // A recipe that was selected and is now gone leaves nothing selected, because a host that
        // read Selected afterwards would otherwise be told the machine is set up to run something
        // it no longer has.
        if (Selected is { } selected
            && string.Equals(selected.Namespace, recipeNamespace, StringComparison.Ordinal)
            && string.Equals(selected.Id, recipeId, StringComparison.Ordinal))
        {
            Selected = null;
        }

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> SelectAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_recipes.TryGetValue((recipeNamespace, recipeId), out var body))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        Selected = new Recipe { Namespace = recipeNamespace, Id = recipeId, Body = body };

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> AbortAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        // Aborting forgets the grant and keeps whatever is already stored. S15F13 is the message
        // that deletes, and an abort that deleted would be doing its job.
        return ValueTask.FromResult(
            _granted.TryRemove((recipeNamespace, recipeId), out _)
                ? RecipeAcknowledgement.Accepted
                : RecipeAcknowledgement.Rejected);
    }
}
