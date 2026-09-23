using Godot;

namespace Kit;

/// <summary>
/// Engine-generic mesh/material helpers (zero game knowledge).
/// </summary>
public static class MeshKit
{
    /// <summary>
    /// Applies the shipping art configuration to every material reached under
    /// <paramref name="root"/> — two duties, one walk:
    /// (1) enables vertex-color-as-albedo on imported-art materials whose mesh actually
    /// carries a COLOR attribute. The glTF exporter ships painted colours as
    /// COLOR_0, but Godot's imported StandardMaterial3D leaves
    /// <c>VertexColorUseAsAlbedo</c> off, so every painted model renders white
    /// (Verdant Crown art pass, 2026-09). Meshes without the attribute keep their
    /// authored albedo; and (2) sets <c>ShadingMode = Unshaded</c> on EVERY
    /// StandardMaterial3D walked — the R36S-measured shipping configuration is
    /// unshaded vertex-colour art with NO realtime lights (lit scenes measured
    /// 27–34 fps on device; unshaded held the 60 fps lock). Unshading is deliberately
    /// NOT gated on hasVertexColors: levels ship zero lights and no WorldEnvironment/
    /// ambient (project.godot configures none), so any remaining LIT material — grey
    /// fallback boxes, primitives, non-coloured overrides — would render pure black.
    /// Materials are shared resources, so each is mutated once; idempotent.
    /// </summary>
    public static void ApplyVertexColors(Node? root)
    {
        if (root is MeshInstance3D meshInstance)
        {
            if (meshInstance.Mesh is ArrayMesh arrayMesh)
            {
                int surfaces = arrayMesh.GetSurfaceCount();
                for (int s = 0; s < surfaces; s++)
                {
                    Godot.Collections.Array arrays = arrayMesh.SurfaceGetArrays(s);
                    Variant colorSlot = arrays[(int)Mesh.ArrayType.Color];
                    // COLOR_0 presence was verified in every exported GLB — the type check
                    // alone is enough (Godot returns PackedColorArray here, log-proven).
                    bool hasVertexColors = colorSlot.VariantType is Variant.Type.PackedColorArray or Variant.Type.Array;
                    StandardMaterial3D? surfaceMaterial = arrayMesh.SurfaceGetMaterial(s) as StandardMaterial3D;
                    Material? overrideMaterial = meshInstance.GetSurfaceOverrideMaterial(s);
                    if (hasVertexColors && surfaceMaterial is not null)
                    {
                        surfaceMaterial.VertexColorUseAsAlbedo = true;
                    }
                    else if (hasVertexColors && overrideMaterial is StandardMaterial3D overrideStd)
                    {
                        overrideStd.VertexColorUseAsAlbedo = true;
                    }
                    Unshade(surfaceMaterial);
                    Unshade(overrideMaterial);
                }
            }

            // Black-render safety (doc comment, duty 2): node-level override and
            // primitive-mesh (BoxMesh fallback) materials are not ArrayMesh surfaces —
            // without this reach they stay LIT and render black in the lightless levels.
            Unshade(meshInstance.MaterialOverride);
            if (meshInstance.Mesh is PrimitiveMesh primitiveMesh)
            {
                if (primitiveMesh.Material is StandardMaterial3D)
                {
                }
                Unshade(primitiveMesh.Material);
            }
        }

        if (root is null)
        {
            return;
        }

        foreach (Node child in root.GetChildren())
        {
            ApplyVertexColors(child);
        }
    }

    /// <summary>Unshades any StandardMaterial3D (BaseMaterial3D.ShadingModeEnum.Unshaded == 0).</summary>
    private static void Unshade(Material? material)
    {
        if (material is StandardMaterial3D standard)
        {
            standard.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
    }
}
