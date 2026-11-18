using ButtonBox.Core;

namespace ButtonBox.Simulator;

/// <summary>
/// ACC stand-in. vJoy button numbers map to ACC action codes through the catalog, so the mapping is an input.
/// Rules come from the ACC spike. The starter is a held input: the engine runs only after about 0.47 s of holding.
/// </summary>
public sealed class AccSimulator : SimulatorBase
{
    private const string IgnitionOn = "ignitionOn";
    private const string StarterOn = "starterEngineOn";
    private const string EngineRunning = "isEngineRunning";
    private const string PitLimiter = "pitLimiterOn";
    private const string LightsStage = "lightsStage";
    private const string RainLights = "rainLights";
    private const string FlashingLights = "flashingLights";
    private const string Wiper = "wiperLV";
    private const string DirLeft = "directionLightsLeft";
    private const string DirRight = "directionLightsRight";
    private const string Start = "start";
    private const string Flash = "flash";

    private readonly IReadOnlyDictionary<int, string> _actions;

    /// <param name="buttonActions">vJoy button number to ACC action code (e.g. 5 to <c>Starter</c>).</param>
    /// <param name="timeProvider">Clock for the starter and flash timers. Defaults to the system clock.</param>
    public AccSimulator(IReadOnlyDictionary<int, string> buttonActions, TimeProvider? timeProvider = null)
        : base(
            timeProvider,
            [IgnitionOn, StarterOn, EngineRunning, PitLimiter, LightsStage, RainLights, FlashingLights, Wiper, DirLeft, DirRight],
            TimeSpan.FromSeconds(0.47))
    {
        _actions = buttonActions;
    }

    /// <summary>How long the game keeps the flashing lights on after <c>EnableFlashingLights</c>.</summary>
    public TimeSpan FlashDuration { get; set; } = TimeSpan.FromSeconds(2.6);

    /// <summary>Buttons without a mapping and actions the model does not know are ignored.</summary>
    protected override bool Handle(OutputKind kind, GameOutput output)
    {
        if (output is not VJoyButton button || !_actions.TryGetValue(button.Number, out var action))
        {
            return false;
        }

        if (action == "Starter")
        {
            // Confirmed by the user: the starter is ignored while the ignition is off.
            return kind switch
            {
                OutputKind.Hold => HoldStarter(),
                OutputKind.Release => ReleaseStarter(),
                _ => false, // A momentary press is far shorter than the hold the game needs.
            };
        }

        if (kind != OutputKind.Press)
        {
            return false;
        }

        switch (action)
        {
            case "IgnitionSequenceOn":
                // Confirmed by the user: switching the ignition off also stops the engine.
                if (Get(IgnitionOn) == 1)
                {
                    Cancel(Start);
                    return Set(IgnitionOn, 0) | Set(EngineRunning, 0) | Set(StarterOn, 0);
                }

                return Set(IgnitionOn, 1);
            case "PitLimiter":
                return Toggle(PitLimiter);
            case "CycleCarLightStages":
                return Set(LightsStage, (Get(LightsStage) + 1) % 3);
            case "EnableRainLights":
                return Toggle(RainLights);
            case "EnableFlashingLights":
                var flashOn = Set(FlashingLights, 1);
                return Schedule(Flash, FlashDuration, () => Set(FlashingLights, 0)) | flashOn;
            case "CycleWiper":
                return Set(Wiper, (Get(Wiper) + 1) % 4);
            case "LeftDirectionalLight":
                return Toggle(DirLeft);
            case "RightDirectionalLight":
                return Toggle(DirRight);
            default:
                return false;
        }
    }

    private bool Toggle(string field) => Set(field, 1 - Get(field));

    private bool HoldStarter()
    {
        if (Get(IgnitionOn) == 0)
        {
            return false;
        }

        var changed = Set(StarterOn, 1);
        if (!EngineNeverStarts && Get(EngineRunning) == 0)
        {
            changed |= Schedule(Start, StarterDelay, () => Set(EngineRunning, 1));
        }

        return changed;
    }

    private bool ReleaseStarter()
    {
        Cancel(Start);
        return Set(StarterOn, 0);
    }
}
