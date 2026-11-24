// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Globalization;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// The recipes an equipment holds, and what it will do with them.
/// </summary>
/// <remarks>
/// <para>
/// Thirteen methods, one per pair this package implements. Each is awaited inside the transaction
/// the host is waiting on, so none should be slow - writing a recipe to flash while a host holds
/// its T3 open is how a download times out.
/// </para>
/// <para>
/// <strong>Nine of the thirteen have no body and four do</strong>, and the line between them is
/// when they arrived rather than what they do. The nine shipped with this interface; the rename,
/// the copy, the lock and the unlock came later, so they are
/// <see href="https://learn.microsoft.com/dotnet/csharp/language-reference/proposals/csharp-8.0/default-interface-methods">default
/// implementations</see> that refuse, and a store written against the nine still compiles. The
/// default is <see cref="RecipeAcknowledgement.Rejected"/>, which is also the honest answer: a
/// machine that will not rename a recipe says so with the one refusal code S15F20 has, and E5 has
/// no code for "I do not implement this".
/// <c>IObjectProvider</c> in <c>Barbatos.SecsGem.Objects</c> is the same arrangement for the same
/// reason.
/// </para>
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
    /// leaves out. <c>RCPSTATE</c> is undefined, and the nearest the dictionary comes is
    /// <c>RCPSTAT</c>, a <c>U1[1]</c> <strong>with no code table at all</strong> - so even taking
    /// the spelling as a misprint there are no states to report, and the undefined name is the
    /// whole body of S15F34.
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

    /// <summary>Renames a recipe - S15F19.</summary>
    /// <param name="recipeNamespace">Which namespace. The rename stays inside it.</param>
    /// <param name="recipeId">What it is called now.</param>
    /// <param name="newRecipeId">What to call it - <c>RCPIDNEW</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was renamed. The default refuses.</returns>
    /// <remarks>
    /// <para>
    /// <strong>One namespace and two identifiers</strong>, which is the row and not a choice: S15F19
    /// carries a single <c>RCPSPEC</c>, where S15F21 carries two. So a recipe cannot be renamed into
    /// another namespace - that is what the copy is for.
    /// </para>
    /// <para>
    /// <c>RCPIDNEW</c> has no definition, and this package carries it anyway because
    /// <strong>both of its readings are the same format</strong>: it extends <c>RCPID</c>, which
    /// sits beside it in the body as an <c>A[n]</c>, and it is an exact anagram of the dictionary's
    /// unused <c>RCPNEWID</c>, also an <c>A[n]</c>. A rival spelling that puts the same bytes on
    /// the wire decides nothing, and item names do not travel - SECS-II bodies are positional.
    /// </para>
    /// </remarks>
    ValueTask<RecipeAcknowledgement> RenameAsync(
        string recipeNamespace,
        string recipeId,
        string newRecipeId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

    /// <summary>Copies a recipe - S15F21.</summary>
    /// <param name="sourceNamespace">Which namespace to copy from - <c>RCPSPEC</c>.</param>
    /// <param name="sourceId">Which recipe - <c>RCPID</c>.</param>
    /// <param name="destinationNamespace">Which namespace to copy into - <c>RCPSPECDST</c>.</param>
    /// <param name="destinationId">What to call the copy - <c>RCPIDDST</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was copied. The default refuses.</returns>
    /// <remarks>
    /// <strong>The firmest of this package's three inferences.</strong> <c>RCPSPECDST</c> and
    /// <c>RCPIDDST</c> are undefined, and each extends a name that is in the same body, two
    /// positions earlier, carrying the same kind of value: source namespace, source identifier,
    /// destination namespace, destination identifier. Neither has a competing definition anywhere -
    /// unlike <c>RCPIDNEW</c>, which at least has an anagram to argue with - so there is nothing to
    /// correct and nothing to choose.
    /// </remarks>
    ValueTask<RecipeAcknowledgement> CopyAsync(
        string sourceNamespace,
        string sourceId,
        string destinationNamespace,
        string destinationId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

    /// <summary>Locks a recipe - S15F23, answered by S15F24.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was locked, and the token when it was. The default refuses.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The token is the store's to mint and the store's to recognise</strong>, because
    /// <c>RCPLOCKID</c> is defined nowhere - see <see cref="RecipeLockToken"/> for why carrying it
    /// opaquely loses nothing. A store that returns <see cref="RecipeAcknowledgement.Accepted"/>
    /// with no token leaves S15F24 carrying a code and an empty item, which a host reads as a lock
    /// it cannot release; return one or refuse.
    /// </para>
    /// <para>
    /// What a lock <em>means</em> is the store's too. E5 says only that there is one; nothing in
    /// stream 15 says a locked recipe cannot be deleted, and
    /// <see cref="RecipeInMemoryStore"/> makes that rule because a lock nobody honours is a lock in
    /// name only.
    /// </para>
    /// </remarks>
    ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<(RecipeAcknowledgement, RecipeLockToken?)>((RecipeAcknowledgement.Rejected, null));

    /// <summary>Releases a lock - S15F25.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <param name="token">The token S15F24 handed out, as it arrived back.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was released. The default refuses.</returns>
    /// <remarks>
    /// The token comes back as the item it went out as, so a store compares it with
    /// <see cref="RecipeLockToken.Matches"/> or with whatever it keeps alongside its own locks.
    /// There is no code for "wrong token" - S15F26 carries one <c>RMACK</c> - so a refusal here
    /// says nothing about why, which is the shape of every refusal in this stream.
    /// </remarks>
    ValueTask<RecipeAcknowledgement> UnlockAsync(
        string recipeNamespace,
        string recipeId,
        RecipeLockToken token,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

    /// <summary>Reports which recipe of a namespace is selected - S15F35.</summary>
    /// <param name="recipeNamespace">Which namespace - <c>RCPSPEC</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// The selected recipe's identifier and its version, or <see langword="null"/> when nothing is
    /// selected in that namespace. The default answers <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The read-back of <see cref="SelectAsync"/>, and the reason this row went in.</strong>
    /// A host that restarts, or one that took over from another, has no record of what the machine
    /// is set up to run, and sending S15F31 again would change the selection rather than report it.
    /// That is the argument that served S6F27 for S2F35's link table and S7F35 for the formatted
    /// half's directory.
    /// </para>
    /// <para>
    /// <strong>Nothing selected is a zero-length body on the wire</strong>, because S15F36 carries
    /// no acknowledgement item - the same convention S7F6, S7F26, S6F16, S6F20 and S6F28 use, and
    /// the reason this row's shape was readable after all. The refusal it replaces said no served
    /// row of this package answers a request with a multi-item body and no acknowledgement, which
    /// is true of this package and was never true of the repository: S7F6 is a <c>PPID</c> and a
    /// <c>PPBODY</c>, flat, and this body is an identifier and a second field exactly like it.
    /// </para>
    /// <para>
    /// <c>RCPVER</c> is undefined and is carried untouched - see <see cref="RecipeVersion"/> for why
    /// that loses nothing here where S15F49's owner could not be carried at all. A store that keeps
    /// no versions answers with a null one and the row sends a zero-length item, which is a
    /// different fact from nothing being selected and arrives as a different body.
    /// </para>
    /// </remarks>
    ValueTask<(string RecipeId, RecipeVersion? Version)?> CurrentAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<(string, RecipeVersion?)?>(null);
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
/// <strong>Seven of its rules are its own and not E5's</strong>, because S15F6, F8, F14, F16, F18,
/// F20, F22, F24, F26, F32 and F54 each carry one refusal code and no way to say why. Each is
/// tested where it is made:
/// </para>
/// <list type="bullet">
/// <item><description>A download must be granted before it is sent, and the grant names the recipe.</description></item>
/// <item><description><c>LENGTH</c> is compared with what arrives - possible here because <c>PPBODY</c> is a <c>B</c>.</description></item>
/// <item><description>A namespace must exist before a recipe goes into it.</description></item>
/// <item><description>A namespace that still holds recipes is not deleted.</description></item>
/// <item><description>A name that is taken is not written over - not by a rename, not by a copy, not by a namespace create.</description></item>
/// <item><description>A locked recipe is not changed: not sent over, not deleted, not renamed.</description></item>
/// <item><description>A lock is released only by the token it was taken with.</description></item>
/// </list>
/// <para>
/// The third and fourth have no counterpart in streams 8, 13 or 21, because stream 15 is the only
/// one of the four with a <strong>two-level space</strong>: it can create and delete the container
/// as well as the contents. The last two have no counterpart anywhere here, because stream 15 is
/// the only stream in this repository with a lock - and E5 defines the transaction without saying
/// what a lock prevents, so <strong>everything the lock actually stops is this class's
/// invention</strong>. A machine of somebody else's may honour it differently, and a host cannot
/// tell from the wire: S15F24 says a lock was taken and nothing says what it is worth.
/// </para>
/// </remarks>
public sealed class RecipeInMemoryStore : IRecipeStore
{
    private readonly ConcurrentDictionary<string, bool> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Namespace, string Id), ReadOnlyMemory<byte>> _recipes = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), uint> _granted = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), RecipeLockToken> _locks = new();

    private long _minted;

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

    /// <summary>Gets how many recipes are locked.</summary>
    /// <remarks>
    /// Observable for the same reason <see cref="GrantedCount"/> is: a lock is state this store
    /// keeps between two transactions, and S15F24 and S15F26 each carry one code, so a test that
    /// could only read the wire could not tell a lock that was taken from one that was refused.
    /// </remarks>
    public int LockedCount => _locks.Count;

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

    /// <summary>Gets whether a recipe is locked.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// Says nothing about <em>which</em> lock, deliberately: handing the token to whoever asks
    /// would make it worthless, and S15F23 has no way to ask for a lock somebody else is holding.
    /// </remarks>
    public bool IsLocked(string recipeNamespace, string recipeId)
        => _locks.ContainsKey((recipeNamespace, recipeId));

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

        // A locked recipe is refused here as well as at the send, for the reason the namespace is
        // checked twice: this is the cheap place to say no, and the whole purpose of S15F5 is to
        // refuse before a megabyte crosses the wire.
        if (_locks.ContainsKey((request.Namespace, request.Id)))
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

        // And locked again, because S15F23 could have arrived between the grant and the send. The
        // grant is left in place where the vanished-namespace branch above removes it: a lock is
        // temporary, so a host that releases it can still send under the grant it already has,
        // where a namespace that is gone is not coming back under the same name.
        if (_locks.ContainsKey((recipeNamespace, recipeId)))
        {
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

        // A locked recipe is not deleted, and the grant is left alone because the delete did not
        // happen. E5 does not say this - nothing in stream 15 says what a lock prevents - but a
        // lock that does not stop a delete is a lock in name only.
        if (_locks.ContainsKey((recipeNamespace, recipeId)))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

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

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> RenameAsync(
        string recipeNamespace,
        string recipeId,
        string newRecipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(newRecipeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (newRecipeId.Length == 0)
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        if (!_recipes.TryGetValue((recipeNamespace, recipeId), out var body))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        if (_locks.ContainsKey((recipeNamespace, recipeId)))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // TryAdd rather than an assignment, so a rename onto a name that is taken is refused
        // instead of taking the other recipe's place. A host meaning to replace one has S15F13 and
        // then this; a host that got an acceptance here would believe both recipes still existed.
        // This also catches a rename to the name it already has.
        if (!_recipes.TryAdd((recipeNamespace, newRecipeId), body))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        _recipes.TryRemove((recipeNamespace, recipeId), out _);
        _granted.TryRemove((recipeNamespace, recipeId), out _);

        // And the selection follows the recipe, where a delete clears it: the machine is still set
        // up to run the same bytes, so reporting nothing selected would be false.
        if (Selected is { } selected
            && string.Equals(selected.Namespace, recipeNamespace, StringComparison.Ordinal)
            && string.Equals(selected.Id, recipeId, StringComparison.Ordinal))
        {
            Selected = new Recipe { Namespace = recipeNamespace, Id = newRecipeId, Body = body };
        }

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> CopyAsync(
        string sourceNamespace,
        string sourceId,
        string destinationNamespace,
        string destinationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceNamespace);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(destinationNamespace);
        ArgumentNullException.ThrowIfNull(destinationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (destinationId.Length == 0)
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // A locked source is copied happily: a lock stops a recipe being changed, and reading one
        // is what S15F9 does to a locked recipe too.
        if (!_recipes.TryGetValue((sourceNamespace, sourceId), out var body))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        // The destination namespace has to exist, for the reason a download's does: a copy that
        // created one would put a recipe somewhere S15F2 does not list.
        if (!_namespaces.ContainsKey(destinationNamespace))
        {
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        return ValueTask.FromResult(
            _recipes.TryAdd((destinationNamespace, destinationId), body)
                ? RecipeAcknowledgement.Accepted
                : RecipeAcknowledgement.Rejected);
    }

    /// <inheritdoc />
    public ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_recipes.ContainsKey((recipeNamespace, recipeId)))
        {
            return ValueTask.FromResult<(RecipeAcknowledgement, RecipeLockToken?)>(
                (RecipeAcknowledgement.Rejected, null));
        }

        // Text, and counted rather than random, so a test can read the wire. Both are this store's
        // choice: RCPLOCKID has no format in the reference, and nothing in this package looks
        // inside the token - it goes back out as whatever came in. Minted before the check that
        // may throw it away, which costs a number on a refusal and is the price of having ONE
        // guard here instead of two: a ContainsKey ahead of this would be the same question asked
        // twice, and a negative control against the second one found nothing.
        var minted = RecipeLockToken.FromText(
            "LOCK-" + Interlocked.Increment(ref _minted).ToString(CultureInfo.InvariantCulture));

        // Already locked is refused rather than handed the token, because S15F23 says who is
        // asking nowhere: granting it twice would let the second asker release the first's lock.
        return ValueTask.FromResult<(RecipeAcknowledgement, RecipeLockToken?)>(
            _locks.TryAdd((recipeNamespace, recipeId), minted)
                ? (RecipeAcknowledgement.Accepted, minted)
                : (RecipeAcknowledgement.Rejected, null));
    }

    /// <inheritdoc />
    public ValueTask<RecipeAcknowledgement> UnlockAsync(
        string recipeNamespace,
        string recipeId,
        RecipeLockToken token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(token);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_locks.TryGetValue((recipeNamespace, recipeId), out var held) || !held.Matches(token))
        {
            // One refusal for "not locked" and for "not your lock", because S15F26 has one code
            // and inventing a second answer would be inventing a vocabulary. Which it was is in
            // this store's own state, where a test can see it.
            return ValueTask.FromResult(RecipeAcknowledgement.Rejected);
        }

        _locks.TryRemove((recipeNamespace, recipeId), out _);

        return ValueTask.FromResult(RecipeAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>No version, and that is the honest answer rather than a gap.</strong> Nothing in
    /// stream 15 <em>sets</em> a recipe's version - the download carries a namespace, an identifier
    /// and a body, and that is all - so a store that only holds what the wire gave it has no
    /// version to report. It answers with the identifier and a null version, which goes out as a
    /// zero-length item. An application whose recipes do have versions implements
    /// <see cref="IRecipeStore.CurrentAsync"/> itself, as it would for any other fact this store
    /// cannot know.
    /// </remarks>
    public ValueTask<(string RecipeId, RecipeVersion? Version)?> CurrentAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        cancellationToken.ThrowIfCancellationRequested();

        // Per namespace, because S15F35 carries one RCPSPEC and asks about it. A selection in
        // another namespace is not this namespace's answer, and a host asking about an empty one
        // must not be told about a recipe it did not ask about.
        return ValueTask.FromResult(
            Selected is { } selected && string.Equals(selected.Namespace, recipeNamespace, StringComparison.Ordinal)
                ? ((string, RecipeVersion?)?)(selected.Id, null)
                : null);
    }
}
