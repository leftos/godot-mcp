namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>A <see cref="SharedProbeSession"/> launched with <c>shutOutRealGamepads</c>, so the machine's real pads stay out.</summary>
public sealed class SharedShutOutProbeSession() : SharedProbeSession(true);
