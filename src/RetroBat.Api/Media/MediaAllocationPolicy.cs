namespace RetroBat.Api.Media;

/// <summary>LOT 5 (§10.1) — how APIExpose writes a resolved medium into a generic ES gamelist slot.</summary>
public enum MediaWritePolicy
{
    /// <summary>Default: fill an empty slot and update a slot APIExpose still owns, but NEVER
    /// overwrite a value the user set (or one APIExpose owned but the user has since changed).</summary>
    FillMissing,

    /// <summary>APIExpose manages the slots it allocated; a manual external change hands ownership
    /// back to the user. Behaves like <see cref="FillMissing"/> for the update/preserve decision.</summary>
    Managed,

    /// <summary>Deliberate reallocation — overwrite whatever is there. Only on an explicit user
    /// action.</summary>
    Force
}

/// <summary>What to do with one slot after applying the policy.</summary>
public readonly record struct MediaWriteDecision(
    bool Write,
    string? Value,
    bool MarkManaged,
    bool AbandonOwnership)
{
    public static readonly MediaWriteDecision Skip = new(false, null, false, false);
}

/// <summary>
/// LOT 5 — the pure decision at the heart of Media Allocation, kept out of the gamelist-writing
/// machinery so it can be reasoned about and tested on its own. Given the policy, the value
/// APIExpose resolved for a slot, what the gamelist currently holds, and whether APIExpose OWNS
/// that current value (it wrote it and the value has not changed since), it returns whether to
/// write, what, and how ownership moves. The invariant: FillMissing/Managed never clobber a user
/// binding, and a slot APIExpose owned but the user edited is preserved AND released.
/// </summary>
public static class MediaAllocationPolicy
{
    public static MediaWriteDecision Decide(
        MediaWritePolicy policy,
        string? preferred,
        string? existing,
        bool apiExposeOwnsExisting)
    {
        var hasPreferred = !string.IsNullOrWhiteSpace(preferred);
        var hasExisting = !string.IsNullOrWhiteSpace(existing);

        if (policy == MediaWritePolicy.Force)
        {
            // Deliberate reallocation: write the resolved value when there is one, and take
            // ownership; with nothing to write, leave the slot alone.
            return hasPreferred
                ? new MediaWriteDecision(Write: true, Value: preferred, MarkManaged: true, AbandonOwnership: false)
                : MediaWriteDecision.Skip;
        }

        // FillMissing / Managed
        if (!hasExisting)
        {
            // Empty slot: fill it and mark it ours, when we have something to put there.
            return hasPreferred
                ? new MediaWriteDecision(Write: true, Value: preferred, MarkManaged: true, AbandonOwnership: false)
                : MediaWriteDecision.Skip;
        }

        if (apiExposeOwnsExisting)
        {
            // Still ours: update it when the resolved value moved; otherwise nothing to do.
            if (hasPreferred && !string.Equals(preferred, existing, StringComparison.Ordinal))
            {
                return new MediaWriteDecision(Write: true, Value: preferred, MarkManaged: true, AbandonOwnership: false);
            }

            return MediaWriteDecision.Skip;
        }

        // Non-empty and NOT ours — a user binding, or one we owned but the user has changed.
        // Preserve it untouched, and release any ownership we thought we had.
        return new MediaWriteDecision(Write: false, Value: null, MarkManaged: false, AbandonOwnership: true);
    }

    public static MediaWritePolicy Parse(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "force" => MediaWritePolicy.Force,
        "managed" => MediaWritePolicy.Managed,
        _ => MediaWritePolicy.FillMissing
    };
}
