namespace Jellyfin.Plugin.RaceToFinish.Data;

/// <summary>
/// Which parts of a movie or episode have been seen, as merged [start, end] ranges in seconds.
/// "Finished" is based on this, so rewinding and rewatching a part never counts twice.
/// </summary>
public static class Coverage
{
    /// <summary>Merges overlapping or touching ranges and drops empty ones.</summary>
    /// <param name="ranges">Ranges as [start, end] in seconds.</param>
    /// <returns>Sorted, non-overlapping ranges.</returns>
    public static List<int[]> Merge(IEnumerable<int[]> ranges)
    {
        var merged = new List<int[]>();
        foreach (int[] range in ranges.Where(r => r.Length == 2 && r[1] > r[0]).OrderBy(r => r[0]))
        {
            if (merged.Count > 0 && range[0] <= merged[^1][1] + 1)
            {
                merged[^1][1] = Math.Max(merged[^1][1], range[1]);
            }
            else
            {
                merged.Add([range[0], range[1]]);
            }
        }

        return merged;
    }

    /// <summary>Total seconds covered by already-merged ranges.</summary>
    /// <param name="merged">Ranges from <see cref="Merge"/>.</param>
    /// <returns>Seconds.</returns>
    public static int Length(IEnumerable<int[]> merged) => merged.Sum(r => r[1] - r[0]);
}
