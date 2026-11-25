namespace ButtonBox.Core.Engine;

/// <summary>SCS telemetry field names the widget behaviours read (decisions §5.1 to §5.3).</summary>
internal static class ScsFields
{
    public const string Electric = "truck.electric.enabled";
    public const string EngineRunning = "truck.engine.enabled";
    public const string Parking = "truck.light.parking";
    public const string LowBeam = "truck.light.beam.low";
    public const string HighBeam = "truck.light.beam.high";
    public const string Wipers = "truck.wipers";

    /// <summary>A field's value, or 0 without telemetry or when the snapshot has no such field.</summary>
    public static int ValueOf(TelemetrySnapshot snapshot, string field) =>
        snapshot.Available && snapshot.Fields.TryGetValue(field, out var value) ? value : 0;
}

/// <summary>The catalog action ids the widget behaviours rely on (decisions §5 "Widget behaviours"). The shipped ETS2 and ATS catalogs have all of them.</summary>
internal static class ScsActions
{
    public const string IgnitionOn = "ignition-on";
    public const string IgnitionOff = "ignition-off";
    public const string Starter = "starter";
    public const string HighBeam = "high-beam";
    public const string LightOff = "light-off";
    public const string LightPark = "light-park";
    public const string LightOn = "light-on";
    public const string WipersOff = "wipers-off";
    public const string WipersAuto = "wipers-auto";
    public const string WipersLow = "wipers-low";
    public const string WipersFast = "wipers-fast";
}
