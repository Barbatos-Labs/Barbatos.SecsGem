// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// Placeholder marking the GEM (SEMI E30) layer, delivered in Phase 7.
/// </summary>
/// <remarks>
/// The project exists from day one so the dependency direction
/// (<c>Gem</c> -&gt; <c>Hsms</c> -&gt; <c>Core</c>) is fixed before any code depends on it.
/// Planned contents: communication and control state models, S1F13/S1F14
/// establish-communications, collection events (CEID/RPTID), status and equipment
/// constants (SVID/ECID), alarms (ALID) and remote commands.
/// </remarks>
internal static class GemRoadmap
{
    internal const string Phase = "Phase 7";
}