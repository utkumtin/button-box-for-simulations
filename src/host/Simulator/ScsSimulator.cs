using ButtonBox.Core;

namespace ButtonBox.Simulator;

/// <summary>
/// ETS2/ATS stand-in. Rules come from the SCS spike: light switch positions, toggles, wiper stages and an
/// engine start that takes about 1.28 s after the starter input.
/// </summary>
public sealed class ScsSimulator : SimulatorBase
{
    private const string Electric = "truck.electric.enabled";
    private const string Engine = "truck.engine.enabled";
    private const string Parking = "truck.light.parking";
    private const string LowBeam = "truck.light.beam.low";
    private const string HighBeam = "truck.light.beam.high";
    private const string Beacon = "truck.light.beacon";
    private const string Hazard = "truck.hazard.warning";
    private const string Wipers = "truck.wipers";
    private const string ParkingBrake = "truck.brake.parking";
    private const string Start = "start";

    public ScsSimulator(TimeProvider? timeProvider = null)
        : base(
            timeProvider,
            [Electric, Engine, Parking, LowBeam, HighBeam, Beacon, Hazard, Wipers, ParkingBrake],
            TimeSpan.FromSeconds(1.28))
    {
    }

    /// <summary>
    /// Only presses act. SCS inputs are momentary; holds and releases are recorded but have no effect.
    /// Unknown input names are ignored.
    /// </summary>
    protected override bool Handle(OutputKind kind, GameOutput output)
    {
        if (kind != OutputKind.Press || output is not ScsInput input)
        {
            return false;
        }

        switch (input.Name)
        {
            case "lightoff":
                return SetLights(parking: 0, low: 0);
            case "lightpark":
                return SetLights(parking: 1, low: 0);
            case "lighton":
                return SetLights(parking: 1, low: 1);
            case "hblight":
                return Toggle(HighBeam);
            case "wipers0":
                return Set(Wipers, 0);
            case "wipers1":
            case "wipers2":
            case "wipers3":
                return Set(Wipers, 1);
            case "beacon":
                return Toggle(Beacon);
            case "parkingbrake":
                return Toggle(ParkingBrake);
            case "flasher4way":
                return Toggle(Hazard);
            case "ignitionon":
                return Set(Electric, 1);
            case "ignitionoff":
                Cancel(Start);
                return Set(Electric, 0) | Set(Engine, 0);
            case "ignitionstrt":
                return BeginStart();
            default:
                return false;
        }
    }

    private bool SetLights(int parking, int low) => Set(Parking, parking) | Set(LowBeam, low);

    private bool Toggle(string field) => Set(field, 1 - Get(field));

    /// <summary>
    /// Confirmed by the user: the starter is ignored while electric is off. The host sends
    /// <c>ignitionon</c> first. A start already in progress or a running engine also ignores it.
    /// </summary>
    private bool BeginStart()
    {
        if (Get(Electric) == 0 || Get(Engine) == 1 || IsScheduled(Start))
        {
            return false;
        }

        return !EngineNeverStarts && Schedule(Start, StarterDelay, () => Set(Engine, 1));
    }
}
