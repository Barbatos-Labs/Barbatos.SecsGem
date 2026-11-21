// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Subsystems;

/// <summary>
/// The subsystems a machine has, and what a host may do to them.
/// </summary>
/// <remarks>
/// <para>
/// Six methods, one per primary the equipment end answers: command one, ask its state, read a
/// variable, write a variable, list them all, and read a 2D code.
/// </para>
/// <para>
/// <strong>This is the arrival point, not a notification.</strong> There is no event beside these
/// methods, the same choice <c>IWaferMapStore</c> and <c>IDataSetStore</c> make for the same
/// reason: an application that cares needs the request, and a store method already has it, can
/// refuse it, and is awaited before the reply goes back.
/// </para>
/// <para>
/// <strong>Three of the six have nowhere to refuse.</strong> E5 gives S18F4, S18F6 and S18F10 no
/// acknowledgement code at all - a status reply, a variable reply and a listing carry data and
/// nothing else - so a subsystem that does not exist and a variable that does not exist are both
/// answered with an empty value. Only the write has a code, and the asymmetry is E5's: a machine
/// may refuse a <em>write</em> with <see cref="SubsystemAcknowledgement.UnknownVariable"/> and
/// cannot refuse a <em>read</em> of the same variable at all.
/// </para>
/// <para>
/// Every method is asked inside a transaction the host is waiting on, so none of them should take
/// long.
/// </para>
/// </remarks>
public interface ISubsystemStore
{
    /// <summary>Commands one subsystem - S18F1.</summary>
    /// <param name="command">Which subsystem, which command, and its parameters.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The answer, and which parameters were wrong if any were.</returns>
    /// <remarks>
    /// Answer <see cref="SubsystemCommandAcknowledgement.CompletionLater"/> for anything that will
    /// not be finished by the time the reply goes out, and report the finish with an event: a
    /// handler that answers <see cref="SubsystemCommandAcknowledgement.Accepted"/> for slow work
    /// has told the host it is done.
    /// </remarks>
    ValueTask<SubsystemCommandResult> CommandAsync(SubsystemCommand command, CancellationToken cancellationToken);

    /// <summary>Answers what state a subsystem is in - S18F3.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The state, or <see langword="null"/> when there is no such subsystem.</returns>
    /// <remarks><see langword="null"/> goes out as an empty <c>SSSTATE</c>, because S18F4 has no code to refuse with.</remarks>
    ValueTask<string?> StatusAsync(SubsystemId id, CancellationToken cancellationToken);

    /// <summary>Reads one of a subsystem's variables - S18F5.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="variableId">Which variable - <c>VID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its value, or <see langword="null"/> when there is no such subsystem or variable.</returns>
    /// <remarks><see langword="null"/> goes out as an empty <c>SV</c>, because S18F6 has no code to refuse with either.</remarks>
    ValueTask<string?> ReadAsync(SubsystemId id, uint variableId, CancellationToken cancellationToken);

    /// <summary>Writes one of a subsystem's variables - S18F7.</summary>
    /// <param name="variable">Which subsystem, which variable, and the value.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was written.</returns>
    /// <remarks>
    /// The one transaction here with a refusal worth sending: <c>SSACK</c>'s three codes were
    /// written for exactly this message, which is why they say unknown variable, out of range and
    /// read-only and nothing about the other three replies they also serve.
    /// </remarks>
    ValueTask<SubsystemAcknowledgement> WriteAsync(SubsystemVariable variable, CancellationToken cancellationToken);

    /// <summary>Lists the subsystems this machine has - S18F9.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them, with their names and states. Empty is a legitimate answer.</returns>
    /// <remarks>S18F9 carries no body: it is the one message in stream 18 that asks nothing in particular.</remarks>
    ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads a 2D code - S18F15.</summary>
    /// <param name="targetId">What to read - <c>TARGETID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was read, or <see langword="null"/> when there is no such target.</returns>
    /// <remarks>
    /// The one pair in stream 18 that is not GEM's message set re-addressed. <see langword="null"/>
    /// is answered with <see cref="SubsystemAcknowledgement.Other"/>, because <c>SSACK</c>'s table
    /// has no code for an unknown target any more than it has one for a failed read - its three
    /// refusals are all about writing a variable.
    /// </remarks>
    ValueTask<SubsystemCodeCondition?> ReadCodeAsync(string targetId, CancellationToken cancellationToken);
}

