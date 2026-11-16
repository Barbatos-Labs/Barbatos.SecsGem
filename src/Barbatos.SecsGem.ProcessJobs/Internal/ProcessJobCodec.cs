// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// Every stream 16 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Both ends go through this class, so a change to a body is a change to both ends or it does not
/// compile. That matters here as much as it did for stream 3, and for the same reason.
/// </para>
/// <para>
/// <strong>Not one row of stream 16 is firm.</strong> The catalogue carries a flag for entries its
/// source marks as a representative subset rather than a definition, and for stream 16 it is set
/// on all twenty-eight rows - where stream 12, for comparison, has seventeen of nineteen firm.
/// Five of the thirteen item names those rows use have no dictionary entry at all. So the bodies
/// below are read off the item lists and the formats of the items that <em>are</em> defined, and
/// they are a careful reading rather than a quotation.
/// </para>
/// <para>
/// What makes this stream better off than stream 3 is that every one of the five undefined names
/// has a defined item beside it doing the same job, so none of them had to be invented from
/// nothing:
/// </para>
/// <code>
/// PJID      written A, as PRJOBID is - the dictionary's own name for a process job identifier
/// MTRLID    written A, as MID is - the dictionary's own name for a material identifier
/// PJACK     written B[1] with PREACK's two values: 0 accepted, 1 rejected
/// JOBSTATE  written U1 with PRSTATE's ten code values, which are E40's state model outright
/// QPOSN     written U2, as PRJOBSPACE is - the one item E40 defines for counting process jobs
/// </code>
/// <para>
/// The defined ones are written as the dictionary gives them: <c>PPID</c> <c>A</c>, <c>RCMD</c>
/// <c>A</c>, <c>CPNAME</c> and <c>CPVAL</c> <c>A</c>, <c>CMDA</c>, <c>CPACK</c> and <c>HCACK</c>
/// <c>B[1]</c>, <c>TIME</c> <c>A[16]</c> - and the timestamp is written in the same sixteen
/// characters the GEM package writes, so a host sees one format across this stack.
/// </para>
/// <para>
/// Everything is read out of any format that could carry it: a state or a code in any numeric
/// format, a state as its name in text, a list of one where a bare item was expected. It is only
/// what this library <em>writes</em> that the inferences decide, and if a machine disagrees, this
/// file is the only one to change.
/// </para>
/// </remarks>
internal static class ProcessJobCodec
{
    /// <summary>The sixteen characters <c>TIME</c> is, matching what the GEM package writes.</summary>
    private const string TimeFormat = "yyyyMMddHHmmssff";

    // ------------------------------------------------------------ one identifier

    /// <summary>S16F5, S16F9 and S16F19 - a bare <c>PRJOBID</c>.</summary>
    internal static SecsItem JobIdBody(string processJobId) => SecsItem.A(processJobId).Named("PRJOBID");

    /// <summary>Reads a bare identifier, or the first entry of a list somebody wrapped it in.</summary>
    internal static string? TryReadJobId(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? TryReadJobId(item.Items[0]) : null;
        }

        var text = Text(item);

