using System.Collections.ObjectModel;
using PortForwarder.Core;

namespace PortForwarder.Services;

internal static class RuleListUpdater
{
    public static void Apply(ObservableCollection<RuleRow> rows, IReadOnlyList<RealmRuntimeState> states)
    {
        var keys = states.Select(r => r.Rule.Id).ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!keys.Contains(rows[index].Rule.Id)) rows.RemoveAt(index);
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var existing = -1;
            for (var candidate = index; candidate < rows.Count; candidate++)
                if (rows[candidate].Rule.Id == state.Rule.Id) { existing = candidate; break; }
            if (existing < 0) rows.Insert(index, new RuleRow(state));
            else { if (existing != index) rows.Move(existing, index); rows[index].Update(state); }
        }
    }
}
