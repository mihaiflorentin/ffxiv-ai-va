namespace AIVoiceActing.Domain;

/// <summary>Plain-language notes about the voice file size setting, shared by the engine and the pages that show its status.</summary>
public static class PrecisionNotes
{
    /// <summary>Status reason while a full-size pin is overridden by Processor = Graphics card.</summary>
    public const string FullSizeIgnoredOnGpu =
        "Using the lighter voice files on your graphics card; full size only runs on the processor.";
}
