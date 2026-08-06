namespace UAssetTexture.Core;

internal static class TextureReplacer
{
    public static void WriteReplacement(TextureAssetInfo info, byte[][] mipPayloads, string outputAssetPath)
    {
        if (mipPayloads.Length != info.Mips.Count)
            throw new InvalidOperationException($"Expected {info.Mips.Count} mip payloads, got {mipPayloads.Length}.");

        var targetAssetPath = Path.GetFullPath(outputAssetPath);
        var targetUexpPath = Path.ChangeExtension(targetAssetPath, ".uexp");
        var targetUbulkPath = Path.ChangeExtension(targetAssetPath, ".ubulk");
        Directory.CreateDirectory(Path.GetDirectoryName(targetAssetPath)!);

        var export = (byte[])info.ExportData.Clone();
        var footer = (byte[])info.UexpFooter.Clone();
        var ubulk = (byte[])info.UbulkData.Clone();
        var payloadByMipIndex = info.Mips
            .Select((mip, payloadIndex) => new { mip.Index, Payload = mipPayloads[payloadIndex] })
            .ToDictionary(item => item.Index, item => item.Payload);

        foreach (var placement in info.MipPlacements.OrderBy(placement => placement.Offset))
        {
            if (!payloadByMipIndex.TryGetValue(placement.Index, out var payload))
                throw new InvalidOperationException($"No replacement payload was generated for mip {placement.Index}.");
            if (payload.Length != placement.ByteLength)
                throw new InvalidOperationException($"Replacement payload for mip {placement.Index} has {payload.Length} bytes, expected {placement.ByteLength}.");

            if (placement.Storage == TextureMipStorage.Ubulk)
            {
                Buffer.BlockCopy(payload, 0, ubulk, placement.Offset, payload.Length);
            }
            else if (placement.Storage == TextureMipStorage.UexpInline)
            {
                Buffer.BlockCopy(payload, 0, export, placement.Offset, payload.Length);
            }
            else
            {
                Buffer.BlockCopy(payload, 0, footer, placement.Offset, payload.Length);
            }
        }

        var uexp = new byte[export.Length + footer.Length];
        Buffer.BlockCopy(export, 0, uexp, 0, export.Length);
        Buffer.BlockCopy(footer, 0, uexp, export.Length, footer.Length);

        File.Copy(info.AssetPath, targetAssetPath, overwrite: true);
        File.WriteAllBytes(targetUexpPath, uexp);
        if (info.ExternalMipCount > 0 || File.Exists(info.UbulkPath))
            File.WriteAllBytes(targetUbulkPath, ubulk);
        else if (File.Exists(targetUbulkPath))
            File.Delete(targetUbulkPath);
    }
}