        return text.Length == 0 ? null : text;
    }

    // ------------------------------------------------------------------- create

    /// <summary>S16F1 - Process Job Create Request.</summary>
    /// <remarks>
    /// Three items, because that is what the catalogue lists: the identifier, the recipe and the
    /// material. Everything else a job has - how its recipe may be tuned, whether it starts
    /// itself, what order its material goes in - is an attribute, and a host sets those over
    /// stream 14 before the machine takes the job up. This package does not add fields to a body
    /// to save a host a second transaction.
    /// </remarks>
    internal static SecsItem CreateBody(string processJobId, string recipeId, IReadOnlyList<string> material)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.A(recipeId).Named("PPID"),
            MaterialList(material));

    /// <summary>Reads S16F1.</summary>
    internal static bool TryReadCreate(
        SecsItem? body,
        out string processJobId,
        out string recipeId,
        out List<string> material)
    {
        processJobId = string.Empty;
        recipeId = string.Empty;
        material = [];

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        processJobId = Text(body.Items[0]);
        recipeId = Text(body.Items[1]);

        if (body.Count > 2)
        {
            material = ReadMaterial(body.Items[2]);
        }

        return processJobId.Length > 0;
    }

    /// <summary>S16F2, S16F10, S16F16 and S16F26 - an identifier and a <c>PJACK</c>.</summary>
    internal static SecsItem AcknowledgeBody(string processJobId, ProcessJobAcknowledgement acknowledgement)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.B((byte)acknowledgement).Named("PJACK"));

    /// <summary>Reads one of those replies.</summary>
    internal static ProcessJobAcknowledgement? TryReadAcknowledge(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        // A bare code, which is what S16F12 and S16F30 carry, and the second element of a pair,
        // which is what the rest do.
        if (!body.IsList)
        {
            return Code(body) is { } bare ? Acknowledgement(bare) : null;
        }

        if (body.Count == 1)
        {
            return TryReadAcknowledge(body.Items[0]);
        }

        return body.Count >= 2 && Code(body.Items[1]) is { } code ? Acknowledgement(code) : null;
    }

    /// <summary>S16F12 and S16F30 - a bare <c>PJACK</c>.</summary>
    internal static SecsItem AcknowledgeOnlyBody(ProcessJobAcknowledgement acknowledgement)
        => SecsItem.B((byte)acknowledgement).Named("PJACK");

    // ------------------------------------------------------------------ command

    /// <summary>S16F3 - Process Job Command Send.</summary>
    internal static SecsItem CommandBody(string processJobId, string command)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.A(command).Named("RCMD"));

    /// <summary>Reads S16F3.</summary>
    internal static bool TryReadCommand(SecsItem? body, out string processJobId, out string command)
    {
        processJobId = string.Empty;
        command = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        processJobId = Text(body.Items[0]);
        command = Text(body.Items[1]);

        return processJobId.Length > 0 && command.Length > 0;
    }

    /// <summary>S16F4 - an identifier and a <c>CMDA</c>.</summary>
    internal static SecsItem CommandResultBody(string processJobId, ProcessJobCommandAcknowledgement acknowledgement)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.B((byte)acknowledgement).Named("CMDA"));

    /// <summary>Reads S16F4.</summary>
    internal static ProcessJobCommandAcknowledgement? TryReadCommandResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (!body.IsList)
        {
            return Code(body) is { } bare ? CommandAcknowledgement(bare) : null;
        }

        if (body.Count == 1)
        {
            return TryReadCommandResult(body.Items[0]);
        }

        return body.Count >= 2 && Code(body.Items[1]) is { } code ? CommandAcknowledgement(code) : null;
    }

    /// <summary>S16F23 - Process Job Multi-Command Send.</summary>
    internal static SecsItem MultiCommandBody(IReadOnlyList<string> processJobIds, string command)
    {
        var jobs = new SecsItem[processJobIds.Count];

        for (var i = 0; i < processJobIds.Count; i++)
        {
            jobs[i] = SecsItem.A(processJobIds[i]).Named("PRJOBID");
        }

        return SecsItem.L(SecsItem.L(jobs), SecsItem.A(command).Named("RCMD"));
    }

    /// <summary>Reads S16F23.</summary>
    internal static bool TryReadMultiCommand(SecsItem? body, out List<string> processJobIds, out string command)
    {
        processJobIds = [];
        command = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        command = Text(body.Items[^1]);
        processJobIds = ReadMaterial(body.Items[0]);

        return processJobIds.Count > 0 && command.Length > 0;
    }

    /// <summary>S16F24 - one <c>CMDA</c> per job.</summary>
    internal static SecsItem MultiCommandResultBody(IReadOnlyList<ProcessJobCommandResult> results)
    {
        var entries = new SecsItem[results.Count];

        for (var i = 0; i < results.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(results[i].ProcessJobId).Named("PRJOBID"),
                SecsItem.B((byte)results[i].Acknowledgement).Named("CMDA"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S16F24.</summary>
    internal static bool TryReadMultiCommandResult(SecsItem? body, out List<ProcessJobCommandResult> results)
    {
        results = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var fields in Entries(body, 2))
        {
            results.Add(new ProcessJobCommandResult
            {
                ProcessJobId = Text(fields[0]),
                Acknowledgement = CommandAcknowledgement(Code(fields[1]) ?? 2),
            });
        }

        return true;
    }

    // ------------------------------------------------------------------- status

    /// <summary>S16F6 - Process Job Status Data.</summary>
    internal static SecsItem StatusBody(ProcessJob job) => SecsItem.L(
        SecsItem.A(job.ProcessJobId).Named("PRJOBID"),
        SecsItem.A(job.RecipeId).Named("PPID"),
        SecsItem.U1((byte)job.State).Named("JOBSTATE"));

    /// <summary>Reads S16F6.</summary>
    internal static ProcessJobStatus? TryReadStatus(SecsItem? body)
    {
        if (body is null || !body.IsList || body.Count < 3)
        {
            return null;
        }

        var processJobId = Text(body.Items[0]);

        if (processJobId.Length == 0)
        {
            return null;
        }

        var (state, code) = ReadState(body.Items[2]);

        return new ProcessJobStatus
        {
            ProcessJobId = processJobId,
            RecipeId = Text(body.Items[1]),
            State = state,
            StateCode = code,
        };
    }

    /// <summary>S16F8 - Process Job List Data.</summary>
    internal static SecsItem ListBody(IReadOnlyList<ProcessJob> jobs)
    {
        var entries = new SecsItem[jobs.Count];

        for (var i = 0; i < jobs.Count; i++)
        {
            entries[i] = StatusBody(jobs[i]);
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S16F8.</summary>
    internal static bool TryReadList(SecsItem? body, out List<ProcessJobStatus> jobs)
    {
        jobs = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var fields in Entries(body, 3))
        {
            var processJobId = Text(fields[0]);

            if (processJobId.Length == 0)
            {
                continue;
            }

            var (state, code) = ReadState(fields[2]);

            jobs.Add(new ProcessJobStatus
            {
                ProcessJobId = processJobId,
                RecipeId = Text(fields[1]),
                State = state,
                StateCode = code,
            });
        }

        return true;
    }

    // ------------------------------------------------------------- event report

    /// <summary>S16F11 - Process Job Event Report Send.</summary>
    internal static SecsItem EventReportBody(string processJobId, ProcessJobState state, DateTimeOffset at)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.U1((byte)state).Named("JOBSTATE"),
            SecsItem.A(at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)).Named("TIME"));

    /// <summary>Reads S16F11.</summary>
    internal static ProcessJobStateReport? TryReadEventReport(SecsItem? body)
    {
        if (body is null || !body.IsList || body.Count < 2)
        {
            return null;
        }

        var processJobId = Text(body.Items[0]);

        if (processJobId.Length == 0)
        {
            return null;
        }

        var (state, code) = ReadState(body.Items[1]);

        return new ProcessJobStateReport
        {
            ProcessJobId = processJobId,
            State = state,
            StateCode = code,
            At = body.Count > 2 ? ReadTime(body.Items[2]) : null,
        };
    }

    /// <summary>S16F29 - Process Job Queue Status Report Send.</summary>
    internal static SecsItem QueueReportBody(IReadOnlyList<ProcessJob> jobs)
    {
        var entries = new SecsItem[jobs.Count];

        for (var i = 0; i < jobs.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(jobs[i].ProcessJobId).Named("PRJOBID"),
                SecsItem.U1((byte)jobs[i].State).Named("JOBSTATE"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S16F29.</summary>
    internal static bool TryReadQueueReport(SecsItem? body, out List<ProcessJobQueueEntry> jobs)
    {
        jobs = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var fields in Entries(body, 2))
        {
            var processJobId = Text(fields[0]);

            if (processJobId.Length == 0)
            {
                continue;
            }

            var (state, code) = ReadState(fields[1]);

            jobs.Add(new ProcessJobQueueEntry
            {
                ProcessJobId = processJobId,
                State = state,
                StateCode = code,
            });
        }

        return true;
    }

    // ------------------------------------------------------------------ material

    /// <summary>S16F15 and S16F25 - an identifier and a list of material.</summary>
    internal static SecsItem MaterialBody(string processJobId, IReadOnlyList<string> material)
        => SecsItem.L(SecsItem.A(processJobId).Named("PRJOBID"), MaterialList(material));

    /// <summary>Reads S16F15 and S16F25.</summary>
    internal static bool TryReadMaterial(SecsItem? body, out string processJobId, out List<string> material)
    {
        processJobId = string.Empty;
        material = [];

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        processJobId = Text(body.Items[0]);
        material = ReadMaterial(body.Items[1]);

        return processJobId.Length > 0;
    }

    // ---------------------------------------------------------------- the queue

    /// <summary>S16F20 - Process Job Queue Position Data.</summary>
    internal static SecsItem QueuePositionBody(string processJobId, ushort position, ProcessJobState state)
        => SecsItem.L(
            SecsItem.A(processJobId).Named("PRJOBID"),
            SecsItem.U2(position).Named("QPOSN"),
            SecsItem.U1((byte)state).Named("JOBSTATE"));

    /// <summary>Reads S16F20.</summary>
    internal static ProcessJobQueuePosition? TryReadQueuePosition(SecsItem? body)
    {
        if (body is null || !body.IsList || body.Count < 3)
        {
            return null;
        }

        var processJobId = Text(body.Items[0]);

        if (processJobId.Length == 0)
        {
            return null;
        }

        var (state, code) = ReadState(body.Items[2]);

        return new ProcessJobQueuePosition
        {
            ProcessJobId = processJobId,
            Position = (ushort)Math.Min(Unsigned(body.Items[1]), ushort.MaxValue),
            State = state,
            StateCode = code,
        };
    }

    /// <summary>S16F21 - Process Job Queue Reorder Send.</summary>
    internal static SecsItem ReorderBody(IReadOnlyList<string> processJobIds) => MaterialList(processJobIds);

    /// <summary>Reads S16F21.</summary>
    internal static List<string> ReadReorder(SecsItem? body) => body is null ? [] : ReadMaterial(body);

    /// <summary>S16F22 - a bare <c>HCACK</c>.</summary>
    internal static SecsItem HostCommandAcknowledgeBody(byte acknowledgement)
        => SecsItem.B(acknowledgement).Named("HCACK");

    /// <summary>Reads S16F22.</summary>
    internal static byte? TryReadHostCommandAcknowledge(SecsItem? body)
        => body is null ? null : body.IsList ? body.Count == 1 ? Code(body.Items[0]) : null : Code(body);

    // -------------------------------------------------------------- parameters

    /// <summary>S16F17 - Process Job Parameter Change Send.</summary>
    internal static SecsItem ParameterChangeBody(string processJobId, IReadOnlyList<ProcessJobParameter> parameters)
    {
        var entries = new SecsItem[parameters.Count];

        for (var i = 0; i < parameters.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(parameters[i].Name).Named("CPNAME"),
                SecsItem.A(parameters[i].Value).Named("CPVAL"));
        }

        return SecsItem.L(SecsItem.A(processJobId).Named("PRJOBID"), SecsItem.L(entries));
    }

    /// <summary>Reads S16F17 and S16F28.</summary>
    internal static bool TryReadParameterChange(
        SecsItem? body,
        out string processJobId,
        out List<ProcessJobParameter> parameters)
    {
        processJobId = string.Empty;
        parameters = [];

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        processJobId = Text(body.Items[0]);

        foreach (var fields in Entries(body.Items[1], 2))
        {
            var name = Text(fields[0]);

            if (name.Length == 0)
            {
                continue;
            }

            parameters.Add(new ProcessJobParameter { Name = name, Value = Value(fields[1]) });
        }

        return processJobId.Length > 0;
    }

    /// <summary>S16F18 - one <c>CPACK</c> per parameter.</summary>
    internal static SecsItem ParameterResultBody(
        string processJobId,
        IReadOnlyList<ProcessJobParameterResult> results)
    {
        var entries = new SecsItem[results.Count];

        for (var i = 0; i < results.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(results[i].Name).Named("CPNAME"),
                SecsItem.B((byte)results[i].Acknowledgement).Named("CPACK"));
        }

        return SecsItem.L(SecsItem.A(processJobId).Named("PRJOBID"), SecsItem.L(entries));
    }

    /// <summary>Reads S16F18.</summary>
    internal static bool TryReadParameterResult(SecsItem? body, out List<ProcessJobParameterResult> results)
    {
        results = [];

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        foreach (var fields in Entries(body.Items[1], 2))
        {
            var name = Text(fields[0]);

            if (name.Length == 0)
            {
                continue;
            }

            results.Add(new ProcessJobParameterResult
            {
                Name = name,
                Acknowledgement = ParameterAcknowledgement(Code(fields[1]) ?? 4),
            });
        }

        return true;
    }

    /// <summary>S16F27 - Process Job Parameter Report Request.</summary>
    internal static SecsItem ParameterRequestBody(string processJobId, IReadOnlyList<string> names)
        => SecsItem.L(SecsItem.A(processJobId).Named("PRJOBID"), NameList(names));

    /// <summary>Reads S16F27.</summary>
    internal static bool TryReadParameterRequest(SecsItem? body, out string processJobId, out List<string> names)
    {
        processJobId = string.Empty;
        names = [];

        if (body is null || !body.IsList || body.Count < 1)
        {
            return false;
        }

        processJobId = Text(body.Items[0]);

        if (body.Count > 1)
        {
            names = ReadMaterial(body.Items[1]);
        }

        return processJobId.Length > 0;
    }

    /// <summary>S16F28 - Process Job Parameter Report Data.</summary>
    internal static SecsItem ParameterReportBody(string processJobId, IReadOnlyList<ProcessJobParameter> parameters)
        => ParameterChangeBody(processJobId, parameters);

    // --------------------------------------------------------------- the pieces

    private static SecsItem MaterialList(IReadOnlyList<string> material)
    {
        var entries = new SecsItem[material.Count];

        for (var i = 0; i < material.Count; i++)
        {
            entries[i] = SecsItem.A(material[i]).Named("MID");
        }

        return SecsItem.L(entries);
    }

    private static SecsItem NameList(IReadOnlyList<string> names)
    {
        var entries = new SecsItem[names.Count];

        for (var i = 0; i < names.Count; i++)
        {
            entries[i] = SecsItem.A(names[i]).Named("CPNAME");
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads a list of identifiers, or one bare identifier.</summary>
    /// <remarks>
    /// A zero-length list is a job with no material, which is a real thing: E40 lets a host bind
    /// material later, and S16F15 exists for exactly that.
    /// </remarks>
    private static List<string> ReadMaterial(SecsItem item)
    {
        List<string> material = [];

        if (!item.IsList)
        {
            var single = Text(item);

            if (single.Length > 0)
            {
                material.Add(single);
            }

            return material;
        }

        foreach (var entry in item.Items)
        {
            var text = entry.IsList && entry.Count > 0 ? Text(entry.Items[0]) : Text(entry);

            if (text.Length > 0)
            {
                material.Add(text);
            }
        }

        return material;
    }

    /// <summary>Reads a <c>JOBSTATE</c>, and says what arrived when it cannot name it.</summary>
    /// <remarks>
    /// Any numeric format, and text too: a machine that sends <c>Processing</c> where this library
    /// writes 3 is understood, because the names are <c>PRSTATE</c>'s own and a machine using them
    /// is not guessing. A code outside the ten the catalogue defines gives a null state and the
    /// byte it arrived as - reading an unnamed code as zero would say the job is queued.
    /// </remarks>
    private static (ProcessJobState? State, byte Code) ReadState(SecsItem item)
    {
        if (item.Format.IsText() && item.Count > 0)
        {
            var text = Text(item);

            if (Enum.TryParse<ProcessJobState>(text, ignoreCase: true, out var named))
            {
                return (named, (byte)named);
            }

            // Digits in a text item: a machine writing the code the slow way.
            return byte.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
                ? (Named(parsed), parsed)
                : (null, 0);
        }

        var code = (byte)Math.Min(Unsigned(item), byte.MaxValue);

        return (Named(code), code);
    }

    private static ProcessJobState? Named(byte code) => code switch
    {
        0 => ProcessJobState.Queued,
        1 => ProcessJobState.SettingUp,
        2 => ProcessJobState.WaitingForStart,
        3 => ProcessJobState.Processing,
        4 => ProcessJobState.ProcessComplete,
        6 => ProcessJobState.Pausing,
        7 => ProcessJobState.Paused,
        8 => ProcessJobState.Stopping,
        9 => ProcessJobState.Aborting,

        // 5 is reserved in PRSTATE and everything above 9 is nobody's, so neither gets a name.
        _ => null,
    };

    private static DateTime? ReadTime(SecsItem item)
    {
        if (!item.Format.IsText() || item.Count == 0)
        {
            return null;
        }

        var text = Text(item);

        string[] patterns = [TimeFormat, "yyyyMMddHHmmss", "yyMMddHHmmss"];

        return DateTime.TryParseExact(
            text,
            patterns,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static ProcessJobAcknowledgement Acknowledgement(byte code)
        => code == 0 ? ProcessJobAcknowledgement.Accepted : ProcessJobAcknowledgement.Rejected;

    /// <summary>Reads a <c>CMDA</c>, with an undefined code reading as the unfavourable one.</summary>
    /// <remarks>
    /// The rule the whole stack follows for acknowledgements: a code E5 does not define never
    /// reads as success. Here the code is 2, "cannot perform now", rather than 1 - a machine
    /// answering with something this library cannot read has the command, or it would have said 1
    /// itself.
    /// </remarks>
    private static ProcessJobCommandAcknowledgement CommandAcknowledgement(byte code) => code switch
    {
        0 => ProcessJobCommandAcknowledgement.Performed,
        1 => ProcessJobCommandAcknowledgement.DoesNotExist,
        _ => ProcessJobCommandAcknowledgement.CannotPerformNow,
    };

    private static ProcessJobParameterAcknowledgement ParameterAcknowledgement(byte code) => code switch
    {
        0 => ProcessJobParameterAcknowledgement.Accepted,
        1 => ProcessJobParameterAcknowledgement.NameDoesNotExist,
        2 => ProcessJobParameterAcknowledgement.IllegalValue,
        3 => ProcessJobParameterAcknowledgement.IllegalFormat,
        _ => ProcessJobParameterAcknowledgement.CannotChangeNow,
    };

    private static List<SecsItem[]> Entries(SecsItem body, int width)
    {
        List<SecsItem[]> entries = [];

        if (!body.IsList || body.Count == 0)
        {
            return entries;
        }

        if (body.Items[0].IsList)
        {
            foreach (var entry in body.Items)
            {
                if (entry.IsList && entry.Count >= width)
                {
                    entries.Add([.. entry.Items[..width]]);
                }
            }

            return entries;
        }

        for (var i = 0; i + width <= body.Count; i += width)
        {
            entries.Add([.. body.Items[i..(i + width)]]);
        }

        return entries;
    }

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    /// <summary>Reads a value that should be text out of whatever format it arrived in.</summary>
    private static string Value(SecsItem item)
    {
        if (item.Format.IsText())
        {
            return Text(item);
        }

        if (item.IsList || item.Count == 0)
        {
            return string.Empty;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U8 => item.FirstValueOrDefault<ulong>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I1 => item.FirstValueOrDefault<sbyte>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I2 => item.FirstValueOrDefault<short>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I4 => item.FirstValueOrDefault<int>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I8 => item.FirstValueOrDefault<long>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.F4 => item.FirstValueOrDefault<float>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.F8 => item.FirstValueOrDefault<double>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.Boolean => item.FirstValueOrDefault<bool>().ToString(),
            _ => string.Empty,
        };
    }

    private static byte? Code(SecsItem item)
        => item.IsList || item.Count == 0 ? null : (byte)Math.Min(Unsigned(item), byte.MaxValue);

    private static ulong Unsigned(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return 0;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>(),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>(),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>(),
            SecsFormat.U8 => item.FirstValueOrDefault<ulong>(),
            SecsFormat.I1 => NonNegative(item.FirstValueOrDefault<sbyte>()),
            SecsFormat.I2 => NonNegative(item.FirstValueOrDefault<short>()),
            SecsFormat.I4 => NonNegative(item.FirstValueOrDefault<int>()),
            SecsFormat.I8 => NonNegative(item.FirstValueOrDefault<long>()),
            SecsFormat.ASCII or SecsFormat.JIS8 => ulong.TryParse(
                Text(item),
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private static ulong NonNegative(long value) => value < 0 ? 0 : (ulong)value;
}
