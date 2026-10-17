namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;

public enum ChoiceOutcome
{
    /// <summary>Everything the tier needs is on disk: write the settings now.</summary>
    Applied,

    /// <summary>Files are missing; the tier waits (<see cref="QualityChoiceModel.Pending"/>) until they are downloaded.</summary>
    NeedsDownload,

    /// <summary>The tier cannot start here; <see cref="QualityChoiceModel.Notice"/> says why.</summary>
    Blocked,
}

/// <summary>
/// What happens when the player picks a quality tier in Settings. A tier whose files are on disk
/// applies at once. A tier that still needs a download does not switch the voices yet: it waits as
/// <see cref="Pending"/> so the current voices keep working, and applies when its files are all
/// there. A download that ends early keeps the tier pending (with a notice) so it can be resumed.
/// </summary>
public sealed class QualityChoiceModel
{
    /// <summary>The tier waiting for its download; null when nothing is waiting.</summary>
    public QualityTier? Pending { get; private set; }

    /// <summary>A one-line note for the card (blocked reason, stopped download); cleared by the next choice.</summary>
    public string? Notice { get; private set; }

    private bool sawDownload;

    public ChoiceOutcome Choose(QualityTier tier, SetupPlan plan)
    {
        this.Notice = null;
        this.sawDownload = false;
        if (plan.Blocked is { } blocked)
        {
            this.Pending = null;
            this.Notice = blocked;
            return ChoiceOutcome.Blocked;
        }

        if (plan.ToDownload.Count == 0)
        {
            this.Pending = null;
            return ChoiceOutcome.Applied;
        }

        this.Pending = tier;
        return ChoiceOutcome.NeedsDownload;
    }

    /// <summary>Gives up on the waiting tier. Files already downloaded stay on disk.</summary>
    public void Cancel()
    {
        this.Pending = null;
        this.Notice = null;
        this.sawDownload = false;
    }

    /// <summary>
    /// Call once per frame. Returns the pending tier once its files are installed and no download
    /// is running, so the page can apply it; otherwise null.
    /// </summary>
    public QualityTier? TakeReady(bool downloadActive, bool installed)
    {
        if (this.Pending is not { } tier)
        {
            return null;
        }

        if (downloadActive)
        {
            this.sawDownload = true;
            this.Notice = null;
            return null;
        }

        if (installed)
        {
            this.Cancel();
            return tier;
        }

        if (this.sawDownload)
        {
            this.sawDownload = false;
            this.Notice = "The download stopped before it finished. Your current voices are unchanged. Download again to continue where it left off.";
        }

        return null;
    }
}
