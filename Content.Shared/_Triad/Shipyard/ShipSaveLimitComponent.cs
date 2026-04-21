using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Shipyard;

/// <summary>
/// Entities with this component and the same ID as other entities will not be saved if it reaches the limit defined in the ship limit prototype.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ShipSaveLimitComponent : Component
{
    [DataField, AutoNetworkedField]
    public string LimitId = "ShipSaveDefault";
}
