using System.Collections.ObjectModel;
using System.Collections.Specialized;
using PortForwarder.Core;
using PortForwarder.Services;

internal static class ListRefreshTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string text)
        { if (!condition) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        var rule = new ForwardRule("127.0.0.1", 18080, "192.0.2.10", 8080);
        var entry = new RealmRule(Guid.NewGuid(), rule with { ConnectAddress = "service.example.com" }, rule, true, "正常", DateTimeOffset.UtcNow);
        var first = new RealmRuntimeState(entry, true);
        var second = new RealmRuntimeState(entry with { Id = Guid.NewGuid(), Desired = rule with { ListenPort = 18081 }, Applied = rule with { ListenPort = 18081 } }, true);
        var rows = new ObservableCollection<RuleRow>();
        RuleListUpdater.Apply(rows, [first, second]);
        var selected = rows[0]; selected.IsChecked = true;
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        var properties = new List<string>(); selected.PropertyChanged += (_, e) => properties.Add(e.PropertyName!);
        RuleListUpdater.Apply(rows, [first, second]);
        Check(events.Count == 0 && properties.Count == 0, "Unchanged refresh generates zero notifications");
        var timed = first with { Rule = first.Rule with { LastChecked = first.Rule.LastChecked!.Value.AddMinutes(1) } };
        RuleListUpdater.Apply(rows, [timed, second]);
        Check(properties.SequenceEqual([nameof(RuleRow.Tooltip)]), "Timestamp changes update only tooltip");
        properties.Clear();
        var off = timed with { Rule = timed.Rule with { Enabled = false }, Running = false };
        RuleListUpdater.Apply(rows, [off, second]);
        Check(ReferenceEquals(rows[0], selected) && !selected.IsEnabled && selected.IsChecked && events.Count == 0, "Disable preserves row identity and checkbox");
        Check(properties.Contains(nameof(RuleRow.IsEnabled)) && properties.Contains(nameof(RuleRow.Details)), "Enable-state changes notify bindings");
        RuleListUpdater.Apply(rows, [second, off]);
        Check(events.SequenceEqual([NotifyCollectionChangedAction.Move]), "Reorder moves existing items");
        events.Clear(); RuleListUpdater.Apply(rows, [off]);
        Check(events.SequenceEqual([NotifyCollectionChangedAction.Remove]) && ReferenceEquals(rows[0], selected), "Delete removes affected row only");
        events.Clear(); RuleListUpdater.Apply(rows, [off, second]);
        Check(events.SequenceEqual([NotifyCollectionChangedAction.Add]), "Add inserts new row only");
        Check(!events.Contains(NotifyCollectionChangedAction.Reset), "List refresh has no reset");
        properties.Clear(); events.Clear();
        var changedProtocol = off with { Rule = off.Rule with
            { Desired = off.Rule.Desired with { Protocol = ForwardProtocol.Udp }, Applied = off.Rule.Applied with { Protocol = ForwardProtocol.Udp } } };
        RuleListUpdater.Apply(rows, [changedProtocol, second]);
        Check(ReferenceEquals(rows[0], selected) && selected.IsChecked && events.Count == 0,
            "Protocol change keeps same row, checkbox and selection identity");
        Check(properties.Contains(nameof(RuleRow.ProtocolText)) && selected.ProtocolText == "UDP",
            "Protocol changes notify visible column");
        return count;
    }
}
