using Autodesk.Revit.DB;

namespace KhBatchRunner.Actions;

/// <summary>
/// Reads the cloud model GUID a <see cref="RevitLinkType"/> CURRENTLY references, so relink can
/// tell "this link already points at that model" apart from "re-point it there". Two storage
/// shapes exist (both live-verified in the relink spike): a cloud external FILE reference (the
/// ModelPath carries the GUID) and an external RESOURCE reference (BIM 360/Forma — the GUID sits in
/// the reference-information dictionary as <c>LinkedModelModelId</c>; older/other providers may
/// use <c>modelGuid</c>). Reading either is legal on a link whose workset is closed — only edits
/// throw.
/// </summary>
public static class LinkCloudIdentity
{
    /// <summary>The linked cloud model's GUID, or null when the link has no readable cloud
    /// reference (local-file links, unloaded exotic providers).</summary>
    public static Guid? TryGetCurrentModelGuid(RevitLinkType type)
    {
        try
        {
            if (type.IsExternalFileReference())
            {
                ExternalFileReference efr = type.GetExternalFileReference();
                ModelPath mp = efr.GetAbsolutePath();
                if (mp != null && mp.CloudPath)
                    return mp.GetModelGUID();
            }
        }
        catch { /* fall through to resource references */ }

        try
        {
            foreach (var kv in type.GetExternalResourceReferences())
            {
                var info = kv.Value.GetReferenceInformation();
                if ((info.TryGetValue("LinkedModelModelId", out string? mgs) ||
                     info.TryGetValue("modelGuid", out mgs)) && Guid.TryParse(mgs, out Guid mg))
                    return mg;
            }
        }
        catch { /* no readable reference at all */ }
        return null;
    }
}
