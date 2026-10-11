using NcmForge.Core.Pipeline;

namespace NcmForge.App.ViewModels;

/// <summary>
/// A labelled format choice for the output-format combo box.
/// </summary>
/// <param name="CodecKey">
/// Set when the entry names one specific codec from <see cref="CodecLadder"/>; null for the
/// higher-level modes such as "automatic" or "copy".
/// </param>
public sealed record FormatChoice(string Label, OutputFormat Value, string? CodecKey = null);

public sealed record SurroundChoice(string Label, SurroundMapping Value);

public sealed record CoverChoice(string Label, CoverMode Value);

public sealed record LyricsChoice(string Label, LyricsMode Value);

public sealed record DecoderInputChoice(string Label, ExternalDecoderInput Value);

public sealed record HardwareChoice(string Label, HardwareAcceleration Value);
