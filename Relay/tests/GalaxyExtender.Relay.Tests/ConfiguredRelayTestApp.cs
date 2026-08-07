namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// <see cref="RelayTestApp"/> with per-test configuration on top of its defaults.
///
/// A separate type rather than a constructor parameter on <see cref="RelayTestApp"/> itself: that
/// host is taken as an xUnit CLASS FIXTURE by several test classes, and xUnit refuses to construct a
/// fixture type that declares more than one public constructor.
/// </summary>
public sealed class ConfiguredRelayTestApp(Dictionary<string, string?> overrides) : RelayTestApp
{
    protected override Dictionary<string, string?>? ExtraConfiguration => overrides;
}
