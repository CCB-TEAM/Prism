using System.Buffers.Binary;

namespace PakTool.Core;

/// <summary>cooked 包的摘要（FPackageFileSummary）用的是哪一套排布。</summary>
public enum PackageSummaryFlavor
{
    /// <summary>认不出来：太短、magic 不对，或者是 UE3 的老式（非负数）版本编码。</summary>
    Unknown,

    /// <summary>包里写了真实版本号，读的时候以文件自己写的版本为准，不会选错排布。</summary>
    Versioned,

    /// <summary>版本号全是 0（cooked unversioned）+ legacy -9：UE5.5 起的排布，带 SavedHash。</summary>
    UnversionedWithSavedHash,

    /// <summary>版本号全是 0 + legacy -8：UE5.0~5.4 的排布，没有 SavedHash。</summary>
    UnversionedBeforeSavedHash,
}

/// <summary>按"摘要头该用哪套排布"给出的结论。</summary>
public enum PackageSummaryProfileDecision
{
    /// <summary>保持调用方要求的引擎版本号。</summary>
    KeepRequestedProfile,

    /// <summary>整包都是 UE5.5 之前 cook 的：换成那个年代的版本号，否则摘要会被读错。</summary>
    UsePreSavedHashProfile,
}

/// <summary>
/// 只看开头几十字节，判断 cooked 包的摘要排布。
///
/// 背景：cooked 包常把版本号全写成 0（unversioned，属性靠 usmap 还原），于是读包的一方只能
/// 借用"自己声明的引擎版本号"。而摘要本身的排布在 UE5.5 改过一次（legacy -9 就是那次改动的标记）：
/// <code>
///   legacy -8：版本块 → CustomVersions → TotalHeaderSize → 包名 …
///   legacy -9：版本块 → SavedHash(20) → TotalHeaderSize → CustomVersions → 包名 …
/// </code>
/// CUE4Parse 只用"假定版本号是否 ≥ PACKAGE_SAVED_HASH"来选排布，所以一个 -8 的老包在默认
/// （按 UE5.6 读）时会被当成 -9：多读 20 字节 SavedHash，接着把包名字符串当成
/// CustomVersionCount 读出天文数字，直接抛 "Read size is bigger than remaining archive length"。
/// 这里先自己看一眼摘要头，再决定要不要把会话降回 UE5.4 的版本号。
/// </summary>
public readonly record struct PackageSummaryLayout(
    PackageSummaryFlavor Flavor,
    int LegacyFileVersion,
    int Ue3Version,
    int Ue4Version,
    int Ue5Version,
    int LicenseeVersion)
{
    /// <summary>FPackageFileSummary.PACKAGE_FILE_TAG</summary>
    public const uint PackageFileTag = 0x9E2A83C1u;

    /// <summary>FPackageFileSummary.PACKAGE_FILE_TAG_SWAPPED（大小端相反的包，我们不处理）</summary>
    public const uint PackageFileTagSwapped = 0xC1832A9Eu;

    /// <summary>UE5.5 起：PACKAGE_SAVED_HASH 与 legacy -9 一起到来，摘要从这一版开始换了排布。</summary>
    public const int SavedHashLegacyFileVersion = -9;

    /// <summary>至少需要这么多字节才可能判断出来（tag + legacy + ue3 + ue4 + ue5 + licensee）。</summary>
    public const int MinHeaderBytes = 24;

    /// <summary>版本号全是 0 —— 也就是 cooked 包里常见的 unversioned。</summary>
    public bool IsUnversioned =>
        Ue3Version == 0 && Ue4Version == 0 && Ue5Version == 0 && LicenseeVersion == 0;

    /// <summary>摘要里有没有 UE5 版本号这个字段（legacy ≤ -8 才有）。</summary>
    public bool HasUe5VersionField => LegacyFileVersion <= -8;

    /// <summary>
    /// 这个包得用 UE5.5 之前（且带 UE5 版本号）的版本号去读，摘要才不会是错的。
    /// legacy -7 及更早是 UE4 年代的编码，换个 UE5 的版本号也治不了，这里不揽这个活。
    /// </summary>
    public bool NeedsPreSavedHashProfile =>
        Flavor == PackageSummaryFlavor.UnversionedBeforeSavedHash && HasUe5VersionField;

    /// <summary>
    /// 解析摘要头。字段顺序与 UE 的 <c>FPackageFileSummary::Serialize</c> 一致：
    /// tag、legacyFileVersion、[UE3 版本]、UE4 版本、[UE5 版本（legacy ≤ -8 才有）]、licensee 版本。
    /// </summary>
    public static PackageSummaryLayout Parse(ReadOnlySpan<byte> head)
    {
        if (head.Length < MinHeaderBytes)
            return Unknown;

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != PackageFileTag)
        {
            // 大小端反过来的包 CUE4Parse 本来就拒绝；其它 magic 一律不认。
            return Unknown;
        }

        int legacy = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        if (legacy >= 0)
        {
            // 非负数代表 UE3 老式编码（高 16 位 licensee、低 16 位引擎版本），不走这个判断。
            return Unknown;
        }

        int offset = 8;
        int ue3 = 0;
        if (legacy != -4)
        {
            ue3 = BinaryPrimitives.ReadInt32LittleEndian(head[offset..]);
            offset += 4;
        }

        int ue4 = BinaryPrimitives.ReadInt32LittleEndian(head[offset..]);
        offset += 4;

        int ue5 = 0;
        if (legacy <= -8)
        {
            ue5 = BinaryPrimitives.ReadInt32LittleEndian(head[offset..]);
            offset += 4;
        }

        int licensee = BinaryPrimitives.ReadInt32LittleEndian(head[offset..]);

        var flavor = ue3 == 0 && ue4 == 0 && ue5 == 0 && licensee == 0
            ? legacy <= SavedHashLegacyFileVersion
                ? PackageSummaryFlavor.UnversionedWithSavedHash
                : PackageSummaryFlavor.UnversionedBeforeSavedHash
            : PackageSummaryFlavor.Versioned;

        return new PackageSummaryLayout(flavor, legacy, ue3, ue4, ue5, licensee);
    }

    private static readonly PackageSummaryLayout Unknown = new(
        PackageSummaryFlavor.Unknown, 0, 0, 0, 0, 0);

    /// <summary>
    /// 抽样结论：只要有一份是老排布、又没有一份是 -9 的新排布，就按老版本号读整包。
    /// 混装（两个年代都有）时保持请求的版本号 —— 指望一套版本号同时读两代包是不可能的，
    /// 至少别把本来能读的新包也读坏。
    /// </summary>
    public static PackageSummaryProfileDecision DecideProfile(IReadOnlyList<PackageSummaryLayout> samples)
    {
        bool legacy = false;
        foreach (var sample in samples)
        {
            if (sample.NeedsPreSavedHashProfile)
            {
                legacy = true;
            }
            else if (sample.Flavor == PackageSummaryFlavor.UnversionedWithSavedHash)
            {
                return PackageSummaryProfileDecision.KeepRequestedProfile;
            }
        }

        return legacy
            ? PackageSummaryProfileDecision.UsePreSavedHashProfile
            : PackageSummaryProfileDecision.KeepRequestedProfile;
    }
}
