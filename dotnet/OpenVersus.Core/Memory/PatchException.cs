namespace OpenVersus.Memory;

/// <summary>A patch or redirect that could not be made: the bytes were not what was expected, the
/// page could not be written, or no trampoline page was reachable. The message says which.</summary>
public sealed class PatchException(string message) : Exception(message);
