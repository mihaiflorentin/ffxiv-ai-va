namespace AIVoiceActing.Domain;

/// <summary>
/// Knobs of the chunked speech decode. Sizes are in speech tokens (25 per second of audio). The first
/// chunk is small so audio starts early; later ones grow by <see cref="Growth"/> up to
/// <see cref="MaxTokens"/>, which amortizes the fixed cost every decoder call pays for its reference prompt.
/// Each decoder window also carries <see cref="LeftContextTokens"/> already-decoded tokens before the chunk
/// and up to <see cref="LookaheadTokens"/> tokens after it: the decoder attends both ways, so the audio
/// near a window edge is worse than in the middle, and the context pushes the edges outside the part that
/// is kept.
/// </summary>
public sealed record ChunkScheduleOptions(
    int FirstTokens = 32,
    double Growth = 1.5,
    int MaxTokens = 120,
    int MinTailTokens = 16,
    int LeftContextTokens = 12,
    int LookaheadTokens = 6)
{
    public static ChunkScheduleOptions Default { get; } = new();

    /// <summary>Throws <see cref="ArgumentException"/> when the numbers cannot describe a working schedule.</summary>
    public void Validate()
    {
        if (this.FirstTokens < 1 || this.MaxTokens < this.FirstTokens)
        {
            throw new ArgumentException("First chunk must be at least one token and no larger than the maximum.");
        }

        if (this.Growth < 1.0 || double.IsNaN(this.Growth))
        {
            throw new ArgumentException("Chunk growth must be at least 1.");
        }

        if (this.LeftContextTokens < 0 || this.LookaheadTokens < 0 || this.MinTailTokens < this.LookaheadTokens)
        {
            throw new ArgumentException("Context cannot be negative and the minimum tail must cover the lookahead.");
        }
    }
}

/// <summary>
/// One decoder call. <see cref="Start"/>..<see cref="End"/> is the chunk the call is responsible for;
/// <see cref="WindowStart"/>..<see cref="WindowEnd"/> adds the context tokens that are decoded but not kept
/// (all indices are positions in the speech-token stream, End exclusive).
/// </summary>
public sealed record ChunkPlan(int Index, int Start, int End, int WindowStart, int WindowEnd, bool IsLast)
{
    public bool IsFirst => this.Index == 0;

    public int Count => this.End - this.Start;

    public int LeftContext => this.Start - this.WindowStart;

    public int Lookahead => this.WindowEnd - this.End;
}

/// <summary>
/// Pure scheduler for chunked decoding: given how many tokens exist so far and whether the language model
/// is done, says which window to decode next, or that it must wait. Chunks cover the stream exactly once,
/// in order, with no gaps; a leftover tail shorter than <see cref="ChunkScheduleOptions.MinTailTokens"/>
/// joins the last chunk instead of costing a decoder call of its own.
/// </summary>
public sealed class ChunkSchedule
{
    private int start;
    private int index;

    private readonly Func<int>? capNow;

    /// <param name="capNow">
    /// When given, asked for the largest chunk to plan right now (see <see cref="CapFor"/>): a slow language model
    /// gets small chunks, so the audio comes in steady pieces and the pre-roll can stay short.
    /// </param>
    public ChunkSchedule(ChunkScheduleOptions? options = null, Func<int>? capNow = null)
    {
        this.Options = options ?? ChunkScheduleOptions.Default;
        this.Options.Validate();
        this.capNow = capNow;
    }

    public ChunkScheduleOptions Options { get; }

    /// <summary>Tokens already handed out in plans.</summary>
    public int Consumed => this.start;

    /// <summary>Planned chunk size for the chunk with this index.</summary>
    public int SizeFor(int chunkIndex) => SizeFor(this.Options, chunkIndex, this.capNow?.Invoke() ?? this.Options.MaxTokens);

    /// <summary>Planned size of chunk <paramref name="chunkIndex"/> when no chunk may exceed <paramref name="cap"/> tokens (never below the first chunk's size).</summary>
    public static int SizeFor(ChunkScheduleOptions options, int chunkIndex, int cap)
    {
        var size = options.FirstTokens * Math.Pow(options.Growth, chunkIndex);
        var limit = Math.Clamp(cap, options.FirstTokens, options.MaxTokens);
        return (int)Math.Min(limit, Math.Round(size));
    }

    /// <summary>
    /// The largest chunk worth planning for a language model that makes <paramref name="rate"/> seconds of speech per
    /// second of work. A chunk can only be decoded once its last token exists, so with a model near real time a big
    /// chunk is a long wait that the pre-roll has to cover (a 120-token chunk is 4.8 s of speech). Twenty tokens per
    /// unit of rate keeps that wait around a second and a half to three seconds; a model several times faster than
    /// real time gets the full size, where the cost of every decoder call is the thing to save. NaN or infinity (no
    /// pace known) means the full size.
    /// </summary>
    public static int CapFor(ChunkScheduleOptions options, double rate) =>
        double.IsNaN(rate) || double.IsInfinity(rate)
            ? options.MaxTokens
            : Math.Clamp((int)Math.Round(rate * 20.0), options.FirstTokens, options.MaxTokens);

    /// <summary>
    /// The next window, or null: either wait for more tokens (<paramref name="finished"/> is false) or
    /// the stream is fully handed out. While the stream is still growing a chunk is only released once
    /// its lookahead tokens exist, so the decoder always sees what follows the part it keeps.
    /// </summary>
    /// <param name="available">Tokens in the stream so far, including any closing silence once finished.</param>
    /// <param name="finished">No token will be added after <paramref name="available"/>.</param>
    public ChunkPlan? TryTake(int available, bool finished)
    {
        if (available <= this.start && finished)
        {
            return null;
        }

        var end = this.start + this.SizeFor(this.index);
        bool last;
        if (finished)
        {
            if (available - end < this.Options.MinTailTokens)
            {
                end = available;
            }

            last = end >= available;
        }
        else
        {
            if (available < end + this.Options.LookaheadTokens)
            {
                return null;
            }

            last = false;
        }

        var windowStart = Math.Max(0, this.start - this.Options.LeftContextTokens);
        var windowEnd = last ? end : Math.Min(available, end + this.Options.LookaheadTokens);
        var plan = new ChunkPlan(this.index, this.start, end, windowStart, windowEnd, last);
        this.start = end;
        this.index++;
        return plan;
    }
}
