using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Server.Tools;

/// <summary>What kind of link a reparse point stores.</summary>
internal enum LinkKind
{
    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    SymbolicLink,

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction, or a volume mount point when it names a volume.</summary>
    MountPoint,
}

/// <summary>A link as its reparse data stores it.</summary>
/// <param name="Kind">A symbolic link or a mount point.</param>
/// <param name="Target">The substitute name as stored, such as <c>\??\X:\folder</c>, <c>\??\Volume{guid}\</c> or <c>..\x</c>.</param>
/// <param name="Relative">Whether a symbolic link's target is relative to the link's own folder.</param>
internal sealed record StoredLink(LinkKind Kind, string Target, bool Relative);

/// <summary>
/// What Windows says of a path: where a handle finally lies once every link is followed, how many names a file has, what
/// link a folder entry stores, and whether a drive letter is a network drive.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class FinalPath
{
    private const uint FileReadAttributes = 0x80;

    /// <summary><c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>.</summary>
    private const uint ShareAll = 0x7;

    private const uint OpenExisting = 3;

    /// <summary><c>FILE_FLAG_BACKUP_SEMANTICS</c>, without which a folder cannot be opened.</summary>
    private const uint BackupSemantics = 0x02000000;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: long names, the volume as its drive letter.</summary>
    private const uint NormalizedDosName = 0;

    private const uint DriveRemote = 4;

    /// <summary><c>MOVEFILE_REPLACE_EXISTING</c>.</summary>
    private const uint MoveReplaceExisting = 0x1;

    /// <summary><c>ERROR_NOT_SAME_DEVICE</c>: a rename cannot cross volumes.</summary>
    private const int NotSameDevice = 17;

    private const uint ReparsePointAttribute = 0x400;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction, or a volume mount point when its target is a volume.</summary>
    private const uint MountPointTag = 0xA0000003;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    private const uint SymlinkTag = 0xA000000C;

    /// <summary><c>SYMLINK_FLAG_RELATIVE</c>: the substitute name is relative to the link's folder.</summary>
    private const uint SymlinkFlagRelative = 1;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT</c>: open the entry itself, not what it links to.</summary>
    private const uint OpenReparsePoint = 0x00200000;

    /// <summary><c>FSCTL_GET_REPARSE_POINT</c>.</summary>
    private const uint GetReparsePoint = 0x000900A8;

    /// <summary><c>MAXIMUM_REPARSE_DATA_BUFFER_SIZE</c>.</summary>
    private const int MaximumReparseDataSize = 16 * 1024;

    private const int FileNotFound = 2;
    private const int PathNotFound = 3;

    private const string LongPathPrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// The final path of the file or folder at <paramref name="path"/>, opened following every link, without the
    /// <c>\\?\</c> prefix: <c>X:\…</c> on a local volume, <c>\\server\share\…</c> on a network one.
    /// </summary>
    /// <exception cref="IOException">The path cannot be opened or its final path cannot be read.</exception>
    internal static string Of(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        return handle.IsInvalid ? throw Failure() : Of(handle);
    }

    /// <summary>The final path of an open file, as <see cref="Of(string)"/> gives it.</summary>
    /// <exception cref="IOException">The final path cannot be read.</exception>
    internal static string Of(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NormalizedDosName);
            if (length == 0)
            {
                throw Failure();
            }

            // A buffer too small gets back the size it needs, the terminating NUL counted.
            if (length < buffer.Length)
            {
                return WithoutLongPathPrefix(new string(buffer, 0, (int)length));
            }

            buffer = new char[length];
        }
    }

    /// <summary><c>\\?\X:\…</c> as <c>X:\…</c>, and <c>\\?\UNC\server\share\…</c> as <c>\\server\share\…</c>.</summary>
    private static string WithoutLongPathPrefix(string final)
    {
        if (final.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + final[UncPrefix.Length..];
        }

        return final.StartsWith(LongPathPrefix, StringComparison.Ordinal) ? final[LongPathPrefix.Length..] : final;
    }

    /// <summary>
    /// The link stored at the entry <paramref name="path"/>, read from its reparse data without following it: a symbolic
    /// link or a mount point (a junction or a volume mount point) with its substitute name as stored; null when nothing is
    /// there, when the entry is no reparse point, and for any other reparse point (cloud files, container layers).
    /// </summary>
    /// <exception cref="IOException">The entry or its reparse data cannot be read.</exception>
    internal static StoredLink? ReadLink(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return error is FileNotFound or PathNotFound ? null : throw Failure(error);
        }

        // FAT32 and exFAT have no reparse points and fail the FSCTL, so only an entry marked as one is asked for its data.
        if (!GetFileInformationByHandle(handle, out FileInformation information))
        {
            throw Failure();
        }

        if ((information.Attributes & ReparsePointAttribute) == 0)
        {
            return null;
        }

        byte[] data = new byte[MaximumReparseDataSize];
        return DeviceIoControl(handle, GetReparsePoint, 0, 0, data, (uint)data.Length, out uint returned, 0)
            ? ParseLink(data, (int)Math.Min(returned, (uint)data.Length))
            : throw Failure();
    }

    /// <summary>
    /// A <c>REPARSE_DATA_BUFFER</c>'s link: the tag, then for a symbolic link the substitute name's offset and length, the
    /// print name's, and its flags before the names; for a mount point the same without the flags.
    /// </summary>
    /// <exception cref="IOException">
    /// The data is too short for the names it describes, the substitute name's offset or length is odd, or the name holds a NUL.
    /// </exception>
    internal static StoredLink? ParseLink(byte[] data, int returned)
    {
        uint tag = returned >= 4 ? BitConverter.ToUInt32(data, 0) : throw Malformed();
        if (tag is not (SymlinkTag or MountPointTag))
        {
            return null;
        }

        bool symbolic = tag == SymlinkTag;
        int names = symbolic ? 20 : 16;
        string substitute = SubstituteName(data, returned, names);
        bool relative = symbolic && (BitConverter.ToUInt32(data, 16) & SymlinkFlagRelative) != 0;
        return new StoredLink(symbolic ? LinkKind.SymbolicLink : LinkKind.MountPoint, substitute, relative);
    }

    /// <summary>The substitute name of a link whose path buffer starts at <paramref name="names"/>, checked against the data read.</summary>
    private static string SubstituteName(byte[] data, int returned, int names)
    {
        if (returned < names)
        {
            throw Malformed();
        }

        int offset = BitConverter.ToUInt16(data, 8);
        int length = BitConverter.ToUInt16(data, 10);
        // A UTF-16 name is whole 2-byte units: an odd offset or length would decode a stray byte as U+FFFD.
        if (offset % 2 != 0 || length % 2 != 0 || names + offset + length > returned)
        {
            throw Malformed();
        }

        string substitute = System.Text.Encoding.Unicode.GetString(data, names + offset, length);
        return substitute.Contains('\0', StringComparison.Ordinal) ? throw Malformed() : substitute;
    }

    private static IOException Malformed() => new("the link's reparse data is malformed");

    /// <summary>How many names (hard links) the open file has.</summary>
    /// <exception cref="IOException">The file's information cannot be read.</exception>
    internal static uint LinkCount(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out FileInformation information) ? information.NumberOfLinks : throw Failure();

    /// <summary>Whether <paramref name="path"/> starts with a drive letter that names a network drive.</summary>
    internal static bool IsRemoteDrive(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && GetDriveTypeW($"{path[0]}:\\") == DriveRemote;

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/>, replacing its folder entry only with
    /// <paramref name="overwrite"/>, and never as a copy: without MOVEFILE_COPY_ALLOWED, which File.Move always passes, Windows
    /// cannot turn the rename into a copy written through a link or hard link at the destination.
    /// </summary>
    /// <returns>Whether it was renamed; false when the two lie on different volumes, and nothing was done.</returns>
    /// <exception cref="IOException">The rename failed for another reason, such as an existing destination without overwrite.</exception>
    internal static bool Rename(string source, string destination, bool overwrite)
    {
        if (MoveFileExW(source, destination, overwrite ? MoveReplaceExisting : 0))
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        return error == NotSameDevice ? false : throw Failure(error);
    }

    private static IOException Failure() => Failure(Marshal.GetLastPInvokeError());

    private static IOException Failure(int error) => new(new Win32Exception(error).Message);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint code,
        nint input,
        uint inputSize,
        [Out] byte[] output,
        uint outputSize,
        out uint returned,
        nint overlapped
    );

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string path,
        uint access,
        uint share,
        nint securityAttributes,
        uint creation,
        uint flags,
        nint template
    );

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string replacement, uint flags);

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>, whose FILETIMEs are two 4-byte halves, so 4-byte packed.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
