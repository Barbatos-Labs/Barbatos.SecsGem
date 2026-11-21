// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Frozen;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// Everything the equipment publishes, and the link state the host configures at run time.
/// </summary>
/// <remarks>
/// <para>
/// The definitions - variables, constants, events, alarms, commands - are fixed when the model is
/// built. What the host changes with S2F33, S2F35, S2F37 and S5F3 is held separately and swapped
/// atomically, so a collection event firing while the host is redefining reports sees either the
/// old set or the new one and never half of each.
/// </para>
/// <para>
/// The E30 semantics live here rather than in the message handlers, and every mutating method
/// returns the acknowledgement code E30 says it should. That is deliberate: it means
/// "S2F33 with an unknown VID is DRACK 4" is a fact about the model, testable without a socket, a
/// host or a state machine - and the handler above it is reduced to decoding a body and encoding a
/// byte.
/// </para>
/// <para>
/// Reads are lock-free. Writes take a gate, validate the whole batch, and only then publish; a
/// batch that fails validation changes nothing at all, because a host that was told DRACK 4 is
/// entitled to assume its definition was rejected in full.
/// </para>
/// </remarks>
public sealed class GemEquipmentModel
{
    private readonly object _gate = new();
    private readonly LinkState _initial;
    private LinkState _state;

    internal GemEquipmentModel(
        FrozenDictionary<uint, GemVariable> variables,
        FrozenDictionary<uint, GemEquipmentConstant> constants,
        FrozenDictionary<uint, GemCollectionEvent> events,
        FrozenDictionary<ulong, GemAlarm> alarms,
        FrozenDictionary<string, GemRemoteCommand> remoteCommands,
        IReadOnlyList<GemReport> reports)
    {
        Variables = variables;
        Constants = constants;
        Events = events;
        Alarms = alarms;
        RemoteCommands = remoteCommands;

        _initial = new LinkState(
            reports.ToFrozenDictionary(report => report.Id, report => report.VariableIds.ToArray()),
            FrozenDictionary<uint, uint[]>.Empty,
            events.Values.Where(e => e.EnabledByDefault).Select(e => e.Id).ToFrozenSet(),
            alarms.Values.Where(a => a.EnabledByDefault).Select(a => a.Id).ToFrozenSet(),

            // No limits until a host asks for some. They live here rather than on the variable
            // because they are the host's, like a report definition - and so Reset clears them on
            // a re-establish without this type having to remember to.
            FrozenDictionary<uint, GemVariableLimit[]>.Empty);

        _state = _initial;
    }

    /// <summary>Gets every variable, by identifier.</summary>
    public IReadOnlyDictionary<uint, GemVariable> Variables { get; }

    /// <summary>Gets every equipment constant, by identifier.</summary>
    public IReadOnlyDictionary<uint, GemEquipmentConstant> Constants { get; }

    /// <summary>Gets every collection event, by identifier.</summary>
    public IReadOnlyDictionary<uint, GemCollectionEvent> Events { get; }

    /// <summary>Gets every alarm, by identifier.</summary>
    public IReadOnlyDictionary<ulong, GemAlarm> Alarms { get; }

    /// <summary>Gets every remote command, by name. Ordinal comparison.</summary>
    public IReadOnlyDictionary<string, GemRemoteCommand> RemoteCommands { get; }

    /// <summary>Gets the report identifiers currently defined.</summary>
    public IReadOnlyCollection<uint> DefinedReports => Current.Reports.Keys;

    /// <summary>Gets the variables a defined report carries, in report order.</summary>
    /// <param name="reportId">The RPTID.</param>
    /// <returns>The variable identifiers, or an empty list when the report is not defined.</returns>
    public IReadOnlyList<uint> VariablesForReport(uint reportId)
        => Current.Reports.TryGetValue(reportId, out var variables) ? variables : [];

    /// <summary>Gets the reports linked to an event.</summary>
    /// <param name="eventId">The CEID.</param>
    /// <returns>The report identifiers, or an empty list when nothing is linked.</returns>
    public IReadOnlyList<uint> ReportsForEvent(uint eventId)
        => Current.EventLinks.TryGetValue(eventId, out var reports) ? reports : [];

    /// <summary>Gets a value indicating whether an event currently reports.</summary>
    /// <param name="eventId">The CEID.</param>
    /// <returns><see langword="true"/> when enabled.</returns>
    public bool IsEventEnabled(uint eventId) => Current.EnabledEvents.Contains(eventId);

    /// <summary>Gets a value indicating whether an alarm currently reports.</summary>
    /// <param name="alarmId">The ALID.</param>
    /// <returns><see langword="true"/> when enabled.</returns>
    public bool IsAlarmEnabled(ulong alarmId) => Current.EnabledAlarms.Contains(alarmId);

