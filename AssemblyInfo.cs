//#define TEST_UPDATER
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Resources;

[assembly: CompilationRelaxations(8)]
[assembly: AssemblyTitle("Crysis 2 Multiplayer Launcher")]
[assembly: AssemblyDescription("Crysis 2 Multiplayer Mod Launcher")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Maximum Crysis Community")]
[assembly: AssemblyProduct("Crysis 2 Multiplayer Launcher")]
[assembly: AssemblyCopyright("Copyright ©  2025")]
[assembly: AssemblyTrademark("")]
[assembly: ComVisible(false)]

#if TEST_UPDATER
[assembly: AssemblyFileVersion("0.9.0.0")]
[assembly: AssemblyVersion("0.9.0.0")]
#else
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyVersion("1.0.1.0")]
#endif

[assembly: AssemblyAssociatedContentFile("webview2loader.dll")]
