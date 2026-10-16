using System;
using System.Collections.Generic;
using Avalonia.Collections;

namespace ChartShared.UI.ViewModels
{
    /// <summary>Changes a list's rows to new ones as little as it can, for lists long enough that remaking them all shows.</summary>
    public static class ListRows
    {
        /// <summary>
        /// Makes <paramref name="rows"/> <paramref name="next"/>: the rows the same, by reference, at either end are left
        /// alone, and only the stretch between them is removed and inserted, each in one change. An edit changes one place
        /// in a list, so a list showing the rows only makes those again.
        /// </summary>
        public static void Replace<T>(AvaloniaList<T> rows, IReadOnlyList<T> next)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(next);

            int start = 0;
            while (start < rows.Count && start < next.Count && ReferenceEquals(rows[start], next[start]))
                start++;

            int endOld = rows.Count, endNew = next.Count;
            while (endOld > start && endNew > start && ReferenceEquals(rows[endOld - 1], next[endNew - 1]))
            {
                endOld--;
                endNew--;
            }

            if (endOld > start)
                rows.RemoveRange(start, endOld - start);
            if (endNew > start)
            {
                var inserted = new T[endNew - start];
                for (int i = 0; i < inserted.Length; i++)
                    inserted[i] = next[start + i];
                rows.InsertRange(start, inserted);
            }
        }
    }
}
