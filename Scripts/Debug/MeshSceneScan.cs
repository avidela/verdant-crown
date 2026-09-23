using System.Collections.Generic;
using Godot;

namespace VerdantCrown.Debug;

/// <summary>One resident read-back of a subtree: mesh nodes and scene triangles.</summary>
internal readonly record struct ScanResult(int MeshNodes, long SceneTris);

/// <summary>
/// Walks a subtree ONCE — per room load, not per frame — and reads back what is actually
/// resident: <see cref="MeshInstance3D"/> / <see cref="MultiMeshInstance3D"/> node counts and
/// their triangle totals. Two important counting rules:
/// <list type="bullet">
/// <item>Triangle counts come from <c>Mesh.GetFaces()</c> array LENGTHS (O(1) after the first
/// call per mesh), cached per mesh instance id — the call itself copies every vertex, so an
/// uncached per-sample walk would be the expensive live count this contract deliberately avoids.</item>
/// <item>A MultiMesh contributes <c>perMeshTris × LIVE instances</c> (<c>VisibleInstanceCount</c>,
/// where −1 means "draw all"), never capacity: counting capacity overstates visible geometry.
/// A pool is hidden by zeroing that count, never by toggling visibility.</item>
/// </list>
/// Depth-guarded so a deep rig can never turn a telemetry read into a stack overflow on device.
/// </summary>
internal static class MeshSceneScan
{
    /// <summary>Depth guard; anything deeper is not a room hierarchy and is not counted.</summary>
    private const int MaxDepth = 24;

    /// <summary>
    /// Per-mesh triangle cache for the life of the process. The game's meshes are built once
    /// and never mutated, so one <c>GetFaces()</c> copy per mesh is the whole cost.
    /// </summary>
    private static readonly Dictionary<ulong, long> TriangleCache = new();

    private struct Acc
    {
        public int Nodes;
        public long Tris;
    }

    public static ScanResult Scan(Node root)
    {
        Acc acc = default;
        Walk(root, ref acc, 0);
        return new ScanResult(acc.Nodes, acc.Tris);
    }

    private static void Walk(Node node, ref Acc acc, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (node)
        {
            case MeshInstance3D mi when mi.Mesh is not null:
                acc.Nodes++;
                acc.Tris += Triangles(mi.Mesh);
                break;

            case MultiMeshInstance3D mmi when mmi.Multimesh is not null:
                MultiMesh mm = mmi.Multimesh;
                acc.Nodes++;
                // −1 means "draw them all" (the default); otherwise the live subset.
                int live = mm.VisibleInstanceCount < 0 ? mm.InstanceCount : mm.VisibleInstanceCount;
                acc.Tris += (mm.Mesh is not null ? Triangles(mm.Mesh) : 0) * live;
                break;
        }

        foreach (Node child in node.GetChildren())
        {
            Walk(child, ref acc, depth + 1);
        }
    }

    /// <summary>Triangles in a mesh; Godot 4 has no surface array length, so count the faces.</summary>
    private static long Triangles(Mesh mesh)
    {
        ulong id = mesh.GetInstanceId();
        if (TriangleCache.TryGetValue(id, out long cached))
        {
            return cached;
        }

        long tris = 0;
        Vector3[]? faces = mesh.GetFaces();
        if (faces is not null)
        {
            tris = faces.Length / 3L;
        }

        TriangleCache[id] = tris;
        return tris;
    }
}
