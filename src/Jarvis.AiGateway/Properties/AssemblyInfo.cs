using System.Runtime.CompilerServices;

// Tests reach internal helpers (e.g. PolicyEngine.IsUserInAllowedGroup) to verify
// behavioural invariants directly rather than coupling to the public API surface.
[assembly: InternalsVisibleTo("Jarvis.AiGateway.Tests")]
