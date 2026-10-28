using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// How a link's reparse data is read: the bounds it is held to and the link it gives, from <c>REPARSE_DATA_BUFFER</c>
/// bytes built by hand; nothing is opened, so the parse runs anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FinalPathTests
{
    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    private const uint SymlinkTag = 0xA000000C;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>.</summary>
    private const uint MountPointTag = 0xA0000003;

    /// <summary><c>IO_REPARSE_TAG_CLOUD</c>, a cloud files placeholder.</summary>
    private const uint CloudTag = 0x9000001A;

    /// <summary>Where a symbolic link's path buffer starts, after its flags.</summary>
    private const int SymlinkNames = 20;

    /// <summary>Where a mount point's path buffer starts.</summary>
    private const int MountPointNames = 16;

    /// <summary><c>SYMLINK_FLAG_RELATIVE</c>.</summary>
    private const uint RelativeFlag = 1;

    private const string Malformed = "the link's reparse data is malformed";

    [Fact]
    public void ThreeBytesAreMalformed() => AssertMalformed(SymbolicLink(@"..\x", RelativeFlag), returned: 3);

    [Fact]
    public void ASymbolicLinkShorterThanItsHeaderIsMalformed() => AssertMalformed(SymbolicLink(@"..\x", RelativeFlag), returned: 18);

    [Fact]
    public void ANameRunningPastTheDataReadIsMalformed()
    {
        byte[] data = SymbolicLink(@"..\x", RelativeFlag);
        // The substitute name is said to run over the print name and two bytes past the end of the data.
        SetName(data, offset: 0, length: data.Length - SymlinkNames + 2);

        AssertMalformed(data, data.Length);
    }

    [Fact]
    public void ANameHoldingANulIsMalformed()
    {
        byte[] data = SymbolicLink("..\\x\0y", RelativeFlag);

        AssertMalformed(data, data.Length);
    }

    [Fact]
    public void AnOddNameOffsetIsMalformed()
    {
        // Every byte of U+4141 is 0x41, so from an odd offset the name still decodes, with no NUL: only the offset is wrong.
        byte[] data = SymbolicLink("\u4141\u4141\u4141", RelativeFlag);
        SetName(data, offset: 1, length: 4);

        AssertMalformed(data, data.Length);
    }

    [Fact]
    public void AnOddNameLengthIsMalformed()
    {
        // Three bytes decode as "A" and a stray byte read as U+FFFD, with no NUL: only the length is wrong.
        byte[] data = SymbolicLink("AB", RelativeFlag);
        SetName(data, offset: 0, length: 3);

        AssertMalformed(data, data.Length);
    }

    [Fact]
    public void AnEmptySubstituteNameIsMalformed()
    {
        byte[] symbolic = SymbolicLink("", RelativeFlag);
        byte[] mountPoint = MountPoint("");

        AssertMalformed(symbolic, symbolic.Length);
        AssertMalformed(mountPoint, mountPoint.Length);
    }

    [Fact]
    public void AWellFormedRelativeSymbolicLinkIsRead()
    {
        byte[] data = SymbolicLink(@"..\shots", RelativeFlag);

        Assert.Equal(new StoredLink(LinkKind.SymbolicLink, @"..\shots", Relative: true), FinalPath.ParseLink(data, data.Length));
    }

    [Fact]
    public void AWellFormedMountPointIsRead()
    {
        byte[] data = MountPoint(@"\??\X:\t");

        Assert.Equal(new StoredLink(LinkKind.MountPoint, @"\??\X:\t", Relative: false), FinalPath.ParseLink(data, data.Length));
    }

    [Fact]
    public void AnotherReparseTagIsNoLink()
    {
        byte[] data = MountPoint(@"\??\X:\t");
        BinaryPrimitives.WriteUInt32LittleEndian(data, CloudTag);

        Assert.Null(FinalPath.ParseLink(data, data.Length));
    }

    private static void AssertMalformed(byte[] data, int returned)
    {
        IOException refused = Assert.Throws<IOException>(() => FinalPath.ParseLink(data, returned));
        Assert.Equal(Malformed, refused.Message);
    }

    /// <summary>A symbolic link's data: the header, its flags at 16, then its names from 20.</summary>
    private static byte[] SymbolicLink(string target, uint flags)
    {
        byte[] data = Data(SymlinkTag, SymlinkNames, target);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), flags);
        return data;
    }

    /// <summary>A mount point's data: the header, then its names from 16.</summary>
    private static byte[] MountPoint(string target) => Data(MountPointTag, MountPointNames, target);

    /// <summary>
    /// A <c>REPARSE_DATA_BUFFER</c> whose path buffer starts at <paramref name="names"/> and holds <paramref name="target"/>
    /// twice, as the substitute name and then as the print name.
    /// </summary>
    private static byte[] Data(uint tag, int names, string target)
    {
        byte[] name = Encoding.Unicode.GetBytes(target);
        byte[] data = new byte[names + (2 * name.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(data.Length - 8));
        SetName(data, offset: 0, length: name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)name.Length);
        name.CopyTo(data, names);
        name.CopyTo(data, names + name.Length);
        return data;
    }

    /// <summary>Sets the substitute name's offset (at 8) and length (at 10), in bytes from the path buffer's start.</summary>
    private static void SetName(byte[] data, int offset, int length)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)length);
    }
}
