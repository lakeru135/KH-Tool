using Autodesk.Revit.DB;

namespace KhBatchRunner.NwdFamily;

/// <summary>
/// Navisworks properties → family TYPE parameters, value set on the current type. A property whose
/// name is already a family parameter of the same kind and not driven by a formula or a dimension
/// (Manufacturer, Model, Description… in most templates) fills that parameter instead of adding
/// a duplicate; any other clash gets " (NW)". Names lose the characters Revit forbids.
/// Runs inside the build transaction.
/// </summary>
internal static class NwdFamilyParameters
{
    private const string ClashSuffix = " (NW)";
    private static readonly char[] Forbidden = "[]{}|;<>?`~:\\".ToCharArray();

    /// <param name="reserved">Names the builder labelled dimensions with (Width, Height…): never written.</param>
    public static void Apply(Document fam, IReadOnlyList<NwdParameter> parameters, ISet<string> reserved, NwdBuildReport report)
    {
        FamilyManager fm = fam.FamilyManager;
        foreach (NwdParameter p in parameters)
        {
            string name = Clean(p.Name);
            if (name.Length == 0)
            {
                report.Note($"Parameter '{p.Name}' skipped: no usable characters in its name.");
                report.ParametersSkipped++;
                continue;
            }

            try
            {
                FamilyParameter? existing = reserved.Contains(name) ? null : fm.get_Parameter(name);
                if (existing != null && Fits(existing, p))
                {
                    Set(fm, existing, p);
                    report.ParametersFilled++;
                    continue;
                }
                if (existing != null || reserved.Contains(name))
                    name = Unique(fm, name + ClashSuffix);

                FamilyParameter created = Add(fm, name, p);
                Set(fm, created, p);
                report.ParametersCreated++;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Names Revit keeps for itself ("Family Name", "Type Name", …) refuse AddParameter.
                try
                {
                    FamilyParameter created = Add(fm, Unique(fm, name + ClashSuffix), p);
                    Set(fm, created, p);
                    report.ParametersCreated++;
                }
                catch (Exception again)
                {
                    report.Note($"Parameter '{p.Name}' skipped: {again.Message}");
                    report.ParametersSkipped++;
                }
            }
        }
    }

    public static string Clean(string name)
    {
        var s = new string(name.Where(c => Array.IndexOf(Forbidden, c) < 0 && !char.IsControl(c)).ToArray());
        return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A name not yet used in the family: "X", then "X 2", "X 3"…</summary>
    public static string Unique(FamilyManager fm, string name)
    {
        string candidate = name;
        for (int n = 2; fm.get_Parameter(candidate) != null; n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    private static FamilyParameter Add(FamilyManager fm, string name, NwdParameter p) =>
        fm.AddParameter(name, GroupTypeId.Data, Spec(p.Type), false);

    private static ForgeTypeId Spec(NwdParameterType type) => type switch
    {
        NwdParameterType.Integer => SpecTypeId.Int.Integer,
        NwdParameterType.Number => SpecTypeId.Number,
        NwdParameterType.Length => SpecTypeId.Length,
        NwdParameterType.YesNo => SpecTypeId.Boolean.YesNo,
        _ => SpecTypeId.String.Text,
    };

    /// <summary>An existing parameter can take the value: a type parameter, same kind, nothing drives it.</summary>
    private static bool Fits(FamilyParameter fp, NwdParameter p) =>
        !fp.IsInstance && !fp.IsReadOnly && !fp.IsDeterminedByFormula && fp.AssociatedParameters.IsEmpty &&
        fp.Definition.GetDataType() == Spec(p.Type);

    private static void Set(FamilyManager fm, FamilyParameter fp, NwdParameter p)
    {
        switch (p.Type)
        {
            case NwdParameterType.Text: fm.Set(fp, p.Text); break;
            case NwdParameterType.Integer:
            case NwdParameterType.YesNo: fm.Set(fp, (int)p.Value); break;
            default: fm.Set(fp, p.Value); break;
        }
    }
}
