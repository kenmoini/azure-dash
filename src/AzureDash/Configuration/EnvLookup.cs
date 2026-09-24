namespace AzureDash.Configuration;

/// <summary>Reads an environment variable; injectable so tests can supply a fake environment.</summary>
public delegate string? EnvLookup(string name);
