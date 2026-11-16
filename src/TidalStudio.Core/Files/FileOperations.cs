using System.Runtime.InteropServices;
using System.Text;

namespace TidalStudio.Core.Files;

/// <summary>Create / delete operations behind the file explorer.</summary>
public static class FileOperations
{
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Null when <paramref name="name"/> can be used as a file or folder name, otherwise why not.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name != name.Trim()) return "Names can't start or end with spaces.";
        if (name is "." or "..") return $"\"{name}\" can't be used as a name.";
        // Windows' set, also on other systems, so files stay portable.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
            return "Names can't contain < > : \" / \\ | ? *";
        if (name.EndsWith('.')) return "Names can't end with a dot.";
        string stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"\"{stem}\" is reserved by Windows.";
        return null;
    }

    /// <summary>Adds the .tidal extension when the name has none.</summary>
    public static string TidalFileName(string name) => Path.HasExtension(name) ? name : name + ".tidal";

    /// <summary>"untitled.tidal", "untitled 2.tidal", … — the first name not used in <paramref name="directory"/>.</summary>
    public static string UniqueName(string directory, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string candidate = name;
        for (int i = 2; File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate)); i++)
            candidate = $"{stem} {i}{ext}";
        return candidate;
    }

    /// <summary>Creates an empty file (UTF-8, no BOM). Throws <see cref="IOException"/> when the name is taken.</summary>
    public static string CreateFile(string directory, string name, string content = "")
    {
        string path = Checked(directory, name);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(content);
        return path;
    }

    /// <summary>Creates a folder. Throws <see cref="IOException"/> when the name is taken.</summary>
    public static string CreateFolder(string directory, string name)
    {
        string path = Checked(directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Checked(string directory, string name)
    {
        if (ValidateName(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        string path = Path.Combine(directory, name);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"\"{name}\" already exists in {directory}.");
        return path;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes a file or a folder with everything in it. On Windows it goes to the Recycle Bin (so it can be
    /// restored) unless <paramref name="permanently"/>; elsewhere it is deleted. Returns true when it went to the bin.
    /// </summary>
    public static bool Delete(string path, bool permanently = false)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("It no longer exists.", path);
        if (Path.GetPathRoot(path) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A whole drive can't be deleted.");

        if (!permanently && OperatingSystem.IsWindows())
        {
            var op = new ShFileOpStruct
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0", // double-null-terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            int result = SHFileOperation(ref op);
            if (result == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path)) return true;
            if (op.fAnyOperationsAborted) throw new OperationCanceledException("Deleting was cancelled.");
            // The bin isn't available (network drive, bin disabled…): fall through to a normal delete only if asked.
            throw new IOException($"Could not move it to the Recycle Bin (error {result}).");
        }

        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
        return false;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