    /// <summary>Returns the link state to what the model was built with.</summary>
    /// <remarks>
    /// Called when communication is re-established. A host that reconnects has no reason to
    /// believe its previous report definitions survived, and E30 does not require them to - so
    /// keeping them would be inventing a guarantee.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            Volatile.Write(ref _state, _initial);
        }
    }

    /// <summary>Applies an S2F33 report definition.</summary>
    /// <param name="definitions">
    /// The reports to define. An empty list deletes every definition and every link. A definition
    /// with no variables deletes that one report.
    /// </param>
    /// <returns>The DRACK to answer with.</returns>
    /// <remarks>
    /// Deleting every report also drops every event link, because a link to a report that no
    /// longer exists is not a link - and leaving them would let a later S2F33 re-define an RPTID
    /// into an event the host had forgotten it was attached to.
    /// </remarks>
    public GemDefineReportAcknowledgement DefineReports(
        IReadOnlyList<(uint ReportId, IReadOnlyList<uint> VariableIds)> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_gate)
        {
            var state = Current;

            if (definitions.Count == 0)
            {
                Publish(state with
                {
                    Reports = FrozenDictionary<uint, uint[]>.Empty,
                    EventLinks = FrozenDictionary<uint, uint[]>.Empty,
                });

                return GemDefineReportAcknowledgement.Accepted;
            }

            var reports = state.Reports.ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (var (reportId, variableIds) in definitions)
            {
                if (variableIds.Count == 0)
                {
                    reports.Remove(reportId);
                    continue;
                }

                if (reports.ContainsKey(reportId))
                {
                    return GemDefineReportAcknowledgement.ReportAlreadyDefined;
                }

                foreach (var variableId in variableIds)
                {
                    if (!Variables.ContainsKey(variableId))
                    {
                        return GemDefineReportAcknowledgement.UnknownVariable;
                    }
                }

                reports[reportId] = [.. variableIds];
            }

            // A report that was deleted must not stay linked to an event.
            var links = state.EventLinks
                .Select(pair => (pair.Key, Reports: pair.Value.Where(reports.ContainsKey).ToArray()))
                .Where(pair => pair.Reports.Length > 0)
                .ToFrozenDictionary(pair => pair.Key, pair => pair.Reports);

            Publish(state with { Reports = reports.ToFrozenDictionary(), EventLinks = links });
            return GemDefineReportAcknowledgement.Accepted;
        }
    }

    /// <summary>Applies an S2F35 event-to-report link.</summary>
    /// <param name="links">
    /// The links to make. An empty list deletes every link. A link with no reports deletes the
    /// links for that event.
    /// </param>
    /// <returns>The LRACK to answer with.</returns>
    public GemLinkReportAcknowledgement LinkEventReports(
        IReadOnlyList<(uint EventId, IReadOnlyList<uint> ReportIds)> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        lock (_gate)
        {
            var state = Current;

            if (links.Count == 0)
            {
                Publish(state with { EventLinks = FrozenDictionary<uint, uint[]>.Empty });
                return GemLinkReportAcknowledgement.Accepted;
            }

            var current = state.EventLinks.ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (var (eventId, reportIds) in links)
            {
                if (!Events.ContainsKey(eventId))
                {
                    return GemLinkReportAcknowledgement.UnknownEvent;
                }

                if (reportIds.Count == 0)
                {
                    current.Remove(eventId);
                    continue;
                }

                if (current.ContainsKey(eventId))
                {
                    return GemLinkReportAcknowledgement.LinkAlreadyDefined;
                }

                foreach (var reportId in reportIds)
                {
                    if (!state.Reports.ContainsKey(reportId))
                    {
                        return GemLinkReportAcknowledgement.UnknownReport;
                    }
                }

                current[eventId] = [.. reportIds];
            }

            Publish(state with { EventLinks = current.ToFrozenDictionary() });
            return GemLinkReportAcknowledgement.Accepted;
        }
    }

    /// <summary>Applies an S2F37 enable or disable.</summary>
    /// <param name="eventIds">The events to change. An empty list means every event.</param>
    /// <param name="enabled">Whether they should report.</param>
    /// <returns>The ERACK to answer with.</returns>
    public GemEnableEventAcknowledgement SetEventsEnabled(IReadOnlyList<uint> eventIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        lock (_gate)
        {
            var state = Current;

            if (eventIds.Count == 0)
            {
                Publish(state with
                {
                    EnabledEvents = enabled ? Events.Keys.ToFrozenSet() : FrozenSet<uint>.Empty,
                });

                return GemEnableEventAcknowledgement.Accepted;
            }

            foreach (var eventId in eventIds)
            {
                if (!Events.ContainsKey(eventId))
                {
                    return GemEnableEventAcknowledgement.UnknownEvent;
                }
            }

            var set = new HashSet<uint>(state.EnabledEvents);

            foreach (var eventId in eventIds)
            {
                if (enabled)
                {
                    set.Add(eventId);
                }
                else
                {
                    set.Remove(eventId);
                }
            }

            Publish(state with { EnabledEvents = set.ToFrozenSet() });
            return GemEnableEventAcknowledgement.Accepted;
        }
    }

    /// <summary>Applies an S5F3 alarm enable or disable.</summary>
    /// <param name="alarmId">The ALID, or zero for every alarm.</param>
    /// <param name="enabled">Whether it should report.</param>
    /// <returns>The ACKC5 to answer with.</returns>
    public GemAlarmAcknowledgement SetAlarmEnabled(ulong alarmId, bool enabled)
    {
        lock (_gate)
        {
            var state = Current;

            if (alarmId == 0)
            {
                Publish(state with
                {
                    EnabledAlarms = enabled ? Alarms.Keys.ToFrozenSet() : FrozenSet<ulong>.Empty,
                });

                return GemAlarmAcknowledgement.Accepted;
            }

            if (!Alarms.ContainsKey(alarmId))
            {
                return GemAlarmAcknowledgement.Error;
            }

            var set = new HashSet<ulong>(state.EnabledAlarms);

            if (enabled)
            {
                set.Add(alarmId);
            }
            else
            {
                set.Remove(alarmId);
            }

            Publish(state with { EnabledAlarms = set.ToFrozenSet() });
            return GemAlarmAcknowledgement.Accepted;
        }
    }

    /// <summary>Applies an S2F45 limit definition.</summary>
    /// <param name="definitions">
    /// The limits to put in force. A variable with no limits has its limits removed; an empty list
    /// removes every limit on every variable.
    /// </param>
    /// <returns>Whether the whole definition was taken, and what happened to each variable.</returns>
    /// <remarks>
    /// <para>
    /// All or nothing per variable, and reported per limit. A variable the equipment does not have
    /// is LVACK 1 with LIMITACK 3 against each of its limits; a band whose upper is below its lower
    /// is LIMITACK 4 - and in both cases the variables around it are still applied, because a host
    /// that sent twenty bands needs to know which one it got wrong.
    /// </para>
    /// <para>
    /// VLAACK is false when any variable was rejected. E5's two codes for it are "match confirmed"
    /// and "mismatch detected", which is the question a host asks first and the reason the detail
    /// is per variable underneath.
    /// </para>
    /// </remarks>
    public (bool Accepted, IReadOnlyList<GemVariableLimitResult> Results) DefineVariableLimits(
        IReadOnlyList<GemVariableLimitDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_gate)
        {
            var state = Current;

            if (definitions.Count == 0)
            {
                Publish(state with { Limits = FrozenDictionary<uint, GemVariableLimit[]>.Empty });
                return (true, []);
            }

            var limits = state.Limits.ToDictionary(pair => pair.Key, pair => pair.Value);
            List<GemVariableLimitResult> results = [];
            var accepted = true;

            foreach (var definition in definitions)
            {
                var known = Variables.TryGetValue(definition.VariableId, out var variable)
                    && variable.Kind == GemVariableKind.Status;

                List<(uint Id, GemVariableLimitAcknowledgement Acknowledgement)> answers = [];
                var variableAccepted = known;

                foreach (var limit in definition.Limits)
                {
                    var answer = !known
                        ? GemVariableLimitAcknowledgement.NoSuchVariable
                        : limit.Upper < limit.Lower
                            ? GemVariableLimitAcknowledgement.ValueError
                            : GemVariableLimitAcknowledgement.Accepted;

                    if (answer != GemVariableLimitAcknowledgement.Accepted)
                    {
                        variableAccepted = false;
                    }

                    answers.Add((limit.Id, answer));
                }

                if (variableAccepted)
                {
                    if (definition.Limits.Count == 0)
                    {
                        limits.Remove(definition.VariableId);
                    }
                    else
                    {
                        limits[definition.VariableId] = [.. definition.Limits];
                    }
                }
                else
                {
                    accepted = false;
                }

                results.Add(new GemVariableLimitResult(definition.VariableId, variableAccepted, answers));
            }

            Publish(state with { Limits = limits.ToFrozenDictionary() });

            return (accepted, results);
        }
    }

    /// <summary>Gets the variables that have limits in force.</summary>
    public IReadOnlyCollection<uint> DefinedLimits => Current.Limits.Keys;

    /// <summary>Gets the limits in force on a variable.</summary>
    /// <param name="variableId">The VID.</param>
    /// <returns>The bands, or an empty list when the host has defined none.</returns>
    public IReadOnlyList<GemVariableLimit> LimitsFor(uint variableId)
        => Current.Limits.TryGetValue(variableId, out var limits) ? limits : [];

    private LinkState Current => Volatile.Read(ref _state);

    private void Publish(LinkState state) => Volatile.Write(ref _state, state);

    /// <summary>Everything the host can change, as one value that is replaced rather than edited.</summary>
    private sealed record LinkState(
        FrozenDictionary<uint, uint[]> Reports,
        FrozenDictionary<uint, uint[]> EventLinks,
        FrozenSet<uint> EnabledEvents,
        FrozenSet<ulong> EnabledAlarms,
        FrozenDictionary<uint, GemVariableLimit[]> Limits);
}

