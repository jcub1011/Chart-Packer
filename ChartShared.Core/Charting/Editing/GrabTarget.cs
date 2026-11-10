namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// What a press takes hold of and drags at a point (<see cref="EditorTools.GrabAt"/>): a note, and whether the pointer is
/// over its gem or its sustain's tail, or a phrase's or a disco flip section's bar in the phrase rail, the others null.
/// </summary>
public readonly record struct GrabTarget(EditableNote? Note = null, bool IsTail = false, Phrase? Phrase = null, DiscoFlipSection? DiscoFlip = null);
