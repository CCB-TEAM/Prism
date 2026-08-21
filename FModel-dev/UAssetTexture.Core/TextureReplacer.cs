namespace UAssetTexture.Core;

public static class TextureReplacer
{
    public static void ExtractMipPayloads(TextureAssetInfo info, string outputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        int ubulkOffset = 0;
        for (int i = 0; i < info.ExternalMipCount; i++)
        {
            TextureMip mip = info.Mips[i];
            byte[] payload = new byte[mip.ByteLength];
            Buffer.BlockCopy(info.UbulkData, ubulkOffset, payload, 0, payload.Length);
            File.WriteAllBytes(Path.Combine(fullOutputDirectory, $"mip{i}.bin"), payload);
            ubulkOffset += payload.Length;
        }

        if (info.InlineMips.Count > 0)
        {
            int firstStart = info.InlineMarkerOffsets[0] - info.InlineMips[0].ByteLength;
            byte[] firstPayload = new byte[info.InlineMips[0].ByteLength];
            Buffer.BlockCopy(info.ExportData, firstStart, firstPayload, 0, firstPayload.Length);
            File.WriteAllBytes(Path.Combine(fullOutputDirectory, $"mip{info.InlineMips[0].Index}.bin"), firstPayload);

            for (int i = 1; i < info.InlineMips.Count; i++)
            {
                TextureMip mip = info.InlineMips[i];
                int start = info.InlineMarkerOffsets[i - 1] + 16;
                byte[] payload = new byte[mip.ByteLength];
                Buffer.BlockCopy(info.ExportData, start, payload, 0, payload.Length);
                File.WriteAllBytes(Path.Combine(fullOutputDirectory, $"mip{mip.Index}.bin"), payload);
            }
        }
    }

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
