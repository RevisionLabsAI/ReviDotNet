// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
// ===================================================================

using Revi;

namespace ReviDotNet.Forge.Components.Observer;

public enum RowKind { DateHeader, Event }

public sealed record LogRow(
    RowKind Kind,
    string Key,
    int Depth,
    DateTime Date,
    RlogEvent? Event,
    bool IsContext = false
);

public static class LogFeedFlattener
{
    public static IReadOnlyList<LogRow> Flatten(IEnumerable<RlogEvent> page, IReadOnlyDictionary<string, RlogEvent>? parents)
    {
        var events = page.OrderByDescending(e => e.Timestamp).ToList();
        var byId = events.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToDictionary(e => e.Id!);
        if (parents is not null)
        {
            foreach (var kv in parents)
                byId[kv.Key] = kv.Value;
        }

        var children = new Dictionary<string, List<RlogEvent>>();
        foreach (var e in byId.Values)
        {
            if (!string.IsNullOrWhiteSpace(e.ParentId))
            {
                if (!children.TryGetValue(e.ParentId!, out var list))
                    children[e.ParentId!] = list = new();
                list.Add(e);
            }
        }

        var roots = events
            .Where(e => string.IsNullOrWhiteSpace(e.ParentId) || !byId.ContainsKey(e.ParentId!))
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        var rows = new List<LogRow>(events.Count * 2);
        DateTime? curLocalDate = null;

        foreach (var root in roots)
            EmitWithChildren(root, 0);

        return rows;

        void EmitWithChildren(RlogEvent e, int depth)
        {
            var eLocalDate = e.Timestamp.ToLocalTime().Date;
            EnsureDateHeader(eLocalDate);
            rows.Add(new LogRow(RowKind.Event, e.Id ?? Guid.NewGuid().ToString(), depth, SpecifyLocal(eLocalDate), e));

            if (children.TryGetValue(e.Id ?? string.Empty, out var kids))
            {
                foreach (var c in kids.OrderBy(x => x.Timestamp))
                {
                    var cLocalDate = c.Timestamp.ToLocalTime().Date;
                    EnsureDateHeader(cLocalDate);
                    rows.Add(new LogRow(RowKind.Event, c.Id ?? Guid.NewGuid().ToString(), depth + 1, SpecifyLocal(cLocalDate), c));
                }
            }
        }

        void EnsureDateHeader(DateTime localDate)
        {
            if (curLocalDate != localDate)
            {
                curLocalDate = localDate;
                rows.Add(new LogRow(RowKind.DateHeader, $"hdr-{localDate:yyyy-MM-dd}", 0, SpecifyLocal(localDate), null));
            }
        }

        static DateTime SpecifyLocal(DateTime d) => new(d.Ticks, DateTimeKind.Local);
    }
}
