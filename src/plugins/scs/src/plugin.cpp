// Phase 1: inert placeholder. The real plugin (Phase 5) will export scs_telemetry_init and scs_input_init.
// Deliberately exports nothing the game would call, so the DLL is not loaded as a plugin yet.

#include <scssdk_input.h>
#include <scssdk_telemetry.h>

static_assert(sizeof(scs_u32_t) == 4, "SCS SDK type sizes must match the game ABI");
static_assert(SCS_RESULT_ok == 0, "SCS_RESULT_ok must be zero");
