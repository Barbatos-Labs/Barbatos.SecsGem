// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// S7F23's body, which is also S7F26's: PPID, MDLN, SOFTREV and a list of CCODE commands.
/// </summary>
/// <remarks>
/// One shape, read and written by both ends - the equipment receives S7F23 and answers S7F26, the
/// host does the reverse - so it lives here rather than twice. A structure defined in two places
/// agrees until somebody fixes one of them.
/// </remarks>
internal static class GemFormattedProcessProgramCodec
{
    /// <summary>Reads the body.</summary>
    /// <param name="body">The inbound body.</param>
    /// <param name="program">The program, or <see langword="null"/> when the body could not be read.</param>
    /// <returns>The ACKC7 to answer with when <paramref name="program"/> is <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// Two different refusals, because they are two different problems for the sender to fix. A
    /// body that is not the shape E5 defines is <c>LengthError</c> - the same code S7F3 uses for a
    /// body this stack could not read, since ACKC7 has none meaning "that made no sense". A body
    /// that is the right shape but carries a CCODE or a PPARM in a form this stack does not
    /// represent is <c>ModeUnsupported</c>: the equipment does formatted process programs, just not
    /// that flavour of one, and a sender told "length error" would go looking for the wrong thing.
    /// </para>
    /// <para>
    /// Nothing is read leniently. Mangling a recipe into something storable would be worse than
    /// refusing it, because the sender would be told its recipe landed.
    /// </para>
    /// </remarks>
    internal static GemProcessProgramAcknowledgement TryRead(
        SecsItem? body,
        out GemFormattedProcessProgram? program)
    {
        program = null;

        if (body is not { IsList: true, Count: 4 }
            || !body[0].Format.IsText()
            || !body[1].Format.IsText()
            || !body[2].Format.IsText()
            || !body[3].IsList)
        {
            return GemProcessProgramAcknowledgement.LengthError;
        }

        var commands = new List<GemProcessProgramCommand>(body[3].Count);

        foreach (var entry in body[3].Items)
        {
            if (entry is not { IsList: true, Count: 2 } || !entry[1].IsList)
            {
                return GemProcessProgramAcknowledgement.LengthError;
            }

            // CCODE in whatever numeric format the sender chose, the way every other identifier in
            // this layer is read - but E5 types it U1[1], so one that does not fit in a byte is a
            // different command, not this one.
            if (!GemItems.TryReadId(entry[0], out var code) || code > byte.MaxValue)
            {
                return GemProcessProgramAcknowledgement.ModeUnsupported;
            }

            var parameters = new List<string>(entry[1].Count);

            foreach (var parameter in entry[1].Items)
            {
                if (!parameter.Format.IsText())
                {
                    return GemProcessProgramAcknowledgement.ModeUnsupported;
                }

                // Not trimmed: S7F26 has to give back what arrived, and a trim is not reversible.
                parameters.Add(parameter.Count == 0 ? string.Empty : parameter.GetString());
            }

            commands.Add(new GemProcessProgramCommand((byte)code, parameters));
        }

        program = new GemFormattedProcessProgram(
            body[0].GetString().TrimEnd(),
            body[1].GetString().TrimEnd(),
            body[2].GetString().TrimEnd(),
            commands);

        return GemProcessProgramAcknowledgement.Accepted;
    }

    /// <summary>Writes the body.</summary>
    /// <param name="program">The program.</param>
    /// <returns>The body item.</returns>
    internal static SecsItem Write(GemFormattedProcessProgram program)
    {
        var commands = new List<SecsItem>(program.Commands.Count);

        foreach (var command in program.Commands)
        {
            var parameters = new List<SecsItem>(command.Parameters.Count);

            foreach (var parameter in command.Parameters)
            {
                parameters.Add(SecsItem.A(parameter).Named("PPARM"));
            }

            commands.Add(SecsItem.L(
                SecsItem.U1(command.Code).Named("CCODE"),
                SecsItem.L(parameters)));
        }

        return SecsItem.L(
            SecsItem.A(program.Id).Named("PPID"),
            SecsItem.A(program.ModelName).Named("MDLN"),
            SecsItem.A(program.SoftwareRevision).Named("SOFTREV"),
            SecsItem.L(commands));
    }
}
