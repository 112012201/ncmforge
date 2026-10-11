using NcmForge.Core.Pipeline;

namespace NcmForge.App.ViewModels;

/// <summary>A labelled enum choice for the settings combo boxes.</summary>
public sealed record FormatChoice(string Label, OutputFormat Value);

public sealed record SurroundChoice(string Label, SurroundMapping Value);

public sealed record CoverChoice(string Label, CoverMode Value);

public sealed record LyricsChoice(string Label, LyricsMode Value);

public sealed record DecoderInputChoice(string Label, ExternalDecoderInput Value);

public sealed record HardwareChoice(string Label, HardwareAcceleration Value);
