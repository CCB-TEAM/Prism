using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetTexture.Core;

public sealed class MaterialInstanceParameterService
{
    public Task<MaterialInstanceParameterSet> InspectAsync(
        string assetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asset = LoadAsset(assetPath, engineVersion, usmapPath);
        var export = FindMaterialInstanceExport(asset);
        return Task.FromResult(ReadParameterSet(asset, export, Path.GetFullPath(assetPath)));
    }

    public Task<MaterialInstanceParameterPatchResult> ApplyAsync(
        string assetPath,
        string outputAssetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        IReadOnlyList<MaterialInstanceParameterUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asset = LoadAsset(assetPath, engineVersion, usmapPath);
        var export = FindMaterialInstanceExport(asset);
        ApplyUpdates(asset, export, updates);

        var targetAssetPath = Path.GetFullPath(outputAssetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetAssetPath)!);
        asset.Write(targetAssetPath);

        var parameters = ReadParameterSet(asset, export, targetAssetPath);
        var uexpPath = Path.ChangeExtension(targetAssetPath, ".uexp");
        return Task.FromResult(new MaterialInstanceParameterPatchResult(
            targetAssetPath,
            File.Exists(uexpPath) ? uexpPath : null,
            parameters));
    }

    private static UAsset LoadAsset(string assetPath, EngineVersion engineVersion, string? usmapPath)
    {
        var mappings = string.IsNullOrWhiteSpace(usmapPath) ? null : new Usmap(usmapPath);
        return new UAsset(Path.GetFullPath(assetPath), engineVersion, mappings);
    }

    private static NormalExport FindMaterialInstanceExport(UAsset asset)
    {
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var className = ResolveClassName(asset, export);
            if (className.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
                return export;
        }

        throw new InvalidOperationException("No parsed MaterialInstance export was found.");
    }

    private static MaterialInstanceParameterSet ReadParameterSet(UAsset asset, NormalExport export, string assetPath)
    {
        var scalarArray = GetArray(export, "ScalarParameterValues");
        var vectorArray = GetArray(export, "VectorParameterValues");
        var textureArray = GetArray(export, "TextureParameterValues");
        var textureOptions = ReadTextureOptions(asset);

        return new MaterialInstanceParameterSet(
            assetPath,
            export.ObjectName?.ToString() ?? Path.GetFileNameWithoutExtension(assetPath),
            ResolveClassName(asset, export),
            ReadScalars(scalarArray).ToArray(),
            ReadVectors(vectorArray).ToArray(),
            ReadTextures(asset, textureArray).ToArray(),
            textureOptions);
    }

    private static IReadOnlyList<MaterialTextureOption> ReadTextureOptions(UAsset asset)
    {
        var options = new List<MaterialTextureOption>();
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (!string.Equals(import.ClassName?.ToString(), "Texture2D", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawIndex = FPackageIndex.FromImport(i).Index;
            options.Add(new MaterialTextureOption(rawIndex, import.ObjectName?.ToString() ?? $"Import {i}", ResolveObjectPath(asset, new FPackageIndex(rawIndex))));
        }

        return options
            .GroupBy(option => option.RawIndex)
            .Select(group => group.First())
            .OrderBy(option => option.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<MaterialScalarParameter> ReadScalars(ArrayPropertyData? array)
    {
        if (array?.Value is null)
            yield break;

        for (var i = 0; i < array.Value.Length; i++)
        {
            if (array.Value[i] is not StructPropertyData item)
                continue;

            if (TryGetFloat(item, "ParameterValue", out var value))
                yield return new MaterialScalarParameter(i, GetParameterName(item), value);
        }
    }

    private static IEnumerable<MaterialVectorParameter> ReadVectors(ArrayPropertyData? array)
    {
        if (array?.Value is null)
            yield break;

        for (var i = 0; i < array.Value.Length; i++)
        {
            if (array.Value[i] is not StructPropertyData item)
                continue;

            if (TryGetLinearColor(item, "ParameterValue", out var color))
                yield return new MaterialVectorParameter(i, GetParameterName(item), color.R, color.G, color.B, color.A);
        }
    }

    private static IEnumerable<MaterialTextureParameter> ReadTextures(UAsset asset, ArrayPropertyData? array)
    {
        if (array?.Value is null)
            yield break;

        for (var i = 0; i < array.Value.Length; i++)
        {
            if (array.Value[i] is not StructPropertyData item)
                continue;

            if (GetProperty(item, "ParameterValue") is not ObjectPropertyData objectProperty || objectProperty.Value is null)
                continue;

            var rawIndex = objectProperty.Value.Index;
            var path = ResolveObjectPath(asset, objectProperty.Value);
            yield return new MaterialTextureParameter(i, GetParameterName(item), rawIndex, ResolveObjectName(asset, objectProperty.Value), path);
        }
    }

    private static void ApplyUpdates(UAsset asset, NormalExport export, IReadOnlyList<MaterialInstanceParameterUpdate> updates)
    {
        foreach (var update in updates)
        {
            var kind = update.Kind.Trim().ToLowerInvariant();
            switch (kind)
            {
                case "scalar":
                    if (update.Value is null)
                        throw new InvalidOperationException("Scalar update is missing a value.");
                    SetScalar(GetArrayItem(export, "ScalarParameterValues", update.Index), update.Value.Value);
                    break;

                case "vector":
                    if (update.R is null || update.G is null || update.B is null || update.A is null)
                        throw new InvalidOperationException("Vector update is missing one or more RGBA values.");
                    SetVector(GetArrayItem(export, "VectorParameterValues", update.Index), update.R.Value, update.G.Value, update.B.Value, update.A.Value);
                    break;

                case "texture":
                    if (update.RawIndex is null)
                        throw new InvalidOperationException("Texture update is missing a texture index.");
                    if (!IsExistingTextureImport(asset, update.RawIndex.Value))
                        throw new InvalidOperationException("Texture parameters can only point to Texture2D imports already present in this material instance.");
                    SetTexture(GetArrayItem(export, "TextureParameterValues", update.Index), update.RawIndex.Value);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported material parameter kind: {update.Kind}");
            }
        }
    }

    private static StructPropertyData GetArrayItem(NormalExport export, string arrayName, int index)
    {
        var array = GetArray(export, arrayName) ?? throw new InvalidOperationException($"{arrayName} was not found.");
        if (array.Value is null || index < 0 || index >= array.Value.Length || array.Value[index] is not StructPropertyData item)
            throw new InvalidOperationException($"{arrayName}[{index}] was not found.");

        return item;
    }

    private static void SetScalar(StructPropertyData item, float value)
    {
        if (GetProperty(item, "ParameterValue") is not FloatPropertyData parameterValue)
            throw new InvalidOperationException("Scalar parameter does not contain a float ParameterValue.");

        parameterValue.Value = value;
    }

    private static void SetVector(StructPropertyData item, float r, float g, float b, float a)
    {
        var color = new FLinearColor(r, g, b, a);
        var property = GetProperty(item, "ParameterValue");
        switch (property)
        {
            case LinearColorPropertyData linearColor:
                linearColor.Value = color;
                return;

            case StructPropertyData structColor:
                SetFloatIfPresent(structColor, "R", r);
                SetFloatIfPresent(structColor, "G", g);
                SetFloatIfPresent(structColor, "B", b);
                SetFloatIfPresent(structColor, "A", a);
                return;

            default:
                throw new InvalidOperationException("Vector parameter does not contain a LinearColor ParameterValue.");
        }
    }

    private static void SetTexture(StructPropertyData item, int rawIndex)
    {
        if (GetProperty(item, "ParameterValue") is not ObjectPropertyData parameterValue)
            throw new InvalidOperationException("Texture parameter does not contain an object ParameterValue.");

        parameterValue.Value = new FPackageIndex(rawIndex);
    }

    private static bool IsExistingTextureImport(UAsset asset, int rawIndex)
    {
        var index = new FPackageIndex(rawIndex);
        if (!index.IsImport())
            return false;

        var import = index.ToImport(asset);
        return string.Equals(import?.ClassName?.ToString(), "Texture2D", StringComparison.OrdinalIgnoreCase);
    }

    private static ArrayPropertyData? GetArray(NormalExport export, string name)
    {
        return export.Data.FirstOrDefault(property => NamesEqual(property.Name, name)) as ArrayPropertyData;
    }

    private static PropertyData? GetProperty(StructPropertyData item, string name)
    {
        return item.Value?.FirstOrDefault(property => NamesEqual(property.Name, name));
    }

    private static string GetParameterName(StructPropertyData item)
    {
        if (GetProperty(item, "ParameterName") is NamePropertyData parameterName &&
            !string.IsNullOrWhiteSpace(parameterName.Value?.ToString()) &&
            !string.Equals(parameterName.Value.ToString(), "None", StringComparison.OrdinalIgnoreCase))
        {
            return parameterName.Value.ToString();
        }

        if (GetProperty(item, "ParameterInfo") is StructPropertyData parameterInfo &&
            GetProperty(parameterInfo, "Name") is NamePropertyData infoName &&
            !string.IsNullOrWhiteSpace(infoName.Value?.ToString()))
        {
            return infoName.Value.ToString();
        }

        return item.Name?.ToString() ?? "Parameter";
    }

    private static bool TryGetFloat(StructPropertyData item, string name, out float value)
    {
        if (GetProperty(item, name) is FloatPropertyData property)
        {
            value = property.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetLinearColor(StructPropertyData item, string name, out FLinearColor color)
    {
        var property = GetProperty(item, name);
        switch (property)
        {
            case LinearColorPropertyData linearColor:
                color = linearColor.Value;
                return true;

            case StructPropertyData structColor
                when TryGetFloat(structColor, "R", out var r) &&
                     TryGetFloat(structColor, "G", out var g) &&
                     TryGetFloat(structColor, "B", out var b):
                TryGetFloat(structColor, "A", out var a);
                color = new FLinearColor(r, g, b, a);
                return true;

            default:
                color = default;
                return false;
        }
    }

    private static void SetFloatIfPresent(StructPropertyData item, string name, float value)
    {
        if (GetProperty(item, name) is FloatPropertyData property)
            property.Value = value;
    }

    private static bool NamesEqual(FName? name, string value)
    {
        return string.Equals(name?.ToString(), value, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveClassName(UAsset asset, Export export)
    {
        if (export.ClassIndex is null || export.ClassIndex.IsNull())
            return string.Empty;

        if (export.ClassIndex.IsExport())
            return export.ClassIndex.ToExport(asset)?.ObjectName?.ToString() ?? string.Empty;

        var import = export.ClassIndex.ToImport(asset);
        return import?.ObjectName?.ToString() ?? string.Empty;
    }

    private static string ResolveObjectName(UAsset asset, FPackageIndex index)
    {
        if (index is null || index.IsNull())
            return "None";

        if (index.IsImport())
            return index.ToImport(asset)?.ObjectName?.ToString() ?? index.Index.ToString();

        if (index.IsExport())
            return index.ToExport(asset)?.ObjectName?.ToString() ?? index.Index.ToString();

        return index.Index.ToString();
    }

    private static string ResolveObjectPath(UAsset asset, FPackageIndex index)
    {
        if (index is null || index.IsNull())
            return "None";

        if (index.IsExport())
            return index.ToExport(asset)?.ObjectName?.ToString() ?? index.Index.ToString();

        var import = index.ToImport(asset);
        if (import is null)
            return index.Index.ToString();

        var parts = new Stack<string>();
        var current = import;
        while (current is not null)
        {
            var name = current.ObjectName?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                parts.Push(name);

            current = current.OuterIndex is { } outer && outer.IsImport()
                ? outer.ToImport(asset)
                : null;
        }

        return string.Join("/", parts);
    }
}
