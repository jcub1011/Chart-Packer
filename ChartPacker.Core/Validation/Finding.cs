namespace ChartPacker.Core.Validation;

/// <summary>One problem found while reading or validating a <c>.sng</c> or song folder.</summary>
/// <param name="RuleId">The stable rule ID from project spec §6, e.g. <see cref="SngRules.Sng003"/>.</param>
/// <param name="Severity">How serious the problem is.</param>
/// <param name="Message">A plain-language description, ready to show to the user.</param>
/// <param name="Offset">Byte offset in the <c>.sng</c> of the structure at fault, when there is one.</param>
public sealed record Finding(string RuleId, Severity Severity, string Message, long? Offset = null);
