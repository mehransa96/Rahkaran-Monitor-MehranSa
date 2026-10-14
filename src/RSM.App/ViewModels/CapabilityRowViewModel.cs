using CommunityToolkit.Mvvm.ComponentModel;
using RSM.App.Localization;
using RSM.Core.Capabilities;

namespace RSM.App.ViewModels;

/// <summary>One row of the Capability Matrix, localised for the current UI language.</summary>
public sealed class CapabilityRowViewModel(CapabilityDefinition definition, CapabilityResult result) : ObservableObject
{
    public string Id => definition.Id;

    public string Layer => definition.Layer.ToString();

    public CapabilityState StateKind => result.State;

    public string Title => definition.Title.Get(Loc.Instance.Language);

    public string State => CapabilityCatalog.DescribeState(result.State).Get(Loc.Instance.Language);

    public string Detail => result.Detail ?? string.Empty;

    public string Enables => definition.Enables.Get(Loc.Instance.Language);

    /// <summary>Shown only when the capability is not available.</summary>
    public string Remediation => result.State is CapabilityState.Available or CapabilityState.NotApplicable or CapabilityState.NotImplemented
        ? string.Empty
        : definition.Remediation.Get(Loc.Instance.Language);

    public void Refresh() => OnPropertyChanged(string.Empty);
}
