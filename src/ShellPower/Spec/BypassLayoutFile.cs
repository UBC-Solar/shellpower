using System.Collections.Generic;

public sealed class BypassLayoutFile
{
    public List<BypassStringEntry> Strings { get; set; } = new();
}

public sealed class BypassStringEntry
{
    public int StringIndex { get; set; }                 // 0-based
    public List<int[]> Diodes { get; set; } = new();     // each as [startIx, endIx], inclusive
}