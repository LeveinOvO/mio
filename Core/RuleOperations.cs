namespace PortForwarder.Core;

public enum RuleOperation { Add, Edit, Delete, Enable, Disable, Batch }

public sealed record RuleBatchItem(RuleOperation Operation, ForwardRule Original, ForwardRule? Updated, RealmRule? Expected = null);
public sealed record RuleItemResult(string Listener, bool Success, string Message);
public sealed record RuleResult(bool Success, string Message, List<RuleItemResult>? Items = null);
