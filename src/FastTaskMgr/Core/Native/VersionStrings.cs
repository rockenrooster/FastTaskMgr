using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Native;

/// <summary>
/// Reads a file's FileDescription and CompanyName the way Explorer does: from the first string
/// table, among the file's own translations and the usual fallbacks, that has either one.
/// FileVersionInfo only accepts a table that also has a FileVersion string, so it reports blanks
/// for files whose version resource has a description but no version string.
/// </summary>
internal static class VersionStrings
{
    private const uint FileVerGetLocalised = 0x01;
    private const uint FileVerGetNeutral = 0x02;
    // US English with the Unicode, Windows-1252 and unspecified code pages, as FileVersionInfo tries.
    private static readonly uint[] FallbackTranslations = [0x040904B0, 0x040904E4, 0x04090000];

    public static (string Description, string Company) Read(string path)
    {
        int size = GetFileVersionInfoSizeEx(FileVerGetLocalised, path, out _);
        if (size <= 0)
        {
            return ("", "");
        }

        IntPtr block = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileVersionInfoEx(FileVerGetLocalised | FileVerGetNeutral, path, 0, size, block))
            {
                return ("", "");
            }

            foreach (uint translation in Translations(block, size))
            {
                string prefix = $@"\StringFileInfo\{translation:X8}\";
                string description = Query(block, size, prefix + "FileDescription");
                string company = Query(block, size, prefix + "CompanyName");
                if (description.Length > 0 || company.Length > 0)
                {
                    return (description, company);
                }
            }

            return ("", "");
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static List<uint> Translations(IntPtr block, int size)
    {
        List<uint> translations = [];
        if (VerQueryValue(block, @"\VarFileInfo\Translation", out IntPtr data, out uint length))
        {
            // Pairs of language ID and code page, in bytes.
            long start = (long)data - (long)block;
            long end = start < 0 ? 0 : Math.Min(length, size - start);
            for (int offset = 0; offset + 4 <= end; offset += 4)
            {
                uint language = (ushort)Marshal.ReadInt16(data, offset);
                uint codePage = (ushort)Marshal.ReadInt16(data, offset + 2);
                translations.Add(language << 16 | codePage);
            }
        }

        foreach (uint fallback in FallbackTranslations)
        {
            if (!translations.Contains(fallback))
            {
                translations.Add(fallback);
            }
        }

        return translations;
    }

    // Lengths are in characters, including the terminator. Reads stay inside the block, since
    // version resources of arbitrary files can be malformed.
    private static string Query(IntPtr block, int size, string subBlock)
    {
        if (!VerQueryValue(block, subBlock, out IntPtr value, out uint length) || length == 0)
        {
            return "";
        }

        long offset = (long)value - (long)block;
        if (offset < 0 || offset >= size)
        {
            return "";
        }

        string text = Marshal.PtrToStringUni(value, (int)Math.Min(length, (size - offset) / 2));
        int terminator = text.IndexOf('\0');
        return (terminator >= 0 ? text[..terminator] : text).Trim();
    }

    [DllImport("version.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFileVersionInfoSizeExW")]
    private static extern int GetFileVersionInfoSizeEx(uint flags, string fileName, out uint handle);

    [DllImport("version.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFileVersionInfoExW")]
    private static extern bool GetFileVersionInfoEx(uint flags, string fileName, uint handle, int length, IntPtr data);

    [DllImport("version.dll", CharSet = CharSet.Unicode, EntryPoint = "VerQueryValueW")]
    private static extern bool VerQueryValue(IntPtr block, string subBlock, out IntPtr buffer, out uint length);
}
