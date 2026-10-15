namespace ChartShared.Core.Validation;

/// <summary>One problem found while reading or validating a <c>.sng</c> or song folder.</summary>
/// <param name="RuleId">The stable rule ID from project spec §6, e.g. <see cref="SngRules.Sng003"/>.</param>
/// <param name="Severity">How serious the problem is.</param>
/// <param name="Message">A plain-language description, ready to show to the user.</param>
/// <param name="Offset">Byte offset in the <c>.sng</c> of the structure at fault, when there is one.</param>
/// <param name="Key">The metadata key at fault, for a finding about one key's pair.</param>
/// <param name="Location">The place in a chart at fault, for a finding about the chart's contents.</param>
/// <param name="Tick">
/// For a chart finding about the song as a whole rather than a part, such as a tempo marker, the tick at fault; null
/// otherwise.
/// </param>
/// <param name="Track">
/// For a chart finding about a vocal part, the name of its MIDI track, such as <c>HARM2</c>, with
/// <paramref name="Tick"/> the tick at fault in it; null otherwise.
/// </param>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    string Message,
    long? Offset = null,
    string? Key = null,
    ChartLocation? Location = null,
    long? Tick = null,
    string? Track = null);
