using System;
using System.Runtime.InteropServices;

namespace VRoidPlus
{
    /// <summary>The standard Windows "Open file" dialog (comdlg32), so no extra libraries are needed.</summary>
    internal static class FileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private const int OFN_FILEMUSTEXIST = 0x00001000, OFN_PATHMUSTEXIST = 0x00000800, OFN_NOCHANGEDIR = 0x00000008, OFN_EXPLORER = 0x00080000;

        /// <param name="filter">e.g. "FBX files|*.fbx|All files|*.*"</param>
        public static string Open(string title, string filter)
        {
            const int max = 4096;
            var buffer = Marshal.AllocHGlobal(max * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = filter.Replace('|', '\0') + "\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = max,
                    lpstrTitle = title,
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER,
                };
                return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("File dialog failed: " + e.Message);
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
