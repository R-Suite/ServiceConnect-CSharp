namespace ServiceConnect.Examples.StressHarness.Patterns;

public static class StressHeaders
{
    public const string FlowId = "X-Stress-FlowId";
    public const string OriginBus = "X-Stress-Origin-Bus";
    public const string Pattern = "X-Stress-Pattern";
}

public enum BusIdentity { Alpha, Beta }

public static class BusIdentityExtensions
{
    public static string ToHeaderValue(this BusIdentity bus) => bus switch
    {
        BusIdentity.Alpha => "alpha",
        BusIdentity.Beta => "beta",
        _ => throw new ArgumentOutOfRangeException(nameof(bus), bus, null),
    };

    public static BusIdentity Other(this BusIdentity bus) =>
        bus == BusIdentity.Alpha ? BusIdentity.Beta : BusIdentity.Alpha;
}
