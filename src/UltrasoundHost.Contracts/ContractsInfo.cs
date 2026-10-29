namespace UltrasoundHost.Contracts;

/// <summary>
/// Semantic version of the contract assembly. Within one major version the contracts only grow
/// (new optional members); incompatible changes get new types. A plugin declares the minimum
/// version it needs in <see cref="PluginManifest.MinContractsVersion"/>.
/// </summary>
public static class ContractsInfo
{
    /// <summary>
    /// 0.2.0 added the view helpers (<see cref="ViewKinds"/>, <see cref="ViewDescriptor.Matches"/>);
    /// 0.3.0 adds the contribution interfaces (06 §C2) and, in WP3, the ADC/frame/display facts.
    /// </summary>
    public static readonly Version Current = new(0, 3, 0);
}
