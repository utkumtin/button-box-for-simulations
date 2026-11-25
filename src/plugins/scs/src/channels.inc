// Telemetry channels the plugin registers (decisions §4 "Plugin and catalog", §4.1 "Telemetry values"). They cover every
// stateField of /catalog/ets2.json and every field the widget behaviours read (decisions §5.1-§5.3); Core.Tests
// ScsPluginListsTests checks that. Mapping to the integer sent to the host:
//   bool     -> 0 or 1
//   u32      -> the value (clamped to the int32 range)
//   positive -> 1 while the float is above 0, else 0
// Usage: #define BBS_CHANNEL(name, mapping) ... then #include "channels.inc".
BBS_CHANNEL("truck.electric.enabled", bool)
BBS_CHANNEL("truck.engine.enabled", bool)
BBS_CHANNEL("truck.light.parking", bool)
BBS_CHANNEL("truck.light.beam.low", bool)
BBS_CHANNEL("truck.light.beam.high", bool)
BBS_CHANNEL("truck.light.beacon", bool)
BBS_CHANNEL("truck.hazard.warning", bool)
BBS_CHANNEL("truck.wipers", bool)
BBS_CHANNEL("truck.brake.parking", bool)
BBS_CHANNEL("truck.brake.retarder", u32)
BBS_CHANNEL("truck.cruise_control", positive)
BBS_CHANNEL("truck.differential_lock", bool)
BBS_CHANNEL("truck.lift_axle", bool)
BBS_CHANNEL("truck.trailer.lift_axle", bool)
BBS_CHANNEL("truck.brake.motor", bool)
BBS_CHANNEL("truck.lblinker", bool)
BBS_CHANNEL("truck.rblinker", bool)
