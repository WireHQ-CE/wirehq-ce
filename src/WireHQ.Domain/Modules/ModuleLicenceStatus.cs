namespace WireHQ.Domain.Modules;

/// <summary>
/// The lifecycle state of a stored, activated CE Marketplace module licence. <see cref="Active"/> grants its
/// feature keys while in grace; <see cref="Revoked"/> is the authoritative disable delivered by the hosted
/// licensing service's verify response (docs/29 M-7) — a revoked licence never grants, distinct from a merely
/// lapsed one (past its offline grace window, which the evaluator derives from the grace boundary, not this
/// status).
///
/// <para>
/// Kept core (in Domain, the layer both halves can see): the CE-only <c>ModuleLicence</c> entity persists it,
/// and the kept-core <c>ActivatedModuleEvaluator</c> / <c>ActivatedModuleRecord</c> in the Application layer read
/// it — so the evaluator's revoke/grace logic is unit-tested on the main CI even though the entity is overlaid
/// CE-only. (docs/29-ce-marketplace-modules.md M-4/M-7/M-17)
/// </para>
/// </summary>
public enum ModuleLicenceStatus
{
    Active,
    Revoked,
}
