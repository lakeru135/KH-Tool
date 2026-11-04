using Autodesk.Revit.DB;

namespace KhBatchRunner.NwdFamily;

/// <summary>One family category the KH-NWD Tool export dialog offers, with its Revit template.</summary>
internal sealed record NwdCategory(string Key, string DisplayName, string Template, BuiltInCategory Category);

/// <summary>
/// Freestanding (non-hosted) family categories. <see cref="NwdCategory.Key"/> is what the
/// Navisworks side writes into the exchange file, so the keys must match the KH-NWD-Tool repo's
/// src/Shared/RevitCategories.cs; an unknown key falls back to Generic Models.
/// </summary>
internal static class NwdCategories
{
    public const string Default = "GenericModels";

    public static readonly IReadOnlyList<NwdCategory> All = new[]
    {
        new NwdCategory("GenericModels", "Generic Models", "Metric Generic Model.rft", BuiltInCategory.OST_GenericModel),
        new NwdCategory("MechanicalEquipment", "Mechanical Equipment", "Metric Mechanical Equipment.rft", BuiltInCategory.OST_MechanicalEquipment),
        new NwdCategory("ElectricalEquipment", "Electrical Equipment", "Metric Electrical Equipment.rft", BuiltInCategory.OST_ElectricalEquipment),
        new NwdCategory("PlumbingFixtures", "Plumbing Fixtures", "Metric Plumbing Fixture.rft", BuiltInCategory.OST_PlumbingFixtures),
        new NwdCategory("SpecialtyEquipment", "Specialty Equipment", "Metric Specialty Equipment.rft", BuiltInCategory.OST_SpecialityEquipment),
        new NwdCategory("Furniture", "Furniture", "Metric Furniture.rft", BuiltInCategory.OST_Furniture),
        new NwdCategory("Casework", "Casework", "Metric Casework.rft", BuiltInCategory.OST_Casework),
        new NwdCategory("LightingFixtures", "Lighting Fixtures", "Metric Lighting Fixture.rft", BuiltInCategory.OST_LightingFixtures),
        new NwdCategory("StructuralColumns", "Structural Columns", "Metric Structural Column.rft", BuiltInCategory.OST_StructuralColumns),
        new NwdCategory("Site", "Site", "Metric Site.rft", BuiltInCategory.OST_Site),
        new NwdCategory("Planting", "Planting", "Metric Planting.rft", BuiltInCategory.OST_Planting),
        new NwdCategory("Entourage", "Entourage", "Metric Entourage.rft", BuiltInCategory.OST_Entourage),
    };

    public static NwdCategory Find(string key) =>
        All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? All.First(c => c.Key == Default);
}
