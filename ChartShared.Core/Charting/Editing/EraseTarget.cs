namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// What the erase tool takes away at a point (<see cref="EditorTools.EraseTargetAt"/>): one of a note, a phrase or a disco
/// flip section, the others null.
/// </summary>
public readonly record struct EraseTarget(EditableNote? Note = null, Phrase? Phrase = null, DiscoFlipSection? DiscoFlip = null);
