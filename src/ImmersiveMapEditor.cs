using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationSimulation;

internal sealed partial class ImmersiveMapCompatibility
{
    // Retain the native marker and save validation, but expose no numeric position fields.
    // New markers keep the selected position; editing never moves existing markers.
    private static bool ComposeMarkerEditor(object __instance)
    {
        var dialog = (GuiDialog)__instance;
        var api = Client(dialog);
        var marker = Field(dialog, "_waypoint")!;
        object Value(string name) => AccessTools.Property(marker.GetType(), name).GetValue(marker)!;
        void Set(string name, object value) => AccessTools.Field(dialog.GetType(), name).SetValue(dialog, value);
        string Text(string key) => Lang.Get("immersivemaps:" + key);

        var renderSystem = api.ModLoader.GetModSystem("ImmersiveMaps.Rendering.MapRenderSystem");
        var icons = AccessTools.Property(renderSystem.GetType(), "Icons").GetValue(renderSystem)!;
        var names = ((IEnumerable<string>)AccessTools.Property(icons.GetType(), "Names").GetValue(icons)!).ToArray();
        var colors = ((IEnumerable<int>)Static("Waypoints.WaypointPalette", "Colors", api.World)!).ToArray();
        int color = (int)Value("Color");
        if (!colors.Contains(color)) colors = [.. colors, color];
        Set("_iconNames", names);
        Set("_colors", colors);

        var background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding)
            .WithSizing(ElementSizing.FitToChildren);
        var label = ElementBounds.Fixed(0, 30, 120, 30);
        var input = ElementBounds.Fixed(130, 30, 200, 30);
        var previous = dialog.SingleComposer;
        var composer = api.Gui.CreateCompo("editwaypointgui", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle))
            .AddShadedDialogBG(background)
            .AddDialogTitleBar(Text((bool)Field(dialog, "_isNew")! ? "waypoint-add-title" : "waypoint-edit-title"), () => dialog.TryClose())
            .BeginChildElements(background)
            .AddStaticText(Text("waypoint-name"), CairoFont.WhiteSmallText(), label)
            .AddTextInput(input, name => Call(dialog, "OnNameChanged", name), CairoFont.TextInput(), "nameInput");
        label = label.BelowCopy(0, 10);
        composer.AddStaticText(Text("waypoint-icon"), CairoFont.WhiteSmallText(), label);
        var iconBounds = label.BelowCopy(0, 5).WithFixedSize(30, 30);
        composer.AddElementListPicker<string>(Type("Gui.GuiElementWaypointIconPicker"), names,
            index => Call(dialog, "OnIconSelected", index), iconBounds, 350, "iconpicker");
        label = ElementBounds.Fixed(0, iconBounds.fixedY + iconBounds.fixedHeight + 10, 120, 30);
        composer.AddStaticText(Text("waypoint-color"), CairoFont.WhiteSmallText(), label);
        var colorBounds = label.BelowCopy(0, 5).WithFixedSize(30, 30);
        composer.AddColorListPicker(colors, index => Call(dialog, "OnColorSelected", index), colorBounds, 350, "colorpicker");
        var cancel = ElementBounds.Fixed(0, colorBounds.fixedY + colorBounds.fixedHeight + 20, 100, 30);
        var save = cancel.FlatCopy().WithAlignment(EnumDialogArea.RightFixed);
        string hint = (string)Call(dialog, "SaveHint")!;
        Set("_saveHint", hint);
        composer.AddSmallButton(Lang.Get("Cancel"), () => (bool)Call(dialog, "OnCancel")!, cancel)
            .AddSmallButton(Lang.Get("Save"), () => (bool)Call(dialog, "OnSave")!, save, key: "saveButton")
            .AddHoverText(hint, CairoFont.WhiteSmallText(), 300, save, key: "saveHint")
            .EndChildElements();
        dialog.SingleComposer = composer;
        composer.Compose();
        composer.GetTextInput("nameInput").SetMaxLength(64);
        composer.GetTextInput("nameInput").SetValue((string)Value("Name"));
        composer.IconListPickerSetValue("iconpicker", Math.Max(0, Array.IndexOf(names, (string)Value("Icon"))));
        composer.ColorListPickerSetValue("colorpicker", Array.IndexOf(colors, color));
        Call(dialog, "UpdateSaveButton");
        previous?.Dispose();
        return false;
    }
}
