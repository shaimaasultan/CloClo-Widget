using System.Runtime.InteropServices;
using System.Windows;

// Every [DllImport] in this app targets a Windows system DLL (user32,
// shell32, ole32, wintrust…). Load them from System32 only — never from the
// app's own folder, where another program could place a look-alike.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
