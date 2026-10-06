using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Gui;

// Small vector marks avoid depending on a check/lock glyph in the active TMP
// font. MaskableGraphic follows the card's clipping, theme color and opacity.
internal sealed class SkillTreeNodeBadge : MaskableGraphic
{
    internal bool Locked;

    public override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (Locked)
        {
            // Closed shackle and outlined body, drawn in a 20-unit square.
            Stroke(mesh, new Vector2(6, 12), new Vector2(6, 17), 2);
            Stroke(mesh, new Vector2(6, 17), new Vector2(14, 17), 2);
            Stroke(mesh, new Vector2(14, 17), new Vector2(14, 12), 2);
            Stroke(mesh, new Vector2(4, 12), new Vector2(16, 12), 2);
            Stroke(mesh, new Vector2(4, 12), new Vector2(4, 3), 2);
            Stroke(mesh, new Vector2(4, 3), new Vector2(16, 3), 2);
            Stroke(mesh, new Vector2(16, 3), new Vector2(16, 12), 2);
            Stroke(mesh, new Vector2(10, 6), new Vector2(10, 9), 2);
        }
        else
        {
            Stroke(mesh, new Vector2(3, 10), new Vector2(8, 5), 2.8f);
            Stroke(mesh, new Vector2(8, 5), new Vector2(17, 16), 2.8f);
        }
    }

    private void Stroke(VertexHelper mesh, Vector2 a, Vector2 b, float width)
    {
        Rect rect = GetPixelAdjustedRect();
        float scale = Mathf.Min(rect.width, rect.height) / 20f;
        Vector2 center = rect.center;
        a = center + (a - new Vector2(10, 10)) * scale;
        b = center + (b - new Vector2(10, 10)) * scale;
        Vector2 direction = (b - a).normalized;
        Vector2 normal = new Vector2(-direction.y, direction.x) * (width * scale * 0.5f);
        int start = mesh.currentVertCount;
        mesh.AddVert(a - normal, color, Vector2.zero);
        mesh.AddVert(a + normal, color, Vector2.zero);
        mesh.AddVert(b + normal, color, Vector2.zero);
        mesh.AddVert(b - normal, color, Vector2.zero);
        mesh.AddTriangle(start, start + 1, start + 2);
        mesh.AddTriangle(start + 2, start + 3, start);
    }
}