/// <summary>Builds a <see cref="GemEquipmentModel"/>.</summary>
/// <remarks>
/// Every <c>Add</c> refuses a duplicate identifier rather than overwriting. Two variables sharing
/// an SVID is a configuration mistake that would otherwise surface as a host reading the wrong
/// sensor.
/// </remarks>
public sealed class GemEquipmentModelBuilder
{
    private readonly Dictionary<uint, GemVariable> _variables = [];
    private readonly Dictionary<uint, GemEquipmentConstant> _constants = [];
    private readonly Dictionary<uint, GemCollectionEvent> _events = [];
    private readonly Dictionary<ulong, GemAlarm> _alarms = [];
    private readonly Dictionary<string, GemRemoteCommand> _commands = new(StringComparer.Ordinal);
    private readonly List<GemReport> _reports = [];

    /// <summary>Adds a status variable the host can read with S1F3.</summary>
    /// <param name="id">The SVID.</param>
    /// <param name="name">The name.</param>
    /// <param name="read">Reads the current value. The caller disposes what it returns.</param>
    /// <param name="units">The units, in E5 notation.</param>
    /// <param name="limitEventId">
    /// The collection event raised when this variable crosses one of the limits a host set with
    /// S2F45. <see langword="null"/> means a crossing is tracked and not reported - see
    /// <see cref="GemVariable.LimitEventId"/> for why the event is the machine's to name and the
    /// band is the host's.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    public GemEquipmentModelBuilder AddStatusVariable(
        uint id,
        string name,
        Func<SecsItem> read,
        string units = "",
        uint? limitEventId = null)
        => AddVariable(new GemVariable
        {
            Id = id,
            Name = name,
            Read = read,
            Units = units,
            Kind = GemVariableKind.Status,
            LimitEventId = limitEventId,
        });

    /// <summary>Adds a data variable, which is meaningful only inside the report of its event.</summary>
    /// <param name="id">The DVID.</param>
    /// <param name="name">The name.</param>
    /// <param name="read">Reads the current value. The caller disposes what it returns.</param>
    /// <param name="units">The units, in E5 notation.</param>
    /// <returns>This builder, for chaining.</returns>
    public GemEquipmentModelBuilder AddDataVariable(uint id, string name, Func<SecsItem> read, string units = "")
        => AddVariable(new GemVariable
        {
            Id = id,
            Name = name,
            Read = read,
            Units = units,
            Kind = GemVariableKind.Data,
        });

    /// <summary>Adds a variable.</summary>
    /// <param name="variable">The variable.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The identifier is already used.</exception>
    public GemEquipmentModelBuilder AddVariable(GemVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        if (!_variables.TryAdd(variable.Id, variable))
        {
            throw new ArgumentException($"Variable {variable.Id} is already defined.", nameof(variable));
        }

        return this;
    }

    /// <summary>Adds an equipment constant.</summary>
    /// <param name="constant">The constant.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The identifier is already used.</exception>
    public GemEquipmentModelBuilder AddConstant(GemEquipmentConstant constant)
    {
        ArgumentNullException.ThrowIfNull(constant);

        if (!_constants.TryAdd(constant.Id, constant))
        {
            throw new ArgumentException($"Equipment constant {constant.Id} is already defined.", nameof(constant));
        }

        return this;
    }

    /// <summary>Adds a collection event.</summary>
    /// <param name="id">The CEID.</param>
    /// <param name="name">The name.</param>
    /// <param name="enabledByDefault">Whether it reports before the host says otherwise.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The identifier is already used.</exception>
    public GemEquipmentModelBuilder AddEvent(uint id, string name, bool enabledByDefault = true)
        => AddEvent(id, name, [], enabledByDefault);

    /// <summary>Adds a collection event and says which variables it is about.</summary>
    /// <param name="id">The CEID.</param>
    /// <param name="name">The name.</param>
    /// <param name="variableIds">The VIDs the event concerns, in the order S1F24 should give them.</param>
    /// <param name="enabledByDefault">Whether it reports before the host says otherwise.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variableIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The identifier is already used.</exception>
    /// <remarks>
    /// <para>
    /// <strong>This is not a report.</strong> A report is what a host asked to be sent with the
    /// event, through S2F33 and S2F35, and it can be replaced at any time. This is what the
    /// machine says the event is <em>about</em>, which is what S1F24 answers - a namelist describes
    /// the equipment, not the conversation. Leaving it out is honest for a machine that has not
    /// made the association.
    /// </para>
    /// <para>
    /// The identifiers are not checked against the variables added to this builder, deliberately:
    /// the order of the calls would otherwise matter, and a model that had to be built
    /// variables-first would be a worse model for one check a host cannot act on anyway.
    /// </para>
    /// </remarks>
    public GemEquipmentModelBuilder AddEvent(
        uint id,
        string name,
        IReadOnlyList<uint> variableIds,
        bool enabledByDefault = true)
    {
        ArgumentNullException.ThrowIfNull(variableIds);

        var added = new GemCollectionEvent
        {
            Id = id,
            Name = name,
            EnabledByDefault = enabledByDefault,
            VariableIds = variableIds,
        };

        if (!_events.TryAdd(id, added))
        {
            throw new ArgumentException($"Collection event {id} is already defined.", nameof(id));
        }

        return this;
    }

    /// <summary>Adds a default report definition.</summary>
    /// <param name="id">The RPTID.</param>
    /// <param name="variableIds">The variables it carries, in report order.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>A host may replace the whole set with S2F33, so this is a starting point.</remarks>
    public GemEquipmentModelBuilder AddReport(uint id, params uint[] variableIds)
    {
        ArgumentNullException.ThrowIfNull(variableIds);

        if (_reports.Exists(report => report.Id == id))
        {
            throw new ArgumentException($"Report {id} is already defined.", nameof(id));
        }

        _reports.Add(new GemReport { Id = id, VariableIds = variableIds });
        return this;
    }

    /// <summary>Adds an alarm.</summary>
    /// <param name="alarm">The alarm.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The identifier is already used, or is zero.</exception>
    public GemEquipmentModelBuilder AddAlarm(GemAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        if (alarm.Id == 0)
        {
            // S5F3 reserves zero for "every alarm", so an alarm numbered zero could never be
            // enabled or disabled on its own.
            throw new ArgumentException("Alarm identifier zero is reserved for 'all alarms'.", nameof(alarm));
        }

        if (!_alarms.TryAdd(alarm.Id, alarm))
        {
            throw new ArgumentException($"Alarm {alarm.Id} is already defined.", nameof(alarm));
        }

        return this;
    }

    /// <summary>Adds a remote command.</summary>
    /// <param name="name">The RCMD.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="requiresRemoteControl">Whether it needs the on-line remote control state.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The name is already used.</exception>
    public GemEquipmentModelBuilder AddRemoteCommand(
        string name,
        GemRemoteCommandHandler handler,
        bool requiresRemoteControl = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        var command = new GemRemoteCommand
        {
            Name = name,
            Handler = handler,
            RequiresRemoteControl = requiresRemoteControl,
        };

        if (!_commands.TryAdd(name, command))
        {
            throw new ArgumentException($"Remote command '{name}' is already defined.", nameof(name));
        }

        return this;
    }

    /// <summary>Freezes what has been added into a model.</summary>
    /// <returns>The model.</returns>
    /// <exception cref="InvalidOperationException">A default report names a variable that does not exist.</exception>
    public GemEquipmentModel Build()
    {
        foreach (var report in _reports)
        {
            foreach (var variableId in report.VariableIds)
            {
                if (!_variables.ContainsKey(variableId))
                {
                    // Caught here rather than at run time: a default report with a typo would
                    // otherwise produce an event report that is silently short a value.
                    throw new InvalidOperationException(
                        $"Report {report.Id} names variable {variableId}, which is not defined.");
                }
            }
        }

        return new GemEquipmentModel(
            _variables.ToFrozenDictionary(),
            _constants.ToFrozenDictionary(),
            _events.ToFrozenDictionary(),
            _alarms.ToFrozenDictionary(),
            _commands.ToFrozenDictionary(StringComparer.Ordinal),
            _reports);
    }
}
