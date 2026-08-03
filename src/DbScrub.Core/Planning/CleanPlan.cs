using DbScrub.Core.Configuration;
using DbScrub.Core.Hygiene;
using DbScrub.Core.Masking;
using DbScrub.Core.Verdicts;

namespace DbScrub.Core.Planning;

/// <summary>
/// Everything a `clean` run would do, in execution order, decided without
/// touching the database.
///
/// This type exists so `report` and `clean` cannot drift apart. SPEC section 2
/// says report "prints ... what `clean` WOULD do", and the only way to keep that
/// true is for both commands to build the same object — one to render it, the
/// other to execute it. A report that describes the plan in its own words is a
/// report that eventually describes a plan nobody is running.
///
/// The ORDER of the four members is the order things happen, and the split
/// between <see cref="PreMask"/> and <see cref="PostMask"/> is load-bearing:
/// system versioning is detached in the first, and only reattached in the last,
/// so that masking happens in between (see HygienePlanner).
/// </summary>
public sealed record CleanPlan(
    ScrubPlan Scrub,
    IReadOnlyList<HygieneStep> PreMask,
    MaskPlan Mask,
    IReadOnlyList<HygieneStep> PostMask)
{
    public static CleanPlan Build(ScrubPlan scrub) => new(
        Scrub: scrub,
        PreMask: HygienePlanner.BuildPreMask(scrub),
        Mask: MaskPlanner.Build(scrub),
        PostMask: HygienePlanner.BuildPostMask(scrub));

    /// <summary>
    /// Everything that blocks a run: config-versus-schema problems from the
    /// verdict pass, plus the ones only the mask planner can see (a static value
    /// the column cannot hold, a scramble with no key to batch on).
    /// </summary>
    public IReadOnlyList<ConfigError> Problems => [.. Scrub.Problems, .. Mask.Problems];

    public bool CanRun => Problems.Count == 0;

    /// <summary>
    /// Tables that will be rewritten one row at a time without a bounded
    /// transaction, because they have no primary key (SPEC 5.3). Surfaced
    /// because it is the one thing in a plan that can behave very differently at
    /// scale than the plan suggests.
    /// </summary>
    public IEnumerable<TableMaskPlan> Unbatched =>
        Mask.Tables.Where(t => t.Mode == MaskMode.WholeTable);
}
