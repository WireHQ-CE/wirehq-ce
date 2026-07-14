using WireHQ.Domain.Modules;

namespace WireHQ.Application.Entitlements;

/// <summary>
/// A stored activated module licence reduced to the fields that decide whether it currently unlocks its
/// capability: the module slug (read from the licence KEY's <c>mod</c> claim at activation — the activation
/// token lacks it, docs/29 M-6), the lifecycle <see cref="Status"/>, and the offline-grace hard boundary
/// <see cref="GraceEndsUtc"/> (the activation token's <c>exp</c>; <c>null</c> before the first online
/// verification — a Wave-2 local activation grants until Wave 3's call-home stamps a grace window). Kept-core
/// so <see cref="ActivatedModuleEvaluator"/> is unit-tested on the main CI; the CE persistence adapter maps its
/// <c>module_licences</c> rows onto this record. (docs/29 M-4/M-17)
/// </summary>
public sealed record ActivatedModuleRecord(string ModuleSlug, ModuleLicenceStatus Status, DateTimeOffset? GraceEndsUtc);
