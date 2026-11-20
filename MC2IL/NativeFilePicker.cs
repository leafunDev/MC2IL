using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace MC2IL;

internal enum NativePickerStatus
{
    Unavailable,
    Cancelled,
    Selected
}

internal readonly struct NativePickerResult
{
    public NativePickerResult(NativePickerStatus status, string? path = null)
    {
        Status = status;
        Path = path;
    }

    public NativePickerStatus Status { get; }
    public string? Path { get; }
}

internal static class NativeFilePicker
{
    private const string PackageFilter = "Unity game packages\0*.apk;*.xapk;*.apkm;*.ipa;*.tipa\0All files\0*.*\0\0";

    public static NativePickerResult PickGamePackage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RunOnSta(PickWindowsFile);

#if NET5_0_OR_GREATER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PickWithAppleScript("POSIX path of (choose file of type {\"apk\", \"xapk\", \"apkm\", \"ipa\", \"tipa\"} with prompt \"Select a Unity game package\")");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PickLinuxFile();
#endif

        return new NativePickerResult(NativePickerStatus.Unavailable);
    }

    public static NativePickerResult PickFolder(string title)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PickWindowsFolderViaSta(title);

#if NET5_0_OR_GREATER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PickWithAppleScript($"POSIX path of (choose folder with prompt \"{EscapeAppleScript(title)}\")");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PickLinuxFolder(title);
#endif

        return new NativePickerResult(NativePickerStatus.Unavailable);
    }

    private static NativePickerResult PickWindowsFile()
    {
        IntPtr pathBuffer = IntPtr.Zero;
        try
        {
            const int maxPathLength = 32768;
            pathBuffer = Marshal.AllocHGlobal(maxPathLength * sizeof(char));
            Marshal.Copy(new char[maxPathLength], 0, pathBuffer, maxPathLength);
            var dialog = new OpenFileName
            {
                Size = Marshal.SizeOf(typeof(OpenFileName)),
                Owner = IntPtr.Zero,
                Filter = PackageFilter,
                FilterIndex = 1,
                File = pathBuffer,
                MaxFile = maxPathLength,
                Title = "Select a Unity game package",
                DefaultExtension = "apk",
                Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 | 0x00000004
            };

            return GetOpenFileName(ref dialog)
                ? new NativePickerResult(NativePickerStatus.Selected, Marshal.PtrToStringUni(pathBuffer))
                : new NativePickerResult(NativePickerStatus.Cancelled);
        }
        catch (DllNotFoundException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        catch (EntryPointNotFoundException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        catch (ArgumentException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        finally
        {
            if (pathBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(pathBuffer);
        }
    }

#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    private static NativePickerResult PickWindowsFolderViaSta(string title)
        => RunOnSta(() => PickWindowsFolder(title));

#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    private static NativePickerResult PickWindowsFolder(string title)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? selectedItem = null;
        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | 0x00000020 | 0x00000040 | 0x00000800);
            dialog.SetTitle(title);

            var showResult = dialog.Show(IntPtr.Zero);
            if (showResult == unchecked((int)0x800704C7))
                return new NativePickerResult(NativePickerStatus.Cancelled);
            if (showResult < 0)
                Marshal.ThrowExceptionForHR(showResult);

            dialog.GetResult(out selectedItem);
            selectedItem.GetDisplayName(0x80058000, out pathPointer);
            return new NativePickerResult(NativePickerStatus.Selected, Marshal.PtrToStringUni(pathPointer));
        }
        catch (COMException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        catch (DllNotFoundException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPointer);
            if (selectedItem != null && Marshal.IsComObject(selectedItem))
                Marshal.ReleaseComObject(selectedItem);
            if (dialog != null && Marshal.IsComObject(dialog))
                Marshal.ReleaseComObject(dialog);
        }
    }

#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    private static NativePickerResult RunOnSta(Func<NativePickerResult> showPicker)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return showPicker();

        NativePickerResult result = new(NativePickerStatus.Unavailable);
        Exception? failure = null;
        var pickerThread = new Thread(() =>
        {
            try { result = showPicker(); }
            catch (Exception e) { failure = e; }
        });
        pickerThread.SetApartmentState(ApartmentState.STA);
        pickerThread.Start();
        pickerThread.Join();

        if (failure != null)
            throw new InvalidOperationException("The native picker could not be opened.", failure);
        return result;
    }

#if NET5_0_OR_GREATER
    private static NativePickerResult PickWithAppleScript(string script)
        => RunPicker("osascript", new[] { "-e", script });

    private static NativePickerResult PickLinuxFile(string title = "Select a Unity game package")
    {
        var zenity = RunPicker("zenity", new[]
        {
            "--file-selection", "--title=" + title,
            "--file-filter=Unity game packages | *.apk *.xapk *.apkm *.ipa *.tipa",
            "--file-filter=All files | *"
        });
        if (zenity.Status != NativePickerStatus.Unavailable)
            return zenity;

        return RunPicker("kdialog", new[] { "--getopenfilename", ".", "Unity packages (*.apk *.xapk *.apkm *.ipa *.tipa)" });
    }

    private static NativePickerResult PickLinuxFolder(string title)
    {
        var zenity = RunPicker("zenity", new[] { "--file-selection", "--directory", "--title=" + title });
        if (zenity.Status != NativePickerStatus.Unavailable)
            return zenity;

        return RunPicker("kdialog", new[] { "--getexistingdirectory", ".", title });
    }

    private static NativePickerResult RunPicker(string executable, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null)
                return new NativePickerResult(NativePickerStatus.Unavailable);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var selectedPath = outputTask.GetAwaiter().GetResult().Trim();
            _ = errorTask.GetAwaiter().GetResult();

            return process.ExitCode == 0 && selectedPath.Length > 0
                ? new NativePickerResult(NativePickerStatus.Selected, selectedPath)
                : new NativePickerResult(NativePickerStatus.Cancelled);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
        catch (IOException)
        {
            return new NativePickerResult(NativePickerStatus.Unavailable);
        }
    }

    private static string EscapeAppleScript(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
#endif

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Filter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? InitialDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsEx;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogCoClass { }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IntPtr item);
        void SetFolder(IntPtr item);
        void GetFolder(out IntPtr item);
        void GetCurrentSelection(out IntPtr item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IntPtr item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint nameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName fileName);


}
