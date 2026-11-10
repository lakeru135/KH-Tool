// NOT `using Autodesk.Revit.DB` — its Form type collides with System.Windows.Forms.Form.
using BuiltInCategory = Autodesk.Revit.DB.BuiltInCategory;
using LabelUtils = Autodesk.Revit.DB.LabelUtils;

namespace KhBatchRunner.UI;

/// <summary>
/// Searchable checklist of Revit categories for a Correct Workset rule. Names come from
/// <see cref="LabelUtils.GetLabelFor(BuiltInCategory)"/> — a static lookup that works with NO
/// document open (the window opens from the ribbon, possibly at the Home screen). Values that
/// have no label (internal/subcategory enum entries) are skipped. The filter box narrows the
/// list; checked state survives filtering. Selected categories are returned as enum NAMES
/// ("OST_Walls") — the JSON-stable form <see cref="Batch.CorrectWorksetRule.Categories"/> stores.
/// </summary>
public sealed class CategoryPickerDialog : Form
{
    private readonly TextBox _search;
    private readonly CheckedListBox _list;
    // Checked state independent of the current filter (enum name → checked).
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CategoryItem> _all = new();

    /// <summary>Enum names of the checked categories, in label order.</summary>
    public List<string> SelectedCategoryNames { get; private set; } = new();

    public CategoryPickerDialog(IEnumerable<string> preChecked)
    {
        foreach (string name in preChecked) _checked.Add(name);

        Text = "Select categories";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(420, 520);
        MinimumSize = new System.Drawing.Size(360, 400);

        _search = new TextBox();
        _search.TextChanged += (_, _) => RefreshList();

        _list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
        CheckListBehavior.EnableRangeCheck(_list); // Shift+click checks a whole range
        _list.ItemCheck += (_, e) =>
        {
            // Track by enum name so the state survives re-filtering.
            if (_list.Items[e.Index] is CategoryItem item)
            {
                if (e.NewValue == CheckState.Checked) _checked.Add(item.EnumName);
                else _checked.Remove(item.EnumName);
            }
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };
        ok.Click += (_, _) => SelectedCategoryNames = OrderedSelection();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new System.Drawing.Size(80, 0) };

        var root = FormLayout.SingleColumn();
        FormLayout.AddAutoRow(root, FormLayout.LabeledRow("Search:", _search));
        FormLayout.AddFillRow(root, _list, 100);
        var buttons = FormLayout.FlowRow(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        FormLayout.AddAutoRow(root, buttons);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.Apply(this);

        BuildCatalog();
        RefreshList();
    }

    // The full BuiltInCategory enum has ~1000+ entries, most of them internal/subcategory noise.
    // Correct Workset only ever moves MODEL elements onto worksets, plus the few DATUM annotation
    // categories that actually live on user worksets. So the picker is curated to those. Version-
    // safe: any name missing in the running Revit (Enum.TryParse fails) or without a display label
    // is dropped silently, so one list serves 2024/2025/2026.
    private static readonly string[] CuratedCategories =
    {
        // --- Architecture ---
        "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Columns", "OST_Doors",
        "OST_Windows", "OST_Furniture", "OST_FurnitureSystems", "OST_Casework", "OST_Stairs",
        "OST_StairsRailing", "OST_Ramps", "OST_CurtainWallPanels", "OST_CurtainWallMullions",
        "OST_GenericModel", "OST_Mass", "OST_Parking", "OST_Roads", "OST_Planting",
        "OST_Entourage", "OST_SpecialtyEquipment", "OST_Site", "OST_Topography", "OST_Toposolid",
        // --- Structure ---
        "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation",
        "OST_StructuralTruss", "OST_StructuralStiffener", "OST_Rebar", "OST_StructConnections",
        "OST_StructuralFramingSystem",
        // --- MEP: mechanical ---
        "OST_MechanicalEquipment", "OST_DuctCurves", "OST_FlexDuctCurves", "OST_DuctFitting",
        "OST_DuctAccessory", "OST_DuctTerminal", "OST_DuctInsulations", "OST_DuctLinings",
        // --- MEP: piping / plumbing ---
        "OST_PipeCurves", "OST_FlexPipeCurves", "OST_PipeFitting", "OST_PipeAccessory",
        "OST_PlumbingFixtures", "OST_Sprinklers", "OST_PipeInsulations",
        // --- MEP: electrical ---
        "OST_ElectricalEquipment", "OST_ElectricalFixtures", "OST_LightingFixtures",
        "OST_LightingDevices", "OST_Wire", "OST_CableTray", "OST_CableTrayFitting",
        "OST_Conduit", "OST_ConduitFitting", "OST_CommunicationDevices", "OST_DataDevices",
        "OST_FireAlarmDevices", "OST_NurseCallDevices", "OST_SecurityDevices",
        "OST_TelephoneDevices", "OST_FireProtection",
        // --- Datum annotation that lives on USER worksets ---
        "OST_Levels", "OST_Grids", "OST_CLines", "OST_VolumeOfInterest", "OST_Matchline",
    };

    private void BuildCatalog()
    {
        foreach (string name in CuratedCategories)
        {
            if (!Enum.TryParse(name, out BuiltInCategory bic)) continue; // not in this Revit version
            string label;
            try { label = LabelUtils.GetLabelFor(bic); }
            catch { continue; } // no label = not user-facing here
            if (string.IsNullOrWhiteSpace(label)) continue;
            _all.Add(new CategoryItem(name, label));
        }
        _all.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshList()
    {
        string filter = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (CategoryItem item in _all)
        {
            if (filter.Length > 0 && item.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _list.Items.Add(item, _checked.Contains(item.EnumName));
        }
        _list.EndUpdate();
    }

    private List<string> OrderedSelection() =>
        _all.Where(i => _checked.Contains(i.EnumName)).Select(i => i.EnumName).ToList();

    private sealed class CategoryItem
    {
        public string EnumName { get; }
        public string Label { get; }
        public CategoryItem(string enumName, string label) { EnumName = enumName; Label = label; }
        public override string ToString() => Label;
    }
}
