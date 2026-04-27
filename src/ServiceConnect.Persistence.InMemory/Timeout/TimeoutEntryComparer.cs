namespace ServiceConnect.Persistence.InMemory;

internal sealed class TimeoutEntryComparer : IComparer<TimeoutEntry>
{
    public static readonly TimeoutEntryComparer Instance = new();

    public int Compare(TimeoutEntry? x, TimeoutEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int byTime = x.Time.CompareTo(y.Time);
        if (byTime != 0)
        {
            return byTime;
        }

        return x.Id.CompareTo(y.Id);
    }
}
