using System.Collections.Generic;
using LethalCompanyInputUtils.Api;
using UnityEngine.InputSystem;

namespace Y4NGZUpgrades.Gui;

public class IngameKeybinds : LcInputActions
{
    [InputAction("<Keyboard>/p", Name="PurchaseMenu")]
    public InputAction PurchaseMenu { get; set; } = null!;

    // F-SHADOW-3: default moved off G, which vanilla binds to the emote wheel.
    [InputAction("<Keyboard>/x", Name="[Y4NGZ] Shadow Step")]
    public InputAction ShadowStep { get; set; } = null!;

    [InputAction("<Keyboard>/q", Name="[Y4NGZ] Foreman Ping")]
    public InputAction ForemanPing { get; set; } = null!;

    // F-FOREMAN-A-6 / WP12 sweep: the "[Y4NGZ] Rally Call" action is gone with RallyCallUpgrade
    // and the Rally half of ForemanSupportPatch. It also freed <Keyboard>/r, which BetterArmory
    // uses for "[Y4NGZ] Reload Weapon" (see the note below).

    [InputAction("<Keyboard>/y", Name="[Y4NGZ] Field Tablet")]
    public InputAction FieldTablet { get; set; } = null!;

    [InputAction("<Keyboard>/j", Name="[Y4NGZ] Command Net Transmit")]
    public InputAction CommandNetTransmit { get; set; } = null!;

    [InputAction("<Keyboard>/v", Name="[Y4NGZ] Worklight Beacon")]
    public InputAction WorklightBeacon { get; set; } = null!;

    [InputAction("<Mouse>/middleButton", Name="[Y4NGZ] Courier Drone Command")]
    public InputAction CourierDroneCommand { get; set; } = null!;

    [InputAction("<Keyboard>/escape", Name="[Y4NGZ] Player Menu Close")]
    public InputAction PlayerMenuClose { get; set; } = null!;

    [InputAction("<Keyboard>/q", Name="[Y4NGZ] Player Menu Previous Section")]
    public InputAction PlayerMenuPreviousSection { get; set; } = null!;

    [InputAction("<Keyboard>/e", Name="[Y4NGZ] Player Menu Next Section")]
    public InputAction PlayerMenuNextSection { get; set; } = null!;

    [InputAction("<Keyboard>/w", Name="[Y4NGZ] Player Menu Up")]
    public InputAction PlayerMenuUp { get; set; } = null!;

    [InputAction("<Keyboard>/s", Name="[Y4NGZ] Player Menu Down")]
    public InputAction PlayerMenuDown { get; set; } = null!;

    [InputAction("<Keyboard>/a", Name="[Y4NGZ] Player Menu Left")]
    public InputAction PlayerMenuLeft { get; set; } = null!;

    [InputAction("<Keyboard>/d", Name="[Y4NGZ] Player Menu Right")]
    public InputAction PlayerMenuRight { get; set; } = null!;

    [InputAction("<Keyboard>/enter", Name="[Y4NGZ] Player Menu Select")]
    public InputAction PlayerMenuSelect { get; set; } = null!;

    [InputAction("<Keyboard>/tab", Name="[Y4NGZ] Player Menu Details / Items")]
    public InputAction PlayerMenuInspector { get; set; } = null!;

    [InputAction("<Keyboard>/leftArrow", Name="[Y4NGZ] Tablet Previous Tab")]
    public InputAction TabletPreviousTab { get; set; } = null!;

    [InputAction("<Keyboard>/rightArrow", Name="[Y4NGZ] Tablet Next Tab")]
    public InputAction TabletNextTab { get; set; } = null!;

    [InputAction("<Keyboard>/upArrow", Name="[Y4NGZ] Tablet Up")]
    public InputAction TabletUp { get; set; } = null!;

    [InputAction("<Keyboard>/downArrow", Name="[Y4NGZ] Tablet Down")]
    public InputAction TabletDown { get; set; } = null!;

    [InputAction("<Keyboard>/enter", Name="[Y4NGZ] Tablet Activate")]
    public InputAction TabletActivate { get; set; } = null!;

    [InputAction("<Mouse>/leftButton", Name="[Y4NGZ] Tablet Primary Action")]
    public InputAction TabletPrimaryAction { get; set; } = null!;

    [InputAction("<Keyboard>/backspace", Name="[Y4NGZ] Tablet Back")]
    public InputAction TabletBack { get; set; } = null!;

    [InputAction("<Keyboard>/equals", Name="[Y4NGZ] Tablet Zoom In")]
    public InputAction TabletZoomIn { get; set; } = null!;

    [InputAction("<Keyboard>/minus", Name="[Y4NGZ] Tablet Zoom Out")]
    public InputAction TabletZoomOut { get; set; } = null!;

    // F-DRONE sweep: Escape is the vanilla pause key, so an exit bound to it opened the quick
    // menu in the same frame it left the drone. Backspace is unbound in vanilla gameplay.
    [InputAction("<Keyboard>/backspace", Name="[Y4NGZ] Drone Pilot Exit")]
    public InputAction DronePilotExit { get; set; } = null!;

