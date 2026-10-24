namespace PortForwarder.Core;

public static class BatchRuleOperations
{
    public static async Task<RuleResult> ExecuteAsync(IReadOnlyList<RuleBatchItem> items,
        Func<RuleBatchItem, Task> apply)
    {
        if (items.Count is < 1 or > 100) throw new ArgumentException("每批请选择 1–100 条规则。");
        if (items.Any(i => i.Operation is not (RuleOperation.Enable or RuleOperation.Disable or RuleOperation.Delete)))
            throw new ArgumentException("不支持的批量操作。");
        if (items.Select(i => i.Original.ListenerKey).Distinct().Count() != items.Count)
            throw new ArgumentException("所选转发规则的监听地址和端口重复，请重新选择。");
        foreach (var item in items) RuleValidation.Validate(item.Original);
        var results = new List<RuleItemResult>();
        foreach (var item in items)
        {
            try
            {
                await apply(item);
                results.Add(new RuleItemResult(item.Original.ListenEndpoint, true, "成功"));
            }
            catch (Exception error) { results.Add(new RuleItemResult(item.Original.ListenEndpoint, false, error.Message)); }
        }
        var succeeded = results.Count(r => r.Success);
        var errors = results.Where(r => !r.Success).Select(r => $"{r.Listener}：{r.Message}");
        return new RuleResult(succeeded == results.Count, $"批量操作：{succeeded} 项成功，{results.Count - succeeded} 项失败。" +
            (succeeded < results.Count ? "\n" + string.Join("\n", errors) : ""), results);
    }
}
