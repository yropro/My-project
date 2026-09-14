using System;

/// <summary>Deterministic packet selection. Group zero is essential state;
/// other groups are optional, indivisible, and considered in publication order.
/// The caller reuses the selection buffer. No wire-format changes or allocations.</summary>
public static class CoreNetworkBudget
{
    public static int Select(int count, int[] lengths, byte[] groups,
        int budget, bool[] selected)
    {
        Array.Clear(selected, 0, selected.Length);
        int used = 2; // class and slot count
        for (int i = 0; i < count; i++)
            if (groups[i] == 0) used += 2 + lengths[i];
        if (used > budget) return -1;
        for (int i = 0; i < count; i++)
        {
            if (groups[i] == 0) { selected[i] = true; continue; }
            bool seen = false;
            for (int j = 0; j < i; j++)
                if (groups[j] == groups[i]) { seen = true; break; }
            if (seen) continue;
            int cost = 0;
            for (int j = i; j < count; j++)
                if (groups[j] == groups[i]) cost += 2 + lengths[j];
            if (cost > budget - used) continue;
            used += cost;
            for (int j = i; j < count; j++)
                if (groups[j] == groups[i]) selected[j] = true;
        }
        return used;
    }
}
