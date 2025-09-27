using Content.Client._NF.Shipyard.UI;
using Content.Shared.Containers.ItemSlots;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Events;
using static Robust.Client.UserInterface.Controls.BaseButton;
using Robust.Client.UserInterface;
using Content.Client.Shuttles.Save;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using System.Linq;

namespace Content.Client._NF.Shipyard.BUI;

public sealed class ShipyardConsoleBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly ShipFileManagementSystem _shipFileManagementSystem = default!;

    private ShipyardConsoleMenu? _menu;
    private ShipyardRulesPopup? _rulesWindow;
    public int Balance { get; private set; }

    public int? ShipSellValue { get; private set; }

    private Button? _loadShipButton;
    private Button? _saveShipButton;
    private ItemList? _savedShipsList;
    private int _selectedShipIndex = -1;



    public ShipyardConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        
        _menu = new ShipyardConsoleMenu(this);
        // Disable the NFSD popup for now.
        // var rules = new FormattedMessage();
        // _rulesWindow = new ShipyardRulesPopup(this);
        _menu.OpenCentered();
        // if (ShipyardConsoleUiKey.Security == (ShipyardConsoleUiKey) UiKey)
        // {
        //     rules.AddText(Loc.GetString($"shipyard-rules-default1"));
        //     rules.PushNewline();
        //     rules.AddText(Loc.GetString($"shipyard-rules-default2"));
        //     _rulesWindow.ShipRules.SetMessage(rules);
        //     _rulesWindow.OpenCentered();
        // }
        _menu.OnClose += Close;
        _menu.OnOrderApproved += ApproveOrder;
        _menu.OnSellShip += SellShip;
        _menu.OnUnassignDeed += UnassignDeed;
        _menu.OnRenameShip += RenameShip;
        _menu.OnSaveShip += SaveShip;
        _menu.TargetIdButton.OnPressed += _ => SendMessage(new ItemSlotButtonPressedEvent("ShipyardConsole-targetId"));

        InitializeSaveLoadControls();
    }

    private void InitializeSaveLoadControls()
    {
        if (_menu == null)
            return;

        var shipCount = _shipFileManagementSystem.GetSavedShipFiles().Count;
        // Only log if there are ships to avoid spam when no ships are saved
        if (shipCount > 0)
        {
            Logger.Debug($"InitializeSaveLoadControls: ShipFileManagementSystem has {shipCount} ships");
        }

        _loadShipButton = _menu.FindControl<Button>("LoadShipButton");
        _saveShipButton = _menu.FindControl<Button>("SaveShipButton");
        _savedShipsList = _menu.FindControl<ItemList>("SavedShipsList");

        if (_loadShipButton != null)
            _loadShipButton.OnPressed += OnLoadShipButtonPressed;
        if (_saveShipButton != null)
            _saveShipButton.OnPressed += OnSaveShipButtonPressed;
        if (_savedShipsList != null)
            _savedShipsList.OnItemSelected += OnSavedShipSelected;

        // Subscribe to ship updates
        _shipFileManagementSystem.OnShipsUpdated += RefreshSavedShipList;
        
        RefreshSavedShipList();
    }

    private void OnSaveShipButtonPressed(BaseButton.ButtonEventArgs args)
    {
        // Only allow saving if the player has a valid ship deed
        if (Owner.Valid && ShipSellValue.HasValue && ShipSellValue.Value > 0)
        {
            _shipFileManagementSystem.RequestSaveShip(Owner);
            Logger.Info($"Requested to save ship for entity {Owner}");
        }
        else
        {
            Logger.Warning("Cannot save ship - no valid ship deed found");
        }
    }

    private async void OnLoadShipButtonPressed(BaseButton.ButtonEventArgs args)
    {
        // Load the currently selected ship from the saved ships list
        if (_savedShipsList == null || _selectedShipIndex < 0 || _selectedShipIndex >= _savedShipsList.Count)
        {
            Logger.Warning("No ship selected for loading");
            return;
        }
        
        var selectedItem = _savedShipsList[_selectedShipIndex];
        var filePath = (string)selectedItem.Metadata!;

        // Load ship YAML data and send via console-specific message
        try
        {
            var yamlData = await _shipFileManagementSystem.GetShipYamlData(filePath);
            if (yamlData != null)
            {
                // Send the load message through the console's BoundUserInterface system
                SendMessage(new ShipyardConsoleLoadMessage(yamlData, filePath));
                Logger.Info($"Sent ship load request for '{selectedItem.Text}' via console");
            }
            else
            {
                Logger.Error($"Failed to load YAML data for ship '{selectedItem.Text}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error loading ship '{selectedItem.Text}': {ex.Message}");
        }
    }

    private void OnSavedShipSelected(ItemList.ItemListSelectedEventArgs args)
    {
        // Store selected index and update Load Ship button state
        _selectedShipIndex = args.ItemIndex;
        if (_loadShipButton != null)
            _loadShipButton.Disabled = false;
    }

    private void RefreshSavedShipList()
    {
        if (_savedShipsList == null)
            return;
        _savedShipsList.Clear();
        
        var savedShipFiles = _shipFileManagementSystem.GetSavedShipFiles();
        //Logger.Info($"RefreshSavedShipList: Found {savedShipFiles.Count} ships to display");
        
        foreach (var filePath in savedShipFiles)
        {
            // Extract filename without extension in a sandbox-safe way
            var fileName = ExtractFileNameWithoutExtension(filePath);
            var item = _savedShipsList.AddItem(fileName);
            item.Metadata = filePath;
            //Logger.Info($"Added ship to UI list: {fileName} (path: {filePath})");
        }
        
        // Enable/disable load button based on available ships
        if (_loadShipButton != null)
        {
            _loadShipButton.Disabled = savedShipFiles.Count == 0;
            Logger.Info($"Load button disabled: {_loadShipButton.Disabled}");
        }
    }

    private static string ExtractFileNameWithoutExtension(string filePath)
    {
        var fileName = filePath;
        var lastSlash = filePath.LastIndexOf('/');
        if (lastSlash >= 0)
            fileName = filePath.Substring(lastSlash + 1);
        var lastBackslash = fileName.LastIndexOf('\\');
        if (lastBackslash >= 0)
            fileName = fileName.Substring(lastBackslash + 1);
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot >= 0)
            fileName = fileName.Substring(0, lastDot);
        return fileName;
    }


    private void Populate(List<string> availablePrototypes, List<string> unavailablePrototypes, bool freeListings, bool validId)
    {
        if (_menu == null)
            return;

        _menu.PopulateProducts(availablePrototypes, unavailablePrototypes, freeListings, validId);
        _menu.PopulateCategories(availablePrototypes, unavailablePrototypes);
        _menu.PopulateClasses(availablePrototypes, unavailablePrototypes);
        _menu.PopulateEngines(availablePrototypes, unavailablePrototypes);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not ShipyardConsoleInterfaceState cState)
            return;

        Balance = cState.Balance;
        ShipSellValue = cState.ShipSellValue;
        var castState = (ShipyardConsoleInterfaceState) state;
        Populate(castState.ShipyardPrototypes.available, castState.ShipyardPrototypes.unavailable, castState.FreeListings, castState.IsTargetIdPresent);
        _menu?.UpdateState(castState);
        
        // Only refresh saved ships list if the UI is actually open
        if (IsOpened)
        {
            RefreshSavedShipList();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _menu?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _menu?.Dispose();
    }

    private void ApproveOrder(ButtonEventArgs args)
    {
        if (args.Button.Parent?.Parent is not VesselRow row || row.Vessel == null)
        {
            return;
        }

        var vesselId = row.Vessel.ID;
        SendMessage(new ShipyardConsolePurchaseMessage(vesselId));
    }
    
    private void SellShip(ButtonEventArgs args)
    {
        //reserved for a sanity check, but im not sure what since we check all the important stuffs on server already
        SendMessage(new ShipyardConsoleSellMessage());
    }
    
    private void UnassignDeed(ButtonEventArgs args)
    {
        SendMessage(new ShipyardConsoleUnassignDeedMessage());
    }

    private void RenameShip(string newName)
    {
        SendMessage(new ShipyardConsoleRenameMessage(newName));
    }

    private void SaveShip(ButtonEventArgs args)
    {
        // Send message to server to save the ship associated with the current deed
        SendMessage(new ShipyardConsoleSaveMessage());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        
        if (disposing)
        {
            // Unsubscribe from events to prevent memory leaks
            _shipFileManagementSystem.OnShipsUpdated -= RefreshSavedShipList;
            _shipFileManagementSystem.OnShipLoaded -= OnShipLoaded;
        }
    }
}
