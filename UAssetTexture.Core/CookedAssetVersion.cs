using System.Buffers.Binary;
using UAssetAPI.UnrealTypes;

namespace UAssetTexture.Core;

/// <summary>
/// 决定"用哪个引擎版本号解析这份资产"。
///
/// cooked 包的摘要里版本号通常全是 0（unversioned，属性靠 usmap 还原），UAssetAPI 只能用调用方
/// 传进去的引擎版本号去套摘要排布。而摘要排布在 UE5.5 改过一次（文件头 legacy -9 就是那次的标记）：
/// <code>
///   legacy -8（UE5.0~5.4）：版本块 → CustomVersions → TotalHeaderSize → 包名 …
///   legacy -9（UE5.5+）    ：版本块 → SavedHash(20) → TotalHeaderSize → CustomVersions → 包名 …
/// </code>
/// 按 5.6 去读 -8 的老包，UAssetAPI 会先多读 20 字节 SavedHash，然后从包名字符串里读出一个
/// 天文数字的长度，直接抛 <c>ArgumentException: The array starting from the specified index is
/// not long enough…</c>（表现就是「加入替换」点下去只报错）。
///
/// 所以这批包按 5.4 解析：5.4 的 ObjectVersionUE5 = 1012，早于 PACKAGE_SAVED_HASH，摘要与后续
/// 那些 5.5+ 才有的字段都不会被读。
/// </summary>
public static class CookedAssetVersion
{
    private const uint PackageFileTag = 0x9E2A83C1u;
    private const int LegacyFileVersionWithoutSavedHash = -8;
    private const int MinHeaderBytes = 24;

    /// <summary>UE5.4：ObjectVersionUE5 = 1012，摘要里还没有 SavedHash。</summary>
    public const EngineVersion PreSavedHashEngineVersion = EngineVersion.VER_UE5_4;

    /// <summary>
    /// 需要时把引擎版本号降到 5.4；其余情况原样返回（有版本号的资产本来就用文件里写的版本，
    /// 这里的种子版本不会覆盖它们）。
    /// </summary>
    public static EngineVersion Resolve(string assetPath, EngineVersion requested)
    {
        if (requested < EngineVersion.VER_UE5_5)
            return requested; // 调用方本来就没按 5.5+ 读

        return IsUnversionedBeforeSavedHash(assetPath) ? PreSavedHashEngineVersion : requested;
    }

    /// <summary>
    /// 只看开头 24 字节：legacy -8 且三个版本号全是 0，就是 5.4 及更早 cook 出来的 unversioned 包。
    /// </summary>
    public static bool IsUnversionedBeforeSavedHash(string assetPath)
    {
        try
        {
            using var stream = File.OpenRead(assetPath);
            Span<byte> head = stackalloc byte[MinHeaderBytes];
            if (stream.ReadAtLeast(head, MinHeaderBytes, throwOnEndOfStream: false) < MinHeaderBytes)
                return false;

            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != PackageFileTag)
                return false;

            if (BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != LegacyFileVersionWithoutSavedHash)
                return false; // -9 是新排布；-7 及更早是 UE4 的编码，换 5.4 也治不了

            return BinaryPrimitives.ReadInt32LittleEndian(head[8..]) == 0 &&   // LegacyUE3Version
                   BinaryPrimitives.ReadInt32LittleEndian(head[12..]) == 0 &&  // FileVersionUE4
                   BinaryPrimitives.ReadInt32LittleEndian(head[16..]) == 0 &&  // FileVersionUE5
                   BinaryPrimitives.ReadInt32LittleEndian(head[20..]) == 0;    // FileVersionLicenseeUE
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