/// <summary>
/// A subsystem store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a controller that passes requests straight on to the hardware
/// behind it. A machine whose subsystems are real registers writes its own store; the interface is
/// six methods.
/// </para>
/// <para>
/// There is no <c>Capture</c> and <c>Restore</c>: a subsystem's state is something the machine
/// observes about its own hardware, and the rule across this repository is that what the machine
/// observed does not come back from a snapshot. A restart re-reads the hardware.
/// </para>
/// </remarks>
public sealed class SubsystemInMemoryStore : ISubsystemStore
{
    private readonly ConcurrentDictionary<SubsystemId, Held> _subsystems = new();
    private readonly ConcurrentDictionary<string, SubsystemCodeCondition> _targets = new(StringComparer.Ordinal);

    /// <summary>Gets or sets what answers a command this store has no handler for. Default <see cref="SubsystemCommandAcknowledgement.InvalidCommand"/>.</summary>
    /// <remarks>Not <see cref="SubsystemCommandAcknowledgement.Accepted"/>, which would claim a machine had done something nobody implemented.</remarks>
    public SubsystemCommandAcknowledgement UnhandledCommand { get; set; }
        = SubsystemCommandAcknowledgement.InvalidCommand;

    /// <summary>Gets or sets what answers a command.</summary>
    /// <remarks>
    /// Null answers every command with <see cref="UnhandledCommand"/>. A handler that returns
    /// <see cref="SubsystemCommandAcknowledgement.InvalidParameter"/> may name the bad parameters
    /// by returning them in the result.
    /// </remarks>
    public Func<SubsystemCommand, SubsystemCommandResult>? AnswerCommand { get; set; }

    /// <summary>Gets how many subsystems are declared.</summary>
    public int Count => _subsystems.Count;

    /// <summary>Declares a subsystem.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="name">What it is called - <c>SSNAME</c>.</param>
    /// <param name="state">What state it starts in - <c>SSSTATE</c>.</param>
    /// <remarks>
    /// Subsystems come from configuration, the way ports and modules do everywhere else in this
    /// repository: stream 18 has no message that creates one, so a machine's subsystems are a fact
    /// about the machine.
    /// </remarks>
    public void Add(SubsystemId id, string name = "", string state = "")
        => _subsystems[id] = new Held(name, state);

    /// <summary>Gets a subsystem, or <see langword="null"/> when there is no such one.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <returns>It.</returns>
    public Subsystem? Find(SubsystemId id)
        => _subsystems.TryGetValue(id, out var held)
            ? new Subsystem { Id = id, Name = held.Name, State = held.State }
            : null;

    /// <summary>Changes what state a subsystem is in.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="state">Its new state.</param>
    /// <returns>Whether there was such a subsystem.</returns>
    /// <remarks>Raises nothing. An event report is a decision, and <see cref="SubsystemEquipment.ReportEventAsync"/> is how it is made.</remarks>
    public bool SetState(SubsystemId id, string state)
    {
        if (!_subsystems.TryGetValue(id, out var held))
        {
            return false;
        }

        _subsystems[id] = held with { State = state };
        return true;
    }

    /// <summary>Sets one of a subsystem's variables, without a transaction.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="variableId">Which variable.</param>
    /// <param name="value">Its value.</param>
    /// <param name="isReadOnly">Whether a host may write it.</param>
    /// <returns>Whether there was such a subsystem.</returns>
    public bool Set(SubsystemId id, uint variableId, string value, bool isReadOnly = false)
    {
        if (!_subsystems.TryGetValue(id, out var held))
        {
            return false;
        }

        held.Variables[variableId] = value;

        if (isReadOnly)
        {
            held.ReadOnly[variableId] = true;
        }
        else
        {
            held.ReadOnly.TryRemove(variableId, out _);
        }

        return true;
    }

