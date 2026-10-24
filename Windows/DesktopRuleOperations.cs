using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class DesktopRuleOperations
{
    // GUI is elevated once at launch; no helper process or second UAC prompt per operation.
    public static async Task<RuleResult> ExecuteAsync(RuleOperation operation, ForwardRule? original, ForwardRule? updated,
        RealmRule? expected = null, List<RuleBatchItem>? items = null)
    {
        if (operation == RuleOperation.Batch)
            return await BatchRuleOperations.ExecuteAsync(items ?? throw new ArgumentException("请选择需要操作的转发规则。"),
                item => DispatchAsync(item.Operation, item.Original, item.Updated, item.Expected));
        await DispatchAsync(operation, original, updated, expected);
        return new RuleResult(true, operation switch
        {
            RuleOperation.Add => "转发规则已添加。",
            RuleOperation.Edit => "转发规则已更新。",
            RuleOperation.Delete => "选中规则及对应配置已删除。",
            RuleOperation.Enable => "转发已启用。",
            RuleOperation.Disable => "转发已停用，配置保留。",
            _ => "操作完成。"
        });
    }

    private static async Task DispatchAsync(RuleOperation operation, ForwardRule? original, ForwardRule? updated, RealmRule? expected)
    {
        if (operation is not (RuleOperation.Add or RuleOperation.Edit or RuleOperation.Delete or RuleOperation.Enable or RuleOperation.Disable))
            throw new ArgumentException("不支持此操作。");
        if (operation != RuleOperation.Add) RuleValidation.Validate(original!);
        if (operation is RuleOperation.Add or RuleOperation.Edit) RuleValidation.Validate(updated!);
        var firewall = original is null ? null : await FirewallOperations.PrepareAsync(original, operation, updated, expected);
        try
        {
            await RealmRuntime.ExecuteAsync(operation, original, updated, expected);
        }
        catch (Exception error)
        {
            if (firewall is not null)
                throw new InvalidOperationException(error.Message + "\n端口转发操作未完成；对应防火墙规则可能已禁用，请打开“防火墙”检查。", error);
            throw;
        }
        try { await FirewallOperations.CompleteAsync(firewall, operation, updated); }
        catch (Exception error)
        {
            throw new InvalidOperationException("转发操作已完成，但防火墙同步失败：" + error.Message + " 请单独打开防火墙配置检查。", error);
        }
    }
}
