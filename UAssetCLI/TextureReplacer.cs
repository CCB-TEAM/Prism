internal static class TextureReplacer
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
        {
            throw new InvalidOperationException($"Expected {info.Mips.Count} mip payloads, got {mipPayloads.Length}.");
        }

        string targetAssetPath = Path.GetFullPath(outputAssetPath);
        string targetUexpPath = Path.ChangeExtension(targetAssetPath, ".uexp");
        string targetUbulkPath = Path.ChangeExtension(targetAssetPath, ".ubulk");
        Directory.CreateDirectory(Path.GetDirectoryName(targetAssetPath)!);

        byte[] export = (byte[])info.ExportData.Clone();
        byte[] ubulk = new byte[info.UbulkData.Length];

        int ubulkOffset = 0;
        for (int i = 0; i < info.ExternalMipCount; i++)
        {
            byte[] payload = mipPayloads[i];
            Buffer.BlockCopy(payload, 0, ubulk, ubulkOffset, payload.Length);
            ubulkOffset += payload.Length;
        }

        if (info.InlineMips.Count > 0)
        {
            int firstMarker = info.InlineMarkerOffsets[0];
            byte[] firstInline = mipPayloads[info.ExternalMipCount];
            int firstStart = firstMarker - firstInline.Length;
            Buffer.BlockCopy(firstInline, 0, export, firstStart, firstInline.Length);

            for (int i = 1; i < info.InlineMips.Count; i++)
            {
                byte[] payload = mipPayloads[info.ExternalMipCount + i];
                int payloadStart = info.InlineMarkerOffsets[i - 1] + 16;
                Buffer.BlockCopy(payload, 0, export, payloadStart, payload.Length);
            }
        }

        byte[] uexp = new byte[export.Length + info.UexpFooter.Length];
        Buffer.BlockCopy(export, 0, uexp, 0, export.Length);
        Buffer.BlockCopy(info.UexpFooter, 0, uexp, export.Length, info.UexpFooter.Length);

        File.Copy(info.AssetPath, targetAssetPath, overwrite: true);
        File.WriteAllBytes(targetUexpPath, uexp);
        if (info.ExternalMipCount > 0 || File.Exists(info.UbulkPath))
        {
            File.WriteAllBytes(targetUbulkPath, ubulk);
        }
    }
}