    /// <summary>Declares what a 2D code read of a target finds.</summary>
    /// <param name="condition">What the reader reports.</param>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is <see langword="null"/>.</exception>
    public void SetCodeCondition(SubsystemCodeCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        _targets[condition.TargetId] = condition;
    }

    /// <inheritdoc />
    public ValueTask<SubsystemCommandResult> CommandAsync(
        SubsystemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_subsystems.ContainsKey(command.Subsystem))
        {
            // HCACK has a code for exactly this, which is more than most of this stack gets.
            return ValueTask.FromResult(new SubsystemCommandResult
            {
                Subsystem = command.Subsystem,
                Acknowledgement = SubsystemCommandAcknowledgement.NoSuchSubsystem,
            });
        }

        return ValueTask.FromResult(AnswerCommand?.Invoke(command) ?? new SubsystemCommandResult
        {
            Subsystem = command.Subsystem,
            Acknowledgement = UnhandledCommand,
        });
    }

    /// <inheritdoc />
    public ValueTask<string?> StatusAsync(SubsystemId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_subsystems.TryGetValue(id, out var held) ? held.State : null);
    }

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(SubsystemId id, uint variableId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _subsystems.TryGetValue(id, out var held) && held.Variables.TryGetValue(variableId, out var value)
                ? value
                : null);
    }

    /// <inheritdoc />
    public ValueTask<SubsystemAcknowledgement> WriteAsync(
        SubsystemVariable variable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variable);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_subsystems.TryGetValue(variable.Subsystem, out var held)
            || !held.Variables.ContainsKey(variable.VariableId))
        {
            // One code for both, because SSACK has one: a variable of a subsystem that does not
            // exist is a variable that does not exist.
            return ValueTask.FromResult(SubsystemAcknowledgement.UnknownVariable);
        }

        if (held.ReadOnly.ContainsKey(variable.VariableId))
        {
            return ValueTask.FromResult(SubsystemAcknowledgement.ReadOnlyVariable);
        }

        held.Variables[variable.VariableId] = variable.Value;

        return ValueTask.FromResult(SubsystemAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered by the text of the identifier, so a host can diff two listings. Numbered
        // identifiers sort before named ones, because a number is not a name here and the ordering
        // had better not suggest it is.
        IReadOnlyList<Subsystem> subsystems =
        [
            .. _subsystems
                .OrderBy(pair => pair.Key.IsNumber ? 0 : 1)
                .ThenBy(pair => pair.Key.IsNumber ? pair.Key.Number : 0)
                .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                .Select(pair => new Subsystem
                {
                    Id = pair.Key,
                    Name = pair.Value.Name,
                    State = pair.Value.State,
                }),
        ];

        return ValueTask.FromResult(subsystems);
    }

    /// <inheritdoc />
    public ValueTask<SubsystemCodeCondition?> ReadCodeAsync(string targetId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _targets.TryGetValue(targetId, out var condition) ? condition : null);
    }

    /// <summary>A declared subsystem: its name, its state, its variables and which of them are read-only.</summary>
    /// <remarks>
    /// A record, so <c>with</c> changes the state and keeps the two collections - the copy
    /// constructor carries their backing fields across, which is what makes
    /// <see cref="SetState"/> safe to write that way. Both collections are concurrent because
    /// <see cref="Set"/> runs on an application's thread while <see cref="WriteAsync"/> runs on
    /// the connection's dispatch loop.
    /// </remarks>
    private sealed record Held(string Name, string State)
    {
        public ConcurrentDictionary<uint, string> Variables { get; } = new();

        public ConcurrentDictionary<uint, bool> ReadOnly { get; } = new();
    }
}
