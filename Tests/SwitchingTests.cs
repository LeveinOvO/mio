using PortForwarder.Core;

internal static class SwitchingTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string text)
        { if (!condition) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        var original = new ForwardRule("127.0.0.1", 18080, "192.0.2.10", 8080);
        var items = Enumerable.Range(0, 3).Select(i => new RuleBatchItem(RuleOperation.Disable, original with { ListenPort = 18080 + i }, null)).ToArray();
        var visited = new List<int>();
        var result = await BatchRuleOperations.ExecuteAsync(items, item =>
        {
            visited.Add(item.Original.ListenPort);
            if (item.Original.ListenPort == 18081) throw new IOException("Simulated failure");
            return Task.CompletedTask;
        });
        Check(!result.Success && result.Items!.Count(r => r.Success) == 2 && visited.Count == 3, "Batch continues after item failure");
        Check(result.Items!.Single(r => !r.Success).Listener == "127.0.0.1:18081", "Batch reports correct failed item");
        visited.Clear();
        try { await BatchRuleOperations.ExecuteAsync([items[0], items[0]], _ => { visited.Add(1); return Task.CompletedTask; }); throw new Exception("Expected duplicate"); }
        catch (ArgumentException) { Check(visited.Count == 0, "Batch duplicate rejected before writes"); }
        try { await BatchRuleOperations.ExecuteAsync([items[0] with { Operation = RuleOperation.Add }], _ => Task.CompletedTask); throw new Exception("Expected invalid action"); }
        catch (ArgumentException) { Check(true, "Batch accepts only enable, disable, delete"); }
        var mixed = new[] { items[0], items[0] with { Original = original with { Protocol = ForwardProtocol.Udp } } };
        result = await BatchRuleOperations.ExecuteAsync(mixed, _ => Task.CompletedTask);
        Check(result.Success, "Same-port different protocols accepted in batch");
        return count;
    }
}