    [InputAction("<Keyboard>/space", Name="[Y4NGZ] Drone Pilot Ascend")]
    public InputAction DronePilotAscend { get; set; } = null!;

    [InputAction("<Keyboard>/leftCtrl", Name="[Y4NGZ] Drone Pilot Descend")]
    public InputAction DronePilotDescend { get; set; } = null!;

    [InputAction("<Mouse>/leftButton", Name="[Y4NGZ] Drone Pilot Flame")]
    public InputAction DronePilotFlame { get; set; } = null!;

    [InputAction("<Mouse>/rightButton", Name="[Y4NGZ] Drone Pilot Grenade")]
    public InputAction DronePilotGrenade { get; set; } = null!;

    [InputAction("<Keyboard>/e", Name="[Y4NGZ] Drone Pilot Lift")]
    public InputAction DronePilotLift { get; set; } = null!;

    [InputAction("<Keyboard>/f", Name="[Y4NGZ] Drone Pilot Light")]
    public InputAction DronePilotLight { get; set; } = null!;

    // "[Y4NGZ] Reload Weapon" moved to BetterArmory.ArmoryKeybinds with the weapons (#266). The
    // action name and its <Keyboard>/r default are unchanged there, so an existing rebind carries
    // over; InputUtils persists rebinds per plugin, keyed by action name.

    [InputAction("<Keyboard>/1", Name="[Y4NGZ] Hotbar Slot 1")]
    public InputAction HotbarSlot1 { get; set; } = null!;

    [InputAction("<Keyboard>/2", Name="[Y4NGZ] Hotbar Slot 2")]
    public InputAction HotbarSlot2 { get; set; } = null!;

    [InputAction("<Keyboard>/3", Name="[Y4NGZ] Hotbar Slot 3")]
    public InputAction HotbarSlot3 { get; set; } = null!;

    [InputAction("<Keyboard>/4", Name="[Y4NGZ] Hotbar Slot 4")]
    public InputAction HotbarSlot4 { get; set; } = null!;

    [InputAction("<Keyboard>/5", Name="[Y4NGZ] Hotbar Slot 5")]
    public InputAction HotbarSlot5 { get; set; } = null!;

    [InputAction("<Keyboard>/6", Name="[Y4NGZ] Hotbar Slot 6")]
    public InputAction HotbarSlot6 { get; set; } = null!;

    [InputAction("<Keyboard>/7", Name="[Y4NGZ] Hotbar Slot 7")]
    public InputAction HotbarSlot7 { get; set; } = null!;

    internal IEnumerable<InputAction> AllActions
    {
        get
        {
            yield return PurchaseMenu;
            yield return ShadowStep;
            yield return ForemanPing;
            yield return FieldTablet;
            yield return CommandNetTransmit;
            yield return WorklightBeacon;
            yield return CourierDroneCommand;
            yield return PlayerMenuClose;
            yield return PlayerMenuPreviousSection;
            yield return PlayerMenuNextSection;
            yield return PlayerMenuUp;
            yield return PlayerMenuDown;
            yield return PlayerMenuLeft;
            yield return PlayerMenuRight;
            yield return PlayerMenuSelect;
            yield return PlayerMenuInspector;
            yield return TabletPreviousTab;
            yield return TabletNextTab;
            yield return TabletUp;
            yield return TabletDown;
            yield return TabletActivate;
            yield return TabletPrimaryAction;
            yield return TabletBack;
            yield return TabletZoomIn;
            yield return TabletZoomOut;
            yield return DronePilotExit;
            yield return DronePilotAscend;
            yield return DronePilotDescend;
            yield return DronePilotFlame;
            yield return DronePilotGrenade;
            yield return DronePilotLift;
            yield return DronePilotLight;
            yield return HotbarSlot1;
            yield return HotbarSlot2;
            yield return HotbarSlot3;
            yield return HotbarSlot4;
            yield return HotbarSlot5;
            yield return HotbarSlot6;
            yield return HotbarSlot7;
        }
    }

    /// <summary>
    /// Actions that can fire during ordinary gameplay and therefore need to be published to
    /// BetterArmory's startup conflict resolver. Context-only menu, tablet, and drone-pilot
    /// controls are deliberately excluded: sharing those keys is safe because their input
    /// surfaces are mutually exclusive.
    /// </summary>
    internal IEnumerable<InputAction> GameplayReservationActions
    {
        get
        {
            yield return PurchaseMenu;
            yield return ShadowStep;
            yield return ForemanPing;
            yield return FieldTablet;
            yield return CommandNetTransmit;
            yield return WorklightBeacon;
            yield return CourierDroneCommand;
            yield return HotbarSlot1;
            yield return HotbarSlot2;
            yield return HotbarSlot3;
            yield return HotbarSlot4;
            yield return HotbarSlot5;
            yield return HotbarSlot6;
            yield return HotbarSlot7;
        }
    }
}
