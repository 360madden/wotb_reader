namespace WotBTreader.Overlay.ViewModels;

/// <summary>One position sample projected onto the ground plane for plotting.</summary>
/// <param name="X">World X coordinate.</param>
/// <param name="Y">World Z coordinate (mapped to canvas Y).</param>
/// <param name="TeamNumber">Team identifier for colouring (1 = blue, 2 = red).</param>
/// <param name="ParticipantId">The participant this position belongs to, used for velocity trail grouping.</param>
public sealed record PlotPoint(double X, double Y, int TeamNumber, string? ParticipantId = null);
