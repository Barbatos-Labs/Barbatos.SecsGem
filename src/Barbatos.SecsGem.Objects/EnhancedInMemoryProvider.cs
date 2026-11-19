// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Which variable belongs to which of E39's enhanced requests.
/// </summary>
/// <remarks>
/// E30 keeps the three apart and the difference is observable - S1F3 answers status variables and
/// refuses data variables - so a store that lumped them together would answer S2F57 with things
/// S1F3 would have refused. One discriminator rather than three dictionaries, because every other
/// property of an entry is the same.
/// </remarks>
public enum EnhancedVariableKind
{
    /// <summary>An equipment constant - <c>ECID</c>, read with S2F55.</summary>
    EquipmentConstant = 0,

    /// <summary>A status variable - <c>SVID</c>, read with S2F57.</summary>
    StatusVariable = 1,

    /// <summary>A data variable - <c>VID</c>, read with S2F59.</summary>
    DataVariable = 2,
}

/// <summary>
/// An enhanced service provider that keeps one machine's per-object data in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or an application whose objects are few and rebuilt at
/// start-up - the same claim <see cref="ObjectInMemoryProvider"/> makes for stream 14, and the
/// same reason for making it. Values are held as encoded bytes rather than as items, because an
/// item belongs to the message it came in and this class outlives every message it sees.
/// </para>
/// <para>
/// The codes are reachable from here rather than being decoration. An object nobody declared gets
/// <see cref="EnhancedCommandAcknowledgement.NoSuchObject"/> - the code the catalogue reserves
/// that GEM can never produce - a command nobody registered gets
/// <see cref="EnhancedCommandAcknowledgement.InvalidCommand"/>, and a variable this machine does
/// not have is a zero-length item in its place rather than a gap in the reply.
/// </para>
/// <para>
/// <strong>The control state is not checked here</strong>, because this class cannot see one: see
/// <see cref="IEnhancedServiceProvider.CommandAsync"/>. A simulator does not care; a machine does,
/// and gates the command in its own handler.
/// </para>
/// </remarks>
public sealed class EnhancedInMemoryProvider : IEnhancedServiceProvider
{
    private readonly ConcurrentDictionary<(string Object, EnhancedVariableKind Kind, uint Id), ReadOnlyMemory<byte>> _values = new();
    private readonly ConcurrentDictionary<(string Object, uint DataId), ReadOnlyMemory<byte>> _diagnostics = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<uint, EnhancedAlarm>> _alarms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Object, string Command), Func<EnhancedCommandRequest, EnhancedCommandResult>> _commands = new();
    private readonly ConcurrentDictionary<string, byte> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(uint ReportId, string Object), byte> _bindings = new();

    /// <summary>Gets the objects this store knows about.</summary>
    public IReadOnlyCollection<string> Objects => [.. _objects.Keys];

    /// <summary>Gets the reports tied to objects, as S2F51 left them.</summary>
    public IReadOnlyCollection<EnhancedReportBinding> Bindings =>
        [.. _bindings.Keys.Select(key => new EnhancedReportBinding { ReportId = key.ReportId, ObjectSpecification = key.Object })];

    /// <summary>Declares an object, so that a command addressed to it is not refused outright.</summary>
    /// <param name="objectSpecification">What it is called.</param>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Adding a variable, an alarm, a diagnostic or a command declares the object too. This method
    /// is for an object that has none of those yet and should still exist.
    /// </remarks>
    public void AddObject(string objectSpecification)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);

        _objects[objectSpecification] = 0;
    }

    /// <summary>Sets one of an object's variables.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="kind">Which request it answers.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="value">Its value. Encoded on the way in, so the item is not kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    public void SetVariable(string objectSpecification, EnhancedVariableKind kind, uint id, SecsItem value)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(value);

        AddObject(objectSpecification);
        _values[(objectSpecification, kind, id)] = ObjectAttributeStore.Encode(value);
    }

    /// <summary>Sets one of an object's alarms.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="alarm">The alarm. Replaces one of the same <c>ALID</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void SetAlarm(string objectSpecification, EnhancedAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(alarm);

        AddObject(objectSpecification);
        _alarms.GetOrAdd(objectSpecification, _ => new ConcurrentDictionary<uint, EnhancedAlarm>())
            [(uint)Math.Min(alarm.Id, uint.MaxValue)] = alarm;
    }

    /// <summary>Sets one of an object's diagnostics.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="dataId">Which diagnostic.</param>
    /// <param name="data">What it produces. Encoded on the way in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> or <paramref name="data"/> is <see langword="null"/>.</exception>
    public void SetDiagnostic(string objectSpecification, uint dataId, SecsItem data)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(data);

        AddObject(objectSpecification);
        _diagnostics[(objectSpecification, dataId)] = ObjectAttributeStore.Encode(data);
    }

    /// <summary>Registers a command one object answers to.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="command">The <c>RCMD</c>.</param>
    /// <param name="handler">What to do, and what to answer with.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void SetCommand(
        string objectSpecification,
        string command,
        Func<EnhancedCommandRequest, EnhancedCommandResult> handler)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        AddObject(objectSpecification);
        _commands[(objectSpecification, command)] = handler;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _values.Clear();
        _diagnostics.Clear();
        _alarms.Clear();
        _commands.Clear();
        _objects.Clear();
        _bindings.Clear();
    }

    /// <inheritdoc/>
    public ValueTask<EnhancedCommandResult> CommandAsync(
        EnhancedCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The object first, because "no such object" and "no such command" are different answers
        // and the catalogue gave this stream a code for the first one specially.
        if (!_objects.ContainsKey(request.ObjectSpecification))
        {
            return ValueTask.FromResult(
                EnhancedCommandResult.From(EnhancedCommandAcknowledgement.NoSuchObject));
        }

        return ValueTask.FromResult(
            _commands.TryGetValue((request.ObjectSpecification, request.Command), out var handler)
                ? handler(request)
                : EnhancedCommandResult.From(EnhancedCommandAcknowledgement.InvalidCommand));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SecsItem>> GetEquipmentConstantsAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => Read(request, EnhancedVariableKind.EquipmentConstant, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SecsItem>> GetStatusVariablesAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => Read(request, EnhancedVariableKind.StatusVariable, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SecsItem>> GetDataVariablesAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => Read(request, EnhancedVariableKind.DataVariable, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<EnhancedAlarm>> GetAlarmsAsync(
        EnhancedAlarmRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_alarms.TryGetValue(request.ObjectSpecification, out var alarms))
        {
            return ValueTask.FromResult<IReadOnlyList<EnhancedAlarm>>([]);
        }

        if (request.AlarmIds.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<EnhancedAlarm>>(
                [.. alarms.Values.OrderBy(alarm => alarm.Id)]);
        }

        List<EnhancedAlarm> found = [];

        foreach (var id in request.AlarmIds)
        {
            // Absent rather than padded: S2F62's entries carry their own ALID, so an alarm this
            // machine does not have needs no place held for it.
            if (alarms.TryGetValue(id, out var alarm))
            {
                found.Add(alarm);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<EnhancedAlarm>>(found);
    }

    /// <inheritdoc/>
    public ValueTask<SecsItem?> GetDiagnosticsAsync(
        EnhancedDiagnosticRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _diagnostics.TryGetValue((request.ObjectSpecification, request.DataId), out var encoded)
                ? ObjectAttributeStore.Decode(encoded)
                : null);
    }

    /// <inheritdoc/>
    /// <remarks>Denies a report already tied to that object, because binding it twice says nothing.</remarks>
    public ValueTask<EnhancedReportAcknowledgement> BindReportAsync(
        EnhancedReportBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_objects.ContainsKey(binding.ObjectSpecification))
        {
            return ValueTask.FromResult(EnhancedReportAcknowledgement.Denied);
        }

        return ValueTask.FromResult(
            _bindings.TryAdd((binding.ReportId, binding.ObjectSpecification), 0)
                ? EnhancedReportAcknowledgement.Accepted
                : EnhancedReportAcknowledgement.Denied);
    }

    /// <inheritdoc/>
    /// <remarks>Denies a report that was not tied to that object in the first place.</remarks>
    public ValueTask<EnhancedReportAcknowledgement> UnbindReportAsync(
        EnhancedReportBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _bindings.TryRemove((binding.ReportId, binding.ObjectSpecification), out _)
                ? EnhancedReportAcknowledgement.Accepted
                : EnhancedReportAcknowledgement.Denied);
    }

    private ValueTask<IReadOnlyList<SecsItem>> Read(
        EnhancedValueRequest request,
        EnhancedVariableKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<SecsItem> values = [];

        if (request.Ids.Count == 0)
        {
            // Everything of that kind, in identifier order, which is what GEM's own S1F3 and
            // S2F13 do with an empty request.
            foreach (var entry in _values
                .Where(e => e.Key.Kind == kind && string.Equals(e.Key.Object, request.ObjectSpecification, StringComparison.Ordinal))
                .OrderBy(e => e.Key.Id))
            {
                values.Add(ObjectAttributeStore.Decode(entry.Value));
            }

            return ValueTask.FromResult<IReadOnlyList<SecsItem>>(values);
        }

        foreach (var id in request.Ids)
        {
            // A zero-length item in its place, never a gap: the reply carries no identifiers, so
            // dropping one would shift every later value against the wrong variable.
            values.Add(
                _values.TryGetValue((request.ObjectSpecification, kind, id), out var encoded)
                    ? ObjectAttributeStore.Decode(encoded)
                    : SecsItem.L());
        }

        return ValueTask.FromResult<IReadOnlyList<SecsItem>>(values);
    }
}
