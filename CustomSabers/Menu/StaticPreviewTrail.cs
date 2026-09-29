using CustomSabersLite.Configuration;
using CustomSabersLite.Utilities.Extensions;
using SabersCore.Models;
using UnityEngine;

namespace CustomSabersLite.Menu;

internal class StaticPreviewTrail
{
    private readonly PluginConfig config;

    private readonly GameObject gameObject = new("StaticPreviewTrail");
    private readonly MeshRenderer meshRenderer;
    private readonly MaterialPropertyBlock materialPropertyBlock = new();
    
    private readonly Mesh mesh = new();
    private readonly Vector3[] vertices = new Vector3[4];
    private readonly int[] triangles = [0, 3, 1, /**/ 0, 2, 3];
    private readonly Vector2[] uvs = [new(1, 0), new(0, 0), new(1, 1), new(0, 1)];
    private readonly Color[] colors = new Color[4];

    public StaticPreviewTrail(PluginConfig config)
    {
        this.config = config;

        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        gameObject.AddComponent<MeshFilter>().mesh = mesh;
        mesh.MarkDynamic();
    }

    private ITrailData? trailData;
    private Color color = Color.white;

    public void Init(Transform parent)
    {
        gameObject.transform.SetParent(parent, false);
    }

    public void ReplaceTrail(ITrailData? trailData)
    {
        ClearPropertyBlock();
        color = Color.white;

        this.trailData = trailData;

        if (trailData is null)
        {
            meshRenderer.enabled = false;
            return;
        }
        
        meshRenderer.enabled = true;
        meshRenderer.sharedMaterials = trailData.Materials;
    }

    public void Dispose()
    {
        if (meshRenderer != null)
        {
            ClearPropertyBlock();
            meshRenderer.sharedMaterials = [];
        }

        if (mesh != null) Object.Destroy(mesh);
    }
    
    public void UpdateMesh()
    {
        if (trailData is null) return;

        var bot = trailData.TrailBottomOffset;
        var top = trailData.TrailTopOffset;

        if (config.OverrideSaberLength)
        {
            bot.z *= config.SaberLength;
            top.z *= config.SaberLength;
        }
        
        if (config.OverrideTrailWidth)
        {
            float distance = Vector3.Distance(top, bot);
            if (distance != 0) bot = Vector3.LerpUnclamped(top, bot, config.TrailWidth / distance);
        }
        
        float length = config.OverrideTrailDuration ? config.TrailDuration * 0.4f 
            : trailData.LengthSeconds.Clamp(0f, 0.4f);

        vertices[0] = bot;
        vertices[1] = top;
        
        bot.y += length;
        top.y += length;
        
        vertices[2] = bot;
        vertices[3] = top;
        
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        UpdateVertexColors();
    }

    public void SetColor(ColorScheme colorScheme, SaberType saberType)
    {
        color = Color.white;
        
        if (trailData is null) return;

        bool[] coloredMaterials = new bool[trailData.Materials.Length];
        foreach (var info in trailData.Colorizer.GetPropertiesWithColors(colorScheme))
        {
            if (info.MaterialIndex < 0 || info.MaterialIndex >= coloredMaterials.Length) continue;
            coloredMaterials[info.MaterialIndex] = true;
            if (info.ApplyToVertexColor) color = info.Color;
            materialPropertyBlock.Clear();
            meshRenderer.GetPropertyBlock(materialPropertyBlock, info.MaterialIndex);
            materialPropertyBlock.SetColor(info.PropertyName, info.Color);
            if (info.Material != null && info.Material.GetTag("ElectroTrail", false, "0") == "1")
            {
                materialPropertyBlock.SetColor("_EmissionColor", info.Color);
            }
            meshRenderer.SetPropertyBlock(materialPropertyBlock, info.MaterialIndex);
        }

        Color fallbackColor = saberType == SaberType.SaberA ? colorScheme.saberAColor : colorScheme.saberBColor;
        for (int i = 0; i < coloredMaterials.Length; i++)
        {
            Material material = trailData.Materials[i];
            if (coloredMaterials[i] || material == null || material.GetTag("ElectroTrail", false, "0") != "1") continue;

            color = fallbackColor;
            materialPropertyBlock.Clear();
            meshRenderer.GetPropertyBlock(materialPropertyBlock, i);
            materialPropertyBlock.SetColor("_Color", fallbackColor);
            materialPropertyBlock.SetColor("_EmissionColor", fallbackColor);
            meshRenderer.SetPropertyBlock(materialPropertyBlock, i);
        }

        UpdateVertexColors();
    }

    private void UpdateVertexColors()
    {
        if (mesh == null) return;
        for (int i = 0; i < colors.Length; i++) colors[i] = color;
        mesh.colors = colors;
    }

    private void ClearPropertyBlock()
    {
        materialPropertyBlock.Clear();
        for (int i = 0; i < meshRenderer.sharedMaterials.Length; i++) meshRenderer.SetPropertyBlock(null, i);
    }
}
